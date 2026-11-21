using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace v2rayN.Desktop.Services;

/// <summary>The accepted content of a signed endpoint list.</summary>
/// <param name="Relays">The list's <c>relays</c>; null when the key is absent (then the built-in relays apply).</param>
public sealed record ColituEndpointData(long Version, IReadOnlyList<string> Api, IReadOnlyList<string> Web, IReadOnlyList<string> Lists,
    IReadOnlyList<ColituApiRelay>? Relays = null);

/// <summary>
/// API failover through a signed endpoint list (spec: panel repo deploy/endpoints/APP_SPEC.md).
/// The list names the API bases, web origins and list URLs; it is accepted only when its ECDSA P-256
/// signature verifies against the key built into the app and its version is newer than the stored
/// one. The last accepted list and the API base that last worked are stored next to the app's other
/// settings; the built-in values are the fallback. A request goes to the base that last worked
/// first and moves on only after a network-level failure (never after any HTTP response).
/// </summary>
public sealed class ColituEndpointList
{
    public const string KeyId = "e1";

    /// <summary>
    /// Mirror origins (https), given by the build as the "ColituMirrors" assembly metadata (a
    /// comma-separated list). They are not in the public source: release builds set the
    /// ColituMirrors environment variable. Builds without them use only the main domain.
    /// </summary>
    public static readonly IReadOnlyList<string> BuiltInMirrors = ParseMirrors(
        typeof(ColituEndpointList).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "ColituMirrors")?.Value);

    /// <summary>Built-in API relays, the "ColituRelays" assembly metadata (<c>id|host:port|sni|region|wl;...</c>); not in the public source.</summary>
    public static readonly IReadOnlyList<ColituApiRelay> BuiltInRelays = ColituApiRelays.ParseBuildValue(
        typeof(ColituEndpointList).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "ColituRelays")?.Value);

    /// <summary>API bases, in order, used when no list was accepted yet.</summary>
    public static readonly IReadOnlyList<string> BuiltInApi = BuildApi(BuiltInMirrors);

    /// <summary>Where the signed list is fetched from, in order, until a list was accepted.</summary>
    public static readonly IReadOnlyList<string> BuiltInLists = BuildLists(BuiltInMirrors);

    /// <summary>Keeps only https:// origins of a comma-separated list and trims the trailing slash.</summary>
    internal static IReadOnlyList<string> ParseMirrors(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsPlainHttpsUrl)
            .Select(origin => origin.TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    internal static IReadOnlyList<string> BuildApi(IEnumerable<string> mirrors) =>
        new[] { ColituAuthService.DefaultApiBaseUrl }.Concat(mirrors.Select(origin => origin + "/capi/v1")).ToList();

    internal static IReadOnlyList<string> BuildLists(IEnumerable<string> mirrors) =>
        new[] { "https://colitu.com/downloads/endpoints.json" }.Concat(mirrors.Select(origin => origin + "/downloads/endpoints.json")).ToList();

    private const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEhv55BVmvEisYIRhkejn+4Leuf0KW
        6jrrRvSL4cu4W09jUwc6HTIcq+YUSG1kJ5AF7qK7PlBtf+xRMTQMGM+xPg==
        -----END PUBLIC KEY-----
        """;

    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);
    private const int MaxListBytes = 64 * 1024;

    private static readonly Lazy<ColituEndpointList> LazyInstance = new(() => new ColituEndpointList(StatePath()));
    public static ColituEndpointList Instance => LazyInstance.Value;

    private readonly object _gate = new();
    private readonly string? _statePath;
    private readonly IReadOnlyList<string> _builtInApi;
    private readonly IReadOnlyList<string> _builtInLists;
    private readonly IReadOnlyList<ColituApiRelay> _builtInRelays;
    private readonly ColituApiPathHealer _healer = new();
    private int _healStarted;
    private ColituEndpointData? _data;
    private string? _raw;
    private string? _lastWorked;
    private int _refreshStarted;
    private HttpClient? _http;

    /// <param name="statePath">File that keeps the accepted list and the working base; null keeps nothing on disk.</param>
    /// <param name="mirrors">Mirror origins; null takes the ones embedded by the build.</param>
    /// <param name="relays">Built-in relays; null takes the ones of the build.</param>
    internal ColituEndpointList(string? statePath, IEnumerable<string>? mirrors = null, IEnumerable<ColituApiRelay>? relays = null)
    {
        _statePath = statePath;
        _builtInRelays = relays?.ToList() ?? BuiltInRelays;
        var origins = mirrors == null ? BuiltInMirrors : ParseMirrors(string.Join(",", mirrors));
        _builtInApi = mirrors == null ? BuiltInApi : BuildApi(origins);
        _builtInLists = mirrors == null ? BuiltInLists : BuildLists(origins);
        Load();
    }

    private static string StatePath() => ColituHardening.UserConfigPath("colitu-endpoints.json");

    /// <summary>Version of the stored list, null when none.</summary>
    public long? StoredVersion
    {
        get { lock (_gate) return _data?.Version; }
    }

    // ---- verification ----------------------------------------------------------------------

    /// <summary>
    /// Checks a signed list (key id, signature over the decoded payload bytes, payload shape,
    /// version newer than <paramref name="storedVersion"/>). Returns null and a reason when it is not acceptable.
    /// </summary>
    public static ColituEndpointData? Verify(string? raw, long? storedVersion, out string reason)
    {
        reason = "";
        try
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                reason = "empty";
                return null;
            }
            using var envelope = JsonDocument.Parse(raw);
            var root = envelope.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryString(root, "key_id", out var keyId)
                || !TryString(root, "payload", out var payloadText)
                || !TryString(root, "signature", out var signatureText))
            {
                reason = "malformed envelope";
                return null;
            }
            if (!string.Equals(keyId, KeyId, StringComparison.Ordinal))
            {
                reason = "unknown key id";
                return null;
            }

            var payload = Convert.FromBase64String(payloadText!);
            var signature = Convert.FromBase64String(signatureText!);
            using (var ecdsa = ECDsa.Create())
            {
                ecdsa.ImportFromPem(PublicKeyPem);
                if (!ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                {
                    reason = "bad signature";
                    return null;
                }
            }

            using var doc = JsonDocument.Parse(payload);
            var body = doc.RootElement;
            if (body.ValueKind != JsonValueKind.Object
                || !body.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number
                || !schema.TryGetInt64(out var schemaValue) || schemaValue != 1
                || !body.TryGetProperty("version", out var versionElement) || versionElement.ValueKind != JsonValueKind.Number
                || !versionElement.TryGetInt64(out var version))
            {
                reason = "bad payload";
                return null;
            }
            var api = ReadUrls(body, "api", trimSlash: true);
            var web = ReadUrls(body, "web", trimSlash: true);
            var lists = ReadUrls(body, "lists", trimSlash: false);
            if (api == null || web == null || lists == null)
            {
                reason = "bad url arrays";
                return null;
            }
            if (storedVersion is { } stored && version <= stored)
            {
                reason = "not newer than the stored list";
                return null;
            }
            // Optional; unknown keys are ignored, a bad relay entry is skipped (not the whole list).
            IReadOnlyList<ColituApiRelay>? relays = body.TryGetProperty("relays", out var relayArray)
                ? ColituApiRelays.ParseArray(relayArray)
                : null;
            return new ColituEndpointData(version, api, web, lists, relays);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException or ArgumentException or InvalidOperationException)
        {
            reason = ex.GetType().Name;
            return null;
        }
    }

    private static bool TryString(JsonElement root, string name, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = element.GetString();
        return value != null;
    }

    private static List<string>? ReadUrls(JsonElement body, string name, bool trimSlash)
    {
        if (!body.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var result = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } text || !IsPlainHttpsUrl(text))
            {
                return null;
            }
            result.Add(trimSlash ? text.TrimEnd('/') : text);
        }
        return result.Count == 0 ? null : result;
    }

    /// <summary>https:// URL without user info, query or fragment.</summary>
    internal static bool IsPlainHttpsUrl(string text)
    {
        if (text.Contains('?') || text.Contains('#') || text.Any(char.IsWhiteSpace)
            || !Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return false;
        }
        return uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) && !string.IsNullOrEmpty(uri.Host);
    }

    // ---- ordering --------------------------------------------------------------------------

    /// <summary>The base that last worked first, then the others in list order, without duplicates.</summary>
    public static IReadOnlyList<string> Order(IEnumerable<string> bases, string? lastWorked)
    {
        var list = new List<string>();
        foreach (var item in bases)
        {
            var normalized = Normalize(item);
            if (normalized.Length > 0 && !list.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(normalized);
            }
        }
        var last = lastWorked == null ? null : Normalize(lastWorked);
        var index = last == null ? -1 : list.FindIndex(item => string.Equals(item, last, StringComparison.OrdinalIgnoreCase));
        if (index > 0)
        {
            var first = list[index];
            list.RemoveAt(index);
            list.Insert(0, first);
        }
        return list;
    }

    private static string Normalize(string? value) => (value ?? "").Trim().TrimEnd('/');

    /// <summary>Set by the VPN service: the client's country and whitelist mode, for the base order.</summary>
    public static Func<ColituRouteContext> RouteContextProvider { get; set; } = () => ColituRouteContext.None;

    private static ColituRouteContext CurrentContext()
    {
        try
        {
            return RouteContextProvider() ?? ColituRouteContext.None;
        }
        catch
        {
            return ColituRouteContext.None;
        }
    }

    /// <summary>The relays of the accepted list when it has the key, else the built-in ones.</summary>
    public IReadOnlyList<ColituApiRelay> Relays()
    {
        lock (_gate)
        {
            return _data?.Relays ?? _builtInRelays;
        }
    }

    /// <summary>The relay of a <c>relay:&lt;id&gt;</c> base, or null.</summary>
    public ColituApiRelay? Relay(string baseUrl) =>
        ColituApiRelays.IsRelay(baseUrl) ? Relays().FirstOrDefault(r => r.Id == baseUrl[ColituApiRelays.BasePrefix.Length..]) : null;

    /// <summary>The order without the last working base: api bases, mirrors and relays as <paramref name="ctx"/> wants them.</summary>
    public IReadOnlyList<string> NaturalOrder(ColituRouteContext? ctx = null)
    {
        lock (_gate)
        {
            return ColituApiRelays.RouteOrder(_data?.Api ?? _builtInApi, _data?.Relays ?? _builtInRelays, ctx ?? CurrentContext());
        }
    }

    /// <summary>The bases for the next request (API bases and relays), the one that last worked first.</summary>
    public IReadOnlyList<string> Bases(ColituRouteContext? ctx = null)
    {
        var natural = NaturalOrder(ctx);
        lock (_gate)
        {
            return Order(natural, _lastWorked);
        }
    }

    /// <summary>The base to build request URLs with: the first real URL of <see cref="Bases"/> (never a relay).</summary>
    public string UrlBase() => Bases().FirstOrDefault(b => !ColituApiRelays.IsRelay(b)) ?? ColituAuthService.DefaultApiBaseUrl;

    /// <summary>The base that last worked, or null.</summary>
    public string? LastWorking()
    {
        lock (_gate)
        {
            return _lastWorked;
        }
    }

    /// <summary>primary | mirror | relay:&lt;id&gt; for reports (never an address).</summary>
    public string ApiPath()
    {
        var current = LastWorking();
        if (current == null)
        {
            return "primary";
        }
        if (ColituApiRelays.IsRelay(current))
        {
            return current;
        }
        var first = NaturalOrder(ColituRouteContext.None).FirstOrDefault(b => !ColituApiRelays.IsRelay(b));
        return string.Equals(current, first, StringComparison.OrdinalIgnoreCase) ? "primary" : "mirror";
    }

    /// <summary>Every API base the app may talk to (built-in and accepted list), in no particular order.</summary>
    public IReadOnlyList<string> KnownBases()
    {
        lock (_gate)
        {
            return Order(_builtInApi.Concat(_data?.Api ?? []), null);
        }
    }

    /// <summary>The URLs the list is fetched from: the accepted list's, else the built-in ones.</summary>
    public IReadOnlyList<string> ListUrls()
    {
        lock (_gate)
        {
            return (_data?.Lists ?? _builtInLists).ToList();
        }
    }

    /// <summary>All API bases and list URLs (built-in and accepted): their hosts must stay reachable under the kill switch.</summary>
    public IEnumerable<string> PinnedUrls()
    {
        lock (_gate)
        {
            var relays = _builtInRelays.Concat(_data?.Relays ?? []).Select(r => $"https://{r.Host}:{r.Port}/");
            return _builtInApi.Concat(_builtInLists).Concat(_data?.Api ?? []).Concat(_data?.Lists ?? []).Concat(relays).ToList();
        }
    }

    // ---- state -----------------------------------------------------------------------------

    /// <summary>Stores a signed list when it is acceptable and newer than the one held.</summary>
    public bool TryAccept(string? raw)
    {
        var data = Verify(raw, StoredVersion, out var reason);
        if (data == null)
        {
            Log($"endpoint list ignored: {reason}");
            return false;
        }
        lock (_gate)
        {
            // A parallel refresh may have stored a newer one in the meantime.
            if (_data != null && data.Version <= _data.Version)
            {
                return false;
            }
            _data = data;
            _raw = raw;
            Save();
        }
        Log($"endpoint list {data.Version} accepted");
        return true;
    }

    /// <summary>Remembers the base that answered, so the next request starts there.</summary>
    public void RememberWorking(string baseUrl)
    {
        var normalized = Normalize(baseUrl);
        lock (_gate)
        {
            if (string.Equals(_lastWorked, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            _lastWorked = normalized;
            Save();
        }
    }

    private void Load()
    {
        if (_statePath == null || !File.Exists(_statePath))
        {
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_statePath));
            var root = doc.RootElement;
            // Verified again on load: the stored text is not trusted just because it is on disk.
            if (TryString(root, "raw", out var raw) && Verify(raw, null, out _) is { } data)
            {
                _data = data;
                _raw = raw;
            }
            if (TryString(root, "last_base", out var last) && !string.IsNullOrWhiteSpace(last))
            {
                _lastWorked = Normalize(last);
            }
        }
        catch
        {
            // Unreadable state: start from the built-in values.
        }
    }

    private void Save()
    {
        if (_statePath == null)
        {
            return;
        }
        try
        {
            var directory = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            var json = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["raw"] = _raw,
                ["version"] = _data?.Version,
                ["last_base"] = _lastWorked
            });
            var temp = _statePath + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, _statePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log($"could not save endpoint state: {ex.Message}");
        }
    }

    // ---- refresh ---------------------------------------------------------------------------

    /// <summary>Starts the background refresh: now (never delaying startup), then every 6 hours. Once per process.</summary>
    public void StartBackgroundRefresh()
    {
        if (Interlocked.Exchange(ref _refreshStarted, 1) != 0)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await RefreshAsync(CancellationToken.None);
                }
                catch
                {
                    // Silent: the built-in or last accepted list stays in use.
                }
                await Task.Delay(RefreshInterval);
            }
        });
    }

    /// <summary>
    /// Fetches each list URL in order (10 s each), then the list through each relay
    /// (<c>relay:&lt;id&gt;/downloads/endpoints.json</c>), and stops at the first file that is accepted.
    /// </summary>
    public async Task<bool> RefreshAsync(CancellationToken token, Func<string, CancellationToken, Task<string?>>? fetch = null)
    {
        fetch ??= FetchAsync;
        var urls = ListUrls().Concat(Relays().Select(r => r.Base + ListPath)).ToList();
        foreach (var url in urls)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(FetchTimeout);
                var text = await fetch(url, timeout.Token);
                if (text != null && TryAccept(text))
                {
                    return true;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Network errors are silent.
            }
        }
        return false;
    }

    private const string ListPath = "/downloads/endpoints.json";
    private const string HealthPath = "/health";

    private async Task<string?> FetchAsync(string url, CancellationToken token)
    {
        if (ColituApiRelays.IsRelay(url))
        {
            var slash = url.IndexOf('/');
            if (slash < 0 || Relay(url[..slash]) is not { } relay)
            {
                return null;
            }
            using var viaRelay = await ColituApiRelays.ClientFor(relay).GetAsync(ColituApiRelays.RelayUri(url[slash..]), token);
            return viaRelay.IsSuccessStatusCode && viaRelay.Content.Headers.ContentLength is null or <= MaxListBytes
                ? await viaRelay.Content.ReadAsStringAsync(token)
                : null;
        }
        var http = _http ??= new HttpClient(ColituCertPins.Pin(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            ConnectCallback = ColituPinnedHosts.ConnectAsync
        }))
        {
            Timeout = FetchTimeout,
            MaxResponseContentBufferSize = MaxListBytes
        };
        using var response = await http.GetAsync(url, token);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(token) : null;
    }

    // ---- self-heal -------------------------------------------------------------------------

    /// <summary>
    /// Probes the bases in order with GET /health and makes the first one that answers the working
    /// base. <paramref name="all"/> = false: only the bases before the current one (back to an earlier
    /// path); true (network change): from the start, current included. Returns the new base or null.
    /// </summary>
    public async Task<string?> HealAsync(ColituRouteContext ctx, bool all, Func<string, CancellationToken, Task<bool>> probe, CancellationToken token)
    {
        var current = LastWorking();
        foreach (var candidate in NaturalOrder(ctx))
        {
            token.ThrowIfCancellationRequested();
            if (!all && string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            bool ok;
            try
            {
                ok = await probe(candidate, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                ok = false;
            }
            if (ok)
            {
                RememberWorking(candidate);
                return candidate;
            }
        }
        return null;
    }

    /// <summary>True when a periodic heal run is due (off the first base for <see cref="ColituApiRelays.HealInterval"/>).</summary>
    internal bool HealDue(DateTimeOffset now, ColituRouteContext ctx)
    {
        var current = LastWorking();
        var first = NaturalOrder(ctx).FirstOrDefault();
        return _healer.Claim(now, current != null && !string.Equals(current, first, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>GET /health of a base (origin of a URL base, or through a relay), 2 s; true on a 2xx.</summary>
    private async Task<bool> ProbeAsync(string baseUrl, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ColituApiRelays.ProbeTimeout);
        if (Relay(baseUrl) is { } relay)
        {
            using var viaRelay = await ColituApiRelays.ClientFor(relay).GetAsync(ColituApiRelays.RelayUri(HealthPath), timeout.Token);
            return viaRelay.IsSuccessStatusCode;
        }
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }
        var http = _http ??= new HttpClient(ColituCertPins.Pin(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            ConnectCallback = ColituPinnedHosts.ConnectAsync
        }))
        {
            Timeout = FetchTimeout,
            MaxResponseContentBufferSize = MaxListBytes
        };
        using var response = await http.GetAsync(new Uri(uri.GetLeftPart(UriPartial.Authority) + HealthPath), timeout.Token);
        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// Starts the self-heal: every minute it checks whether the client has worked off its first base
    /// for 10 minutes and then tries the earlier bases again; a network change chooses the path from the
    /// start and refreshes the list through it. Once per process.
    /// </summary>
    public void StartSelfHeal()
    {
        if (Interlocked.Exchange(ref _healStarted, 1) != 0)
        {
            return;
        }
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) => _ = OnNetworkChangedAsync();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMinutes(1));
                try
                {
                    var ctx = CurrentContext();
                    if (HealDue(DateTimeOffset.UtcNow, ctx) && await HealAsync(ctx, all: false, ProbeAsync, CancellationToken.None) is { } back)
                    {
                        Log($"API path back to an earlier base ({(ColituApiRelays.IsRelay(back) ? back : HostOf(back))})");
                    }
                }
                catch
                {
                    // Silent: the current path stays.
                }
            }
        });
    }

    private int _networkHealRunning;

    private async Task OnNetworkChangedAsync()
    {
        if (Interlocked.Exchange(ref _networkHealRunning, 1) != 0)
        {
            return;
        }
        try
        {
            // Let the new network settle (addresses, routes) before probing it.
            await Task.Delay(TimeSpan.FromSeconds(3));
            _healer.Reset();
            await HealAsync(CurrentContext(), all: true, ProbeAsync, CancellationToken.None);
            await RefreshAsync(CancellationToken.None);
        }
        catch
        {
            // Silent.
        }
        finally
        {
            Interlocked.Exchange(ref _networkHealRunning, 0);
        }
    }

    // ---- failover --------------------------------------------------------------------------

    /// <summary>
    /// True when <paramref name="ex"/> is a network-level failure before any HTTP response (DNS,
    /// refused, timeout, TLS handshake, reset or closed). For a non-GET request only failures that
    /// happen while connecting count: once the body may have been sent it is not sent again.
    /// </summary>
    public static bool ShouldFailover(Exception ex, bool isGet)
    {
        switch (ex)
        {
            case TaskCanceledException:
                // The client timeout (the caller's own cancellation is handled before).
                return isGet;
            case HttpRequestException http:
                return IsConnectPhase(http) || isGet;
            case SocketException or AuthenticationException or IOException:
                return isGet;
            default:
                return false;
        }
    }

    private static bool IsConnectPhase(HttpRequestException ex)
    {
        if (ex.HttpRequestError is HttpRequestError.NameResolutionError
            or HttpRequestError.ConnectionError
            or HttpRequestError.SecureConnectionError
            or HttpRequestError.ProxyTunnelError)
        {
            return true;
        }
        for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
        {
            if (inner is AuthenticationException)
            {
                return true;
            }
            if (inner is SocketException socket && socket.SocketErrorCode is SocketError.ConnectionRefused
                or SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData
                or SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.TimedOut)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Sends through <paramref name="bases"/> in order, each at most once. A network-level failure
    /// (per <paramref name="shouldFailover"/>) moves to the next base; any response, also 4xx/5xx,
    /// is returned and its base reported through <paramref name="onWorked"/>. The last error is thrown
    /// when no base answered.
    /// </summary>
    public static async Task<HttpResponseMessage> SendWithFailoverAsync(
        IReadOnlyList<string> bases,
        Func<string, Task<HttpResponseMessage>> send,
        Func<Exception, bool> shouldFailover,
        Action<string>? onWorked,
        CancellationToken token)
    {
        Exception? last = null;
        for (var i = 0; i < bases.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var response = await send(bases[i]);
                onWorked?.Invoke(bases[i]);
                return response;
            }
            catch (Exception ex) when (!token.IsCancellationRequested && shouldFailover(ex))
            {
                last = ex;
                Log($"API base {HostOf(bases[i])} failed ({ex.GetType().Name}); trying the next one");
            }
        }
        throw last ?? new HttpRequestException("No API base configured");
    }

    /// <summary>Replaces the known API base at the start of <paramref name="uri"/> with <paramref name="target"/>; other URLs stay.</summary>
    public static Uri Rebase(Uri uri, IEnumerable<string> knownBases, string target)
    {
        var text = uri.AbsoluteUri;
        foreach (var known in knownBases)
        {
            var prefix = Normalize(known);
            if (prefix.Length == 0 || !text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var rest = text[prefix.Length..];
            if (rest.Length == 0 || rest[0] is '/' or '?')
            {
                return new Uri(Normalize(target) + rest);
            }
        }
        return uri;
    }

    private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    private static void Log(string message)
    {
        try
        {
            Logging.SaveLog($"ColituEndpointList | {message}");
        }
        catch
        {
            // Logging must never break a request.
        }
    }
}
