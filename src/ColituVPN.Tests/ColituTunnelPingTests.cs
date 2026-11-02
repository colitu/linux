using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

/// <summary>
/// Pings through the tunnel while connected: shown when the pinned probe missed a server and no
/// VPN-off ping of the last 30 minutes is there; never part of the hiding decision.
/// </summary>
public class ColituTunnelPingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 21, 0, 0, TimeSpan.Zero);

    private static Dictionary<string, int?> R(params (string Id, int? Ms)[] items) =>
        items.ToDictionary(item => item.Id, item => item.Ms);

    [Fact]
    public void TunnelPingIsShownOnlyWhenNothingBetterIsThere()
    {
        var reach = new ColituReachability();
        // VPN off 10 min ago: a measured; b never measured with the VPN off.
        reach.RecordRound(R(("a", 40), ("b", null), ("c", 30), ("d", 35)), vpnOff: true, Now - TimeSpan.FromMinutes(10));
        // Connected: the pinned probe fails for a and b.
        reach.RecordRound(R(("a", null), ("b", null), ("c", 32), ("d", 33)), vpnOff: false, Now);

        reach.NeedsTunnelPing("a", pinnedOk: false, Now).Should().BeFalse("a has a VPN-off ping of the last 30 min");
        reach.NeedsTunnelPing("b", pinnedOk: false, Now).Should().BeTrue();
        reach.NeedsTunnelPing("c", pinnedOk: true, Now).Should().BeFalse();

        reach.RecordTunnel(R(("b", 120)), Now);
        reach.DisplayPing("a", connected: true, Now).Should().Be(40);
        reach.DisplayPing("b", connected: true, Now).Should().Be(120);
        reach.DisplayPing("c", connected: true, Now).Should().Be(32);
        reach.TunnelPing("b", Now).Should().Be(120);
        reach.TunnelRounds("b").Should().Be(1);

        // A pinned ping wins over the tunnel one; an old tunnel ping is not shown.
        reach.RecordRound(R(("b", 70)), vpnOff: false, Now + TimeSpan.FromMinutes(1));
        reach.DisplayPing("b", connected: true, Now + TimeSpan.FromMinutes(1)).Should().Be(70);
        reach.RecordRound(R(("b", null)), vpnOff: false, Now + TimeSpan.FromMinutes(40));
        reach.DisplayPing("b", connected: true, Now + TimeSpan.FromMinutes(40)).Should().BeNull();
        // With the VPN off the tunnel value is never shown.
        reach.DisplayPing("b", connected: false, Now).Should().BeNull();
    }

    [Fact]
    public void TunnelPingsNeverHideOrUnhide()
    {
        var reach = new ColituReachability();
        var ids = new[] { "a", "b", "c", "d", "e" };
        for (var round = 0; round < 2; round++)
        {
            reach.RecordRound(R(("a", null), ("b", 20), ("c", 20), ("d", 20), ("e", 20)), vpnOff: true, Now + TimeSpan.FromMinutes(round));
        }
        reach.Hidden(ids).Should().BeEquivalentTo(["a"]);
        // A successful tunnel ping does not bring it back...
        reach.RecordTunnel(R(("a", 90)), Now + TimeSpan.FromMinutes(3));
        reach.Hidden(ids).Should().BeEquivalentTo(["a"]);
        reach.IsUnreachable("a").Should().BeTrue();
        // ...and failed tunnel pings hide nothing.
        reach.RecordTunnel(R(("b", null), ("c", null)), Now + TimeSpan.FromMinutes(4));
        reach.RecordTunnel(R(("b", null), ("c", null)), Now + TimeSpan.FromMinutes(5));
        reach.Hidden(ids).Should().BeEquivalentTo(["a"]);
    }

    [Fact]
    public async Task SocksRequestAndReplyAreParsed()
    {
        var request = ColituTunnelPing.ConnectRequest(IPAddress.Parse("203.0.113.42"), 3443);
        request.Should().Equal(0x05, 0x01, 0x00, 0x01, 203, 0, 113, 42, 0x0D, 0x73);

        (await ColituTunnelPing.ReadConnectReplyAsync(new MemoryStream([0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0]), CancellationToken.None)).Should().BeTrue();
        (await ColituTunnelPing.ReadConnectReplyAsync(new MemoryStream([0x05, 0x00, 0x00, 0x03, 3, (byte)'a', (byte)'.', (byte)'b', 0, 80]), CancellationToken.None)).Should().BeTrue();
        (await ColituTunnelPing.ReadConnectReplyAsync(new MemoryStream([0x05, 0x05, 0x00, 0x01, 0, 0, 0, 0, 0, 0]), CancellationToken.None)).Should().BeFalse("connection refused");

        var out1 = new DuplexStream([0x05, 0x00]);
        (await ColituTunnelPing.HandshakeAsync(out1, null, CancellationToken.None)).Should().BeTrue();
        out1.Written.Should().Equal(0x05, 0x01, 0x00);

        var out2 = new DuplexStream([0x05, 0x02, 0x01, 0x00]);
        (await ColituTunnelPing.HandshakeAsync(out2, new NetworkCredential("u", "pw"), CancellationToken.None)).Should().BeTrue();
        out2.Written.Should().Equal(0x05, 0x01, 0x02, 0x01, 1, (byte)'u', 2, (byte)'p', (byte)'w');

        (await ColituTunnelPing.HandshakeAsync(new DuplexStream([0x05, 0xFF]), null, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task NoProxyOrPrivateTargetGivesNoPing()
    {
        (await ColituTunnelPing.ProbeAsync("192.168.1.10", 3443, 1080, null, 500)).Should().BeNull();
        (await ColituTunnelPing.ProbeAsync("203.0.113.42", 3443, 0, null, 500)).Should().BeNull();
        // Nothing listens on the port: no ping, no exception.
        var free = new TcpListener(IPAddress.Loopback, 0);
        free.Start();
        var port = ((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();
        (await ColituTunnelPing.ProbeAsync("203.0.113.42", 3443, port, null, 1000)).Should().BeNull();
    }

    /// <summary>Reads from a fixed answer, records what was written.</summary>
    private sealed class DuplexStream(byte[] answer) : Stream
    {
        private readonly MemoryStream _in = new(answer);
        private readonly MemoryStream _out = new();
        public byte[] Written => _out.ToArray();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _in.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _out.Write(buffer, offset, count);
    }
}
