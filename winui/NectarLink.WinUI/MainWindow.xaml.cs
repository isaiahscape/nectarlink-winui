// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NectarLink.WinUI.Controls;
using NectarLink.WinUI.Dialogs;
using NectarLink.WinUI.Pages;
using NectarLink.WinUI.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace NectarLink.WinUI;

public sealed partial class MainWindow : Window
{
    private sealed record NavDef(string Id, string Label, string Glyph, bool RequiresPhone, bool Bottom);

    private static readonly NavDef[] NavItems =
    [
        new("home", "Home", "\uE80F", false, false),
        new("messages", "Messages", "\uE8BD", true, false),
        new("calls", "Calls", "\uE717", true, false),
        new("photos", "Photos", "\uE91B", true, false),
        new("apps", "Apps", "\uE74C", true, false),
        new("deck", "Deck", "\uE8FD", false, false),
        new("timeline", "Timeline", "\uE81C", false, false),
        new("guide", "Guide", "\uE897", false, true),
        new("settings", "Settings", "\uE713", false, true),
    ];

    private readonly Dictionary<string, Button> _railButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Border> _railIndicators = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _toastTimer = new();
    private string _activePageId = "home";
    private bool _forceClosing;
    private string? _shownGuideDocKey;
    private string? _shownAgentConfirmId;

    // Cached page instances so scroll/input state stays intact across navigation
    private readonly Dictionary<string, Page> _pageCache = new(StringComparer.Ordinal);
    private string _appliedBackdropKey = "";
    private string? _lastProfileWallpaperKey;
    private string _lastRailSig = "";
    private string _lastAlertSig = "";

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarGrid);

        var appWin = WindowChrome.GetAppWindow(this);
        WindowChrome.ApplyAppIcon(this);
        if (UiKit.LoadLocalImage("ms-appx:///Assets/brand/icon-512.png", 64) is { } titleLogo)
            TitleBarLogo.Source = titleLogo;
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            appWin.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        }
        TitleBarGrid.Loaded += (_, _) => SyncTitleBarMetrics(appWin);
        appWin.Changed += (_, args) =>
        {
            if (args.DidSizeChange || args.DidPresenterChange)
                SyncTitleBarMetrics(appWin);
        };
        appWin.Resize(new Windows.Graphics.SizeInt32(1140, 760));
        if (appWin.Presenter is OverlappedPresenter op)
        {
            op.PreferredMinimumWidth = 880;
            op.PreferredMinimumHeight = 600;
        }
        appWin.Closing += OnAppWindowClosing;

        BuildRail();

        _toastTimer.Interval = TimeSpan.FromSeconds(4);
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastBanner.Visibility = Visibility.Collapsed;
        };

        var engine = NectarLinkEngine.Current;
        engine.StateChanged += OnEngineStateChanged;
        engine.ToastRequested += ShowToast;
        engine.OpenDoctorRequested += () => _ = AppDialogs.ShowDoctorAsync(RootGrid.XamlRoot);
        engine.OpenFilePickerRequested += async () => await PickAndSendFilesAsync(handoff: false);
        engine.OpenHandoffPickerRequested += async () => await PickAndSendFilesAsync(handoff: true);
        engine.ReportRequested += () => _ = AppDialogs.ShowReportAsync(RootGrid.XamlRoot);
        engine.CommandPaletteRequested += OpenCommandPalette;

        ThemeManager.Current.ThemeUpdated += ApplyThemeToWindow;
        ApplyThemeToWindow();
        SyncUiFromEngine();
        NavigateToInitialPage();

        // Register Ctrl+K in-window shortcut for Command Palette
        var accel = new KeyboardAccelerator { Key = VirtualKey.K, Modifiers = VirtualKeyModifiers.Control };
        accel.Invoked += (_, e) => { OpenCommandPalette(); e.Handled = true; };
        RootGrid.KeyboardAccelerators.Add(accel);
    }

    private void SyncTitleBarMetrics(AppWindow appWin)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported() || TitleBarGrid.XamlRoot is null) return;
        double scale = Math.Max(1.0, TitleBarGrid.XamlRoot.RasterizationScale);
        double rightInset = appWin.TitleBar.RightInset / scale;
        if (rightInset > 0)
            TitleBarRightPaddingCol.Width = new GridLength(rightInset);
        double titleH = appWin.TitleBar.Height / scale;
        if (titleH >= 32 && RootGrid.RowDefinitions.Count > 0)
            RootGrid.RowDefinitions[0].Height = new GridLength(titleH);
    }

    public void RestoreAndActivate()
    {
        WindowChrome.BringToFront(this);
    }

    public void ForceClose()
    {
        _forceClosing = true;
        Close();
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        bool closeToTray = NectarLinkEngine.Current.Prefs["closeToTray"]?.GetValue<bool>() ?? true;
        if (!_forceClosing && closeToTray)
        {
            args.Cancel = true;
            sender.Hide();
            NectarLinkEngine.Current.Invoke("app.releaseIdleHomeSummary");
        }
    }

    private void ApplyThemeToWindow()
    {
        RootGrid.RequestedTheme = ThemeManager.Current.Dark ? ElementTheme.Dark : ElementTheme.Light;
        WindowChrome.SetDarkTitleBar(this, ThemeManager.Current.Dark);

        string targetBackdropKey = ThemeManager.Current.ActiveBackdropKey;
        if (targetBackdropKey != _appliedBackdropKey)
        {
            _appliedBackdropKey = targetBackdropKey;
            SystemBackdrop = ThemeManager.Current.CreateWindowBackdrop();
        }

        // Let native Mica / Desktop Acrylic show through RootGrid only when a compositor backdrop is active
        RootGrid.Background = _appliedBackdropKey != "none"
            ? new SolidColorBrush(Microsoft.UI.Colors.Transparent)
            : UiKit.Brush("NlSurfaceBrush");
        TitleBarGrid.RequestedTheme = RootGrid.RequestedTheme;
        UpdateRailSelection(force: true);
    }

    private void BuildRail()
    {
        RailTopItems.Children.Clear();
        RailBottomItems.Children.Clear();
        _railButtons.Clear();
        _railIndicators.Clear();

        foreach (var item in NavItems)
        {
            var indicator = new Border
            {
                Width = 3,
                Height = 16,
                CornerRadius = new CornerRadius(2),
                Background = UiKit.Brush("NlPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                CenterPoint = new System.Numerics.Vector3(1.5f, 8f, 0f),
                Opacity = 0
            };
            Motion.ConfigureTransitions(indicator, opacityMs: 140, translationMs: 140, scaleMs: 160);

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(indicator);
            row.Children.Add(new FontIcon { Glyph = item.Glyph, FontSize = 15.5 });
            row.Children.Add(new TextBlock
            {
                Text = Loc.Tr("Nav", item.Label),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center
            });

            var btn = new Button
            {
                Content = row,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Height = 36,
                Padding = new Thickness(5, 0, 12, 0),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0)
            };
            Motion.AttachPressFeedback(btn, 0.98f);
            btn.Click += (_, _) => NavigateTo(item.Id);
            _railButtons[item.Id] = btn;
            _railIndicators[item.Id] = indicator;
            if (item.Bottom) RailBottomItems.Children.Add(btn);
            else RailTopItems.Children.Add(btn);
        }

        // "+ Pair device" row in footer
        var pairRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        pairRow.Children.Add(new Border { Width = 3, Height = 16, Opacity = 0 });
        pairRow.Children.Add(new FontIcon { Glyph = "\uE710", FontSize = 15.5, Foreground = UiKit.Brush("NlPrimaryBrush") });
        pairRow.Children.Add(new TextBlock
        {
            Text = Loc.Tr("Nav", "Pair a device"),
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center
        });
        var pairBtn = new Button
        {
            Content = pairRow,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Height = 36,
            Padding = new Thickness(5, 0, 12, 0),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Foreground = UiKit.Brush("NlOnSurfaceBrush"),
            BorderThickness = new Thickness(0)
        };
        pairBtn.Click += async (_, _) => await AppDialogs.ShowPairingAsync(RootGrid.XamlRoot);
        RailBottomItems.Children.Insert(0, pairBtn);
    }

    private void NavigateToInitialPage()
    {
        var engine = NectarLinkEngine.Current;
        bool walkthroughDone = engine.Prefs["walkthroughCompleted"]?.GetValue<bool>() ?? false;
        if (!walkthroughDone && engine.Devices.Count == 0)
            NavigateTo("walkthrough");
        else if (engine.Devices.Count == 0)
            NavigateTo("welcome");
        else
            NavigateTo("home");
    }

    public void NavigateTo(string pageId)
    {
        if (string.IsNullOrEmpty(pageId)) pageId = "home";
        string? settingsSub = null;
        if (pageId.StartsWith("settings:", StringComparison.OrdinalIgnoreCase))
        {
            settingsSub = pageId["settings:".Length..];
            pageId = "settings";
        }

        string prevPageId = _activePageId;
        bool pageChanged = !string.Equals(prevPageId, pageId, StringComparison.Ordinal) || PageHost.Content is null;
        _activePageId = pageId;
        NectarLinkEngine.Current.Invoke("app.setCurrentPage", new { page = pageId });

        if (!_pageCache.TryGetValue(pageId, out var page))
        {
            page = pageId switch
            {
                "welcome" => new WelcomePage(),
                "walkthrough" => new WalkthroughPage(),
                "home" => new HomePage(),
                "messages" => new MessagesPage(),
                "calls" => new CallsPage(),
                "photos" => new PhotosPage(),
                "apps" => new AppsPage(),
                "deck" => new DeckPage(),
                "timeline" => new TimelinePage(),
                "guide" => new FeaturesGuidePage(),
                "settings" => new SettingsPage(),
                _ => new HomePage()
            };
            _pageCache[pageId] = page;
        }
        if (page is SettingsPage sp && !string.IsNullOrEmpty(settingsSub))
        {
            sp.OpenCategory(settingsSub);
        }
        PageHost.Content = page;
        if (pageChanged)
        {
            int prevIdx = Array.FindIndex(NavItems, n => n.Id == prevPageId);
            int nextIdx = Array.FindIndex(NavItems, n => n.Id == pageId);
            float dirY = (prevIdx >= 0 && nextIdx >= 0 && nextIdx < prevIdx) ? -14f : 14f;
            Motion.PlayEntrance(PageHost, offsetX: 0f, offsetY: dirY, durationMs: 200);
        }
        UpdateRailSelection();
        if (page is IRefreshablePage rp) rp.OnNavigatedTo();
    }

    private void UpdateRailSelection(bool force = false)
    {
        bool hasDevices = NectarLinkEngine.Current.Devices.Count > 0;
        string railSig = $"{_activePageId}|{hasDevices}";
        if (!force && railSig == _lastRailSig) return;
        _lastRailSig = railSig;

        foreach (var item in NavItems)
        {
            bool selected = item.Id == _activePageId;
            if (_railButtons.TryGetValue(item.Id, out var btn))
            {
                btn.IsEnabled = !item.RequiresPhone || hasDevices;
                btn.Background = selected
                    ? UiKit.Brush("NlSurfaceContainerHighBrush")
                    : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                btn.Foreground = UiKit.Brush(selected ? "NlOnSurfaceBrush" : "NlOnSurfaceVariantBrush");
            }
            if (_railIndicators.TryGetValue(item.Id, out var ind))
            {
                ind.Background = UiKit.Brush("NlPrimaryBrush");
                bool wasVisible = ind.Opacity > 0.5;
                ind.Opacity = selected ? 1.0 : 0.0;
                if (selected && !wasVisible && Motion.Enabled)
                {
                    ind.Scale = new System.Numerics.Vector3(1f, 0.35f, 1f);
                    ind.DispatcherQueue?.TryEnqueue(() => ind.Scale = System.Numerics.Vector3.One);
                }
            }
        }
    }

    private void OnEngineStateChanged(uint mask)
    {
        SyncUiFromEngine();
        if (PageHost.Content is IRefreshablePage rp) rp.OnEngineChanged(mask);
    }

    private void SyncUiFromEngine()
    {
        var engine = NectarLinkEngine.Current;
        var app = engine.App;

        // Device profile block in left sidebar
        var curDev = engine.CurrentDevice;
        string wpPath = curDev?["wallpaperPath"]?.GetValue<string>() ?? "";
        long wpMtime = (!string.IsNullOrEmpty(wpPath) && System.IO.File.Exists(wpPath))
            ? System.IO.File.GetLastWriteTimeUtc(wpPath).Ticks
            : 0;
        string wpKey = $"{engine.CurrentDeviceId}|{wpPath}|{wpMtime}";
        if (curDev is not null)
        {
            string name = curDev["name"]?.GetValue<string>() ?? "Device";
            bool online = curDev["online"]?.GetValue<bool>() ?? false;
            int bat = curDev["battery"]?.GetValue<int>() ?? -1;
            string path = curDev["path"]?.GetValue<string>() ?? "";
            string conn = online ? (path == "usb" ? "USB" : path == "relay" ? "Relay" : "Wi-Fi") : "Offline";
            DeviceProfileTitle.Text = name;
            DeviceProfileSubtitle.Text = bat >= 0 ? $"{conn} · {bat}%" : conn;
        }
        else
        {
            DeviceProfileTitle.Text = Loc.Tr("App", "No phone paired");
            DeviceProfileSubtitle.Text = Loc.Tr("App", "Pair a device");
        }
        if (wpKey != _lastProfileWallpaperKey)
        {
            _lastProfileWallpaperKey = wpKey;
            UiKit.ApplyDeviceWallpaperOrIcon(DeviceProfileAvatarBorder, DeviceProfileAvatarIcon, curDev, 128);
        }

        // Core error badge
        string coreStatus = app["coreStatus"]?.GetValue<string>() ?? "starting";
        if (coreStatus == "error")
        {
            OfflineBadge.Visibility = Visibility.Visible;
            OfflineBadgeText.Text = app["coreError"]?.GetValue<string>() ?? "Error";
        }
        else if (coreStatus == "starting")
        {
            OfflineBadge.Visibility = Visibility.Visible;
            OfflineBadgeText.Text = Loc.Tr("App", "Starting…");
        }
        else
        {
            OfflineBadge.Visibility = Visibility.Collapsed;
        }

        PausePopupsBtn.IsChecked = app["popupsPaused"]?.GetValue<bool>() ?? false;

        // Redirect from welcome page if a phone just paired
        if (_activePageId == "welcome" && engine.Devices.Count > 0)
            NavigateTo("home");

        UpdateRailSelection();
        RenderAlertBanners();
        RenderCommandPaletteIfOpen();

        // Modal prompts triggered by core
        bool guideOpen = app["guideDocOpen"]?.GetValue<bool>() ?? false;
        string guideTitle = app["guideDocTitle"]?.GetValue<string>() ?? "";
        string guideBody = app["guideDocBody"]?.GetValue<string>() ?? "";
        if (guideOpen && _shownGuideDocKey != guideTitle && RootGrid.XamlRoot is not null)
        {
            _shownGuideDocKey = guideTitle;
            _ = AppDialogs.ShowGuideDocAsync(RootGrid.XamlRoot, guideTitle, guideBody).ContinueWith(_ => _shownGuideDocKey = null);
        }

        if (engine.Agents["confirmation"] is JsonObject conf && RootGrid.XamlRoot is not null)
        {
            string cid = conf["id"]?.GetValue<string>() ?? "";
            if (!string.IsNullOrEmpty(cid) && _shownAgentConfirmId != cid)
            {
                _shownAgentConfirmId = cid;
                _ = AppDialogs.ShowAgentConfirmAsync(RootGrid.XamlRoot, conf).ContinueWith(_ => _shownAgentConfirmId = null);
            }
        }
    }

    private void RenderAlertBanners()
    {
        var engine = NectarLinkEngine.Current;
        var app = engine.App;
        string alertSig = $"{engine.Devices.ToJsonString()}|{app["remotePromptOpen"]?.ToJsonString()}|{app["pcMirrorPromptOpen"]?.ToJsonString()}|{app["pcMirrorActive"]?.ToJsonString()}|{app["continuityPromptOpen"]?.ToJsonString()}|{app["updateAvailable"]?.ToJsonString()}|{app["updateBusy"]?.ToJsonString()}|{app["updateStatus"]?.ToJsonString()}";
        if (alertSig == _lastAlertSig) return;
        _lastAlertSig = alertSig;

        AlertBannerStack.Children.Clear();

        // Find My PC ringing
        foreach (var item in engine.Devices)
        {
            if (item is JsonObject d && (d["ringing"]?.GetValue<bool>() ?? false))
            {
                string dname = d["name"]?.GetValue<string>() ?? "Your phone";
                AlertBannerStack.Children.Add(BuildBanner(
                    Loc.Tr("App", "%1 is ringing this PC", dname),
                    Loc.Tr("App", "Stop ringing"),
                    () => engine.Invoke("device.stopRingingPc")));
            }
        }

        // Remote input approval request
        if (app["remotePromptOpen"]?.GetValue<bool>() ?? false)
        {
            string pname = app["remotePromptName"]?.GetValue<string>() ?? "Your phone";
            AlertBannerStack.Children.Add(BuildBanner(
                Loc.Tr("App", "%1 asked to control your PC mouse and keyboard.", pname),
                Loc.Tr("Common", "Allow"),
                () => engine.Invoke("device.answerRemotePrompt", new { allow = true }),
                Loc.Tr("Common", "Deny"),
                () => engine.Invoke("device.answerRemotePrompt", new { allow = false })));
        }

        // Reverse PC Screen Mirror request
        if (app["pcMirrorPromptOpen"]?.GetValue<bool>() ?? false)
        {
            string pname = app["pcMirrorPromptName"]?.GetValue<string>() ?? "Your phone";
            AlertBannerStack.Children.Add(BuildBanner(
                Loc.Tr("App", "%1 wants to view this PC's screen.", pname),
                Loc.Tr("Common", "Allow"),
                () => engine.Invoke("device.answerPcMirrorPrompt", new { allow = true }),
                Loc.Tr("Common", "Deny"),
                () => engine.Invoke("device.answerPcMirrorPrompt", new { allow = false })));
        }

        // Active PC Mirror indicator
        if (app["pcMirrorActive"]?.GetValue<bool>() ?? false)
        {
            string pname = app["pcMirrorDeviceName"]?.GetValue<string>() ?? "phone";
            AlertBannerStack.Children.Add(BuildBanner(
                Loc.Tr("App", "Sharing your PC screen to %1", pname),
                Loc.Tr("App", "Stop sharing"),
                () => engine.Invoke("device.stopPcMirror")));
        }

        // Continuity Camera missing target fallback paste
        if (app["continuityPromptOpen"]?.GetValue<bool>() ?? false)
        {
            string title = app["continuityPromptTitle"]?.GetValue<string>() ?? "";
            string body = app["continuityPromptBody"]?.GetValue<string>() ?? "";
            AlertBannerStack.Children.Add(BuildBanner(
                $"{title} — {body}",
                Loc.Tr("Continuity", "Paste into previous window"),
                () => engine.Invoke("continuity.pasteFallback"),
                Loc.Tr("Common", "Dismiss"),
                () => engine.Invoke("continuity.dismissFallback")));
        }

        // Available update banner
        if (app["updateAvailable"]?.GetValue<bool>() ?? false)
        {
            string ver = app["updateVersion"]?.GetValue<string>() ?? "";
            bool busy = app["updateBusy"]?.GetValue<bool>() ?? false;
            string status = app["updateStatus"]?.GetValue<string>() ?? "";
            AlertBannerStack.Children.Add(BuildBanner(
                busy ? status : Loc.Tr("Updates", "NectarLink %1 is available", ver),
                busy ? Loc.Tr("Updates", "Updating…") : Loc.Tr("Updates", "Install & restart"),
                () => { if (!busy) engine.Invoke("updates.install"); },
                Loc.Tr("WhatsNew", "What's new"),
                () => _ = AppDialogs.ShowWhatsNewAsync(RootGrid.XamlRoot)));
        }

        AlertBannerStack.Visibility = AlertBannerStack.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Border BuildBanner(
        string message,
        string primaryLabel,
        Action onPrimary,
        string? secondaryLabel = null,
        Action? onSecondary = null)
    {
        var g = new Grid { ColumnSpacing = 10 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new TextBlock
        {
            Text = message,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = UiKit.Brush("NlOnPrimaryContainerBrush"),
            TextWrapping = TextWrapping.Wrap
        });
        var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        btns.Children.Add(UiKit.PillButton(primaryLabel, onPrimary, accent: true));
        if (!string.IsNullOrEmpty(secondaryLabel) && onSecondary is not null)
            btns.Children.Add(UiKit.PillButton(secondaryLabel, onSecondary));
        Grid.SetColumn(btns, 1);
        g.Children.Add(btns);
        return new Border
        {
            Background = UiKit.Brush("NlPrimaryContainerBrush"),
            BorderThickness = new Thickness(1),
            BorderBrush = UiKit.Brush("NlCardStrokeBrush"),
            CornerRadius = ThemeManager.Current.RadiusSm,
            Padding = new Thickness(14, 10, 14, 10),
            Child = g
        };
    }

    private void ShowToast(string message, string actionLabel)
    {
        ToastMessage.Text = message;
        if (!string.IsNullOrEmpty(actionLabel))
        {
            ToastActionButton.Content = actionLabel;
            ToastActionButton.Visibility = Visibility.Visible;
        }
        else
        {
            ToastActionButton.Visibility = Visibility.Collapsed;
        }
        ToastBanner.Visibility = Visibility.Visible;
        Motion.PlayEntrance(ToastBanner, offsetX: 0f, offsetY: 16f, durationMs: 180);
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void OnToastActionClick(object sender, RoutedEventArgs e)
    {
        ToastBanner.Visibility = Visibility.Collapsed;
        NectarLinkEngine.Current.Invoke("clipboard.runSuggestion");
    }

    private void OnDeviceProfileClick(object sender, RoutedEventArgs e)
    {
        var engine = NectarLinkEngine.Current;
        if (engine.Devices.Count == 0)
        {
            _ = AppDialogs.ShowPairingAsync(RootGrid.XamlRoot);
            return;
        }
        var flyout = new MenuFlyout();
        int curIdx = engine.App["currentDevice"]?.GetValue<int>() ?? 0;
        for (int i = 0; i < engine.Devices.Count; i++)
        {
            if (engine.Devices[i] is not JsonObject d) continue;
            int idx = i;
            string name = d["name"]?.GetValue<string>() ?? "Device";
            bool online = d["online"]?.GetValue<bool>() ?? false;
            int bat = d["battery"]?.GetValue<int>() ?? -1;
            string sub = online ? (bat >= 0 ? $" · {bat}%" : " · Online") : " · Offline";
            var item = new ToggleMenuFlyoutItem
            {
                Text = name + sub,
                IsChecked = idx == curIdx
            };
            item.Click += (_, _) => engine.Invoke("app.setCurrentDevice", new { index = idx });
            flyout.Items.Add(item);
        }
        flyout.Items.Add(new MenuFlyoutSeparator());
        if (!string.IsNullOrEmpty(engine.CurrentDeviceId))
        {
            var wpItem = new MenuFlyoutItem
            {
                Text = Loc.Tr("Device", "Phone wallpaper…"),
                Icon = new FontIcon { Glyph = "\uE91B" }
            };
            wpItem.Click += async (_, _) => await AppDialogs.ShowPhoneWallpaperAsync(RootGrid.XamlRoot, engine.CurrentDeviceId);
            flyout.Items.Add(wpItem);
        }
        var pairItem = new MenuFlyoutItem
        {
            Text = Loc.Tr("Nav", "Pair another device"),
            Icon = new FontIcon { Glyph = "\uE710" }
        };
        pairItem.Click += async (_, _) => await AppDialogs.ShowPairingAsync(RootGrid.XamlRoot);
        flyout.Items.Add(pairItem);
        flyout.ShowAt(DeviceProfileBtn);
    }

    private void OnTogglePausePopups(object sender, RoutedEventArgs e)
    {
        bool paused = PausePopupsBtn.IsChecked == true;
        NectarLinkEngine.Current.Invoke("app.setPausePopups", new { paused });
    }

    public void OpenCommandPalette()
    {
        RestoreAndActivate();
        NectarLinkEngine.Current.Invoke("palette.open");
        PaletteScrim.Visibility = Visibility.Visible;
        if (PaletteScrim.Child is UIElement paletteCard)
            Motion.PlayEntrance(paletteCard, offsetX: 0f, offsetY: -12f, durationMs: 170);
        PaletteSearchBox.Text = "";
        PaletteSearchBox.Focus(FocusState.Programmatic);
        NectarLinkEngine.Current.Invoke("palette.noteShown");
    }

    private void RenderCommandPaletteIfOpen()
    {
        var app = NectarLinkEngine.Current.App;
        bool open = app["commandPaletteOpen"]?.GetValue<bool>() ?? false;
        PaletteScrim.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (!open) return;

        PaletteResultsPanel.Children.Clear();
        if (app["commandPaletteResults"] is JsonArray results)
        {
            foreach (var r in results)
            {
                if (r is not JsonObject item) continue;
                string id = item["id"]?.GetValue<string>() ?? "";
                string title = item["title"]?.GetValue<string>() ?? "";
                string sub = item["subtitle"]?.GetValue<string>() ?? "";
                var row = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(12, 8, 12, 8),
                    CornerRadius = new CornerRadius(10),
                    Content = new StackPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Foreground = UiKit.Brush("NlOnSurfaceBrush") },
                            new TextBlock { Text = sub, FontSize = 12, Foreground = UiKit.Brush("NlOnSurfaceVariantBrush") }
                        }
                    }
                };
                row.Click += (_, _) =>
                {
                    PaletteScrim.Visibility = Visibility.Collapsed;
                    NectarLinkEngine.Current.Invoke("palette.run", new { id });
                };
                PaletteResultsPanel.Children.Add(row);
            }
        }
    }

    private void OnOpenCommandPaletteClick(object sender, RoutedEventArgs e) => OpenCommandPalette();

    private void OnPaletteScrimPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, PaletteScrim))
            NectarLinkEngine.Current.Invoke("palette.close");
    }

    private void OnPaletteQueryChanged(object sender, TextChangedEventArgs e)
    {
        NectarLinkEngine.Current.Invoke("palette.search", new { query = PaletteSearchBox.Text ?? "" });
    }

    private void OnPaletteKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            NectarLinkEngine.Current.Invoke("palette.close");
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter && PaletteResultsPanel.Children.Count > 0 && PaletteResultsPanel.Children[0] is Button firstBtn)
        {
            var results = NectarLinkEngine.Current.App["commandPaletteResults"] as JsonArray;
            string? firstId = results is { Count: > 0 } ? results[0]?["id"]?.GetValue<string>() : null;
            if (!string.IsNullOrEmpty(firstId))
            {
                PaletteScrim.Visibility = Visibility.Collapsed;
                NectarLinkEngine.Current.Invoke("palette.run", new { id = firstId });
            }
            e.Handled = true;
        }
    }

    private void OnRootDragOver(object sender, DragEventArgs e)
    {
        if (NectarLinkEngine.Current.Devices.Count == 0) return;
        if (e.DataView.Contains(StandardDataFormats.StorageItems)
            || e.DataView.Contains(StandardDataFormats.Text)
            || e.DataView.Contains(StandardDataFormats.WebLink))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            string name = NectarLinkEngine.Current.CurrentDevice?["name"]?.GetValue<string>() ?? "your phone";
            DropOverlayText.Text = Loc.Tr("App", "Drop to send to %1", name);
            DropOverlay.Visibility = Visibility.Visible;
        }
    }

    private void OnRootDragLeave(object sender, DragEventArgs e) => DropOverlay.Visibility = Visibility.Collapsed;

    private async void OnRootDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        string devId = NectarLinkEngine.Current.CurrentDeviceId;
        if (string.IsNullOrEmpty(devId)) return;

        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = new List<string>();
            foreach (var it in items)
            {
                if (!string.IsNullOrEmpty(it.Path)) paths.Add(it.Path);
            }
            if (paths.Count > 0)
                NectarLinkEngine.Current.Invoke("transfers.send", new { device = devId, paths });
        }
        else if (e.DataView.Contains(StandardDataFormats.WebLink))
        {
            var uri = await e.DataView.GetWebLinkAsync();
            if (uri is not null)
                NectarLinkEngine.Current.Invoke("clipboard.sendLink", new { device = devId, url = uri.ToString() });
        }
        else if (e.DataView.Contains(StandardDataFormats.Text))
        {
            string text = await e.DataView.GetTextAsync();
            if (!string.IsNullOrEmpty(text))
                NectarLinkEngine.Current.Invoke("shelf.drop", new { text });
        }
    }

    public async System.Threading.Tasks.Task PickAndSendFilesAsync(bool handoff)
    {
        string devId = NectarLinkEngine.Current.CurrentDeviceId;
        if (string.IsNullOrEmpty(devId)) return;
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowChrome.GetHwnd(this));
        var files = await picker.PickMultipleFilesAsync();
        if (files is { Count: > 0 })
        {
            var paths = new List<string>(files.Count);
            foreach (var f in files) paths.Add(f.Path);
            NectarLinkEngine.Current.Invoke(handoff ? "transfers.sendHandoff" : "transfers.send", new { device = devId, paths });
        }
    }
}

public interface IRefreshablePage
{
    void OnNavigatedTo();
    void OnEngineChanged(uint mask);
}
