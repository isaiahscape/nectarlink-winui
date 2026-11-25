// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace NectarLink.WinUI.Services;

/// <summary>
/// Win32 / AppWindow helpers for floating HUD windows (Island, Shelf, Laser),
/// monitor work-area bounds, dark titlebar hints, and native Shell drag-out.
/// </summary>
public static class WindowChrome
{
    private const int GwlExStyle = -20;
    private const uint WsExTopMost = 0x00000008;
    private const uint WsExTransparent = 0x00000020;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private static readonly IntPtr HwndTopMost = new(-1);

    private const int SwRestore = 9;
    private const int DwmwaUseImmersiveDarkMode = 20;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    public static IntPtr GetHwnd(Window window) => WindowNative.GetWindowHandle(window);

    public static AppWindow GetAppWindow(Window window)
    {
        var hwnd = GetHwnd(window);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        return AppWindow.GetFromWindowId(windowId);
    }

    public static void SetDarkTitleBar(Window window, bool dark)
    {
        var hwnd = GetHwnd(window);
        if (hwnd == IntPtr.Zero) return;
        int val = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref val, sizeof(int));
    }

    public static void TryHideFromSwitchers(AppWindow appWindow)
    {
        try
        {
            appWindow.IsShownInSwitchers = false;
        }
        catch
        {
            // E_NOTIMPL on some Windows sessions before activation; WS_EX_TOOLWINDOW handles this too.
        }
    }

    public static void ConfigureFloatingHud(Window window, bool clickThrough = false)
    {
        var hwnd = GetHwnd(window);
        if (hwnd == IntPtr.Zero) return;
        long ex = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        ex |= WsExToolWindow | WsExNoActivate | WsExTopMost;
        if (clickThrough) ex |= WsExTransparent;
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(ex));
        SetWindowPos(hwnd, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpFrameChanged);
    }

    public static void BringToFront(Window window)
    {
        var hwnd = GetHwnd(window);
        if (hwnd == IntPtr.Zero) return;
        if (IsIconic(hwnd))
            ShowWindow(hwnd, SwRestore);
        window.Activate();
        SetForegroundWindow(hwnd);
    }

    public static (int X, int Y, int Width, int Height) GetPrimaryWorkArea(Window? referenceWindow = null)
    {
        IntPtr mon = IntPtr.Zero;
        if (referenceWindow is not null)
            mon = MonitorFromWindow(GetHwnd(referenceWindow), 2); // MONITOR_DEFAULTTONEAREST
        if (mon == IntPtr.Zero)
            mon = MonitorFromWindow(IntPtr.Zero, 1); // MONITOR_DEFAULTTOPRIMARY

        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi))
        {
            return (
                mi.rcWork.Left,
                mi.rcWork.Top,
                mi.rcWork.Right - mi.rcWork.Left,
                mi.rcWork.Bottom - mi.rcWork.Top);
        }
        return (0, 0, 1920, 1080);
    }

    public static (int X, int Y, int Width, int Height) GetMonitorBounds(Window? referenceWindow = null)
    {
        IntPtr mon = referenceWindow is not null
            ? MonitorFromWindow(GetHwnd(referenceWindow), 2)
            : MonitorFromWindow(IntPtr.Zero, 1);
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi))
        {
            return (
                mi.rcMonitor.Left,
                mi.rcMonitor.Top,
                mi.rcMonitor.Right - mi.rcMonitor.Left,
                mi.rcMonitor.Bottom - mi.rcMonitor.Top);
        }
        return (0, 0, 1920, 1080);
    }
}
