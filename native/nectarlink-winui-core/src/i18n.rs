// SPDX-License-Identifier: GPL-3.0-or-later
//! The app's language: the system's or the one chosen in Settings, for the
//! WinUI 3 UI and for the text the app builds in Rust through [`tr!`].

use std::{
    collections::HashMap,
    sync::{OnceLock, RwLock},
};

/// Languages the app speaks, as Settings offers them: a locale name ("" for
/// the system's) and how the language calls itself.
pub const LANGUAGES: &[(&str, &str)] = &[
    ("", ""),
    ("en", "English"),
    ("de", "Deutsch"),
    ("ja", "日本語"),
    ("zh_CN", "简体中文"),
    ("tr", "Türkçe"),
    ("pt_BR", "Português (Brasil)"),
    ("vi", "Tiếng Việt"),
    ("es", "Español"),
    ("ru", "Русский"),
    ("pl", "Polski"),
];

const TS_DE: &str = include_str!("../i18n/nectarlink_de.ts");
const TS_ES: &str = include_str!("../i18n/nectarlink_es.ts");
const TS_JA: &str = include_str!("../i18n/nectarlink_ja.ts");
const TS_PL: &str = include_str!("../i18n/nectarlink_pl.ts");
const TS_PT_BR: &str = include_str!("../i18n/nectarlink_pt_BR.ts");
const TS_RU: &str = include_str!("../i18n/nectarlink_ru.ts");
const TS_TR: &str = include_str!("../i18n/nectarlink_tr.ts");
const TS_VI: &str = include_str!("../i18n/nectarlink_vi.ts");
const TS_ZH_CN: &str = include_str!("../i18n/nectarlink_zh_CN.ts");

static ACTIVE_LANG: OnceLock<RwLock<String>> = OnceLock::new();
static TRANSLATIONS: OnceLock<RwLock<HashMap<(String, String), String>>> = OnceLock::new();

fn lang_lock() -> &'static RwLock<String> {
    ACTIVE_LANG.get_or_init(|| RwLock::new("en".to_owned()))
}

fn map_lock() -> &'static RwLock<HashMap<(String, String), String>> {
    TRANSLATIONS.get_or_init(|| RwLock::new(HashMap::new()))
}

/// [`LANGUAGES`] without the system's, as JSON for Settings: `[{ code, name }]`.
pub fn languages_json() -> String {
    let list: Vec<_> = LANGUAGES
        .iter()
        .filter(|(code, _)| !code.is_empty())
        .map(|(code, name)| serde_json::json!({ "code": code, "name": name }))
        .collect();
    serde_json::Value::Array(list).to_string()
}

/// Whether `language` is one [`LANGUAGES`] offers.
pub fn is_known(language: &str) -> bool {
    LANGUAGES.iter().any(|(code, _)| *code == language)
}

pub fn active_language() -> String {
    lang_lock().read().map(|g| g.clone()).unwrap_or_else(|_| "en".to_owned())
}

#[cfg(windows)]
#[allow(unsafe_code)]
fn system_ui_languages() -> Vec<String> {
    use windows::Win32::Globalization::{GetUserPreferredUILanguages, MUI_LANGUAGE_NAME};
    let mut num_langs = 0u32;
    let mut buf_len = 0u32;
    // SAFETY: Standard two-call Win32 pattern to query multi-sz UI language list.
    unsafe {
        if GetUserPreferredUILanguages(MUI_LANGUAGE_NAME, &mut num_langs, None, &mut buf_len).is_err()
            || buf_len == 0
        {
            return Vec::new();
        }
        let mut buf = vec![0u16; buf_len as usize];
        if GetUserPreferredUILanguages(
            MUI_LANGUAGE_NAME,
            &mut num_langs,
            Some(windows::core::PWSTR(buf.as_mut_ptr())),
            &mut buf_len,
        )
        .is_err()
        {
            return Vec::new();
        }
        buf.split(|&c| c == 0)
            .take_while(|s| !s.is_empty())
            .map(String::from_utf16_lossy)
            .collect()
    }
}

#[cfg(not(windows))]
fn system_ui_languages() -> Vec<String> {
    Vec::new()
}

fn ts_xml_for(lang: &str) -> Option<&'static str> {
    match lang {
        "de" => Some(TS_DE),
        "es" => Some(TS_ES),
        "ja" => Some(TS_JA),
        "pl" => Some(TS_PL),
        "pt_BR" => Some(TS_PT_BR),
        "ru" => Some(TS_RU),
        "tr" => Some(TS_TR),
        "vi" => Some(TS_VI),
        "zh_CN" => Some(TS_ZH_CN),
        _ => None,
    }
}

fn unescape_xml(s: &str) -> String {
    s.replace("&lt;", "<")
        .replace("&gt;", ">")
        .replace("&quot;", "\"")
        .replace("&apos;", "'")
        .replace("&amp;", "&")
}

fn extract_tag(block: &str, tag: &str) -> Option<String> {
    let open_prefix = format!("<{tag}");
    let start_idx = block.find(&open_prefix)?;
    let after_open = &block[start_idx..];
    let gt = after_open.find('>')?;
    let content_start = &after_open[gt + 1..];
    let close_tag = format!("</{tag}>");
    let end_idx = content_start.find(&close_tag)?;
    Some(unescape_xml(&content_start[..end_idx]))
}

fn parse_ts(xml: &str) -> HashMap<(String, String), String> {
    let mut map = HashMap::new();
    let mut rest = xml;
    while let Some(ctx_start) = rest.find("<context>") {
        rest = &rest[ctx_start + "<context>".len()..];
        let Some(ctx_end) = rest.find("</context>") else { break };
        let ctx_block = &rest[..ctx_end];
        rest = &rest[ctx_end + "</context>".len()..];

        let context_name = extract_tag(ctx_block, "name").unwrap_or_default();
        let mut msg_rest = ctx_block;
        while let Some(m_start) = msg_rest.find("<message") {
            msg_rest = &msg_rest[m_start..];
            let Some(m_end) = msg_rest.find("</message>") else { break };
            let msg_block = &msg_rest[..m_end + "</message>".len()];
            msg_rest = &msg_rest[m_end + "</message>".len()..];

            let Some(source) = extract_tag(msg_block, "source") else { continue };
            let Some(trans) = extract_tag(msg_block, "translation") else { continue };
            if !trans.is_empty() {
                map.insert((context_name.clone(), source), trans);
            }
        }
    }
    map
}

/// Uses `language` ("" for the system's) from now on.
pub fn install(language: &str) {
    let chosen = if is_known(language) { language } else { "" };
    let system = if chosen.is_empty() {
        system_language(&system_ui_languages())
    } else {
        ""
    };
    let language = if chosen.is_empty() { system } else { chosen };
    tracing::info!(chosen, language, "app language");

    let parsed = ts_xml_for(language).map(parse_ts).unwrap_or_default();
    if let Ok(mut g) = map_lock().write() {
        *g = parsed;
    }
    if let Ok(mut g) = lang_lock().write() {
        *g = language.to_owned();
    }
}

/// The language to use when following the system: the first of the user's
/// languages the app speaks, English included, from tags like `de-DE` or `zh-Hans-CN`.
fn system_language(preferred: &[String]) -> &'static str {
    for tag in preferred {
        let tag = tag.replace('_', "-").to_ascii_lowercase();
        let mut parts = tag.split('-');
        let base = parts.next().unwrap_or_default();
        let rest: Vec<&str> = parts.collect();
        let code = match base {
            "en" => "en",
            "de" => "de",
            "es" => "es",
            "ja" => "ja",
            "pl" => "pl",
            "ru" => "ru",
            "tr" => "tr",
            "vi" => "vi",
            "pt" if rest.contains(&"br") => "pt_BR",
            "zh" if rest.contains(&"hans") || rest.contains(&"cn") || rest.contains(&"sg") => "zh_CN",
            "zh" if rest.is_empty() => "zh_CN",
            _ => continue,
        };
        if is_known(code) {
            return code;
        }
    }
    "en"
}

/// `text` (from `context`) in the app's language. Use [`tr!`], which also
/// fills in `%1`, `%2`...
pub fn translate(context: &str, text: &str) -> String {
    if let Ok(guard) = map_lock().read()
        && let Some(found) = guard.get(&(context.to_owned(), text.to_owned()))
    {
        return found.clone();
    }
    text.to_owned()
}

/// Returns all translations for the active language as a JSON object keyed by
/// `"context\nkey"` (or `"key"` fallback), for the WinUI 3 frontend.
pub fn catalog_json() -> String {
    let lang = active_language();
    let entries: serde_json::Map<String, serde_json::Value> = if let Ok(guard) = map_lock().read() {
        guard
            .iter()
            .map(|((ctx, src), dst)| (format!("{ctx}\n{src}"), serde_json::Value::String(dst.clone())))
            .collect()
    } else {
        serde_json::Map::new()
    };
    serde_json::json!({
        "language": lang,
        "entries": entries,
    })
    .to_string()
}

/// Puts `args` in for `%1`, `%2`... in `text` (Qt's placeholders, which
/// translators may reorder). `%%` is a literal `%`.
pub fn fill(text: &str, args: &[&dyn std::fmt::Display]) -> String {
    let mut out = String::with_capacity(text.len());
    let mut chars = text.chars().peekable();
    while let Some(c) = chars.next() {
        if c != '%' {
            out.push(c);
            continue;
        }
        match chars.peek().copied() {
            Some('%') => {
                chars.next();
                out.push('%');
            }
            Some(d @ '1'..='9') => {
                chars.next();
                match args.get(d as usize - '1' as usize) {
                    Some(arg) => out.push_str(&arg.to_string()),
                    None => {
                        out.push('%');
                        out.push(d);
                    }
                }
            }
            _ => out.push('%'),
        }
    }
    out
}

/// Text the app shows, in the user's language: `tr!("Context", "Sent to %1", name)`.
macro_rules! tr {
    ($context:literal, $text:literal $(,)?) => {
        $crate::i18n::translate($context, $text)
    };
    ($context:literal, $text:literal, $($arg:expr),+ $(,)?) => {
        $crate::i18n::fill(&$crate::i18n::translate($context, $text), &[$(&$arg as &dyn std::fmt::Display),+])
    };
}
pub(crate) use tr;

/// A session's title in the app's language, by what it was.
pub fn session_title(kind: nectarlink_core::SessionKind) -> String {
    use nectarlink_core::SessionKind;
    match kind {
        SessionKind::PhoneScreen | SessionKind::PcScreen => tr!("Timeline", "Screen mirroring"),
        SessionKind::AppWindow(n) => tr!("Timeline", "App window #%1", n),
        SessionKind::Webcam => tr!("Timeline", "Phone webcam"),
        SessionKind::Microphone => tr!("Timeline", "Phone microphone"),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn placeholders_fill_in_any_order() {
        assert_eq!(fill("Sent to %1", &[&"Pixel"]), "Sent to Pixel");
        assert_eq!(fill("%2 then %1", &[&"a", &"b"]), "b then a");
        assert_eq!(fill("100%% of %1", &[&3]), "100% of 3");
        assert_eq!(fill("%1 and %2", &[&"x"]), "x and %2");
        assert_eq!(fill("50% off", &[]), "50% off");
    }

    #[test]
    fn tr_fills_its_arguments() {
        let name = "Pixel 9";
        assert_eq!(tr!("Tests", "Sent to %1", name), "Sent to Pixel 9");
        assert_eq!(tr!("Tests", "Done"), "Done");
    }

    #[test]
    fn following_the_system_stops_at_the_first_language_it_speaks() {
        let list = |tags: &[&str]| tags.iter().map(|t| (*t).to_owned()).collect::<Vec<_>>();
        assert_eq!(system_language(&list(&["en-US", "de-DE"])), "en");
        assert_eq!(system_language(&list(&["en-IN", "hi-IN", "de"])), "en");
        assert_eq!(system_language(&list(&["de-DE", "en-US"])), "de");
        assert_eq!(system_language(&list(&["es-ES", "en-US"])), "es");
        assert_eq!(system_language(&list(&["es-MX"])), "es");
        assert_eq!(system_language(&list(&["fr-FR", "tr-TR"])), "tr");
        assert_eq!(system_language(&list(&["ja-JP", "en-US"])), "ja");
        assert_eq!(system_language(&list(&["ja"])), "ja");
        assert_eq!(system_language(&list(&["ru-RU", "en-US"])), "ru");
        assert_eq!(system_language(&list(&["vi-VN"])), "vi");
        assert_eq!(system_language(&list(&["pl-PL"])), "pl");
        assert_eq!(system_language(&list(&["pl"])), "pl");
        assert_eq!(system_language(&list(&["zh-Hans-CN"])), "zh_CN");
        assert_eq!(system_language(&list(&["zh-Hant-TW", "en"])), "en");
        assert_eq!(system_language(&list(&["pt-BR"])), "pt_BR");
        assert_eq!(system_language(&list(&["pt-PT", "en-GB"])), "en");
        assert_eq!(system_language(&list(&["fr-FR"])), "en");
        assert_eq!(system_language(&[]), "en");
    }

    #[test]
    fn only_offered_languages_are_kept() {
        assert!(
            is_known("")
                && is_known("zh_CN")
                && is_known("de")
                && is_known("en")
                && is_known("ja")
                && is_known("tr")
                && is_known("pt_BR")
        );
        let listed: Vec<serde_json::Value> = serde_json::from_str(&languages_json()).unwrap();
        assert_eq!(listed[0], serde_json::json!({ "code": "en", "name": "English" }));
        assert!(listed.iter().all(|l| l["code"] != ""), "the system's isn't a choice in the list");
        assert!(!is_known("xx"));
    }

    #[test]
    fn every_offered_language_parses() {
        for (code, name) in LANGUAGES.iter().filter(|(code, _)| !code.is_empty() && *code != "en") {
            let xml = ts_xml_for(code).expect(name);
            let map = parse_ts(xml);
            assert!(!map.is_empty(), "{name} catalogue should not be empty");
        }
    }
}
