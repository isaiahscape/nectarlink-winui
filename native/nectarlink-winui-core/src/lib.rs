// SPDX-License-Identifier: GPL-3.0-or-later
//! Native Rust core + Windows OS integration DLL for the WinUI 3 NectarLink desktop app.
//!
//! Exports the C ABI consumed by `NectarLink.WinUI.exe` (`nl_*`), handles all
//! CLI / elevated / widget / single-instance startup modes, runs the `nectarlink-core`
//! QUIC node and OS integration services, and pushes coalesced state updates &
//! 60fps BGRX video frames up to WinUI 3.

#![allow(unsafe_code)]
#![recursion_limit = "512"]

mod agents;
mod app_shortcuts;
mod automations;
mod battery;
mod bridge;
mod call_audio;
mod calls;
mod clipboard;
mod command_palette;
mod continuity_camera;
mod core_host;
mod crash_events;
mod deck;
mod doctor;
mod feed;
mod folder_sync;
mod gamepad;
mod hotspot;
mod i18n;
mod icons;
mod input_share;
mod launch;
mod links;
mod local_api;
mod logging;
mod mark;
mod media;
mod media_file;
mod messages;
mod mirror;
mod notification_store;
mod notifications;
mod obs;
mod palette;
mod pc_audio_stream;
mod pc_mirror;
#[cfg_attr(not(target_os = "linux"), allow(dead_code))]
mod pc_notifications;
mod pc_storage;
mod phone_install;
mod phone_mic;
mod phone_sound;
mod phone_wallpaper;
mod photo_backup;
mod photos;
mod play_on_phone;
#[cfg_attr(not(windows), allow(dead_code))]
mod pointer_flow;
mod qr;
mod recordings;
mod remote;
mod report_requests;
mod send_to;
mod settings;
#[allow(dead_code)]
mod setup;
mod startup;
mod state;
mod storage;
mod transfers;
mod update_relay;
mod updater;
mod version;
mod webcam;
mod win;

use std::{
    ffi::{CStr, CString, c_char, c_int},
    path::{Path, PathBuf},
    sync::{
        Arc, Mutex, OnceLock,
        atomic::{AtomicBool, Ordering},
    },
    time::Duration,
};

use nectarlink_core::{
    MediaAction, MediaError, MirrorInput, MirrorStart, Platform, PowerAction, StorageEntry, StorageError,
    StorageReadFile, StorageWriteDone,
};

use crate::{
    bridge::{EventCallback, VideoCallback, app::request_activation, winui},
    launch::Request,
    win::single_instance::{self, Instance},
};

#[derive(Debug)]
struct DesktopPlatform;

impl Platform for DesktopPlatform {
    fn diag_facts(&self) -> Option<(nectarlink_core::diag::report::DeviceFacts, Vec<(String, String)>)> {
        Some(bridge::report::own_facts())
    }
    fn start_ringing(&self) {
        win::sound::start_ringing();
    }
    fn stop_ringing(&self) {
        win::sound::stop_ringing();
    }
    fn set_clipboard(&self, text: &str) -> Result<(), String> {
        win::clipboard::write(text)
    }
    fn set_clipboard_rich(&self, text: &str, html: Option<&str>, rtf: Option<&str>) -> Result<(), String> {
        win::clipboard::write_rich(text, html, rtf)
    }
    fn set_clipboard_private(&self, text: &str) -> Result<(), String> {
        win::clipboard::write_private(text)
    }
    fn clear_clipboard_private(&self, text: &str) {
        win::clipboard::clear_private(text);
    }
    fn set_clipboard_image(&self, mime: &str, bytes: &[u8]) -> Result<(), String> {
        win::clipboard::write_received_image(mime, bytes)
    }
    fn power(&self, action: PowerAction) -> Result<(), String> {
        links::power(action)
    }
    fn open_link(&self, _from: &nectarlink_core::DeviceId, url: &str) -> Result<(), String> {
        links::open_here(url)
    }
    fn media_command(
        &self,
        player: &str,
        action: MediaAction,
        position: Option<u64>,
    ) -> Result<(), MediaError> {
        win::media_sessions::command(player, action, position)
    }
    fn mirror_requested(&self, peer: &nectarlink_core::DeviceId, options: &MirrorStart) -> Result<(), String> {
        if options.session != nectarlink_core::MIRROR_SCREEN {
            return Err("unsupported mirror session on PC".into());
        }
        pc_mirror::start(*peer, options.clone());
        Ok(())
    }
    fn mirror_displays(&self) -> Vec<nectarlink_core::MirrorDisplay> {
        win::displays::list()
            .into_iter()
            .map(|d| nectarlink_core::MirrorDisplay {
                id: d.id,
                width: d.width(),
                height: d.height(),
                primary: d.primary,
            })
            .collect()
    }
    fn mirror_stop_requested(&self, peer: &nectarlink_core::DeviceId, session: u32) {
        if session == nectarlink_core::MIRROR_SCREEN {
            pc_mirror::stop_for(peer);
        }
    }
    fn mirror_keyframe_requested(&self, peer: &nectarlink_core::DeviceId, session: u32) {
        if session == nectarlink_core::MIRROR_SCREEN {
            pc_mirror::request_keyframe(peer);
        }
    }
    fn mirror_input(&self, peer: &nectarlink_core::DeviceId, session: u32, input: MirrorInput) {
        pc_mirror::handle_input(peer, session, input);
    }
    fn mirror_sink(
        &self,
        peer: &nectarlink_core::DeviceId,
        session: u32,
    ) -> Option<std::sync::Arc<dyn nectarlink_core::MirrorSink>> {
        mirror::sink(peer, session)
    }
    fn mic_sink(&self, peer: &nectarlink_core::DeviceId) -> Option<std::sync::Arc<dyn nectarlink_core::MicSink>> {
        phone_mic::sink(peer)
    }
    fn call_audio_sink(
        &self,
        peer: &nectarlink_core::DeviceId,
    ) -> Option<std::sync::Arc<dyn nectarlink_core::MicSink>> {
        call_audio::sink(peer)
    }
    fn call_audio_stop_requested(&self, peer: &nectarlink_core::DeviceId) {
        call_audio::end(*peer);
    }
    fn phone_sound_asked(&self, peer: &nectarlink_core::DeviceId) {
        tracing::info!(peer = %peer.short(), "the phone asked to play its sound here");
        phone_sound::set_on(*peer, true);
    }
    fn webcam_sink(&self, peer: &nectarlink_core::DeviceId) -> Option<std::sync::Arc<dyn nectarlink_core::WebcamSink>> {
        webcam::sink(peer)
    }
    fn remote_input(&self, peer: &nectarlink_core::DeviceId, input: nectarlink_core::RemoteInput) -> Result<(), String> {
        remote::handle_input(peer, input);
        Ok(())
    }
    fn remote_pad(
        &self,
        peer: &nectarlink_core::DeviceId,
        pad: Option<nectarlink_core::PadState>,
        wait: bool,
    ) -> Result<(), String> {
        match pad {
            Some(pad) => gamepad::set(*peer, pad, wait),
            None => {
                gamepad::off(*peer);
                Ok(())
            }
        }
    }
    fn deck_press(&self, peer: &nectarlink_core::DeviceId, tile: &str) -> Result<(), String> {
        deck::handle_press(peer, tile)
    }
    fn set_pc_audio(&self, peer: &nectarlink_core::DeviceId, volume: Option<u8>, muted: Option<bool>) -> Result<(), String> {
        deck::handle_set_audio(peer, volume, muted)
    }
    fn set_pc_audio_output(&self, _peer: &nectarlink_core::DeviceId, id: &str) -> Result<(), String> {
        deck::handle_set_output(id)
    }
    fn storage_list(&self, path: &str) -> Result<Vec<StorageEntry>, StorageError> {
        pc_storage::storage_list(path)
    }
    fn storage_open_read(&self, path: &str) -> Result<StorageReadFile, StorageError> {
        pc_storage::storage_open_read(path)
    }
    fn storage_write(&self, path: &str, staged: &Path, modified: Option<i64>) -> Result<StorageWriteDone, StorageError> {
        pc_storage::storage_write(path, staged, modified)
    }
    fn storage_mkdir(&self, path: &str) -> Result<(), StorageError> {
        pc_storage::storage_mkdir(path)
    }
    fn storage_rename(&self, path: &str, new_name: &str) -> Result<(), StorageError> {
        pc_storage::storage_rename(path, new_name)
    }
    fn storage_delete(&self, path: &str, confirmed: bool) -> Result<(), StorageError> {
        pc_storage::storage_delete(path, confirmed)
    }
}

#[derive(Debug, Default)]
struct Options {
    data_dir: Option<PathBuf>,
    minimized: bool,
    updated: bool,
    send_to: Option<(String, Vec<PathBuf>)>,
    share: Option<win::share_target::SharedPayload>,
    task: Option<String>,
    tel: Option<Result<String, ()>>,
    quit: bool,
    uninstall: bool,
    register_vcam: bool,
    unregister_vcam: bool,
    register_unlock: bool,
    unregister_unlock: bool,
    register_package: bool,
    widget_provider: bool,
    update_service: bool,
    update_elevated: Option<String>,
    setup: Option<PathBuf>,
    remove: bool,
    installed: Option<PathBuf>,
    ui_check: bool,
    version: bool,
}

fn parse_options_from<I: IntoIterator<Item = std::ffi::OsString>>(iter: I) -> Options {
    let mut options = Options::default();
    let mut paths = Vec::new();
    let mut share_targets = Vec::new();
    let mut in_share = false;
    let mut args = iter.into_iter();
    while let Some(arg) = args.next() {
        match arg.to_str() {
            Some("--data-dir") => {
                in_share = false;
                options.data_dir = args.next().map(PathBuf::from);
            }
            Some("--tel") => {
                let number = args.next().and_then(|uri| launch::telephone_number(uri.to_str()?));
                let tel = number.filter(|_| args.next().is_none()).ok_or(());
                return Options { tel: Some(tel), data_dir: options.data_dir, ..Options::default() };
            }
            Some("--minimized") => { in_share = false; options.minimized = true; }
            Some("--quit") => { in_share = false; options.quit = true; }
            Some("--uninstall") => { in_share = false; options.uninstall = true; }
            Some("--ui-check") => { in_share = false; options.ui_check = true; }
            Some("--version" | "-V") => { in_share = false; options.version = true; }
            Some(setup::SETUP_ARG) => { in_share = false; options.setup = Some(PathBuf::new()); }
            Some(setup::INSTALLER_ARG) => { in_share = false; options.setup = args.next().map(PathBuf::from); }
            Some(setup::REMOVE_ARG) => { in_share = false; options.remove = true; }
            Some(setup::INSTALLED_ARG) => { in_share = false; options.installed = args.next().map(PathBuf::from); }
            Some(updater::SERVICE_ARG) => { in_share = false; options.update_service = true; }
            Some(updater::ELEVATED_ARG) => {
                in_share = false;
                options.update_elevated = Some(args.next().and_then(|a| a.into_string().ok()).unwrap_or_default());
            }
            Some(updater::UPDATED_ARG) => { in_share = false; options.updated = true; }
            Some(win::vcam::REGISTER_ARG) => { in_share = false; options.register_vcam = true; }
            Some(win::vcam::UNREGISTER_ARG) => { in_share = false; options.unregister_vcam = true; }
            Some(win::unlock::REGISTER_ARG) => { in_share = false; options.register_unlock = true; }
            Some(win::unlock::UNREGISTER_ARG) => { in_share = false; options.unregister_unlock = true; }
            Some("--register-package") => { in_share = false; options.register_package = true; }
            Some(win::widget::ARG) => { in_share = false; options.widget_provider = true; }
            Some(send_to::ARG) => {
                in_share = false;
                let device = args.next().map(|d| d.to_string_lossy().into_owned()).unwrap_or_default();
                options.send_to = Some((device, Vec::new()));
            }
            Some(win::jumplist::ARG) => {
                in_share = false;
                options.task = args.next().map(|a| a.to_string_lossy().into_owned()).filter(|s| !s.is_empty());
            }
            Some(win::share_target::ARG) => { in_share = true; }
            _ if in_share && !arg.to_string_lossy().starts_with('-') => {
                share_targets.push(arg.to_string_lossy().into_owned());
            }
            _ if !arg.to_string_lossy().starts_with('-') => paths.push(PathBuf::from(arg)),
            _ => { in_share = false; }
        }
    }
    if let Some((_, files)) = &mut options.send_to {
        *files = paths;
        options.minimized = true;
    } else if share_targets.is_empty() && !paths.is_empty() {
        share_targets = paths.into_iter().map(|p| p.to_string_lossy().into_owned()).collect();
    }
    if matches!(options.task.as_deref(), Some("send-clipboard" | "find-phone" | "take-photo")) {
        options.minimized = true;
    }
    if !share_targets.is_empty() {
        let payload = win::share_target::parse_targets(&share_targets);
        if !payload.is_empty() {
            options.share = Some(payload);
            options.minimized = true;
        }
    }
    options
}

fn default_data_dir() -> PathBuf {
    dirs::data_local_dir().unwrap_or_else(std::env::temp_dir).join("Nectarlink")
}

fn watch_network() {
    static PENDING: AtomicBool = AtomicBool::new(false);
    win::net::watch(|| {
        if PENDING.swap(true, Ordering::AcqRel) {
            return;
        }
        core_host::spawn(async {
            tokio::time::sleep(Duration::from_secs(1)).await;
            PENDING.store(false, Ordering::Release);
            core_host::refresh_wake().await;
            if let Some(node) = core_host::node() {
                tracing::debug!("network changed");
                node.network_changed().await;
            }
        });
    });
}

fn start_toasts() {
    let icon = core_host::host().data_dir.join("cache").join("nectarlink.png");
    win::toast::start(icon, notifications::on_toast);
}

fn uninstall() {
    tracing::info!("uninstalling");
    if let Err(e) = send_to::remove_all() {
        tracing::warn!(error = %e, "can't remove the Send to entries");
    }
    storage::unregister_all();
    let _ = win::vcam::unregister_hklm();
    win::toast::unregister();
    win::jumplist::unregister();
    startup::remove();
}

fn note_previous_run(diag_dir: &Path) {
    use nectarlink_core::diag::crash;
    use nectarlink_core::diag::health::{self, Code, Feature};
    let version = env!("CARGO_PKG_VERSION");
    health::init(diag_dir);
    let Some(previous) = crash::begin_run(diag_dir, version) else { return };
    if previous.clean {
        return;
    }
    if let Some(record) = crash::crashed(&previous, &crash::records(diag_dir)) {
        tracing::warn!(sig = %record.sig, kind = %record.kind, version = %previous.version, "the last run crashed");
        health::count(Feature::Crash, Code::Ok);
        return;
    }
    std::thread::Builder::new()
        .name("crash-events".into())
        .spawn(move || match crash_events::native_crash_since(previous.started, &previous.version) {
            Some(record) => {
                tracing::warn!(sig = %record.sig, kind = %record.kind, version = %previous.version, "the last run crashed");
                crash::record(&record);
                health::count(Feature::Crash, Code::Ok);
            }
            None => {
                tracing::info!(version = %previous.version, "the last run ended without quitting");
            }
        })
        .ok();
}

struct RuntimeHolder {
    data_dir: PathBuf,
    minimized: bool,
    ui_check: bool,
    initial_request: Request,
}

static HOLDER: OnceLock<Mutex<Option<RuntimeHolder>>> = OnceLock::new();
static PRIMARY_INSTANCE: OnceLock<single_instance::PrimaryInstance> = OnceLock::new();

fn c_str(ptr: *const c_char) -> Option<String> {
    if ptr.is_null() {
        return None;
    }
    // SAFETY: Caller passes a valid null-terminated UTF-8 C string.
    Some(unsafe { CStr::from_ptr(ptr) }.to_string_lossy().into_owned())
}

fn alloc_cstring(s: &str) -> *mut c_char {
    CString::new(s).map_or(std::ptr::null_mut(), CString::into_raw)
}

/// Initializes CLI/elevated helpers OR starts the full NectarLink core runtime.
///
/// Returns:
/// - `0`: Full core initialized; WinUI should open (or stay minimized to tray if `nl_should_start_minimized() != 0`).
/// - `1`: Single-action CLI / elevated / secondary-instance completed cleanly -> exit 0.
/// - `2`: Fatal initialization failure -> exit 1.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nl_init(
    argc: c_int,
    argv: *const *const c_char,
    event_cb: EventCallback,
    video_cb: VideoCallback,
    user_data: usize,
) -> c_int {
    let mut os_args = Vec::new();
    if argc > 1 && !argv.is_null() {
        for i in 1..argc as isize {
            // SAFETY: Bounded by `argc` from caller's CLI vector.
            let p = unsafe { *argv.offset(i) };
            if let Some(s) = c_str(p) {
                os_args.push(std::ffi::OsString::from(s));
            }
        }
    } else {
        os_args.extend(std::env::args_os().skip(1));
    }

    let options = parse_options_from(os_args);
    if matches!(options.tel, Some(Err(()))) {
        return 2;
    }
    if options.version {
        println!("nectarlink-desktop {}", env!("CARGO_PKG_VERSION"));
        return 1;
    }
    if options.update_service {
        let code = updater::run_service();
        return if code == std::process::ExitCode::SUCCESS { 1 } else { 2 };
    }
    if let Some(channel) = &options.update_elevated {
        let code = updater::run_elevated(Some(channel.as_str()));
        return if code == std::process::ExitCode::SUCCESS { 1 } else { 2 };
    }
    if options.register_vcam {
        return match win::vcam::register_hklm() {
            Ok(()) => 1,
            Err(e) => {
                eprintln!("nectarlink: register-vcam failed: {e}");
                2
            }
        };
    }
    if options.unregister_vcam {
        return match win::vcam::unregister_hklm() {
            Ok(()) => 1,
            Err(e) => {
                eprintln!("nectarlink: unregister-vcam failed: {e}");
                2
            }
        };
    }
    if options.register_package {
        let code = win::share_target::register_elevated();
        return if code == std::process::ExitCode::SUCCESS { 1 } else { 2 };
    }
    if options.register_unlock || options.unregister_unlock {
        let done = if options.register_unlock {
            win::unlock::register_hklm()
        } else {
            win::unlock::unregister_hklm()
        };
        return match done {
            Ok(()) => 1,
            Err(e) => {
                eprintln!("nectarlink: unlock with phone: {e}");
                2
            }
        };
    }
    if options.ui_check && options.data_dir.is_none() {
        eprintln!("nectarlink: --ui-check needs --data-dir");
        return 2;
    }

    let data_dir = options.data_dir.clone().unwrap_or_else(default_data_dir);
    if let Err(e) = std::fs::create_dir_all(&data_dir) {
        eprintln!("nectarlink: can't create {}: {e}", data_dir.display());
        return 2;
    }
    logging::init(&data_dir);
    nectarlink_core::diag::crash::install_panic_hook();

    if options.ui_check && let Err(e) = nectarlink_core::add_ui_check_phone(&data_dir) {
        eprintln!("nectarlink: --ui-check: can't add the pretend phone: {e}");
        return 2;
    }
    if options.widget_provider {
        let code = win::widget::run_provider(&data_dir);
        return if code == std::process::ExitCode::SUCCESS { 1 } else { 2 };
    }
    tracing::info!(version = env!("CARGO_PKG_VERSION"), minimized = options.minimized, "starting");

    if options.uninstall {
        uninstall();
    }
    let share_payload = options.share.clone().or_else(|| win::share_target::take_activated_share(&data_dir));
    let request = if options.quit || options.uninstall {
        Request::Quit
    } else if let Some((device, paths)) = options.send_to.clone() {
        Request::Send { device, paths }
    } else if let Some(Ok(number)) = options.tel {
        Request::OpenDialer { number }
    } else if let Some(shared) = share_payload {
        Request::Share { files: shared.files, url: shared.url, text: shared.text }
    } else if let Some(action) = options.task.clone() {
        Request::Task { action }
    } else {
        Request::Show
    };

    let instance = match single_instance::acquire(&data_dir) {
        Ok(Instance::Primary(_)) if request == Request::Quit => return 1,
        Ok(Instance::Primary(primary)) => Some(primary),
        Ok(Instance::Secondary) => {
            if let Err(e) = launch::queue(&data_dir, &request) {
                tracing::warn!(error = %e, "can't pass the request on");
            }
            single_instance::wake(&data_dir);
            tracing::info!("already running; passed the request on");
            return 1;
        }
        Err(e) => {
            tracing::warn!(error = %e, "single-instance check failed; continuing");
            None
        }
    };

    note_previous_run(&data_dir.join("diag"));
    win::toast::set_process_id();
    win::jumplist::register(options.data_dir.as_deref());
    phone_install::init(&data_dir);
    let initial_settings = settings::Settings::load(&data_dir);
    settings::set_usb_debugging(initial_settings.usb_debugging);
    i18n::install(&initial_settings.language);

    bridge::set_callbacks(event_cb, video_cb, user_data);

    if let Err(e) = core_host::start(data_dir.clone(), Arc::new(DesktopPlatform)) {
        tracing::error!(error = %e, "can't start the core runtime");
        return 2;
    }

    bridge::start_hub_pump();

    core_host::spawn(async {
        if let Some(node) = core_host::wait_for_node().await {
            win::ble_beacon::start(node.device_id());
        }
    });
    core_host::host().hub.update(|s| {
        notification_store::load(&data_dir, s);
        state::Changes::APPS | state::Changes::HISTORY
    });
    notification_store::start(data_dir.clone());
    std::thread::spawn(notifications::prune_images);
    watch_network();
    start_toasts();
    win::share_target::ensure_sparse_package(data_dir.clone());
    clipboard::start();
    win::smtc::start(media::on_flyout);
    win::media_sessions::start(media::local_changed);
    deck::init(&data_dir);

    initial_settings.apply_runtime();
    clipboard::set_auto_send(initial_settings.auto_clipboard);
    clipboard::set_history_enabled(initial_settings.clipboard_history);
    clipboard::set_suggest_actions(initial_settings.suggest_clipboard_actions);
    notifications::set_auto_copy_otp(initial_settings.auto_copy_otp);
    notifications::set_paused(initial_settings.keep_popups_paused && initial_settings.popups_paused);
    notifications::set_sync_dnd(initial_settings.sync_dnd);
    notifications::set_hold_when_locked(initial_settings.hold_when_locked);
    notifications::start_lock_watch();
    calls::set_pause_media_on_call(initial_settings.pause_media_on_call);
    pc_mirror::set_mute_while_watched(initial_settings.mute_while_watched);
    recordings::init(&initial_settings);
    send_to::set_enabled(initial_settings.send_to_menu);
    battery::set_enabled(initial_settings.battery_alerts);
    storage::start();
    folder_sync::start_poller();
    photo_backup::start();
    webcam::init(&initial_settings);
    phone_mic::init(&initial_settings);
    call_audio::init(&initial_settings);
    local_api::init(&initial_settings);
    agents::init(&initial_settings);
    win::widget::write_state_snapshot(&data_dir);
    win::tray::update_hotkeys(
        &initial_settings.continuity_photo_hotkey,
        &initial_settings.continuity_scan_hotkey,
        &initial_settings.command_palette_hotkey,
        &initial_settings.shelf_hotkey,
    );

    let updated_from = updater::note_running_version(&data_dir);
    nectarlink_core::whats_new::note_start(&data_dir, updated_from.is_some());
    if updated_from.is_some() {
        let mut saved = settings::Settings::load(&data_dir);
        let now = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map_or(0, |d| d.as_secs());
        let star = saved.ask_for_star_after_update();
        let join = saved.ask_to_join_after_update(now);
        if (star || join) && let Err(e) = saved.save(&data_dir) {
            tracing::warn!(error = %e, "can't save settings");
        }
    }

    if options.data_dir.is_none() {
        send_to::start();
        startup::start(initial_settings.start_with_windows);
        updater::set_auto(initial_settings.auto_update);
        updater::set_auto_install(initial_settings.install_updates);
        let mine = version::Channel::parse(&initial_settings.update_channel);
        let pc = updater::machine_channel().filter(|pc| mine != Some(*pc) && updater::task_installs());
        let channel = match (pc, mine) {
            (None, Some(mine)) => mine,
            (pc, _) => {
                let channel = pc.unwrap_or_else(updater::channel);
                let mut saved = settings::Settings::load(&data_dir);
                saved.update_channel = channel.as_str().into();
                let _ = saved.save(&data_dir);
                channel
            }
        };
        updater::set_channel(channel);
        updater::start();
        match (updated_from, options.updated) {
            (Some(previous), _) => {
                let mut saved = settings::Settings::load(&data_dir);
                if std::mem::take(&mut saved.hive_invite_dismissed) {
                    let _ = saved.save(&data_dir);
                }
                updater::announce_update(previous, !options.minimized);
            }
            (None, true) => updater::announce_update_failed(!options.minimized),
            (None, false) => {}
        }
    }

    bridge::app::init_app_controller();
    winui::refresh_startup_async();

    let instance_watch_dir = data_dir.clone();
    if let Some(inst) = instance {
        PRIMARY_INSTANCE.get_or_init(|| inst).watch(move || {
            let requests = launch::drain(&instance_watch_dir);
            if requests.is_empty() {
                request_activation();
            }
            requests.into_iter().for_each(send_to::handle);
        });
    }

    let _ = HOLDER.get_or_init(|| {
        Mutex::new(Some(RuntimeHolder {
            data_dir,
            minimized: options.minimized,
            ui_check: options.ui_check,
            initial_request: request,
        }))
    });

    0
}

/// Processes any queued or initial launch requests once the WinUI main window is ready.
#[unsafe(no_mangle)]
pub extern "C" fn nl_on_ui_ready() {
    let holder = HOLDER.get().and_then(|m| m.lock().ok()).and_then(|mut g| {
        let h = g.take()?;
        let data_dir = h.data_dir.clone();
        let req = h.initial_request.clone();
        let ui_check = h.ui_check;
        *g = Some(h);
        Some((data_dir, req, ui_check))
    });
    let Some((data_dir, request, ui_check)) = holder else { return };
    for waiting in launch::drain(&data_dir) {
        if matches!(
            waiting,
            Request::Send { .. }
                | Request::Share { .. }
                | Request::TaskNotify { .. }
                | Request::Task { .. }
                | Request::OpenDialer { .. }
        ) {
            send_to::handle(waiting);
        }
    }
    if matches!(
        request,
        Request::Send { .. } | Request::Share { .. } | Request::Task { .. } | Request::OpenDialer { .. }
    ) {
        send_to::handle(request);
    }
    if ui_check {
        std::thread::spawn(|| {
            std::thread::sleep(Duration::from_secs(2));
            bridge::app::open_shelf();
            std::thread::sleep(Duration::from_secs(2));
            bridge::app::close_shelf();
            std::thread::sleep(Duration::from_secs(2));
            tracing::info!("ui-check: requesting quit");
            bridge::app::request_quit();
        });
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn nl_should_start_minimized() -> c_int {
    let min = HOLDER
        .get()
        .and_then(|m| m.lock().ok())
        .and_then(|g| g.as_ref().map(|h| h.minimized))
        .unwrap_or(false);
    i32::from(min)
}

/// Returns a newly allocated UTF-8 JSON snapshot for `mask` (`u32::MAX` for full state).
/// Caller MUST free with `nl_free_string`.
#[unsafe(no_mangle)]
pub extern "C" fn nl_get_state(mask: u32) -> *mut c_char {
    let json = winui::snapshot_json(mask);
    alloc_cstring(&json)
}

/// Invokes a command `cmd` with JSON `args_json`, returning a UTF-8 JSON response.
/// Caller MUST free with `nl_free_string`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nl_invoke(cmd: *const c_char, args_json: *const c_char) -> *mut c_char {
    let Some(cmd_str) = c_str(cmd) else {
        return alloc_cstring(r#"{"ok":false,"error":"null command"}"#);
    };
    let args = c_str(args_json)
        .and_then(|s| serde_json::from_str::<serde_json::Value>(&s).ok())
        .unwrap_or(serde_json::Value::Object(Default::default()));
    let res = winui::invoke_command(&cmd_str, &args);
    alloc_cstring(&res.to_string())
}

/// Fast-path mirror pointer touch input avoiding JSON allocations during 60-120Hz drags.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nl_mirror_touch(key: *const c_char, action: u32, x: f32, y: f32) {
    let Some(k) = c_str(key) else { return };
    let Some(w) = mirror::Window::parse(&k) else { return };
    let act = match action {
        0 => nectarlink_core::TouchAction::Down,
        1 => nectarlink_core::TouchAction::Move,
        2 => nectarlink_core::TouchAction::Up,
        _ => return,
    };
    mirror::input(w, MirrorInput::Touch { action: act, x: x.clamp(0.0, 1.0), y: y.clamp(0.0, 1.0) });
}

/// Fast-path mirror scroll input.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nl_mirror_scroll(key: *const c_char, x: f32, y: f32, dx: f32, dy: f32) {
    let Some(k) = c_str(key) else { return };
    let Some(w) = mirror::Window::parse(&k) else { return };
    mirror::input(
        w,
        MirrorInput::Scroll {
            x: x.clamp(0.0, 1.0),
            y: y.clamp(0.0, 1.0),
            dx: dx.clamp(-100.0, 100.0),
            dy: dy.clamp(-100.0, 100.0),
        },
    );
}

#[unsafe(no_mangle)]
pub extern "C" fn nl_free_string(ptr: *mut c_char) {
    if !ptr.is_null() {
        // SAFETY: Pointer returned from `CString::into_raw` in `alloc_cstring`.
        unsafe {
            drop(CString::from_raw(ptr));
        }
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn nl_shutdown() {
    tracing::info!("shutting down");
    bridge::app::stop_tray_thread();
    mirror::stop_decoders();
    pc_audio_stream::set_enabled(false);
    pc_mirror::stop_all();
    photos::clear_shared_files();
    local_api::stop();
    agents::stop();
    win::ble_beacon::stop();
    webcam::shutdown();
    storage::shutdown();
    win::net::stop();
    core_host::shutdown();
    bridge::clear_callbacks();
    nectarlink_core::diag::health::flush();
    nectarlink_core::diag::crash::end_run();
}
