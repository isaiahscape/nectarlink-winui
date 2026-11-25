// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NectarLink.WinUI.Controls;
using NectarLink.WinUI.Dialogs;
using NectarLink.WinUI.Services;

namespace NectarLink.WinUI.Pages;

/// <summary>
/// Full 11-section Settings page matching SettingsPage.qml, backed directly by
/// <c>prefs.set</c>, <c>device.*</c>, <c>phoneInstall.*</c>, <c>webcam.*</c>,
/// <c>phoneMic.*</c>, <c>photoBackup.*</c>, <c>agents.*</c>, and <c>storage.*</c>.
/// </summary>
public sealed partial class SettingsPage : Page, IRefreshablePage
{
    private readonly StackPanel _detailStack = new()
    {
        Spacing = 4,
        HorizontalAlignment = HorizontalAlignment.Center
    };
    private readonly VideoSurfaceControl _webcamPreview = new() { Height = 200 };
    private Panel? _webcamPreviewParent;
    private bool _draggingMicGain;
    private string _category = "hub";
    private string _lastRenderSig = string.Empty;
    private string _lastStructuralSig = string.Empty;
    private long _selfPrefSuppressUntilTick;
    private readonly List<Action> _inPlaceSyncs = [];

    private sealed record Category(string Id, string Label, string Subtitle, string Glyph);

    private static readonly Category[] Categories =
    [
        new("appearance", "Appearance", "Theme, Mica & Acrylic backdrop, color mode, scale, language", "\uE790"),
        new("general", "General", "Windows startup, tray behavior, Send To menu, Nectar Island", "\uE713"),
        new("devices", "Devices", "Paired phones, custom nicknames, Windows Unlock, permissions", "\uE8EA"),
        new("usb", "Phone USB install", "Android ADB platform tools and USB APK installation", "\uE88E"),
        new("av", "Camera & audio", "Phone as Webcam, phone microphone gain, mirroring quality", "\uE722"),
        new("notifications", "Notifications", "OTP auto-copy, Do Not Disturb sync, lock screen & per-app rules", "\uEA8F"),
        new("files", "Files & sync", "Clipboard sync, Photo Backup, recordings, LocalSend", "\uE8B7"),
        new("shortcuts", "Shortcuts & rules", "Global hotkeys, mirror input behavior, automation rules", "\uE765"),
        new("agents", "AI agents & API", "MCP access, localhost automation API, client permissions", "\uE99A"),
        new("storage", "Storage & privacy", "Data directory, cached thumbnails, notification & chat caches", "\uEDA2"),
        new("about", "Connection & about", "Relay away mode, automatic updates, diagnostics, logs", "\uE946"),
    ];

    public SettingsPage()
    {
        var contentHost = new Grid
        {
            Padding = new Thickness(14, 12, 24, 28),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children = { _detailStack }
        };
        contentHost.SizeChanged += (_, e) =>
        {
            double inner = Math.Max(200, e.NewSize.Width - 38);
            _detailStack.Width = Math.Min(inner, 1020);
        };

        var detailScroll = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = contentHost
        };

        Content = detailScroll;
        Unloaded += (_, _) =>
        {
            NectarLinkEngine.Current.Invoke("webcam.setPreviewVisible", new { visible = false });
            _webcamPreview.DetachStream();
        };
    }

    public void OpenCategory(string id)
    {
        if (string.IsNullOrEmpty(id)) id = "hub";
        SelectCategory(id);
    }

    public void OnNavigatedTo()
    {
        NectarLinkEngine.Current.Invoke("storage.refreshRetention");
        NectarLinkEngine.Current.Invoke("phoneInstall.watch", new { watch = true });
        if (_category == "av")
        {
            _webcamPreview.AttachStream("webcam");
            NectarLinkEngine.Current.Invoke("webcam.setPreviewVisible", new { visible = true });
        }
        RenderDetails(force: false);
    }

    public void OnEngineChanged(uint mask)
    {
        if (!EngineChanges.HasAny(mask, RelevantMaskForCategory(_category)))
            return;
        // Never wipe active text input controls or active mic gain slider drag
        if (XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is TextBox)
            return;
        if (_category == "av" && _draggingMicGain)
            return;

        var engine = NectarLinkEngine.Current;
        string sig = ComputeCategorySignature(_category, engine);
        if (mask != EngineChanges.All && sig == _lastRenderSig)
            return;

        // Avoid tearing down cards, toggles, and animations when the user's own toggle/pill click triggers a PREFS sync
        string structSig = ComputeStructuralSignature(_category, engine);
        bool onlyPrefsOrStatus = (mask & ~(EngineChanges.Prefs | EngineChanges.Status)) == 0;
        if (onlyPrefsOrStatus
            && Environment.TickCount64 <= _selfPrefSuppressUntilTick
            && structSig == _lastStructuralSig)
        {
            _lastRenderSig = sig;
            foreach (var sync in _inPlaceSyncs) sync();
            return;
        }

        RenderDetails();
    }

    private void SelectCategory(string id)
    {
        string prev = _category;
        _category = id;
        if (id == "av")
        {
            _webcamPreview.AttachStream("webcam");
            NectarLinkEngine.Current.Invoke("webcam.setPreviewVisible", new { visible = true });
        }
        else
        {
            NectarLinkEngine.Current.Invoke("webcam.setPreviewVisible", new { visible = false });
            _webcamPreview.DetachStream();
        }
        RenderDetails(force: true);
        if (!string.Equals(prev, id, StringComparison.Ordinal))
        {
            // Drill-in slide matching Windows 11 Settings navigation hierarchy
            if (id == "hub")
                Motion.PlayEntrance(_detailStack, offsetX: -18f, offsetY: 0f, durationMs: 190);
            else if (prev == "hub")
                Motion.PlayEntrance(_detailStack, offsetX: 22f, offsetY: 0f, durationMs: 200);
            else
                Motion.PlayEntrance(_detailStack, offsetX: 0f, offsetY: 12f, durationMs: 175);
        }
    }

    private static uint RelevantMaskForCategory(string cat) => cat switch
    {
        "hub" => EngineChanges.Devices | EngineChanges.Status | EngineChanges.Update | EngineChanges.Prefs,
        "appearance" => EngineChanges.Prefs,
        "general" => EngineChanges.Prefs,
        "devices" => EngineChanges.Devices | EngineChanges.Pairing | EngineChanges.Capabilities | EngineChanges.Status | EngineChanges.Ringing | EngineChanges.Prefs,
        "usb" => EngineChanges.PhoneInstall | EngineChanges.Devices | EngineChanges.Prefs,
        "av" => EngineChanges.Webcam | EngineChanges.Mic | EngineChanges.Mirror | EngineChanges.Devices | EngineChanges.Prefs,
        "notifications" => EngineChanges.Apps | EngineChanges.Notifications | EngineChanges.History | EngineChanges.Prefs | EngineChanges.Devices,
        "files" => EngineChanges.Clipboard | EngineChanges.PhotoBackup | EngineChanges.LocalSend | EngineChanges.Prefs,
        "shortcuts" => EngineChanges.Prefs,
        "agents" => EngineChanges.Prefs,
        "storage" => EngineChanges.DataRetention | EngineChanges.Prefs,
        "about" => EngineChanges.Status | EngineChanges.Update | EngineChanges.Devices | EngineChanges.Prefs | EngineChanges.Apps,
        _ => EngineChanges.All
    };

    private static string ComputeStructuralSignature(string cat, NectarLinkEngine engine)
    {
        string lang = engine.Prefs["language"]?.GetValue<string>() ?? "";
        return cat switch
        {
            "appearance" => $"{lang}|{engine.Prefs["languages"]?.ToJsonString()}",
            "general" or "shortcuts" or "storage" or "about" => $"{lang}|{engine.Prefs["automationRules"]?.ToJsonString()}",
            "av" => $"{lang}|{engine.Webcam["addonRegistered"]?.ToJsonString()}|{engine.Webcam["phase"]?.ToJsonString()}|{engine.Webcam["statusText"]?.ToJsonString()}|{engine.PhoneMic["phase"]?.ToJsonString()}",
            "files" => $"{lang}|{engine.PhotoBackup.ToJsonString()}",
            _ => $"{lang}|{ComputeCategorySignature(cat, engine)}"
        };
    }

    private static string ComputeCategorySignature(string cat, NectarLinkEngine engine) => cat + "|" + (cat switch
    {
        "hub" => $"{engine.CurrentDeviceId}|{engine.CurrentDevice?.ToJsonString()}|{engine.App["updateAvailable"]?.ToJsonString()}|{engine.App["updateVersion"]?.ToJsonString()}|{engine.App["version"]?.ToJsonString()}",
        "appearance" or "general" or "shortcuts" => engine.Prefs.ToJsonString(),
        "devices" => $"{engine.CurrentDeviceId}|{engine.Devices.ToJsonString()}|{engine.App["deviceToggles"]?.ToJsonString()}|{engine.Prefs.ToJsonString()}",
        "usb" => $"{engine.PhoneInstall.ToJsonString()}|{engine.Prefs["usbDebugging"]?.ToJsonString()}",
        "av" => $"{engine.CurrentDeviceId}|{engine.Webcam.ToJsonString()}|{engine.PhoneMic.ToJsonString()}|{engine.Prefs.ToJsonString()}",
        "notifications" => $"{engine.Notifications["apps"]?.ToJsonString()}|{engine.Prefs.ToJsonString()}",
        "files" => $"{engine.PhotoBackup.ToJsonString()}|{engine.LocalSend["enabled"]?.ToJsonString()}|{engine.Prefs.ToJsonString()}",
        "agents" => $"{engine.Agents.ToJsonString()}|{engine.Prefs.ToJsonString()}",
        "storage" => $"{engine.App["dataRetentionSummary"]?.ToJsonString()}|{engine.Prefs["dataDir"]?.ToJsonString()}",
        "about" => $"{engine.App["version"]?.ToJsonString()}|{engine.Prefs.ToJsonString()}",
        _ => $"{engine.Prefs.ToJsonString()}|{engine.Devices.ToJsonString()}"
    });

    private void DetachPreviewFromParent()
    {
        if (_webcamPreviewParent is not null)
        {
            _webcamPreviewParent.Children.Remove(_webcamPreview);
            _webcamPreviewParent = null;
        }
        if (_webcamPreview.Parent is Panel lp)
            lp.Children.Remove(_webcamPreview);
        if (VisualTreeHelper.GetParent(_webcamPreview) is Panel vp)
            vp.Children.Remove(_webcamPreview);
    }

    private void RenderDetails(bool force = false)
    {
        var engine = NectarLinkEngine.Current;
        string sig = ComputeCategorySignature(_category, engine);
        if (!force && sig == _lastRenderSig && _detailStack.Children.Count > 0)
            return;
        _lastRenderSig = sig;
        _lastStructuralSig = ComputeStructuralSignature(_category, engine);
        _inPlaceSyncs.Clear();

        // Detach persistent preview control before clearing cards so WinUI never throws on re-parenting
        DetachPreviewFromParent();
        _detailStack.Children.Clear();
        var prefs = engine.Prefs;

        if (_category == "hub")
        {
            RenderHub(engine, prefs);
            return;
        }

        // Windows 11 Settings Breadcrumb Header: "Settings  >  Appearance"
        var cat = Array.Find(Categories, c => c.Id == _category);
        var bread = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 0, 0, 10)
        };
        var backBtn = new Button
        {
            Content = Loc.Tr("Settings", "Settings"),
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily(ThemeManager.Current.FontDisplay),
            Foreground = UiKit.Brush("NlOnSurfaceVariantBrush"),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        backBtn.Click += (_, _) => SelectCategory("hub");
        bread.Children.Add(backBtn);
        bread.Children.Add(new FontIcon
        {
            Glyph = "\uE76C",
            FontSize = 14,
            Foreground = UiKit.Brush("NlOnSurfaceVariantBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        bread.Children.Add(new TextBlock
        {
            Text = Loc.Tr("Settings", cat?.Label ?? _category),
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily(ThemeManager.Current.FontDisplay),
            Foreground = UiKit.Brush("NlOnSurfaceBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        _detailStack.Children.Add(bread);

        // Quick-jump category pill strip
        var jumpBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 10) };
        foreach (var c in Categories)
        {
            string cid = c.Id;
            jumpBar.Children.Add(UiKit.PillButton(Loc.Tr("Settings", c.Label), () => SelectCategory(cid), accent: cid == _category));
        }
        _detailStack.Children.Add(UiKit.HScroll(jumpBar));

        switch (_category)
        {
            case "appearance": RenderAppearance(prefs); break;
            case "general": RenderGeneral(prefs); break;
            case "devices": RenderDevices(prefs); break;
            case "usb": RenderUsbInstall(); break;
            case "av": RenderAudioVideo(prefs); break;
            case "notifications": RenderNotifications(prefs); break;
            case "files": RenderFilesAndSync(prefs); break;
            case "shortcuts": RenderShortcutsAndRules(prefs); break;
            case "agents": RenderAgentsAndApi(prefs); break;
            case "storage": RenderStorageAndPrivacy(prefs); break;
            case "about": RenderConnectionAndAbout(prefs); break;
        }
    }

    private void RenderHub(NectarLinkEngine engine, JsonObject prefs)
    {
        _detailStack.Children.Add(UiKit.PageTitle(Loc.Tr("Settings", "Settings")));

        // Windows Settings Hero Banner (Active Device left + Updates & Diagnostics right)
        var hero = new Grid { ColumnSpacing = 20, Margin = new Thickness(0, 4, 0, 16) };
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var curDev = engine.CurrentDevice;
        string curDevId = engine.CurrentDeviceId;
        var left = new Grid { ColumnSpacing = 14 };
        left.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        left.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var heroIcon = new FontIcon
        {
            Glyph = "\uE8EA",
            FontSize = 24,
            Foreground = UiKit.Brush("NlOnPrimaryContainerBrush")
        };
        var heroBox = new Border
        {
            Width = 86,
            Height = 56,
            CornerRadius = new CornerRadius(6),
            Background = UiKit.Brush("NlPrimaryContainerBrush"),
            Child = heroIcon
        };
        UiKit.ApplyDeviceWallpaperOrIcon(heroBox, heroIcon, curDev, 256);
        var heroBtn = new Button
        {
            Content = heroBox,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6)
        };
        ToolTipService.SetToolTip(heroBtn, curDev is not null
            ? Loc.Tr("Settings", "Click to fetch or set phone wallpaper")
            : Loc.Tr("Settings", "Pair a device"));
        heroBtn.Click += async (_, _) =>
        {
            if (!string.IsNullOrEmpty(curDevId))
                await AppDialogs.ShowPhoneWallpaperAsync(XamlRoot, curDevId);
            else
                await AppDialogs.ShowPairingAsync(XamlRoot);
        };
        left.Children.Add(heroBtn);

        var devMeta = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        if (curDev is not null)
        {
            string dname = curDev["name"]?.GetValue<string>() ?? "Phone";
            string model = curDev["model"]?.GetValue<string>() ?? "";
            string osVer = curDev["osVer"]?.GetValue<string>() ?? "";
            bool online = curDev["online"]?.GetValue<bool>() ?? false;
            string detail = !string.IsNullOrEmpty(model)
                ? $"{model} · Android {osVer}"
                : (online ? "Connected" : "Offline");
            devMeta.Children.Add(new TextBlock
            {
                Text = dname,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiKit.Brush("NlOnSurfaceBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            devMeta.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 12.5,
                Foreground = UiKit.Brush("NlOnSurfaceVariantBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            var linkRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
            var renameBtn = new HyperlinkButton
            {
                Content = Loc.Tr("Settings", "Manage device"),
                Padding = new Thickness(0, 2, 0, 0),
                FontSize = 12.5
            };
            renameBtn.Click += (_, _) => SelectCategory("devices");
            linkRow.Children.Add(renameBtn);

            var wpLink = new HyperlinkButton
            {
                Content = Loc.Tr("Settings", "Fetch wallpaper"),
                Padding = new Thickness(0, 2, 0, 0),
                FontSize = 12.5
            };
            wpLink.Click += async (_, _) => await AppDialogs.ShowPhoneWallpaperAsync(XamlRoot, curDevId);
            linkRow.Children.Add(wpLink);
            devMeta.Children.Add(linkRow);
        }
        else
        {
            devMeta.Children.Add(new TextBlock { Text = Loc.Tr("Settings", "No device paired"), FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = UiKit.Brush("NlOnSurfaceBrush") });
            devMeta.Children.Add(UiKit.MutedText(Loc.Tr("Settings", "Pair your Android phone over Wi-Fi or USB"), 12.5));
            var pairLink = new HyperlinkButton
            {
                Content = Loc.Tr("Settings", "Pair a device"),
                Padding = new Thickness(0, 2, 0, 0),
                FontSize = 12.5
            };
            pairLink.Click += async (_, _) => await AppDialogs.ShowPairingAsync(XamlRoot);
            devMeta.Children.Add(pairLink);
        }
        Grid.SetColumn(devMeta, 1);
        left.Children.Add(devMeta);
        hero.Children.Add(left);

        // Right quick status widget matching Windows Update block
        bool updateAvail = engine.App["updateAvailable"]?.GetValue<bool>() ?? false;
        string ver = engine.App["version"]?.GetValue<string>() ?? "0.1.5";
        var updBtn = new Button
        {
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 6, 10, 6),
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center
        };
        var updRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        updRow.Children.Add(new FontIcon
        {
            Glyph = updateAvail ? "\uE896" : "\uE895",
            FontSize = 18,
            Foreground = UiKit.Brush("NlPrimaryBrush")
        });
        var updMeta = new StackPanel { Spacing = 1 };
        updMeta.Children.Add(new TextBlock
        {
            Text = updateAvail ? Loc.Tr("Settings", "Update available") : $"NectarLink v{ver}",
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = UiKit.Brush("NlOnSurfaceBrush")
        });
        updMeta.Children.Add(UiKit.MutedText(
            updateAvail ? $"Version {engine.App["updateVersion"]?.GetValue<string>()}" : Loc.Tr("Settings", "Up to date · Check connection"),
            12));
        updRow.Children.Add(updMeta);
        updBtn.Content = updRow;
        updBtn.Click += async (_, _) => await AppDialogs.ShowDoctorAsync(XamlRoot);
        Grid.SetColumn(updBtn, 1);
        hero.Children.Add(updBtn);

        _detailStack.Children.Add(hero);

        foreach (var c in Categories)
        {
            string cid = c.Id;
            _detailStack.Children.Add(UiKit.SettingsCard(
                c.Glyph,
                Loc.Tr("Settings", c.Label),
                Loc.Tr("Settings", c.Subtitle),
                onClick: () => SelectCategory(cid)));
        }
        _ = prefs;
    }

    private void RenderAppearance(JsonObject prefs)
    {
        string theme = prefs["theme"]?.GetValue<string>() ?? "mica";
        string mode = prefs["colorMode"]?.GetValue<string>() ?? "system";
        int scale = prefs["uiScale"]?.GetValue<int>() ?? 100;

        _detailStack.Children.Add(UiKit.SettingsCard("\uE790", "Theme", "Windows 11 Mica or frosted Desktop Acrylic material",
            trailing: BuildExclusivePillRow(
                new[] { ("mica", "Mica"), ("acrylic", "Acrylic") },
                theme,
                id => SetPref("theme", id),
                () => NectarLinkEngine.Current.Prefs["theme"]?.GetValue<string>() ?? "mica",
                ignoreCase: true)));

        _detailStack.Children.Add(UiKit.SettingsCard("\uE793", "Color mode", "Match Windows or choose light or dark",
            trailing: BuildExclusivePillRow(
                new[] { ("system", "System"), ("light", "Light"), ("dark", "Dark") },
                mode,
                id => SetPref("colorMode", id),
                () => NectarLinkEngine.Current.Prefs["colorMode"]?.GetValue<string>() ?? "system")));

        _detailStack.Children.Add(UiKit.ToggleSettingCard("\uE708", "Pure black OLED dark surfaces", "Replaces Mica dark tint with solid black in dark mode", prefs["amoled"]?.GetValue<bool>() ?? false, v => SetPref("amoled", v)));
        _detailStack.Children.Add(UiKit.ToggleSettingCard("\uE81E", "Translucent material backdrop", "Show desktop Mica or Acrylic behind the window", prefs["backdrop"]?.GetValue<bool>() ?? true, v => SetPref("backdrop", v)));

        _detailStack.Children.Add(UiKit.SettingsCard("\uE8A9", "UI scale", "Restart may apply full DPI scaling",
            trailing: BuildExclusivePillRow(
                new[] { ("100", "100%"), ("110", "110%"), ("125", "125%"), ("150", "150%") },
                scale.ToString(CultureInfo.InvariantCulture),
                id => { if (int.TryParse(id, out int s)) SetPref("uiScale", s); },
                () => (NectarLinkEngine.Current.Prefs["uiScale"]?.GetValue<int>() ?? 100).ToString(CultureInfo.InvariantCulture))));

        var langBox = new ComboBox { MinWidth = 190 };
        string curLang = prefs["language"]?.GetValue<string>() ?? "";
        langBox.Items.Add(new ComboBoxItem { Content = "System default", Tag = "" });
        int selIdx = 0;
        if (prefs["languages"] is JsonArray langs)
        {
            for (int i = 0; i < langs.Count; i++)
            {
                string code = langs[i]?["code"]?.GetValue<string>() ?? "";
                string name = langs[i]?["name"]?.GetValue<string>() ?? code;
                langBox.Items.Add(new ComboBoxItem { Content = name, Tag = code });
                if (code == curLang) selIdx = i + 1;
            }
        }
        langBox.SelectedIndex = selIdx;
        langBox.SelectionChanged += (_, _) =>
        {
            if (langBox.SelectedItem is ComboBoxItem ci && ci.Tag is string code)
                SetPref("language", code);
        };
        _detailStack.Children.Add(UiKit.SettingsCard("\uF2B7", "Language", "Display language for menus and controls", trailing: langBox));
    }

    private void RenderGeneral(JsonObject prefs)
    {
        _detailStack.Children.Add(ToggleRow("Start NectarLink with Windows", prefs["startWithWindows"]?.GetValue<bool>() ?? false, v => SetPref("startWithWindows", v)));
        _detailStack.Children.Add(ToggleRow("Close window to system tray instead of quitting", prefs["closeToTray"]?.GetValue<bool>() ?? true, v => SetPref("closeToTray", v)));
        _detailStack.Children.Add(ToggleRow("Monochrome tray icon matching Windows taskbar", prefs["trayOneColour"]?.GetValue<bool>() ?? false, v => SetPref("trayOneColour", v)));
        _detailStack.Children.Add(ToggleRow("Add paired phones to Explorer Send To menu", prefs["sendToMenu"]?.GetValue<bool>() ?? true, v => SetPref("sendToMenu", v)));

        _detailStack.Children.Add(UiKit.SectionLabel("Nectar Island (Top HUD pill)"));
        _detailStack.Children.Add(ToggleRow("Enable Nectar Island floating status pill", prefs["nectarIsland"]?.GetValue<bool>() ?? false, v => SetPref("nectarIsland", v)));
        _detailStack.Children.Add(ToggleRow("Show calls on Island", prefs["nectarIslandCalls"]?.GetValue<bool>() ?? true, v => SetPref("nectarIslandCalls", v)));
        _detailStack.Children.Add(ToggleRow("Show transfers on Island", prefs["nectarIslandTransfers"]?.GetValue<bool>() ?? true, v => SetPref("nectarIslandTransfers", v)));
        _detailStack.Children.Add(ToggleRow("Show media playback on Island", prefs["nectarIslandMedia"]?.GetValue<bool>() ?? true, v => SetPref("nectarIslandMedia", v)));
        _detailStack.Children.Add(ToggleRow("Show notifications on Island", prefs["nectarIslandNotifications"]?.GetValue<bool>() ?? false, v => SetPref("nectarIslandNotifications", v)));

        string pos = prefs["nectarIslandPosition"]?.GetValue<string>() ?? "center";
        _detailStack.Children.Add(LabeledCard("Island position", BuildExclusivePillRow(
            new[] { ("left", "left"), ("center", "center"), ("right", "right") },
            pos,
            p => SetPref("nectarIslandPosition", p),
            () => NectarLinkEngine.Current.Prefs["nectarIslandPosition"]?.GetValue<string>() ?? "center")));

        _detailStack.Children.Add(UiKit.PillButton("Replay guided walkthrough", () =>
        {
            NectarLinkEngine.Current.Invoke("prefs.restartWalkthrough");
            (App.Current as App)?.MainWin?.NavigateTo("walkthrough");
        }));
    }

    private void RenderDevices(JsonObject prefs)
    {
        var engine = NectarLinkEngine.Current;
        _detailStack.Children.Add(UiKit.PillButton("Pair another device", async () => await AppDialogs.ShowPairingAsync(XamlRoot), glyph: "\uE710", accent: true));

        foreach (var item in engine.Devices)
        {
            if (item is not JsonObject d) continue;
            string did = d["id"]?.GetValue<string>() ?? "";
            string name = d["name"]?.GetValue<string>() ?? "Device";
            string rawName = d["rawName"]?.GetValue<string>() ?? "";
            bool online = d["online"]?.GetValue<bool>() ?? false;
            bool reconnect = d["reconnect"]?.GetValue<bool>() ?? true;

            var card = new StackPanel { Spacing = 10 };
            var hdr = new Grid();
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hdr.Children.Add(UiKit.TitleText($"{name} ({rawName})", 16));
            var badge = UiKit.StatusBadge(online ? "Online" : "Offline", highlight: online);
            Grid.SetColumn(badge, 1);
            hdr.Children.Add(badge);
            card.Children.Add(hdr);

            var nickRow = new Grid { ColumnSpacing = 8 };
            nickRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            nickRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var nickBox = new TextBox { PlaceholderText = "Custom nickname (blank for phone name)", Text = d["hasNickname"]?.GetValue<bool>() == true ? name : "" };
            var saveNick = UiKit.PillButton("Save name", () => engine.Invoke("device.setNickname", new { device = did, nickname = nickBox.Text ?? "" }));
            Grid.SetColumn(saveNick, 1);
            nickRow.Children.Add(nickBox);
            nickRow.Children.Add(saveNick);
            card.Children.Add(nickRow);

            card.Children.Add(ToggleRow("Reconnect automatically", reconnect, v => engine.Invoke("device.setReconnect", new { device = did, reconnect = v })));

            // Phone wallpaper preview & actions
            bool hasWp = !string.IsNullOrEmpty(d["wallpaperPath"]?.GetValue<string>());
            bool wpBusy = d["wallpaperFetchBusy"]?.GetValue<bool>() ?? false;
            var wpRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var wpThumbIcon = new FontIcon { Glyph = "\uE8EA", FontSize = 16, Foreground = UiKit.Brush("NlOnPrimaryContainerBrush") };
            var wpThumb = new Border { Width = 52, Height = 34, CornerRadius = new CornerRadius(5), Child = wpThumbIcon };
            UiKit.ApplyDeviceWallpaperOrIcon(wpThumb, wpThumbIcon, d, 128);
            wpRow.Children.Add(wpThumb);
            wpRow.Children.Add(UiKit.PillButton(
                wpBusy ? Loc.Tr("Device", "Fetching…") : Loc.Tr("Device", "Fetch / change"),
                async () => await AppDialogs.ShowPhoneWallpaperAsync(XamlRoot, did),
                glyph: "\uE91B"));
            if (hasWp)
                wpRow.Children.Add(UiKit.PillButton(Loc.Tr("Common", "Clear"), () => engine.Invoke("device.clearWallpaper", new { device = did })));
            card.Children.Add(UiKit.SettingsCard("\uE91B", Loc.Tr("Device", "Phone wallpaper"), Loc.Tr("Device", "Displayed on Home, sidebar avatar, and Settings"), trailing: wpRow));

            // Unlock with phone
            var unlockBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            bool unlockEnrolled = engine.App["deviceUnlockEnrolled"]?.GetValue<bool>() ?? false;
            unlockBtns.Children.Add(UiKit.PillButton(unlockEnrolled ? "Re-enroll Windows Unlock" : "Set up Windows Unlock", async () =>
            {
                var pw = new PasswordBox { Header = "Enter your Windows sign-in password (verified locally via Windows Logon)" };
                ContentDialog dlg = new() { Title = "Unlock with phone", PrimaryButtonText = "Enroll", CloseButtonText = "Cancel", XamlRoot = XamlRoot, Content = pw };
                if (await dlg.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrEmpty(pw.Password))
                    engine.Invoke("device.setupUnlock", new { device = did, password = pw.Password });
            }));
            if (unlockEnrolled)
                unlockBtns.Children.Add(UiKit.PillButton("Remove Unlock", () => engine.Invoke("device.removeUnlock", new { device = did })));
            unlockBtns.Children.Add(UiKit.PillButton("Unpair", () => engine.Invoke("device.unpair", new { device = did })));
            card.Children.Add(HScroll(unlockBtns));

            _detailStack.Children.Add(UiKit.Card(card));
        }

        // Per-device capability toggles for selected phone
        if (engine.App["deviceToggles"] is JsonArray toggles && !string.IsNullOrEmpty(engine.CurrentDeviceId))
        {
            _detailStack.Children.Add(UiKit.SectionLabel("Feature permissions for current device"));
            foreach (var t in toggles)
            {
                if (t is not JsonObject obj) continue;
                string key = obj["name"]?.GetValue<string>() ?? "";
                string label = obj["label"]?.GetValue<string>() ?? FormatFeatureLabel(key);
                bool enabled = obj["on"]?.GetValue<bool>() ?? false;
                if (string.IsNullOrEmpty(key)) continue;
                _detailStack.Children.Add(ToggleRow(label, enabled, v =>
                    engine.Invoke("device.setToggle", new { device = engine.CurrentDeviceId, name = key, on = v })));
            }
        }
    }

    private void RenderUsbInstall()
    {
        var engine = NectarLinkEngine.Current;
        var pi = engine.PhoneInstall;
        _detailStack.Children.Add(ToggleRow("Enable Android USB debugging integration (restart takes effect)", engine.Prefs["usbDebugging"]?.GetValue<bool>() ?? true, v => SetPref("usbDebugging", v)));

        string toolsKind = pi["tools"]?["kind"]?.GetValue<string>() ?? "unknown";
        var toolsStack = new StackPanel { Spacing = 8 };
        toolsStack.Children.Add(UiKit.BodyText($"ADB platform-tools status: {toolsKind}"));
        if (toolsKind is "missing" or "failed")
            toolsStack.Children.Add(UiKit.PillButton("Download platform-tools", () => engine.Invoke("phoneInstall.getTools"), accent: true));
        _detailStack.Children.Add(UiKit.Card(toolsStack));

        if (pi["phones"] is JsonArray phones && phones.Count > 0)
        {
            foreach (var p in phones)
            {
                if (p is not JsonObject ph) continue;
                string serial = ph["serial"]?.GetValue<string>() ?? "";
                string pname = ph["name"]?.GetValue<string>() ?? serial;
                string installed = ph["installed"]?.GetValue<string>() ?? "";
                var card = new StackPanel { Spacing = 6 };
                card.Children.Add(UiKit.BodyText($"{pname} ({serial}) · {(string.IsNullOrEmpty(installed) ? "Not installed" : $"v{installed}")}"));
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(UiKit.PillButton("Install / Update", () => engine.Invoke("phoneInstall.install", new { serial }), accent: true));
                row.Children.Add(UiKit.PillButton("Replace build", () => engine.Invoke("phoneInstall.replace", new { serial })));
                row.Children.Add(UiKit.PillButton("Open on phone", () => engine.Invoke("phoneInstall.openOnPhone", new { serial })));
                card.Children.Add(HScroll(row));
                _detailStack.Children.Add(UiKit.Card(card));
            }
        }
        else
        {
            _detailStack.Children.Add(UiKit.MutedText("Plug your Android phone in with a USB cable and enable USB debugging in Developer options."));
        }
    }

    private void RenderAudioVideo(JsonObject prefs)
    {
        var engine = NectarLinkEngine.Current;
        var cam = engine.Webcam;
        var mic = engine.PhoneMic;

        // Webcam section
        var camCard = new StackPanel { Spacing = 10 };
        camCard.Children.Add(UiKit.SectionLabel("Phone as Windows Webcam"));
        bool registered = cam["addonRegistered"]?.GetValue<bool>() ?? false;
        string phase = cam["phase"]?.GetValue<string>() ?? "idle";
        string statusTxt = cam["statusText"]?.GetValue<string>() ?? "";

        var camBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        camBtns.Children.Add(UiKit.PillButton(
            registered ? "Uninstall virtual camera driver" : "Register NectarLink Virtual Camera",
            () => engine.Invoke(registered ? "webcam.removeAddon" : "webcam.setupAddon")));
        camBtns.Children.Add(UiKit.PillButton(
            phase is "streaming" or "asking" ? "Stop webcam" : "Start webcam",
            () => engine.Invoke(phase is "streaming" or "asking" ? "webcam.stop" : "webcam.start", new { device = engine.CurrentDeviceId }),
            accent: true));
        camCard.Children.Add(HScroll(camBtns));
        if (!string.IsNullOrEmpty(statusTxt)) camCard.Children.Add(UiKit.MutedText(statusTxt));

        var lensRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        string lens = cam["camera"]?.GetValue<string>() ?? "back";
        lensRow.Children.Add(UiKit.PillButton("Back camera", () => engine.Invoke("webcam.setCamera", new { camera = "back" }), accent: lens == "back"));
        lensRow.Children.Add(UiKit.PillButton("Front camera", () => engine.Invoke("webcam.setCamera", new { camera = "front" }), accent: lens == "front"));
        int h = cam["height"]?.GetValue<int>() ?? 720;
        foreach (int resH in new[] { 720, 1080, 2160 })
            lensRow.Children.Add(UiKit.PillButton($"{resH}p", () => engine.Invoke("webcam.setResolution", new { height = resH }), accent: h == resH));
        camCard.Children.Add(HScroll(lensRow));
        DetachPreviewFromParent();
        camCard.Children.Add(_webcamPreview);
        _webcamPreviewParent = camCard;
        _detailStack.Children.Add(UiKit.Card(camCard));

        // Phone Mic section
        var micCard = new StackPanel { Spacing = 10 };
        micCard.Children.Add(UiKit.SectionLabel("Phone as Microphone"));
        string mphase = mic["phase"]?.GetValue<string>() ?? "off";
        micCard.Children.Add(UiKit.PillButton(
            mphase == "off" ? "Start phone microphone" : $"Stop phone microphone ({mphase})",
            () => engine.Invoke(mphase == "off" ? "phoneMic.start" : "phoneMic.stop", new { device = engine.CurrentDeviceId }),
            accent: mphase == "live"));
        int gain = Math.Clamp(mic["gain"]?.GetValue<int>() ?? 150, 50, 400);
        var gainSlider = new Slider { Header = $"Mic gain ({gain}%)", Minimum = 50, Maximum = 400, Value = gain };
        void CommitGain()
        {
            if (!_draggingMicGain) return;
            _draggingMicGain = false;
            engine.Invoke("phoneMic.setGain", new { gain = (int)gainSlider.Value });
        }
        gainSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => _draggingMicGain = true), handledEventsToo: true);
        gainSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => CommitGain()), handledEventsToo: true);
        gainSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => CommitGain()), handledEventsToo: true);
        micCard.Children.Add(gainSlider);
        _detailStack.Children.Add(UiKit.Card(micCard));

        // Mirror & Audio streaming prefs
        int fps = prefs["mirrorFps"]?.GetValue<int>() ?? 60;
        _detailStack.Children.Add(LabeledCard("Mirror frame rate", BuildExclusivePillRow(
            new[] { ("30", "30 fps"), ("60", "60 fps"), ("90", "90 fps"), ("120", "120 fps") },
            fps.ToString(CultureInfo.InvariantCulture),
            id => { if (int.TryParse(id, out int f)) SetPref("mirrorFps", f); },
            () => (NectarLinkEngine.Current.Prefs["mirrorFps"]?.GetValue<int>() ?? 60).ToString(CultureInfo.InvariantCulture))));

        string mq = prefs["mirrorQuality"]?.GetValue<string>() ?? "";
        _detailStack.Children.Add(LabeledCard("Mirror stream quality", BuildExclusivePillRow(
            new[] { ("", "Balanced"), ("sharper", "Sharper"), ("smoother", "Smoother") },
            mq,
            k => SetPref("mirrorQuality", k),
            () => NectarLinkEngine.Current.Prefs["mirrorQuality"]?.GetValue<string>() ?? "")));

        _detailStack.Children.Add(ToggleRow("Stream PC audio to phone", prefs["streamPcAudioToPhone"]?.GetValue<bool>() ?? false, v => SetPref("streamPcAudioToPhone", v)));
        _detailStack.Children.Add(ToggleRow("Pause PC media automatically during phone calls", prefs["pauseMediaOnCall"]?.GetValue<bool>() ?? true, v => SetPref("pauseMediaOnCall", v)));
        _detailStack.Children.Add(ToggleRow("Mute PC speakers while phone watches Play on Phone", prefs["muteWhileWatched"]?.GetValue<bool>() ?? true, v => SetPref("muteWhileWatched", v)));
    }

    private void RenderNotifications(JsonObject prefs)
    {
        var engine = NectarLinkEngine.Current;
        _detailStack.Children.Add(ToggleRow("Auto-copy one-time verification codes (OTP)", prefs["autoCopyOtp"]?.GetValue<bool>() ?? false, v => SetPref("autoCopyOtp", v)));
        _detailStack.Children.Add(ToggleRow("Sync Do Not Disturb with phone", prefs["syncDnd"]?.GetValue<bool>() ?? false, v => SetPref("syncDnd", v)));
        _detailStack.Children.Add(ToggleRow("Suppress pop-ups while Windows is locked", prefs["quietWhenLocked"]?.GetValue<bool>() ?? false, v => SetPref("quietWhenLocked", v)));
        _detailStack.Children.Add(ToggleRow("Hold notifications on phone while PC is locked", prefs["holdWhenLocked"]?.GetValue<bool>() ?? false, v => SetPref("holdWhenLocked", v)));
        _detailStack.Children.Add(ToggleRow("Forward Windows toast notifications to paired phones", prefs["forwardPcNotificationsToPhone"]?.GetValue<bool>() ?? false, v => SetPref("forwardPcNotificationsToPhone", v)));
        _detailStack.Children.Add(ToggleRow("Keep paused state across restarts", prefs["keepPopupsPaused"]?.GetValue<bool>() ?? false, v => SetPref("keepPopupsPaused", v)));

        _detailStack.Children.Add(UiKit.SectionLabel("Per-app notification rules"));
        if (engine.Notifications["apps"] is JsonArray apps && apps.Count > 0)
        {
            foreach (var item in apps)
            {
                if (item is not JsonObject a) continue;
                string pkg = a["app"]?.GetValue<string>() ?? "";
                string name = a["name"]?.GetValue<string>() ?? pkg;
                string rule = a["rule"]?.GetValue<string>() ?? "show";
                var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                foreach (var r in new[] { "show", "quiet", "hidden" })
                    btns.Children.Add(UiKit.PillButton(r, () => engine.Invoke("notifications.setAppRule", new { app = pkg, rule = r }), accent: rule == r));
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(UiKit.BodyText(name));
                Grid.SetColumn(btns, 1);
                row.Children.Add(btns);
                _detailStack.Children.Add(UiKit.Card(row, new Thickness(12, 8, 12, 8)));
            }
        }
        else _detailStack.Children.Add(UiKit.MutedText("No phone apps have posted notifications yet."));
    }

    private void RenderFilesAndSync(JsonObject prefs)
    {
        var engine = NectarLinkEngine.Current;
        _detailStack.Children.Add(ToggleRow("Auto-send Windows clipboard changes to phone", prefs["autoClipboard"]?.GetValue<bool>() ?? true, v => SetPref("autoClipboard", v)));
        _detailStack.Children.Add(ToggleRow("Keep encrypted local clipboard history (50 items)", prefs["clipboardHistory"]?.GetValue<bool>() ?? true, v => SetPref("clipboardHistory", v)));
        _detailStack.Children.Add(ToggleRow("Suggest smart actions on copied links, maps, OTPs", prefs["suggestClipboardActions"]?.GetValue<bool>() ?? true, v => SetPref("suggestClipboardActions", v)));
        _detailStack.Children.Add(ToggleRow("Place small received files automatically on clipboard", prefs["clipboardAutoCopyReceivedFiles"]?.GetValue<bool>() ?? true, v => SetPref("clipboardAutoCopyReceivedFiles", v)));
        _detailStack.Children.Add(ToggleRow("Open single shared files upon arrival", prefs["openSharedFiles"]?.GetValue<bool>() ?? false, v => SetPref("openSharedFiles", v)));
        _detailStack.Children.Add(ToggleRow("Pause folder sync & photo backup over cellular relay", prefs["syncWifiOnly"]?.GetValue<bool>() ?? false, v => SetPref("syncWifiOnly", v)));
        _detailStack.Children.Add(ToggleRow("Expose PC Libraries to phone (Share PC storage)", prefs["sharePcStorage"]?.GetValue<bool>() ?? false, v => SetPref("sharePcStorage", v)));
        _detailStack.Children.Add(ToggleRow("LocalSend LAN interop discovery & receive", engine.LocalSend["enabled"]?.GetValue<bool>() ?? false, v => SetPref("localsendEnabled", v)));

        // Recordings format
        string recFmt = prefs["recordingsFormat"]?.GetValue<string>() ?? "m4a";
        _detailStack.Children.Add(LabeledCard("Voice recording format", BuildExclusivePillRow(
            new[] { ("m4a", "M4A"), ("mp3", "MP3"), ("wav", "WAV"), ("flac", "FLAC") },
            recFmt,
            fmt => SetPref("recordingsFormat", fmt),
            () => NectarLinkEngine.Current.Prefs["recordingsFormat"]?.GetValue<string>() ?? "m4a")));

        var folders = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        folders.Children.Add(UiKit.PillButton("Open Downloads folder", () => engine.Invoke("app.openDownloadsFolder")));
        folders.Children.Add(UiKit.PillButton("Open Recordings folder", () => engine.Invoke("app.openRecordingsFolder")));
        _detailStack.Children.Add(HScroll(folders));

        // Photo Backup per phone
        _detailStack.Children.Add(UiKit.SectionLabel("Photo Backup"));
        if (engine.PhotoBackup["phones"] is JsonArray pbPhones && pbPhones.Count > 0)
        {
            foreach (var p in pbPhones)
            {
                if (p is not JsonObject obj) continue;
                string pid = obj["id"]?.GetValue<string>() ?? "";
                string pname = obj["name"]?.GetValue<string>() ?? "Phone";
                bool en = obj["enabled"]?.GetValue<bool>() ?? false;
                bool vid = obj["videos"]?.GetValue<bool>() ?? false;
                string status = obj["status"]?.GetValue<string>() ?? "";
                var st = new StackPanel { Spacing = 6 };
                st.Children.Add(UiKit.BodyText($"{pname} — {status}"));
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(UiKit.PillButton(en ? "Turn off" : "Turn on", () => engine.Invoke("photoBackup.setEnabled", new { device = pid, enabled = !en }), accent: en));
                row.Children.Add(UiKit.PillButton(vid ? "Videos: On" : "Videos: Off", () => engine.Invoke("photoBackup.setVideos", new { device = pid, videos = !vid }), accent: vid));
                row.Children.Add(UiKit.PillButton("Back up now", () => engine.Invoke("photoBackup.backUpNow", new { device = pid })));
                row.Children.Add(UiKit.PillButton("Open folder", () => engine.Invoke("photoBackup.openFolder", new { device = pid })));
                st.Children.Add(HScroll(row));
                _detailStack.Children.Add(UiKit.Card(st));
            }
        }
    }

    private void RenderShortcutsAndRules(JsonObject prefs)
    {
        var engine = NectarLinkEngine.Current;
        _detailStack.Children.Add(ToggleRow("Enter sends text in Mirror windows (Shift/Ctrl+Enter newline)", prefs["enterSends"]?.GetValue<bool>() ?? false, v => SetPref("enterSends", v)));
        _detailStack.Children.Add(ToggleRow("Right-click sends Long Press instead of Back", prefs["rightClickLongPress"]?.GetValue<bool>() ?? false, v => SetPref("rightClickLongPress", v)));
        _detailStack.Children.Add(ToggleRow("Hide floating Mirror toolbar by default", prefs["hideMirrorToolbar"]?.GetValue<bool>() ?? false, v => SetPref("hideMirrorToolbar", v)));
        _detailStack.Children.Add(ToggleRow("Follow clipboard when switching mouse between PC & phone", prefs["flowFollowClipboard"]?.GetValue<bool>() ?? true, v => SetPref("flowFollowClipboard", v)));

        string[] hkKeys = ["commandPaletteHotkey", "shelfHotkey", "continuityPhotoHotkey", "continuityScanHotkey"];
        string[] hkLabels = ["Command Palette hotkey", "Edge Shelf hotkey", "Take Photo hotkey", "Scan Document hotkey"];
        for (int i = 0; i < hkKeys.Length; i++)
        {
            string k = hkKeys[i];
            var box = new TextBox { Header = hkLabels[i], Text = prefs[k]?.GetValue<string>() ?? "" };
            var save = UiKit.PillButton("Apply", () => SetPref(k, box.Text?.Trim() ?? ""));
            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(box);
            Grid.SetColumn(save, 1);
            grid.Children.Add(save);
            _detailStack.Children.Add(UiKit.Card(grid));
        }

        _detailStack.Children.Add(UiKit.SectionLabel("Automation rules"));
        if (prefs["automationRules"] is JsonArray rules)
        {
            foreach (var r in rules)
            {
                if (r is not JsonObject obj) continue;
                string rid = obj["id"]?.GetValue<string>() ?? "";
                string rname = obj["name"]?.GetValue<string>() ?? "";
                bool ren = obj["enabled"]?.GetValue<bool>() ?? true;
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(UiKit.BodyText(rname));
                row.Children.Add(UiKit.PillButton(ren ? "Enabled" : "Disabled", () => engine.Invoke("prefs.setAutomationRuleEnabled", new { ruleId = rid, enabled = !ren }), accent: ren));
                row.Children.Add(UiKit.IconButton("\uE74D", "Delete", () => engine.Invoke("prefs.removeAutomationRule", new { ruleId = rid })));
                _detailStack.Children.Add(UiKit.Card(row));
            }
        }
    }

    private void RenderAgentsAndApi(JsonObject prefs)
    {
        var engine = NectarLinkEngine.Current;
        var ag = engine.Agents;

        _detailStack.Children.Add(ToggleRow("Allow AI agents (MCP stdio, CLI, localhost API v2)", prefs["agentAccessEnabled"]?.GetValue<bool>() ?? false, v => SetPref("agentAccessEnabled", v)));
        _detailStack.Children.Add(ToggleRow("Global Read-Only lock across all AI agents", prefs["agentReadOnly"]?.GetValue<bool>() ?? false, v => SetPref("agentReadOnly", v)));
        _detailStack.Children.Add(ToggleRow($"Enable Localhost Automation API v1 (port {prefs["localApiPort"]?.GetValue<int>() ?? 53320})", prefs["localApiEnabled"]?.GetValue<bool>() ?? false, v => SetPref("localApiEnabled", v)));
        _detailStack.Children.Add(ToggleRow("Allow v1 API to read private notifications & messages", prefs["localApiPrivate"]?.GetValue<bool>() ?? false, v => SetPref("localApiPrivate", v)));

        _detailStack.Children.Add(UiKit.PillButton("Add AI agent client", async () =>
        {
            var labelBox = new TextBox { Header = "Safe label (letters, digits, hyphens)", PlaceholderText = "claude-desktop" };
            var nameBox = new TextBox { Header = "Display name", PlaceholderText = "Claude Desktop" };
            ContentDialog dlg = new() { Title = "Add AI agent", PrimaryButtonText = "Create", CloseButtonText = "Cancel", XamlRoot = XamlRoot, Content = new StackPanel { Spacing = 8, Children = { labelBox, nameBox } } };
            if (await dlg.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(labelBox.Text))
                engine.Invoke("agents.add", new { label = labelBox.Text.Trim(), name = nameBox.Text?.Trim() ?? "", kind = "mcp", preset = "readOnly" });
        }, glyph: "\uE710", accent: true));

        if (ag["clients"] is JsonArray clients)
        {
            foreach (var c in clients)
            {
                if (c is not JsonObject cl) continue;
                string cid = cl["id"]?.GetValue<string>() ?? "";
                string cname = cl["name"]?.GetValue<string>() ?? cid;
                bool paused = cl["paused"]?.GetValue<bool>() ?? false;
                bool ro = cl["readOnly"]?.GetValue<bool>() ?? false;
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(UiKit.BodyText($"{cname} ({cl["grants"]?.GetValue<string>()})"));
                row.Children.Add(UiKit.PillButton(paused ? "Paused" : "Active", () => engine.Invoke("agents.setPaused", new { id = cid, paused = !paused }), accent: !paused));
                row.Children.Add(UiKit.PillButton(ro ? "Read-only" : "Full grants", () => engine.Invoke("agents.setReadOnly", new { id = cid, readOnly = !ro })));
                row.Children.Add(UiKit.IconButton("\uE74D", "Remove", () => engine.Invoke("agents.remove", new { id = cid })));
                _detailStack.Children.Add(UiKit.Card(row));
            }
        }
    }

    private void RenderStorageAndPrivacy(JsonObject prefs)
    {
        var engine = NectarLinkEngine.Current;

        var dr = engine.App["dataRetentionSummary"];
        if (dr is JsonObject obj)
        {
            var stack = new StackPanel { Spacing = 6 };
            stack.Children.Add(UiKit.BodyText($"Data directory: {prefs["dataDir"]?.GetValue<string>()}"));
            stack.Children.Add(UiKit.MutedText($"Cached photo thumbs: {obj["photoThumbFiles"]?.GetValue<int>() ?? 0} files ({(obj["photoThumbBytes"]?.GetValue<long>() ?? 0) / 1024} KB)"));
            stack.Children.Add(UiKit.MutedText($"Cached message threads: {obj["cachedSmsThreads"]?.GetValue<int>() ?? 0} | Chat threads: {obj["chatThreads"]?.GetValue<int>() ?? 0}"));
            _detailStack.Children.Add(UiKit.Card(stack));
        }

        var clearRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        clearRow.Children.Add(UiKit.PillButton("Clear photo thumbs", () => engine.Invoke("storage.clearPhotoThumbs")));
        clearRow.Children.Add(UiKit.PillButton("Clear notification cache", () => engine.Invoke("storage.clearNotificationCache")));
        clearRow.Children.Add(UiKit.PillButton("Clear message cache", () => engine.Invoke("storage.clearMessageCache")));
        clearRow.Children.Add(UiKit.PillButton("Clear notification history", () => engine.Invoke("storage.clearNotificationHistory")));
        _detailStack.Children.Add(HScroll(clearRow));
        _detailStack.Children.Add(UiKit.PillButton("Open NectarLink data folder", () => engine.Invoke("app.openDataDir")));
    }

    private void RenderConnectionAndAbout(JsonObject prefs)
    {
        var engine = NectarLinkEngine.Current;
        _detailStack.Children.Add(UiKit.Card(UiKit.BodyText($"NectarLink WinUI 3 · Version {engine.App["version"]?.GetValue<string>() ?? "0.1.5"}")));

        _detailStack.Children.Add(ToggleRow("Stay connected over mobile data relay when away from home", prefs["awayMode"]?.GetValue<bool>() ?? false, v => SetPref("awayMode", v)));
        _detailStack.Children.Add(ToggleRow("Check for updates automatically", prefs["autoUpdate"]?.GetValue<bool>() ?? true, v => SetPref("autoUpdate", v)));
        _detailStack.Children.Add(ToggleRow("Install downloaded updates automatically", prefs["installUpdates"]?.GetValue<bool>() ?? true, v => SetPref("installUpdates", v)));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(UiKit.PillButton("Connection Doctor", async () => await AppDialogs.ShowDoctorAsync(XamlRoot), glyph: "\uE9D9", accent: true));
        actions.Children.Add(UiKit.PillButton("Relay diagnostics", async () => await AppDialogs.ShowRelaysAsync(XamlRoot), glyph: "\uE704"));
        actions.Children.Add(UiKit.PillButton("Check for updates", () => engine.Invoke("updates.check", new { told = true })));
        actions.Children.Add(UiKit.PillButton("Report a problem", async () => await AppDialogs.ShowReportAsync(XamlRoot)));
        actions.Children.Add(UiKit.PillButton("Open logs folder", () => engine.Invoke("app.openLogsDir")));
        _detailStack.Children.Add(HScroll(actions));
    }

    private void SetPref(string key, object value)
    {
        _selfPrefSuppressUntilTick = key == "language" ? 0 : Environment.TickCount64 + 700;
        var dict = new Dictionary<string, object> { [key] = value };
        NectarLinkEngine.Current.Invoke("prefs.set", dict);
    }

    private StackPanel BuildExclusivePillRow(
        (string Id, string Label)[] options,
        string initialSelected,
        Action<string> onSelect,
        Func<string> getSelected,
        bool ignoreCase = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var map = new List<(string Id, Button Btn)>(options.Length);
        var cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        foreach (var (id, lbl) in options)
        {
            string optId = id;
            var btn = UiKit.PillButton(lbl, () =>
            {
                foreach (var (k, b) in map) UiKit.SetPillAccent(b, string.Equals(k, optId, cmp));
                onSelect(optId);
            }, accent: string.Equals(initialSelected, optId, cmp));
            map.Add((optId, btn));
            row.Children.Add(btn);
        }

        _inPlaceSyncs.Add(() =>
        {
            string cur = getSelected();
            foreach (var (k, b) in map) UiKit.SetPillAccent(b, string.Equals(k, cur, cmp));
        });
        return row;
    }

    private static ScrollViewer HScroll(UIElement content) => new()
    {
        HorizontalScrollMode = ScrollMode.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollMode = ScrollMode.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Content = content
    };

    private static string FormatFeatureLabel(string key) => key switch
    {
        "notifications" => "Notifications",
        "messages" => "Messages",
        "calls" => "Calls",
        "call_audio" => "Call audio",
        "phone_sound" => "Phone audio",
        "clipboard" => "Clipboard sync",
        "files" => "File transfer",
        "storage" => "Shared storage",
        "mirror" => "Screen & app mirror",
        "webcam" => "Phone webcam",
        "phone_mic" => "Phone microphone",
        "photo_backup" => "Photo backup",
        "continuity" => "Continuity camera",
        "device_toggles" => "Quick settings toggles",
        "find" => "Find phone",
        _ => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(key.Replace('_', ' '))
    };

    private static UIElement LabeledCard(string title, UIElement inner)
        => UiKit.Card(new StackPanel { Spacing = 8, Children = { UiKit.SectionLabel(title), inner } });

    private static UIElement ToggleRow(string label, bool value, Action<bool> onChanged)
        => UiKit.ToggleSettingCard("\uE713", label, null, value, onChanged);
}
