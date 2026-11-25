// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NectarLink.WinUI.Controls;
using NectarLink.WinUI.Services;

namespace NectarLink.WinUI.Pages;

/// <summary>
/// Main Home dashboard matching HomePage.qml: device hero with phone cutout/aspect
/// silhouette, telemetry, quick actions, Now Playing + Phone Sound + Play on Phone,
/// phone quick settings toggles, active calls, grouped Notifications + History,
/// Transfers &amp; Send Queue, Clipboard history, LocalSend peers, and summary strip.
/// </summary>
public sealed partial class HomePage : Page, IRefreshablePage
{
    private readonly StackPanel _rootStack = new() { Spacing = 14, Padding = new Thickness(14, 12, 24, 28) };
    private readonly Border _emptyHost = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _dashboardPanel = new() { Spacing = 14 };
    private readonly Border _heroHost = new();
    private readonly Border _activeCallHost = new() { Visibility = Visibility.Collapsed };
    private readonly Border _notifsHost = new();
    private readonly Border _summaryHost = new();
    private readonly Border _mediaHost = new();
    private readonly Border _togglesHost = new();
    private readonly Border _transfersHost = new();
    private readonly Border _clipboardHost = new();
    private readonly Border _localSendHost = new() { Visibility = Visibility.Collapsed };

    private bool _showNotifHistory;
    private readonly Dictionary<string, string> _inlineReplies = new(StringComparer.Ordinal);

    // Per-section change tracking to avoid rebuilding unaffected cards on unrelated events
    private string _heroStructKey = string.Empty;
    private TextBlock? _heroTitleText;
    private Border? _heroPathBadge;
    private TextBlock? _heroPathBadgeText;
    private Border? _heroPowerBadge;
    private TextBlock? _heroPowerBadgeText;
    private TextBlock? _heroTelemetryText;
    private Button? _heroRingBtn;

    private string _callsSig = string.Empty;
    private string _notifsSig = string.Empty;
    private string _summarySig = string.Empty;
    private string _mediaSig = string.Empty;
    private string _togglesStructKey = string.Empty;
    private string _transfersSig = string.Empty;
    private string _clipboardSig = string.Empty;
    private string _localSendSig = string.Empty;

    // Persistent Phone Controls widgets updated in-place
    private Button? _dndBtn;
    private Button? _wifiBtn;
    private Button? _btBtn;
    private Button? _torchBtn;
    private Button? _ringerCycleBtn;
    private Grid? _ringerIconHost;
    private FontIcon? _ringerIcon;
    private int _ringerAnimGen;
    private DispatcherTimer? _ringerCleanupTimer;
    private Slider? _volSlider;
    private Slider? _brightSlider;
    private DispatcherTimer? _volDebounce;
    private DispatcherTimer? _brightDebounce;
    private bool _draggingVol;
    private bool _draggingBright;
    private bool _suppressToggleSliderEvents;
    private long _volHoldUntilTick;
    private long _brightHoldUntilTick;
    private bool _dndState;
    private bool _wifiState;
    private bool _btState;
    private bool _torchState;
    private string _ringerState = "";

    public HomePage()
    {
        var twoCol = new Grid { ColumnSpacing = 16 };
        twoCol.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        twoCol.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var leftCol = new StackPanel { Spacing = 16 };
        leftCol.Children.Add(_activeCallHost);
        leftCol.Children.Add(_notifsHost);
        leftCol.Children.Add(_summaryHost);

        var rightCol = new StackPanel { Spacing = 16 };
        rightCol.Children.Add(_mediaHost);
        rightCol.Children.Add(_togglesHost);
        rightCol.Children.Add(_transfersHost);
        rightCol.Children.Add(_clipboardHost);
        rightCol.Children.Add(_localSendHost);

        Grid.SetColumn(leftCol, 0);
        Grid.SetColumn(rightCol, 1);
        twoCol.Children.Add(leftCol);
        twoCol.Children.Add(rightCol);

        _dashboardPanel.Children.Add(_heroHost);
        _dashboardPanel.Children.Add(twoCol);

        _emptyHost.Child = UiKit.Card(UiKit.TitleText(Loc.Tr("Home", "No device selected"), 18));
        _rootStack.Children.Add(_emptyHost);
        _rootStack.Children.Add(_dashboardPanel);

        Content = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _rootStack
        };
    }

    public void OnNavigatedTo() => Render(EngineChanges.All, force: false);
    public void OnEngineChanged(uint mask) => Render(mask, force: false);

    private void Render(uint mask, bool force)
    {
        var engine = NectarLinkEngine.Current;
        var dev = engine.CurrentDevice;
        string devId = engine.CurrentDeviceId;
        if (dev is null || string.IsNullOrEmpty(devId))
        {
            _emptyHost.Visibility = Visibility.Visible;
            _dashboardPanel.Visibility = Visibility.Collapsed;
            return;
        }

        _emptyHost.Visibility = Visibility.Collapsed;
        _dashboardPanel.Visibility = Visibility.Visible;

        if (force || EngineChanges.HasAny(mask, EngineChanges.Status | EngineChanges.Devices | EngineChanges.Capabilities | EngineChanges.Ringing | EngineChanges.DeviceMetrics | EngineChanges.Prefs))
            SyncHero(dev, devId, force);

        if (force || EngineChanges.HasAny(mask, EngineChanges.Calls | EngineChanges.Devices))
            SyncActiveCall(force);

        if (force || EngineChanges.HasAny(mask, EngineChanges.Notifications | EngineChanges.History | EngineChanges.Devices | EngineChanges.Status))
            SyncNotifications(devId, force);

        if (force || EngineChanges.HasAny(mask, EngineChanges.Messages | EngineChanges.Calls | EngineChanges.Devices | EngineChanges.Status))
            SyncSummary(force);

        if (force || EngineChanges.HasAny(mask, EngineChanges.Media | EngineChanges.Devices | EngineChanges.Capabilities))
            SyncMedia(devId, force);

        if (force || EngineChanges.HasAny(mask, EngineChanges.Toggles | EngineChanges.Devices | EngineChanges.Capabilities))
            SyncToggles(dev, devId, force);

        if (force || EngineChanges.HasAny(mask, EngineChanges.Transfers | EngineChanges.Queue | EngineChanges.Devices))
            SyncTransfers(force);

        if (force || EngineChanges.HasAny(mask, EngineChanges.Clipboard | EngineChanges.Continuity | EngineChanges.Prefs | EngineChanges.Devices))
            SyncClipboard(devId, force);

        if (force || EngineChanges.HasAny(mask, EngineChanges.LocalSend | EngineChanges.Status))
            SyncLocalSend(force);
    }

    private void SyncHero(JsonObject dev, string devId, bool force)
    {
        string name = dev["name"]?.GetValue<string>() ?? "Phone";
        bool online = dev["online"]?.GetValue<bool>() ?? false;
        string path = dev["path"]?.GetValue<string>() ?? "offline";
        int rtt = dev["rttMs"]?.GetValue<int>() ?? -1;
        int bat = dev["battery"]?.GetValue<int>() ?? -1;
        bool charging = dev["batteryCharging"]?.GetValue<bool>() ?? false;
        int cellBars = dev["cellBars"]?.GetValue<int>() ?? -1;
        string cellType = dev["cellType"]?.GetValue<string>() ?? "";
        int wifiBars = dev["wifiBars"]?.GetValue<int>() ?? -1;
        bool ringing = dev["ringing"]?.GetValue<bool>() ?? false;
        string power = dev["powerLevel"]?.GetValue<string>() ?? "basic";
        string osVer = dev["osVer"]?.GetValue<string>() ?? "";
        string model = dev["model"]?.GetValue<string>() ?? "";
        string wpPath = dev["wallpaperPath"]?.GetValue<string>() ?? "";
        long wpMtime = (!string.IsNullOrEmpty(wpPath) && System.IO.File.Exists(wpPath))
            ? System.IO.File.GetLastWriteTimeUtc(wpPath).Ticks
            : 0;
        string aspect = dev["screen"]?["aspect"]?.ToJsonString() ?? "";

        string structKey = $"{devId}|{wpPath}|{wpMtime}|{aspect}";
        if (force || structKey != _heroStructKey || _heroHost.Child is null)
        {
            _heroStructKey = structKey;
            _heroHost.Child = BuildDeviceHeroCard(dev, devId);
        }

        // Update live telemetry & badges in-place without destroying silhouette or buttons
        if (_heroTitleText is not null) _heroTitleText.Text = name;
        string pathLabel = path switch
        {
            "lan" => rtt >= 0 ? $"Wi-Fi · {rtt} ms" : "Wi-Fi",
            "usb" => rtt >= 0 ? $"USB · {rtt} ms" : "USB",
            "relay" => rtt >= 0 ? $"Relay · {rtt} ms" : "Relay",
            _ => Loc.Tr("Home", "Offline")
        };
        if (_heroPathBadge is not null && _heroPathBadgeText is not null)
        {
            _heroPathBadgeText.Text = pathLabel;
            _heroPathBadge.Background = UiKit.Brush(online ? "NlPrimaryContainerBrush" : "NlSurfaceContainerHighestBrush");
            _heroPathBadgeText.Foreground = UiKit.Brush(online ? "NlOnPrimaryContainerBrush" : "NlOnSurfaceVariantBrush");
        }
        if (_heroPowerBadge is not null && _heroPowerBadgeText is not null)
        {
            bool showPower = power is "elevated" or "assist";
            _heroPowerBadge.Visibility = showPower ? Visibility.Visible : Visibility.Collapsed;
            if (showPower) _heroPowerBadgeText.Text = power == "assist" ? "Assist" : "Elevated";
        }
        if (_heroTelemetryText is not null)
        {
            var parts = new List<string>();
            if (bat >= 0) parts.Add($"{(charging ? "⚡ " : "")}{bat}% battery");
            if (!string.IsNullOrEmpty(cellType) || cellBars >= 0)
                parts.Add($"{cellType} {(cellBars >= 0 ? $"{cellBars}/4" : "")}".Trim());
            if (wifiBars >= 0) parts.Add($"Wi-Fi {wifiBars}/4");
            if (!string.IsNullOrEmpty(model)) parts.Add($"{model} · Android {osVer}");
            _heroTelemetryText.Text = string.Join("   ", parts);
        }
        if (_heroRingBtn is not null && _heroRingBtn.Content is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is TextBlock tb)
        {
            tb.Text = ringing ? Loc.Tr("Home", "Stop ring") : Loc.Tr("Home", "Find phone");
        }
    }

    private void SyncActiveCall(bool force)
    {
        var calls = NectarLinkEngine.Current.Calls;
        bool active = calls["active"]?.GetValue<bool>() ?? false;
        string sig = active ? calls.ToJsonString() : "inactive";
        if (!force && sig == _callsSig) return;
        _callsSig = sig;
        if (!active)
        {
            _activeCallHost.Visibility = Visibility.Collapsed;
            _activeCallHost.Child = null;
        }
        else
        {
            _activeCallHost.Visibility = Visibility.Visible;
            _activeCallHost.Child = BuildActiveCallCard();
        }
    }

    private void SyncNotifications(string devId, bool force)
    {
        if (!force && XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is TextBox)
            return;
        var notifs = NectarLinkEngine.Current.Notifications;
        string sig = $"{devId}|{_showNotifHistory}|{notifs.ToJsonString()}";
        if (!force && sig == _notifsSig) return;
        _notifsSig = sig;
        _notifsHost.Child = BuildNotificationsCard(devId);
    }

    private void SyncSummary(bool force)
    {
        var summary = NectarLinkEngine.Current.App["homeSummary"];
        string sig = summary?.ToJsonString() ?? "";
        if (!force && sig == _summarySig && _summaryHost.Child is not null) return;
        _summarySig = sig;
        _summaryHost.Child = BuildHomeSummaryCard();
    }

    private void SyncMedia(string devId, bool force)
    {
        var media = NectarLinkEngine.Current.Media;
        string sig = $"{devId}|{media.ToJsonString()}";
        if (!force && sig == _mediaSig) return;
        _mediaSig = sig;
        _mediaHost.Child = BuildMediaCard(devId);
    }

    private void SyncToggles(JsonObject dev, string devId, bool force)
    {
        var toggles = dev["phoneToggles"] as JsonObject;
        bool hasData = toggles?["hasData"]?.GetValue<bool>() ?? false;
        bool canTorch = toggles?["hasFlashlight"]?.GetValue<bool>() ?? false;
        string structKey = $"{devId}|{hasData}|{canTorch}";

        if (force || structKey != _togglesStructKey || _togglesHost.Child is null)
        {
            _togglesStructKey = structKey;
            _togglesHost.Child = BuildTogglesCard(devId, hasData, canTorch);
        }
        if (!hasData || toggles is null) return;

        bool newDnd = toggles["dnd"]?.GetValue<bool>() ?? false;
        bool newWifi = toggles["wifi"]?.GetValue<bool>() ?? false;
        bool newBt = toggles["bluetooth"]?.GetValue<bool>() ?? false;
        bool newTorch = toggles["flashlight"]?.GetValue<bool>() ?? false;
        string ringer = toggles["ringer"]?.GetValue<string>() ?? "ring";
        int vol = toggles["volume"]?.GetValue<int>() ?? 50;
        int bright = toggles["brightness"]?.GetValue<int>() ?? 50;

        if (_dndBtn is not null)
        {
            if (newDnd != _dndState) PulseChipIcon(_dndBtn);
            _dndState = newDnd;
            UiKit.SetPillAccent(_dndBtn, _dndState);
        }
        if (_wifiBtn is not null)
        {
            if (newWifi != _wifiState) PulseChipIcon(_wifiBtn);
            _wifiState = newWifi;
            UiKit.SetPillAccent(_wifiBtn, _wifiState);
        }
        if (_btBtn is not null)
        {
            if (newBt != _btState) PulseChipIcon(_btBtn);
            _btState = newBt;
            UiKit.SetPillAccent(_btBtn, _btState);
        }
        if (_torchBtn is not null)
        {
            if (newTorch != _torchState) PulseChipIcon(_torchBtn);
            _torchState = newTorch;
            UiKit.SetPillAccent(_torchBtn, _torchState);
        }
        ApplyRingerVisual(ringer, animate: !string.IsNullOrEmpty(_ringerState));

        _suppressToggleSliderEvents = true;
        try
        {
            long now = Environment.TickCount64;
            if (_volSlider is not null && !_draggingVol && now >= _volHoldUntilTick)
                _volSlider.Value = vol;
            if (_brightSlider is not null && !_draggingBright && now >= _brightHoldUntilTick)
                _brightSlider.Value = bright;
        }
        finally
        {
            _suppressToggleSliderEvents = false;
        }
    }

    private static void PulseChipIcon(Button btn)
    {
        if (!Motion.Enabled) return;
        if (btn.Content is StackPanel sp && sp.Children.Count > 0 && sp.Children[0] is FontIcon icon)
        {
            icon.CenterPoint = new System.Numerics.Vector3(8f, 8f, 0f);
            Motion.ConfigureTransitions(icon, opacityMs: 120, translationMs: 120, scaleMs: 180);
            icon.Scale = new System.Numerics.Vector3(1.22f, 1.22f, 1f);
            icon.DispatcherQueue?.TryEnqueue(() => icon.Scale = System.Numerics.Vector3.One);
        }
    }

    private void ApplyRingerVisual(string mode, bool animate)
    {
        string normalized = mode switch
        {
            "vibrate" => "vibrate",
            "silent" => "silent",
            _ => "ring"
        };
        string glyph = normalized switch
        {
            "vibrate" => "\uE877",
            "silent" => "\uE7ED",
            _ => "\uEA8F"
        };
        string tip = normalized switch
        {
            "vibrate" => Loc.Tr("Toggles", "Ringer: Vibrate (click to switch)"),
            "silent" => Loc.Tr("Toggles", "Ringer: Silent (click to switch)"),
            _ => Loc.Tr("Toggles", "Ringer: Ring (click to switch)")
        };

        if (_ringerCycleBtn is not null)
        {
            UiKit.SetPillAccent(_ringerCycleBtn, normalized != "silent");
            ToolTipService.SetToolTip(_ringerCycleBtn, tip);
        }

        if (_ringerIconHost is null || string.Equals(_ringerState, normalized, StringComparison.Ordinal))
        {
            _ringerState = normalized;
            return;
        }
        _ringerState = normalized;

        int gen = ++_ringerAnimGen;
        _ringerCleanupTimer?.Stop();

        var prevIcon = _ringerIcon;
        for (int i = _ringerIconHost.Children.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(_ringerIconHost.Children[i], prevIcon))
                _ringerIconHost.Children.RemoveAt(i);
        }

        var nextIcon = new FontIcon
        {
            Glyph = glyph,
            FontSize = 15,
            CenterPoint = new System.Numerics.Vector3(9f, 9f, 0f),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _ringerIcon = nextIcon;

        if (!animate || !Motion.Enabled || prevIcon is null)
        {
            _ringerIconHost.Children.Clear();
            _ringerIconHost.Children.Add(nextIcon);
            return;
        }

        // Slide/scale crossfade outgoing and incoming icons smoothly on the compositor
        Motion.ConfigureTransitions(prevIcon, opacityMs: 130, translationMs: 150, scaleMs: 150);
        prevIcon.Opacity = 0;
        prevIcon.Translation = new System.Numerics.Vector3(0f, -8f, 0f);
        prevIcon.Scale = new System.Numerics.Vector3(0.72f, 0.72f, 1f);

        nextIcon.Opacity = 0;
        nextIcon.Translation = new System.Numerics.Vector3(0f, 8f, 0f);
        nextIcon.Scale = new System.Numerics.Vector3(0.72f, 0.72f, 1f);
        _ringerIconHost.Children.Add(nextIcon);

        var dq = _ringerIconHost.DispatcherQueue;
        dq?.TryEnqueue(() =>
        {
            if (_ringerAnimGen != gen) return;
            Motion.ConfigureTransitions(nextIcon, opacityMs: 160, translationMs: 180, scaleMs: 180);
            nextIcon.Opacity = 1;
            nextIcon.Translation = System.Numerics.Vector3.Zero;
            nextIcon.Scale = System.Numerics.Vector3.One;
        });

        var hostRef = _ringerIconHost;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(185) };
        _ringerCleanupTimer = timer;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            hostRef.Children.Remove(prevIcon);
        };
        timer.Start();
    }

    private void SyncTransfers(bool force)
    {
        string sig = NectarLinkEngine.Current.Transfers.ToJsonString();
        if (!force && sig == _transfersSig) return;
        _transfersSig = sig;
        _transfersHost.Child = BuildTransfersCard();
    }

    private void SyncClipboard(string devId, bool force)
    {
        var engine = NectarLinkEngine.Current;
        string sig = $"{devId}|{engine.App["clipSuggestion"]?.ToJsonString()}|{engine.Clipboard.ToJsonString()}";
        if (!force && sig == _clipboardSig) return;
        _clipboardSig = sig;
        _clipboardHost.Child = BuildClipboardCard(devId);
    }

    private void SyncLocalSend(bool force)
    {
        var lsPeers = NectarLinkEngine.Current.LocalSend["peers"] as JsonArray;
        string sig = lsPeers?.ToJsonString() ?? "";
        if (!force && sig == _localSendSig) return;
        _localSendSig = sig;
        if (lsPeers is not null && lsPeers.Count > 0)
        {
            _localSendHost.Visibility = Visibility.Visible;
            _localSendHost.Child = BuildLocalSendCard(lsPeers);
        }
        else
        {
            _localSendHost.Visibility = Visibility.Collapsed;
            _localSendHost.Child = null;
        }
    }

    private UIElement BuildDeviceHeroCard(JsonObject dev, string devId)
    {
        var engine = NectarLinkEngine.Current;
        string name = dev["name"]?.GetValue<string>() ?? "Phone";
        bool online = dev["online"]?.GetValue<bool>() ?? false;
        bool ringing = dev["ringing"]?.GetValue<bool>() ?? false;

        var grid = new Grid { ColumnSpacing = 20 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var silhouette = UiKit.PhoneSilhouette(dev);
        var silhouetteBtn = new Button
        {
            Content = silhouette,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(14)
        };
        ToolTipService.SetToolTip(silhouetteBtn, Loc.Tr("Home", "Click to fetch or set phone wallpaper"));
        silhouetteBtn.Click += async (_, _) => await Dialogs.AppDialogs.ShowPhoneWallpaperAsync(XamlRoot, devId);
        grid.Children.Add(silhouetteBtn);

        var info = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        var titleHeader = new Grid { ColumnSpacing = 10 };
        titleHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        _heroTitleText = UiKit.TitleText(name, 26);
        _heroTitleText.VerticalAlignment = VerticalAlignment.Center;
        titleRow.Children.Add(_heroTitleText);
        _heroPathBadge = UiKit.StatusBadge("", highlight: online);
        _heroPathBadgeText = _heroPathBadge.Child as TextBlock;
        titleRow.Children.Add(_heroPathBadge);
        _heroPowerBadge = UiKit.StatusBadge("Elevated", highlight: true);
        _heroPowerBadgeText = _heroPowerBadge.Child as TextBlock;
        titleRow.Children.Add(_heroPowerBadge);
        titleHeader.Children.Add(titleRow);

        var wallpaperBtn = UiKit.PillButton(
            Loc.Tr("Home", "Wallpaper"),
            async () => await Dialogs.AppDialogs.ShowPhoneWallpaperAsync(XamlRoot, devId),
            glyph: "\uE91B");
        wallpaperBtn.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(wallpaperBtn, 1);
        titleHeader.Children.Add(wallpaperBtn);
        info.Children.Add(titleHeader);

        _heroTelemetryText = UiKit.MutedText("");
        info.Children.Add(_heroTelemetryText);

        // Primary quick actions
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var mirrorBtn = UiKit.PillButton(
            Loc.Tr("Home", "Mirror screen"),
            () => engine.Invoke("mirror.start", new { device = devId }),
            glyph: "\uE7F4",
            accent: true);
        var sendBtn = UiKit.PillButton(
            Loc.Tr("Home", "Send files"),
            async () =>
            {
                if ((App.Current as App)?.MainWin is { } mw) await mw.PickAndSendFilesAsync(false);
            },
            glyph: "\uE898");
        _heroRingBtn = UiKit.PillButton(
            ringing ? Loc.Tr("Home", "Stop ring") : Loc.Tr("Home", "Find phone"),
            () =>
            {
                bool curRing = NectarLinkEngine.Current.CurrentDevice?["ringing"]?.GetValue<bool>() ?? false;
                engine.Invoke("device.ring", new { device = devId, on = !curRing });
            },
            glyph: "\uE8F3");
        var photoBtn = UiKit.PillButton(
            Loc.Tr("Home", "Take photo"),
            () => engine.Invoke("continuity.capture", new { device = devId, kind = "photo" }),
            glyph: "\uE722");
        var scanBtn = UiKit.PillButton(
            Loc.Tr("Home", "Scan doc"),
            () => engine.Invoke("continuity.capture", new { device = devId, kind = "scan" }),
            glyph: "\uE8FE");
        var shelfBtn = UiKit.PillButton(
            Loc.Tr("Home", "Shelf"),
            () => engine.Invoke("shelf.toggle"),
            glyph: "\uE8A9");
        foreach (var b in new[] { mirrorBtn, sendBtn, _heroRingBtn, photoBtn, scanBtn, shelfBtn })
        {
            b.Padding = new Thickness(11, 7, 11, 7);
            actions.Children.Add(b);
        }
        info.Children.Add(UiKit.HScroll(actions));

        Grid.SetColumn(info, 1);
        grid.Children.Add(info);
        return UiKit.Card(grid);
    }

    private UIElement BuildActiveCallCard()
    {
        var engine = NectarLinkEngine.Current;
        var calls = engine.Calls;
        string caller = calls["caller"]?.GetValue<string>() ?? "Unknown";
        string number = calls["number"]?.GetValue<string>() ?? "";
        bool ringing = calls["ringing"]?.GetValue<bool>() ?? false;
        bool muted = calls["muted"]?.GetValue<bool>() ?? false;
        bool callAudio = calls["callAudio"]?.GetValue<bool>() ?? false;
        bool callAudioAvail = calls["callAudioAvailable"]?.GetValue<bool>() ?? false;

        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(UiKit.SectionLabel(ringing ? Loc.Tr("Calls", "Incoming call") : Loc.Tr("Calls", "Active call")));
        stack.Children.Add(UiKit.TitleText(caller, 18));
        if (!string.IsNullOrEmpty(number)) stack.Children.Add(UiKit.MutedText(number));

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (ringing)
        {
            row.Children.Add(UiKit.PillButton(Loc.Tr("Calls", "Answer"), () => engine.Invoke("calls.command", new { action = "answer" }), accent: true));
        }
        row.Children.Add(UiKit.PillButton(ringing ? Loc.Tr("Calls", "Decline") : Loc.Tr("Calls", "End call"), () => engine.Invoke("calls.command", new { action = "end" })));
        row.Children.Add(UiKit.PillButton(muted ? Loc.Tr("Calls", "Unmute") : Loc.Tr("Calls", "Mute"), () => engine.Invoke("calls.command", new { action = "mute", on = !muted })));
        if (callAudioAvail)
        {
            row.Children.Add(UiKit.PillButton(
                callAudio ? Loc.Tr("Calls", "Back to phone") : Loc.Tr("Calls", "Move audio to PC"),
                () => engine.Invoke("calls.moveCallAudio", new { here = !callAudio })));
        }
        stack.Children.Add(row);
        return UiKit.Card(stack, highContrast: true);
    }

    private UIElement BuildNotificationsCard(string devId)
    {
        var engine = NectarLinkEngine.Current;
        var notifs = engine.Notifications;
        string undoLabel = notifs["undoLabel"]?.GetValue<string>() ?? "";
        var stack = new StackPanel { Spacing = 10 };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        tabs.Children.Add(UiKit.PillButton(Loc.Tr("Notifications", "Notifications"), () => { _showNotifHistory = false; SyncNotifications(devId, force: true); }, accent: !_showNotifHistory));
        tabs.Children.Add(UiKit.PillButton(Loc.Tr("Notifications", "History"), () => { _showNotifHistory = true; SyncNotifications(devId, force: true); }, accent: _showNotifHistory));
        header.Children.Add(tabs);

        var hdrRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (!string.IsNullOrEmpty(undoLabel))
            hdrRight.Children.Add(UiKit.PillButton(Loc.Tr("Notifications", "Undo"), () => engine.Invoke("notifications.undoDismiss"), accent: true));
        if (!_showNotifHistory)
            hdrRight.Children.Add(UiKit.PillButton(Loc.Tr("Notifications", "Clear all"), () => engine.Invoke("notifications.dismissAll", new { device = devId })));
        Grid.SetColumn(hdrRight, 1);
        header.Children.Add(hdrRight);
        stack.Children.Add(header);

        var items = (_showNotifHistory ? notifs["history"] : notifs["rows"]) as JsonArray;
        if (items is null || items.Count == 0)
        {
            stack.Children.Add(UiKit.MutedText(_showNotifHistory
                ? Loc.Tr("Notifications", "No recent notification history.")
                : Loc.Tr("Notifications", "You're all caught up.")));
        }
        else
        {
            int shown = 0;
            foreach (var item in items)
            {
                if (item is not JsonObject n) continue;
                if (n["deviceId"]?.GetValue<string>() != devId) continue;
                if (++shown > 16) break;

                stack.Children.Add(_showNotifHistory ? BuildHistoryRow(n) : BuildNotifRow(n, devId));
            }
        }
        return UiKit.Card(stack);
    }

    private UIElement BuildNotifRow(JsonObject n, string devId)
    {
        var engine = NectarLinkEngine.Current;
        string key = n["key"]?.GetValue<string>() ?? "";
        string app = n["app"]?.GetValue<string>() ?? "";
        string appName = n["appName"]?.GetValue<string>() ?? app;
        string title = n["title"]?.GetValue<string>() ?? "";
        string text = n["text"]?.GetValue<string>() ?? "";
        string otp = n["otpCode"]?.GetValue<string>() ?? "";
        string replyAct = n["replyAction"]?.GetValue<string>() ?? "";
        string iconPath = n["iconPath"]?.GetValue<string>() ?? "";
        string imagePath = n["imagePath"]?.GetValue<string>() ?? "";
        bool ongoing = n["ongoing"]?.GetValue<bool>() ?? false;
        int groupCount = n["groupCount"]?.GetValue<int>() ?? 1;
        int groupIndex = n["groupIndex"]?.GetValue<int>() ?? 0;
        bool groupExpanded = n["groupExpanded"]?.GetValue<bool>() ?? false;
        string groupSummary = n["groupSummary"]?.GetValue<string>() ?? "";

        var row = new StackPanel { Spacing = 6 };
        var top = new Grid { ColumnSpacing = 8 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var img = UiKit.LoadLocalImage(iconPath, 32);
        if (img is not null)
            top.Children.Add(new Image { Source = img, Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center });
        else
            top.Children.Add(new FontIcon { Glyph = "\uEA8F", FontSize = 18, VerticalAlignment = VerticalAlignment.Center });

        var appTitle = new TextBlock
        {
            Text = $"{appName}{(groupCount > 1 && groupIndex == 0 ? $" ({groupCount})" : "")}",
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = UiKit.Brush("NlOnSurfaceVariantBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(appTitle, 1);
        top.Children.Add(appTitle);

        var topRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        if (groupCount > 1 && groupIndex == 0)
        {
            topRight.Children.Add(UiKit.IconButton(
                groupExpanded ? "\uE70E" : "\uE70D",
                groupExpanded ? "Collapse" : "Expand group",
                () => engine.Invoke("notifications.toggleGroup", new { app })));
        }
        if (!ongoing)
        {
            topRight.Children.Add(UiKit.IconButton("\uE711", Loc.Tr("Notifications", "Dismiss"), () =>
                engine.Invoke("notifications.dismiss", new { device = devId, key })));
        }
        Grid.SetColumn(topRight, 2);
        top.Children.Add(topRight);
        row.Children.Add(top);

        if (!string.IsNullOrEmpty(title)) row.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(text)) row.Children.Add(UiKit.BodyText(text, 13));
        if (!string.IsNullOrEmpty(groupSummary) && !groupExpanded) row.Children.Add(UiKit.MutedText(groupSummary, 12));

        var pic = UiKit.LoadLocalImage(imagePath, 320);
        if (pic is not null)
        {
            row.Children.Add(new Image { Source = pic, MaxHeight = 140, Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill });
        }

        if (n["live"] is JsonObject live)
        {
            int max = live["max"]?.GetValue<int>() ?? 100;
            int prog = live["progress"]?.GetValue<int>() ?? 0;
            bool indet = live["indeterminate"]?.GetValue<bool>() ?? false;
            string chip = live["chip"]?.GetValue<string>() ?? "";
            row.Children.Add(new ProgressBar { IsIndeterminate = indet, Maximum = Math.Max(1, max), Value = prog });
            if (!string.IsNullOrEmpty(chip)) row.Children.Add(UiKit.StatusBadge(chip, highlight: true));
        }

        var actionsPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (!string.IsNullOrEmpty(otp))
        {
            actionsPanel.Children.Add(UiKit.PillButton(Loc.Tr("Notifications", "Copy %1", otp), () =>
                engine.Invoke("notifications.copyOtp", new { code = otp }), glyph: "\uE8C8", accent: true));
        }
        if (n["actions"] is JsonArray actArr)
        {
            foreach (var a in actArr)
            {
                string aid = a?["id"]?.GetValue<string>() ?? "";
                string atitle = a?["title"]?.GetValue<string>() ?? "";
                if (!string.IsNullOrEmpty(aid))
                {
                    actionsPanel.Children.Add(UiKit.PillButton(atitle, () =>
                        engine.Invoke("notifications.act", new { device = devId, key, action = aid })));
                }
            }
        }
        if (actionsPanel.Children.Count > 0) row.Children.Add(actionsPanel);

        if (!string.IsNullOrEmpty(replyAct))
        {
            var replyGrid = new Grid { ColumnSpacing = 6 };
            replyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            replyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var box = new TextBox
            {
                PlaceholderText = Loc.Tr("Notifications", "Reply to %1…", appName),
                Text = _inlineReplies.TryGetValue(key, out var draft) ? draft : ""
            };
            box.TextChanged += (_, _) => _inlineReplies[key] = box.Text ?? "";
            var sendBtn = UiKit.PillButton(Loc.Tr("Common", "Send"), () =>
            {
                string txt = box.Text ?? "";
                if (!string.IsNullOrWhiteSpace(txt))
                {
                    _inlineReplies.Remove(key);
                    engine.Invoke("notifications.act", new { device = devId, key, action = replyAct, reply = txt });
                }
            }, accent: true);
            Grid.SetColumn(sendBtn, 1);
            replyGrid.Children.Add(box);
            replyGrid.Children.Add(sendBtn);
            row.Children.Add(replyGrid);
        }

        return UiKit.Card(row, new Thickness(12, 10, 12, 10), highContrast: groupIndex == 0);
    }

    private static UIElement BuildHistoryRow(JsonObject h)
    {
        string appName = h["appName"]?.GetValue<string>() ?? "";
        string title = h["title"]?.GetValue<string>() ?? "";
        string text = h["text"]?.GetValue<string>() ?? "";
        var row = new StackPanel { Spacing = 3 };
        row.Children.Add(UiKit.MutedText(appName, 11.5));
        if (!string.IsNullOrEmpty(title)) row.Children.Add(UiKit.BodyText(title, 13));
        if (!string.IsNullOrEmpty(text)) row.Children.Add(UiKit.MutedText(text, 12.5));
        return UiKit.Card(row, new Thickness(12, 8, 12, 8));
    }

    private UIElement BuildHomeSummaryCard()
    {
        var summary = NectarLinkEngine.Current.App["homeSummary"];
        int unreadSms = summary?["unreadSms"]?.GetValue<int>() ?? 0;
        int missedCalls = summary?["missedCalls"]?.GetValue<int>() ?? 0;

        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(UiKit.SectionLabel(Loc.Tr("Home", "At a glance")));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(UiKit.PillButton(
            Loc.Tr("Home", "Messages (%1 unread)", unreadSms),
            () => (App.Current as App)?.MainWin?.NavigateTo("messages"),
            glyph: "\uE8BD"));
        row.Children.Add(UiKit.PillButton(
            Loc.Tr("Home", "Calls (%1 missed)", missedCalls),
            () => (App.Current as App)?.MainWin?.NavigateTo("calls"),
            glyph: "\uE717"));
        row.Children.Add(UiKit.PillButton(
            Loc.Tr("Home", "Photos"),
            () => (App.Current as App)?.MainWin?.NavigateTo("photos"),
            glyph: "\uE91B"));
        stack.Children.Add(row);
        return UiKit.Card(stack);
    }

    private UIElement BuildMediaCard(string devId)
    {
        var engine = NectarLinkEngine.Current;
        var media = engine.Media;
        string soundState = media["phoneSoundStates"]?[devId]?.GetValue<string>() ?? "off";
        bool canPlayOnPhone = media["canPlayOnPhone"]?.GetValue<bool>() ?? false;
        string playOnPhoneState = media["playOnPhoneState"]?.GetValue<string>() ?? "off";

        var stack = new StackPanel { Spacing = 10 };
        var hdr = new Grid();
        hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hdr.Children.Add(UiKit.SectionLabel(Loc.Tr("Media", "Media & sound")));

        var toggles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        toggles.Children.Add(UiKit.PillButton(
            soundState == "playing" ? Loc.Tr("Media", "Phone audio: On") : soundState == "asking" ? Loc.Tr("Media", "Asking phone…") : Loc.Tr("Media", "Phone audio"),
            () => engine.Invoke("media.setPhoneSound", new { device = devId, on = soundState == "off" }),
            accent: soundState != "off"));
        if (canPlayOnPhone || playOnPhoneState != "off")
        {
            toggles.Children.Add(UiKit.PillButton(
                playOnPhoneState != "off" ? Loc.Tr("Media", "Playing on phone") : Loc.Tr("Media", "Play on phone"),
                () => engine.Invoke("media.setPlayOnPhone", new { on = playOnPhoneState == "off" }),
                accent: playOnPhoneState != "off"));
        }
        Grid.SetColumn(toggles, 1);
        hdr.Children.Add(toggles);
        stack.Children.Add(hdr);

        if (media["rows"] is JsonArray rows && rows.Count > 0)
        {
            foreach (var r in rows)
            {
                if (r is not JsonObject p) continue;
                string pDev = p["deviceId"]?.GetValue<string>() ?? "";
                string playerId = p["playerId"]?.GetValue<string>() ?? "";
                string title = p["title"]?.GetValue<string>() ?? p["app"]?.GetValue<string>() ?? "Media";
                string artist = p["artist"]?.GetValue<string>() ?? "";
                bool playing = p["playing"]?.GetValue<bool>() ?? false;
                string artPath = p["artPath"]?.GetValue<string>() ?? "";

                var pGrid = new Grid { ColumnSpacing = 12 };
                pGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                pGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                pGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var artImg = UiKit.LoadLocalImage(artPath, 96);
                if (artImg is not null)
                    pGrid.Children.Add(new Image { Source = artImg, Width = 44, Height = 44 });
                else
                    pGrid.Children.Add(new FontIcon { Glyph = "\uE8D6", FontSize = 26 });

                var meta = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                meta.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                if (!string.IsNullOrEmpty(artist)) meta.Children.Add(UiKit.MutedText(artist, 12));
                Grid.SetColumn(meta, 1);
                pGrid.Children.Add(meta);

                var ctrls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
                ctrls.Children.Add(UiKit.IconButton("\uE892", "Previous", () => engine.Invoke("media.command", new { device = pDev, player = playerId, action = "previous" })));
                ctrls.Children.Add(UiKit.IconButton(playing ? "\uE769" : "\uE768", playing ? "Pause" : "Play", () =>
                    engine.Invoke("media.command", new { device = pDev, player = playerId, action = playing ? "pause" : "play" }), accent: true));
                ctrls.Children.Add(UiKit.IconButton("\uE893", "Next", () => engine.Invoke("media.command", new { device = pDev, player = playerId, action = "next" })));
                Grid.SetColumn(ctrls, 2);
                pGrid.Children.Add(ctrls);

                stack.Children.Add(UiKit.Card(pGrid, new Thickness(10, 8, 10, 8), highContrast: true));
            }
        }
        else
        {
            stack.Children.Add(UiKit.MutedText(Loc.Tr("Media", "Nothing playing right now.")));
        }

        return UiKit.Card(stack);
    }

    private UIElement BuildTogglesCard(string devId, bool hasData, bool canTorch)
    {
        var engine = NectarLinkEngine.Current;
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(UiKit.SectionLabel(Loc.Tr("Toggles", "Phone controls")));

        _dndBtn = null;
        _wifiBtn = null;
        _btBtn = null;
        _torchBtn = null;
        _ringerCycleBtn = null;
        _ringerIconHost = null;
        _ringerIcon = null;
        _ringerState = "";
        _volSlider = null;
        _brightSlider = null;

        if (!hasData)
        {
            stack.Children.Add(UiKit.MutedText(Loc.Tr("Toggles", "Waiting for quick settings from phone…")));
            return UiKit.Card(stack);
        }

        static Button ToggleIconChip(string glyph, string tooltip, Action onClick)
        {
            var btn = UiKit.PillButton("", onClick, glyph: glyph);
            btn.MinWidth = 44;
            btn.Height = 34;
            btn.Padding = new Thickness(12, 0, 12, 0);
            ToolTipService.SetToolTip(btn, tooltip);
            return btn;
        }

        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _dndBtn = ToggleIconChip("\uE708", Loc.Tr("Toggles", "Do Not Disturb"), () =>
        {
            _dndState = !_dndState;
            PulseChipIcon(_dndBtn!);
            UiKit.SetPillAccent(_dndBtn!, _dndState);
            engine.Invoke("device.setPhoneToggle", new { device = devId, id = "dnd", value = _dndState.ToString().ToLowerInvariant() });
        });
        _wifiBtn = ToggleIconChip("\uE701", Loc.Tr("Toggles", "Wi-Fi"), () =>
        {
            _wifiState = !_wifiState;
            PulseChipIcon(_wifiBtn!);
            UiKit.SetPillAccent(_wifiBtn!, _wifiState);
            engine.Invoke("device.setPhoneToggle", new { device = devId, id = "wifi", value = _wifiState.ToString().ToLowerInvariant() });
        });
        _btBtn = ToggleIconChip("\uE702", Loc.Tr("Toggles", "Bluetooth"), () =>
        {
            _btState = !_btState;
            PulseChipIcon(_btBtn!);
            UiKit.SetPillAccent(_btBtn!, _btState);
            engine.Invoke("device.setPhoneToggle", new { device = devId, id = "bluetooth", value = _btState.ToString().ToLowerInvariant() });
        });
        chips.Children.Add(_dndBtn);
        chips.Children.Add(_wifiBtn);
        chips.Children.Add(_btBtn);
        if (canTorch)
        {
            _torchBtn = ToggleIconChip("\uE754", Loc.Tr("Toggles", "Flashlight"), () =>
            {
                _torchState = !_torchState;
                PulseChipIcon(_torchBtn!);
                UiKit.SetPillAccent(_torchBtn!, _torchState);
                engine.Invoke("device.setPhoneToggle", new { device = devId, id = "flashlight", value = _torchState.ToString().ToLowerInvariant() });
            });
            chips.Children.Add(_torchBtn);
        }

        _ringerIconHost = new Grid { Width = 20, Height = 20 };
        _ringerCycleBtn = new Button
        {
            Content = _ringerIconHost,
            MinWidth = 44,
            Height = 34,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = ThemeManager.Current.RadiusSm,
            Background = UiKit.Brush("NlSurfaceContainerHighBrush"),
            Foreground = UiKit.Brush("NlOnSurfaceBrush"),
            BorderThickness = new Thickness(0)
        };
        Motion.AttachPressFeedback(_ringerCycleBtn, 0.965f);
        _ringerCycleBtn.Click += (_, _) =>
        {
            string next = _ringerState switch
            {
                "ring" => "vibrate",
                "vibrate" => "silent",
                _ => "ring"
            };
            ApplyRingerVisual(next, animate: true);
            engine.Invoke("device.setPhoneToggle", new { device = devId, id = "ringer", value = next });
        };
        chips.Children.Add(_ringerCycleBtn);
        stack.Children.Add(chips);

        _volDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _volDebounce.Tick += (_, _) =>
        {
            _volDebounce.Stop();
            if (_volSlider is null) return;
            _volHoldUntilTick = Environment.TickCount64 + 700;
            engine.Invoke("device.setPhoneToggle", new { device = devId, id = "volume", value = ((int)_volSlider.Value).ToString() });
        };
        _volSlider = new Slider { Header = Loc.Tr("Toggles", "Media volume"), Minimum = 0, Maximum = 100 };
        void CommitVol()
        {
            if (!_draggingVol) return;
            _draggingVol = false;
            _volHoldUntilTick = Environment.TickCount64 + 700;
            _volDebounce?.Stop();
            engine.Invoke("device.setPhoneToggle", new { device = devId, id = "volume", value = ((int)_volSlider.Value).ToString() });
        }
        _volSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => _draggingVol = true), handledEventsToo: true);
        _volSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => CommitVol()), handledEventsToo: true);
        _volSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => CommitVol()), handledEventsToo: true);
        _volSlider.ValueChanged += (_, _) =>
        {
            if (_suppressToggleSliderEvents) return;
            _volHoldUntilTick = Environment.TickCount64 + 700;
            if (!_draggingVol)
            {
                _volDebounce?.Stop();
                _volDebounce?.Start();
            }
        };
        stack.Children.Add(_volSlider);

        _brightDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _brightDebounce.Tick += (_, _) =>
        {
            _brightDebounce.Stop();
            if (_brightSlider is null) return;
            _brightHoldUntilTick = Environment.TickCount64 + 700;
            engine.Invoke("device.setPhoneToggle", new { device = devId, id = "brightness", value = ((int)_brightSlider.Value).ToString() });
        };
        _brightSlider = new Slider { Header = Loc.Tr("Toggles", "Screen brightness"), Minimum = 0, Maximum = 100 };
        void CommitBright()
        {
            if (!_draggingBright) return;
            _draggingBright = false;
            _brightHoldUntilTick = Environment.TickCount64 + 700;
            _brightDebounce?.Stop();
            engine.Invoke("device.setPhoneToggle", new { device = devId, id = "brightness", value = ((int)_brightSlider.Value).ToString() });
        }
        _brightSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => _draggingBright = true), handledEventsToo: true);
        _brightSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => CommitBright()), handledEventsToo: true);
        _brightSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => CommitBright()), handledEventsToo: true);
        _brightSlider.ValueChanged += (_, _) =>
        {
            if (_suppressToggleSliderEvents) return;
            _brightHoldUntilTick = Environment.TickCount64 + 700;
            if (!_draggingBright)
            {
                _brightDebounce?.Stop();
                _brightDebounce?.Start();
            }
        };
        stack.Children.Add(_brightSlider);

        return UiKit.Card(stack);
    }

    private UIElement BuildTransfersCard()
    {
        var engine = NectarLinkEngine.Current;
        var transfers = engine.Transfers;
        var stack = new StackPanel { Spacing = 10 };

        var hdr = new Grid();
        hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hdr.Children.Add(UiKit.SectionLabel(Loc.Tr("Transfers", "Transfers")));
        var clearBtn = UiKit.PillButton(Loc.Tr("Transfers", "Clear finished"), () => engine.Invoke("transfers.clearFinished"));
        Grid.SetColumn(clearBtn, 1);
        hdr.Children.Add(clearBtn);
        stack.Children.Add(hdr);

        if (transfers["rows"] is JsonArray rows && rows.Count > 0)
        {
            int count = 0;
            foreach (var r in rows)
            {
                if (r is not JsonObject t || ++count > 6) continue;
                string id = t["transferId"]?.GetValue<string>() ?? "";
                string title = t["title"]?.GetValue<string>() ?? "File";
                string status = t["status"]?.GetValue<string>() ?? "";
                double prog = t["progress"]?.GetValue<double>() ?? 0.0;

                var item = new StackPanel { Spacing = 4 };
                var top = new Grid();
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                top.Children.Add(UiKit.BodyText(title, 13));
                var sBadge = UiKit.StatusBadge(status, highlight: status is "done" or "running");
                Grid.SetColumn(sBadge, 1);
                top.Children.Add(sBadge);
                item.Children.Add(top);

                if (status is "running" or "paused" or "waiting")
                    item.Children.Add(new ProgressBar { Maximum = 1.0, Value = prog });

                var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                if (status == "requested")
                    btns.Children.Add(UiKit.PillButton("Accept", () => engine.Invoke("transfers.accept", new { id }), accent: true));
                if (status == "running")
                    btns.Children.Add(UiKit.PillButton("Pause", () => engine.Invoke("transfers.pause", new { id })));
                if (status == "paused")
                    btns.Children.Add(UiKit.PillButton("Resume", () => engine.Invoke("transfers.resume", new { id }), accent: true));
                if (status is "requested" or "waiting" or "running" or "paused")
                    btns.Children.Add(UiKit.PillButton("Cancel", () => engine.Invoke("transfers.cancel", new { id })));
                if (status == "done")
                {
                    btns.Children.Add(UiKit.PillButton("Open", () => engine.Invoke("transfers.open", new { id })));
                    btns.Children.Add(UiKit.PillButton("Show in folder", () => engine.Invoke("transfers.showInFolder", new { id })));
                }
                if (btns.Children.Count > 0) item.Children.Add(btns);
                stack.Children.Add(UiKit.Card(item, new Thickness(10, 8, 10, 8), highContrast: true));
            }
        }
        else
        {
            stack.Children.Add(UiKit.MutedText(Loc.Tr("Transfers", "No recent file transfers.")));
        }
        return UiKit.Card(stack);
    }

    private UIElement BuildClipboardCard(string devId)
    {
        var engine = NectarLinkEngine.Current;
        var stack = new StackPanel { Spacing = 10 };

        var hdr = new Grid();
        hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hdr.Children.Add(UiKit.SectionLabel(Loc.Tr("Clipboard", "Clipboard")));

        var hBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        hBtns.Children.Add(UiKit.PillButton(Loc.Tr("Clipboard", "Send clipboard"), () => engine.Invoke("clipboard.sendNow", new { device = devId }), accent: true));
        hBtns.Children.Add(UiKit.PillButton(Loc.Tr("Clipboard", "Clear"), () => engine.Invoke("clipboard.clearHistory")));
        Grid.SetColumn(hBtns, 1);
        hdr.Children.Add(hBtns);
        stack.Children.Add(hdr);

        if (engine.App["clipSuggestion"] is JsonObject sug)
        {
            string lbl = sug["label"]?.GetValue<string>() ?? "";
            string det = sug["detail"]?.GetValue<string>() ?? "";
            if (!string.IsNullOrEmpty(lbl))
            {
                stack.Children.Add(UiKit.PillButton($"{lbl}: {det}", () => engine.Invoke("clipboard.runSuggestion"), accent: true));
            }
        }

        if (engine.Clipboard["history"] is JsonArray hist && hist.Count > 0)
        {
            int count = 0;
            foreach (var h in hist)
            {
                if (h is not JsonObject c || ++count > 6) continue;
                string cid = c["id"]?.GetValue<string>() ?? "";
                string text = c["text"]?.GetValue<string>() ?? "";
                bool pinned = c["pinned"]?.GetValue<bool>() ?? false;

                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = text, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });

                var cBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                cBtns.Children.Add(UiKit.IconButton("\uE8C8", "Copy", () => engine.Invoke("clipboard.copyHistoryItem", new { id = cid })));
                cBtns.Children.Add(UiKit.IconButton("\uE724", "Send to phone", () => engine.Invoke("clipboard.sendHistoryItem", new { device = devId, id = cid })));
                cBtns.Children.Add(UiKit.IconButton(pinned ? "\uE840" : "\uE718", "Pin", () => engine.Invoke("clipboard.pinHistoryItem", new { id = cid, pinned = !pinned }), accent: pinned));
                cBtns.Children.Add(UiKit.IconButton("\uE74D", "Delete", () => engine.Invoke("clipboard.deleteHistoryItem", new { id = cid })));
                Grid.SetColumn(cBtns, 1);
                row.Children.Add(cBtns);

                stack.Children.Add(UiKit.Card(row, new Thickness(10, 8, 10, 8)));
            }
        }
        else
        {
            stack.Children.Add(UiKit.MutedText(Loc.Tr("Clipboard", "Clipboard history is empty.")));
        }

        return UiKit.Card(stack);
    }

    private UIElement BuildLocalSendCard(JsonArray peers)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(UiKit.SectionLabel(Loc.Tr("LocalSend", "Nearby LocalSend peers")));
        foreach (var p in peers)
        {
            if (p is not JsonObject obj) continue;
            string alias = obj["name"]?.GetValue<string>() ?? "Peer";
            string addr = obj["addr"]?.GetValue<string>() ?? "";
            stack.Children.Add(UiKit.Card(UiKit.BodyText($"{alias} · {addr}", 13), new Thickness(10, 8, 10, 8)));
        }
        return UiKit.Card(stack);
    }
}
