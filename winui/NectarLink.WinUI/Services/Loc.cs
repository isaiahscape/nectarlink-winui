// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;

namespace NectarLink.WinUI.Services;

/// <summary>
/// Runtime translation lookup matching Rust's <c>tr!("Context", "Text", args...)</c>
/// with <c>%1..%9</c> positional placeholders and <c>%%</c> escaping.
/// </summary>
public static class Loc
{
    private static readonly object LockObj = new();
    private static Dictionary<string, string> _entries = new(StringComparer.Ordinal);
    public static string ActiveLanguage { get; private set; } = "en";

    public static void LoadCatalog(JsonObject? catalog)
    {
        if (catalog is null) return;
        var next = new Dictionary<string, string>(StringComparer.Ordinal);
        string lang = catalog["language"]?.GetValue<string>() ?? "en";
        if (catalog["entries"] is JsonObject entries)
        {
            foreach (var kv in entries)
            {
                if (kv.Value is not null)
                    next[kv.Key] = kv.Value.GetValue<string>();
            }
        }
        lock (LockObj)
        {
            ActiveLanguage = lang;
            _entries = next;
        }
    }

    public static string Tr(string context, string source, params object[] args)
    {
        string template;
        lock (LockObj)
        {
            if (!_entries.TryGetValue($"{context}\n{source}", out template!))
                template = source;
        }
        if (args.Length == 0 && !template.Contains('%'))
            return template;

        var sb = new StringBuilder(template.Length + 16);
        for (int i = 0; i < template.Length; i++)
        {
            char c = template[i];
            if (c != '%')
            {
                sb.Append(c);
                continue;
            }
            if (i + 1 < template.Length)
            {
                char next = template[i + 1];
                if (next == '%')
                {
                    sb.Append('%');
                    i++;
                    continue;
                }
                if (next >= '1' && next <= '9')
                {
                    int idx = next - '1';
                    if (idx < args.Length)
                    {
                        sb.Append(args[idx]?.ToString() ?? string.Empty);
                    }
                    else
                    {
                        sb.Append('%').Append(next);
                    }
                    i++;
                    continue;
                }
            }
            sb.Append('%');
        }
        return sb.ToString();
    }
}
