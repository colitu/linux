using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ServiceLib.Enums;
using ServiceLib.Models;
using ServiceLib.Services.CoreConfig;
using ServiceLib.Tests.CoreConfig;
using v2rayN.Desktop.Services;
using Xunit;

namespace v2rayN.Tests;

public class ColituVlessWsTests
{
    private const string WsPayload = """{"schema_version":1,"protocol":"vless-ws","endpoint":{"host":"relay.example.test","port":443},"credentials":{"uuid":"00000000-0000-4000-8000-000000000004"},"transport":{"type":"ws","path":"/service/telemetry/fixture/","host":"relay.example.test"},"security":{"type":"tls","server_name":"relay.example.test","fingerprint":"firefox","alpn":["http/1.1"]},"dns_policy":{},"routing_policy":{}}""";

    private static JsonElement Payload(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void WsPayload_BecomesAVlessWsLinkWithoutFlow()
    {
        var link = ColituShareLinkBuilder.Build(Payload(WsPayload), "x");

        link.Should().StartWith("vless://00000000-0000-4000-8000-000000000004@relay.example.test:443?")
            .And.Contain("type=ws").And.Contain("security=tls")
            .And.Contain("path=%2Fservice%2Ftelemetry%2Ffixture%2F")
            .And.Contain("host=relay.example.test").And.Contain("sni=relay.example.test")
            .And.Contain("alpn=http%2F1.1").And.Contain("fp=firefox")
            .And.NotContain("flow=").And.NotContain("reality");
    }

    [Fact]
    public void WsPayload_WithoutPath_OrWithWrongTransport_IsRejected()
    {
        ColituShareLinkBuilder.Build(Payload(WsPayload.Replace("\"path\":\"/service/telemetry/fixture/\",", "")), "x").Should().BeNull();
        ColituShareLinkBuilder.Build(Payload(WsPayload.Replace("\"type\":\"ws\"", "\"type\":\"tcp\"")), "x").Should().BeNull();
        ColituShareLinkBuilder.Build(Payload(WsPayload.Replace("\"protocol\":\"vless-ws\"", "\"protocol\":\"vless-reality\"")), "x").Should().BeNull();
    }

    [Fact]
    public void WsPayload_MissingHostAndAlpn_FallsBackToDefaults()
    {
        var json = WsPayload.Replace(",\"host\":\"relay.example.test\"}", "}").Replace(",\"alpn\":[\"http/1.1\"]", "").Replace("\"fingerprint\":\"firefox\"", "\"fingerprint\":\"\"");
        var link = ColituShareLinkBuilder.Build(Payload(json), "x");

        link.Should().Contain("host=relay.example.test").And.Contain("alpn=http%2F1.1");
    }

    [Fact]
    public void WsLink_GivesTheXrayOutboundOfTheContract()
    {
        var node = ServiceLib.Handler.Fmt.VLESSFmt.Resolve(ColituShareLinkBuilder.Build(Payload(WsPayload), "Colitu")!, out _)!;
        node.IndexId = "n-ws";
        node.CoreType = ECoreType.Xray;
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.Xray);
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var result = new CoreConfigV2rayService(CoreConfigTestFactory.CreateContext(config, node, ECoreType.Xray)).GenerateClientConfigContent();
        result.Success.Should().BeTrue($"ret msg: {result.Msg}");

        var outbound = JsonNode.Parse(result.Data!.ToString()!)!["outbounds"]!.AsArray()
            .First(o => o!["protocol"]!.GetValue<string>() == "vless")!;
        // Older Xray format: settings.vnext[0] with users[0]; newer: the fields sit directly in settings.
        var server = outbound["settings"]!["vnext"]?[0] ?? outbound["settings"]!;
        var user = server["users"]?[0] ?? server;
        var stream = outbound["streamSettings"]!;

        server["address"]!.GetValue<string>().Should().Be("relay.example.test");
        server["port"]!.GetValue<int>().Should().Be(443);
        user["id"]!.GetValue<string>().Should().Be("00000000-0000-4000-8000-000000000004");
        user["flow"]?.GetValue<string>().Should().BeNullOrEmpty();
        stream["network"]!.GetValue<string>().Should().Be("ws");
        stream["wsSettings"]!["path"]!.GetValue<string>().Should().Be("/service/telemetry/fixture/");
        (stream["wsSettings"]!["host"]?.GetValue<string>() ?? stream["wsSettings"]!["headers"]?["Host"]?.GetValue<string>())
            .Should().Be("relay.example.test");
        stream["security"]!.GetValue<string>().Should().Be("tls");
        stream["tlsSettings"]!["serverName"]!.GetValue<string>().Should().Be("relay.example.test");
        stream["tlsSettings"]!["alpn"]![0]!.GetValue<string>().Should().Be("http/1.1");
        stream["tlsSettings"]!["fingerprint"]!.GetValue<string>().Should().Be("firefox");
        stream["realitySettings"].Should().BeNull();
    }

    [Fact]
    public void WsTransport_IsLastAndNotAMultihopChain()
    {
        ColituVpnService.TransportPriority("vless-ws").Should().BeGreaterThan(ColituVpnService.TransportPriority("shadowsocks"));
        ColituWarmSpare.RunsOn("vless-ws", singBox: false).Should().BeTrue();
    }
}
