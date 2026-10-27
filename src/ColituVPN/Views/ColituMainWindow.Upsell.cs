using v2rayN.Desktop.Services;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Premium encouragement for free-plan accounts on Home: one compact line normally, a stronger card when
/// the month's traffic runs low or is used up (connecting is refused then) and right after Premium ended.
/// The decisions are in <see cref="ColituUpsell"/>; every number comes from the panel. "Get Premium" opens
/// the plan page, which manages the subscription on app.colitu.com (nothing is bought in the Linux app).
/// </summary>
public partial class ColituMainWindow
{
    private ColituUpsellState _upsell = ColituUpsellState.None;

    private void WireUpsell()
    {
        UpsellCompactButton.Click += (_, _) => Navigate("plan");
        UpsellPremiumButton.Click += (_, _) => Navigate("plan");
        PlanNoticePremium.Click += (_, _) => Navigate("plan");
        UpsellLaterButton.Click += (_, _) =>
        {
            // "Not now" on the Premium-ended card: back to the compact line for good.
            _vpn.SetPremiumSeenUntil(null);
            ApplyUpsell();
        };
    }

    private void ApplyUpsell()
    {
        var subscription = _auth.CurrentSubscription;
        var now = DateTimeOffset.UtcNow;
        var seen = ColituUpsell.RememberPremium(subscription, _vpn.PremiumSeenUntil);
        if (seen != _vpn.PremiumSeenUntil)
        {
            try
            {
                _vpn.SetPremiumSeenUntil(seen);
            }
            catch (Exception ex)
            {
                Logging.SaveLog("ColituMainWindow.ApplyUpsell", ex);
            }
        }

        var state = _auth.HasSession
            ? ColituUpsell.Decide(subscription, _usage?.Stats.TotalUsedBytes, now, ColituUpsell.PremiumEndedAt(seen, now))
            : ColituUpsellState.None;
        var changed = state.Kind != _upsell.Kind;
        _upsell = state;
        RenderUpsell();
        if (changed)
        {
            // The panel's own usage notices repeat what the banner says; the home title follows a used-up allowance.
            ShowNextNotice();
            ApplyStatus();
        }
    }

    private void RenderUpsell()
    {
        var state = _upsell;
        var loc = Loc.I;
        if (state.Kind == ColituUpsellKind.None || state.LimitBytes <= 0)
        {
            UpsellBanner.IsVisible = false;
            return;
        }

        var limit = FormatBytes(state.LimitBytes);
        var devices = loc.Count("device", state.DeviceLimit);
        var strong = state.Kind != ColituUpsellKind.Free;
        UpsellCompact.IsVisible = !strong;
        UpsellStrong.IsVisible = strong;
        UpsellLaterButton.IsVisible = state.Kind == ColituUpsellKind.PremiumEnded;
        // The accent violet box when there is something to act on; the plain one for the compact line.
        UpsellBanner.Classes.Set("infoBox", strong);
        UpsellBanner.Classes.Set("neutralBox", !strong);

        switch (state.Kind)
        {
            case ColituUpsellKind.Free:
                UpsellCompactText.Text = loc.Format("upsell.free", ("left", FormatBytes(state.LeftBytes)), ("limit", limit), ("devices", devices));
                break;
            case ColituUpsellKind.Low:
                UpsellTitle.Text = loc.Format("upsell.low.title", ("percent", state.Percent));
                UpsellBody.Text = (state.RenewsAt is { } until
                    ? loc.Format("upsell.leftUntil", ("left", FormatBytes(state.LeftBytes)), ("date", FormatDate(until)))
                    : loc.Format("upsell.left", ("left", FormatBytes(state.LeftBytes)))) + " " + loc["upsell.unlimited"];
                break;
            case ColituUpsellKind.UsedUp:
                UpsellTitle.Text = loc["upsell.out.title"];
                UpsellBody.Text = UsedUpRenewText() + " " + loc["upsell.out.premium"];
                break;
            case ColituUpsellKind.PremiumEnded:
                UpsellTitle.Text = loc["upsell.ended.title"];
                UpsellBody.Text = loc.Format("upsell.ended.body", ("limit", limit), ("devices", devices));
                break;
        }
        UpsellBanner.IsVisible = true;
    }

    /// <summary>"Free traffic comes back on 1 November 2026." (or "at the start of next month" without a date).</summary>
    private string UsedUpRenewText() => _upsell.RenewsAt is { } renews
        ? Loc.I.Format("upsell.out.renews", ("date", FormatDate(renews)))
        : Loc.I["upsell.out.renewsSoon"];

    /// <summary>Connecting was refused (or would be) because the free allowance is used up: say when it returns and offer Premium.</summary>
    private void ShowQuotaUsedUp()
    {
        ShowToast($"{Loc.I["upsell.out.title"]}. {UsedUpRenewText()}", true);
        Navigate("plan");
    }
}
