// SPDX-License-Identifier: GPL-3.0-or-later
//! WinUI 3 state snapshot serializer and command dispatcher.
//!
//! Exposes a single coalesced snapshot / slice serializer (`snapshot_json`) and
//! a unified command dispatcher (`invoke_command`) so C# WinUI 3 controls and
//! view models access every feature in `nectarlink-core` and `win/*` natively.

#![allow(unsafe_code)]

use std::{
    path::PathBuf,
    sync::{
        Mutex,
        atomic::{AtomicBool, AtomicU64, Ordering},
    },
    time::{Duration, SystemTime, UNIX_EPOCH},
};

use nectarlink_actions::{Access, Decision, Grant, Scope};
use nectarlink_core::{
    CallCommand, ConnectionPath, DeviceId, DeviceKind, Direction, LinkState, MediaAction, MirrorInput,
    PairingFailure, PowerLevel, TimelineEntry, TimelineKind, TimelineQuery, TouchAction, TransferFailure,
    TransferState,
};
use serde_json::{Value, json};

use crate::{
    agents, call_audio, calls, clipboard, core_host, deck, folder_sync, hotspot,
    i18n::{self, tr},
    links, local_api, media, messages, mirror, notifications, pc_mirror, phone_install, phone_mic,
    phone_sound, photo_backup, photos, play_on_phone, qr, recordings, send_to, settings, startup,
    state::{AppRule, AppState, Changes, CoreStatus, NotificationView, PairingView, SentReply},
    transfers, updater, version, webcam, win,
};

use super::{app, report};

static SNAPSHOT_REVISION: AtomicU64 = AtomicU64::new(1);
static NEARBY_SHOWN: AtomicBool = AtomicBool::new(false);
static LAST_QR_CACHE: Mutex<(String, String, i32)> = Mutex::new((String::new(), String::new(), 0));
static UPDATE_CHANNEL_SHARED: AtomicBool = AtomicBool::new(false);

pub fn refresh_startup_async() {
    std::thread::spawn(|| {
        let shared = updater::task_installs();
        UPDATE_CHANNEL_SHARED.store(shared, Ordering::Relaxed);
        crate::bridge::notify_changes(Changes::PREFS);
    });
}

fn parse_device(s: &str) -> Option<DeviceId> {
    let t = s.trim();
    if t.is_empty() {
        return None;
    }
    t.parse().ok()
}

fn path_to_local(raw: &str) -> Option<PathBuf> {
    let trimmed = raw.trim();
    if trimmed.is_empty() {
        return None;
    }
    let normalized = if let Some(rest) = trimmed.strip_prefix("file:///") {
        percent_decode(rest).replace('/', "\\")
    } else if let Some(rest) = trimmed.strip_prefix("file://") {
        format!("\\\\{}", percent_decode(rest).replace('/', "\\"))
    } else {
        trimmed.replace('/', "\\")
    };
    if normalized.trim().is_empty() {
        None
    } else {
        Some(PathBuf::from(normalized.trim()))
    }
}

fn percent_decode(s: &str) -> String {
    let bytes = s.as_bytes();
    let mut out = Vec::with_capacity(bytes.len());
    let mut i = 0;
    while i < bytes.len() {
        if bytes[i] == b'%'
            && i + 2 < bytes.len()
            && let Ok(hex) = std::str::from_utf8(&bytes[i + 1..i + 3])
            && let Ok(val) = u8::from_str_radix(hex, 16)
        {
            out.push(val);
            i += 3;
            continue;
        }
        out.push(bytes[i]);
        i += 1;
    }
    String::from_utf8_lossy(&out).into_owned()
}

fn rule_name(rule: AppRule) -> &'static str {
    match rule {
        AppRule::Show => "show",
        AppRule::Quiet => "quiet",
        AppRule::Hidden => "hidden",
    }
}

fn pairing_failure_code(f: &PairingFailure) -> &'static str {
    match f {
        PairingFailure::Rejected => "rejected",
        PairingFailure::Declined => "declined",
        PairingFailure::Expired => "expired",
        PairingFailure::Unreachable => "unreachable",
        PairingFailure::Other(_) => "other",
    }
}

fn epoch_ms(t: SystemTime) -> f64 {
    t.duration_since(UNIX_EPOCH).map_or(0.0, |d| d.as_millis() as f64)
}

/// Serializes either every UI slice (`mask == u32::MAX`) or all slices affected by `mask`.
pub fn snapshot_json(mask: u32) -> String {
    let rev = SNAPSHOT_REVISION.fetch_add(1, Ordering::Relaxed);
    let full = mask == u32::MAX;
    let has = |c: Changes| full || (mask & c.bits()) != 0;

    let mut root = serde_json::Map::new();
    root.insert("revision".into(), json!(rev));
    root.insert("mask".into(), json!(mask));

    if has(
        Changes::STATUS
            | Changes::DEVICES
            | Changes::CAPABILITIES
            | Changes::TOGGLES
            | Changes::REMOTE
            | Changes::CONTINUITY
            | Changes::PALETTE
            | Changes::DATA_RETENTION
            | Changes::UPDATE
            | Changes::PREFS
            | Changes::RINGING
            | Changes::DEVICE_METRICS,
    ) {
        root.insert("app".into(), build_app_json());
    }
    if has(Changes::DEVICES | Changes::DEVICE_METRICS | Changes::RINGING) {
        root.insert("devices".into(), build_devices_json());
    }
    if has(Changes::PAIRING | Changes::DISCOVERED | Changes::DEVICES) {
        root.insert("pairing".into(), build_pairing_json());
    }
    if has(Changes::PREFS | Changes::STATUS) {
        root.insert("prefs".into(), build_prefs_json());
    }
    if has(Changes::DECK) {
        root.insert("deck".into(), build_deck_json());
    }
    if has(Changes::NOTIFICATIONS | Changes::DEVICES | Changes::APPS | Changes::HISTORY) {
        root.insert("notifications".into(), build_notifications_json());
    }
    if has(Changes::TRANSFERS | Changes::DEVICES | Changes::LOCALSEND | Changes::QUEUE) {
        root.insert("transfers".into(), build_transfers_json());
    }
    if has(Changes::MEDIA | Changes::DEVICES | Changes::CAPABILITIES | Changes::REMOTE | Changes::MIRROR) {
        root.insert("media".into(), build_media_json());
    }
    if has(Changes::MESSAGES | Changes::CAPABILITIES | Changes::DEVICES) {
        root.insert("messages".into(), build_messages_json());
    }
    if has(Changes::CALLS | Changes::CAPABILITIES | Changes::DEVICES) {
        root.insert("calls".into(), build_calls_json());
    }
    if has(Changes::PHOTOS | Changes::DEVICES) {
        if let Some(paths) = photos::take_share_ready()
            && win::share::show(&paths).is_err()
        {
            photos::show_share_failed();
        }
        root.insert("photos".into(), build_photos_json());
    }
    if has(Changes::MIRROR | Changes::DEVICES | Changes::CAPABILITIES | Changes::PREFS) {
        root.insert("mirror".into(), build_mirror_json());
    }
    if has(Changes::WEBCAM | Changes::DEVICES | Changes::CAPABILITIES) {
        root.insert("webcam".into(), build_webcam_json());
    }
    if has(Changes::MIC | Changes::DEVICES | Changes::CAPABILITIES) {
        root.insert("phoneMic".into(), build_phone_mic_json());
    }
    if has(Changes::PHOTO_BACKUP | Changes::DEVICES | Changes::STATUS) {
        root.insert("photoBackup".into(), build_photo_backup_json());
    }
    if has(Changes::PHONE_INSTALL | Changes::PREFS) {
        root.insert("phoneInstall".into(), build_phone_install_json());
    }
    if has(Changes::STATUS | Changes::PREFS) {
        root.insert("agents".into(), build_agents_json());
    }
    if has(Changes::CLIPBOARD | Changes::PREFS | Changes::DEVICES) {
        root.insert("clipboard".into(), build_clipboard_json());
    }
    if has(Changes::LOCALSEND | Changes::DEVICES) {
        root.insert("localsend".into(), build_localsend_json());
    }
    if has(Changes::TIMELINE | Changes::DEVICES | Changes::CLIPBOARD | Changes::STATUS) {
        root.insert("timelineRevision".into(), json!(rev));
    }
    if full {
        root.insert("i18n".into(), serde_json::from_str(&i18n::catalog_json()).unwrap_or(json!({})));
        root.insert("tokens".into(), load_tokens_json());
    }
    root.insert("report".into(), serde_json::from_str(&report::snapshot_json()).unwrap_or(json!({})));

    Value::Object(root).to_string()
}

fn load_tokens_json() -> Value {
    let raw = include_str!("../../../../docs/design/tokens.json");
    serde_json::from_str(raw).unwrap_or(json!({}))
}

fn build_app_json() -> Value {
    let ui = app::ui_state(|u| u.clone());
    let current_dev = app::selected_device(ui.current_device);
    let hub = &core_host::host().hub;

    let (
        core_status_str,
        status_err,
        devices,
        phone_colors,
        has_hotspot,
        hotspot_on,
        hotspot_name,
        is_usb,
        remote_prompt_dev,
        pc_mirror_prompt_dev,
        laser_opt,
        wake_status,
    ) = hub.read(|s| {
        let (st, err) = match s.core_status() {
            CoreStatus::Starting => ("starting", String::new()),
            CoreStatus::Ready { .. } => ("running", String::new()),
            CoreStatus::Failed(e) => ("error", e),
        };
        let colors = app::phone_colors_json(&s.devices);
        let hs = current_dev.is_some_and(|id| {
            s.matrices.get(&id).and_then(|m| m.state("device.hotspot")).is_some()
        });
        let hs_on = current_dev.is_some_and(|id| hotspot::network_for(id).is_some());
        let hs_name = current_dev.and_then(hotspot::network_for).unwrap_or_default();
        let usb = current_dev.is_some_and(|id| {
            s.device(&id).is_some_and(|d| matches!(d.link, LinkState::Online { path: ConnectionPath::Usb, .. }))
        });
        let r_prompt = s.remote_prompt.map(|id| (id.to_string(), s.name_of(&id).unwrap_or_default()));
        let pcm_prompt = s
            .pc_mirror_prompt
            .as_ref()
            .map(|(id, _)| (id.to_string(), s.name_of(id).unwrap_or_default()));
        (
            st,
            err,
            s.devices.clone(),
            colors,
            hs,
            hs_on,
            hs_name,
            usb,
            r_prompt,
            pcm_prompt,
            s.laser,
            s.wake.clone(),
        )
    });

    // Build feature matrix & toggles outside `hub.read` to prevent re-entrant lock contention.
    let feature_matrix = current_dev
        .map(build_feature_matrix)
        .unwrap_or_else(|| json!({}));
    let device_toggles = app::device_toggles_json(current_dev);

    let home_summary = current_dev.map(app::home_summary_json).unwrap_or_else(|| "{}".into());
    let (avail_version, avail_url) = updater::available()
        .map(|u| (u.version, u.notes_url))
        .unwrap_or_default();
    let whats_new_json = nectarlink_core::whats_new::json(&core_host::host().data_dir);
    let unlock_enrolled = current_dev.is_some_and(win::unlock::enrolled_with);
    let unlock_supported = win::unlock::is_registered();

    let clip_suggestion = clipboard::current_suggestion();
    let clip_suggestion_json = match clip_suggestion {
        Some((label, detail)) => json!({ "label": label, "detail": detail }),
        None => Value::Null,
    };

    let (laser_active, laser_x, laser_y) = match laser_opt {
        Some((_, x, y)) => (true, f64::from(x), f64::from(y)),
        None => (false, 0.5, 0.5),
    };

    let cont = crate::continuity_camera::view();

    let pc_mirror_active = pc_mirror::active_peer().is_some();
    let pc_mirror_device_name = pc_mirror::active_peer()
        .and_then(|id| hub.read(|s| s.name_of(&id)))
        .unwrap_or_default();

    let can_send_files = !devices.is_empty();

    json!({
        "version": env!("CARGO_PKG_VERSION"),
        "coreStatus": core_status_str,
        "coreError": status_err,
        "currentPage": ui.current_page,
        "currentDevice": ui.current_device,
        "currentDeviceId": current_dev.map(|d| d.to_string()).unwrap_or_default(),
        "pendingDial": ui.pending_dial,
        "togglesRevision": ui.toggles_revision,
        "homeSummaryRevision": ui.home_summary_revision,
        "homeSummary": serde_json::from_str::<Value>(&home_summary).unwrap_or(json!({})),
        "systemDark": ui.system_dark,
        "reduceMotion": ui.reduce_motion,
        "powerSaver": ui.power_saver,
        "wallpaperColors": serde_json::from_str::<Value>(&ui.wallpaper_colors).unwrap_or(Value::Null),
        "systemAccent": ui.system_accent,
        "phoneColors": serde_json::from_str::<Value>(&phone_colors).unwrap_or(json!([])),
        "toastsEnabled": ui.toasts_enabled,
        "doctorChecks": serde_json::from_str::<Value>(&ui.doctor_checks).unwrap_or(json!([])),
        "doctorBusy": ui.doctor_busy,
        "relayProbes": serde_json::from_str::<Value>(&ui.relay_probes).unwrap_or(json!([])),
        "relayCheckBusy": ui.relay_check_busy,
        "updateAvailable": !avail_version.is_empty(),
        "updateVersion": if ui.update_version.is_empty() { avail_version } else { ui.update_version },
        "updateUrl": avail_url,
        "updateBusy": ui.update_busy,
        "updateProgress": ui.update_progress,
        "updateStatus": ui.update_status,
        "updateChannel": updater::channel().as_str(),
        "updateCanAuto": updater::can_update(),
        "updateCanUnattended": updater::can_install_unattended(),
        "whatsNew": serde_json::from_str::<Value>(&whats_new_json).unwrap_or(json!({})),
        "commandPaletteOpen": ui.command_palette_open,
        "commandPaletteResults": serde_json::from_str::<Value>(&ui.command_palette_results).unwrap_or(json!([])),
        "commandPaletteOpenMs": ui.command_palette_open_ms,
        "dataRetentionSummary": serde_json::from_str::<Value>(&ui.data_retention_summary).unwrap_or(json!({})),
        "shelfOpen": ui.shelf_open,
        "shelfItems": serde_json::from_str::<Value>(&ui.shelf_items).unwrap_or(json!([])),
        "apkQrPath": ui.apk_qr_path,
        "apkQrSize": ui.apk_qr_size,
        "apkQrSvg": ui.apk_qr_svg,
        "guideDocOpen": ui.guide_doc_open,
        "guideDocTitle": ui.guide_doc_title,
        "guideDocBody": ui.guide_doc_body,
        "highlightTarget": ui.highlight_target,
        "featureMatrix": feature_matrix,
        "deviceToggles": device_toggles,
        "deviceHasHotspot": has_hotspot,
        "deviceHotspot": hotspot_on,
        "deviceHotspotName": hotspot_name,
        "deviceIsUsb": is_usb,
        "deviceUnlockSupported": unlock_supported,
        "deviceUnlockEnrolled": unlock_enrolled,
        "remotePromptDevice": remote_prompt_dev.as_ref().map(|(id, _)| id.as_str()).unwrap_or(""),
        "remotePromptName": remote_prompt_dev.as_ref().map(|(_, n)| n.as_str()).unwrap_or(""),
        "remotePromptOpen": remote_prompt_dev.is_some(),
        "pcMirrorPromptDevice": pc_mirror_prompt_dev.as_ref().map(|(id, _)| id.as_str()).unwrap_or(""),
        "pcMirrorPromptName": pc_mirror_prompt_dev.as_ref().map(|(_, n)| n.as_str()).unwrap_or(""),
        "pcMirrorPromptOpen": pc_mirror_prompt_dev.is_some(),
        "pcMirrorActive": pc_mirror_active,
        "pcMirrorDeviceName": pc_mirror_device_name,
        "pcMirrorWatching": pc_mirror::is_watching(),
        "pcMirrorCaptureAvailable": pc_mirror::is_capture_available(),
        "laserActive": laser_active,
        "laserX": laser_x,
        "laserY": laser_y,
        "continuityPromptOpen": cont.paste_prompt_visible,
        "continuityPromptTitle": cont.paste_prompt_title,
        "continuityPromptBody": cont.paste_prompt_body,
        "continuityPromptTarget": cont.paste_target_name,
        "continuityBusy": cont.busy,
        "continuityMode": cont.mode,
        "continuityStatus": cont.status,
        "clipSuggestion": clip_suggestion_json,
        "canSendFiles": can_send_files,
        "popupsPaused": notifications::paused(),
        "wakeOnLan": {
            "ready": matches!(wake_status.state_str(), "enabled"),
            "state": wake_status.state_str(),
            "adapters": wake_status.adapters.iter().map(|a| json!({
                "name": a.label,
                "wired": a.wired,
                "mac": a.mac,
                "broadcasts": a.broadcasts,
                "support": a.support.as_str(),
            })).collect::<Vec<_>>(),
        },
    })
}

fn build_feature_matrix(device: DeviceId) -> Value {
    let mut map = serde_json::Map::new();
    for feat in [
        "notifications.feed",
        "notifications.actions",
        "messages.sms",
        "messages.calls",
        "calls.audio",
        "photos.gallery",
        "clipboard.sync",
        "files.transfer",
        "files.storage",
        "mirror.screen",
        "mirror.apps",
        "mirror.control",
        "media.control",
        "media.sound",
        "media.play_on_phone",
        "camera.webcam",
        "camera.continuity",
        "mic.phone",
        "device.find",
        "device.toggles",
        "device.hotspot",
        "device.lock",
        "deck.controls",
    ] {
        map.insert(feat.into(), app::feature_state_json(Some(device), feat));
    }
    Value::Object(map)
}

fn build_devices_json() -> Value {
    let list: Vec<Value> = core_host::host().hub.read(|s| {
        s.devices
            .iter()
            .map(|d| {
                let id_str = d.id.to_string();
                let nickname = settings::nickname_for(&id_str);
                let shown_name = nickname.as_deref().unwrap_or(&d.info.name);
                let (online, path, rtt_ms) = match &d.link {
                    LinkState::Online { path, rtt_ms } => (
                        true,
                        match path {
                            ConnectionPath::Lan => "lan",
                            ConnectionPath::Usb => "usb",
                            ConnectionPath::Relay => "relay",
                        },
                        *rtt_ms as i32,
                    ),
                    LinkState::Offline { .. } => (false, "offline", -1),
                    LinkState::Connecting => (false, "connecting", -1),
                };
                let (bat, chg, plugged, full_in, cell_bars, cell_type, wifi_bars) =
                    match &d.battery {
                        Some(b) => (
                            b.level as i32,
                            b.charging,
                            b.plugged.as_deref().unwrap_or(""),
                            b.full_in.map(|m| m as i32).unwrap_or(-1),
                            b.cell_bars.map(i32::from).unwrap_or(-1),
                            b.cell_type.as_deref().unwrap_or(""),
                            b.wifi_bars.map(i32::from).unwrap_or(-1),
                        ),
                        None => (-1, false, "", -1, -1, "", -1),
                    };
                let kind_str = match d.info.kind {
                    DeviceKind::Phone => "phone",
                    DeviceKind::Tablet => "tablet",
                    DeviceKind::Desktop => "desktop",
                    DeviceKind::Laptop => "laptop",
                    DeviceKind::Unknown => "unknown",
                };
                let power_str = match d.power {
                    PowerLevel::Basic => "basic",
                    PowerLevel::Elevated => "elevated",
                    PowerLevel::Assist => "assist",
                    PowerLevel::NotApplicable => "na",
                    PowerLevel::Unknown => "basic",
                };
                let accent = d
                    .info
                    .accent
                    .map(|c| format!("#{:06X}", c & 0x00FF_FFFF))
                    .unwrap_or_default();
                let screen = d
                    .info
                    .screen
                    .as_ref()
                    .and_then(|sc| serde_json::to_value(sc).ok())
                    .unwrap_or(Value::Null);
                let phone_toggles = match s.toggles.get(&d.id) {
                    Some(t) => json!({
                        "hasData": true,
                        "wifi": t.wifi,
                        "bluetooth": t.bluetooth,
                        "dnd": t.dnd,
                        "flashlight": t.flashlight.unwrap_or(false),
                        "hasFlashlight": t.flashlight.is_some(),
                        "ringer": t.ringer,
                        "volume": t.volume,
                        "brightness": t.brightness,
                    }),
                    None => json!({ "hasData": false }),
                };
                let wallpaper_path = crate::phone_wallpaper::path_for(&d.id)
                    .map(|p| p.to_string_lossy().into_owned())
                    .unwrap_or_default();
                let wallpaper_busy = crate::phone_wallpaper::is_fetching(&d.id);
                let wallpaper_status = crate::phone_wallpaper::status_for(&d.id);
                let mirror_has_frame = crate::mirror::has_latest_frame(&d.id);
                json!({
                    "id": id_str,
                    "name": shown_name,
                    "rawName": d.info.name,
                    "hasNickname": nickname.is_some(),
                    "kind": kind_str,
                    "isPhone": matches!(d.info.kind, DeviceKind::Phone | DeviceKind::Tablet | DeviceKind::Unknown),
                    "os": d.info.os,
                    "osVer": d.info.os_ver,
                    "model": d.info.model.as_deref().unwrap_or(""),
                    "accent": accent,
                    "screen": screen,
                    "wallpaperPath": wallpaper_path,
                    "wallpaperFetchBusy": wallpaper_busy,
                    "wallpaperStatus": wallpaper_status,
                    "mirrorHasFrame": mirror_has_frame,
                    "pairedAt": d.paired_at,
                    "online": online,
                    "path": path,
                    "rttMs": rtt_ms,
                    "battery": bat,
                    "batteryCharging": chg,
                    "batteryPlugged": plugged,
                    "batteryFullIn": full_in,
                    "cellBars": cell_bars,
                    "cellType": cell_type,
                    "wifiBars": wifi_bars,
                    "powerLevel": power_str,
                    "reconnect": d.reconnect,
                    "ringing": s.ringing_devices.contains(&d.id),
                    "phoneToggles": phone_toggles,
                })
            })
            .collect()
    });
    Value::Array(list)
}

fn build_pairing_json() -> Value {
    core_host::host().hub.read(|s| {
        let peer_id = match &s.pairing {
            PairingView::Connecting { peer }
            | PairingView::Comparing { peer, .. }
            | PairingView::Confirmed { peer, .. } => Some(*peer),
            PairingView::Paired { device, .. } => Some(*device),
            _ => None,
        };
        let resolved_name = peer_id.and_then(|p| s.name_of(&p)).unwrap_or_default();
        let (state_str, uri, expires_at, code, peer_name, failure) = match &s.pairing {
            PairingView::Idle => ("idle", String::new(), 0.0, String::new(), String::new(), ""),
            PairingView::Starting => ("starting", String::new(), 0.0, String::new(), String::new(), ""),
            PairingView::Hosting { uri, expires_at } => {
                ("hosting", uri.clone(), epoch_ms(*expires_at), String::new(), String::new(), "")
            }
            PairingView::Connecting { .. } => {
                ("connecting", String::new(), 0.0, String::new(), resolved_name.clone(), "")
            }
            PairingView::Comparing { code, .. } => {
                ("comparing", String::new(), 0.0, code.clone(), resolved_name.clone(), "")
            }
            PairingView::Confirmed { code, .. } => {
                ("confirmed", String::new(), 0.0, code.clone(), resolved_name.clone(), "")
            }
            PairingView::Paired { name, .. } => {
                ("paired", String::new(), 0.0, String::new(), name.clone(), "")
            }
            PairingView::Failed(f) => {
                ("failed", String::new(), 0.0, String::new(), String::new(), pairing_failure_code(f))
            }
        };

        let (qr_path, qr_size) = if uri.is_empty() {
            (String::new(), 0)
        } else {
            let mut cache = LAST_QR_CACHE.lock().unwrap_or_else(|e| e.into_inner());
            if cache.0 != uri {
                if let Ok(encoded) = qr::encode(&uri) {
                    *cache = (uri.clone(), encoded.path, encoded.size as i32);
                } else {
                    *cache = (uri.clone(), String::new(), 0);
                }
            }
            (cache.1.clone(), cache.2)
        };

        let nearby: Vec<Value> = s
            .discovered
            .iter()
            .filter(|d| !s.devices.iter().any(|p| p.id == d.id))
            .map(|d| {
                json!({
                    "id": d.id.to_string(),
                    "name": d.name.as_deref().unwrap_or("Android device"),
                })
            })
            .collect();

        json!({
            "state": state_str,
            "qrPath": qr_path,
            "qrSize": qr_size,
            "expiresAt": expires_at,
            "code": code,
            "peerName": peer_name,
            "failure": failure,
            "nearby": nearby,
        })
    })
}

fn build_prefs_json() -> Value {
    let dir = &core_host::host().data_dir;
    let st = settings::Settings::load(dir);
    let color_mode = match st.color_mode {
        settings::ColorMode::Light => "light",
        settings::ColorMode::Dark => "dark",
        settings::ColorMode::System => "system",
    };
    let rec_dir = recordings::effective_folder(st.recordings_folder.as_deref());
    let rec_default = recordings::default_folder();
    let dl_dir = crate::core_host::downloads_dir();
    let screenshots_dir = crate::core_host::downloads_dir();
    let photos_dir = crate::core_host::downloads_dir();
    let token_path = local_api::token_file_path();
    let (obs_port, obs_pw) = settings::obs_settings();

    json!({
        "theme": st.theme.name(),
        "seed": st.seed,
        "colorMode": color_mode,
        "amoled": st.amoled,
        "backdrop": st.backdrop,
        "combMotion": st.comb_motion,
        "closeToTray": st.close_to_tray,
        "trayOneColour": st.tray_one_colour,
        "autoClipboard": st.auto_clipboard,
        "clipboardHistory": st.clipboard_history,
        "suggestClipboardActions": st.suggest_clipboard_actions,
        "autoCopyOtp": st.auto_copy_otp,
        "pauseMediaOnCall": st.pause_media_on_call,
        "language": st.language,
        "muteWhileWatched": st.mute_while_watched,
        "syncDnd": st.sync_dnd,
        "enterSends": st.enter_sends,
        "rightClickLongPress": st.right_click_long_press,
        "hideMirrorToolbar": st.hide_mirror_toolbar,
        "quietWhenLocked": st.quiet_when_locked,
        "holdWhenLocked": st.hold_when_locked,
        "sendToMenu": st.send_to_menu,
        "autoUpdate": st.auto_update,
        "installUpdates": st.install_updates,
        "updateChannel": st.update_channel,
        "updateChannelShared": UPDATE_CHANNEL_SHARED.load(Ordering::Relaxed),
        "batteryAlerts": st.battery_alerts,
        "batteryFullThreshold": st.battery_full_threshold,
        "batteryLowThreshold": st.battery_low_threshold,
        "phoneSoundBufferMs": st.phone_sound_buffer_ms,
        "uiScale": st.ui_scale,
        "uiScales": settings::UI_SCALES,
        "keepScreenOff": settings::keep_screen_off_phones(),
        "deviceNicknames": settings::device_nicknames(),
        "pinnedApps": settings::pinned_apps_all(),
        "startWithWindows": startup::effective(st.start_with_windows),
        "recordingsFolder": rec_dir.to_string_lossy(),
        "recordingsFolderIsDefault": rec_dir == rec_default,
        "recordingsFormat": st.recordings_format.as_str(),
        "downloadsFolder": dl_dir.to_string_lossy(),
        "screenshotsFolder": screenshots_dir.to_string_lossy(),
        "photosFolder": photos_dir.to_string_lossy(),
        "dataDir": dir.to_string_lossy(),
        "logsDir": crate::logging::logs_dir(dir).to_string_lossy(),
        "timelineRetentionDays": st.timeline_retention_days,
        "continuityPhotoHotkey": st.continuity_photo_hotkey,
        "continuityScanHotkey": st.continuity_scan_hotkey,
        "commandPaletteHotkey": st.command_palette_hotkey,
        "shelfHotkey": st.shelf_hotkey,
        "localApiEnabled": st.local_api_enabled,
        "localApiPort": st.local_api_port,
        "localApiPrivate": st.local_api_private,
        "localApiTokenPath": token_path.to_string_lossy(),
        "agentAccessEnabled": st.agent_access_enabled,
        "agentReadOnly": st.agent_read_only,
        "clipboardAutoCopyReceivedFiles": st.clipboard_auto_copy_received_files,
        "openSharedFiles": st.open_shared_files,
        "syncWifiOnly": st.sync_wifi_only,
        "syncPasswords": st.sync_passwords,
        "keepPopupsPaused": st.keep_popups_paused,
        "popupsPaused": notifications::paused(),
        "usbDebugging": st.usb_debugging,
        "usbDebuggingAtStart": settings::usb_debugging(),
        "notificationHistoryDays": st.notification_history_days,
        "mirrorQuality": st.mirror_quality,
        "mirrorFps": st.mirror_fps,
        "flowFollowClipboard": st.flow_follow_clipboard,
        "phoneBeside": st.phone_beside,
        "phoneBesideSide": st.phone_beside_side,
        "folderSyncRules": settings::folder_sync_rules(),
        "automationRules": settings::automation_rules(),
        "nectarIsland": st.nectar_island,
        "nectarIslandCalls": st.nectar_island_calls,
        "nectarIslandTransfers": st.nectar_island_transfers,
        "nectarIslandMedia": st.nectar_island_media,
        "nectarIslandNotifications": st.nectar_island_notifications,
        "nectarIslandPosition": st.nectar_island_position,
        "nectarIslandLook": st.nectar_island_look,
        "nectarIslandPcMedia": st.nectar_island_pc_media,
        "streamPcAudioToPhone": st.stream_pc_audio_to_phone,
        "sharePcStorage": st.share_pc_storage,
        "forwardPcNotificationsToPhone": st.forward_pc_notifications_to_phone,
        "awayMode": st.away_mode,
        "networkInterface": st.network_interface,
        "walkthroughCompleted": st.walkthrough_completed,
        "walkthroughStep": st.walkthrough_step,
        "whatsNewDismissed": st.whats_new_dismissed,
        "hiveInviteDismissed": st.hive_invite_dismissed,
        "starPromptDone": st.star_prompt_done,
        "starPromptUpdated": st.star_prompt_updated,
        "starPromptMinutes": st.star_prompt_minutes,
        "communityPromptDone": st.community_prompt_done,
        "communityPromptDue": st.community_prompt_due,
        "communityPromptCount": st.community_prompt_count,
        "obsPort": obs_port,
        "obsHasPassword": obs_pw.is_some(),
        "languages": serde_json::from_str::<Value>(&i18n::languages_json()).unwrap_or(json!([])),
        "canRecord": win::mp4::can_record(win::mp4::VideoCodec::H264),
    })
}

fn build_deck_json() -> Value {
    let cfg = deck::config();
    let st = deck::state();
    let pages: Vec<Value> = cfg
        .pages
        .iter()
        .map(|p| {
            let tiles: Vec<Value> = p
                .tiles
                .iter()
                .map(|t| {
                    let (param, ctrl, alt, shift, win, subtitle) = deck_action_fields(&t.action);
                    json!({
                        "id": t.id,
                        "label": t.label,
                        "icon": t.icon,
                        "color": t.color,
                        "kind": deck_action_kind(&t.action),
                        "param": param,
                        "ctrl": ctrl,
                        "alt": alt,
                        "shift": shift,
                        "win": win,
                        "status": "",
                        "subtitle": subtitle,
                    })
                })
                .collect();
            json!({
                "id": p.id,
                "name": p.name,
                "tiles": tiles,
            })
        })
        .collect();
    let mic_state = match st.mic_muted {
        Some(true) => 2,
        Some(false) => 1,
        None => 0,
    };
    json!({
        "volume": st.volume,
        "muted": st.muted,
        "micState": mic_state,
        "playing": st.playing,
        "pages": pages,
    })
}

fn deck_action_kind(a: &nectarlink_core::DeckAction) -> &'static str {
    use nectarlink_core::{DeckAction, deck_kinds};
    match a {
        DeckAction::MediaPlayPause => deck_kinds::MEDIA_PLAY_PAUSE,
        DeckAction::MediaNext => deck_kinds::MEDIA_NEXT,
        DeckAction::MediaPrevious => deck_kinds::MEDIA_PREVIOUS,
        DeckAction::VolumeUp => deck_kinds::VOLUME_UP,
        DeckAction::VolumeDown => deck_kinds::VOLUME_DOWN,
        DeckAction::VolumeMute => deck_kinds::VOLUME_MUTE,
        DeckAction::MicMute => deck_kinds::MIC_MUTE,
        DeckAction::LockPc => deck_kinds::LOCK_PC,
        DeckAction::ShowDesktop => deck_kinds::SHOW_DESKTOP,
        DeckAction::SwitchWindow => deck_kinds::SWITCH_WINDOW,
        DeckAction::Screenshot => deck_kinds::SCREENSHOT,
        DeckAction::Shortcut { .. } => deck_kinds::SHORTCUT,
        DeckAction::OpenUrl { .. } => deck_kinds::OPEN_URL,
        DeckAction::TypeText { .. } => deck_kinds::TYPE_TEXT,
        DeckAction::LaunchApp { .. } => deck_kinds::LAUNCH_APP,
        DeckAction::RunCommand { .. } => deck_kinds::RUN_COMMAND,
    }
}

fn deck_action_fields(action: &nectarlink_core::DeckAction) -> (String, bool, bool, bool, bool, String) {
    use nectarlink_core::{DeckAction, KeyMod};
    match action {
        DeckAction::MediaPlayPause | DeckAction::MediaNext | DeckAction::MediaPrevious => {
            (String::new(), false, false, false, false, tr!("Deck", "Media key"))
        }
        DeckAction::VolumeUp => (String::new(), false, false, false, false, "+2%".into()),
        DeckAction::VolumeDown => (String::new(), false, false, false, false, "−2%".into()),
        DeckAction::VolumeMute => (String::new(), false, false, false, false, "Speaker".into()),
        DeckAction::MicMute => (String::new(), false, false, false, false, "Microphone".into()),
        DeckAction::LockPc => (String::new(), false, false, false, false, "Win + L".into()),
        DeckAction::ShowDesktop => (String::new(), false, false, false, false, "Win + D".into()),
        DeckAction::SwitchWindow => (String::new(), false, false, false, false, tr!("Deck", "Alt + Tab")),
        DeckAction::Screenshot => (String::new(), false, false, false, false, "Win + Shift + S".into()),
        DeckAction::Shortcut { key, mods } => {
            let ctrl = mods.contains(&KeyMod::Ctrl);
            let alt = mods.contains(&KeyMod::Alt);
            let shift = mods.contains(&KeyMod::Shift);
            let win = mods.contains(&KeyMod::Win);
            let mut parts = Vec::new();
            if ctrl { parts.push("Ctrl".to_owned()); }
            if alt { parts.push("Alt".to_owned()); }
            if shift { parts.push("Shift".to_owned()); }
            if win { parts.push("Win".to_owned()); }
            parts.push(key.to_ascii_uppercase());
            (key.clone(), ctrl, alt, shift, win, parts.join(" + "))
        }
        DeckAction::OpenUrl { url } => (url.clone(), false, false, false, false, url.clone()),
        DeckAction::TypeText { text } => {
            let preview: String = text.chars().take(28).collect();
            (text.clone(), false, false, false, false, format!("\"{preview}\""))
        }
        DeckAction::LaunchApp { path } => {
            let file_name = std::path::Path::new(path).file_name().and_then(|s| s.to_str()).unwrap_or(path).to_owned();
            (path.clone(), false, false, false, false, file_name)
        }
        DeckAction::RunCommand { command } => {
            let preview: String = command.chars().take(28).collect();
            (command.clone(), false, false, false, false, format!("cmd · {preview}"))
        }
    }
}

fn build_notifications_json() -> Value {
    core_host::host().hub.read(|s| {
        let visible: Vec<&NotificationView> =
            s.notifications.iter().filter(|v| s.app_rule(&v.notification.app) != AppRule::Hidden).collect();
        let mut rows = Vec::with_capacity(visible.len());
        let mut seen_groups: std::collections::HashSet<(DeviceId, String)> = std::collections::HashSet::new();

        for view in &visible {
            if view.notification.live.is_some() {
                rows.push(notif_row_json(s, view, 1, 0, false, String::new()));
                continue;
            }
            let gk = (view.device, view.notification.app.clone());
            if !seen_groups.insert(gk.clone()) {
                continue;
            }
            let members: Vec<&NotificationView> = visible
                .iter()
                .copied()
                .filter(|v| v.notification.live.is_none() && v.device == gk.0 && v.notification.app == gk.1)
                .collect();
            let count = members.len() as i32;
            let expanded = count > 1 && s.is_app_expanded(&gk.1);
            let summary = if count > 1 {
                members[1..]
                    .iter()
                    .filter_map(|v| {
                        v.notification
                            .title
                            .as_deref()
                            .filter(|t| !t.trim().is_empty())
                            .or_else(|| v.notification.text.as_deref().filter(|t| !t.trim().is_empty()))
                    })
                    .map(str::trim)
                    .take(2)
                    .collect::<Vec<_>>()
                    .join(" · ")
            } else {
                String::new()
            };
            rows.push(notif_row_json(s, members[0], count, 0, expanded, summary));
            if expanded {
                for (idx, m) in members.iter().enumerate().skip(1) {
                    rows.push(notif_row_json(s, m, count, idx as i32, true, String::new()));
                }
            }
        }

        let total_count: usize = visible.len();
        let latest = visible
            .iter()
            .filter(|v| v.notification.live.is_none())
            .max_by_key(|v| v.notification.when);
        let latest_app = latest
            .map(|v| {
                if v.notification.app_name.trim().is_empty() {
                    v.notification.app.clone()
                } else {
                    v.notification.app_name.clone()
                }
            })
            .unwrap_or_default();
        let latest_title = latest.and_then(|v| v.notification.title.clone()).unwrap_or_default();
        let latest_icon = latest
            .and_then(|v| s.app_icons.get(&v.notification.app).map(|p| p.to_string_lossy().into_owned()))
            .unwrap_or_default();

        let mut apps: Vec<(&String, &String)> = s.app_names.iter().collect();
        apps.sort_by_key(|(app, name)| (name.to_lowercase(), (*app).clone()));
        let apps_json: Vec<Value> = apps
            .into_iter()
            .map(|(app, name)| {
                json!({
                    "app": app,
                    "name": name,
                    "icon": s.app_icons.get(app).map(|p| p.to_string_lossy().into_owned()).unwrap_or_default(),
                    "rule": rule_name(s.app_rule(app)),
                })
            })
            .collect();

        let history_json: Vec<Value> = s
            .history
            .iter()
            .filter(|h| s.app_rule(&h.notification.app) != AppRule::Hidden)
            .map(|h| {
                let n = &h.notification;
                json!({
                    "deviceId": h.device.to_string(),
                    "deviceName": s.name_of(&h.device).unwrap_or_default(),
                    "app": n.app,
                    "appName": n.app_name,
                    "title": n.title.as_deref().unwrap_or(""),
                    "text": n.text.as_deref().unwrap_or(""),
                    "sub": n.sub.as_deref().unwrap_or(""),
                    "when": n.when,
                    "removedAt": h.removed_at,
                    "iconPath": s.app_icons.get(&n.app).map(|p| p.to_string_lossy().into_owned()).unwrap_or_default(),
                })
            })
            .collect();

        json!({
            "count": total_count,
            "historyEnabled": s.history_enabled,
            "undoLabel": s.last_dismissed_label,
            "latestApp": latest_app,
            "latestTitle": latest_title,
            "latestIcon": latest_icon,
            "apps": apps_json,
            "rows": rows,
            "history": history_json,
        })
    })
}

fn notif_row_json(
    s: &AppState,
    view: &NotificationView,
    group_count: i32,
    group_index: i32,
    group_expanded: bool,
    group_summary: String,
) -> Value {
    let n = &view.notification;
    let reply = n.actions.iter().find(|a| a.reply);
    let replies: &[SentReply] = s.replies.get(&(view.device, n.key.clone())).map(Vec::as_slice).unwrap_or(&[]);
    let live = n.live.as_ref().map(|l| {
        json!({
            "progress": l.progress.unwrap_or(0),
            "max": l.max.unwrap_or(0),
            "indeterminate": l.indeterminate,
            "chip": l.chip.as_deref().unwrap_or(""),
            "segments": l.segments.iter().map(|seg| json!({
                "length": seg.length,
                "color": seg.color.map(|c| format!("#{:06X}", c & 0x00FF_FFFF)).unwrap_or_default(),
            })).collect::<Vec<_>>(),
            "points": l.points.iter().map(|pt| json!({
                "position": pt.position,
                "color": pt.color.map(|c| format!("#{:06X}", c & 0x00FF_FFFF)).unwrap_or_default(),
            })).collect::<Vec<_>>(),
            "chronometer": l.chronometer,
            "countdown": l.countdown,
            "when": n.when,
        })
    });
    json!({
        "deviceId": view.device.to_string(),
        "deviceName": s.name_of(&view.device).unwrap_or_default(),
        "key": n.key,
        "app": n.app,
        "appName": n.app_name,
        "title": n.title.as_deref().unwrap_or(""),
        "text": n.text.as_deref().unwrap_or(""),
        "sub": n.sub.as_deref().unwrap_or(""),
        "when": n.when,
        "iconPath": s.app_icons.get(&n.app).map(|p| p.to_string_lossy().into_owned()).unwrap_or_default(),
        "imagePath": s.notification_images.get(&(view.device, n.key.clone())).map(|p| p.to_string_lossy().into_owned()).unwrap_or_default(),
        "actions": n.actions.iter().filter(|a| !a.reply).map(|a| json!({ "id": a.id, "title": a.title })).collect::<Vec<_>>(),
        "replyAction": reply.map_or("", |a| a.id.as_str()),
        "replyLabel": reply.map_or("", |a| a.title.as_str()),
        "silent": n.silent,
        "ongoing": n.ongoing,
        "replies": replies.iter().map(|r| json!({ "text": r.text, "pending": r.pending })).collect::<Vec<_>>(),
        "otpCode": notifications::notification_otp(n).unwrap_or_default(),
        "live": live,
        "groupCount": group_count,
        "groupIndex": group_index,
        "groupExpanded": group_expanded,
        "groupSummary": group_summary,
    })
}

fn build_transfers_json() -> Value {
    let localsend_peers = core_host::node().map(|n| n.localsend_peers()).unwrap_or_default();
    let (rows, active_count): (Vec<Value>, usize) = core_host::host().hub.read(|s| {
        let mut active = 0usize;
        let rows = s
            .transfers
            .iter()
            .map(|view| {
                let t = &view.transfer;
                let status = match &t.state {
                    TransferState::Requested => { active += 1; "requested" }
                    TransferState::Waiting => { active += 1; "waiting" }
                    TransferState::Running => { active += 1; "running" }
                    TransferState::Paused => { active += 1; "paused" }
                    TransferState::Done { .. } => "done",
                    TransferState::Failed(_) => "failed",
                    TransferState::Cancelled => "cancelled",
                };
                let reason = match &t.state {
                    TransferState::Failed(TransferFailure::Denied) => "denied",
                    TransferState::Failed(TransferFailure::Unreachable) => "unreachable",
                    TransferState::Failed(TransferFailure::NoSpace) => "noSpace",
                    TransferState::Failed(TransferFailure::Integrity) => "integrity",
                    TransferState::Failed(TransferFailure::Interrupted) => "interrupted",
                    TransferState::Failed(TransferFailure::Other(_)) => "other",
                    _ => "",
                };
                let saved_path = match &t.state {
                    TransferState::Done { saved } => saved.first().map(|p| p.to_string_lossy().into_owned()).unwrap_or_default(),
                    _ => String::new(),
                };
                let title = match t.names.as_slice() {
                    [one] => one.clone(),
                    [first, rest @ ..] => format!("{first} and {} more", rest.len()),
                    [] => String::new(),
                };
                let device_name = s
                    .name_of(&t.device)
                    .or_else(|| localsend_peers.iter().find(|p| p.id == t.device).map(|p| p.alias.clone()))
                    .unwrap_or_default();
                let progress = if t.total == 0 { 1.0 } else { t.done as f64 / t.total as f64 };
                json!({
                    "transferId": t.id,
                    "deviceId": t.device.to_string(),
                    "deviceName": device_name,
                    "incoming": t.direction == Direction::Incoming,
                    "recording": t.recording,
                    "title": title,
                    "fileCount": t.files,
                    "total": t.total,
                    "done": t.done,
                    "progress": progress,
                    "rate": view.rate,
                    "status": status,
                    "reason": reason,
                    "savedPath": saved_path,
                })
            })
            .collect();
        (rows, active)
    });
    let (queue_json, waiting_count, queue_count, has_failed) = transfers::send_queue_snapshot();
    json!({
        "count": rows.len(),
        "active": active_count,
        "sendQueue": serde_json::from_str::<Value>(&queue_json).unwrap_or(json!([])),
        "waitingCount": waiting_count,
        "queueCount": queue_count,
        "hasFailedOrExpiredQueue": has_failed,
        "rows": rows,
    })
}

fn build_media_json() -> Value {
    let mut views = core_host::host().hub.read(|s| s.media.clone());
    views.extend(media::local_views());

    let mut phone_sound_states = serde_json::Map::new();
    core_host::host().hub.read(|s| {
        for d in &s.devices {
            phone_sound_states.insert(d.id.to_string(), json!(phone_sound::state(&d.id)));
        }
    });

    let rows: Vec<Value> = views
        .iter()
        .map(|row| {
            let p = &row.player;
            let dev_id = if row.device == media::LOCAL_DEVICE {
                "local".to_owned()
            } else {
                row.device.to_string()
            };
            let has = |act: &str| p.actions.iter().any(|a| a == act);
            json!({
                "deviceId": dev_id,
                "playerId": p.id,
                "app": p.app,
                "title": p.title.as_deref().unwrap_or(""),
                "artist": p.artist.as_deref().unwrap_or(""),
                "album": p.album.as_deref().unwrap_or(""),
                "playing": p.playing,
                "duration": p.duration.map_or(-1.0, |d| d as f64),
                "position": p.position.map_or(-1.0, |d| d as f64),
                "positionAt": epoch_ms(row.at),
                "artPath": row.art.as_ref().map(|path| path.to_string_lossy().into_owned()).unwrap_or_default(),
                "canPlay": has("play"),
                "canPause": has("pause"),
                "canNext": has("next"),
                "canPrevious": has("previous"),
                "canSeek": has("seek"),
            })
        })
        .collect();

    let (active_art, active_colors) = match media::active_artwork() {
        Some((p, c)) => (
            p.to_string_lossy().into_owned(),
            serde_json::from_str::<Value>(&c).unwrap_or(Value::Null),
        ),
        None => (String::new(), Value::Null),
    };
    let play_phone_name = play_on_phone::phone().map(|(_, n)| n).unwrap_or_default();
    let can_play_on_phone = !play_phone_name.is_empty() && play_on_phone::watchable().is_some();

    json!({
        "count": rows.len(),
        "activeArt": active_art,
        "activeColors": active_colors,
        "playOnPhoneName": play_phone_name,
        "canPlayOnPhone": can_play_on_phone,
        "playOnPhoneState": play_on_phone::state(),
        "phoneSoundStates": Value::Object(phone_sound_states),
        "rows": rows,
    })
}

fn build_messages_json() -> Value {
    let v = messages::view();
    let draft_text = v
        .thread
        .as_deref()
        .map(messages::draft)
        .unwrap_or_default();
    json!({
        "device": v.device.map(|d| d.to_string()).unwrap_or_default(),
        "status": v.status.as_str(),
        "threads": v.threads,
        "thread": v.thread.unwrap_or_default(),
        "messages": v.messages,
        "more": v.more,
        "loadingOlder": v.loading_older,
        "sending": v.sending,
        "composeTo": v.compose_to.unwrap_or_default(),
        "composeName": v.compose_name.unwrap_or_default(),
        "appFilter": v.app_filter,
        "availableApps": v.available_apps,
        "hasChatThreads": v.has_chat_threads,
        "currentIsChat": v.current_is_chat,
        "currentAppName": v.current_app_name,
        "currentCanReply": v.current_can_reply,
        "currentIsGroup": v.current_is_group,
        "hasAttachment": v.has_attachment,
        "attachmentPreview": v.attachment_preview,
        "attachmentName": v.attachment_name,
        "attachmentSize": v.attachment_size,
        "attachmentError": v.attachment_error,
        "sims": v.sims,
        "sim": v.sim,
        "draft": draft_text,
    })
}

fn build_calls_json() -> Value {
    let view = calls::view();
    let preferred_device = view.device;
    let active_call = calls::active_or_ringing(preferred_device);
    let call_device = active_call.as_ref().map(|(d, _, _)| *d).or(preferred_device);
    let is_ringing = active_call.as_ref().is_some_and(|(_, _, r)| *r);
    let call = active_call.map(|(_, c, _)| c);
    let controls = call.as_ref().and_then(|c| c.controls);
    let (caller, number) = match &call {
        Some(c) => {
            let name = c.name.clone().filter(|n| !n.trim().is_empty());
            let num = c.number.clone().filter(|n| !n.trim().is_empty());
            match name {
                Some(n) => (n, num.unwrap_or_default()),
                None => (num.unwrap_or_else(|| tr!("Calls", "Unknown caller")), String::new()),
            }
        }
        None => Default::default(),
    };
    let app = call.as_ref().and_then(|c| c.app.clone()).unwrap_or_default();
    let can_end = call_device.zip(call.as_ref()).is_some_and(|(d, c)| calls::can_control_call(d, c));
    let can_dial = preferred_device.or(call_device).is_some_and(calls::can_dial);
    let ctrl_vals = controls.unwrap_or_default();
    let hfp = calls::hfp_status(call_device);
    let here = call_device.is_some_and(|d| call_audio::phase().device() == Some(d));
    let call_audio_avail = !is_ringing && app.is_empty() && (here || call_device.is_some_and(call_audio::available));
    let (mics, current_mic) = call_audio::mics();

    json!({
        "device": view.device.map(|d| d.to_string()).unwrap_or_default(),
        "active": call.is_some(),
        "callId": call.as_ref().map(|c| c.id.as_str()).unwrap_or(""),
        "callDevice": call_device.map(|d| d.to_string()).unwrap_or_default(),
        "ringing": is_ringing,
        "caller": caller,
        "number": number,
        "app": app,
        "since": call.as_ref().and_then(|c| c.since).unwrap_or(0) as f64,
        "canEnd": can_end,
        "canDial": can_dial,
        "controls": controls.is_some(),
        "muted": ctrl_vals.muted,
        "speaker": ctrl_vals.speaker,
        "held": ctrl_vals.held,
        "canHold": ctrl_vals.can_hold,
        "hfpAvailable": hfp.available,
        "hfpConnected": hfp.connected,
        "hfpDetail": hfp.detail,
        "callAudio": here,
        "callAudioAvailable": call_audio_avail,
        "callMics": mics.into_iter().map(|m| json!({ "id": m.id, "name": m.name })).collect::<Vec<_>>(),
        "callMic": current_mic.unwrap_or_default(),
        "logStatus": view.log_status.as_str(),
        "callLog": view.call_log,
        "moreLog": view.more_log,
        "loadingOlder": view.loading_older,
        "contactsStatus": view.contacts_status.as_str(),
        "contacts": view.contacts,
        "dialing": view.dialing,
        "sims": view.sims,
        "sim": view.sim,
    })
}

fn build_photos_json() -> Value {
    let view = photos::view_since(&[]);
    let rows = match view.rows {
        photos::Rows::All(list) => list,
        photos::Rows::Patch { changed, appended, .. } => {
            // When returning full JSON snapshot for WinUI, materialize `view()` via empty slice isn't needed;
            // `photos::view()` always returns `Rows::All`.
            let _ = (changed, appended);
            Vec::new()
        }
    };
    let rows_json: Vec<Value> = rows
        .into_iter()
        .map(|r| {
            json!({
                "kind": r.kind.name(),
                "id": r.id,
                "name": r.name,
                "date": r.date,
                "prevDate": r.prev_date,
                "size": r.size,
                "width": r.width,
                "height": r.height,
                "duration": r.duration,
                "isVideo": r.is_video,
                "album": r.album,
                "thumb": r.thumb,
                "fullUrl": r.full_url,
            })
        })
        .collect();
    json!({
        "device": view.device.map(|d| d.to_string()).unwrap_or_default(),
        "status": view.status.as_str(),
        "albums": view.albums,
        "album": view.album,
        "rows": rows_json,
        "photoCount": view.photo_count,
        "more": view.more,
        "loadingOlder": view.loading_older,
        "saving": view.saving,
        "busyItem": view.busy_item,
        "saveFolder": view.save_folder,
    })
}

fn build_mirror_json() -> Value {
    let hub = &core_host::host().hub;
    let windows: Vec<Value> = mirror::windows()
        .into_iter()
        .map(|(w, shown)| {
            let (phase, error) = match &shown.phase {
                mirror::Phase::Asking => ("asking", String::new()),
                mirror::Phase::Showing => ("showing", String::new()),
                mirror::Phase::Ended(e) => ("ended", e.clone().unwrap_or_default()),
            };
            let device_name = hub.read(|s| s.name_of(&w.device)).unwrap_or_default();
            let geom = shown
                .pkg
                .as_deref()
                .and_then(|pkg| mirror::geometry(&w.device, pkg))
                .map(|g| json!({ "x": g.x, "y": g.y, "width": g.width, "height": g.height }));
            json!({
                "key": w.key(),
                "device": w.device.to_string(),
                "deviceName": device_name,
                "session": w.session,
                "phase": phase,
                "error": error,
                "app": shown.app.unwrap_or_default(),
                "pkg": shown.pkg.unwrap_or_default(),
                "restarting": shown.restarting,
                "recording": mirror::is_recording(&w),
                "recordingStartedMs": mirror::recording_started_ms(&w),
                "stayAwake": mirror::stay_awake(&w.device),
                "screenOff": mirror::screen_off(&w.device),
                "hasSound": mirror::has_sound(&w.device),
                "canRecord": win::mp4::can_record(win::mp4::VideoCodec::H264),
                "powerMatters": mirror::power_matters(&w),
                "flexible": mirror::flexible(&w),
                "dark": mirror::dark(&w),
                "zoom": mirror::zoom(&w),
                "notice": mirror::last_notice(&w),
                "geometry": geom,
            })
        })
        .collect();

    let kb_dev = mirror::keyboard_device();
    let kb_name = kb_dev.and_then(|id| hub.read(|s| s.name_of(&id))).unwrap_or_default();

    // Return apps catalog for all paired phones or selected device so AppsPage updates cleanly.
    let ui_dev = app::ui_state(|u| u.current_device);
    let active_dev = app::selected_device(ui_dev);
    let (apps_status, apps_error, apps_list, recent_apps) = if let Some(dev) = active_dev {
        let recent = mirror::recent_apps(&dev);
        match mirror::apps(&dev) {
            None => ("idle", String::new(), Vec::new(), recent),
            Some(mirror::Apps::Loading) => ("loading", String::new(), Vec::new(), recent),
            Some(mirror::Apps::Failed(e)) => ("failed", e, Vec::new(), recent),
            Some(mirror::Apps::Ready(list)) => {
                let items = list
                    .into_iter()
                    .map(|a| {
                        json!({
                            "pkg": a.pkg,
                            "label": a.label,
                            "iconPath": a.icon.map(|p| p.to_string_lossy().into_owned()).unwrap_or_default(),
                        })
                    })
                    .collect();
                ("ready", String::new(), items, recent)
            }
        }
    } else {
        ("idle", String::new(), Vec::new(), Vec::new())
    };

    json!({
        "windows": windows,
        "muted": mirror::muted(),
        "keyboardDevice": kb_dev.map(|d| d.to_string()).unwrap_or_default(),
        "keyboardDeviceName": kb_name,
        "appsDevice": active_dev.map(|d| d.to_string()).unwrap_or_default(),
        "appsStatus": apps_status,
        "appsError": apps_error,
        "apps": apps_list,
        "recentApps": recent_apps,
    })
}

fn build_webcam_json() -> Value {
    let hub = &core_host::host().hub;
    let selected = webcam::selected_phone();
    let selected_name = selected.and_then(|id| hub.read(|s| s.name_of(&id))).unwrap_or_default();
    let (phase_str, active_dev, status_text) = match webcam::phase() {
        webcam::Phase::Idle => ("idle", String::new(), String::new()),
        webcam::Phase::Asking { device } => {
            let name = hub.read(|s| s.name_of(&device)).unwrap_or_else(|| "phone".into());
            ("asking", device.to_string(), tr!("Webcam", "Starting camera on %1…", name))
        }
        webcam::Phase::Streaming { device, width, height, fps } => {
            let name = hub.read(|s| s.name_of(&device)).unwrap_or_else(|| "phone".into());
            (
                "streaming",
                device.to_string(),
                tr!("Webcam", "Streaming %1 × %2 · %3 fps from %4", width, height, fps, name),
            )
        }
        webcam::Phase::Ended { reason } => ("ended", String::new(), reason),
    };
    json!({
        "addonRegistered": webcam::addon_registered(),
        "addonBusy": webcam::addon_busy(),
        "selectedPhone": selected.map(|d| d.to_string()).unwrap_or_default(),
        "selectedPhoneName": selected_name,
        "height": webcam::height(),
        "uhd": webcam::uhd(),
        "camera": webcam::camera(),
        "mirror": webcam::mirror(),
        "torch": webcam::torch(),
        "phase": phase_str,
        "activeDevice": active_dev,
        "statusText": status_text,
    })
}

fn build_phone_mic_json() -> Value {
    let phase = match phone_mic::phase() {
        phone_mic::Phase::Off => "off",
        phone_mic::Phase::Asking(_) => "asking",
        phone_mic::Phase::Live(_) => "live",
    };
    let (device, device_name) =
        phone_mic::phone().map(|(id, name)| (id.to_string(), name)).unwrap_or_default();
    let (devices, output) = phone_mic::outputs();
    let list: Vec<Value> = devices.iter().map(|d| json!({ "id": d.id, "name": d.name })).collect();
    let chosen = output.as_deref().and_then(|id| devices.iter().find(|d| d.id == id));
    let output_name = chosen.map(|d| d.name.clone()).unwrap_or_default();
    let cable = chosen.is_some_and(|d| phone_mic::is_cable(&d.name));
    json!({
        "phase": phase,
        "device": device,
        "deviceName": device_name,
        "outputs": list,
        "output": output.unwrap_or_default(),
        "outputName": output_name,
        "cable": cable,
        "gain": phone_mic::gain(),
        "minGain": phone_mic::MIN_GAIN,
        "maxGain": phone_mic::MAX_GAIN,
    })
}

fn build_photo_backup_json() -> Value {
    let phones: Vec<_> = core_host::host().hub.read(|s| {
        s.devices
            .iter()
            .filter(|d| matches!(d.info.kind, DeviceKind::Phone | DeviceKind::Tablet | DeviceKind::Unknown))
            .map(|d| (d.id, d.info.name.clone(), matches!(d.link, LinkState::Online { .. })))
            .collect()
    });
    let list: Vec<Value> = phones
        .into_iter()
        .map(|(id, name, online)| {
            let summary = photo_backup::summary(id, &name);
            let (text, problem) = match (summary.enabled, &summary.activity) {
                (false, _) => (tr!("PhotoBackup", "Off"), false),
                (true, photo_backup::Activity::Problem(t)) => (t.clone(), true),
                (true, photo_backup::Activity::Looking) => (tr!("PhotoBackup", "Looking for new photos…"), false),
                (true, photo_backup::Activity::Copying { left }) => {
                    (tr!("PhotoBackup", "Copying, %1 left", left), false)
                }
                (true, _) if !online => (tr!("PhotoBackup", "Waiting for %1", name), false),
                (true, photo_backup::Activity::Waiting) => (tr!("PhotoBackup", "Waiting for %1", name), false),
                (true, photo_backup::Activity::WaitingForWifi) => {
                    (tr!("PhotoBackup", "Waiting for Wi-Fi"), false)
                }
                (true, photo_backup::Activity::Idle) if summary.done > 0 => {
                    (tr!("PhotoBackup", "Up to date, %1 backed up", summary.done), false)
                }
                (true, photo_backup::Activity::Idle) => (tr!("PhotoBackup", "Up to date"), false),
            };
            json!({
                "id": id.to_string(),
                "name": name,
                "online": online,
                "enabled": summary.enabled,
                "videos": summary.videos,
                "folder": summary.folder.to_string_lossy(),
                "done": summary.done,
                "status": text,
                "problem": problem,
            })
        })
        .collect();
    json!({ "phones": list })
}

fn build_phone_install_json() -> Value {
    use crate::phone_install::{Job, PhoneState, Problem, Tools};
    let status = phone_install::status();
    let tools = match &status.tools {
        Tools::Unknown => json!({ "kind": "unknown" }),
        Tools::Ready { own } => json!({ "kind": "ready", "own": own }),
        Tools::Unavailable => json!({ "kind": "unavailable" }),
        Tools::Missing => json!({ "kind": "missing" }),
        Tools::Getting { done, total } => json!({ "kind": "getting", "done": done, "total": total }),
        Tools::Failed(e) => json!({ "kind": "failed", "error": e }),
    };
    let latest = status.latest.clone().unwrap_or_default();
    let phones: Vec<Value> = status
        .phones
        .iter()
        .map(|p| {
            let state = match p.state {
                PhoneState::Ready => "ready",
                PhoneState::Unauthorized => "unauthorized",
                PhoneState::NoPermissions => "noPermissions",
                PhoneState::Offline => "offline",
            };
            let installed = p.installed.clone().unwrap_or_default();
            json!({
                "serial": p.serial,
                "state": state,
                "name": p.name,
                "android": p.android.clone().unwrap_or_default(),
                "known": p.known,
                "installed": installed,
                "outdated": !installed.is_empty() && !latest.is_empty() && phone_install::is_outdated(&installed, &latest),
                "tooOld": p.sdk.is_some_and(|sdk| sdk < 26),
            })
        })
        .collect();
    let job = match &status.job {
        Job::Idle => json!({ "kind": "idle" }),
        Job::Downloading { serial, done, total } => {
            json!({ "kind": "downloading", "serial": serial, "done": done, "total": total })
        }
        Job::Sending { serial, done, total } => {
            json!({ "kind": "sending", "serial": serial, "done": done, "total": total })
        }
        Job::Installing { serial } => json!({ "kind": "installing", "serial": serial }),
        Job::Uninstalling { serial } => json!({ "kind": "uninstalling", "serial": serial }),
        Job::Done { serial, version } => json!({ "kind": "done", "serial": serial, "version": version }),
        Job::Failed { serial, problem } => {
            let (code, title, text, action) = match problem {
                Problem::Download(e) => ("download", tr!("PhoneInstall", "Couldn't download the phone app"), e.clone(), "retry"),
                Problem::Damaged => ("damaged", tr!("PhoneInstall", "The download was damaged"), tr!("PhoneInstall", "It didn't match its checksum, so it wasn't installed. Try again."), "retry"),
                Problem::Lost(_) => ("lost", tr!("PhoneInstall", "The phone stopped answering"), tr!("PhoneInstall", "Keep it plugged in and unlocked until the install is done, then try again."), "retry"),
                Problem::OldAndroid => ("oldAndroid", tr!("PhoneInstall", "This phone's Android is too old"), tr!("PhoneInstall", "NectarLink needs Android 8.0 or newer."), ""),
                Problem::Restricted => ("restricted", tr!("PhoneInstall", "The phone blocks installs over USB"), tr!("PhoneInstall", "Turn on Install via USB in Developer options, then try again."), "retry"),
                Problem::OtherBuild => ("otherBuild", tr!("PhoneInstall", "Another build of NectarLink is on this phone"), tr!("PhoneInstall", "Replacing it removes it with its data, then installs this one."), "replace"),
                Problem::Newer => ("newer", tr!("PhoneInstall", "The phone already has a newer version"), tr!("PhoneInstall", "There's nothing to install."), ""),
                Problem::NoSpace => ("noSpace", tr!("PhoneInstall", "The phone is out of space"), tr!("PhoneInstall", "Free up some storage on the phone, then try again."), "retry"),
                Problem::Declined => ("declined", tr!("PhoneInstall", "The install was stopped on the phone"), tr!("PhoneInstall", "Try again and allow the install on your phone."), "retry"),
                Problem::Other(r) => ("other", tr!("PhoneInstall", "The phone didn't install it"), r.clone(), "retry"),
            };
            json!({ "kind": "failed", "serial": serial, "problem": { "code": code, "title": title, "text": text, "action": action } })
        }
    };
    json!({
        "usbOff": status.usb_off,
        "platform": "windows",
        "server": status.server,
        "driverMissing": status.driver_missing,
        "latest": latest,
        "tools": tools,
        "phones": phones,
        "job": job,
    })
}

fn build_agents_json() -> Value {
    let clients: Vec<Value> = agents::clients()
        .into_iter()
        .map(|c| {
            let preset = if c.grants == nectarlink_actions::preset_read_only() {
                "readOnly"
            } else if c.grants == nectarlink_actions::preset_everyday() {
                "everyday"
            } else {
                "custom"
            };
            json!({
                "id": c.id,
                "label": c.label,
                "name": c.display_name,
                "kind": c.kind,
                "preset": preset,
                "grants": c.grants_text(),
                "readOnly": c.read_only,
                "paused": c.paused,
                "lastUsed": c.last_used.unwrap_or(0),
            })
        })
        .collect();
    let activity: Vec<Value> = agents::activity(100)
        .into_iter()
        .map(|a| {
            json!({
                "ts": a.ts,
                "client": a.client_name,
                "action": a.action,
                "device": a.device,
                "summary": a.summary,
                "decision": a.decision,
                "confirm": a.confirm,
            })
        })
        .collect();
    let confirmation = agents::current_confirmation().map(|r| {
        json!({
            "id": r.id,
            "client": r.client,
            "action": r.action,
            "title": r.title,
            "device": r.device,
            "details": r.details.iter().map(|(k, v)| json!({ "label": k, "value": v })).collect::<Vec<_>>(),
            "canRemember": r.can_remember,
        })
    });
    json!({
        "port": agents::port().unwrap_or(0),
        "clients": clients,
        "activity": activity,
        "confirmation": confirmation,
    })
}

fn build_clipboard_json() -> Value {
    let hist = serde_json::from_str::<Value>(&clipboard::history_json()).unwrap_or(json!([]));
    json!({
        "history": hist,
    })
}

fn build_localsend_json() -> Value {
    let (enabled, receiving, peers) = core_host::host().hub.read(|s| {
        let list: Vec<Value> = s
            .localsend_peers
            .iter()
            .map(|p| {
                json!({
                    "id": p.id.to_string(),
                    "name": p.alias,
                    "model": p.device_model.as_deref().unwrap_or(""),
                    "addr": format!("{}:{}", p.ip, p.port),
                })
            })
            .collect();
        (s.localsend_enabled, s.localsend_receiving, list)
    });
    json!({
        "enabled": enabled,
        "receiving": receiving,
        "peers": peers,
    })
}

fn timeline_query_json(args: &Value) -> Value {
    let Some(node) = core_host::node() else {
        return json!({ "rows": [], "hasAny": false, "hasMore": false });
    };
    let search_raw = args.get("search").and_then(|v| v.as_str()).unwrap_or("").trim().to_owned();
    let kind_raw = args.get("kind").and_then(|v| v.as_str()).unwrap_or("").trim();
    let device_raw = args.get("device").and_then(|v| v.as_str()).unwrap_or("").trim();
    let limit = args.get("limit").and_then(|v| v.as_u64()).unwrap_or(100).clamp(1, 5000) as u32;

    let mut entries = Vec::new();
    let mut has_more = false;
    let mut offset = 0u32;
    while offset < limit {
        let chunk = (limit - offset).min(nectarlink_core::MAX_TIMELINE_PAGE_LIMIT);
        let q = TimelineQuery {
            kind: TimelineKind::from_str_opt(kind_raw),
            device: device_raw.parse::<DeviceId>().ok(),
            search: (!search_raw.is_empty()).then(|| search_raw.clone()),
            offset,
            limit: chunk,
        };
        let Ok(page) = node.timeline_page(&q) else { break };
        let fetched = page.entries.len() as u32;
        has_more = page.has_more;
        entries.extend(page.entries);
        if !has_more || fetched == 0 {
            break;
        }
        offset += fetched;
    }

    let has_any = if !entries.is_empty() {
        true
    } else if search_raw.is_empty() && kind_raw.is_empty() && device_raw.is_empty() {
        false
    } else {
        node.timeline_page(&TimelineQuery { limit: 1, ..TimelineQuery::default() })
            .is_ok_and(|p| !p.entries.is_empty())
    };

    let names: std::collections::HashMap<DeviceId, String> =
        core_host::host().hub.read(|s| s.devices.iter().map(|d| (d.id, d.info.name.clone())).collect());

    let mut prev_ts = 0i64;
    let rows: Vec<Value> = entries
        .into_iter()
        .map(|e| {
            let dev_name = names
                .get(&e.device_id)
                .cloned()
                .filter(|n| !n.is_empty())
                .unwrap_or_else(|| e.device_name.clone());
            let dev_label = if dev_name.is_empty() { tr!("Timeline", "device") } else { dev_name.clone() };
            let primary_path = e
                .target
                .lines()
                .map(str::trim)
                .find(|s| !s.is_empty() && std::path::Path::new(s).exists())
                .or_else(|| e.target.lines().map(str::trim).find(|s| !s.is_empty()))
                .unwrap_or("")
                .to_owned();
            let exists = !primary_path.is_empty() && std::path::Path::new(&primary_path).exists();
            let item_count = e.target.lines().map(str::trim).filter(|s| !s.is_empty()).count().max(1);
            let who = match (e.kind, e.incoming) {
                (TimelineKind::Link, true) => tr!("Timeline", "Opened from %1", dev_label),
                (TimelineKind::Link, false) => tr!("Timeline", "Opened on %1", dev_label),
                (TimelineKind::Photo, _) => tr!("Timeline", "Saved from %1", dev_label),
                (TimelineKind::Recording, _) => tr!("Timeline", "Recorded on %1", dev_label),
                (TimelineKind::Session, _) => tr!("Timeline", "With %1", dev_label),
                (_, true) => tr!("Timeline", "From %1", dev_label),
                (_, false) => tr!("Timeline", "Sent to %1", dev_label),
            };
            let title = if e.title.is_empty() { e.target.clone() } else { e.title.clone() };
            let full_text = if e.kind == TimelineKind::Clip && !e.target.is_empty() {
                e.target.clone()
            } else {
                String::new()
            };
            let can_open = matches!(e.kind, TimelineKind::File | TimelineKind::Photo | TimelineKind::Recording | TimelineKind::Link)
                && (e.kind == TimelineKind::Link || exists);
            let can_show = matches!(e.kind, TimelineKind::File | TimelineKind::Photo | TimelineKind::Recording) && exists;
            let can_copy = matches!(e.kind, TimelineKind::Clip | TimelineKind::Link)
                || (e.kind == TimelineKind::Photo && exists);
            let can_send = matches!(
                e.kind,
                TimelineKind::File | TimelineKind::Photo | TimelineKind::Recording | TimelineKind::Clip | TimelineKind::Link
            );
            let row = json!({
                "entryId": e.id.to_string(),
                "kind": e.kind.as_str(),
                "direction": if e.incoming { "incoming" } else { "outgoing" },
                "incoming": e.incoming,
                "deviceId": e.device_id.to_string(),
                "deviceName": dev_name,
                "timestamp": e.timestamp,
                "prevTimestamp": prev_ts,
                "title": title,
                "subtitle": who,
                "fullText": full_text,
                "primaryPath": primary_path,
                "sizeBytes": e.size_bytes,
                "durationSecs": e.duration_secs,
                "itemCount": item_count,
                "thumbUrl": if exists && matches!(e.kind, TimelineKind::Photo) { primary_path } else { String::new() },
                "canOpen": can_open,
                "canShowInFolder": can_show,
                "canCopy": can_copy,
                "canSendAgain": can_send,
            });
            prev_ts = e.timestamp;
            row
        })
        .collect();

    json!({
        "rows": rows,
        "hasAny": has_any,
        "hasMore": has_more,
    })
}

fn timeline_find_entry(entry_id: &str) -> Option<TimelineEntry> {
    let id: i64 = entry_id.parse().ok()?;
    let node = core_host::node()?;
    let mut offset = 0u32;
    while offset < 5000 {
        let q = TimelineQuery { offset, limit: nectarlink_core::MAX_TIMELINE_PAGE_LIMIT, ..TimelineQuery::default() };
        let Ok(page) = node.timeline_page(&q) else { break };
        if let Some(found) = page.entries.into_iter().find(|e| e.id == id) {
            return Some(found);
        }
        if !page.has_more {
            break;
        }
        offset += nectarlink_core::MAX_TIMELINE_PAGE_LIMIT;
    }
    None
}

fn new_rule_id<'a>(prefix: &str, taken: impl Iterator<Item = &'a str>) -> String {
    let taken: std::collections::HashSet<&str> = taken.collect();
    let mut n = std::time::SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |d| u64::try_from(d.as_millis()).unwrap_or(u64::MAX));
    loop {
        let id = format!("{prefix}-{n}");
        if !taken.contains(id.as_str()) {
            return id;
        }
        n = n.wrapping_add(1);
    }
}

fn arg_str<'a>(args: &'a Value, key: &str) -> &'a str {
    args.get(key).and_then(|v| v.as_str()).unwrap_or("")
}

fn arg_bool(args: &Value, key: &str) -> bool {
    args.get(key).and_then(|v| v.as_bool()).unwrap_or(false)
}

fn arg_i64(args: &Value, key: &str) -> i64 {
    args.get(key).and_then(|v| v.as_i64()).unwrap_or(0)
}

fn arg_f64(args: &Value, key: &str) -> f64 {
    args.get(key).and_then(|v| v.as_f64()).unwrap_or(0.0)
}

fn mutate_settings(f: impl FnOnce(&mut settings::Settings)) {
    let dir = core_host::host().data_dir.clone();
    let updated = settings::Settings::update(&dir, |s| {
        f(s);
    });
    if let Err(e) = updated {
        tracing::warn!(error = %e, "can't save settings");
    } else {
        let s = settings::Settings::load(&dir);
        s.apply_runtime();
        local_api::sync_from_settings();
        agents::sync_from_settings();
    }
    crate::bridge::notify_changes(Changes::PREFS | Changes::STATUS);
}

/// Executes any command from the WinUI 3 frontend and returns a JSON response.
pub fn invoke_command(cmd: &str, args: &Value) -> Value {
    match cmd {
        // ---- App & Window ----
        "app.setCurrentPage" => {
            let page = arg_str(args, "page").to_owned();
            app::update_ui(Changes::STATUS, |u| u.current_page = page);
        }
        "app.setCurrentDevice" => {
            let idx = arg_i64(args, "index") as i32;
            app::update_ui(Changes::STATUS | Changes::TOGGLES, |u| u.current_device = idx);
            let dev = app::selected_device(idx);
            app::watch_home(dev);
        }
        "app.refreshAppearance" => app::refresh_appearance(),
        "app.refreshToastsEnabled" => app::refresh_toasts_enabled(),
        "app.quit" => app::request_quit(),
        "app.activate" => app::request_activation(),
        "app.openUrl" => {
            let url = arg_str(args, "url");
            if let Err(e) = win::shell::open_url(url) {
                app::show_message(e);
            }
        }
        "app.copyText" => {
            let text = arg_str(args, "text");
            if let Err(e) = win::clipboard::write(text) {
                app::show_message(e);
            } else {
                app::show_message(tr!("App", "Copied to clipboard."));
            }
        }
        "app.workArea" => {
            if let Some((x, y, w, h)) = win::work_area() {
                return json!({ "x": x, "y": y, "width": w, "height": h });
            }
        }
        "app.triggerHighlight" => {
            let target = arg_str(args, "target").to_owned();
            app::update_ui(Changes::STATUS, |u| u.highlight_target = target);
        }
        "app.clearHighlight" => {
            app::update_ui(Changes::STATUS, |u| u.highlight_target.clear());
        }
        "app.clearPendingDial" => {
            app::update_ui(Changes::STATUS, |u| u.pending_dial.clear());
        }
        "app.openGuideDoc" => app::open_guide_doc(arg_str(args, "doc")),
        "app.closeGuideDoc" => app::close_guide_doc(),
        "app.setPausePopups" => {
            let on = arg_bool(args, "paused");
            notifications::set_paused(on);
            settings::remember_popups_paused(on);
            crate::bridge::notify_changes(Changes::STATUS | Changes::PREFS);
        }
        "app.releaseIdleHomeSummary" => app::release_home_summary(),
        "app.openDataDir" => {
            let dir = core_host::host().data_dir.clone();
            let _ = std::fs::create_dir_all(&dir);
            let _ = win::shell::open_folder(&dir);
        }
        "app.openLogsDir" => {
            let dir = crate::logging::logs_dir(&core_host::host().data_dir);
            let _ = std::fs::create_dir_all(&dir);
            let _ = win::shell::open_folder(&dir);
        }
        "app.openDownloadsFolder" => {
            let dir = crate::core_host::downloads_dir();
            let _ = std::fs::create_dir_all(&dir);
            let _ = win::shell::open_folder(&dir);
        }
        "app.openRecordingsFolder" => {
            let dir = recordings::effective_folder(settings::Settings::load(&core_host::host().data_dir).recordings_folder.as_deref());
            let _ = std::fs::create_dir_all(&dir);
            let _ = win::shell::open_folder(&dir);
        }
        "app.openScreenshotsFolder" => {
            let dir = crate::core_host::downloads_dir();
            let _ = std::fs::create_dir_all(&dir);
            let _ = win::shell::open_folder(&dir);
        }
        "app.openPhotosFolder" => {
            let dir = crate::core_host::downloads_dir();
            let _ = std::fs::create_dir_all(&dir);
            let _ = win::shell::open_folder(&dir);
        }
        "app.openWindowsNotificationSettings" => {
            let _ = win::shell::open_url("ms-settings:notifications");
        }

        // ---- Command Palette ----
        "palette.open" => app::open_command_palette(),
        "palette.close" => app::close_command_palette(),
        "palette.search" => app::search_command_palette(arg_str(args, "query")),
        "palette.noteShown" => app::note_command_palette_shown(),
        "palette.run" => app::run_command_palette(arg_str(args, "id")),

        // ---- Shelf & Continuity & Diagnostics ----
        "shelf.open" => app::open_shelf(),
        "shelf.close" => app::close_shelf(),
        "shelf.toggle" => app::toggle_shelf_from_anywhere(),
        "shelf.refresh" => app::refresh_shelf(),
        "shelf.activate" => {
            let kind = arg_str(args, "kind");
            let action = arg_str(args, "action");
            let target = arg_str(args, "target");
            let ui_dev = app::ui_state(|u| u.current_device);
            let dev = app::selected_device(ui_dev);
            match kind {
                "photo" | "screenshot" => {
                    let p = std::path::Path::new(target);
                    if !target.is_empty() && p.exists() {
                        match action {
                            "folder" | "reveal" | "save" => transfers::show_in_folder(p),
                            "copy" => {
                                let mime = if p.extension().and_then(|e| e.to_str()).is_some_and(|e| e.eq_ignore_ascii_case("png")) {
                                    "image/png"
                                } else {
                                    "image/jpeg"
                                };
                                match std::fs::read(p).map_err(|e| e.to_string()).and_then(|b| win::clipboard::write_image(mime, &b)) {
                                    Ok(()) => app::show_message(tr!("App", "Copied to clipboard.")),
                                    Err(_) => app::show_message(tr!("App", "The photo couldn't be copied to the clipboard.")),
                                }
                            }
                            _ => transfers::open(p),
                        }
                    } else if !target.is_empty() && let Some(d) = dev {
                        let toast_action = match action {
                            "save" => Some(photos::ACTION_SAVE),
                            "copy" => Some(photos::ACTION_COPY),
                            _ => None,
                        };
                        photos::on_toast(&format!("{d} {target}"), toast_action);
                    }
                }
                "clip" => {
                    if !target.is_empty() {
                        clipboard::copy_history_item(target);
                    }
                }
                "file" => {
                    let p = std::path::Path::new(target);
                    if !target.is_empty() && p.exists() {
                        if action == "folder" || action == "reveal" {
                            transfers::show_in_folder(p);
                        } else {
                            transfers::open(p);
                        }
                    } else {
                        app::show_message(tr!("App", "That file is no longer in its saved location."));
                    }
                }
                _ => {}
            }
        }
        "shelf.drop" => {
            let paths: Vec<PathBuf> = args
                .get("paths")
                .and_then(|v| v.as_array())
                .map(|arr| arr.iter().filter_map(|v| v.as_str()).filter_map(path_to_local).collect())
                .unwrap_or_default();
            let text = arg_str(args, "text").to_owned();
            let ui_dev = app::ui_state(|u| u.current_device);
            let Some(dev) = app::selected_device(ui_dev) else {
                app::show_message(tr!("App", "No phone is paired yet."));
                return json!({ "ok": false });
            };
            if !paths.is_empty() {
                transfers::send(dev, paths);
            } else if !text.is_empty() {
                if links::is_handoff_text(&text) {
                    links::send_to(dev, text);
                } else if let Some(node) = core_host::node() {
                    core_host::spawn(async move {
                        let _ = node.send_clipboard(dev, text).await;
                    });
                }
            }
        }
        "continuity.capture" => {
            let kind = arg_str(args, "kind").to_owned();
            let ui_dev = app::ui_state(|u| u.current_device);
            let idx = (ui_dev.max(0)) as usize;
            crate::continuity_camera::start_for_index(idx, &kind);
        }
        "continuity.pasteFallback" => crate::continuity_camera::confirm_paste(),
        "continuity.dismissFallback" => crate::continuity_camera::dismiss_paste(),

        "doctor.run" => {
            let fix = args.get("fix").and_then(|v| v.as_str()).map(str::to_owned);
            app::diagnose_in_background(fix);
        }
        "relays.check" => app::check_relays(),
        "updates.check" => {
            let told = arg_bool(args, "told");
            std::thread::spawn(move || {
                if let Some(msg) = updater::check_now(told) {
                    app::show_message(msg);
                }
                crate::bridge::notify_changes(Changes::UPDATE);
            });
        }
        "updates.install" => app::install_update_in_background(),
        "whatsNew.seen" => {
            nectarlink_core::whats_new::mark_seen(&core_host::host().data_dir);
            mutate_settings(|s| s.whats_new_dismissed = true);
        }

        // ---- Devices & Controls ----
        "device.ring" => {
            if let Some(id) = parse_device(arg_str(args, "device")) {
                app::ring_device(id, arg_bool(args, "on"));
            }
        }
        "device.stopRingingPc" => {
            win::sound::stop_ringing();
            core_host::host().hub.update(|s| {
                s.ringing_from = None;
                Changes::RINGING
            });
        }
        "device.setToggle" => {
            if let Some(id) = parse_device(arg_str(args, "device")) {
                app::set_device_toggle(id, arg_str(args, "name"), arg_bool(args, "on"));
            }
        }
        "device.setPhoneToggle" => {
            if let Some(id) = parse_device(arg_str(args, "device")) {
                app::set_phone_toggle(id, arg_str(args, "id").to_owned(), arg_str(args, "value").to_owned());
            }
        }
        "device.setNickname" => {
            let dev = arg_str(args, "device").to_owned();
            let nick = arg_str(args, "nickname").to_owned();
            settings::set_nickname(&dev, if nick.trim().is_empty() { None } else { Some(nick.clone()) });
            mutate_settings(|s| {
                if nick.trim().is_empty() {
                    s.device_nicknames.remove(&dev);
                } else {
                    s.device_nicknames.insert(dev, nick.trim().to_owned());
                }
            });
            core_host::host().hub.changed(Changes::DEVICES);
        }
        "device.unpair" => {
            if let Some(id) = parse_device(arg_str(args, "device"))
                && let Some(node) = core_host::node()
            {
                core_host::spawn(async move {
                    if let Err(e) = node.unpair(id).await {
                        app::show_message(app::describe(&e));
                    }
                });
            }
        }
        "device.setReconnect" => {
            if let Some(id) = parse_device(arg_str(args, "device"))
                && let Some(node) = core_host::node()
            {
                let _ = node.set_reconnect(id, arg_bool(args, "reconnect"));
            }
        }
        "device.setHotspot" => {
            if let Some(id) = parse_device(arg_str(args, "device")) {
                let ssid = args.get("ssid").and_then(|v| v.as_str()).map(str::trim).filter(|s| !s.is_empty()).map(str::to_owned);
                hotspot::set_network(id, ssid);
                crate::bridge::notify_changes(Changes::STATUS);
            }
        }
        "device.setupUnlock" => {
            if let Some(id) = parse_device(arg_str(args, "device")) {
                let pw = arg_str(args, "password").to_owned();
                app::set_up_unlock(id, pw);
            }
        }
        "device.removeUnlock" => {
            if let Some(id) = parse_device(arg_str(args, "device"))
                && let Some(node) = core_host::node()
            {
                let sid = win::unlock::current_user_sid();
                core_host::spawn(async move {
                    let _ = node.unlock_forget(id, sid.as_deref()).await;
                    crate::bridge::notify_changes(Changes::STATUS);
                });
            }
        }
        "device.sendPowerAction" => {
            if let Some(id) = parse_device(arg_str(args, "device"))
                && let Some(node) = core_host::node()
            {
                let action = match arg_str(args, "action") {
                    "lock" => nectarlink_core::PowerAction::Lock,
                    "sleep" => nectarlink_core::PowerAction::Sleep,
                    _ => return json!({ "ok": false }),
                };
                core_host::spawn(async move {
                    let _ = node.pc_power(id, action).await;
                });
            }
        }
        "device.answerRemotePrompt" => {
            let allow = arg_bool(args, "allow");
            let dev = core_host::host().hub.read(|s| s.remote_prompt);
            core_host::host().hub.update(|s| {
                s.remote_prompt = None;
                Changes::REMOTE
            });
            if allow && let Some(id) = dev {
                app::set_device_toggle(id, "remote_input", true);
            }
        }
        "device.answerPcMirrorPrompt" => {
            let allow = arg_bool(args, "allow");
            let req = core_host::host().hub.read(|s| s.pc_mirror_prompt.clone());
            core_host::host().hub.update(|s| {
                s.pc_mirror_prompt = None;
                Changes::REMOTE
            });
            if allow && let Some((id, start)) = req {
                app::set_device_toggle(id, "pc_mirror", true);
                pc_mirror::start(id, start);
            }
        }
        "device.stopPcMirror" => {
            pc_mirror::stop_all();
            crate::bridge::notify_changes(Changes::STATUS | Changes::REMOTE);
        }
        "device.fetchWallpaper" => {
            if let Some(id) = parse_device(arg_str(args, "device")) {
                let mode = match arg_str(args, "mode") {
                    "" => "auto",
                    m => m,
                };
                crate::phone_wallpaper::fetch(id, mode.to_owned(), arg_str(args, "serial").to_owned());
            }
        }
        "device.setWallpaperFromFile" => {
            if let Some(id) = parse_device(arg_str(args, "device"))
                && let Some(path) = path_to_local(arg_str(args, "path"))
            {
                match crate::phone_wallpaper::set_from_file(id, &path) {
                    Ok(saved) => {
                        app::show_message(tr!("Device", "Updated phone preview wallpaper."));
                        return json!({ "ok": true, "path": saved.to_string_lossy() });
                    }
                    Err(e) => {
                        app::show_message(&e);
                        return json!({ "ok": false, "error": e });
                    }
                }
            }
        }
        "device.clearWallpaper" => {
            if let Some(id) = parse_device(arg_str(args, "device")) {
                crate::phone_wallpaper::clear(id);
            }
        }

        // ---- Pairing ----
        "pairing.start" => {
            let skip = core_host::host().hub.read(|s| {
                matches!(
                    s.pairing,
                    PairingView::Comparing { .. } | PairingView::Confirmed { .. } | PairingView::Connecting { .. }
                )
            });
            if !skip {
                core_host::host().hub.update(|s| s.set_pairing(PairingView::Starting));
                if let Some(node) = core_host::node() {
                    core_host::spawn(async move {
                        let next = match node.pairing_start_qr().await {
                            Ok(uri) => PairingView::Hosting {
                                uri: uri.to_uri(),
                                expires_at: SystemTime::now() + Duration::from_secs(5 * 60),
                            },
                            Err(e) => PairingView::Failed(PairingFailure::Other(e.to_string())),
                        };
                        core_host::host().hub.update(|s| {
                            if s.pairing == PairingView::Starting {
                                s.set_pairing(next)
                            } else {
                                Changes::NONE
                            }
                        });
                    });
                }
            }
        }
        "pairing.pairNearby" => {
            if let Some(peer) = parse_device(arg_str(args, "device"))
                && let Some(node) = core_host::node()
            {
                core_host::host().hub.update(|s| s.set_pairing(PairingView::Connecting { peer }));
                core_host::spawn(async move {
                    if let Err(e) = node.pairing_start_nearby(peer).await {
                        tracing::info!(error = %e, "nearby pairing ended");
                        core_host::host().hub.update(|s| match s.pairing {
                            PairingView::Connecting { .. } => {
                                s.set_pairing(PairingView::Failed(PairingFailure::Unreachable))
                            }
                            _ => Changes::NONE,
                        });
                    }
                });
            }
        }
        "pairing.confirm" => {
            let match_ok = arg_bool(args, "matches");
            let Some(node) = core_host::node() else {
                return json!({ "ok": false });
            };
            let comparing = core_host::host().hub.read(|s| match &s.pairing {
                PairingView::Comparing { peer, code } => Some((*peer, code.clone())),
                _ => None,
            });
            if let Some((peer, code)) = comparing {
                if let Err(e) = node.pairing_confirm(match_ok) {
                    tracing::warn!(error = %e, "can't answer the code check");
                }
                let next = if match_ok {
                    PairingView::Confirmed { peer, code }
                } else {
                    PairingView::Failed(PairingFailure::Declined)
                };
                core_host::host().hub.update(|s| s.set_pairing(next));
            }
        }
        "pairing.cancel" => {
            if let Some(node) = core_host::node() {
                node.pairing_cancel();
            }
            core_host::host().hub.update(|s| s.set_pairing(PairingView::Idle));
        }
        "pairing.setNearbyShown" => {
            let shown = arg_bool(args, "shown");
            NEARBY_SHOWN.store(shown, Ordering::Release);
            if let Some(node) = core_host::node() {
                node.set_discovery_fast(shown);
            }
        }

        // ---- Notifications ----
        "notifications.dismiss" => {
            let dev_str = arg_str(args, "device");
            let key = arg_str(args, "key").to_owned();
            if let Some(dev_id) = parse_device(dev_str) {
                let found = core_host::host().hub.read(|s| {
                    s.notifications.iter().find(|n| n.device == dev_id && n.notification.key == key).cloned()
                });
                if found.as_ref().is_some_and(|v| v.notification.ongoing) {
                    return json!({ "ok": false });
                }
                if let Some(view) = found {
                    let label = if view.notification.app_name.is_empty() {
                        tr!("Notifications", "Notification dismissed")
                    } else {
                        tr!("Notifications", "Dismissed from %1", view.notification.app_name)
                    };
                    core_host::host().hub.update(|s| s.record_dismissed(vec![view], label));
                }
            }
            notifications::dismiss(dev_str, key);
        }
        "notifications.dismissGroup" => {
            let dev_str = arg_str(args, "device");
            let app_str = arg_str(args, "app");
            if let Some(dev_id) = parse_device(dev_str) {
                let views: Vec<NotificationView> = core_host::host().hub.read(|s| {
                    s.notifications
                        .iter()
                        .filter(|n| n.device == dev_id && n.notification.app == app_str && !n.notification.ongoing)
                        .cloned()
                        .collect()
                });
                if !views.is_empty() {
                    let app_name = views[0].notification.app_name.clone();
                    let label = if views.len() == 1 {
                        tr!("Notifications", "Dismissed from %1", app_name)
                    } else {
                        tr!("Notifications", "Dismissed %1 from %2", views.len(), app_name)
                    };
                    let keys: Vec<String> = views.iter().map(|v| v.notification.key.clone()).collect();
                    core_host::host().hub.update(|s| s.record_dismissed(views, label));
                    for k in keys {
                        notifications::dismiss(dev_str, k);
                    }
                }
            }
        }
        "notifications.dismissAll" => {
            let dev_str = arg_str(args, "device");
            if let Some(dev_id) = parse_device(dev_str) {
                let views: Vec<NotificationView> = core_host::host().hub.read(|s| {
                    s.notifications
                        .iter()
                        .filter(|n| {
                            n.device == dev_id && !n.notification.ongoing && s.app_rule(&n.notification.app) != AppRule::Hidden
                        })
                        .cloned()
                        .collect()
                });
                if !views.is_empty() {
                    let label = if views.len() == 1 {
                        tr!("Notifications", "Notification dismissed")
                    } else {
                        tr!("Notifications", "Dismissed %1 notifications", views.len())
                    };
                    let keys: Vec<String> = views.iter().map(|v| v.notification.key.clone()).collect();
                    core_host::host().hub.update(|s| s.record_dismissed(views, label));
                    for k in keys {
                        notifications::dismiss(dev_str, k);
                    }
                }
            }
        }
        "notifications.toggleGroup" => {
            let app_str = arg_str(args, "app").to_owned();
            core_host::host().hub.update(|s| s.toggle_app_expanded(&app_str));
        }
        "notifications.undoDismiss" => core_host::host().hub.update(AppState::undo_dismiss),
        "notifications.act" => {
            let dev = arg_str(args, "device");
            let key = arg_str(args, "key").to_owned();
            let action = arg_str(args, "action").to_owned();
            let reply = args.get("reply").and_then(|v| v.as_str()).map(str::to_owned);
            notifications::run_action(dev, key, action, reply);
        }
        "notifications.copyOtp" => {
            let code = arg_str(args, "code");
            notifications::copy_otp(code);
        }
        "notifications.setAppRule" => {
            let app_str = arg_str(args, "app");
            let rule = match arg_str(args, "rule") {
                "quiet" => AppRule::Quiet,
                "hidden" => AppRule::Hidden,
                _ => AppRule::Show,
            };
            notifications::set_app_rule(app_str, rule);
        }
        "notifications.setHistoryEnabled" => {
            let on = arg_bool(args, "enabled");
            core_host::host().hub.update(|s| s.set_history_enabled(on));
        }

        // ---- Transfers ----
        "transfers.send" => {
            if let Some(dev) = parse_device(arg_str(args, "device")) {
                let paths: Vec<PathBuf> = args
                    .get("paths")
                    .and_then(|v| v.as_array())
                    .map(|a| a.iter().filter_map(|v| v.as_str()).filter_map(path_to_local).collect())
                    .unwrap_or_default();
                if !paths.is_empty() {
                    transfers::send(dev, paths);
                }
            }
        }
        "transfers.sendHandoff" => {
            if let Some(dev) = parse_device(arg_str(args, "device")) {
                let paths: Vec<PathBuf> = args
                    .get("paths")
                    .and_then(|v| v.as_array())
                    .map(|a| a.iter().filter_map(|v| v.as_str()).filter_map(path_to_local).collect())
                    .unwrap_or_default();
                if !paths.is_empty() {
                    transfers::send_handoff(dev, paths);
                }
            }
        }
        "transfers.accept" => transfers::accept(arg_str(args, "id")),
        "transfers.cancel" => transfers::cancel(arg_str(args, "id")),
        "transfers.pause" => transfers::pause(arg_str(args, "id")),
        "transfers.resume" => transfers::resume(arg_str(args, "id")),
        "transfers.open" => {
            let id = arg_str(args, "id");
            let path = core_host::host().hub.read(|s| {
                s.transfers.iter().find(|t| t.transfer.id == id).and_then(|t| match &t.transfer.state {
                    TransferState::Done { saved } => saved.first().cloned(),
                    _ => None,
                })
            });
            if let Some(p) = path {
                transfers::open(&p);
            }
        }
        "transfers.showInFolder" => {
            let id = arg_str(args, "id");
            let path = core_host::host().hub.read(|s| {
                s.transfers.iter().find(|t| t.transfer.id == id).and_then(|t| match &t.transfer.state {
                    TransferState::Done { saved } => saved.first().cloned(),
                    _ => None,
                })
            });
            if let Some(p) = path {
                transfers::show_in_folder(&p);
            }
        }
        "transfers.clearFinished" => {
            core_host::host().hub.update(|s| {
                s.transfers.retain(|t| {
                    matches!(
                        t.transfer.state,
                        TransferState::Requested | TransferState::Waiting | TransferState::Running | TransferState::Paused
                    )
                });
                Changes::TRANSFERS
            });
        }
        "transfers.cancelQueueItem" => transfers::cancel_queue_item(arg_str(args, "id")),
        "transfers.cancelAllQueued" => transfers::cancel_all_queued(parse_device(arg_str(args, "device"))),
        "transfers.retryQueueItem" => transfers::retry_queue_item(arg_str(args, "id")),
        "transfers.clearFinishedQueue" => transfers::clear_finished_queue(None),

        // ---- Clipboard ----
        "clipboard.sendNow" => {
            if let Some(dev) = parse_device(arg_str(args, "device")) {
                clipboard::send_now(dev);
            }
        }
        "clipboard.sendHistoryItem" => {
            if let Some(dev) = parse_device(arg_str(args, "device")) {
                clipboard::send_history_item(dev, arg_str(args, "id"));
            }
        }
        "clipboard.copyHistoryItem" => clipboard::copy_history_item(arg_str(args, "id")),
        "clipboard.pinHistoryItem" => clipboard::pin_history_item(arg_str(args, "id"), arg_bool(args, "pinned")),
        "clipboard.deleteHistoryItem" => clipboard::delete_history_item(arg_str(args, "id")),
        "clipboard.clearHistory" => clipboard::clear_history(),
        "clipboard.runSuggestion" => app::run_clip_suggestion(),
        "clipboard.sendLink" => {
            if let Some(dev) = parse_device(arg_str(args, "device")) {
                links::send_to(dev, arg_str(args, "url").to_owned());
            }
        }

        // ---- Media ----
        "media.command" => {
            let dev_raw = arg_str(args, "device");
            let player = arg_str(args, "player").to_owned();
            let act_str = arg_str(args, "action");
            let pos = arg_f64(args, "positionMs");
            let device = if dev_raw == "local" {
                Some(media::LOCAL_DEVICE)
            } else {
                parse_device(dev_raw)
            };
            let action = match act_str {
                "play" => Some(MediaAction::Play),
                "pause" => Some(MediaAction::Pause),
                "next" => Some(MediaAction::Next),
                "previous" => Some(MediaAction::Previous),
                "seek" if pos >= 0.0 => Some(MediaAction::Seek),
                "play_on_phone" if device == Some(media::LOCAL_DEVICE) => {
                    play_on_phone::play(player);
                    return json!({ "ok": true });
                }
                _ => None,
            };
            if let (Some(d), Some(a)) = (device, action) {
                media::command(d, player, a, (a == MediaAction::Seek).then_some(pos as u64));
            }
        }
        "media.setPlayOnPhone" => play_on_phone::set_on(arg_bool(args, "on")),
        "media.setPhoneSound" => {
            if let Some(dev) = parse_device(arg_str(args, "device")) {
                phone_sound::set_on(dev, arg_bool(args, "on"));
            }
        }

        // ---- Messages ----
        "messages.open" => {
            if let Some(id) = parse_device(arg_str(args, "device")) {
                messages::open_device(id);
            }
        }
        "messages.openThread" => messages::open_thread(arg_str(args, "thread").to_owned()),
        "messages.closeThread" => messages::close_thread(),
        "messages.startChat" => {
            if let Some(id) = parse_device(arg_str(args, "device")) {
                messages::start_chat(id, arg_str(args, "number").to_owned(), arg_str(args, "name").to_owned());
            }
        }
        "messages.loadOlder" => messages::load_older(),
        "messages.refresh" => messages::reload(),
        "messages.setAppFilter" => messages::set_app_filter(arg_str(args, "app").to_owned()),
        "messages.deleteChatThread" => messages::delete_chat_thread(arg_str(args, "thread").to_owned()),
        "messages.send" => messages::send(arg_str(args, "body").to_owned()),
        "messages.sendTo" => {
            let recipients: Vec<String> = args
                .get("recipients")
                .and_then(|v| v.as_array())
                .map(|a| a.iter().filter_map(|v| v.as_str()).map(str::to_owned).collect())
                .unwrap_or_default();
            messages::send_to(recipients, arg_str(args, "body").to_owned());
        }
        "messages.attachFile" => {
            let ok = messages::attach_file(arg_str(args, "path").to_owned());
            return json!({ "ok": ok });
        }
        "messages.pasteClipboardImage" => {
            let ok = messages::paste_clipboard_image();
            return json!({ "ok": ok });
        }
        "messages.clearAttachment" => messages::clear_attachment(),
        "messages.saveDraft" => {
            messages::save_draft(arg_str(args, "thread").to_owned(), arg_str(args, "text").to_owned());
        }
        "messages.chooseSim" => messages::choose_sim(arg_str(args, "sim").to_owned()),

        // ---- Calls ----
        "calls.open" => {
            if let Some(id) = parse_device(arg_str(args, "device")) {
                calls::open_device(id);
            }
        }
        "calls.ensureContacts" => {
            if let Some(id) = parse_device(arg_str(args, "device")) {
                calls::ensure_contacts(id);
            }
        }
        "calls.refresh" => calls::reload(),
        "calls.loadOlder" => calls::load_older(),
        "calls.chooseSim" => {
            if let Some(id) = parse_device(arg_str(args, "device")) {
                calls::choose_sim(id, arg_str(args, "sim").to_owned());
            }
        }
        "calls.dial" => {
            if let Some(id) = parse_device(arg_str(args, "device")) {
                calls::dial(id, arg_str(args, "number").to_owned());
            }
        }
        "calls.command" => {
            let preferred = parse_device(arg_str(args, "device"));
            if let Some((dev, call, _)) = calls::active_or_ringing(preferred) {
                let cmd_val = match arg_str(args, "action") {
                    "answer" => Some(CallCommand::Answer),
                    "decline" | "end" => Some(CallCommand::Decline),
                    "mute" => Some(CallCommand::Mute(arg_bool(args, "on"))),
                    "speaker" => Some(CallCommand::Speaker(arg_bool(args, "on"))),
                    "hold" => Some(CallCommand::Hold(arg_bool(args, "on"))),
                    "volume" => Some(CallCommand::Volume(arg_bool(args, "up"))),
                    "dtmf" => arg_str(args, "digit").chars().next().map(CallCommand::Dtmf),
                    _ => None,
                };
                if let Some(c) = cmd_val {
                    calls::command(dev, call.id, c);
                }
            }
        }
        "calls.connectPcAudio" => {
            calls::connect_pc_audio(parse_device(arg_str(args, "device")));
            crate::bridge::notify_changes(Changes::CALLS);
        }
        "calls.openBluetoothSettings" => calls::open_bluetooth_settings(),
        "calls.chooseMic" => {
            call_audio::set_mic(arg_str(args, "id").to_owned());
            crate::bridge::notify_changes(Changes::CALLS);
        }
        "calls.moveCallAudio" => {
            let preferred = parse_device(arg_str(args, "device"));
            if let Some((dev, _, _)) = calls::active_or_ringing(preferred) {
                if arg_bool(args, "here") {
                    call_audio::start(dev);
                } else {
                    call_audio::stop(dev);
                }
            }
        }

        // ---- Photos ----
        "photos.open" => {
            if let Some(id) = parse_device(arg_str(args, "device")) {
                photos::open_device(id);
            }
        }
        "photos.selectAlbum" => photos::select_album(arg_str(args, "album").to_owned()),
        "photos.setColumns" => photos::set_columns(arg_i64(args, "columns").max(0) as u32),
        "photos.refresh" => photos::reload(),
        "photos.loadOlder" => photos::load_older(),
        "photos.needThumb" => photos::need_thumb(arg_str(args, "id").to_owned()),
        "photos.dropThumb" => photos::drop_thumb(arg_str(args, "id")),
        "photos.ensureFull" => photos::ensure_full(arg_str(args, "id").to_owned()),
        "photos.openItem" => photos::open_item(arg_str(args, "id").to_owned()),
        "photos.copyItem" => photos::copy_item(arg_str(args, "id").to_owned()),
        "photos.setAsWallpaper" => photos::set_as_wallpaper(arg_str(args, "id").to_owned(), parse_device(arg_str(args, "device"))),
        "photos.deleteItems" => {
            let ids: Vec<String> = args
                .get("ids")
                .and_then(|v| v.as_array())
                .map(|a| a.iter().filter_map(|v| v.as_str()).map(str::to_owned).collect())
                .unwrap_or_default();
            photos::delete_items(ids);
        }
        "photos.saveItems" => {
            let ids: Vec<String> = args
                .get("ids")
                .and_then(|v| v.as_array())
                .map(|a| a.iter().filter_map(|v| v.as_str()).map(str::to_owned).collect())
                .unwrap_or_default();
            let folder = path_to_local(arg_str(args, "folder"));
            photos::save_items(ids, folder);
        }
        "photos.setSaveFolder" => photos::set_save_folder(path_to_local(arg_str(args, "folder"))),
        "photos.shareItems" => {
            let ids: Vec<String> = args
                .get("ids")
                .and_then(|v| v.as_array())
                .map(|a| a.iter().filter_map(|v| v.as_str()).map(str::to_owned).collect())
                .unwrap_or_default();
            photos::share_items(ids);
        }
        "photos.stageForDrag" => {
            let p = photos::stage_for_drag(arg_str(args, "id"));
            return json!({ "path": p.map(|p| p.to_string_lossy().into_owned()).unwrap_or_default() });
        }

        // ---- Mirroring & Apps ----
        "mirror.start" => {
            if let Some(id) = parse_device(arg_str(args, "device")) {
                mirror::start(id);
            }
        }
        "mirror.startApp" => {
            let pkg = arg_str(args, "pkg").to_owned();
            if let (Some(id), true) = (parse_device(arg_str(args, "device")), nectarlink_core::is_package_name(&pkg)) {
                mirror::start_app(id, pkg, arg_str(args, "label").to_owned());
            }
        }
        "mirror.createAppShortcut" => {
            if let Some(id) = parse_device(arg_str(args, "device")) {
                match crate::app_shortcuts::add(&core_host::host().data_dir, id, arg_str(args, "pkg"), arg_str(args, "label")) {
                    Ok(_) => app::show_message(tr!("Apps", "Created desktop shortcut.")),
                    Err(e) => app::show_message(e),
                }
            }
        }
        "mirror.loadApps" => {
            if let Some(id) = parse_device(arg_str(args, "device")) {
                mirror::load_apps(id);
            }
        }
        "mirror.reopen" => {
            if let Some(w) = mirror::Window::parse(arg_str(args, "key")) {
                mirror::reopen(w);
            }
        }
        "mirror.stop" => {
            if let Some(w) = mirror::Window::parse(arg_str(args, "key")) {
                mirror::stop(w);
            }
        }
        "mirror.resize" => {
            if let Some(w) = mirror::Window::parse(arg_str(args, "key")) {
                mirror::resize(w, arg_i64(args, "width").max(1) as u32, arg_i64(args, "height").max(1) as u32);
            }
        }
        "mirror.changeZoom" => {
            if let Some(w) = mirror::Window::parse(arg_str(args, "key")) {
                let ok = mirror::change_zoom(w, arg_i64(args, "steps") as i32);
                return json!({ "ok": ok });
            }
        }
        "mirror.zoomDisplay" => {
            if let Some(w) = mirror::Window::parse(arg_str(args, "key")) {
                mirror::zoom_display(w, arg_i64(args, "width").max(1) as u32, arg_i64(args, "height").max(1) as u32);
            }
        }
        "mirror.saveGeometry" => {
            if let Some(w) = mirror::Window::parse(arg_str(args, "key")) {
                mirror::save_geometry(
                    w,
                    arg_i64(args, "x") as i32,
                    arg_i64(args, "y") as i32,
                    arg_i64(args, "width").max(1) as u32,
                    arg_i64(args, "height").max(1) as u32,
                );
            }
        }
        "mirror.touch" => {
            if let Some(w) = mirror::Window::parse(arg_str(args, "key")) {
                let act = match arg_str(args, "action") {
                    "down" => TouchAction::Down,
                    "move" => TouchAction::Move,
                    "up" => TouchAction::Up,
                    _ => return json!({ "ok": false }),
                };
                mirror::input(
                    w,
                    MirrorInput::Touch {
                        action: act,
                        x: arg_f64(args, "x").clamp(0.0, 1.0) as f32,
                        y: arg_f64(args, "y").clamp(0.0, 1.0) as f32,
                    },
                );
            }
        }
        "mirror.scroll" => {
            if let Some(w) = mirror::Window::parse(arg_str(args, "key")) {
                mirror::input(
                    w,
                    MirrorInput::Scroll {
                        x: arg_f64(args, "x").clamp(0.0, 1.0) as f32,
                        y: arg_f64(args, "y").clamp(0.0, 1.0) as f32,
                        dx: arg_f64(args, "dx").clamp(-100.0, 100.0) as f32,
                        dy: arg_f64(args, "dy").clamp(-100.0, 100.0) as f32,
                    },
                );
            }
        }
        "mirror.press" => {
            if let Some(w) = mirror::Window::parse(arg_str(args, "key")) {
                mirror::input(w, MirrorInput::Key { key: arg_str(args, "name").to_owned() });
            }
        }
        "mirror.text" => {
            let txt = arg_str(args, "text");
            if let Some(w) = mirror::Window::parse(arg_str(args, "key"))
                && !txt.is_empty()
                && txt.len() <= nectarlink_core::MIRROR_MAX_TEXT_BYTES
            {
                mirror::input(w, MirrorInput::Text { text: txt.to_owned() });
            }
        }
        "mirror.paste" => {
            if let Some(w) = mirror::Window::parse(arg_str(args, "key"))
                && let win::clipboard::Clip::Text { text, .. } = win::clipboard::read_for_paste()
            {
                let mut piece = String::new();
                for c in text.chars() {
                    if piece.len() + c.len_utf8() > nectarlink_core::MIRROR_MAX_TEXT_BYTES {
                        mirror::input(w, MirrorInput::Text { text: std::mem::take(&mut piece) });
                    }
                    piece.push(c);
                }
                if !piece.is_empty() {
                    mirror::input(w, MirrorInput::Text { text: piece });
                }
            }
        }
        "mirror.toggleSound" => mirror::set_muted(!mirror::muted()),
        "mirror.screenshotClipboard" => {
            if let Some(w) = mirror::Window::parse(arg_str(args, "key")) {
                mirror::screenshot_clipboard(w);
            }
        }
        "mirror.screenshotFile" => {
            if let Some(w) = mirror::Window::parse(arg_str(args, "key")) {
                mirror::screenshot_file(w);
            }
        }
        "mirror.setFlexible" => {
            if let Some(w) = mirror::Window::parse(arg_str(args, "key")) {
                mirror::set_flexible(w, arg_bool(args, "on"));
            }
        }
        "mirror.setDark" => {
            if let Some(w) = mirror::Window::parse(arg_str(args, "key")) {
                mirror::set_dark(w, arg_bool(args, "on"));
            }
        }
        "mirror.toggleRecording" => {
            if let Some(w) = mirror::Window::parse(arg_str(args, "key")) {
                mirror::toggle_recording(w);
            }
        }
        "mirror.setStayAwake" => {
            if let Some(w) = mirror::Window::parse(arg_str(args, "key")) {
                mirror::set_stay_awake(w, arg_bool(args, "on"));
            }
        }
        "mirror.setScreenOff" => {
            if let Some(w) = mirror::Window::parse(arg_str(args, "key")) {
                mirror::set_screen_off(w, arg_bool(args, "on"));
            }
        }
        "mirror.rotate" => {
            if let Some(w) = mirror::Window::parse(arg_str(args, "key")) {
                mirror::rotate(w, arg_bool(args, "landscape"));
            }
        }
        "mirror.setKeepScreenOff" => {
            if let Some(d) = parse_device(arg_str(args, "device")) {
                mirror::remember_screen_off(d, arg_bool(args, "on"));
            }
        }
        "mirror.toggleKeyboard" => {
            if let Some(d) = parse_device(arg_str(args, "device")) {
                mirror::toggle_remote_keyboard(d);
            }
        }
        "mirror.stopKeyboard" => mirror::set_remote_keyboard(None),
        "mirror.keyboardPress" => {
            if let Some(d) = parse_device(arg_str(args, "device")) {
                mirror::keyboard_press(d, arg_str(args, "name").to_owned());
            }
        }
        "mirror.keyboardText" => {
            if let Some(d) = parse_device(arg_str(args, "device")) {
                mirror::keyboard_text(d, arg_str(args, "text").to_owned());
            }
        }
        "mirror.keyboardPaste" => {
            if let Some(d) = parse_device(arg_str(args, "device")) {
                mirror::keyboard_paste(d);
            }
        }
        "mirror.dropFiles" => {
            let urls: Vec<String> = args
                .get("paths")
                .and_then(|v| v.as_array())
                .map(|a| a.iter().filter_map(|v| v.as_str()).map(str::to_owned).collect())
                .unwrap_or_default();
            mirror::drop_files(&urls);
        }
        "mirror.requestControl" => mirror::request_control(arg_str(args, "key")),
        "mirror.enterWindow" => mirror::enter_window(arg_str(args, "key")),
        "mirror.leaveWindow" => mirror::leave_window(),
        "mirror.syncClipboardOnFocus" => {
            if let Some(w) = mirror::Window::parse(arg_str(args, "key")) {
                mirror::sync_clipboard_on_focus(w);
            }
        }
        "apps.setPinned" => {
            let dev = arg_str(args, "device");
            let pkg = arg_str(args, "pkg");
            let on = arg_bool(args, "pinned");
            settings::set_app_pinned(dev, pkg, on);
            mutate_settings(|s| {
                s.pinned_apps = settings::pinned_apps_all();
            });
        }

        // ---- Deck ----
        "deck.setPageActive" => deck::set_page_active(arg_bool(args, "active")),
        "deck.addPage" => {
            let id = deck::add_page(arg_str(args, "name"));
            return json!({ "id": id });
        }
        "deck.renamePage" => deck::rename_page(arg_str(args, "pageId"), arg_str(args, "name")),
        "deck.removePage" => deck::remove_page(arg_str(args, "pageId")),
        "deck.resetDefault" => deck::reset_default(),
        "deck.moveTile" => {
            let from = arg_i64(args, "from").max(0) as usize;
            let to = arg_i64(args, "to").max(0) as usize;
            deck::move_tile(arg_str(args, "pageId"), from, to);
        }
        "deck.removeTile" => deck::remove_tile(arg_str(args, "pageId"), arg_str(args, "tileId")),
        "deck.saveTile" => {
            let action = match deck::build_action(
                arg_str(args, "kind"),
                arg_str(args, "param"),
                arg_bool(args, "ctrl"),
                arg_bool(args, "alt"),
                arg_bool(args, "shift"),
                arg_bool(args, "win"),
            ) {
                Ok(a) => a,
                Err(e) => return json!({ "error": e }),
            };
            if let Err(e) = deck::save_tile(
                arg_str(args, "pageId"),
                arg_str(args, "tileId"),
                arg_str(args, "label"),
                arg_str(args, "icon"),
                arg_str(args, "color"),
                action,
            ) {
                return json!({ "error": e });
            }
        }
        "deck.testTile" => {
            if let Err(e) = deck::execute_tile(arg_str(args, "tileId")) {
                app::show_message(e);
            }
        }
        "deck.saveObs" => {
            let port_arg = arg_i64(args, "port");
            let (cur_port, cur_pw) = settings::obs_settings();
            let port = u16::try_from(port_arg).ok().filter(|&p| p >= 1024).unwrap_or(cur_port);
            let protected = if arg_bool(args, "replacePassword") {
                let pw = arg_str(args, "password");
                if pw.is_empty() { None } else { settings::protect_obs_password(pw) }
            } else {
                cur_pw
            };
            settings::set_obs_settings(port, protected.clone());
            mutate_settings(|s| {
                s.obs_port = port;
                s.obs_password = protected;
            });
        }
        "deck.testObs" => {
            std::thread::spawn(|| {
                let msg = match deck::send_obs_command("GetVersion") {
                    Ok(()) => tr!("Prefs", "Connected to OBS."),
                    Err(e) => e,
                };
                app::show_message(msg);
            });
        }

        // ---- Timeline ----
        "timeline.query" => return timeline_query_json(args),
        "timeline.open" => {
            if let Some(entry) = timeline_find_entry(arg_str(args, "id")) {
                match entry.kind {
                    TimelineKind::File | TimelineKind::Photo | TimelineKind::Recording => {
                        if let Some(p) = entry.target.lines().map(str::trim).find(|s| !s.is_empty()) {
                            let path = std::path::Path::new(p);
                            if path.exists() {
                                transfers::open(path);
                            } else {
                                app::show_message(tr!("Timeline", "That file was moved or deleted."));
                            }
                        }
                    }
                    TimelineKind::Link => {
                        let url = if entry.target.is_empty() { &entry.title } else { &entry.target };
                        if let Some(u) = links::as_link(url) {
                            if let Err(e) = links::open_here(u) {
                                app::show_message(e);
                            }
                        } else {
                            app::show_message(tr!("Timeline", "That isn't a web link."));
                        }
                    }
                    _ => {}
                }
            }
        }
        "timeline.reveal" => {
            if let Some(entry) = timeline_find_entry(arg_str(args, "id"))
                && let Some(p) = entry.target.lines().map(str::trim).find(|s| !s.is_empty())
            {
                let path = std::path::Path::new(p);
                if path.exists() {
                    transfers::show_in_folder(path);
                } else {
                    app::show_message(tr!("Timeline", "That file was moved or deleted."));
                }
            }
        }
        "timeline.copy" => {
            if let Some(entry) = timeline_find_entry(arg_str(args, "id")) {
                match entry.kind {
                    TimelineKind::Clip => {
                        if let Some(clip_id) = entry.ref_id.as_deref() {
                            clipboard::copy_history_item(clip_id);
                        }
                    }
                    TimelineKind::Link => {
                        let url = if entry.target.is_empty() { &entry.title } else { &entry.target };
                        match win::clipboard::write(url) {
                            Ok(()) => app::show_message(tr!("Timeline", "Copied link to clipboard.")),
                            Err(e) => app::show_message(e),
                        }
                    }
                    TimelineKind::Photo | TimelineKind::File => {
                        if let Some(p) = entry.target.lines().map(str::trim).find(|s| !s.is_empty()) {
                            let path = std::path::Path::new(p);
                            let mime = if path.extension().and_then(|e| e.to_str()).is_some_and(|e| e.eq_ignore_ascii_case("png")) {
                                "image/png"
                            } else {
                                "image/jpeg"
                            };
                            if let Ok(bytes) = std::fs::read(path)
                                && win::clipboard::write_image(mime, &bytes).is_ok()
                            {
                                app::show_message(tr!("Timeline", "Copied to clipboard."));
                            }
                        }
                    }
                    _ => {}
                }
            }
        }
        "timeline.sendAgain" => {
            if let Some(entry) = timeline_find_entry(arg_str(args, "id")) {
                let target = parse_device(arg_str(args, "device")).unwrap_or(entry.device_id);
                match entry.kind {
                    TimelineKind::File | TimelineKind::Photo | TimelineKind::Recording => {
                        let paths: Vec<PathBuf> = entry
                            .target
                            .lines()
                            .map(str::trim)
                            .filter(|s| !s.is_empty())
                            .map(PathBuf::from)
                            .filter(|p| p.exists())
                            .collect();
                        if !paths.is_empty() {
                            transfers::send(target, paths);
                        }
                    }
                    TimelineKind::Clip => {
                        if let Some(clip_id) = entry.ref_id
                            && let Some(node) = core_host::node()
                        {
                            core_host::spawn(async move {
                                let _ = node.resend_clipboard_history(target, &clip_id).await;
                            });
                        }
                    }
                    TimelineKind::Link => {
                        let url = if entry.target.is_empty() { entry.title } else { entry.target };
                        if !url.is_empty() {
                            links::send_to(target, url);
                        }
                    }
                    _ => {}
                }
            }
        }
        "timeline.remove" => {
            if let Ok(id) = arg_str(args, "id").parse::<i64>()
                && let Some(node) = core_host::node()
            {
                let _ = node.delete_timeline_entry(id);
            }
        }
        "timeline.clearAll" => {
            if let Some(node) = core_host::node() {
                let _ = node.clear_timeline();
            }
        }

        // ---- Webcam ----
        "webcam.setupAddon" => webcam::setup_addon(),
        "webcam.removeAddon" => webcam::remove_addon(),
        "webcam.selectPhone" => {
            if let Some(id) = parse_device(arg_str(args, "device")) {
                webcam::set_selected_phone(id);
                mutate_settings(|s| s.webcam_phone = Some(id.to_string()));
            }
        }
        "webcam.setResolution" => {
            let h = arg_i64(args, "height").max(0) as u32;
            webcam::set_height(h);
            mutate_settings(|s| s.webcam_height = h);
        }
        "webcam.setCamera" => {
            let cam = arg_str(args, "camera").to_owned();
            webcam::set_camera(&cam);
            mutate_settings(|s| s.webcam_camera = webcam::camera().to_owned());
        }
        "webcam.setMirror" => {
            let on = arg_bool(args, "mirror");
            webcam::set_mirror(on);
            mutate_settings(|s| s.webcam_mirror = on);
        }
        "webcam.setTorch" => {
            let on = arg_bool(args, "torch");
            webcam::set_torch(on);
            mutate_settings(|s| s.webcam_torch = on);
        }
        "webcam.setPreviewVisible" => webcam::set_preview_visible("settings", arg_bool(args, "visible")),
        "webcam.start" => webcam::start(parse_device(arg_str(args, "device"))),
        "webcam.stop" => webcam::stop(),

        // ---- Phone Mic ----
        "phoneMic.start" => {
            if let Some(id) = parse_device(arg_str(args, "device")) {
                phone_mic::start(id);
            }
        }
        "phoneMic.stop" => phone_mic::stop(),
        "phoneMic.chooseOutput" => {
            let id = arg_str(args, "id").to_owned();
            phone_mic::set_output(id.clone());
            mutate_settings(|s| s.phone_mic_output = Some(id));
        }
        "phoneMic.setGain" => {
            let gain = arg_i64(args, "gain").max(0) as u32;
            phone_mic::set_gain(gain);
            mutate_settings(|s| s.phone_mic_gain = phone_mic::gain());
        }
        "phoneMic.refresh" => crate::bridge::notify_changes(Changes::MIC),

        // ---- Photo Backup ----
        "photoBackup.setEnabled" => {
            if let Some(id) = parse_device(arg_str(args, "device")) {
                photo_backup::set_enabled(id, arg_bool(args, "enabled"));
            }
        }
        "photoBackup.setVideos" => {
            if let Some(id) = parse_device(arg_str(args, "device")) {
                photo_backup::set_videos(id, arg_bool(args, "videos"));
            }
        }
        "photoBackup.setFolder" => {
            if let Some(id) = parse_device(arg_str(args, "device"))
                && let Some(folder) = path_to_local(arg_str(args, "folder"))
            {
                photo_backup::set_folder(id, folder);
            }
        }
        "photoBackup.backUpNow" => {
            if let Some(id) = parse_device(arg_str(args, "device")) {
                photo_backup::back_up_now(id);
            }
        }
        "photoBackup.openFolder" => {
            if let Some(id) = parse_device(arg_str(args, "device")) {
                let name = core_host::host().hub.read(|s| s.name_of(&id)).unwrap_or_default();
                let folder = photo_backup::summary(id, &name).folder;
                if std::fs::create_dir_all(&folder).is_ok() {
                    let _ = win::shell::open_folder(&folder);
                }
            }
        }

        // ---- Phone Install (USB ADB) ----
        "phoneInstall.watch" => phone_install::watch(arg_bool(args, "watch")),
        "phoneInstall.getTools" => phone_install::get_tools(),
        "phoneInstall.install" => phone_install::install(arg_str(args, "serial").to_owned()),
        "phoneInstall.replace" => phone_install::replace(arg_str(args, "serial").to_owned()),
        "phoneInstall.openOnPhone" => {
            let serial = arg_str(args, "serial").to_owned();
            std::thread::spawn(move || phone_install::open_on_phone(&serial));
        }
        "phoneInstall.dismiss" => phone_install::dismiss(),

        // ---- AI Agents ----
        "agents.add" => {
            let grants = if arg_str(args, "preset") == "everyday" {
                nectarlink_actions::preset_everyday()
            } else {
                nectarlink_actions::preset_read_only()
            };
            match agents::add_client(
                arg_str(args, "label"),
                arg_str(args, "name"),
                arg_str(args, "kind"),
                &grants,
            ) {
                Ok(_) => return json!({ "error": "" }),
                Err(e) => return json!({ "error": e }),
            }
        }
        "agents.remove" => agents::remove_client(arg_str(args, "id")),
        "agents.setPaused" => {
            let p = arg_bool(args, "paused");
            agents::update_client(arg_str(args, "id"), |c| c.paused = p);
        }
        "agents.setReadOnly" => {
            let ro = arg_bool(args, "readOnly");
            agents::update_client(arg_str(args, "id"), |c| c.read_only = ro);
        }
        "agents.setGrant" => {
            if let Some(g) = Grant::parse(arg_str(args, "grant")) {
                let on = arg_bool(args, "on");
                agents::update_client(arg_str(args, "id"), |c| {
                    if on {
                        c.grants.insert(g);
                    } else {
                        c.grants.remove(&g);
                        if g.access == Access::Write {
                            c.grants.insert(Grant::new(g.scope, Access::Read));
                        }
                    }
                });
            }
        }
        "agents.setAccess" => {
            if let Some(scope) = Scope::parse(arg_str(args, "scope")) {
                let level = arg_str(args, "level").to_owned();
                agents::update_client(arg_str(args, "id"), |c| {
                    c.grants.retain(|g| g.scope != scope);
                    match level.as_str() {
                        "read" => { c.grants.insert(Grant::new(scope, Access::Read)); }
                        "write" => { c.grants.insert(Grant::new(scope, Access::Write)); }
                        _ => {}
                    }
                });
            }
        }
        "agents.answer" => {
            let dec = match arg_str(args, "decision") {
                "allow" => Decision::Allow,
                "always" => Decision::AllowAlways,
                _ => Decision::Deny,
            };
            agents::answer(arg_str(args, "id"), dec);
        }
        "agents.clearActivity" => agents::clear_activity(),

        // ---- Report a problem ----
        "report.open" => report::open(),
        "report.prepare" => {
            report::prepare(
                arg_str(args, "area"),
                arg_str(args, "other"),
                arg_str(args, "text"),
                arg_bool(args, "justNow"),
                arg_i64(args, "parts") as i32,
            );
        }
        "report.openOnGithub" => return json!({ "ok": report::open_on_github() }),
        "report.send" => report::send(),
        "report.openSentIssue" => return json!({ "ok": report::open_sent_issue() }),
        "report.crashSend" => report::crash_send(),
        "report.crashDone" => report::crash_done(arg_bool(args, "look")),
        "report.crashOff" => report::crash_off(),

        // ---- Preferences & Rules & Storage ----
        "prefs.set" => {
            return apply_pref_update(args);
        }
        "prefs.restartWalkthrough" => {
            mutate_settings(|s| {
                s.walkthrough_step = 0;
                s.walkthrough_completed = false;
            });
        }
        "prefs.chooseRecordingsFolder" => {
            if let Some(p) = path_to_local(arg_str(args, "folder")) {
                recordings::set_folder(Some(p.clone()));
                mutate_settings(|s| s.recordings_folder = Some(p));
            }
        }
        "prefs.resetRecordingsFolder" => {
            recordings::set_folder(None);
            mutate_settings(|s| s.recordings_folder = None);
        }
        "prefs.addFolderSyncRule" => {
            let phone_id = arg_str(args, "deviceId").trim().to_owned();
            let Some(pc_path) = path_to_local(arg_str(args, "pcFolder")) else {
                return json!({ "ok": false });
            };
            let pc_trimmed = pc_path.to_string_lossy().into_owned();
            let Some(phone_trimmed) = folder_sync::phone_folder_path(arg_str(args, "phoneFolder")) else {
                return json!({ "ok": false });
            };
            let mut rules = settings::folder_sync_rules();
            if !rules.iter().any(|r| r.phone_id == phone_id && r.phone_folder == phone_trimmed && r.pc_folder == pc_trimmed) {
                let next_id = new_rule_id("sync", rules.iter().map(|r| r.id.as_str()));
                let mode = match arg_str(args, "mode") {
                    m if m == folder_sync::MODE_PC_TO_PHONE || m == folder_sync::MODE_TWO_WAY => m.to_owned(),
                    _ => folder_sync::MODE_PHONE_TO_PC.to_owned(),
                };
                rules.push(folder_sync::FolderSyncRule {
                    id: next_id,
                    phone_id,
                    phone_folder: phone_trimmed,
                    pc_folder: pc_trimmed,
                    remove_deleted: arg_bool(args, "removeDeleted") && mode != folder_sync::MODE_PC_TO_PHONE,
                    mode,
                    enabled: true,
                });
                settings::set_folder_sync_rules(rules.clone());
                mutate_settings(|s| s.folder_sync_rules = rules);
            }
        }
        "prefs.removeFolderSyncRule" => {
            let id = arg_str(args, "ruleId").to_owned();
            let mut rules = settings::folder_sync_rules();
            rules.retain(|r| r.id != id);
            folder_sync::forget_rule(&id);
            settings::set_folder_sync_rules(rules.clone());
            mutate_settings(|s| s.folder_sync_rules = rules);
        }
        "prefs.setFolderSyncRuleEnabled" => {
            let id = arg_str(args, "ruleId");
            let on = arg_bool(args, "enabled");
            let mut rules = settings::folder_sync_rules();
            if let Some(r) = rules.iter_mut().find(|r| r.id == id) {
                r.enabled = on;
            }
            settings::set_folder_sync_rules(rules.clone());
            mutate_settings(|s| s.folder_sync_rules = rules);
        }
        "prefs.syncFoldersNow" => folder_sync::trigger_sync_now(),
        "prefs.addAutomationRule" => {
            let trig = arg_str(args, "trigger").trim().to_owned();
            let act = arg_str(args, "action").trim().to_owned();
            if !trig.is_empty() && !act.is_empty() {
                let mut rules = settings::automation_rules();
                let next_id = new_rule_id("auto", rules.iter().map(|r| r.id.as_str()));
                rules.push(crate::automations::AutomationRule {
                    id: next_id,
                    name: format!("{trig} → {act}"),
                    enabled: true,
                    trigger: trig,
                    action_kind: act,
                    action_arg: arg_str(args, "param").trim().to_owned(),
                });
                settings::set_automation_rules(rules.clone());
                mutate_settings(|s| s.automation_rules = rules);
            }
        }
        "prefs.removeAutomationRule" => {
            let id = arg_str(args, "ruleId");
            let mut rules = settings::automation_rules();
            rules.retain(|r| r.id != id);
            settings::set_automation_rules(rules.clone());
            mutate_settings(|s| s.automation_rules = rules);
        }
        "prefs.setAutomationRuleEnabled" => {
            let id = arg_str(args, "ruleId");
            let on = arg_bool(args, "enabled");
            let mut rules = settings::automation_rules();
            if let Some(r) = rules.iter_mut().find(|r| r.id == id) {
                r.enabled = on;
            }
            settings::set_automation_rules(rules.clone());
            mutate_settings(|s| s.automation_rules = rules);
        }
        "storage.refreshRetention" => app::refresh_data_retention(),
        "storage.clearPhotoThumbs" => {
            photos::clear_cache();
            app::refresh_data_retention();
        }
        "storage.clearNotificationCache" => {
            notifications::clear_cached_images();
            app::refresh_data_retention();
        }
        "storage.clearMessageCache" => {
            messages::clear_cache();
            app::refresh_data_retention();
        }
        "storage.clearNotificationHistory" => {
            core_host::host().hub.update(|s| {
                s.history.clear();
                Changes::HISTORY
            });
            app::refresh_data_retention();
        }

        _ => {
            tracing::warn!(cmd, "unknown winui bridge command");
            return json!({ "ok": false, "error": "unknown command" });
        }
    }
    json!({ "ok": true })
}

fn apply_pref_update(args: &Value) -> Value {
    let Some(obj) = args.as_object() else {
        return json!({ "ok": false });
    };
    let dir = core_host::host().data_dir.clone();
    let mut need_hotkeys = false;
    let mut need_notif_apps = false;
    let res = settings::Settings::update(&dir, |s| {
        for (k, v) in obj {
            match k.as_str() {
                "theme" => if let Some(sv) = v.as_str() { s.theme = settings::Theme::parse(sv); }
                "seed" => if let Some(sv) = v.as_str() { s.seed = sv.to_owned(); }
                "colorMode" => if let Some(sv) = v.as_str() {
                    s.color_mode = match sv {
                        "light" => settings::ColorMode::Light,
                        "dark" => settings::ColorMode::Dark,
                        _ => settings::ColorMode::System,
                    };
                }
                "amoled" => if let Some(b) = v.as_bool() { s.amoled = b; }
                "backdrop" => if let Some(b) = v.as_bool() { s.backdrop = b; }
                "combMotion" => if let Some(b) = v.as_bool() { s.comb_motion = b; }
                "closeToTray" => if let Some(b) = v.as_bool() { s.close_to_tray = b; }
                "trayOneColour" => if let Some(b) = v.as_bool() {
                    s.tray_one_colour = b;
                    win::tray::set_one_colour(b);
                }
                "autoClipboard" => if let Some(b) = v.as_bool() {
                    s.auto_clipboard = b;
                    clipboard::set_auto_send(b);
                }
                "clipboardHistory" => if let Some(b) = v.as_bool() {
                    s.clipboard_history = b;
                    clipboard::set_history_enabled(b);
                }
                "suggestClipboardActions" => if let Some(b) = v.as_bool() {
                    s.suggest_clipboard_actions = b;
                    clipboard::set_suggest_actions(b);
                }
                "autoCopyOtp" => if let Some(b) = v.as_bool() {
                    s.auto_copy_otp = b;
                    notifications::set_auto_copy_otp(b);
                }
                "pauseMediaOnCall" => if let Some(b) = v.as_bool() {
                    s.pause_media_on_call = b;
                    calls::set_pause_media_on_call(b);
                }
                "language" => if let Some(sv) = v.as_str() {
                    s.language = sv.to_owned();
                    i18n::install(sv);
                }
                "muteWhileWatched" => if let Some(b) = v.as_bool() {
                    s.mute_while_watched = b;
                    pc_mirror::set_mute_while_watched(b);
                }
                "syncDnd" => if let Some(b) = v.as_bool() {
                    s.sync_dnd = b;
                    notifications::set_sync_dnd(b);
                }
                "enterSends" => if let Some(b) = v.as_bool() { s.enter_sends = b; }
                "rightClickLongPress" => if let Some(b) = v.as_bool() { s.right_click_long_press = b; }
                "hideMirrorToolbar" => if let Some(b) = v.as_bool() { s.hide_mirror_toolbar = b; }
                "quietWhenLocked" => if let Some(b) = v.as_bool() {
                    s.quiet_when_locked = b;
                    notifications::set_quiet_when_locked(b);
                }
                "holdWhenLocked" => if let Some(b) = v.as_bool() {
                    s.hold_when_locked = b;
                    notifications::set_hold_when_locked(b);
                }
                "sendToMenu" => if let Some(b) = v.as_bool() {
                    s.send_to_menu = b;
                    send_to::set_enabled(b);
                }
                "autoUpdate" => if let Some(b) = v.as_bool() {
                    s.auto_update = b;
                    updater::set_auto(b);
                }
                "installUpdates" => if let Some(b) = v.as_bool() {
                    s.install_updates = b;
                    updater::set_auto_install(b);
                }
                "updateChannel" => if let Some(sv) = v.as_str() {
                    s.update_channel = sv.to_owned();
                    updater::set_channel(version::Channel::parse(sv).unwrap_or_else(updater::channel));
                }
                "batteryAlerts" => if let Some(b) = v.as_bool() {
                    s.battery_alerts = b;
                    crate::battery::set_enabled(b);
                }
                "batteryFullThreshold" => if let Some(n) = v.as_u64() {
                    s.battery_full_threshold = n.clamp(50, 100) as u8;
                }
                "batteryLowThreshold" => if let Some(n) = v.as_u64() {
                    s.battery_low_threshold = n.clamp(5, 50) as u8;
                }
                "phoneSoundBufferMs" => if let Some(n) = v.as_u64() {
                    s.phone_sound_buffer_ms = n.clamp(20, 500) as u16;
                }
                "uiScale" => if let Some(n) = v.as_u64() {
                    s.ui_scale = n as u16;
                }
                "startWithWindows" => if let Some(b) = v.as_bool() {
                    s.start_with_windows = Some(b);
                    startup::apply(b);
                }
                "recordingsFormat" => if let Some(sv) = v.as_str() {
                    let fmt = settings::RecordingFormat::from_str_lossy(sv);
                    s.recordings_format = fmt;
                    recordings::set_format(fmt);
                }
                "timelineRetentionDays" => if let Some(n) = v.as_u64() {
                    s.timeline_retention_days = n as u32;
                    if let Some(node) = core_host::node() {
                        let _ = node.set_timeline_retention(nectarlink_core::TimelineRetention {
                            max_days: s.timeline_retention_days,
                            max_entries: nectarlink_core::DEFAULT_TIMELINE_MAX_ENTRIES,
                        });
                    }
                }
                "continuityPhotoHotkey" => if let Some(sv) = v.as_str() { s.continuity_photo_hotkey = sv.to_owned(); need_hotkeys = true; }
                "continuityScanHotkey" => if let Some(sv) = v.as_str() { s.continuity_scan_hotkey = sv.to_owned(); need_hotkeys = true; }
                "commandPaletteHotkey" => if let Some(sv) = v.as_str() { s.command_palette_hotkey = sv.to_owned(); need_hotkeys = true; }
                "shelfHotkey" => if let Some(sv) = v.as_str() { s.shelf_hotkey = sv.to_owned(); need_hotkeys = true; }
                "localApiEnabled" => if let Some(b) = v.as_bool() { s.local_api_enabled = b; }
                "localApiPort" => if let Some(n) = v.as_u64() { s.local_api_port = (n as u16).max(1024); }
                "localApiPrivate" => if let Some(b) = v.as_bool() { s.local_api_private = b; }
                "agentAccessEnabled" => if let Some(b) = v.as_bool() { s.agent_access_enabled = b; }
                "agentReadOnly" => if let Some(b) = v.as_bool() { s.agent_read_only = b; }
                "clipboardAutoCopyReceivedFiles" => if let Some(b) = v.as_bool() { s.clipboard_auto_copy_received_files = b; }
                "openSharedFiles" => if let Some(b) = v.as_bool() { s.open_shared_files = b; }
                "syncWifiOnly" => if let Some(b) = v.as_bool() { s.sync_wifi_only = b; }
                "syncPasswords" => if let Some(b) = v.as_bool() { s.sync_passwords = b; }
                "keepPopupsPaused" => if let Some(b) = v.as_bool() { s.keep_popups_paused = b; }
                "usbDebugging" => if let Some(b) = v.as_bool() { s.usb_debugging = b; }
                "notificationHistoryDays" => if let Some(n) = v.as_u64() {
                    s.notification_history_days = n as u32;
                    crate::state::set_history_days(s.notification_history_days);
                    need_notif_apps = true;
                }
                "mirrorQuality" => if let Some(sv) = v.as_str() { s.mirror_quality = sv.to_owned(); }
                "mirrorFps" => if let Some(n) = v.as_u64() { s.mirror_fps = settings::sane_mirror_fps(n as u32); }
                "flowFollowClipboard" => if let Some(b) = v.as_bool() { s.flow_follow_clipboard = b; }
                "phoneBeside" => if let Some(sv) = v.as_str() { s.phone_beside = sv.to_owned(); }
                "phoneBesideSide" => if let Some(sv) = v.as_str() { s.phone_beside_side = sv.to_owned(); }
                "nectarIsland" => if let Some(b) = v.as_bool() { s.nectar_island = b; }
                "nectarIslandCalls" => if let Some(b) = v.as_bool() { s.nectar_island_calls = b; }
                "nectarIslandTransfers" => if let Some(b) = v.as_bool() { s.nectar_island_transfers = b; }
                "nectarIslandMedia" => if let Some(b) = v.as_bool() { s.nectar_island_media = b; }
                "nectarIslandNotifications" => if let Some(b) = v.as_bool() { s.nectar_island_notifications = b; }
                "nectarIslandPosition" => if let Some(sv) = v.as_str() { s.nectar_island_position = sv.to_owned(); }
                "nectarIslandLook" => if let Some(sv) = v.as_str() { s.nectar_island_look = sv.to_owned(); }
                "nectarIslandPcMedia" => if let Some(b) = v.as_bool() { s.nectar_island_pc_media = b; }
                "streamPcAudioToPhone" => if let Some(b) = v.as_bool() { s.stream_pc_audio_to_phone = b; }
                "sharePcStorage" => if let Some(b) = v.as_bool() { s.share_pc_storage = b; }
                "forwardPcNotificationsToPhone" => if let Some(b) = v.as_bool() { s.forward_pc_notifications_to_phone = b; }
                "awayMode" => if let Some(b) = v.as_bool() { s.away_mode = b; }
                "walkthroughCompleted" => if let Some(b) = v.as_bool() { s.walkthrough_completed = b; }
                "walkthroughStep" => if let Some(n) = v.as_u64() { s.walkthrough_step = n.min(6) as u8; }
                "whatsNewDismissed" => if let Some(b) = v.as_bool() { s.whats_new_dismissed = b; }
                "hiveInviteDismissed" => if let Some(b) = v.as_bool() { s.hive_invite_dismissed = b; }
                "starPromptDone" => if let Some(b) = v.as_bool() { s.star_prompt_done = b; }
                "starPromptUpdated" => if let Some(b) = v.as_bool() { s.star_prompt_updated = b; }
                "starPromptMinutes" => if let Some(n) = v.as_u64() { s.star_prompt_minutes = n as u32; }
                "communityPromptDone" => if let Some(b) = v.as_bool() { s.community_prompt_done = b; }
                "communityPromptDue" => if let Some(b) = v.as_bool() { s.community_prompt_due = b; }
                "communityPromptCount" => if let Some(n) = v.as_u64() { s.community_prompt_count = n as u32; }
                "localsendEnabled" => if let Some(b) = v.as_bool() {
                    if let Some(node) = core_host::node() {
                        let _ = node.set_localsend_enabled(b);
                    }
                }
                _ => {}
            }
        }
    });
    if let Err(e) = res {
        tracing::warn!(error = %e, "can't save pref update");
        return json!({ "ok": false, "error": e.to_string() });
    }
    let loaded = settings::Settings::load(&dir);
    loaded.apply_runtime();
    if need_hotkeys {
        win::tray::update_hotkeys(
            &loaded.continuity_photo_hotkey,
            &loaded.continuity_scan_hotkey,
            &loaded.command_palette_hotkey,
            &loaded.shelf_hotkey,
        );
    }
    if need_notif_apps {
        core_host::host().hub.changed(Changes::HISTORY);
    }
    local_api::sync_from_settings();
    agents::sync_from_settings();
    crate::bridge::notify_changes(Changes::PREFS | Changes::STATUS);
    json!({ "ok": true })
}
