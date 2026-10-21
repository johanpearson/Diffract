//! When the sidecar stops on its own, the core starts it again, but not forever: a sidecar that crashes on start
//! would otherwise be restarted in a tight loop.

use std::collections::VecDeque;
use std::time::{Duration, Instant};

/// At most this many restarts...
pub const MAX_RESTARTS: usize = 3;
/// ...within any window this long.
pub const WINDOW: Duration = Duration::from_secs(60);

#[derive(Default)]
pub struct RestartPolicy {
    recent: VecDeque<Instant>,
}

impl RestartPolicy {
    /// Called when the sidecar stopped unexpectedly at `now`. True when it may be started again, which counts as a restart.
    pub fn allow(&mut self, now: Instant) -> bool {
        while self.recent.front().is_some_and(|&at| now.duration_since(at) >= WINDOW) {
            self.recent.pop_front();
        }
        if self.recent.len() >= MAX_RESTARTS {
            return false;
        }
        self.recent.push_back(now);
        true
    }

    /// Forgets earlier restarts, for when the user asks to try again.
    pub fn reset(&mut self) {
        self.recent.clear();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn secs(start: Instant, s: u64) -> Instant {
        start + Duration::from_secs(s)
    }

    #[test]
    fn allows_three_restarts_in_a_minute_and_refuses_the_fourth() {
        let start = Instant::now();
        let mut policy = RestartPolicy::default();

        let answers: Vec<bool> = [0, 10, 20, 30].iter().map(|&s| policy.allow(secs(start, s))).collect();

        assert_eq!(answers, [true, true, true, false]);
    }

    #[test]
    fn allows_again_once_the_oldest_restart_is_a_minute_old() {
        let start = Instant::now();
        let mut policy = RestartPolicy::default();
        for s in [0, 10, 20] {
            assert!(policy.allow(secs(start, s)));
        }

        assert!(!policy.allow(secs(start, 59)));
        assert!(policy.allow(secs(start, 60)));
        assert!(!policy.allow(secs(start, 61)));
    }

    #[test]
    fn a_refused_restart_does_not_count() {
        let start = Instant::now();
        let mut policy = RestartPolicy::default();
        for s in [0, 1, 2, 3, 4, 5] {
            policy.allow(secs(start, s));
        }

        // Only the three allowed ones at 0, 1 and 2 count, so the window opens again at 60.
        assert!(policy.allow(secs(start, 60)));
    }

    #[test]
    fn reset_forgets_earlier_restarts() {
        let start = Instant::now();
        let mut policy = RestartPolicy::default();
        for s in [0, 1, 2] {
            policy.allow(secs(start, s));
        }

        policy.reset();

        assert!(policy.allow(secs(start, 3)));
    }
}
