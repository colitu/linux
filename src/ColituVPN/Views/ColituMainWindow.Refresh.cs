using Avalonia.Layout;
using Avalonia.Platform.Storage;
using v2rayN.Desktop.Services;

namespace v2rayN.Desktop.Views;

/// <summary>
/// The interface refresh (same as Windows 3.x): connection panel with live speed and the leak test,
/// per-row pings, the animated plan badge, device cleanup / rename, the 30-day usage chart and the
/// support chat's typing indicator, read receipts, drag and drop and desktop notification.
/// </summary>
public partial class ColituMainWindow
{
    private void WireRefresh()
    {
        LeakTestButton.Click += async (_, _) => await LeakTestAsync();
        PingAllButton.Click += async (_, _) => await PingAllAsync();
        CleanupDevicesButton.Click += async (_, _) => await CleanupDevicesAsync();
        RenameSaveButton.Click += async (_, _) => await RenameSaveAsync();
        RenameCancelButton.Click += (_, _) => CloseRename();
        RenameOverlay.PointerPressed += (_, e) =>
        {
            if (ReferenceEquals(e.Source, RenameOverlay))
            {
                CloseRename();
            }
        };
        RenameBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await RenameSaveAsync();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseRename();
            }
        };
        SupportReplyBox.TextChanged += async (_, _) => await SupportTypingAsync();
        foreach (var zone in new Control[] { SupportNewForm, SupportThread })
        {
            zone.AddHandler(DragDrop.DragOverEvent, SupportDragOver);
        }
        SupportNewForm.AddHandler(DragDrop.DropEvent, (_, e) => DropSupportFiles(e, _supportNewFiles, SupportNewFiles));
        SupportThread.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (SupportComposer.IsVisible)
            {
                DropSupportFiles(e, _supportReplyFiles, SupportReplyFiles);
            }
        });
        ColituReducedMotion.Changed += () => Dispatcher.UIThread.Post(RestartBadgeMotion);
    }

    // ── Keyboard ───────────────────────────────────────────────────────────
    /// <summary>
    /// Cards and chips that open something on click (location card, protocol chip, …) are
    /// focusable with Tab (class "clickable"); Enter or Space does what the click does.
    /// </summary>
    private static void KeyboardClick(Control element, Action click)
    {
        element.Focusable = true;
        element.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                e.Handled = true;
                click();
            }
        };
    }

    // ── Connection panel ───────────────────────────────────────────────────
    private readonly ColituTrafficMeter _meter = new();
    private DispatcherTimer? _meterTimer;
    private string? _exitFor;
    private bool _leakBusy;

    /// <summary>Shows the panel while connected; a new server or protocol asks for the exit country again.</summary>
    private void ApplyConnectionInfo()
    {
        var on = _vpn.Status == ColituVpnStatus.Connected;
        ConnInfoCard.IsVisible = on;
        if (!on)
        {
            _meterTimer?.Stop();
            _meter.Reset();
            ConnGraph.Push([]);
            _exitFor = null;
            LeakResults.Children.Clear();
            LeakResults.IsVisible = false;
            return;
        }

        var server = _vpn.ConnectedServer;
        var protocol = _vpn.ConnectedProtocol;
        ConnProtocolText.Text = _vpn.ConnectedOverGecko ? "ColituGecko"
            : _vpn.WhitelistSelected ? Loc.I["transport.vless-ws"]
            : protocol is { Length: > 0 } ? ColituTransportNames.Of(protocol, Loc.I) : "—";
        var ping = server?.Id is { } id ? _vpn.DisplayPing(id) : null;
        ConnPingText.Text = ping is > 0 ? $"{ping} ms" : "— ms";

        var key = $"{server?.Id}|{protocol}|{_vpn.WhitelistSelected}";
        if (key != _exitFor)
        {
            _exitFor = key;
            ConnCountryText.Text = "…";
            LeakResults.Children.Clear();
            LeakResults.IsVisible = false;
            _ = LoadExitAsync(key);
        }

        if (_meterTimer == null)
        {
            _meterTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _meterTimer.Tick += (_, _) => TickMeter();
        }
        if (!_meterTimer.IsEnabled)
        {
            _meter.Reset();
            _meterTimer.Start();
        }
    }

    /// <summary>One second of the speed graph; reads the counters only while Home is on screen.</summary>
    private void TickMeter()
    {
        if (_vpn.Status != ColituVpnStatus.Connected)
        {
            _meterTimer?.Stop();
            return;
        }
        if (_page != "home" || !IsVisible || WindowState == WindowState.Minimized)
        {
            // Hidden: the next reading starts a fresh baseline instead of averaging the gap.
            _meter.Reset();
            return;
        }
        _meter.Tick();
        var language = Loc.I.Language;
        ConnDownText.Text = Loc.I.Format("conn.down", ("v", ColituTrafficMeter.Format(_meter.Latest.Down, language)));
        ConnUpText.Text = Loc.I.Format("conn.up", ("v", ColituTrafficMeter.Format(_meter.Latest.Up, language)));
        ConnGraphNote.Text = Loc.I[_meter.TunnelOnly ? "conn.speedTunnel" : "conn.speedPc"];
        ConnGraph.Push(_meter.Samples);
    }

    private async Task LoadExitAsync(string key)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var (port, credentials) = _vpn.TunnelProxy();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var exit = await ColituConnectionCheck.ExitAsync(port, credentials, cts.Token);
                if (key != _exitFor)
                {
                    return;
                }
                ShowExit(exit);
                return;
            }
            catch (Exception ex)
            {
                Logging.SaveLog($"ColituMainWindow.LoadExitAsync: {ex.GetType().Name}");
            }
            // Right after a connect the route may still settle: one more try a little later.
            await Task.Delay(TimeSpan.FromSeconds(4));
            if (key != _exitFor)
            {
                return;
            }
        }
        ConnCountryText.Text = Loc.I["conn.unknown"];
    }

    private void ShowExit(ColituExitInfo? exit)
    {
        // The exit IP itself is not shown (owner 2026-11-21): only where the tunnel leaves.
        ConnCountryText.Text = exit == null ? Loc.I["conn.unknown"] : CountryLabel(exit.CountryCode);
    }

    internal static string CountryLabel(string? code) =>
        code == null ? "—" : ColituServerRow.CountryName(code) is { Length: > 0 } name ? $"{name} ({code})" : code;

    private async Task LeakTestAsync()
    {
        if (_leakBusy || _vpn.Status != ColituVpnStatus.Connected)
        {
            return;
        }
        _leakBusy = true;
        LeakTestButton.IsEnabled = false;
        LeakTestSpinner.IsVisible = true;
        LeakResults.Children.Clear();
        LeakResults.IsVisible = true;
        LeakResults.Children.Add(LeakLine(ColituLeakState.Unknown, Loc.I["conn.checking"]));
        var key = _exitFor;
        try
        {
            var (port, credentials) = _vpn.TunnelProxy();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            // The Beyaz Liste way in and ColituGecko leave through their own exit, not the node's country: no country check.
            var serverCountry = _vpn.WhitelistSelected || _vpn.ConnectedOverGecko ? null : _vpn.ConnectedServer?.CountryCode;
            var report = await ColituConnectionCheck.LeakTestAsync(port, credentials, serverCountry, cts.Token);
            if (key != _exitFor)
            {
                return;
            }
            LeakResults.Children.Clear();
            if (report.Exit == null)
            {
                LeakResults.Children.Add(LeakLine(ColituLeakState.Warn, Loc.I["conn.leak.failed"]));
                return;
            }
            ShowExit(report.Exit);
            foreach (var (state, text) in LeakLines(report))
            {
                LeakResults.Children.Add(LeakLine(state, text));
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"ColituMainWindow.LeakTest: {ex.GetType().Name}");
            LeakResults.Children.Clear();
            LeakResults.Children.Add(LeakLine(ColituLeakState.Warn, Loc.I["conn.leak.failed"]));
        }
        finally
        {
            _leakBusy = false;
            LeakTestButton.IsEnabled = true;
            LeakTestSpinner.IsVisible = false;
        }
    }

    /// <summary>
    /// The two result lines of a finished test. The IP line names the exit country only, never the
    /// address (owner 2026-11-21); the DNS line names the resolver the system used.
    /// </summary>
    internal static List<(ColituLeakState State, string Text)> LeakLines(ColituLeakReport report)
    {
        var loc = Loc.I;
        var country = CountryLabel(report.Exit?.CountryCode);
        var ip = (report.IpState, loc.Format(report.IpState == ColituLeakState.Warn ? "conn.leak.ipWarn" : "conn.leak.ipOk", ("country", country)));
        var dns = report.DnsState switch
        {
            ColituLeakState.Ok => loc.Format("conn.leak.dnsOk", ("ip", report.ResolverIp ?? "—"), ("country", CountryLabel(report.ResolverCountry))),
            ColituLeakState.Warn => loc.Format("conn.leak.dnsWarn", ("ip", report.ResolverIp ?? "—"), ("country", CountryLabel(report.ResolverCountry))),
            _ => report.ResolverIp == null ? loc["conn.leak.failed"] : loc.Format("conn.leak.dnsUnknown", ("ip", report.ResolverIp))
        };
        return [ip, (report.DnsState, dns)];
    }

    private Control LeakLine(ColituLeakState state, string text) => LeakLineCore(state, text, key => Resource<IBrush>(key));

    /// <summary>One line of the leak test result: a round ✓ / ! / • mark and the sentence.</summary>
    internal static Control LeakLineCore(ColituLeakState state, string text, Func<string, IBrush> brush)
    {
        var (glyph, brushKey) = state switch
        {
            ColituLeakState.Ok => ("✓", "SuccessBrush"),
            ColituLeakState.Warn => ("!", "WarningBrush"),
            _ => ("•", "MutedBrush")
        };
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8), ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var mark = brush(brushKey);
        var badge = new Border
        {
            Width = 20,
            Height = 20,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = new CornerRadius(10),
            BorderBrush = mark,
            BorderThickness = new Thickness(1.4),
            Child = new TextBlock { Text = glyph, FontSize = 12, FontWeight = FontWeight.Bold, Foreground = mark, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        var label = new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = brush("TextBrush") };
        Grid.SetColumn(label, 1);
        grid.Children.Add(badge);
        grid.Children.Add(label);
        Avalonia.Automation.AutomationProperties.SetName(grid, text);
        return grid;
    }

    // ── Plan badge ─────────────────────────────────────────────────────────
    private static readonly Color BadgeLight = Color.FromRgb(0xC4, 0xB5, 0xFD);
    private static readonly Color BadgeMid = Color.FromRgb(0xB4, 0xA4, 0xFF);
    private static readonly Color BadgeDeep = Color.FromRgb(0x8B, 0x78, 0xFF);
    private readonly HashSet<Border> _activeBadges = [];
    private DispatcherTimer? _badgeTimer;
    private readonly DateTime _badgeStart = DateTime.UtcNow;

    /// <summary>
    /// The "ACTIVE" badge: its two ends slowly trade the brand violets (2.8 s, back and forth). Still
    /// when the desktop asks for reduced motion (ColituReducedMotion). Dark text stays above 5:1 on every frame.
    /// </summary>
    private IBrush ActiveBadgeBrush(Border badge)
    {
        _activeBadges.Add(badge);
        RestartBadgeMotion();
        return BadgeFrame(ColituReducedMotion.IsOn ? 0 : BadgePhase(DateTime.UtcNow - _badgeStart));
    }

    /// <summary>0 → 1 → 0 over 5.6 s, eased like a sine (the WPF ColorAnimation with AutoReverse).</summary>
    internal static double BadgePhase(TimeSpan elapsed) => (1 - Math.Cos(elapsed.TotalSeconds / 2.8 * Math.PI)) / 2;

    internal static IBrush BadgeFrame(double t) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Mix(BadgeMid, BadgeDeep, t), 0),
            new GradientStop(Mix(BadgeDeep, BadgeLight, t), 1)
        }
    };

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    private void RestartBadgeMotion()
    {
        if (ColituReducedMotion.IsOn || PreviewMode)
        {
            _badgeTimer?.Stop();
            foreach (var badge in _activeBadges)
            {
                badge.Background = BadgeFrame(0);
            }
            return;
        }
        if (_badgeTimer == null)
        {
            _badgeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
            _badgeTimer.Tick += (_, _) => TickBadges();
        }
        if (!_badgeTimer.IsEnabled)
        {
            _badgeTimer.Start();
        }
    }

    private void TickBadges()
    {
        // A badge that turned inactive (SetBadge gave it the rose glass) leaves the animation.
        _activeBadges.RemoveWhere(badge => badge.Background is not LinearGradientBrush);
        if (_activeBadges.Count == 0)
        {
            _badgeTimer?.Stop();
            return;
        }
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            return;
        }
        var frame = BadgeFrame(BadgePhase(DateTime.UtcNow - _badgeStart));
        foreach (var badge in _activeBadges.Where(badge => badge.IsEffectivelyVisible))
        {
            badge.Background = frame;
        }
    }

    // ── Plan: 30-day usage ─────────────────────────────────────────────────
    private bool _usageBusy;

    private async Task LoadUsageHistoryAsync()
    {
        if (_usageBusy || !_auth.HasSession)
        {
            return;
        }
        _usageBusy = true;
        try
        {
            var days = await ColituAccountApi.DailyUsageAsync(30);
            if (!_auth.HasSession)
            {
                return;
            }
            if (days == null || days.Count == 0)
            {
                // An older panel without the history: no card.
                UsageHistoryCard.IsVisible = false;
                return;
            }
            ShowUsageHistory(days);
        }
        catch (Exception ex) when (ColituVpnService.IsNetworkFailure(ex))
        {
            // Offline: the card keeps its last numbers.
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.LoadUsageHistoryAsync", ex);
        }
        finally
        {
            _usageBusy = false;
        }
    }

    internal void ShowUsageHistory(IReadOnlyList<ColituDailyUsage> days)
    {
        var culture = Loc.I.Culture;
        UsageHistoryTotal.Text = FormatBytes(days.Sum(day => day.Total));
        var peak = days.MaxBy(day => day.Total)!;
        UsageHistoryPeak.Text = peak.Total > 0 ? Loc.I.Format("usage.peak", ("day", ColituUsageBars.DayLabel(peak.Date, culture)), ("total", FormatBytes(peak.Total))) : "";
        UsageFirstDay.Text = ColituUsageBars.DayLabel(days[0].Date, culture);
        UsageBars.SetDays(days, day => Loc.I.Format("usage.day", ("day", ColituUsageBars.DayLabel(day.Date, culture)), ("total", FormatBytes(day.Total)),
            ("down", FormatBytes(day.DownloadBytes)), ("up", FormatBytes(day.UploadBytes))));
        Avalonia.Automation.AutomationProperties.SetName(UsageBars, $"{Loc.I["usage.title"]}: {UsageHistoryTotal.Text}. {UsageHistoryPeak.Text}");
        UsageHistoryCard.IsVisible = true;
    }

    // ── Account: old devices, rename ───────────────────────────────────────
    private const int StaleDeviceDays = 30;
    private List<ColituDeviceRow> _staleDevices = [];
    private ColituDeviceRow? _renaming;

    internal void ApplyCleanupButton(List<ColituDeviceRow> rows)
    {
        var ids = ColituAccountApi.StaleDeviceIds(rows.Select(row => (row.Id, row.IsCurrent, row.LastSeen)), StaleDeviceDays, DateTimeOffset.UtcNow);
        _staleDevices = rows.Where(row => ids.Contains(row.Id)).ToList();
        CleanupDevicesButton.IsVisible = _staleDevices.Count > 0;
        CleanupDevicesText.Text = Loc.I.Format("account.cleanupCount", ("n", _staleDevices.Count));
    }

    private async Task CleanupDevicesAsync()
    {
        CleanupDevicesButton.IsEnabled = false;
        try
        {
            // The panel decides (it knows the last connection); an older panel: the list shown here.
            var ids = await ColituAccountApi.CleanupDevicesAsync(StaleDeviceDays, dryRun: true);
            var serverSide = ids != null;
            ids ??= _staleDevices.Select(device => device.Id).ToList();
            // Never this computer, whatever the answer says.
            ids = ids.Where(id => !string.Equals(id, _auth.RegisteredDeviceId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (ids.Count == 0)
            {
                ShowToast(Loc.I["account.cleanupNone"]);
                return;
            }
            var names = ids.Select(id => _staleDevices.FirstOrDefault(device => device.Id == id)?.Name ?? id[..Math.Min(8, id.Length)]).ToList();
            var list = string.Join("\n", names.Take(8).Select(name => "• " + name)) + (names.Count > 8 ? "\n…" : "");
            if (!await ConfirmAsync(Loc.I.Format("account.cleanupConfirm", ("n", ids.Count), ("names", list))))
            {
                return;
            }
            var removed = 0;
            if (serverSide)
            {
                removed = (await ColituAccountApi.CleanupDevicesAsync(StaleDeviceDays, dryRun: false))?.Count ?? 0;
            }
            else
            {
                foreach (var id in ids)
                {
                    await _auth.RemoveDeviceAsync(id);
                    removed++;
                }
            }
            ShowToast(Loc.I.Format("account.cleanupDone", ("n", removed)));
        }
        catch (Exception ex)
        {
            ShowToast(ex is ColituApiException api ? api.Message : Loc.I["err.generic"], true);
        }
        finally
        {
            CleanupDevicesButton.IsEnabled = true;
        }
        await LoadDevicesAsync();
    }

    private void RenameDevice_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ColituDeviceRow device })
        {
            return;
        }
        _renaming = device;
        RenameBox.Text = device.Name;
        RenameOverlay.IsVisible = true;
        ColituMotion.FadeIn(RenameCard, 8);
        Dispatcher.UIThread.Post(() =>
        {
            RenameBox.Focus();
            RenameBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void CloseRename()
    {
        RenameOverlay.IsVisible = false;
        _renaming = null;
    }

    private async Task RenameSaveAsync()
    {
        var device = _renaming;
        var name = RenameBox.Text?.Trim() ?? "";
        if (device == null || name.Length == 0 || !RenameSaveButton.IsEnabled)
        {
            return;
        }
        if (name == device.Name)
        {
            CloseRename();
            return;
        }
        RenameSaveButton.IsEnabled = false;
        try
        {
            await ColituAccountApi.RenameDeviceAsync(device.Id, name);
            CloseRename();
            ShowToast(Loc.I["account.renamed"]);
            await LoadDevicesAsync();
        }
        catch (Exception ex)
        {
            ShowToast(ex is ColituApiException api ? api.Message : Loc.I["err.generic"], true);
        }
        finally
        {
            RenameSaveButton.IsEnabled = true;
        }
    }

    // ── Support: typing, drag and drop, desktop notification ───────────────
    private DateTimeOffset _typingSentAt = DateTimeOffset.MinValue;
    private bool _typingUnsupported;

    private void ShowSupportTyping(bool typing) => SupportTypingText.IsVisible = typing;

    /// <summary>Tells support the user is typing, at most every 3 seconds; the answer says whether support is.</summary>
    private async Task SupportTypingAsync()
    {
        var current = _supportCurrent;
        if (_typingUnsupported || current == null || string.IsNullOrEmpty(SupportReplyBox.Text)
            || DateTimeOffset.UtcNow - _typingSentAt < TimeSpan.FromSeconds(3))
        {
            return;
        }
        _typingSentAt = DateTimeOffset.UtcNow;
        try
        {
            var peerTyping = await ColituAccountApi.SupportTypingAsync(current.Id);
            if (peerTyping == null)
            {
                // The panel has no typing endpoint (404): the indicator stays off.
                _typingUnsupported = true;
                return;
            }
            if (_supportCurrent?.Id == current.Id)
            {
                ShowSupportTyping(peerTyping.Value);
            }
        }
        catch (Exception ex) when (ColituVpnService.IsNetworkFailure(ex) || ex is ColituApiException)
        {
            // Best effort: the indicator is a nicety.
        }
    }

    private static void SupportDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.TryGetFiles() is { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void DropSupportFiles(DragEventArgs e, List<string> files, Panel chips)
    {
        if (e.DataTransfer.TryGetFiles() is not { Length: > 0 } dropped)
        {
            return;
        }
        e.Handled = true;
        AddSupportFiles(files, chips, dropped.Select(item => item.TryGetLocalPath()).OfType<string>().Where(File.Exists));
    }

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp"];

    /// <summary>A small preview of an image file for its chip; null for other files or one that cannot be decoded.</summary>
    internal static Bitmap? SupportThumbnail(string file)
    {
        if (!ImageExtensions.Contains(System.IO.Path.GetExtension(file).ToLowerInvariant()))
        {
            return null;
        }
        try
        {
            using var stream = File.OpenRead(file);
            return Bitmap.DecodeToWidth(stream, 96);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Window hidden or minimised: a desktop notification (notify-send) with the reply's first words.</summary>
    private async Task NotifySupportReplyHiddenAsync()
    {
        var text = Loc.I["support.newReply"];
        try
        {
            var latest = (await _support.ListAsync()).Where(conversation => conversation.Unread > 0).MaxBy(conversation => conversation.LastMessageAt);
            if (latest?.LastMessage is { Length: > 0 } preview)
            {
                text = ReplyPreview(preview);
            }
        }
        catch (Exception ex) when (ColituVpnService.IsNetworkFailure(ex) || ex is ColituApiException)
        {
            // The plain "new reply" text then.
        }
        Notify(text);
    }

    /// <summary>"Support: first words of the reply…" (one line, at most 120 characters).</summary>
    internal static string ReplyPreview(string message)
    {
        var preview = message.ReplaceLineEndings(" ").Trim();
        return Loc.I.Format("support.replyFrom", ("text", preview.Length > 120 ? preview[..117] + "…" : preview));
    }
}
