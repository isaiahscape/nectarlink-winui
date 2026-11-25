// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NectarLink.WinUI.Controls;
using NectarLink.WinUI.Services;

namespace NectarLink.WinUI.Dialogs;

/// <summary>
/// Modal WinUI 3 <see cref="ContentDialog"/> flows for Pairing, Connection Doctor,
/// Relay Diagnostics, Problem Reporting, What's New, Guide Docs, and Agent Approvals.
/// </summary>
public static class AppDialogs
{
    public static async Task ShowPairingAsync(XamlRoot xamlRoot)
    {
        var engine = NectarLinkEngine.Current;
        engine.Invoke("pairing.setNearbyShown", new { shown = true });
        engine.Invoke("pairing.start");

        var contentStack = new StackPanel { Spacing = 14, Width = 440 };
        ContentDialog dialog = new()
        {
            Title = Loc.Tr("Pairing", "Pair a device"),
            CloseButtonText = Loc.Tr("Common", "Close"),
            XamlRoot = xamlRoot,
            Content = new ScrollViewer { MaxHeight = 520, Content = contentStack }
        };

        void Render()
        {
            contentStack.Children.Clear();
            var p = engine.Pairing;
            string st = p["state"]?.GetValue<string>() ?? "idle";
            string peerName = p["peerName"]?.GetValue<string>() ?? "";
            string code = p["code"]?.GetValue<string>() ?? "";
            string qrPath = p["qrPath"]?.GetValue<string>() ?? "";
            int qrSize = p["qrSize"]?.GetValue<int>() ?? 0;
            string failure = p["failure"]?.GetValue<string>() ?? "";

            if (st is "starting" or "hosting")
            {
                var qrWrap = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
                qrWrap.Children.Add(UiKit.QrCodeView(qrPath, qrSize, 188));
                qrWrap.Children.Add(UiKit.MutedText(
                    Loc.Tr("Pairing", "Scan this QR code in the NectarLink app on your phone.")));
                contentStack.Children.Add(qrWrap);
            }
            else if (st == "connecting")
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center };
                row.Children.Add(new ProgressRing { IsActive = true, Width = 24, Height = 24 });
                row.Children.Add(UiKit.BodyText(Loc.Tr("Pairing", "Connecting to %1…", peerName)));
                contentStack.Children.Add(row);
            }
            else if (st == "comparing")
            {
                var card = new StackPanel { Spacing = 10 };
                card.Children.Add(UiKit.BodyText(Loc.Tr("Pairing", "Does this code match on %1?", peerName)));
                card.Children.Add(new TextBlock
                {
                    Text = code,
                    FontSize = 32,
                    FontWeight = FontWeights.Bold,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(ThemeManager.Current.FontMono),
                    Foreground = UiKit.Brush("NlPrimaryBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
                btns.Children.Add(UiKit.PillButton(Loc.Tr("Pairing", "Codes match"), () => engine.Invoke("pairing.confirm", new { matches = true }), accent: true));
                btns.Children.Add(UiKit.PillButton(Loc.Tr("Pairing", "They don't match"), () => engine.Invoke("pairing.confirm", new { matches = false })));
                card.Children.Add(btns);
                contentStack.Children.Add(UiKit.Card(card, highContrast: true));
            }
            else if (st == "confirmed")
            {
                contentStack.Children.Add(UiKit.BodyText(Loc.Tr("Pairing", "Waiting for %1 to confirm…", peerName)));
            }
            else if (st == "paired")
            {
                var ok = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
                ok.Children.Add(new FontIcon { Glyph = "\uE73E", FontSize = 36, Foreground = UiKit.Brush("NlPrimaryBrush") });
                ok.Children.Add(UiKit.TitleText(Loc.Tr("Pairing", "Paired with %1!", peerName), 18));
                contentStack.Children.Add(ok);
            }
            else if (st == "failed")
            {
                string msg = failure switch
                {
                    "rejected" => Loc.Tr("Pairing", "The phone rejected pairing."),
                    "declined" => Loc.Tr("Pairing", "Pairing was declined."),
                    "expired" => Loc.Tr("Pairing", "The pairing code expired."),
                    "unreachable" => Loc.Tr("Pairing", "Couldn't reach the phone on the local network."),
                    _ => Loc.Tr("Pairing", "Pairing did not complete.")
                };
                var fail = new StackPanel { Spacing = 8 };
                fail.Children.Add(UiKit.BodyText(msg));
                fail.Children.Add(UiKit.PillButton(Loc.Tr("Common", "Try again"), () => engine.Invoke("pairing.start"), accent: true));
                contentStack.Children.Add(UiKit.Card(fail));
            }

            if (p["nearby"] is JsonArray nearby && nearby.Count > 0)
            {
                contentStack.Children.Add(UiKit.SectionLabel(Loc.Tr("Pairing", "Nearby devices")));
                foreach (var n in nearby)
                {
                    if (n is not JsonObject peer) continue;
                    string pid = peer["id"]?.GetValue<string>() ?? "";
                    string pname = peer["name"]?.GetValue<string>() ?? "Android phone";
                    var row = new Grid();
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.Children.Add(UiKit.BodyText(pname));
                    var pairBtn = UiKit.PillButton(Loc.Tr("Pairing", "Pair"), () => engine.Invoke("pairing.pairNearby", new { device = pid }));
                    Grid.SetColumn(pairBtn, 1);
                    row.Children.Add(pairBtn);
                    contentStack.Children.Add(UiKit.Card(row, new Thickness(12, 10, 12, 10)));
                }
            }
        }

        void OnChanged(uint mask) => Render();
        Render();
        engine.StateChanged += OnChanged;
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            engine.StateChanged -= OnChanged;
            engine.Invoke("pairing.setNearbyShown", new { shown = false });
            engine.Invoke("pairing.cancel");
        }
    }

    public static async Task ShowDoctorAsync(XamlRoot xamlRoot)
    {
        var engine = NectarLinkEngine.Current;
        engine.Invoke("doctor.run");

        var stack = new StackPanel { Spacing = 10, Width = 500 };
        ContentDialog dialog = new()
        {
            Title = Loc.Tr("Doctor", "Connection Doctor"),
            PrimaryButtonText = Loc.Tr("Doctor", "Re-run checks"),
            CloseButtonText = Loc.Tr("Common", "Close"),
            XamlRoot = xamlRoot,
            Content = new ScrollViewer { MaxHeight = 480, Content = stack }
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            engine.Invoke("doctor.run");
        };

        void Render()
        {
            stack.Children.Clear();
            bool busy = engine.App["doctorBusy"]?.GetValue<bool>() ?? false;
            if (busy)
            {
                var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                p.Children.Add(new ProgressRing { IsActive = true, Width = 20, Height = 20 });
                p.Children.Add(UiKit.MutedText(Loc.Tr("Doctor", "Running diagnostics…")));
                stack.Children.Add(p);
            }
            if (engine.App["doctorChecks"] is JsonArray checks)
            {
                foreach (var item in checks)
                {
                    if (item is not JsonObject c) continue;
                    string id = c["id"]?.GetValue<string>() ?? "";
                    string title = c["title"]?.GetValue<string>() ?? "";
                    string detail = c["detail"]?.GetValue<string>() ?? "";
                    string status = c["status"]?.GetValue<string>() ?? "ok";
                    string fixLabel = c["fixLabel"]?.GetValue<string>() ?? "";
                    bool canFix = c["canFix"]?.GetValue<bool>() ?? false;

                    var row = new StackPanel { Spacing = 4 };
                    var top = new Grid();
                    top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    top.Children.Add(UiKit.BodyText(title));
                    var badge = UiKit.StatusBadge(
                        status.ToUpperInvariant(),
                        highlight: status == "ok",
                        warning: status == "warn",
                        error: status is "fail" or "error");
                    Grid.SetColumn(badge, 1);
                    top.Children.Add(badge);
                    row.Children.Add(top);
                    if (!string.IsNullOrEmpty(detail))
                        row.Children.Add(UiKit.MutedText(detail));
                    if (canFix && !string.IsNullOrEmpty(id))
                    {
                        row.Children.Add(UiKit.PillButton(
                            string.IsNullOrEmpty(fixLabel) ? Loc.Tr("Doctor", "Fix") : fixLabel,
                            () => engine.Invoke("doctor.run", new { fix = id }),
                            accent: true));
                    }
                    stack.Children.Add(UiKit.Card(row, new Thickness(12, 10, 12, 10)));
                }
            }
        }

        void OnChanged(uint _) => Render();
        Render();
        engine.StateChanged += OnChanged;
        try { await dialog.ShowAsync(); }
        finally { engine.StateChanged -= OnChanged; }
    }

    public static async Task ShowRelaysAsync(XamlRoot xamlRoot)
    {
        var engine = NectarLinkEngine.Current;
        engine.Invoke("relays.check");

        var stack = new StackPanel { Spacing = 10, Width = 460 };
        ContentDialog dialog = new()
        {
            Title = Loc.Tr("Relays", "Relay status"),
            PrimaryButtonText = Loc.Tr("Relays", "Check again"),
            CloseButtonText = Loc.Tr("Common", "Close"),
            XamlRoot = xamlRoot,
            Content = new ScrollViewer { MaxHeight = 420, Content = stack }
        };
        dialog.PrimaryButtonClick += (_, args) => { args.Cancel = true; engine.Invoke("relays.check"); };

        void Render()
        {
            stack.Children.Clear();
            if (engine.App["relayCheckBusy"]?.GetValue<bool>() ?? false)
            {
                var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                p.Children.Add(new ProgressRing { IsActive = true, Width = 20, Height = 20 });
                p.Children.Add(UiKit.MutedText(Loc.Tr("Relays", "Checking relays…")));
                stack.Children.Add(p);
            }
            if (engine.App["relayProbes"] is JsonArray probes)
            {
                foreach (var item in probes)
                {
                    if (item is not JsonObject r) continue;
                    string url = r["url"]?.GetValue<string>() ?? "";
                    bool ok = r["reachable"]?.GetValue<bool>() ?? false;
                    int rtt = r["latencyMs"]?.GetValue<int>() ?? -1;
                    string err = r["error"]?.GetValue<string>() ?? "";

                    var grid = new Grid();
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var left = new StackPanel { Spacing = 2 };
                    left.Children.Add(UiKit.BodyText(url, 13));
                    if (!string.IsNullOrEmpty(err)) left.Children.Add(UiKit.MutedText(err, 12));
                    grid.Children.Add(left);
                    var badge = UiKit.StatusBadge(ok ? $"{rtt} ms" : "Unreachable", highlight: ok, error: !ok);
                    Grid.SetColumn(badge, 1);
                    grid.Children.Add(badge);
                    stack.Children.Add(UiKit.Card(grid, new Thickness(12, 10, 12, 10)));
                }
            }
        }

        void OnChanged(uint _) => Render();
        Render();
        engine.StateChanged += OnChanged;
        try { await dialog.ShowAsync(); }
        finally { engine.StateChanged -= OnChanged; }
    }

    public static async Task ShowReportAsync(XamlRoot xamlRoot)
    {
        var engine = NectarLinkEngine.Current;
        engine.Invoke("report.open");

        var areaBox = new ComboBox { Header = Loc.Tr("Report", "Where did it happen?"), HorizontalAlignment = HorizontalAlignment.Stretch };
        string[] areas = ["connection", "notifications", "messages", "calls", "photos", "mirroring", "files", "webcam", "other"];
        foreach (var a in areas) areaBox.Items.Add(a);
        areaBox.SelectedIndex = 0;

        var descBox = new TextBox
        {
            Header = Loc.Tr("Report", "What happened?"),
            AcceptsReturn = true,
            Height = 96,
            PlaceholderText = Loc.Tr("Report", "Describe what you tried and what went wrong…")
        };
        var justNowCheck = new CheckBox { Content = Loc.Tr("Report", "This just happened"), IsChecked = true };
        var includeLogs = new CheckBox { Content = Loc.Tr("Report", "Include recent logs"), IsChecked = true };
        var includeDevices = new CheckBox { Content = Loc.Tr("Report", "Include device & capability summary"), IsChecked = true };
        var includeNet = new CheckBox { Content = Loc.Tr("Report", "Include network summary"), IsChecked = true };
        var statusLabel = UiKit.MutedText("");

        void SyncPrep()
        {
            int mask = (includeLogs.IsChecked == true ? 1 : 0)
                | (includeDevices.IsChecked == true ? 2 : 0)
                | (includeNet.IsChecked == true ? 4 : 0);
            engine.Invoke("report.prepare", new
            {
                area = areaBox.SelectedItem?.ToString() ?? "other",
                other = "",
                text = descBox.Text ?? "",
                justNow = justNowCheck.IsChecked == true,
                parts = mask
            });
        }

        var stack = new StackPanel { Spacing = 12, Width = 480 };
        stack.Children.Add(areaBox);
        stack.Children.Add(descBox);
        stack.Children.Add(justNowCheck);
        stack.Children.Add(includeLogs);
        stack.Children.Add(includeDevices);
        stack.Children.Add(includeNet);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        btns.Children.Add(UiKit.PillButton(Loc.Tr("Report", "Send report"), () =>
        {
            SyncPrep();
            engine.Invoke("report.send");
        }, accent: true));
        btns.Children.Add(UiKit.PillButton(Loc.Tr("Report", "Open on GitHub"), () =>
        {
            SyncPrep();
            engine.Invoke("report.openOnGithub");
        }));
        stack.Children.Add(btns);
        stack.Children.Add(statusLabel);

        void Render()
        {
            statusLabel.Text = engine.Report["status"]?.GetValue<string>() ?? "";
        }

        void OnChanged(uint _) => Render();
        Render();
        engine.StateChanged += OnChanged;

        ContentDialog dialog = new()
        {
            Title = Loc.Tr("Report", "Report a problem"),
            CloseButtonText = Loc.Tr("Common", "Close"),
            XamlRoot = xamlRoot,
            Content = new ScrollViewer { MaxHeight = 520, Content = stack }
        };
        try { await dialog.ShowAsync(); }
        finally { engine.StateChanged -= OnChanged; }
    }

    public static async Task ShowWhatsNewAsync(XamlRoot xamlRoot)
    {
        var engine = NectarLinkEngine.Current;
        var wn = engine.App["whatsNew"];
        var stack = new StackPanel { Spacing = 12, Width = 480 };
        string version = wn?["version"]?.GetValue<string>() ?? (engine.App["version"]?.GetValue<string>() ?? "");
        stack.Children.Add(UiKit.SectionLabel(Loc.Tr("WhatsNew", "Version %1", version)));

        if (wn?["releases"] is JsonArray releases)
        {
            foreach (var r in releases)
            {
                if (r is not JsonObject rel) continue;
                string ver = rel["version"]?.GetValue<string>() ?? "";
                string date = rel["date"]?.GetValue<string>() ?? "";
                stack.Children.Add(UiKit.TitleText($"{ver} {(string.IsNullOrEmpty(date) ? "" : $"· {date}")}", 16));
                if (rel["sections"] is JsonArray secs)
                {
                    foreach (var s in secs)
                    {
                        if (s is not JsonObject sec) continue;
                        stack.Children.Add(UiKit.SectionLabel(sec["title"]?.GetValue<string>() ?? ""));
                        if (sec["entries"] is JsonArray entries)
                        {
                            foreach (var e in entries)
                            {
                                string title = e?["title"]?.GetValue<string>() ?? "";
                                string body = e?["body"]?.GetValue<string>() ?? "";
                                stack.Children.Add(UiKit.BodyText($"• {(string.IsNullOrEmpty(title) ? "" : title + ": ")}{body}", 13));
                            }
                        }
                    }
                }
            }
        }
        else
        {
            stack.Children.Add(UiKit.BodyText(Loc.Tr("WhatsNew", "All native WinUI 3 features are active and up to date.")));
        }

        ContentDialog dialog = new()
        {
            Title = Loc.Tr("WhatsNew", "What's new"),
            CloseButtonText = Loc.Tr("Common", "Got it"),
            XamlRoot = xamlRoot,
            Content = new ScrollViewer { MaxHeight = 460, Content = stack }
        };
        await dialog.ShowAsync();
        engine.Invoke("whatsNew.seen");
    }

    public static async Task ShowGuideDocAsync(XamlRoot xamlRoot, string title, string body)
    {
        var scroll = new ScrollViewer
        {
            MaxHeight = 460,
            Content = UiKit.BodyText(body, 14)
        };
        ContentDialog dialog = new()
        {
            Title = title,
            CloseButtonText = Loc.Tr("Common", "Close"),
            XamlRoot = xamlRoot,
            Content = scroll
        };
        await dialog.ShowAsync();
        NectarLinkEngine.Current.Invoke("app.closeGuideDoc");
    }

    public static async Task ShowAgentConfirmAsync(XamlRoot xamlRoot, JsonObject confirmation)
    {
        string id = confirmation["id"]?.GetValue<string>() ?? "";
        string client = confirmation["client"]?.GetValue<string>() ?? "";
        string title = confirmation["title"]?.GetValue<string>() ?? "";
        string device = confirmation["device"]?.GetValue<string>() ?? "";
        bool canRemember = confirmation["canRemember"]?.GetValue<bool>() ?? false;

        var stack = new StackPanel { Spacing = 10, Width = 420 };
        stack.Children.Add(UiKit.TitleText(title, 17));
        stack.Children.Add(UiKit.MutedText($"{client} · {device}"));
        if (confirmation["details"] is JsonArray details)
        {
            foreach (var d in details)
            {
                string lbl = d?["label"]?.GetValue<string>() ?? "";
                string val = d?["value"]?.GetValue<string>() ?? "";
                stack.Children.Add(UiKit.BodyText($"{lbl}: {val}", 13));
            }
        }

        ContentDialog dialog = new()
        {
            Title = Loc.Tr("Agents", "Agent request"),
            PrimaryButtonText = Loc.Tr("Agents", "Allow once"),
            SecondaryButtonText = canRemember ? Loc.Tr("Agents", "Always allow") : "",
            CloseButtonText = Loc.Tr("Agents", "Deny"),
            XamlRoot = xamlRoot,
            Content = stack
        };
        var result = await dialog.ShowAsync();
        string dec = result switch
        {
            ContentDialogResult.Primary => "allow",
            ContentDialogResult.Secondary => "always",
            _ => "deny"
        };
        NectarLinkEngine.Current.Invoke("agents.answer", new { id, decision = dec });
    }

    public static async Task ShowPhoneWallpaperAsync(XamlRoot xamlRoot, string? preferredDeviceId = null)
    {
        var engine = NectarLinkEngine.Current;
        string devId = !string.IsNullOrEmpty(preferredDeviceId) ? preferredDeviceId : engine.CurrentDeviceId;
        if (string.IsNullOrEmpty(devId)) return;

        // Load phone gallery albums so user can also pick a wallpaper wirelessly from Phone Photos
        engine.Invoke("photos.open", new { device = devId });

        var previewHost = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Spacing = 8 };
        var actionsHost = new StackPanel { Spacing = 8 };
        var photosSection = new StackPanel { Spacing = 8 };

        var rootStack = new StackPanel { Spacing = 14, Width = 520 };
        rootStack.Children.Add(previewHost);
        rootStack.Children.Add(actionsHost);
        rootStack.Children.Add(photosSection);

        ContentDialog dialog = new()
        {
            Title = Loc.Tr("Device", "Phone wallpaper"),
            CloseButtonText = Loc.Tr("Common", "Close"),
            XamlRoot = xamlRoot,
            Content = new ScrollViewer
            {
                MaxHeight = 560,
                HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = rootStack
            }
        };

        string? lastSig = null;

        JsonObject? FindDevice()
        {
            foreach (var item in engine.Devices)
            {
                if (item is JsonObject d && d["id"]?.GetValue<string>() == devId) return d;
            }
            return engine.CurrentDevice;
        }

        void Render(bool force = false)
        {
            var dev = FindDevice();
            string wpPath = dev?["wallpaperPath"]?.GetValue<string>() ?? "";
            bool busy = dev?["wallpaperFetchBusy"]?.GetValue<bool>() ?? false;
            string wpStatus = dev?["wallpaperStatus"]?.GetValue<string>() ?? "";
            bool hasMirror = dev?["mirrorHasFrame"]?.GetValue<bool>() ?? false;

            var ph = engine.Photos;
            string curAlbum = ph["album"]?.GetValue<string>() ?? "";
            var phRows = ph["rows"] as JsonArray;
            int phCount = phRows?.Count ?? 0;
            int thumbReady = 0;
            if (phRows is not null)
            {
                foreach (var r in phRows)
                {
                    if (!string.IsNullOrEmpty(r?["thumb"]?.GetValue<string>())) thumbReady++;
                }
            }
            int albumCount = (ph["albums"] as JsonArray)?.Count ?? 0;
            string sig = $"{wpPath}|{busy}|{wpStatus}|{hasMirror}|{curAlbum}|{phCount}|{thumbReady}|{albumCount}";
            if (!force && sig == lastSig) return;
            lastSig = sig;

            previewHost.Children.Clear();
            previewHost.Children.Add(UiKit.PhoneSilhouette(dev, height: 176));
            previewHost.Children.Add(UiKit.MutedText(
                string.IsNullOrEmpty(wpPath)
                    ? Loc.Tr("Device", "No wallpaper cached yet for this phone.")
                    : Loc.Tr("Device", "Applied to Home silhouette, sidebar avatar, and Settings banner."),
                12));

            actionsHost.Children.Clear();
            var primaryBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (busy)
            {
                var busyRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                busyRow.Children.Add(new ProgressRing { IsActive = true, Width = 18, Height = 18 });
                busyRow.Children.Add(UiKit.MutedText(Loc.Tr("Device", "Fetching from phone…")));
                actionsHost.Children.Add(busyRow);
            }
            else
            {
                primaryBtns.Children.Add(UiKit.PillButton(
                    Loc.Tr("Device", "Fetch from phone (ADB)"),
                    () => engine.Invoke("device.fetchWallpaper", new { device = devId, mode = "auto" }),
                    glyph: "\uE896",
                    accent: true));
                primaryBtns.Children.Add(UiKit.PillButton(
                    Loc.Tr("Device", "Capture screen (ADB)"),
                    () => engine.Invoke("device.fetchWallpaper", new { device = devId, mode = "adb_screen" }),
                    glyph: "\uE7F4"));
                if (hasMirror)
                {
                    primaryBtns.Children.Add(UiKit.PillButton(
                        Loc.Tr("Device", "Capture mirror"),
                        () => engine.Invoke("device.fetchWallpaper", new { device = devId, mode = "mirror" }),
                        glyph: "\uE722"));
                }
                primaryBtns.Children.Add(UiKit.PillButton(
                    Loc.Tr("Device", "Choose file…"),
                    async () =>
                    {
                        if ((App.Current as App)?.MainWin is not { } mw) return;
                        var picker = new Windows.Storage.Pickers.FileOpenPicker();
                        foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp" })
                            picker.FileTypeFilter.Add(ext);
                        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowChrome.GetHwnd(mw));
                        var file = await picker.PickSingleFileAsync();
                        if (file is not null)
                            engine.Invoke("device.setWallpaperFromFile", new { device = devId, path = file.Path });
                    },
                    glyph: "\uE8E5"));
                if (!string.IsNullOrEmpty(wpPath))
                {
                    primaryBtns.Children.Add(UiKit.PillButton(
                        Loc.Tr("Common", "Clear"),
                        () => engine.Invoke("device.clearWallpaper", new { device = devId }),
                        glyph: "\uE74D"));
                }
                actionsHost.Children.Add(UiKit.HScroll(primaryBtns));
            }

            if (!string.IsNullOrEmpty(wpStatus))
            {
                actionsHost.Children.Add(UiKit.Card(UiKit.MutedText(wpStatus, 12.5), new Thickness(12, 8, 12, 8), highContrast: true));
            }

            // Wireless Phone Photos picker
            photosSection.Children.Clear();
            photosSection.Children.Add(UiKit.SectionLabel(Loc.Tr("Device", "Or pick from Phone Photos (wireless)")));
            if (ph["albums"] is JsonArray albums && albums.Count > 0)
            {
                var albumStrip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                foreach (var a in albums)
                {
                    string aid = a?["id"]?.GetValue<string>() ?? "";
                    string aname = a?["name"]?.GetValue<string>() ?? "Album";
                    albumStrip.Children.Add(UiKit.PillButton(
                        aname,
                        () => engine.Invoke("photos.selectAlbum", new { album = aid }),
                        accent: aid == curAlbum));
                }
                photosSection.Children.Add(UiKit.HScroll(albumStrip));
            }

            if (phRows is not null && phRows.Count > 0)
            {
                var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
                for (int c = 0; c < 4; c++)
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                int idx = 0;
                foreach (var item in phRows)
                {
                    if (item is not JsonObject r) continue;
                    if ((r["kind"]?.GetValue<string>() ?? "photo") != "photo") continue;
                    if (r["isVideo"]?.GetValue<bool>() ?? false) continue;
                    string pid = r["id"]?.GetValue<string>() ?? "";
                    if (string.IsNullOrEmpty(pid)) continue;
                    if (idx >= 16) break;

                    string thumb = r["thumb"]?.GetValue<string>() ?? "";
                    if (string.IsNullOrEmpty(thumb))
                        engine.Invoke("photos.needThumb", new { id = pid });

                    int rowIdx = idx / 4;
                    int colIdx = idx % 4;
                    while (grid.RowDefinitions.Count <= rowIdx)
                        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                    var thumbImg = UiKit.LoadLocalImage(thumb, 192);
                    var tileContent = new Border
                    {
                        Height = 96,
                        CornerRadius = new CornerRadius(8),
                        Background = thumbImg is not null
                            ? new Microsoft.UI.Xaml.Media.ImageBrush { ImageSource = thumbImg, Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill }
                            : UiKit.Brush("NlSurfaceContainerHighBrush"),
                        Child = thumbImg is null ? new FontIcon { Glyph = "\uE91B", FontSize = 20 } : null
                    };
                    var btn = new Button
                    {
                        Content = tileContent,
                        Padding = new Thickness(0),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                        BorderThickness = new Thickness(0),
                        CornerRadius = new CornerRadius(8)
                    };
                    string chosenId = pid;
                    btn.Click += (_, _) => engine.Invoke("photos.setAsWallpaper", new { id = chosenId, device = devId });
                    Grid.SetRow(btn, rowIdx);
                    Grid.SetColumn(btn, colIdx);
                    grid.Children.Add(btn);
                    idx++;
                }

                if (idx == 0)
                    photosSection.Children.Add(UiKit.MutedText(Loc.Tr("Device", "No still photos in this album.")));
                else
                    photosSection.Children.Add(grid);
            }
            else
            {
                photosSection.Children.Add(UiKit.MutedText(Loc.Tr("Device", "Connect your phone with Photos access enabled to browse its gallery here.")));
            }
        }

        void OnChanged(uint _) => Render(false);
        Render(true);
        engine.StateChanged += OnChanged;
        try { await dialog.ShowAsync(); }
        finally { engine.StateChanged -= OnChanged; }
    }
}
