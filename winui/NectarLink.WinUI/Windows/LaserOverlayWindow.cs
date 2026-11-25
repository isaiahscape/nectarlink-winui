// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using NectarLink.WinUI.Services;

namespace NectarLink.WinUI.Hud;

/// <summary>
/// Full-screen transparent, non-activating click-through overlay for the remote
/// phone laser pointer (<c>LaserOverlay.qml</c>).
/// </summary>
public sealed partial class LaserOverlayWindow : Window
{
    private readonly Canvas _canvas = new();
    private readonly Ellipse _dot = new()
    {
        Width = 22,
        Height = 22,
        StrokeThickness = 3
    };
    private bool _shown;
    private bool _activatedOnce;
    private bool _forceClosing;
    public bool IsClosed { get; private set; }

    public LaserOverlayWindow()
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
                _shown = false;
                try { appWin.Hide(); } catch { }
            }
        };
        Closed += (_, _) => IsClosed = true;
        _canvas.Background = new SolidColorBrush(Colors.Transparent);
        _canvas.Children.Add(_dot);
        Content = _canvas;
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
        var app = NectarLinkEngine.Current.App;
        bool active = app["laserActive"]?.GetValue<bool>() ?? false;
        if (!active)
        {
            if (_shown)
            {
                _shown = false;
                try { WindowChrome.GetAppWindow(this).Hide(); } catch { }
            }
            return;
        }

        double nx = app["laserX"]?.GetValue<double>() ?? 0.5;
        double ny = app["laserY"]?.GetValue<double>() ?? 0.5;
        var (mx, my, mw, mh) = WindowChrome.GetMonitorBounds();

        if (!_shown)
        {
            var aw = WindowChrome.GetAppWindow(this);
            aw.MoveAndResize(new Windows.Graphics.RectInt32(mx, my, mw, mh));
            if (!_activatedOnce)
            {
                _activatedOnce = true;
                Activate();
                WindowChrome.ConfigureFloatingHud(this, clickThrough: true);
            }
            else
            {
                aw.Show(false);
                WindowChrome.ConfigureFloatingHud(this, clickThrough: true);
            }
            _shown = true;
        }

        _dot.Fill = new SolidColorBrush(ColorHelper.FromArgb(220, 255, 68, 68));
        _dot.Stroke = new SolidColorBrush(Colors.White);
        Canvas.SetLeft(_dot, nx * mw - 11);
        Canvas.SetTop(_dot, ny * mh - 11);
    }
}
