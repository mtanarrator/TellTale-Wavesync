use std::ffi::{CStr, CString};
use std::os::raw::{c_char, c_void};
use std::panic::catch_unwind;

use crate::models::SyncTaskRequest;

pub type ProgressCallback =
    extern "C" fn(progress: f32, message: *const c_char, user_data: *mut c_void) -> i32;

pub const VEGAS_SYNC_SUCCESS: i32 = 0;
pub const VEGAS_SYNC_ERR_NULL_PTR: i32 = -1;
pub const VEGAS_SYNC_ERR_INVALID_UTF8: i32 = -2;
pub const VEGAS_SYNC_ERR_JSON_PARSE: i32 = -3;
pub const VEGAS_SYNC_ERR_PROCESSING: i32 = -4;
pub const VEGAS_SYNC_ERR_CANCELLED: i32 = -5;
pub const VEGAS_SYNC_ERR_PANIC: i32 = -99;

#[no_mangle]
pub extern "C" fn vegas_sync_version() -> *mut c_char {
    let version = concat!(
        "telltale_sync_core v",
        env!("CARGO_PKG_VERSION"),
        " (SIMD-enabled Rust Native Engine)"
    );
    match CString::new(version) {
        Ok(c_str) => c_str.into_raw(),
        Err(_) => std::ptr::null_mut(),
    }
}

#[no_mangle]
pub extern "C" fn vegas_sync_free_string(ptr: *mut c_char) {
    if !ptr.is_null() {
        unsafe {
            let _ = CString::from_raw(ptr);
        }
    }
}

pub struct ProgressReporter {
    cb: Option<ProgressCallback>,
    user_data: *mut c_void,
    lock: std::sync::Mutex<()>,
}

unsafe impl Send for ProgressReporter {}
unsafe impl Sync for ProgressReporter {}

impl ProgressReporter {
    pub fn new(cb: Option<ProgressCallback>, user_data: *mut c_void) -> Self {
        Self {
            cb,
            user_data,
            lock: std::sync::Mutex::new(()),
        }
    }

    pub fn report(&self, progress: f32, message: &str) -> bool {
        if let Some(cb) = self.cb {
            if let Ok(c_msg) = CString::new(message) {
                let _guard = self.lock.lock();
                let code = cb(progress, c_msg.as_ptr(), self.user_data);
                return code == 0; // Return true if not cancelled
            }
        }
        true
    }
}

#[no_mangle]
pub extern "C" fn vegas_sync_execute(
    json_request_ptr: *const c_char,
    progress_cb: Option<ProgressCallback>,
    user_data: *mut c_void,
    out_json_manifest_ptr: *mut *mut c_char,
) -> i32 {
    let result = catch_unwind(|| {
        if json_request_ptr.is_null() || out_json_manifest_ptr.is_null() {
            return VEGAS_SYNC_ERR_NULL_PTR;
        }

        let json_str = match unsafe { CStr::from_ptr(json_request_ptr) }.to_str() {
            Ok(s) => s,
            Err(_) => return VEGAS_SYNC_ERR_INVALID_UTF8,
        };

        let request: SyncTaskRequest = match serde_json::from_str(json_str) {
            Ok(req) => req,
            Err(e) => {
                log::error!("Failed to parse SyncTaskRequest: {}", e);
                return VEGAS_SYNC_ERR_JSON_PARSE;
            }
        };

        let reporter = ProgressReporter::new(progress_cb, user_data);

        reporter.report(0.01, "Sync engine initialized");

        // Execute the synchronization pipeline
        let manifest = match crate::engine::run_sync_pipeline(&request, &reporter) {
            Ok(m) => m,
            Err(e) => {
                if e == crate::audio::decoder::CANCELLED {
                    return VEGAS_SYNC_ERR_CANCELLED;
                }
                log::error!("Sync pipeline execution failed: {}", e);
                return VEGAS_SYNC_ERR_PROCESSING;
            }
        };

        reporter.report(1.0, "Generating sync manifest");

        let manifest_json = match serde_json::to_string_pretty(&manifest) {
            Ok(j) => j,
            Err(_) => return VEGAS_SYNC_ERR_PROCESSING,
        };

        match CString::new(manifest_json) {
            Ok(c_str) => {
                unsafe {
                    *out_json_manifest_ptr = c_str.into_raw();
                }
                VEGAS_SYNC_SUCCESS
            }
            Err(_) => VEGAS_SYNC_ERR_PROCESSING,
        }
    });

    match result {
        Ok(code) => code,
        Err(_) => VEGAS_SYNC_ERR_PANIC,
    }
}
