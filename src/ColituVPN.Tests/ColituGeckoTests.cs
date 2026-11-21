using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using v2rayN.Desktop.Services;
using v2rayN.Desktop.Views;
using Xunit;

namespace ColituVPN.Tests;

/// <summary>ColituGecko as a protocol on Linux: sidecar contract, entry addresses, kill switch, the Gecko-only profile, Adaptive Connect steps, reports, label.</summary>
[Collection("ColituGecko sidecar")]
public class ColituGeckoTests
{
    private const string SocksUser = "gu-0123";
    private const string SocksPassword = "gp-0123456789abcdef";
    private const string ConfigSecret = "s3cr3t-ticket-0123456789abcdef";
    private const string ClientPath = "/opt/colitu-vpn/bin/colitu-gecko/gecko-client";

    private static ColituGeckoPlan Plan() => new("n", 42000, SocksUser, SocksPassword, ClientPath);

    private static string? S(JsonNode? node) => node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;

    // ── Entry records (hex): type | length | value, type 3 = atyp | address | port ──
    private static string Record(int type, byte[] value) => Convert.ToHexString([(byte)type, (byte)value.Length, .. value]);

    private static string Entry(string id, byte[] address) =>
        Record(1, System.Text.Encoding.ASCII.GetBytes(id)) + Record(2, "s1"u8.ToArray()) + Record(3, address);

    private static byte[] V4(string ip, int port) => [1, .. IPAddress.Parse(ip).GetAddressBytes(), (byte)((port >> 8) & 0xff), (byte)(port & 0xff)];

    private static byte[] V6(string ip, int port) => [4, .. IPAddress.Parse(ip).GetAddressBytes(), (byte)((port >> 8) & 0xff), (byte)(port & 0xff)];

    private static byte[] Domain(string name, int port) => [3, (byte)name.Length, .. System.Text.Encoding.ASCII.GetBytes(name), (byte)((port >> 8) & 0xff), (byte)(port & 0xff)];

    private static string ConfigWith(params string[] entries) =>
        $$"""{ "v": 1, "anchors": [ { "id": "0102030405060708", "ticket": "{{ConfigSecret}}", "entries": [{{string.Join(",", entries.Select(e => $"\"{e}\""))}}] } ] }""";

    private static readonly string Config = ConfigWith(Entry("e1", V4("203.0.113.9", 443)));

    private const string ConfigKeyHex = "e0a6c63f2313311887b9e7e7f670390af3a16e8a9249b7d97b6ad32426615faa";
    private static readonly string Signature = "01" + new string('a', 128);
    // Whitespace that a parse-and-write would change: the signature covers these exact bytes.
    private static readonly byte[] ConfigBytes = System.Text.Encoding.UTF8.GetBytes(Config.Replace("{ \"v\"", "{   \"v\"") + "\n");

    private static ColituGeckoSignedConfig Signed() => ColituGeckoRules.Sign(ConfigBytes, Signature)!;

    // ── Command line and standard input ────────────────────────────────────

    [Fact]
    public void Arguments_HoldThePortAndSkinKey_NeverASecret()
    {
        var args = ColituGeckoRules.Arguments(new ColituGeckoLaunch(42000, SocksUser, SocksPassword));
        args.Should().StartWith(["-socks", "127.0.0.1:42000", "-secrets-stdin"]);
        args.Should().Contain("-skin-key");
        args.Should().ContainInConsecutiveOrder("-config-key", $"1={ConfigKeyHex}");
        ColituGeckoRules.ConfigKeys.Keys.Should().Equal(1);
        args.Should().Contain(arg => arg.StartsWith("2=", StringComparison.Ordinal));
        args.Should().NotContain(arg => arg.Contains(SocksPassword) || arg.Contains(ConfigSecret) || arg.Contains(SocksUser));
        ColituGecko.ExecutablePath.Should().EndWith(Path.Combine("bin", "colitu-gecko", "gecko-client"));
    }

    [Fact]
    public void Secrets_IsOneLine_WithTheExactBytesTheirSignatureAndTheFloor()
    {
        var floor = new ColituGeckoFloor(1_763_190_000, new Dictionary<string, long> { ["s2"] = 5, ["s1"] = 3 });
        var line = ColituGeckoRules.Secrets(new ColituGeckoLaunch(42000, SocksUser, SocksPassword), Signed(), floor);
        line.Should().EndWith("\n");
        line.TrimEnd('\n').Should().NotContain("\n").And.NotContain("\r");
        var root = JsonNode.Parse(line)!.AsObject();
        S(root["socks_user"]).Should().Be(SocksUser);
        S(root["socks_password"]).Should().Be(SocksPassword);
        root.ContainsKey("config").Should().BeFalse("the unsigned form is for the lab only");
        Convert.FromBase64String(S(root["config_b64"])!).Should().Equal(ConfigBytes, "the bytes go through untouched");
        S(root["config_sig"]).Should().Be(Signature);
        root["min_issued"]!.GetValue<long>().Should().Be(1_763_190_000);
        root["skin_floor"]!["s1"]!.GetValue<long>().Should().Be(3);
        root["skin_floor"]!["s2"]!.GetValue<long>().Should().Be(5);

        // First start of an install: nothing to give back.
        var fresh = JsonNode.Parse(ColituGeckoRules.Secrets(new ColituGeckoLaunch(42000, SocksUser, SocksPassword), Signed(), null))!.AsObject();
        fresh.Select(p => p.Key).Should().BeEquivalentTo(["socks_user", "socks_password", "config_b64", "config_sig"]);

        var later = JsonNode.Parse(ColituGeckoRules.ConfigLine(Signed()))!.AsObject();
        later.Select(p => p.Key).Should().BeEquivalentTo(["config_b64", "config_sig"]);
        Convert.FromBase64String(S(later["config_b64"])!).Should().Equal(ConfigBytes);
        ColituGeckoRules.ConfigLine(Signed()).Should().EndWith("\n");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "anchors": [] }""")]
    [InlineData("""{ "anchors": {} }""")]
    [InlineData("""{ "version": 1 }""")]
    public void UnusableConfig_IsRefused(string body)
    {
        ColituGeckoRules.IsUsableConfig(body).Should().BeFalse();
        ColituGeckoRules.Sign(System.Text.Encoding.UTF8.GetBytes(body), Signature).Should().BeNull();
        ColituApiClient.GeckoResult(System.Text.Encoding.UTF8.GetBytes(body), Signature).Offer.Should().Be(ColituGeckoOffer.Unavailable);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("01aa")]
    [InlineData("zz")]
    public void NoUsableSignature_GeckoDoesNotStart_AndSaysWhy(string? header)
    {
        var result = ColituApiClient.GeckoResult(ConfigBytes, header);
        result.Offer.Should().Be(ColituGeckoOffer.Unavailable);
        result.Signed.Should().BeNull("no signature, no configuration for gecko-client");
        result.Reason.Should().StartWith("signature header").And.NotContain(ConfigSecret);
        ColituGeckoRules.Sign(ConfigBytes, header).Should().BeNull();
    }

    [Fact]
    public void PanelAnswer_KeepsTheBytesAsTheyCame_AndNeverPrintsThem()
    {
        var result = ColituApiClient.GeckoResult(ConfigBytes, Signature.ToUpperInvariant());
        result.Offer.Should().Be(ColituGeckoOffer.Offered);
        result.Signed!.Body.Should().Equal(ConfigBytes);
        result.Signed.Signature.Should().Be(Signature, "hex is compared case-insensitively; sent lower case");
        result.ToString().Should().NotContain(ConfigSecret).And.Contain("redacted");
        result.Signed.ToString().Should().NotContain(ConfigSecret).And.NotContain(Signature);
        ColituApiClient.GeckoConfigPath.Should().Be("/gecko/config");
        ColituApiClient.GeckoSignatureHeader.Should().Be("X-Gecko-Config-Signature");
    }

    // ── F-14/F-15: refused configurations and the floor kept across runs ───

    [Fact]
    public void ConfigRefused_IsOnlyLogged_AndFloorEventsAreParsed()
    {
        var refused = ColituGeckoRules.ParseEvent("gecko-client: event {\"event\":\"config_refused\",\"reason\":\"signature\"}")!;
        refused.Kind.Should().Be("config_refused");
        refused.Reason.Should().Be("signature");
        refused.Describe().Should().Contain("last valid one stays");
        ColituGeckoRules.ParseEvent("gecko-client: event {\"event\":\"config_refused\",\"reason\":\"old\"}")!.Reason.Should().Be("old");
        ColituGeckoRules.ParseEvent("gecko-client: event {\"event\":\"config_refused\",\"reason\":\"x y\"}")!.Reason.Should().Be("other");

        var accepted = ColituGeckoRules.ParseEvent("gecko-client: event {\"event\":\"config\",\"issued\":1763190000}")!;
        accepted.Issued.Should().Be(1763190000);
        var revs = ColituGeckoRules.ParseEvent("gecko-client: event {\"event\":\"skin_revs\",\"revs\":{\"s1\":3,\"s2\":5,\"bad key\":9,\"neg\":-1}}")!;
        revs.Revs.Should().Equal(new Dictionary<string, long> { ["s1"] = 3, ["s2"] = 5 });
    }

    [Fact]
    public void Floor_OnlyGoesUp_SurvivesAStateSaveAndComesBackAtTheNextStart()
    {
        var stored = new ColituGeckoFloor(0, null);
        stored = ColituGeckoRules.MergeFloor(stored, 100, new Dictionary<string, long> { ["s1"] = 3 });
        stored = ColituGeckoRules.MergeFloor(stored, 90, new Dictionary<string, long> { ["s1"] = 2, ["s2"] = 7 });
        stored.MinIssued.Should().Be(100, "an older issued never lowers it");
        stored.SkinFloor.Should().Equal(new Dictionary<string, long> { ["s1"] = 3, ["s2"] = 7 });

        // Save: the app's state file; restart: read it back.
        var prefs = new ColituVpnPreferences { GeckoMinIssued = stored.MinIssued, GeckoSkinRevs = stored.SkinFloor!.ToDictionary(r => r.Key, r => r.Value) };
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var back = JsonSerializer.Deserialize<ColituVpnPreferences>(JsonSerializer.Serialize(prefs, options), options)!.Normalize();
        back.GeckoMinIssued.Should().Be(100);
        back.GeckoSkinRevs.Should().Equal(new Dictionary<string, long> { ["s1"] = 3, ["s2"] = 7 });

        var line = JsonNode.Parse(ColituGeckoRules.Secrets(new ColituGeckoLaunch(1, "u", "p"), Signed(), new ColituGeckoFloor(back.GeckoMinIssued, back.GeckoSkinRevs)))!.AsObject();
        line["min_issued"]!.GetValue<long>().Should().Be(100);
        line["skin_floor"]!["s2"]!.GetValue<long>().Should().Be(7);
        new ColituVpnPreferences().GeckoMinIssued.Should().Be(0, "an install that never ran Gecko gives back nothing");
    }

    [Fact]
    public void Events_AreParsed_WithOnlySafeFields_AndVerdictsFollowTheContract()
    {
        ColituGeckoRules.IsReadyLine("2026/11/17 10:00:00 gecko-client: SOCKS5 on 127.0.0.1:42000").Should().BeTrue();
        var session = ColituGeckoRules.ParseEvent("gecko-client: event {\"event\":\"session\",\"anchor\":1,\"bootstrap\":false}")!;
        session.Kind.Should().Be("session");
        session.Bootstrap.Should().BeFalse();
        ColituGeckoRules.ParseEvent("gecko-client: event {\"event\":\"need_config\",\"reason\":\"half_life\"}")!.Reason.Should().Be("half_life");
        ColituGeckoRules.ParseEvent("gecko-client: event {\"event\":\"need_config\",\"reason\":\"ticket=abc\"}")!.Reason.Should().Be("other");
        ColituGeckoRules.ParseEvent("gecko-client: event {\"event\":\"nope\"}").Should().BeNull();
        ColituGeckoRules.ParseEvent("gecko-client: event {broken").Should().BeNull();
        ColituGeckoRules.ParseEvent("hello").Should().BeNull();

        var limit = TimeSpan.FromSeconds(15);
        ColituGeckoRules.Verdict(false, false, false, null, TimeSpan.FromSeconds(1)).Should().BeNull();
        ColituGeckoRules.Verdict(true, true, false, null, TimeSpan.FromSeconds(1)).Should().Be("");
        ColituGeckoRules.Verdict(true, false, false, null, limit).Should().Contain("no session");
        ColituGeckoRules.Verdict(false, false, false, null, limit).Should().Contain("no SOCKS port");
        ColituGeckoRules.Verdict(true, true, true, 2, TimeSpan.FromSeconds(1)).Should().Contain("exited");
    }

    // ── Off reasons, Adaptive Connect steps, cool-down ─────────────────────

    [Fact]
    public void OffReason_NamesTheFirstMissingThing_AndAManualPickIgnoresTheCoolDown()
    {
        ColituGeckoRules.OffReason(ColituGeckoOffer.NotOffered, false, true, true, false).Should().Be("not offered to this account");
        ColituGeckoRules.OffReason(ColituGeckoOffer.Unavailable, true, false, false, false).Should().Be("Gecko settings unavailable");
        ColituGeckoRules.OffReason(ColituGeckoOffer.Unknown, true, false, false, true).Should().Be("Gecko settings unavailable");
        ColituGeckoRules.OffReason(ColituGeckoOffer.Offered, false, false, false, false).Should().Be("gecko-client missing");
        ColituGeckoRules.OffReason(ColituGeckoOffer.Offered, true, true, false, false).Should().Be("the kill switch allows only the VPN servers' addresses");
        ColituGeckoRules.OffReason(ColituGeckoOffer.Offered, true, false, true, manual: false).Should().Be("cooling down after a failure on this network");
        ColituGeckoRules.OffReason(ColituGeckoOffer.Offered, true, false, true, manual: true).Should().BeNull("a manual pick ignores the cool-down (11-20 bug)");
        ColituGeckoRules.OffReason(ColituGeckoOffer.Offered, true, false, false, false).Should().BeNull();
    }

    [Fact]
    public void EffectiveOffer_IsOfferedOnlyWithASignedConfigurationInHand()
    {
        ColituGeckoRules.EffectiveOffer(null).Should().Be(ColituGeckoOffer.Unknown);
        ColituGeckoRules.EffectiveOffer(ColituGeckoConfigResult.NotOffered).Should().Be(ColituGeckoOffer.NotOffered);
        ColituGeckoRules.EffectiveOffer(ColituGeckoConfigResult.Unavailable).Should().Be(ColituGeckoOffer.Unavailable);
        ColituGeckoRules.EffectiveOffer(new ColituGeckoConfigResult(ColituGeckoOffer.Offered, null)).Should().Be(ColituGeckoOffer.Unavailable);
        ColituGeckoRules.EffectiveOffer(ColituApiClient.GeckoResult(ConfigBytes, Signature)).Should().Be(ColituGeckoOffer.Offered);
    }

    [Fact]
    public void AdaptiveConnect_NormalFails_ThenGecko_ThenWhitelist()
    {
        var steps = ColituConnectSteps.Plan(geckoPicked: false, automatic: true, whitelistMode: false, ColituGeckoOffer.Offered);
        steps.Should().Equal(ColituConnectSteps.Normal, ColituConnectSteps.Gecko, ColituConnectSteps.Whitelist);
        ColituConnectSteps.After(steps, ColituConnectSteps.Normal).Should().Be(ColituConnectSteps.Gecko, "Gecko comes right after the normal servers");
        ColituConnectSteps.After(steps, ColituConnectSteps.Gecko).Should().Be(ColituConnectSteps.Whitelist);
        ColituConnectSteps.After(steps, ColituConnectSteps.Whitelist).Should().BeNull();
        ColituConnectSteps.Gecko.Should().Be("gecko", "the journal's step name");
        ColituEngineJournal.Gecko.Should().Be("gecko");
        // Not asked yet: the configuration may still come within 6 s, so the step stays (and is skipped then if nothing came).
        ColituConnectSteps.Plan(false, true, false, ColituGeckoOffer.Unknown).Should().Contain(ColituConnectSteps.Gecko);
        ColituConnectSteps.Plan(false, true, false, ColituGeckoOffer.Unavailable).Should().Contain(ColituConnectSteps.Gecko);
    }

    [Fact]
    public void AdaptiveConnect_GeckoNotOffered_IsSkipped()
    {
        var steps = ColituConnectSteps.Plan(false, true, false, ColituGeckoOffer.NotOffered);
        steps.Should().Equal(ColituConnectSteps.Normal, ColituConnectSteps.Whitelist);
        ColituConnectSteps.After(steps, ColituConnectSteps.Normal).Should().Be(ColituConnectSteps.Whitelist);
        ColituConnectSteps.Plan(false, true, true, ColituGeckoOffer.NotOffered).Should().Equal(ColituConnectSteps.Whitelist);
    }

    [Fact]
    public void AdaptiveConnect_WhitelistMode_GeckoBeforeTheWhitelistRows_APickedServerIsNeverSwitched()
    {
        ColituConnectSteps.Plan(false, true, whitelistMode: true, ColituGeckoOffer.Offered).Should().Equal(ColituConnectSteps.Gecko, ColituConnectSteps.Whitelist);
        ColituConnectSteps.Plan(false, automatic: false, false, ColituGeckoOffer.Offered).Should().Equal(ColituConnectSteps.Normal);
    }

    [Fact]
    public void ManualPick_IsGeckoOnly_NoSilentSwitch()
    {
        ColituConnectSteps.Plan(geckoPicked: true, automatic: true, false, ColituGeckoOffer.Offered).Should().Equal(ColituConnectSteps.Gecko);
        ColituConnectSteps.Plan(geckoPicked: true, automatic: false, true, ColituGeckoOffer.Unavailable).Should().Equal(ColituConnectSteps.Gecko);
        ColituConnectSteps.GeckoPicked(ColituGeckoRules.Protocol, false, ColituGeckoOffer.Offered).Should().BeTrue();
        ColituConnectSteps.GeckoPicked(ColituGeckoRules.Protocol, false, ColituGeckoOffer.Unavailable).Should().BeTrue("offered but unavailable: the connect error says so");
        ColituConnectSteps.GeckoPicked(ColituGeckoRules.Protocol, false, ColituGeckoOffer.Unknown).Should().BeTrue();
        ColituConnectSteps.GeckoPicked(ColituGeckoRules.Protocol, false, ColituGeckoOffer.NotOffered).Should().BeFalse("404 hides Gecko: automatic");
        ColituConnectSteps.GeckoPicked(ColituGeckoRules.Protocol, whitelistSelected: true, ColituGeckoOffer.Offered).Should().BeFalse("a Whitelist row has no method");
        ColituConnectSteps.GeckoPicked("hysteria2", false, ColituGeckoOffer.Offered).Should().BeFalse();
    }

    [Fact]
    public void CoolDown_SkipsGeckoInAutomatic_ButNotOnAManualPick()
    {
        var cooldown = new ColituGeckoCooldown();
        var now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        cooldown.Active("net-a", now).Should().BeFalse();
        cooldown.Failed("net-a", now);
        cooldown.Active("net-a", now.AddMinutes(9)).Should().BeTrue();
        cooldown.Active("net-b", now.AddMinutes(1)).Should().BeFalse("per network");
        cooldown.Active("net-a", now.AddMinutes(10)).Should().BeFalse();
        var active = cooldown.Active("net-a", now.AddMinutes(1));
        ColituGeckoRules.OffReason(ColituGeckoOffer.Offered, true, false, active, manual: false).Should().NotBeNull("Auto skips Gecko while it cools down");
        ColituGeckoRules.OffReason(ColituGeckoOffer.Offered, true, false, active, manual: true).Should().BeNull("a manual pick tries it anyway");
        cooldown.Succeeded("net-a");
        cooldown.Active("net-a", now.AddMinutes(1)).Should().BeFalse("a working Gecko connection ends it");
        ColituGeckoRules.CoolDown.Should().Be(TimeSpan.FromMinutes(10));
        ColituGeckoRules.ConfigWait.Should().Be(TimeSpan.FromSeconds(6));
    }

    // ── Protocol menu: offer states ────────────────────────────────────────

    [Fact]
    public void ProtocolMenu_404HidesGecko_503ShowsItDisabled_200ShowsIt()
    {
        static List<string> Methods(ColituGeckoOffer offer) => ColituTransportChoice.Menu(offer).Select(entry => entry.Method).ToList();
        Methods(ColituGeckoOffer.NotOffered).Should().Equal("");
        Methods(ColituGeckoOffer.Unknown).Should().NotContain(ColituTransportChoice.Gecko, "not known yet: not shown");
        ColituTransportChoice.Menu(ColituGeckoOffer.Offered)[1].Should().Be(new ColituMethodEntry(ColituGeckoRules.Protocol, true));
        ColituTransportChoice.Menu(ColituGeckoOffer.Unavailable)[1].Should().Be(new ColituMethodEntry(ColituGeckoRules.Protocol, false));
        Methods(ColituGeckoOffer.Offered).Should().Equal("", ColituGeckoRules.Protocol);

        ColituTransportChoice.Normalize("COLITU-GECKO").Should().Be(ColituGeckoRules.Protocol);
        ColituTransportChoice.Shown(ColituGeckoRules.Protocol, ColituGeckoOffer.NotOffered).Should().Be("", "the pick reads as automatic while Gecko is not offered");
        ColituTransportChoice.Shown(ColituGeckoRules.Protocol, ColituGeckoOffer.Offered).Should().Be(ColituGeckoRules.Protocol);
        ColituTransportChoice.Normalize("hysteria2").Should().Be("", "only automatic and ColituGecko are picks here");
        ColituTransportNames.Of(ColituGeckoRules.Protocol, Loc.I).Should().Be("ColituGecko");
    }

    [Fact]
    public void OldToggle_IsGone_AndItsStoredKeyIsIgnored()
    {
        typeof(ColituVpnPreferences).GetProperty("GeckoAlphaEnabled").Should().BeNull("no toggle: the panel's offer decides");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var session = JsonSerializer.Deserialize<ColituVpnSession>("""{"Status":0,"Preferences":{"GeckoAlphaEnabled":false,"PreferredTransport":"colitu-gecko"},"PreferencesMigration":2}""", options)!;
        session.Preferences.Normalize().PreferredTransport.Should().Be(ColituGeckoRules.Protocol, "the pick is kept across restarts");
        JsonSerializer.Serialize(session, options).Should().NotContain("GeckoAlphaEnabled");
    }

    [Fact]
    public void Strings_ExistInEveryLanguage_WithoutAlpha()
    {
        foreach (var key in new[] { "transport.colitu-gecko", "gecko.autoExit", "gecko.unavailable", "connect.gecko", "err.gecko", "err.geckoUnavailable" })
        {
            Loc.ValuesOf(key).Should().HaveCount(Loc.Languages.Length).And.OnlyContain(value => value.Length > 0);
            Loc.ValuesOf(key).Should().OnlyContain(value => !value.Contains("alpha", StringComparison.OrdinalIgnoreCase) && !value.Contains("альфа", StringComparison.OrdinalIgnoreCase));
        }
        Loc.ValuesOf("transport.colitu-gecko").Should().OnlyContain(value => value == "ColituGecko");
        // [ru, tr, en]
        Loc.ValuesOf("gecko.autoExit").Take(3).Should().Equal("автовыход", "otomatik çıkış", "automatic exit");
        Loc.Keys.Should().NotContain(["settings.geckoAlpha", "settings.geckoAlphaHint"], "the Advanced-mode toggle is gone");
        foreach (var key in new[] { "transport.auto", "home.protocol.hint" })
        {
            Loc.ValuesOf(key).Should().HaveCount(Loc.Languages.Length).And.OnlyContain(value => value.Length > 0);
        }
    }

    // ── Entry addresses and the kill switch ────────────────────────────────

    [Fact]
    public void EntryTargets_ReadAddressesAndFrontNames_AndSkipBrokenEntries()
    {
        var config = ConfigWith(
            Entry("e1", V4("203.0.113.9", 443)),
            Entry("e2", V6("2001:db8::7", 8443)),
            Entry("e3", Domain("front.example.test", 443)),
            "zz-not-hex",
            "0105",
            Entry("e1", V4("203.0.113.9", 443)));
        ColituGeckoRules.EntryTargets(config).Should().Equal(
            new ColituGeckoTarget("203.0.113.9", 443),
            new ColituGeckoTarget("2001:db8::7", 8443),
            new ColituGeckoTarget("front.example.test", 443));
        ColituGeckoRules.EntryTargets("nonsense").Should().BeEmpty();
        ColituGeckoRules.EntryTargets(null).Should().BeEmpty();
    }

    [Fact]
    public void KillSwitch_PermitsTheGeckoEntries_ForTheAppsUserOnly_OnTheirPorts()
    {
        var allowed = new ColituKillSwitch.Allowance
        {
            Servers = [IPAddress.Parse("198.51.100.1")],
            Gecko = [new(IPAddress.Parse("203.0.113.9"), 443), new(IPAddress.Parse("203.0.113.9"), 8443), new(IPAddress.Parse("2001:db8::7"), 443)]
        };
        var script = ColituKillSwitch.BuildEngageScript(allowed, 4242, null, true, 1000);
        script.Should().Contain("meta skuid 1000 ip daddr { 203.0.113.9 } tcp dport { 443, 8443 } accept");
        script.Should().Contain("meta skuid 1000 ip daddr { 203.0.113.9 } udp dport { 443, 8443 } accept");
        script.Should().Contain("meta skuid 1000 ip6 daddr { 2001:db8::7 } tcp dport { 443, 8443 } accept");
        script.Should().NotContain("ip daddr { 203.0.113.9 } accept", "never an all-ports rule");

        // Without the user id there is no owner to limit it to: nothing is opened.
        ColituKillSwitch.BuildEngageScript(allowed, 4242, null, true, null).Should().NotContain("203.0.113.9");
        // Nothing for Gecko when there are no entries.
        ColituKillSwitch.BuildEngageScript(new ColituKillSwitch.Allowance { Servers = allowed.Servers }, 4242, null, true, 1000).Should().NotContain("203.0.113.9");
    }

    // ── Core configs ───────────────────────────────────────────────────────

    private const string XrayTemplate = """
    { "inbounds": [ { "tag": "socks", "protocol": "socks", "port": 10808 }, { "tag": "colitu-check", "protocol": "socks", "port": 10810 } ],
      "outbounds": [ { "tag": "proxy", "protocol": "vless", "settings": { "vnext": [] } }, { "tag": "direct", "protocol": "freedom" } ],
      "routing": { "rules": [
        { "type": "field", "inboundTag": [ "colitu-check" ], "outboundTag": "proxy" },
        { "type": "field", "ip": [ "geoip:ru" ], "outboundTag": "direct" },
        { "type": "field", "domain": [ "domain:example.org" ], "network": "udp", "outboundTag": "proxy" },
        { "type": "field", "domain": [ "domain:example.net" ], "outboundTag": "proxy" } ] } }
    """;

    private static string SingboxConfig(bool tun) => $$"""
    { "inbounds": [ { "type": "{{(tun ? "tun" : "mixed")}}", "tag": "in" } ],
      "outbounds": [ { "type": "hysteria2", "tag": "proxy" }, { "type": "direct", "tag": "direct" } ],
      "route": { "final": "proxy", "rules": [
        { "ip_cidr": [ "10.0.0.0/8" ], "outbound": "direct" },
        { "domain_suffix": [ "example.org" ], "outbound": "proxy" },
        { "network": [ "udp" ], "outbound": "proxy" } ] } }
    """;

    private static List<JsonObject> Rules(string json, bool singBox)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        return (singBox ? root["route"]!["rules"] : root["routing"]!["rules"])!.AsArray().Select(rule => rule!.AsObject()).ToList();
    }

    /// <summary>The core config the app's generator makes for the Gecko SOCKS profile (loopback SOCKS "proxy"), with the app's routing rules.</summary>
    private static string GeckoProfileConfig(bool singBox, bool tun) => singBox
        ? $$"""
          { "inbounds": [ { "type": "{{(tun ? "tun" : "mixed")}}", "tag": "in" } ],
            "dns": { "servers": [ { "tag": "remote", "type": "https", "server": "1.1.1.1", "detour": "proxy" }, { "tag": "local", "type": "udp", "server": "1.1.1.1" } ] },
            "outbounds": [ { "type": "socks", "tag": "proxy", "server": "127.0.0.1", "server_port": 1080, "version": "5" },
                           { "type": "direct", "tag": "direct" }, { "type": "block", "tag": "block" } ],
            "route": { "final": "proxy", "rules": [
              { "action": "sniff" },
              { "port": [ 53 ], "action": "hijack-dns" },
              { "ip_is_private": true, "outbound": "direct" },
              { "rule_set": [ "geoip-ru" ], "outbound": "direct" },
              { "rule_set": [ "geosite-category-ru" ], "outbound": "direct" },
              { "domain_suffix": [ "example.org" ], "outbound": "proxy" },
              { "network": [ "udp" ], "outbound": "proxy" } ] } }
          """
        : """
          { "inbounds": [ { "tag": "socks", "protocol": "socks", "port": 10808 } ],
            "outbounds": [ { "tag": "proxy", "protocol": "socks", "settings": { "servers": [ { "address": "127.0.0.1", "port": 1080 } ] } },
                           { "tag": "direct", "protocol": "freedom" }, { "tag": "block", "protocol": "blackhole" } ],
            "routing": { "rules": [
              { "type": "field", "ip": [ "geoip:private" ], "outboundTag": "direct" },
              { "type": "field", "ip": [ "geoip:ru" ], "outboundTag": "direct" },
              { "type": "field", "domain": [ "geosite:category-ru" ], "outboundTag": "direct" },
              { "type": "field", "port": "53", "outboundTag": "proxy" },
              { "type": "field", "network": "tcp,udp", "outboundTag": "proxy" } ] } }
          """;

    private static List<JsonObject> Rules(JsonObject root, bool singBox) =>
        (singBox ? root["route"]!["rules"] : root["routing"]!["rules"])!.AsArray().Select(rule => rule!.AsObject()).ToList();

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void GeckoOnly_TcpAndUdpGoToGecko_RuAndPrivateDirect_NoServerOutbound(bool singBox, bool tun)
    {
        var before = GeckoProfileConfig(singBox, tun);
        before.Should().NotContain(SocksPassword, "the per-run account is never in the stored profile");
        var applied = ColituGeckoRules.ApplyOnly(before, singBox, Plan())!;
        applied.Should().NotBeNull();
        ColituGeckoRules.IsGeckoOnly(applied, singBox, 42000).Should().BeTrue("gecko-client is the only way out");
        var root = JsonNode.Parse(applied)!.AsObject();
        var outbounds = root["outbounds"]!.AsArray().Select(item => item!.AsObject()).ToList();
        var proxy = outbounds.Single(item => S(item["tag"]) == "proxy");
        if (singBox)
        {
            S(proxy["type"]).Should().Be("socks");
            S(proxy["server"]).Should().Be("127.0.0.1");
            proxy["server_port"]!.GetValue<int>().Should().Be(42000);
            S(proxy["username"]).Should().Be(SocksUser);
            S(proxy["password"]).Should().Be(SocksPassword);
            proxy.ContainsKey("network").Should().BeFalse("no network field: TCP and UDP (UDP ASSOCIATE)");
        }
        else
        {
            S(proxy["protocol"]).Should().Be("socks");
            proxy["settings"]!["servers"]![0]!["port"]!.GetValue<int>().Should().Be(42000);
            S(proxy["settings"]!["servers"]![0]!["users"]![0]!["user"]).Should().Be(SocksUser);
            S(proxy["settings"]!["servers"]![0]!["users"]![0]!["pass"]).Should().Be(SocksPassword);
            S(outbounds[0]["tag"]).Should().Be("proxy", "unmatched traffic goes to the first outbound: Gecko");
        }
        outbounds.Should().NotContain(item => S(item["tag"]) == ColituGeckoRules.OutboundTag, "no alpha chain outbound");
        outbounds.Count.Should().Be(3, "proxy, direct, block: no server outbound");

        var rules = Rules(root, singBox);
        var target = singBox ? "outbound" : "outboundTag";
        // Nothing routes traffic to a server, and no rule splits TCP from UDP.
        rules.Where(rule => S(rule[target]) is { }).Should().OnlyContain(rule => S(rule[target]) == "proxy" || S(rule[target]) == "direct" || S(rule[target]) == "block");
        rules.Where(rule => S(rule[target]) == "proxy").Should().OnlyContain(rule => rule["network"] == null || rule["network"]!.ToJsonString().Contains("udp"));
        // Russian and private destinations direct, ahead of the proxy rules.
        var ru = rules.FindIndex(rule => S(rule[target]) == "direct" && (rule.ToJsonString().Contains("geoip:ru") || rule.ToJsonString().Contains("geoip-ru")));
        var lan = rules.FindIndex(rule => S(rule[target]) == "direct" && (rule.ToJsonString().Contains("geoip:private") || rule.ToJsonString().Contains("ip_is_private")));
        var firstProxy = rules.FindIndex(rule => S(rule[target]) == "proxy");
        ru.Should().BeGreaterThanOrEqualTo(0, "Russian destinations go direct: the anchors refuse them");
        lan.Should().BeGreaterThanOrEqualTo(0, "the local network stays direct");
        ru.Should().BeLessThan(firstProxy);
        lan.Should().BeLessThan(firstProxy);
        if (singBox && tun)
        {
            // gecko-client's own connections leave directly (its DNS answered by sing-box), before every other rule, by full path.
            S(rules[0]["action"]).Should().Be("hijack-dns");
            S(rules[0]["process_path"]![0]).Should().Be(ClientPath);
            ColituGeckoRules.IsDirectRule(rules[1], ClientPath).Should().BeTrue();
            rules[1]["process_name"].Should().BeNull("a file name alone matches any program called that");
            ColituGeckoRules.HasDirectRules(applied, ClientPath).Should().BeTrue();
        }
        else
        {
            applied.Should().NotContain("process_path");
        }
        ColituGeckoRules.ApplyOnly(applied, singBox, Plan()).Should().NotBeNull("a restart rebuilds the config from the profile, never from the applied one");
    }

    [Fact]
    public void GeckoOnly_SingboxDnsServers_UseTheGeckoOutbound()
    {
        var applied = ColituGeckoRules.ApplyOnly(GeckoProfileConfig(singBox: true, tun: true), true, Plan())!;
        var root = JsonNode.Parse(applied)!.AsObject();
        var tags = root["outbounds"]!.AsArray().Select(item => S(item!["tag"])).ToHashSet();
        var servers = root["dns"]!["servers"]!.AsArray().Select(item => item!.AsObject()).ToList();
        servers.Should().NotBeEmpty();
        // A detour names an outbound that exists: the remote resolver goes through "proxy" (= gecko-client).
        servers.Where(server => S(server["detour"]) is { }).Should().OnlyContain(server => tags.Contains(S(server["detour"])));
        servers.Should().Contain(server => S(server["detour"]) == "proxy");
    }

    [Fact]
    public void ApplyOnly_NeverReplacesAServerOutbound_AndRefusesOddShapes()
    {
        ColituGeckoRules.ApplyOnly(XrayTemplate, false, Plan()).Should().BeNull("a VLESS server is not the Gecko profile");
        ColituGeckoRules.ApplyOnly(SingboxConfig(tun: true), true, Plan()).Should().BeNull("a Hysteria2 server is not the Gecko profile");
        ColituGeckoRules.ApplyOnly("""{ "outbounds": [ { "tag": "direct", "protocol": "freedom" } ] }""", false, Plan()).Should().BeNull();
        ColituGeckoRules.ApplyOnly("{", false, Plan()).Should().BeNull();
        ColituGeckoRules.ApplyOnly(GeckoProfileConfig(true, true), true, Plan() with { ClientPath = "gecko-client" }).Should().BeNull("TUN needs the sidecar's full path");
        ColituGeckoRules.ApplyOnly(GeckoProfileConfig(true, false), true, Plan() with { ClientPath = "gecko-client" }).Should().NotBeNull("no TUN, no process rule");
        ColituGeckoRules.AddSidecarDirectRules(SingboxConfig(true), "gecko-client").Should().BeNull();
    }

    [Fact]
    public void ApplyOnly_DropsEveryOtherCarrier_WithItsRulesAndBalancers()
    {
        const string xray = """
        { "outbounds": [ { "tag": "proxy", "protocol": "socks", "settings": { "servers": [ { "address": "127.0.0.1", "port": 1080 } ] } },
                         { "tag": "spare", "protocol": "vless" }, { "tag": "direct", "protocol": "freedom" }, { "tag": "block", "protocol": "blackhole" } ],
          "routing": { "balancers": [ { "tag": "colitu-spare", "selector": [ "proxy", "spare" ] } ],
                       "rules": [ { "type": "field", "ip": [ "geoip:ru" ], "outboundTag": "direct" },
                                  { "type": "field", "inboundTag": [ "colitu-verify-spare" ], "outboundTag": "spare" },
                                  { "type": "field", "network": "tcp,udp", "balancerTag": "colitu-spare" },
                                  { "type": "field", "port": "53", "outboundTag": "proxy" } ] } }
        """;
        var x = JsonNode.Parse(ColituGeckoRules.ApplyOnly(xray, false, Plan())!)!.AsObject();
        x["outbounds"]!.AsArray().Select(item => S(item!["tag"])).Should().Equal("proxy", "direct", "block");
        x["routing"]!["balancers"].Should().BeNull();
        Rules(x, false).Select(rule => S(rule["outboundTag"])).Should().Equal("direct", "proxy");

        const string singbox = """
        { "inbounds": [ { "type": "mixed", "tag": "socks" } ],
          "outbounds": [ { "type": "socks", "tag": "proxy", "server": "127.0.0.1", "server_port": 1080 },
                         { "type": "hysteria2", "tag": "spare" }, { "type": "direct", "tag": "direct" } ],
          "route": { "final": "spare", "rules": [ { "inbound": [ "colitu-verify-spare" ], "outbound": "spare" },
                                                  { "rule_set": [ "geoip-ru" ], "outbound": "direct" },
                                                  { "action": "sniff" } ] } }
        """;
        var b = JsonNode.Parse(ColituGeckoRules.ApplyOnly(singbox, true, Plan())!)!.AsObject();
        b["outbounds"]!.AsArray().Select(item => S(item!["tag"])).Should().Equal("proxy", "direct");
        S(b["route"]!["final"]).Should().Be("proxy");
        Rules(b, true).Should().HaveCount(2).And.NotContain(rule => S(rule["outbound"]) == "spare");
        ColituGeckoRules.IsGeckoOnly(b.ToJsonString(), true, 42000).Should().BeTrue();
        ColituGeckoRules.IsGeckoOnly(singbox, true, 1080).Should().BeFalse("a spare could still carry traffic");
    }

    [Fact]
    public void GeckoProfile_HoldsNoSecret()
    {
        var profile = ColituGeckoRules.Profile("colitu-api", 42000);
        profile.IndexId.Should().Be(ColituGeckoRules.ProfileIndexId);
        profile.ConfigType.Should().Be(ServiceLib.Enums.EConfigType.SOCKS);
        profile.Address.Should().Be("127.0.0.1");
        profile.Username.Should().BeNullOrEmpty();
        profile.Password.Should().BeNullOrEmpty();
    }

    [Fact]
    public void TunFront_GetsTheDirectRules_Once_AndANonTunConfigIsUntouched()
    {
        const string front = """
        { "inbounds": [ { "type": "tun", "tag": "tun-in" } ],
          "outbounds": [ { "type": "socks", "tag": "proxy", "server": "127.0.0.1", "server_port": 10808 } ],
          "route": { "final": "proxy", "rules": [ { "process_name": [ "xray", "sing-box" ], "outbound": "direct" } ] } }
        """;
        var applied = ColituGeckoRules.AddSidecarDirectRules(front, ClientPath)!;
        var root = JsonNode.Parse(applied)!.AsObject();
        var rules = Rules(applied, true);
        S(rules[0]["action"]).Should().Be("hijack-dns");
        ColituGeckoRules.IsDirectRule(rules[1], ClientPath).Should().BeTrue();
        root["outbounds"]!.AsArray().Should().Contain(item => S(item!["tag"]) == "direct" && S(item!["type"]) == "direct");
        root["outbounds"]!.AsArray().Should().NotContain(item => S(item!["tag"]) == ColituGeckoRules.OutboundTag);
        ColituGeckoRules.AddSidecarDirectRules(applied, ClientPath).Should().BeSameAs(applied, "already there");
        ColituGeckoRules.AddSidecarDirectRules(XrayTemplate, ClientPath).Should().BeSameAs(XrayTemplate, "no TUN inbound");
        ColituGeckoRules.AddSidecarDirectRules("not json", ClientPath).Should().BeNull();
    }

    [Fact]
    public void ProcessRules_PassTheRootCorePolicyShape()
    {
        // colitu-core-root accepts process_path values anywhere (they are matches, not files the core opens).
        var applied = ColituGeckoRules.ApplyOnly(GeckoProfileConfig(true, true), true, Plan())!;
        applied.Should().Contain("\"process_path\"").And.NotContain("process_path_regex");
    }

    // ── Sidecar process ────────────────────────────────────────────────────

    [Fact]
    public void Sidecar_MissingBinary_Throws_AndLogsNoSecret()
    {
        var dir = Path.Combine(Path.GetTempPath(), "colitu-gecko-test-" + Guid.NewGuid().ToString("N"));
        ColituGecko.PathOverride = Path.Combine(dir, "missing", "gecko-client");
        var log = new List<string>();
        try
        {
            var start = () => ColituGecko.Instance.Start(new ColituGeckoLaunch(42000, SocksUser, SocksPassword), Signed(), null, log.Add);
            start.Should().Throw<Exception>().Which.Message.Should().NotContain(ConfigSecret).And.NotContain(SocksPassword);
            ColituGecko.Instance.Running.Should().BeFalse();
            ColituGecko.Instance.SendConfig(Signed()).Should().BeFalse("nothing runs");
            log.Should().OnlyContain(line => !line.Contains(ConfigSecret) && !line.Contains(SocksPassword));
            Directory.Exists(dir).Should().BeFalse();
        }
        finally
        {
            ColituGecko.PathOverride = null;
            ColituGecko.Instance.Stop();
        }
    }

    [Fact]
    public async Task Sidecar_ARunningProcess_GetsSecretsOnStdin_ReportsReady_AndStopsWhenAsked()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        // A stand-in sidecar (sh): reads the first line, prints the ready line and a session event, ends with its input.
        var dir = Path.Combine(Path.GetTempPath(), "colitu-gecko-fake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "gecko-client");
        await File.WriteAllTextAsync(path, "#!/bin/sh\nread first\necho \"gecko-client: SOCKS5 on 127.0.0.1:42000\" >&2\necho 'gecko-client: event {\"event\":\"session\",\"anchor\":1,\"bootstrap\":false}' >&2\necho 'gecko-client: event {\"event\":\"config\",\"issued\":1763190000}' >&2\necho 'gecko-client: event {\"event\":\"skin_revs\",\"revs\":{\"s1\":3}}' >&2\necho \"$first\" > \"$0.first\"\ncat >/dev/null\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        ColituGecko.PathOverride = path;
        var log = new List<string>();
        var floors = new List<(long?, IReadOnlyDictionary<string, long>?)>();
        void OnFloor(long? issued, IReadOnlyDictionary<string, long>? revs) { lock (floors) floors.Add((issued, revs)); }
        ColituGecko.Instance.FloorChanged += OnFloor;
        try
        {
            var floor = new ColituGeckoFloor(1_763_000_000, new Dictionary<string, long> { ["s1"] = 2 });
            var run = ColituGecko.Instance.Start(new ColituGeckoLaunch(42000, SocksUser, SocksPassword), Signed(), floor, log.Add);
            (await run.Ready.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be("");
            ColituGecko.Instance.Running.Should().BeTrue();
            // What the app kept goes in on the first line; what the sidecar reports comes back to be kept.
            var first = JsonNode.Parse(await File.ReadAllTextAsync(path + ".first"))!.AsObject();
            Convert.FromBase64String(S(first["config_b64"])!).Should().Equal(ConfigBytes);
            S(first["config_sig"]).Should().Be(Signature);
            first["min_issued"]!.GetValue<long>().Should().Be(1_763_000_000);
            first["skin_floor"]!["s1"]!.GetValue<long>().Should().Be(2);
            for (var i = 0; i < 50 && floors.Count < 2; i++) await Task.Delay(100);
            lock (floors)
            {
                floors.Should().Contain(f => f.Item1 == 1_763_190_000);
                floors.Should().Contain(f => f.Item2 != null && f.Item2["s1"] == 3);
            }
            ColituGecko.Instance.SendConfig(Signed()).Should().BeTrue();
            ColituGecko.Instance.Stop();
            ColituGecko.Instance.Running.Should().BeFalse();
            log.Should().OnlyContain(line => !line.Contains(ConfigSecret) && !line.Contains(SocksPassword));
        }
        finally
        {
            ColituGecko.Instance.FloorChanged -= OnFloor;
            ColituGecko.PathOverride = null;
            ColituGecko.Instance.Stop();
            Directory.Delete(dir, true);
        }
    }

    // ── Grip failure reports to the panel ──────────────────────────────────

    private static ColituGeckoEvent Grip(string entry = "e1", string cls = "frozen") =>
        ColituGeckoRules.ParseEvent($"gecko-client: event {{\"event\":\"grip\",\"entry\":\"{entry}\",\"skin\":\"s1\",\"rev\":3,\"class\":\"{cls}\",\"bytes\":20000,\"bucket\":\"4-32KiB\"}}")!;

    [Fact]
    public void GripEvent_KeepsTheEntryForThePanel_ButNeverDescribesIt()
    {
        var grip = Grip("secret-entry");
        grip.Entry.Should().Be("secret-entry");
        grip.Describe().Should().NotContain("secret-entry");
    }

    [Fact]
    public async Task Reports_GoInBatchesOfAHundred_AsTheContractSays_AndSurviveFailures()
    {
        var reports = new ColituGeckoReports { App = "3.1.4" };
        for (var i = 0; i < 350; i++)
        {
            reports.Add(Grip($"e{i}"), DateTimeOffset.UtcNow);
        }
        reports.Waiting.Should().Be(ColituGeckoReports.MaxWaiting);
        var bodies = new List<string>();
        var now = DateTimeOffset.UtcNow;
        (await reports.FlushAsync(body => { bodies.Add(body); return Task.FromResult(HttpStatusCode.NoContent); }, now)).Should().Be(100);
        var first = JsonNode.Parse(bodies[0])!.AsObject();
        first["v"]!.GetValue<int>().Should().Be(1);
        first["reports"]!.AsArray().Count.Should().Be(100);
        first["reports"]![0]!["rev"]!.GetValue<int>().Should().Be(3);
        first["reports"]![0]!.AsObject().Select(p => p.Key).Should().BeEquivalentTo(["entry", "skin", "rev", "class", "bucket", "at", "app"]);
        // Not before the interval has passed.
        (await reports.FlushAsync(_ => Task.FromResult(HttpStatusCode.NoContent), now.AddSeconds(5))).Should().Be(0);
        // A failure keeps the batch.
        (await reports.FlushAsync(_ => Task.FromResult(HttpStatusCode.BadGateway), now.AddSeconds(61))).Should().Be(0);
        reports.Waiting.Should().Be(200);
        // 429 pauses ten minutes; 404 stops and clears.
        (await reports.FlushAsync(_ => Task.FromResult(HttpStatusCode.TooManyRequests), now.AddSeconds(130))).Should().Be(0);
        (await reports.FlushAsync(_ => Task.FromResult(HttpStatusCode.NoContent), now.AddMinutes(5))).Should().Be(0);
        (await reports.FlushAsync(_ => Task.FromResult(HttpStatusCode.NotFound), now.AddMinutes(13))).Should().Be(0);
        reports.Waiting.Should().Be(0);
        reports.Add(Grip(), now).Should().BeTrue();
        (await reports.FlushAsync(_ => Task.FromResult(HttpStatusCode.NoContent), now.AddMinutes(30))).Should().Be(0, "off until the next offer");
        reports.Resume();
        (await reports.FlushAsync(_ => Task.FromResult(HttpStatusCode.NoContent), now.AddMinutes(31))).Should().Be(1);
    }

    [Theory]
    [InlineData(0, "<50")] [InlineData(49.9, "<50")] [InlineData(50, "50-100")] [InlineData(99.9, "50-100")]
    [InlineData(100, "100-200")] [InlineData(199.9, "100-200")] [InlineData(200, "200-400")] [InlineData(399.9, "200-400")]
    [InlineData(400, ">400")] [InlineData(5000, ">400")]
    public void Buckets_Rtt_EveryBoundary(double ms, string label) => ColituGeckoBuckets.Rtt(ms).Should().Be(label);

    [Theory]
    [InlineData(0, "<1")] [InlineData(0.99, "<1")] [InlineData(1, "1-5")] [InlineData(4.99, "1-5")]
    [InlineData(5, "5-20")] [InlineData(19.9, "5-20")] [InlineData(20, "20-100")] [InlineData(99.9, "20-100")]
    [InlineData(100, ">100")] [InlineData(900, ">100")]
    public void Buckets_Rate_EveryBoundary(double mbit, string label) => ColituGeckoBuckets.Rate(mbit).Should().Be(label);

    [Theory]
    [InlineData(0, "<10s")] [InlineData(9.9, "<10s")] [InlineData(10, "10-60s")] [InlineData(59.9, "10-60s")]
    [InlineData(60, "1-10m")] [InlineData(599.9, "1-10m")] [InlineData(600, ">10m")] [InlineData(86400, ">10m")]
    public void Buckets_Life_EveryBoundary(double seconds, string label) => ColituGeckoBuckets.Life(TimeSpan.FromSeconds(seconds)).Should().Be(label);

    [Fact]
    public void Buckets_RefuseUnusableValues_AndBadVersions()
    {
        ColituGeckoBuckets.Rtt(-1).Should().BeNull();
        ColituGeckoBuckets.Rate(double.NaN).Should().BeNull();
        ColituGeckoBuckets.Life(TimeSpan.FromSeconds(-1)).Should().BeNull();
        ColituGeckoBuckets.MbitPerSecond(1_000_000, TimeSpan.Zero).Should().BeNull();
        ColituGeckoBuckets.MbitPerSecond(12_500_000, TimeSpan.FromSeconds(10)).Should().Be(10);
        ColituGeckoBuckets.App("3.1.4+abc-1").Should().Be("3.1.4+abc-1");
        ColituGeckoBuckets.App("").Should().BeNull();
        ColituGeckoBuckets.App(new string('1', 33)).Should().BeNull();
        ColituGeckoBuckets.App("3.1 4").Should().BeNull();
    }

    [Fact]
    public async Task Reports_Ok_CarriesOnlyLabels_AndNoRawNumber()
    {
        var reports = new ColituGeckoReports { App = "3.1.4" };
        var now = DateTimeOffset.FromUnixTimeSeconds(1_763_190_000);
        reports.AddOk("e1", "s1", "3", now, rttMs: 73.4, rateMbit: ColituGeckoBuckets.MbitPerSecond(5_000_000, TimeSpan.FromSeconds(10)), life: TimeSpan.FromSeconds(754)).Should().BeTrue();
        string? body = null;
        await reports.FlushAsync(b => { body = b; return Task.FromResult(System.Net.HttpStatusCode.NoContent); }, now);
        var r = JsonNode.Parse(body!)!["reports"]!.AsArray()[0]!.AsObject();
        r.Select(p => p.Key).Should().BeEquivalentTo(["entry", "skin", "rev", "class", "at", "rtt", "rate", "life", "app"]);
        r["class"]!.GetValue<string>().Should().Be("ok");
        r["rtt"]!.GetValue<string>().Should().Be("50-100");
        r["rate"]!.GetValue<string>().Should().Be("1-5");
        r["life"]!.GetValue<string>().Should().Be(">10m");
        body.Should().NotContain("73").And.NotContain("754").And.NotContain("5000000");
        foreach (var key in new[] { "rtt", "rate", "life" }) r[key]!.GetValue<string>().Should().MatchRegex("^[<>0-9a-z-]+$");
    }

    [Fact]
    public async Task Reports_Ok_OnePerEntryPerTenMinutes_AndUnknownValuesAreLeftOut()
    {
        var reports = new ColituGeckoReports { App = null };
        var now = DateTimeOffset.FromUnixTimeSeconds(1_763_190_000);
        reports.AddOk("e1", "s1", "3", now).Should().BeTrue();
        reports.AddOk("e1", "s1", "3", now.AddMinutes(9)).Should().BeFalse();
        reports.AddOk("e2", "s1", "3", now.AddMinutes(9)).Should().BeTrue();
        reports.AddOk("e1", "s1", "3", now.AddMinutes(10)).Should().BeTrue();
        reports.Waiting.Should().Be(3);
        string? body = null;
        await reports.FlushAsync(b => { body = b; return Task.FromResult(System.Net.HttpStatusCode.NoContent); }, now.AddMinutes(11));
        JsonNode.Parse(body!)!["reports"]!.AsArray()[0]!.AsObject().Select(p => p.Key).Should().BeEquivalentTo(["entry", "skin", "rev", "class", "at"]);
    }

    [Fact]
    public async Task Reports_KeepAtMostThreeHundred_AndSurviveAFailure()
    {
        var reports = new ColituGeckoReports();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 320; i++) reports.Add(Grip($"e{i}"), now);
        reports.Waiting.Should().Be(ColituGeckoReports.MaxWaiting);
        (await reports.FlushAsync(_ => throw new System.Net.Http.HttpRequestException("down"), now)).Should().Be(0);
        reports.Waiting.Should().Be(ColituGeckoReports.MaxWaiting, "a failed call loses nothing");
        string? body = null;
        await reports.FlushAsync(b => { body = b; return Task.FromResult(System.Net.HttpStatusCode.NoContent); }, now.AddMinutes(2));
        JsonNode.Parse(body!)!["reports"]!.AsArray()[0]!["entry"]!.GetValue<string>().Should().Be("e20", "the oldest went first when the cap was hit");
    }

    private static string GripEndLine(string cls, string entry = "e1") =>
        $"gecko-client: event {{\"event\":\"grip_end\",\"entry\":\"{entry}\",\"skin\":\"s1\",\"rev\":3,\"class\":\"{cls}\",\"life_ms\":754321,\"sent\":111,\"received\":98765,\"rtt_ms\":73,\"goodput\":333444}}";

    [Fact]
    public async Task GripEnd_Ok_BecomesASlicedReport_WithoutRawNumbers()
    {
        var reports = new ColituGeckoReports { App = "3.1.4" };
        var evt = ColituGeckoRules.ParseEvent(GripEndLine("ok"))!;
        evt.Describe().Should().NotContain("754321").And.NotContain("333444");
        var now = DateTimeOffset.FromUnixTimeSeconds(1_763_190_000);
        reports.AddEnd(evt, now).Should().BeTrue();
        string? body = null;
        await reports.FlushAsync(b => { body = b; return Task.FromResult(System.Net.HttpStatusCode.NoContent); }, now);
        var r = JsonNode.Parse(body!)!["reports"]!.AsArray()[0]!.AsObject();
        r.Select(p => p.Key).Should().BeEquivalentTo(["entry", "skin", "rev", "class", "at", "rtt", "rate", "life", "app"]);
        r["class"]!.GetValue<string>().Should().Be("ok");
        r["rtt"]!.GetValue<string>().Should().Be("50-100");
        r["rate"]!.GetValue<string>().Should().Be("<1", "98765 B in 754 s is 0.001 Mbit/s; the goodput (upload direction) is not used");
        r["life"]!.GetValue<string>().Should().Be(">10m");
        foreach (var raw in new[] { "73,", "754321", "333444", "98765", "111" }) body.Should().NotContain(raw);
    }

    [Fact]
    public void GripEnd_RateIsTheBusierDirectionOverLife_AndNonOkOrIncompleteGivesNothing()
    {
        var reports = new ColituGeckoReports { App = null };
        var now = DateTimeOffset.UtcNow;
        reports.AddEnd(ColituGeckoRules.ParseEvent(GripEndLine("frozen"))!, now).Should().BeFalse();
        reports.AddEnd(ColituGeckoRules.ParseEvent(GripEndLine("lost"))!, now).Should().BeFalse();
        reports.AddEnd(ColituGeckoRules.ParseEvent("gecko-client: event {\"event\":\"grip_end\",\"class\":\"ok\"}")!, now).Should().BeFalse();
        reports.Waiting.Should().Be(0);
        // goodput 0 and rtt 0: not measured; 2_500_000 B in 10 s = 2 Mbit/s.
        var evt = ColituGeckoRules.ParseEvent("gecko-client: event {\"event\":\"grip_end\",\"entry\":\"e1\",\"skin\":\"s1\",\"rev\":3,\"class\":\"ok\",\"life_ms\":10000,\"sent\":1,\"received\":2500000,\"rtt_ms\":0,\"goodput\":0}")!;
        reports.AddEnd(evt, now).Should().BeTrue();
        // a download seen on the canary: 158539591 B in 25389 ms is about 50 Mbit/s; the goodput (642 B/s) is the upload reading.
        var big = ColituGeckoRules.ParseEvent("gecko-client: event {\"event\":\"grip_end\",\"entry\":\"e3\",\"skin\":\"s1\",\"rev\":3,\"class\":\"ok\",\"life_ms\":25389,\"sent\":1000,\"received\":158539591,\"rtt_ms\":0,\"goodput\":642}")!;
        reports.AddEnd(big, now).Should().BeTrue();
        // an upload: sent counts when it is the larger one (25 MB in 10 s = 20 Mbit/s).
        var up = ColituGeckoRules.ParseEvent("gecko-client: event {\"event\":\"grip_end\",\"entry\":\"e4\",\"skin\":\"s1\",\"rev\":3,\"class\":\"ok\",\"life_ms\":10000,\"sent\":25000000,\"received\":100,\"rtt_ms\":0,\"goodput\":0}")!;
        reports.AddEnd(up, now).Should().BeTrue();
        // too short a life for a rate (under 1 s), still a life slice.
        var brief = ColituGeckoRules.ParseEvent("gecko-client: event {\"event\":\"grip_end\",\"entry\":\"e2\",\"skin\":\"s1\",\"rev\":3,\"class\":\"ok\",\"life_ms\":900,\"sent\":1,\"received\":5000,\"rtt_ms\":0,\"goodput\":0}")!;
        reports.AddEnd(brief, now).Should().BeTrue();
        var body = "";
        reports.FlushAsync(b => { body = b; return Task.FromResult(System.Net.HttpStatusCode.NoContent); }, now).GetAwaiter().GetResult();
        var list = JsonNode.Parse(body)!["reports"]!.AsArray();
        list[0]!.AsObject().Select(p => p.Key).Should().BeEquivalentTo(["entry", "skin", "rev", "class", "at", "rate", "life"]);
        list[0]!["rate"]!.GetValue<string>().Should().Be("1-5");
        list[1]!["rate"]!.GetValue<string>().Should().Be("20-100");
        list[2]!["rate"]!.GetValue<string>().Should().Be("20-100");
        list[3]!.AsObject().Select(p => p.Key).Should().BeEquivalentTo(["entry", "skin", "rev", "class", "at", "life"]);
    }

    [Fact]
    public void Reports_IgnoreOtherEventsAndIncompleteGrips()
    {
        var reports = new ColituGeckoReports();
        reports.Add(ColituGeckoRules.ParseEvent("gecko-client: event {\"event\":\"lost\"}")!, DateTimeOffset.UtcNow).Should().BeFalse();
        reports.Add(ColituGeckoRules.ParseEvent("gecko-client: event {\"event\":\"grip\",\"entry\":\"e1\"}")!, DateTimeOffset.UtcNow).Should().BeFalse();
        reports.Waiting.Should().Be(0);
    }

    // ── The label ──────────────────────────────────────────────────────────

    [Fact]
    public void Label_UsesTheBrandColours_AndTheLoopsAreTheSpecifiedOnes()
    {
        var (darkDeep, darkLight, _) = ColituGeckoLabel.Colors(dark: true);
        (darkDeep.ToString().ToUpperInvariant(), darkLight.ToString().ToUpperInvariant()).Should().Be(("#FF8F7CF0", "#FFC4B8FF"));
        var (lightDeep, lightLight, _) = ColituGeckoLabel.Colors(dark: false);
        (lightDeep.ToString().ToUpperInvariant(), lightLight.ToString().ToUpperInvariant()).Should().Be(("#FF262280", "#FF6650CC"));

        ColituGeckoLabel.SlideAt(TimeSpan.Zero).Should().Be(0);
        ColituGeckoLabel.SlideAt(TimeSpan.FromSeconds(1.75)).Should().BeApproximately(0.5, 1e-9);
        ColituGeckoLabel.SlideAt(TimeSpan.FromSeconds(3.5)).Should().BeApproximately(0, 1e-9, "one loop is 3.5 s");

        var (low, lowScale) = ColituGeckoLabel.BreathAt(TimeSpan.Zero);
        (low, lowScale).Should().Be((0.45, 0.85));
        var (high, highScale) = ColituGeckoLabel.BreathAt(TimeSpan.FromSeconds(1));
        high.Should().BeApproximately(1, 1e-9);
        highScale.Should().BeApproximately(1, 1e-9);
        ColituGeckoLabel.BreathAt(TimeSpan.FromSeconds(2)).Opacity.Should().BeApproximately(0.45, 1e-9, "one breath is 2 s");
    }

    [Fact]
    public void ReducedMotion_FollowsTheOverride()
    {
        try
        {
            ColituReducedMotion.Override = true;
            ColituGeckoLabel.ReducedMotion.Should().BeTrue();
            ColituReducedMotion.Override = false;
            ColituGeckoLabel.ReducedMotion.Should().BeFalse();
        }
        finally
        {
            ColituReducedMotion.Override = null;
        }
    }
}
