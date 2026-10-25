using System.Text.Json;
using System.Text.Json.Nodes;

namespace v2rayN.Desktop.Services;

/// <summary>Achievements: each gives a title. Secret ones show as "???" until earned. Titles stay in English (like a badge).</summary>
public sealed record PassportAchievement(string Id, string Title, bool Secret)
{
    public static readonly PassportAchievement First = new("first", "First Connection", false);
    public static readonly PassportAchievement Globetrotter = new("globe", "Globetrotter", false);
    public static readonly PassportAchievement Origin = new("origin", "Origin Collector", false);
    public static readonly PassportAchievement NordicMaster = new("nordic_master", "Nordic Master", false);
    public static readonly PassportAchievement Night = new("night", "Night Explorer", true);
    public static readonly PassportAchievement Early = new("early", "Early Bird", true);
    public static readonly PassportAchievement LongHaul = new("longhaul", "Long Haul", true);
    public static readonly PassportAchievement Diamond = new("diamond", "Diamond Collector", true);
    public static readonly PassportAchievement Pathfinder = new("pathfinder", "Pathfinder", false);
    public static readonly PassportAchievement LegacyGold = new("legacy_gold", "Golden Passport", false);
    public static readonly PassportAchievement LegacyDiamond = new("legacy_diamond", "Diamond Passport", false);

    public static readonly IReadOnlyList<PassportAchievement> All =
        [First, Globetrotter, Origin, NordicMaster, Night, Early, LongHaul, Diamond, Pathfinder, LegacyGold, LegacyDiamond];

    public static PassportAchievement? Of(string? id) => All.FirstOrDefault(a => a.Id == id);
}

/// <summary>Passport cover looks. Classic for everyone, Premium with the plan, the rest from sets.</summary>
public enum CoverTheme
{
    Classic,
    Premium,
    Nordic,
    Baltic,
    Atlantic,
    Pacific
}

public static class CoverThemes
{
    public static readonly CoverTheme[] All = Enum.GetValues<CoverTheme>();

    public static string Key(this CoverTheme theme) => theme.ToString().ToLowerInvariant();

    public static CoverTheme? FromKey(string? key) => All.Cast<CoverTheme?>().FirstOrDefault(t => t!.Value.Key() == key);
}

/// <summary>
/// Collection sets. Their countries may belong to a later chapter: those stamps are recorded
/// already, so a set can be finished before its chapter opens.
/// </summary>
public sealed record PassportCollection(string Id, string Title, IReadOnlyList<string> Codes, bool Secret, CoverTheme? Cover)
{
    public static readonly PassportCollection Nordic = new("nordic", "Nordic", ["SE", "FI", "NO"], false, CoverTheme.Nordic);
    public static readonly PassportCollection Baltic = new("baltic", "Baltic", ["LV", "EE", "LT"], false, CoverTheme.Baltic);
    public static readonly PassportCollection Atlantic = new("atlantic", "Across the Atlantic", ["US", "GB", "CA"], false, CoverTheme.Atlantic);
    public static readonly PassportCollection Heart = new("heart", "Heart of Europe", ["DE", "FR", "NL", "PL", "CH"], false, null);
    public static readonly PassportCollection Pacific = new("pacific", "Pacific Rim", ["JP", "SG", "HK", "AU"], false, CoverTheme.Pacific);
    public static readonly PassportCollection GoldRush = new("gold_rush", "Gold Rush", [], true, null);

    public static readonly IReadOnlyList<PassportCollection> All = [Nordic, Baltic, Atlantic, Heart, Pacific, GoldRush];

    public static PassportCollection? Of(string? id) => All.FirstOrDefault(c => c.Id == id);
}

/// <summary>
/// Achievements, collections and the passport identity as the server reports them. The favourite
/// stamp stays on this device (a view choice); name, title and cover are saved to the account.
/// </summary>
public sealed class PassportGoals(ColituPassport passport)
{
    public const int GoldRushCount = 3;
    public const int DiamondCount = 3;
    public const int NicknameMax = 24;

    private ColituPassportStore Store => passport.Store;
    private PassportState State => passport.State;

    public string Nickname => State.Nickname ?? "";
    public string? Title => string.IsNullOrWhiteSpace(State.TitleId) ? null : PassportIds.Normalize(State.TitleId);
    public CoverTheme? Cover => CoverThemes.FromKey(PassportIds.Normalize(State.CoverId));
    public string? Favorite => Store.GetString("favorite");

    /// <summary>Six digits the server gave this account ("000000" until the first answer).</summary>
    public string CollectorNo => string.IsNullOrWhiteSpace(State.CollectorNo) ? "000000" : State.CollectorNo!;

    public void UpdateFavorite(string? code) => Store.Set("favorite", code);

    /// <summary>Stamps of <paramref name="chapter"/> at <paramref name="level"/> or higher (Chapter 03 — Legacy).</summary>
    public int Legacy(StampLevel level, IEnumerable<ColituStamp> chapter) =>
        chapter.Count(s => passport.ProgressOf(s.Code).Level is { } l && l >= level);

    /// <summary>Chapter 03 opens with Chapter 02 complete.</summary>
    public bool Chapter3Open => passport.CountIn(ColituPassport.Chapter2) == ColituPassport.Chapter2.Count;

    private int CountAtLeast(StampLevel level) => ColituPassport.AllStamps.Count(s => passport.ProgressOf(s.Code).Level is { } l && l >= level);

    public bool Achieved(PassportAchievement a) => State.Achievements.Any(e => PassportIds.Normalize(e.Id) == a.Id);

    /// <summary>Stamps of the set already collected, and how many it needs (for the list; the server decides completion).</summary>
    public (int Have, int Need) Progress(PassportCollection c)
    {
        var shown = c == PassportCollection.GoldRush
            ? (Math.Min(CountAtLeast(StampLevel.Gold), GoldRushCount), GoldRushCount)
            : (c.Codes.Count(passport.UnlockedAnywhere), c.Codes.Count);
        return Completed(c) ? (shown.Item2, shown.Item2) : shown;
    }

    public bool Completed(PassportCollection c) => State.Collections.Any(e => PassportIds.Normalize(e.Id) == c.Id);

    /// <summary>When the set ("c_nordic") or achievement ("a_first") was earned (0 when not yet).</summary>
    public long EarnedAt(string key)
    {
        var list = key.StartsWith("c_", StringComparison.Ordinal) ? State.Collections : State.Achievements;
        var id = key.Length > 2 ? key[2..] : key;
        return list.FirstOrDefault(e => PassportIds.Normalize(e.Id) == id)?.EarnedAt?.ToUnixTimeMilliseconds() ?? 0;
    }

    /// <summary>Covers this account may pick (the server's list; Classic always).</summary>
    public IReadOnlyList<CoverTheme> Covers(bool premium)
    {
        var list = State.Covers.Select(k => CoverThemes.FromKey(PassportIds.Normalize(k))).OfType<CoverTheme>().ToList();
        if (!list.Contains(CoverTheme.Classic)) list.Insert(0, CoverTheme.Classic);
        if ((premium || State.Premium) && !list.Contains(CoverTheme.Premium)) list.Insert(1, CoverTheme.Premium);
        return list;
    }

    /// <summary>The cover shown: the chosen one while still allowed, else Premium or Classic.</summary>
    public CoverTheme ActiveCover(bool premium)
    {
        var allowed = Covers(premium);
        return Cover is { } chosen && allowed.Contains(chosen) ? chosen : premium || State.Premium ? CoverTheme.Premium : CoverTheme.Classic;
    }

    public string? TitleOf(string? id) => PassportAchievement.Of(id) is { } a && Achieved(a) ? a.Title : null;
}

/// <summary>One kind of weekly expedition and how many steps it takes.</summary>
public sealed record ExpeditionGoal(string Id, int Target)
{
    public static readonly ExpeditionGoal NewCountry = new("new_country", 1);
    public static readonly ExpeditionGoal Share = new("share", 1);
    public static readonly ExpeditionGoal Customize = new("customize", 1);
    public static readonly ExpeditionGoal Days10 = new("days10", 3);
    public static readonly ExpeditionGoal SetStamp = new("set_stamp", 1);
    public static readonly ExpeditionGoal Pieces = new("pieces", 4);

    public static readonly IReadOnlyList<ExpeditionGoal> All = [NewCountry, Share, Customize, Days10, SetStamp, Pieces];

    public static ExpeditionGoal? Of(string? id) => All.FirstOrDefault(g => g.Id == id);
}

/// <summary>
/// Weekly expeditions as the server counts them: three goals per ISO week, the same rotation as
/// the phones. The app only reports shares (cosmetic). Missing a week costs nothing.
/// </summary>
public sealed class PassportWeekly(ColituPassport passport)
{
    public const int PathfinderAt = 5;

    public static string WeekKey(DateTime date) =>
        $"{ISOWeek.GetYear(date)}-W{ISOWeek.GetWeekOfYear(date)}";

    /// <summary>The server's week ("2026-W43"), else this week's key.</summary>
    public string WeekKey() => passport.State.Weekly?.Week ?? WeekKey(passport.Now);

    /// <summary>The three goals of the week of <paramref name="date"/> (the rotation the server uses too).</summary>
    public static IReadOnlyList<ExpeditionGoal> GoalsOf(DateTime date)
    {
        var week = ISOWeek.GetYear(date) * 53 + ISOWeek.GetWeekOfYear(date);
        var all = ExpeditionGoal.All;
        return Enumerable.Range(0, 3).Select(i => all[(week * 3 + i) % all.Count]).ToList();
    }

    private PassportWeekGoal? Dto(ExpeditionGoal goal) => passport.State.Weekly?.Goals.FirstOrDefault(g => PassportIds.Normalize(g.Id) == goal.Id);

    /// <summary>This week's goals from the server; the local rotation until it answered.</summary>
    public IReadOnlyList<ExpeditionGoal> Goals()
    {
        var server = passport.State.Weekly?.Goals.Select(g => ExpeditionGoal.Of(PassportIds.Normalize(g.Id))).OfType<ExpeditionGoal>().ToList();
        return server is { Count: > 0 } ? server : GoalsOf(passport.Now);
    }

    public int Progress(ExpeditionGoal goal) => Dto(goal)?.Progress ?? 0;

    public int Target(ExpeditionGoal goal) => Dto(goal)?.Target is > 0 and var t ? t : goal.Target;

    public bool Done(ExpeditionGoal goal) => Dto(goal) is { } d && (d.Done || d.Progress >= Target(goal));

    /// <summary>Expeditions finished over all weeks.</summary>
    public int Total => passport.State.ExpeditionsDone;
}

/// <summary>A friend's passport as read from their link (no account data, only what their share card shows).</summary>
public sealed record PassportFriend(string Nick, string Collector, string? Title, string Levels, long AddedAt)
{
    public int Level(string code)
    {
        var index = ColituPassport.AllStamps.ToList().FindIndex(s => s.Code == code);
        return index >= 0 && index < Levels.Length && char.IsAsciiDigit(Levels[index]) ? Levels[index] - '0' : 0;
    }

    public int CountIn(IEnumerable<ColituStamp> chapter) => chapter.Count(s => Level(s.Code) > 0);

    /// <summary>Sum of levels: the tie-breaker of the friends ranking.</summary>
    public int Score => Levels.Where(char.IsAsciiDigit).Sum(c => c - '0');
}

/// <summary>What a passport QR / link carries: no account data, only what the share card shows.</summary>
public sealed record DuelPassport(string Nick, string Collector, string? Title, IReadOnlyDictionary<string, int> Levels)
{
    public int CountIn(IEnumerable<ColituStamp> chapter) => chapter.Count(s => Levels.GetValueOrDefault(s.Code) > 0);

    public PassportFriend ToFriend(long addedAt) =>
        new(Nick, Collector, Title, string.Concat(ColituPassport.AllStamps.Select(s => Levels.GetValueOrDefault(s.Code).ToString(CultureInfo.InvariantCulture))), addedAt);

    public static DuelPassport From(PassportFriend f) =>
        new(f.Nick, f.Collector, f.Title, ColituPassport.AllStamps.ToDictionary(s => s.Code, s => f.Level(s.Code)));
}

/// <summary>
/// Passport Duel link: https://colitu.com/p#&lt;base64url&gt; of "1|nick|collector|title|levels",
/// the same format as the phones. The part after # never reaches a server.
/// </summary>
public static class PassportDuel
{
    public const string Prefix = "https://colitu.com/p#";

    public static string Link(string nick, string collector, string? title, string levels)
    {
        var raw = string.Join("|", "1", nick.Replace("|", ""), collector, title ?? "", levels);
        return Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static DuelPassport? Parse(string? text)
    {
        try
        {
            var value = (text ?? "").Trim();
            if (!value.StartsWith(Prefix, StringComparison.Ordinal)) return null;
            var data = value[Prefix.Length..].Replace('-', '+').Replace('_', '/');
            data = data.PadRight(data.Length + (4 - data.Length % 4) % 4, '=');
            var p = Encoding.UTF8.GetString(Convert.FromBase64String(data)).Split('|');
            if (p.Length < 5 || p[0] != "1") return null;
            var levels = ColituPassport.AllStamps.Select((s, i) => (s.Code, Level: i < p[4].Length && char.IsAsciiDigit(p[4][i]) ? p[4][i] - '0' : 0))
                .ToDictionary(x => x.Code, x => x.Level);
            return new DuelPassport(p[1], p[2], p[3].Length == 0 ? null : p[3], levels);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>Friends, the Companion stamp and the passport's view settings. Friends and settings stay on this device.</summary>
public sealed class PassportSocial(ColituPassport passport)
{
    public const string Companion = "companion";

    private ColituPassportStore Store => passport.Store;

    public bool ShowRanking { get => Store.GetBool("friends_ranking", true); set => Store.Set("friends_ranking", value); }
    public bool Sound { get => Store.GetBool("sound", true); set => Store.Set("sound", value); }

    /// <summary>Whether the first-visit intro has been shown on this device.</summary>
    public bool IntroSeen { get => Store.GetBool("intro_seen"); set => Store.Set("intro_seen", value); }

    /// <summary>My passport as a duel link (the server state this app shows).</summary>
    public string MyLink()
    {
        var levels = string.Concat(ColituPassport.AllStamps.Select(s => passport.ProgressOf(s.Code).DuelLevel.ToString(CultureInfo.InvariantCulture)));
        return PassportDuel.Link(passport.Goals.Nickname, passport.Goals.CollectorNo, passport.Goals.Title, levels);
    }

    public IReadOnlyList<PassportFriend> Friends => LoadFriends();

    /// <summary>Adds or refreshes a friend (one per collector number); never yourself.</summary>
    public void AddFriend(PassportFriend friend)
    {
        if (friend.Collector == passport.Goals.CollectorNo) return;
        SaveFriends(LoadFriends().Where(f => f.Collector != friend.Collector).Append(friend).OrderByDescending(f => f.AddedAt).ToList());
    }

    public void RemoveFriend(string collector) => SaveFriends(LoadFriends().Where(f => f.Collector != collector).ToList());

    private void SaveFriends(List<PassportFriend> friends)
    {
        var array = new JsonArray();
        foreach (var f in friends)
        {
            var o = new JsonObject { ["nick"] = f.Nick, ["collector"] = f.Collector, ["levels"] = f.Levels, ["added"] = f.AddedAt };
            if (f.Title != null) o["title"] = f.Title;
            array.Add(o);
        }
        Store.Set("friends", array.ToJsonString());
    }

    private List<PassportFriend> LoadFriends()
    {
        try
        {
            if (Store.GetString("friends") is not { } raw || JsonNode.Parse(raw) is not JsonArray array) return [];
            return array.OfType<JsonObject>()
                .Where(o => o["collector"] != null)
                .Select(o => new PassportFriend(
                    o["nick"]?.GetValue<string>() ?? "",
                    o["collector"]!.GetValue<string>(),
                    o["title"]?.GetValue<string>(),
                    o["levels"]?.GetValue<string>() ?? "",
                    o["added"]?.GetValue<long>() ?? 0))
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return [];
        }
    }

    /// <summary>The Companion stamp (granted by the server from the referral data).</summary>
    public bool HasCompanion => passport.State.Companion;
}
