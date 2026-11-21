namespace v2rayN.Desktop.Services;

/// <summary>
/// The connection method a user can pick on the home screen: automatic or ColituGecko. ColituGecko is
/// a connection of its own (Gecko only, independent of the server); every other method is chosen by
/// Adaptive Connect.
/// </summary>
public static class ColituTransportChoice
{
    /// <summary>ColituGecko: in the menu only while the panel offers it (<see cref="Menu"/>).</summary>
    public const string Gecko = ColituGeckoRules.Protocol;

    /// <summary>
    /// The menu, in order: automatic, then ColituGecko (when offered: enabled; offered but unavailable
    /// now: shown disabled; not offered or not known yet: left out).
    /// </summary>
    public static List<ColituMethodEntry> Menu(ColituGeckoOffer offer)
    {
        var menu = new List<ColituMethodEntry> { new("", true) };
        if (offer is ColituGeckoOffer.Offered or ColituGeckoOffer.Unavailable)
        {
            menu.Add(new ColituMethodEntry(Gecko, offer == ColituGeckoOffer.Offered));
        }
        return menu;
    }

    /// <summary>The pick the UI shows: ColituGecko picked while the panel does not offer it reads as automatic (the pick stays stored).</summary>
    public static string Shown(string? picked, ColituGeckoOffer offer) =>
        string.Equals(picked, Gecko, StringComparison.Ordinal) && offer == ColituGeckoOffer.NotOffered ? "" : picked ?? "";

    /// <summary>The stored pick: <see cref="Gecko"/> or "" (automatic); anything else is automatic.</summary>
    public static string Normalize(string? protocol) =>
        string.Equals(protocol?.Trim(), Gecko, StringComparison.OrdinalIgnoreCase) ? Gecko : "";
}

/// <summary>One entry of the protocol menu; a disabled one is shown greyed out (offered, unavailable now).</summary>
public sealed record ColituMethodEntry(string Method, bool Enabled);
