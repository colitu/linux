namespace v2rayN.Desktop.Services;

/// <summary>One passport stamp: the country it stands for and its printed number.</summary>
public sealed record ColituStamp(string Code, int Number)
{
    public string NumberText => Number.ToString("00", CultureInfo.InvariantCulture);
}

/// <summary>Stamp material; every level is one 12-piece puzzle.</summary>
public enum StampLevel
{
    Bronze,
    Silver,
    Gold,
    Diamond
}

public static class StampLevels
{
    public static readonly StampLevel[] All = [StampLevel.Bronze, StampLevel.Silver, StampLevel.Gold, StampLevel.Diamond];

    public static string Key(this StampLevel level) => level switch
    {
        StampLevel.Bronze => "bronze",
        StampLevel.Silver => "silver",
        StampLevel.Gold => "gold",
        _ => "diamond"
    };

    public static StampLevel? FromKey(string? key) => All.Cast<StampLevel?>().FirstOrDefault(level => level!.Value.Key() == key);
}

/// <summary>What the device knows about one stamp. Unlocked once <see cref="UnlockedAt"/> is set.</summary>
public sealed record StampProgress(int Pieces, int Minutes, string? City, long UnlockedAt)
{
    public const int PiecesPerLevel = 12;

    public static readonly StampProgress None = new(0, 0, null, 0);

    public bool Unlocked => UnlockedAt > 0;

    public StampLevel? Level => Unlocked ? StampLevels.All[Math.Min(Pieces / PiecesPerLevel, StampLevels.All.Length - 1)] : null;

    /// <summary>Diamond with its puzzle full: nothing left to collect.</summary>
    public bool Mastered => Pieces >= PiecesPerLevel * StampLevels.All.Length;

    /// <summary>Filled pieces (0-12) of the current level's puzzle.</summary>
    public int LevelPieces => Mastered ? PiecesPerLevel : Pieces % PiecesPerLevel;

    public int ToNext => PiecesPerLevel - LevelPieces;

    /// <summary>Level code of the passport QR: 0 locked, 1-4 Bronze..Diamond, 5 Diamond complete.</summary>
    public int DuelLevel => !Unlocked ? 0 : Mastered ? 5 : (int)Level!.Value + 1;
}

/// <summary>A reward waiting for the app to show it (earned while the window was hidden too).</summary>
public abstract record StampEvent(string Code)
{
    public sealed record Unlocked(string Code) : StampEvent(Code);
    public sealed record Piece(string Code, int LevelPieces) : StampEvent(Code);
    public sealed record LevelUp(string Code, StampLevel Level) : StampEvent(Code);
    public sealed record Mastered(string Code) : StampEvent(Code);
    /// <summary>A hidden or visible achievement; <c>Code</c> is its id.</summary>
    public sealed record Achieved(string Code) : StampEvent(Code);
    /// <summary>A collection set completed; <c>Code</c> is its id.</summary>
    public sealed record Collected(string Code) : StampEvent(Code);
    /// <summary>A chapter opened; <c>Code</c> is its number ("2").</summary>
    public sealed record Chapter(string Code) : StampEvent(Code);
    /// <summary>A weekly expedition done; <c>Code</c> is the goal id.</summary>
    public sealed record Expedition(string Code) : StampEvent(Code);
    /// <summary>A special stamp outside the chapters (the Companion stamp).</summary>
    public sealed record Special(string Code) : StampEvent(Code);

    internal string Encode() => this switch
    {
        Unlocked => $"U|{Code}",
        Piece p => $"P|{Code}|{p.LevelPieces}",
        LevelUp l => $"L|{Code}|{l.Level.Key()}",
        Mastered => $"M|{Code}",
        Achieved => $"A|{Code}",
        Collected => $"C|{Code}",
        Chapter => $"H|{Code}",
        Expedition => $"X|{Code}",
        _ => $"S|{Code}"
    };

    internal static StampEvent? Decode(string text)
    {
        var p = text.Split('|');
        if (p.Length < 2 || p[1].Length == 0) return null;
        var code = p[1];
        return p[0] switch
        {
            "U" => new Unlocked(code),
            "P" => new Piece(code, p.Length > 2 && int.TryParse(p[2], out var n) ? n : 0),
            "L" => StampLevels.FromKey(p.Length > 2 ? p[2] : null) is { } level ? new LevelUp(code, level) : null,
            "M" => new Mastered(code),
            "A" => new Achieved(code),
            "C" => new Collected(code),
            "H" => new Chapter(code),
            "X" => new Expedition(code),
            "S" => new Special(code),
            _ => null
        };
    }
}

/// <summary>
/// Colitu Passport v2: the account's passport lives on the server (docs/passport-server.md), which
/// counts the minutes from the nodes' traffic reports and evaluates the same rules as before. The
/// app only shows the last server state (kept as an offline cache), plays the reveals for the
/// server's events and sends cosmetic choices. Device-local are only the view settings, the
/// friends list, the favourite stamp, the city shown under a stamp and the one-time import of the
/// v1 progress this device counted before.
/// </summary>
public sealed class ColituPassport
{
    /// <summary>The 10th and the 60th minute of the day add a piece (shown in the rules and goals).</summary>
    public static readonly int[] PieceMinutes = [10, 60];

    private static readonly Lazy<ColituPassport> Shared = new(() =>
        new ColituPassport(new ColituPassportStore(Utils.GetConfigPath("colitu-passport.json"))));

    public static ColituPassport Instance => Shared.Value;

    /// <summary>Chapter 01 — Origin.</summary>
    public static readonly IReadOnlyList<ColituStamp> Stamps =
        new[] { "SE", "FI", "DE", "NL", "GB", "PL", "LV", "FR", "US", "JP" }.Select((code, i) => new ColituStamp(code, i + 1)).ToList();

    /// <summary>Chapter 02 — Horizons; opens once Chapter 01 is complete. No Russia or Kazakhstan (owner decision).</summary>
    public static readonly IReadOnlyList<ColituStamp> Chapter2 =
        new[] { "NO", "EE", "LT", "CH", "CA", "TR", "SG", "HK", "BR", "AU" }.Select((code, i) => new ColituStamp(code, i + 11)).ToList();

    public static readonly IReadOnlyList<ColituStamp> AllStamps = Stamps.Concat(Chapter2).ToList();

    private readonly Func<DateTime> _clock;
    private PassportState _state;

    public ColituPassport(ColituPassportStore store, Func<DateTime>? clock = null)
    {
        Store = store;
        _clock = clock ?? (() => DateTime.Now);
        _state = PassportState.Parse(store.GetString("server_state")) ?? PassportState.Empty;
        Goals = new PassportGoals(this);
        Weekly = new PassportWeekly(this);
        Social = new PassportSocial(this);
    }

    internal ColituPassportStore Store { get; }
    public PassportGoals Goals { get; }
    public PassportWeekly Weekly { get; }
    public PassportSocial Social { get; }

    /// <summary>Raised (on any thread) after a new server state was applied.</summary>
    public event Action? Changed;

    internal DateTime Now => _clock();

    /// <summary>The last server state (the offline cache until the first answer of this session).</summary>
    public PassportState State => _state;

    /// <summary>Whether any server state was ever received on this device.</summary>
    public bool HasState => Store.GetString("server_state") != null;

    /// <summary>Takes a new server state and keeps it as the offline cache.</summary>
    public void Apply(PassportState state)
    {
        _state = state;
        Store.Set("server_state", state.ToJson());
        Changed?.Invoke();
    }

    /// <summary>Signed out: the next account must never see this one's passport (the v1 import flag stays: it is per device).</summary>
    public void ForgetAccount()
    {
        _state = PassportState.Empty;
        Store.Remove("server_state");
        Store.Remove("tz_sent");
        Changed?.Invoke();
    }

    public static ColituStamp? StampOf(string? code) => AllStamps.FirstOrDefault(stamp => stamp.Code == code);

    /// <summary>The stamp of a server's country code ("UK" counts as GB); null for countries without one.</summary>
    public static ColituStamp? StampFor(string? countryCode) => Normalize(countryCode) is { } code ? StampOf(code) : null;

    /// <summary>Upper-case ISO code, UK as GB; null when not two letters.</summary>
    public static string? Normalize(string? code)
    {
        var value = (code ?? "").Trim().ToUpperInvariant();
        if (value == "UK") value = "GB";
        return value.Length == 2 && value.All(char.IsAsciiLetterUpper) ? value : null;
    }

    public StampProgress ProgressOf(string code)
    {
        var dto = _state.Stamps.FirstOrDefault(s => Normalize(s.Country) == code);
        if (dto == null) return StampProgress.None with { City = Store.GetString($"city_{code}") };
        var pieces = Math.Clamp(dto.Pieces, 0, StampProgress.PiecesPerLevel * StampLevels.All.Length);
        var unlocked = dto.UnlockedAt?.ToUnixTimeMilliseconds() ?? 1;
        return new StampProgress(pieces, 0, Store.GetString($"city_{code}"), Math.Max(1, unlocked));
    }

    public bool UnlockedAnywhere(string code) => ProgressOf(code).Unlocked;

    /// <summary>Chapter 01 stamps collected (the passport's main count).</summary>
    public int Count => CountIn(Stamps);

    public bool Complete => Count == Stamps.Count;

    public int CountIn(IEnumerable<ColituStamp> chapter) => chapter.Count(stamp => UnlockedAnywhere(stamp.Code));

    /// <summary>Chapter 02 is open: the server says so (every Chapter 01 stamp collected).</summary>
    public bool Chapter2Open => _state.Chapter2Open || Stamps.All(stamp => UnlockedAnywhere(stamp.Code));

    /// <summary>Minutes counted today and pieces earned today (zero once the server's day is over here).</summary>
    public (int Minutes, int Pieces) Today
    {
        get
        {
            var today = _state.Today;
            if (today == null) return (0, 0);
            var local = Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return today.Day == null || today.Day == local ? (today.Minutes, today.Pieces) : (0, 0);
        }
    }

    /// <summary>Unlocked stamps (all chapters) with their progress.</summary>
    public IReadOnlyDictionary<string, StampProgress> Progress =>
        AllStamps.Select(stamp => (stamp.Code, Progress: ProgressOf(stamp.Code)))
            .Where(item => item.Progress.Unlocked)
            .ToDictionary(item => item.Code, item => item.Progress);

    /// <summary>No daily cap: the account sees the panel's alpha nodes (the server decides).</summary>
    public bool Tester => _state.Tester;

    /// <summary>The city shown under a stamp (the server only knows countries); kept on this device.</summary>
    public void RememberCity(string? countryCode, string? city)
    {
        if (Normalize(countryCode) is { } code && !string.IsNullOrWhiteSpace(city))
        {
            Store.Set($"city_{code}", city.Trim());
        }
    }

    // ── One-time import of the progress this device counted before (v1) ──

    /// <summary>The v1 progress of this device went to the account already (or there was none).</summary>
    public bool Imported
    {
        get => Store.GetBool("v2_imported");
        set => Store.Set("v2_imported", value);
    }

    /// <summary>Whether the v1 store of this device has anything worth importing.</summary>
    public bool HasLocalProgress => AllStamps.Any(stamp => Store.GetLong($"t_{stamp.Code}") > 0);

    /// <summary>The body of POST /client/passport/import from the v1 store (the server caps and merges it).</summary>
    public Dictionary<string, object?> ImportBody(string deviceId)
    {
        var stamps = AllStamps
            .Where(stamp => Store.GetLong($"t_{stamp.Code}") > 0)
            .Select(stamp => new Dictionary<string, object?>
            {
                ["country"] = stamp.Code,
                ["pieces"] = Math.Clamp(Store.GetInt($"p_{stamp.Code}"), 0, StampProgress.PiecesPerLevel * StampLevels.All.Length),
                ["unlocked_at"] = DateTimeOffset.FromUnixTimeMilliseconds(Store.GetLong($"t_{stamp.Code}")).ToString("o", CultureInfo.InvariantCulture)
            })
            .ToList();
        var achievements = PassportAchievement.All
            .Where(a => Store.GetLong($"a_{a.Id}") > 0 || Store.GetLong($"seen_a_{a.Id}") > 0)
            .Select(a => a.Id.Replace('_', '-'))
            .ToList();
        var collections = PassportCollection.All
            .Where(c => Store.GetLong($"seen_c_{c.Id}") > 0)
            .Select(c => c.Id.Replace('_', '-'))
            .ToList();
        return new Dictionary<string, object?>
        {
            ["device_id"] = deviceId,
            ["stamps"] = stamps,
            ["achievements"] = achievements,
            ["collections"] = collections,
            ["expeditions_done"] = Store.GetInt("expeditions_done"),
            ["companion"] = Store.GetLong($"special_{PassportSocial.Companion}") > 0,
            ["nickname"] = Store.GetString("nick"),
            ["title_id"] = Store.GetString("title")?.Replace('_', '-'),
            ["cover_id"] = Store.GetString("cover")
        };
    }
}
