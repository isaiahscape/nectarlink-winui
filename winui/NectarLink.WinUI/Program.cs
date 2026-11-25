// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using NectarLink.WinUI.Services;

namespace NectarLink.WinUI;

/// <summary>
/// Custom process entry point so CLI, elevated registration, Widget provider,
/// and secondary-instance forwarding execute cleanly before starting XAML.
/// </summary>
public static class Program
{
    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();

    [STAThread]
    public static int Main(string[] args)
    {
        XamlCheckProcessRequirements();

        string[] argv = Environment.GetCommandLineArgs();
        int initCode = NativeCore.Initialize(argv);
        if (initCode == 1)
            return 0;
        if (initCode != 0)
            return 1;

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

        NativeCore.nl_shutdown();
        return 0;
    }
}
