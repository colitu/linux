using System.Text.Json;
using AwesomeAssertions;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

/// <summary>Locations list order (as on Android/iOS): by ping, by name, by load.</summary>
public class ColituLocationSortTests
{
    private static ColituVpnServer Server(string id, string code, string country, string city, double? load = null) =>
        new() { Id = id, CountryCode = code, Country = country, City = city, Name = $"{country} {city}", Load = load };

    private static readonly List<ColituVpnServer> Servers =
    [
        Server("de-fra", "DE", "Germany", "Frankfurt", 55),
        Server("de-ber", "DE", "Germany", "Berlin", 85),
        Server("fi-hel", "FI", "Finland", "Helsinki", 25),
        Server("nl-ams", "NL", "Netherlands", "Amsterdam"),
        Server("se-sto", "SE", "Sweden", "Stockholm", 85),
    ];

    private static readonly Dictionary<string, int> Pings = new() { ["de-ber"] = 40, ["de-fra"] = 90, ["fi-hel"] = 60, ["se-sto"] = 0 };

    private static List<ColituServerGroup> Sorted(string mode, bool flat = false) =>
        ColituLocationSort.Order(
            ColituServerGroups.Build(Servers, _ => null, StringComparer.OrdinalIgnoreCase, flat),
            mode,
            server => Pings.TryGetValue(server.Id!, out var ms) ? ms : null);

    [Fact]
    public void ByName_KeepsCountryThenCityOrder()
    {
        Sorted(ColituLocationSort.Name).Select(g => g.Key).Should().Equal("FI", "DE", "NL", "SE");
        Sorted(ColituLocationSort.Name).Single(g => g.Key == "DE").Servers.Select(s => s.Id).Should().Equal("de-ber", "de-fra");
    }

    [Fact]
    public void ByPing_CountryCountsWithItsBestCity_UnmeasuredLast()
    {
        var groups = Sorted(ColituLocationSort.Ping);
        // Germany's best city (Berlin, 40 ms) beats Finland (60); Netherlands and Sweden (0 = failed) have no ping.
        groups.Select(g => g.Key).Should().Equal("DE", "FI", "NL", "SE");
        groups[0].Servers.Select(s => s.Id).Should().Equal("de-ber", "de-fra");
    }

    [Fact]
    public void ByLoad_LowestFirst_UnknownLast()
    {
        var groups = Sorted(ColituLocationSort.Load);
        groups.Select(g => g.Key).Should().Equal("FI", "DE", "SE", "NL");
        groups.Single(g => g.Key == "DE").Servers.Select(s => s.Id).Should().Equal("de-fra", "de-ber");
    }

    [Fact]
    public void Search_FlatListIsSortedToo()
    {
        Sorted(ColituLocationSort.Ping, flat: true).Select(g => g.Servers[0].Id).Should().Equal("de-ber", "fi-hel", "de-fra", "nl-ams", "se-sto");
    }

    [Theory]
    [InlineData("ping", "ping")]
    [InlineData(" LOAD ", "load")]
    [InlineData("name", "name")]
    [InlineData(null, "name")]
    [InlineData("speed", "name")]
    public void Normalize_UnknownFallsBackToName(string? mode, string expected)
    {
        ColituLocationSort.Normalize(mode).Should().Be(expected);
    }

    [Fact]
    public void Preference_IsSavedAndOldStatesSortByName()
    {
        var saved = JsonSerializer.Serialize(new ColituVpnPreferences() with { LocationSort = "load" });
        JsonSerializer.Deserialize<ColituVpnPreferences>(saved)!.Normalize().LocationSort.Should().Be("load");
        JsonSerializer.Deserialize<ColituVpnPreferences>("""{"AdvancedMode": true}""")!.Normalize().LocationSort.Should().Be("name");
        (new ColituVpnPreferences() with { LocationSort = "bogus" }).Normalize().LocationSort.Should().Be("name");
    }

    [Fact]
    public void Strings_InEveryLanguage()
    {
        foreach (var key in new[] { "locations.sortTitle", "locations.sort.ping", "locations.sort.name", "locations.sort.load" })
        {
            Loc.ValuesOf(key).Should().HaveCount(3).And.AllSatisfy(value => value.Should().NotBeNullOrWhiteSpace());
        }
        Loc.ValuesOf("locations.sort.ping").Should().Contain("Ping’e göre");
    }
}
