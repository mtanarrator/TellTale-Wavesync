//! Captures engine warnings so they can be surfaced in the sync manifest.
//!
//! The crate does not install a logger by default, so every `log::warn!` was a
//! no-op and users never saw decode/partial-read diagnostics. This module installs
//! a small ring buffer once and drains it into `SyncManifest.warnings` per execute.

use std::sync::{Mutex, OnceLock};

const MAX_CAPTURED_WARNINGS: usize = 200;

static CAPTURED: OnceLock<Mutex<Vec<String>>> = OnceLock::new();

struct CaptureLogger;

impl log::Log for CaptureLogger {
    fn enabled(&self, metadata: &log::Metadata) -> bool {
        metadata.level() <= log::Level::Warn
    }

    fn log(&self, record: &log::Record) {
        if !self.enabled(record.metadata()) {
            return;
        }

        if let Some(buffer) = CAPTURED.get() {
            if let Ok(mut lines) = buffer.lock() {
                if lines.len() < MAX_CAPTURED_WARNINGS {
                    // Keep the manifest text readable: the UI already prefixes notes.
                    let prefix = if record.level() == log::Level::Error {
                        "ERROR: "
                    } else {
                        ""
                    };
                    lines.push(format!("{}{}", prefix, record.args()));
                }
            }
        }
    }

    fn flush(&self) {}
}

/// Installs the capturing logger once. Safe to call on every execute.
pub fn install() {
    CAPTURED.get_or_init(|| Mutex::new(Vec::new()));

    static LOGGER: CaptureLogger = CaptureLogger;
    // If a host or test already installed a logger, keep it and simply do not capture.
    let _ = log::set_logger(&LOGGER);
    log::set_max_level(log::LevelFilter::Warn);
}

/// Clears the buffer at the start of a run.
pub fn begin() {
    if let Some(buffer) = CAPTURED.get() {
        if let Ok(mut lines) = buffer.lock() {
            lines.clear();
        }
    }
}

/// Drains up to `limit` captured lines for the manifest.
pub fn drain(limit: usize) -> Vec<String> {
    let Some(buffer) = CAPTURED.get() else {
        return Vec::new();
    };
    let Ok(mut lines) = buffer.lock() else {
        return Vec::new();
    };

    let take = lines.len().min(limit);
    lines.drain(0..take).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn captures_and_drains_warnings() {
        install();
        begin();
        log::warn!("test warning {}", 42);
        let drained = drain(10);
        assert!(
            drained.iter().any(|line| line.contains("test warning 42")),
            "expected the warning to be captured, got {:?}",
            drained
        );
    }
}
