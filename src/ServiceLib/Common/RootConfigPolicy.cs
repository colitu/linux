using System.Text.Json;
using System.Text.RegularExpressions;

namespace ServiceLib.Common;

/// <summary>
/// What a config may ask of a core that runs as root (TUN mode). The root side is
/// scripts/colitu-core-root (installed as /opt/colitu-vpn/bin/colitu-core-root): it copies the
/// config out of the user's data folder, lets the core print it in canonical form and checks
/// that with the same rules as this class. The app checks first so a refusal gets a readable
/// error instead of a core that never starts; ColituRootConfigPolicyTests runs both on the same
/// samples (TestData/rootconfig).
/// Rules: no log file; no file or socket path (a value starting with "/", "@" or "~", or with a
/// ".." segment) except URL paths and sing-box process_path rules; sing-box local rule sets only
/// from the install folder; no sing-box tor outbound; Xray "ext:" files only by name; every inbound
/// except the TUN device, and sing-box's control API (with a secret), listen on loopback only; no
/// sing-box v2ray_api.
/// </summary>
public static class RootConfigPolicy
{
    /// <summary>Start of every refusal message, from the app and from colitu-core-root.</summary>
    public const string RefusedMarker = "colitu-core-root: refused";

    private static readonly Regex DotDotSegment = new(@"(^|/)\.\.(/|$)", RegexOptions.CultureInvariant);
    private static readonly Regex DotSegment = new(@"(^|/)\.\.?(/|$)", RegexOptions.CultureInvariant);

    private sealed record Frame(string Key, string Type);

    /// <summary>"sing-box" or "xray" for a core file name; null for a core that never runs as root.</summary>
    public static string? KindOf(string coreFile) => Path.GetFileName(coreFile) switch
    {
        "sing-box" or "sing-box-client" => "sing-box",
        "xray" => "xray",
        _ => null
    };

    /// <summary>True when <paramref name="json"/> may run as root; otherwise the reason.</summary>
    public static bool Check(string json, string kind, string installBin, out string reason)
    {
        if (kind is not ("sing-box" or "xray"))
        {
            reason = "unknown core";
            return false;
        }
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
        }
        catch (JsonException ex)
        {
            reason = $"not plain JSON ({ex.Message})";
            return false;
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                reason = "incomplete config";
                return false;
            }
            reason = Walk(document.RootElement, "[]", [], kind, installBin.TrimEnd('/')) ?? "";
            return reason.Length == 0;
        }
    }

    private static string? Walk(JsonElement container, string key, List<Frame> frames, string kind, string here)
    {
        var type = container.ValueKind == JsonValueKind.Object && container.TryGetProperty("type", out var typeValue) && typeValue.ValueKind == JsonValueKind.String
            ? typeValue.GetString() ?? ""
            : "";
        frames.Add(new Frame(key, type));
        try
        {
            if (container.ValueKind == JsonValueKind.Object)
            {
                if (kind == "sing-box" && key == "v2ray_api")
                {
                    return "v2ray_api";
                }
                foreach (var member in container.EnumerateObject())
                {
                    if (Visit(member.Name, member.Value, frames, kind, here) is { } reason)
                    {
                        return reason;
                    }
                }
                // A root core listens on loopback only: an inbound (other than the TUN device) and
                // sing-box's control API. The control API also needs its secret.
                if (frames.Count == 3 && frames[1].Key == "inbounds" && frames[2].Key == "[]")
                {
                    var protocol = StringMember(container, kind == "xray" ? "protocol" : "type");
                    if (protocol != "tun" && !IsLoopbackListen(StringMember(container, "listen")))
                    {
                        return "inbound not on loopback";
                    }
                }
                if (kind == "sing-box" && key == "clash_api"
                    && (!IsLoopbackListen(StringMember(container, "external_controller")) || string.IsNullOrEmpty(StringMember(container, "secret"))))
                {
                    return "control API not on loopback with a secret";
                }
            }
            else
            {
                foreach (var item in container.EnumerateArray())
                {
                    if (Visit(null, item, frames, kind, here) is { } reason)
                    {
                        return reason;
                    }
                }
            }
            return null;
        }
        finally
        {
            frames.RemoveAt(frames.Count - 1);
        }
    }

    private static string? Visit(string? name, JsonElement value, List<Frame> frames, string kind, string here) => value.ValueKind switch
    {
        JsonValueKind.Object or JsonValueKind.Array => Walk(value, name ?? "[]", frames, kind, here),
        // An array's strings count under the array's key (like "path": ["/etc/hosts"]).
        JsonValueKind.String => CheckValue(name ?? frames[^1].Key, value.GetString() ?? "", frames, kind, here),
        _ => null
    };

    private static string? CheckValue(string key, string value, List<Frame> frames, string kind, string here)
    {
        var current = frames[^1];
        if (kind == "sing-box")
        {
            if (key == "type" && value == "tor")
            {
                return "tor outbound";
            }
            if (key == "output")
            {
                return value.Length > 0 ? "log file" : null;
            }
            if (key is "process_path" or "process_path_regex")
            {
                return null;
            }
            if (key == "path")
            {
                // URL paths: v2ray transports, DNS over HTTPS/HTTP3 servers, HTTP proxy outbounds.
                if (current.Key == "transport" || current.Type is "https" or "h3" or "http")
                {
                    return null;
                }
                // Local rule sets: only the ones shipped in the root-owned install folder.
                if (current.Key == "[]" && frames.Count > 1 && frames[^2].Key == "rule_set" && IsPathLike(value))
                {
                    return IsInside(value, here) ? null : "rule set outside the install folder";
                }
            }
        }
        else
        {
            if (key is "access" or "error" && current.Key == "log" && value is not ("" or "none"))
            {
                return "log file";
            }
            if ((value.StartsWith("ext:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("ext-", StringComparison.OrdinalIgnoreCase))
                && (value.IndexOfAny(['/', '\\']) >= 0 || value.Contains("..", StringComparison.Ordinal)))
            {
                return "ext: file outside the asset folder";
            }
            if (key is "path" or "serviceName")
            {
                return null;
            }
        }
        return IsPathLike(value) ? $"file path in \"{key}\"" : null;
    }

    private static string? StringMember(JsonElement container, string name) =>
        container.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static readonly Regex LoopbackListen = new(@"^(127\.[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}|localhost|::1|\[::1\])(:[0-9]{1,5})?$", RegexOptions.CultureInvariant);

    /// <summary>A loopback listen address, with or without a port ("127.0.0.1", "[::1]:9090"); "::1" only without one.</summary>
    public static bool IsLoopbackListen(string? value) =>
        value != null && LoopbackListen.IsMatch(value) && !(value.StartsWith("::1:", StringComparison.Ordinal));

    /// <summary>A file or socket path: absolute, abstract socket, home, or with a ".." segment.</summary>
    internal static bool IsPathLike(string value) =>
        value.Length > 0 && value[0] is '/' or '@' or '~' || DotDotSegment.IsMatch(value);

    private static bool IsInside(string value, string here) =>
        value.StartsWith(here + "/", StringComparison.Ordinal) && !DotSegment.IsMatch(value);
}
