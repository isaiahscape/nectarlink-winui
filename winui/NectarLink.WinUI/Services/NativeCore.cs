// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NectarLink.WinUI.Services;

/// <summary>
/// P/Invoke bindings to <c>nectarlink_winui_core.dll</c> plus thread-safe event
/// and video-frame fan-out for WinUI 3 surfaces.
/// </summary>
public static unsafe class NativeCore
{
    private const string DllName = "nectarlink_winui_core.dll";

    public const uint EventStateChanged = 1;
    public const uint EventToast = 2;
    public const uint EventActivate = 3;
    public const uint EventQuit = 4;
    public const uint EventOpenDoctor = 5;
    public const uint EventOpenWhatsNew = 6;
    public const uint EventOpenFilePicker = 7;
    public const uint EventOpenHandoffPicker = 8;
    public const uint EventUnlockResult = 9;
    public const uint EventPhoneToggleFailed = 10;
    public const uint EventReportRequested = 11;
    public const uint EventShelfToggle = 14;
    public const uint EventCommandPaletteOpen = 15;
    public const uint EventVideoClear = 16;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void EventCallbackDelegate(uint kind, IntPtr payloadUtf8, UIntPtr payloadLen, UIntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void VideoCallbackDelegate(
        IntPtr streamUtf8,
        uint width,
        uint height,
        IntPtr bgrxPtr,
        UIntPtr bgrxLen,
        UIntPtr userData);

    private static EventCallbackDelegate? _pinnedEventCb;
    private static VideoCallbackDelegate? _pinnedVideoCb;

    public static event Action<uint, string>? OnNativeEvent;

    private static readonly ConcurrentDictionary<string, Action<uint, uint, byte[]>> _videoListeners = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Action> _videoClearListeners = new(StringComparer.Ordinal);

    public static void SubscribeVideo(string stream, Action<uint, uint, byte[]> onFrame, Action onClear)
    {
        _videoListeners[stream] = onFrame;
        _videoClearListeners[stream] = onClear;
    }

    public static void UnsubscribeVideo(string stream)
    {
        _videoListeners.TryRemove(stream, out _);
        _videoClearListeners.TryRemove(stream, out _);
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nl_init(
        int argc,
        IntPtr[] argv,
        EventCallbackDelegate eventCb,
        VideoCallbackDelegate videoCb,
        UIntPtr userData);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void nl_on_ui_ready();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nl_should_start_minimized();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr nl_get_state(uint mask);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr nl_invoke(byte* cmdUtf8, byte* argsJsonUtf8);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void nl_free_string(IntPtr ptr);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void nl_mirror_touch(byte* keyUtf8, uint action, float x, float y);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void nl_mirror_scroll(byte* keyUtf8, float x, float y, float dx, float dy);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void nl_shutdown();

    public static int Initialize(string[] commandLineArgs)
    {
        _pinnedEventCb = HandleNativeEvent;
        _pinnedVideoCb = HandleVideoFrame;

        var ptrs = new IntPtr[commandLineArgs.Length];
        try
        {
            for (int i = 0; i < commandLineArgs.Length; i++)
            {
                byte[] utf8 = Encoding.UTF8.GetBytes(commandLineArgs[i] + "\0");
                ptrs[i] = Marshal.AllocHGlobal(utf8.Length);
                Marshal.Copy(utf8, 0, ptrs[i], utf8.Length);
            }
            return nl_init(ptrs.Length, ptrs, _pinnedEventCb, _pinnedVideoCb, UIntPtr.Zero);
        }
        finally
        {
            foreach (var p in ptrs)
            {
                if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
            }
        }
    }

    private static void HandleNativeEvent(uint kind, IntPtr payloadUtf8, UIntPtr payloadLen, UIntPtr userData)
    {
        string payload = payloadUtf8 != IntPtr.Zero && payloadLen != UIntPtr.Zero
            ? Marshal.PtrToStringUTF8(payloadUtf8, (int)payloadLen) ?? "{}"
            : "{}";

        if (kind == EventVideoClear)
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.TryGetProperty("stream", out var streamProp))
                {
                    var s = streamProp.GetString();
                    if (!string.IsNullOrEmpty(s) && _videoClearListeners.TryGetValue(s, out var clearCb))
                    {
                        clearCb();
                    }
                }
            }
            catch { /* malformed clear event */ }
            return;
        }

        OnNativeEvent?.Invoke(kind, payload);
    }

    private static void HandleVideoFrame(
        IntPtr streamUtf8,
        uint width,
        uint height,
        IntPtr bgrxPtr,
        UIntPtr bgrxLen,
        UIntPtr userData)
    {
        if (streamUtf8 == IntPtr.Zero || bgrxPtr == IntPtr.Zero || width == 0 || height == 0)
            return;

        string? stream = Marshal.PtrToStringUTF8(streamUtf8);
        if (string.IsNullOrEmpty(stream))
            return;

        if (_videoListeners.TryGetValue(stream, out var listener))
        {
            int len = checked((int)bgrxLen);
            byte[] copy = new byte[len];
            Marshal.Copy(bgrxPtr, copy, 0, len);
            listener(width, height, copy);
        }
    }

    public static JsonObject GetState(uint mask = uint.MaxValue)
    {
        IntPtr ptr = nl_get_state(mask);
        if (ptr == IntPtr.Zero)
            return new JsonObject();
        try
        {
            string? json = Marshal.PtrToStringUTF8(ptr);
            if (string.IsNullOrEmpty(json))
                return new JsonObject();
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        finally
        {
            nl_free_string(ptr);
        }
    }

    public static JsonObject Invoke(string command, object? args = null)
    {
        string argsJson = args switch
        {
            null => "{}",
            string raw => raw,
            JsonNode node => node.ToJsonString(),
            _ => JsonSerializer.Serialize(args)
        };

        byte[] cmdBytes = Encoding.UTF8.GetBytes(command + "\0");
        byte[] argBytes = Encoding.UTF8.GetBytes(argsJson + "\0");
        fixed (byte* pCmd = cmdBytes)
        fixed (byte* pArgs = argBytes)
        {
            IntPtr ptr = nl_invoke(pCmd, pArgs);
            if (ptr == IntPtr.Zero)
                return new JsonObject();
            try
            {
                string? res = Marshal.PtrToStringUTF8(ptr);
                if (string.IsNullOrEmpty(res))
                    return new JsonObject();
                return JsonNode.Parse(res) as JsonObject ?? new JsonObject();
            }
            finally
            {
                nl_free_string(ptr);
            }
        }
    }

    public static void SendMirrorTouch(string key, uint action, float x, float y)
    {
        byte[] keyBytes = Encoding.UTF8.GetBytes(key + "\0");
        fixed (byte* pKey = keyBytes)
        {
            nl_mirror_touch(pKey, action, x, y);
        }
    }

    public static void SendMirrorScroll(string key, float x, float y, float dx, float dy)
    {
        byte[] keyBytes = Encoding.UTF8.GetBytes(key + "\0");
        fixed (byte* pKey = keyBytes)
        {
            nl_mirror_scroll(pKey, x, y, dx, dy);
        }
    }
}
