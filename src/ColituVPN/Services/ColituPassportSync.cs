using System.Net.Http;
using System.Text.Json;

namespace v2rayN.Desktop.Services;

/// <summary>One answer of the panel: HTTP status and body (status 0 = no answer: offline or timed out).</summary>
public sealed record PassportResponse(int Status, string Body)
{
    public bool Ok => Status is >= 200 and < 300;

    public string? ErrorCode
    {
        get
        {
            try
            {
                using var doc = JsonDocument.Parse(Body);
                return doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object && e.TryGetProperty("code", out var c) ? c.GetString() : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}

/// <summary>Sends a request to the panel with the device's session (the app: <see cref="ColituAuthService"/>; tests: a fake).</summary>
public interface IPassportTransport
{
    Task<PassportResponse> SendAsync(HttpMethod method, string path, object? body, CancellationToken token = default);
}

/// <summary>The app's transport: the signed-in session of <see cref="ColituAuthService"/>.</summary>
public sealed class ColituAuthPassportTransport(ColituAuthService auth) : IPassportTransport
{
    public Task<PassportResponse> SendAsync(HttpMethod method, string path, object? body, CancellationToken token = default) =>
        auth.SendPassportAsync(method, path, body, token);
}

/// <summary>
/// The passport's server calls (docs/passport-server.md): GET /client/passport, the unseen events
/// and their ack, the profile (name, title, cover, time zone), the share report and the one-time
/// import of this device's v1 progress. Every answer that carries a state is applied to
/// <see cref="ColituPassport"/>, which keeps it as the offline cache.
/// </summary>
public sealed class ColituPassportSync(ColituPassport passport, IPassportTransport transport, Func<string?> deviceId, Func<TimeSpan>? utcOffset = null)
{
    public const string StatePath = "/client/passport";

    private readonly Func<TimeSpan> _utcOffset = utcOffset ?? (() => TimeZoneInfo.Local.GetUtcOffset(DateTime.Now));
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Brings the passport up to date: the one-time import, the time zone, the state and its unseen
    /// events (oldest first, to be played and then acked). Null when the panel could not be reached
    /// or does not offer the passport yet (the cached state stays on screen).
    /// </summary>
    public async Task<IReadOnlyList<PassportServerEvent>?> RefreshAsync(CancellationToken token = default)
    {
        if (!await _gate.WaitAsync(0, token)) return null;
        try
        {
            await ImportIfNeededAsync(token);
            var answer = await transport.SendAsync(HttpMethod.Get, StatePath, null, token);
            if (!answer.Ok || PassportState.Parse(answer.Body) is not { } state) return null;
            passport.Apply(state);
            await SyncTimeZoneAsync(token);
            return state.UnseenEvents > 0 ? await UnseenEventsAsync(token) : [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PassportServerEvent>> UnseenEventsAsync(CancellationToken token = default)
    {
        var answer = await transport.SendAsync(HttpMethod.Get, $"{StatePath}/events?unseen=1", null, token);
        return answer.Ok ? PassportIds.ParseEvents(answer.Body, passport.ProgressOf) : [];
    }

    /// <summary>The reveals up to <paramref name="throughId"/> were shown.</summary>
    public async Task<bool> AckAsync(long throughId, CancellationToken token = default)
    {
        try
        {
            var answer = await transport.SendAsync(HttpMethod.Post, $"{StatePath}/events/ack", new Dictionary<string, object> { ["through_id"] = throughId }, token);
            return answer.Ok;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Saves name, title and cover to the account (null = unchanged, "" clears the title). The
    /// server validates them; on success its answer becomes the new state. False when refused or offline.
    /// </summary>
    public async Task<bool> UpdateProfileAsync(string? nickname = null, string? titleId = null, string? coverId = null, CancellationToken token = default)
    {
        var body = new Dictionary<string, object>();
        if (nickname != null) body["nickname"] = nickname.Trim().Length > PassportGoals.NicknameMax ? nickname.Trim()[..PassportGoals.NicknameMax] : nickname.Trim();
        if (titleId != null) body["title_id"] = titleId.Replace('_', '-');
        if (coverId != null) body["cover_id"] = coverId;
        if (body.Count == 0) return true;
        try
        {
            var answer = await transport.SendAsync(HttpMethod.Put, $"{StatePath}/profile", body, token);
            if (!answer.Ok) return false;
            if (PassportState.Parse(answer.Body) is { } state) passport.Apply(state);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>Tells the server this device's offset from UTC when it changed (the passport's day starts at local midnight).</summary>
    public async Task SyncTimeZoneAsync(CancellationToken token = default)
    {
        var minutes = (int)Math.Round(_utcOffset().TotalMinutes);
        if (passport.Store.GetString("tz_sent") == minutes.ToString(CultureInfo.InvariantCulture)) return;
        var answer = await transport.SendAsync(HttpMethod.Put, $"{StatePath}/profile", new Dictionary<string, object> { ["tz_offset_min"] = minutes }, token);
        if (answer.Ok)
        {
            passport.Store.Set("tz_sent", minutes);
            if (PassportState.Parse(answer.Body) is { } state) passport.Apply(state);
        }
    }

    /// <summary>A stamp or the passport was shared (the weekly share goal; cosmetic, rate-limited by the server).</summary>
    public async Task ReportShareAsync(CancellationToken token = default)
    {
        try
        {
            await transport.SendAsync(HttpMethod.Post, $"{StatePath}/events", new Dictionary<string, object> { ["kind"] = "share" }, token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
        }
    }

    /// <summary>
    /// Once per device: the progress this device counted before v2 goes to the account (the server
    /// caps and merges it, highest wins). Marked done after a success or 409 PASSPORT_ALREADY_IMPORTED;
    /// a device without v1 progress is marked done without a call.
    /// </summary>
    public async Task ImportIfNeededAsync(CancellationToken token = default)
    {
        if (passport.Imported) return;
        if (!passport.HasLocalProgress)
        {
            passport.Imported = true;
            return;
        }
        var device = deviceId();
        if (string.IsNullOrWhiteSpace(device)) return;
        var answer = await transport.SendAsync(HttpMethod.Post, $"{StatePath}/import", passport.ImportBody(device), token);
        if (answer.Ok)
        {
            passport.Imported = true;
            if (PassportState.Parse(answer.Body) is { } state) passport.Apply(state);
        }
        else if (answer.Status == 409)
        {
            passport.Imported = true;
        }
    }
}
