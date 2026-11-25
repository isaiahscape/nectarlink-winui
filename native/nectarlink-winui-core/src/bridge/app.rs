// SPDX-License-Identifier: GPL-3.0-or-later
//! Application-wide state, tray icon pump, Home summary store, Connection Doctor,
//! updater, Shelf, Command Palette, and service-to-UI event callbacks.

use std::{
    collections::HashMap,
    path::PathBuf,
    sync::{
        Mutex, OnceLock,
        atomic::{AtomicBool, AtomicU64, Ordering},
    },
};

use nectarlink_core::{DeviceId, Error, FeatureState, LinkState, PhoneToggleValue, features::Upgrade};
use serde_json::json;

use crate::{
    bridge::{
        EVENT_ACTIVATE, EVENT_COMMAND_PALETTE_OPEN, EVENT_OPEN_DOCTOR, EVENT_OPEN_FILE_PICKER,
        EVENT_OPEN_HANDOFF_PICKER, EVENT_PHONE_TOGGLE_FAILED, EVENT_QUIT,
        EVENT_SHELF_TOGGLE, EVENT_TOAST, EVENT_UNLOCK_RESULT, emit_event, notify_changes,
    },
    core_host,
    i18n::tr,
    state::Changes,
    win::{self, tray},
};

#[derive(Debug, Clone)]
pub(crate) struct AppUiState {
    pub current_page: String,
    pub current_device: i32,
    pub pending_dial: String,
    pub toggles_revision: i32,
    pub home_summary_revision: i32,
    pub system_dark: bool,
    pub reduce_motion: bool,
    pub power_saver: bool,
    pub wallpaper_colors: String,
    pub system_accent: String,
    pub toasts_enabled: bool,
    pub doctor_checks: String,
    pub doctor_busy: bool,
    pub relay_probes: String,
    pub relay_check_busy: bool,
    pub update_version: String,
    pub update_busy: bool,
    pub update_progress: f64,
    pub update_status: String,
    pub command_palette_open: bool,
    pub command_palette_results: String,
    pub command_palette_open_ms: f64,
    pub data_retention_summary: String,
    pub shelf_open: bool,
    pub shelf_items: String,
    pub apk_qr_path: String,
    pub apk_qr_size: i32,
    pub apk_qr_svg: String,
    pub guide_doc_open: bool,
    pub guide_doc_title: String,
    pub guide_doc_body: String,
    pub highlight_target: String,
    pub selected: Option<(DeviceId, i32)>,
    pub last_online_count: usize,
    pub last_tray_tooltip: String,
}

impl Default for AppUiState {
    fn default() -> Self {
        Self {
            current_page: "home".into(),
            current_device: 0,
            pending_dial: String::new(),
            toggles_revision: 0,
            home_summary_revision: 0,
            system_dark: false,
            reduce_motion: false,
            power_saver: false,
            wallpaper_colors: String::new(),
            system_accent: String::new(),
            toasts_enabled: true,
            doctor_checks: "[]".into(),
            doctor_busy: false,
            relay_probes: String::new(),
            relay_check_busy: false,
            update_version: String::new(),
            update_busy: false,
            update_progress: -1.0,
            update_status: String::new(),
            command_palette_open: false,
            command_palette_results: "[]".into(),
            command_palette_open_ms: 0.0,
            data_retention_summary: String::new(),
            shelf_open: false,
            shelf_items: String::new(),
            apk_qr_path: String::new(),
            apk_qr_size: 0,
            apk_qr_svg: String::new(),
            guide_doc_open: false,
            guide_doc_title: String::new(),
            guide_doc_body: String::new(),
            highlight_target: String::new(),
            selected: None,
            last_online_count: 0,
            last_tray_tooltip: String::new(),
        }
    }
}

static UI_STATE: OnceLock<Mutex<AppUiState>> = OnceLock::new();
static SHELF_OPEN: AtomicBool = AtomicBool::new(false);
static QUIT_REQUESTED: AtomicBool = AtomicBool::new(false);
static WALLPAPER_GENERATION: AtomicU64 = AtomicU64::new(0);
static RELAY_CHECK_GENERATION: AtomicU64 = AtomicU64::new(0);
static RELAY_CHECK_TASK: OnceLock<Mutex<Option<tokio::task::AbortHandle>>> = OnceLock::new();
static PENDING_JUMP_TASKS: Mutex<Vec<String>> = Mutex::new(Vec::new());
static TRAY_TX: OnceLock<std::sync::mpsc::Sender<TrayCommand>> = OnceLock::new();

#[derive(Debug)]
enum TrayCommand {
    SetTooltip(String),
    SetFindPhoneEnabled(bool),
    SetNotificationsPaused(bool),
    Stop,
}

#[derive(Debug, Clone)]
enum ToastAction {
    ClipSuggestion,
    UndoQueuedSend { device: DeviceId, item_id: Option<String> },
}

static LAST_TOAST_ACTION: OnceLock<Mutex<Option<ToastAction>>> = OnceLock::new();

fn set_last_toast_action(action: Option<ToastAction>) {
    *LAST_TOAST_ACTION.get_or_init(Mutex::default).lock().unwrap_or_else(|e| e.into_inner()) = action;
}

pub(crate) fn ui_state<T>(f: impl FnOnce(&mut AppUiState) -> T) -> T {
    let lock = UI_STATE.get_or_init(|| Mutex::new(AppUiState::default()));
    f(&mut lock.lock().unwrap_or_else(|e| e.into_inner()))
}

pub(crate) fn update_ui(changes: Changes, f: impl FnOnce(&mut AppUiState)) {
    ui_state(f);
    if !changes.is_empty() {
        notify_changes(changes);
    }
}

pub fn init_app_controller() {
    let (apk_qr_path, apk_qr_size, apk_qr_svg) =
        match crate::qr::encode("https://github.com/thtbee/nectarlink/releases/latest") {
            Ok(qr) => {
                let svg = format!(
                    "data:image/svg+xml;utf8,<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {size} {size}\" shape-rendering=\"crispEdges\"><rect width=\"{size}\" height=\"{size}\" fill=\"white\"/><path d=\"{path}\" fill=\"black\"/></svg>",
                    size = qr.size,
                    path = qr.path
                );
                (qr.path, qr.size as i32, svg)
            }
            Err(_) => (String::new(), 0, String::new()),
        };

    ui_state(|s| {
        s.apk_qr_path = apk_qr_path;
        s.apk_qr_size = apk_qr_size;
        s.apk_qr_svg = apk_qr_svg;
    });

    refresh_appearance();
    start_tray_thread();

    core_host::host().hub.subscribe(
        Changes::STATUS | Changes::DEVICES | Changes::DEVICE_METRICS | Changes::QUEUE | Changes::TOGGLES,
        || {
            sync_tray_and_selection();
            true
        },
    );
    core_host::host().hub.subscribe(Changes::CLIPBOARD | Changes::TIMELINE | Changes::TRANSFERS | Changes::PHOTOS, || {
        let open = is_shelf_open();
        if open {
            let idx = ui_state(|s| s.current_device);
            let shelf = compute_shelf_items_json(idx);
            ui_state(|s| s.shelf_items = shelf);
        }
        true
    });

    let pending: Vec<String> = std::mem::take(&mut *PENDING_JUMP_TASKS.lock().unwrap_or_else(|e| e.into_inner()));
    for task in pending {
        handle_jump_task(&task);
    }
}

fn sync_tray_and_selection() {
    let hub = &core_host::host().hub;
    let current_idx = ui_state(|s| s.current_device);
    let (devices, online, current_is_online, battery, waiting_count, selected_dev) = hub.read(|s| {
        let connected: Vec<_> =
            s.devices.iter().filter(|d| matches!(d.link, LinkState::Online { .. })).collect();
        let current_is_online = usize::try_from(current_idx)
            .ok()
            .and_then(|i| s.devices.get(i))
            .is_some_and(|d| matches!(d.link, LinkState::Online { .. }));
        let battery = match connected.as_slice() {
            [one] => one.battery.as_ref().map(|b| format_tray_device_summary(&one.info.name, b)),
            _ => None,
        };
        let waiting_count = s
            .send_queue
            .iter()
            .filter(|i| {
                matches!(
                    i.status,
                    nectarlink_core::QueueStatus::Waiting | nectarlink_core::QueueStatus::Sending
                )
            })
            .count();
        let selected_dev = usize::try_from(current_idx)
            .ok()
            .and_then(|i| s.devices.get(i))
            .map(|d| d.id);
        (s.devices.len(), connected.len(), current_is_online, battery, waiting_count, selected_dev)
    });

    ui_state(|s| {
        let prev_online = s.last_online_count;
        s.last_online_count = online;
        let should_reset = (online > prev_online
            && (!current_is_online || current_idx < 0 || (current_idx as usize) >= devices))
            || (devices > 0 && (current_idx < 0 || (current_idx as usize) >= devices));
        if should_reset {
            s.current_device = 0;
        } else if let Some((id, at)) = s.selected
            && at == current_idx
            && let Some(now) = hub.read(|st| st.devices.iter().position(|d| d.id == id))
            && now as i32 != current_idx
        {
            s.current_device = now as i32;
        }
        let idx = s.current_device;
        s.selected = selected_dev.or_else(|| selected_device(idx)).map(|id| (id, idx));
    });

    let mut tooltip = match (devices, online) {
        (0, _) => tr!("App", "NectarLink · no devices paired"),
        (_, 0) => tr!("App", "NectarLink · not connected"),
        (_, 1) => battery.map_or_else(
            || tr!("App", "NectarLink · 1 device connected"),
            |b| tr!("App", "NectarLink · %1", b),
        ),
        (_, n) => tr!("App", "NectarLink · %1 devices connected", n),
    };
    if waiting_count > 0 {
        tooltip.push_str(&format!(" · {waiting_count} waiting"));
    }

    let changed = ui_state(|s| {
        if s.last_tray_tooltip != tooltip {
            s.last_tray_tooltip.clone_from(&tooltip);
            true
        } else {
            false
        }
    });
    if let Some(tx) = TRAY_TX.get() {
        let _ = tx.send(TrayCommand::SetFindPhoneEnabled(devices > 0));
        if changed {
            let _ = tx.send(TrayCommand::SetTooltip(tooltip));
        }
    }
}

fn start_tray_thread() {
    let (tx, rx) = std::sync::mpsc::channel::<TrayCommand>();
    if TRAY_TX.set(tx).is_err() {
        return;
    }
    let _ = std::thread::Builder::new().name("nl-tray".into()).spawn(move || {
        use windows::Win32::UI::WindowsAndMessaging::{
            DispatchMessageW, MSG, PeekMessageW, PM_REMOVE, TranslateMessage,
        };
        let labels = tray::MenuLabels {
            open: tr!("App", "Open NectarLink"),
            command_palette: tr!("App", "Command palette"),
            shelf: tr!("App", "Toggle Shelf"),
            take_photo: tr!("App", "Take photo with phone"),
            scan_document: tr!("App", "Scan document with phone"),
            find_phone: tr!("App", "Find my phone"),
            open_link: tr!("App", "Open copied link on phone"),
            pause_notifications: tr!("App", "Pause phone notifications"),
            resume_notifications: tr!("App", "Resume phone notifications"),
            quit: tr!("App", "Quit NectarLink"),
        };
        let tray_obj = match tray::Tray::create("NectarLink", labels, on_tray_event) {
            Ok(t) => {
                t.set_notifications_paused(crate::notifications::paused());
                let settings = crate::settings::Settings::load(&core_host::host().data_dir);
                tray::update_hotkeys(
                    &settings.continuity_photo_hotkey,
                    &settings.continuity_scan_hotkey,
                    &settings.command_palette_hotkey,
                    &settings.shelf_hotkey,
                );
                Some(t)
            }
            Err(e) => {
                tracing::warn!(error = %e, "tray creation failed");
                None
            }
        };

        loop {
            while let Ok(cmd) = rx.try_recv() {
                match cmd {
                    TrayCommand::SetTooltip(tip) => {
                        if let Some(t) = tray_obj.as_ref() {
                            t.set_tooltip(&tip);
                        }
                    }
                    TrayCommand::SetFindPhoneEnabled(en) => {
                        if let Some(t) = tray_obj.as_ref() {
                            t.set_find_phone_enabled(en);
                        }
                    }
                    TrayCommand::SetNotificationsPaused(p) => {
                        if let Some(t) = tray_obj.as_ref() {
                            t.set_notifications_paused(p);
                        }
                    }
                    TrayCommand::Stop => return,
                }
            }
            // SAFETY: Standard Win32 non-blocking message pump for tray + hotkeys.
            unsafe {
                let mut msg = MSG::default();
                while PeekMessageW(&mut msg, None, 0, 0, PM_REMOVE).as_bool() {
                    let _ = TranslateMessage(&msg);
                    DispatchMessageW(&msg);
                }
            }
            std::thread::sleep(std::time::Duration::from_millis(25));
        }
    });
}

pub fn stop_tray_thread() {
    if let Some(tx) = TRAY_TX.get() {
        let _ = tx.send(TrayCommand::Stop);
    }
}

pub fn is_shelf_open() -> bool {
    SHELF_OPEN.load(Ordering::Relaxed)
}

pub fn toggle_shelf_from_anywhere() {
    let currently_open = is_shelf_open();
    if currently_open {
        close_shelf();
    } else {
        open_shelf();
    }
    emit_event(EVENT_SHELF_TOGGLE, &json!({ "open": !currently_open }).to_string());
}

pub fn open_shelf() {
    SHELF_OPEN.store(true, Ordering::Relaxed);
    let idx = ui_state(|s| s.current_device);
    if let (Some(dev), _, true) = resolve_shelf_device(idx) {
        refresh_shelf_photo_if_needed(dev, true);
    }
    let items = compute_shelf_items_json(idx);
    update_ui(Changes::STATUS, |s| {
        s.shelf_open = true;
        s.shelf_items = items;
    });
}

pub fn close_shelf() {
    SHELF_OPEN.store(false, Ordering::Relaxed);
    update_ui(Changes::STATUS, |s| {
        s.shelf_open = false;
        s.shelf_items.clear();
    });
}

pub fn refresh_shelf() {
    if !is_shelf_open() {
        return;
    }
    let idx = ui_state(|s| s.current_device);
    if let (Some(dev), _, true) = resolve_shelf_device(idx) {
        refresh_shelf_photo_if_needed(dev, true);
    }
    let items = compute_shelf_items_json(idx);
    update_ui(Changes::STATUS, |s| s.shelf_items = items);
}

pub fn request_quit() {
    QUIT_REQUESTED.store(true, Ordering::Release);
    emit_event(EVENT_QUIT, "{}");
}

pub fn is_quit_requested() -> bool {
    QUIT_REQUESTED.load(Ordering::Acquire)
}

pub fn request_activation() {
    emit_event(EVENT_ACTIVATE, "{}");
}

pub(crate) fn toggle_notifications_pause() {
    let paused = !crate::notifications::paused();
    crate::notifications::set_paused(paused);
    crate::settings::remember_popups_paused(paused);
    if let Some(tx) = TRAY_TX.get() {
        let _ = tx.send(TrayCommand::SetNotificationsPaused(paused));
    }
    notify_changes(Changes::STATUS | Changes::PREFS);
}

pub(crate) fn show_message(message: impl Into<String>) {
    let message = message.into();
    set_last_toast_action(None);
    emit_event(EVENT_TOAST, &json!({ "message": message, "actionLabel": "" }).to_string());
}

pub(crate) fn show_message_with_action(message: impl Into<String>, action_label: impl Into<String>) {
    let message = message.into();
    let action_label = action_label.into();
    set_last_toast_action(Some(ToastAction::ClipSuggestion));
    emit_event(EVENT_TOAST, &json!({ "message": message, "actionLabel": action_label }).to_string());
}

pub(crate) fn show_offline_queued_toast(device: DeviceId, item_id: Option<String>) -> bool {
    let (online, device_name) = core_host::host().hub.read(|s| {
        let online = s.devices.iter().any(|d| d.id == device && matches!(d.link, LinkState::Online { .. }));
        let name = s.name_of(&device).unwrap_or_else(|| tr!("App", "Your phone"));
        (online, name)
    });
    if online {
        return false;
    }
    let resolved_item_id = item_id.or_else(|| {
        core_host::node().and_then(|n| n.latest_queued_id_for_peer(device)).or_else(|| {
            core_host::host().hub.read(|s| {
                s.send_queue
                    .iter()
                    .rev()
                    .find(|i| {
                        i.peer == device
                            && matches!(
                                i.status,
                                nectarlink_core::QueueStatus::Waiting | nectarlink_core::QueueStatus::Sending
                            )
                    })
                    .map(|i| i.id.clone())
            })
        })
    });
    set_last_toast_action(Some(ToastAction::UndoQueuedSend { device, item_id: resolved_item_id }));
    let message = format!("{device_name} is offline. Will send when it's back.");
    emit_event(EVENT_TOAST, &json!({ "message": message, "actionLabel": "Undo" }).to_string());
    true
}

pub fn run_clip_suggestion() {
    let action = LAST_TOAST_ACTION.get_or_init(Mutex::default).lock().unwrap_or_else(|e| e.into_inner()).take();
    match action {
        Some(ToastAction::UndoQueuedSend { device, item_id }) => {
            if crate::continuity_camera::view().busy {
                crate::continuity_camera::cancel();
            }
            if let Some(id) = item_id {
                crate::transfers::cancel_queue_item(&id);
            } else {
                crate::transfers::cancel_all_queued(Some(device));
            }
            show_message(tr!("App", "Send cancelled"));
        }
        _ => crate::clipboard::run_last_suggestion(),
    }
}

pub(crate) fn open_dialer(device: Option<DeviceId>, number: &str) {
    let number = number.trim().to_owned();
    if number.is_empty() {
        return;
    }
    let idx = device.and_then(|device| {
        core_host::host().hub.read(|s| {
            s.devices
                .iter()
                .position(|d| d.id == device && matches!(d.link, LinkState::Online { .. }))
                .or_else(|| s.devices.iter().position(|d| matches!(d.link, LinkState::Online { .. })))
                .or_else(|| s.devices.iter().position(|d| d.id == device))
        })
    });
    let current = ui_state(|s| s.current_device);
    let resolved_idx = idx.or_else(|| online_instead_of(current));
    update_ui(Changes::STATUS, |s| {
        if let Some(i) = resolved_idx {
            s.current_device = i as i32;
        }
        s.pending_dial = number;
        s.current_page = "calls".into();
    });
    request_activation();
}

fn online_instead_of(current: i32) -> Option<usize> {
    core_host::host().hub.read(|s| {
        let selected = s.devices.get(current.max(0) as usize);
        if selected.is_some_and(|d| matches!(d.link, LinkState::Online { .. })) {
            return None;
        }
        s.devices.iter().position(|d| matches!(d.link, LinkState::Online { .. }))
    })
}

pub(crate) fn describe(error: &Error) -> String {
    match error {
        Error::Offline => tr!("Errors", "The device isn't connected right now."),
        Error::NotPaired => tr!("Errors", "That device isn't paired anymore."),
        Error::Timeout => tr!("Errors", "The device didn't answer in time."),
        Error::Denied | Error::Declined => tr!("Errors", "The device declined."),
        Error::Unsupported => tr!("Errors", "The device's app doesn't support that yet."),
        Error::QueueFull(msg) => msg.clone(),
        other => tr!("Errors", "Something went wrong: %1", other),
    }
}

pub fn install_update_in_background() {
    let already_busy = ui_state(|s| {
        if s.update_busy {
            true
        } else {
            s.update_busy = true;
            s.update_progress = -1.0;
            s.update_status = "Downloading the update…".into();
            false
        }
    });
    if already_busy {
        return;
    }
    notify_changes(Changes::UPDATE);
    std::thread::spawn(|| {
        let result = std::panic::catch_unwind(|| {
            crate::updater::install(|done, total| {
                let fraction = if total > 0 { (done as f64 / total as f64).clamp(0.0, 1.0) } else { -1.0 };
                let text = if total > 0 {
                    tr!("App", "Downloading the update · %1 of %2", megabytes(done), megabytes(total))
                } else {
                    tr!("App", "Downloading the update…")
                };
                update_ui(Changes::UPDATE, |s| {
                    s.update_progress = fraction;
                    s.update_status = text;
                });
            })
        })
        .unwrap_or_else(|_| Err(tr!("App", "The update couldn't be installed. Try again.")));

        match result {
            Ok(()) => update_ui(Changes::UPDATE, |s| {
                s.update_progress = -1.0;
                s.update_status = "Installing. NectarLink reopens by itself.".into();
            }),
            Err(message) => {
                update_ui(Changes::UPDATE, |s| {
                    s.update_busy = false;
                    s.update_progress = -1.0;
                    s.update_status.clear();
                });
                show_message(message);
            }
        }
    });
}

fn megabytes(bytes: u64) -> String {
    format!("{:.1} MB", bytes as f64 / 1_000_000.0)
}

pub fn check_for_updates() {
    let busy = ui_state(|s| {
        if s.update_busy {
            true
        } else {
            s.update_busy = true;
            false
        }
    });
    if busy {
        return;
    }
    notify_changes(Changes::UPDATE);
    std::thread::spawn(|| {
        let message = std::panic::catch_unwind(|| crate::updater::check_now(true))
            .unwrap_or_else(|_| Some(tr!("App", "Couldn't check for updates. Try again.")));
        update_ui(Changes::UPDATE, |s| {
            s.update_busy = false;
            s.update_version = crate::updater::available().map(|u| u.title()).unwrap_or_default();
        });
        if let Some(message) = message {
            show_message(message);
        }
    });
}

pub fn diagnose_in_background(fix: Option<String>) {
    let busy = ui_state(|s| {
        if s.doctor_busy {
            true
        } else {
            s.doctor_busy = true;
            false
        }
    });
    if busy {
        return;
    }
    notify_changes(Changes::STATUS);
    let spawned = std::thread::Builder::new().name("doctor".into()).spawn(move || {
        if let Some(fix) = fix {
            crate::doctor::fix(&fix);
            if fix == "reconnect" {
                std::thread::sleep(std::time::Duration::from_secs(3));
            }
        }
        let now = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_or(0, |d| d.as_secs() as i64);
        let checks = std::panic::catch_unwind(|| crate::doctor::diagnose(&crate::doctor::gather(), now))
            .unwrap_or_default();
        let json = serde_json::to_string(&checks).unwrap_or_else(|_| "[]".into());
        update_ui(Changes::STATUS, |s| {
            s.doctor_checks = json;
            s.doctor_busy = false;
        });
    });
    if spawned.is_err() {
        update_ui(Changes::STATUS, |s| s.doctor_busy = false);
    }
}

pub fn handle_jump_task(action: &str) {
    let action = action.trim().to_owned();
    if action.is_empty() {
        return;
    }
    if UI_STATE.get().is_none() {
        PENDING_JUMP_TASKS.lock().unwrap_or_else(|e| e.into_inner()).push(action);
        return;
    }
    if let Some((device, pkg)) = crate::app_shortcuts::parse_task(&action) {
        let data_dir = &core_host::host().data_dir;
        let label = crate::mirror::saved_label(data_dir, &device, &pkg).unwrap_or_else(|| pkg.clone());
        crate::mirror::start_app(device, pkg, label);
        return;
    }
    match action.as_str() {
        "send-files" => {
            update_ui(Changes::STATUS, |s| s.current_page = "home".into());
            request_activation();
            emit_event(EVENT_OPEN_FILE_PICKER, "{}");
        }
        "send-clipboard" => {
            let idx = ui_state(|s| s.current_device);
            if let Some(device) = selected_device(idx) {
                crate::clipboard::send_now(device);
            } else {
                show_message("No paired phone is connected.");
            }
        }
        "find-phone" => {
            let idx = ui_state(|s| s.current_device);
            if let Some(device) = selected_device(idx) {
                ring_device(device, true);
            } else {
                show_message("No paired phone is connected.");
            }
        }
        "take-photo" => crate::continuity_camera::start("photo"),
        "open-timeline" => {
            update_ui(Changes::STATUS, |s| s.current_page = "timeline".into());
            request_activation();
        }
        "open-guide" => {
            update_ui(Changes::STATUS, |s| s.current_page = "guide".into());
            request_activation();
        }
        "open-settings" => {
            update_ui(Changes::STATUS, |s| s.current_page = "settings".into());
            request_activation();
        }
        s if s.starts_with("learn-more:") => {
            let key = &s["learn-more:".len()..];
            open_guide_doc(key);
            update_ui(Changes::STATUS, |s| s.current_page = "guide".into());
            request_activation();
        }
        s if s.starts_with("show-me:") => {
            let target = &s["show-me:".len()..];
            update_ui(Changes::STATUS, |s| {
                s.guide_doc_open = false;
                s.highlight_target = target.to_owned();
            });
            request_activation();
        }
        other => tracing::debug!(task = other, "unknown jump list task"),
    }
}

pub(crate) fn format_tray_device_summary(name: &str, b: &nectarlink_core::Battery) -> String {
    let charging = if b.charging { ", charging" } else { "" };
    let mut parts = vec![format!("{name} · {}%{charging}", b.level)];
    if let Some(w) = b.wifi_bars {
        parts.push(tr!("App", "Wi-Fi %1/4", w));
    }
    if let Some(c) = b.cell_bars {
        let label = b.cell_type.as_deref().filter(|s| !s.is_empty()).unwrap_or("Cell");
        parts.push(format!("{label} {c}/4"));
    }
    parts.join(" · ")
}

fn on_tray_event(event: tray::TrayEvent) {
    match event {
        tray::TrayEvent::Open => request_activation(),
        tray::TrayEvent::CommandPalette => open_command_palette(),
        tray::TrayEvent::Shelf => toggle_shelf_from_anywhere(),
        tray::TrayEvent::TakePhoto => crate::continuity_camera::start("photo"),
        tray::TrayEvent::ScanDocument => crate::continuity_camera::start("scan"),
        tray::TrayEvent::OpenLinkOnPhone => crate::links::open_copied_link_on_phone(),
        tray::TrayEvent::ToggleNotificationsPause => toggle_notifications_pause(),
        tray::TrayEvent::Quit => request_quit(),
        tray::TrayEvent::FindPhone => {
            let phone = core_host::host().hub.read(|s| {
                s.devices
                    .iter()
                    .find(|d| matches!(d.link, LinkState::Online { .. }))
                    .or(s.devices.first())
                    .map(|d| d.id)
            });
            if let Some(phone) = phone {
                ring_device(phone, true);
            }
        }
        tray::TrayEvent::Resumed => {
            tracing::info!("resumed from sleep; reconnecting");
            if let Some(node) = core_host::node() {
                core_host::spawn(async move { node.network_changed().await });
            }
        }
        tray::TrayEvent::AppearanceChanged => refresh_appearance(),
    }
}

pub fn open_command_palette() {
    let results = crate::command_palette::open();
    update_ui(Changes::PALETTE, |s| {
        s.command_palette_results = results;
        s.command_palette_open = true;
    });
    emit_event(EVENT_COMMAND_PALETTE_OPEN, "{}");
    request_activation();
}

pub fn close_command_palette() {
    crate::command_palette::close();
    update_ui(Changes::PALETTE, |s| s.command_palette_open = false);
}

pub fn search_command_palette(query: &str) {
    let results = crate::command_palette::search(query);
    update_ui(Changes::PALETTE, |s| s.command_palette_results = results);
}

pub fn note_command_palette_shown() {
    let ms = crate::command_palette::mark_shown();
    update_ui(Changes::PALETTE, |s| s.command_palette_open_ms = ms);
}

pub fn run_command_palette(id: &str) {
    let action = crate::command_palette::execute(id);
    update_ui(Changes::PALETTE, |s| s.command_palette_open = false);
    let Some(action) = action else { return };
    use crate::command_palette::PaletteUiAction;
    match action {
        PaletteUiAction::NavigatePage { page, device_index } => {
            update_ui(Changes::STATUS, |s| {
                if let Some(idx) = device_index {
                    s.current_device = idx as i32;
                }
                s.current_page = page;
            });
            request_activation();
        }
        PaletteUiAction::StartChat { device_id, number, name } => {
            if let Ok(dev) = device_id.parse::<DeviceId>() {
                let idx = core_host::host().hub.read(|s| s.devices.iter().position(|d| d.id == dev));
                if let Some(idx) = idx {
                    update_ui(Changes::STATUS, |s| s.current_device = idx as i32);
                }
                crate::messages::open_device(dev);
                crate::messages::start_chat(dev, number, name);
            }
            update_ui(Changes::STATUS, |s| s.current_page = "messages".into());
            request_activation();
        }
        PaletteUiAction::OpenDialer { device_id, number } => {
            if let Ok(dev) = device_id.parse::<DeviceId>() {
                open_dialer(Some(dev), &number);
            }
        }
        PaletteUiAction::OpenHandoffFilePicker { device_index } => {
            update_ui(Changes::STATUS, |s| {
                if let Some(idx) = device_index {
                    s.current_device = idx as i32;
                }
                s.current_page = "home".into();
            });
            request_activation();
            emit_event(EVENT_OPEN_HANDOFF_PICKER, "{}");
        }
        PaletteUiAction::OpenDoctor => {
            emit_event(EVENT_OPEN_DOCTOR, "{}");
            diagnose_in_background(None);
        }
        PaletteUiAction::OpenPairing | PaletteUiAction::OpenWalkthrough => {
            update_ui(Changes::STATUS, |s| s.current_page = "home".into());
            request_activation();
        }
    }
}

pub fn refresh_appearance() {
    let sys_dark = win::system_dark();
    let accent = win::accent_colors().map(|(light, dark)| accent_json(light, dark)).unwrap_or_default();
    let reduce_motion = win::reduce_motion();
    let power_saver = win::power_saver();
    let toasts = win::toast::enabled();
    update_ui(Changes::STATUS, |s| {
        s.system_dark = sys_dark;
        s.system_accent = accent;
        s.reduce_motion = reduce_motion;
        s.power_saver = power_saver;
        s.toasts_enabled = toasts;
    });
    refresh_wallpaper_colors();
}

pub fn refresh_toasts_enabled() {
    let ps = win::power_saver();
    let te = win::toast::enabled();
    update_ui(Changes::STATUS, |s| {
        s.power_saver = ps;
        s.toasts_enabled = te;
    });
    core_host::spawn(core_host::refresh_wake());
}

fn accent_json(light: [u8; 3], dark: [u8; 3]) -> String {
    let hex = |c: [u8; 3]| format!("#{:02X}{:02X}{:02X}", c[0], c[1], c[2]);
    json!({ "light": hex(light), "dark": hex(dark) }).to_string()
}

fn refresh_wallpaper_colors() {
    let generation = WALLPAPER_GENERATION.fetch_add(1, Ordering::SeqCst) + 1;
    let _ = std::thread::Builder::new().name("wallpaper-colors".into()).spawn(move || {
        let seed = win::wallpaper::seed();
        let json = seed.map(|seed| crate::palette::scheme_json(seed).to_string()).unwrap_or_default();
        if WALLPAPER_GENERATION.load(Ordering::SeqCst) != generation {
            return;
        }
        update_ui(Changes::STATUS, |s| {
            if WALLPAPER_GENERATION.load(Ordering::SeqCst) == generation {
                s.wallpaper_colors = json;
            }
        });
    });
}

pub fn phone_colors_json(devices: &[crate::state::DeviceView]) -> String {
    let mut ordered: Vec<&crate::state::DeviceView> = devices.iter().collect();
    ordered.sort_by_key(|d| (!matches!(d.link, LinkState::Online { .. }), std::cmp::Reverse(d.paired_at)));
    let mut seen_names = std::collections::HashSet::new();
    let deduped: Vec<&crate::state::DeviceView> = ordered
        .into_iter()
        .filter(|d| {
            let key = d.info.name.trim().to_lowercase();
            key.is_empty() || seen_names.insert(key)
        })
        .collect();
    if deduped.is_empty() {
        return json!([{
            "id": "",
            "key": "phone",
            "name": "",
            "label": "My phone's colors",
            "hasAccent": false,
            "scheme": serde_json::Value::Null,
        }])
        .to_string();
    }
    let multiple = deduped.len() > 1;
    let entries: Vec<serde_json::Value> = deduped
        .into_iter()
        .map(|d| {
            let effective_accent = d.info.accent.or_else(|| {
                devices
                    .iter()
                    .filter(|o| matches!(o.link, LinkState::Online { .. }))
                    .find(|o| {
                        o.info.name.trim().eq_ignore_ascii_case(d.info.name.trim()) && o.info.accent.is_some()
                    })
                    .or_else(|| {
                        devices.iter().find(|o| {
                            o.info.name.trim().eq_ignore_ascii_case(d.info.name.trim())
                                && o.info.accent.is_some()
                        })
                    })
                    .and_then(|o| o.info.accent)
            });
            let scheme = effective_accent.map(|argb| {
                let r = ((argb >> 16) & 0xFF) as u8;
                let g = ((argb >> 8) & 0xFF) as u8;
                let b = (argb & 0xFF) as u8;
                crate::palette::scheme_json(material_colors::color::Rgb::new(r, g, b))
            });
            let id_str = d.id.to_string();
            let key = if multiple { format!("phone:{id_str}") } else { "phone".to_owned() };
            let label = if multiple {
                tr!("App", "My phone's colors (%1)", d.info.name)
            } else {
                tr!("App", "My phone's colors")
            };
            json!({
                "id": id_str,
                "key": key,
                "name": d.info.name,
                "label": label,
                "hasAccent": scheme.is_some(),
                "scheme": scheme,
            })
        })
        .collect();
    serde_json::Value::Array(entries).to_string()
}

pub fn ring_device(device: DeviceId, on: bool) {
    let Some(node) = core_host::node() else { return };
    core_host::spawn(async move {
        if let Err(e) = node.ring(device, on).await {
            show_message(describe(&e));
        }
    });
}

pub fn set_device_toggle(id: DeviceId, name: &str, on: bool) {
    let Some(node) = core_host::node() else { return };
    if let Err(e) = node.set_device_toggle(id, name, on) {
        show_message(describe(&e));
    }
    if name == nectarlink_core::unlock::TOGGLE {
        unlock_toggled(id, on);
    }
    if name == "remote_input" {
        if !on {
            crate::remote::stop_all();
        }
        core_host::host().hub.update(|s| {
            if s.remote_prompt == Some(id) {
                s.remote_prompt = None;
                Changes::REMOTE
            } else {
                Changes::NONE
            }
        });
        core_host::refresh_capabilities();
    }
    if name == "pc_mirror" {
        if !on {
            crate::pc_mirror::stop_for(&id);
        }
        core_host::host().hub.update(|s| {
            if s.pc_mirror_prompt.as_ref().is_some_and(|(d, _)| *d == id) {
                s.pc_mirror_prompt = None;
                Changes::REMOTE
            } else {
                Changes::NONE
            }
        });
        core_host::refresh_capabilities();
    }
    if name == "storage" {
        crate::storage::sync();
    }
    update_ui(Changes::TOGGLES, |s| s.toggles_revision = s.toggles_revision.wrapping_add(1));
}

fn unlock_toggled(id: DeviceId, on: bool) {
    use crate::win::unlock;
    let Some(node) = core_host::node() else { return };
    let others_on = node.paired_devices().unwrap_or_default().iter().any(|d| {
        d.id != id
            && node
                .device_toggles(d.id)
                .is_ok_and(|t| t.iter().any(|(n, v)| *n == nectarlink_core::unlock::TOGGLE && *v))
    });
    let device = id.to_string();
    let arg = match (on, unlock::is_registered()) {
        (true, false) => unlock::REGISTER_ARG,
        (false, true) if !others_on => unlock::UNREGISTER_ARG,
        _ => return,
    };
    crate::win::vcam::run_elevated(arg, move |ok| {
        core_host::refresh_capabilities();
        if on {
            let result = if ok { "registered" } else { "register_failed" };
            emit_event(EVENT_UNLOCK_RESULT, &json!({ "device": device, "result": result }).to_string());
        }
    });
}

pub fn set_up_unlock(id: DeviceId, password: String) {
    let device = id.to_string();
    core_host::spawn(async move {
        let result = match crate::win::unlock::enroll(id, password).await {
            crate::win::unlock::Enrolled::Done => "done",
            crate::win::unlock::Enrolled::WrongPassword => "wrong_password",
            crate::win::unlock::Enrolled::Declined => "declined",
            crate::win::unlock::Enrolled::PhoneAway => "phone_away",
            crate::win::unlock::Enrolled::PhoneCant => "phone_cant",
            crate::win::unlock::Enrolled::Failed => "failed",
        };
        emit_event(EVENT_UNLOCK_RESULT, &json!({ "device": device, "result": result }).to_string());
    });
}

pub fn set_phone_toggle(device: DeviceId, id: String, raw: String) {
    let Some(node) = core_host::node() else { return };
    let parsed = match id.as_str() {
        "dnd" | "flashlight" | "wifi" | "bluetooth" | "hotspot" | "pc_active" => {
            Some(PhoneToggleValue::Bool(raw == "true"))
        }
        "volume" | "brightness" => raw.parse::<u8>().ok().map(|v| PhoneToggleValue::Level(v.min(100))),
        "ringer" => Some(PhoneToggleValue::Mode(raw)),
        _ => None,
    };
    let Some(val) = parsed else { return };
    core_host::host().hub.update(|s| {
        let Some(t) = s.toggles.get(&device).cloned() else { return Changes::NONE };
        let mut next = t;
        match (id.as_str(), &val) {
            ("dnd", PhoneToggleValue::Bool(on)) => next.dnd = *on,
            ("flashlight", PhoneToggleValue::Bool(on)) => next.flashlight = Some(*on),
            ("wifi", PhoneToggleValue::Bool(on)) => next.wifi = *on,
            ("bluetooth", PhoneToggleValue::Bool(on)) => next.bluetooth = *on,
            ("volume", PhoneToggleValue::Level(v)) => next.volume = *v,
            ("brightness", PhoneToggleValue::Level(v)) => next.brightness = *v,
            ("ringer", PhoneToggleValue::Mode(m)) => next.ringer.clone_from(m),
            _ => {}
        }
        s.set_toggles(device, Some(next))
    });
    let hotspot_on = id == "hotspot" && val == PhoneToggleValue::Bool(true);
    let failed_id = id.clone();
    core_host::spawn(async move {
        match node.set_phone_toggle(device, id, val).await {
            Ok(()) if hotspot_on => crate::hotspot::hotspot_turned_on(device),
            Ok(()) => {}
            Err(e) => {
                let latest = node.phone_toggles(device);
                core_host::host().hub.update(|s| s.set_toggles(device, latest));
                show_message(describe(&e));
                emit_event(
                    EVENT_PHONE_TOGGLE_FAILED,
                    &json!({ "device": device.to_string(), "id": failed_id }).to_string(),
                );
            }
        }
    });
}

pub fn check_relays() {
    let busy = ui_state(|s| {
        if s.relay_check_busy {
            true
        } else {
            s.relay_check_busy = true;
            s.relay_probes.clear();
            false
        }
    });
    if busy {
        return;
    }
    let Some(node) = core_host::node() else {
        update_ui(Changes::STATUS, |s| s.relay_check_busy = false);
        return;
    };
    let task_slot = RELAY_CHECK_TASK.get_or_init(|| Mutex::new(None));
    if let Some(prev) = task_slot.lock().unwrap_or_else(|e| e.into_inner()).take() {
        prev.abort();
    }
    let generation = RELAY_CHECK_GENERATION.fetch_add(1, Ordering::SeqCst) + 1;
    notify_changes(Changes::STATUS);
    let task = core_host::host().runtime.spawn(async move {
        let results = serde_json::to_string(&node.check_relays().await).unwrap_or_default();
        if RELAY_CHECK_GENERATION.load(Ordering::SeqCst) != generation {
            return;
        }
        update_ui(Changes::STATUS, |s| {
            s.relay_probes = results;
            s.relay_check_busy = false;
        });
    });
    *task_slot.lock().unwrap_or_else(|e| e.into_inner()) = Some(task.abort_handle());
}

pub fn refresh_data_retention() {
    core_host::host().runtime.spawn_blocking(|| {
        let summary = compute_data_retention_json();
        update_ui(Changes::DATA_RETENTION, |s| s.data_retention_summary = summary);
    });
}

pub fn compute_data_retention_json() -> String {
    let counts = core_host::node().map(|n| n.data_retention_counts()).unwrap_or_default();
    let (notification_history, notification_history_enabled, transfer_records) =
        core_host::host().hub.read(|s| {
            let finished = s.transfers.iter().filter(|t| t.transfer.state.is_finished()).count();
            (s.history.len(), s.history_enabled, finished)
        });
    let notification_image_bytes = crate::notifications::cached_image_bytes();
    let (cached_sms_threads, cached_sms_messages, message_attachment_bytes) = crate::messages::cache_stats();
    let (photo_thumb_files, photo_thumb_bytes) = crate::photos::cache_stats();
    json!({
        "clipboardItems": counts.clipboard_items,
        "clipboardBytes": counts.clipboard_bytes,
        "timelineItems": counts.timeline_items,
        "timelineRetentionDays": counts.timeline_retention_days,
        "notificationHistory": notification_history,
        "notificationHistoryEnabled": notification_history_enabled,
        "notificationImageBytes": notification_image_bytes,
        "chatThreads": counts.chat_threads,
        "chatMessages": counts.chat_messages,
        "cachedSmsThreads": cached_sms_threads,
        "cachedSmsMessages": cached_sms_messages,
        "messageAttachmentBytes": message_attachment_bytes,
        "photoThumbFiles": photo_thumb_files,
        "photoThumbBytes": photo_thumb_bytes,
        "receivedFileRecords": counts.received_file_records as usize + transfer_records,
    })
    .to_string()
}

pub fn open_guide_doc(doc_name: &str) {
    let key = doc_name.trim().to_ascii_lowercase();
    let (title, body) = guide_topic_content(&key);
    update_ui(Changes::STATUS, |s| {
        s.guide_doc_title = title;
        s.guide_doc_body = body;
        s.guide_doc_open = true;
    });
}

pub fn close_guide_doc() {
    update_ui(Changes::STATUS, |s| s.guide_doc_open = false);
}

fn guide_topic_content(key: &str) -> (String, String) {
    let (title, body) = match key {
        "overview" => (
            tr!("Guide", "Overview"),
            tr!(
                "Guide",
                "NectarLink connects your Android phone and PC directly over Wi-Fi, USB, or Tailscale with end-to-end TLS 1.3 encryption and no cloud account.\n\nUse the cards on this page to see which features are ready on your paired phone, jump straight to any control with Show me, or read how each permission works."
            ),
        ),
        "setup" => (
            tr!("Guide", "Setup & pairing"),
            tr!(
                "Guide",
                "1. Install NectarLink on both your PC and your Android phone.\n2. Open NectarLink on your phone and scan the QR code shown in Pair a phone, or choose Pair nearby.\n3. Confirm that the 6-digit code matches on both screens.\n4. Enable only the features you want per device in Settings › Devices."
            ),
        ),
        "permissions" => (
            tr!("Guide", "Permissions"),
            tr!(
                "Guide",
                "NectarLink asks only for the Android permissions needed by features you turn on:\n- Notifications: Notification Access to mirror and reply to alerts.\n- Messages & Calls: SMS, Contacts, Call Log, and Phone state.\n- Photos & Files: Photos/Media or All Files access for gallery browsing and storage.\n- Webcam & Continuity Camera: Camera access."
            ),
        ),
        "elevated" => (
            tr!("Guide", "Power levels"),
            tr!(
                "Guide",
                "Standard mode works out of the box with normal Android permissions.\n\nAssist mode adds NectarLink's Accessibility service so you can click, scroll, and type during screen mirroring.\n\nElevated mode uses Wireless debugging (or USB ADB / root) with no separate helper app to unlock individual app windows, turning the phone screen off while mirroring, and background clipboard sync on newer Android versions."
            ),
        ),
        "troubleshooting" => (
            tr!("Guide", "Troubleshooting"),
            tr!(
                "Guide",
                "If your phone and PC can't find each other:\n- Run Connection Doctor in Settings › Connection & About to check Windows Firewall rules and network profile.\n- Ensure both devices are on the same local Wi-Fi or connected by USB.\n- Turn on Connect over mobile data in Settings › Connection & About when away from Wi-Fi."
            ),
        ),
        "faq" => (
            tr!("Guide", "FAQ"),
            tr!(
                "Guide",
                "Does NectarLink need an account or cloud server?\nNo. Pairing keys stay on your devices and connections stay on your local network unless you turn on Connect over mobile data.\n\nCan I pair more than one phone or PC?\nYes. Use Pair a device in the top bar and switch between paired devices at any time."
            ),
        ),
        "notifications" => (
            tr!("Guide", "Notifications"),
            tr!("Guide", "Mirrors phone notifications to your PC with inline replies, action buttons, and dismissal sync."),
        ),
        "messages" => (
            tr!("Guide", "Messages"),
            tr!("Guide", "Reads SMS/MMS threads and MessagingStyle chats, sends replies and MMS attachments, and supports dual-SIM selection."),
        ),
        "calls" => (
            tr!("Guide", "Calls"),
            tr!("Guide", "Notifies you of incoming calls with Answer, Decline, and Silence actions, syncs your call log and contacts, lets you dial from your PC, and supports Bluetooth HFP / call audio."),
        ),
        "clipboard" => (
            tr!("Guide", "Clipboard"),
            tr!("Guide", "Shares copied text, rich HTML/RTF, and images between your PC and phone with encrypted local history and smart context chips."),
        ),
        "transfers" => (
            tr!("Guide", "Send files"),
            tr!("Guide", "Sends files and folders both ways at Wi-Fi speed with BLAKE3 verification, pause/resume, and automatic offline queueing."),
        ),
        "photos" => (
            tr!("Guide", "Photos"),
            tr!("Guide", "Browses recent photos, videos, and albums from your phone, with multi-select save, copy, share, and Photo backup."),
        ),
        "mirroring" => (
            tr!("Guide", "Screen mirroring"),
            tr!("Guide", "Streams your phone screen or individual Android apps in hardware-decoded windows on your PC with mouse, touch, and keyboard control."),
        ),
        "pc-mirror" => (
            tr!("Guide", "Share PC screen"),
            tr!("Guide", "Streams your PC screen to your phone with DXGI Desktop Duplication, trackpad control, and presentation laser pointer."),
        ),
        "webcam" => (
            tr!("Guide", "Phone as webcam"),
            tr!("Guide", "Streams your phone's rear or front camera at up to 4K into the Windows virtual camera for Zoom, Teams, Meet, Discord, and OBS."),
        ),
        "continuity-camera" => (
            tr!("Guide", "Continuity camera"),
            tr!("Guide", "Triggers your phone camera (Ctrl+Alt+C / Ctrl+Alt+D) to snap a photo or scan a document straight to your PC clipboard."),
        ),
        "speaker" => (
            tr!("Guide", "Phone as speaker"),
            tr!("Guide", "Streams PC system audio to your phone over WASAPI loopback."),
        ),
        "recordings" => (
            tr!("Guide", "Voice recorder"),
            tr!("Guide", "Saves phone voice recordings directly to your PC in M4A, MP3, WAV, or FLAC."),
        ),
        "deck" => (
            tr!("Guide", "Deck"),
            tr!("Guide", "Turns your phone into a programmable macro Deck with live volume, mic mute, OBS control, shortcuts, and app launchers."),
        ),
        "remote-input" => (
            tr!("Guide", "Remote input"),
            tr!("Guide", "Lets your phone act as a wireless trackpad, keyboard, presentation remote, laser pointer, and Xbox 360 gamepad."),
        ),
        "type-on-phone" => (
            tr!("Guide", "Type on phone"),
            tr!("Guide", "Forwards PC keystrokes and pasted text directly to your phone without mirroring the screen."),
        ),
        "phone-controls" => (
            tr!("Guide", "Phone controls"),
            tr!("Guide", "Controls your phone's Do Not Disturb, ringer, flashlight, volume, brightness, Wi-Fi, Bluetooth, and Find My Phone ring."),
        ),
        "shelf" => (
            tr!("Guide", "Drop shelf"),
            tr!("Guide", "Slide-out edge panel (Ctrl+Alt+S) showing latest photo, screenshot, clipboard, and recent files with drag-in and drag-out."),
        ),
        "nectar-island" => (
            tr!("Guide", "Nectar Island"),
            tr!("Guide", "Floating pill at the top of the screen for calls, file transfers, media playback, and notification previews."),
        ),
        "timeline" => (
            tr!("Guide", "Timeline"),
            tr!("Guide", "Unified searchable chronological history of files, clips, links, photos, recordings, and sessions."),
        ),
        "command-palette" => (
            tr!("Guide", "Command palette"),
            tr!("Guide", "Keyboard-first search bar (Ctrl+Alt+Space / Ctrl+K) over actions, contacts, threads, apps, and pages."),
        ),
        "handoff" => (
            tr!("Guide", "Handoff"),
            tr!("Guide", "Sends links, timestamped videos, addresses, and documents to open immediately on your phone."),
        ),
        "wol" => (
            tr!("Guide", "Wake PC"),
            tr!("Guide", "Lets your phone wake this PC over the local network via Wake-on-LAN."),
        ),
        "folder-sync" => (
            tr!("Guide", "Share PC folders"),
            tr!("Guide", "Keeps phone and PC folders synced one-way or two-way, and exposes PC folders to your phone."),
        ),
        "automations" => (
            tr!("Guide", "Automations"),
            tr!("Guide", "Trigger-action rules such as locking the PC when the phone disconnects or pausing media during a call."),
        ),
        "localsend" => (
            tr!("Guide", "LocalSend"),
            tr!("Guide", "Discovers and exchanges files with nearby LocalSend v2 devices without pairing."),
        ),
        "local-api" => (
            tr!("Guide", "Local HTTP API"),
            tr!("Guide", "Opt-in bearer-authenticated localhost HTTP/JSON API (127.0.0.1:53320) for scripts, PowerToys, and Stream Deck."),
        ),
        "doctor" => (
            tr!("Guide", "Connection Doctor"),
            tr!("Guide", "Checks Windows Firewall, network profile, discovery sockets, and phone reachability with one-click fixes."),
        ),
        _ => (
            tr!("Guide", "Features guide"),
            tr!("Guide", "Select any feature card in the Features guide to inspect its live status or jump to its control."),
        ),
    };
    (title, body)
}

pub(crate) fn selected_device(current_device_idx: i32) -> Option<DeviceId> {
    let idx = current_device_idx.max(0) as usize;
    core_host::host().hub.read(|s| s.devices.get(idx).map(|d| d.id))
}

fn resolve_shelf_device(current_device_idx: i32) -> (Option<DeviceId>, String, bool) {
    core_host::host().hub.read(|s| {
        let idx = current_device_idx.max(0) as usize;
        let chosen = s
            .devices
            .get(idx)
            .filter(|d| matches!(d.link, LinkState::Online { .. }))
            .or_else(|| s.devices.iter().find(|d| matches!(d.link, LinkState::Online { .. })))
            .or_else(|| s.devices.get(idx))
            .or_else(|| s.devices.first());
        match chosen {
            Some(d) => (Some(d.id), d.info.name.clone(), matches!(d.link, LinkState::Online { .. })),
            None => (None, String::new(), false),
        }
    })
}

fn unix_ms(ts: i64) -> i64 {
    if ts < 10_000_000_000 { ts.saturating_mul(1000) } else { ts }
}

pub fn compute_shelf_items_json(current_device_idx: i32) -> String {
    use nectarlink_core::{ClipboardItemKind, Direction, TimelineKind, TimelineQuery, TransferState};

    let (device_opt, device_name, online) = resolve_shelf_device(current_device_idx);
    let node = core_host::node();

    let mut latest_photo: Option<serde_json::Value> = None;
    let mut latest_photo_ts: i64 = -1;
    let mut latest_screenshot: Option<serde_json::Value> = None;
    let mut latest_screenshot_ts: i64 = -1;

    if let Some(dev) = device_opt {
        let summary = home_store(|s| s.devices.get(&dev).cloned());
        match summary {
            Some(d) if d.photo_loaded => {
                if !d.photo_id.is_empty() {
                    let thumb_p = crate::photos::thumb_path(dev, &d.photo_id);
                    let thumb_url = if !d.photo_thumb.is_empty() {
                        d.photo_thumb.clone()
                    } else if thumb_p.exists() {
                        crate::icons::file_url(&thumb_p)
                    } else {
                        String::new()
                    };
                    let obj = json!({
                        "id": d.photo_id,
                        "name": d.photo_name,
                        "date": d.photo_date,
                        "thumbUrl": thumb_url,
                        "localPath": "",
                    });
                    if d.photo_is_screenshot {
                        latest_screenshot = Some(obj);
                        latest_screenshot_ts = unix_ms(d.photo_date);
                    } else {
                        latest_photo = Some(obj);
                        latest_photo_ts = unix_ms(d.photo_date);
                    }
                }
            }
            _ if online => refresh_shelf_photo_if_needed(dev, false),
            _ => {}
        }
    }

    if let Some(ref n) = node
        && let Ok(page) = n.timeline_page(&TimelineQuery {
            kind: Some(TimelineKind::Photo),
            device: device_opt,
            limit: 25,
            ..Default::default()
        })
    {
        for entry in page.entries {
            let first_target = entry.target.lines().next().unwrap_or("").trim();
            if first_target.is_empty() {
                continue;
            }
            let path = PathBuf::from(first_target);
            if !path.exists() {
                continue;
            }
            let name = path.file_name().and_then(|f| f.to_str()).unwrap_or(&entry.title).to_owned();
            let lower_title = entry.title.to_ascii_lowercase();
            let lower_name = name.to_ascii_lowercase();
            let is_shot = lower_title.contains("screenshot")
                || lower_name.contains("screenshot")
                || entry.detail.to_ascii_lowercase().contains("screenshot");
            let obj = json!({
                "id": path.to_string_lossy(),
                "name": name,
                "date": entry.timestamp,
                "thumbUrl": crate::icons::file_url(&path),
                "localPath": path.to_string_lossy(),
            });
            let ts = unix_ms(entry.timestamp);
            if is_shot {
                if ts >= latest_screenshot_ts {
                    latest_screenshot_ts = ts;
                    latest_screenshot = Some(obj);
                }
            } else if ts >= latest_photo_ts {
                latest_photo_ts = ts;
                latest_photo = Some(obj);
            }
        }
    }

    let dl_dir = core_host::downloads_dir();
    let mut dl_files: Vec<(i64, u64, PathBuf, String)> = Vec::new();
    if let Ok(entries) = std::fs::read_dir(&dl_dir) {
        for e in entries.flatten() {
            let p = e.path();
            let Ok(meta) = e.metadata() else { continue };
            if !meta.is_file() {
                continue;
            }
            let Some(name) = p.file_name().and_then(|n| n.to_str()).map(str::to_owned) else {
                continue;
            };
            let ts = meta
                .modified()
                .ok()
                .and_then(|m| m.duration_since(std::time::UNIX_EPOCH).ok())
                .map_or(0, |d| d.as_millis() as i64);
            dl_files.push((ts, meta.len(), p, name));
        }
    }
    dl_files.sort_by_key(|f| std::cmp::Reverse(f.0));

    for (ts, _, p, name) in &dl_files {
        let ext = p.extension().and_then(|e| e.to_str()).map(str::to_ascii_lowercase).unwrap_or_default();
        if !matches!(ext.as_str(), "png" | "jpg" | "jpeg" | "webp") {
            continue;
        }
        let is_shot = name.to_ascii_lowercase().contains("screenshot");
        if is_shot && *ts > latest_screenshot_ts {
            latest_screenshot_ts = *ts;
            latest_screenshot = Some(json!({
                "id": p.to_string_lossy(),
                "name": name,
                "date": *ts,
                "thumbUrl": crate::icons::file_url(p),
                "localPath": p.to_string_lossy(),
            }));
        } else if !is_shot && *ts > latest_photo_ts {
            latest_photo_ts = *ts;
            latest_photo = Some(json!({
                "id": p.to_string_lossy(),
                "name": name,
                "date": *ts,
                "thumbUrl": crate::icons::file_url(p),
                "localPath": p.to_string_lossy(),
            }));
        }
    }

    let current_clip = if let Some(entry) =
        node.as_ref().and_then(|n| n.clipboard_history(None).into_iter().next())
    {
        let (kind, image_data_url) = match entry.kind {
            ClipboardItemKind::Text => ("text", String::new()),
            ClipboardItemKind::Image => {
                ("image", crate::clipboard::thumb_for_clip(&entry.id).unwrap_or_default())
            }
        };
        json!({
            "id": entry.id,
            "kind": kind,
            "text": entry.text,
            "imageDataUrl": image_data_url,
            "deviceName": entry.device_name,
            "localPath": "",
        })
    } else {
        match win::clipboard::read() {
            win::clipboard::Clip::Text { text: t, .. } if !t.trim().is_empty() => json!({
                "id": "",
                "kind": "text",
                "text": t,
                "imageDataUrl": "",
                "deviceName": "This PC",
                "localPath": "",
            }),
            _ => serde_json::Value::Null,
        }
    };

    let mut recent_files: Vec<serde_json::Value> = Vec::new();
    let mut seen_paths: std::collections::HashSet<String> = std::collections::HashSet::new();
    let mut add_file = |recent_files: &mut Vec<serde_json::Value>, path: &std::path::Path, dev_label: &str| {
        if recent_files.len() >= 5 || !path.exists() || !path.is_file() {
            return;
        }
        let path_str = path.to_string_lossy().into_owned();
        let key = path_str.to_ascii_lowercase();
        if !seen_paths.insert(key) {
            return;
        }
        let name = path.file_name().and_then(|n| n.to_str()).unwrap_or("File").to_owned();
        let size = std::fs::metadata(path).map_or(0, |m| m.len());
        recent_files.push(json!({
            "name": name,
            "path": path_str,
            "size": size,
            "deviceName": dev_label,
        }));
    };

    let transfer_files: Vec<(PathBuf, String)> = core_host::host().hub.read(|s| {
        let mut out = Vec::new();
        for tv in &s.transfers {
            if tv.transfer.direction == Direction::Incoming
                && let TransferState::Done { ref saved } = tv.transfer.state
            {
                let dname = s.name_of(&tv.transfer.device).unwrap_or_else(|| "Phone".into());
                for p in saved {
                    out.push((p.clone(), dname.clone()));
                }
            }
        }
        out
    });
    for (p, dname) in &transfer_files {
        add_file(&mut recent_files, p, dname);
    }

    if recent_files.len() < 5
        && let Some(ref n) = node
        && let Ok(page) = n.timeline_page(&TimelineQuery {
            kind: Some(TimelineKind::File),
            limit: 20,
            ..Default::default()
        })
    {
        for entry in page.entries {
            for line in entry.target.lines() {
                let trimmed = line.trim();
                if !trimmed.is_empty() {
                    add_file(&mut recent_files, std::path::Path::new(trimmed), &entry.device_name);
                }
            }
        }
    }

    for (_, _, p, _) in &dl_files {
        if recent_files.len() >= 5 {
            break;
        }
        add_file(&mut recent_files, p, if device_name.is_empty() { "Phone" } else { &device_name });
    }

    json!({
        "hasDevice": device_opt.is_some(),
        "deviceId": device_opt.map(|d| d.to_string()).unwrap_or_default(),
        "deviceName": device_name,
        "online": online,
        "latestPhoto": latest_photo.unwrap_or(serde_json::Value::Null),
        "latestScreenshot": latest_screenshot.unwrap_or(serde_json::Value::Null),
        "currentClip": current_clip,
        "recentFiles": recent_files,
    })
    .to_string()
}

pub fn feature_state_json(device: Option<DeviceId>, feature: &str) -> serde_json::Value {
    let state = device.and_then(|id| {
        core_host::host().hub.read(|s| s.matrices.get(&id).and_then(|m| m.state(feature)))
    });
    let mut map = serde_json::Map::new();
    let put_upgrade = |map: &mut serde_json::Map<String, serde_json::Value>, upgrade: &Upgrade| {
        let (action, target) = upgrade.action.describe();
        map.insert("action".into(), json!(action));
        map.insert("target".into(), json!(target));
        map.insert("minutes".into(), json!(upgrade.effort.minutes()));
    };
    match state {
        None => {
            map.insert("state".into(), json!("unknown"));
        }
        Some(FeatureState::Available) => {
            map.insert("state".into(), json!("available"));
        }
        Some(FeatureState::Partial { limit, upgrade }) => {
            map.insert("state".into(), json!("partial"));
            map.insert("limit".into(), json!(limit));
            if let Some(upgrade) = upgrade {
                put_upgrade(&mut map, &upgrade);
            }
        }
        Some(FeatureState::Locked { upgrade }) => {
            map.insert("state".into(), json!("locked"));
            put_upgrade(&mut map, &upgrade);
        }
        Some(FeatureState::Unsupported { reason }) => {
            map.insert("state".into(), json!("unsupported"));
            map.insert("reason".into(), json!(reason.describe()));
        }
    }
    serde_json::Value::Object(map)
}

pub fn device_toggles_json(device: Option<DeviceId>) -> serde_json::Value {
    let (Some(id), Some(node)) = (device, core_host::node()) else { return json!([]) };
    let unlock_supported = node.peer_offers(id, nectarlink_core::unlock::KEY);
    let list: Vec<serde_json::Value> = node
        .device_toggles(id)
        .unwrap_or_default()
        .into_iter()
        .filter(|(name, _)| *name != "clipboard_receive" && (*name != nectarlink_core::unlock::TOGGLE || unlock_supported))
        .map(|(name, on)| json!({ "name": name, "on": on }))
        .collect();
    serde_json::Value::Array(list)
}

// ---- Event-driven Home card summary ----

#[derive(Debug, Clone, Default, PartialEq, Eq)]
struct HomeDeviceSummary {
    sms_loaded: bool,
    sms_busy: bool,
    unread_messages: u32,
    unread_sender: String,
    calls_loaded: bool,
    calls_busy: bool,
    missed_calls: u32,
    missed_caller: String,
    photo_loaded: bool,
    photo_busy: bool,
    photo_id: String,
    photo_name: String,
    photo_date: i64,
    photo_thumb: String,
    photo_is_video: bool,
    photo_is_screenshot: bool,
}

#[derive(Debug, Default)]
struct HomeSummaryStore {
    watched: Option<DeviceId>,
    devices: HashMap<DeviceId, HomeDeviceSummary>,
}

static HOME_SUMMARY: Mutex<Option<HomeSummaryStore>> = Mutex::new(None);

fn home_store<T>(f: impl FnOnce(&mut HomeSummaryStore) -> T) -> T {
    f(HOME_SUMMARY.lock().unwrap_or_else(|e| e.into_inner()).get_or_insert_default())
}

fn bump_home_summary() {
    update_ui(Changes::STATUS, |s| {
        s.home_summary_revision = s.home_summary_revision.wrapping_add(1);
        if s.shelf_open {
            s.shelf_items = compute_shelf_items_json(s.current_device);
        }
    });
}

pub fn release_home_summary() {
    home_store(|s| {
        s.watched = None;
        s.devices.clear();
    });
}

pub fn home_summary_json(device: DeviceId) -> String {
    home_store(|s| {
        let Some(d) = s.devices.get(&device) else { return String::new() };
        json!({
            "smsReady": d.sms_loaded,
            "unreadMessages": d.unread_messages,
            "unreadSender": d.unread_sender,
            "callsReady": d.calls_loaded,
            "missedCalls": d.missed_calls,
            "missedCaller": d.missed_caller,
            "photoReady": d.photo_loaded,
            "photoId": d.photo_id,
            "photoName": d.photo_name,
            "photoDate": d.photo_date,
            "photoThumb": d.photo_thumb,
            "photoIsVideo": d.photo_is_video,
            "photoIsScreenshot": d.photo_is_screenshot,
        })
        .to_string()
    })
}

pub fn watch_home(device: Option<DeviceId>) {
    crate::command_palette::set_preferred_device(device);
    home_store(|s| s.watched = device);
    if let Some(id) = device {
        refresh_home_if_needed(id, false, false, false);
    }
}

pub(crate) fn update_home_sms(device: DeviceId, threads: &[nectarlink_core::SmsThread]) {
    let changed = home_store(|s| {
        let entry = s.devices.entry(device).or_default();
        let unread_messages: u32 = threads.iter().map(|t| t.unread).sum();
        let unread_sender =
            threads.iter().find(|t| t.unread > 0).map(crate::messages::title_of).unwrap_or_default();
        let prev = (entry.sms_loaded, entry.unread_messages, entry.unread_sender.clone());
        entry.sms_loaded = true;
        entry.sms_busy = false;
        entry.unread_messages = unread_messages;
        entry.unread_sender = unread_sender;
        prev != (entry.sms_loaded, entry.unread_messages, entry.unread_sender.clone())
    });
    if changed {
        bump_home_summary();
    }
}

pub(crate) fn update_home_calls(device: DeviceId, entries: &[nectarlink_core::CallLogEntry]) {
    let changed = home_store(|s| {
        let entry = s.devices.entry(device).or_default();
        let missed_calls = entries.iter().filter(|e| e.direction == "missed").count() as u32;
        let missed_caller = entries
            .iter()
            .find(|e| e.direction == "missed")
            .map(|e| e.name.clone().filter(|n| !n.trim().is_empty()).unwrap_or_else(|| e.number.clone()))
            .unwrap_or_default();
        let prev = (entry.calls_loaded, entry.missed_calls, entry.missed_caller.clone());
        entry.calls_loaded = true;
        entry.calls_busy = false;
        entry.missed_calls = missed_calls;
        entry.missed_caller = missed_caller;
        prev != (entry.calls_loaded, entry.missed_calls, entry.missed_caller.clone())
    });
    if changed {
        bump_home_summary();
    }
}

pub(crate) fn on_home_event(event: &nectarlink_core::NodeEvent) {
    use nectarlink_core::NodeEvent;
    let watched = home_store(|s| s.watched);
    let shelf_active = is_shelf_open();
    match event {
        NodeEvent::LinkChanged { device, link: LinkState::Online { .. } } if watched == Some(*device) => {
            refresh_home_if_needed(*device, true, true, true);
        }
        NodeEvent::LinkChanged { device, link: LinkState::Offline { .. } } => {
            home_store(|s| {
                if let Some(d) = s.devices.get_mut(device) {
                    d.sms_busy = false;
                    d.calls_busy = false;
                    d.photo_busy = false;
                }
            });
        }
        NodeEvent::Capabilities(matrix) if watched == Some(matrix.device) => {
            refresh_home_if_needed(matrix.device, false, false, false);
        }
        NodeEvent::SmsChanged { device, .. } if watched == Some(*device) => {
            refresh_home_if_needed(*device, true, false, false);
        }
        NodeEvent::CallLogChanged { device } if watched == Some(*device) => {
            refresh_home_if_needed(*device, false, true, false);
        }
        NodeEvent::Call { device, call } if watched == Some(*device) && call.state == "ended" => {
            let dev = *device;
            core_host::spawn(async move {
                tokio::time::sleep(std::time::Duration::from_millis(650)).await;
                refresh_home_if_needed(dev, false, true, false);
            });
        }
        NodeEvent::PhotoAdded { device, photo } if watched == Some(*device) || shelf_active => {
            let thumb_url = crate::photos::write_thumb(*device, &photo.id, &photo.thumb)
                .or_else(|| {
                    let p = crate::photos::thumb_path(*device, &photo.id);
                    p.exists().then_some(p)
                })
                .map(|p| crate::icons::file_url(&p))
                .unwrap_or_default();
            home_store(|s| {
                let entry = s.devices.entry(*device).or_default();
                entry.photo_loaded = true;
                entry.photo_busy = false;
                entry.photo_id.clone_from(&photo.id);
                entry.photo_name.clone_from(&photo.name);
                entry.photo_date = photo.taken;
                entry.photo_thumb = thumb_url;
                entry.photo_is_video = photo.id.starts_with("video:");
                entry.photo_is_screenshot = photo.screenshot;
            });
            bump_home_summary();
        }
        NodeEvent::PhotosChanged { device } if watched == Some(*device) || shelf_active => {
            if watched == Some(*device) {
                refresh_home_if_needed(*device, false, false, true);
            } else {
                refresh_shelf_photo_if_needed(*device, true);
            }
        }
        NodeEvent::DeviceRemoved(device) => {
            home_store(|s| {
                s.devices.remove(device);
                if s.watched == Some(*device) {
                    s.watched = None;
                }
            });
        }
        _ => {}
    }
}

fn refresh_shelf_photo_if_needed(device: DeviceId, force_photo: bool) {
    let (online, photos_avail) = core_host::host().hub.read(|s| {
        let online = s.devices.iter().any(|d| d.id == device && matches!(d.link, LinkState::Online { .. }));
        let avail = s.matrices.get(&device).and_then(|m| m.state("files.recent_photos"))
            == Some(FeatureState::Available);
        (online, avail)
    });
    if !online || !photos_avail {
        return;
    }
    let do_photo = home_store(|s| {
        let d = s.devices.entry(device).or_default();
        let should = !d.photo_busy && (force_photo || !d.photo_loaded);
        if should {
            d.photo_busy = true;
        }
        should
    });
    if do_photo {
        spawn_home_photo_fetch(device);
    }
}

fn spawn_home_photo_fetch(device: DeviceId) {
    let Some(node) = core_host::node() else {
        home_store(|s| {
            if let Some(d) = s.devices.get_mut(&device) {
                d.photo_busy = false;
            }
        });
        return;
    };
    core_host::spawn(async move {
        let Ok(items) = node.photo_list(device, None, None, 1).await else {
            home_store(|s| {
                if let Some(d) = s.devices.get_mut(&device) {
                    d.photo_busy = false;
                }
            });
            return;
        };
        let Some(item) = items.into_iter().next() else {
            home_store(|s| {
                let d = s.devices.entry(device).or_default();
                d.photo_loaded = true;
                d.photo_busy = false;
                d.photo_id.clear();
                d.photo_name.clear();
                d.photo_date = 0;
                d.photo_thumb.clear();
            });
            bump_home_summary();
            return;
        };
        let mut disk_path = crate::photos::thumb_path(device, &item.id);
        if !disk_path.exists()
            && let Ok(thumbs) = node.photo_thumbs(device, vec![item.id.clone()]).await
            && let Some(first) = thumbs.into_iter().next()
            && let Some(saved) = crate::photos::write_thumb(device, &first.id, &first.data)
        {
            disk_path = saved;
        }
        let thumb_url = if disk_path.exists() { crate::icons::file_url(&disk_path) } else { String::new() };
        let lower_name = item.name.to_ascii_lowercase();
        let is_screenshot = lower_name.contains("screenshot")
            || item.album.as_deref().is_some_and(|a| a.to_ascii_lowercase().contains("screenshot"));
        let is_video = item.duration.is_some() || item.id.starts_with("video:");
        home_store(|s| {
            let d = s.devices.entry(device).or_default();
            d.photo_loaded = true;
            d.photo_busy = false;
            d.photo_id = item.id;
            d.photo_name = item.name;
            d.photo_date = item.date;
            d.photo_thumb = thumb_url;
            d.photo_is_video = is_video;
            d.photo_is_screenshot = is_screenshot;
        });
        bump_home_summary();
    });
}

fn refresh_home_if_needed(device: DeviceId, force_sms: bool, force_calls: bool, force_photo: bool) {
    let (online, sms_avail, calls_avail, photos_avail) = core_host::host().hub.read(|s| {
        let online = s.devices.iter().any(|d| d.id == device && matches!(d.link, LinkState::Online { .. }));
        let m = s.matrices.get(&device);
        let avail = |f: &str| m.and_then(|m| m.state(f)) == Some(FeatureState::Available);
        (online, avail("messages.sms"), avail("calls.log"), avail("files.recent_photos"))
    });
    let Some(node) = core_host::node().filter(|_| online) else { return };
    let (do_sms, do_calls, do_photo) = home_store(|s| {
        if s.watched != Some(device) {
            return (false, false, false);
        }
        let d = s.devices.entry(device).or_default();
        let do_sms = sms_avail && !d.sms_busy && (force_sms || !d.sms_loaded);
        let do_calls = calls_avail && !d.calls_busy && (force_calls || !d.calls_loaded);
        let do_photo = photos_avail && !d.photo_busy && (force_photo || !d.photo_loaded);
        if do_sms {
            d.sms_busy = true;
        }
        if do_calls {
            d.calls_busy = true;
        }
        if do_photo {
            d.photo_busy = true;
        }
        (do_sms, do_calls, do_photo)
    });
    if do_sms {
        let node = node.clone();
        core_host::spawn(async move {
            match node.sms_threads(device, 40).await {
                Ok(threads) => update_home_sms(device, &threads),
                Err(_) => home_store(|s| {
                    if let Some(d) = s.devices.get_mut(&device) {
                        d.sms_busy = false;
                    }
                }),
            }
        });
    }
    if do_calls {
        let node = node.clone();
        core_host::spawn(async move {
            match node.call_log(device, None, 25).await {
                Ok(entries) => update_home_calls(device, &entries),
                Err(_) => home_store(|s| {
                    if let Some(d) = s.devices.get_mut(&device) {
                        d.calls_busy = false;
                    }
                }),
            }
        });
    }
    if do_photo {
        spawn_home_photo_fetch(device);
    }
}
