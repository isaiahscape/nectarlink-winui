// SPDX-License-Identifier: GPL-3.0-or-later
//! Problem reporting and crash diagnostics bridge (`nectarlink_core::diag::report`).

use std::{
    sync::Mutex,
    time::{SystemTime, UNIX_EPOCH},
};

use nectarlink_core::{
    DeviceId, LinkState, PeerLog,
    diag::{
        crash, known,
        report::{self, Area, DeviceFacts, DevicePart, Parts},
        send::{self, Answer, Kind, Outgoing, Sent},
        usage,
    },
};

use crate::{
    bridge::{EVENT_REPORT_REQUESTED, emit_event, notify_changes},
    core_host,
    state::Changes,
};

#[derive(Default, Clone, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ReportSnapshot {
    pub preview: String,
    pub busy: bool,
    pub send_state: String,
    pub sent_issue: i32,
    pub phone_log_waiting: bool,
    pub crash: String,
    pub crash_state: String,
    pub crash_fixed_in: String,
}

static STATE: Mutex<ReportSnapshot> = Mutex::new(ReportSnapshot {
    preview: String::new(),
    busy: false,
    send_state: String::new(),
    sent_issue: 0,
    phone_log_waiting: false,
    crash: String::new(),
    crash_state: String::new(),
    crash_fixed_in: String::new(),
});

static CARD: Mutex<Option<crash::CrashRecord>> = Mutex::new(None);
static LAST: Mutex<Option<report::Report>> = Mutex::new(None);
static PEER_LOGS: Mutex<Vec<PeerLogEntry>> = Mutex::new(Vec::new());
type PeerLogEntry = (DeviceId, u64, Option<Vec<String>>);
static INPUTS: Mutex<Option<Inputs>> = Mutex::new(None);
type Inputs = (Area, String, String, bool, Parts);
static GENERATION: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
static ASKED: Mutex<Vec<(DeviceId, nectarlink_core::PeerPart)>> = Mutex::new(Vec::new());

fn update_state(f: impl FnOnce(&mut ReportSnapshot)) {
    {
        let mut guard = STATE.lock().unwrap_or_else(|e| e.into_inner());
        f(&mut guard);
    }
    notify_changes(Changes::REMOTE);
}

pub fn snapshot_json() -> String {
    let guard = STATE.lock().unwrap_or_else(|e| e.into_inner());
    serde_json::to_string(&*guard).unwrap_or_else(|_| "{}".into())
}

pub fn open() {
    update_state(|s| {
        s.send_state.clear();
        s.sent_issue = 0;
    });
    emit_event(EVENT_REPORT_REQUESTED, "{}");
}

pub fn guess_area() -> &'static str {
    report::guess_area(&diag_dir()).id()
}

pub fn prepare(area: &str, other: &str, text: &str, just_now: bool, parts_mask: i32) {
    let generation = GENERATION.fetch_add(1, std::sync::atomic::Ordering::SeqCst) + 1;
    let area = Area::parse(area).unwrap_or(Area::Other);
    let other = other.to_owned();
    let text = text.to_owned();
    let parts = Parts {
        devices: parts_mask & 1 != 0,
        checks: parts_mask & 2 != 0,
        health: parts_mask & 4 != 0,
        crashes: parts_mask & 8 != 0,
        log: parts_mask & 16 != 0,
    };
    update_state(|s| {
        s.send_state.clear();
        s.busy = true;
    });
    *INPUTS.lock().unwrap_or_else(|e| e.into_inner()) =
        Some((area, other.clone(), text.clone(), just_now, parts));
    let spawned = std::thread::Builder::new().name("report".into()).spawn(move || {
        let made = std::panic::catch_unwind(|| build(area, &other, &text, just_now, parts, true)).ok();
        let rendered = made.as_ref().map(report::Report::render).unwrap_or_default();
        if GENERATION.load(std::sync::atomic::Ordering::SeqCst) != generation {
            return;
        }
        *LAST.lock().unwrap_or_else(|e| e.into_inner()) = made;
        let waiting = log_waiting();
        update_state(|s| {
            s.preview = rendered;
            s.phone_log_waiting = waiting;
            s.busy = false;
        });
    });
    if spawned.is_err() {
        update_state(|s| s.busy = false);
    }
}

pub fn looks_like_contact(text: &str) -> bool {
    report::looks_like_contact(text)
}

pub fn open_on_github() -> bool {
    let Some(url) = LAST.lock().unwrap_or_else(|e| e.into_inner()).as_ref().map(report::Report::issue_url)
    else {
        return false;
    };
    match crate::win::shell::open_url(&url) {
        Ok(()) => true,
        Err(e) => {
            tracing::warn!(error = %e, "can't open the new issue page");
            false
        }
    }
}

pub fn send() {
    let Some(made) = LAST.lock().unwrap_or_else(|e| e.into_inner()).clone() else { return };
    update_state(|s| s.send_state = "sending".into());
    let spawned = std::thread::Builder::new().name("report-send".into()).spawn(move || {
        let outgoing = Outgoing::new(&made, Kind::Person, None, now());
        let (state, issue) = match deliver(&outgoing, true) {
            Some(Answer::Filed { issue }) => ("filed", issue),
            Some(Answer::Queued) => ("queued", 0),
            Some(Answer::Limited) => ("limited", 0),
            Some(Answer::Refused) => ("failed", 0),
            Some(Answer::Later) | None => {
                send::keep_pending(&diag_dir(), &outgoing);
                ("later", 0)
            }
        };
        update_state(|s| {
            s.sent_issue = i32::try_from(issue).unwrap_or(0);
            s.send_state = state.into();
        });
    });
    if spawned.is_err() {
        update_state(|s| s.send_state = "failed".into());
    }
}

pub fn open_sent_issue() -> bool {
    let issue = STATE.lock().unwrap_or_else(|e| e.into_inner()).sent_issue;
    issue > 0 && crate::win::shell::open_url(&send::issue_url(issue as u64)).is_ok()
}

pub fn check_crash() {
    let found = crash::for_card(&diag_dir(), env!("CARGO_PKG_VERSION"), now());
    let sig = found.as_ref().map(|r| r.sig.clone()).unwrap_or_default();
    *CARD.lock().unwrap_or_else(|e| e.into_inner()) = found;
    update_state(|s| {
        s.crash_fixed_in.clear();
        s.crash = sig.clone();
    });
    if sig.is_empty() {
        return;
    }
    let _ = std::thread::Builder::new().name("known-crashes".into()).spawn(move || {
        let Ok(list) = crate::win::http::get(known::KNOWN_URL, known::MAX_BYTES) else { return };
        if let Some(version) = known::fixed_in(&list, &sig, env!("CARGO_PKG_VERSION")) {
            update_state(|s| s.crash_fixed_in = version);
        }
    });
}

pub fn crash_send() {
    let Some(record) = CARD.lock().unwrap_or_else(|e| e.into_inner()).clone() else { return };
    update_state(|s| s.crash_state = "sending".into());
    let spawned = std::thread::Builder::new().name("crash-report".into()).spawn(move || {
        let area = Area::guess(Some(&record.location), None);
        let parts = Parts { devices: true, checks: true, health: true, crashes: true, log: false };
        let state = std::panic::catch_unwind(|| build(area, "", "", false, parts, false))
            .ok()
            .map(|made| Outgoing::new(&made, Kind::Crash, Some(&record.sig), now()))
            .map_or("failed", |outgoing| match deliver(&outgoing, false) {
                Some(Answer::Filed { .. } | Answer::Queued) => "sent",
                Some(Answer::Later) | None => {
                    send::keep_pending(&diag_dir(), &outgoing);
                    "sent"
                }
                _ => "failed",
            });
        crash::card_shown(&diag_dir(), &record);
        update_state(|s| s.crash_state = state.into());
    });
    if spawned.is_err() {
        update_state(|s| s.crash_state = "failed".into());
    }
}

pub fn crash_done(look: bool) {
    if let Some(record) = CARD.lock().unwrap_or_else(|e| e.into_inner()).take() {
        crash::card_shown(&diag_dir(), &record);
    }
    update_state(|s| {
        s.crash.clear();
        s.crash_state.clear();
    });
    if look {
        open();
    }
}

pub fn crash_off() {
    crash::card_off(&diag_dir());
    CARD.lock().unwrap_or_else(|e| e.into_inner()).take();
    update_state(|s| {
        s.crash.clear();
        s.crash_state.clear();
    });
}

fn deliver(outgoing: &Outgoing, follow: bool) -> Option<Answer> {
    let url = format!("{}/v1/report", send::REPORT_SERVICE);
    let answer = match crate::win::http::post_json(
        &url,
        &[("X-Proof", &outgoing.proof)],
        outgoing.body.as_bytes(),
        16 * 1024,
    ) {
        Ok((status, body)) => Answer::read(status, &body),
        Err(e) => {
            tracing::info!(error = e, "the report service can't be reached");
            return None;
        }
    };
    tracing::info!(state = ?answer, "report sent");
    let issue = match answer {
        Answer::Filed { issue } => Some(issue),
        Answer::Queued => None,
        _ => return Some(answer),
    };
    if !follow {
        return Some(answer);
    }
    send::remember(
        &diag_dir(),
        Sent { rid: outgoing.rid.clone(), issue, at: outgoing.at, comments: 0, closed: false },
        now(),
    );
    Some(answer)
}

pub fn follow_up() {
    daily_count();
    let dir = diag_dir();
    if let Some(pending) = send::pending(&dir) {
        match deliver(&pending, true) {
            Some(Answer::Later) | None => {}
            Some(_) => send::clear_pending(&dir),
        }
    }
    for followed in send::sent(&dir, now()) {
        let Some(issue) = followed.issue else { continue };
        if let Ok((status, body)) = crate::win::http::get_status(&send::issue_api_url(issue), 64 * 1024)
            && let Some(notice) = send::check_issue(&dir, &followed.rid, status, &body, now())
        {
            crate::report_requests::notice(notice);
        }
    }
    for queued in send::sent(&dir, now()).into_iter().filter(|s| s.issue.is_none()) {
        let url = format!("{}/v1/report/{}", send::REPORT_SERVICE, queued.rid);
        match crate::win::http::get_status(&url, 4096).map(|(status, body)| Answer::read(status, &body)) {
            Ok(Answer::Filed { issue }) => {
                send::remember(&dir, Sent { issue: Some(issue), ..queued }, now());
            }
            Ok(Answer::Refused) => {
                send::forget(&dir, &queued.rid);
            }
            _ => {}
        }
    }
}

fn daily_count() {
    if cfg!(debug_assertions) {
        return;
    }
    let dir = diag_dir();
    let facts = pc_facts();
    let install = usage::Install {
        app: facts.app_version,
        channel: facts.channel,
        platform: std::env::consts::OS.into(),
        os: facts.os,
        hardware: String::new(),
    };
    let Some((body, proof)) = usage::due(&dir, &install, now()) else { return };
    let url = format!("{}/v1/ping", send::REPORT_SERVICE);
    match crate::win::http::post_json(&url, &[("X-Proof", &proof)], body.as_bytes(), 4096) {
        Ok((status, _)) if status < 500 && status != 429 => usage::sent(&dir, now()),
        Ok((status, _)) => tracing::info!(code = status, "the daily count wasn't taken"),
        Err(e) => tracing::info!(error = e, "the daily count couldn't be sent"),
    }
}

fn diag_dir() -> std::path::PathBuf {
    core_host::host().data_dir.join("diag")
}

fn build(area: Area, other: &str, text: &str, just_now: bool, parts: Parts, ask: bool) -> report::Report {
    let host = core_host::host();
    let diag_dir = host.data_dir.join("diag");
    let log_dir = crate::logging::logs_dir(&host.data_dir);
    let mut made = report::Report::new(area, other, text, just_now);
    made.parts = parts;
    let checks = doctor_checks();
    made.devices.push(report::local_part(&diag_dir, &log_dir, pc_facts(), checks, just_now));
    let node = core_host::node();
    let paired = node.as_ref().and_then(|node| node.paired_devices().ok()).unwrap_or_default();
    if paired.is_empty() {
        made.missing.push("no_paired_device".into());
    }
    if ask {
        PEER_LOGS.lock().unwrap_or_else(|e| e.into_inner()).clear();
        ASKED.lock().unwrap_or_else(|e| e.into_inner()).clear();
    }
    for device in paired {
        let online = matches!(device.link, LinkState::Online { .. });
        if ask
            && online
            && let Some(node) = node.clone()
        {
            let asked = core_host::host()
                .runtime
                .block_on(async move { node.diag_ask(device.id, parts.log, just_now).await });
            match asked {
                Ok(part) => {
                    if part.log == PeerLog::Asking {
                        PEER_LOGS
                            .lock()
                            .unwrap_or_else(|e| e.into_inner())
                            .push((device.id, part.nonce, None));
                    }
                    ASKED.lock().unwrap_or_else(|e| e.into_inner()).push((device.id, part));
                }
                Err(e) => tracing::info!(error = %e, "a paired device didn't send its part"),
            }
        }
        let asked =
            ASKED.lock().unwrap_or_else(|e| e.into_inner()).iter().find(|(id, _)| *id == device.id).cloned();
        if let Some((_, peer)) = asked {
            let mut part = peer.part;
            part.facts.role = "Phone".into();
            let logs = PEER_LOGS.lock().unwrap_or_else(|e| e.into_inner());
            match logs.iter().find(|(id, _, _)| *id == device.id) {
                Some((_, _, Some(lines))) => part.log = lines.clone(),
                Some((_, _, None)) if parts.log => {
                    made.missing.push("phone_log_waiting_for_share_on_the_phone".into())
                }
                _ => {}
            }
            made.devices.push(part);
            continue;
        }
        let link = match device.link {
            LinkState::Online { path, .. } => format!("online ({path:?})").to_ascii_lowercase(),
            LinkState::Connecting => "connecting".into(),
            LinkState::Offline { .. } => "offline".into(),
        };
        let facts = DeviceFacts {
            role: "Phone".into(),
            app_version: String::new(),
            channel: String::new(),
            install: String::new(),
            os: format!("{} {}", device.info.os, device.info.os_ver),
            arch: String::new(),
            hardware: device.info.model.into_iter().collect(),
            extra: vec![("link".into(), link)],
        };
        made.devices.push(DevicePart { facts, ..DevicePart::default() });
    }
    made
}

pub fn peer_log_arrived(device: DeviceId, nonce: u64, lines: Option<Vec<String>>) {
    {
        let mut logs = PEER_LOGS.lock().unwrap_or_else(|e| e.into_inner());
        let Some(entry) = logs.iter_mut().find(|(id, n, _)| *id == device && *n == nonce) else { return };
        match lines {
            Some(lines) => entry.2 = Some(lines),
            None => {
                logs.retain(|(id, _, _)| *id != device);
            }
        }
    }
    let Some((area, other, text, just_now, parts)) = INPUTS.lock().unwrap_or_else(|e| e.into_inner()).clone()
    else {
        return;
    };
    let generation = GENERATION.load(std::sync::atomic::Ordering::SeqCst);
    let _ = std::thread::Builder::new().name("report".into()).spawn(move || {
        let Ok(made) = std::panic::catch_unwind(|| build(area, &other, &text, just_now, parts, false)) else {
            return;
        };
        if GENERATION.load(std::sync::atomic::Ordering::SeqCst) != generation {
            return;
        }
        let rendered = made.render();
        *LAST.lock().unwrap_or_else(|e| e.into_inner()) = Some(made);
        let waiting = log_waiting();
        update_state(|s| {
            s.preview = rendered;
            s.phone_log_waiting = waiting;
        });
    });
}

fn log_waiting() -> bool {
    PEER_LOGS.lock().unwrap_or_else(|e| e.into_inner()).iter().any(|(_, _, lines)| lines.is_none())
}

pub fn own_facts() -> (DeviceFacts, Vec<(String, String)>) {
    (pc_facts(), doctor_checks())
}

fn doctor_checks() -> Vec<(String, String)> {
    std::panic::catch_unwind(|| crate::doctor::diagnose(&crate::doctor::gather(), now() as i64))
        .unwrap_or_default()
        .into_iter()
        .map(|c| (c.id, format!("{:?}", c.outcome).to_ascii_lowercase()))
        .collect()
}

fn pc_facts() -> DeviceFacts {
    let version = env!("CARGO_PKG_VERSION");
    let channel = match crate::version::channel_of(version) {
        crate::version::Channel::Nightly => "nightly",
        crate::version::Channel::Stable => "stable",
    };
    let install = if crate::updater::can_update() {
        "setup"
    } else {
        "portable or built from source"
    };
    let os = format!("Windows {}", crate::win::os_version());
    DeviceFacts {
        role: "PC".into(),
        app_version: version.into(),
        channel: channel.into(),
        install: install.into(),
        os,
        arch: std::env::consts::ARCH.into(),
        hardware: Vec::new(),
        extra: Vec::new(),
    }
}

fn now() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map_or(0, |d| d.as_secs())
}
