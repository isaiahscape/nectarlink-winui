// SPDX-License-Identifier: GPL-3.0-or-later
//! Video frame bridge called by `mirror.rs` and `webcam.rs`.

pub mod ffi {
    use crate::bridge::{EVENT_VIDEO_CLEAR, emit_event, emit_video_frame};

    /// The latest picture of a video stream (32-bit BGRX rows), for WinUI 3
    /// swapchain/video surfaces. Any thread.
    pub fn video_frame(stream: &str, width: u32, height: u32, bgrx: &[u8]) {
        emit_video_frame(stream, width, height, bgrx);
    }

    /// The stream ended: its views go blank. Any thread.
    pub fn video_clear(stream: &str) {
        let payload = serde_json::json!({ "stream": stream }).to_string();
        emit_event(EVENT_VIDEO_CLEAR, &payload);
    }
}
