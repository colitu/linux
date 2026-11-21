using System.Diagnostics;
using System.Net;

namespace v2rayN.Desktop.Services;

/// <summary>
/// ColituGecko (Colitu's own protocol) in the service (ColituGecko.cs has the rules and the sidecar).
/// Kept in its own file: <c>ColituVpnService.cs</c> only calls the hooks named here.
/// </summary>
public sealed partial class ColituVpnService
{
    // The panel's last answer (memory only; the configuration is a secret), the request in flight, why
    // the Gecko-only core failed, the next core start's plan, whether the running config got it, the
    // running gecko-client, and when need_config last fetched a new configuration.
    private volatile ColituGeckoConfigResult? _geckoConfig;
    private volatile Task<ColituGeckoConfigResult>? _geckoFetch;
    private volatile string? _geckoFailure;
    private volatile ColituGeckoPlan? _geckoPlan;
    private volatile bool _geckoApplied;
    /// <summary>A config that runs the TUN adapter got gecko-client's direct rules (required in TUN mode).</summary>
    private volatile bool _geckoDirectRule;
    private volatile ColituGeckoRun? _geckoRun;
    private long _geckoRefreshedAt;
    private int _geckoRefreshing;
    private int _geckoOfferChecked;
    /// <summary>This connect runs (or ran) the Gecko-only profile; the next core start of that profile gets gecko-client.</summary>
    private volatile bool _geckoOnly;
    /// <summary>The Gecko step ran in this connect (it is tried once per connect).</summary>
    private bool _geckoTriedThisConnect;
    private readonly ColituGeckoCooldown _geckoCooldown = new();

    /// <summary>The panel's last answer about Gecko (<see cref="ColituGeckoOffer.Unknown"/> before the first one).</summary>
    public ColituGeckoOffer GeckoOffer => _geckoConfig?.Offer ?? ColituGeckoOffer.Unknown;

    /// <summary>Raised when <see cref="GeckoOffer"/> may have changed (the protocol menu and chip follow it).</summary>
    public event Action? GeckoOfferChanged;

    /// <summary>Connected over the Gecko-only profile: the home card shows ColituGecko and the automatic exit.</summary>
    public bool ConnectedOverGecko => Status == ColituVpnStatus.Connected && _geckoOnly
        && string.Equals(_activeTransport, ColituGeckoRules.Protocol, StringComparison.Ordinal);

    /// <summary>The protocol menu's pick is ColituGecko and counts (not on a Whitelist row, not when the panel does not offer it).</summary>
    public bool GeckoPicked => ColituConnectSteps.GeckoPicked(Preferences.PreferredTransport, WhitelistSelected, GeckoOffer);

    /// <summary>The connection method picked on the home screen ("" = automatic, or ColituGecko).</summary>
    public string PreferredTransport => Preferences.PreferredTransport;

    public void SetPreferredTransport(string? protocol)
    {
        var normalized = ColituTransportChoice.Normalize(protocol);
        if (normalized == Preferences.PreferredTransport)
        {
            return;
        }
        _session = _session with { Preferences = _session.Preferences with { PreferredTransport = normalized } };
        SaveState();
        LogConnection($"Connection method picked by the user: {(normalized.Length == 0 ? "automatic" : normalized)}");
    }

    /// <summary>Constructor hook: the sidecar's events.</summary>
    private void InitGecko()
    {
        ColituGecko.Instance.Exited += OnGeckoExited;
        ColituGecko.Instance.NeedConfig += OnGeckoNeedConfig;
        ColituGecko.Instance.FloorChanged += OnGeckoFloorChanged;
    }

    /// <summary>Start of a connect: Gecko is tried again.</summary>
    private void ResetGeckoForConnect()
    {
        _geckoFailure = null;
        _geckoOnly = false;
        _geckoPlan = null;
        _geckoTriedThisConnect = false;
    }

    /// <summary>
    /// <c>GET /gecko/config</c> in the background, never waited for here: a slow or unreachable panel
    /// (whitelist, blocked network) must not delay a connect that does not need Gecko. The Gecko step
    /// waits for it (at most <see cref="ColituGeckoRules.ConfigWait"/>) when nothing is cached.
    /// </summary>
    private void StartGeckoConfigFetch(CancellationToken token)
    {
        if (_geckoFetch is { IsCompleted: false })
        {
            return;
        }
        _geckoFetch = Task.Run(async () =>
        {
            try
            {
                var result = await LoadGeckoConfigAsync(token);
                Interlocked.Exchange(ref _geckoRefreshedAt, Environment.TickCount64);
                return result;
            }
            catch (OperationCanceledException)
            {
                return new ColituGeckoConfigResult(ColituGeckoOffer.Unknown, null);
            }
            catch (Exception ex)
            {
                Logging.SaveLog("ColituVpnService.StartGeckoConfigFetch", ex);
                return new ColituGeckoConfigResult(ColituGeckoOffer.Unknown, null);
            }
        });
    }

    /// <summary>
    /// The configuration for the Gecko step: the answer to the request in flight (started at the connect,
    /// or now when nothing is cached) within <see cref="ColituGeckoRules.ConfigWait"/>, else the cached one.
    /// </summary>
    private async Task<ColituGeckoConfigResult?> GeckoConfigForStepAsync(CancellationToken token)
    {
        if (_geckoFetch is not { IsCompleted: false } && _geckoConfig?.Signed == null)
        {
            StartGeckoConfigFetch(token);
        }
        if (_geckoFetch is { IsCompleted: false } fetch)
        {
            try
            {
                await fetch.WaitAsync(ColituGeckoRules.ConfigWait, token);
            }
            catch (TimeoutException)
            {
                LogConnection($"ColituGecko: no configuration within {ColituGeckoRules.ConfigWait.TotalSeconds:0} s");
            }
        }
        return _geckoConfig;
    }

    /// <summary>Asks the panel; keeps the answer in memory (offered or not even when the request failed: unknown).</summary>
    private async Task<ColituGeckoConfigResult> LoadGeckoConfigAsync(CancellationToken token)
    {
        ColituGeckoConfigResult result;
        try
        {
            result = await ColituApiClient.Instance.GetGeckoConfigAsync(token).WaitAsync(TimeSpan.FromSeconds(8), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Type only: a message could quote the answer.
            LogConnection($"ColituGecko: settings request failed ({ex.GetType().Name})");
            result = new ColituGeckoConfigResult(ColituGeckoOffer.Unknown, null);
        }
        if (result.Offer == ColituGeckoOffer.Offered)
        {
            ColituGeckoReports.Instance.Resume();
        }
        var before = GeckoOffer;
        // An unknown answer keeps what the last real one said about the offer (and a configuration it brought).
        _geckoConfig = result.Offer == ColituGeckoOffer.Unknown && _geckoConfig is { } last
            ? last.Signed != null ? last : new ColituGeckoConfigResult(last.Offer == ColituGeckoOffer.NotOffered ? ColituGeckoOffer.NotOffered : ColituGeckoOffer.Unavailable, null)
            : result;
        LogConnection($"ColituGecko: panel answered {result.Offer}{(result.Reason != null ? $" ({result.Reason})" : "")}");
        if (before != GeckoOffer)
        {
            GeckoOfferChanged?.Invoke();
        }
        return result;
    }

    /// <summary>
    /// Once per app run, when the server list comes and nothing was asked yet: the offer decides
    /// whether the protocol menu shows ColituGecko before the first connect.
    /// </summary>
    private void CheckGeckoOfferInBackground()
    {
        if (_geckoConfig != null || Interlocked.Exchange(ref _geckoOfferChecked, 1) != 0)
        {
            return;
        }
        StartGeckoConfigFetch(CancellationToken.None);
    }

    /// <summary>
    /// The kill switch lets the entry addresses of the Gecko configuration through for gecko-client's
    /// user (see <see cref="ColituKillSwitch.Allowance.Gecko"/>); a CDN entry's front name is resolved
    /// here, before the rules go in. Only while a Gecko-only connection is being set up or runs.
    /// </summary>
    private async Task<List<ColituKillSwitch.GeckoEntry>> GeckoKillSwitchEntriesAsync()
    {
        var entries = new List<ColituKillSwitch.GeckoEntry>();
        if (!_geckoOnly || _geckoConfig?.Signed is not { } signedConfig)
        {
            return entries;
        }
        foreach (var target in ColituGeckoRules.EntryTargets(System.Text.Encoding.UTF8.GetString(signedConfig.Body)))
        {
            if (IPAddress.TryParse(target.Host, out var literal))
            {
                entries.Add(new ColituKillSwitch.GeckoEntry(literal, target.Port));
                continue;
            }
            var addresses = ColituPinnedHosts.AddressesOf(target.Host).ToList();
            try
            {
                if (await ColituNetwork.ResolveServerAsync(target.Host) is { } resolved)
                {
                    addresses.Add(resolved);
                }
            }
            catch (Exception ex)
            {
                Logging.SaveLog($"ColituVpnService.GeckoKillSwitchEntries: {ex.GetType().Name}");
            }
            entries.AddRange(addresses.Select(address => new ColituKillSwitch.GeckoEntry(address, target.Port)));
        }
        return entries;
    }

    /// <summary>Gecko for the core about to start: only for the Gecko-only profile of a Gecko step (a fresh SOCKS account per start).</summary>
    private ColituGeckoPlan? PlanGecko(ProfileItem profile)
    {
        if (!_geckoOnly || !string.Equals(profile.IndexId, ColituGeckoRules.ProfileIndexId, StringComparison.Ordinal))
        {
            return null;
        }
        // gecko-client's SOCKS port takes a per-run account: only the core may use it.
        var socks = ColituWarmSpare.NewVerifyInbound();
        return new ColituGeckoPlan(profile.IndexId, socks.Port, socks.User, socks.Password, Path.GetFullPath(ColituGecko.ExecutablePath));
    }

    /// <summary>
    /// The Gecko stage of <see cref="PostProcessConfig"/>: the main core's config becomes the Gecko-only
    /// profile; every sing-box config that runs the TUN adapter (also the front of an Xray tunnel)
    /// sends gecko-client's own connections direct. Without a plan the config is returned as it is.
    /// </summary>
    private string ApplyGecko(CoreConfigContext context, string json)
    {
        var plan = _geckoPlan;
        if (plan == null)
        {
            return json;
        }
        var singBox = context.RunCoreType == ECoreType.sing_box;
        try
        {
            if (string.Equals(context.Node?.IndexId, plan.PrimaryIndexId, StringComparison.Ordinal))
            {
                if (ColituGeckoRules.ApplyOnly(json, singBox, plan) is { } applied)
                {
                    _geckoApplied = true;
                    _geckoDirectRule |= singBox && ColituGeckoRules.HasDirectRules(applied, plan.ClientPath);
                    return applied;
                }
                _geckoFailure = $"the {context.RunCoreType} config could not take the Gecko-only profile";
                LogConnection($"ColituGecko: {_geckoFailure}");
                return json;
            }
            if (singBox && context.IsTunEnabled)
            {
                if (ColituGeckoRules.AddSidecarDirectRules(json, plan.ClientPath) is { } front)
                {
                    _geckoDirectRule |= ColituGeckoRules.HasDirectRules(front, plan.ClientPath);
                    return front;
                }
                // gecko-client's connections would loop into the TUN.
                _geckoFailure = "gecko-client's direct rule could not be added to the TUN config";
                LogConnection($"ColituGecko: {_geckoFailure}");
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituVpnService.ApplyGecko", ex);
        }
        return json;
    }

    /// <summary>Before the core loads: the previous gecko-client goes (its SOCKS account is not in the new config) and the plan is made.</summary>
    private void PrepareGeckoForCoreStart(ProfileItem profileItem)
    {
        ColituGecko.Instance.Stop();
        _geckoRun = null;
        _geckoApplied = false;
        _geckoDirectRule = false;
        _geckoFailure = null;
        _geckoPlan = PlanGecko(profileItem);
    }

    /// <summary>
    /// Starts gecko-client for a Gecko-only core and waits for its session (at most
    /// <see cref="ColituGeckoRules.ReadyDeadline"/>). Nothing to do for any other core. Throws when
    /// gecko-client cannot carry the connection: the Gecko step (or the restart) then fails.
    /// </summary>
    private async Task StartGeckoAsync(bool tun, CancellationToken token)
    {
        if (_geckoPlan is not { } plan)
        {
            return;
        }
        if (!_geckoApplied)
        {
            _geckoFailure ??= "the core config did not take the Gecko-only profile";
        }
        else if (tun && !_geckoDirectRule)
        {
            // No config that runs the TUN adapter sends gecko-client direct: its traffic would loop.
            _geckoFailure ??= "no TUN config took gecko-client's direct rule";
        }
        if (_geckoFailure == null && _geckoConfig?.Signed == null)
        {
            _geckoFailure = "Gecko settings unavailable";
        }
        if (_geckoFailure is { } failure)
        {
            throw new InvalidOperationException($"ColituGecko: {failure}");
        }
        try
        {
            _geckoRun = ColituGecko.Instance.Start(new ColituGeckoLaunch(plan.SocksPort, plan.SocksUser, plan.SocksPassword), _geckoConfig!.Signed!,
                new ColituGeckoFloor(Preferences.GeckoMinIssued, Preferences.GeckoSkinRevs), LogConnection);
        }
        catch (Exception ex)
        {
            ColituGecko.Instance.Stop();
            _geckoRun = null;
            _geckoFailure = $"gecko-client could not start ({ex.GetType().Name})";
            throw new InvalidOperationException($"ColituGecko: {_geckoFailure}");
        }
        var verdict = await _geckoRun.Ready.WaitAsync(token);
        if (verdict.Length > 0)
        {
            _geckoFailure = verdict;
            _geckoRun = null;
            ColituGecko.Instance.Stop();
            throw new InvalidOperationException($"ColituGecko: {verdict}");
        }
        LogConnection("ColituGecko: session up");
    }

    /// <summary>
    /// The Gecko step of a connect (once per connect): the Gecko-only profile, checked like any other
    /// connection. True when it carries traffic. Skipped (false) when the panel does not offer it, no
    /// configuration comes within 6 s, or (not on a manual pick) the network is cooling down after a
    /// failure. A failure starts the cool-down. <paramref name="manual"/>: the user picked ColituGecko; a
    /// skip or a failure throws the normal connect error instead (no silent switch to another protocol).
    /// </summary>
    private async Task<bool> TryGeckoStepAsync(bool manual, string why, CancellationToken token)
    {
        if (_geckoTriedThisConnect)
        {
            return false;
        }
        _geckoTriedThisConnect = true;
        var watch = Stopwatch.StartNew();
        var config = await GeckoConfigForStepAsync(token);
        token.ThrowIfCancellationRequested();
        // The kill switch lets gecko-client reach the configuration's entries (GeckoKillSwitchEntriesAsync)
        // and the TUN's direct rule marks its connections, so it never pins the cores here.
        var reason = ColituGeckoRules.OffReason(ColituGeckoRules.EffectiveOffer(config), File.Exists(ColituGecko.ExecutablePath), false,
            _geckoCooldown.Active(_networkKey, DateTimeOffset.UtcNow), manual);
        if (reason != null)
        {
            LogConnection($"ColituGecko: step skipped ({reason}; {why})");
            JournalGeckoStep(manual, why, ok: false, reason, watch.ElapsedMilliseconds, skipped: true);
            if (manual)
            {
                throw new ColituConnectException(Loc.I["err.geckoUnavailable"]);
            }
            return false;
        }
        LogConnection($"ColituGecko: Gecko-only connection ({why})");
        ConnectStage = "connect.gecko";
        StatusChanged?.Invoke(Status);
        _geckoOnly = true;
        _geckoFailure = null;
        _attemptProtocol = ColituGeckoRules.Protocol;
        _attemptNodeId = null;
        try
        {
            await StopCoreAsync();
            _sparePlan = null;
            _spareServer = null;
            _pendingSpare = null;
            _currentProfiles = [];
            var profile = ColituGeckoRules.Profile(ColituSubId, ColituWarmSpare.NewVerifyInbound().Port);
            await ConfigHandler.AddSocksServer(_config, profile);
            if (await ConfigHandler.SetDefaultServerIndex(_config, ColituGeckoRules.ProfileIndexId) != 0)
            {
                throw new InvalidOperationException("The ColituGecko profile could not be selected.");
            }
            await PrepareConnectionModeAsync(null);
            if (KillSwitchApplies)
            {
                // The entries of the configuration in hand are let through for gecko-client.
                await EngageKillSwitchAsync("ColituGecko");
            }
            await ReloadCoreAsync(token);
            var probe = await VerifyConnectionActiveAsync(null, 2, token, TimeSpan.FromSeconds(6));
            if (!probe.Success)
            {
                throw new ColituConnectException(Loc.I["err.gecko"]);
            }
            _activeTransport = ColituGeckoRules.Protocol;
            _geckoCooldown.Succeeded(_networkKey);
            LogConnection($"ColituGecko: connected in {watch.ElapsedMilliseconds} ms (automatic exit)");
            JournalGeckoStep(manual, why, ok: true, null, watch.ElapsedMilliseconds);
            return true;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && token.IsCancellationRequested))
        {
            _geckoOnly = false;
            _geckoRun = null;
            _geckoPlan = null;
            await StopCoreAsync();
            _geckoCooldown.Failed(_networkKey, DateTimeOffset.UtcNow);
            var failure = _geckoFailure ?? ex.Message;
            LogConnection($"ColituGecko: failed ({failure}); cool-down {ColituGeckoRules.CoolDown.TotalMinutes:0} min for automatic connects on this network");
            JournalGeckoStep(manual, why, ok: false, failure, watch.ElapsedMilliseconds);
            if (manual)
            {
                throw ex as ColituConnectException ?? new ColituConnectException(Loc.I["err.gecko"], ex);
            }
            return false;
        }
    }

    /// <summary>The Adaptive Connect journal records the Gecko step like the other steps (name <c>gecko</c>).</summary>
    private void JournalGeckoStep(bool manual, string why, bool ok, string? reason, long ms, bool skipped = false)
    {
        try
        {
            ColituEngineJournal.Instance.Add(ColituEngineJournal.Gecko, new
            {
                manual,
                why,
                ok,
                skipped,
                // Reasons are the app's own words (no configuration, no server name).
                reason,
                ms
            });
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"ColituVpnService.JournalGeckoStep: {ex.GetType().Name}");
        }
    }

    /// <summary>The normal servers' failure lets the Gecko step follow: not when there is no plan, the device is paused, the allowance is used up or the computer is offline.</summary>
    private static bool GeckoMayFollow(Exception ex) =>
        ex is not OperationCanceledException
        && !(ex is ColituApiException api && (IsPlanError(api) || api.ErrorCode == ColituDevicePause.ErrorCode))
        && ex is not ColituSudoRequiredException
        && !ColituUpsell.IsQuotaError(ex)
        && !ColituConnectException.IsOffline(ex);

    /// <summary>
    /// gecko-client of a Gecko-only connection exited: nothing carries the traffic, so the Gecko-only
    /// core is started again; if that fails the connection is set up again (Adaptive Connect or the pick).
    /// </summary>
    private void OnGeckoExited(string reason)
    {
        if (Status != ColituVpnStatus.Connected || _userDisconnected || !_geckoOnly)
        {
            // A connect or restart in progress settles Gecko itself.
            return;
        }
        _geckoRun = null;
        LogConnection($"ColituGecko: {reason}; starting it again");
        _ = Task.Run(async () =>
        {
            try
            {
                if (!await ReloadCoreForSpareAsync("ColituGecko restart") && !_userDisconnected && _geckoOnly)
                {
                    LogConnection("ColituGecko: the restart did not bring it back; connecting again");
                    await ReconnectAsync();
                }
            }
            catch (Exception ex)
            {
                Logging.SaveLog("ColituVpnService.OnGeckoExited", ex);
            }
        });
    }

    /// <summary>Right after "Connected": a gecko-client that exited while the connect was finishing, and the one-time offer check.</summary>
    private void AfterGeckoConnected()
    {
        if (_geckoOnly && _geckoRun != null && !ColituGecko.Instance.Running)
        {
            OnGeckoExited("gecko-client exited");
        }
        CheckGeckoOfferInBackground();
    }

    private void OnGeckoFloorChanged(long? issued, IReadOnlyDictionary<string, long>? revs)
    {
        var current = _session.Preferences;
        var next = ColituGeckoRules.MergeFloor(new ColituGeckoFloor(current.GeckoMinIssued, current.GeckoSkinRevs), issued, revs);
        if (next.MinIssued == current.GeckoMinIssued && FloorEquals(next.SkinFloor, current.GeckoSkinRevs))
        {
            return;
        }
        // Kept in the app's own state: a lower value than the stored one is not taken (F-14, F-15).
        _session = _session with
        {
            Preferences = current with { GeckoMinIssued = next.MinIssued, GeckoSkinRevs = next.SkinFloor?.ToDictionary(r => r.Key, r => r.Value) }
        };
        SaveState();
    }

    private static bool FloorEquals(IReadOnlyDictionary<string, long>? a, IReadOnlyDictionary<string, long>? b) =>
        (a?.Count ?? 0) == (b?.Count ?? 0) && (a == null || a.All(r => b != null && b.TryGetValue(r.Key, out var v) && v == r.Value));

    /// <summary>
    /// gecko-client asked for a new configuration: fetched from the panel (the app's normal HTTP
    /// client) at most once per <see cref="ColituGeckoRules.ConfigRefreshInterval"/> and handed over.
    /// </summary>
    private void OnGeckoNeedConfig(string reason)
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _geckoRefreshedAt);
        if (last != 0 && now - last < (long)ColituGeckoRules.ConfigRefreshInterval.TotalMilliseconds)
        {
            return;
        }
        if (Interlocked.Exchange(ref _geckoRefreshing, 1) != 0)
        {
            return;
        }
        Interlocked.Exchange(ref _geckoRefreshedAt, now);
        _ = Task.Run(async () =>
        {
            try
            {
                LogConnection($"ColituGecko: new configuration requested ({reason})");
                var result = await LoadGeckoConfigAsync(CancellationToken.None);
                if (result.Signed is { } signedConfig)
                {
                    LogConnection(ColituGecko.Instance.SendConfig(signedConfig)
                        ? "ColituGecko: new configuration handed over"
                        : "ColituGecko: gecko-client did not take the new configuration");
                }
            }
            catch (Exception ex)
            {
                Logging.SaveLog($"ColituVpnService.OnGeckoNeedConfig: {ex.GetType().Name}");
            }
            finally
            {
                Interlocked.Exchange(ref _geckoRefreshing, 0);
            }
        });
    }
}
