using System.Text.Json;

namespace v2rayN.Desktop.Services;

/// <summary>
/// Colitu's ad-blocking DNS-over-HTTPS servers, tried in order. They are Colitu's own nodes, so they
/// are not in the public source. The panel names them in the signed-in bootstrap
/// (<c>/client/bootstrap</c>, <c>ad_block.doh</c>) and the app keeps the last non-empty answer, so
/// they can change without a release. An empty list or no field (a panel that has no list
/// configured yet) never turns ad blocking off: the kept list, else the one embedded by
/// the package build, is used. No list at all: the switch is hidden.
/// </summary>
public static class ColituAdBlock
{
    private const int MaxServers = 8;
    private static readonly object Gate = new();
    private static string[]? _stored;
    private static bool _loaded;

    /// <summary>The list the package build embedded (assembly metadata "ColituAdBlockDoh"): the fallback.</summary>
    internal static readonly string[] Embedded = Parse(
        typeof(ColituAdBlock).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "ColituAdBlockDoh")?.Value);

    /// <summary>The panel's last non-empty list, else the embedded list.</summary>
    public static string[] Servers
    {
        get
        {
            lock (Gate)
            {
                if (!_loaded)
                {
                    _stored = Load(StorePath());
                    _loaded = true;
                }
                return _stored ?? Embedded;
            }
        }
    }

    public static bool Available => Servers.Length > 0;

    /// <summary>
    /// Keeps <c>ad_block.doh</c> of a bootstrap answer when it names servers. An empty list or an
    /// answer without the field leaves the kept list as it is. True when the list changed.
    /// </summary>
    public static bool Remember(JsonElement bootstrap, string? path = null)
    {
        if (ServersOf(bootstrap) is not { Length: > 0 } servers)
        {
            return false;
        }
        lock (Gate)
        {
            var before = _loaded ? _stored : Load(path ?? StorePath());
            _loaded = true;
            if (before != null && before.SequenceEqual(servers, StringComparer.Ordinal))
            {
                _stored = before;
                return false;
            }
            _stored = servers;
            try
            {
                ColituHardening.WritePrivateText(path ?? StorePath(), JsonSerializer.Serialize(servers));
            }
            catch (Exception ex)
            {
                Logging.SaveLog($"ColituAdBlock.Remember: {ex.GetType().Name}");
            }
            return true;
        }
    }

    /// <summary><c>ad_block.doh</c> of a bootstrap answer (https URLs only); null when the answer has no such field.</summary>
    internal static string[]? ServersOf(JsonElement bootstrap)
    {
        if (bootstrap.ValueKind != JsonValueKind.Object
            || !bootstrap.TryGetProperty("ad_block", out var adBlock) || adBlock.ValueKind != JsonValueKind.Object
            || !adBlock.TryGetProperty("doh", out var doh) || doh.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        return Parse(string.Join(",", doh.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString())));
    }

    /// <summary>Comma-separated https URLs: absolute, a host, no user info; at most <see cref="MaxServers"/>.</summary>
    internal static string[] Parse(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(url => Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0 && string.IsNullOrEmpty(uri.UserInfo))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxServers)
            .ToArray();

    /// <summary>Tests: forget what was loaded so the next read comes from <paramref name="path"/>' answer.</summary>
    internal static void ResetForTest(string[]? stored)
    {
        lock (Gate)
        {
            _stored = stored;
            _loaded = true;
        }
    }

    private static string[]? Load(string path)
    {
        try
        {
            var kept = File.Exists(path) ? Parse(string.Join(",", JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? [])) : null;
            return kept is { Length: > 0 } ? kept : null;
        }
        catch
        {
            return null;
        }
    }

    // The user's private config folder (owner-only file).
    private static string StorePath() => ColituHardening.UserConfigPath("colitu-adblock-doh.json");
}
