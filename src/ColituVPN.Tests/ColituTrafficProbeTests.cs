using System.Net;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

/// <summary>
/// The connect check must not pass on a server that refused us: a fake proxy that takes every
/// connection and answers with a cover site's page (as a Reality/VLESS fallback to the cover site,
/// or a captive portal, does) must not count as "connected".
/// </summary>
public class ColituTrafficProbeTests
{
    /// <summary>SOCKS5 without auth; whatever comes after the CONNECT gets the same HTTP answer.</summary>
    private sealed class FakeFallbackProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public FakeFallbackProxy(string answer)
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            try
                            {
                                var s = client.GetStream();
                                var buf = new byte[1024];
                                await s.ReadAsync(buf, _stop.Token); // greeting
                                await s.WriteAsync(new byte[] { 5, 0 }, _stop.Token);
                                await s.ReadAsync(buf, _stop.Token); // CONNECT
                                await s.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, _stop.Token);
                                await s.ReadAsync(buf, _stop.Token); // the request (or a TLS hello)
                                await s.WriteAsync(Encoding.ASCII.GetBytes(answer), _stop.Token);
                                await Task.Delay(200, _stop.Token);
                            }
                            catch
                            {
                                // The client gave up: fine.
                            }
                        }
                    });
                }
            });
        }

        public ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            return ValueTask.CompletedTask;
        }
    }

    private static string Http(int status, string reason, string body) =>
        $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html\r\nContent-Length: {Encoding.ASCII.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";

    [Fact]
    public async Task ACoverSiteBehindTheProxy_IsNotConnected()
    {
        await using var proxy = new FakeFallbackProxy(Http(200, "OK", "<!doctype html><html><head><title>Welcome</title></head><body>cover site</body></html>"));
        var result = await ColituVpnService.ProbeThroughLocalProxyAsync(proxy.Port, rounds: 1, roundTimeout: TimeSpan.FromSeconds(3));
        result.Success.Should().BeFalse(result.Detail);
    }

    [Fact]
    public async Task TheCheckPagesOwnAnswer_IsConnected()
    {
        // The plain-http check page answered exactly: the tunnel carries traffic.
        await using var proxy = new FakeFallbackProxy(Http(200, "OK", "Microsoft Connect Test"));
        var result = await ColituVpnService.ProbeThroughLocalProxyAsync(proxy.Port, rounds: 1, roundTimeout: TimeSpan.FromSeconds(3));
        result.Success.Should().BeTrue(result.Detail);
    }
}
