using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace v2rayN.Desktop.Services;

/// <summary>Where the tunnel leaves the internet: the exit IP and its country.</summary>
public sealed record ColituExitInfo(string Ip, string? CountryCode);

/// <summary>Outcome of one leak check line.</summary>
public enum ColituLeakState { Ok, Warn, Unknown }

/// <summary>The one-click IP / DNS leak test: what was seen and what it means.</summary>
public sealed record ColituLeakReport(
    ColituExitInfo? Exit,
    ColituLeakState IpState,
    string? ResolverIp,
    string? ResolverCountry,
    ColituLeakState DnsState);

/// <summary>
/// Connection info and the leak test. Both ask through the local proxy port, so the answer is what
/// the tunnel's exit looks like. Services used (no account, no key, nothing about the user is sent):
/// <list type="bullet">
/// <item>Cloudflare <c>/cdn-cgi/trace</c>: answers with the caller's IP and its country.</item>
/// <item><c>whoami.akamai.net</c>: an A record holding the IP of the DNS resolver that asked for it,
/// resolved through the system resolver (glibc / systemd-resolved), which is what every app uses.</item>
/// <item><c>api.country.is/&lt;resolver ip&gt;</c> (open source): the resolver's country. Only the
/// resolver's address is sent, never the user's.</item>
/// </list>
/// </summary>
public static class ColituConnectionCheck
{
    public const string TraceUrl = "https://www.cloudflare.com/cdn-cgi/trace";
    public const string ResolverEcho = "whoami.akamai.net";
    public const string CountryUrl = "https://api.country.is/";

    /// <summary>The exit IP and country, asked through the local SOCKS port.</summary>
    public static async Task<ColituExitInfo?> ExitAsync(int socksPort, NetworkCredential? credentials, CancellationToken token)
    {
        using var http = Client(socksPort, credentials);
        var text = await http.GetStringAsync(TraceUrl, token);
        return FromTrace(text);
    }

    /// <summary>Full leak test: exit through the tunnel, DNS resolver through the system resolver, then the verdicts.</summary>
    public static async Task<ColituLeakReport> LeakTestAsync(int socksPort, NetworkCredential? credentials, string? serverCountry, CancellationToken token)
    {
        using var http = Client(socksPort, credentials);
        ColituExitInfo? exit = null;
        try
        {
            exit = FromTrace(await http.GetStringAsync(TraceUrl, token));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            Logging.SaveLog($"ColituConnectionCheck.Exit: {ex.GetType().Name}");
        }

        string? resolver = null;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(ResolverEcho, AddressFamily.InterNetwork, token);
            resolver = addresses.FirstOrDefault()?.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            Logging.SaveLog($"ColituConnectionCheck.Resolver: {ex.GetType().Name}");
        }

        string? resolverCountry = null;
        if (resolver != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(await http.GetStringAsync(CountryUrl + resolver, token));
                resolverCountry = doc.RootElement.TryGetProperty("country", out var country) ? country.GetString() : null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                Logging.SaveLog($"ColituConnectionCheck.ResolverCountry: {ex.GetType().Name}");
            }
        }

        return new ColituLeakReport(exit, JudgeIp(exit, serverCountry), resolver, resolverCountry, JudgeDns(exit, resolver, resolverCountry));
    }

    /// <summary>Parses Cloudflare's trace ("ip=…", "loc=…" lines).</summary>
    public static ColituExitInfo? FromTrace(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        string? ip = null, loc = null;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cut = line.IndexOf('=');
            if (cut <= 0)
            {
                continue;
            }
            var key = line[..cut];
            var value = line[(cut + 1)..];
            if (key == "ip") ip = value;
            else if (key == "loc") loc = value;
        }
        if (ip == null || !IPAddress.TryParse(ip, out _))
        {
            return null;
        }
        return new ColituExitInfo(ip, loc is { Length: 2 } && loc != "XX" ? loc.ToUpperInvariant() : null);
    }

    /// <summary>
    /// The exit IP is fine when it comes from the connected server's country. Unknown when either
    /// side is unknown (the automatic choice before the panel names the country, a failed check).
    /// </summary>
    public static ColituLeakState JudgeIp(ColituExitInfo? exit, string? serverCountry)
    {
        if (exit?.CountryCode == null || string.IsNullOrWhiteSpace(serverCountry))
        {
            return exit == null ? ColituLeakState.Unknown : ColituLeakState.Ok;
        }
        return string.Equals(exit.CountryCode, serverCountry, StringComparison.OrdinalIgnoreCase) ? ColituLeakState.Ok : ColituLeakState.Warn;
    }

    /// <summary>
    /// DNS is fine when the resolver the system used sits next to the exit (same /16) or in the exit's
    /// country: the queries went out through the server. A resolver elsewhere, typically the home
    /// ISP's, may see every name the PC looks up.
    /// </summary>
    public static ColituLeakState JudgeDns(ColituExitInfo? exit, string? resolverIp, string? resolverCountry)
    {
        if (resolverIp == null || exit == null)
        {
            return ColituLeakState.Unknown;
        }
        if (SameSlash16(exit.Ip, resolverIp))
        {
            return ColituLeakState.Ok;
        }
        if (resolverCountry == null || exit.CountryCode == null)
        {
            return ColituLeakState.Unknown;
        }
        return string.Equals(resolverCountry, exit.CountryCode, StringComparison.OrdinalIgnoreCase) ? ColituLeakState.Ok : ColituLeakState.Warn;
    }

    internal static bool SameSlash16(string a, string b)
    {
        if (!IPAddress.TryParse(a, out var left) || !IPAddress.TryParse(b, out var right)
            || left.AddressFamily != AddressFamily.InterNetwork || right.AddressFamily != AddressFamily.InterNetwork)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
        var l = left.GetAddressBytes();
        var r = right.GetAddressBytes();
        return l[0] == r[0] && l[1] == r[1];
    }

    private static HttpClient Client(int socksPort, NetworkCredential? credentials)
    {
        var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"socks5://{Global.Loopback}:{socksPort}") { Credentials = credentials },
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(8)
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ColituVPN");
        return http;
    }
}
