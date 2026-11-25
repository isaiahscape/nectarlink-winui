// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Text.Json.Nodes;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NectarLink.WinUI.Controls;
using NectarLink.WinUI.Services;

namespace NectarLink.WinUI.Pages;

/// <summary>
/// Calls &amp; Contacts page matching CallsPage.qml: active/ringing call controls,
/// DTMF keypad, Bluetooth HFP / native PC Call Audio switching + microphone selector,
/// dual-SIM dialer, call log history, and searchable phone contacts.
/// </summary>
public sealed partial class CallsPage : Page, IRefreshablePage
{
    private readonly StackPanel _activeCallContainer = new();
    private readonly StackPanel _listPanel = new() { Spacing = 8 };
    private readonly TextBox _dialBox = new() { PlaceholderText = "Phone number…" };
    private readonly TextBox _contactSearchBox = new() { PlaceholderText = "Search call log or contacts…" };
    private readonly ComboBox _simBox = new() { Visibility = Visibility.Collapsed, MinWidth = 110 };
    private readonly StackPanel _gatePanel = new();

    private bool _showContacts;
    private bool _suppressSim;
    private string? _lastDev;

    public CallsPage()
    {
        _contactSearchBox.TextChanged += (_, _) => RenderList();
        _simBox.SelectionChanged += (_, _) =>
        {
            if (_suppressSim || _simBox.SelectedItem is not ComboBoxItem item || item.Tag is not string sim) return;
            NectarLinkEngine.Current.Invoke("calls.chooseSim", new { device = NectarLinkEngine.Current.CurrentDeviceId, sim });
        };

        var root = new Grid { Padding = new Thickness(14, 12, 24, 22), ColumnSpacing = 16, RowSpacing = 12 };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Header
        var hdr = new Grid();
        hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hdr.Children.Add(UiKit.PageTitle(Loc.Tr("Calls", "Calls & contacts")));
        var refreshBtn = UiKit.IconButton("\uE72C", Loc.Tr("Common", "Refresh"), () => NectarLinkEngine.Current.Invoke("calls.refresh"));
        refreshBtn.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(refreshBtn, 1);
        hdr.Children.Add(refreshBtn);
        Grid.SetColumnSpan(hdr, 2);
        root.Children.Add(hdr);

        // Left column: Active Call + Dialer Keypad
        var leftStack = new StackPanel { Spacing = 14 };
        leftStack.Children.Add(_gatePanel);
        leftStack.Children.Add(_activeCallContainer);
        leftStack.Children.Add(BuildDialerCard());
        var leftScroll = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = leftStack
        };
        Grid.SetRow(leftScroll, 1);
        root.Children.Add(leftScroll);

        // Right column: Call log / Contacts segmented list
        var rightGrid = new Grid { RowSpacing = 10 };
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var segBar = new Grid { ColumnSpacing = 10 };
        segBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        segBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        tabs.Children.Add(UiKit.PillButton(Loc.Tr("Calls", "Call log"), () => { _showContacts = false; RenderList(); }, accent: true));
        tabs.Children.Add(UiKit.PillButton(Loc.Tr("Calls", "Contacts"), () =>
        {
            _showContacts = true;
            NectarLinkEngine.Current.Invoke("calls.ensureContacts", new { device = NectarLinkEngine.Current.CurrentDeviceId });
            RenderList();
        }));
        segBar.Children.Add(tabs);
        Grid.SetColumn(_contactSearchBox, 1);
        segBar.Children.Add(_contactSearchBox);
        rightGrid.Children.Add(segBar);

        var listScroll = new ScrollViewer { Content = _listPanel };
        Grid.SetRow(listScroll, 1);
        rightGrid.Children.Add(listScroll);

        var rightCard = UiKit.Card(rightGrid, new Thickness(14));
        Grid.SetRow(rightCard, 1);
        Grid.SetColumn(rightCard, 1);
        root.Children.Add(rightCard);

        Content = root;
    }

    private string _lastGateSig = string.Empty;
    private string _lastActiveCallSig = string.Empty;
    private string _lastListSig = string.Empty;

    public void OnNavigatedTo()
    {
        EnsureDevice();
        Render();
    }

    public void OnEngineChanged(uint mask)
    {
        if (!EngineChanges.HasAny(mask, EngineChanges.Calls | EngineChanges.Devices | EngineChanges.Capabilities | EngineChanges.Status))
            return;
        EnsureDevice();
        Render();
    }

    private void EnsureDevice()
    {
        var engine = NectarLinkEngine.Current;
        string dev = engine.CurrentDeviceId;
        if (!string.IsNullOrEmpty(dev) && _lastDev != dev)
        {
            _lastDev = dev;
            engine.Invoke("calls.open", new { device = dev });
        }
        string pending = engine.App["pendingDial"]?.GetValue<string>() ?? "";
        if (!string.IsNullOrEmpty(pending))
        {
            _dialBox.Text = pending;
            engine.Invoke("app.clearPendingDial");
        }
    }

    private UIElement BuildDialerCard()
    {
        var engine = NectarLinkEngine.Current;
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(UiKit.SectionLabel(Loc.Tr("Calls", "Dialer")));

        var numRow = new Grid { ColumnSpacing = 8 };
        numRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        numRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        numRow.Children.Add(_dialBox);
        Grid.SetColumn(_simBox, 1);
        numRow.Children.Add(_simBox);
        stack.Children.Add(numRow);

        var pad = new Grid { RowSpacing = 6, ColumnSpacing = 6 };
        for (int r = 0; r < 4; r++) pad.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int c = 0; c < 3; c++) pad.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        string[] keys = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "*", "0", "#"];
        for (int i = 0; i < keys.Length; i++)
        {
            string k = keys[i];
            var btn = UiKit.PillButton(k, () =>
            {
                if (engine.Calls["active"]?.GetValue<bool>() ?? false)
                    engine.Invoke("calls.command", new { action = "dtmf", digit = k });
                else
                    _dialBox.Text = (_dialBox.Text ?? "") + k;
            });
            btn.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetRow(btn, i / 3);
            Grid.SetColumn(btn, i % 3);
            pad.Children.Add(btn);
        }
        stack.Children.Add(pad);

        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        bottom.Children.Add(UiKit.PillButton(Loc.Tr("Calls", "Call"), () =>
        {
            string num = _dialBox.Text?.Trim() ?? "";
            if (!string.IsNullOrEmpty(num))
                engine.Invoke("calls.dial", new { device = engine.CurrentDeviceId, number = num });
        }, glyph: "\uE717", accent: true));
        bottom.Children.Add(UiKit.IconButton("\uE750", "Backspace", () =>
        {
            if (!string.IsNullOrEmpty(_dialBox.Text))
                _dialBox.Text = _dialBox.Text[..^1];
        }));
        stack.Children.Add(bottom);
        return UiKit.Card(stack);
    }

    private void Render(bool force = false)
    {
        var engine = NectarLinkEngine.Current;
        var c = engine.Calls;

        string gateSig = $"{engine.CurrentDeviceId}|{engine.FeatureState("messages.calls")}|{c["sims"]?.ToJsonString()}|{c["sim"]?.ToJsonString()}";
        if (force || gateSig != _lastGateSig)
        {
            _lastGateSig = gateSig;
            _gatePanel.Children.Clear();
            var gate = UiKit.FeatureGateCard("messages.calls", Loc.Tr("Calls", "Calls & Call Log permissions"));
            if (gate.Visibility == Visibility.Visible) _gatePanel.Children.Add(gate);

            // Dual SIM picker
            _suppressSim = true;
            _simBox.Items.Clear();
            if (c["sims"] is JsonArray sims && sims.Count > 1)
            {
                string cur = c["sim"]?.GetValue<string>() ?? "";
                int sel = 0;
                for (int i = 0; i < sims.Count; i++)
                {
                    string sid = sims[i]?["id"]?.GetValue<string>() ?? "";
                    string slbl = sims[i]?["label"]?.GetValue<string>() ?? $"SIM {i + 1}";
                    _simBox.Items.Add(new ComboBoxItem { Content = slbl, Tag = sid });
                    if (sid == cur) sel = i;
                }
                _simBox.SelectedIndex = sel;
                _simBox.Visibility = Visibility.Visible;
            }
            else _simBox.Visibility = Visibility.Collapsed;
            _suppressSim = false;
        }

        string activeSig = $"{c["active"]?.ToJsonString()}|{c["caller"]?.ToJsonString()}|{c["number"]?.ToJsonString()}|{c["ringing"]?.ToJsonString()}|{c["muted"]?.ToJsonString()}|{c["speaker"]?.ToJsonString()}|{c["held"]?.ToJsonString()}|{c["callAudio"]?.ToJsonString()}|{c["hfpConnected"]?.ToJsonString()}|{c["callMics"]?.ToJsonString()}|{c["callMic"]?.ToJsonString()}";
        if (force || activeSig != _lastActiveCallSig)
        {
            _lastActiveCallSig = activeSig;
            RenderActiveCall(c);
        }
        RenderList(force);
    }

    private void RenderActiveCall(JsonObject c)
    {
        var engine = NectarLinkEngine.Current;
        _activeCallContainer.Children.Clear();
        if (!(c["active"]?.GetValue<bool>() ?? false)) return;

        string caller = c["caller"]?.GetValue<string>() ?? "Unknown";
        string number = c["number"]?.GetValue<string>() ?? "";
        bool ringing = c["ringing"]?.GetValue<bool>() ?? false;
        bool muted = c["muted"]?.GetValue<bool>() ?? false;
        bool speaker = c["speaker"]?.GetValue<bool>() ?? false;
        bool held = c["held"]?.GetValue<bool>() ?? false;
        bool canHold = c["canHold"]?.GetValue<bool>() ?? false;
        bool callAudio = c["callAudio"]?.GetValue<bool>() ?? false;
        bool callAudioAvail = c["callAudioAvailable"]?.GetValue<bool>() ?? false;
        bool hfpAvail = c["hfpAvailable"]?.GetValue<bool>() ?? false;
        bool hfpConn = c["hfpConnected"]?.GetValue<bool>() ?? false;

        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(UiKit.SectionLabel(ringing ? Loc.Tr("Calls", "Ringing") : Loc.Tr("Calls", "On call")));
        stack.Children.Add(UiKit.TitleText(caller, 20));
        if (!string.IsNullOrEmpty(number)) stack.Children.Add(UiKit.MutedText(number));

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (ringing) row.Children.Add(UiKit.PillButton("Answer", () => engine.Invoke("calls.command", new { action = "answer" }), accent: true));
        row.Children.Add(UiKit.PillButton(ringing ? "Decline" : "End", () => engine.Invoke("calls.command", new { action = "end" })));
        row.Children.Add(UiKit.PillButton(muted ? "Unmute" : "Mute", () => engine.Invoke("calls.command", new { action = "mute", on = !muted }), accent: muted));
        row.Children.Add(UiKit.PillButton(speaker ? "Earpiece" : "Speaker", () => engine.Invoke("calls.command", new { action = "speaker", on = !speaker }), accent: speaker));
        if (canHold) row.Children.Add(UiKit.PillButton(held ? "Resume" : "Hold", () => engine.Invoke("calls.command", new { action = "hold", on = !held }), accent: held));
        stack.Children.Add(row);

        var audioRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (callAudioAvail)
        {
            audioRow.Children.Add(UiKit.PillButton(
                callAudio ? Loc.Tr("Calls", "Audio on PC") : Loc.Tr("Calls", "Move call audio here"),
                () => engine.Invoke("calls.moveCallAudio", new { here = !callAudio }),
                accent: callAudio));
        }
        if (hfpAvail && !hfpConn)
        {
            audioRow.Children.Add(UiKit.PillButton(Loc.Tr("Calls", "Connect Bluetooth audio"), () => engine.Invoke("calls.connectPcAudio", new { device = engine.CurrentDeviceId })));
        }
        if (audioRow.Children.Count > 0) stack.Children.Add(audioRow);

        if (c["callMics"] is JsonArray mics && mics.Count > 0)
        {
            string curMic = c["callMic"]?.GetValue<string>() ?? "";
            var micBox = new ComboBox { Header = Loc.Tr("Calls", "Microphone on PC"), HorizontalAlignment = HorizontalAlignment.Stretch };
            int sel = 0;
            for (int i = 0; i < mics.Count; i++)
            {
                string mid = mics[i]?["id"]?.GetValue<string>() ?? "";
                string mname = mics[i]?["name"]?.GetValue<string>() ?? "";
                micBox.Items.Add(new ComboBoxItem { Content = mname, Tag = mid });
                if (mid == curMic) sel = i;
            }
            micBox.SelectedIndex = sel;
            micBox.SelectionChanged += (_, _) =>
            {
                if (micBox.SelectedItem is ComboBoxItem ci && ci.Tag is string id)
                    engine.Invoke("calls.chooseMic", new { id });
            };
            stack.Children.Add(micBox);
        }

        _activeCallContainer.Children.Add(UiKit.Card(stack, highContrast: true));
    }

    private void RenderList(bool force = false)
    {
        var engine = NectarLinkEngine.Current;
        var c = engine.Calls;
        string q = (_contactSearchBox.Text ?? "").Trim();
        string devId = engine.CurrentDeviceId;
        string listSig = $"{devId}|{_showContacts}|{q}|{c["calls"]?.ToJsonString()}|{c["contacts"]?.ToJsonString()}";
        if (!force && listSig == _lastListSig) return;
        _lastListSig = listSig;

        _listPanel.Children.Clear();

        if (_showContacts)
        {
            if (c["contacts"] is JsonArray contacts && contacts.Count > 0)
            {
                foreach (var item in contacts)
                {
                    if (item is not JsonObject ct) continue;
                    string name = ct["name"]?.GetValue<string>() ?? "Contact";
                    string num = (ct["numbers"] as JsonArray)?[0]?["number"]?.GetValue<string>() ?? ct["number"]?.GetValue<string>() ?? "";
                    if (!string.IsNullOrEmpty(q) && !name.Contains(q, StringComparison.OrdinalIgnoreCase) && !num.Contains(q, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var row = new Grid { ColumnSpacing = 8 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.Children.Add(new StackPanel { Children = { new TextBlock { Text = name, FontWeight = FontWeights.SemiBold }, UiKit.MutedText(num, 12) } });
                    var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                    btns.Children.Add(UiKit.IconButton("\uE717", "Call", () => engine.Invoke("calls.dial", new { device = devId, number = num }), accent: true));
                    btns.Children.Add(UiKit.IconButton("\uE8BD", "Message", () =>
                    {
                        engine.Invoke("messages.startChat", new { device = devId, number = num, name });
                        (App.Current as App)?.MainWin?.NavigateTo("messages");
                    }));
                    Grid.SetColumn(btns, 1);
                    row.Children.Add(btns);
                    _listPanel.Children.Add(UiKit.Card(row, new Thickness(12, 8, 12, 8)));
                }
            }
            else
            {
                _listPanel.Children.Add(UiKit.MutedText(Loc.Tr("Calls", "No contacts synced yet.")));
            }
        }
        else
        {
            if (c["callLog"] is JsonArray log && log.Count > 0)
            {
                foreach (var item in log)
                {
                    if (item is not JsonObject e) continue;
                    string name = e["name"]?.GetValue<string>() ?? "";
                    string num = e["number"]?.GetValue<string>() ?? "";
                    string kind = e["kind"]?.GetValue<string>() ?? "";
                    string shown = string.IsNullOrEmpty(name) ? num : name;
                    if (!string.IsNullOrEmpty(q) && !shown.Contains(q, StringComparison.OrdinalIgnoreCase) && !num.Contains(q, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var row = new Grid { ColumnSpacing = 8 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var left = new StackPanel();
                    left.Children.Add(new TextBlock { Text = shown, FontWeight = FontWeights.SemiBold });
                    left.Children.Add(UiKit.MutedText($"{kind} {(string.IsNullOrEmpty(name) ? "" : num)}".Trim(), 12));
                    row.Children.Add(left);
                    var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                    btns.Children.Add(UiKit.IconButton("\uE717", "Call back", () => engine.Invoke("calls.dial", new { device = devId, number = num }), accent: true));
                    btns.Children.Add(UiKit.IconButton("\uE8C8", "Copy number", () => engine.Invoke("app.copyText", new { text = num })));
                    Grid.SetColumn(btns, 1);
                    row.Children.Add(btns);
                    _listPanel.Children.Add(UiKit.Card(row, new Thickness(12, 8, 12, 8)));
                }
                if (c["moreLog"]?.GetValue<bool>() ?? false)
                    _listPanel.Children.Add(UiKit.PillButton(Loc.Tr("Calls", "Load older calls"), () => engine.Invoke("calls.loadOlder")));
            }
            else
            {
                _listPanel.Children.Add(UiKit.MutedText(Loc.Tr("Calls", "No recent calls.")));
            }
        }
    }
}
