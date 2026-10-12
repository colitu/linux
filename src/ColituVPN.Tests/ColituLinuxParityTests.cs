using System.Net;
using AwesomeAssertions;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

/// <summary>
/// Parity with the Android security fixes (2026-10-12): bounded latency probes, and the limits of the
/// Linux security audit (update package, API responses, server list).
/// </summary>
public class ColituLinuxParityTests
{
    // ── Latency probes ─────────────────────────────────────────────────────
    [Fact]
    public void Latency_IsBoundedLikeAndroid()
    {
        ColituLatency.MaxServers.Should().Be(200);
        ColituLatency.Parallel.Should().Be(8);
        ColituLatency.AllowedPorts.Should().BeEquivalentTo([443, 2053, 2083, 2087, 2096, 3443, 4433, 7443, 8388, 8443, 9443]);
    }

    [Theory]
    [InlineData("node.example.test", 443, true)]
    [InlineData("node.example.test", 8443, true)]
    [InlineData("node.example.test", 9443, true)]
    [InlineData("203.0.113.7", 2053, true)]
    [InlineData("node.example.test", 22, false)]
    [InlineData("node.example.test", 80, false)]
    [InlineData("node.example.test", 65535, false)]
    [InlineData("127.0.0.1", 443, false)]
    [InlineData("10.0.0.1", 443, false)]
    [InlineData("192.168.1.1", 443, false)]
    [InlineData("172.16.5.5", 443, false)]
    [InlineData("100.64.0.1", 443, false)]
    [InlineData("169.254.169.254", 443, false)]
    [InlineData("0.0.0.0", 443, false)]
    [InlineData("::1", 443, false)]
    [InlineData("[::1]", 443, false)]
    [InlineData("fe80::1", 443, false)]
    [InlineData("localhost", 443, false)]
    [InlineData("printer.localhost", 443, false)]
    [InlineData("", 443, false)]
    [InlineData(null, 443, false)]
    public void Latency_ProbeAllowed_OnlyPublicTargetsOnAllowedPorts(string? host, int port, bool allowed)
    {
        ColituLatency.ProbeAllowed(host, port).Should().Be(allowed);
    }

    [Fact]
    public void Latency_CanProbe_RefusesDisallowedPortsAndLocalLiterals()
    {
        ColituLatency.CanProbe(new ColituVpnServer { Id = "a", Host = "node.example.test", Port = 443 }).Should().BeTrue();
        ColituLatency.CanProbe(new ColituVpnServer { Id = "a", Host = "node.example.test", Port = 3306 }).Should().BeFalse();
        ColituLatency.CanProbe(new ColituVpnServer { Id = "a", Host = "192.168.0.1", Port = 443 }).Should().BeFalse();
        ColituLatency.CanProbe(new ColituVpnServer { Id = "", Host = "node.example.test", Port = 443 }).Should().BeFalse();
        ColituLatency.CanProbe(new ColituVpnServer { Id = "a", Host = "node.example.test", Port = null }).Should().BeFalse();
    }

    [Fact]
    public async Task Latency_MeasureAsync_NeverDialsARefusedTarget()
    {
        // Refused before any lookup or connect: returns at once.
        (await ColituLatency.MeasureAsync("127.0.0.1", 443, null)).Should().BeNull();
        (await ColituLatency.MeasureAsync("node.example.test", 22, null)).Should().BeNull();
    }

    [Theory]
    [InlineData("127.0.0.2")]
    [InlineData("10.20.30.40")]
    [InlineData("100.127.255.255")]
    [InlineData("169.254.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.255.255")]
    public void Latency_ResolvedLocalAddresses_AreNotPublic(string address)
    {
        // What a name resolves to is checked again before the connect.
        ColituLatency.IsPublic(IPAddress.Parse(address)).Should().BeFalse();
    }

    // ── Update package ─────────────────────────────────────────────────────
    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", true)]
    [InlineData("0123456789abcdef", false)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg", false)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde\n", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Update_Sha256_MustBe64HexDigits(string? value, bool valid)
    {
        ColituUpdateService.IsSha256Hex(value).Should().Be(valid);
        new ColituUpdateInfo { PackageKind = "deb", Sha256 = value }.CanInstall.Should().Be(valid);
    }

    [Fact]
    public void Update_PackageSize_IsCapped()
    {
        ColituUpdateService.MaxPackageBytes.Should().BeInRange(200L << 20, 1L << 30);
    }

    // ── API responses ──────────────────────────────────────────────────────
    [Fact]
    public void Api_ResponseBuffer_IsCappedButFitsTheLargestAttachment()
    {
        ColituAuthService.MaxResponseBytes.Should().BeGreaterThan(50L << 20);
        ColituAuthService.MaxResponseBytes.Should().BeLessThanOrEqualTo(128L << 20);
        ColituApiClient.MaxServerList.Should().Be(1000);
    }
}
