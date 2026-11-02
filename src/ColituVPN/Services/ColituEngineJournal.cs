using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace v2rayN.Desktop.Services;

/// <summary>One journal record: when, what kind, and its data (a JSON snapshot taken when it was added).</summary>
public sealed class ColituEngineJournalEntry
{
    [JsonPropertyName("at")] public DateTimeOffset At { get; init; }
    /// <summary>decision, outcome, ping_round, hidden or whitelist.</summary>
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("data")] public JsonNode? Data { get; init; }
}

/// <summary>
/// The Adaptive Connect engine journal for the support report (AC4 phase 4): every automatic
/// decision (candidates, features, scores, choice, reason), connection outcomes, ping round
/// summaries, servers hidden by the honest list and whitelist checks. A ring of the last
/// <see cref="Capacity"/> records, in memory only; it never leaves the device by itself.
/// </summary>
public sealed class ColituEngineJournal
{
    public const int Capacity = 300;

    public static ColituEngineJournal Instance { get; } = new();

    public const string Decision = "decision";
    public const string Outcome = "outcome";
    public const string PingRound = "ping_round";
    public const string Hidden = "hidden";
    public const string Whitelist = "whitelist";

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _gate = new();
    private readonly ColituEngineJournalEntry?[] _ring = new ColituEngineJournalEntry?[Capacity];
    private int _next;
    private int _count;

    public void Add(string kind, object? data, DateTimeOffset? at = null)
    {
        JsonNode? node;
        try
        {
            node = data == null ? null : JsonSerializer.SerializeToNode(data, data.GetType(), Options);
        }
        catch (Exception ex)
        {
            node = new JsonObject { ["error"] = ex.GetType().Name };
        }
        var entry = new ColituEngineJournalEntry { At = at ?? DateTimeOffset.UtcNow, Kind = kind, Data = node };
        lock (_gate)
        {
            _ring[_next] = entry;
            _next = (_next + 1) % Capacity;
            _count = Math.Min(_count + 1, Capacity);
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>The records, oldest first.</summary>
    public List<ColituEngineJournalEntry> Snapshot()
    {
        lock (_gate)
        {
            var result = new List<ColituEngineJournalEntry>(_count);
            var start = (_next - _count + Capacity) % Capacity;
            for (var i = 0; i < _count; i++)
            {
                result.Add(_ring[(start + i) % Capacity]!);
            }
            return result;
        }
    }

    /// <summary>The journal as a JSON array (oldest first), for the support report.</summary>
    public string ToJson(bool indented = false) =>
        JsonSerializer.Serialize(Snapshot(), new JsonSerializerOptions(Options) { WriteIndented = indented });

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_ring);
            _next = 0;
            _count = 0;
        }
    }
}
