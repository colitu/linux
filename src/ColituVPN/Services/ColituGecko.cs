using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace v2rayN.Desktop.Services;

/// <summary>
/// ColituGecko, Colitu's own protocol: a peer of VLESS, Trojan, Hysteria2 and the others.
/// <c>gecko-client</c> (Colitu's private colitu-gecko repository, built like csl-client) opens a SOCKS5
/// front (TCP CONNECT and UDP ASSOCIATE, username/password) on loopback and carries what it gets over
/// its own TLS/QUIC connections to Colitu's Gecko servers; the exit is chosen by the panel's anchors,
/// never by a server row. Offered exactly when the panel offers it (<c>GET /gecko/config</c>: 200 with a
/// signed configuration; 404 hides everything Gecko; 503 or an unusable answer = offered, unavailable now).
/// <para>
/// Gecko-only profile (<see cref="ApplyOnly"/>): the app's normal TUN / local proxy inbound, every
/// proxied connection (TCP and UDP, DNS included) → one SOCKS outbound to gecko-client (per-run
/// account), Russian and private destinations direct as on every profile, no server outbound. In TUN
/// mode the sing-box config that runs the adapter sends gecko-client's own connections to
/// <c>direct</c> by its full path (<see cref="AddSidecarDirectRules"/>), the way it sends the cores'.
/// </para>
/// <para>
/// Secrets: the panel's configuration (passed through unchanged) and the per-run SOCKS account go
/// to gecko-client's standard input only; never to its command line, a log line or the profile store.
/// </para>
/// <para>
/// Adaptive Connect: normal servers → Gecko-only → Whitelist (<see cref="ColituConnectSteps"/>). A failed
/// Gecko attempt (no non-bootstrap session within <see cref="ReadyDeadline"/>, gecko-client exiting, no
/// traffic) starts a cool-down on that network that automatic connects respect; a manual pick does not.
/// </para>
/// </summary>
internal static class ColituGeckoRules
{
    public const string OutboundTag = "colitu-gecko";
    public const string ExecutableName = "gecko-client";
    /// <summary>The protocol's id: the picker's method, the connected transport and the journal's step name source.</summary>
    public const string Protocol = "colitu-gecko";
    /// <summary>The Adaptive Connect journal's name of the Gecko step.</summary>
    public const string StepName = "gecko";
    /// <summary>The profile store's id of the Gecko-only profile (a SOCKS profile to loopback, no secret in it).</summary>
    public const string ProfileIndexId = "colitu-gecko";
    /// <summary>An automatic connect waits at most this long for a configuration when none is cached.</summary>
    public static readonly TimeSpan ConfigWait = TimeSpan.FromSeconds(6);
    /// <summary>After a failed Gecko attempt on a network, automatic connects skip Gecko there this long.</summary>
    public static readonly TimeSpan CoolDown = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ReadyDeadline = TimeSpan.FromSeconds(15);
    /// <summary><c>need_config</c> fetches a new configuration from the panel at most this often.</summary>
    public static readonly TimeSpan ConfigRefreshInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Skin keys pinned in the app (id → 32-byte Ed25519 public key, hex). Id 2 is the production
    /// skin key: descriptors of real entries are signed with it. The lab key (id 1) signs only the
    /// canary's and the tests' descriptors and is deliberately not pinned here, so an app never
    /// takes a lab descriptor.
    /// </summary>
    public static readonly IReadOnlyDictionary<int, string> SkinKeys = new SortedDictionary<int, string>
    {
        [2] = "05c3ca41534e35dfe1f1ec807733a1d5b77d60ac24e272e29fcc1a0fdb5eb2fc"
    };

    /// <summary>
    /// The configuration key pinned in the app (F-14): id 1, Ed25519 public key, hex. The panel signs
    /// <c>GET /gecko/config</c> with it (<c>X-Gecko-Config-Signature</c>) and gecko-client refuses
    /// anything else. It is separate from the skin key.
    /// </summary>
    public static readonly IReadOnlyDictionary<int, string> ConfigKeys = new SortedDictionary<int, string>
    {
        [1] = "e0a6c63f2313311887b9e7e7f670390af3a16e8a9249b7d97b6ad32426615faa"
    };

    /// <summary>Tests only: these keys replace the pinned one on the command line (the lab test with its own signing key).</summary>
    internal static IReadOnlyDictionary<int, string>? TestConfigKeys { get; set; }

    /// <summary>
    /// Tests only (the lab test against the canary sets the lab key, id 1, from its environment):
    /// extra keys added to the command line. The app never sets it.
    /// </summary>
    internal static IReadOnlyDictionary<int, string>? TestSkinKeys { get; set; }

    private static readonly Regex HexKey = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> NeedConfigReasons = new(StringComparer.Ordinal) { "half_life", "bootstrap", "expired" };

    /// <summary>
    /// Why the Gecko step is skipped; null when it runs. <paramref name="offer"/> is
    /// <see cref="ColituGeckoOffer.Offered"/> only with a signed configuration in hand. Checked in this
    /// order, so the reason names the first thing missing. A manual pick ignores the cool-down.
    /// </summary>
    public static string? OffReason(ColituGeckoOffer offer, bool binaryPresent, bool killSwitchPinsCores, bool coolingDown, bool manual)
    {
        if (offer == ColituGeckoOffer.NotOffered) return "not offered to this account";
        if (offer != ColituGeckoOffer.Offered) return "Gecko settings unavailable";
        if (!binaryPresent) return "gecko-client missing";
        // The kill switch would let the cores (and gecko-client with them) reach only the VPN servers'
        // addresses; the Gecko servers are not among them.
        if (killSwitchPinsCores) return "the kill switch allows only the VPN servers' addresses";
        if (coolingDown && !manual) return "cooling down after a failure on this network";
        return null;
    }

    /// <summary>The offer as the Gecko step sees it: offered only with a signed configuration in hand.</summary>
    public static ColituGeckoOffer EffectiveOffer(ColituGeckoConfigResult? config) =>
        config?.Signed != null ? ColituGeckoOffer.Offered
        : config?.Offer == ColituGeckoOffer.NotOffered ? ColituGeckoOffer.NotOffered
        : config == null ? ColituGeckoOffer.Unknown
        : ColituGeckoOffer.Unavailable;

    /// <summary>
    /// The state of a starting gecko-client: still waiting (null), on (empty string) or off with a
    /// reason. Exiting wins over everything; on needs both the SOCKS line and a non-bootstrap session.
    /// </summary>
    public static string? Verdict(bool listening, bool session, bool exited, int? exitCode, TimeSpan elapsed, TimeSpan? deadline = null)
    {
        var limit = deadline ?? ReadyDeadline;
        var code = exitCode?.ToString() ?? "?";
        if (exited) return listening && session ? $"gecko-client exited (code {code})" : $"gecko-client exited before its session came up (code {code})";
        if (listening && session) return "";
        if (elapsed >= limit) return listening ? $"no session within {limit.TotalSeconds:0} s" : $"no SOCKS port within {limit.TotalSeconds:0} s";
        return null;
    }

    /// <summary>gecko-client logs this once its SOCKS port is open.</summary>
    public static bool IsReadyLine(string? line) => line?.Contains("gecko-client: SOCKS5 on ", StringComparison.Ordinal) == true;

    /// <summary>
    /// A <c>gecko-client: event {json}</c> line; null for any other line, malformed JSON and unknown
    /// events. Only the fields the app needs are kept, and only in a shape that cannot carry secrets.
    /// </summary>
    public static ColituGeckoEvent? ParseEvent(string? line)
    {
        const string marker = "gecko-client: event ";
        var at = line?.IndexOf(marker, StringComparison.Ordinal) ?? -1;
        if (at < 0)
        {
            return null;
        }
        JsonObject? body;
        try
        {
            body = JsonNode.Parse(line![(at + marker.Length)..]) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
        if (body == null || Str(body["event"]) is not { } kind)
        {
            return null;
        }
        switch (kind)
        {
            case "session":
                return new ColituGeckoEvent(kind, Anchor: Long(body["anchor"]), Bootstrap: Bool(body["bootstrap"]));
            case "need_config":
                return new ColituGeckoEvent(kind, Reason: Str(body["reason"]) is { } reason && NeedConfigReasons.Contains(reason) ? reason : "other");
            case "grip":
                // "entry" names a server of the fleet: kept for the panel's report endpoint, never logged (Describe leaves it out).
                return new ColituGeckoEvent(kind, Skin: Token(body["skin"]), Rev: Token(body["rev"]), Class: Token(body["class"]),
                    Bytes: Long(body["bytes"]), Bucket: Token(body["bucket"]), Entry: Token(body["entry"]));
            case "grip_end":
                // Exact figures stay in memory: ColituGeckoReports cuts them into slice labels, nothing else reads them.
                return new ColituGeckoEvent(kind, Skin: Token(body["skin"]), Rev: Token(body["rev"]), Class: Token(body["class"]), Entry: Token(body["entry"]),
                    LifeMs: Long(body["life_ms"]), Received: Long(body["received"]), Sent: Long(body["sent"]), RttMs: Long(body["rtt_ms"]), Goodput: Long(body["goodput"]));
            case "config":
                return new ColituGeckoEvent(kind, Issued: Long(body["issued"]));
            case "config_refused":
                return new ColituGeckoEvent(kind, Reason: Str(body["reason"]) is "signature" or "old" ? Str(body["reason"]) : "other");
            case "skin_revs":
                return new ColituGeckoEvent(kind, Revs: Revs(body["revs"]));
            case "lost":
                return new ColituGeckoEvent(kind);
            default:
                return null;
        }
    }

    /// <summary>The panel's configuration is a JSON object with a non-empty <c>anchors</c> array; nothing else is checked.</summary>
    public static bool IsUsableConfig(string? json) => ParseConfig(json) != null;

    /// <summary>
    /// The header value as the panel sends it: hex of one key-id byte and a 64-byte signature. Only the
    /// shape is checked here; whether it verifies is gecko-client's job (it holds the pinned key).
    /// </summary>
    public static bool IsSignatureShape(string? signature) => signature is { Length: 130 } && HexOnly.IsMatch(signature);

    private static readonly Regex HexOnly = new("^[0-9a-fA-F]+$", RegexOptions.CultureInvariant);

    /// <summary>
    /// A signed configuration from the exact response bytes and the signature header; null when the header
    /// is missing or malformed, or the body is not a usable configuration. The body is never parsed and
    /// written again: the signature covers these bytes.
    /// </summary>
    public static ColituGeckoSignedConfig? Sign(byte[]? body, string? signature) =>
        body is { Length: > 0 } && IsSignatureShape(signature?.Trim()) && IsUsableConfig(Encoding.UTF8.GetString(body))
            ? new ColituGeckoSignedConfig(body, signature!.Trim().ToLowerInvariant())
            : null;

    private static JsonObject? ParseConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonNode.Parse(json) is JsonObject root && root["anchors"] is JsonArray { Count: > 0 } ? root : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Where the entries of a configuration (hex records, docs of the C.3 entry layout: type 3 is
    /// atyp | address | port) are dialled: a literal address, or a domain (a CDN's front name) the
    /// caller resolves. Entries that do not parse are skipped; the list is capped. The kill switch
    /// lets gecko-client's process reach exactly these.
    /// </summary>
    public static List<ColituGeckoTarget> EntryTargets(string? configJson)
    {
        var targets = new List<ColituGeckoTarget>();
        if (ParseConfig(configJson) is not { } root || root["anchors"] is not JsonArray anchors)
        {
            return targets;
        }
        foreach (var anchor in anchors.OfType<JsonObject>())
        {
            if (anchor["entries"] is not JsonArray entries)
            {
                continue;
            }
            foreach (var item in entries)
            {
                if (Str(item) is { } hex && EntryTarget(hex) is { } target && !targets.Contains(target))
                {
                    targets.Add(target);
                    if (targets.Count >= 256)
                    {
                        return targets;
                    }
                }
            }
        }
        return targets;
    }

    internal static ColituGeckoTarget? EntryTarget(string hex)
    {
        byte[] raw;
        try
        {
            raw = Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
        var at = 0;
        while (at < raw.Length)
        {
            if (!Uvarint(raw, ref at, out var type) || !Uvarint(raw, ref at, out var length) || length > (ulong)(raw.Length - at))
            {
                return null;
            }
            var value = raw.AsSpan(at, (int)length);
            at += (int)length;
            if (type != 3 || value.Length < 3)
            {
                continue;
            }
            var port = (value[^2] << 8) | value[^1];
            var body = value[1..^2];
            switch (value[0])
            {
                case 1 when body.Length == 4:
                case 4 when body.Length == 16:
                    return port > 0 ? new ColituGeckoTarget(new System.Net.IPAddress(body).ToString(), port) : null;
                case 3 when body.Length >= 2 && body[0] == body.Length - 1:
                    var host = Encoding.ASCII.GetString(body[1..]);
                    return port > 0 && HostName.IsMatch(host) ? new ColituGeckoTarget(host, port) : null;
                default:
                    return null;
            }
        }
        return null;
    }

    private static readonly Regex HostName = new("^[A-Za-z0-9._-]{1,253}$", RegexOptions.CultureInvariant);

    private static bool Uvarint(byte[] data, ref int at, out ulong value)
    {
        value = 0;
        for (var shift = 0; shift < 63 && at < data.Length; shift += 7)
        {
            var b = data[at++];
            value |= (ulong)(b & 0x7f) << shift;
            if (b < 0x80)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The gecko-client command line. Secrets never appear on it: the configuration and the SOCKS
    /// account go to its standard input (<see cref="Secrets"/>). The skin keys are public.
    /// </summary>
    public static List<string> Arguments(ColituGeckoLaunch launch)
    {
        var args = new List<string> { "-socks", $"127.0.0.1:{launch.SocksPort}", "-secrets-stdin" };
        foreach (var (id, key) in TestConfigKeys ?? ConfigKeys)
        {
            if (id > 0 && HexKey.IsMatch(key))
            {
                args.Add("-config-key");
                args.Add($"{id}={key}");
            }
        }
        foreach (var (id, key) in TestSkinKeys == null ? SkinKeys : SkinKeys.Concat(TestSkinKeys.Where(k => !SkinKeys.ContainsKey(k.Key))))
        {
            if (id > 0 && HexKey.IsMatch(key))
            {
                args.Add("-skin-key");
                args.Add($"{id}={key}");
            }
        }
        if (launch.Standby)
        {
            args.Add("-mode");
            args.Add("standby");
        }
        return args;
    }

    /// <summary>The stored floor with new values folded in: each number only ever goes up.</summary>
    public static ColituGeckoFloor MergeFloor(ColituGeckoFloor? stored, long? issued, IReadOnlyDictionary<string, long>? revs)
    {
        var map = new Dictionary<string, long>(stored?.SkinFloor ?? new Dictionary<string, long>(), StringComparer.Ordinal);
        if (revs != null)
        {
            foreach (var (skin, rev) in revs)
            {
                if (rev >= 0 && (!map.TryGetValue(skin, out var held) || rev > held))
                {
                    map[skin] = rev;
                }
            }
        }
        return new ColituGeckoFloor(Math.Max(stored?.MinIssued ?? 0, issued ?? 0), map.Count == 0 ? null : map);
    }

    /// <summary>
    /// The first line on gecko-client's standard input: the per-run SOCKS account, the panel's
    /// configuration as the exact response bytes (<c>config_b64</c>) with its signature
    /// (<c>config_sig</c>), and what the app kept from earlier runs: the highest accepted
    /// <c>issued</c> (<c>min_issued</c>) and the highest skin revisions (<c>skin_floor</c>), both left
    /// out when there are none. One line.
    /// </summary>
    public static string Secrets(ColituGeckoLaunch launch, ColituGeckoSignedConfig config, ColituGeckoFloor? floor = null)
    {
        var line = new JsonObject
        {
            ["socks_user"] = launch.SocksUser,
            ["socks_password"] = launch.SocksPassword,
            ["config_b64"] = Convert.ToBase64String(config.Body),
            ["config_sig"] = config.Signature
        };
        if (floor is { MinIssued: > 0 })
        {
            line["min_issued"] = floor.MinIssued;
        }
        if (floor?.SkinFloor is { Count: > 0 } revs)
        {
            var map = new JsonObject();
            foreach (var (skin, rev) in revs.OrderBy(r => r.Key, StringComparer.Ordinal))
            {
                map[skin] = rev;
            }
            line["skin_floor"] = map;
        }
        return line.ToJsonString() + "\n";
    }

    /// <summary>A later standard-input line: a new configuration replaces the running one (if it verifies).</summary>
    public static string ConfigLine(ColituGeckoSignedConfig config) => new JsonObject
    {
        ["config_b64"] = Convert.ToBase64String(config.Body),
        ["config_sig"] = config.Signature
    }.ToJsonString() + "\n";

    /// <summary>
    /// The Gecko-only profile, made from the core config generated for the Gecko SOCKS profile
    /// (<see cref="Profile"/>): its <c>proxy</c> outbound becomes the SOCKS outbound to gecko-client
    /// (per-run account; TCP and UDP, so DNS through the tunnel goes there too) and every other
    /// outbound that could carry traffic out (a spare, a chain) goes with the rules and balancers that
    /// use it. Direct, block and DNS outbounds stay, so the Russian and private direct rules and ad
    /// blocking work as on every profile. A sing-box config that runs the TUN adapter also sends
    /// gecko-client's own connections direct (<see cref="AddSidecarDirectRules"/>). Null when the
    /// config does not have the expected shape: no <c>proxy</c> outbound, or one that is not the
    /// loopback SOCKS profile (a server's outbound is never replaced), or a TUN config without the
    /// sidecar's full path.
    /// </summary>
    public static string? ApplyOnly(string config, bool singBox, ColituGeckoPlan plan)
    {
        if (Parse(config) is not { } root || root["outbounds"] is not JsonArray outbounds)
        {
            return null;
        }
        var proxy = outbounds.OfType<JsonObject>().FirstOrDefault(item => Tag(item) == ColituWarmSpare.ProxyTag);
        if (proxy == null || !IsLoopbackSocks(proxy, singBox) || outbounds.OfType<JsonObject>().Any(item => Tag(item) == OutboundTag))
        {
            return null;
        }
        if (singBox && HasTun(root) && !IsFullPath(plan.ClientPath))
        {
            // Without the sidecar's path its connections would loop into the TUN.
            return null;
        }
        outbounds[outbounds.IndexOf(proxy)] = singBox ? SingboxOutbound(plan) : XrayOutbound(plan);
        var kept = new HashSet<string>(StringComparer.Ordinal) { ColituWarmSpare.ProxyTag };
        foreach (var item in outbounds.OfType<JsonObject>().ToList())
        {
            if (Tag(item) == ColituWarmSpare.ProxyTag)
            {
                continue;
            }
            if (IsLocalOutbound(item, singBox))
            {
                if (Tag(item) is { } tag) kept.Add(tag);
                continue;
            }
            outbounds.Remove(item);
        }
        if (singBox)
        {
            var route = root["route"] as JsonObject ?? new JsonObject();
            root["route"] = route;
            var rules = route["rules"] as JsonArray ?? new JsonArray();
            route["rules"] = rules;
            foreach (var rule in rules.OfType<JsonObject>().ToList())
            {
                if (Str(rule["outbound"]) is { } target && !kept.Contains(target))
                {
                    rules.Remove(rule);
                }
            }
            if (Str(route["final"]) is { } final && !kept.Contains(final))
            {
                route["final"] = ColituWarmSpare.ProxyTag;
            }
            if (HasTun(root))
            {
                InsertDirectRules(root, rules, plan.ClientPath);
            }
        }
        else if (root["routing"] is JsonObject routing)
        {
            // Balancers pick among server outbounds, and there are none any more.
            routing.Remove("balancers");
            if (routing["rules"] is JsonArray rules)
            {
                foreach (var rule in rules.OfType<JsonObject>().ToList())
                {
                    if (rule["balancerTag"] != null || Str(rule["outboundTag"]) is { } target && !kept.Contains(target))
                    {
                        rules.Remove(rule);
                    }
                }
            }
        }
        return root.ToJsonString(Indented);
    }

    /// <summary>
    /// The Gecko-only profile in the profile store: a SOCKS profile to loopback (no account: the per-run
    /// one is written into the core config by <see cref="ApplyOnly"/> and never stored).
    /// </summary>
    public static ServiceLib.Models.Entities.ProfileItem Profile(string subId, int port) => new()
    {
        IndexId = ProfileIndexId,
        ConfigType = ServiceLib.Enums.EConfigType.SOCKS,
        Remarks = "ColituGecko",
        Address = "127.0.0.1",
        Port = port,
        Network = "raw",
        StreamSecurity = string.Empty,
        Subid = subId
    };

    /// <summary>The config's only way out is gecko-client on <paramref name="port"/> (direct, block and DNS aside).</summary>
    public static bool IsGeckoOnly(string config, bool singBox, int port)
    {
        if (Parse(config) is not { } root || root["outbounds"] is not JsonArray outbounds)
        {
            return false;
        }
        var carriers = outbounds.OfType<JsonObject>().Where(item => !IsLocalOutbound(item, singBox)).ToList();
        if (carriers.Count != 1 || Tag(carriers[0]) != ColituWarmSpare.ProxyTag || !IsLoopbackSocks(carriers[0], singBox))
        {
            return false;
        }
        var actual = singBox ? carriers[0]["server_port"] : carriers[0]["settings"]?["servers"]?[0]?["port"];
        return actual?.GetValueKind() == JsonValueKind.Number && actual.GetValue<int>() == port;
    }

    private static JsonObject XrayOutbound(ColituGeckoPlan plan) => new()
    {
        ["tag"] = ColituWarmSpare.ProxyTag,
        ["protocol"] = "socks",
        ["settings"] = new JsonObject
        {
            ["servers"] = new JsonArray(new JsonObject
            {
                ["address"] = "127.0.0.1",
                ["port"] = plan.SocksPort,
                ["users"] = new JsonArray(new JsonObject { ["user"] = plan.SocksUser, ["pass"] = plan.SocksPassword })
            })
        }
    };

    // No "network" field: sing-box's SOCKS outbound then carries TCP and UDP (UDP ASSOCIATE).
    private static JsonObject SingboxOutbound(ColituGeckoPlan plan) => new()
    {
        ["type"] = "socks",
        ["tag"] = ColituWarmSpare.ProxyTag,
        ["server"] = "127.0.0.1",
        ["server_port"] = plan.SocksPort,
        ["version"] = "5",
        ["username"] = plan.SocksUser,
        ["password"] = plan.SocksPassword
    };

    private static bool IsLoopbackSocks(JsonObject outbound, bool singBox) => singBox
        ? Str(outbound["type"]) == "socks" && Str(outbound["server"]) == "127.0.0.1"
        : Str(outbound["protocol"]) == "socks" && Str(outbound["settings"]?["servers"]?[0]?["address"]) == "127.0.0.1";

    private static readonly HashSet<string> LocalXray = new(StringComparer.OrdinalIgnoreCase) { "freedom", "blackhole", "dns", "loopback" };
    private static readonly HashSet<string> LocalSingbox = new(StringComparer.OrdinalIgnoreCase) { "direct", "block", "dns" };

    private static bool IsLocalOutbound(JsonObject outbound, bool singBox) => singBox
        ? Str(outbound["type"]) is { } type && LocalSingbox.Contains(type)
        : Str(outbound["protocol"]) is { } protocol && LocalXray.Contains(protocol);

    /// <summary>
    /// For a sing-box config that runs the TUN adapter (the sing-box front of an Xray tunnel):
    /// gecko-client's DNS (port 53) is answered by sing-box like Xray's, and everything else it opens
    /// goes out through <c>direct</c>, before any other rule. The config itself when it has no TUN
    /// inbound or already has the rules; null when it is unreadable or the path is not a full path.
    /// </summary>
    public static string? AddSidecarDirectRules(string config, string clientPath)
    {
        if (Parse(config) is not { } root || !IsFullPath(clientPath))
        {
            return null;
        }
        if (!HasTun(root))
        {
            return config;
        }
        var route = root["route"] as JsonObject ?? new JsonObject();
        root["route"] = route;
        var rules = route["rules"] as JsonArray ?? new JsonArray();
        route["rules"] = rules;
        if (rules.OfType<JsonObject>().Any(rule => IsDirectRule(rule, clientPath)))
        {
            return config;
        }
        InsertDirectRules(root, rules, clientPath);
        return root.ToJsonString(Indented);
    }

    /// <summary>The config runs the TUN adapter and sends <paramref name="clientPath"/> direct.</summary>
    public static bool HasDirectRules(string config, string clientPath) =>
        Parse(config) is { } root && HasTun(root)
        && (root["route"]?["rules"] as JsonArray)?.OfType<JsonObject>().Any(rule => IsDirectRule(rule, clientPath)) == true;

    /// <summary>The direct rule for <paramref name="clientPath"/> (full path, case-insensitive, anchored).</summary>
    public static bool IsDirectRule(JsonObject rule, string clientPath) =>
        Str(rule["outbound"]) == "direct" && rule["action"] == null
        && rule["process_path"] is JsonArray { Count: 1 } paths && Str(paths[0]) == clientPath;

    private static void InsertDirectRules(JsonObject root, JsonArray rules, string clientPath)
    {
        var outbounds = root["outbounds"] as JsonArray ?? new JsonArray();
        root["outbounds"] = outbounds;
        if (!outbounds.OfType<JsonObject>().Any(outbound => Tag(outbound) == "direct"))
        {
            outbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = "direct" });
        }
        // As the cores': DNS is hijacked (the adapter's resolver is sing-box itself, so a query sent
        // direct would go nowhere), then everything else of the process goes direct. On Linux the
        // direct outbound carries the routing mark the kill switch lets through (SingboxRoutingService),
        // and the root sing-box finds the process of a TUN connection through /proc.
        rules.Insert(0, new JsonObject { ["process_path"] = new JsonArray(clientPath), ["outbound"] = "direct" });
        rules.Insert(0, new JsonObject { ["port"] = new JsonArray(53), ["process_path"] = new JsonArray(clientPath), ["action"] = "hijack-dns" });
    }

    /// <summary>An absolute path (the sing-box process match is by full path).</summary>
    private static bool IsFullPath(string path) => path.Length > 1 && path[0] == '/' && !path.Contains("/../", StringComparison.Ordinal) && !path.EndsWith("/..", StringComparison.Ordinal);

    private static bool HasTun(JsonObject root) => (root["inbounds"] as JsonArray)?.OfType<JsonObject>()
        .Any(inbound => string.Equals(Str(inbound["type"]), "tun", StringComparison.OrdinalIgnoreCase)) == true;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static JsonObject? Parse(string config)
    {
        try
        {
            return JsonNode.Parse(config) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Tag(JsonObject item) => Str(item["tag"]);

    private static string? Str(JsonNode? node) => node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;

    /// <summary>The <c>revs</c> object of a skin_revs event: skin id (a safe token) to a non-negative revision.</summary>
    private static Dictionary<string, long>? Revs(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }
        var revs = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (skin, value) in obj)
        {
            if (SafeToken.IsMatch(skin) && Long(value) is >= 0 and var rev)
            {
                revs[skin] = rev;
            }
        }
        return revs;
    }

    private static long? Long(JsonNode? node) => node?.GetValueKind() == JsonValueKind.Number && node.AsValue().TryGetValue<long>(out var value) ? value : null;

    private static bool? Bool(JsonNode? node) => node?.GetValueKind() switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    private static readonly Regex SafeToken = new("^[A-Za-z0-9._-]{1,32}$", RegexOptions.CultureInvariant);

    /// <summary>A short scalar as text when it cannot hold more than a label; otherwise null.</summary>
    private static string? Token(JsonNode? node)
    {
        var text = node?.GetValueKind() switch
        {
            JsonValueKind.String => node.GetValue<string>(),
            JsonValueKind.Number => node.ToJsonString(),
            _ => null
        };
        return text != null && SafeToken.IsMatch(text) ? text : null;
    }
}

/// <summary>An entry's address (an IP literal or a domain) and port.</summary>
public sealed record ColituGeckoTarget(string Host, int Port);

/// <summary>What the panel said at the last <c>GET /gecko/config</c>.</summary>
public enum ColituGeckoOffer
{
    /// <summary>Not asked yet, or the request failed.</summary>
    Unknown,
    Offered,
    /// <summary>404: Gecko is not offered to this account (everything Gecko is hidden).</summary>
    NotOffered,
    /// <summary>503, or an answer that is not a usable configuration: offered, not available now.</summary>
    Unavailable
}

/// <summary>
/// Adaptive Connect's steps for one connect, in order: the normal servers (every server and transport
/// AC already tries), then Gecko-only, then the Whitelist servers. "whitelist" after "normal" is the
/// switch to the whitelist servers inside the connect once the operator whitelist is found (trigger
/// T1); in whitelist mode the normal servers already are the whitelist rows, so Gecko goes first.
/// </summary>
public static class ColituConnectSteps
{
    public const string Normal = "normal";
    public const string Gecko = ColituGeckoRules.StepName;
    public const string Whitelist = "whitelist";

    /// <param name="geckoPicked">The user picked ColituGecko in the protocol menu (see <see cref="GeckoPicked"/>): Gecko only, no silent switch.</param>
    /// <param name="automatic">Best server; a server picked by hand is never switched to anything else.</param>
    /// <param name="whitelistMode">The operator whitelist is known on this network already.</param>
    /// <param name="offer">The panel's last answer; only "not offered" leaves Gecko out (unknown may still come within <see cref="ColituGeckoRules.ConfigWait"/>).</param>
    public static IReadOnlyList<string> Plan(bool geckoPicked, bool automatic, bool whitelistMode, ColituGeckoOffer offer)
    {
        if (geckoPicked)
        {
            return [Gecko];
        }
        if (!automatic)
        {
            return [Normal];
        }
        var gecko = offer != ColituGeckoOffer.NotOffered;
        if (whitelistMode)
        {
            return gecko ? [Gecko, Whitelist] : [Whitelist];
        }
        return gecko ? [Normal, Gecko, Whitelist] : [Normal, Whitelist];
    }

    /// <summary>The protocol menu's pick is ColituGecko and it counts: not on a Whitelist row, and not when the panel does not offer Gecko (then it is automatic).</summary>
    public static bool GeckoPicked(string? preferredTransport, bool whitelistSelected, ColituGeckoOffer offer) =>
        string.Equals(preferredTransport, ColituGeckoRules.Protocol, StringComparison.Ordinal) && !whitelistSelected && offer != ColituGeckoOffer.NotOffered;

    /// <summary>The step that comes after <paramref name="step"/> in <paramref name="plan"/>; null at the end.</summary>
    public static string? After(IReadOnlyList<string> plan, string step)
    {
        for (var i = 0; i < plan.Count - 1; i++)
        {
            if (plan[i] == step)
            {
                return plan[i + 1];
            }
        }
        return null;
    }
}

/// <summary>
/// The Gecko cool-down per network (memory only): after a failed Gecko attempt, automatic connects on
/// that network skip Gecko for <see cref="ColituGeckoRules.CoolDown"/>. A manual pick ignores it, and a
/// Gecko connection that works ends it.
/// </summary>
public sealed class ColituGeckoCooldown
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _failed = new(StringComparer.Ordinal);

    public void Failed(string network, DateTimeOffset at) => _failed[network] = at;

    public void Succeeded(string network) => _failed.TryRemove(network, out _);

    public bool Active(string network, DateTimeOffset now) =>
        _failed.TryGetValue(network, out var at) && now - at < ColituGeckoRules.CoolDown && now >= at;
}

/// <summary>
/// A configuration as the panel sent it: the exact response bytes (a secret: memory only, never logged)
/// and the <c>X-Gecko-Config-Signature</c> header that covers them.
/// </summary>
public sealed record ColituGeckoSignedConfig(byte[] Body, string Signature)
{
    public override string ToString() => "signed gecko config (redacted)";
}

/// <summary>What the app kept of earlier runs and gives gecko-client back at its start (F-14, F-15).</summary>
public sealed record ColituGeckoFloor(long MinIssued, IReadOnlyDictionary<string, long>? SkinFloor);

/// <summary>
/// <c>GET /gecko/config</c>. <see cref="Signed"/> is the panel's answer as it came: a secret, kept in
/// memory only and never logged (<see cref="ToString"/> leaves it out). <see cref="Reason"/> says why an
/// answer was not usable (no secret in it).
/// </summary>
public sealed record ColituGeckoConfigResult(ColituGeckoOffer Offer, ColituGeckoSignedConfig? Signed, string? Reason = null)
{
    public static ColituGeckoConfigResult NotOffered { get; } = new(ColituGeckoOffer.NotOffered, null);
    public static ColituGeckoConfigResult Unavailable { get; } = new(ColituGeckoOffer.Unavailable, null);

    public override string ToString() => $"gecko {Offer} config {(Signed == null ? "none" : "redacted")}";
}

/// <summary>
/// A parsed gecko-client event; only fields that cannot carry a secret. <see cref="Entry"/> names a
/// server of the fleet: it goes to the panel's report endpoint and never into a log line.
/// </summary>
internal sealed record ColituGeckoEvent(string Kind, long? Anchor = null, bool? Bootstrap = null, string? Reason = null,
    string? Skin = null, string? Rev = null, string? Class = null, long? Bytes = null, string? Bucket = null, string? Entry = null,
    long? LifeMs = null, long? Received = null, long? RttMs = null, long? Goodput = null,
    long? Issued = null, IReadOnlyDictionary<string, long>? Revs = null, long? Sent = null)
{
    public string Describe() => Kind switch
    {
        "session" => $"session (anchor {Anchor?.ToString() ?? "?"}, bootstrap {(Bootstrap?.ToString().ToLowerInvariant() ?? "?")})",
        "need_config" => $"needs a new configuration ({Reason})",
        "grip" => $"grip (skin {Skin ?? "?"}, rev {Rev ?? "?"}, class {Class ?? "?"}, bucket {Bucket ?? "?"}, {Bytes?.ToString() ?? "?"} bytes)",
        // grip_end carries exact figures: only the class is ever described (nothing raw goes to a log).
        "grip_end" => $"grip end (class {Class ?? "?"})",
        "config" => $"configuration accepted (issued {Issued?.ToString() ?? "?"})",
        "config_refused" => $"configuration refused ({Reason ?? "?"}); the last valid one stays",
        "skin_revs" => $"skin revisions ({Revs?.Count ?? 0} skins)",
        _ => Kind
    };
}

/// <summary>Gecko for the next core start: gecko-client's SOCKS port and per-run account, and its full path (TUN direct rule).</summary>
internal sealed record ColituGeckoPlan(string PrimaryIndexId, int SocksPort, string SocksUser, string SocksPassword, string ClientPath);

internal sealed record ColituGeckoLaunch(int SocksPort, string SocksUser, string SocksPassword, bool Standby = false);

/// <summary>A started gecko-client: <see cref="Ready"/> ends with "" (on) or the reason it is off.</summary>
internal sealed record ColituGeckoRun(int ProcessId, ColituGeckoLaunch Launch, Task<string> Ready);

/// <summary>The gecko-client sidecar process (<c>bin/colitu-gecko/gecko-client</c>); one at a time.</summary>
internal sealed class ColituGecko
{
    public static ColituGecko Instance { get; } = new();

    internal static string DefaultPath => Path.Combine(Utils.StartupPath(), "bin", "colitu-gecko", ColituGeckoRules.ExecutableName);

    internal static string ExecutablePath => PathOverride ?? LabPath ?? DefaultPath;

    /// <summary>Tests only: another gecko-client binary.</summary>
    internal static string? PathOverride { get; set; }

    /// <summary>
    /// Lab runs: <c>COLITU_GECKO_CLIENT</c> names another binary. Debug builds only: a release build
    /// never hands the configuration to a program an environment variable chose.
    /// </summary>
    private static string? LabPath =>
#if DEBUG
        Environment.GetEnvironmentVariable("COLITU_GECKO_CLIENT") is { Length: > 0 } path && Path.IsPathFullyQualified(path) ? path : null;
#else
        null;
#endif

    /// <summary>How long gecko-client gets for its session (tests shorten it).</summary>
    internal static TimeSpan Deadline { get; set; } = ColituGeckoRules.ReadyDeadline;

    /// <summary>A gecko-client that was on exited by itself (not stopped): the reason.</summary>
    public event Action<string>? Exited;

    /// <summary>The running gecko-client asked for a new configuration (<c>half_life</c>, <c>bootstrap</c>, <c>expired</c> or <c>other</c>).</summary>
    public event Action<string>? NeedConfig;

    /// <summary>
    /// What gecko-client accepted (<c>config</c>: the highest <c>issued</c>) or holds (<c>skin_revs</c>);
    /// the app keeps both and gives them back at the next start. Either value may be null.
    /// </summary>
    public event Action<long?, IReadOnlyDictionary<string, long>?>? FloorChanged;

    private readonly object _gate = new();
    private Process? _process;
    private int _generation;

    public bool Running
    {
        get
        {
            lock (_gate)
            {
                try
                {
                    return _process is { HasExited: false };
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }
        }
    }

    /// <summary>
    /// Starts gecko-client and writes the SOCKS account and configuration to its standard input
    /// (kept open for <see cref="SendConfig"/>). Never waits: <see cref="ColituGeckoRun.Ready"/> tells
    /// when its session is up, it exited, or it missed the deadline (it is then stopped). Throws when
    /// the configuration is unusable or the process cannot be created.
    /// </summary>
    public ColituGeckoRun Start(ColituGeckoLaunch launch, ColituGeckoSignedConfig config, ColituGeckoFloor? floor, Action<string> log)
    {
        Stop();
        var secrets = ColituGeckoRules.Secrets(launch, config, floor);
        var info = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(ExecutablePath)!
        };
        foreach (var arg in ColituGeckoRules.Arguments(launch))
        {
            info.ArgumentList.Add(arg);
        }
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var watch = Stopwatch.StartNew();
        int generation;
        var listening = false;
        var session = false;
        var lines = 0;
        lock (_gate)
        {
            generation = ++_generation;
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not { Length: > 0 } line) return;
                string entry;
                if (ColituGeckoRules.IsReadyLine(line))
                {
                    listening = true;
                    entry = StripTimestamp(line);
                }
                else if (ColituGeckoRules.ParseEvent(line) is { } evt)
                {
                    entry = evt.Describe();
                    if (evt.Kind == "session" && evt.Bootstrap == false)
                    {
                        session = true;
                    }
                    else if (evt.Kind == "need_config" && IsCurrent(generation))
                    {
                        NeedConfig?.Invoke(evt.Reason ?? "other");
                    }
                    else if (evt.Kind is "config" or "skin_revs" && IsCurrent(generation))
                    {
                        FloorChanged?.Invoke(evt.Issued, evt.Revs);
                    }
                    else if (evt.Kind == "grip")
                    {
                        ColituGeckoReports.Instance.Add(evt, DateTimeOffset.UtcNow);
                    }
                    else if (evt.Kind == "grip_end")
                    {
                        ColituGeckoReports.Instance.AddEnd(evt, DateTimeOffset.UtcNow);
                    }
                }
                else
                {
                    // Anything else may echo configuration: its length only.
                    entry = $"(other output, {line.Length} chars)";
                }
                if (ColituGeckoRules.Verdict(listening, session, false, null, watch.Elapsed) == "")
                {
                    ready.TrySetResult("");
                }
                if (Interlocked.Increment(ref lines) <= 40)
                {
                    log($"ColituGecko | {entry}");
                }
            };
            _ = ColituGeckoReports.Instance.PumpAsync(() => IsCurrent(generation),
                body => ColituApiClient.Instance.PostGeckoReportsAsync(body));
            process.Exited += (_, _) =>
            {
                int? code = null;
                try
                {
                    code = process.ExitCode;
                }
                catch (InvalidOperationException)
                {
                }
                var reason = ColituGeckoRules.Verdict(listening, session, true, code, watch.Elapsed)!;
                if (!ready.TrySetResult(reason) && IsCurrent(generation))
                {
                    // It was on: the service restarts the Gecko-only core or moves on.
                    Exited?.Invoke(reason);
                }
            };
            try
            {
                process.Start();
            }
            catch
            {
                process.Dispose();
                throw;
            }
            try
            {
                process.StandardInput.Write(secrets);
                process.StandardInput.Flush();
            }
            catch
            {
                Kill(process);
                throw;
            }
            _process = process;
        }
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        log($"ColituGecko | gecko-client started (pid {process.Id}, SOCKS 127.0.0.1:{launch.SocksPort})");
        var deadline = Deadline;
        _ = Task.Delay(deadline).ContinueWith(_ =>
        {
            if (ColituGeckoRules.Verdict(listening, session, false, null, watch.Elapsed, deadline) is { Length: > 0 } reason && ready.TrySetResult(reason))
            {
                StopGeneration(generation);
            }
        }, TaskScheduler.Default);
        return new ColituGeckoRun(process.Id, launch, ready.Task);
    }

    /// <summary>Hands the running gecko-client a new configuration; false when none runs or it is unusable.</summary>
    public bool SendConfig(ColituGeckoSignedConfig config)
    {
        var line = ColituGeckoRules.ConfigLine(config);
        lock (_gate)
        {
            if (_process == null)
            {
                return false;
            }
            try
            {
                if (_process.HasExited)
                {
                    return false;
                }
                _process.StandardInput.Write(line);
                _process.StandardInput.Flush();
                return true;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                return false;
            }
        }
    }

    /// <summary>Stops gecko-client (if any): its standard input is closed, then it is ended. Not reported as an exit.</summary>
    public void Stop()
    {
        Process? process;
        lock (_gate)
        {
            _generation++;
            process = _process;
            _process = null;
        }
        if (process != null)
        {
            try
            {
                // Closing standard input asks it to exit.
                process.StandardInput.Close();
                process.WaitForExit(1000);
            }
            catch
            {
                // Already gone.
            }
        }
        Kill(process);
    }

    private bool IsCurrent(int generation)
    {
        lock (_gate)
        {
            return generation == _generation;
        }
    }

    private void StopGeneration(int generation)
    {
        if (IsCurrent(generation))
        {
            Stop();
        }
    }

    private static void Kill(Process? process)
    {
        if (process == null)
        {
            return;
        }
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(2000);
            }
        }
        catch
        {
            // Already gone.
        }
        process.Dispose();
    }

    private static string StripTimestamp(string line) =>
        Regex.Replace(line, @"^\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2}(\.\d+)? ", "");
}
