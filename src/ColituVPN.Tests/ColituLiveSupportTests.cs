using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using AwesomeAssertions;
using v2rayN.Desktop.Services;
using v2rayN.Desktop.Views;
using Xunit;

namespace ColituVPN.Tests;

/// <summary>
/// 2026-10-14: the assistant chat in Support, and the app's language on every API request
/// (the panel keeps it as the account's language for e-mails and notices).
/// </summary>
public class ColituLiveSupportTests
{
    [Theory]
    [InlineData("tr")]
    [InlineData("ru")]
    [InlineData("en")]
    public void ApiRequests_CarryTheAppLanguage(string language)
    {
        var previous = Loc.I.Language;
        try
        {
            Loc.I.SetLanguage(language);
            var request = ColituAuthService.WithAppLanguage(new HttpRequestMessage(HttpMethod.Get, "https://api.colitu.com/api/v1/me"));
            request.Headers.GetValues("Accept-Language").Should().Equal(language);

            // The chat names the same language and the app channel.
            var body = JsonDocument.Parse(JsonSerializer.Serialize(ColituBotService.ChatBody("c1", "  Bağlanamıyorum  "))).RootElement;
            body.GetProperty("locale").GetString().Should().Be(language);
            body.GetProperty("channel").GetString().Should().Be("app");
            body.GetProperty("message").GetString().Should().Be("Bağlanamıyorum");
            body.GetProperty("conversation_id").GetString().Should().Be("c1");
        }
        finally
        {
            Loc.I.SetLanguage(previous);
        }
    }

    [Fact]
    public void ApiRequests_KeepAnExplicitLanguage()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.colitu.com/");
        request.Headers.TryAddWithoutValidation("Accept-Language", "fa");

        ColituAuthService.WithAppLanguage(request).Headers.GetValues("Accept-Language").Should().Equal("fa");
    }

    [Fact]
    public void SignIn_SendsTheLanguageOnlyAsAHeader()
    {
        // The panel refuses unknown fields in login/reset bodies (DisallowUnknownFields): a
        // "locale" there would break sign-in against a panel that does not know it yet.
        var source = File.ReadAllText(Path.Combine(AppSource(), "Services", "ColituAuthService.cs"));
        source.Should().Contain("\"/auth/login\", new { email = email.Trim(), password }")
            .And.Contain("\"/auth/password/reset\", new { email = email.Trim(), code = code.Trim(), password }")
            .And.Contain("\"/client/locale\"");
        File.ReadAllText(Path.Combine(AppSource(), "Views", "ColituMainWindow.axaml.cs")).Should().Contain("ReportLanguageAsync()");
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "BOT_CONVERSATION_TOO_LONG", "bot.err.long")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "BOT_DISABLED", "bot.err.off")]
    [InlineData(HttpStatusCode.TooManyRequests, "RATE_LIMITED", "bot.err.limit")]
    [InlineData(HttpStatusCode.BadRequest, "BOT_INVALID_INPUT", "bot.err.invalid")]
    [InlineData(HttpStatusCode.InternalServerError, "INTERNAL_ERROR", "bot.err.network")]
    public void ChatErrors_HaveTheirOwnText(HttpStatusCode status, string code, string key)
    {
        ColituBotService.ErrorKey(new ColituApiException(status, "x", code)).Should().Be(key);
        Loc.Has(key).Should().BeTrue();
    }

    [Fact]
    public void ChatErrors_NetworkFailure()
    {
        ColituBotService.ErrorKey(new HttpRequestException("down")).Should().Be("bot.err.network");
    }

    [Fact]
    public void AnswerAndHandover_ReadThePanelShape()
    {
        var answer = JsonSerializer.Deserialize<ColituBotAnswer>("""{"conversation_id":"c1","answer":"Merhaba","sources":[{"title":"Bağlantı","url":"https://docs.colitu.com/tr/connect"}],"fallback":false,"locale":"tr","handoff":true}""")!;
        answer.ConversationId.Should().Be("c1");
        answer.Handoff.Should().BeTrue();
        answer.Sources!.Single().Url.Should().Be("https://docs.colitu.com/tr/connect");

        var handover = JsonSerializer.Deserialize<ColituBotHandover>("""{"support_conversation_id":"s1","ticket_number":"CL-1234","signed_in":true}""")!;
        handover.SupportConversationId.Should().Be("s1");
        handover.TicketNumber.Should().Be("CL-1234");
    }

    [Theory]
    [InlineData("https://docs.colitu.com/tr/connect", true)]
    [InlineData("https://colitu.com/pricing", true)]
    [InlineData("http://docs.colitu.com/tr", false)]
    [InlineData("https://colitu.com.evil.example/", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("", false)]
    public void AnswerLinks_OnlyColituPages(string url, bool allowed)
    {
        ColituMainWindow.IsHelpLink(url).Should().Be(allowed);
    }

    [Fact]
    public void ChatTexts_InEveryLanguage_WithTheTicketPlaceholder()
    {
        foreach (var key in new[] { "bot.button", "bot.greeting", "bot.typing", "bot.helpful", "bot.human", "bot.handover", "bot.escalated" })
        {
            Loc.ValuesOf(key).Should().HaveCount(3, key).And.OnlyContain(value => value.Length > 0);
        }
        Loc.ValuesOf("bot.escalated").Should().OnlyContain(value => value.Contains("{ticket}"));
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

public class ColituLiveSupportMarkdownTests
{
    [Theory]
    [InlineData("Bakın: [bağlantı testleri](https://docs.colitu.com/tr/connection-tests) rehberi.", "Bakın: bağlantı testleri rehberi.")]
    [InlineData("**Önemli:** `TUN` modunu açın.", "Önemli: TUN modunu açın.")]
    [InlineData("1. Adım\n2. Adım", "1. Adım\n2. Adım")]
    public void AssistantMarkdown_ShowsAsPlainText(string answer, string shown)
    {
        AwesomeAssertions.AssertionExtensions.Should(v2rayN.Desktop.Views.ColituMainWindow.PlainAnswer(answer)).Be(shown);
    }
}

/// <summary>2026-10-14: "Live support" is a top tab (with an unread dot), not a window button.</summary>
public class ColituLiveSupportTabTests
{
    [Fact]
    public void LiveSupport_IsATopTab_WithDot_AndNoLauncher()
    {
        var xaml = File.ReadAllText(Path.Combine(AppSource(), "Views", "ColituMainWindow.axaml"));
        xaml.Should().Contain("x:Name=\"NavSupport\"")
            .And.Contain("AutomationProperties.Name=\"{s:T nav.liveSupport}\"")
            .And.Contain("Tag=\"support\"")
            .And.Contain("x:Name=\"NavSupportDot\"")
            .And.NotContain("SupportLauncher");
        var window = File.ReadAllText(Path.Combine(AppSource(), "Views", "ColituMainWindow.axaml.cs"));
        window.Should().Contain("\"support\" => NavSupport,").And.Contain("private void FitNav()");
    }

    [Fact]
    public void LiveSupport_TabLabel_InEveryLanguage()
    {
        Loc.ValuesOf("nav.liveSupport").Should().Equal("Поддержка", "Canlı destek", "Live support");
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
