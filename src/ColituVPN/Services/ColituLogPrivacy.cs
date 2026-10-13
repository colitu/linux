using System.Diagnostics;
using Process = System.Diagnostics.Process;
using System.Text.RegularExpressions;

namespace v2rayN.Desktop.Services;

/// <summary>
/// Keeps the sites a user visits out of the app log. The VPN cores print a line
/// for DNS lookups and connections; those lines are dropped, and host names or
/// addresses in the remaining core messages (errors, warnings) are masked,
/// except the VPN server itself, which support needs to see.
/// </summary>
public static partial class ColituLogPrivacy
{
    private const string Mask = "<host>";

    /// <summary>
    /// Returns the core line to log, or null when it only describes user traffic.
    /// <paramref name="keepHosts"/> are the VPN server's own names and addresses.
    /// </summary>
    public static string? SanitizeCoreLine(string line, IReadOnlyCollection<string>? keepHosts = null)
    {
        if (string.IsNullOrWhiteSpace(line) || IsTrafficLine(line) || IsRoutineClose(line))
        {
            return null;
        }
        return MaskHosts(line, keepHosts);
    }

    /// <summary>For logs attached to support requests: removes traffic lines and masks other hosts.</summary>
    public static string StripTraffic(string text)
    {
        var lines = text.Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            // App log lines carry core output after "core notify=<bool>: ".
            var marker = line.IndexOf("core notify=", StringComparison.Ordinal);
            var start = marker < 0 ? -1 : line.IndexOf(": ", marker, StringComparison.Ordinal);
            if (start < 0)
            {
                kept.Add(line);
                continue;
            }
            var core = line[(start + 2)..];
            if (IsTrafficLine(core) || IsRoutineClose(core))
            {
                continue;
            }
            kept.Add(line[..(start + 2)] + MaskHosts(core, null));
        }
        return string.Join('\n', kept);
    }

    /// <summary>
    /// A server, mirror or resolved address as the app's own log lines show it: colitu.com names
    /// as they are; other names only by their last label ("***.org"); IPv4 by its first two
    /// octets ("203.0.x.x"), IPv6 by its first two groups. Enough to tell a network problem from a
    /// DNS one, without node or mirror names and addresses in a log that may be sent to support.
    /// </summary>
    public static string ForLog(string? hostOrAddress)
    {
        var value = hostOrAddress?.Trim().Trim('[', ']') ?? "";
        if (value.Length == 0)
        {
            return "?";
        }
        if (System.Net.IPAddress.TryParse(value, out var address))
        {
            if (System.Net.IPAddress.IsLoopback(address))
            {
                return value;
            }
            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var bytes = address.GetAddressBytes();
                return $"{bytes[0]}.{bytes[1]}.x.x";
            }
            var groups = address.ToString().Split(':');
            return groups.Length >= 2 ? $"{groups[0]}:{groups[1]}::x" : "::x";
        }
        var host = value.TrimEnd('.');
        if (host.Equals("colitu.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".colitu.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return host;
        }
        var dot = host.LastIndexOf('.');
        return dot > 0 && dot < host.Length - 1 ? "***" + host[dot..] : "***";
    }

    /// <summary><see cref="ForLog(string?)"/> for a list of addresses.</summary>
    public static string ForLog(IEnumerable<System.Net.IPAddress> addresses) =>
        string.Join(", ", addresses.Select(address => ForLog(address.ToString())));

    internal static bool IsTrafficLine(string line)
    {
        return TrafficLine().IsMatch(line);
    }

    /// <summary>
    /// sing-box reports every closed connection as an error: a page closing its socket, the remote
    /// end hanging up, a site the server cannot reach. Hundreds a day, nothing about the tunnel,
    /// and they buried the lines that matter.
    /// </summary>
    internal static bool IsRoutineClose(string line)
    {
        return RoutineClose().IsMatch(line);
    }

    internal static string MaskHosts(string line, IReadOnlyCollection<string>? keepHosts)
    {
        string Replace(Match match)
        {
            var value = match.Value;
            if (value is "127.0.0.1" or "0.0.0.0" or "localhost"
                || (keepHosts != null && keepHosts.Contains(value, StringComparer.OrdinalIgnoreCase)))
            {
                return value;
            }
            return Mask;
        }

        line = Ipv4().Replace(line, Replace);
        line = Ipv6().Replace(line, Replace);
        return HostName().Replace(line, Replace);
    }

    // Access-log lines, sniffing, and info/debug output (DNS answers, connections) of Xray and sing-box.
    // Xray access lines look like "from 127.0.0.1:5000 accepted tcp:host:443" or "accepted //host:443 [socks >> proxy]".
    [GeneratedRegex(@"\baccepted\s|\bsniffed\b|\[(Info|Debug)\]|(^|\s)(INFO|DEBUG|TRACE)\s")]
    private static partial Regex TrafficLine();

    [GeneratedRegex(@"connection: connection (?:upload|download) closed: .*(?:endpoint not connected|canceled by remote with error code 0|use of closed network connection|connection reset by peer|forcibly closed|broken pipe|\bEOF\b|remote error: dial tcp[46]? .*(?:i/o timeout|network is unreachable|connection refused))")]
    private static partial Regex RoutineClose();

    [GeneratedRegex(@"\b(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3}\b")]
    private static partial Regex Ipv4();

    // Three or more colons, or a "::" run, so clock times (01:18:22) are left alone.
    // The "::" form is tried first so a compressed address (2a00:1450::1aca) is masked as a whole.
    [GeneratedRegex(@"\b(?:[0-9a-f]{1,4}:){1,6}:(?:[0-9a-f]{1,4}(?::[0-9a-f]{1,4})*\b)?|\b(?:[0-9a-f]{1,4}:){3,7}[0-9a-f]{1,4}\b", RegexOptions.IgnoreCase)]
    private static partial Regex Ipv6();

    // Host names with a real top-level domain; file names (config.json, xray.exe) are not matched.
    [GeneratedRegex(@"\b(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+(?!(?:json|exe|dll|dat|txt|log|db|go|yaml|yml)\b)[a-z]{2,24}\b", RegexOptions.IgnoreCase)]
    private static partial Regex HostName();
}
