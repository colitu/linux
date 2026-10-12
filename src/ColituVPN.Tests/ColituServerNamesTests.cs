using AwesomeAssertions;
using v2rayN.Desktop.Services;
using v2rayN.Desktop.Views;
using Xunit;

namespace ColituVPN.Tests;

/// <summary>
/// Server names in the app language (sunucu-adlari-ceviri.json): the panel writes names and cities
/// in Turkish; known ones are shown in the app language, unknown text stays as it is.
/// </summary>
public class ColituServerNamesTests
{
    [Theory]
    [InlineData("Letonya (Köprü)", "LV", "en", "Latvia (Bridge)")]
    [InlineData("Letonya (Köprü)", "LV", "ru", "Латвия (Мост)")]
    [InlineData("Letonya (Köprü)", "LV", "tr", "Letonya (Köprü)")]
    [InlineData("letonya (KOPRU)", null, "en", "Latvia (Bridge)")]
    [InlineData("Almanya", "DE", "en", "Germany")]
    [InlineData("Almanya", "DE", "ru", "Германия")]
    [InlineData("Germany", null, "tr", "Almanya")]
    [InlineData("Londra", null, "en", "London")]
    [InlineData("Londra", null, "ru", "Лондон")]
    [InlineData("Almatı", null, "en", "Almaty")]
    [InlineData("Varşova", null, "en", "Warsaw")]
    [InlineData("Codex staging Docker node", "DE", "en", "Codex staging Docker node")]
    [InlineData("Codex staging Docker node", null, "ru", "Codex staging Docker node")]
    [InlineData("Almanya (Yeni)", "DE", "en", "Germany (Yeni)")]
    [InlineData("", null, "en", "")]
    public void DisplayName(string raw, string? code, string language, string expected)
    {
        ColituServerNames.DisplayName(raw, code, language).Should().Be(expected);
    }

    [Theory]
    [InlineData("Londra", "en", "London")]
    [InlineData("Londra", "ru", "Лондон")]
    [InlineData("LONDON", "tr", "Londra")]
    [InlineData("istanbul", "en", "Istanbul")]
    [InlineData("Sao Paulo", "en", "São Paulo")]
    [InlineData("Atlantis", "ru", "Atlantis")]
    [InlineData(null, "en", "")]
    public void City(string? raw, string language, string expected)
    {
        ColituServerNames.City(raw, language).Should().Be(expected);
    }

    [Fact]
    public void Route_EachEndLocalized()
    {
        ColituServerNames.Route("Varşova → Frankfurt", "en").Should().Be("Warsaw → Frankfurt");
        ColituServerNames.Route("Varşova → Almanya", "ru").Should().Be("Варшава → Германия");
    }

    [Fact]
    public void Normalize_IgnoresCaseAndDiacritics()
    {
        ColituServerNames.Normalize("  İstanbul  ").Should().Be(ColituServerNames.Normalize("istanbul"));
        ColituServerNames.Normalize("Almatı").Should().Be("almati");
        ColituServerNames.Normalize("Zürih").Should().Be("zurih");
    }

    [Theory]
    [InlineData("London")]
    [InlineData("лондон")]
    [InlineData("londra")]
    [InlineData("United Kingdom")]
    [InlineData("Великобритания")]
    public void Search_FindsTheServerInAnyLanguage(string query)
    {
        var server = new ColituVpnServer { Id = "n-1", DisplayName = "Londra", City = "Londra", CountryCode = "GB", Country = "United Kingdom" };
        ColituServerRow.From(server).Matches(query).Should().BeTrue();
    }

    [Fact]
    public void Search_NoFalseMatch()
    {
        var server = new ColituVpnServer { Id = "n-1", DisplayName = "Londra", City = "Londra", CountryCode = "GB", Country = "United Kingdom" };
        ColituServerRow.From(server).Matches("Warsaw").Should().BeFalse();
    }
}
