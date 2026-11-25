// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NectarLink.WinUI.Controls;
using NectarLink.WinUI.Services;

namespace NectarLink.WinUI.Pages;

/// <summary>
/// PC Deck editor &amp; live controller matching DeckPage.qml: multiple pages,
/// customizable macro/shortcut/URL/app/OBS tiles, reorder arrows, test fire,
/// live PC volume/mute/mic/media indicator, and OBS WebSocket settings.
/// </summary>
public sealed partial class DeckPage : Page, IRefreshablePage
{
    private readonly StackPanel _pageTabs = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly StackPanel _liveBar = new() { Orientation = Orientation.Horizontal, Spacing = 14 };
    private readonly GridView _tileGrid = new() { SelectionMode = ListViewSelectionMode.None };
    private string _selectedPageId = "";

    public DeckPage()
    {
        var root = new Grid { Padding = new Thickness(14, 12, 24, 22), RowSpacing = 12 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var hdr = new Grid();
        hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hdr.Children.Add(UiKit.PageTitle(Loc.Tr("Deck", "Stream & PC Deck")));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(UiKit.PillButton(Loc.Tr("Deck", "New tile"), OnAddTileClick, glyph: "\uE710", accent: true));
        actions.Children.Add(UiKit.PillButton(Loc.Tr("Deck", "Add page"), OnAddPageClick));
        actions.Children.Add(UiKit.PillButton(Loc.Tr("Deck", "OBS settings"), OnObsSettingsClick, glyph: "\uE713"));
        actions.Children.Add(UiKit.PillButton(Loc.Tr("Deck", "Reset defaults"), () => NectarLinkEngine.Current.Invoke("deck.resetDefault")));
        Grid.SetColumn(actions, 1);
        hdr.Children.Add(actions);
        root.Children.Add(hdr);

        var subBar = new Grid { RowSpacing = 8 };
        subBar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        subBar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        subBar.Children.Add(_pageTabs);
        Grid.SetRow(_liveBar, 1);
        subBar.Children.Add(_liveBar);
        Grid.SetRow(subBar, 1);
        root.Children.Add(subBar);

        var scroll = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _tileGrid
        };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);

        Content = root;
        Unloaded += (_, _) => NectarLinkEngine.Current.Invoke("deck.setPageActive", new { active = false });
    }

    private string _lastLayoutSig = string.Empty;
    private string _lastLiveSig = string.Empty;

    public void OnNavigatedTo()
    {
        NectarLinkEngine.Current.Invoke("deck.setPageActive", new { active = true });
        Render();
    }

    public void OnEngineChanged(uint mask)
    {
        if (EngineChanges.HasAny(mask, EngineChanges.Deck | EngineChanges.Status | EngineChanges.Devices))
            Render();
    }

    private void Render(bool force = false)
    {
        var d = NectarLinkEngine.Current.Deck;
        int vol = d["volume"]?.GetValue<int>() ?? 0;
        bool muted = d["muted"]?.GetValue<bool>() ?? false;
        int mic = d["micState"]?.GetValue<int>() ?? 0;
        bool playing = d["playing"]?.GetValue<bool>() ?? false;

        string liveSig = $"{vol}|{muted}|{mic}|{playing}";
        if (force || liveSig != _lastLiveSig)
        {
            _lastLiveSig = liveSig;
            _liveBar.Children.Clear();
            _liveBar.Children.Add(UiKit.StatusBadge(muted ? $"Speaker: Muted ({vol}%)" : $"Speaker: {vol}%", highlight: !muted));
            _liveBar.Children.Add(UiKit.StatusBadge(mic == 2 ? "Mic: Muted" : mic == 1 ? "Mic: Live" : "Mic: N/A"));
            _liveBar.Children.Add(UiKit.StatusBadge(playing ? "Media: Playing" : "Media: Paused", highlight: playing));
        }

        string layoutSig = $"{_selectedPageId}|{d["pages"]?.ToJsonString()}";
        if (!force && layoutSig == _lastLayoutSig) return;
        _lastLayoutSig = layoutSig;

        _pageTabs.Children.Clear();
        _tileGrid.Items.Clear();
        if (d["pages"] is not JsonArray pages || pages.Count == 0) return;

        JsonObject? activePage = null;
        foreach (var item in pages)
        {
            if (item is not JsonObject p) continue;
            string pid = p["id"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(_selectedPageId) || _selectedPageId == pid)
            {
                activePage ??= p;
                if (_selectedPageId == pid) activePage = p;
            }
        }
        activePage ??= pages[0] as JsonObject;
        _selectedPageId = activePage?["id"]?.GetValue<string>() ?? "";

        foreach (var item in pages)
        {
            if (item is not JsonObject p) continue;
            string pid = p["id"]?.GetValue<string>() ?? "";
            string pname = p["name"]?.GetValue<string>() ?? "Page";
            _pageTabs.Children.Add(UiKit.PillButton(pname, () => { _selectedPageId = pid; Render(); }, accent: pid == _selectedPageId));
        }

        if (activePage?["tiles"] is JsonArray tiles)
        {
            for (int i = 0; i < tiles.Count; i++)
            {
                if (tiles[i] is not JsonObject t) continue;
                string tid = t["id"]?.GetValue<string>() ?? "";
                string label = t["label"]?.GetValue<string>() ?? "Tile";
                string subtitle = t["subtitle"]?.GetValue<string>() ?? "";
                int idx = i;

                var card = new StackPanel { Spacing = 8, Width = 172 };
                card.Children.Add(new TextBlock { Text = label, FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                card.Children.Add(UiKit.MutedText(subtitle, 12));

                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                row.Children.Add(UiKit.PillButton("Run", () => NectarLinkEngine.Current.Invoke("deck.testTile", new { tileId = tid }), accent: true));
                if (idx > 0)
                    row.Children.Add(UiKit.IconButton("\uE72B", "Move left", () => NectarLinkEngine.Current.Invoke("deck.moveTile", new { pageId = _selectedPageId, from = idx, to = idx - 1 })));
                if (idx + 1 < tiles.Count)
                    row.Children.Add(UiKit.IconButton("\uE72A", "Move right", () => NectarLinkEngine.Current.Invoke("deck.moveTile", new { pageId = _selectedPageId, from = idx, to = idx + 1 })));
                row.Children.Add(UiKit.IconButton("\uE74D", "Remove", () => NectarLinkEngine.Current.Invoke("deck.removeTile", new { pageId = _selectedPageId, tileId = tid })));
                card.Children.Add(row);

                _tileGrid.Items.Add(UiKit.Card(card, new Thickness(12, 10, 12, 10)));
            }
        }
    }

    private async void OnAddPageClick()
    {
        var box = new TextBox { Header = Loc.Tr("Deck", "Page name"), PlaceholderText = "OBS / Work" };
        ContentDialog dlg = new() { Title = Loc.Tr("Deck", "Add Deck page"), PrimaryButtonText = Loc.Tr("Common", "Add"), CloseButtonText = Loc.Tr("Common", "Cancel"), XamlRoot = XamlRoot, Content = box };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text))
            NectarLinkEngine.Current.Invoke("deck.addPage", new { name = box.Text.Trim() });
    }

    private async void OnAddTileClick()
    {
        var labelBox = new TextBox { Header = Loc.Tr("Deck", "Tile label"), PlaceholderText = "Screenshot" };
        var kindBox = new ComboBox { Header = Loc.Tr("Deck", "Action"), HorizontalAlignment = HorizontalAlignment.Stretch };
        string[] kinds = ["media_play_pause", "media_next", "media_previous", "volume_up", "volume_down", "volume_mute", "mic_mute", "lock_pc", "show_desktop", "screenshot", "open_url", "type_text", "launch_app", "run_command"];
        foreach (var k in kinds) kindBox.Items.Add(k);
        kindBox.SelectedIndex = 0;
        var paramBox = new TextBox { Header = Loc.Tr("Deck", "Parameter / URL / Path (if needed)") };

        var stack = new StackPanel { Spacing = 10, Children = { labelBox, kindBox, paramBox } };
        ContentDialog dlg = new() { Title = Loc.Tr("Deck", "New Deck tile"), PrimaryButtonText = Loc.Tr("Common", "Save"), CloseButtonText = Loc.Tr("Common", "Cancel"), XamlRoot = XamlRoot, Content = stack };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
        {
            NectarLinkEngine.Current.Invoke("deck.saveTile", new
            {
                pageId = _selectedPageId,
                tileId = "",
                label = string.IsNullOrWhiteSpace(labelBox.Text) ? (kindBox.SelectedItem?.ToString() ?? "Tile") : labelBox.Text.Trim(),
                icon = "",
                color = "",
                kind = kindBox.SelectedItem?.ToString() ?? "screenshot",
                param = paramBox.Text ?? "",
                ctrl = false,
                alt = false,
                shift = false,
                win = false
            });
        }
    }

    private async void OnObsSettingsClick()
    {
        var prefs = NectarLinkEngine.Current.Prefs;
        var portBox = new TextBox { Header = "OBS WebSocket Port", Text = (prefs["obsPort"]?.GetValue<int>() ?? 4455).ToString() };
        var pwBox = new PasswordBox { Header = "OBS Password (leave blank to keep existing)" };
        var stack = new StackPanel { Spacing = 10, Children = { portBox, pwBox } };
        stack.Children.Add(UiKit.PillButton("Test connection", () => NectarLinkEngine.Current.Invoke("deck.testObs")));
        ContentDialog dlg = new() { Title = "OBS WebSocket", PrimaryButtonText = "Save", CloseButtonText = "Cancel", XamlRoot = XamlRoot, Content = stack };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary && int.TryParse(portBox.Text, out int port))
        {
            NectarLinkEngine.Current.Invoke("deck.saveObs", new
            {
                port,
                password = pwBox.Password ?? "",
                replacePassword = !string.IsNullOrEmpty(pwBox.Password)
            });
        }
    }
}
