// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;

namespace NectarLink.WinUI.Services;

/// <summary>
/// Bitmask flags matching Rust <c>Changes</c> in <c>native/nectarlink-winui-core/src/state.rs</c>.
/// </summary>
public static class EngineChanges
{
    public const uint None = 0;
    public const uint All = uint.MaxValue;
    public const uint Status = 1u << 0;
    public const uint Devices = 1u << 1;
    public const uint Discovered = 1u << 2;
    public const uint Pairing = 1u << 3;
    public const uint Capabilities = 1u << 4;
    public const uint Ringing = 1u << 5;
    public const uint Notifications = 1u << 6;
    public const uint Transfers = 1u << 7;
    public const uint Media = 1u << 8;
    public const uint History = 1u << 9;
    public const uint Apps = 1u << 10;
    public const uint Update = 1u << 11;
    public const uint Messages = 1u << 12;
    public const uint Mirror = 1u << 13;
    public const uint Calls = 1u << 14;
    public const uint Photos = 1u << 15;
    public const uint Remote = 1u << 16;
    public const uint Toggles = 1u << 17;
    public const uint Deck = 1u << 18;
    public const uint Webcam = 1u << 19;
    public const uint Clipboard = 1u << 20;
    public const uint Timeline = 1u << 21;
    public const uint Continuity = 1u << 22;
    public const uint Palette = 1u << 23;
    public const uint DataRetention = 1u << 24;
    public const uint LocalSend = 1u << 25;
    public const uint Prefs = 1u << 26;
    public const uint DeviceMetrics = 1u << 27;
    public const uint Queue = 1u << 28;
    public const uint Mic = 1u << 29;
    public const uint PhotoBackup = 1u << 30;
    public const uint PhoneInstall = 1u << 31;

    public static bool HasAny(uint mask, uint flags) => mask == All || (mask & flags) != 0;
}

/// <summary>
/// Central WinUI 3 reactive state store synchronized with <c>nectarlink_winui_core.dll</c>.
/// Receives coalesced Hub change bitmasks on the UI DispatcherQueue and merges
/// updated JSON slices without re-allocating unchanged models.
/// </summary>
public sealed class NectarLinkEngine
{
    public static NectarLinkEngine Current { get; } = new();

    private DispatcherQueue? _dispatcher;
    private bool _started;

    public JsonObject App { get; private set; } = new();
    public JsonArray Devices { get; private set; } = new();
    public JsonObject Pairing { get; private set; } = new();
    public JsonObject Prefs { get; private set; } = new();
    public JsonObject Deck { get; private set; } = new();
    public JsonObject Notifications { get; private set; } = new();
    public JsonObject Transfers { get; private set; } = new();
    public JsonObject Media { get; private set; } = new();
    public JsonObject Messages { get; private set; } = new();
    public JsonObject Calls { get; private set; } = new();
    public JsonObject Photos { get; private set; } = new();
    public JsonObject Mirror { get; private set; } = new();
    public JsonObject Webcam { get; private set; } = new();
    public JsonObject PhoneMic { get; private set; } = new();
    public JsonObject PhotoBackup { get; private set; } = new();
    public JsonObject PhoneInstall { get; private set; } = new();
    public JsonObject Agents { get; private set; } = new();
    public JsonObject Clipboard { get; private set; } = new();
    public JsonObject LocalSend { get; private set; } = new();
    public JsonObject Report { get; private set; } = new();
    public JsonObject Tokens { get; private set; } = new();
    public ulong TimelineRevision { get; private set; }

    public event Action<uint>? StateChanged;
    public event Action<string, string>? ToastRequested;
    public event Action? ActivateRequested;
    public event Action? QuitRequested;
    public event Action? OpenDoctorRequested;
    public event Action? OpenFilePickerRequested;
    public event Action? OpenHandoffPickerRequested;
    public event Action<string, string>? UnlockResultReceived;
    public event Action<string, string>? PhoneToggleFailed;
    public event Action? ReportRequested;
    public event Action<bool>? ShelfToggleRequested;
    public event Action? CommandPaletteRequested;

    public void Start(DispatcherQueue dispatcher)
    {
        if (_started) return;
        _started = true;
        _dispatcher = dispatcher;

        var full = NativeCore.GetState(uint.MaxValue);
        MergeSnapshot(full);
        if (full["tokens"] is JsonObject tokens)
        {
            Tokens = tokens;
            ThemeManager.Current.SetTokens(tokens);
        }
        if (full["i18n"] is JsonObject cat)
        {
            Loc.LoadCatalog(cat);
        }
        ThemeManager.Current.EnsureSystemAccentWatcher();
        ThemeManager.Current.SystemColorsChanged += () =>
        {
            _dispatcher?.TryEnqueue(() => Invoke("app.refreshAppearance"));
        };
        ThemeManager.Current.UpdateFromEngine(App, Prefs, Media);

        NativeCore.OnNativeEvent += OnNativeEvent;
        NativeCore.nl_on_ui_ready();
        StateChanged?.Invoke(uint.MaxValue);
    }

    private void OnNativeEvent(uint kind, string payload)
    {
        if (_dispatcher is null) return;
        _dispatcher.TryEnqueue(() => HandleEventOnUiThread(kind, payload));
    }

    private void HandleEventOnUiThread(uint kind, string payload)
    {
        switch (kind)
        {
            case NativeCore.EventStateChanged:
            {
                uint mask = uint.MaxValue;
                try
                {
                    using var doc = JsonDocument.Parse(payload);
                    if (doc.RootElement.TryGetProperty("changes", out var prop) && prop.TryGetUInt32(out uint m))
                        mask = m;
                }
                catch { }
                var delta = NativeCore.GetState(mask);
                MergeSnapshot(delta);

                // Reload translation catalog dynamically when language changed
                if ((mask & (1u << 26)) != 0) // Changes::PREFS
                {
                    string lang = Prefs["language"]?.GetValue<string>() ?? "";
                    if (!string.Equals(lang, Loc.ActiveLanguage, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(lang)
                        || (string.IsNullOrEmpty(lang) && mask != 0))
                    {
                        var fullCat = NativeCore.GetState(uint.MaxValue)["i18n"] as JsonObject;
                        Loc.LoadCatalog(fullCat);
                    }
                }

                ThemeManager.Current.UpdateFromEngine(App, Prefs, Media);
                StateChanged?.Invoke(mask);
                break;
            }
            case NativeCore.EventToast:
            {
                try
                {
                    using var doc = JsonDocument.Parse(payload);
                    string msg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                    string act = doc.RootElement.TryGetProperty("actionLabel", out var a) ? a.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(msg)) ToastRequested?.Invoke(msg, act);
                }
                catch { }
                break;
            }
            case NativeCore.EventActivate:
                ActivateRequested?.Invoke();
                break;
            case NativeCore.EventQuit:
                QuitRequested?.Invoke();
                break;
            case NativeCore.EventOpenDoctor:
                OpenDoctorRequested?.Invoke();
                break;
            case NativeCore.EventOpenFilePicker:
                OpenFilePickerRequested?.Invoke();
                break;
            case NativeCore.EventOpenHandoffPicker:
                OpenHandoffPickerRequested?.Invoke();
                break;
            case NativeCore.EventUnlockResult:
            {
                try
                {
                    using var doc = JsonDocument.Parse(payload);
                    string dev = doc.RootElement.TryGetProperty("device", out var d) ? d.GetString() ?? "" : "";
                    string res = doc.RootElement.TryGetProperty("result", out var r) ? r.GetString() ?? "" : "";
                    UnlockResultReceived?.Invoke(dev, res);
                }
                catch { }
                break;
            }
            case NativeCore.EventPhoneToggleFailed:
            {
                try
                {
                    using var doc = JsonDocument.Parse(payload);
                    string dev = doc.RootElement.TryGetProperty("device", out var d) ? d.GetString() ?? "" : "";
                    string id = doc.RootElement.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                    PhoneToggleFailed?.Invoke(dev, id);
                }
                catch { }
                break;
            }
            case NativeCore.EventReportRequested:
                ReportRequested?.Invoke();
                break;
            case NativeCore.EventShelfToggle:
            {
                bool open = false;
                try
                {
                    using var doc = JsonDocument.Parse(payload);
                    if (doc.RootElement.TryGetProperty("open", out var o)) open = o.GetBoolean();
                }
                catch { }
                ShelfToggleRequested?.Invoke(open);
                break;
            }
            case NativeCore.EventCommandPaletteOpen:
                CommandPaletteRequested?.Invoke();
                break;
        }
    }

    private void MergeSnapshot(JsonObject snap)
    {
        if (snap["app"] is JsonObject a) App = a;
        if (snap["devices"] is JsonArray d) Devices = d;
        if (snap["pairing"] is JsonObject p) Pairing = p;
        if (snap["prefs"] is JsonObject pr) Prefs = pr;
        if (snap["deck"] is JsonObject dk) Deck = dk;
        if (snap["notifications"] is JsonObject n) Notifications = n;
        if (snap["transfers"] is JsonObject t) Transfers = t;
        if (snap["media"] is JsonObject m) Media = m;
        if (snap["messages"] is JsonObject ms) Messages = ms;
        if (snap["calls"] is JsonObject c) Calls = c;
        if (snap["photos"] is JsonObject ph) Photos = ph;
        if (snap["mirror"] is JsonObject mi) Mirror = mi;
        if (snap["webcam"] is JsonObject w) Webcam = w;
        if (snap["phoneMic"] is JsonObject pm) PhoneMic = pm;
        if (snap["photoBackup"] is JsonObject pb) PhotoBackup = pb;
        if (snap["phoneInstall"] is JsonObject pi) PhoneInstall = pi;
        if (snap["agents"] is JsonObject ag) Agents = ag;
        if (snap["clipboard"] is JsonObject cl) Clipboard = cl;
        if (snap["localsend"] is JsonObject ls) LocalSend = ls;
        if (snap["report"] is JsonObject rp) Report = rp;
        if (snap["timelineRevision"]?.GetValue<ulong>() is ulong rev) TimelineRevision = rev;
    }

    public JsonObject Invoke(string command, object? args = null) => NativeCore.Invoke(command, args);

    public string CurrentDeviceId => App["currentDeviceId"]?.GetValue<string>() ?? "";

    public JsonObject? CurrentDevice
    {
        get
        {
            string id = CurrentDeviceId;
            foreach (var item in Devices)
            {
                if (item is JsonObject obj && obj["id"]?.GetValue<string>() == id)
                    return obj;
            }
            return Devices.Count > 0 ? Devices[0] as JsonObject : null;
        }
    }

    public string FeatureState(string feature)
        => App["featureMatrix"]?[feature]?["state"]?.GetValue<string>() ?? "unavailable";

    public string FeatureReason(string feature)
        => App["featureMatrix"]?[feature]?["reason"]?.GetValue<string>() ?? "";

    public bool IsFeatureReady(string feature) => FeatureState(feature) == "available";
}
