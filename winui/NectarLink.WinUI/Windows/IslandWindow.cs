// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Text.Json.Nodes;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NectarLink.WinUI.Controls;
using NectarLink.WinUI.Services;

namespace NectarLink.WinUI.Hud;

/// <summary>
/// Frameless, non-activating, always-on-top dynamic HUD pill (<c>Nectar Island</c>)
/// anchored along the top of the primary monitor matching IslandWindow.qml: surfaces
/// active phone calls, transfers, media playback controls, or incoming notifications.
/// </summary>
public sealed partial class IslandWindow : Window
{
    private readonly Border _pill = new()
    {
        CornerRadius = new CornerRadius(24),
        Padding = new Thickness(16, 10, 16, 10),
        BorderThickness = new Thickness(0)
    };
    private readonly StackPanel _content = new() { Orientation = Orientation.Horizontal, Spacing = 10 };
    private bool _visible;
    private bool _activatedOnce;
    private bool _forceClosing;
    public bool IsClosed { get; private set; }

    public IslandWindow()
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
                HideIsland();
            }
        };
        Closed += (_, _) => IsClosed = true;
        _pill.Child = _content;
        Content = new Grid { Padding = new Thickness(4), Children = { _pill } };
    }

    public void ForceClose()
    {
        if (IsClosed) return;
        _forceClosing = true;
        try { Close(); } catch { }
    }

    public void SyncState()
    {
        if (IsClosed) return;
        var engine = NectarLinkEngine.Current;
        var prefs = engine.Prefs;
        if (!(prefs["nectarIsland"]?.GetValue<bool>() ?? false))
        {
            HideIsland();
            return;
        }

        _content.Children.Clear();
        bool showCalls = prefs["nectarIslandCalls"]?.GetValue<bool>() ?? true;
        bool showTransfers = prefs["nectarIslandTransfers"]?.GetValue<bool>() ?? true;
        bool showMedia = prefs["nectarIslandMedia"]?.GetValue<bool>() ?? true;
        bool showNotifs = prefs["nectarIslandNotifications"]?.GetValue<bool>() ?? false;
        string look = prefs["nectarIslandLook"]?.GetValue<string>() ?? "dark";

        bool dark = look == "dark" || ThemeManager.Current.Dark;
        _pill.Background = new SolidColorBrush(dark ? ColorHelper.FromArgb(240, 22, 20, 18) : ColorHelper.FromArgb(242, 248, 245, 240));
        _pill.BorderBrush = UiKit.Brush("NlOutlineVariantBrush");

        bool hasContent = false;
        if (showCalls && (engine.Calls["active"]?.GetValue<bool>() ?? false))
        {
            hasContent = true;
            string caller = engine.Calls["caller"]?.GetValue<string>() ?? "Call";
            bool ringing = engine.Calls["ringing"]?.GetValue<bool>() ?? false;
            _content.Children.Add(new FontIcon { Glyph = "\uE717", FontSize = 16, Foreground = UiKit.Brush("NlPrimaryBrush") });
            _content.Children.Add(new TextBlock { Text = caller, VerticalAlignment = VerticalAlignment.Center });
            if (ringing)
                _content.Children.Add(UiKit.PillButton("Answer", () => engine.Invoke("calls.command", new { action = "answer" }), accent: true));
            _content.Children.Add(UiKit.PillButton(ringing ? "Decline" : "End", () => engine.Invoke("calls.command", new { action = "end" })));
        }
        else if (showTransfers && (engine.Transfers["active"]?.GetValue<int>() ?? 0) > 0)
        {
            hasContent = true;
            int cnt = engine.Transfers["active"]?.GetValue<int>() ?? 1;
            _content.Children.Add(new FontIcon { Glyph = "\uE898", FontSize = 16, Foreground = UiKit.Brush("NlPrimaryBrush") });
            _content.Children.Add(new TextBlock { Text = Loc.Tr("Island", "%1 active transfer(s)", cnt), VerticalAlignment = VerticalAlignment.Center });
        }
        else if (showMedia && engine.Media["rows"] is JsonArray mediaRows && mediaRows.Count > 0 && mediaRows[0] is JsonObject p && (p["playing"]?.GetValue<bool>() ?? false))
        {
            hasContent = true;
            string title = p["title"]?.GetValue<string>() ?? "Playing";
            string pDev = p["deviceId"]?.GetValue<string>() ?? "";
            string pid = p["playerId"]?.GetValue<string>() ?? "";
            _content.Children.Add(new FontIcon { Glyph = "\uE8D6", FontSize = 16, Foreground = UiKit.Brush("NlPrimaryBrush") });
            _content.Children.Add(new TextBlock { Text = title, MaxWidth = 180, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            _content.Children.Add(UiKit.IconButton("\uE769", "Pause", () => engine.Invoke("media.command", new { device = pDev, player = pid, action = "pause" })));
            _content.Children.Add(UiKit.IconButton("\uE893", "Next", () => engine.Invoke("media.command", new { device = pDev, player = pid, action = "next" })));
        }
        else if (showNotifs && !string.IsNullOrEmpty(engine.Notifications["latestTitle"]?.GetValue<string>()))
        {
            hasContent = true;
            string appName = engine.Notifications["latestApp"]?.GetValue<string>() ?? "";
            string title = engine.Notifications["latestTitle"]?.GetValue<string>() ?? "";
            _content.Children.Add(new FontIcon { Glyph = "\uEA8F", FontSize = 15 });
            _content.Children.Add(new TextBlock { Text = $"{appName}: {title}", MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        }

        if (!hasContent)
        {
            HideIsland();
            return;
        }

        if (!_visible)
        {
            var (wx, wy, ww, _) = WindowChrome.GetPrimaryWorkArea();
            int width = 340;
            int height = 56;
            string pos = prefs["nectarIslandPosition"]?.GetValue<string>() ?? "center";
            int x = pos switch
            {
                "left" => wx + 18,
                "right" => wx + ww - width - 18,
                _ => wx + (ww - width) / 2
            };
            var aw = WindowChrome.GetAppWindow(this);
            aw.MoveAndResize(new Windows.Graphics.RectInt32(x, wy + 12, width, height));
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
            _visible = true;
            Motion.PlayEntrance(_pill, offsetX: 0f, offsetY: -14f, durationMs: 200);
            return;
        }

        {
            var (wx, wy, ww, _) = WindowChrome.GetPrimaryWorkArea();
            int width = 340;
            int height = 56;
            string pos = prefs["nectarIslandPosition"]?.GetValue<string>() ?? "center";
            int x = pos switch
            {
                "left" => wx + 18,
                "right" => wx + ww - width - 18,
                _ => wx + (ww - width) / 2
            };
            var aw = WindowChrome.GetAppWindow(this);
            aw.MoveAndResize(new Windows.Graphics.RectInt32(x, wy + 12, width, height));
        }
    }

    private void HideIsland()
    {
        if (IsClosed || !_visible) return;
        _visible = false;
        try { WindowChrome.GetAppWindow(this).Hide(); } catch { }
    }
}
