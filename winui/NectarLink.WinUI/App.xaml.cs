// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using NectarLink.WinUI.Hud;
using NectarLink.WinUI.Services;

namespace NectarLink.WinUI;

public partial class App : Application
{
    public MainWindow? MainWin { get; private set; }
    private readonly Dictionary<string, MirrorWindow> _mirrorWindows = new(StringComparer.Ordinal);
    private IslandWindow? _islandWindow;
    private ShelfWindow? _shelfWindow;
    private LaserOverlayWindow? _laserWindow;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Console.Error.WriteLine($"WINUI_UNHANDLED: {e.Message}\n{e.Exception}");
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            var engine = NectarLinkEngine.Current;
            engine.QuitRequested += () =>
            {
                CloseAllAuxWindows();
                try { MainWin?.ForceClose(); } catch { }
                Exit();
            };
            engine.Start(dispatcher);

            MainWin = new MainWindow();
            if (NativeCore.nl_should_start_minimized() == 0)
            {
                MainWin.RestoreAndActivate();
            }

            _islandWindow = new IslandWindow();
            _shelfWindow = new ShelfWindow();
            _laserWindow = new LaserOverlayWindow();

            if (Array.Exists(Environment.GetCommandLineArgs(), a => a == "--ui-check"))
            {
                dispatcher.TryEnqueue(() =>
                {
                    MainWin?.NavigateTo("settings:av");
                    MainWin?.NavigateTo("settings:av");
                    MainWin?.NavigateTo("settings:hub");
                    MainWin?.NavigateTo("settings:av");
                    MainWin?.NavigateTo("settings:hub");
                });
            }

            engine.StateChanged += _ => SyncFloatingWindows();
            engine.ActivateRequested += () =>
            {
                if (MainWin is not null) MainWin.RestoreAndActivate();
            };
            engine.ShelfToggleRequested += open =>
            {
                if (_shelfWindow is null || _shelfWindow.IsClosed)
                    _shelfWindow = new ShelfWindow();
                if (open) _shelfWindow.ShowNearEdge();
                else _shelfWindow.HideShelf();
            };

            SyncFloatingWindows();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WINUI_ONLAUNCHED_EX: {ex}");
            throw;
        }
    }

    private void SyncFloatingWindows()
    {
        var engine = NectarLinkEngine.Current;

        // Sync Mirror & App Windows
        var activeKeys = new HashSet<string>(StringComparer.Ordinal);
        if (engine.Mirror["windows"] is System.Text.Json.Nodes.JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is not System.Text.Json.Nodes.JsonObject w) continue;
                string key = w["key"]?.GetValue<string>() ?? "";
                if (string.IsNullOrEmpty(key)) continue;
                activeKeys.Add(key);
                if (!_mirrorWindows.TryGetValue(key, out var win))
                {
                    win = new MirrorWindow(key);
                    _mirrorWindows[key] = win;
                    win.Closed += (_, _) => _mirrorWindows.Remove(key);
                    win.Activate();
                }
                win.UpdateFromState(w);
            }
        }
        foreach (var stale in new List<string>(_mirrorWindows.Keys))
        {
            if (!activeKeys.Contains(stale) && _mirrorWindows.Remove(stale, out var w))
            {
                w.CloseFromEngine();
            }
        }

        _islandWindow?.SyncState();
        _shelfWindow?.SyncState();
        _laserWindow?.SyncState();
    }

    private void CloseAllAuxWindows()
    {
        foreach (var w in _mirrorWindows.Values)
        {
            try { w.CloseFromEngine(); } catch { }
        }
        _mirrorWindows.Clear();
        try { _islandWindow?.ForceClose(); } catch { }
        try { _shelfWindow?.ForceClose(); } catch { }
        try { _laserWindow?.ForceClose(); } catch { }
    }
}
