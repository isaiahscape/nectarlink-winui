// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NectarLink.WinUI.Controls;
using NectarLink.WinUI.Services;

namespace NectarLink.WinUI.Pages;

/// <summary>
/// Live Capabilities matrix &amp; Handbook matching FeaturesGuidePage.qml: shows every
/// NectarLink feature's live readiness state from Rust's <c>CapabilityMatrix</c>,
/// lets users jump directly to any feature ("Open"), and opens detailed Guide docs.
/// </summary>
public sealed partial class FeaturesGuidePage : Page, IRefreshablePage
{
    private readonly TextBox _filterBox = new() { PlaceholderText = "Search features & guides…", Width = 260 };
    private readonly StackPanel _listPanel = new() { Spacing = 4 };

    private sealed record FeatureEntry(string Glyph, string Title, string FeatureKey, string TargetPage, string Description);

    private static readonly FeatureEntry[] Entries =
    [
        new("\uEA8F", "Notification feed & inline reply", "notifications.feed", "home", "Mirror phone notifications and reply inline from Windows."),
        new("\uE8BD", "SMS, MMS & messaging chats", "messages.sms", "messages", "Read/send SMS & MMS with attachment support and dual-SIM switching."),
        new("\uE717", "Calls, dialer & PC Call Audio", "messages.calls", "calls", "View call log, dial numbers, and route call audio through Windows speakers and mic."),
        new("\uE91B", "Photos & Camera roll", "photos.gallery", "photos", "Browse albums, inspect full-resolution photos, save/share/delete, and drag straight to Explorer."),
        new("\uE8C8", "Clipboard sync & encrypted history", "clipboard.sync", "home", "Two-way clipboard sync with smart action chips and DPAPI-encrypted history."),
        new("\uE898", "File transfers & send queue", "files.transfer", "home", "Fast QUIC file/folder transfer over Wi-Fi, USB, or relay with automatic offline queue."),
        new("\uE7F4", "Screen mirroring & audio", "mirror.screen", "apps", "60-120fps low-latency phone mirroring, MP4 recording, stay-awake, and physical screen-off."),
        new("\uE74C", "App windows (Elevated)", "mirror.apps", "apps", "Launch individual Android apps inside resizable Windows windows."),
        new("\uE722", "Phone as Webcam", "camera.webcam", "settings:av", "Stream your phone's front/back camera into Zoom, Teams, or OBS via NectarLink Virtual Camera."),
        new("\uE8FE", "Continuity Camera", "camera.continuity", "home", "Trigger Capture Photo or Scan Document from Windows and auto-paste straight into your active app."),
        new("\uE720", "Phone as Microphone", "mic.phone", "settings:av", "Use your phone microphone as a live low-latency audio source on this PC."),
        new("\uE8FD", "Stream & PC Deck", "deck.controls", "deck", "Customize button pages on your phone to trigger shortcuts, OBS actions, URLs, or scripts."),
    ];

    public FeaturesGuidePage()
    {
        _filterBox.TextChanged += (_, _) => Render();

        var root = new Grid { Padding = new Thickness(14, 12, 24, 22), RowSpacing = 12 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var hdr = new Grid();
        hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hdr.Children.Add(UiKit.PageTitle(Loc.Tr("Guide", "Features & guide")));
        _filterBox.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_filterBox, 1);
        hdr.Children.Add(_filterBox);
        root.Children.Add(hdr);

        var docsBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (doc, title) in new[] { ("overview", "Overview"), ("setup", "Setup"), ("permissions", "Permissions"), ("elevated", "Elevated & Assist"), ("troubleshooting", "Troubleshooting"), ("privacy", "Privacy & security") })
        {
            docsBar.Children.Add(UiKit.PillButton(title, () => NectarLinkEngine.Current.Invoke("app.openGuideDoc", new { doc })));
        }
        var docsScroll = UiKit.HScroll(docsBar);
        Grid.SetRow(docsScroll, 1);
        root.Children.Add(docsScroll);

        var scroll = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _listPanel
        };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);
        Content = root;
    }

    private string _lastSig = string.Empty;

    public void OnNavigatedTo() => Render();
    public void OnEngineChanged(uint mask)
    {
        if (EngineChanges.HasAny(mask, EngineChanges.Devices | EngineChanges.Capabilities | EngineChanges.Status | EngineChanges.Ringing))
            Render();
    }

    private void Render(bool force = false)
    {
        string q = (_filterBox.Text ?? "").Trim();
        var engine = NectarLinkEngine.Current;
        string sig = $"{engine.Devices.Count}|{q}|{engine.App["featureMatrix"]?.ToJsonString()}";
        if (!force && sig == _lastSig) return;
        _lastSig = sig;

        _listPanel.Children.Clear();

        foreach (var e in Entries)
        {
            if (!string.IsNullOrEmpty(q) && !e.Title.Contains(q, StringComparison.OrdinalIgnoreCase) && !e.Description.Contains(q, StringComparison.OrdinalIgnoreCase))
                continue;

            string state = engine.Devices.Count == 0 ? "no_device" : engine.FeatureState(e.FeatureKey);
            string reason = engine.FeatureReason(e.FeatureKey);
            string subtitle = (!string.IsNullOrEmpty(reason) && state != "available")
                ? $"{e.Description} · {reason}"
                : e.Description;

            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(UiKit.StatusBadge(
                state == "available" ? "Ready" : state == "no_device" ? "Needs phone" : state,
                highlight: state == "available",
                warning: state is "locked" or "off"));
            right.Children.Add(UiKit.PillButton("Open", () => (App.Current as App)?.MainWin?.NavigateTo(e.TargetPage)));

            _listPanel.Children.Add(UiKit.SettingsCard(e.Glyph, e.Title, subtitle, trailing: right));
        }
    }
}
