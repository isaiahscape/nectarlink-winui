// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json.Nodes;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NectarLink.WinUI.Controls;
using NectarLink.WinUI.Services;

namespace NectarLink.WinUI.Pages;

/// <summary>
/// Cross-device activity Timeline matching TimelinePage.qml: searchable, filterable
/// by kind/device, paged on demand (100 entries/page), with Open, Show in Folder,
/// Copy, Send Again, Delete entry, and Clear All actions.
/// </summary>
public sealed partial class TimelinePage : Page, IRefreshablePage
{
    private readonly TextBox _searchBox = new() { PlaceholderText = "Search timeline history…", Width = 240 };
    private readonly StackPanel _kindFilterBar = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly StackPanel _rowsPanel = new() { Spacing = 4 };
    private string _kind = "";
    private int _limit = 100;

    public TimelinePage()
    {
        _searchBox.TextChanged += (_, _) => { _limit = 100; QueryAndRender(); };

        var root = new Grid { Padding = new Thickness(14, 12, 24, 22), RowSpacing = 12 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var hdr = new Grid();
        hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hdr.Children.Add(UiKit.PageTitle(Loc.Tr("Timeline", "Timeline")));
        var hdrRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        hdrRight.Children.Add(_searchBox);
        hdrRight.Children.Add(UiKit.PillButton(Loc.Tr("Timeline", "Clear all"), () =>
        {
            NectarLinkEngine.Current.Invoke("timeline.clearAll");
            QueryAndRender();
        }));
        Grid.SetColumn(hdrRight, 1);
        hdr.Children.Add(hdrRight);
        root.Children.Add(hdr);

        var filterScroll = UiKit.HScroll(_kindFilterBar);
        Grid.SetRow(filterScroll, 1);
        root.Children.Add(filterScroll);

        var scroll = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _rowsPanel
        };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);

        Content = root;
    }

    public void OnNavigatedTo() => QueryAndRender();
    public void OnEngineChanged(uint mask)
    {
        if ((mask & (1u << 21)) != 0 || mask == uint.MaxValue)
            QueryAndRender();
    }

    private void QueryAndRender()
    {
        _kindFilterBar.Children.Clear();
        foreach (var (k, lbl) in new[] { ("", "All"), ("file", "Files"), ("photo", "Photos"), ("clip", "Clipboard"), ("link", "Links"), ("recording", "Recordings"), ("session", "Sessions") })
        {
            string kindKey = k;
            _kindFilterBar.Children.Add(UiKit.PillButton(lbl, () =>
            {
                _kind = kindKey;
                _limit = 100;
                QueryAndRender();
            }, accent: _kind == kindKey));
        }

        var res = NectarLinkEngine.Current.Invoke("timeline.query", new
        {
            search = _searchBox.Text ?? "",
            kind = _kind,
            device = "",
            limit = _limit
        });

        _rowsPanel.Children.Clear();
        if (res["rows"] is not JsonArray rows || rows.Count == 0)
        {
            _rowsPanel.Children.Add(UiKit.MutedText(Loc.Tr("Timeline", "No timeline history matches your filter.")));
            return;
        }

        foreach (var item in rows)
        {
            if (item is not JsonObject r) continue;
            string eid = r["entryId"]?.GetValue<string>() ?? "";
            string title = r["title"]?.GetValue<string>() ?? "";
            string sub = r["subtitle"]?.GetValue<string>() ?? "";
            string kind = r["kind"]?.GetValue<string>() ?? "";
            bool canOpen = r["canOpen"]?.GetValue<bool>() ?? false;
            bool canShow = r["canShowInFolder"]?.GetValue<bool>() ?? false;
            bool canCopy = r["canCopy"]?.GetValue<bool>() ?? false;
            bool canSend = r["canSendAgain"]?.GetValue<bool>() ?? false;

            var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            if (canOpen) btns.Children.Add(UiKit.PillButton("Open", () => NectarLinkEngine.Current.Invoke("timeline.open", new { id = eid })));
            if (canShow) btns.Children.Add(UiKit.IconButton("\uE8B7", "Show in folder", () => NectarLinkEngine.Current.Invoke("timeline.reveal", new { id = eid })));
            if (canCopy) btns.Children.Add(UiKit.IconButton("\uE8C8", "Copy", () => NectarLinkEngine.Current.Invoke("timeline.copy", new { id = eid })));
            if (canSend) btns.Children.Add(UiKit.IconButton("\uE724", "Send again", () => NectarLinkEngine.Current.Invoke("timeline.sendAgain", new { id = eid })));
            btns.Children.Add(UiKit.IconButton("\uE74D", "Remove", () =>
            {
                NectarLinkEngine.Current.Invoke("timeline.remove", new { id = eid });
                QueryAndRender();
            }));

            string glyph = kind switch
            {
                "file" => "\uE8A5",
                "photo" => "\uE91B",
                "clip" => "\uE8C8",
                "link" => "\uE71B",
                "recording" => "\uE720",
                _ => "\uE81C"
            };
            _rowsPanel.Children.Add(UiKit.SettingsCard(glyph, title, $"{kind} · {sub}", trailing: btns));
        }

        if (res["hasMore"]?.GetValue<bool>() ?? false)
        {
            _rowsPanel.Children.Add(UiKit.PillButton(Loc.Tr("Timeline", "Load more"), () =>
            {
                _limit += 100;
                QueryAndRender();
            }, accent: true));
        }
    }
}
