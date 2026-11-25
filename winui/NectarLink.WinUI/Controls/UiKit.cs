// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using NectarLink.WinUI.Services;
using Windows.Foundation;
using Windows.UI;

namespace NectarLink.WinUI.Controls;

/// <summary>
/// Shared WinUI 3 styling and micro-component factory matching the Bloom / Hive /
/// Glass / Graphite / Fluent design system tokens.
/// </summary>
public static class UiKit
{
    public static SolidColorBrush Brush(string resourceKey)
        => Application.Current.Resources.TryGetValue(resourceKey, out var v) && v is SolidColorBrush b
            ? b
            : new SolidColorBrush(Colors.Transparent);

    public static Border Card(UIElement child, Thickness? padding = null, bool highContrast = false)
    {
        var border = new Border
        {
            Background = Brush(highContrast ? "NlSurfaceContainerHighBrush" : "NlSurfaceContainerBrush"),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush("NlCardStrokeBrush"),
            CornerRadius = ThemeManager.Current.RadiusSm,
            Padding = padding ?? new Thickness(16, 14, 16, 14),
            Child = child
        };
        Motion.AttachBackgroundTransition(border);
        return border;
    }

    public static TextBlock PageTitle(string text)
        => new()
        {
            Text = text,
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily(ThemeManager.Current.FontDisplay),
            Foreground = Brush("NlOnSurfaceBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };

    /// <summary>
    /// Windows 11 Settings-style row card: leading icon, title + optional description,
    /// and either a trailing control or a subtle navigation chevron when <paramref name="onClick"/> is supplied.
    /// </summary>
    public static FrameworkElement SettingsCard(
        string glyph,
        string title,
        string? subtitle = null,
        FrameworkElement? trailing = null,
        Action? onClick = null,
        bool accentIcon = false)
    {
        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon
        {
            Glyph = glyph,
            FontSize = 18,
            Width = 22,
            Foreground = Brush(accentIcon ? "NlPrimaryBrush" : "NlOnSurfaceBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        grid.Children.Add(icon);

        var textStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            Foreground = Brush("NlOnSurfaceBrush"),
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrEmpty(subtitle))
        {
            textStack.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 12,
                Foreground = Brush("NlOnSurfaceVariantBrush"),
                TextWrapping = TextWrapping.Wrap
            });
        }
        Grid.SetColumn(textStack, 1);
        grid.Children.Add(textStack);

        if (trailing is not null)
        {
            Grid.SetColumn(trailing, 2);
            grid.Children.Add(trailing);
        }
        else if (onClick is not null)
        {
            var chevron = new FontIcon
            {
                Glyph = "\uE76C",
                FontSize = 11,
                Foreground = Brush("NlOnSurfaceVariantBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(chevron, 2);
            grid.Children.Add(chevron);
        }

        var cardStroke = Brush("NlCardStrokeBrush");
        if (onClick is not null && trailing is null)
        {
            var btn = new Button
            {
                Content = grid,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                MinHeight = 54,
                Padding = new Thickness(16, 11, 16, 11),
                CornerRadius = ThemeManager.Current.RadiusSm,
                Background = Brush("NlSurfaceContainerBrush"),
                Foreground = Brush("NlOnSurfaceBrush"),
                BorderThickness = new Thickness(1),
                BorderBrush = cardStroke
            };
            btn.Resources["ButtonBorderBrushPointerOver"] = cardStroke;
            btn.Resources["ButtonBorderBrushPressed"] = cardStroke;
            Motion.AttachPressFeedback(btn, 0.99f);
            btn.Click += (_, _) => onClick();
            return btn;
        }

        var rowBorder = new Border
        {
            Background = Brush("NlSurfaceContainerBrush"),
            BorderThickness = new Thickness(1),
            BorderBrush = cardStroke,
            CornerRadius = ThemeManager.Current.RadiusSm,
            MinHeight = 54,
            Padding = new Thickness(16, 11, 16, 11),
            Child = grid
        };
        Motion.AttachBackgroundTransition(rowBorder);
        return rowBorder;
    }

    public static FrameworkElement ToggleSettingCard(
        string glyph,
        string title,
        string? subtitle,
        bool value,
        Action<bool> onChanged)
    {
        var ts = new ToggleSwitch
        {
            IsOn = value,
            OnContent = null,
            OffContent = null,
            MinWidth = 0,
            Padding = new Thickness(0),
            Margin = new Thickness(12, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        ts.Toggled += (_, _) => onChanged(ts.IsOn);
        return SettingsCard(glyph, title, subtitle, trailing: ts);
    }

    public static ScrollViewer HScroll(UIElement content) => new()
    {
        HorizontalScrollMode = ScrollMode.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollMode = ScrollMode.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Content = content
    };

    public static TextBlock TitleText(string text, double size = 22)
        => new()
        {
            Text = text,
            FontSize = size,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("NlOnSurfaceBrush"),
            TextWrapping = TextWrapping.Wrap
        };

    public static TextBlock SectionLabel(string text)
        => new()
        {
            Text = text,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("NlOnSurfaceVariantBrush")
        };

    public static TextBlock MutedText(string text, double size = 12.5)
        => new()
        {
            Text = text,
            FontSize = size,
            Foreground = Brush("NlOnSurfaceVariantBrush"),
            TextWrapping = TextWrapping.Wrap
        };

    public static TextBlock BodyText(string text, double size = 14)
        => new()
        {
            Text = text,
            FontSize = size,
            Foreground = Brush("NlOnSurfaceBrush"),
            TextWrapping = TextWrapping.Wrap
        };

    public static Button PillButton(string text, Action onClick, string? glyph = null, bool accent = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (!string.IsNullOrEmpty(glyph))
        {
            content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
        }
        if (!string.IsNullOrEmpty(text))
        {
            content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        }

        var btn = new Button
        {
            Content = content,
            CornerRadius = ThemeManager.Current.RadiusSm,
            Padding = new Thickness(13, 7, 13, 7),
            BorderThickness = new Thickness(0)
        };
        SetPillAccent(btn, accent);
        Motion.AttachPressFeedback(btn, 0.965f);
        btn.Click += (_, _) => onClick();
        return btn;
    }

    public static void SetPillAccent(Button btn, bool accent)
    {
        var bg = Brush(accent ? "NlPrimaryBrush" : "NlSurfaceContainerHighBrush");
        var fg = Brush(accent ? "NlOnPrimaryBrush" : "NlOnSurfaceBrush");
        var hoverBg = Brush(accent ? "NlPrimaryBrush" : "NlSurfaceContainerHighestBrush");
        btn.Background = bg;
        btn.Foreground = fg;
        btn.Resources["ButtonBackground"] = bg;
        btn.Resources["ButtonBackgroundPointerOver"] = hoverBg;
        btn.Resources["ButtonBackgroundPressed"] = hoverBg;
        btn.Resources["ButtonForeground"] = fg;
        btn.Resources["ButtonForegroundPointerOver"] = fg;
        btn.Resources["ButtonForegroundPressed"] = fg;
    }

    public static Button IconButton(string glyph, string tooltip, Action onClick, bool accent = false)
    {
        var btn = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 15 },
            CornerRadius = ThemeManager.Current.RadiusSm,
            Width = 34,
            Height = 34,
            Padding = new Thickness(0),
            Background = Brush(accent ? "NlPrimaryContainerBrush" : "NlSurfaceContainerHighBrush"),
            Foreground = Brush(accent ? "NlOnPrimaryContainerBrush" : "NlOnSurfaceBrush"),
            BorderThickness = new Thickness(0)
        };
        ToolTipService.SetToolTip(btn, tooltip);
        Motion.AttachPressFeedback(btn, 0.93f);
        btn.Click += (_, _) => onClick();
        return btn;
    }

    public static Border StatusBadge(string text, bool highlight = false, bool warning = false, bool error = false)
    {
        string bgKey = error ? "NlErrorBrush" : warning ? "NlWarningBrush" : highlight ? "NlPrimaryContainerBrush" : "NlSurfaceContainerHighestBrush";
        string fgKey = error ? "NlOnErrorBrush" : warning ? "NlOnPrimaryBrush" : highlight ? "NlOnPrimaryContainerBrush" : "NlOnSurfaceVariantBrush";
        return new Border
        {
            Background = Brush(bgKey),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3, 8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush(fgKey),
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    public static FrameworkElement FeatureGateCard(string featureKey, string? fallbackTitle = null)
    {
        string state = NectarLinkEngine.Current.FeatureState(featureKey);
        if (state == "available") return new Border { Visibility = Visibility.Collapsed };
        string reason = NectarLinkEngine.Current.FeatureReason(featureKey);
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(TitleText(fallbackTitle ?? Loc.Tr("Feature", "Feature needs attention"), 15));
        if (!string.IsNullOrEmpty(reason))
            stack.Children.Add(MutedText(reason));
        return Card(stack, highContrast: true);
    }

    private static readonly Dictionary<string, BitmapImage> LocalImageCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> LocalImageCacheOrder = new();

    public static ImageSource? LoadLocalImage(string? pathOrUrl, int decodeWidth = 0)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl)) return null;
        try
        {
            string normalized = pathOrUrl.Trim();
            if (normalized.StartsWith("ms-appx:///", StringComparison.OrdinalIgnoreCase))
            {
                string rel = normalized["ms-appx:///".Length..].Replace('/', '\\');
                string localAsset = System.IO.Path.Combine(AppContext.BaseDirectory, rel);
                if (System.IO.File.Exists(localAsset))
                    normalized = localAsset;
            }
            if (normalized.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
                normalized = Uri.UnescapeDataString(normalized[8..]).Replace('/', '\\');
            bool isAppx = normalized.StartsWith("ms-appx:", StringComparison.OrdinalIgnoreCase);
            if (!isAppx && !System.IO.File.Exists(normalized))
                return null;

            long mtime = isAppx ? 0 : System.IO.File.GetLastWriteTimeUtc(normalized).Ticks;
            string cacheKey = $"{normalized}|{mtime}|{decodeWidth}";
            if (LocalImageCache.TryGetValue(cacheKey, out var cached))
                return cached;

            while (LocalImageCache.Count >= 512 && LocalImageCacheOrder.Count > 0)
            {
                string oldest = LocalImageCacheOrder.Dequeue();
                LocalImageCache.Remove(oldest);
            }

            var bmp = new BitmapImage();
            if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
            bmp.UriSource = new Uri(normalized);
            LocalImageCache[cacheKey] = bmp;
            LocalImageCacheOrder.Enqueue(cacheKey);
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Renders an SVG path string generated by Rust <c>qr::encode</c> (<c>M x y h w v h ... z</c>)
    /// on a crisp WinUI <see cref="Path"/> scaled cleanly onto a white quiet zone.
    /// </summary>
    public static Border QrCodeView(string? svgPathData, int gridSize, double pixelSize = 180)
    {
        var box = new Border
        {
            Background = new SolidColorBrush(Colors.White),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12),
            Width = pixelSize + 24,
            Height = pixelSize + 24
        };
        if (string.IsNullOrWhiteSpace(svgPathData) || gridSize <= 0)
        {
            box.Child = new ProgressRing { IsActive = true, Width = 32, Height = 32 };
            return box;
        }
        var viewbox = new Viewbox
        {
            Width = pixelSize,
            Height = pixelSize,
            Stretch = Stretch.Uniform
        };
        var canvas = new Canvas { Width = gridSize, Height = gridSize };
        try
        {
            var xamlPath = (Microsoft.UI.Xaml.Shapes.Path)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                $"<Path xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Fill=\"Black\" Data=\"{System.Security.SecurityElement.Escape(svgPathData)}\" />");
            canvas.Children.Add(xamlPath);
        }
        catch { }
        viewbox.Child = canvas;
        box.Child = viewbox;
        return box;
    }

    /// <summary>
    /// Applies a device's cached phone wallpaper as a clipped <see cref="ImageBrush"/>
    /// when available, or falls back to the primary-container phone glyph badge.
    /// </summary>
    public static void ApplyDeviceWallpaperOrIcon(
        Border border,
        FontIcon? fallbackIcon,
        System.Text.Json.Nodes.JsonObject? device,
        int decodeWidth = 256)
    {
        string? wpPath = device?["wallpaperPath"]?.GetValue<string>();
        var bmp = LoadLocalImage(wpPath, decodeWidth);
        if (bmp is not null)
        {
            border.Background = new ImageBrush
            {
                ImageSource = bmp,
                Stretch = Stretch.UniformToFill
            };
            if (fallbackIcon is not null) fallbackIcon.Visibility = Visibility.Collapsed;
        }
        else
        {
            border.Background = Brush("NlPrimaryContainerBrush");
            if (fallbackIcon is not null) fallbackIcon.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Phone silhouette preview reflecting live screen aspect ratio, rounded corners,
    /// camera punch-hole/notch cutouts, fetched wallpaper or accent tint, and clock on HomePage.
    /// </summary>
    public static Grid PhoneSilhouette(System.Text.Json.Nodes.JsonObject? device, double height = 136)
    {
        double aspect = device?["screen"]?["aspect"]?.GetValue<double>() ?? 0.46;
        aspect = Math.Clamp(aspect, 0.4, 0.72);
        double width = Math.Round(height * aspect);

        var accentStr = device?["accent"]?.GetValue<string>();
        Color accent = ThemeManager.TryParseHex(accentStr, out var c) ? c : ThemeManager.Current.Primary;
        string? wpPath = device?["wallpaperPath"]?.GetValue<string>();
        var wpBmp = LoadLocalImage(wpPath, 320);

        Brush surfaceFill = wpBmp is not null
            ? new ImageBrush { ImageSource = wpBmp, Stretch = Stretch.UniformToFill }
            : new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = ThemeManager.WithAlpha(accent, 90), Offset = 0 },
                    new GradientStop { Color = ThemeManager.Current.SurfaceContainerHigh, Offset = 1 }
                }
            };

        var inner = new Grid();
        if (wpBmp is not null)
        {
            // Keep camera punch-hole and clock readable on bright wallpapers
            inner.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(11.5),
                Background = new LinearGradientBrush
                {
                    StartPoint = new Point(0.5, 0),
                    EndPoint = new Point(0.5, 0.65),
                    GradientStops =
                    {
                        new GradientStop { Color = Color.FromArgb(140, 0, 0, 0), Offset = 0 },
                        new GradientStop { Color = Color.FromArgb(40, 0, 0, 0), Offset = 0.5 },
                        new GradientStop { Color = Color.FromArgb(0, 0, 0, 0), Offset = 1 }
                    }
                }
            });
        }

        // Punch-hole camera dot
        inner.Children.Add(new Ellipse
        {
            Width = 5,
            Height = 5,
            Fill = new SolidColorBrush(Colors.Black),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 0, 0)
        });
        inner.Children.Add(new TextBlock
        {
            Text = DateTime.Now.ToString("HH:mm"),
            FontSize = Math.Max(11, Math.Round(height * 0.082)),
            FontWeight = FontWeights.SemiBold,
            Foreground = wpBmp is not null ? new SolidColorBrush(Colors.White) : Brush("NlOnSurfaceBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, Math.Round(height * 0.145), 0, 0)
        });

        var frame = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(2.5),
            BorderBrush = Brush("NlOutlineBrush"),
            Background = surfaceFill,
            Child = inner
        };

        var root = new Grid();
        root.Children.Add(frame);
        return root;
    }
}
