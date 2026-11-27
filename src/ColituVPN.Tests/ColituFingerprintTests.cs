using System.IO;
using System.Text.Json;
using AwesomeAssertions;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

/// <summary>Learned uTLS fingerprint per network (FP-SPEC), driven by the shared fixtures in TestData/fp-fixtures.json.</summary>
public class ColituFingerprintTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    private static JsonElement Fixtures()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "fp-fixtures.json")));
        return doc.RootElement.Clone();
    }

    private static ColituFpPolicy Policy(params string[] order) => new() { Order = [.. order], Blocked = [], Spread = 3 };

    [Fact]
    public void Fnv1a32_MatchesTheSharedVectors()
    {
        foreach (var vector in Fixtures().GetProperty("fnv1a32").EnumerateArray())
        {
            ColituFingerprint.Fnv1a32(vector.GetProperty("input").GetString()!).Should().Be(vector.GetProperty("hash").GetUInt32());
        }
    }

    [Fact]
    public void Choose_MatchesTheSharedFixtures()
    {
        foreach (var c in Fixtures().GetProperty("choose").EnumerateArray())
        {
            var policy = ColituFingerprint.ParsePolicy(c.GetProperty("policy"))!;
            var state = new Dictionary<string, ColituFpRecord>();
            foreach (var entry in c.GetProperty("state").EnumerateObject())
            {
                var ok = entry.Value.TryGetProperty("ok", out var okValue) && okValue.ValueKind == JsonValueKind.True;
                state[entry.Name] = ok
                    ? new ColituFpRecord { OkAt = Now.AddSeconds(-1) }
                    : new ColituFpRecord { FailAt = Now.AddSeconds(-1), Fails = 1 };
            }
            ColituFingerprint.Choose(policy, c.GetProperty("device_id").GetString()!, c.GetProperty("network").GetString()!, state, Now)
                .Should().Be(c.GetProperty("expect").GetString(), c.GetProperty("name").GetString());
        }
    }

    [Fact]
    public void Retry_MatchesTheSharedFixtures()
    {
        foreach (var r in Fixtures().GetProperty("retry").EnumerateArray())
        {
            var probe = r.TryGetProperty("probe", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            ColituFingerprint.ShouldRetry(r.GetProperty("transport").GetString(), r.GetProperty("tcp_ok").GetBoolean(), probe, r.GetProperty("retries_used").GetInt32())
                .Should().Be(r.GetProperty("expect_retry").GetBoolean(), r.GetProperty("name").GetString());
        }
    }

    [Fact]
    public void Memory_FailureLasts24Hours_AndASuccessClearsIt()
    {
        var policy = Policy("chrome", "edge", "safari");
        var memory = new ColituFpMemory();
        memory.MarkOk("RU-AS1", "chrome", Now);
        memory.Choose(policy, "d", "RU-AS1", Now.AddMinutes(1)).Should().Be("chrome");
        memory.MarkFailed("RU-AS1", "chrome", Now.AddMinutes(2));
        memory.Choose(policy, "d", "RU-AS1", Now.AddMinutes(3)).Should().NotBe("chrome");
        // After 24 h the failure is forgotten: chrome may be chosen again.
        ColituFingerprint.LocallyFailed(memory.Of("RU-AS1")["chrome"], Now.AddMinutes(2).Add(ColituFingerprint.FailureMemory).AddSeconds(1)).Should().BeFalse();
        memory.MarkOk("RU-AS1", "chrome", Now.AddMinutes(4));
        memory.Choose(policy, "d", "RU-AS1", Now.AddMinutes(5)).Should().Be("chrome");
        // Another network knows nothing of it.
        memory.Of("RU-AS2").Should().BeEmpty();
    }

    [Fact]
    public void Memory_RoundTripsAndStaysBounded()
    {
        var memory = new ColituFpMemory();
        for (var n = 1; n <= 12; n++)
        {
            memory.MarkFailed($"net{n}", "chrome", Now.AddSeconds(n));
        }
        memory.Snapshot().Count.Should().BeLessThanOrEqualTo(ColituFpMemory.MaxNetworks);
        memory.Snapshot().Should().ContainKey("net12").And.NotContainKey("net1");
        var back = new ColituFpMemory();
        back.Load(JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, ColituFpRecord>>>(JsonSerializer.Serialize(memory.Snapshot())));
        JsonSerializer.Serialize(back.Snapshot()).Should().Be(JsonSerializer.Serialize(memory.Snapshot()));
    }

    [Fact]
    public void Policy_AbsentMeansNone_AndFeatureNeedsALiteralTrue()
    {
        ColituFingerprint.ParsePolicy(null).Should().BeNull();
        using var doc = JsonDocument.Parse("""{"order":["Chrome","edge"],"blocked":["firefox"],"spread":99,"scope":"network"}""");
        var policy = ColituFingerprint.ParsePolicy(doc.RootElement)!;
        policy.Order.Should().Equal("chrome", "edge");
        policy.Spread.Should().Be(ColituFingerprint.Names.Length);
        using var features = JsonDocument.Parse("""{"fp_events":true,"other":1}""");
        var map = features.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        ColituFingerprint.ParseEventsFeature(map).Should().BeTrue();
        map["fp_events"] = JsonDocument.Parse("\"true\"").RootElement.Clone();
        ColituFingerprint.ParseEventsFeature(map).Should().BeFalse();
        ColituFingerprint.ParseEventsFeature(null).Should().BeFalse();
    }

    [Fact]
    public void WithFingerprint_SetsFpOnlyOnTlsTransports()
    {
        const string reality = "vless://u@203.0.113.10:443?encryption=none&security=reality&sni=a.example.test&fp=chrome&pbk=k&sid=1&type=tcp#n";
        var edged = ColituFingerprint.WithFingerprint(reality, "vless-reality", "edge");
        edged.Should().Contain("fp=edge").And.NotContain("fp=chrome");
        const string trojan = "trojan://p@203.0.113.10:443?security=tls&sni=a.example.test&type=tcp#n";
        ColituFingerprint.WithFingerprint(trojan, "trojan", "safari").Should().Contain("fp=safari");
        const string hy2 = "hysteria2://p@203.0.113.10:443?sni=a.example.test#n";
        ColituFingerprint.WithFingerprint(hy2, "hysteria2", "edge").Should().Be(hy2);
        ColituFingerprint.WithFingerprint(reality, "vless-reality", "not-a-name").Should().Be(reality);
    }
}
