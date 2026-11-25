// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NectarLink.WinUI.Controls;
using NectarLink.WinUI.Dialogs;
using NectarLink.WinUI.Services;

namespace NectarLink.WinUI.Pages;

/// <summary>
/// Seven-step guided first-run walkthrough mirroring WalkthroughPage.qml:
/// 1. Welcome, 2. Get the phone app (APK QR + USB install shortcut), 3. Pair,
/// 4. Permissions, 5. Appearance &amp; Theme picker, 6. Key features, 7. Ready.
/// </summary>
public sealed partial class WalkthroughPage : Page, IRefreshablePage
{
    private readonly StackPanel _stepHeader = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly StackPanel _body = new() { Spacing = 16, MaxWidth = 680 };
    private readonly Button _backBtn;
    private readonly Button _nextBtn;
    private int _step;

    public WalkthroughPage()
    {
        _backBtn = UiKit.PillButton(Loc.Tr("Walkthrough", "Back"), () => SetStep(_step - 1));
        _nextBtn = UiKit.PillButton(Loc.Tr("Walkthrough", "Next"), OnNext, accent: true);

        var root = new Grid { Margin = new Thickness(32, 24, 32, 24), RowSpacing = 18 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(_stepHeader, 0);
        root.Children.Add(_stepHeader);

        var scroll = new ScrollViewer
        {
            Content = new Grid { Children = { _body } }
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var skipBtn = UiKit.PillButton(Loc.Tr("Walkthrough", "Skip setup"), Finish);
        footer.Children.Add(skipBtn);

        var rightBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        rightBtns.Children.Add(_backBtn);
        rightBtns.Children.Add(_nextBtn);
        Grid.SetColumn(rightBtns, 2);
        footer.Children.Add(rightBtns);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;
    }

    public void OnNavigatedTo()
    {
        _step = Math.Clamp(NectarLinkEngine.Current.Prefs["walkthroughStep"]?.GetValue<int>() ?? 0, 0, 6);
        Render();
    }

    public void OnEngineChanged(uint mask)
    {
        if (_step == 2) Render();
    }

    private void SetStep(int s)
    {
        _step = Math.Clamp(s, 0, 6);
        NectarLinkEngine.Current.Invoke("prefs.set", new { walkthroughStep = _step });
        Render();
    }

    private void OnNext()
    {
        if (_step >= 6) Finish();
        else SetStep(_step + 1);
    }

    private void Finish()
    {
        NectarLinkEngine.Current.Invoke("prefs.set", new { walkthroughCompleted = true, walkthroughStep = 6 });
        string target = NectarLinkEngine.Current.Devices.Count > 0 ? "home" : "welcome";
        (App.Current as App)?.MainWin?.NavigateTo(target);
    }

    private void Render()
    {
        _stepHeader.Children.Clear();
        for (int i = 0; i < 7; i++)
        {
            bool active = i == _step;
            bool done = i < _step;
            var dot = new Border
            {
                Height = 8,
                Width = active ? 28 : 10,
                CornerRadius = new CornerRadius(4),
                Background = UiKit.Brush(active ? "NlPrimaryBrush" : done ? "NlPrimaryContainerBrush" : "NlSurfaceContainerHighestBrush")
            };
            int idx = i;
            dot.PointerPressed += (_, _) => SetStep(idx);
            _stepHeader.Children.Add(dot);
        }
        _stepHeader.Children.Add(UiKit.MutedText(Loc.Tr("Walkthrough", "Step %1 of 7", _step + 1)));

        _backBtn.IsEnabled = _step > 0;
        _nextBtn.Content = _step == 6 ? Loc.Tr("Walkthrough", "Finish") : Loc.Tr("Walkthrough", "Next");

        _body.Children.Clear();
        switch (_step)
        {
            case 0:
                if (UiKit.LoadLocalImage("ms-appx:///Assets/brand/icon-512.png", 144) is { } welcomeLogo)
                {
                    _body.Children.Add(new Image
                    {
                        Source = welcomeLogo,
                        Width = 72,
                        Height = 72,
                        HorizontalAlignment = HorizontalAlignment.Left
                    });
                }
                _body.Children.Add(UiKit.TitleText(Loc.Tr("Walkthrough", "Welcome to NectarLink"), 28));
                _body.Children.Add(UiKit.BodyText(Loc.Tr(
                    "Walkthrough",
                    "NectarLink bridges your Android phone and Windows PC directly over your local Wi-Fi or USB cable — encrypted end-to-end with zero accounts or cloud servers.")));
                _body.Children.Add(UiKit.Card(UiKit.MutedText(Loc.Tr(
                    "Walkthrough",
                    "You're running the native WinUI 3 edition backed directly by the NectarLink Rust QUIC core."))));
                break;

            case 1:
                _body.Children.Add(UiKit.TitleText(Loc.Tr("Walkthrough", "Get NectarLink on your phone"), 26));
                _body.Children.Add(UiKit.BodyText(Loc.Tr(
                    "Walkthrough",
                    "Scan this QR code with your phone camera to download the Android APK, or plug in a USB cable to install straight from this PC.")));
                var app = NectarLinkEngine.Current.App;
                _body.Children.Add(UiKit.QrCodeView(
                    app["apkQrPath"]?.GetValue<string>(),
                    app["apkQrSize"]?.GetValue<int>() ?? 0,
                    164));
                _body.Children.Add(UiKit.PillButton(
                    Loc.Tr("Walkthrough", "Install over USB instead"),
                    () => (App.Current as App)?.MainWin?.NavigateTo("settings"),
                    glyph: "\uE88E"));
                break;

            case 2:
                _body.Children.Add(UiKit.TitleText(Loc.Tr("Walkthrough", "Pair your phone"), 26));
                var devices = NectarLinkEngine.Current.Devices;
                if (devices.Count > 0)
                {
                    foreach (var item in devices)
                    {
                        if (item is JsonObject d)
                            _body.Children.Add(UiKit.Card(UiKit.BodyText(
                                $"✓ {d["name"]?.GetValue<string>() ?? "Phone"} paired")));
                    }
                }
                else
                {
                    _body.Children.Add(UiKit.MutedText(Loc.Tr("Walkthrough", "Make sure both devices are on the same Wi-Fi network, or connected via USB.")));
                }
                _body.Children.Add(UiKit.PillButton(
                    Loc.Tr("Walkthrough", "Open Pair dialog"),
                    async () => await AppDialogs.ShowPairingAsync(XamlRoot),
                    glyph: "\uE710",
                    accent: true));
                break;

            case 3:
                _body.Children.Add(UiKit.TitleText(Loc.Tr("Walkthrough", "Android permissions"), 26));
                _body.Children.Add(UiKit.BodyText(Loc.Tr(
                    "Walkthrough",
                    "Everything in NectarLink is opt-in. Grant permissions on your phone only for the features you want:")));
                string[] perms =
                [
                    Loc.Tr("Walkthrough", "• Notification access — Mirror notifications and reply inline from Windows."),
                    Loc.Tr("Walkthrough", "• SMS & Contacts — Read and send texts and view caller names."),
                    Loc.Tr("Walkthrough", "• Photos & Media — Browse camera roll, drag-out photos, and sync folders."),
                    Loc.Tr("Walkthrough", "• Camera & Microphone — Phone Webcam, Continuity Camera, and Phone Mic.")
                ];
                foreach (var p in perms) _body.Children.Add(UiKit.Card(UiKit.BodyText(p, 13.5)));
                break;

            case 4:
                _body.Children.Add(UiKit.TitleText(Loc.Tr("Walkthrough", "Choose your look"), 26));
                var themesPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                foreach (var (id, label) in new[] { ("mica", "Mica"), ("acrylic", "Acrylic") })
                {
                    bool selected = ThemeManager.Current.ThemeName == id;
                    themesPanel.Children.Add(UiKit.PillButton(label, () =>
                    {
                        NectarLinkEngine.Current.Invoke("prefs.set", new { theme = id });
                        Render();
                    }, accent: selected));
                }
                _body.Children.Add(new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Content = themesPanel });
                var modePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                foreach (var (id, label) in new[] { ("system", "System"), ("light", "Light"), ("dark", "Dark") })
                {
                    bool selected = ThemeManager.Current.ColorMode == id;
                    modePanel.Children.Add(UiKit.PillButton(label, () =>
                    {
                        NectarLinkEngine.Current.Invoke("prefs.set", new { colorMode = id });
                        Render();
                    }, accent: selected));
                }
                _body.Children.Add(modePanel);
                break;

            case 5:
                _body.Children.Add(UiKit.TitleText(Loc.Tr("Walkthrough", "Highlights"), 26));
                _body.Children.Add(UiKit.Card(UiKit.BodyText(Loc.Tr("Walkthrough", "• Drop files anywhere on NectarLink to send them to your phone."))));
                _body.Children.Add(UiKit.Card(UiKit.BodyText(Loc.Tr("Walkthrough", "• Press Ctrl+Alt+Space anytime for the Command Palette."))));
                _body.Children.Add(UiKit.Card(UiKit.BodyText(Loc.Tr("Walkthrough", "• Press Ctrl+Alt+S or toggle the Edge Shelf for quick access to recent photos & clips."))));
                break;

            case 6:
                _body.Children.Add(UiKit.TitleText(Loc.Tr("Walkthrough", "You're all set"), 28));
                _body.Children.Add(UiKit.BodyText(Loc.Tr("Walkthrough", "Click Finish to start using NectarLink. You can replay this guide anytime from Settings.")));
                break;
        }
    }
}
