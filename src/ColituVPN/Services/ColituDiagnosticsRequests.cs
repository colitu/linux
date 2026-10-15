using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace v2rayN.Desktop.Services;

/// <summary>A request from a support agent for this device's connection report.</summary>
public sealed class ColituDiagnosticsRequest
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("conversation_id")] public string ConversationId { get; set; } = "";
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("created_at")] public DateTimeOffset? CreatedAt { get; set; }
    [JsonPropertyName("expires_at")] public DateTimeOffset? ExpiresAt { get; set; }
}

public enum ColituDiagnosticsOutcome
{
    /// <summary>The report was uploaded.</summary>
    Sent,
    /// <summary>The user chose "Don't send".</summary>
    Declined,
    /// <summary>The prompt closed without a choice (Esc, sign-out): ask again later.</summary>
    Dismissed,
    /// <summary>The upload failed: ask again later.</summary>
    Failed
}

/// <summary>
/// Pure parts of the connection-report flow: reading the panel's answer, and the timing and
/// decision rules (at most one check per minute, a request already answered in this run is not
/// shown again, a dismissed or failed one comes back after ten minutes).
/// </summary>
public sealed class ColituDiagnosticsAsker
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly Dictionary<string, DateTimeOffset> _askAgainAt = new(StringComparer.Ordinal);
    private readonly HashSet<string> _answered = new(StringComparer.Ordinal);
    private DateTimeOffset _lastCheck = DateTimeOffset.MinValue;

    /// <summary>True (and the check is booked) when the last one was at least a minute ago.</summary>
    public bool TryBeginCheck(DateTimeOffset now)
    {
        if (now - _lastCheck < CheckInterval && now >= _lastCheck)
        {
            return false;
        }
        _lastCheck = now;
        return true;
    }

    /// <summary>Whether the prompt should be shown for this request now.</summary>
    public bool ShouldAsk(ColituDiagnosticsRequest? request, DateTimeOffset now)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.ConversationId)
            || !string.Equals(request.Status, "pending", StringComparison.OrdinalIgnoreCase)
            || (request.ExpiresAt is { } expires && expires <= now)
            || _answered.Contains(request.Id))
        {
            return false;
        }
        return !_askAgainAt.TryGetValue(request.Id, out var again) || now >= again;
    }

    public void Record(string requestId, ColituDiagnosticsOutcome outcome, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(requestId))
        {
            return;
        }
        switch (outcome)
        {
            case ColituDiagnosticsOutcome.Sent:
            case ColituDiagnosticsOutcome.Declined:
                _answered.Add(requestId);
                _askAgainAt.Remove(requestId);
                break;
            default:
                _askAgainAt[requestId] = now + RetryDelay;
                break;
        }
    }

    /// <summary>Forgets everything (sign-out: the next account starts clean).</summary>
    public void Reset()
    {
        _askAgainAt.Clear();
        _answered.Clear();
        _lastCheck = DateTimeOffset.MinValue;
    }

    /// <summary>Reads <c>{"data":{"request":...}}</c>; null for no request or anything unreadable.</summary>
    public static ColituDiagnosticsRequest? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            var envelope = JsonSerializer.Deserialize<RequestEnvelope>(json, JsonOptions);
            var request = envelope?.Data?.Request;
            return request == null || string.IsNullOrWhiteSpace(request.Id) ? null : request;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The JSON body of the upload; <paramref name="requestId"/> only when answering a request.</summary>
    public static string BuildUploadBody(string? requestId, string report, string platform, string? appVersion, string? osVersion)
    {
        var body = new Dictionary<string, string?>();
        if (!string.IsNullOrWhiteSpace(requestId))
        {
            body["request_id"] = requestId;
        }
        body["report"] = report;
        body["platform"] = platform;
        body["app_version"] = appVersion;
        body["os_version"] = osVersion;
        return JsonSerializer.Serialize(body);
    }

    /// <summary>
    /// The plain-text report: one "===== Section =====" block per topic. Empty values are left out;
    /// <paramref name="appLog"/> is expected to be redacted already.
    /// </summary>
    public static string BuildReportText(ColituSupportDiagnostics diagnostics, string? appLog, DateTimeOffset generatedAt)
    {
        var sb = new StringBuilder();
        Section(sb, "Report", $"Generated: {generatedAt:u}");
        Section(sb, "Device",
            Line("Device", diagnostics.Device), Line("OS", diagnostics.Os), Line("Platform", diagnostics.Platform));
        Section(sb, "App", Line("Version", diagnostics.AppVersion));
        Section(sb, "Connection",
            Line("Connected", diagnostics.Connected is { } connected ? (connected ? "yes" : "no") : null),
            Line("Mode", diagnostics.Network), Line("Server", diagnostics.Server), Line("Protocol", diagnostics.Protocol));
        var errors = diagnostics.LastErrors is { Count: > 0 } list ? string.Join('\n', list) : "(none)";
        Section(sb, "Last errors", errors);
        Section(sb, "App log (latest lines)", string.IsNullOrWhiteSpace(appLog) ? "(no log)" : appLog!.TrimEnd());
        return sb.ToString();
    }

    private static string? Line(string name, string? value) => string.IsNullOrWhiteSpace(value) ? null : $"{name}: {value}";

    private static void Section(StringBuilder sb, string title, params string?[] lines)
    {
        if (sb.Length > 0)
        {
            sb.Append('\n');
        }
        sb.Append("===== ").Append(title).Append(" =====\n");
        foreach (var line in lines)
        {
            if (!string.IsNullOrEmpty(line))
            {
                sb.Append(line).Append('\n');
            }
        }
    }

    private sealed class RequestEnvelope
    {
        [JsonPropertyName("data")] public RequestData? Data { get; set; }
    }

    private sealed class RequestData
    {
        [JsonPropertyName("request")] public ColituDiagnosticsRequest? Request { get; set; }
    }
}
