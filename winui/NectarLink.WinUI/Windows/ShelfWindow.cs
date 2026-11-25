// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NectarLink.WinUI.Controls;
using NectarLink.WinUI.Services;
using Windows.ApplicationModel.DataTransfer;

namespace NectarLink.WinUI.Hud;

/// <summary>
/// Screen-edge slide-out Shelf panel matching ShelfWindow.qml: holds latest
/// phone photo, screenshot, clipboard clip, and received files for one-click open,
/// OLE drag-out onto Explorer/browsers, and file/text drop-to-send to your phone.
/// </summary>
public sealed partial class ShelfWindow : Window
{
    private readonly Border _root = new()
    {
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(14),
        Padding = new Thickness(14),
        AllowDrop = true
    };
    private readonly StackPanel _itemsStack = new() { Spacing = 6 };
    private bool _activatedOnce;
    private bool _isShown;
    private bool _forceClosing;
    public bool IsClosed { get; private set; }

    public ShelfWindow()
    {
        var appWin = WindowChrome.GetAppWindow(this);
        WindowChrome.TryHideFromSwitchers(appWin);
        if (appWin.Presenter is OverlappedPresenter op)
        {
            op.SetBorderAndTitleBar(false, false);
            op.IsAlwaysOnTop = true;
            op.IsResizable = false;
        }
        appWin.Closing += (_, args) =>
        {
            if (!_forceClosing)
            {
                args.Cancel = true;
                HideShelf();
                NectarLinkEngine.Current.Invoke("shelf.close");
            }
        };
        Closed += (_, _) => IsClosed = true;

        _root.Background = UiKit.Brush("NlFlyoutSurfaceBrush");
        _root.BorderBrush = UiKit.Brush("NlCardStrokeBrush");
        _root.DragOver += (_, e) =>
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems) || e.DataView.Contains(StandardDataFormats.Text))
                e.AcceptedOperation = DataPackageOperation.Copy;
        };
        _root.Drop += OnDrop;

        var layout = new Grid { RowSpacing = 10 };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var hdr = new Grid { ColumnSpacing = 6 };
        hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hdr.Children.Add(UiKit.TitleText("Shelf", 17));

        var refreshBtn = UiKit.IconButton("\uE72C", "Refresh shelf", () => NectarLinkEngine.Current.Invoke("shelf.refresh"));
        Grid.SetColumn(refreshBtn, 1);
        hdr.Children.Add(refreshBtn);

        var closeBtn = UiKit.IconButton("\uE711", "Hide shelf", () => NectarLinkEngine.Current.Invoke("shelf.close"));
        Grid.SetColumn(closeBtn, 2);
        hdr.Children.Add(closeBtn);
        layout.Children.Add(hdr);

        var scroll = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _itemsStack
        };
        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);
        _root.Child = layout;
        Content = _root;
    }

    public void ForceClose()
    {
        if (IsClosed) return;
        _forceClosing = true;
        try { Close(); } catch { }
    }

    public void ShowNearEdge()
    {
        if (IsClosed) return;
        try
        {
            _root.RequestedTheme = ThemeManager.Current.Dark ? ElementTheme.Dark : ElementTheme.Light;
            _root.Background = UiKit.Brush("NlFlyoutSurfaceBrush");
        _root.BorderBrush = UiKit.Brush("NlCardStrokeBrush");

            bool opening = !_isShown;
            if (opening)
            {
                var (wx, wy, ww, wh) = WindowChrome.GetPrimaryWorkArea();
                int width = 328;
                int height = Math.Min(540, Math.Max(320, wh - 40));
                var aw = WindowChrome.GetAppWindow(this);
                aw.MoveAndResize(new Windows.Graphics.RectInt32(wx + ww - width - 14, wy + (wh - height) / 2, width, height));

                if (!_activatedOnce)
                {
                    _activatedOnce = true;
                    Activate();
                    WindowChrome.ConfigureFloatingHud(this);
                }
                else
                {
                    aw.Show(false);
                    WindowChrome.ConfigureFloatingHud(this);
                }
                _isShown = true;
                Motion.PlayEntrance(_root, offsetX: 26f, offsetY: 0f, durationMs: 210);
            }
            Render();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ShelfWindow.ShowNearEdge error: {ex.Message}");
        }
    }

    public void HideShelf()
    {
        if (IsClosed || !_isShown) return;
        _isShown = false;
        try { WindowChrome.GetAppWindow(this).Hide(); } catch { }
    }

    public void SyncState()
    {
        if (IsClosed) return;
        bool open = NectarLinkEngine.Current.App["shelfOpen"]?.GetValue<bool>() ?? false;
        if (open) ShowNearEdge();
        else HideShelf();
    }

    private void Render()
    {
        _itemsStack.Children.Clear();
        var engine = NectarLinkEngine.Current;
        if (engine.App["shelfItems"] is not JsonObject shelf)
        {
            _itemsStack.Children.Add(UiKit.MutedText("Recent photos, screenshots, clips, and received files appear here. Drop files anywhere on Shelf to send to your phone."));
            return;
        }

        int rendered = 0;

        if (shelf["latestPhoto"] is JsonObject photo)
        {
            rendered++;
            _itemsStack.Children.Add(BuildMediaCard("Latest photo", "photo", photo));
        }

        if (shelf["latestScreenshot"] is JsonObject shot)
        {
            rendered++;
            _itemsStack.Children.Add(BuildMediaCard("Latest screenshot", "screenshot", shot));
        }

        if (shelf["currentClip"] is JsonObject clip)
        {
            string ctext = clip["text"]?.GetValue<string>() ?? "";
            string cid = clip["id"]?.GetValue<string>() ?? "";
            string devName = clip["deviceName"]?.GetValue<string>() ?? "";
            if (!string.IsNullOrEmpty(ctext))
            {
                rendered++;
                var card = new StackPanel { Spacing = 6 };
                card.Children.Add(UiKit.SectionLabel(string.IsNullOrEmpty(devName) ? "Clipboard" : $"Clipboard · {devName}"));
                card.Children.Add(new TextBlock { Text = ctext, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap });
                var copyBtn = UiKit.PillButton("Copy", () =>
                {
                    if (!string.IsNullOrEmpty(cid))
                        engine.Invoke("shelf.activate", new { kind = "clip", action = "open", target = cid });
                    else
                    {
                        var dp = new DataPackage();
                        dp.SetText(ctext);
                        Clipboard.SetContent(dp);
                    }
                }, accent: true);
                card.Children.Add(copyBtn);

                var b = UiKit.Card(card, new Thickness(12, 10, 12, 10));
                b.CanDrag = true;
                b.DragStarting += (_, args) =>
                {
                    args.Cancel = true;
                    DragDropHelper.StartTextDrag(ctext);
                };
                _itemsStack.Children.Add(b);
            }
        }

        if (shelf["recentFiles"] is JsonArray files && files.Count > 0)
        {
            foreach (var f in files)
            {
                if (f is not JsonObject fileObj) continue;
                string fname = fileObj["name"]?.GetValue<string>() ?? "File";
                string fpath = fileObj["path"]?.GetValue<string>() ?? "";
                string dname = fileObj["deviceName"]?.GetValue<string>() ?? "";
                if (string.IsNullOrEmpty(fpath)) continue;

                rendered++;
                var card = new StackPanel { Spacing = 6 };
                card.Children.Add(new TextBlock { Text = fname, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                if (!string.IsNullOrEmpty(dname)) card.Children.Add(UiKit.MutedText(dname, 11.5));

                var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                btns.Children.Add(UiKit.PillButton("Open", () => engine.Invoke("shelf.activate", new { kind = "file", action = "open", target = fpath }), accent: true));
                btns.Children.Add(UiKit.PillButton("Folder", () => engine.Invoke("shelf.activate", new { kind = "file", action = "reveal", target = fpath })));
                card.Children.Add(btns);

                var b = UiKit.Card(card, new Thickness(12, 10, 12, 10));
                b.CanDrag = true;
                b.DragStarting += (_, args) =>
                {
                    args.Cancel = true;
                    if (File.Exists(fpath)) DragDropHelper.StartFileDrag([fpath]);
                };
                _itemsStack.Children.Add(b);
            }
        }

        if (rendered == 0)
        {
            _itemsStack.Children.Add(UiKit.MutedText("Recent photos, screenshots, clips, and received files appear here. Drop files anywhere on Shelf to send to your phone."));
        }
    }

    private UIElement BuildMediaCard(string heading, string kind, JsonObject item)
    {
        var engine = NectarLinkEngine.Current;
        string name = item["name"]?.GetValue<string>() ?? heading;
        string localPath = item["localPath"]?.GetValue<string>() ?? "";
        string thumbUrl = item["thumbUrl"]?.GetValue<string>() ?? "";
        string id = item["id"]?.GetValue<string>() ?? "";
        string target = !string.IsNullOrEmpty(localPath) ? localPath : id;

        var card = new StackPanel { Spacing = 6 };
        card.Children.Add(UiKit.SectionLabel(heading));

        var img = UiKit.LoadLocalImage(thumbUrl, 280);
        if (img is not null)
        {
            card.Children.Add(new Image { Source = img, MaxHeight = 110, Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill });
        }
        card.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });

        var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        btns.Children.Add(UiKit.PillButton("Open", () => engine.Invoke("shelf.activate", new { kind, action = "open", target }), accent: true));
        btns.Children.Add(UiKit.PillButton("Copy", () => engine.Invoke("shelf.activate", new { kind, action = "copy", target })));
        btns.Children.Add(UiKit.PillButton(!string.IsNullOrEmpty(localPath) ? "Folder" : "Save",
            () => engine.Invoke("shelf.activate", new { kind, action = !string.IsNullOrEmpty(localPath) ? "reveal" : "save", target })));
        card.Children.Add(btns);

        var border = UiKit.Card(card, new Thickness(12, 10, 12, 10));
        border.CanDrag = !string.IsNullOrEmpty(localPath) && File.Exists(localPath);
        border.DragStarting += (_, args) =>
        {
            args.Cancel = true;
            if (!string.IsNullOrEmpty(localPath) && File.Exists(localPath))
                DragDropHelper.StartFileDrag([localPath]);
        };
        return border;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = new List<string>();
            foreach (var it in items)
            {
                if (!string.IsNullOrEmpty(it.Path)) paths.Add(it.Path);
            }
            if (paths.Count > 0) NectarLinkEngine.Current.Invoke("shelf.drop", new { paths });
        }
        else if (e.DataView.Contains(StandardDataFormats.Text))
        {
            string text = await e.DataView.GetTextAsync();
            if (!string.IsNullOrEmpty(text)) NectarLinkEngine.Current.Invoke("shelf.drop", new { text });
        }
    }
}
