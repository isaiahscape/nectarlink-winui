// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NectarLink.WinUI.Tests;

internal static unsafe class Program
{
    private const string DllName = "nectarlink_winui_core.dll";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void EventCallback(uint kind, IntPtr payloadUtf8, UIntPtr payloadLen, UIntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VideoCallback(IntPtr streamUtf8, uint width, uint height, IntPtr bgrxPtr, UIntPtr bgrxLen, UIntPtr userData);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nl_init(int argc, IntPtr[] argv, EventCallback eventCb, VideoCallback videoCb, UIntPtr userData);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void nl_on_ui_ready();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nl_should_start_minimized();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr nl_get_state(uint mask);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr nl_invoke(byte* cmd, byte* args);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void nl_free_string(IntPtr ptr);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void nl_shutdown();

    private static readonly List<(uint Kind, string Payload)> Events = [];

    private static string ReadAndFree(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return "";
        try { return Marshal.PtrToStringUTF8(ptr) ?? ""; }
        finally { nl_free_string(ptr); }
    }

    private static JsonObject Invoke(string cmd, object? args = null)
    {
        byte[] cb = Encoding.UTF8.GetBytes(cmd + "\0");
        byte[] ab = Encoding.UTF8.GetBytes((args is null ? "{}" : JsonSerializer.Serialize(args)) + "\0");
        fixed (byte* pc = cb)
        fixed (byte* pa = ab)
        {
            string s = ReadAndFree(nl_invoke(pc, pa));
            return JsonNode.Parse(s) as JsonObject ?? [];
        }
    }

    private static void Assert(bool cond, string message)
    {
        if (!cond) throw new Exception($"Assertion failed: {message}");
    }

    public static int Main()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"nl-winui-abi-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            EventCallback evCb = (kind, ptr, len, _) =>
            {
                string payload = ptr != IntPtr.Zero && len != UIntPtr.Zero
                    ? Marshal.PtrToStringUTF8(ptr, (int)len) ?? "{}"
                    : "{}";
                lock (Events) Events.Add((kind, payload));
            };
            VideoCallback vidCb = (_, _, _, _, _, _) => { };

            string[] args = ["NectarLink.WinUI.Tests.exe", "--data-dir", tempDir, "--minimized"];
            var ptrs = new IntPtr[args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                byte[] utf8 = Encoding.UTF8.GetBytes(args[i] + "\0");
                ptrs[i] = Marshal.AllocHGlobal(utf8.Length);
                Marshal.Copy(utf8, 0, ptrs[i], utf8.Length);
            }

            int rc = nl_init(ptrs.Length, ptrs, evCb, vidCb, UIntPtr.Zero);
            foreach (var p in ptrs) Marshal.FreeHGlobal(p);

            Assert(rc == 0, $"nl_init expected 0, got {rc}");
            Assert(nl_should_start_minimized() == 1, "expected minimized == 1");
            nl_on_ui_ready();

            // Verify full snapshot schema across all 20 slices
            var full = JsonNode.Parse(ReadAndFree(nl_get_state(uint.MaxValue))) as JsonObject;
            Assert(full is not null, "full state is JSON object");
            foreach (var key in new[]
            {
                "app", "devices", "pairing", "prefs", "deck", "notifications",
                "transfers", "media", "messages", "calls", "photos", "mirror",
                "webcam", "phoneMic", "photoBackup", "phoneInstall", "agents",
                "clipboard", "localsend", "i18n", "tokens", "report"
            })
            {
                Assert(full!.ContainsKey(key), $"full snapshot missing slice '{key}'");
            }

            // Verify design tokens structure includes Mica and Acrylic
            var themes = full!["tokens"]?["themes"] as JsonObject;
            Assert(themes is not null, "tokens.themes present");
            foreach (var t in new[] { "mica", "acrylic" })
                Assert(themes!.ContainsKey(t), $"tokens missing theme '{t}'");

            // Verify prefs round-trip via nl_invoke("prefs.set") for mica & acrylic + legacy fallback
            foreach (var themeName in new[] { "mica", "acrylic" })
            {
                var setTheme = Invoke("prefs.set", new { theme = themeName });
                Assert(setTheme["ok"]?.GetValue<bool>() == true, $"prefs.set theme={themeName} ok");
                var snap = JsonNode.Parse(ReadAndFree(nl_get_state(uint.MaxValue))) as JsonObject;
                Assert(snap?["prefs"]?["theme"]?.GetValue<string>() == themeName, $"theme updated to {themeName}");
            }
            var setRes = Invoke("prefs.set", new { theme = "mica", amoled = true, backdrop = true });
            Assert(setRes["ok"]?.GetValue<bool>() == true, "prefs.set ok");
            var after = JsonNode.Parse(ReadAndFree(nl_get_state(uint.MaxValue))) as JsonObject;
            Assert(after?["prefs"]?["theme"]?.GetValue<string>() == "mica", "theme updated to mica");
            Assert(after?["prefs"]?["amoled"]?.GetValue<bool>() == true, "amoled updated to true");
            Assert(after?["prefs"]?["backdrop"]?.GetValue<bool>() == true, "backdrop updated to true");

            // Verify timeline query & unknown command safety
            var tl = Invoke("timeline.query", new { search = "", kind = "", device = "", limit = 10 });
            Assert(tl.ContainsKey("rows"), "timeline.query returned rows");
            var bad = Invoke("nonexistent.command");
            Assert(bad["ok"]?.GetValue<bool>() == false, "unknown command returns ok=false cleanly");

            nl_shutdown();
            Console.WriteLine("PASS: NectarLink WinUI native C ABI + 22 JSON slices + prefs round-trip verified.");
            return 0;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
