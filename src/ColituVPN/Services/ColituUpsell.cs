using System.Globalization;

namespace v2rayN.Desktop.Services;

/// <summary>What the home screen says about Premium to a free-plan account.</summary>
public enum ColituUpsellKind
{
    /// <summary>Paid or gifted Premium, no plan at all, or not signed in: nothing to say.</summary>
    None,
    /// <summary>Free plan with traffic left: one compact line.</summary>
    Free,
    /// <summary>Free plan, at least 80 % of the month's traffic used.</summary>
    Low,
    /// <summary>Free plan, the month's traffic is used up: connecting is refused until it renews.</summary>
    UsedUp,
    /// <summary>Back on the free plan after Premium (bought, gifted or from an invite) ended within the last week.</summary>
    PremiumEnded
}

/// <summary>The decision and the numbers the home banner shows.</summary>
public sealed record ColituUpsellState(ColituUpsellKind Kind, long UsedBytes, long LimitBytes, DateTimeOffset? RenewsAt, int DeviceLimit)
{
    public static readonly ColituUpsellState None = new(ColituUpsellKind.None, 0, 0, null, 0);

    public long LeftBytes => Math.Max(0, LimitBytes - UsedBytes);

    /// <summary>Whole percent used (rounded down, so 79.9 % is never shown as 80 %).</summary>
    public int Percent => LimitBytes > 0 ? (int)Math.Clamp(Math.Floor(UsedBytes * 100.0 / LimitBytes), 0, 100) : 0;

    /// <summary>The panel refuses to connect a free account whose traffic is used up.</summary>
    public bool BlocksConnect => Kind == ColituUpsellKind.UsedUp;
}

/// <summary>
/// Pure decisions for the Premium encouragement (free plan: 1 GB a month, 1 device; numbers always come
/// from the panel's entitlement, never from here).
/// </summary>
public static class ColituUpsell
{
    /// <summary>From this share of the month's traffic on, the banner turns into the stronger "running low" state.</summary>
    public const double LowShare = 0.8;

    /// <summary>The "your Premium is over" banner shows for this long after the end (or until closed).</summary>
    public static readonly TimeSpan EndedNoticeFor = TimeSpan.FromDays(7);

    public static bool IsFreePlan(ColituSubscription? subscription) =>
        string.Equals(subscription?.PlanName, "Free", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Which banner the home screen shows. <paramref name="usedBytes"/> is the freshest usage the app has
    /// (the stats endpoint), else the entitlement's. A period that already ended counts as renewed.
    /// </summary>
    public static ColituUpsellState Decide(ColituSubscription? subscription, long? usedBytes, DateTimeOffset now, DateTimeOffset? premiumEndedAt = null)
    {
        if (subscription == null || !IsFreePlan(subscription))
        {
            return ColituUpsellState.None;
        }
        var quotaStatus = string.Equals(subscription.Status, "quota_exceeded", StringComparison.OrdinalIgnoreCase);
        if (!subscription.Active && !quotaStatus)
        {
            return ColituUpsellState.None;
        }

        var limit = Math.Max(0, subscription.TrafficLimitBytes ?? 0);
        var used = Math.Max(0, usedBytes ?? subscription.TrafficUsedBytes);
        var renews = ParseTime(subscription.TrafficPeriodEnd);
        var stale = renews is { } end && end <= now;
        if (stale)
        {
            // The month turned over since the numbers were read: the allowance is back.
            used = 0;
            renews = null;
        }
        var devices = Math.Max(1, subscription.DeviceLimit);

        if (!stale && (quotaStatus || (limit > 0 && (used >= limit || subscription.TrafficRemainingBytes == 0))))
        {
            return new ColituUpsellState(ColituUpsellKind.UsedUp, limit > 0 ? Math.Max(used, limit) : used, limit, renews, devices);
        }
        if (limit > 0 && used >= limit * LowShare)
        {
            return new ColituUpsellState(ColituUpsellKind.Low, used, limit, renews, devices);
        }
        if (premiumEndedAt is { } ended && ended <= now && now - ended < EndedNoticeFor)
        {
            return new ColituUpsellState(ColituUpsellKind.PremiumEnded, used, limit, renews, devices);
        }
        return new ColituUpsellState(ColituUpsellKind.Free, used, limit, renews, devices);
    }

    /// <summary>
    /// The end of the last Premium this install saw (saved in the preferences): the expiry of an active
    /// non-free plan; unchanged while on the free plan or without a plan.
    /// </summary>
    public static string? RememberPremium(ColituSubscription? subscription, string? saved)
    {
        if (subscription is { Active: true } && !IsFreePlan(subscription) && !string.IsNullOrWhiteSpace(subscription.ExpiresAt))
        {
            return subscription.ExpiresAt;
        }
        return saved;
    }

    /// <summary>When the remembered Premium ended: its expiry, or now when it was cut short.</summary>
    public static DateTimeOffset? PremiumEndedAt(string? saved, DateTimeOffset now) =>
        ParseTime(saved) is { } end ? (end < now ? end : now) : null;

    /// <summary>The panel's own usage notices (usage_80, usage_100) say what the banner already says.</summary>
    public static bool CoversNotice(ColituUpsellState state, ColituNotice notice) =>
        state.Kind is ColituUpsellKind.Low or ColituUpsellKind.UsedUp
        && (notice.Kind ?? notice.Id).StartsWith("usage_", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="ex"/> or an inner exception is the panel's QUOTA_EXCEEDED answer.</summary>
    public static bool IsQuotaError(Exception? ex)
    {
        for (; ex != null; ex = ex.InnerException)
        {
            if (ex is ColituApiException { ErrorCode: "QUOTA_EXCEEDED" })
            {
                return true;
            }
        }
        return false;
    }

    private static DateTimeOffset? ParseTime(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
}
