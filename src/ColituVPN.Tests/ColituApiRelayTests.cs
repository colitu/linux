using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

/// <summary>API relays of the signed endpoint list: parser, order, self-heal and the TLS dial (SNI ≠ verified name).</summary>
public class ColituApiRelayTests
{
    private static readonly string[] Api = ["https://api.colitu.com/api/v1", "https://mirror.example.test/capi/v1"];
    private static ColituApiRelay Relay(string id, string? region = null, bool whitelist = false) =>
        new(id, "203.0.113.10", 8444, "front.example.test", "api.colitu.com", "/api/v1", region, whitelist);
    private static readonly ColituApiRelay Ru = Relay("ru1", "RU");
    private static readonly ColituApiRelay Bgs = Relay("bgs1", "RU", whitelist: true);
    private static readonly ColituApiRelay De = Relay("de1", "DE");
    private static ColituEndpointList NewList() => new(null, ["https://mirror.example.test"], [Ru, Bgs, De]);

    private static ColituApiRelay? ParseOne(string fields)
    {
        using var doc = JsonDocument.Parse("{" + fields + "}");
        return ColituApiRelays.Parse(doc.RootElement);
    }

    // ---- parser ----------------------------------------------------------------------------

    [Fact]
    public void AWellFormedRelay_IsAccepted_UnknownKeysIgnored()
    {
        ParseOne("\"id\":\"ru1\",\"addr\":\"203.0.113.10:8444\",\"sni\":\"Front.Example.test\",\"verify\":\"api.colitu.com\",\"prefix\":\"/api/v1/\",\"region\":\"ru\",\"whitelist\":true,\"future\":{\"x\":1}")
            .Should().Be(new ColituApiRelay("ru1", "203.0.113.10", 8444, "front.example.test", "api.colitu.com", "/api/v1", "RU", true));
        var named = ParseOne("\"id\":\"bgs1\",\"addr\":\"cdn.example.test:443\",\"sni\":\"a.example.test\",\"verify\":\"api.colitu.com\",\"prefix\":\"/api/v1\"");
        named!.Host.Should().Be("cdn.example.test");
        named.Whitelist.Should().BeFalse();
    }

    [Theory]
    [InlineData("203.0.113.10")]
    [InlineData("203.0.113.10:0")]
    [InlineData("203.0.113.10:65536")]
    [InlineData("203.0.113.10:x")]
    [InlineData("[2001:db8::1]:443")]
    [InlineData(":443")]
    [InlineData("300.1.1.1:443")]
    [InlineData("bad_host:443")]
    public void BadAddr_IsRefused(string addr)
    {
        ParseOne($"\"id\":\"r\",\"addr\":\"{addr}\",\"sni\":\"a.example.test\",\"verify\":\"api.colitu.com\",\"prefix\":\"/api/v1\"").Should().BeNull();
    }

    [Theory]
    [InlineData("evil.example.test")]
    [InlineData("API.colitu.com")]
    [InlineData("*.colitu.com")]
    [InlineData("")]
    public void VerifyOtherThanTheApiName_IsRefused(string verify)
    {
        ParseOne($"\"id\":\"r\",\"addr\":\"203.0.113.10:443\",\"sni\":\"a.example.test\",\"verify\":\"{verify}\",\"prefix\":\"/api/v1\"").Should().BeNull();
    }

    [Theory]
    [InlineData("\"sni\":\"203.0.113.9\",\"verify\":\"api.colitu.com\",\"prefix\":\"/api/v1\"")]
    [InlineData("\"sni\":\"localhost\",\"verify\":\"api.colitu.com\",\"prefix\":\"/api/v1\"")]
    [InlineData("\"sni\":\"a.example.test\",\"prefix\":\"/api/v1\"")]
    [InlineData("\"sni\":\"a.example.test\",\"verify\":\"api.colitu.com\",\"prefix\":\"api/v1\"")]
    [InlineData("\"sni\":\"a.example.test\",\"verify\":\"api.colitu.com\",\"prefix\":\"/api/../x\"")]
    [InlineData("\"sni\":\"a.example.test\",\"verify\":\"api.colitu.com\",\"prefix\":\"//evil.example\"")]
    public void BadSniVerifyOrPrefix_IsRefused(string rest)
    {
        ParseOne("\"id\":\"r\",\"addr\":\"203.0.113.10:443\"," + rest).Should().BeNull();
    }

    [Fact]
    public void ABadEntry_IsSkipped_NotTheList()
    {
        using var doc = JsonDocument.Parse("[" +
            "{\"id\":\"ru1\",\"addr\":\"203.0.113.10:8444\",\"sni\":\"a.example.test\",\"verify\":\"api.colitu.com\",\"prefix\":\"/api/v1\"}," +
            "{\"id\":\"bad\",\"addr\":\"203.0.113.11:8444\",\"sni\":\"a.example.test\",\"verify\":\"evil.example.test\",\"prefix\":\"/api/v1\"}," +
            "\"not an object\"," +
            "{\"id\":\"ru1\",\"addr\":\"203.0.113.12:8444\",\"sni\":\"b.example.test\",\"verify\":\"api.colitu.com\",\"prefix\":\"/api/v1\"}]");
        var relays = ColituApiRelays.ParseArray(doc.RootElement);
        relays.Select(r => r.Id).Should().Equal("ru1");
        relays[0].Host.Should().Be("203.0.113.10");
    }

    [Fact]
    public void RelaysFromTheBuild_AreParsed()
    {
        var relays = ColituApiRelays.ParseBuildValue(" ru1|203.0.113.10:8444|front.example.test|RU|0 ; bgs1|cdn.example.test:443|wl.example.test|RU|1;broken|x;");
        relays.Select(r => r.Id).Should().Equal("ru1", "bgs1");
        relays[1].Whitelist.Should().BeTrue();
        relays[0].Prefix.Should().Be("/api/v1");
        ColituApiRelays.ParseBuildValue(null).Should().BeEmpty();
    }

    // ---- order -----------------------------------------------------------------------------

    [Fact]
    public void Order_PutsRelaysWhereTheNetworkNeedsThem()
    {
        var list = NewList();
        list.Bases(ColituRouteContext.None).Should().Equal(Api[0], Api[1], "relay:de1", "relay:ru1", "relay:bgs1");
        list.Bases(new("ru", false)).Should().Equal(Api[0], "relay:ru1", "relay:bgs1", Api[1], "relay:de1");
        list.Bases(new("RU", true)).Should().Equal("relay:bgs1", "relay:ru1", Api[0], Api[1], "relay:de1");
        list.RememberWorking("relay:ru1");
        list.Bases(ColituRouteContext.None).Should().Equal("relay:ru1", Api[0], Api[1], "relay:de1", "relay:bgs1");
        // Request URLs are never built on a relay.
        list.UrlBase().Should().Be(Api[0]);
        list.ApiPath().Should().Be("relay:ru1");
        list.RememberWorking(Api[1]);
        list.ApiPath().Should().Be("mirror");
        list.RememberWorking(Api[0]);
        list.ApiPath().Should().Be("primary");
        list.PinnedUrls().Should().Contain("https://203.0.113.10:8444/");
    }

    [Fact]
    public async Task Failover_WalksIntoTheRelays()
    {
        var list = NewList();
        var tried = new List<string>();
        using var response = await ColituEndpointList.SendWithFailoverAsync(
            list.Bases(new("RU", false)),
            b =>
            {
                tried.Add(b);
                return b == "relay:bgs1"
                    ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))
                    : throw new HttpRequestException(HttpRequestError.ConnectionError, "refused");
            },
            ex => ColituEndpointList.ShouldFailover(ex, isGet: true),
            list.RememberWorking,
            CancellationToken.None);
        tried.Should().Equal(Api[0], "relay:ru1", "relay:bgs1");
        list.LastWorking().Should().Be("relay:bgs1");
    }

    [Fact]
    public async Task TheList_IsRefreshedThroughARelay()
    {
        var list = NewList();
        var asked = new List<string>();
        var signed = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "valid.signed.json"));
        var ok = await list.RefreshAsync(CancellationToken.None, (url, _) =>
        {
            asked.Add(url);
            return Task.FromResult(url == "relay:bgs1/downloads/endpoints.json" ? signed : null);
        });
        ok.Should().BeTrue();
        asked.Should().Equal(
            "https://colitu.com/downloads/endpoints.json",
            "https://mirror.example.test/downloads/endpoints.json",
            "relay:ru1/downloads/endpoints.json",
            "relay:bgs1/downloads/endpoints.json");
        // The fixture has no relays key: the built-in relays stay.
        list.Relays().Select(r => r.Id).Should().Equal("ru1", "bgs1", "de1");
    }

    // ---- self-heal -------------------------------------------------------------------------

    [Fact]
    public void Healer_WaitsTenMinutesOffTheFirstBase()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var healer = new ColituApiPathHealer(TimeSpan.FromMinutes(10));
        healer.Claim(t0, offFirst: false).Should().BeFalse();
        healer.Claim(t0.AddSeconds(1), offFirst: true).Should().BeFalse();
        healer.Claim(t0.AddSeconds(600), offFirst: true).Should().BeFalse();
        healer.Claim(t0.AddSeconds(601), offFirst: true).Should().BeTrue();
        healer.Claim(t0.AddSeconds(602), offFirst: true).Should().BeFalse();
        healer.Claim(t0.AddSeconds(1201), offFirst: true).Should().BeTrue();
        healer.Claim(t0.AddSeconds(1300), offFirst: false).Should().BeFalse();
        healer.Claim(t0.AddSeconds(1400), offFirst: true).Should().BeFalse();
    }

    [Fact]
    public async Task Heal_GoesBackToThePrimaryOnceItAnswers()
    {
        var list = NewList();
        list.RememberWorking("relay:ru1");
        var probed = new List<string>();
        var primaryUp = false;
        Task<bool> Probe(string b, CancellationToken _)
        {
            probed.Add(b);
            return Task.FromResult(b == Api[0] && primaryUp);
        }
        (await list.HealAsync(ColituRouteContext.None, all: false, Probe, CancellationToken.None)).Should().BeNull();
        probed.Should().Equal(Api[0], Api[1], "relay:de1");
        list.LastWorking().Should().Be("relay:ru1");
        primaryUp = true;
        probed.Clear();
        (await list.HealAsync(ColituRouteContext.None, all: false, Probe, CancellationToken.None)).Should().Be(Api[0]);
        probed.Should().Equal(Api[0]);
        list.LastWorking().Should().Be(Api[0]);
    }

    [Fact]
    public async Task NetworkChange_ChoosesThePathFromTheStart()
    {
        var list = NewList();
        list.RememberWorking(Api[0]);
        var back = await list.HealAsync(new("RU", true), all: true, (b, _) => Task.FromResult(b == "relay:bgs1"), CancellationToken.None);
        back.Should().Be("relay:bgs1");
        list.LastWorking().Should().Be("relay:bgs1");
    }

    // ---- the TLS dial ----------------------------------------------------------------------

    private sealed class FakeRelay : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        public string? Sni;
        public string? Head;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public FakeRelay(X509Certificate2 certificate)
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await using var tls = new SslStream(client.GetStream());
                    try
                    {
                        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                        {
                            ServerCertificateSelectionCallback = (_, name) =>
                            {
                                Sni = name;
                                return certificate;
                            }
                        }, _stop.Token);
                        var buffer = new byte[4096];
                        var text = new StringBuilder();
                        while (!text.ToString().Contains("\r\n\r\n"))
                        {
                            var n = await tls.ReadAsync(buffer, _stop.Token);
                            if (n == 0)
                            {
                                break;
                            }
                            text.Append(Encoding.ASCII.GetString(buffer, 0, n));
                        }
                        Head = text.ToString();
                        await tls.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"), _stop.Token);
                    }
                    catch
                    {
                        // A refused handshake ends this connection only.
                    }
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

    private static readonly X509Certificate2 Root = MakeRoot();

    private static X509Certificate2 MakeRoot()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Colitu Test Root", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    private static X509Certificate2 Leaf(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(name);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var serial = RandomNumberGenerator.GetBytes(8);
        using var cert = request.Create(Root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10), serial);
        // SslStream needs the private key in an exportable form.
        return X509CertificateLoader.LoadPkcs12(cert.CopyWithPrivateKey(key).Export(X509ContentType.Pfx), null);
    }

    /// <summary>The production name rule plus a chain built on the test root (instead of the system roots and pins).</summary>
    private static bool TestValidate(X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (certificate == null)
        {
            return false;
        }
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(Root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        using var leaf = new X509Certificate2(certificate);
        var trusted = chain.Build(leaf);
        return trusted && ColituApiRelays.RelayNameOk(certificate, errors & ~SslPolicyErrors.RemoteCertificateChainErrors, "api.colitu.com");
    }

    private static HttpClient ClientFor(ColituApiRelay relay) => new(ColituApiRelays.CreateHandler(
        relay,
        (cert, _, errors) => TestValidate(cert, errors),
        async (endpoint, token) =>
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(IPAddress.Loopback, endpoint.Port, token);
            return new NetworkStream(socket, ownsSocket: true);
        }))
    { Timeout = TimeSpan.FromSeconds(10) };

    [Fact]
    public async Task RelayDial_SendsTheSni_AndTheApiHost()
    {
        await using var server = new FakeRelay(Leaf("api.colitu.com"));
        var relay = new ColituApiRelay("t1", "127.0.0.1", server.Port, "front.example.test", "api.colitu.com", "/api/v1", "RU", false);
        using var client = ClientFor(relay);
        using var response = await client.GetAsync(ColituApiRelays.RelayUri("/api/v1/ping"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("ok");
        server.Sni.Should().Be("front.example.test");
        server.Head.Should().StartWith("GET /api/v1/ping HTTP/1.1\r\n");
        server.Head!.Split("\r\n").Should().Contain(line => line.Equals("Host: api.colitu.com", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ACertificateForAnotherName_IsRefused()
    {
        // Valid chain, but for the SNI's name, not for api.colitu.com.
        await using var server = new FakeRelay(Leaf("front.example.test"));
        var relay = new ColituApiRelay("t2", "127.0.0.1", server.Port, "front.example.test", "api.colitu.com", "/api/v1", null, false);
        using var client = ClientFor(relay);
        var act = () => client.GetAsync(ColituApiRelays.RelayUri("/api/v1/ping"));
        var thrown = await act.Should().ThrowAsync<HttpRequestException>();
        ColituEndpointList.ShouldFailover(thrown.Which, isGet: false).Should().BeTrue();
        // No request byte reaches the relay. On Linux (OpenSSL) the server side finishes its handshake
        // before the client drops the connection, so it reads an empty head instead of none.
        server.Head.Should().BeNullOrEmpty();
    }

    [Fact]
    public void RelayNameRule_AcceptsOnlyTheNameMismatchError()
    {
        using var good = Leaf("api.colitu.com");
        using var other = Leaf("front.example.test");
        ColituApiRelays.RelayNameOk(good, SslPolicyErrors.RemoteCertificateNameMismatch, "api.colitu.com").Should().BeTrue();
        ColituApiRelays.RelayNameOk(good, SslPolicyErrors.None, "api.colitu.com").Should().BeTrue();
        ColituApiRelays.RelayNameOk(good, SslPolicyErrors.RemoteCertificateChainErrors, "api.colitu.com").Should().BeFalse();
        ColituApiRelays.RelayNameOk(other, SslPolicyErrors.RemoteCertificateNameMismatch, "api.colitu.com").Should().BeFalse();
        ColituApiRelays.RelayNameOk(null, SslPolicyErrors.None, "api.colitu.com").Should().BeFalse();
        // The production check also needs the API pins: a test root is not one of them.
        ColituApiRelays.ValidateRelayCertificate(good, null, SslPolicyErrors.None, "api.colitu.com", DateTimeOffset.UtcNow).Should().BeFalse();
    }

    [Fact]
    public async Task TheRelayHandler_RefusesAnyOtherHost()
    {
        using var client = new HttpClient(ColituApiRelays.CreateHandler(Ru, (_, _, _) => true, (_, _) => throw new InvalidOperationException("must not dial")));
        var act = () => client.GetAsync("http://evil.example.test/x");
        await act.Should().ThrowAsync<HttpRequestException>();
    }
}
