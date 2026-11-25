// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NectarLink.WinUI.Controls;
using NectarLink.WinUI.Services;

namespace NectarLink.WinUI.Pages;

/// <summary>
/// Phone Apps catalog matching AppsPage.qml: launch Android apps in their own
/// virtual-display windows, pin up to 24 favorites, create desktop launch shortcuts,
/// or start full screen mirroring when individual app windows are unavailable.
/// </summary>
public sealed partial class AppsPage : Page, IRefreshablePage
{
    private readonly TextBox _searchBox = new() { PlaceholderText = "Filter phone apps…", Width = 240 };
    private readonly StackPanel _contentStack = new() { Spacing = 14 };
    private readonly StackPanel _gatePanel = new();
    private string? _lastDev;

    public AppsPage()
    {
        _searchBox.TextChanged += (_, _) => Render();

        var root = new Grid { Padding = new Thickness(14, 12, 24, 22), RowSpacing = 12 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var hdr = new Grid { ColumnSpacing = 10 };
        hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hdr.Children.Add(UiKit.PageTitle(Loc.Tr("Apps", "Phone apps")));

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(_searchBox);
        right.Children.Add(UiKit.PillButton(Loc.Tr("Apps", "Mirror whole screen"), () =>
            NectarLinkEngine.Current.Invoke("mirror.start", new { device = NectarLinkEngine.Current.CurrentDeviceId }), glyph: "\uE7F4", accent: true));
        right.Children.Add(UiKit.IconButton("\uE72C", Loc.Tr("Common", "Refresh"), () =>
            NectarLinkEngine.Current.Invoke("mirror.loadApps", new { device = NectarLinkEngine.Current.CurrentDeviceId })));
        Grid.SetColumn(right, 1);
        hdr.Children.Add(right);
        root.Children.Add(hdr);

        Grid.SetRow(_gatePanel, 1);
        root.Children.Add(_gatePanel);

        var scroll = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _contentStack
        };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);

        Content = root;
    }

    private string _lastSig = string.Empty;

    public void OnNavigatedTo()
    {
        EnsureAppsLoaded();
        Render();
    }

    public void OnEngineChanged(uint mask)
    {
        if (!EngineChanges.HasAny(mask, EngineChanges.Mirror | EngineChanges.Apps | EngineChanges.Prefs | EngineChanges.Devices | EngineChanges.Capabilities | EngineChanges.Status))
            return;
        EnsureAppsLoaded();
        Render();
    }

    private void EnsureAppsLoaded()
    {
        string dev = NectarLinkEngine.Current.CurrentDeviceId;
        if (!string.IsNullOrEmpty(dev) && _lastDev != dev)
        {
            _lastDev = dev;
            NectarLinkEngine.Current.Invoke("mirror.loadApps", new { device = dev });
        }
    }

    private void Render(bool force = false)
    {
        var engine = NectarLinkEngine.Current;
        string devId = engine.CurrentDeviceId;
        var m = engine.Mirror;
        string q = (_searchBox.Text ?? "").Trim();
        string sig = $"{devId}|{q}|{engine.FeatureState("mirror.apps")}|{m["appsStatus"]?.GetValue<string>()}|{m["appsError"]?.GetValue<string>()}|{engine.Prefs["pinnedApps"]?[devId]?.ToJsonString()}|{m["apps"]?.ToJsonString()}";
        if (!force && sig == _lastSig) return;
        _lastSig = sig;

        _gatePanel.Children.Clear();
        var gate = UiKit.FeatureGateCard("mirror.apps", Loc.Tr("Apps", "Individual app windows need Elevated mode on your phone"));
        if (gate.Visibility == Visibility.Visible) _gatePanel.Children.Add(gate);

        _contentStack.Children.Clear();
        string st = m["appsStatus"]?.GetValue<string>() ?? "idle";
        if (st == "loading")
        {
            _contentStack.Children.Add(new ProgressRing { IsActive = true });
            return;
        }
        if (st == "failed")
        {
            _contentStack.Children.Add(UiKit.Card(UiKit.BodyText(m["appsError"]?.GetValue<string>() ?? "Couldn't load apps.")));
            return;
        }

        var pinnedSet = new HashSet<string>(StringComparer.Ordinal);
        if (engine.Prefs["pinnedApps"]?[devId] is JsonArray pinArr)
        {
            foreach (var p in pinArr)
            {
                string? pkg = p?.GetValue<string>();
                if (!string.IsNullOrEmpty(pkg)) pinnedSet.Add(pkg);
            }
        }

        var pinnedApps = new List<JsonObject>();
        var allApps = new List<JsonObject>();
        if (m["apps"] is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is not JsonObject app) continue;
                string label = app["label"]?.GetValue<string>() ?? "";
                string pkg = app["pkg"]?.GetValue<string>() ?? "";
                if (!string.IsNullOrEmpty(q) && !label.Contains(q, StringComparison.OrdinalIgnoreCase) && !pkg.Contains(q, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (pinnedSet.Contains(pkg)) pinnedApps.Add(app);
                allApps.Add(app);
            }
        }

        if (pinnedApps.Count > 0)
        {
            _contentStack.Children.Add(UiKit.SectionLabel(Loc.Tr("Apps", "Pinned")));
            _contentStack.Children.Add(BuildAppsGrid(devId, pinnedApps, pinnedSet));
        }

        _contentStack.Children.Add(UiKit.SectionLabel(Loc.Tr("Apps", "All apps (%1)", allApps.Count)));
        _contentStack.Children.Add(BuildAppsGrid(devId, allApps, pinnedSet));
    }

    private static GridView BuildAppsGrid(string devId, List<JsonObject> apps, HashSet<string> pinnedSet)
    {
        var engine = NectarLinkEngine.Current;
        var gv = new GridView { SelectionMode = ListViewSelectionMode.None };
        foreach (var app in apps)
        {
            string pkg = app["pkg"]?.GetValue<string>() ?? "";
            string label = app["label"]?.GetValue<string>() ?? pkg;
            string iconPath = app["iconPath"]?.GetValue<string>() ?? "";
            bool pinned = pinnedSet.Contains(pkg);

            var cardStack = new StackPanel { Spacing = 8, Width = 176 };
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = UiKit.LoadLocalImage(iconPath, 64);
            if (icon is not null) top.Children.Add(new Image { Source = icon, Width = 36, Height = 36 });
            else top.Children.Add(new FontIcon { Glyph = "\uE74C", FontSize = 26 });

            var pinBtn = UiKit.IconButton(pinned ? "\uE840" : "\uE718", pinned ? "Unpin" : "Pin", () =>
                engine.Invoke("apps.setPinned", new { device = devId, pkg, pinned = !pinned }), accent: pinned);
            Grid.SetColumn(pinBtn, 2);
            top.Children.Add(pinBtn);
            cardStack.Children.Add(top);

            cardStack.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });

            var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            btns.Children.Add(UiKit.PillButton(Loc.Tr("Apps", "Open"), () =>
                engine.Invoke("mirror.startApp", new { device = devId, pkg, label }), accent: true));
            btns.Children.Add(UiKit.IconButton("\uE71B", Loc.Tr("Apps", "Desktop shortcut"), () =>
                engine.Invoke("mirror.createAppShortcut", new { device = devId, pkg, label })));
            cardStack.Children.Add(btns);

            gv.Items.Add(UiKit.Card(cardStack, new Thickness(12, 10, 12, 10)));
        }
        return gv;
    }
}
