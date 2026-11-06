namespace v2rayN.Desktop.Services;

/// <summary>
/// After the computer wakes up the tunnel is checked, but only once the network is really back:
/// a slow Wi-Fi that needs 15 s to join must not look like a dead tunnel. Pure rules, no clock of its own.
/// </summary>
internal static class ColituResumeWait
{
    /// <summary>Never checked sooner than this after the wake-up (the adapters report their old state first).</summary>
    internal static readonly TimeSpan MinWait = TimeSpan.FromSeconds(4);
    /// <summary>The network (adapter and default route) must have been up this long before the check.</summary>
    internal static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);
    /// <summary>Checked anyway after this; the recovery itself waits for a missing network.</summary>
    internal static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <param name="sinceWake">Time since the wake-up.</param>
    /// <param name="networkUpFor">How long the physical network and a default route have been present without a break; null while one is missing.</param>
    /// <returns>True when the tunnel check should run now.</returns>
    internal static bool ShouldCheck(TimeSpan sinceWake, TimeSpan? networkUpFor)
    {
        if (sinceWake >= MaxWait)
        {
            return true;
        }
        return sinceWake >= MinWait && networkUpFor is { } up && up >= Settle;
    }
}
