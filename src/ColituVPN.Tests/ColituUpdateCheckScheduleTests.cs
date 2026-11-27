using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

/// <summary>
/// Same as Windows 3.2.2: the update check runs at start, every 6 hours, after a connection and
/// 15 minutes after a failure (one at a time), and reads the signed Linux manifest from the API bases
/// and relays when the website is unreachable. An unsigned or tampered manifest is never taken.
/// </summary>
public class ColituUpdateCheckScheduleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    // ---- schedule ------------------------------------------------------------------------------

    [Fact]
    public void OnlyOneCheckRunsAtATime()
    {
        var schedule = new ColituUpdateSchedule();
        schedule.TryBegin(T0, ColituUpdateTrigger.Startup).Should().BeTrue();
        schedule.Running.Should().BeTrue();
        foreach (var trigger in Enum.GetValues<ColituUpdateTrigger>())
        {
            schedule.TryBegin(T0, trigger).Should().BeFalse(trigger.ToString());
            schedule.IsDue(T0, trigger).Should().BeFalse(trigger.ToString());
        }
        schedule.Finish(T0.AddSeconds(5), failed: false);
        schedule.Running.Should().BeFalse();
        schedule.TryBegin(T0.AddSeconds(6), ColituUpdateTrigger.Manual).Should().BeTrue();
    }

    [Fact]
    public void AfterAGoodCheck_PeriodicWaitsSixHours()
    {
        var schedule = new ColituUpdateSchedule();
        schedule.TryBegin(T0, ColituUpdateTrigger.Startup).Should().BeTrue();
        schedule.Finish(T0, failed: false);

        schedule.IsDue(T0.AddHours(6).AddSeconds(-1), ColituUpdateTrigger.Periodic).Should().BeFalse();
        schedule.IsDue(T0.AddHours(6), ColituUpdateTrigger.Periodic).Should().BeTrue();
        schedule.TryBegin(T0.AddHours(1), ColituUpdateTrigger.Periodic).Should().BeFalse();
        schedule.TryBegin(T0.AddHours(6), ColituUpdateTrigger.Periodic).Should().BeTrue();
    }

    [Fact]
    public void AfterAFailedCheck_AutomaticTriggersBackOffFifteenMinutes()
    {
        var schedule = new ColituUpdateSchedule();
        schedule.TryBegin(T0, ColituUpdateTrigger.Startup).Should().BeTrue();
        schedule.Finish(T0, failed: true);

        var early = T0 + ColituUpdateSchedule.FailureBackoff - TimeSpan.FromSeconds(1);
        schedule.IsDue(early, ColituUpdateTrigger.Periodic).Should().BeFalse();
        schedule.IsDue(early, ColituUpdateTrigger.AfterConnect).Should().BeFalse();
        // The user's button and a new start are never held back.
        schedule.IsDue(early, ColituUpdateTrigger.Manual).Should().BeTrue();
        schedule.IsDue(early, ColituUpdateTrigger.Startup).Should().BeTrue();

        var due = T0 + ColituUpdateSchedule.FailureBackoff;
        schedule.IsDue(due, ColituUpdateTrigger.Periodic).Should().BeTrue();
        schedule.IsDue(due, ColituUpdateTrigger.AfterConnect).Should().BeTrue();

        // A second failure starts the back-off again.
        schedule.TryBegin(due, ColituUpdateTrigger.Periodic).Should().BeTrue();
        schedule.Finish(due, failed: true);
        schedule.IsDue(due.AddMinutes(14), ColituUpdateTrigger.Periodic).Should().BeFalse();
        schedule.IsDue(due.AddMinutes(15), ColituUpdateTrigger.Periodic).Should().BeTrue();
    }

    [Fact]
    public void AfterConnect_RunsUnlessAGoodCheckIsRecent()
    {
        var schedule = new ColituUpdateSchedule();
        // Nothing finished yet: a connection may check.
        schedule.IsDue(T0, ColituUpdateTrigger.AfterConnect).Should().BeTrue();

        schedule.TryBegin(T0, ColituUpdateTrigger.Startup).Should().BeTrue();
        schedule.Finish(T0, failed: false);
        // Reconnects right after a good check do not each run one.
        schedule.IsDue(T0.AddMinutes(29), ColituUpdateTrigger.AfterConnect).Should().BeFalse();
        schedule.IsDue(T0 + ColituUpdateSchedule.AfterConnectMinGap, ColituUpdateTrigger.AfterConnect).Should().BeTrue();
        // ...and well before the 6-hour periodic one.
        schedule.IsDue(T0 + ColituUpdateSchedule.AfterConnectMinGap, ColituUpdateTrigger.Periodic).Should().BeFalse();
    }

    [Fact]
    public void ScheduleConstants_MatchTheSpec()
    {
        ColituUpdateSchedule.Interval.Should().Be(TimeSpan.FromHours(6));
        ColituUpdateSchedule.FailureBackoff.Should().Be(TimeSpan.FromMinutes(15));
        ColituUpdateSchedule.AfterConnectDelay.Should().Be(TimeSpan.FromSeconds(30));
        ColituUpdateSchedule.AfterConnectMinGap.Should().Be(TimeSpan.FromMinutes(30));
    }

    // ---- sources -------------------------------------------------------------------------------

    private const string Primary = "https://colitu.com/downloads/linux/latest.json";

    [Fact]
    public void ManifestSources_WebsiteFirst_ThenApiBasesInOrder_ThenRelays()
    {
        var bases = new[]
        {
            "relay:ru1",                                   // the base that last worked comes first in Bases()
            "https://api.colitu.com/api/v1",
            "https://mirror.example/capi/v1",
            "relay:wl2",
            "https://api.colitu.com/api/v1",               // duplicate origin
            "http://plain.example/api/v1",                 // never over plain http
            "not a url"
        };
        ColituUpdateService.ManifestSources(Primary, bases).Should().Equal(
            Primary,
            "relay:ru1/downloads/linux/latest.json",
            "https://api.colitu.com/downloads/linux/latest.json",
            "https://mirror.example/downloads/linux/latest.json",
            "relay:wl2/downloads/linux/latest.json");
    }

    [Fact]
    public void ManifestSources_WithoutBases_IsTheWebsiteOnly()
    {
        ColituUpdateService.ManifestSources(Primary, []).Should().Equal(Primary);
    }

    [Fact]
    public void DownloadSources_RetryTheSignedUrl_ThenSamePathElsewhere()
    {
        const string deb = "https://colitu.com/downloads/linux/colitu-vpn_1.9.1_amd64.deb";
        ColituUpdateService.DownloadSources(deb, ["https://api.colitu.com/api/v1", "relay:ru1"]).Should().Equal(
            deb,
            deb,
            "https://api.colitu.com/downloads/linux/colitu-vpn_1.9.1_amd64.deb",
            "relay:ru1/downloads/linux/colitu-vpn_1.9.1_amd64.deb",
            deb);
        // The rpm keeps its own path; only the host changes.
        const string rpm = "https://colitu.com/downloads/linux/colitu-vpn-1.9.1-1.x86_64.rpm";
        ColituUpdateService.DownloadSources(rpm, ["relay:ru1"]).Should().Equal(
            rpm, rpm, "relay:ru1/downloads/linux/colitu-vpn-1.9.1-1.x86_64.rpm", rpm);
        // No other path known: the three attempts of the earlier releases.
        ColituUpdateService.DownloadSources(deb, []).Should().Equal(deb, deb, deb);
    }

    // ---- fallback and signature --------------------------------------------------------------

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

    private static Dictionary<string, object?> Manifest(int code) => new()
    {
        ["latestVersionCode"] = code,
        ["versionName"] = $"1.9.{code % 10}",
        ["deb"] = new Dictionary<string, object?> { ["url"] = "https://colitu.com/downloads/linux/colitu-vpn_amd64.deb", ["sha256"] = new string('a', 64) },
        ["rpm"] = new Dictionary<string, object?> { ["url"] = "https://colitu.com/downloads/linux/colitu-vpn.x86_64.rpm", ["sha256"] = new string('b', 64) },
        ["forceUpdate"] = true,
        ["issued_at"] = Iso(DateTimeOffset.UtcNow.AddHours(-1)),
        ["expires_at"] = Iso(DateTimeOffset.UtcNow.AddDays(20))
    };

    private static string Signed(ECDsa key, Dictionary<string, object?> fields)
    {
        var payload = JsonSerializer.Deserialize<ColituLinuxVersionPayload>(JsonSerializer.Serialize(fields), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        fields["signature_v2"] = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(ColituUpdateSignature.MessageV2(payload)), HashAlgorithmName.SHA256));
        return JsonSerializer.Serialize(fields);
    }

    [Fact]
    public void AcceptManifest_RefusesUnsignedTamperedAndForeignKeyManifests()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportSubjectPublicKeyInfoPem();
        var now = DateTimeOffset.UtcNow;

        var good = Signed(key, Manifest(191));
        ColituUpdateService.AcceptManifest(good, now, pem)!.LatestVersionCode.Should().Be(191);

        // Unsigned.
        ColituUpdateService.AcceptManifest(JsonSerializer.Serialize(Manifest(191)), now, pem).Should().BeNull();
        // Only the v1 signature (no dates covered).
        var legacy = Manifest(191);
        legacy["signature"] = "AAAA";
        ColituUpdateService.AcceptManifest(JsonSerializer.Serialize(legacy), now, pem).Should().BeNull();
        // Signed, then the version or a package hash changed.
        var tampered = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(good)!
            .ToDictionary(pair => pair.Key, pair => (object?)pair.Value);
        tampered["latestVersionCode"] = 999;
        ColituUpdateService.AcceptManifest(JsonSerializer.Serialize(tampered), now, pem).Should().BeNull();
        tampered = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(good)!
            .ToDictionary(pair => pair.Key, pair => (object?)pair.Value);
        tampered["deb"] = new Dictionary<string, object?> { ["url"] = "https://colitu.com/downloads/linux/colitu-vpn_amd64.deb", ["sha256"] = new string('c', 64) };
        ColituUpdateService.AcceptManifest(JsonSerializer.Serialize(tampered), now, pem).Should().BeNull();
        // Signed with a key the app does not know.
        ColituUpdateService.AcceptManifest(Signed(other, Manifest(191)), now, pem).Should().BeNull();
        // The built-in Linux release key never takes a test-signed manifest.
        ColituUpdateService.AcceptManifest(good, now).Should().BeNull();
        // A Windows-style message (no colitu-linux-update-v1 prefix) signed with the right key is refused.
        var windowsStyle = Manifest(191);
        windowsStyle["signature_v2"] = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes("191\n1.9.1"), HashAlgorithmName.SHA256));
        ColituUpdateService.AcceptManifest(JsonSerializer.Serialize(windowsStyle), now, pem).Should().BeNull();
        // Not JSON at all.
        ColituUpdateService.AcceptManifest("<html>blocked</html>", now, pem).Should().BeNull();
    }

    [Fact]
    public async Task FetchManifest_FallsBackInOrder_AndSkipsUnsignedSources()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportSubjectPublicKeyInfoPem();
        var sources = new[] { Primary, "https://api.colitu.com/downloads/linux/latest.json", "relay:ru1/downloads/linux/latest.json", "relay:wl2/downloads/linux/latest.json" };
        var answers = new Dictionary<string, Func<string?>>
        {
            [sources[0]] = () => throw new HttpRequestException(HttpRequestError.ConnectionError, "blocked"),
            [sources[1]] = () => JsonSerializer.Serialize(Manifest(199)),   // unsigned: must not win
            [sources[2]] = () => Signed(key, Manifest(191)),
            [sources[3]] = () => Signed(key, Manifest(192))
        };
        var asked = new List<string>();

        var payload = await ColituUpdateService.FetchManifestAsync(sources, (source, _) =>
        {
            asked.Add(source);
            return Task.FromResult(answers[source]());
        }, text => ColituUpdateService.AcceptManifest(text, DateTimeOffset.UtcNow, pem), CancellationToken.None);

        payload!.LatestVersionCode.Should().Be(191);
        asked.Should().Equal(sources[0], sources[1], sources[2]);
    }

    [Fact]
    public async Task FetchManifest_NullWhenNoSourceHasASignedManifest()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportSubjectPublicKeyInfoPem();
        var sources = new[] { Primary, "relay:ru1/downloads/linux/latest.json", "https://api.colitu.com/downloads/linux/latest.json" };
        var asked = new List<string>();

        var payload = await ColituUpdateService.FetchManifestAsync(sources, (source, _) =>
        {
            asked.Add(source);
            return source == Primary
                ? Task.FromResult<string?>(null)                                     // HTTP error
                : source.StartsWith("relay:", StringComparison.Ordinal)
                    ? Task.FromResult<string?>(JsonSerializer.Serialize(Manifest(193)))  // unsigned
                    : throw new TaskCanceledException("timeout");
        }, text => ColituUpdateService.AcceptManifest(text, DateTimeOffset.UtcNow, pem), CancellationToken.None);

        payload.Should().BeNull();
        asked.Should().Equal(sources);
    }

    [Fact]
    public async Task FetchManifest_StopsAtTheWebsiteWhenItAnswers()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportSubjectPublicKeyInfoPem();
        var asked = 0;
        var payload = await ColituUpdateService.FetchManifestAsync([Primary, "relay:ru1/downloads/linux/latest.json"], (_, _) =>
        {
            asked++;
            return Task.FromResult<string?>(Signed(key, Manifest(191)));
        }, text => ColituUpdateService.AcceptManifest(text, DateTimeOffset.UtcNow, pem), CancellationToken.None);

        payload!.ForceUpdate.Should().BeTrue();
        asked.Should().Be(1);
    }
}
