// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Text.Json.Nodes;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NectarLink.WinUI.Controls;
using NectarLink.WinUI.Services;
using Windows.System;

namespace NectarLink.WinUI.Pages;

/// <summary>
/// SMS, MMS, and Notification-backed chat threads page with dual-SIM selection,
/// app filter chips, MMS file/clipboard-image staging, drafts, and group sender labels.
/// </summary>
public sealed partial class MessagesPage : Page, IRefreshablePage
{
    private readonly StackPanel _filterBar = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly StackPanel _threadList = new() { Spacing = 6 };
    private readonly StackPanel _messageList = new() { Spacing = 8 };
    private readonly ScrollViewer _msgScroll = new();
    private readonly StackPanel _threadHeader = new() { Orientation = Orientation.Horizontal, Spacing = 10 };
    private readonly StackPanel _attachmentBar = new() { Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly TextBox _searchBox = new() { PlaceholderText = "Search conversations…" };
    private readonly TextBox _composeBox = new() { PlaceholderText = "Type a message…", AcceptsReturn = true, MaxHeight = 110 };
    private readonly ComboBox _simBox = new() { Visibility = Visibility.Collapsed, MinWidth = 110 };
    private readonly TextBlock _statusText = UiKit.MutedText("");
    private readonly StackPanel _gatePanel = new();

    private string? _lastLoadedDevice;
    private string _loadedThread = "";
    private bool _suppressSimChange;

    public MessagesPage()
    {
        _searchBox.TextChanged += (_, _) => Render();
        _simBox.SelectionChanged += (_, _) =>
        {
            if (_suppressSimChange || _simBox.SelectedItem is not ComboBoxItem item || item.Tag is not string simId) return;
            NectarLinkEngine.Current.Invoke("messages.chooseSim", new { sim = simId });
        };
        _composeBox.TextChanged += (_, _) =>
        {
            string thread = NectarLinkEngine.Current.Messages["thread"]?.GetValue<string>() ?? "";
            if (!string.IsNullOrEmpty(thread))
                NectarLinkEngine.Current.Invoke("messages.saveDraft", new { thread, text = _composeBox.Text ?? "" });
        };
        _composeBox.KeyDown += OnComposeKeyDown;

        var root = new Grid { Padding = new Thickness(14, 12, 24, 22), ColumnSpacing = 16, RowSpacing = 12 };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Top bar across both panes
        var topBar = new Grid { ColumnSpacing = 10 };
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topBar.Children.Add(UiKit.PageTitle(Loc.Tr("Messages", "Messages")));
        var filterScroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _filterBar, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(filterScroll, 1);
        topBar.Children.Add(filterScroll);

        var topActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        topActions.Children.Add(UiKit.PillButton(Loc.Tr("Messages", "New chat"), OnNewChatClick, glyph: "\uE710", accent: true));
        topActions.Children.Add(UiKit.IconButton("\uE72C", Loc.Tr("Common", "Refresh"), () => NectarLinkEngine.Current.Invoke("messages.refresh")));
        Grid.SetColumn(topActions, 2);
        topBar.Children.Add(topActions);
        Grid.SetColumnSpan(topBar, 2);
        root.Children.Add(topBar);

        // Left pane: search + thread list
        var leftCard = new Grid { RowSpacing = 10 };
        leftCard.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        leftCard.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        leftCard.Children.Add(_searchBox);
        var tScroll = new ScrollViewer { Content = _threadList };
        Grid.SetRow(tScroll, 1);
        leftCard.Children.Add(tScroll);
        var leftBorder = UiKit.Card(leftCard, new Thickness(12));
        Grid.SetRow(leftBorder, 1);
        root.Children.Add(leftBorder);

        // Right pane: thread header + gate banner + messages + attachment + composer
        var rightGrid = new Grid { RowSpacing = 10 };
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(_gatePanel, 0);
        rightGrid.Children.Add(_gatePanel);

        var hdrRow = new Grid();
        hdrRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hdrRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hdrRow.Children.Add(_threadHeader);
        Grid.SetColumn(_statusText, 1);
        hdrRow.Children.Add(_statusText);
        Grid.SetRow(hdrRow, 1);
        rightGrid.Children.Add(hdrRow);

        _msgScroll.Content = _messageList;
        Grid.SetRow(_msgScroll, 2);
        rightGrid.Children.Add(_msgScroll);

        Grid.SetRow(_attachmentBar, 3);
        rightGrid.Children.Add(_attachmentBar);

        var composer = new Grid { ColumnSpacing = 8 };
        composer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        composer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        composer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        composer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        composer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var attachBtn = UiKit.IconButton("\uE723", Loc.Tr("Messages", "Attach image or file"), OnAttachFileClick);
        var pasteImgBtn = UiKit.IconButton("\uE77F", Loc.Tr("Messages", "Paste clipboard image"), () => NectarLinkEngine.Current.Invoke("messages.pasteClipboardImage"));
        Grid.SetColumn(pasteImgBtn, 1);
        Grid.SetColumn(_simBox, 2);
        Grid.SetColumn(_composeBox, 3);
        var sendBtn = UiKit.PillButton(Loc.Tr("Common", "Send"), SendCurrentMessage, glyph: "\uE724", accent: true);
        Grid.SetColumn(sendBtn, 4);

        composer.Children.Add(attachBtn);
        composer.Children.Add(pasteImgBtn);
        composer.Children.Add(_simBox);
        composer.Children.Add(_composeBox);
        composer.Children.Add(sendBtn);
        Grid.SetRow(composer, 4);
        rightGrid.Children.Add(composer);

        var rightBorder = UiKit.Card(rightGrid, new Thickness(14));
        Grid.SetRow(rightBorder, 1);
        Grid.SetColumn(rightBorder, 1);
        root.Children.Add(rightBorder);

        Content = root;
    }

    private string _lastSig = string.Empty;

    public void OnNavigatedTo()
    {
        EnsureOpenDevice();
        Render();
    }

    public void OnEngineChanged(uint mask)
    {
        if (!EngineChanges.HasAny(mask, EngineChanges.Messages | EngineChanges.Devices | EngineChanges.Capabilities | EngineChanges.Status))
            return;
        EnsureOpenDevice();
        Render();
    }

    private void EnsureOpenDevice()
    {
        string devId = NectarLinkEngine.Current.CurrentDeviceId;
        if (!string.IsNullOrEmpty(devId) && _lastLoadedDevice != devId)
        {
            _lastLoadedDevice = devId;
            NectarLinkEngine.Current.Invoke("messages.open", new { device = devId });
        }
    }

    private void Render(bool force = false)
    {
        var engine = NectarLinkEngine.Current;
        var m = engine.Messages;
        string filterQ = (_searchBox.Text ?? "").Trim();
        string sig = $"{engine.CurrentDeviceId}|{engine.FeatureState("messages.sms")}|{filterQ}|{m.ToJsonString()}";
        if (!force && sig == _lastSig) return;
        _lastSig = sig;

        _gatePanel.Children.Clear();
        var gate = UiKit.FeatureGateCard("messages.sms", Loc.Tr("Messages", "SMS & Messaging permissions needed on phone"));
        if (gate.Visibility == Visibility.Visible) _gatePanel.Children.Add(gate);

        string status = m["status"]?.GetValue<string>() ?? "idle";
        _statusText.Text = status == "loading" ? Loc.Tr("Common", "Loading…") : status;

        // App filter pills
        _filterBar.Children.Clear();
        string curFilter = m["appFilter"]?.GetValue<string>() ?? "";
        if (m["availableApps"] is JsonArray apps)
        {
            foreach (var item in apps)
            {
                string key = item?["key"]?.GetValue<string>() ?? "";
                string lbl = item?["label"]?.GetValue<string>() ?? key;
                _filterBar.Children.Add(UiKit.PillButton(lbl, () => engine.Invoke("messages.setAppFilter", new { app = key }), accent: key == curFilter));
            }
        }

        // Thread list
        _threadList.Children.Clear();
        string openThread = m["thread"]?.GetValue<string>() ?? "";
        if (m["threads"] is JsonArray threads)
        {
            foreach (var item in threads)
            {
                if (item is not JsonObject t) continue;
                string tid = t["id"]?.GetValue<string>() ?? "";
                string name = t["title"]?.GetValue<string>() ?? t["address"]?.GetValue<string>() ?? "Conversation";
                string snippet = t["snippet"]?.GetValue<string>() ?? "";
                int unread = t["unread"]?.GetValue<int>() ?? 0;
                string appName = t["appName"]?.GetValue<string>() ?? "";

                if (!string.IsNullOrEmpty(filterQ)
                    && !name.Contains(filterQ, StringComparison.OrdinalIgnoreCase)
                    && !snippet.Contains(filterQ, StringComparison.OrdinalIgnoreCase))
                    continue;

                bool selected = tid == openThread;
                var stack = new StackPanel { Spacing = 3 };
                var topRow = new Grid();
                topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                topRow.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                if (unread > 0)
                {
                    var b = UiKit.StatusBadge(unread.ToString(), highlight: true);
                    Grid.SetColumn(b, 1);
                    topRow.Children.Add(b);
                }
                stack.Children.Add(topRow);
                if (!string.IsNullOrEmpty(appName)) stack.Children.Add(UiKit.MutedText(appName, 11));
                stack.Children.Add(new TextBlock { Text = snippet, FontSize = 12, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = UiKit.Brush("NlOnSurfaceVariantBrush") });

                var card = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(12, 9, 12, 9),
                    CornerRadius = ThemeManager.Current.RadiusSm,
                    Background = UiKit.Brush(selected ? "NlPrimaryContainerBrush" : "NlSurfaceContainerHighBrush"),
                    Content = stack
                };
                card.Click += (_, _) => engine.Invoke("messages.openThread", new { thread = tid });
                _threadList.Children.Add(card);
            }
        }

        // Thread header & messages
        _threadHeader.Children.Clear();
        string composeTo = m["composeTo"]?.GetValue<string>() ?? "";
        string composeName = m["composeName"]?.GetValue<string>() ?? "";
        string curApp = m["currentAppName"]?.GetValue<string>() ?? "SMS";
        bool canReply = m["currentCanReply"]?.GetValue<bool>() ?? true;
        bool isChat = m["currentIsChat"]?.GetValue<bool>() ?? false;

        if (!string.IsNullOrEmpty(openThread))
        {
            _threadHeader.Children.Add(UiKit.TitleText($"{curApp}", 16));
            if (isChat)
            {
                _threadHeader.Children.Add(UiKit.PillButton(Loc.Tr("Messages", "Remove chat"), () => engine.Invoke("messages.deleteChatThread", new { thread = openThread })));
            }
        }
        else if (!string.IsNullOrEmpty(composeTo))
        {
            _threadHeader.Children.Add(UiKit.TitleText(Loc.Tr("Messages", "New message to %1", string.IsNullOrEmpty(composeName) ? composeTo : composeName), 16));
        }
        else
        {
            _threadHeader.Children.Add(UiKit.MutedText(Loc.Tr("Messages", "Select a conversation or start a new chat.")));
        }

        if (_loadedThread != openThread)
        {
            _loadedThread = openThread;
            _composeBox.Text = m["draft"]?.GetValue<string>() ?? "";
        }
        _composeBox.IsEnabled = canReply && (!string.IsNullOrEmpty(openThread) || !string.IsNullOrEmpty(composeTo));

        // Dual SIM picker
        _suppressSimChange = true;
        _simBox.Items.Clear();
        if (m["sims"] is JsonArray sims && sims.Count > 1 && !isChat)
        {
            string curSim = m["sim"]?.GetValue<string>() ?? "";
            int sel = 0;
            for (int i = 0; i < sims.Count; i++)
            {
                string sid = sims[i]?["id"]?.GetValue<string>() ?? "";
                string slbl = sims[i]?["label"]?.GetValue<string>() ?? $"SIM {i + 1}";
                _simBox.Items.Add(new ComboBoxItem { Content = slbl, Tag = sid });
                if (sid == curSim) sel = i;
            }
            _simBox.SelectedIndex = sel;
            _simBox.Visibility = Visibility.Visible;
        }
        else
        {
            _simBox.Visibility = Visibility.Collapsed;
        }
        _suppressSimChange = false;

        // Attachment bar
        bool hasAtt = m["hasAttachment"]?.GetValue<bool>() ?? false;
        _attachmentBar.Children.Clear();
        if (hasAtt)
        {
            _attachmentBar.Visibility = Visibility.Visible;
            string preview = m["attachmentPreview"]?.GetValue<string>() ?? "";
            string aname = m["attachmentName"]?.GetValue<string>() ?? "Attachment";
            string asize = m["attachmentSize"]?.GetValue<string>() ?? "";
            string aerr = m["attachmentError"]?.GetValue<string>() ?? "";
            var thumb = UiKit.LoadLocalImage(preview, 96);
            if (thumb is not null) _attachmentBar.Children.Add(new Image { Source = thumb, Width = 40, Height = 40 });
            _attachmentBar.Children.Add(UiKit.BodyText($"{aname} {asize}".Trim(), 13));
            if (!string.IsNullOrEmpty(aerr)) _attachmentBar.Children.Add(UiKit.StatusBadge(aerr, error: true));
            _attachmentBar.Children.Add(UiKit.IconButton("\uE711", "Remove attachment", () => engine.Invoke("messages.clearAttachment")));
        }
        else
        {
            _attachmentBar.Visibility = Visibility.Collapsed;
        }

        // Message bubbles
        _messageList.Children.Clear();
        if (m["more"]?.GetValue<bool>() ?? false)
        {
            _messageList.Children.Add(UiKit.PillButton(
                Loc.Tr("Messages", "Load older messages"),
                () => engine.Invoke("messages.loadOlder")));
        }

        if (m["messages"] is JsonArray msgs)
        {
            foreach (var item in msgs)
            {
                if (item is not JsonObject msg) continue;
                bool outgoing = (msg["outgoing"]?.GetValue<bool>() ?? false) || (msg["fromMe"]?.GetValue<bool>() ?? false);
                string body = msg["body"]?.GetValue<string>() ?? msg["text"]?.GetValue<string>() ?? "";
                string sender = msg["sender"]?.GetValue<string>() ?? "";
                string imgUrl = msg["image"]?.GetValue<string>() ?? "";
                bool failed = msg["failed"]?.GetValue<bool>() ?? false;

                var bubbleStack = new StackPanel { Spacing = 4, MaxWidth = 480 };
                if (!string.IsNullOrEmpty(sender) && !outgoing)
                    bubbleStack.Children.Add(UiKit.MutedText(sender, 11));
                var img = UiKit.LoadLocalImage(imgUrl, 480);
                if (img is not null)
                    bubbleStack.Children.Add(new Image { Source = img, MaxHeight = 240 });
                if (!string.IsNullOrEmpty(body))
                {
                    bubbleStack.Children.Add(new TextBlock
                    {
                        Text = body,
                        IsTextSelectionEnabled = true,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = UiKit.Brush(outgoing ? "NlOnPrimaryContainerBrush" : "NlOnSurfaceBrush")
                    });
                }
                if (failed) bubbleStack.Children.Add(UiKit.StatusBadge("Failed", error: true));

                var bubble = new Border
                {
                    Background = UiKit.Brush(outgoing ? "NlPrimaryContainerBrush" : "NlSurfaceContainerHighBrush"),
                    CornerRadius = ThemeManager.Current.RadiusMd,
                    Padding = new Thickness(12, 8, 12, 8),
                    HorizontalAlignment = outgoing ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                    Child = bubbleStack
                };
                _messageList.Children.Add(bubble);
            }
        }
    }

    private void OnComposeKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            bool enterSends = NectarLinkEngine.Current.Prefs["enterSends"]?.GetValue<bool>() ?? true;
            var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
            bool ctrlDown = (ctrl & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
            if ((enterSends && !ctrlDown) || (!enterSends && ctrlDown))
            {
                SendCurrentMessage();
                e.Handled = true;
            }
        }
    }

    private void SendCurrentMessage()
    {
        var engine = NectarLinkEngine.Current;
        string txt = _composeBox.Text ?? "";
        string openThread = engine.Messages["thread"]?.GetValue<string>() ?? "";
        string composeTo = engine.Messages["composeTo"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(txt) && !(engine.Messages["hasAttachment"]?.GetValue<bool>() ?? false))
            return;

        if (!string.IsNullOrEmpty(openThread))
        {
            engine.Invoke("messages.send", new { body = txt });
        }
        else if (!string.IsNullOrEmpty(composeTo))
        {
            engine.Invoke("messages.sendTo", new { recipients = new[] { composeTo }, body = txt });
        }
        _composeBox.Text = "";
    }

    private async void OnAttachFileClick()
    {
        if ((App.Current as App)?.MainWin is not { } mw) return;
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".gif");
        picker.FileTypeFilter.Add(".webp");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowChrome.GetHwnd(mw));
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            NectarLinkEngine.Current.Invoke("messages.attachFile", new { path = file.Path });
        }
    }

    private async void OnNewChatClick()
    {
        var numBox = new TextBox { Header = Loc.Tr("Messages", "Phone number"), PlaceholderText = "+1 555 123 4567" };
        var nameBox = new TextBox { Header = Loc.Tr("Messages", "Contact name (optional)") };
        var stack = new StackPanel { Spacing = 10, Children = { numBox, nameBox } };
        ContentDialog dlg = new()
        {
            Title = Loc.Tr("Messages", "New chat"),
            PrimaryButtonText = Loc.Tr("Messages", "Start"),
            CloseButtonText = Loc.Tr("Common", "Cancel"),
            XamlRoot = XamlRoot,
            Content = stack
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(numBox.Text))
        {
            NectarLinkEngine.Current.Invoke("messages.startChat", new
            {
                device = NectarLinkEngine.Current.CurrentDeviceId,
                number = numBox.Text.Trim(),
                name = nameBox.Text?.Trim() ?? ""
            });
        }
    }
}
