// SPDX-License-Identifier: GPL-3.0-or-later
//! WinUI 3 native bridge boundary: pushes coalesced state-change notifications
//! and UI events to the C# WinUI 3 host, and exposes typed JSON snapshots and
//! command handlers across all NectarLink features.

#[allow(unsafe_code)]
pub mod app;
#[allow(unsafe_code)]
pub mod native;
#[allow(unsafe_code)]
pub mod report;
#[allow(unsafe_code)]
pub mod winui;

use std::{
    ffi::c_char,
    sync::{
        Mutex, OnceLock,
        atomic::{AtomicU32, Ordering},
    },
};

use nectarlink_core::DeviceId;

use crate::state::Changes;

pub const EVENT_STATE_CHANGED: u32 = 1;
pub const EVENT_TOAST: u32 = 2;
pub const EVENT_ACTIVATE: u32 = 3;
pub const EVENT_QUIT: u32 = 4;
pub const EVENT_OPEN_DOCTOR: u32 = 5;
pub const EVENT_OPEN_WHATS_NEW: u32 = 6;
pub const EVENT_OPEN_FILE_PICKER: u32 = 7;
pub const EVENT_OPEN_HANDOFF_PICKER: u32 = 8;
pub const EVENT_UNLOCK_RESULT: u32 = 9;
pub const EVENT_PHONE_TOGGLE_FAILED: u32 = 10;
pub const EVENT_REPORT_REQUESTED: u32 = 11;
pub const EVENT_SHELF_TOGGLE: u32 = 14;
pub const EVENT_COMMAND_PALETTE_OPEN: u32 = 15;
pub const EVENT_VIDEO_CLEAR: u32 = 16;
#[allow(dead_code)]
pub const EVENT_PAIRING_CHANGED: u32 = 20;

pub type EventCallback =
    unsafe extern "C" fn(kind: u32, payload_utf8: *const c_char, payload_len: usize, user_data: usize);

pub type VideoCallback = unsafe extern "C" fn(
    stream_utf8: *const c_char,
    width: u32,
    height: u32,
    bgrx_ptr: *const u8,
    bgrx_len: usize,
    user_data: usize,
);

#[derive(Clone, Copy)]
struct Callbacks {
    event_cb: EventCallback,
    video_cb: VideoCallback,
    user_data: usize,
}

static CALLBACKS: OnceLock<Mutex<Option<Callbacks>>> = OnceLock::new();
static PENDING_CHANGES: AtomicU32 = AtomicU32::new(0);
static NOTIFY_STARTED: OnceLock<()> = OnceLock::new();
static NOTIFY_TX: OnceLock<std::sync::mpsc::SyncSender<()>> = OnceLock::new();

pub(crate) fn set_callbacks(event_cb: EventCallback, video_cb: VideoCallback, user_data: usize) {
    let lock = CALLBACKS.get_or_init(|| Mutex::new(None));
    *lock.lock().unwrap_or_else(|e| e.into_inner()) = Some(Callbacks { event_cb, video_cb, user_data });
}

pub(crate) fn clear_callbacks() {
    if let Some(lock) = CALLBACKS.get() {
        *lock.lock().unwrap_or_else(|e| e.into_inner()) = None;
    }
}

pub(crate) fn emit_event(kind: u32, payload: &str) {
    let cb = CALLBACKS
        .get()
        .and_then(|l| l.lock().ok().and_then(|g| *g));
    let Some(cb) = cb else { return };
    // SAFETY: `payload` lives for the duration of the synchronous callback call.
    unsafe {
        (cb.event_cb)(kind, payload.as_ptr().cast::<c_char>(), payload.len(), cb.user_data);
    }
}

pub(crate) fn emit_video_frame(stream: &str, width: u32, height: u32, bgrx: &[u8]) {
    let cb = CALLBACKS
        .get()
        .and_then(|l| l.lock().ok().and_then(|g| *g));
    let Some(cb) = cb else { return };
    let Ok(stream_c) = std::ffi::CString::new(stream) else { return };
    // SAFETY: `stream_c` and `bgrx` live for the duration of the synchronous callback call.
    unsafe {
        (cb.video_cb)(
            stream_c.as_ptr(),
            width,
            height,
            bgrx.as_ptr(),
            bgrx.len(),
            cb.user_data,
        );
    }
}

/// Starts a single coalescing pump that forwards `Hub` change masks to WinUI 3
/// without flooding the UI thread during bursts.
pub(crate) fn start_hub_pump() {
    NOTIFY_STARTED.get_or_init(|| {
        let (tx, rx) = std::sync::mpsc::sync_channel::<()>(1);
        let _ = NOTIFY_TX.set(tx.clone());
        std::thread::Builder::new()
            .name("nl-winui-events".into())
            .spawn(move || {
                while rx.recv().is_ok() {
                    // Coalesce rapid consecutive Hub updates within 16 ms.
                    std::thread::sleep(std::time::Duration::from_millis(16));
                    let mask = PENDING_CHANGES.swap(0, Ordering::AcqRel);
                    if mask != 0 {
                        let json = serde_json::json!({ "changes": mask }).to_string();
                        emit_event(EVENT_STATE_CHANGED, &json);
                    }
                }
            })
            .ok();

        let all = Changes::ALL;
        crate::core_host::host().hub.subscribe(all, move || {
            true
        });
    });
}

/// Called by `Hub` or bridge helpers whenever a specific `Changes` bitmask changes.
pub(crate) fn notify_changes(changes: Changes) {
    let bits = changes.bits();
    if bits == 0 {
        return;
    }
    PENDING_CHANGES.fetch_or(bits, Ordering::AcqRel);
    if let Some(tx) = NOTIFY_TX.get() {
        let _ = tx.try_send(());
    }
}

pub(crate) fn parse_device(id: &str) -> Option<DeviceId> {
    id.trim().parse().ok()
}
