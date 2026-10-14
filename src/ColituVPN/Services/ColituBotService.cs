using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace v2rayN.Desktop.Services;

/// <summary>
/// Live support chat with Colitu's assistant (the panel's <c>/api/v1/bot</c>, the same assistant
/// as the website's): questions in the app's language, "was this helpful", and a handover that
/// opens an ordinary support ticket for the signed-in account (it shows in the Support list).
/// </summary>
public sealed class ColituBotService
{
    public static ColituBotService Instance { get; } = new();

    /// <summary>The panel refuses longer questions.</summary>
    public const int MaxQuestionChars = 1500;

    public const string ErrorDisabled = "BOT_DISABLED";
    public const string ErrorNotFound = "BOT_CONVERSATION_NOT_FOUND";
    public const string ErrorTooLong = "BOT_CONVERSATION_TOO_LONG";
    public const string ErrorRateLimited = "RATE_LIMITED";

    private readonly ColituAuthService _auth = ColituAuthService.Instance;

    private ColituBotService() { }

    /// <summary>
    /// Asks the assistant. A conversation the panel no longer knows for this account (it was
    /// opened before a sign-out, or expired) starts over instead of failing every time.
    /// </summary>
    public async Task<ColituBotAnswer> AskAsync(string? conversationId, string question)
    {
        try
        {
            return await PostAsync<ColituBotAnswer>("/bot/chat", ChatBody(conversationId, question));
        }
        catch (ColituApiException ex) when (ex.ErrorCode == ErrorNotFound && !string.IsNullOrEmpty(conversationId))
        {
            return await PostAsync<ColituBotAnswer>("/bot/chat", ChatBody(null, question));
        }
    }

    /// <summary>"Was this helpful?" for the conversation. Best effort.</summary>
    public async Task FeedbackAsync(string conversationId, bool helpful)
    {
        try
        {
            using var response = await _auth.SendAuthorizedRequestAsync(() => Json(HttpMethod.Post, "/bot/feedback", new { conversation_id = conversationId, helpful }));
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"ColituBotService.Feedback: {ex.GetType().Name}");
        }
    }

    /// <summary>Hands the conversation (or, without one, the note) to the support team as a ticket.</summary>
    public Task<ColituBotHandover> EscalateAsync(string? conversationId, string note) =>
        PostAsync<ColituBotHandover>("/bot/escalate", new { conversation_id = conversationId ?? "", note = note.Trim() });

    /// <summary>The chat request: the app's language and the "app" channel (the panel's statistics tell apps from the website).</summary>
    internal static object ChatBody(string? conversationId, string question) =>
        new { conversation_id = conversationId ?? "", message = question.Trim(), locale = Loc.I.Language, channel = "app" };

    /// <summary>What a failure means for the chat: the key of the text to show.</summary>
    public static string ErrorKey(Exception ex) => ex switch
    {
        ColituApiException { ErrorCode: ErrorTooLong } => "bot.err.long",
        ColituApiException { ErrorCode: ErrorDisabled } or ColituApiException { StatusCode: HttpStatusCode.ServiceUnavailable } => "bot.err.off",
        ColituApiException { ErrorCode: ErrorRateLimited } or ColituApiException { StatusCode: HttpStatusCode.TooManyRequests } => "bot.err.limit",
        ColituApiException { StatusCode: HttpStatusCode.BadRequest } => "bot.err.invalid",
        _ => "bot.err.network"
    };

    private async Task<T> PostAsync<T>(string path, object body) where T : class, new()
    {
        using var response = await _auth.SendAuthorizedRequestAsync(() => Json(HttpMethod.Post, path, body));
        return (await _auth.ReadResponseJsonAsync<Envelope<T>>(response))?.Data ?? new T();
    }

    private HttpRequestMessage Json(HttpMethod method, string path, object body) => new(method, _auth.ApiUri(path))
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };

    private sealed class Envelope<T> where T : class
    {
        [JsonPropertyName("data")]
        public T? Data { get; set; }
    }
}

public sealed class ColituBotAnswer
{
    [JsonPropertyName("conversation_id")]
    public string? ConversationId { get; set; }

    [JsonPropertyName("answer")]
    public string Answer { get; set; } = "";

    [JsonPropertyName("sources")]
    public List<ColituBotSource>? Sources { get; set; }

    /// <summary>The assistant recommends a person (it could not answer, or the user asked for one).</summary>
    [JsonPropertyName("handoff")]
    public bool Handoff { get; set; }
}

public sealed class ColituBotSource
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";
}

public sealed class ColituBotHandover
{
    [JsonPropertyName("support_conversation_id")]
    public string SupportConversationId { get; set; } = "";

    [JsonPropertyName("ticket_number")]
    public string TicketNumber { get; set; } = "";
}
