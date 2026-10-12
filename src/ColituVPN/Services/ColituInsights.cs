using System.Text.Json;
using System.Text.Json.Serialization;

namespace v2rayN.Desktop.Services;

/// <summary>
/// Anonymous usage statistics (POST /client/metrics): the names the panel accepts, the bucketing of
/// durations and the caps. Only totals leave the computer: no account, device, IP or visited site.
/// </summary>
public static class ColituInsightSpec
{
    public static readonly string[] CounterNames =
    [
        "connect_attempts", "connect_successes", "first_connect_attempts", "first_connect_successes",
        "sessions", "unexpected_disconnects", "functional_checks", "functional_ok",
        "recoveries", "recovery_successes", "protocol_switches", "protocol_switch_successes", "flaps"
    ];

    public static readonly string[] HistogramNames = ["connect_ms", "recovery_ms", "detection_ms"];

    public static readonly string[] FeatureNames =
    [
        "split_tunnel", "shield", "ad_block", "advanced_mode", "multihop",
        "auto_connect", "kill_switch", "manual_server", "custom_dns"
    ];

    /// <summary>Upper bounds in milliseconds (inclusive); the ninth bucket is everything slower.</summary>
    public static readonly int[] BucketBoundsMs = [500, 1000, 2000, 3000, 5000, 8000, 15000, 30000];

    public const int BucketCount = 9;

    /// <summary>Each counter, and each histogram as a whole, is capped at this per report (the panel refuses more).</summary>
    public const int MaxPerReport = 10000;

    /// <summary>At most one report per this long.</summary>
    public static readonly TimeSpan MinSendInterval = TimeSpan.FromHours(6);

    /// <summary>After a failed send (429, 5xx, no network) the next try waits this long.</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(30);

    /// <summary>Switching back to the protocol just left, within this long, is a flap.</summary>
    public static readonly TimeSpan FlapWindow = TimeSpan.FromMinutes(2);

    /// <summary>A recovery that has not ended after this long is forgotten (never counted).</summary>
    public static readonly TimeSpan MaxRecoveryAge = TimeSpan.FromMinutes(10);

    public const string Platform = "linux";

    /// <summary>The histogram bucket of a duration: the first whose upper bound is not below it.</summary>
    public static int BucketIndex(long ms)
    {
        for (var index = 0; index < BucketBoundsMs.Length; index++)
        {
            if (ms <= BucketBoundsMs[index])
            {
                return index;
            }
        }
        return BucketBoundsMs.Length;
    }

    /// <summary>The features in use with these preferences (reported at every successful connect).</summary>
    public static List<string> FeaturesOf(ColituVpnPreferences preferences, bool manualServer, bool multihop)
    {
        var features = new List<string>();
        if (!string.Equals(ColituSplitTunnelModes.Normalize(preferences.SplitTunnelMode), ColituSplitTunnelModes.Off, StringComparison.Ordinal))
        {
            features.Add("split_tunnel");
        }
        // Privacy mode: everything through the tunnel, no direct-routing exceptions.
        if (preferences.PrivacyModeEnabled)
        {
            features.Add("shield");
        }
        if (preferences.AdBlockEnabled)
        {
            features.Add("ad_block");
        }
        if (preferences.AdvancedMode)
        {
            features.Add("advanced_mode");
        }
        if (multihop)
        {
            features.Add("multihop");
        }
        if (preferences.AutoConnectEnabled)
        {
            features.Add("auto_connect");
        }
        // On Linux the kill switch (nftables) only works in TUN mode: report it where it actually applies.
        if (preferences.KillSwitchEnabled && preferences.IsTunMode)
        {
            features.Add("kill_switch");
        }
        if (manualServer)
        {
            features.Add("manual_server");
        }
        return features;
    }
}

/// <summary>What is saved between runs: the two switches, the pending counters and a few dates.</summary>
public sealed class ColituInsightState
{
    [JsonPropertyName("stats")] public bool Stats { get; set; } = true;
    [JsonPropertyName("surveys")] public bool Surveys { get; set; } = true;
    [JsonPropertyName("counters")] public Dictionary<string, long> Counters { get; set; } = [];
    [JsonPropertyName("histograms")] public Dictionary<string, int[]> Histograms { get; set; } = [];
    [JsonPropertyName("features")] public List<string> Features { get; set; } = [];
    /// <summary>The very first connection attempt of this install has been seen (counted or not).</summary>
    [JsonPropertyName("firstConnectDone")] public bool FirstConnectDone { get; set; }
    [JsonPropertyName("lastSentAt")] public DateTimeOffset? LastSentAt { get; set; }
    /// <summary>Local day (yyyy-MM-dd) the survey endpoint was last asked.</summary>
    [JsonPropertyName("lastSurveyDay")] public string? LastSurveyDay { get; set; }
    /// <summary>Adaptive Connect / connection events not sent yet (oldest first, at most 1000).</summary>
    [JsonPropertyName("events")] public List<ColituAcEvent> Events { get; set; } = [];
    [JsonPropertyName("lastEventsSentAt")] public DateTimeOffset? LastEventsSentAt { get; set; }
}

/// <summary>One request of events and exactly which queued events it carries (removed once the panel took them).</summary>
public sealed class ColituAcBatch
{
    public Dictionary<string, object> Body { get; init; } = [];
    public List<ColituAcEvent> Events { get; init; } = [];

    public string ToJson() => JsonSerializer.Serialize(Body);
}

/// <summary>One report: the body to send and exactly what it carried (to subtract once the panel took it).</summary>
public sealed class ColituInsightReport
{
    public Dictionary<string, object> Body { get; init; } = [];
    public Dictionary<string, long> Counters { get; init; } = [];
    public Dictionary<string, int[]> Histograms { get; init; } = [];
    public List<string> Features { get; init; } = [];

    public string ToJson() => JsonSerializer.Serialize(Body);
}

/// <summary>
/// The local collector: counters, duration histograms and feature use since the last successful
/// report. Everything is a no-op while the statistics are off. Thread safe; saved after every change.
/// </summary>
public sealed class ColituInsights
{
    public static ColituInsights Instance { get; } = new(
        () => ColituHardening.UserConfigPath("colitu-insights.json"),
        () => Environment.TickCount64,
        // An install that already has a connection state file is not a new install.
        () => File.Exists(Utils.GetConfigPath("colitu-vpn-state.json")));

    private readonly Func<string> _path;
    private readonly Func<long> _ticks;
    private readonly Func<bool> _existingInstall;
    private readonly object _gate = new();
    private ColituInsightState? _state;

    /// <summary>Tick the open recovery started at; null when none.</summary>
    private long? _recoveryStart;
    private bool _dropCounted;
    private (string From, string To, DateTimeOffset At)? _lastSwitch;
    private DateTimeOffset _lastFailedSend = DateTimeOffset.MinValue;
    private DateTimeOffset _lastFailedEventSend = DateTimeOffset.MinValue;

    public ColituInsights(Func<string> path, Func<long>? ticks = null, Func<bool>? existingInstall = null)
    {
        _path = path;
        _ticks = ticks ?? (() => Environment.TickCount64);
        _existingInstall = existingInstall ?? (() => false);
    }

    public bool StatsEnabled
    {
        get { lock (_gate) { return Load().Stats; } }
    }

    public bool SurveysEnabled
    {
        get { lock (_gate) { return Load().Surveys; } }
    }

    /// <summary>Loads the saved state now (at app start, before anything writes the connection state file).</summary>
    public void Touch()
    {
        lock (_gate)
        {
            Load();
        }
    }

    /// <summary>Off: nothing is collected or sent and what is pending is deleted.</summary>
    public void SetStatsEnabled(bool enabled)
    {
        lock (_gate)
        {
            var state = Load();
            state.Stats = enabled;
            if (!enabled)
            {
                state.Counters.Clear();
                state.Histograms.Clear();
                state.Features.Clear();
                state.Events.Clear();
                _recoveryStart = null;
                _dropCounted = false;
                _lastSwitch = null;
            }
            Save();
        }
    }

    public void SetSurveysEnabled(bool enabled)
    {
        lock (_gate)
        {
            Load().Surveys = enabled;
            Save();
        }
    }

    // ── Collecting ─────────────────────────────────────────────────────────
    public void Count(string name, int amount = 1)
    {
        lock (_gate)
        {
            if (AddCounter(Load(), name, amount))
            {
                Save();
            }
        }
    }

    /// <summary>Adds a duration to a histogram (connect_ms, recovery_ms, detection_ms).</summary>
    public void Observe(string histogram, long ms)
    {
        lock (_gate)
        {
            if (AddObservation(Load(), histogram, ms))
            {
                Save();
            }
        }
    }

    public void AddFeatures(IEnumerable<string> features)
    {
        lock (_gate)
        {
            var state = Load();
            if (!state.Stats)
            {
                return;
            }
            var changed = false;
            foreach (var feature in features)
            {
                if (Array.IndexOf(ColituInsightSpec.FeatureNames, feature) >= 0 && !state.Features.Contains(feature))
                {
                    state.Features.Add(feature);
                    changed = true;
                }
            }
            if (changed)
            {
                Save();
            }
        }
    }

    /// <summary>
    /// A connection attempt that the user (or auto-start) began has ended. Only the very first one of
    /// this install also feeds first_connect_*; it is "used up" even while the statistics are off.
    /// </summary>
    public void ConnectFinished(bool success, long ms)
    {
        lock (_gate)
        {
            var state = Load();
            var first = !state.FirstConnectDone;
            state.FirstConnectDone = true;
            if (state.Stats)
            {
                AddCounter(state, "connect_attempts", 1);
                if (success)
                {
                    AddCounter(state, "connect_successes", 1);
                    AddCounter(state, "sessions", 1);
                    AddObservation(state, "connect_ms", ms);
                    _dropCounted = false;
                }
                if (first)
                {
                    AddCounter(state, "first_connect_attempts", 1);
                    if (success)
                    {
                        AddCounter(state, "first_connect_successes", 1);
                    }
                }
            }
            Save();
        }
    }

    /// <summary>Internet verified (or not) through the tunnel right after connecting.</summary>
    public void FunctionalCheck(bool ok)
    {
        lock (_gate)
        {
            var state = Load();
            if (!state.Stats)
            {
                return;
            }
            AddCounter(state, "functional_checks", 1);
            if (ok)
            {
                AddCounter(state, "functional_ok", 1);
            }
            Save();
        }
    }

    /// <summary>The tunnel was lost without the user asking (counted once until it is back or a new session starts).</summary>
    public bool UnexpectedDisconnect()
    {
        lock (_gate)
        {
            var state = Load();
            if (!state.Stats || _dropCounted)
            {
                return false;
            }
            _dropCounted = true;
            AddCounter(state, "unexpected_disconnects", 1);
            Save();
            return true;
        }
    }

    /// <summary>Adaptive Connect starts repairing a tunnel. A second start while one is open does not restart the clock.</summary>
    /// <summary>True when this started a new recovery (none was open).</summary>
    public bool BeginRecovery()
    {
        lock (_gate)
        {
            if (!Load().Stats)
            {
                return false;
            }
            var now = _ticks();
            if (_recoveryStart is { } start && now - start < ColituInsightSpec.MaxRecoveryAge.TotalMilliseconds)
            {
                return false;
            }
            _recoveryStart = now;
            return true;
        }
    }

    /// <summary>The recovery ended: working again (recovery_ms) or given up. No recovery open: nothing.</summary>
    /// <returns>How long the recovery took (ms), or null when none was open.</returns>
    public long? EndRecovery(bool success)
    {
        lock (_gate)
        {
            var state = Load();
            if (_recoveryStart is not { } start)
            {
                return null;
            }
            _recoveryStart = null;
            _dropCounted = false;
            if (!state.Stats)
            {
                return null;
            }
            var elapsed = _ticks() - start;
            AddCounter(state, "recoveries", 1);
            if (success)
            {
                AddCounter(state, "recovery_successes", 1);
                AddObservation(state, "recovery_ms", elapsed);
            }
            Save();
            return elapsed;
        }
    }

    /// <summary>The user disconnected or signed out in the middle of a recovery: it counts as neither.</summary>
    public void AbandonRecovery()
    {
        lock (_gate)
        {
            _recoveryStart = null;
            _dropCounted = false;
        }
    }

    /// <summary>
    /// A protocol switch ended. A switch back to the protocol just left, inside two minutes, is also a flap.
    /// </summary>
    /// <returns>True when the switch was a flap.</returns>
    public bool ProtocolSwitch(string? from, string? to, bool success, DateTimeOffset now)
    {
        lock (_gate)
        {
            var state = Load();
            if (!state.Stats)
            {
                return false;
            }
            var flap = false;
            AddCounter(state, "protocol_switches", 1);
            if (success)
            {
                AddCounter(state, "protocol_switch_successes", 1);
                if (!string.IsNullOrEmpty(from) && !string.IsNullOrEmpty(to))
                {
                    if (_lastSwitch is { } last
                        && string.Equals(last.From, to, StringComparison.OrdinalIgnoreCase)
                        && now - last.At <= ColituInsightSpec.FlapWindow)
                    {
                        AddCounter(state, "flaps", 1);
                        flap = true;
                    }
                    _lastSwitch = (from, to, now);
                }
            }
            Save();
            return flap;
        }
    }

    // ── Adaptive Connect / connection events (POST /client/ac-events) ──────
    /// <summary>
    /// Queues an event (validated and cut to its kind, see <see cref="ColituAcSpec.Normalize"/>): nothing while the
    /// statistics are off. The queue holds 1000 events, the oldest are dropped. Of the probes of one node (and
    /// protocol) only the last one of each clock hour is kept.
    /// </summary>
    public void AddEvent(ColituAcEvent? source, DateTimeOffset? now = null)
    {
        var stamp = now ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            var state = Load();
            if (!state.Stats)
            {
                return;
            }
            if (source != null)
            {
                source.Stamp = stamp;
            }
            var item = ColituAcSpec.Normalize(source);
            if (item == null)
            {
                return;
            }
            if (item.Kind == "probe")
            {
                var hour = stamp.ToUnixTimeSeconds() / 3600;
                state.Events.RemoveAll(other => other.Kind == "probe" && other.NodeId == item.NodeId && other.Protocol == item.Protocol
                    && other.Stamp != default && other.Stamp.ToUnixTimeSeconds() / 3600 == hour);
            }
            state.Events.Add(item);
            if (state.Events.Count > ColituAcSpec.MaxQueued)
            {
                state.Events.RemoveRange(0, state.Events.Count - ColituAcSpec.MaxQueued);
            }
            Save();
        }
    }

    public int QueuedEvents
    {
        get { lock (_gate) { return Load().Events.Count; } }
    }

    /// <summary>
    /// The next request of up to 300 events (oldest first), or null: statistics off, nothing queued, less than an hour
    /// since the last send, or a failed send less than half an hour ago.
    /// </summary>
    public ColituAcBatch? BuildEventBatch(DateTimeOffset now, string appVersion, string networkToken, string networkType)
    {
        lock (_gate)
        {
            var state = Load();
            if (!state.Stats || state.Events.Count == 0
                || (state.LastEventsSentAt is { } sent && now - sent < ColituAcSpec.MinSendInterval)
                || now - _lastFailedEventSend < ColituInsightSpec.RetryAfterFailure)
            {
                return null;
            }
            return ComposeEvents(state, appVersion, networkToken, networkType);
        }
    }

    internal static ColituAcBatch ComposeEvents(ColituInsightState state, string appVersion, string networkToken, string networkType)
    {
        var events = state.Events.Take(ColituAcSpec.MaxPerRequest).ToList();
        return new ColituAcBatch
        {
            Events = events,
            Body = new Dictionary<string, object>
            {
                ["platform"] = ColituInsightSpec.Platform,
                ["app_version"] = appVersion,
                ["network_token"] = networkToken ?? "",
                ["network_type"] = ColituAcSpec.NetworkType(networkType),
                ["events"] = events
            }
        };
    }

    /// <summary>The panel took the batch (or refused it for good): exactly those events leave the queue.</summary>
    public void CommitEvents(ColituAcBatch batch, DateTimeOffset now)
    {
        lock (_gate)
        {
            var state = Load();
            var sent = new HashSet<ColituAcEvent>(batch.Events, ReferenceEqualityComparer.Instance as IEqualityComparer<ColituAcEvent>);
            state.Events.RemoveAll(sent.Contains);
            state.LastEventsSentAt = now;
            _lastFailedEventSend = DateTimeOffset.MinValue;
            Save();
        }
    }

    public void EventSendFailed(DateTimeOffset now)
    {
        lock (_gate)
        {
            _lastFailedEventSend = now;
        }
    }

    // ── Reporting ──────────────────────────────────────────────────────────
    /// <summary>True when there is a counter or a duration to report.</summary>
    public bool HasData
    {
        get
        {
            lock (_gate)
            {
                var state = Load();
                return state.Counters.Values.Any(value => value > 0) || state.Histograms.Values.Any(buckets => buckets.Any(value => value > 0));
            }
        }
    }

    /// <summary>
    /// The report to send now, or null: statistics off, nothing to report, less than six hours since the last
    /// report, or a failed send less than half an hour ago.
    /// </summary>
    public ColituInsightReport? BuildReport(DateTimeOffset now, string appVersion, bool killSwitch)
    {
        lock (_gate)
        {
            var state = Load();
            if (!state.Stats
                || (state.LastSentAt is { } sent && now - sent < ColituInsightSpec.MinSendInterval)
                || now - _lastFailedSend < ColituInsightSpec.RetryAfterFailure)
            {
                return null;
            }
            return Compose(state, appVersion, killSwitch);
        }
    }

    /// <summary>The report body for the current data, whatever the timing (null without data).</summary>
    internal ColituInsightReport? Compose(ColituInsightState state, string appVersion, bool killSwitch)
    {
        var counters = new Dictionary<string, long>();
        foreach (var name in ColituInsightSpec.CounterNames)
        {
            if (state.Counters.TryGetValue(name, out var value) && value > 0)
            {
                counters[name] = Math.Min(value, ColituInsightSpec.MaxPerReport);
            }
        }
        var histograms = new Dictionary<string, int[]>();
        foreach (var name in ColituInsightSpec.HistogramNames)
        {
            if (state.Histograms.TryGetValue(name, out var buckets) && buckets.Length == ColituInsightSpec.BucketCount && buckets.Any(value => value > 0))
            {
                histograms[name] = CapBuckets(buckets);
            }
        }
        if (counters.Count == 0 && histograms.Count == 0)
        {
            return null;
        }
        var features = ColituInsightSpec.FeatureNames.Where(state.Features.Contains).ToList();
        return new ColituInsightReport
        {
            Counters = counters,
            Histograms = histograms,
            Features = features,
            Body = new Dictionary<string, object>
            {
                ["platform"] = ColituInsightSpec.Platform,
                ["app_version"] = appVersion,
                ["counters"] = counters,
                ["histograms"] = histograms,
                ["kill_switch"] = killSwitch,
                ["features"] = features
            }
        };
    }

    /// <summary>The panel took the report (or refused it for good): what it carried is subtracted, what came in meanwhile stays.</summary>
    public void Commit(ColituInsightReport report, DateTimeOffset now)
    {
        lock (_gate)
        {
            var state = Load();
            foreach (var (name, sent) in report.Counters)
            {
                if (state.Counters.TryGetValue(name, out var value))
                {
                    var left = value - sent;
                    if (left > 0)
                    {
                        state.Counters[name] = left;
                    }
                    else
                    {
                        state.Counters.Remove(name);
                    }
                }
            }
            foreach (var (name, sent) in report.Histograms)
            {
                if (state.Histograms.TryGetValue(name, out var buckets) && buckets.Length == sent.Length)
                {
                    for (var index = 0; index < buckets.Length; index++)
                    {
                        buckets[index] = Math.Max(0, buckets[index] - sent[index]);
                    }
                    if (buckets.All(value => value == 0))
                    {
                        state.Histograms.Remove(name);
                    }
                }
            }
            state.Features.RemoveAll(report.Features.Contains);
            state.LastSentAt = now;
            _lastFailedSend = DateTimeOffset.MinValue;
            Save();
        }
    }

    /// <summary>A send that did not get through (429, 5xx, no network): everything stays, the next try waits.</summary>
    public void SendFailed(DateTimeOffset now)
    {
        lock (_gate)
        {
            _lastFailedSend = now;
        }
    }

    // ── Surveys ────────────────────────────────────────────────────────────
    /// <summary>The survey endpoint has not been asked today (local date) and surveys are on.</summary>
    public bool SurveyCheckDue(DateTime today)
    {
        lock (_gate)
        {
            var state = Load();
            return state.Surveys && !string.Equals(state.LastSurveyDay, DayKey(today), StringComparison.Ordinal);
        }
    }

    public void MarkSurveyChecked(DateTime today)
    {
        lock (_gate)
        {
            Load().LastSurveyDay = DayKey(today);
            Save();
        }
    }

    internal static string DayKey(DateTime day) => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    // ── Internals ──────────────────────────────────────────────────────────
    internal static int[] CapBuckets(int[] buckets)
    {
        var capped = new int[buckets.Length];
        var room = ColituInsightSpec.MaxPerReport;
        for (var index = 0; index < buckets.Length && room > 0; index++)
        {
            capped[index] = Math.Clamp(buckets[index], 0, room);
            room -= capped[index];
        }
        return capped;
    }

    private static bool AddCounter(ColituInsightState state, string name, int amount)
    {
        if (!state.Stats || amount <= 0 || Array.IndexOf(ColituInsightSpec.CounterNames, name) < 0)
        {
            return false;
        }
        state.Counters.TryGetValue(name, out var current);
        // A counter that nobody could send for a long time stops growing instead of overflowing.
        state.Counters[name] = Math.Min(current + amount, ColituInsightSpec.MaxPerReport * 10L);
        return true;
    }

    private static bool AddObservation(ColituInsightState state, string histogram, long ms)
    {
        if (!state.Stats || ms < 0 || Array.IndexOf(ColituInsightSpec.HistogramNames, histogram) < 0)
        {
            return false;
        }
        if (!state.Histograms.TryGetValue(histogram, out var buckets) || buckets.Length != ColituInsightSpec.BucketCount)
        {
            buckets = new int[ColituInsightSpec.BucketCount];
            state.Histograms[histogram] = buckets;
        }
        var index = ColituInsightSpec.BucketIndex(ms);
        if (buckets.Sum() < ColituInsightSpec.MaxPerReport * 10L && buckets[index] < int.MaxValue / 2)
        {
            buckets[index]++;
        }
        return true;
    }

    private ColituInsightState Load()
    {
        if (_state != null)
        {
            return _state;
        }
        var fresh = false;
        try
        {
            var path = _path();
            if (File.Exists(path))
            {
                _state = JsonSerializer.Deserialize<ColituInsightState>(File.ReadAllText(path));
            }
            else
            {
                fresh = true;
            }
        }
        catch
        {
            _state = null;
        }
        if (_state == null)
        {
            _state = new ColituInsightState();
            if (fresh)
            {
                // The very first run of this install: its first connection is still ahead. A missing file next
                // to an existing connection state is an update from an older version.
                _state.FirstConnectDone = _existingInstall();
            }
        }
        _state.Counters ??= [];
        _state.Histograms ??= [];
        _state.Features ??= [];
        // Whatever a damaged file holds: only known names with sane numbers survive.
        foreach (var name in _state.Counters.Keys.Where(key => Array.IndexOf(ColituInsightSpec.CounterNames, key) < 0).ToList())
        {
            _state.Counters.Remove(name);
        }
        foreach (var name in _state.Histograms.Keys.Where(key => Array.IndexOf(ColituInsightSpec.HistogramNames, key) < 0 || _state.Histograms[key]?.Length != ColituInsightSpec.BucketCount).ToList())
        {
            _state.Histograms.Remove(name);
        }
        _state.Features.RemoveAll(feature => Array.IndexOf(ColituInsightSpec.FeatureNames, feature) < 0);
        // Whatever a damaged file holds: only valid events survive, at most 1000.
        _state.Events = (_state.Events ?? []).Select(ColituAcSpec.Normalize).Where(item => item != null).Select(item => item!).TakeLast(ColituAcSpec.MaxQueued).ToList();
        if (fresh)
        {
            Save();
        }
        return _state;
    }

    private void Save()
    {
        try
        {
            // Owner-only (0600), written atomically like the other per-user files.
            ColituHardening.WritePrivateText(_path(), JsonSerializer.Serialize(_state));
        }
        catch
        {
            // Not saved: the counters of this run still count, a restart loses them. Nothing worse.
        }
    }
}

/// <summary>Sends the pending report. Never blocks the UI: call it from the thread pool.</summary>
public static class ColituInsightSender
{
    private static int _sending;

    public const string ApiPath = "/client/metrics";

    /// <summary>One send if signed in, switched on, due and there is data. 202 resets; 429, 5xx and network keep the data; 400 drops it.</summary>
    public static async Task TrySendAsync()
    {
        var insights = ColituInsights.Instance;
        var auth = ColituAuthService.Instance;
        if (!auth.HasSession || auth.PendingVerification || !insights.StatsEnabled || !(insights.HasData || insights.QueuedEvents > 0))
        {
            return;
        }
        if (Interlocked.Exchange(ref _sending, 1) == 1)
        {
            return;
        }
        try
        {
            await SendMetricsAsync(insights, auth);
            await SendEventsAsync(insights, auth);
        }
        finally
        {
            Interlocked.Exchange(ref _sending, 0);
        }
    }

    private static async Task SendMetricsAsync(ColituInsights insights, ColituAuthService auth)
    {
        var now = DateTimeOffset.UtcNow;
        // The kill switch only works in TUN mode on Linux: report whether it applies.
        var report = insights.BuildReport(now, ColituAuthService.ClientVersion, ColituVpnService.Instance.KillSwitchApplies);
        if (report == null)
        {
            return;
        }
        try
        {
            await auth.PostAuthorizedAsync(ApiPath, report.Body);
            insights.Commit(report, now);
        }
        catch (ColituApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            // The panel will never take this body: drop it instead of sending it again and again.
            Logging.SaveLog("ColituInsightSender", ex);
            insights.Commit(report, now);
        }
        catch (Exception ex)
        {
            insights.SendFailed(now);
            Logging.SaveLog("ColituInsightSender", ex);
        }
    }

    /// <summary>Events: at most hourly. 202 clears what was sent; 429, 5xx and network keep it; 400 drops it.</summary>
    private static async Task SendEventsAsync(ColituInsights insights, ColituAuthService auth)
    {
        var now = DateTimeOffset.UtcNow;
        var vpn = ColituVpnService.Instance;
        var batch = insights.BuildEventBatch(now, ColituAuthService.ClientVersion, vpn.InsightNetworkToken, vpn.InsightNetworkType);
        if (batch == null)
        {
            return;
        }
        try
        {
            await auth.PostAuthorizedAsync(ColituAcSpec.ApiPath, batch.Body);
            insights.CommitEvents(batch, now);
        }
        catch (ColituApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            Logging.SaveLog("ColituInsightSender.Events", ex);
            insights.CommitEvents(batch, now);
        }
        catch (Exception ex)
        {
            insights.EventSendFailed(now);
            Logging.SaveLog("ColituInsightSender.Events", ex);
        }
    }
}
