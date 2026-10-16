using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace v2rayN.Desktop.Services;

/// <summary>
/// The Adaptive Connect decision engine (Go, colitu-csl repo engine/) as a sidecar process:
/// <c>bin/colitu-engine/colitu-engine</c>, one JSON request per line on its stdin, one answer per
/// line on its stdout. It needs no network and keeps no state. Shadow mode for now: the app still
/// decides with its own code (same rules, shared test vectors) and logs where the engine would
/// have decided otherwise. Any failure (missing file, crash, slow answer) only turns the engine off
/// for a while; nothing waits on it.
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
