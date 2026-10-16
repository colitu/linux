using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace v2rayN.Desktop.Services;

/// <summary>
/// The Adaptive Connect decision engine (Go, colitu-csl repo engine/) as a sidecar process:
/// <c>bin/colitu-engine/colitu-engine</c>, one JSON request per line on its stdin, one answer per
/// line on its stdout. It needs no network and keeps no state. The engine decides the automatic
/// server order (<see cref="DecideOrderAsync{T}"/>); the app's own code (same rules, shared test
/// vectors) is the fallback whenever the engine is missing, slow, failing or answers with a set
/// of servers other than the app's candidates. Any failure only turns the engine off for a while.
/// </summary>
internal sealed class ColituEngine : IDisposable
{
    public static ColituEngine Instance { get; } = new();

    /// <summary>An answer slower than this counts as a failure.</summary>
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(2);

    /// <summary>After a failure the engine is left alone this long.</summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private long _nextId;
    private DateTimeOffset _disabledUntil = DateTimeOffset.MinValue;
    private int _mismatches;

    /// <summary>Where the package puts the engine, next to the cores (optional: without it nothing changes).</summary>
    internal static string ExecutablePath => PathOverride ?? Path.Combine(Utils.StartupPath(), "bin", "colitu-engine", OperatingSystem.IsWindows() ? "colitu-engine.exe" : "colitu-engine");

    /// <summary>Tests only: another engine binary.</summary>
    internal static string? PathOverride { get; set; }

    /// <summary>Mismatches logged in this run (each kind of decision is logged a few times at most).</summary>
    public int Mismatches => _mismatches;

    /// <summary>How long a connect waits for the engine's order before it uses the app's own.</summary>
    internal static TimeSpan DecisionBudget { get; set; } = TimeSpan.FromMilliseconds(800);

    /// <summary>
    /// The engine's order of <paramref name="app"/> for <paramref name="op"/> (an array of ids): used
    /// only when it holds exactly the app's candidates (same ids, no more, no fewer, no repeats), so
    /// the engine can reorder but never add a server the app left out (Russia, penalised, hidden) or
    /// drop one. Otherwise, or when no answer comes within <see cref="DecisionBudget"/>, the app's
    /// own order. <paramref name="source"/> says which one was used.
    /// </summary>
    public async Task<(List<T> Order, string Source)> DecideOrderAsync<T>(string op, JsonNode input, List<T> app, Func<T, string?> id)
    {
        if (app.Count < 2)
        {
            return (app, "app");
        }
        var call = CallAsync(op, input);
        if (await Task.WhenAny(call, Task.Delay(DecisionBudget)) != call)
        {
            return (app, "app (engine slow)");
        }
        if (await call is not JsonArray answer)
        {
            return (app, "app (engine unavailable)");
        }
        var byId = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in app)
        {
            if (id(item) is not { Length: > 0 } key || !byId.TryAdd(key, item))
            {
                return (app, "app");
            }
        }
        var order = new List<T>(app.Count);
        foreach (var node in answer)
        {
            if (node?.GetValueKind() != JsonValueKind.String || !byId.Remove(node.GetValue<string>(), out var item))
            {
                return (app, MismatchSource(op, app.Select(id), answer));
            }
            order.Add(item);
        }
        if (byId.Count > 0)
        {
            return (app, MismatchSource(op, app.Select(id), answer));
        }
        return (order, "engine");
    }

    private string MismatchSource(string op, IEnumerable<string?> app, JsonArray engine)
    {
        if (Interlocked.Increment(ref _mismatches) <= 20)
        {
            Logging.SaveLog($"ColituEngine | {op}: engine answered other servers than the app's candidates (app {string.Join(",", app)}, engine {engine.ToJsonString()}); app order used");
        }
        return "app (engine set differs)";
    }

    /// <summary>
    /// Sends <paramref name="op"/> with <paramref name="input"/> and returns the engine's <c>out</c>;
    /// null when the engine is not available, failed or answered with an error.
    /// </summary>
    public async Task<JsonNode?> CallAsync(string op, JsonNode? input)
    {
        if (DateTimeOffset.UtcNow < _disabledUntil || !File.Exists(ExecutablePath))
        {
            return null;
        }
        if (!await _gate.WaitAsync(AnswerTimeout))
        {
            return null;
        }
        try
        {
            var process = EnsureStarted();
            if (process == null)
            {
                return null;
            }
            var id = ++_nextId;
            var line = new JsonObject { ["id"] = id, ["op"] = op, ["in"] = input?.DeepClone() }.ToJsonString();
            await process.StandardInput.WriteLineAsync(line);
            await process.StandardInput.FlushAsync();
            var read = process.StandardOutput.ReadLineAsync();
            if (await Task.WhenAny(read, Task.Delay(AnswerTimeout)) != read || await read is not { } answer)
            {
                Fail($"no answer to {op} within {AnswerTimeout.TotalSeconds:0} s");
                return null;
            }
            var node = JsonNode.Parse(answer);
            if (node?["id"]?.GetValue<long>() != id)
            {
                Fail($"answer out of step for {op}");
                return null;
            }
            if (node["error"]?.GetValue<string>() is { Length: > 0 } error)
            {
                Logging.SaveLog($"ColituEngine | {op}: {error}");
                return null;
            }
            return node["out"]?.DeepClone();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or ObjectDisposedException or System.ComponentModel.Win32Exception)
        {
            Fail($"{op}: {ex.GetType().Name}");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Shadow comparison: logs (at most a few times per kind) when the engine's answer to
    /// <paramref name="op"/> differs from what the app decided. Never throws, never blocks the caller
    /// for long (run it without awaiting).
    /// </summary>
    public async Task CompareAsync(string op, JsonNode input, JsonNode? appDecision)
    {
        try
        {
            var engine = await CallAsync(op, input);
            if (engine == null && DateTimeOffset.UtcNow < _disabledUntil)
            {
                return;
            }
            if (JsonNode.DeepEquals(engine, appDecision))
            {
                return;
            }
            if (Interlocked.Increment(ref _mismatches) <= 20)
            {
                Logging.SaveLog($"ColituEngine | {op} differs: app {appDecision?.ToJsonString() ?? "null"}, engine {engine?.ToJsonString() ?? "null"}");
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"ColituEngine | compare {op}: {ex.GetType().Name}");
        }
    }

    /// <summary>The <c>rank</c> request for the automatic order (engine.RankInput on the Go side).</summary>
    internal static JsonObject RankRequest(IEnumerable<ColituVpnServer> servers, IReadOnlyDictionary<string, ColituPingSample> pings,
        ColituAdaptiveMemory memory, string networkKey, string? clientCountry, DateTimeOffset now)
    {
        var list = servers.Where(server => !string.IsNullOrWhiteSpace(server.Id)).ToList();
        var serverArray = new JsonArray();
        var pingObject = new JsonObject();
        var penalized = new JsonArray();
        foreach (var server in list)
        {
            serverArray.Add(new JsonObject
            {
                ["id"] = server.Id,
                ["country"] = server.CountryCode,
                ["available"] = server.Available && !server.Locked,
                ["multihop"] = server.IsMultihop
            });
            if (pings.TryGetValue(server.Id!, out var sample))
            {
                pingObject[server.Id!] = new JsonObject
                {
                    ["ms"] = sample.Ms,
                    ["at_ms"] = sample.MeasuredAt.ToUnixTimeMilliseconds(),
                    ["network"] = sample.NetworkKey
                };
            }
            if (memory.PenalizedAt(networkKey, server.Id, now) is { } at)
            {
                penalized.Add(new JsonObject { ["id"] = server.Id, ["at_ms"] = at.ToUnixTimeMilliseconds() });
            }
        }
        return new JsonObject
        {
            ["now_ms"] = now.ToUnixTimeMilliseconds(),
            ["network"] = networkKey,
            ["client_country"] = clientCountry ?? "",
            ["last_good"] = memory.LastGoodServer(networkKey, now) ?? "",
            ["servers"] = serverArray,
            ["pings"] = pingObject,
            ["penalized"] = penalized
        };
    }

    private Process? EnsureStarted()
    {
        if (_process is { HasExited: false } running)
        {
            return running;
        }
        _process?.Dispose();
        var info = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(ExecutablePath)!
        };
        _process = Process.Start(info);
        if (_process != null)
        {
            Logging.SaveLog($"ColituEngine | started (pid {_process.Id})");
        }
        return _process;
    }

    private void Fail(string reason)
    {
        Logging.SaveLog($"ColituEngine | {reason}; off for {RetryAfter.TotalMinutes:0} minutes");
        _disabledUntil = DateTimeOffset.UtcNow + RetryAfter;
        Stop();
    }

    private void Stop()
    {
        try
        {
            if (_process is { HasExited: false } process)
            {
                process.Kill();
            }
        }
        catch
        {
            // Already gone.
        }
        _process?.Dispose();
        _process = null;
    }

    public void Dispose()
    {
        Stop();
        _gate.Dispose();
    }
}
