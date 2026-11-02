using System.Text.Json.Serialization;

namespace v2rayN.Desktop.Services;

/// <summary>
/// Adaptive Connect 4 decision model ("Colitu LM"): an exact port of the panel's reference
/// (colitu-panel internal/acmodel, docs/adaptive-connect-4.md "Colitu LM reference"). Every app
/// repeats the same arithmetic and must reproduce docs/ac-model-vectors.json to 1e-9, so nothing
/// here may be "improved": only +, -, *, /, ln, exp2, sqrt and cos. Pure logic, no I/O.
/// </summary>
public static class ColituLm
{
    /// <summary>The model file's <c>version</c>; any other version keeps the AC 3 rules.</summary>
    public const int FormatVersion = 1;

    public const double DefaultExplore = 0.05;
    public const double DefaultHalflifeH = 72.0;
    public const double DefaultLatencyCapMs = 1000.0;

    /// <summary>A model fetched longer ago than this is "stale": still used, and a refetch is tried.</summary>
    public const double StaleAfterH = 6.0;

    /// <summary>Priors key of server-level evidence (probes without a protocol, server-hint votes).</summary>
    public const string AnyProtocol = "*";

    public const string EngineAc3 = "ac3";
    public const string EngineAc4 = "ac4";

    /// <summary>The weight of an outcome <paramref name="ageH"/> hours old: 2^(−ageH / halflifeH).</summary>
    public static double Decay(double ageH, double halflifeH)
    {
        if (halflifeH <= 0)
        {
            return 1;
        }
        if (ageH < 0)
        {
            ageH = 0;
        }
        return double.Exp2(-ageH / halflifeH);
    }

    internal static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;

    private static double B01(bool value) => value ? 1 : 0;

    /// <summary>low 0, medium 0.5, high 1, unknown 0.</summary>
    public static double LoadFeature(string? load) => load switch
    {
        "medium" => 0.5,
        "high" => 1,
        _ => 0
    };

    /// <summary>1 − ms / cap, clamped to 0..1; unknown is 0.</summary>
    public static double LatencyFeature(double? ms, double capMs)
    {
        if (ms is not { } value || value < 0)
        {
            return 0;
        }
        if (capMs <= 0)
        {
            capMs = DefaultLatencyCapMs;
        }
        return Clamp01(1 - value / capMs);
    }

    /// <summary>The panel prior: the protocol's own entry, else the server-level "*", else Beta(1, 1).</summary>
    public static (double Alpha, double Beta) PriorFor(ColituLmModel? model, string? country, string? nodeId, string? protocol)
    {
        if (model?.Priors != null
            && model.Priors.TryGetValue((country ?? "").ToUpperInvariant(), out var nodes) && nodes != null
            && nodes.TryGetValue((nodeId ?? "").ToLowerInvariant(), out var protocols) && protocols != null)
        {
            if (protocols.TryGetValue(protocol ?? "", out var own) && Usable(own))
            {
                return (own[0], own[1]);
            }
            if (protocols.TryGetValue(AnyProtocol, out var any) && Usable(any))
            {
                return (any[0], any[1]);
            }
        }
        return (1, 1);

        static bool Usable(double[]? pair) => pair is { Length: >= 2 } && pair[0] > 0 && pair[1] > 0;
    }

    /// <summary>The protocol fit for the network type, 0.5 when unknown.</summary>
    public static double Fit(ColituLmModel? model, string? networkType, string? protocol)
    {
        if (model?.ProtocolFit != null
            && model.ProtocolFit.TryGetValue(networkType ?? "", out var protocols) && protocols != null
            && protocols.TryGetValue(protocol ?? "", out var value))
        {
            return Clamp01(value);
        }
        return 0.5;
    }

    /// <summary>Enters in one country and exits in another (a bridge RU → abroad).</summary>
    public static bool IsBridge(ColituLmCandidate candidate)
    {
        var entry = (candidate.EntryCountry ?? "").ToUpperInvariant();
        var exit = (candidate.ExitCountry ?? "").ToUpperInvariant();
        return entry.Length > 0 && exit.Length > 0 && entry != exit;
    }

    /// <summary>Traffic leaves in Russia: never chosen automatically outside whitelist mode (the 2026-10-10 rule).</summary>
    public static bool ExitsInRussia(ColituLmCandidate candidate) =>
        string.Equals(candidate.ExitCountry, "RU", StringComparison.OrdinalIgnoreCase);

    internal static string CandidateKey(ColituLmCandidate candidate) =>
        (candidate.NodeId ?? "").ToLowerInvariant() + "\0" + (candidate.Protocol ?? "");

    /// <summary>Scores the candidates and picks one (see the reference for every rule).</summary>
    public static ColituLmDecision Decide(ColituLmInput input)
    {
        var model = input.Model;
        if (!input.EngineEnabled || model == null || model.Version != FormatVersion)
        {
            return new ColituLmDecision { Engine = EngineAc3 };
        }
        var candidates = input.Candidates ?? [];
        var halflife = model.HalflifeH;
        if (halflife <= 0)
        {
            halflife = DefaultHalflifeH;
        }
        var decision = new ColituLmDecision
        {
            Engine = EngineAc4,
            Stale = input.ModelAgeH > StaleAfterH,
            Candidates = new List<ColituLmScored>(candidates.Count),
            Ranking = []
        };

        // Own feature: this device's decayed successes and failures with the server on this
        // network, over all its protocols.
        var ownOk = new Dictionary<string, double>(StringComparer.Ordinal);
        var ownFail = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var node = (candidate.NodeId ?? "").ToLowerInvariant();
            foreach (var outcome in candidate.History ?? [])
            {
                var target = outcome.Ok ? ownOk : ownFail;
                target[node] = target.GetValueOrDefault(node) + Decay(outcome.AgeH, halflife);
            }
        }

        var whitelistAny = false;
        if (input.WhitelistMode)
        {
            whitelistAny = candidates.Any(candidate => candidate.WhitelistOk);
            decision.WhitelistNone = !whitelistAny;
        }
        var useWhitelist = input.WhitelistMode && whitelistAny;

        var w = model.Weights ?? new ColituLmWeights();
        foreach (var candidate in candidates)
        {
            var node = (candidate.NodeId ?? "").ToLowerInvariant();
            var (priorAlpha, priorBeta) = PriorFor(model, input.Country, candidate.NodeId, candidate.Protocol);
            var ok = ownOk.GetValueOrDefault(node);
            var fail = ownFail.GetValueOrDefault(node);
            var f = new ColituLmFeatures
            {
                Latency = LatencyFeature(candidate.LatencyMs, model.LatencyCapMs),
                Own = (ok + 1) / (ok + fail + 2),
                Prior = priorAlpha / (priorAlpha + priorBeta),
                Hint = B01(candidate.HintBlocked),
                Recommended = B01(candidate.Recommended),
                Load = LoadFeature(candidate.Load),
                ProtocolFit = Fit(model, input.NetworkType, candidate.Protocol),
                Whitelist = B01(input.WhitelistMode && candidate.WhitelistOk)
            };
            double alpha = priorAlpha, beta = priorBeta;
            foreach (var outcome in candidate.History ?? [])
            {
                if (outcome.Ok)
                {
                    alpha += Decay(outcome.AgeH, halflife);
                }
                else
                {
                    beta += Decay(outcome.AgeH, halflife);
                }
            }
            var logOdds = Math.Log(alpha) - Math.Log(beta);
            var score = w.Latency * f.Latency + w.Own * f.Own + w.Prior * f.Prior + w.Hint * f.Hint + w.Recommended * f.Recommended
                + w.Load * f.Load + w.ProtocolFit * f.ProtocolFit + w.Whitelist * f.Whitelist + logOdds;
            var scored = new ColituLmScored { Features = f, Alpha = alpha, Beta = beta, LogOdds = logOdds, Score = score };
            if (useWhitelist)
            {
                scored.Eligible = candidate.WhitelistOk;
                scored.Tier = IsBridge(candidate) ? 0 : 1;
            }
            else
            {
                scored.Eligible = !ExitsInRussia(candidate);
            }
            decision.Candidates.Add(scored);
        }

        int Compare(int a, int b)
        {
            var x = decision.Candidates[a];
            var y = decision.Candidates[b];
            if (x.Tier != y.Tier)
            {
                return x.Tier.CompareTo(y.Tier);
            }
            if (x.Score != y.Score)
            {
                return y.Score.CompareTo(x.Score);
            }
            return string.CompareOrdinal(CandidateKey(candidates[a]), CandidateKey(candidates[b]));
        }

        // LINQ OrderBy is stable, like sort.SliceStable.
        decision.Ranking = Enumerable.Range(0, candidates.Count)
            .Where(i => decision.Candidates[i].Eligible)
            .OrderBy(i => i, Comparer<int>.Create(Compare))
            .ToList();
        if (decision.Ranking.Count == 0)
        {
            return decision;
        }
        var choice = decision.Ranking[0];

        // Exploration: one uniform draw decides; when it explores, every candidate of the best tier
        // gets a Thompson sample in key order and the highest sample wins.
        var rng = new ColituLmRng(input.Seed);
        if (rng.NextFloat() < model.Explore)
        {
            decision.Explored = true;
            var top = decision.Candidates[choice].Tier;
            var pool = decision.Ranking
                .Where(i => decision.Candidates[i].Tier == top)
                .OrderBy(i => CandidateKey(candidates[i]), StringComparer.Ordinal)
                .ToList();
            var best = -1.0;
            foreach (var i in pool)
            {
                var theta = SampleBeta(ref rng, decision.Candidates[i].Alpha, decision.Candidates[i].Beta);
                decision.Candidates[i].Theta = theta;
                if (theta > best)
                {
                    best = theta;
                    choice = i;
                }
            }
        }
        decision.Choice = choice;
        return decision;
    }

    /// <summary>
    /// Thompson sample of Beta(α, β) by its normal approximation, clamped to 0..1 (always exactly two
    /// draws, so the draw sequence is the same on every platform).
    /// </summary>
    public static double SampleBeta(ref ColituLmRng rng, double alpha, double beta)
    {
        var u1 = 1 - rng.NextFloat();
        var u2 = rng.NextFloat();
        var z = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        var n = alpha + beta;
        var mean = alpha / n;
        var sd = Math.Sqrt(alpha * beta / (n * n * (n + 1)));
        return Clamp01(mean + sd * z);
    }

    /// <summary>A random seed for production decisions (the vectors fix theirs).</summary>
    public static uint RandomSeed() => BitConverter.ToUInt32(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));
}

/// <summary>xorshift32 (shifts 13, 17, 5); seed 0 becomes 0x9E3779B9; the first 8 outputs are discarded.</summary>
public struct ColituLmRng
{
    private const uint ZeroSeed = 0x9E3779B9;
    private const int Warmup = 8;
    private uint _state;

    public ColituLmRng(uint seed)
    {
        _state = seed == 0 ? ZeroSeed : seed;
        for (var i = 0; i < Warmup; i++)
        {
            Next();
        }
    }

    public uint Next()
    {
        var x = _state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _state = x;
        return x;
    }

    /// <summary>Top 24 bits / 2^24: [0, 1), exact in a double.</summary>
    public double NextFloat() => (Next() >> 8) / 16777216.0;
}

public sealed class ColituLmWeights
{
    [JsonPropertyName("latency")] public double Latency { get; set; } = 1.0;
    [JsonPropertyName("own")] public double Own { get; set; } = 1.5;
    [JsonPropertyName("prior")] public double Prior { get; set; } = 1.0;
    [JsonPropertyName("hint")] public double Hint { get; set; } = -2.0;
    [JsonPropertyName("recommended")] public double Recommended { get; set; } = 0.25;
    [JsonPropertyName("load")] public double Load { get; set; } = -0.5;
    [JsonPropertyName("protocol_fit")] public double ProtocolFit { get; set; } = 0.5;
    [JsonPropertyName("whitelist")] public double Whitelist { get; set; } = 1.0;
}

/// <summary>Body of <c>GET /api/v1/client/ac-model</c>.</summary>
public sealed class ColituLmModel
{
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("generated_at")] public string? GeneratedAt { get; set; }
    /// <summary>Country whose priors the model holds ("" when unknown: no priors).</summary>
    [JsonPropertyName("country")] public string? Country { get; set; }
    [JsonPropertyName("weights")] public ColituLmWeights? Weights { get; set; }
    /// <summary>country → node id → protocol (or "*") → [α, β].</summary>
    [JsonPropertyName("priors")] public Dictionary<string, Dictionary<string, Dictionary<string, double[]>>>? Priors { get; set; }
    /// <summary>network type → protocol → 0..1.</summary>
    [JsonPropertyName("protocol_fit")] public Dictionary<string, Dictionary<string, double>>? ProtocolFit { get; set; }
    [JsonPropertyName("explore")] public double Explore { get; set; } = ColituLm.DefaultExplore;
    [JsonPropertyName("halflife_h")] public double HalflifeH { get; set; } = ColituLm.DefaultHalflifeH;
    [JsonPropertyName("latency_cap_ms")] public double LatencyCapMs { get; set; } = ColituLm.DefaultLatencyCapMs;
    /// <summary>City the priors were narrowed to (optional; journal and diagnostics only, not in the arithmetic).</summary>
    [JsonPropertyName("city")] public string? City { get; set; }
    /// <summary>country → node id → where its prior comes from (optional; journal and diagnostics only).</summary>
    [JsonPropertyName("prior_scope")] public Dictionary<string, Dictionary<string, ColituLmPriorScope>>? PriorScope { get; set; }

    /// <summary>"city:Moscow", "country", or null when the model does not say.</summary>
    public string? ScopeOf(string? country, string? nodeId)
    {
        if (PriorScope == null
            || !PriorScope.TryGetValue((country ?? "").ToUpperInvariant(), out var nodes) || nodes == null
            || !nodes.TryGetValue((nodeId ?? "").ToLowerInvariant(), out var scope) || scope == null)
        {
            return null;
        }
        return scope.Scope == "city" && scope.City is { Length: > 0 } city ? $"city:{city}" : scope.Scope;
    }

    /// <summary>How many nodes of <paramref name="country"/> have a city-level and a country-level prior.</summary>
    public (int City, int Country) ScopeCounts(string? country)
    {
        if (PriorScope == null || !PriorScope.TryGetValue((country ?? "").ToUpperInvariant(), out var nodes) || nodes == null)
        {
            return (0, 0);
        }
        return (nodes.Values.Count(scope => scope?.Scope == "city"), nodes.Values.Count(scope => scope?.Scope == "country"));
    }
}

/// <summary>Where a node's prior comes from: <c>{scope: "city", city}</c> or <c>{scope: "country"}</c>.</summary>
public sealed class ColituLmPriorScope
{
    [JsonPropertyName("scope")] public string? Scope { get; set; }
    [JsonPropertyName("city")] public string? City { get; set; }
}

/// <summary>One past outcome of this device on this network with a server and protocol, <see cref="AgeH"/> hours ago.</summary>
public sealed class ColituLmOutcome
{
    [JsonPropertyName("age_h")] public double AgeH { get; set; }
    [JsonPropertyName("ok")] public bool Ok { get; set; }
}

/// <summary>One (server or route, protocol) automatic mode may use.</summary>
public sealed class ColituLmCandidate
{
    [JsonPropertyName("node_id")] public string NodeId { get; set; } = "";
    [JsonPropertyName("protocol")] public string Protocol { get; set; } = "";
    [JsonPropertyName("entry_country")] public string? EntryCountry { get; set; }
    [JsonPropertyName("exit_country")] public string? ExitCountry { get; set; }
    [JsonPropertyName("whitelist_ok")] public bool WhitelistOk { get; set; }
    [JsonPropertyName("recommended")] public bool Recommended { get; set; }
    [JsonPropertyName("hint_blocked")] public bool HintBlocked { get; set; }
    [JsonPropertyName("load")] public string? Load { get; set; }
    [JsonPropertyName("latency_ms")] public double? LatencyMs { get; set; }
    [JsonPropertyName("history")] public List<ColituLmOutcome>? History { get; set; }
}

/// <summary>Everything one decision needs.</summary>
public sealed class ColituLmInput
{
    [JsonPropertyName("model")] public ColituLmModel? Model { get; set; }
    [JsonPropertyName("model_age_h")] public double ModelAgeH { get; set; }
    [JsonPropertyName("engine_enabled")] public bool EngineEnabled { get; set; }
    [JsonPropertyName("country")] public string? Country { get; set; }
    [JsonPropertyName("network_type")] public string? NetworkType { get; set; }
    [JsonPropertyName("whitelist_mode")] public bool WhitelistMode { get; set; }
    [JsonPropertyName("seed")] public uint Seed { get; set; }
    [JsonPropertyName("candidates")] public List<ColituLmCandidate>? Candidates { get; set; }
}

public sealed class ColituLmFeatures
{
    [JsonPropertyName("latency")] public double Latency { get; set; }
    [JsonPropertyName("own")] public double Own { get; set; }
    [JsonPropertyName("prior")] public double Prior { get; set; }
    [JsonPropertyName("hint")] public double Hint { get; set; }
    [JsonPropertyName("recommended")] public double Recommended { get; set; }
    [JsonPropertyName("load")] public double Load { get; set; }
    [JsonPropertyName("protocol_fit")] public double ProtocolFit { get; set; }
    [JsonPropertyName("whitelist")] public double Whitelist { get; set; }
}

public sealed class ColituLmScored
{
    [JsonPropertyName("features")] public ColituLmFeatures Features { get; set; } = new();
    [JsonPropertyName("alpha")] public double Alpha { get; set; }
    [JsonPropertyName("beta")] public double Beta { get; set; }
    [JsonPropertyName("log_odds")] public double LogOdds { get; set; }
    [JsonPropertyName("score")] public double Score { get; set; }
    [JsonPropertyName("eligible")] public bool Eligible { get; set; }
    /// <summary>In whitelist mode bridges are 0 and other whitelist servers 1; otherwise always 0.</summary>
    [JsonPropertyName("tier")] public int Tier { get; set; }
    /// <summary>The Thompson sample, only when the decision explored.</summary>
    [JsonPropertyName("theta")] public double? Theta { get; set; }
}

/// <summary>The model's answer. Engine "ac3": use the Adaptive Connect 3 rules (the other fields are empty).</summary>
public sealed class ColituLmDecision
{
    [JsonPropertyName("engine")] public string Engine { get; set; } = ColituLm.EngineAc3;
    [JsonPropertyName("stale")] public bool Stale { get; set; }
    [JsonPropertyName("explored")] public bool Explored { get; set; }
    [JsonPropertyName("whitelist_none")] public bool WhitelistNone { get; set; }
    [JsonPropertyName("candidates")] public List<ColituLmScored>? Candidates { get; set; }
    /// <summary>Indexes of the eligible candidates, best first.</summary>
    [JsonPropertyName("ranking")] public List<int>? Ranking { get; set; }
    /// <summary>Index of the candidate to connect to first; null when none is eligible.</summary>
    [JsonPropertyName("choice")] public int? Choice { get; set; }
}
