//! The sidecar writes its logs to stderr. The core appends them to `sidecar.log` in the app's log folder,
//! each line with a UTC time, and keeps one older file, so the log cannot grow without limit.

use std::fs::{self, File, OpenOptions};
use std::io::{self, Write};
use std::path::Path;
use std::time::{SystemTime, UNIX_EPOCH};

/// A log bigger than this becomes `sidecar.old.log` the next time the app starts.
pub const MAX_BYTES: u64 = 1024 * 1024;

pub struct SidecarLog {
    file: File,
}

impl SidecarLog {
    /// Opens `sidecar.log` in `dir` for appending, creating the folder if needed.
    pub fn open(dir: &Path) -> io::Result<Self> {
        fs::create_dir_all(dir)?;
        let path = dir.join("sidecar.log");
        if fs::metadata(&path).is_ok_and(|m| m.len() > MAX_BYTES) {
            // Replaces an existing sidecar.old.log.
            fs::rename(&path, dir.join("sidecar.old.log"))?;
        }
        let file = OpenOptions::new().create(true).append(true).open(path)?;
        Ok(Self { file })
    }

    pub fn write_line(&mut self, line: &str) -> io::Result<()> {
        self.write_line_at(SystemTime::now(), line)
    }

    pub fn write_line_at(&mut self, at: SystemTime, line: &str) -> io::Result<()> {
        writeln!(self.file, "{} {line}", utc_timestamp(at))
    }
}

/// "2026-10-21T09:30:00Z". Written out by hand, because no date crate is approved and this is all the log needs.
pub fn utc_timestamp(at: SystemTime) -> String {
    let seconds = at.duration_since(UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0);
    let (days, rest) = (seconds / 86_400, seconds % 86_400);
    let (year, month, day) = civil_from_days(days as i64);
    format!("{year:04}-{month:02}-{day:02}T{:02}:{:02}:{:02}Z", rest / 3600, rest % 3600 / 60, rest % 60)
}

/// Days since 1970-01-01 to a calendar date. Howard Hinnant's algorithm.
fn civil_from_days(days: i64) -> (i64, u32, u32) {
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let day = (doy - (153 * mp + 2) / 5 + 1) as u32;
    let month = if mp < 10 { mp + 3 } else { mp - 9 } as u32;
    let year = yoe + era * 400 + i64::from(month <= 2);
    (year, month, day)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;
    use std::time::Duration;

    fn at(unix_seconds: u64) -> SystemTime {
        UNIX_EPOCH + Duration::from_secs(unix_seconds)
    }

    /// A fresh folder per test, removed when dropped.
    struct TempDir(PathBuf);

    impl TempDir {
        fn new(name: &str) -> Self {
            let path = std::env::temp_dir().join(format!("diffract-log-{}-{name}", std::process::id()));
            let _ = fs::remove_dir_all(&path);
            Self(path)
        }
    }

    impl Drop for TempDir {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    #[test]
    fn creates_the_folder_and_appends_lines_with_their_time() {
        let dir = TempDir::new("append");
        SidecarLog::open(&dir.0).unwrap().write_line_at(at(0), "first").unwrap();
        SidecarLog::open(&dir.0).unwrap().write_line_at(at(61), "räksmörgås").unwrap();

        assert_eq!(
            fs::read_to_string(dir.0.join("sidecar.log")).unwrap(),
            "1970-01-01T00:00:00Z first\n1970-01-01T00:01:01Z räksmörgås\n"
        );
    }

    #[test]
    fn a_log_over_the_limit_becomes_the_old_log() {
        let dir = TempDir::new("rotate");
        fs::create_dir_all(&dir.0).unwrap();
        fs::write(dir.0.join("sidecar.old.log"), "oldest").unwrap();
        fs::write(dir.0.join("sidecar.log"), vec![b'x'; MAX_BYTES as usize + 1]).unwrap();

        SidecarLog::open(&dir.0).unwrap().write_line_at(at(0), "new").unwrap();

        assert_eq!(fs::read_to_string(dir.0.join("sidecar.log")).unwrap(), "1970-01-01T00:00:00Z new\n");
        assert_eq!(fs::metadata(dir.0.join("sidecar.old.log")).unwrap().len(), MAX_BYTES + 1);
    }

    #[test]
    fn a_log_under_the_limit_is_kept() {
        let dir = TempDir::new("keep");
        fs::create_dir_all(&dir.0).unwrap();
        fs::write(dir.0.join("sidecar.log"), "earlier\n").unwrap();

        SidecarLog::open(&dir.0).unwrap().write_line_at(at(0), "later").unwrap();

        assert_eq!(fs::read_to_string(dir.0.join("sidecar.log")).unwrap(), "earlier\n1970-01-01T00:00:00Z later\n");
        assert!(!dir.0.join("sidecar.old.log").exists());
    }

    #[test]
    fn timestamps_are_utc_calendar_dates() {
        assert_eq!(utc_timestamp(at(0)), "1970-01-01T00:00:00Z");
        assert_eq!(utc_timestamp(at(951_782_400)), "2000-02-29T00:00:00Z");
        assert_eq!(utc_timestamp(at(1_792_575_000)), "2026-10-21T09:30:00Z");
        assert_eq!(utc_timestamp(at(1_798_761_599)), "2026-12-31T23:59:59Z");
    }
}
