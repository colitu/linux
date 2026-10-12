using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ServiceLib.Models.Entities;
using SQLite;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

/// <summary>
/// 2026-10-12, the Windows changes on Linux: the kill switch stays closed when a connect fails
/// (fail-closed), ad-blocking resolvers from the panel's bootstrap, the TUN ping targets kept for
/// the first automatic connect, stale update packages removed, unique Xray rule tags (26.10),
/// pinned cores in the package script, and an old profile database opening with sqlite-net 1.11.
/// </summary>
public class ColituWindowsParity20261012Tests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("colitu-test-").FullName;

    public void Dispose()
    {
        ColituAdBlock.ResetForTest(null);
        try { Directory.Delete(_folder, true); } catch { }
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    // ── Kill switch: fail-closed ─────────────────────────────────────────
    [Fact]
    public void FailedConnect_KeepsTheKillSwitch()
    {
        ColituVpnService.ReleaseKillSwitchAfterFailure(killSwitchApplies: true, devicePaused: false, planError: false).Should().BeFalse();
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void NothingToReconnectTo_OrKillSwitchOff_Releases(bool applies, bool devicePaused, bool planError)
    {
        ColituVpnService.ReleaseKillSwitchAfterFailure(applies, devicePaused, planError).Should().BeTrue();
    }

    [Theory]
    [InlineData("tr", "Bağlanılamadı. Kill switch internetinizi koruyor; yeniden deneyin ya da bağlantıyı kesin.")]
    [InlineData("en", "Couldn’t connect. The kill switch is protecting your internet; try again or disconnect.")]
    [InlineData("ru", "Не удалось подключиться. Kill switch защищает ваш интернет; повторите попытку или отключитесь.")]
    public void FailedConnectMessage_InEveryLanguage(string language, string expected)
    {
        var previous = Loc.I.Language;
        try
        {
            Loc.I.SetLanguage(language);
            Loc.I["home.sub.blockedFailed"].Should().Be(expected);
        }
        finally
        {
            Loc.I.SetLanguage(previous);
        }
    }

    [Fact]
    public void Source_ArmsBeforeConnecting_KeepsOnFailure_ReleasesOnDisconnectOffSignOutAndExit()
    {
        var root = AppSource();
        var service = File.ReadAllText(Path.Combine(root, "Services", "ColituVpnService.cs"));
        service.Should().Contain("EngageKillSwitchAsync(\"TUN connecting\")");
        service.Should().Contain("ReleaseKillSwitchAfterFailure(KillSwitchApplies, devicePaused, noPlan)");
        service.Should().Contain("ReleaseKillSwitchAsync(\"disconnected by the user\")");
        service.Should().Contain("ReleaseKillSwitchAsync(\"kill switch turned off\")");
        // Sign-out disconnects while the rules hold the internet closed after a failed connect.
        service.Should().Contain("ColituVpnStatus.Reconnecting || KillSwitchEngaged)");
        var window = File.ReadAllText(Path.Combine(root, "Views", "ColituMainWindow.axaml.cs"));
        window.Should().Contain("|| _vpn.KillSwitchEngaged)");
    }

    // ── Ad blocking from the bootstrap ───────────────────────────────────
    [Fact]
    public void AdBlock_ServersOf_ReadsOnlyHttpsUrls()
    {
        ColituAdBlock.ServersOf(Json("""{"ad_block":{"doh":["https://dns-a.example.test:3443/dns-query","http://plain.example.test/dns-query","https://user:pw@x.example.test/q","not a url","https://dns-b.example.test/dns-query"]}}"""))
            .Should().Equal("https://dns-a.example.test:3443/dns-query", "https://dns-b.example.test/dns-query");
        ColituAdBlock.ServersOf(Json("""{"ad_block":{"doh":[]}}""")).Should().BeEmpty();
        ColituAdBlock.ServersOf(Json("""{"user":{}}""")).Should().BeNull("an older panel without the field changes nothing");
    }

    [Fact]
    public void AdBlock_Remember_EmptyOrMissingNeverTurnsItOff()
    {
        var path = Path.Combine(_folder, "adblock.json");
        ColituAdBlock.ResetForTest(null);
        ColituAdBlock.Servers.Should().Equal(ColituAdBlock.Embedded, "without a panel answer the build's list is used");

        ColituAdBlock.Remember(Json("""{"ad_block":{"doh":["https://dns-a.example.test/dns-query"]}}"""), path).Should().BeTrue();
        ColituAdBlock.Servers.Should().Equal("https://dns-a.example.test/dns-query");
        File.ReadAllText(path).Should().Contain("dns-a.example.test");

        ColituAdBlock.Remember(Json("""{"user":{}}"""), path).Should().BeFalse();
        ColituAdBlock.Remember(Json("""{"ad_block":{"doh":[]}}"""), path).Should().BeFalse();
        ColituAdBlock.Servers.Should().Equal("https://dns-a.example.test/dns-query");
        File.ReadAllText(path).Should().Contain("dns-a.example.test");
    }

    // ── Ping targets kept for the first connect ──────────────────────────
    [Fact]
    public void PingTargets_SavedAndLoaded_OnlyPublicIpv4()
    {
        var path = Path.Combine(_folder, "targets.json");
        ColituLatency.LoadTargets(path).Should().BeEmpty();
        ColituLatency.SaveTargets(path, ["203.0.113.7", "198.51.100.20"]);
        ColituLatency.LoadTargets(path).Should().Equal("203.0.113.7", "198.51.100.20");
        // An edited file: private, loopback and non-addresses are dropped.
        File.WriteAllText(path, """["10.0.0.1","127.0.0.1","192.168.1.1","x","203.0.113.9"]""");
        ColituLatency.LoadTargets(path).Should().Equal("203.0.113.9");
        File.WriteAllText(path, "{ broken");
        ColituLatency.LoadTargets(path).Should().BeEmpty();
    }

    // ── Stale update packages ────────────────────────────────────────────
    [Fact]
    public void Updates_StalePackagesRemoved_NewerKept()
    {
        foreach (var name in new[] { "colitu-vpn-140.deb", "colitu-vpn-141.rpm", "colitu-vpn-142.deb", "colitu-vpn-x.deb", "update-state.json" })
        {
            File.WriteAllText(Path.Combine(_folder, name), name);
        }

        ColituUpdateService.DeleteStalePackages(_folder, 141);

        Directory.GetFiles(_folder).Select(Path.GetFileName).Should().BeEquivalentTo(["colitu-vpn-142.deb", "colitu-vpn-x.deb", "update-state.json"]);
    }

    // ── Xray 26.10: unique rule tags ─────────────────────────────────────
    private static List<string?> Tags(string config) =>
        JsonNode.Parse(config)!["routing"]!["rules"]!.AsArray().Select(rule => rule?["ruleTag"]?.GetValue<string>()).ToList();

    [Fact]
    public void XrayRuleTags_RepeatedBecomeUnique_FirstKeepsItsTag()
    {
        const string config = """
        {"routing":{"rules":[
          {"ruleTag":"colitu-ru-direct","domain":["geosite:category-ru"],"outboundTag":"direct"},
          {"ruleTag":"colitu-ru-direct","ip":["geoip:ru"],"outboundTag":"direct"},
          {"ruleTag":"colitu-ru-direct-2","ip":["198.51.100.0/24"],"outboundTag":"direct"},
          {"ruleTag":"colitu-ru-direct","port":"53","outboundTag":"proxy"},
          {"inboundTag":["colitu-check"],"outboundTag":"proxy"}
        ]}}
        """;
        Tags(ColituXrayRuleTags.Unique(config)).Should().Equal(
            "colitu-ru-direct", "colitu-ru-direct-3", "colitu-ru-direct-2", "colitu-ru-direct-4", null);
    }

    [Theory]
    [InlineData("""{"routing":{"rules":[{"ruleTag":"a","outboundTag":"direct"},{"ruleTag":"b","outboundTag":"proxy"}]}}""")]
    [InlineData("{ not json")]
    [InlineData("""{"outbounds":[]}""")]
    public void XrayRuleTags_NothingToChange_SameString(string config)
    {
        ColituXrayRuleTags.Unique(config).Should().BeSameAs(config);
    }

    // ── Pinned cores in the package script ───────────────────────────────
    [Fact]
    public void PackageScript_PinsCoreVersionsAndHashes()
    {
        var script = File.ReadAllText(Path.Combine(AppSource(), "..", "..", "scripts", "package-linux.sh"));
        script.Should().Contain("XRAY_VERSION=\"26.10.10\"").And.Contain("SINGBOX_VERSION=\"1.14.3\"");
        script.Should().Contain("https://github.com/XTLS/Xray-core/releases/download/v$XRAY_VERSION/$XRAY_ZIP");
        script.Should().Contain("https://github.com/SagerNet/sing-box/releases/download/v$SINGBOX_VERSION/$SINGBOX_TGZ");
        foreach (var name in new[] { "XRAY_ZIP_SHA256", "XRAY_SHA256", "SINGBOX_TGZ_SHA256", "SINGBOX_SHA256", "CRONET_SHA256" })
        {
            System.Text.RegularExpressions.Regex.Matches(script, $@"\b{name}=[0-9a-f]{{64}}\b").Count.Should().Be(2, $"{name} for x64 and arm64");
        }
        script.Should().Contain("check_file \"$WORK/xray/xray\" \"$XRAY_SHA256\"");
        script.Should().Contain("check_file \"$WORK/sing-box/sing-box\" \"$SINGBOX_SHA256\"");
        script.Should().Contain(".dgst");
    }

    // ── sqlite-net 1.11: an old profile database ─────────────────────────
    /// <summary>
    /// TestData/legacy-guiNDB.db was written with sqlite-net 1.9.172 and has two ProfileItem columns
    /// removed (CertSha, EchConfigList), as an older version left it; ProfileGroupItem is missing.
    /// Opening it as the app does must keep every row and add the missing columns and table.
    /// </summary>
    [Fact]
    public async Task OldDatabase_OpensMigratesAndKeepsTheRows()
    {
        var path = Path.Combine(_folder, "guiNDB.db");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestData", "legacy-guiNDB.db"), path);
        try
        {
            using (var db = new SQLiteConnection(path, false))
            {
                db.CreateTable<SubItem>().Should().Be(CreateTableResult.Migrated, "the table exists");
                db.CreateTable<ProfileItem>().Should().Be(CreateTableResult.Migrated);
                db.CreateTable<ServerStatItem>();
                db.CreateTable<RoutingItem>();
                db.CreateTable<ProfileExItem>();
                db.CreateTable<DNSItem>();
                db.CreateTable<FullConfigTemplateItem>();
#pragma warning disable CS0618
                db.CreateTable<ProfileGroupItem>().Should().Be(CreateTableResult.Created);
#pragma warning restore CS0618

                db.GetTableInfo(nameof(ProfileItem)).Select(column => column.Name).Should().Contain(["CertSha", "EchConfigList"]);
                var profiles = db.Table<ProfileItem>().OrderBy(item => item.IndexId).ToList();
                profiles.Select(item => item.IndexId).Should().Equal("legacy-hy2", "legacy-vless");
                var vless = profiles.Single(item => item.IndexId == "legacy-vless");
                vless.Address.Should().Be("node.example.test");
                vless.Port.Should().Be(443);
                vless.StreamSecurity.Should().Be("reality");
                vless.PublicKey.Should().Be("PBK");
                vless.CertSha.Should().BeNullOrEmpty("the new column starts empty");
                profiles.Single(item => item.IndexId == "legacy-hy2").ConfigType.Should().Be(ServiceLib.Enums.EConfigType.Hysteria2);
                db.Table<SubItem>().Single().Id.Should().Be("colitu-api");

                vless.CertSha = "abc";
                db.Update(vless).Should().Be(1);
            }

            var async = new SQLiteAsyncConnection(path, false);
            (await async.Table<ProfileItem>().Where(item => item.IndexId == "legacy-vless").FirstAsync()).CertSha.Should().Be("abc");
            await async.CloseAsync();
        }
        finally
        {
            SQLiteAsyncConnection.ResetPool();
        }
    }

    [Fact]
    public void NativeSqlite_IsCurrent()
    {
        using var db = new SQLiteConnection(":memory:", false);
        // CVE-2025-6965 is fixed in SQLite 3.50.2.
        Version.Parse(db.ExecuteScalar<string>("select sqlite_version()")).Should().BeGreaterThanOrEqualTo(new Version(3, 50, 2));
    }

    private static string AppSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "ColituVPN", "Services")))
        {
            dir = dir.Parent;
        }
        return Path.Combine(dir!.FullName, "ColituVPN");
    }
}
