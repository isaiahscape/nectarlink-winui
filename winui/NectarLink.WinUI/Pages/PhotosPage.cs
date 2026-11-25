// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NectarLink.WinUI.Controls;
using NectarLink.WinUI.Services;

namespace NectarLink.WinUI.Pages;

/// <summary>
/// Phone Photos gallery matching PhotosPage.qml: albums bar, day-grouped lazy thumbnail
/// grid, multi-select toolbar (Save, Copy, Share via Windows Share Sheet, Delete on phone),
/// native OLE drag-out onto Explorer, and an in-app full-resolution Lightbox viewer.
/// </summary>
public sealed partial class PhotosPage : Page, IRefreshablePage
{
    private readonly StackPanel _albumsBar = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly StackPanel _daysContainer = new() { Spacing = 14 };
    private readonly StackPanel _selectionBar = new() { Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly StackPanel _gatePanel = new();
    private readonly TextBlock _countLabel = UiKit.MutedText("");
    private readonly HashSet<string> _selectedIds = new(StringComparer.Ordinal);

    private string? _lastDev;

    public PhotosPage()
    {
        var root = new Grid { Padding = new Thickness(14, 12, 24, 22), RowSpacing = 12 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Top header
        var hdr = new Grid { ColumnSpacing = 10 };
        hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hdr.Children.Add(UiKit.PageTitle(Loc.Tr("Photos", "Photos")));
        Grid.SetColumn(_countLabel, 1);
        _countLabel.VerticalAlignment = VerticalAlignment.Center;
        hdr.Children.Add(_countLabel);

        var rightBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        rightBtns.Children.Add(UiKit.PillButton(Loc.Tr("Photos", "Save folder"), OnChooseFolderClick, glyph: "\uE8B7"));
        rightBtns.Children.Add(UiKit.IconButton("\uE72C", Loc.Tr("Common", "Refresh"), () => NectarLinkEngine.Current.Invoke("photos.refresh")));
        Grid.SetColumn(rightBtns, 2);
        hdr.Children.Add(rightBtns);
        root.Children.Add(hdr);

        // Albums strip
        var albumScroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _albumsBar };
        var topSub = new StackPanel { Spacing = 8, Children = { _gatePanel, albumScroll } };
        Grid.SetRow(topSub, 1);
        root.Children.Add(topSub);

        Grid.SetRow(_selectionBar, 2);
        root.Children.Add(_selectionBar);

        var gridScroll = new ScrollViewer { Content = _daysContainer };
        Grid.SetRow(gridScroll, 3);
        root.Children.Add(gridScroll);

        Content = root;
    }

    private string _lastSig = string.Empty;
    private string _lastGridStructSig = string.Empty;
    private readonly Dictionary<string, (Grid Tile, FrameworkElement VisualHost, Button SelBtn, string Thumb)> _tileCache = new(StringComparer.Ordinal);

    public void OnNavigatedTo()
    {
        EnsureDevice();
        Render();
    }

    public void OnEngineChanged(uint mask)
    {
        if (!EngineChanges.HasAny(mask, EngineChanges.Photos | EngineChanges.PhotoBackup | EngineChanges.Devices | EngineChanges.Capabilities | EngineChanges.Status))
            return;
        EnsureDevice();
        Render();
    }

    private void EnsureDevice()
    {
        string dev = NectarLinkEngine.Current.CurrentDeviceId;
        if (!string.IsNullOrEmpty(dev) && _lastDev != dev)
        {
            _lastDev = dev;
            _selectedIds.Clear();
            NectarLinkEngine.Current.Invoke("photos.open", new { device = dev });
        }
    }

    private void Render(bool force = false)
    {
        var engine = NectarLinkEngine.Current;
        var ph = engine.Photos;
        string albumsSig = ph["albums"]?.ToJsonString() ?? "";
        string curAlbum = ph["album"]?.GetValue<string>() ?? "";
        int total = ph["photoCount"]?.GetValue<int>() ?? 0;
        bool hasMore = ph["more"]?.GetValue<bool>() ?? false;

        // Build signature over grid-visible fields only (exclude fullUrl/full fetch status so opening a photo never refreshes the gallery)
        var rowIdsSb = new System.Text.StringBuilder();
        var rowThumbsSb = new System.Text.StringBuilder();
        if (ph["rows"] is JsonArray rowArr)
        {
            foreach (var item in rowArr)
            {
                if (item is not JsonObject r) continue;
                if ((r["kind"]?.GetValue<string>() ?? "photo") != "photo") continue;
                string pid = r["id"]?.GetValue<string>() ?? "";
                string thumb = r["thumb"]?.GetValue<string>() ?? "";
                bool isVid = r["isVideo"]?.GetValue<bool>() ?? false;
                rowIdsSb.Append(pid).Append('|').Append(isVid ? '1' : '0').Append(';');
                rowThumbsSb.Append(pid).Append('=').Append(thumb).Append(';');
            }
        }
        string gridStructSig = $"{engine.CurrentDeviceId}|{engine.FeatureState("photos.gallery")}|{curAlbum}|{albumsSig}|{hasMore}|{rowIdsSb}";
        string sig = $"{gridStructSig}|{total}|{_selectedIds.Count}|{rowThumbsSb}";
        if (!force && sig == _lastSig) return;
        _lastSig = sig;

        _countLabel.Text = total > 0 ? Loc.Tr("Photos", "%1 items", total) : "";
        SyncSelectionBar();

        // Update thumbnails and selection icons in-place when only thumbs or selection changed
        if (!force && gridStructSig == _lastGridStructSig && _daysContainer.Children.Count > 0 && ph["rows"] is JsonArray updRows)
        {
            foreach (var item in updRows)
            {
                if (item is not JsonObject r) continue;
                if ((r["kind"]?.GetValue<string>() ?? "photo") != "photo") continue;
                string pid = r["id"]?.GetValue<string>() ?? "";
                string thumb = r["thumb"]?.GetValue<string>() ?? "";
                bool selected = _selectedIds.Contains(pid);
                if (string.IsNullOrEmpty(thumb) && !string.IsNullOrEmpty(pid))
                    engine.Invoke("photos.needThumb", new { id = pid });

                if (_tileCache.TryGetValue(pid, out var entry))
                {
                    if (entry.SelBtn.Content is FontIcon selIcon)
                        selIcon.Glyph = selected ? "\uE73E" : "\uE73A";
                    entry.SelBtn.Background = UiKit.Brush(selected ? "NlPrimaryContainerBrush" : "NlSurfaceContainerHighBrush");
                    entry.SelBtn.Foreground = UiKit.Brush(selected ? "NlOnPrimaryContainerBrush" : "NlOnSurfaceBrush");

                    if (thumb != entry.Thumb)
                    {
                        var imgSrc = UiKit.LoadLocalImage(thumb, 256);
                        if (imgSrc is not null && entry.VisualHost is Border hostBorder)
                        {
                            hostBorder.Child = new Image { Source = imgSrc, Stretch = Stretch.UniformToFill };
                            _tileCache[pid] = (entry.Tile, entry.VisualHost, entry.SelBtn, thumb);
                        }
                    }
                }
            }
            return;
        }
        _lastGridStructSig = gridStructSig;
        _tileCache.Clear();

        _gatePanel.Children.Clear();
        var gate = UiKit.FeatureGateCard("photos.gallery", Loc.Tr("Photos", "Photos access needed on phone"));
        if (gate.Visibility == Visibility.Visible) _gatePanel.Children.Add(gate);

        // Albums strip
        _albumsBar.Children.Clear();
        if (ph["albums"] is JsonArray albums)
        {
            foreach (var a in albums)
            {
                string aid = a?["id"]?.GetValue<string>() ?? "";
                string aname = a?["name"]?.GetValue<string>() ?? "Album";
                int cnt = a?["count"]?.GetValue<int>() ?? 0;
                _albumsBar.Children.Add(UiKit.PillButton($"{aname} ({cnt})", () =>
                {
                    _selectedIds.Clear();
                    engine.Invoke("photos.selectAlbum", new { album = aid });
                }, accent: aid == curAlbum));
            }
        }

        // Photos Grid
        _daysContainer.Children.Clear();
        if (ph["rows"] is not JsonArray rows || rows.Count == 0)
        {
            _daysContainer.Children.Add(UiKit.MutedText(Loc.Tr("Photos", "No photos loaded for this album.")));
            return;
        }

        var wrap = new GridView
        {
            SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = false
        };

        foreach (var item in rows)
        {
            if (item is not JsonObject r) continue;
            if ((r["kind"]?.GetValue<string>() ?? "photo") != "photo") continue;
            string pid = r["id"]?.GetValue<string>() ?? "";
            string name = r["name"]?.GetValue<string>() ?? "";
            string thumb = r["thumb"]?.GetValue<string>() ?? "";
            bool isVideo = r["isVideo"]?.GetValue<bool>() ?? false;
            bool selected = _selectedIds.Contains(pid);

            if (string.IsNullOrEmpty(thumb) && !string.IsNullOrEmpty(pid))
            {
                engine.Invoke("photos.needThumb", new { id = pid });
            }

            var tile = new Grid
            {
                Width = 148,
                Height = 148,
                CornerRadius = ThemeManager.Current.RadiusSm,
                Background = UiKit.Brush("NlSurfaceContainerHighBrush")
            };

            var imgSrc = UiKit.LoadLocalImage(thumb, 256);
            var visualHost = new Border
            {
                CornerRadius = ThemeManager.Current.RadiusSm,
                Child = imgSrc is not null
                    ? new Image { Source = imgSrc, Stretch = Stretch.UniformToFill }
                    : new FontIcon { Glyph = isVideo ? "\uE714" : "\uE91B", FontSize = 28 }
            };
            tile.Children.Add(visualHost);

            if (isVideo)
            {
                tile.Children.Add(new Border
                {
                    Background = UiKit.Brush("NlScrimBrush"),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(6, 2, 6, 2),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(6),
                    Child = new FontIcon { Glyph = "\uE714", FontSize = 12 }
                });
            }

            // Selection toggle button
            var selBtn = UiKit.IconButton(selected ? "\uE73E" : "\uE73A", "Select", () =>
            {
                if (!_selectedIds.Add(pid)) _selectedIds.Remove(pid);
                Render();
            }, accent: selected);
            selBtn.HorizontalAlignment = HorizontalAlignment.Right;
            selBtn.VerticalAlignment = VerticalAlignment.Top;
            selBtn.Margin = new Thickness(6);
            tile.Children.Add(selBtn);
            _tileCache[pid] = (tile, visualHost, selBtn, thumb);

            // Context menu & drag-out + lightbox click
            tile.CanDrag = true;
            tile.DragStarting += (_, args) =>
            {
                args.Cancel = true; // Use native Win32 OLE DoDragDrop so Explorer drops work cleanly
                var staged = engine.Invoke("photos.stageForDrag", new { id = pid })["path"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(staged))
                    DragDropHelper.StartFileDrag([staged]);
            };
            tile.Tapped += async (_, e) =>
            {
                if (ReferenceEquals(e.OriginalSource, selBtn)) return;
                string curFull = "";
                if (NectarLinkEngine.Current.Photos["rows"] is JsonArray curArr)
                {
                    foreach (var it in curArr)
                    {
                        if (it?["id"]?.GetValue<string>() == pid)
                        {
                            curFull = it["fullUrl"]?.GetValue<string>() ?? "";
                            break;
                        }
                    }
                }
                await OpenLightboxAsync(pid, name, !string.IsNullOrEmpty(curFull) ? curFull : thumb, isVideo);
            };

            wrap.Items.Add(tile);
        }

        _daysContainer.Children.Add(wrap);
        if (hasMore)
        {
            _daysContainer.Children.Add(UiKit.PillButton(Loc.Tr("Photos", "Load older photos"), () => engine.Invoke("photos.loadOlder")));
        }
    }

    private void SyncSelectionBar()
    {
        var engine = NectarLinkEngine.Current;
        _selectionBar.Children.Clear();
        if (_selectedIds.Count > 0)
        {
            _selectionBar.Visibility = Visibility.Visible;
            var ids = _selectedIds.ToArray();
            _selectionBar.Children.Add(UiKit.BodyText(Loc.Tr("Photos", "%1 selected", ids.Length)));
            _selectionBar.Children.Add(UiKit.PillButton(Loc.Tr("Photos", "Save"), () => engine.Invoke("photos.saveItems", new { ids }), accent: true));
            _selectionBar.Children.Add(UiKit.PillButton(Loc.Tr("Photos", "Share"), () => engine.Invoke("photos.shareItems", new { ids })));
            _selectionBar.Children.Add(UiKit.PillButton(Loc.Tr("Photos", "Delete"), () =>
            {
                engine.Invoke("photos.deleteItems", new { ids });
                _selectedIds.Clear();
                Render();
            }));
            _selectionBar.Children.Add(UiKit.PillButton(Loc.Tr("Common", "Clear"), () => { _selectedIds.Clear(); Render(); }));
        }
        else
        {
            _selectionBar.Visibility = Visibility.Collapsed;
        }
    }

    private async System.Threading.Tasks.Task OpenLightboxAsync(string id, string name, string url, bool isVideo)
    {
        var engine = NectarLinkEngine.Current;
        engine.Invoke("photos.ensureFull", new { id });

        var content = new StackPanel { Spacing = 12, MaxWidth = 720 };
        var imgHolder = new Grid { MaxHeight = 480 };
        string lastImgKey = string.Empty;

        void RefreshImg()
        {
            string curUrl = url;
            if (engine.Photos["rows"] is JsonArray arr)
            {
                foreach (var it in arr)
                {
                    if (it?["id"]?.GetValue<string>() == id)
                    {
                        string f = it["fullUrl"]?.GetValue<string>() ?? "";
                        if (!string.IsNullOrEmpty(f)) curUrl = f;
                        break;
                    }
                }
            }
            string key = curUrl;
            if (key == lastImgKey && imgHolder.Children.Count > 0)
                return;
            lastImgKey = key;

            imgHolder.Children.Clear();
            var src = UiKit.LoadLocalImage(curUrl);
            if (src is not null) imgHolder.Children.Add(new Image { Source = src, MaxHeight = 460, Stretch = Stretch.Uniform });
            else imgHolder.Children.Add(new ProgressRing { IsActive = true });
        }

        RefreshImg();
        void OnChanged(uint mask)
        {
            if (EngineChanges.HasAny(mask, EngineChanges.Photos))
                RefreshImg();
        }
        engine.StateChanged += OnChanged;
        content.Children.Add(imgHolder);

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        bar.Children.Add(UiKit.PillButton(Loc.Tr("Photos", "Save"), () => engine.Invoke("photos.saveItems", new { ids = new[] { id } }), accent: true));
        bar.Children.Add(UiKit.PillButton(Loc.Tr("Photos", "Copy"), () => engine.Invoke("photos.copyItem", new { id })));
        if (!isVideo)
        {
            bar.Children.Add(UiKit.PillButton(
                Loc.Tr("Photos", "Set as wallpaper"),
                () => engine.Invoke("photos.setAsWallpaper", new { id, device = engine.CurrentDeviceId }),
                glyph: "\uE91B"));
        }
        bar.Children.Add(UiKit.PillButton(Loc.Tr("Photos", "Open externally"), () => engine.Invoke("photos.openItem", new { id })));
        if (isVideo) bar.Children.Add(UiKit.MutedText("Click Open externally to play full-resolution video."));
        content.Children.Add(bar);

        ContentDialog dlg = new()
        {
            Title = name,
            CloseButtonText = Loc.Tr("Common", "Close"),
            XamlRoot = XamlRoot,
            Content = content
        };
        try { await dlg.ShowAsync(); }
        finally { engine.StateChanged -= OnChanged; }
    }

    private async void OnChooseFolderClick()
    {
        if ((App.Current as App)?.MainWin is not { } mw) return;
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowChrome.GetHwnd(mw));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            NectarLinkEngine.Current.Invoke("photos.setSaveFolder", new { folder = folder.Path });
        }
    }
}
