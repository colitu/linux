using System.Text.Json;
using AwesomeAssertions;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

/// <summary>AC4 phase 1, honest server list: hiding rule, safeguards, VPN-off cache, recommended order.</summary>
public class ColituReachabilityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] Ids = ["a", "b", "c", "d", "e"];

    /// <summary>A VPN-off round where <paramref name="failed"/> fail and the rest answer in 40 ms.</summary>
    private static Dictionary<string, int?> Round(params string[] failed) =>
        Ids.ToDictionary(id => id, id => failed.Contains(id) ? null : (int?)40);

    [Fact]
    public void Server_IsHidden_AfterTwoFailedVpnOffRounds_NotAfterOne()
    {
        var reach = new ColituReachability();
        reach.RecordRound(Round("a"), vpnOff: true, T0);
        reach.Hidden(Ids).Should().BeEmpty();

        reach.RecordRound(Round("a"), vpnOff: true, T0.AddSeconds(30));
        reach.Hidden(Ids).Should().BeEquivalentTo(["a"]);
        reach.IsUnreachable("A").Should().BeTrue();
    }

    [Fact]
    public void Server_ComesBack_AfterOneSuccessfulMeasurement()
    {
        var reach = new ColituReachability();
        reach.RecordRound(Round("a"), true, T0);
        reach.RecordRound(Round("a"), true, T0.AddSeconds(30));
        reach.RecordRound(Round(), true, T0.AddSeconds(60));

        reach.Hidden(Ids).Should().BeEmpty();
        reach.DisplayPing("a", connected: false, T0.AddSeconds(60)).Should().Be(40);
    }

    [Fact]
    public void FailuresInBetweenSuccesses_DoNotAddUp()
    {
        var reach = new ColituReachability();
        reach.RecordRound(Round("a"), true, T0);
        reach.RecordRound(Round(), true, T0.AddSeconds(30));
        reach.RecordRound(Round("a"), true, T0.AddSeconds(60));

        reach.Hidden(Ids).Should().BeEmpty();
    }

    [Fact]
    public void RoundsWithTheVpnOn_NeverHide()
    {
        var reach = new ColituReachability();
        reach.RecordRound(Round("a"), vpnOff: false, T0);
        reach.RecordRound(Round("a"), vpnOff: false, T0.AddSeconds(30));
        reach.RecordRound(Round("a"), vpnOff: false, T0.AddSeconds(60));

        reach.Hidden(Ids).Should().BeEmpty();
    }

    [Fact]
    public void Safeguard_EveryServerFailed_HidesNothing_AndDoesNotCount()
    {
        var reach = new ColituReachability();
        reach.RecordRound(Round(Ids), true, T0);
        reach.RecordRound(Round(Ids), true, T0.AddSeconds(30));
        reach.Hidden(Ids).Should().BeEmpty();

        // The outage rounds did not count: one real failure afterwards is not yet two.
        reach.RecordRound(Round("a"), true, T0.AddSeconds(60));
        reach.Hidden(Ids).Should().BeEmpty();
    }

    [Fact]
    public void Safeguard_HalfOrMoreFailed_HidesNothing()
    {
        var reach = new ColituReachability();
        // 3 of 5 fail twice: the network is the problem.
        reach.RecordRound(Round("a", "b", "c"), true, T0);
        reach.RecordRound(Round("a", "b", "c"), true, T0.AddSeconds(30));
        reach.Hidden(Ids).Should().BeEmpty();

        // 2 of 5 (40 %) is under the line: both are hidden.
        var other = new ColituReachability();
        other.RecordRound(Round("a", "b"), true, T0);
        other.RecordRound(Round("a", "b"), true, T0.AddSeconds(30));
        other.Hidden(Ids).Should().BeEquivalentTo(["a", "b"]);
    }

    [Fact]
    public void Safeguard_HalfOfTheKnownListUnreachable_HidesNothing()
    {
        var reach = new ColituReachability();
        reach.RecordRound(Round("a"), true, T0);
        reach.RecordRound(Round("a"), true, T0.AddSeconds(30));

        // The list asked about holds only "a" and "b": half of it would go.
        reach.Hidden(["a", "b"]).Should().BeEmpty();
        reach.Hidden(["a", "b", "c"]).Should().BeEquivalentTo(["a"]);
    }

    [Fact]
    public void ConnectedOrConnectingServer_IsNeverHidden()
    {
        var reach = new ColituReachability();
        reach.RecordRound(Round("a"), true, T0);
        reach.RecordRound(Round("a"), true, T0.AddSeconds(30));

        reach.Hidden(Ids, keep: ["A"]).Should().BeEmpty();
        var servers = Ids.Select(id => new ColituVpnServer { Id = id }).ToList();
        reach.Visible(servers, server => server.Id).Select(server => server.Id).Should().Equal("b", "c", "d", "e");
        reach.Visible(servers, server => server.Id, ["a"]).Should().HaveCount(5);
    }

    [Fact]
    public void Connected_ShowsVpnOffPing_ForAtMostThirtyMinutes()
    {
        var reach = new ColituReachability();
        reach.RecordRound(new Dictionary<string, int?> { ["a"] = 55, ["b"] = 70 }, vpnOff: true, T0);
        // Connected: "a" could not be measured outside the tunnel (not dialled), "b" failed.
        reach.RecordRound(new Dictionary<string, int?> { ["b"] = null }, vpnOff: false, T0.AddMinutes(5));

        reach.DisplayPing("a", connected: true, T0.AddMinutes(29)).Should().Be(55);
        reach.DisplayPing("b", connected: true, T0.AddMinutes(29)).Should().Be(70);
        reach.DisplayPing("a", connected: true, T0.AddMinutes(31)).Should().BeNull();
        reach.DisplayPing("unknown", connected: true, T0).Should().BeNull();
    }

    [Fact]
    public void Connected_ShowsPingMeasuredOutsideTheTunnel_OverTheCache()
    {
        var reach = new ColituReachability();
        reach.RecordRound(new Dictionary<string, int?> { ["a"] = 55 }, vpnOff: true, T0);
        reach.RecordRound(new Dictionary<string, int?> { ["a"] = 48 }, vpnOff: false, T0.AddMinutes(40));

        reach.DisplayPing("a", connected: true, T0.AddMinutes(41)).Should().Be(48);
    }

    [Fact]
    public void VpnOff_FailedPing_ShowsDash()
    {
        var reach = new ColituReachability();
        reach.RecordRound(new Dictionary<string, int?> { ["a"] = 55 }, true, T0);
        reach.RecordRound(new Dictionary<string, int?> { ["a"] = null }, true, T0.AddSeconds(30));

        reach.DisplayPing("a", connected: false, T0.AddSeconds(31)).Should().BeNull();
        // The last VPN-off success still covers the connected view.
        reach.DisplayPing("a", connected: true, T0.AddSeconds(31)).Should().Be(55);
    }

    [Fact]
    public void Recommended_IsParsed_FalseWhenAbsent()
    {
        var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        ColituVpnServer Map(string text) => ColituApiClient.MapServer(JsonSerializer.Deserialize<ColituServerDto>(text, json)!);

        Map("""{"id":"a","country":"DE","status":"online","recommended":true}""").Recommended.Should().BeTrue();
        Map("""{"id":"b","country":"DE","status":"online","recommended":false}""").Recommended.Should().BeFalse();
        Map("""{"id":"c","country":"DE","status":"online"}""").Recommended.Should().BeFalse();
    }

    [Theory]
    [InlineData(ColituLocationSort.Name)]
    [InlineData(ColituLocationSort.Ping)]
    [InlineData(ColituLocationSort.Load)]
    public void Recommended_SortsFirstInsideItsCountry(string mode)
    {
        ColituVpnServer Server(string id, string city, bool recommended = false, double? load = null) =>
            new() { Id = id, CountryCode = "DE", City = city, Name = city, Recommended = recommended, Load = load };
        var servers = new[]
        {
            Server("de-ber", "Berlin", load: 10),
            Server("de-fra", "Frankfurt", load: 30),
            Server("de-muc", "München", recommended: true, load: 90),
            new ColituVpnServer { Id = "nl-ams", CountryCode = "NL", City = "Amsterdam", Name = "Amsterdam" }
        };
        var pings = new Dictionary<string, int> { ["de-ber"] = 20, ["de-fra"] = 30, ["de-muc"] = 90 };

        var groups = ColituLocationSort.Order(
            ColituServerGroups.Build(servers, code => code, StringComparer.OrdinalIgnoreCase, false),
            mode,
            server => pings.TryGetValue(server.Id!, out var ms) ? ms : null);

        var germany = groups.Single(group => group.Servers.Count == 3);
        germany.Servers[0].Id.Should().Be("de-muc");
        germany.Servers.Skip(1).Select(server => server.Id).Should().Equal("de-ber", "de-fra");
    }
}
