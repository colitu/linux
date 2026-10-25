using System.Text.Json;
using System.Text.Json.Serialization;

namespace v2rayN.Desktop.Services;

/// <summary>
/// The account's passport as the panel returns it (GET /client/passport, docs/passport-server.md).
/// The server is the only source of truth; the app keeps the last answer as an offline cache.
/// </summary>
public sealed class PassportState
{
    [JsonPropertyName("collector_no")] public string? CollectorNo { get; set; }
    [JsonPropertyName("nickname")] public string? Nickname { get; set; }
    [JsonPropertyName("title_id")] public string? TitleId { get; set; }
    [JsonPropertyName("cover_id")] public string? CoverId { get; set; }
    [JsonPropertyName("tester")] public bool Tester { get; set; }
    [JsonPropertyName("premium")] public bool Premium { get; set; }
    [JsonPropertyName("chapter2_open")] public bool Chapter2Open { get; set; }
    [JsonPropertyName("companion")] public bool Companion { get; set; }
    [JsonPropertyName("today")] public PassportToday? Today { get; set; }
    [JsonPropertyName("run")] public PassportRun? Run { get; set; }
    [JsonPropertyName("stamps")] public List<PassportStampDto> Stamps { get; set; } = [];
    [JsonPropertyName("achievements")] public List<PassportEarned> Achievements { get; set; } = [];
    [JsonPropertyName("collections")] public List<PassportEarned> Collections { get; set; } = [];
    [JsonPropertyName("covers")] public List<string> Covers { get; set; } = [];
    [JsonPropertyName("weekly")] public PassportWeek? Weekly { get; set; }
    [JsonPropertyName("expeditions_done")] public int ExpeditionsDone { get; set; }
    [JsonPropertyName("unseen_events")] public int UnseenEvents { get; set; }
    [JsonPropertyName("updated_at")] public DateTimeOffset? UpdatedAt { get; set; }

    public static readonly PassportState Empty = new();

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The state inside <c>{"data":{…}}</c> (or a bare object); null when it is not one.</summary>
    public static PassportState? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object) root = data;
            return root.Deserialize<PassportState>(Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this);
}

public sealed class PassportToday
{
    [JsonPropertyName("day")] public string? Day { get; set; }
    [JsonPropertyName("minutes")] public int Minutes { get; set; }
    [JsonPropertyName("pieces")] public int Pieces { get; set; }
    [JsonPropertyName("next_piece_at_minute")] public int? NextPieceAtMinute { get; set; }
}

public sealed class PassportRun
{
    [JsonPropertyName("country")] public string? Country { get; set; }
    [JsonPropertyName("minutes")] public int Minutes { get; set; }
}

public sealed class PassportStampDto
{
    [JsonPropertyName("country")] public string? Country { get; set; }
    [JsonPropertyName("unlocked_at")] public DateTimeOffset? UnlockedAt { get; set; }
    [JsonPropertyName("pieces")] public int Pieces { get; set; }
    [JsonPropertyName("level")] public string? Level { get; set; }
    [JsonPropertyName("mastered")] public bool Mastered { get; set; }
}

public sealed class PassportEarned
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("earned_at")] public DateTimeOffset? EarnedAt { get; set; }
}

public sealed class PassportWeek
{
    [JsonPropertyName("week")] public string? Week { get; set; }
    [JsonPropertyName("goals")] public List<PassportWeekGoal> Goals { get; set; } = [];
}

public sealed class PassportWeekGoal
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("target")] public int Target { get; set; }
    [JsonPropertyName("progress")] public int Progress { get; set; }
    [JsonPropertyName("done")] public bool Done { get; set; }
}

/// <summary>An unseen passport event from GET /client/passport/events; <see cref="Event"/> is what the reveal plays.</summary>
public sealed record PassportServerEvent(long Id, StampEvent? Event);

/// <summary>Turns the panel's ids and events into the app's (the panel writes "nordic-master", the app "nordic_master").</summary>
public static class PassportIds
{
    public static string Normalize(string? id) => (id ?? "").Trim().ToLowerInvariant().Replace('-', '_');

    /// <summary>The events of GET /client/passport/events, oldest first; unknown kinds keep their id (to be acked) with no reveal.</summary>
    public static IReadOnlyList<PassportServerEvent> ParseEvents(string? json, Func<string, StampProgress> progressOf)
    {
        var list = new List<PassportServerEvent>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data)) root = data;
            if (root.ValueKind != JsonValueKind.Array) return list;
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id)) continue;
                var kind = item.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
                var payload = item.TryGetProperty("payload", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
                list.Add(new PassportServerEvent(id, ToEvent(kind, payload, progressOf)));
            }
        }
        catch (JsonException)
        {
        }
        return list.OrderBy(e => e.Id).ToList();
    }

    private static string? Text(JsonElement payload, params string[] names)
    {
        if (payload.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (payload.TryGetProperty(name, out var value))
            {
                if (value.ValueKind == JsonValueKind.String) return value.GetString();
                if (value.ValueKind == JsonValueKind.Number) return value.GetRawText();
            }
        }
        return null;
    }

    private static int? Number(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var value) && value.TryGetInt32(out var n) ? n : null;

    /// <summary>One panel event as the reveal the phones play; null for a kind this version does not know.</summary>
    public static StampEvent? ToEvent(string? kind, JsonElement payload, Func<string, StampProgress> progressOf)
    {
        var country = ColituPassport.Normalize(Text(payload, "country"));
        switch (kind)
        {
            case "stamp_unlocked":
                return country == null ? null : new StampEvent.Unlocked(country);
            case "piece":
            {
                if (country == null) return null;
                // Filled pieces of the level the piece went into: from the payload, else from the stamp now.
                var levelPieces = Number(payload, "level_pieces");
                if (levelPieces == null && Number(payload, "pieces") is { } total)
                {
                    levelPieces = total % StampProgress.PiecesPerLevel == 0 && total > 0 ? StampProgress.PiecesPerLevel : total % StampProgress.PiecesPerLevel;
                }
                levelPieces ??= progressOf(country) is { } now ? (now.LevelPieces == 0 && now.Pieces > 0 ? StampProgress.PiecesPerLevel : now.LevelPieces) : 1;
                return new StampEvent.Piece(country, Math.Clamp(levelPieces.Value, 1, StampProgress.PiecesPerLevel));
            }
            case "level_up":
            {
                if (country == null) return null;
                var level = StampLevels.FromKey(Normalize(Text(payload, "level"))) ?? progressOf(country).Level;
                return level == null ? null : new StampEvent.LevelUp(country, level.Value);
            }
            case "mastered":
                return country == null ? null : new StampEvent.Mastered(country);
            case "achievement":
                return Text(payload, "id", "achievement") is { } a ? new StampEvent.Achieved(Normalize(a)) : null;
            case "collection":
                return Text(payload, "id", "collection") is { } c ? new StampEvent.Collected(Normalize(c)) : null;
            case "chapter_opened":
                return Text(payload, "chapter", "id") is { } ch ? new StampEvent.Chapter(ch.Trim().TrimStart('0')) : new StampEvent.Chapter("2");
            case "weekly_done":
                return Text(payload, "goal_id", "goal", "id") is { } g ? new StampEvent.Expedition(Normalize(g)) : null;
            case "companion":
                return new StampEvent.Special(PassportSocial.Companion);
            default:
                return null;
        }
    }
}
