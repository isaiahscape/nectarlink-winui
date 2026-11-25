// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Text.Json.Nodes;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace NectarLink.WinUI.Services;

/// <summary>
/// Resolves NectarLink's native Windows 11 <c>mica</c> and <c>acrylic</c> themes
/// plus Light/Dark/System, OLED AMOLED pure black dark mode, Windows accent color
/// tinting, corner radii, and Segoe UI Variable typography.
/// </summary>
public sealed class ThemeManager
{
    public static ThemeManager Current { get; } = new();

    public event Action? ThemeUpdated;

    public string ThemeName { get; private set; } = "mica";
    public string ColorMode { get; private set; } = "system";
    public bool Dark { get; private set; } = true;
    public bool Amoled { get; private set; }
    public bool UseBackdrop { get; private set; } = true;
    public bool ReduceMotion { get; private set; }

    public bool IsAcrylic => ThemeName == "acrylic";
    public bool IsMica => !IsAcrylic;

    // Resolved color tokens matching docs/design/tokens.json
    public Color Primary { get; private set; } = ColorHelper.FromArgb(255, 96, 205, 255);
    public Color OnPrimary { get; private set; } = ColorHelper.FromArgb(255, 0, 43, 64);
    public Color PrimaryContainer { get; private set; } = ColorHelper.FromArgb(255, 27, 58, 75);
    public Color OnPrimaryContainer { get; private set; } = ColorHelper.FromArgb(255, 207, 235, 255);
    public Color SecondaryContainer { get; private set; } = ColorHelper.FromArgb(255, 46, 50, 54);
    public Color OnSecondaryContainer { get; private set; } = ColorHelper.FromArgb(255, 240, 244, 248);
    public Color Surface { get; private set; } = ColorHelper.FromArgb(255, 31, 33, 36);
    public Color SurfaceContainerLow { get; private set; } = ColorHelper.FromArgb(0, 28, 30, 33);
    public Color SurfaceContainer { get; private set; } = ColorHelper.FromArgb(18, 255, 255, 255);
    public Color SurfaceContainerHigh { get; private set; } = ColorHelper.FromArgb(30, 255, 255, 255);
    public Color SurfaceContainerHighest { get; private set; } = ColorHelper.FromArgb(44, 255, 255, 255);
    public Color FlyoutSurface { get; private set; } = ColorHelper.FromArgb(230, 32, 34, 38);
    public Color CardStroke { get; private set; } = ColorHelper.FromArgb(24, 255, 255, 255);
    public Color ControlStroke { get; private set; } = ColorHelper.FromArgb(26, 255, 255, 255);
    public Color OnSurface { get; private set; } = ColorHelper.FromArgb(255, 245, 247, 250);
    public Color OnSurfaceVariant { get; private set; } = ColorHelper.FromArgb(255, 193, 198, 206);
    public Color Outline { get; private set; } = ColorHelper.FromArgb(255, 143, 149, 158);
    public Color OutlineVariant { get; private set; } = ColorHelper.FromArgb(255, 59, 62, 68);
    public Color Warning { get; private set; } = ColorHelper.FromArgb(255, 252, 225, 0);
    public Color Error { get; private set; } = ColorHelper.FromArgb(255, 255, 153, 164);
    public Color OnError { get; private set; } = ColorHelper.FromArgb(255, 38, 0, 4);
    public Color Scrim { get; private set; } = ColorHelper.FromArgb(128, 0, 0, 0);

    // Radii & Font Families
    public CornerRadius RadiusXs { get; private set; } = new(4);
    public CornerRadius RadiusSm { get; private set; } = new(6);
    public CornerRadius RadiusMd { get; private set; } = new(8);
    public CornerRadius RadiusLg { get; private set; } = new(10);
    public CornerRadius RadiusXl { get; private set; } = new(14);

    public string FontUi { get; private set; } = "Segoe UI Variable Text, Segoe UI, Figtree";
    public string FontDisplay { get; private set; } = "Segoe UI Variable Display, Segoe UI, Figtree";
    public string FontMono { get; private set; } = "Cascadia Mono, Space Mono";

    private JsonObject? _tokens;
    private string _lastVisualSignature = string.Empty;
    private UISettings? _uiSettings;

    public event Action? SystemColorsChanged;

    public void EnsureSystemAccentWatcher()
    {
        if (_uiSettings is not null) return;
        try
        {
            _uiSettings = new UISettings();
            _uiSettings.ColorValuesChanged += (_, _) => SystemColorsChanged?.Invoke();
        }
        catch { }
    }

    public void SetTokens(JsonObject? tokens)
    {
        if (tokens is not null) _tokens = tokens;
    }

    public void UpdateFromEngine(JsonObject? app, JsonObject? prefs, JsonObject? media)
    {
        if (prefs is not null)
        {
            string rawTheme = prefs["theme"]?.GetValue<string>() ?? "mica";
            ThemeName = string.Equals(rawTheme, "acrylic", StringComparison.OrdinalIgnoreCase)
                ? "acrylic"
                : "mica";
            ColorMode = prefs["colorMode"]?.GetValue<string>() ?? "system";
            Amoled = prefs["amoled"]?.GetValue<bool>() ?? false;
            UseBackdrop = prefs["backdrop"]?.GetValue<bool>() ?? true;
        }
        bool sysDark = app?["systemDark"]?.GetValue<bool>() ?? true;
        ReduceMotion = (app?["reduceMotion"]?.GetValue<bool>() ?? false)
            || (app?["powerSaver"]?.GetValue<bool>() ?? false);

        Dark = ColorMode switch
        {
            "light" => false,
            "dark" => true,
            _ => sysDark
        };

        ResolvePalette(app);
        ResolveShapes();

        string sig = $"{ThemeName}|{Dark}|{Amoled}|{UseBackdrop}|{ReduceMotion}|{Primary}|{OnPrimary}|{PrimaryContainer}|{OnPrimaryContainer}|{Surface}|{SurfaceContainerLow}|{SurfaceContainer}|{SurfaceContainerHigh}|{SurfaceContainerHighest}|{FlyoutSurface}|{CardStroke}|{OutlineVariant}|{RadiusMd}";
        if (sig == _lastVisualSignature)
            return;
        _lastVisualSignature = sig;

        ApplyToResourceDictionary();
        ThemeUpdated?.Invoke();
        _ = media;
    }

    private void ResolvePalette(JsonObject? app)
    {
        string modeKey = Dark ? "dark" : "light";
        string themeKey = IsAcrylic ? "acrylic" : "mica";

        var variant = _tokens?["themes"]?[themeKey]?["variants"]?[modeKey] as JsonObject;
        ApplyPaletteJson(variant);
        ApplyWindowsSystemAccent(app);

        // OLED pure black override in dark mode (for Mica or when backdrop is off)
        if (Dark && Amoled && !IsAcrylic)
        {
            Surface = ColorHelper.FromArgb(255, 0, 0, 0);
            SurfaceContainerLow = ColorHelper.FromArgb(255, 0, 0, 0);
            SurfaceContainer = ColorHelper.FromArgb(255, 16, 16, 18);
            SurfaceContainerHigh = ColorHelper.FromArgb(255, 26, 26, 30);
            SurfaceContainerHighest = ColorHelper.FromArgb(255, 38, 38, 42);
            FlyoutSurface = ColorHelper.FromArgb(245, 18, 18, 22);
            CardStroke = ColorHelper.FromArgb(255, 34, 34, 38);
            ControlStroke = ColorHelper.FromArgb(28, 255, 255, 255);
        }
        else
        {
            if (IsAcrylic)
                Surface = WithAlpha(Surface, Dark ? (byte)155 : (byte)170);

            SurfaceContainerLow = WithAlpha(SurfaceContainerLow, 0);
            if (Dark)
            {
                // Native WinUI 3 translucent glass card layers + subtle frosted specular border
                SurfaceContainer = ColorHelper.FromArgb(18, 255, 255, 255);
                SurfaceContainerHigh = ColorHelper.FromArgb(30, 255, 255, 255);
                SurfaceContainerHighest = ColorHelper.FromArgb(44, 255, 255, 255);
                SecondaryContainer = ColorHelper.FromArgb(28, 255, 255, 255);
                PrimaryContainer = WithAlpha(PrimaryContainer, 150);
                CardStroke = ColorHelper.FromArgb(24, 255, 255, 255);
                ControlStroke = ColorHelper.FromArgb(26, 255, 255, 255);
                FlyoutSurface = ColorHelper.FromArgb(230, 32, 34, 38);
            }
            else
            {
                // Native WinUI 3 Light CardBackgroundFillColorDefault (#B3FFFFFF) frosted translucent layers
                SurfaceContainer = ColorHelper.FromArgb(172, 255, 255, 255);
                SurfaceContainerHigh = ColorHelper.FromArgb(204, 255, 255, 255);
                SurfaceContainerHighest = ColorHelper.FromArgb(228, 255, 255, 255);
                SecondaryContainer = ColorHelper.FromArgb(168, SecondaryContainer.R, SecondaryContainer.G, SecondaryContainer.B);
                PrimaryContainer = WithAlpha(PrimaryContainer, 204);
                CardStroke = ColorHelper.FromArgb(20, 0, 0, 0);
                ControlStroke = ColorHelper.FromArgb(20, 0, 0, 0);
                FlyoutSurface = ColorHelper.FromArgb(240, 249, 250, 252);
            }
        }

        var status = _tokens?["themes"]?[themeKey]?["status"]?[modeKey] as JsonObject;
        if (TryParseHex(status?["warning"]?.GetValue<string>(), out var w)) Warning = w;
        else Warning = Dark ? ColorHelper.FromArgb(255, 252, 225, 0) : ColorHelper.FromArgb(255, 148, 87, 0);

        if (TryParseHex(status?["error"]?.GetValue<string>(), out var err)) Error = err;
        else Error = Dark ? ColorHelper.FromArgb(255, 255, 153, 164) : ColorHelper.FromArgb(255, 196, 43, 28);

        if (TryParseHex(status?["onError"]?.GetValue<string>(), out var onErr)) OnError = onErr;
        else OnError = Dark ? ColorHelper.FromArgb(255, 38, 0, 4) : Colors.White;

        Scrim = ColorHelper.FromArgb(Dark ? (byte)128 : (byte)82, 0, 0, 0);
    }

    private void ApplyPaletteJson(JsonObject? obj)
    {
        if (obj is null) return;
        Color Get(string key, Color def) => TryParseHex(obj[key]?.GetValue<string>(), out var c) ? c : def;

        Primary = Get("primary", Primary);
        OnPrimary = Get("onPrimary", OnPrimary);
        PrimaryContainer = Get("primaryContainer", PrimaryContainer);
        OnPrimaryContainer = Get("onPrimaryContainer", OnPrimaryContainer);
        SecondaryContainer = Get("secondaryContainer", SecondaryContainer);
        OnSecondaryContainer = Get("onSecondaryContainer", OnSecondaryContainer);
        Surface = Get("surface", Surface);
        SurfaceContainerLow = Get("surfaceContainerLow", SurfaceContainerLow);
        SurfaceContainer = Get("surfaceContainer", SurfaceContainer);
        SurfaceContainerHigh = Get("surfaceContainerHigh", SurfaceContainerHigh);
        SurfaceContainerHighest = Get("surfaceContainerHighest", SurfaceContainerHighest);
        OnSurface = Get("onSurface", OnSurface);
        OnSurfaceVariant = Get("onSurfaceVariant", OnSurfaceVariant);
        Outline = Get("outline", Outline);
        OutlineVariant = Get("outlineVariant", OutlineVariant);
    }

    private void ApplyWindowsSystemAccent(JsonObject? app)
    {
        Color accentBase = default;
        Color accentLight2 = default;
        Color accentLight3 = default;
        Color accentDark1 = default;
        Color accentDark2 = default;
        bool hasUiSettings = false;

        try
        {
            _uiSettings ??= new UISettings();
            accentBase = _uiSettings.GetColorValue(UIColorType.Accent);
            accentLight2 = _uiSettings.GetColorValue(UIColorType.AccentLight2);
            accentLight3 = _uiSettings.GetColorValue(UIColorType.AccentLight3);
            accentDark1 = _uiSettings.GetColorValue(UIColorType.AccentDark1);
            accentDark2 = _uiSettings.GetColorValue(UIColorType.AccentDark2);
            hasUiSettings = accentBase.A > 0;
        }
        catch { }

        if (!hasUiSettings && TryParseSystemAccentJson(app?["systemAccent"]?.GetValue<string>(), out var jsonLight, out var jsonDark))
        {
            accentDark1 = jsonLight;
            accentLight2 = jsonDark;
            accentBase = Dark ? jsonDark : jsonLight;
            accentLight3 = Blend(accentLight2, Colors.White, 0.35);
            accentDark2 = Blend(accentDark1, Colors.Black, 0.3);
            hasUiSettings = true;
        }

        if (!hasUiSettings) return;

        Primary = Dark ? accentLight2 : accentDark1;
        double luminance = (0.299 * Primary.R + 0.587 * Primary.G + 0.114 * Primary.B) / 255.0;
        OnPrimary = luminance > 0.56
            ? ColorHelper.FromArgb(255, 18, 20, 24)
            : Colors.White;

        if (Dark)
        {
            PrimaryContainer = Blend(ColorHelper.FromArgb(255, 28, 30, 34), accentBase, 0.36);
            OnPrimaryContainer = accentLight3.A > 0 ? accentLight3 : Blend(Primary, Colors.White, 0.35);
        }
        else
        {
            PrimaryContainer = Blend(Colors.White, accentBase, 0.18);
            OnPrimaryContainer = accentDark2.A > 0 ? accentDark2 : Blend(Primary, Colors.Black, 0.35);
        }
    }

    private static bool TryParseSystemAccentJson(string? raw, out Color light, out Color dark)
    {
        light = default;
        dark = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        // Support both plain "#RRGGBB" and Rust's serialized {"light":"#...","dark":"#..."} object string
        if (TryParseHex(raw, out var single))
        {
            light = single;
            dark = single;
            return true;
        }
        try
        {
            if (JsonNode.Parse(raw) is JsonObject obj)
            {
                bool hasLight = TryParseHex(obj["light"]?.GetValue<string>(), out light);
                bool hasDark = TryParseHex(obj["dark"]?.GetValue<string>(), out dark);
                if (hasLight && !hasDark) dark = light;
                if (hasDark && !hasLight) light = dark;
                return hasLight || hasDark;
            }
        }
        catch { }
        return false;
    }

    private static Color Blend(Color bg, Color fg, double t)
    {
        byte r = (byte)Math.Clamp(Math.Round(bg.R + (fg.R - bg.R) * t), 0, 255);
        byte g = (byte)Math.Clamp(Math.Round(bg.G + (fg.G - bg.G) * t), 0, 255);
        byte b = (byte)Math.Clamp(Math.Round(bg.B + (fg.B - bg.B) * t), 0, 255);
        return ColorHelper.FromArgb(255, r, g, b);
    }

    private void ResolveShapes()
    {
        string key = IsAcrylic ? "acrylic" : "mica";
        var shape = _tokens?["themes"]?[key]?["shape"] as JsonObject;
        RadiusXs = new CornerRadius(shape?["xs"]?.GetValue<double>() ?? 4);
        RadiusSm = new CornerRadius(shape?["sm"]?.GetValue<double>() ?? 6);
        RadiusMd = new CornerRadius(shape?["md"]?.GetValue<double>() ?? 8);
        RadiusLg = new CornerRadius(shape?["lg"]?.GetValue<double>() ?? (IsAcrylic ? 12 : 10));
        RadiusXl = new CornerRadius(shape?["xl"]?.GetValue<double>() ?? (IsAcrylic ? 16 : 14));
    }

    private void ApplyToResourceDictionary()
    {
        if (Application.Current?.Resources is not { } res) return;
        SetBrush(res, "NlPrimaryBrush", Primary);
        SetBrush(res, "NlOnPrimaryBrush", OnPrimary);
        SetBrush(res, "NlPrimaryContainerBrush", PrimaryContainer);
        SetBrush(res, "NlOnPrimaryContainerBrush", OnPrimaryContainer);
        SetBrush(res, "NlSecondaryContainerBrush", SecondaryContainer);
        SetBrush(res, "NlOnSecondaryContainerBrush", OnSecondaryContainer);
        SetBrush(res, "NlSurfaceBrush", Surface);
        SetBrush(res, "NlSurfaceContainerLowBrush", SurfaceContainerLow);
        SetBrush(res, "NlSurfaceContainerBrush", SurfaceContainer);
        SetBrush(res, "NlSurfaceContainerHighBrush", SurfaceContainerHigh);
        SetBrush(res, "NlSurfaceContainerHighestBrush", SurfaceContainerHighest);
        SetBrush(res, "NlFlyoutSurfaceBrush", FlyoutSurface);
        SetBrush(res, "NlCardStrokeBrush", CardStroke);
        SetBrush(res, "NlControlStrokeBrush", ControlStroke);
        SetBrush(res, "NlOnSurfaceBrush", OnSurface);
        SetBrush(res, "NlOnSurfaceVariantBrush", OnSurfaceVariant);
        SetBrush(res, "NlOutlineBrush", Outline);
        SetBrush(res, "NlOutlineVariantBrush", OutlineVariant);
        SetBrush(res, "NlWarningBrush", Warning);
        SetBrush(res, "NlErrorBrush", Error);
        SetBrush(res, "NlOnErrorBrush", OnError);
        SetBrush(res, "NlScrimBrush", Scrim);

        SetBrush(res, "ToggleSwitchFillOn", Primary);
        SetBrush(res, "ToggleSwitchFillOnPointerOver", Primary);
        SetBrush(res, "ToggleSwitchFillOnPressed", Primary);
        SetBrush(res, "ToggleSwitchKnobFillOn", OnPrimary);
        SetBrush(res, "ToggleSwitchKnobFillOnPointerOver", OnPrimary);
        SetBrush(res, "ToggleSwitchKnobFillOnPressed", OnPrimary);

        res["NlPrimaryColor"] = Primary;
        res["NlSurfaceColor"] = Surface;
        res["NlSurfaceContainerColor"] = SurfaceContainer;
        res["NlRadiusXs"] = RadiusXs;
        res["NlRadiusSm"] = RadiusSm;
        res["NlRadiusMd"] = RadiusMd;
        res["NlRadiusLg"] = RadiusLg;
        res["NlRadiusXl"] = RadiusXl;
        res["NlFontUi"] = new FontFamily(FontUi);
        res["NlFontDisplay"] = new FontFamily(FontDisplay);
        res["NlFontMono"] = new FontFamily(FontMono);
    }

    private static void SetBrush(ResourceDictionary res, string key, Color c)
    {
        if (res.TryGetValue(key, out var existing) && existing is SolidColorBrush scb)
            scb.Color = c;
        else
            res[key] = new SolidColorBrush(c);
    }

    public static bool TryParseHex(string? hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        string s = hex.Trim().TrimStart('#');
        if (s.Length == 6 && uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out uint rgb))
        {
            color = ColorHelper.FromArgb(255, (byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
            return true;
        }
        if (s.Length == 8 && uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out uint argb))
        {
            color = ColorHelper.FromArgb((byte)((argb >> 24) & 0xFF), (byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));
            return true;
        }
        return false;
    }

    public static Color WithAlpha(Color c, byte alpha) => ColorHelper.FromArgb(alpha, c.R, c.G, c.B);

    /// <summary>
    /// Stable key identifying the active compositor backdrop kind so MainWindow
    /// never tears down and recreates SystemBackdrop on unrelated state/page updates.
    /// </summary>
    public string ActiveBackdropKey
    {
        get
        {
            if (IsAcrylic)
                return (UseBackdrop && DesktopAcrylicController.IsSupported()) ? $"acrylic:{(Dark ? "dark" : "light")}" : "none";
            return (UseBackdrop && !(Dark && Amoled) && MicaController.IsSupported()) ? "mica-base" : "none";
        }
    }

    public SystemBackdrop? CreateWindowBackdrop()
    {
        return ActiveBackdropKey switch
        {
            "acrylic:dark" => new TranslucentAcrylicBackdrop(dark: true),
            "acrylic:light" => new TranslucentAcrylicBackdrop(dark: false),
            "mica-base" => new MicaBackdrop { Kind = MicaKind.Base },
            _ => null
        };
    }

    /// <summary>
    /// Thin frosted Desktop Acrylic backdrop (<see cref="DesktopAcrylicKind.Thin"/>)
    /// initialized with a valid <c>Windows.System.DispatcherQueue</c> and tuned
    /// luminosity/tint opacity so blurred desktop wallpaper and windows show
    /// cleanly through the window and translucent cards.
    /// </summary>
    private sealed class TranslucentAcrylicBackdrop : SystemBackdrop
    {
        private readonly bool _dark;
        private DesktopAcrylicController? _controller;
        private SystemBackdropConfiguration? _config;

        public TranslucentAcrylicBackdrop(bool dark) => _dark = dark;

        protected override void OnTargetConnected(
            Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop connectedTarget,
            XamlRoot xamlRoot)
        {
            base.OnTargetConnected(connectedTarget, xamlRoot);
            if (_controller is not null) return;

            EnsureWindowsSystemDispatcherQueue();

            _config = new SystemBackdropConfiguration
            {
                IsInputActive = true,
                Theme = _dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light
            };

            _controller = new DesktopAcrylicController
            {
                Kind = DesktopAcrylicKind.Thin,
                TintColor = _dark ? ColorHelper.FromArgb(255, 24, 24, 28) : ColorHelper.FromArgb(255, 248, 250, 252),
                FallbackColor = _dark ? ColorHelper.FromArgb(255, 31, 33, 36) : ColorHelper.FromArgb(255, 243, 245, 248),
                TintOpacity = _dark ? 0.25f : 0.35f,
                LuminosityOpacity = _dark ? 0.55f : 0.65f
            };
            _controller.AddSystemBackdropTarget(connectedTarget);
            _controller.SetSystemBackdropConfiguration(_config);
        }

        protected override void OnTargetDisconnected(
            Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop disconnectedTarget)
        {
            base.OnTargetDisconnected(disconnectedTarget);
            if (_controller is null) return;
            _controller.RemoveSystemBackdropTarget(disconnectedTarget);
            _controller.ResetProperties();
            _controller.Dispose();
            _controller = null;
            _config = null;
        }

        protected override void OnDefaultSystemBackdropConfigurationChanged(
            Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop target,
            XamlRoot xamlRoot)
        {
            base.OnDefaultSystemBackdropConfigurationChanged(target, xamlRoot);
            if (_controller is not null && _config is not null)
            {
                var def = GetDefaultSystemBackdropConfiguration(target, xamlRoot);
                _config.IsHighContrast = def.IsHighContrast;
                _config.HighContrastBackgroundColor = def.HighContrastBackgroundColor;
                _config.Theme = def.Theme;
                _controller.SetSystemBackdropConfiguration(_config);
            }
        }
    }

    private static IntPtr _wsdqController;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        internal int dwSize;
        internal int threadType;
        internal int apartmentType;
    }

    [System.Runtime.InteropServices.DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(
        DispatcherQueueOptions options,
        ref IntPtr dispatcherQueueController);

    private static void EnsureWindowsSystemDispatcherQueue()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() is not null || _wsdqController != IntPtr.Zero)
            return;

        var options = new DispatcherQueueOptions
        {
            dwSize = System.Runtime.InteropServices.Marshal.SizeOf<DispatcherQueueOptions>(),
            threadType = 2,   // DQTYPE_THREAD_CURRENT
            apartmentType = 2 // DQTAT_COM_STA
        };
        _ = CreateDispatcherQueueController(options, ref _wsdqController);
    }
}
