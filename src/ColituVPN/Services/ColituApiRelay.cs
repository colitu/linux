using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace v2rayN.Desktop.Services;

/// <summary>
/// An API relay of the signed endpoint list (key <c>relays</c>): TLS to <see cref="Host"/>:<see cref="Port"/>
/// with the server name <see cref="Sni"/>, the certificate checked for <see cref="Verify"/> (always
/// api.colitu.com), HTTP <c>Host: api.colitu.com</c>, API base https://api.colitu.com + <see cref="Prefix"/>.
/// </summary>
public sealed record ColituApiRelay(string Id, string Host, int Port, string Sni, string Verify, string Prefix, string? Region, bool Whitelist)
{
    /// <summary>The relay's entry in a base list (not a URL).</summary>
    public string Base => ColituApiRelays.BasePrefix + Id;
}

/// <summary>What the base order depends on: the client's country and whitelist mode.</summary>
public sealed record ColituRouteContext(string? Country, bool Whitelist)
{
    public static readonly ColituRouteContext None = new(null, false);
}

/// <summary>
/// Parsing, ordering and the HTTP path of API relays, and the self-heal timer.
/// A relay request is sent as http://api.colitu.com/... through a handler whose connect step dials the
/// relay and does the TLS itself (server name = SNI, certificate valid for api.colitu.com, the API pins),
/// so the handler writes plain HTTP/1.1 with <c>Host: api.colitu.com</c> into that TLS stream. .NET takes
/// the SNI from the Host header otherwise, which is why the TLS is done here.
/// </summary>
public static class ColituApiRelays
{
    public const string BasePrefix = "relay:";
    public const string VerifyName = "api.colitu.com";
    public const string ApiHost = "api.colitu.com";
    public static readonly TimeSpan HealInterval = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant);
    private static readonly Regex LabelPattern = new("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant);
    private static readonly Regex Ipv4Pattern = new(@"^(25[0-5]|2[0-4][0-9]|1[0-9]{2}|[1-9]?[0-9])(\.(25[0-5]|2[0-4][0-9]|1[0-9]{2}|[1-9]?[0-9])){3}$", RegexOptions.CultureInvariant);

    public static bool IsRelay(string? baseUrl) => baseUrl != null && baseUrl.StartsWith(BasePrefix, StringComparison.Ordinal);

    // ---- parsing ---------------------------------------------------------------------------

    /// <summary>The valid entries of a <c>relays</c> array; a bad entry is skipped, never the whole list. Ids are unique (first wins).</summary>
    public static IReadOnlyList<ColituApiRelay> ParseArray(JsonElement array)
    {
        var result = new List<ColituApiRelay>();
        if (array.ValueKind != JsonValueKind.Array)
        {
            return result;
        }
        foreach (var item in array.EnumerateArray())
        {
            if (Parse(item) is { } relay && !result.Any(r => r.Id == relay.Id))
            {
                result.Add(relay);
            }
        }
        return result;
    }

    /// <summary>One relay, or null when a field is out of shape. A <c>verify</c> other than api.colitu.com is refused.</summary>
    public static ColituApiRelay? Parse(JsonElement o)
    {
        if (o.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var id = Str(o, "id");
        var addr = Str(o, "addr");
        var sni = Str(o, "sni")?.ToLowerInvariant();
        var verify = Str(o, "verify");
        var prefix = Str(o, "prefix")?.TrimEnd('/');
        if (id == null || !IdPattern.IsMatch(id) || addr == null || sni == null || !IsDnsName(sni)
            || verify != VerifyName || prefix == null || !IsPrefix(prefix))
        {
            return null;
        }
        var colon = addr.LastIndexOf(':');
        if (colon <= 0)
        {
            return null;
        }
        var host = addr[..colon].ToLowerInvariant();
        var portText = addr[(colon + 1)..];
        if (portText.Length is 0 or > 5 || !portText.All(c => c is >= '0' and <= '9'))
        {
            return null;
        }
        var port = int.Parse(portText, System.Globalization.CultureInfo.InvariantCulture);
        if (port is < 1 or > 65535 || !(IsDnsName(host) || Ipv4Pattern.IsMatch(host)))
        {
            return null;
        }
        var region = Str(o, "region")?.ToUpperInvariant();
        var whitelist = o.TryGetProperty("whitelist", out var wl) && wl.ValueKind == JsonValueKind.True;
        return new ColituApiRelay(id, host, port, sni, verify, prefix, region, whitelist);
    }

    private static bool IsPrefix(string p) =>
        p.StartsWith('/') && !p.Contains('?') && !p.Contains('#') && !p.Contains("..") && !p.Contains("//") && !p.Any(char.IsWhiteSpace);

    /// <summary>A DNS name (two labels or more, not an IPv4 literal).</summary>
    public static bool IsDnsName(string s)
    {
        if (s.Length is 0 or > 253 || Ipv4Pattern.IsMatch(s))
        {
            return false;
        }
        var labels = s.Split('.');
        return labels.Length >= 2 && labels.All(l => LabelPattern.IsMatch(l)) && !labels[^1].All(char.IsAsciiDigit);
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Built-in relays from the build value: <c>id|host:port|sni|region|wl</c> entries separated by <c>;</c>.</summary>
    public static IReadOnlyList<ColituApiRelay> ParseBuildValue(string? raw)
    {
        var items = new List<Dictionary<string, object?>>();
        foreach (var entry in (raw ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var f = entry.Split('|', StringSplitOptions.TrimEntries);
            if (f.Length < 3)
            {
                continue;
            }
            items.Add(new Dictionary<string, object?>
            {
                ["id"] = f[0],
                ["addr"] = f[1],
                ["sni"] = f[2],
                ["verify"] = VerifyName,
                ["prefix"] = "/api/v1",
                ["region"] = f.Length > 3 && f[3].Length > 0 ? f[3] : null,
                ["whitelist"] = f.Length > 4 && f[4] == "1"
            });
        }
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(items));
        return ParseArray(doc.RootElement);
    }

    // ---- order -----------------------------------------------------------------------------

    /// <summary>
    /// Base order before the last working one is put first. Normal: api bases (primary, then mirrors),
    /// then relays. In Russia: primary, RU relays, whitelist relays, the rest. In whitelist mode:
    /// whitelist relays first, then RU relays, then the rest.
    /// </summary>
    public static IReadOnlyList<string> RouteOrder(IEnumerable<string> api, IEnumerable<ColituApiRelay> relays, ColituRouteContext ctx)
    {
        var bases = api.Select(b => b.Trim().TrimEnd('/')).Where(b => b.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var all = relays.ToList();
        var wl = all.Where(r => r.Whitelist).Select(r => r.Base).ToList();
        var ru = all.Where(r => !r.Whitelist && r.Region == "RU").Select(r => r.Base).ToList();
        var other = all.Where(r => !r.Whitelist && r.Region != "RU").Select(r => r.Base).ToList();
        IEnumerable<string> order;
        if (ctx.Whitelist)
        {
            order = wl.Concat(ru).Concat(bases).Concat(other);
        }
        else if (string.Equals(ctx.Country, "RU", StringComparison.OrdinalIgnoreCase))
        {
            order = bases.Take(1).Concat(ru).Concat(wl).Concat(bases.Skip(1)).Concat(other);
        }
        else
        {
            order = bases.Concat(other).Concat(ru).Concat(wl);
        }
        return order.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---- HTTP through a relay ----------------------------------------------------------------

    /// <summary>The URL a relay request is sent to: http://api.colitu.com + <paramref name="path"/> (the TLS is done by the connect step).</summary>
    public static Uri RelayUri(string path) => new("http://" + ApiHost + path);

    /// <summary>
    /// The certificate check of a relay: a chain that is valid apart from the name (the name asked for
    /// is the SNI), the leaf valid for <paramref name="verify"/>, then the API pins.
    /// </summary>
    public static bool ValidateRelayCertificate(X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors, string verify, DateTimeOffset now) =>
        RelayNameOk(certificate, errors, verify) && ColituCertPins.Validate(verify, chain, SslPolicyErrors.None, now);

    /// <summary>The chain had no error but the name (the SNI was asked for), and the leaf is valid for <paramref name="verify"/>.</summary>
    public static bool RelayNameOk(X509Certificate? certificate, SslPolicyErrors errors, string verify)
    {
        if (certificate == null || (errors & ~SslPolicyErrors.RemoteCertificateNameMismatch) != SslPolicyErrors.None)
        {
            return false;
        }
        using var leaf = new X509Certificate2(certificate);
        return leaf.MatchesHostname(verify, allowWildcards: true, allowCommonName: false);
    }

    /// <summary>The handler for one relay; <paramref name="validate"/> replaces the certificate check (tests).</summary>
    public static SocketsHttpHandler CreateHandler(
        ColituApiRelay relay,
        Func<X509Certificate?, X509Chain?, SslPolicyErrors, bool>? validate = null,
        Func<DnsEndPoint, CancellationToken, ValueTask<Stream>>? dial = null)
    {
        validate ??= (cert, chain, errors) => ValidateRelayCertificate(cert, chain, errors, relay.Verify, DateTimeOffset.UtcNow);
        dial ??= ColituPinnedHosts.ConnectToAsync;
        return new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            ConnectCallback = async (context, token) =>
            {
                // Only the API host is ever sent here; anything else would go out without TLS.
                if (!string.Equals(context.DnsEndPoint.Host, ApiHost, StringComparison.OrdinalIgnoreCase))
                {
                    throw new HttpRequestException(HttpRequestError.ConnectionError, "relay handler used for another host");
                }
                var raw = await dial(new DnsEndPoint(relay.Host, relay.Port), token);
                var tls = new SslStream(raw, leaveInnerStreamOpen: false);
                try
                {
                    await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = relay.Sni,
                        ApplicationProtocols = [SslApplicationProtocol.Http11],
                        RemoteCertificateValidationCallback = (_, cert, chain, errors) => validate(cert, chain, errors)
                    }, token);
                    return tls;
                }
                catch
                {
                    await tls.DisposeAsync();
                    throw;
                }
            }
        };
    }

    private static readonly Dictionary<ColituApiRelay, HttpClient> Clients = new();

    /// <summary>One pooled client per relay (no timeout of its own: the caller's token limits each call).</summary>
    public static HttpClient ClientFor(ColituApiRelay relay)
    {
        lock (Clients)
        {
            if (!Clients.TryGetValue(relay, out var client))
            {
                client = new HttpClient(CreateHandler(relay)) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 64L << 20 };
                Clients[relay] = client;
            }
            return client;
        }
    }
}

/// <summary>
/// Self-heal timer: while the client works off its first base, the earlier bases are probed again
/// every <see cref="ColituApiRelays.HealInterval"/> (the first period starts when the move is seen).
/// </summary>
public sealed class ColituApiPathHealer(TimeSpan interval)
{
    private DateTimeOffset? _since;

    public ColituApiPathHealer() : this(ColituApiRelays.HealInterval)
    {
    }

    /// <summary>True when a periodic probe run is due at <paramref name="now"/>; claims it.</summary>
    public bool Claim(DateTimeOffset now, bool offFirst)
    {
        lock (this)
        {
            if (!offFirst)
            {
                _since = null;
                return false;
            }
            if (_since is not { } start)
            {
                _since = now;
                return false;
            }
            if (now - start < interval)
            {
                return false;
            }
            _since = now;
            return true;
        }
    }

    public void Reset()
    {
        lock (this)
        {
            _since = null;
        }
    }
}
