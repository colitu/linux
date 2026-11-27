using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Avalonia.Media.Transformation;
using v2rayN.Desktop.Services;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace v2rayN.Desktop.Views;

/// <summary>
/// The Colitu shell: one window that hosts sign-in and the signed-in app, styled
/// like colitu.com and the Windows app. Page logic lives in the partial files
/// next to this one.
/// </summary>
public partial class ColituMainWindow : Window
{
    private readonly ColituVpnService _vpn = ColituVpnService.Instance;
    private readonly ColituAuthService _auth = ColituAuthService.Instance;
    private readonly ColituUpdateService _updater = ColituUpdateService.Instance;
    private readonly DispatcherTimer _clock;
    private readonly DispatcherTimer _refresh;
    private readonly DispatcherTimer _toastTimer;
    /// <summary>Every minute: runs the periodic update check when <see cref="ColituUpdateSchedule"/> says it is due.</summary>
    private readonly DispatcherTimer _updateTimer;
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _trayConnectItem;
    private bool _exiting;
    private bool _trayHintShown;
    private bool _started;
    private string _page = "home";
    private bool _offline;
    private Task? _refreshRun;

    public ColituMainWindow()
    {
        InitializeComponent();
        ApplyLanguage(_vpn.Preferences.Language);
        Loc.I.Changed += OnLanguageChanged;

        _vpn.StatusChanged += _ => Dispatcher.UIThread.Post(ApplyStatus);
        // ColituGecko comes and goes in the protocol menu with the panel's offer.
        _vpn.GeckoOfferChanged += () => Dispatcher.UIThread.Post(ApplyStatus);
        _vpn.Notice += key => Dispatcher.UIThread.Post(() =>
        {
            ShowToast(Loc.I[key], key.StartsWith("err", StringComparison.Ordinal));
            ApplyStatus();
        });
        _auth.SessionExpired += _ => Dispatcher.UIThread.Post(async () => await GuardAsync("SessionExpired", OnSessionExpiredAsync));
        _updater.UpdateAvailable += info => Dispatcher.UIThread.Post(() => ShowUpdate(info));
        _vpn.StatusChanged += status => Dispatcher.UIThread.Post(() => OnStatusForUpdates(status));
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _updateTimer.Tick += (_, _) => OnUpdateTimerTick();

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => UpdateSessionTimer();
        _clock.Start();

        _refresh = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _refresh.Tick += async (_, _) => await RefreshDataAsync();

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.5) };
        _toastTimer.Tick += (_, _) => HideToast();

        Opened += OnOpened;
        Closing += OnClosing;
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
            {
                ApplyWindowShape();
            }
        };

        WireChrome();
        WireAuth();
        WireReset();
        WireVerify();
        WireMfa();
        WireRecovery();
        WirePaused();
        WireUpsell();
        WireNotices();
        WireSplit();
        WireHome();
        WireLocations();
        WirePlan();
        WireAccount();
        WireSupport();
        WireSudo();
        WireRefresh();
        InitPassport();
        BuildLanguageSelectors();
        BuildTrayIcon();
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_started || PreviewMode)
        {
            return;
        }
        _started = true;
        VersionText.Text = Loc.I.Format("settings.version", ("version", ColituAuthService.ClientVersion));
        await StartAsync();
    }

    private async Task StartAsync()
    {
        ShowView(LoadingView);
        _ = CheckForUpdatesAsync(quiet: true, ColituUpdateTrigger.Startup);
        // Not only at start: every 6 hours, 15 minutes after a failed check, and after a connection.
        _updateTimer.Start();
        await _vpn.RecoverFromPreviousRunAsync();
        await RestoreSessionAsync();
        ApplyDevicePause();
        // The kill switch of a crashed run still blocks the internet: explain why, and
        // offer to reconnect or to turn protection off.
        if (_vpn.KillSwitchRecoveryPending)
        {
            ShowKillSwitchRecovery();
        }
    }

    private async Task RestoreSessionAsync()
    {
        var state = await _auth.InitializeAsync();
        if (state == ColituStartupState.VerificationRequired)
        {
            ShowVerify(codeJustSent: false);
            return;
        }
        if (state == ColituStartupState.SignedOut)
        {
            ShowAuth();
            if (_auth.SessionWasRevoked)
            {
                ShowAuthError(Loc.I["auth.expired"]);
            }
            return;
        }

        await EnterAppAsync(state == ColituStartupState.Offline);
    }

    private async Task EnterAppAsync(bool offline)
    {
        _offline = offline;
        ShowView(AppView);
        NavHome.IsChecked = true;
        Navigate("home", animate: false);
        ApplyPreferencesToUi();
        ApplyAccount();
        ApplyStatus();
        Dispatcher.UIThread.Post(FitNav, DispatcherPriority.Loaded);

        if (offline)
        {
            ShowToast(Loc.I["home.offline"], true);
            _refresh.Interval = TimeSpan.FromSeconds(20);
        }
        else
        {
            await RefreshDataAsync(includeAccount: false);
        }
        _refresh.Start();
        StartSupportPolling();

        // Once: offer to protect all traffic (TUN). Not while a crashed run's kill switch
        // waits for an answer (that prompt comes first).
        if (_vpn.ShouldOfferTun && !_vpn.KillSwitchRecoveryPending)
        {
            await OfferTunModeAsync();
        }

        // TUN auto-connect waits for the sudo password: it is never asked for at startup
        // (unless the user just chose TUN in the offer above). After a crash with the kill
        // switch on, the user decides first.
        if ((!_planRequired || offline) && !_vpn.NeedsSudoPassword && !_vpn.KillSwitchRecoveryPending)
        {
            if (await _vpn.TryAutoConnectAsync())
            {
                ApplyStatus();
            }
        }
    }

    /// <summary>
    /// Reloads the account, plan, servers and usage. Also resumes an offline session. Callers share
    /// a run that is still going: during an outage one run can outlast the 20 s timer, and runs that
    /// piled up behind it all hit the API at once when the network came back.
    /// </summary>
    private Task RefreshDataAsync(bool includeAccount = true)
    {
        if (_refreshRun is { IsCompleted: false } running)
        {
            return running;
        }
        _refreshRun = RefreshDataOnceAsync(includeAccount);
        return _refreshRun;
    }

    private async Task RefreshDataOnceAsync(bool includeAccount)
    {
        if (!_auth.HasSession)
        {
            return;
        }

        try
        {
            if (_offline)
            {
                var state = await _auth.ResumeAsync();
                if (state == ColituStartupState.SignedOut)
                {
                    await OnSessionExpiredAsync();
                    return;
                }
                if (state == ColituStartupState.VerificationRequired)
                {
                    ShowVerify(codeJustSent: false);
                    return;
                }
                if (state == ColituStartupState.Offline)
                {
                    return;
                }
                _offline = false;
                _refresh.Interval = TimeSpan.FromMinutes(5);
            }
            else if (includeAccount)
            {
                await _auth.RefreshAccountAsync();
            }

            var servers = await _vpn.GetServersAsync();
            _servers = servers.Servers;
            BuildCategoryFilter();
            _planRequired = servers.PlanRequired || _auth.CurrentSubscription?.Active != true;
            _usage = _planRequired ? null : await _vpn.GetStatsAsync();
            ApplyAccount();
            RenderServers();
            ApplyStatus();
            _ = MeasurePingsAsync();
            _ = RefreshNoticesAsync();
            // Anonymous statistics: one report and one event batch when due (thread pool, never the UI thread).
            _ = Task.Run(ColituInsightSender.TrySendAsync);
            if (_page == "account")
            {
                await LoadDevicesAsync();
            }
        }
        catch (ColituApiException ex) when (ex.Terminal)
        {
            await OnSessionExpiredAsync();
        }
        catch (Exception ex) when (ColituVpnService.IsNetworkFailure(ex))
        {
            if (!_offline)
            {
                _offline = true;
                _refresh.Interval = TimeSpan.FromSeconds(20);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.RefreshDataAsync", ex);
        }
    }

    private async Task OnSessionExpiredAsync()
    {
        if (AuthView.IsVisible)
        {
            return;
        }
        await _vpn.ForgetAccountAsync();
        StopSupportPolling();
        ClearNotices();
        ForgetPassport();
        ShowAuth();
        ShowAuthError(Loc.I["auth.expired"]);
        ShowFromTray();
        if (_vpn.KillSwitchEngaged)
        {
            ShowKillSwitchRecovery();
        }
    }

    // ── Views and navigation ───────────────────────────────────────────────
    private void ShowView(Control view)
    {
        foreach (var candidate in new Control[] { LoadingView, AuthView, ResetView, VerifyView, MfaView, AppView })
        {
            candidate.IsVisible = candidate == view;
        }
        ColituMotion.FadeIn(view, 10);
    }

    private void Nav_Checked(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string page, IsChecked: true } && IsLoaded && _page != page)
        {
            Navigate(page);
        }
    }

    /// <summary>Set by the preview test (ColituUiPreviewTests): the window starts nothing (no session, no network, no tray).</summary>
    internal static bool PreviewMode { get; set; }

    /// <summary>The preview test: the signed-in app on <paramref name="page"/>, without loading anything.</summary>
    internal void ShowPreviewPage(string page)
    {
        ShowView(AppView);
        Navigate(page, animate: false, load: false);
    }

    /// <summary>The preview test: the location list built from <paramref name="servers"/> by the real <see cref="RenderServers"/>.</summary>
    internal void PreviewSetServers(List<ColituVpnServer> servers)
    {
        _servers = servers;
        RenderServers();
    }

    private void Navigate(string page, bool animate = true, bool load = true)
    {
        _page = page;
        Control target = page switch
        {
            "locations" => LocationsPage,
            "plan" => PlanPage,
            "account" => AccountPage,
            "settings" => SettingsPage,
            "support" => SupportPage,
            "passport" => PassportPage,
            _ => HomePage
        };
        foreach (var child in Pages.Children)
        {
            child.IsVisible = child == target;
        }
        HomeBackdrop.IsVisible = target == HomePage;

        var nav = page switch
        {
            "locations" => NavLocations,
            "plan" => NavPlan,
            "account" => NavAccount,
            "settings" => NavSettings,
            "support" => NavSupport,
            "passport" => null,
            _ => NavHome
        };
        if (nav == null)
        {
            foreach (var item in NavList.Children.OfType<RadioButton>())
            {
                item.IsChecked = false;
            }
        }
        else if (nav.IsChecked != true)
        {
            nav.IsChecked = true;
        }
        NavThumb.Opacity = nav == null ? 0 : 1;
        MoveNavThumb();
        if (animate)
        {
            ColituMotion.FadeIn(target, 14);
        }
        if (target == HomePage && animate)
        {
            ColituMotion.StaggerIn(HomeCards);
        }
        if (!load)
        {
            return;
        }

        switch (page)
        {
            case "locations":
                RenderServers();
                _ = MeasurePingsAsync();
                break;
            case "plan":
                _ = LoadUsageHistoryAsync();
                break;
            case "account":
                _ = LoadDevicesAsync();
                break;
            case "support":
                _ = OpenSupportAsync();
                break;
            case "passport":
                OnPassportOpened();
                break;
            case "home":
                ApplyPassportHome();
                break;
        }
        UpdateSupportPolling();
    }

    /// <summary>
    /// Keeps the tab pill inside the header at every window width and language (six tabs, Russian
    /// labels are the longest), centred in the window as long as it fits: first between the fixed
    /// 240 px side columns, with the tabs coming closer if needed; then between equal side columns
    /// as wide as the wider side; then the wordmark gives way to the mark alone; last the side
    /// columns take only what they need and the font gets slightly smaller.
    /// </summary>
    private void FitNav()
    {
        if (HeaderBar == null || HeaderBar.Bounds.Width <= 0)
        {
            return;
        }
        var tabs = NavList.Children.OfType<RadioButton>().Where(tab => tab.IsVisible).ToList();
        double Tabs(double padding, double font)
        {
            var width = 10.0; // the pill's padding and border
            foreach (var tab in tabs)
            {
                tab.Padding = new Thickness(padding, 0);
                tab.FontSize = font;
                width += Remeasure(tab).Width;
            }
            return width;
        }
        BrandWordmark.IsVisible = true;
        BrandLogoMark.IsVisible = false;
        var logo = Remeasure(BrandMark).Width;
        // The C mark alone (28 px high) when the wordmark does not fit.
        const double mark = 34;
        // HeaderRight's margin (room for the window buttons) is part of its desired width.
        var right = Remeasure(HeaderRight).Width;
        var total = HeaderBar.Bounds.Width;
        const double Gap = 32;
        // Equal side columns keep the tab pill centred in the window.
        const double SideColumn = 240;

        void Columns(GridLength left, GridLength rightSide)
        {
            HeaderBar.ColumnDefinitions[0].Width = left;
            HeaderBar.ColumnDefinitions[2].Width = rightSide;
        }

        void Brand(bool wordmark)
        {
            BrandWordmark.IsVisible = wordmark;
            BrandLogoMark.IsVisible = !wordmark;
        }

        // 1. Fixed 240 px sides (the design), the tabs closer together if needed.
        if (logo <= SideColumn && right <= SideColumn)
        {
            foreach (var padding in new[] { 20.0, 16.0, 13.0 })
            {
                if (Tabs(padding, 14) <= total - 2 * SideColumn - Gap)
                {
                    Columns(new GridLength(SideColumn), new GridLength(SideColumn));
                    Dispatcher.UIThread.Post(MoveNavThumb, DispatcherPriority.Loaded);
                    return;
                }
            }
        }
        // 2. Still centred: both sides as wide as the wider one; 3. then the side columns take what they need.
        foreach (var (wordmark, padding, font, centred) in new[]
                 {
                     (true, 16.0, 14.0, true), (true, 12.0, 14.0, true), (false, 12.0, 14.0, true),
                     (true, 12.0, 14.0, false), (false, 16.0, 14.0, false), (false, 12.0, 14.0, false), (false, 10.0, 13.0, false)
                 })
        {
            var leftWidth = wordmark ? logo : mark;
            var side = centred ? 2 * Math.Max(leftWidth, right) : leftWidth + right;
            if (Tabs(padding, font) <= total - side - Gap || (padding == 10.0 && font == 13.0))
            {
                var both = new GridLength(Math.Max(leftWidth, right));
                Columns(centred ? both : GridLength.Auto, centred ? both : GridLength.Auto);
                Brand(wordmark);
                break;
            }
        }
        Dispatcher.UIThread.Post(MoveNavThumb, DispatcherPriority.Loaded);
    }

    /// <summary>The control's natural size now (a changed padding or font inside it included).</summary>
    private static Size Remeasure(Control control)
    {
        foreach (var item in control.GetVisualDescendants().OfType<Layoutable>())
        {
            item.InvalidateMeasure();
        }
        control.InvalidateMeasure();
        control.Measure(Size.Infinity);
        return control.DesiredSize;
    }

    private void MoveNavThumb()
    {
        var active = NavList.Children.OfType<RadioButton>().FirstOrDefault(item => item.IsChecked == true);
        if (active == null || active.Bounds.Width <= 0)
        {
            return;
        }
        var x = active.TranslatePoint(new Point(0, 0), NavList)?.X ?? 0;
        NavThumb.Height = active.Bounds.Height;
        NavThumb.Width = active.Bounds.Width;
        NavThumb.RenderTransform = TransformOperations.Parse($"translateX({x.ToString(CultureInfo.InvariantCulture)}px)");
    }

    // ── Language ───────────────────────────────────────────────────────────
    private static void ApplyLanguage(string? language)
    {
        Loc.I.SetLanguage(language);
        var culture = Loc.I.Culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    private void OnLanguageChanged()
    {
        var culture = Loc.I.Culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        BuildLanguageSelectors();
        VersionText.Text = Loc.I.Format("settings.version", ("version", ColituAuthService.ClientVersion));
        ApplyAuthMode();
        ApplyResetState();
        ApplyAccount();
        ApplyStatus();
        ApplyModeHint();
        BuildCategoryFilter();
        RenderServers();
        ApplyVerifyTexts();
        ApplyMfaTexts();
        if (KillSwitchRecoveryPrompt.IsVisible)
        {
            KillSwitchRecoveryBody.Text = Loc.I[_vpn.KillSwitchApplies ? "ksr.body" : "ksr.bodyOff"];
        }
        RenderSupportList();
        RenderSupportChat();
        UpdateTrayMenu();
        _ = LoadDevicesAsync();
        if (AppView.IsVisible)
        {
            // The panel writes the notices in the app's language.
            _ = RefreshNoticesAsync();
        }
        Dispatcher.UIThread.Post(FitNav, DispatcherPriority.Loaded);
    }

    private void BuildLanguageSelectors()
    {
        foreach (var (host, group) in new (Panel, string)[] { (AuthLanguageList, "AuthLang"), (SettingsLanguageList, "SettingsLang") })
        {
            host.Children.Clear();
            foreach (var language in Loc.Languages)
            {
                var option = new RadioButton
                {
                    Content = language switch { "ru" => "Русский", "tr" => "Türkçe", _ => "English" },
                    GroupName = group,
                    Tag = language,
                    Theme = Resource<ControlTheme>("SegmentOption"),
                    IsChecked = language == Loc.I.Language,
                    MinHeight = group == "AuthLang" ? 30 : 40,
                    FontSize = group == "AuthLang" ? 12.5 : 13.5
                };
                if (group == "AuthLang")
                {
                    option.Padding = new Thickness(10, 0);
                }
                option.IsCheckedChanged += LanguageOption_Checked;
                host.Children.Add(option);
            }
        }
    }

    private void LanguageOption_Checked(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string language, IsChecked: true } || language == Loc.I.Language)
        {
            return;
        }

        // Rebuilding the selectors inside their own change event would recurse; defer it.
        Dispatcher.UIThread.Post(async () => await GuardAsync("Language", async () =>
        {
            ApplyLanguage(language);
            await _vpn.UpdatePreferencesAsync(_vpn.Preferences with { Language = language });
            // The account's e-mails and notices follow the app's language.
            _ = ColituAuthService.Instance.ReportLanguageAsync();
        }), DispatcherPriority.Background);
    }

    /// <summary>
    /// Runs an event handler's async work. An exception escaping an async void handler
    /// would end the whole app (and leave a root core or the system proxy behind);
    /// here it is logged and shown as a short error instead.
    /// </summary>
    private async Task GuardAsync(string name, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"ColituMainWindow.{name}", ex);
            ShowToast(Loc.I["err.generic"], true);
        }
    }

    // ── Toast and notifications ────────────────────────────────────────────
    private void ShowToast(string text, bool error = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        ToastText.Text = text;
        ToastDot.Fill = Resource<IBrush>(error ? "DangerBrush" : "SuccessBrush");
        Toast.Opacity = 1;
        Toast.RenderTransform = TransformOperations.Parse("translateY(0px)");
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(text.Length / 14.0, 3.5, 8));
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        Toast.Opacity = 0;
        Toast.RenderTransform = TransformOperations.Parse("translateY(16px)");
    }

    /// <summary>Desktop notification through the freedesktop service (notify-send), when available.</summary>
    private static void Notify(string text)
    {
        try
        {
            var startInfo = new ProcessStartInfo(ColituShell.SystemBinary("notify-send")) { UseShellExecute = false };
            foreach (var arg in new[] { "--app-name=Colitu VPN", "--icon=colitu-vpn", "Colitu VPN", text })
            {
                startInfo.ArgumentList.Add(arg);
            }
            using var _ = Process.Start(startInfo);
        }
        catch
        {
            // No notification daemon: the tray tooltip still shows the state.
        }
    }

    private T Resource<T>(string key) where T : class
    {
        return this.TryFindResource(key, ActualThemeVariant, out var value) && value is T typed
            ? typed
            : throw new KeyNotFoundException(key);
    }

    // ── Window chrome ──────────────────────────────────────────────────────
    private const double FrameRadius = 18;

    private void WireChrome()
    {
        DragArea.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                return;
            }
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                return;
            }
            BeginMoveDrag(e);
        };
        foreach (var (grip, edge) in new (Border, WindowEdge)[]
                 {
                     (GripLeft, WindowEdge.West), (GripRight, WindowEdge.East), (GripTop, WindowEdge.North),
                     (GripBottom, WindowEdge.South), (GripBottomRight, WindowEdge.SouthEast), (GripBottomLeft, WindowEdge.SouthWest)
                 })
        {
            grip.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && WindowState == WindowState.Normal)
                {
                    BeginResizeDrag(edge, e);
                }
            };
        }
        MinimizeButton.Click += (_, _) => WindowState = WindowState.Minimized;
        CloseButton.Click += (_, _) => Close();
        foreach (var nav in NavList.Children.OfType<RadioButton>())
        {
            nav.IsCheckedChanged += Nav_Checked;
        }
        NavList.SizeChanged += (_, _) => MoveNavThumb();
        HeaderBar.SizeChanged += (_, _) => FitNav();
    }

    private void ApplyWindowShape()
    {
        var maximized = WindowState is WindowState.Maximized or WindowState.FullScreen;
        WindowFrame.CornerRadius = new CornerRadius(maximized ? 0 : FrameRadius);
        WindowFrame.BorderThickness = new Thickness(maximized ? 0 : 1);
        foreach (var grip in new[] { GripLeft, GripRight, GripTop, GripBottom, GripBottomRight, GripBottomLeft })
        {
            grip.IsVisible = !maximized;
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_exiting)
        {
            return;
        }

        // Logout or power-off: never hold the session open (cancelling here makes the desktop
        // report "Colitu is preventing logout"). Stop the tunnel and hand the proxy back now.
        if (e.CloseReason is WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown)
        {
            _exiting = true;
            _vpn.CleanupForSessionEnd();
            if (_trayIcon != null)
            {
                _trayIcon.IsVisible = false;
                _trayIcon.Dispose();
            }
            // ShutdownMode.OnExplicitShutdown: without this the process outlives the closed window.
            Dispatcher.UIThread.Post(() =>
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.Shutdown();
                }
            });
            return;
        }

        var keepRunning = _vpn.Preferences.CloseToTray && AppView.IsVisible && _trayIcon != null;
        e.Cancel = true;
        if (keepRunning && ColituShell.HasTrayHost() == false)
        {
            // No tray on this desktop (GNOME without AppIndicator): a hidden window could never
            // be opened again, so it stays in the task bar and the connection keeps running.
            WindowState = WindowState.Minimized;
            return;
        }
        if (keepRunning)
        {
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                Notify(Loc.I["tray.hidden"]);
            }
            return;
        }
        _ = ExitAsync();
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
        Topmost = true;
        Topmost = false;
    }

    /// <summary>Called when a second copy of the app is started.</summary>
    public void BringToFront() => ShowFromTray();

    // ── Tray ───────────────────────────────────────────────────────────────
    private void BuildTrayIcon()
    {
        if (PreviewMode)
        {
            return;
        }
        try
        {
            _trayConnectItem = new NativeMenuItem(Loc.I["tray.connect"]);
            _trayConnectItem.Click += async (_, _) => await GuardAsync("TrayConnect", TrayConnectAsync);
            var open = new NativeMenuItem(Loc.I["tray.open"]);
            open.Click += (_, _) => ShowFromTray();
            var exit = new NativeMenuItem(Loc.I["tray.exit"]);
            exit.Click += async (_, _) => await ExitAsync();
            var menu = new NativeMenu();
            menu.Items.Add(open);
            menu.Items.Add(_trayConnectItem);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(exit);
            FillTrayServers(menu);

            _trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://ColituVPN/Assets/Colitu/colitu-tray.ico"))),
                ToolTipText = "Colitu VPN",
                Menu = menu,
                IsVisible = true
            };
            _trayIcon.Clicked += (_, _) => ShowFromTray();
            if (Application.Current is { } app)
            {
                TrayIcon.SetIcons(app, [_trayIcon]);
            }
        }
        catch (Exception ex)
        {
            // No StatusNotifier host (plain X11 without a tray): closing the window then quits.
            Logging.SaveLog("ColituMainWindow.BuildTrayIcon", ex);
            _trayIcon = null;
        }
    }

    private void UpdateTrayMenu()
    {
        if (_trayIcon?.Menu is not { } menu)
        {
            return;
        }
        if (menu.Items.Count >= 4 && menu.Items[0] is NativeMenuItem open && menu.Items[^1] is NativeMenuItem exit)
        {
            open.Header = Loc.I["tray.open"];
            exit.Header = Loc.I["tray.exit"];
        }
        FillTrayServers(menu);
    }

    /// <summary>Tray entries added for servers (favourites submenu, recently used) since the last fill.</summary>
    private readonly List<NativeMenuItemBase> _trayServerItems = [];

    /// <summary>
    /// Favourites in a submenu (all of them), then up to three recently used servers, between "Connect"
    /// and the separator; a click picks the server and, when the VPN is off, connects right away.
    /// </summary>
    private void FillTrayServers(NativeMenu menu)
    {
        foreach (var item in _trayServerItems)
        {
            menu.Items.Remove(item);
        }
        _trayServerItems.Clear();
        var pickedId = _vpn.IsAutoSelection ? null : _vpn.SavedServerId;
        var favorites = ResolveServers(_vpn.FavoriteServerIds).Where(server => server.Available).ToList();
        var recent = ResolveServers(_vpn.RecentServerIds).Where(server => server.Available && !favorites.Contains(server)).Take(3).ToList();
        if (favorites.Count + recent.Count == 0)
        {
            return;
        }
        _trayServerItems.Add(new NativeMenuItemSeparator());
        if (favorites.Count > 0)
        {
            var submenu = new NativeMenu();
            foreach (var server in favorites)
            {
                submenu.Items.Add(TrayServerItem(server, pickedId));
            }
            _trayServerItems.Add(new NativeMenuItem(Loc.I["tray.favorites"]) { Menu = submenu });
        }
        if (recent.Count > 0)
        {
            _trayServerItems.Add(new NativeMenuItem(Loc.I["tray.recent"]) { IsEnabled = false });
            _trayServerItems.AddRange(recent.Select(server => TrayServerItem(server, pickedId)));
        }
        // After "Open" and "Connect", before the separator and "Exit".
        var at = Math.Min(2, menu.Items.Count);
        foreach (var item in _trayServerItems)
        {
            menu.Items.Insert(at++, item);
        }
    }

    private NativeMenuItem TrayServerItem(ColituVpnServer server, string? pickedId)
    {
        var title = ColituServerRow.From(server).Title;
        var item = new NativeMenuItem(title)
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = string.Equals(server.Id, pickedId, StringComparison.OrdinalIgnoreCase)
        };
        item.Click += async (_, _) => await GuardAsync("TrayServer", () => TrayConnectToAsync(server, title));
        return item;
    }

    /// <summary>A server from the tray menu: picks it and, when the VPN is off, connects right away.</summary>
    private async Task TrayConnectToAsync(ColituVpnServer server, string title)
    {
        if (!AppView.IsVisible)
        {
            ShowFromTray();
            return;
        }
        var wasActive = _vpn.Status is ColituVpnStatus.Connected or ColituVpnStatus.Reconnecting;
        _vpn.SelectServer(server, false);
        ApplyLocationCard();
        RenderServers();
        UpdateTrayMenu();
        if (wasActive)
        {
            ShowToast(Loc.I.Format("locations.switching", ("server", title)));
            await _vpn.SwitchServerAsync(server);
            ApplyStatus();
            return;
        }
        if (_vpn.Status == ColituVpnStatus.Disconnected && !_vpn.KillSwitchEngaged)
        {
            if (_vpn.NeedsSudoPassword)
            {
                // The password is asked for in the window.
                ShowFromTray();
            }
            await ToggleConnectionAsync();
        }
    }

    private async Task TrayConnectAsync()
    {
        if (!AppView.IsVisible)
        {
            ShowFromTray();
            return;
        }
        if (_vpn.KillSwitchEngaged && _vpn.Status != ColituVpnStatus.Connected)
        {
            ShowFromTray();
            await TurnOffKillSwitchBlockAsync();
            return;
        }
        if (_vpn.Status is not (ColituVpnStatus.Connected or ColituVpnStatus.Connecting or ColituVpnStatus.Reconnecting) && _vpn.NeedsSudoPassword)
        {
            // The password is asked for in the window.
            ShowFromTray();
        }
        await ToggleConnectionAsync();
    }

    public async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }
        _exiting = true;
        Hide();
        try
        {
            if (_vpn.Status is ColituVpnStatus.Connected or ColituVpnStatus.Connecting or ColituVpnStatus.Reconnecting || _vpn.KillSwitchEngaged)
            {
                await _vpn.DisconnectAsync();
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.ExitAsync", ex);
        }
        if (_vpn.KillSwitchEngaged)
        {
            // Rules adopted from a crashed run and no password this run: they stay (fail
            // closed). The next start explains them again.
            Notify(Loc.I["ksr.exitNotice"]);
        }
        if (_trayIcon != null)
        {
            _trayIcon.IsVisible = false;
            _trayIcon.Dispose();
        }
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private static void OpenUrl(string url) => ColituShell.OpenUrl(url);

    private static string LocalizedPath(string path) => $"{ColituAuthService.WebBaseUrl}{path}";
}
