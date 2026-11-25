// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NectarLink.WinUI.Controls;
using NectarLink.WinUI.Dialogs;
using NectarLink.WinUI.Services;

namespace NectarLink.WinUI.Pages;

/// <summary>
/// Shown when setup was finished/skipped and no phone is paired yet.
/// </summary>
public sealed partial class WelcomePage : Page, IRefreshablePage
{
    private readonly TextBlock _nearbyHint = UiKit.MutedText("");

    public WelcomePage()
    {
        var center = new StackPanel
        {
            Spacing = 18,
            MaxWidth = 520,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(28)
        };

        center.Children.Add(new Image
        {
            Source = UiKit.LoadLocalImage("ms-appx:///Assets/brand/icon-512.png"),
            Width = 76,
            Height = 76,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        center.Children.Add(UiKit.TitleText(Loc.Tr("Welcome", "Your phone, on your PC"), 28));
        center.Children.Add(UiKit.MutedText(Loc.Tr(
            "Welcome",
            "Notifications, text messages, calls, photos, clipboard, and screen mirroring — direct over your Wi-Fi or USB cable, with no account and no cloud."), 14.5));

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        actions.Children.Add(UiKit.PillButton(
            Loc.Tr("Welcome", "Pair a phone"),
            async () => await AppDialogs.ShowPairingAsync(XamlRoot),
            glyph: "\uE710",
            accent: true));
        actions.Children.Add(UiKit.PillButton(
            Loc.Tr("Welcome", "Guided setup"),
            () => (App.Current as App)?.MainWin?.NavigateTo("walkthrough"),
            glyph: "\uE82D"));
        center.Children.Add(actions);
        center.Children.Add(_nearbyHint);

        Content = new Grid { Children = { center } };
    }

    private int _lastNearbyCount = -1;

    public void OnNavigatedTo() => Refresh();
    public void OnEngineChanged(uint mask)
    {
        if (EngineChanges.HasAny(mask, EngineChanges.Pairing | EngineChanges.Discovered | EngineChanges.Devices))
            Refresh();
    }

    private void Refresh()
    {
        int nearbyCount = (NectarLinkEngine.Current.Pairing["nearby"] as System.Text.Json.Nodes.JsonArray)?.Count ?? 0;
        if (nearbyCount == _lastNearbyCount) return;
        _lastNearbyCount = nearbyCount;
        _nearbyHint.Text = nearbyCount > 0
            ? Loc.Tr("Welcome", "%1 nearby device(s) ready to pair.", nearbyCount)
            : Loc.Tr("Welcome", "Open NectarLink on your phone and tap Pair a phone to connect.");
    }
}
