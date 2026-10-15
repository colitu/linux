using System.Net;
using System.Text.RegularExpressions;

namespace v2rayN.Desktop.Services;

/// <summary>
/// Last pass over a connection report before it leaves the device: host names and addresses of
/// sites the user visited (dial targets, DNS lookups, URLs) become "&lt;site&gt;" or "&lt;ip&gt;".
/// Kept: the Colitu servers the app knows (and hosts on the same domain), loopback, colitu.com,
/// and a few well-known connectivity-check hosts and public resolvers. The same rules run in the
/// panel; no server name is built into the app.
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
            if (!IPAddress.TryParse(host, out _) && LastTwoLabels(host) is { } domain)
            {
                domains.Add(domain);
            }
        }

        string Mask(string value)
        {
            var host = Normalize(value);
            return IsKept(host, exact, domains) ? value : IPAddress.TryParse(host, out _) ? IpMask : SiteMask;
        }

        text = Dial().Replace(text, match => $"{match.Groups[1].Value}:{Mask(match.Groups[2].Value)}:{match.Groups[3].Value}");
        text = Lookup().Replace(text, match =>
        {
            var name = match.Groups[2].Value;
            var host = Normalize(name);
            return HasFileExtension(host) ? match.Value : $"{match.Groups[1].Value} {Mask(name)}{match.Groups[3].Value}";
        });
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
        return LastTwoLabels(host) is { } domain && domains.Contains(domain);
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

    private static string? LastTwoLabels(string host)
    {
        var labels = host.Split('.');
        return labels.Length < 2 ? null : $"{labels[^2]}.{labels[^1]}";
    }

    [GeneratedRegex(@"\b(tcp|udp):(\[[0-9a-f:.]+\]|[a-z0-9._-]+):(\d{1,5})\b", RegexOptions.IgnoreCase)]
    private static partial Regex Dial();

    [GeneratedRegex(@"\b(response for|lookup|resolve|resolving|query for|sniffed domain:|domain:?|domain) (\[?[a-z0-9_-]+(?:\.[a-z0-9_-]+)+\]?)(\.?)", RegexOptions.IgnoreCase)]
    private static partial Regex Lookup();

    [GeneratedRegex(@"\b(https?|wss?)://([a-z0-9._-]+|\[[0-9a-f:.]+\])", RegexOptions.IgnoreCase)]
    private static partial Regex Url();
}
