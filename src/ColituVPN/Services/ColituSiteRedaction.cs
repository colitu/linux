using System.Net;
using System.Text.RegularExpressions;

namespace v2rayN.Desktop.Services;

/// <summary>
/// Last pass over a connection report before it leaves the device: host names and addresses of
/// sites the user visited (dial targets, DNS lookups, URLs) become "&lt;site&gt;" or "&lt;ip&gt;".
/// Kept: the Colitu servers the app knows (and hosts on the same registrable domain, a "co.uk"-style
/// suffix counting as one label), loopback, colitu.com, and a few well-known connectivity-check
/// hosts and public resolvers. Covers Xray dial targets ("tcp:host:443"), Go network errors
/// ("dial tcp 1.2.3.4:443", "read udp 10.0.0.2:5353->8.8.8.8:53"), sing-box "outbound connection
/// to host:443", Xray DNS ("got answer: host.", "exchanged host."), JSON "addr" fields, "sni=host"
/// and URLs. The same rules run in the panel; no server name is built into the app.
/// </summary>
public static partial class ColituSiteRedaction
{
    private const string SiteMask = "<site>";
    private const string IpMask = "<ip>";

    private static readonly HashSet<string> KeepHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "www.gstatic.com", "connectivitycheck.gstatic.com", "captive.apple.com", "www.apple.com", "ya.ru",
        "www.msftconnecttest.com", "8.8.8.8", "8.8.4.4", "77.88.8.8", "208.67.222.222", "1.1.1.1",
        "localhost", "colitu.com"
    };

    /// <summary>Public suffixes of two labels: the registrable domain under them has three ("example.co.uk").</summary>
    private static readonly HashSet<string> MultiLabelSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "co.uk", "org.uk", "ac.uk", "gov.uk", "com.tr", "com.ru", "com.cn", "net.cn", "com.au", "co.jp", "com.br"
    };

    private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "json", "jsonl", "log", "txt", "yaml", "yml", "dat", "db", "go", "swift", "kt", "cs", "dart", "js"
    };

    /// <summary>Masks the sites in <paramref name="text"/>; <paramref name="serverHosts"/> are the app's own servers.</summary>
    public static string Apply(string text, IEnumerable<string?>? serverHosts)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var exact = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in serverHosts ?? [])
        {
            var host = Normalize(raw);
            if (host.Length == 0)
            {
                continue;
            }
            exact.Add(host);
            if (!IPAddress.TryParse(host, out _) && RegistrableDomain(host) is { } domain)
            {
                domains.Add(domain);
            }
        }

        string Mask(string value)
        {
            var host = Normalize(value);
            return IsKept(host, exact, domains) ? value : IPAddress.TryParse(host, out _) ? IpMask : SiteMask;
        }

        string HostPort(Group host, Group port) => $"{Mask(host.Value)}:{port.Value}";

        text = Dial().Replace(text, match => $"{match.Groups[1].Value}:{Mask(match.Groups[2].Value)}:{match.Groups[3].Value}");
        // Go: "dial tcp 1.2.3.4:443", "read udp 10.0.0.2:5353->8.8.8.8:53" (both ends).
        text = GoNet().Replace(text, match =>
            $"{match.Groups[1].Value} "
            + (match.Groups[2].Success ? $"{HostPort(match.Groups[2], match.Groups[3])}->" : "")
            + HostPort(match.Groups[4], match.Groups[5]));
        // sing-box: "outbound connection to host:443", "inbound connection from 1.2.3.4:50000".
        text = SingBoxConnection().Replace(text, match => $"{match.Groups[1].Value} {HostPort(match.Groups[2], match.Groups[3])}");
        // JSON: "addr":"host:443".
        text = JsonAddr().Replace(text, match => $"{match.Groups[1].Value}{HostPort(match.Groups[2], match.Groups[3])}\"");
        text = Sni().Replace(text, match => $"{match.Groups[1].Value}{Mask(match.Groups[2].Value)}");
        text = Lookup().Replace(text, match =>
        {
            var name = match.Groups[2].Value;
            var host = Normalize(name);
            return HasFileExtension(host) ? match.Value : $"{match.Groups[1].Value} {Mask(name)}{match.Groups[3].Value}";
        });
        // Xray DNS: the addresses of an answer ("got answer: host. TypeA -> [1.2.3.4 5.6.7.8]").
        text = AnswerAddresses().Replace(text, match =>
            match.Groups[1].Value + AddressInList().Replace(match.Groups[2].Value, address => Mask(address.Value)) + "]");
        return Url().Replace(text, match => $"{match.Groups[1].Value}://{Mask(match.Groups[2].Value)}");
    }

    private static bool IsKept(string host, HashSet<string> exact, HashSet<string> domains)
    {
        if (host.Length == 0 || KeepHosts.Contains(host) || exact.Contains(host))
        {
            return true;
        }
        if (IPAddress.TryParse(host, out var address))
        {
            return IPAddress.IsLoopback(address);
        }
        if (host.EndsWith(".colitu.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return RegistrableDomain(host) is { } domain && domains.Contains(domain);
    }

    private static bool HasFileExtension(string host)
    {
        var dot = host.LastIndexOf('.');
        return dot >= 0 && FileExtensions.Contains(host[(dot + 1)..]);
    }

    private static string Normalize(string? host)
    {
        return (host ?? "").Trim().Trim('[', ']').TrimEnd('.').ToLowerInvariant();
    }

    /// <summary>The registrable domain: the last two labels, or three under a suffix like "co.uk"; null for a bare suffix.</summary>
    internal static string? RegistrableDomain(string host)
    {
        var labels = host.Split('.');
        if (labels.Length < 2)
        {
            return null;
        }
        var lastTwo = $"{labels[^2]}.{labels[^1]}";
        if (!MultiLabelSuffixes.Contains(lastTwo))
        {
            return lastTwo;
        }
        return labels.Length < 3 ? null : $"{labels[^3]}.{lastTwo}";
    }

    /// <summary>A host name, IPv4 address or bracketed IPv6 address (one capture group).</summary>
    private const string HostPattern = @"(\[[0-9a-f:.]+\]|[a-z0-9._-]+)";

    [GeneratedRegex(@"\b(tcp|udp):(\[[0-9a-f:.]+\]|[a-z0-9._-]+):(\d{1,5})\b", RegexOptions.IgnoreCase)]
    private static partial Regex Dial();

    [GeneratedRegex(@"\b((?:dial|read|write) (?:tcp|udp)[46]?) (?:" + HostPattern + @":(\d{1,5})->)?" + HostPattern + @":(\d{1,5})\b", RegexOptions.IgnoreCase)]
    private static partial Regex GoNet();

    [GeneratedRegex(@"\b((?:inbound|outbound) connection (?:to|from)) " + HostPattern + @":(\d{1,5})\b", RegexOptions.IgnoreCase)]
    private static partial Regex SingBoxConnection();

    [GeneratedRegex(@"(""addr""\s*:\s*"")" + HostPattern + @":(\d{1,5})""", RegexOptions.IgnoreCase)]
    private static partial Regex JsonAddr();

    [GeneratedRegex(@"\b(sni=)" + HostPattern, RegexOptions.IgnoreCase)]
    private static partial Regex Sni();

    [GeneratedRegex(@"\b(response for|lookup|resolve|resolving|query for|sniffed domain:|got answer:|exchanged|exchanging|domain:?|domain) (\[?[a-z0-9_-]+(?:\.[a-z0-9_-]+)+\]?)(\.?)", RegexOptions.IgnoreCase)]
    private static partial Regex Lookup();

    [GeneratedRegex(@"(got answer: \S+ \S+ -> \[)([^\]]*)\]", RegexOptions.IgnoreCase)]
    private static partial Regex AnswerAddresses();

    [GeneratedRegex(@"[0-9a-f]*[:.][0-9a-f:.]*[0-9a-f]", RegexOptions.IgnoreCase)]
    private static partial Regex AddressInList();

    [GeneratedRegex(@"\b(https?|wss?)://([a-z0-9._-]+|\[[0-9a-f:.]+\])", RegexOptions.IgnoreCase)]
    private static partial Regex Url();
}
