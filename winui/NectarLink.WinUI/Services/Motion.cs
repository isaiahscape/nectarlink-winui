// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NectarLink.WinUI.Services;

/// <summary>
/// Shared hardware-accelerated Composition / implicit UIElement transitions
/// matching Windows 11 Fluent motion while honoring system Reduce Motion and
/// Power Saver preferences.
/// </summary>
public static class Motion
{
    private sealed class GenBox { public int Value; }
    private static readonly ConditionalWeakTable<UIElement, GenBox> EntranceGens = new();

    public static bool Enabled => !ThemeManager.Current.ReduceMotion;

    /// <summary>
    /// Attaches smooth implicit opacity, translation, and scale transitions to
    /// <paramref name="el"/> (or clears durations when Reduce Motion is active).
    /// </summary>
    public static void ConfigureTransitions(
        UIElement el,
        int opacityMs = 170,
        int translationMs = 200,
        int scaleMs = 140)
    {
        if (!Enabled)
        {
            el.OpacityTransition = null;
            el.TranslationTransition = null;
            el.ScaleTransition = null;
            return;
        }
        el.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(opacityMs) };
        el.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(translationMs) };
        el.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(scaleMs) };
    }

    /// <summary>
    /// Attaches a smooth background <see cref="BrushTransition"/> to a <see cref="Border"/>.
    /// </summary>
    public static void AttachBackgroundTransition(Border border, int ms = 180)
    {
        border.BackgroundTransition = Enabled
            ? new BrushTransition { Duration = TimeSpan.FromMilliseconds(ms) }
            : null;
    }

    /// <summary>
    /// Plays an entrance fade + directional slide on <paramref name="el"/> using
    /// GPU composition transitions. Safe before Loaded and across rapid re-triggers.
    /// </summary>
    public static void PlayEntrance(UIElement el, float offsetX = 0f, float offsetY = 14f, int durationMs = 200)
    {
        if (!Enabled)
        {
            el.Opacity = 1;
            el.Translation = Vector3.Zero;
            return;
        }

        var box = EntranceGens.GetOrCreateValue(el);
        int gen = ++box.Value;

        el.OpacityTransition = null;
        el.TranslationTransition = null;
        el.Opacity = 0;
        el.Translation = new Vector3(offsetX, offsetY, 0f);

        var dq = el.DispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
        if (dq is null)
        {
            el.Opacity = 1;
            el.Translation = Vector3.Zero;
            return;
        }

        dq.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            if (box.Value != gen) return;
            el.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(Math.Max(90, durationMs - 20)) };
            el.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(durationMs) };
            el.Opacity = 1;
            el.Translation = Vector3.Zero;
        });
    }

    /// <summary>
    /// Adds subtle Windows 11 press feedback (<c>0.975</c> scale on pointer down)
    /// centered on <paramref name="el"/>.
    /// </summary>
    public static void AttachPressFeedback(FrameworkElement el, float pressScale = 0.975f)
    {
        el.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(120) };
        void SyncCenter() =>
            el.CenterPoint = new Vector3((float)(el.ActualWidth * 0.5), (float)(el.ActualHeight * 0.5), 0f);

        el.SizeChanged += (_, _) => SyncCenter();
        el.PointerPressed += (_, _) =>
        {
            if (!Enabled) return;
            SyncCenter();
            el.Scale = new Vector3(pressScale, pressScale, 1f);
        };
        el.PointerReleased += (_, _) => el.Scale = Vector3.One;
        el.PointerExited += (_, _) => el.Scale = Vector3.One;
        el.PointerCaptureLost += (_, _) => el.Scale = Vector3.One;
    }
}
