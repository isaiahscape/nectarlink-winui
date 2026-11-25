// SPDX-License-Identifier: GPL-3.0-or-later
//! Per-device phone wallpaper cache and fetch pipeline.
//!
//! Wallpapers are stored under `<data_dir>/cache/wallpapers/` with monotonic
//! timestamped filenames (`<device_id>-<epoch_ms>.<ext>`) so UI surfaces reload
//! immediately whenever a device's wallpaper is updated.

use std::{
    collections::{HashMap, HashSet},
    path::{Path, PathBuf},
    sync::Mutex,
    time::{SystemTime, UNIX_EPOCH},
};

use nectarlink_core::{DeviceId, NodeEvent};

use crate::{bridge::app::show_message, core_host, i18n::tr, state::Changes};

const MAX_WALLPAPER_BYTES: usize = 32 * 1024 * 1024;

#[derive(Debug, Default)]
struct WallpaperState {
    loaded: bool,
    paths: HashMap<DeviceId, PathBuf>,
    fetching: HashSet<DeviceId>,
    status: HashMap<DeviceId, String>,
}

static STATE: Mutex<Option<WallpaperState>> = Mutex::new(None);

fn state<T>(f: impl FnOnce(&mut WallpaperState) -> T) -> T {
    let mut guard = STATE.lock().unwrap_or_else(|e| e.into_inner());
    let st = guard.get_or_insert_with(WallpaperState::default);
    if !st.loaded {
        st.loaded = true;
        load_existing_into(st);
    }
    f(st)
}

fn cache_dir() -> PathBuf {
    core_host::host().data_dir.join("cache").join("wallpapers")
}

fn load_existing_into(st: &mut WallpaperState) {
    let dir = cache_dir();
    let Ok(entries) = std::fs::read_dir(&dir) else {
        return;
    };
    let mut best: HashMap<DeviceId, (SystemTime, PathBuf)> = HashMap::new();
    for entry in entries.flatten() {
        let path = entry.path();
        if !path.is_file() {
            continue;
        }
        let Some(stem) = path.file_stem().and_then(|s| s.to_str()) else {
            continue;
        };
        let dev_str = stem.split('-').next().unwrap_or(stem);
        let Ok(dev_id) = dev_str.parse::<DeviceId>() else {
            continue;
        };
        let modified = entry.metadata().and_then(|m| m.modified()).unwrap_or(UNIX_EPOCH);
        let replace = best.get(&dev_id).is_none_or(|(prev, _)| modified >= *prev);
        if replace {
            best.insert(dev_id, (modified, path));
        }
    }
    for (dev_id, (_, path)) in best {
        st.paths.insert(dev_id, path);
    }
}

pub fn path_for(device: &DeviceId) -> Option<PathBuf> {
    state(|st| st.paths.get(device).filter(|p| p.is_file()).cloned())
}

pub fn is_fetching(device: &DeviceId) -> bool {
    state(|st| st.fetching.contains(device))
}

pub fn status_for(device: &DeviceId) -> String {
    state(|st| st.status.get(device).cloned().unwrap_or_default())
}

pub fn on_event(event: &NodeEvent) {
    if let NodeEvent::DeviceRemoved(device) = event {
        remove_device_files(device);
        state(|st| {
            st.paths.remove(device);
            st.fetching.remove(device);
            st.status.remove(device);
        });
    }
}

fn remove_device_files(device: &DeviceId) {
    let dir = cache_dir();
    let prefix_dash = format!("{device}-");
    let prefix_exact = device.to_string();
    let Ok(entries) = std::fs::read_dir(&dir) else {
        return;
    };
    for entry in entries.flatten() {
        let path = entry.path();
        let Some(stem) = path.file_stem().and_then(|s| s.to_str()) else {
            continue;
        };
        if stem == prefix_exact || stem.starts_with(&prefix_dash) {
            let _ = std::fs::remove_file(&path);
        }
    }
}

/// Detects the image extension from binary magic bytes. Returns `None` for
/// text errors (such as `"Permission denied"`) or unsupported data.
pub fn detect_image_ext(bytes: &[u8]) -> Option<&'static str> {
    if bytes.len() < 12 {
        return None;
    }
    if bytes.starts_with(&[0x89, b'P', b'N', b'G', 0x0D, 0x0A, 0x1A, 0x0A]) {
        return Some("png");
    }
    if bytes.starts_with(&[0xFF, 0xD8, 0xFF]) {
        return Some("jpg");
    }
    if bytes.starts_with(b"RIFF") && &bytes[8..12] == b"WEBP" {
        return Some("webp");
    }
    if bytes.starts_with(b"BM") {
        return Some("bmp");
    }
    if bytes.starts_with(b"GIF87a") || bytes.starts_with(b"GIF89a") {
        return Some("gif");
    }
    None
}

pub fn set_from_bytes(device: DeviceId, bytes: &[u8]) -> Result<PathBuf, String> {
    let Some(ext) = detect_image_ext(bytes) else {
        return Err(tr!("Device", "The fetched file was not a supported image (PNG, JPEG, WebP, or BMP)."));
    };
    if bytes.len() > MAX_WALLPAPER_BYTES {
        return Err(tr!("Device", "The selected wallpaper image is larger than 32 MB."));
    }
    let dir = cache_dir();
    std::fs::create_dir_all(&dir).map_err(|e| format!("Failed to create wallpaper cache: {e}"))?;
    remove_device_files(&device);

    let stamp = SystemTime::now().duration_since(UNIX_EPOCH).map_or(0, |d| d.as_millis());
    let dest = dir.join(format!("{device}-{stamp}.{ext}"));
    std::fs::write(&dest, bytes).map_err(|e| format!("Failed to write wallpaper file: {e}"))?;

    state(|st| {
        st.paths.insert(device, dest.clone());
        st.status.remove(&device);
    });
    core_host::host().hub.changed(Changes::DEVICES);
    Ok(dest)
}

pub fn set_from_file(device: DeviceId, path: &Path) -> Result<PathBuf, String> {
    let meta = std::fs::metadata(path).map_err(|e| format!("Can't read image file: {e}"))?;
    if !meta.is_file() {
        return Err(tr!("Device", "Selected item is not a file."));
    }
    if meta.len() > MAX_WALLPAPER_BYTES as u64 {
        return Err(tr!("Device", "The selected wallpaper image is larger than 32 MB."));
    }
    let bytes = std::fs::read(path).map_err(|e| format!("Can't read image file: {e}"))?;
    set_from_bytes(device, &bytes)
}

pub fn clear(device: DeviceId) {
    remove_device_files(&device);
    state(|st| {
        st.paths.remove(&device);
        st.status.remove(&device);
    });
    core_host::host().hub.changed(Changes::DEVICES);
}

fn set_status(device: DeviceId, msg: impl Into<String>) {
    let msg = msg.into();
    state(|st| {
        if msg.is_empty() {
            st.status.remove(&device);
        } else {
            st.status.insert(device, msg);
        }
    });
    core_host::host().hub.changed(Changes::DEVICES);
}

struct FetchingGuard(DeviceId);

impl Drop for FetchingGuard {
    fn drop(&mut self) {
        state(|st| st.fetching.remove(&self.0));
        core_host::host().hub.changed(Changes::DEVICES);
    }
}

/// Fetches the phone wallpaper using `"auto"`, `"adb"`, `"adb_screen"`, or `"mirror"`.
pub fn fetch(device: DeviceId, mode: String, preferred_serial: String) {
    let already_busy = state(|st| !st.fetching.insert(device));
    if already_busy {
        return;
    }
    state(|st| st.status.remove(&device));
    core_host::host().hub.changed(Changes::DEVICES);

    core_host::spawn(async move {
        let _guard = FetchingGuard(device);

        match mode.as_str() {
            "mirror" => match crate::mirror::latest_png_for_device(device) {
                Some(png) => match set_from_bytes(device, &png) {
                    Ok(_) => show_message(tr!("Device", "Updated preview wallpaper from phone mirror.")),
                    Err(e) => {
                        set_status(device, &e);
                        show_message(e);
                    }
                },
                None => {
                    let msg = tr!("Device", "Start Screen Mirror first to capture a live mirror frame.");
                    set_status(device, &msg);
                    show_message(msg);
                }
            },
            "adb_screen" => match fetch_via_adb(device, &preferred_serial, true).await {
                Ok(bytes) => match set_from_bytes(device, &bytes) {
                    Ok(_) => show_message(tr!("Device", "Captured phone screen as preview wallpaper.")),
                    Err(e) => {
                        set_status(device, &e);
                        show_message(e);
                    }
                },
                Err(msg) => {
                    set_status(device, &msg);
                    show_message(msg);
                }
            },
            _ => match fetch_via_adb(device, &preferred_serial, false).await {
                Ok(bytes) => match set_from_bytes(device, &bytes) {
                    Ok(_) => show_message(tr!("Device", "Fetched wallpaper from phone.")),
                    Err(e) => {
                        set_status(device, &e);
                        show_message(e);
                    }
                },
                Err(msg) => {
                    set_status(device, &msg);
                    show_message(msg);
                }
            },
        }
    });
}

fn normalize_model(text: &str) -> String {
    text.chars().filter(|c| c.is_ascii_alphanumeric()).map(|c| c.to_ascii_lowercase()).collect()
}

fn is_safe_device_path(path: &str) -> bool {
    path.starts_with('/')
        && !path.is_empty()
        && path.len() <= 512
        && path.bytes().all(|b| b.is_ascii_alphanumeric() || matches!(b, b'/' | b'.' | b'_' | b'-' | b' '))
}

fn extract_candidate_paths(text: &str) -> Vec<String> {
    let mut out = Vec::new();
    for line in text.lines() {
        let mut rest = line;
        for prefix in ["/storage/emulated/", "/sdcard/"] {
            while let Some(idx) = rest.find(prefix) {
                let slice = &rest[idx..];
                let end = slice
                    .find(|c: char| matches!(c, '\r' | '\n' | '\'' | '"' | ',' | ';' | ')' | ']' | '>'))
                    .unwrap_or(slice.len());
                let cand = slice[..end].trim();
                let lower = cand.to_ascii_lowercase();
                if (lower.ends_with(".jpg")
                    || lower.ends_with(".jpeg")
                    || lower.ends_with(".png")
                    || lower.ends_with(".webp")
                    || lower.ends_with(".bmp"))
                    && is_safe_device_path(cand)
                    && !out.iter().any(|existing| existing == cand)
                {
                    out.push(cand.to_owned());
                }
                rest = &slice[end.max(1)..];
            }
        }
    }
    out
}

async fn resolve_adb_serial(
    server: std::net::SocketAddr,
    device: DeviceId,
    preferred_serial: &str,
) -> Result<String, String> {
    if !preferred_serial.trim().is_empty() {
        return Ok(preferred_serial.trim().to_owned());
    }
    let devices = nectarlink_adb::list_devices(server)
        .await
        .map_err(|e| format!("ADB server unavailable: {e}"))?;
    let online: Vec<_> = devices.into_iter().filter(|d| d.is_online()).collect();
    if online.is_empty() {
        return Err(tr!(
            "Device",
            "No ADB-authorized phone found. Connect over USB or Wireless debugging, or pick an image from Phone Photos."
        ));
    }
    let (known_model, known_name) = core_host::host().hub.read(|s| {
        s.devices
            .iter()
            .find(|d| d.id == device)
            .map(|d| (d.info.model.clone().unwrap_or_default(), d.info.name.clone()))
            .unwrap_or_default()
    });
    let target_model = normalize_model(&known_model);
    let target_name = normalize_model(&known_name);

    for adb_dev in &online {
        if let Some(m) = adb_dev.model.as_deref() {
            let nm = normalize_model(m);
            if !target_model.is_empty() && (nm == target_model || nm.contains(&target_model) || target_model.contains(&nm)) {
                return Ok(adb_dev.serial.clone());
            }
            if !target_name.is_empty() && (nm == target_name || target_name.contains(&nm)) {
                return Ok(adb_dev.serial.clone());
            }
        }
        if let Ok(prop_model) = nectarlink_adb::shell_output(server, &adb_dev.serial, "getprop ro.product.model", 256).await {
            let np = normalize_model(&prop_model);
            if !target_model.is_empty() && !np.is_empty() && (np == target_model || np.contains(&target_model) || target_model.contains(&np)) {
                return Ok(adb_dev.serial.clone());
            }
            if !target_name.is_empty() && !np.is_empty() && (np == target_name || target_name.contains(&np)) {
                return Ok(adb_dev.serial.clone());
            }
        }
    }

    if online.len() == 1 {
        return Ok(online[0].serial.clone());
    }

    Err(tr!(
        "Device",
        "Multiple ADB devices are connected and none matched this phone model. Unplug other devices or pick from Phone Photos."
    ))
}

async fn try_adb_image_cmd(server: std::net::SocketAddr, serial: &str, cmd: &str) -> Option<Vec<u8>> {
    let bytes = nectarlink_adb::exec_bytes(server, serial, cmd, MAX_WALLPAPER_BYTES).await.ok()?;
    detect_image_ext(&bytes).map(|_| bytes)
}

async fn fetch_via_adb(device: DeviceId, preferred_serial: &str, capture_screen: bool) -> Result<Vec<u8>, String> {
    if nectarlink_adb::locate_adb().is_none() {
        return Err(tr!(
            "Device",
            "ADB platform-tools not installed. Download them in Settings → Phone USB install, or pick an image from Phone Photos."
        ));
    }
    let server = nectarlink_adb::configured_server();
    if !nectarlink_adb::server_answers(server) {
        let _ = tokio::task::spawn_blocking(move || nectarlink_adb::start_server(server)).await;
    }
    if !nectarlink_adb::server_answers(server) {
        return Err(tr!(
            "Device",
            "Couldn't reach the local ADB server. Connect over USB or pick an image from Phone Photos."
        ));
    }

    let serial = resolve_adb_serial(server, device, preferred_serial).await?;

    if capture_screen {
        return try_adb_image_cmd(server, &serial, "screencap -p").await.ok_or_else(|| {
            tr!("Device", "Couldn't capture the phone screen over ADB. Check USB debugging authorization on the phone.")
        });
    }

    // 1. Direct / rooted system wallpaper files
    const RAW_PATHS: &[&str] = &[
        "/data/system/users/0/wallpaper_orig",
        "/data/system/users/0/wallpaper",
        "/data/system/users/0/wallpaper_lock_orig",
        "/data/system/users/0/wallpaper_lock",
    ];
    for path in RAW_PATHS {
        if let Some(bytes) = try_adb_image_cmd(server, &serial, &format!("cat {path}")).await {
            return Ok(bytes);
        }
    }
    for path in RAW_PATHS {
        if let Some(bytes) = try_adb_image_cmd(server, &serial, &format!("su -c \"cat {path}\"")).await {
            return Ok(bytes);
        }
    }

    // 2. Readable shared-storage paths mentioned by WallpaperManager / system settings
    let mut hints = String::new();
    if let Ok(dump) = nectarlink_adb::shell_output(server, &serial, "dumpsys wallpaper", 64 * 1024).await {
        hints.push_str(&dump);
        hints.push('\n');
    }
    if let Ok(sys) = nectarlink_adb::shell_output(server, &serial, "settings list system", 64 * 1024).await {
        hints.push_str(&sys);
    }
    for cand in extract_candidate_paths(&hints).into_iter().take(6) {
        let cmd = format!("cat '{cand}'");
        if let Some(bytes) = try_adb_image_cmd(server, &serial, &cmd).await {
            return Ok(bytes);
        }
    }

    Err(tr!(
        "Device",
        "Android blocks reading system wallpaper directly without root. Use \"Capture phone screen (ADB)\" on an empty Home/Lock screen, or pick an image from Phone Photos."
    ))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn detects_supported_image_headers_and_rejects_text() {
        assert_eq!(detect_image_ext(b"\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR"), Some("png"));
        assert_eq!(detect_image_ext(b"\xFF\xD8\xFF\xE0\x00\x10JFIF\x00\x01"), Some("jpg"));
        assert_eq!(detect_image_ext(b"RIFF\x00\x00\x00\x00WEBPVP8 "), Some("webp"));
        assert_eq!(detect_image_ext(b"BM1234567890"), Some("bmp"));
        assert_eq!(detect_image_ext(b"cat: /data/system/users/0/wallpaper: Permission denied"), None);
        assert_eq!(detect_image_ext(b""), None);
    }

    #[test]
    fn extracts_only_safe_storage_image_paths() {
        let sample = "\
            lockscreen_wallpaper_path=/storage/emulated/0/Pictures/Wallpapers/my wall.jpg\n\
            bad=/sdcard/evil;rm -rf /;.png\n\
            other=/sdcard/Download/bg.png,extra\n\
        ";
        assert_eq!(
            extract_candidate_paths(sample),
            vec![
                "/storage/emulated/0/Pictures/Wallpapers/my wall.jpg".to_owned(),
                "/sdcard/Download/bg.png".to_owned(),
            ]
        );
    }
}
