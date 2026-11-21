using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

/// <summary>
/// The whitelist check while the nftables kill switch is engaged: uid-scoped, self-expiring probe sets
/// (string tests only, no live nft on this PC), and CheckAsync's resolve → hook → probe order.
/// </summary>
public class ColituWhitelistProbeKillSwitchTests
{
    private static readonly ColituKillSwitch.Allowance Allowed = new()
    {
        Servers = [IPAddress.Parse("203.0.113.10")],
        Api = [IPAddress.Parse("198.51.100.20")],
        Resolvers = [IPAddress.Parse("1.1.1.1")]
    };

    [Fact]
    public void EngageScriptHasTheProbeSetsAndTheUidScopedRuleBeforeTheEndOfTheChain()
    {
        var script = ColituKillSwitch.BuildEngageScript(Allowed, 4242, appUid: 1000);
        script.Should().Contain("set wlprobe4 { type ipv4_addr; flags timeout; }");
        script.Should().Contain("set wlprobe6 { type ipv6_addr; flags timeout; }");
        var rule4 = script.IndexOf("meta skuid 1000 ip daddr @wlprobe4 tcp dport 443 accept", StringComparison.Ordinal);
        var rule6 = script.IndexOf("meta skuid 1000 ip6 daddr @wlprobe6 tcp dport 443 accept", StringComparison.Ordinal);
        rule4.Should().BePositive();
        rule6.Should().BePositive();
        var policy = script.IndexOf("policy drop;", StringComparison.Ordinal);
        var chainEnd = script.IndexOf("\n  }\n", policy, StringComparison.Ordinal);
        policy.Should().BeLessThan(rule4);
        rule4.Should().BeLessThan(chainEnd);
        rule6.Should().BeLessThan(chainEnd);
        // The sets are declared before the chain that uses them.
        script.IndexOf("set wlprobe4", StringComparison.Ordinal).Should().BeLessThan(script.IndexOf("chain output", StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutAUidThereIsNoProbeRule()
    {
        var script = ColituKillSwitch.BuildEngageScript(Allowed, 4242, appUid: null);
        script.Should().NotContain("@wlprobe4");
        script.Should().NotContain("@wlprobe6");
    }

    [Fact]
    public void ElementScriptAddsOnlyPublicAddressesForTenSeconds()
    {
        var script = ColituKillSwitch.BuildWhitelistProbeScript(
        [
            IPAddress.Parse("87.250.250.242"),
            IPAddress.Parse("::ffff:93.186.225.194"),
            IPAddress.Parse("87.250.250.242"),
            IPAddress.Parse("127.0.0.1"),
            IPAddress.Parse("10.0.0.5"),
            IPAddress.Parse("192.168.1.1"),
            IPAddress.Parse("172.20.1.1"),
            IPAddress.Parse("100.64.0.1"),
            IPAddress.Parse("169.254.1.1"),
            IPAddress.Parse("224.0.0.1"),
            IPAddress.Parse("::1"),
            IPAddress.Parse("fe80::1"),
            IPAddress.Parse("fd00::1"),
            IPAddress.Parse("2a02:6b8::2:242")
        ]);
        script.Should().NotBeNull();
        script!.Should().Contain("nft add element inet colitu_killswitch wlprobe4 { 87.250.250.242 timeout 10s, 93.186.225.194 timeout 10s }");
        script.Should().Contain("nft add element inet colitu_killswitch wlprobe6 { 2a02:6b8::2:242 timeout 10s }");
        foreach (var dropped in new[] { "127.0.0.1", "10.0.0.5", "192.168.1.1", "172.20.1.1", "100.64.0.1", "169.254.1.1", "224.0.0.1", "::1", "fe80::1", "fd00::1" })
        {
            script.Should().NotContain(dropped + " timeout");
        }
        ColituKillSwitch.BuildWhitelistProbeScript([IPAddress.Parse("192.168.0.1"), IPAddress.Loopback]).Should().BeNull();
    }

    [Fact]
    public void ElementScriptTakesAtMostSixteenAddresses()
    {
        var many = Enumerable.Range(1, 40).Select(i => IPAddress.Parse($"203.0.113.{i}"));
        ColituKillSwitch.WhitelistProbeAddresses(many).Should().HaveCount(16);
        var script = ColituKillSwitch.BuildWhitelistProbeScript(many)!;
        script.Split("timeout 10s").Length.Should().Be(17);
    }

    [Fact]
    public async Task CheckCallsTheHookOnceWithTheResolvedAddressesBeforeAnyProbe()
    {
        var addresses = new Dictionary<string, IPAddress?>
        {
            ["www.google.com"] = IPAddress.Parse("142.250.74.4"),
            ["de.example"] = IPAddress.Parse("203.0.113.7"),
            ["ya.ru"] = IPAddress.Parse("87.250.250.242"),
            ["vk.com"] = IPAddress.Parse("93.186.225.194"),
            ["lan.example"] = IPAddress.Parse("192.168.1.10"),
            ["gone.example"] = null
        };
        var probes = 0;
        var hookCalls = new List<(IReadOnlyList<IPAddress> Addresses, int ProbesBefore)>();
        var check = await ColituWhitelist.CheckAsync(
            [new ColituWhitelistTarget("www.google.com", 443), new ColituWhitelistTarget("de.example", 2053), new ColituWhitelistTarget("gone.example", 443)],
            ["ya.ru", "vk.com", "lan.example"],
            null, requirePin: false,
            (host, _) => Task.FromResult(addresses[host]),
            (address, _, _) =>
            {
                Interlocked.Increment(ref probes);
                // Only the domestic sites answer: an operator whitelist.
                return Task.FromResult<(int?, bool)>((address.ToString() is "87.250.250.242" or "93.186.225.194" ? 30 : null, false));
            },
            list =>
            {
                hookCalls.Add((list, Volatile.Read(ref probes)));
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);
        hookCalls.Should().ContainSingle();
        hookCalls[0].ProbesBefore.Should().Be(0);
        hookCalls[0].Addresses.Select(address => address.ToString()).Should().BeEquivalentTo("142.250.74.4", "203.0.113.7", "87.250.250.242", "93.186.225.194");
        probes.Should().Be(4);
        check.Should().NotBeNull();
        check!.ForeignOk.Should().Be(0);
        check.ForeignTotal.Should().Be(3);
        check.DomesticOk.Should().Be(2);
        check.Whitelist.Should().BeTrue();
    }

    [Fact]
    public async Task AFailingHookDoesNotFailTheCheck()
    {
        var check = await ColituWhitelist.CheckAsync(
            [new ColituWhitelistTarget("www.google.com", 443)], ["ya.ru", "vk.com"], null, requirePin: false,
            (_, _) => Task.FromResult<IPAddress?>(IPAddress.Parse("87.250.250.242")),
            (_, _, _) => Task.FromResult<(int?, bool)>((20, false)),
            _ => throw new InvalidOperationException("sudo refused"),
            TestContext.Current.CancellationToken);
        check.Should().NotBeNull();
        check!.DomesticOk.Should().Be(2);
    }
}
