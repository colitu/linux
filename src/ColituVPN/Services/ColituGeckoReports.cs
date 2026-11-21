using System.Net;
using System.Text.Json.Nodes;

namespace v2rayN.Desktop.Services;

/// <summary>One grip failure as the panel takes it (docs: GECKO-G4-CONTRACTS.md §3).</summary>
internal sealed record ColituGeckoReport(string Entry, string Skin, string Rev, string Class, string? Bucket, long At,
    string? Rtt = null, string? Rate = null, string? Life = null, string? App = null);

/// <summary>
/// The slice labels of the contract (§3): the value is cut into a bucket on the device and only the label
/// leaves it. Each bucket holds its lower bound and excludes its upper one ("50-100" is 50 up to, not
/// including, 100); a value that is negative or not a number has no label.
/// </summary>
internal static class ColituGeckoBuckets
{
    public static readonly string[] RttLabels = ["<50", "50-100", "100-200", "200-400", ">400"];
    public static readonly string[] RateLabels = ["<1", "1-5", "5-20", "20-100", ">100"];
    public static readonly string[] LifeLabels = ["<10s", "10-60s", "1-10m", ">10m"];

    /// <summary>Round-trip time in milliseconds.</summary>
    public static string? Rtt(double ms) => Pick(ms, [50, 100, 200, 400], RttLabels);

    /// <summary>Rate in Mbit/s.</summary>
    public static string? Rate(double mbit) => Pick(mbit, [1, 5, 20, 100], RateLabels);

    /// <summary>A grip's lifetime.</summary>
    public static string? Life(TimeSpan life) => Pick(life.TotalSeconds, [10, 60, 600], LifeLabels);

    /// <summary>Mbit/s of <paramref name="bytes"/> carried in <paramref name="duration"/>; null when either is unusable.</summary>
    public static double? MbitPerSecond(long bytes, TimeSpan duration) =>
        bytes < 0 || duration <= TimeSpan.Zero ? null : bytes * 8d / 1_000_000d / duration.TotalSeconds;

    /// <summary>The app version as the contract takes it: 1 to 32 characters of 0-9 A-Z a-z . + -.</summary>
    public static string? App(string? version)
    {
        if (string.IsNullOrEmpty(version) || version.Length > 32)
        {
            return null;
        }
        foreach (var c in version)
        {
            if (!(c is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '.' or '+' or '-'))
            {
                return null;
            }
        }
        return version;
    }

    private static string? Pick(double value, double[] upperBounds, string[] labels)
    {
        if (double.IsNaN(value) || value < 0)
        {
            return null;
        }
        for (var i = 0; i < upperBounds.Length; i++)
        {
            if (value < upperBounds[i])
            {
                return labels[i];
            }
        }
        return labels[^1];
    }
}

/// <summary>
/// The grip failures gecko-client reports, kept in memory and sent to the panel in batches
/// (<c>POST /gecko/reports</c>): at most every 60 s, at most 100 per call, at most 300 waiting
/// (the oldest go first when more arrive). Nothing is written to disk or to the log: the entry id
/// names a server of the fleet. A 404 (Gecko not offered) stops sending until the next offer, a
/// 429 pauses for ten minutes, any other failure keeps the batch for the next round.
/// </summary>
internal sealed class ColituGeckoReports
{
    public const int MaxWaiting = 300;
    public const int MaxPerCall = 100;
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PauseAfterTooMany = TimeSpan.FromMinutes(10);
    /// <summary>At most one "ok" report per entry in this time, so working grips do not flood the queue.</summary>
    public static readonly TimeSpan OkInterval = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly LinkedList<ColituGeckoReport> _waiting = new();
    private DateTimeOffset _notBefore = DateTimeOffset.MinValue;
    private readonly Dictionary<string, DateTimeOffset> _lastOk = new(StringComparer.Ordinal);
    private bool _off;

    /// <summary>The app version sent with every report (null: left out).</summary>
    public string? App { get; set; } = ColituGeckoBuckets.App(ColituAuthService.ClientVersion);

    public static ColituGeckoReports Instance { get; } = new();

    public int Waiting
    {
        get { lock (_gate) return _waiting.Count; }
    }

    /// <summary>Takes a parsed <c>grip</c> event; anything else, or one without the fields, is ignored.</summary>
    public bool Add(ColituGeckoEvent evt, DateTimeOffset now)
    {
        if (evt.Kind != "grip" || evt.Entry is not { } entry || evt.Skin is not { } skin || evt.Rev is not { } rev
            || evt.Class is not { } cls || evt.Bucket is not { } bucket)
        {
            return false;
        }
        lock (_gate)
        {
            _waiting.AddLast(new ColituGeckoReport(entry, skin, rev, cls, bucket, now.ToUnixTimeSeconds(), App: App));
            while (_waiting.Count > MaxWaiting)
            {
                _waiting.RemoveFirst();
            }
        }
        return true;
    }

    /// <summary>
    /// Takes a working grip ("ok"); false when this entry already had one in the last
    /// <see cref="OkInterval"/>. The numbers are cut into labels here and dropped; a value that is
    /// not known is left out of the report.
    /// </summary>
    public bool AddOk(string entry, string skin, string rev, DateTimeOffset now, double? rttMs = null, double? rateMbit = null,
        TimeSpan? life = null, string? bucket = null)
    {
        lock (_gate)
        {
            if (_lastOk.TryGetValue(entry, out var last) && now >= last && now - last < OkInterval)
            {
                return false;
            }
            _lastOk[entry] = now;
            _waiting.AddLast(new ColituGeckoReport(entry, skin, rev, "ok", bucket, now.ToUnixTimeSeconds(),
                rttMs is { } r ? ColituGeckoBuckets.Rtt(r) : null,
                rateMbit is { } m ? ColituGeckoBuckets.Rate(m) : null,
                life is { } l ? ColituGeckoBuckets.Life(l) : null, App));
            while (_waiting.Count > MaxWaiting)
            {
                _waiting.RemoveFirst();
            }
        }
        return true;
    }

    /// <summary>
    /// Takes a parsed <c>grip_end</c> event. Only class "ok" counts (a failure was already reported by its
    /// <c>grip</c> event); the exact figures are cut into labels here: rtt when measured (&gt; 0), rate from
    /// the larger of received and sent bytes over the lifetime (at least 1 s), life from the lifetime.
    /// </summary>
    public bool AddEnd(ColituGeckoEvent evt, DateTimeOffset now)
    {
        if (evt.Kind != "grip_end" || evt.Class != "ok" || evt.Entry is not { } entry || evt.Skin is not { } skin || evt.Rev is not { } rev)
        {
            return false;
        }
        double? rtt = evt.RttMs is > 0 ? evt.RttMs : null;
        // The busier direction over the lifetime. "goodput" is the anchor's reading of what we sent, so it says nothing
        // about a download and is not used.
        double? rate = null;
        if (evt.LifeMs is >= 1000 && Math.Max(evt.Received ?? 0, evt.Sent ?? 0) is > 0 and var bytes)
        {
            rate = bytes * 8d / (evt.LifeMs.Value / 1000d) / 1_000_000d;
        }
        TimeSpan? life = evt.LifeMs is >= 0 ? TimeSpan.FromMilliseconds(evt.LifeMs.Value) : null;
        return AddOk(entry, skin, rev, now, rtt, rate, life);
    }

    /// <summary>Sending may resume: the panel offered Gecko again.</summary>
    public void Resume()
    {
        lock (_gate)
        {
            _off = false;
        }
    }

    /// <summary>
    /// Sends what is waiting every <see cref="Interval"/> while <paramref name="running"/> holds, and
    /// once more when it stops (a session that ends on a failure is when reports matter most).
    /// </summary>
    public async Task PumpAsync(Func<bool> running, Func<string, Task<HttpStatusCode>> post, TimeSpan? tick = null)
    {
        var step = tick ?? TimeSpan.FromSeconds(5);
        try
        {
            while (running())
            {
                await Task.Delay(step);
                await FlushAsync(post, DateTimeOffset.UtcNow);
            }
            lock (_gate)
            {
                _notBefore = DateTimeOffset.MinValue;
            }
            await FlushAsync(post, DateTimeOffset.UtcNow);
        }
        catch (Exception)
        {
            // Reports are best effort; they never take the connection with them.
        }
    }

    /// <summary>The request body for a batch.</summary>
    public static string Body(IEnumerable<ColituGeckoReport> batch)
    {
        var reports = new JsonArray();
        foreach (var r in batch)
        {
            var item = new JsonObject
            {
                ["entry"] = r.Entry,
                ["skin"] = r.Skin,
                ["rev"] = long.TryParse(r.Rev, out var rev) ? JsonValue.Create(rev) : JsonValue.Create(r.Rev),
                ["class"] = r.Class
            };
            if (r.Bucket != null) item["bucket"] = r.Bucket;
            item["at"] = r.At;
            if (r.Rtt != null) item["rtt"] = r.Rtt;
            if (r.Rate != null) item["rate"] = r.Rate;
            if (r.Life != null) item["life"] = r.Life;
            if (r.App != null) item["app"] = r.App;
            reports.Add(item);
        }
        return new JsonObject { ["v"] = 1, ["reports"] = reports }.ToJsonString();
    }

    /// <summary>
    /// Sends one batch if one is due. <paramref name="post"/> sends a body and returns the HTTP
    /// status (or throws on a network failure). Returns the number of reports the panel took.
    /// </summary>
    public async Task<int> FlushAsync(Func<string, Task<HttpStatusCode>> post, DateTimeOffset now)
    {
        List<ColituGeckoReport> batch;
        lock (_gate)
        {
            if (_off || now < _notBefore || _waiting.Count == 0)
            {
                return 0;
            }
            batch = _waiting.Take(MaxPerCall).ToList();
            for (var i = 0; i < batch.Count; i++)
            {
                _waiting.RemoveFirst();
            }
            _notBefore = now + Interval;
        }
        HttpStatusCode status;
        try
        {
            status = await post(Body(batch));
        }
        catch (Exception)
        {
            status = 0;
        }
        lock (_gate)
        {
            switch (status)
            {
                case HttpStatusCode.NoContent or HttpStatusCode.OK:
                    return batch.Count;
                case HttpStatusCode.NotFound:
                    _off = true;
                    _waiting.Clear();
                    return 0;
                case HttpStatusCode.BadRequest:
                    // The panel will not take this batch however often it comes.
                    return 0;
                case HttpStatusCode.TooManyRequests:
                    _notBefore = now + PauseAfterTooMany;
                    break;
            }
            // Back to the front, in order, within the cap.
            for (var i = batch.Count - 1; i >= 0; i--)
            {
                _waiting.AddFirst(batch[i]);
            }
            while (_waiting.Count > MaxWaiting)
            {
                _waiting.RemoveFirst();
            }
            return 0;
        }
    }
}
