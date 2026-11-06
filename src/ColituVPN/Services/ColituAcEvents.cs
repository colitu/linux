using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json.Serialization;

namespace v2rayN.Desktop.Services;

/// <summary>
/// One Adaptive Connect / connection event for POST /client/ac-events. Only the fields of its kind are set;
/// the rest stay null and are not written. Never carries an account, a device, an IP or a visited site.
/// </summary>
public sealed class ColituAcEvent
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("node_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? NodeId { get; set; }
    [JsonPropertyName("protocol"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Protocol { get; set; }
    [JsonPropertyName("to_protocol"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ToProtocol { get; set; }
    [JsonPropertyName("outcome"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Outcome { get; set; }
    [JsonPropertyName("reason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Reason { get; set; }
    [JsonPropertyName("ms"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Ms { get; set; }
    [JsonPropertyName("detect_ms"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? DetectMs { get; set; }
    [JsonPropertyName("first_try"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? FirstTry { get; set; }
    [JsonPropertyName("mode"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Mode { get; set; }
    /// <summary>connect, automatic only: what ordered the servers of this connect ("ac4", "ac3" or "panel"). Absent for a manual connect.</summary>
    [JsonPropertyName("engine"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Engine { get; set; }
    [JsonPropertyName("spare"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Spare { get; set; }
    [JsonPropertyName("flap"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Flap { get; set; }
    [JsonPropertyName("minutes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Minutes { get; set; }
    [JsonPropertyName("best_node_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? BestNodeId { get; set; }
    [JsonPropertyName("best_ms"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? BestMs { get; set; }
    /// <summary>whitelist: "on" (whitelist mode entered) or "off" (left).</summary>
    [JsonPropertyName("state"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? State { get; set; }
    [JsonPropertyName("foreign_ok"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? ForeignOk { get; set; }
    [JsonPropertyName("foreign_total"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? ForeignTotal { get; set; }
    [JsonPropertyName("domestic_ok"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? DomesticOk { get; set; }
    [JsonPropertyName("domestic_total"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? DomesticTotal { get; set; }

    /// <summary>When the event was queued (not sent): decides which probe of an hour is the last one.</summary>
    [JsonIgnore] public DateTimeOffset Stamp { get; set; }
}

/// <summary>The accepted names and limits of the events, their normalizing and the failure-to-reason mapping.</summary>
public static class ColituAcSpec
{
    public const string ApiPath = "/client/ac-events";
    public const int MaxQueued = 1000;
    public const int MaxPerRequest = 300;
    public const int MaxMs = 600_000;
    public static readonly TimeSpan MinSendInterval = TimeSpan.FromHours(1);

    public static readonly string[] Kinds = ["connect", "recovery", "switch", "drop", "probe", "choice", "whitelist"];
    public static readonly string[] Protocols = ["vless-reality", "vless-xhttp", "hysteria2", "trojan", "shadowsocks", "tuic"];
    public static readonly string[] Reasons =
        ["timeout", "reset", "tls", "dns", "refused", "handshake", "blocked", "no_network", "core_crash", "auth", "quota", "network_change", "resume", "other"];
    /// <summary>Who ordered the servers of an automatic connect: the on-device AC 4 model, the app's own AC 3 rules, or the panel (no list yet).</summary>
    public static readonly string[] Engines = ["ac4", "ac3", "panel"];
    public static readonly string[] NetworkTypes = ["wifi", "ethernet", "cellular", "other"];

    /// <summary>A node id as the panel writes it: a lower-case UUID. Null for anything else (a multihop route id is not a node).</summary>
    public static string? NodeId(string? id) =>
        Guid.TryParse(id?.Trim(), out var guid) ? guid.ToString("D") : null;

    public static string? Protocol(string? protocol)
    {
        var value = protocol?.Trim().ToLowerInvariant();
        return value != null && Array.IndexOf(Protocols, value) >= 0 ? value : null;
    }

    /// <summary>"wifi", "ethernet", "cellular" or "other"; empty when unknown ("none" included).</summary>
    public static string NetworkType(string? link)
    {
        var value = link?.Trim().ToLowerInvariant() ?? "";
        return Array.IndexOf(NetworkTypes, value) >= 0 ? value : "";
    }

    /// <summary>
    /// The engine tag of one round of an automatic connect: "panel" when the panel chose the server (no ranked list, or every
    /// ranked server already failed), else "ac4" when the on-device model ordered the list, else "ac3".
    /// </summary>
    public static string EngineOfRound(int rankedCount, bool panelChose, bool ac4Order) =>
        rankedCount == 0 || panelChose ? "panel" : ac4Order ? "ac4" : "ac3";

    public static int? ClampMs(long? ms) => ms is { } value ? (int)Math.Clamp(value, 0, MaxMs) : null;

    /// <summary>
    /// Validates an event and cuts it to its kind's fields; null when a required field is missing or unknown.
    /// Reasons default to "other", modes to "manual".
    /// </summary>
    public static ColituAcEvent? Normalize(ColituAcEvent? source)
    {
        if (source == null)
        {
            return null;
        }
        var kind = source.Kind?.Trim().ToLowerInvariant() ?? "";
        var outcome = source.Outcome?.Trim().ToLowerInvariant();
        if (outcome is not (null or "ok" or "fail"))
        {
            return null;
        }
        var node = NodeId(source.NodeId);
        var result = new ColituAcEvent { Kind = kind, Stamp = source.Stamp };
        switch (kind)
        {
            case "connect":
                if (node == null || outcome == null)
                {
                    return null;
                }
                result.NodeId = node;
                result.Protocol = Protocol(source.Protocol);
                result.Outcome = outcome;
                result.Reason = outcome == "fail" ? ReasonName(source.Reason) : null;
                result.Ms = ClampMs(source.Ms);
                result.FirstTry = source.FirstTry ?? false;
                result.Mode = ModeName(source.Mode);
                result.Engine = result.Mode == "auto" ? EngineName(source.Engine) : null;
                return result;
            case "recovery":
                if (node == null || outcome == null)
                {
                    return null;
                }
                result.NodeId = node;
                result.Protocol = Protocol(source.Protocol);
                result.ToProtocol = Protocol(source.ToProtocol);
                result.Outcome = outcome;
                result.Reason = ReasonName(source.Reason);
                result.Ms = ClampMs(source.Ms);
                result.DetectMs = ClampMs(source.DetectMs);
                result.Spare = source.Spare ?? false;
                return result;
            case "switch":
                if (Protocol(source.Protocol) == null || Protocol(source.ToProtocol) == null || outcome == null)
                {
                    return null;
                }
                result.Protocol = Protocol(source.Protocol);
                result.ToProtocol = Protocol(source.ToProtocol);
                result.Outcome = outcome;
                result.Flap = source.Flap ?? false;
                return result;
            case "drop":
                if (node == null)
                {
                    return null;
                }
                result.NodeId = node;
                result.Protocol = Protocol(source.Protocol);
                result.Reason = ReasonName(source.Reason);
                result.Minutes = (int)Math.Clamp(source.Minutes ?? 0, 0, 1_000_000);
                return result;
            case "probe":
                if (node == null || outcome == null)
                {
                    return null;
                }
                result.NodeId = node;
                result.Protocol = Protocol(source.Protocol);
                result.Outcome = outcome;
                result.Ms = outcome == "ok" ? ClampMs(source.Ms) : null;
                return result;
            case "choice":
                var best = NodeId(source.BestNodeId);
                if (node == null || best == null || source.Ms == null || source.BestMs == null)
                {
                    return null;
                }
                result.NodeId = node;
                result.Ms = ClampMs(source.Ms);
                result.BestNodeId = best;
                // The best is the lowest of the candidates, the chosen one included.
                result.BestMs = Math.Min(ClampMs(source.BestMs)!.Value, result.Ms!.Value);
                result.Mode = "auto";
                return result;
            case "whitelist":
                // No node: stored per country + ASN only. Totals 1..10, counts 0..total.
                var state = source.State?.Trim().ToLowerInvariant();
                if (state is not ("on" or "off") || source.ForeignTotal is not (>= 1 and <= 10) || source.DomesticTotal is not (>= 1 and <= 10)
                    || source.ForeignOk is not { } foreignOk || source.DomesticOk is not { } domesticOk)
                {
                    return null;
                }
                result.State = state;
                result.ForeignTotal = source.ForeignTotal;
                result.DomesticTotal = source.DomesticTotal;
                result.ForeignOk = Math.Clamp(foreignOk, 0, source.ForeignTotal.Value);
                result.DomesticOk = Math.Clamp(domesticOk, 0, source.DomesticTotal.Value);
                result.Ms = ClampMs(source.Ms);
                return result;
            default:
                return null;
        }
    }

    /// <summary>
    /// A drop or recovery caused by the computer sleeping: its own reason, out of the panel's drop total.
    /// </summary>
    public const string ResumeReason = "resume";

    private static string ReasonName(string? reason)
    {
        var value = reason?.Trim().ToLowerInvariant();
        return value != null && Array.IndexOf(Reasons, value) >= 0 ? value : "other";
    }

    private static string? EngineName(string? engine)
    {
        var value = engine?.Trim().ToLowerInvariant();
        return value != null && Array.IndexOf(Engines, value) >= 0 ? value : null;
    }

    private static string ModeName(string? mode) =>
        string.Equals(mode?.Trim(), "auto", StringComparison.OrdinalIgnoreCase) ? "auto" : "manual";

    // ── Failure → reason ───────────────────────────────────────────────────
    // The app's failure information is mostly English log text (probe details, watcher reasons) and
    // exceptions. Mapping, first match wins:
    //   core_crash      the VPN core / TUN front stopped or exited
    //   network_change  the network or adapter changed
    //   no_network      no internet outside the tunnel, no adapter, network/host unreachable
    //   tls             certificate, TLS/SSL errors (AuthenticationException)
    //   handshake       protocol handshake failures
    //   refused         connection refused (SocketError.ConnectionRefused, 403 from the node)
    //   reset           reset, aborted, broken pipe, forcibly closed
    //   dns             name not resolved (SocketError.HostNotFound/TryAgain)
    //   blocked         region / access blocks (REGION_NOT_SUPPORTED, "blocked", "censor")
    //   auth            credentials refused, 401, session/device revoked
    //   quota           QUOTA_EXCEEDED
    //   timeout         timeouts, and "the tunnel carried no traffic" (nothing else is known)
    //   other           everything else
    public static string ReasonOfText(string? text)
    {
        var t = text?.ToLowerInvariant() ?? "";
        if (t.Length == 0)
        {
            return "other";
        }
        if (Has(t, "core stopped", "core exited", "core crash", "pre-core", "stopped unexpectedly"))
        {
            return "core_crash";
        }
        if (Has(t, "network changed", "network change", "adapter changed"))
        {
            return "network_change";
        }
        if (Has(t, "no internet", "no network", "network is unreachable", "network unreachable", "host unreachable", "offline"))
        {
            return "no_network";
        }
        if (Has(t, "certificate", "tls", "ssl"))
        {
            return "tls";
        }
        if (Has(t, "handshake"))
        {
            return "handshake";
        }
        if (Has(t, "refused", "403"))
        {
            return "refused";
        }
        if (Has(t, "reset", "broken pipe", "forcibly closed", "aborted"))
        {
            return "reset";
        }
        if (Has(t, "no such host", "name resolution", "could not be resolved", "dns"))
        {
            return "dns";
        }
        if (Has(t, "blocked", "censor", "region_not_supported"))
        {
            return "blocked";
        }
        if (Has(t, "unauthorized", "credential", "401", "revoked", "auth"))
        {
            return "auth";
        }
        if (Has(t, "quota"))
        {
            return "quota";
        }
        if (Has(t, "timed out", "timeout", "no traffic", "carried nothing", "does not answer", "did not answer", "stalled"))
        {
            return "timeout";
        }
        return "other";
    }

    /// <summary>The reason of an exception: its type and code first, then the text of the whole chain.</summary>
    public static string ReasonOf(Exception? exception)
    {
        for (var ex = exception; ex != null; ex = ex.InnerException)
        {
            var reason = ex switch
            {
                ColituConnectException { Offline: true } => "no_network",
                ColituApiException api => ReasonOfApi(api),
                SocketException socket => ReasonOfSocket(socket.SocketErrorCode),
                AuthenticationException => "tls",
                TimeoutException => "timeout",
                OperationCanceledException => "timeout",
                _ => "other"
            };
            if (reason != "other")
            {
                return reason;
            }
        }
        for (var ex = exception; ex != null; ex = ex.InnerException)
        {
            var reason = ReasonOfText(ex.Message);
            if (reason != "other")
            {
                return reason;
            }
        }
        return "other";
    }

    private static string ReasonOfApi(ColituApiException api) => api.ErrorCode switch
    {
        "QUOTA_EXCEEDED" => "quota",
        "REGION_NOT_SUPPORTED" => "blocked",
        "AUTH_INVALID_CREDENTIALS" or "AUTH_REFRESH_REUSED" or "AUTH_TOKEN_EXPIRED" or "DEVICE_REVOKED" or "DEVICE_TOKEN_MISMATCH"
            or "SESSION_EXPIRED" or "SIGNED_OUT" or "EMAIL_NOT_VERIFIED" => "auth",
        _ => "other"
    };

    private static string ReasonOfSocket(SocketError error) => error switch
    {
        SocketError.ConnectionRefused => "refused",
        SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.Shutdown => "reset",
        SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData => "dns",
        SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown => "no_network",
        SocketError.TimedOut => "timeout",
        _ => "other"
    };

    private static bool Has(string text, params string[] needles) => needles.Any(text.Contains);
}
