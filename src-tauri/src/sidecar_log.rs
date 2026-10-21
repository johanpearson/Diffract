//! The sidecar writes its logs to stderr. The core appends them to `sidecar.log` in the app's log folder,
//! and keeps one older file, so the log cannot grow without limit.

use std::fs::{self, File, OpenOptions};
use std::io::{self, Write};
use std::path::Path;

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
        writeln!(self.file, "{line}")
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;

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
    fn creates_the_folder_and_appends_lines() {
        let dir = TempDir::new("append");
        SidecarLog::open(&dir.0).unwrap().write_line("first").unwrap();
        SidecarLog::open(&dir.0).unwrap().write_line("räksmörgås").unwrap();

        assert_eq!(fs::read_to_string(dir.0.join("sidecar.log")).unwrap(), "first\nräksmörgås\n");
    }

    #[test]
    fn a_log_over_the_limit_becomes_the_old_log() {
        let dir = TempDir::new("rotate");
        fs::create_dir_all(&dir.0).unwrap();
        fs::write(dir.0.join("sidecar.old.log"), "oldest").unwrap();
        fs::write(dir.0.join("sidecar.log"), vec![b'x'; MAX_BYTES as usize + 1]).unwrap();

        SidecarLog::open(&dir.0).unwrap().write_line("new").unwrap();

        assert_eq!(fs::read_to_string(dir.0.join("sidecar.log")).unwrap(), "new\n");
        assert_eq!(fs::metadata(dir.0.join("sidecar.old.log")).unwrap().len(), MAX_BYTES + 1);
    }

    #[test]
    fn a_log_under_the_limit_is_kept() {
        let dir = TempDir::new("keep");
        fs::create_dir_all(&dir.0).unwrap();
        fs::write(dir.0.join("sidecar.log"), "earlier\n").unwrap();

        SidecarLog::open(&dir.0).unwrap().write_line("later").unwrap();

        assert_eq!(fs::read_to_string(dir.0.join("sidecar.log")).unwrap(), "earlier\nlater\n");
        assert!(!dir.0.join("sidecar.old.log").exists());
    }
}
