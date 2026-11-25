// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NectarLink.WinUI.Controls;
using NectarLink.WinUI.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace NectarLink.WinUI.Hud;

/// <summary>
/// Standalone WinUI 3 window rendering a mirrored Android screen (<c>device/0</c>)
/// or individual virtual-display Android app (<c>device/session</c>), with full
/// pointer, wheel/zoom, keyboard, paste, drag-drop, recording, screen-off, and
/// floating toolbar controls.
/// </summary>
public sealed partial class MirrorWindow : Window
{
    private readonly string _key;
    private readonly Grid _root = new();
    private readonly VideoSurfaceControl _video = new();
    private readonly StackPanel _toolbar = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 4,
        Padding = new Thickness(8, 6, 8, 6),
        CornerRadius = new CornerRadius(12),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(0, 10, 0, 0)
    };
    private readonly Border _statusOverlay = new()
    {
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(18, 12, 18, 12),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly Border _noticePill = new()
    {
        Visibility = Visibility.Collapsed,
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(12, 6, 12, 6),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Bottom,
        Margin = new Thickness(0, 0, 0, 52)
    };
    private readonly TextBlock _noticeText = new();
    private readonly StackPanel _navBar = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 12,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Bottom,
        Margin = new Thickness(0, 0, 0, 10)
    };

    private bool _closedByEngine;
    private bool _pointerDown;
    private uint _videoW = 1080;
    private uint _videoH = 2400;
    private bool _isAppWindow;
    private bool _landscape;
    private readonly DispatcherTimer _resizeDebounce = new() { Interval = TimeSpan.FromMilliseconds(180) };

    public MirrorWindow(string key)
    {
        _key = key;
        Title = "NectarLink Mirror";

        var appWin = WindowChrome.GetAppWindow(this);
        WindowChrome.ApplyAppIcon(this);
        appWin.Resize(new Windows.Graphics.SizeInt32(440, 880));
        appWin.Closing += (_, _) =>
        {
            if (!_closedByEngine)
                NectarLinkEngine.Current.Invoke("mirror.stop", new { key = _key });
            _video.DetachStream();
        };

        _root.Background = new SolidColorBrush(Colors.Black);
        _root.AllowDrop = true;
        _root.DragOver += (_, e) =>
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
                e.AcceptedOperation = DataPackageOperation.Copy;
        };
        _root.Drop += OnDropFiles;

        // Interactive video surface
        _video.AttachStream(_key);
        _video.FrameDimensionsChanged += (w, h) =>
        {
            _videoW = w;
            _videoH = h;
            _statusOverlay.Visibility = Visibility.Collapsed;
        };
        _video.PointerPressed += OnVideoPointerPressed;
        _video.PointerMoved += OnVideoPointerMoved;
        _video.PointerReleased += OnVideoPointerReleased;
        _video.PointerWheelChanged += OnVideoWheel;
        _root.Children.Add(_video);

        // Status overlay while waiting/asking/ended
        _root.Children.Add(_statusOverlay);

        // Notice toast inside window
        _noticePill.Background = UiKit.Brush("NlFlyoutSurfaceBrush");
        _noticeText.Foreground = UiKit.Brush("NlOnSurfaceBrush");
        _noticePill.Child = _noticeText;
        _root.Children.Add(_noticePill);

        // Toolbar backdrop
        var tbWrap = new Border
        {
            Background = UiKit.Brush("NlFlyoutSurfaceBrush"),
            BorderThickness = new Thickness(1),
            BorderBrush = UiKit.Brush("NlCardStrokeBrush"),
            CornerRadius = new CornerRadius(12),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 10, 0, 0),
            Child = _toolbar
        };
        _root.Children.Add(tbWrap);

        // Bottom Android navigation pills (Back, Home, Recents)
        _navBar.Children.Add(UiKit.IconButton("\uE72B", "Back", () => NectarLinkEngine.Current.Invoke("mirror.press", new { key = _key, name = "back" })));
        _navBar.Children.Add(UiKit.IconButton("\uE80F", "Home", () => NectarLinkEngine.Current.Invoke("mirror.press", new { key = _key, name = "home" })));
        _navBar.Children.Add(UiKit.IconButton("\uE737", "Recents", () => NectarLinkEngine.Current.Invoke("mirror.press", new { key = _key, name = "recents" })));
        _root.Children.Add(_navBar);

        _root.KeyDown += OnKeyDown;
        _root.CharacterReceived += OnCharacterReceived;
        _root.IsTabStop = true;

        _root.SizeChanged += (_, _) =>
        {
            _resizeDebounce.Stop();
            _resizeDebounce.Start();
        };
        _resizeDebounce.Tick += (_, _) =>
        {
            _resizeDebounce.Stop();
            if (_isAppWindow && _root.ActualWidth > 100 && _root.ActualHeight > 100)
            {
                NectarLinkEngine.Current.Invoke("mirror.resize", new
                {
                    key = _key,
                    width = (int)_root.ActualWidth,
                    height = (int)_root.ActualHeight
                });
            }
        };

        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated)
                NectarLinkEngine.Current.Invoke("mirror.syncClipboardOnFocus", new { key = _key });
        };

        Content = _root;
        WindowChrome.SetDarkTitleBar(this, true);
    }

    public void CloseFromEngine()
    {
        _closedByEngine = true;
        _video.DetachStream();
        Close();
    }

    public void UpdateFromState(JsonObject w)
    {
        string devName = w["deviceName"]?.GetValue<string>() ?? "Phone";
        string appName = w["app"]?.GetValue<string>() ?? "";
        _isAppWindow = !string.IsNullOrEmpty(appName);
        Title = _isAppWindow ? $"{appName} — {devName}" : $"{devName} — Screen Mirror";

        string phase = w["phase"]?.GetValue<string>() ?? "asking";
        string error = w["error"]?.GetValue<string>() ?? "";
        string notice = w["notice"]?.GetValue<string>() ?? "";
        bool muted = NectarLinkEngine.Current.Mirror["muted"]?.GetValue<bool>() ?? false;
        bool recording = w["recording"]?.GetValue<bool>() ?? false;
        bool stayAwake = w["stayAwake"]?.GetValue<bool>() ?? false;
        bool screenOff = w["screenOff"]?.GetValue<bool>() ?? false;
        bool flexible = w["flexible"]?.GetValue<bool>() ?? false;
        bool dark = w["dark"]?.GetValue<bool>() ?? false;
        bool hideToolbar = NectarLinkEngine.Current.Prefs["hideMirrorToolbar"]?.GetValue<bool>() ?? false;

        // Status overlay
        if (phase == "showing" && _video.HasFrame)
        {
            _statusOverlay.Visibility = Visibility.Collapsed;
        }
        else if (phase == "ended")
        {
            _statusOverlay.Visibility = Visibility.Visible;
            _statusOverlay.Background = UiKit.Brush("NlFlyoutSurfaceBrush");
            var st = new StackPanel { Spacing = 8 };
            st.Children.Add(UiKit.BodyText(string.IsNullOrEmpty(error) ? "Mirroring ended." : error));
            st.Children.Add(UiKit.PillButton("Reconnect", () => NectarLinkEngine.Current.Invoke("mirror.reopen", new { key = _key }), accent: true));
            _statusOverlay.Child = st;
        }
        else if (phase == "asking")
        {
            _statusOverlay.Visibility = Visibility.Visible;
            _statusOverlay.Background = UiKit.Brush("NlFlyoutSurfaceBrush");
            _statusOverlay.Child = new StackPanel
            {
                Spacing = 8,
                Children = { new ProgressRing { IsActive = true }, UiKit.BodyText($"Starting on {devName}…") }
            };
        }

        if (!string.IsNullOrEmpty(notice))
        {
            _noticeText.Text = notice;
            _noticePill.Visibility = Visibility.Visible;
        }
        else
        {
            _noticePill.Visibility = Visibility.Collapsed;
        }

        // Toolbar buttons
        _toolbar.Visibility = hideToolbar ? Visibility.Collapsed : Visibility.Visible;
        _toolbar.Children.Clear();
        _toolbar.Children.Add(UiKit.IconButton("\uE8C8", "Copy screenshot to clipboard", () => NectarLinkEngine.Current.Invoke("mirror.screenshotClipboard", new { key = _key })));
        _toolbar.Children.Add(UiKit.IconButton("\uE74E", "Save screenshot PNG", () => NectarLinkEngine.Current.Invoke("mirror.screenshotFile", new { key = _key })));
        _toolbar.Children.Add(UiKit.IconButton(recording ? "\uE71A" : "\uE7C8", recording ? "Stop recording MP4" : "Record MP4", () => NectarLinkEngine.Current.Invoke("mirror.toggleRecording", new { key = _key }), accent: recording));
        _toolbar.Children.Add(UiKit.IconButton(muted ? "\uE74F" : "\uE767", muted ? "Unmute sound" : "Mute sound", () => NectarLinkEngine.Current.Invoke("mirror.toggleSound")));
        _toolbar.Children.Add(UiKit.IconButton("\uE706", "Keep screen awake", () => NectarLinkEngine.Current.Invoke("mirror.setStayAwake", new { key = _key, on = !stayAwake }), accent: stayAwake));
        _toolbar.Children.Add(UiKit.IconButton("\uE708", "Turn phone screen off while mirroring", () => NectarLinkEngine.Current.Invoke("mirror.setScreenOff", new { key = _key, on = !screenOff }), accent: screenOff));
        _toolbar.Children.Add(UiKit.IconButton("\uE7AD", "Rotate orientation", () =>
        {
            _landscape = !_landscape;
            NectarLinkEngine.Current.Invoke("mirror.rotate", new { key = _key, landscape = _landscape });
        }));
        if (_isAppWindow)
        {
            _toolbar.Children.Add(UiKit.IconButton("\uE740", "Flexible display resolution", () => NectarLinkEngine.Current.Invoke("mirror.setFlexible", new { key = _key, on = !flexible }), accent: flexible));
            _toolbar.Children.Add(UiKit.IconButton("\uE790", "Force dark invert", () => NectarLinkEngine.Current.Invoke("mirror.setDark", new { key = _key, on = !dark }), accent: dark));
        }
        _toolbar.Children.Add(UiKit.IconButton("\uE77F", "Paste PC clipboard text", () => NectarLinkEngine.Current.Invoke("mirror.paste", new { key = _key })));
    }

    private (float X, float Y) NormalizePointer(PointerPoint pt)
    {
        double w = Math.Max(1.0, _video.ActualWidth);
        double h = Math.Max(1.0, _video.ActualHeight);
        double vidAspect = _videoH > 0 ? (double)_videoW / _videoH : (w / h);
        double ctrlAspect = w / h;

        double dispW, dispH, offX, offY;
        if (ctrlAspect > vidAspect)
        {
            dispH = h;
            dispW = h * vidAspect;
            offX = (w - dispW) / 2.0;
            offY = 0;
        }
        else
        {
            dispW = w;
            dispH = w / vidAspect;
            offX = 0;
            offY = (h - dispH) / 2.0;
        }
        float nx = (float)Math.Clamp((pt.Position.X - offX) / Math.Max(1.0, dispW), 0.0, 1.0);
        float ny = (float)Math.Clamp((pt.Position.Y - offY) / Math.Max(1.0, dispH), 0.0, 1.0);
        return (nx, ny);
    }

    private void OnVideoPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _root.Focus(FocusState.Pointer);
        var pt = e.GetCurrentPoint(_video);
        if (pt.Properties.IsRightButtonPressed)
        {
            NectarLinkEngine.Current.Invoke("mirror.press", new { key = _key, name = "back" });
            return;
        }
        _pointerDown = true;
        _video.CapturePointer(e.Pointer);
        var (x, y) = NormalizePointer(pt);
        NativeCore.SendMirrorTouch(_key, 0, x, y);
    }

    private void OnVideoPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerDown) return;
        var (x, y) = NormalizePointer(e.GetCurrentPoint(_video));
        NativeCore.SendMirrorTouch(_key, 1, x, y);
    }

    private void OnVideoPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerDown) return;
        _pointerDown = false;
        _video.ReleasePointerCapture(e.Pointer);
        var (x, y) = NormalizePointer(e.GetCurrentPoint(_video));
        NativeCore.SendMirrorTouch(_key, 2, x, y);
    }

    private void OnVideoWheel(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(_video);
        int delta = pt.Properties.MouseWheelDelta;
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        if ((ctrl & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0)
        {
            NectarLinkEngine.Current.Invoke("mirror.changeZoom", new { key = _key, steps = delta > 0 ? 1 : -1 });
            return;
        }
        var (x, y) = NormalizePointer(pt);
        NativeCore.SendMirrorScroll(_key, x, y, 0f, delta / 120f);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (ctrl && e.Key == VirtualKey.V)
        {
            NectarLinkEngine.Current.Invoke("mirror.paste", new { key = _key });
            e.Handled = true;
            return;
        }
        string? mapped = e.Key switch
        {
            VirtualKey.Back => "backspace",
            VirtualKey.Enter => "enter",
            VirtualKey.Escape => "back",
            VirtualKey.Left => "left",
            VirtualKey.Right => "right",
            VirtualKey.Up => "up",
            VirtualKey.Down => "down",
            VirtualKey.Tab => "tab",
            VirtualKey.Delete => "delete",
            VirtualKey.Home => "home",
            _ => null
        };
        if (mapped is not null)
        {
            NectarLinkEngine.Current.Invoke("mirror.press", new { key = _key, name = mapped });
            e.Handled = true;
        }
    }

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        char ch = args.Character;
        if (!char.IsControl(ch))
        {
            NectarLinkEngine.Current.Invoke("mirror.text", new { key = _key, text = ch.ToString() });
        }
    }

    private async void OnDropFiles(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var list = new List<string> { _key };
        foreach (var it in items)
        {
            if (!string.IsNullOrEmpty(it.Path)) list.Add(it.Path);
        }
        if (list.Count > 1)
            NectarLinkEngine.Current.Invoke("mirror.dropFiles", new { paths = list });
    }
}
