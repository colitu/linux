using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace v2rayN.Desktop.Services;

/// <summary>One day of the account's traffic (UTC day).</summary>
public sealed class ColituDailyUsage
{
    [JsonPropertyName("date")] public string Date { get; set; } = "";
    [JsonPropertyName("upload_bytes")] public long UploadBytes { get; set; }
    [JsonPropertyName("download_bytes")] public long DownloadBytes { get; set; }
    [JsonIgnore] public long Total => UploadBytes + DownloadBytes;
}

/// <summary>Device rename and cleanup, the 30-day usage history and the support typing signal.</summary>
public static class ColituAccountApi
{
    private static ColituAuthService Auth => ColituAuthService.Instance;

    private static HttpRequestMessage Json(HttpMethod method, string path, object body) =>
        new(method, Auth.ApiUri(path)) { Content = JsonContent.Create(body) };

    /// <summary>Renames a device (1–100 characters).</summary>
    public static async Task RenameDeviceAsync(string id, string name)
    {
        using var response = await Auth.SendAuthorizedRequestAsync(() => Json(HttpMethod.Patch, $"/devices/{Uri.EscapeDataString(id)}", new { name = name.Trim() }));
    }

    /// <summary>
    /// The devices not seen for <paramref name="inactiveDays"/> days, this one excluded. With
    /// <paramref name="dryRun"/> only lists them; otherwise removes them. Null when the panel has no
    /// cleanup endpoint yet (the caller falls back to removing one by one).
    /// </summary>
    public static async Task<List<string>?> CleanupDevicesAsync(int inactiveDays, bool dryRun)
    {
        try
        {
            using var response = await Auth.SendAuthorizedRequestAsync(() => Json(HttpMethod.Post, "/me/devices/cleanup" + (dryRun ? "?dry_run=1" : ""), new { inactive_days = inactiveDays }));
            return (await Auth.ReadResponseJsonAsync<Envelope<CleanupDto>>(response))?.Data?.DeviceIds ?? [];
        }
        catch (ColituApiException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            return null;
        }
    }

    /// <summary>Which devices a cleanup removes: not seen for <paramref name="days"/> days, never the current one.</summary>
    public static List<string> StaleDeviceIds(IEnumerable<(string Id, bool Current, DateTimeOffset? LastSeen)> devices, int days, DateTimeOffset now) =>
        devices.Where(device => !device.Current && device.Id.Length > 0 && device.LastSeen is { } seen && now - seen > TimeSpan.FromDays(days))
            .Select(device => device.Id)
            .ToList();

    /// <summary>Daily traffic of the last <paramref name="days"/> days, oldest first; null when the panel has no history endpoint yet.</summary>
    public static async Task<List<ColituDailyUsage>?> DailyUsageAsync(int days = 30)
    {
        try
        {
            using var response = await Auth.SendAuthorizedRequestAsync(() => new HttpRequestMessage(HttpMethod.Get, Auth.ApiUri($"/me/usage/daily?days={days}")));
            return (await Auth.ReadResponseJsonAsync<Envelope<DailyDto>>(response))?.Data?.Days ?? [];
        }
        catch (ColituApiException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            return null;
        }
    }

    /// <summary>
    /// Tells support the user is typing; answers whether support is typing. Null when the panel has
    /// no typing endpoint (the indicator then stays off).
    /// </summary>
    public static async Task<bool?> SupportTypingAsync(string conversationId)
    {
        try
        {
            using var response = await Auth.SendAuthorizedRequestAsync(() => Json(HttpMethod.Post, $"/support/conversations/{Uri.EscapeDataString(conversationId)}/typing", new { }));
            return (await Auth.ReadResponseJsonAsync<Envelope<TypingDto>>(response))?.Data?.PeerTyping ?? false;
        }
        catch (ColituApiException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            return null;
        }
    }

    private sealed class Envelope<T>
    {
        [JsonPropertyName("data")] public T? Data { get; set; }
    }

    private sealed class CleanupDto
    {
        [JsonPropertyName("removed")] public int Removed { get; set; }
        [JsonPropertyName("device_ids")] public List<string>? DeviceIds { get; set; }
    }

    private sealed class DailyDto
    {
        [JsonPropertyName("days")] public List<ColituDailyUsage>? Days { get; set; }
    }

    private sealed class TypingDto
    {
        [JsonPropertyName("peer_typing")] public bool PeerTyping { get; set; }
    }
}
