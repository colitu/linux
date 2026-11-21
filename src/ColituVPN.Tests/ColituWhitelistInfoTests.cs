using AwesomeAssertions;
using v2rayN.Desktop.Services;
using v2rayN.Desktop.Views;
using Xunit;

namespace v2rayN.Tests;

public class ColituWhitelistInfoTests
{
    private static readonly string[] Keys = ["title", "lead", "when", "how", "note", "ok"];

    [Fact]
    public void ExplainerCopy_ExistsInEveryLanguage_AndNeverClaimsEncryption()
    {
        foreach (var key in Keys)
        {
            var values = Loc.ValuesOf("whitelist.info." + key);
            values.Should().HaveCount(Loc.Languages.Length);
            values.Should().OnlyContain(value => value.Length > 0);
            foreach (var value in values)
            {
                value.Should().NotContainEquivalentOf("encrypt").And.NotContainEquivalentOf("шифр");
            }
        }
    }

    [Fact]
    public void InfoButton_IsOnlyOnTheWhitelistRow()
    {
        var whitelist = new ColituVpnServer { Id = "w", Name = "w", WhitelistOk = true, Protocols = ["vless-ws"] };
        var plain = new ColituVpnServer { Id = "p", Name = "p", WhitelistOk = true, Protocols = ["vless-reality"] };

        ColituServerRow.Whitelist(whitelist).HasInfo.Should().BeTrue();
        ColituServerRow.From(whitelist).HasInfo.Should().BeFalse();
        ColituServerRow.From(plain).HasInfo.Should().BeFalse();
        ColituServerRow.Auto("x").HasInfo.Should().BeFalse();
    }

    [Fact]
    public void Search_FindsTheWhitelistRow_ByCountryCityAndWords()
    {
        var helsinki = new ColituVpnServer { Id = "fi", Name = "Finlandiya", DisplayName = "Finlandiya", CountryCode = "FI", City = "Helsinki", WhitelistOk = true, Protocols = ["vless-reality-relay"] };
        var row = ColituServerRow.Whitelist(helsinki);
        foreach (var q in new[] { "Helsinki", "хельсинки", "Finland", "Финляндия", "finlandiya", "beyaz liste", "Whitelist", "белый", "白名单", "لیست سفید" })
        {
            Assert.True(row.Matches(q), q);
        }
        Assert.False(row.Matches("Prag"));
        Assert.False(ColituServerRow.From(helsinki).Matches("beyaz liste"));
    }
}
