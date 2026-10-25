using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using AwesomeAssertions;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

/// <summary>
/// Colitu Passport v2: the server keeps the passport (docs/passport-server.md); the app shows its
/// state, plays its events, acks them, saves cosmetic choices and imports this device's v1 progress once.
/// </summary>
public class ColituPassportTests
{
    private DateTime _now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Local);

    private const string ContractState = """
        {"data":{
          "collector_no":"992562","nickname":"Ali","title_id":"nordic-master","cover_id":"nordic",
          "tester":false,"premium":true,"chapter2_open":false,"companion":true,
          "today":{"day":"2026-10-25","minutes":34,"pieces":1,"next_piece_at_minute":60},
          "run":{"country":"SE","minutes":3},
          "stamps":[{"country":"SE","unlocked_at":"2026-10-21T10:00:00Z","pieces":13,"level":"silver","mastered":false},
                    {"country":"FI","unlocked_at":"2026-10-22T10:00:00Z","pieces":48,"level":"diamond","mastered":true},
                    {"country":"RU","unlocked_at":"2026-10-22T10:00:00Z","pieces":3}],
          "achievements":[{"id":"first","earned_at":"2026-10-21T10:05:00Z"},{"id":"nordic-master","earned_at":"2026-10-24T10:05:00Z"}],
          "collections":[{"id":"gold-rush","earned_at":"2026-10-24T11:00:00Z"}],
          "covers":["classic","premium","nordic"],
          "weekly":{"week":"2026-W43","goals":[{"id":"days10","target":3,"progress":1,"done":false},
                                              {"id":"set_stamp","target":1,"progress":1,"done":true},
                                              {"id":"pieces","target":4,"progress":2,"done":false}]},
          "expeditions_done":2,"unseen_events":0,"updated_at":"2026-10-25T09:00:00Z"}}
        """;

    private ColituPassport NewPassport(ColituPassportStore? store = null) => new(store ?? new ColituPassportStore(null), () => _now);

    private ColituPassport WithState(string json = ContractState)
    {
        var passport = NewPassport();
        passport.Apply(PassportState.Parse(json)!);
        return passport;
    }

    /// <summary>Answers by method and path; records every call with its JSON body.</summary>
    private sealed class FakeTransport : IPassportTransport
    {
        public readonly Dictionary<string, PassportResponse> Answers = [];
        public readonly List<(string Method, string Path, string? Body)> Calls = [];

        public Task<PassportResponse> SendAsync(HttpMethod method, string path, object? body, CancellationToken token = default)
        {
            Calls.Add((method.Method, path, body == null ? null : JsonSerializer.Serialize(body)));
            return Task.FromResult(Answers.TryGetValue($"{method.Method} {path}", out var answer) ? answer : new PassportResponse(404, """{"error":{"code":"NOT_FOUND"}}"""));
        }
    }

    private ColituPassportSync NewSync(ColituPassport passport, FakeTransport transport, string? device = "device-1", int offsetMinutes = 180) =>
        new(passport, transport, () => device, () => TimeSpan.FromMinutes(offsetMinutes));

    [Fact]
    public void TheServerState_IsWhatThePassportShows()
    {
        var passport = WithState();

        passport.Count.Should().Be(2);
        passport.ProgressOf("SE").Level.Should().Be(StampLevel.Silver);
        passport.ProgressOf("SE").LevelPieces.Should().Be(1);
        passport.ProgressOf("FI").Mastered.Should().BeTrue();
        passport.ProgressOf("DE").Unlocked.Should().BeFalse();
        passport.Progress.Keys.Should().BeEquivalentTo("SE", "FI"); // RU has no stamp
        passport.Today.Should().Be((34, 1));
        passport.Goals.CollectorNo.Should().Be("992562");
        passport.Goals.Nickname.Should().Be("Ali");
        passport.Social.HasCompanion.Should().BeTrue();
        passport.Weekly.Total.Should().Be(2);
    }

    [Fact]
    public void ServerIds_WithHyphens_MatchTheAppsIds()
    {
        var passport = WithState();
        passport.Goals.Achieved(PassportAchievement.NordicMaster).Should().BeTrue();
        passport.Goals.Achieved(PassportAchievement.Night).Should().BeFalse();
        passport.Goals.Title.Should().Be("nordic_master");
        passport.Goals.TitleOf(passport.Goals.Title).Should().Be("Nordic Master");
        passport.Goals.Completed(PassportCollection.GoldRush).Should().BeTrue();
        passport.Goals.Progress(PassportCollection.GoldRush).Should().Be((3, 3));
        passport.Goals.Completed(PassportCollection.Nordic).Should().BeFalse();
        passport.Goals.Progress(PassportCollection.Nordic).Should().Be((2, 3));
        passport.Goals.EarnedAt("c_gold_rush").Should().BeGreaterThan(0);
    }

    [Fact]
    public void Covers_ComeFromTheServer_AndTheChosenOneIsShown()
    {
        var passport = WithState();
        passport.Goals.Covers(premium: false).Should().Equal(CoverTheme.Classic, CoverTheme.Premium, CoverTheme.Nordic);
        passport.Goals.ActiveCover(premium: false).Should().Be(CoverTheme.Nordic);

        var empty = NewPassport();
        empty.Goals.Covers(premium: false).Should().Equal(CoverTheme.Classic);
        empty.Goals.ActiveCover(premium: true).Should().Be(CoverTheme.Premium);
        empty.Goals.CollectorNo.Should().Be("000000");
    }

    [Fact]
    public void Weekly_UsesTheServersGoals_OrTheRotationBeforeTheFirstAnswer()
    {
        var passport = WithState();
        passport.Weekly.Goals().Should().Equal(ExpeditionGoal.Days10, ExpeditionGoal.SetStamp, ExpeditionGoal.Pieces);
        passport.Weekly.Progress(ExpeditionGoal.Days10).Should().Be(1);
        passport.Weekly.Done(ExpeditionGoal.SetStamp).Should().BeTrue();
        passport.Weekly.Done(ExpeditionGoal.Pieces).Should().BeFalse();
        passport.Weekly.WeekKey().Should().Be("2026-W43");

        var empty = NewPassport();
        empty.Weekly.Goals().Should().Equal(PassportWeekly.GoalsOf(_now));
        PassportWeekly.GoalsOf(new DateTime(2026, 10, 26)).Should().Equal(ExpeditionGoal.NewCountry, ExpeditionGoal.Share, ExpeditionGoal.Customize);
    }

    [Fact]
    public void Today_IsZero_OnceTheServersDayIsOverHere()
    {
        var passport = WithState();
        _now = _now.AddDays(1);
        passport.Today.Should().Be((0, 0));
    }

    [Fact]
    public void TheLastState_StaysAsOfflineCache()
    {
        var path = Path.Combine(Path.GetTempPath(), $"colitu-passport-test-{Guid.NewGuid():N}.json");
        try
        {
            var passport = NewPassport(new ColituPassportStore(path));
            passport.HasState.Should().BeFalse();
            passport.Apply(PassportState.Parse(ContractState)!);

            var again = NewPassport(new ColituPassportStore(path));
            again.HasState.Should().BeTrue();
            again.Count.Should().Be(2);
            again.Goals.Nickname.Should().Be("Ali");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SigningOut_ForgetsTheAccountsPassport_ButNotTheImportFlag()
    {
        var passport = WithState();
        passport.Imported = true;
        passport.ForgetAccount();
        passport.Count.Should().Be(0);
        passport.HasState.Should().BeFalse();
        passport.Goals.Nickname.Should().BeEmpty();
        passport.Imported.Should().BeTrue();
    }

    [Fact]
    public void Events_BecomeTheReveals_OldestFirst()
    {
        var passport = WithState();
        const string json = """
            {"data":[
              {"id":15,"kind":"companion","payload":{}},
              {"id":12,"kind":"stamp_unlocked","payload":{"country":"de"}},
              {"id":13,"kind":"piece","payload":{"country":"SE","pieces":24}},
              {"id":14,"kind":"level_up","payload":{"country":"SE","level":"gold"}},
              {"id":16,"kind":"mastered","payload":{"country":"FI"}},
              {"id":17,"kind":"achievement","payload":{"id":"legacy-gold"}},
              {"id":18,"kind":"collection","payload":{"id":"gold-rush"}},
              {"id":19,"kind":"chapter_opened","payload":{"chapter":2}},
              {"id":20,"kind":"weekly_done","payload":{"goal_id":"new_country"}},
              {"id":21,"kind":"something_new","payload":{}},
              {"id":22,"kind":"piece","payload":{"country":"SE"}}
            ]}
            """;

        var events = PassportIds.ParseEvents(json, passport.ProgressOf);

        events.Select(e => e.Id).Should().BeInAscendingOrder();
        events.Select(e => e.Event).Should().Equal(
            new StampEvent.Unlocked("DE"),
            new StampEvent.Piece("SE", 12),
            new StampEvent.LevelUp("SE", StampLevel.Gold),
            new StampEvent.Special(PassportSocial.Companion),
            new StampEvent.Mastered("FI"),
            new StampEvent.Achieved("legacy_gold"),
            new StampEvent.Collected("gold_rush"),
            new StampEvent.Chapter("2"),
            new StampEvent.Expedition("new_country"),
            null,
            new StampEvent.Piece("SE", 1));
    }

    [Fact]
    public async Task Refresh_AppliesTheState_AndFetchesUnseenEventsOnlyWhenThereAreSome()
    {
        var passport = NewPassport();
        var transport = new FakeTransport();
        transport.Answers["GET /client/passport"] = new(200, ContractState);
        var sync = NewSync(passport, transport);

        var events = await sync.RefreshAsync();

        events.Should().BeEmpty();
        passport.Count.Should().Be(2);
        transport.Calls.Should().NotContain(c => c.Path.Contains("/events"));

        transport.Answers["GET /client/passport"] = new(200, ContractState.Replace("\"unseen_events\":0", "\"unseen_events\":1"));
        transport.Answers["GET /client/passport/events?unseen=1"] = new(200, """{"data":[{"id":7,"kind":"stamp_unlocked","payload":{"country":"NL"}}]}""");
        events = await sync.RefreshAsync();

        events.Should().ContainSingle().Which.Should().Be(new PassportServerEvent(7, new StampEvent.Unlocked("NL")));
    }

    [Fact]
    public async Task Offline_OrBeforeTheServerOffersIt_TheCachedStateStays()
    {
        var passport = WithState();
        var transport = new FakeTransport();
        transport.Answers["GET /client/passport"] = new(0, "");
        (await NewSync(passport, transport).RefreshAsync()).Should().BeNull();
        transport.Answers.Remove("GET /client/passport"); // 404: not live yet
        (await NewSync(passport, transport).RefreshAsync()).Should().BeNull();
        passport.Count.Should().Be(2);
    }

    [Fact]
    public async Task Ack_SendsTheLastShownId()
    {
        var transport = new FakeTransport();
        transport.Answers["POST /client/passport/events/ack"] = new(204, "");
        (await NewSync(NewPassport(), transport).AckAsync(12)).Should().BeTrue();
        transport.Calls.Single().Body.Should().Be("""{"through_id":12}""");
    }

    private static void WriteV1Progress(ColituPassportStore store)
    {
        store.Set("t_SE", 1761000000000L);
        store.Set("p_SE", 13);
        store.Set("t_NO", 1761000000000L);
        store.Set("a_night", 1761000000000L);
        store.Set("seen_c_nordic", 1761000000000L);
        store.Set("expeditions_done", 2);
        store.Set("nick", "Ali");
        store.Set("title", "nordic_master");
    }

    [Fact]
    public async Task V1Progress_IsImportedOncePerDevice()
    {
        var store = new ColituPassportStore(null);
        WriteV1Progress(store);
        var passport = NewPassport(store);
        var transport = new FakeTransport();
        transport.Answers["POST /client/passport/import"] = new(200, ContractState);
        transport.Answers["GET /client/passport"] = new(200, ContractState);
        var sync = NewSync(passport, transport);

        await sync.RefreshAsync();
        await sync.RefreshAsync();

        transport.Calls.Count(c => c.Path == "/client/passport/import").Should().Be(1);
        passport.Imported.Should().BeTrue();
        using var body = JsonDocument.Parse(transport.Calls.First(c => c.Path == "/client/passport/import").Body!);
        var root = body.RootElement;
        root.GetProperty("device_id").GetString().Should().Be("device-1");
        root.GetProperty("stamps").EnumerateArray().Select(s => s.GetProperty("country").GetString()).Should().Equal("SE", "NO");
        root.GetProperty("stamps")[0].GetProperty("pieces").GetInt32().Should().Be(13);
        root.GetProperty("achievements").EnumerateArray().Select(a => a.GetString()).Should().Equal("night");
        root.GetProperty("collections").EnumerateArray().Select(a => a.GetString()).Should().Equal("nordic");
        root.GetProperty("expeditions_done").GetInt32().Should().Be(2);
        root.GetProperty("title_id").GetString().Should().Be("nordic-master");
    }

    [Theory]
    [InlineData(409, true)]
    [InlineData(500, false)]
    [InlineData(0, false)]
    public async Task Import_IsMarkedDone_OnSuccessOr409Only(int status, bool done)
    {
        var store = new ColituPassportStore(null);
        WriteV1Progress(store);
        var passport = NewPassport(store);
        var transport = new FakeTransport();
        transport.Answers["POST /client/passport/import"] = new(status, """{"error":{"code":"PASSPORT_ALREADY_IMPORTED"}}""");
        await NewSync(passport, transport).ImportIfNeededAsync();
        passport.Imported.Should().Be(done);
    }

    [Fact]
    public async Task ADeviceWithoutV1Progress_DoesNotImport()
    {
        var passport = NewPassport();
        var transport = new FakeTransport();
        await NewSync(passport, transport).ImportIfNeededAsync();
        passport.Imported.Should().BeTrue();
        transport.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Profile_GoesToTheAccount_AndItsAnswerIsShown()
    {
        var passport = NewPassport();
        var transport = new FakeTransport();
        transport.Answers["PUT /client/passport/profile"] = new(200, ContractState);
        var sync = NewSync(passport, transport);

        (await sync.UpdateProfileAsync(nickname: "  " + new string('a', 30) + "  ", titleId: "nordic_master")).Should().BeTrue();

        using var body = JsonDocument.Parse(transport.Calls.Single().Body!);
        body.RootElement.GetProperty("nickname").GetString().Should().HaveLength(PassportGoals.NicknameMax);
        body.RootElement.GetProperty("title_id").GetString().Should().Be("nordic-master");
        body.RootElement.TryGetProperty("cover_id", out _).Should().BeFalse();
        passport.Goals.CollectorNo.Should().Be("992562");

        transport.Answers["PUT /client/passport/profile"] = new(400, """{"error":{"code":"PASSPORT_TITLE_NOT_EARNED"}}""");
        (await sync.UpdateProfileAsync(titleId: "night")).Should().BeFalse();
    }

    [Fact]
    public async Task TheTimeZone_IsSentWhenItChanges()
    {
        var passport = NewPassport();
        var transport = new FakeTransport();
        transport.Answers["GET /client/passport"] = new(200, ContractState);
        transport.Answers["PUT /client/passport/profile"] = new(200, ContractState);

        await NewSync(passport, transport, offsetMinutes: 180).RefreshAsync();
        await NewSync(passport, transport, offsetMinutes: 180).RefreshAsync();
        await NewSync(passport, transport, offsetMinutes: 120).RefreshAsync();

        transport.Calls.Where(c => c.Method == "PUT").Select(c => c.Body).Should().Equal("""{"tz_offset_min":180}""", """{"tz_offset_min":120}""");
    }

    [Fact]
    public async Task Shares_AreReported()
    {
        var transport = new FakeTransport();
        await NewSync(NewPassport(), transport).ReportShareAsync();
        transport.Calls.Single().Should().Be(("POST", "/client/passport/events", """{"kind":"share"}"""));
    }

    [Fact]
    public void DuelLink_RoundTrips_AndMatchesThePhonesFormat()
    {
        var link = PassportDuel.Link("Deniz Ç", "482913", "night", "12345000003000000000");
        link.Should().Be("https://colitu.com/p#MXxEZW5peiDDh3w0ODI5MTN8bmlnaHR8MTIzNDUwMDAwMDMwMDAwMDAwMDA");
        var parsed = PassportDuel.Parse(link)!;
        parsed.Nick.Should().Be("Deniz Ç");
        parsed.Levels["GB"].Should().Be(5);
        parsed.Levels["NO"].Should().Be(3);
        parsed.CountIn(ColituPassport.Stamps).Should().Be(5);
        PassportDuel.Parse("https://colitu.com/p#not base64!").Should().BeNull();
        PassportDuel.Parse(null).Should().BeNull();
    }

    [Fact]
    public void MyLink_CarriesTheServerState_AndFriendsAreKeptOncePerCollector()
    {
        var passport = WithState();
        var mine = PassportDuel.Parse(passport.Social.MyLink())!;
        mine.Levels["SE"].Should().Be(2);
        mine.Levels["FI"].Should().Be(5);
        mine.Collector.Should().Be("992562");
        mine.Title.Should().Be("nordic_master");

        var friend = PassportDuel.Parse(PassportDuel.Link("Ada", "111111", null, "11110000000000000000"))!;
        passport.Social.AddFriend(friend.ToFriend(1));
        passport.Social.AddFriend(friend.ToFriend(2));
        passport.Social.AddFriend(mine.ToFriend(3)); // never yourself
        passport.Social.Friends.Should().ContainSingle().Which.CountIn(ColituPassport.Stamps).Should().Be(4);
        passport.Social.RemoveFriend("111111");
        passport.Social.Friends.Should().BeEmpty();
    }

    [Fact]
    public void Levels_FollowTheTwelvePieceRule()
    {
        var progress = new StampProgress(11, 0, null, 1);
        progress.Level.Should().Be(StampLevel.Bronze);
        (progress with { Pieces = 12 }).Level.Should().Be(StampLevel.Silver);
        (progress with { Pieces = 36 }).Level.Should().Be(StampLevel.Diamond);
        (progress with { Pieces = 48 }).Mastered.Should().BeTrue();
        StampProgress.None.Level.Should().BeNull();
    }
}
