using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Moq;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Anidb;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Matching;
using Shoko.Abstractions.Metadata.Providers;
using Shoko.Abstractions.Metadata.Search;
using Shoko.Abstractions.Metadata.Stub;
using Shoko.Plugin.Anilist.Api;
using Shoko.Plugin.Anilist.Mapping;
using Shoko.Plugin.Anilist.Services;
using Xunit;

namespace Shoko.Plugin.Anilist.Tests;

/// <summary>
/// The automatic search asks AniList and has the core's matching engine judge
/// what came back; the engine here is a stand-in rating each candidate as the
/// test says.
/// </summary>
[Collection(nameof(ImageServerCollection))]
public class AnilistSearchServiceTests
{
    private static string SearchPage(params (int ID, string Romaji, string? English, int Year, int? Episodes, string Format)[] media)
        => $$"""
            { "data": { "Page": { "pageInfo": { "currentPage": 1, "lastPage": 1, "hasNextPage": false, "total": {{media.Length}} }, "media": [
            {{string.Join(",", media.Select(m => $$"""
                { "id": {{m.ID}}, "format": "{{m.Format}}", "title": { "romaji": "{{m.Romaji}}", "english": {{(m.English is null ? "null" : $"\"{m.English}\"")}}, "native": null }, "synonyms": ["{{m.Romaji}} Synonym"], "startDate": { "year": {{m.Year}}, "month": 4, "day": 6 }, "seasonYear": {{m.Year}}, "episodes": {{(m.Episodes is null ? "null" : m.Episodes.ToString())}}, "isAdult": false, "countryOfOrigin": "JP" }
                """))}}
            ] } } }
            """;

    private static string EmptyPage => SearchPage();

    private static string Json(int? value) => value is { } known ? known.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null";

    private static string NextAiring(int? episode, DateTimeOffset? airingAt)
        => episode is null ? "null" : $$"""{ "episode": {{episode}}, "airingAt": {{airingAt!.Value.ToUnixTimeSeconds()}} }""";

    // One search result as AniList sends it, with its next episode to air.
    private static string AiringMedia(int id, string romaji, (int Year, int? Month, int? Day) start, int? episodes, int? nextEpisode = null, DateTimeOffset? nextAiringAt = null)
        => $$"""
            { "id": {{id}}, "format": "TV", "title": { "romaji": "{{romaji}}", "english": null, "native": null }, "synonyms": [], "startDate": { "year": {{start.Year}}, "month": {{Json(start.Month)}}, "day": {{Json(start.Day)}} }, "seasonYear": {{start.Year}}, "episodes": {{Json(episodes)}}, "isAdult": false, "countryOfOrigin": "JP", "nextAiringEpisode": {{NextAiring(nextEpisode, nextAiringAt)}} }
            """;

    private static string MediaPage(params string[] media)
        => $$"""
            { "data": { "Page": { "pageInfo": { "currentPage": 1, "lastPage": 1, "hasNextPage": false, "total": {{media.Length}} }, "media": [{{string.Join(",", media)}}] } } }
            """;

    // Weekly slots of one anime, its first episode airing at the time given.
    private static string SchedulePage(bool hasNextPage, int mediaID, IEnumerable<int> episodes, DateTimeOffset firstAiring)
        => $$"""
            { "data": { "Page": { "pageInfo": { "currentPage": 1, "hasNextPage": {{(hasNextPage ? "true" : "false")}} }, "airingSchedules": [
            {{string.Join(",", episodes.Select(episode => $$"""{ "mediaId": {{mediaID}}, "episode": {{episode}}, "airingAt": {{firstAiring.AddDays(7 * (episode - 1)).ToUnixTimeSeconds()}} }"""))}}
            ] } } }
            """;

    private static AnilistSearchResult Result(string media) => new(JsonNode.Parse(media)!);

    // Only the anime is dated, so no candidate's episodes are fetched to line them up.
    private static void Undate(IEnumerable<IAnidbEpisode> episodes)
    {
        foreach (var episode in episodes)
            Mock.Get(episode).SetupGet(e => e.AirDate).Returns((DateOnly?)null);
    }

    private static bool HasName(MetadataSearchResult candidate, string name)
        => new[] { candidate.Title, candidate.OriginalTitle }.Concat(candidate.AlternateTitles).Contains(name, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task FindAutoMatches_TakesWhatTheEngineTakes_AndKeepsTheRestWithWhy()
    {
        using var harness = new ServiceHarness();
        var (_, anime, episodes) = harness.AddShokoSeries(1, 100, 26, new DateOnly(2019, 4, 6));
        Undate(episodes);
        anime.SetupGet(a => a.Titles).Returns([new TitleStub { Source = MetadataSource.AniDB, Language = TitleLanguage.Romaji, LanguageCode = "x-jat", Value = "Kimetsu no Yaiba", Type = TitleType.Main }]);
        var calls = harness.JudgeSeries((candidate, options) => HasName(candidate, options!.Query!) ? MatchRating.DateAndTitleMatches : MatchRating.None);
        harness
            .Respond(SearchPage((112151, "Kimetsu no Yaiba: Mugen Ressha-hen", null, 2020, 1, "MOVIE"), (101922, "Kimetsu no Yaiba", "Demon Slayer", 2019, 26, "TV")))
            .Respond(EmptyPage);

        var matches = await harness.Get<AnilistSearchService>().FindAutoMatches(anime.Object, TestContext.Current.CancellationToken);

        Assert.Equal([101922, 112151], matches.Select(match => match.Anime.ID));
        Assert.Null(matches[0].Rejection);
        Assert.Equal(MatchRating.DateAndTitleMatches, matches[0].MatchRating);
        Assert.Equal(MatchRejectionReason.TitleMismatch, matches[1].Rejection?.Reason);
        Assert.Equal("Searched for \"Kimetsu no Yaiba\".", matches[1].Rejection?.Details);

        var (candidates, options) = Assert.Single(calls);
        Assert.Equal("Kimetsu no Yaiba", options?.Query);
        Assert.Null(options?.QueryLanguage);
        Assert.False(options?.IncludeRestricted);
        Assert.True(options?.SeasonsAreSeparateEntries);
        var demonSlayer = candidates.Single(candidate => candidate.ID == matches[0].Candidate.ID);
        Assert.Equal("Demon Slayer", demonSlayer.Title);
        Assert.Equal(["Kimetsu no Yaiba", "Kimetsu no Yaiba Synonym"], demonSlayer.AlternateTitles);
        Assert.Equal(26, demonSlayer.EpisodeCount);
        Assert.Equal(2019, demonSlayer.SeasonYear);
        Assert.Equal(AnimeType.TV, demonSlayer.Type);
        Assert.Equal(new PartialDateOnly(new DateOnly(2019, 4, 6)), demonSlayer.FirstAiredAt);
    }

    [Fact]
    public async Task FindAutoMatches_TriesTheNextTitle_WhenTheEngineTakesNothing()
    {
        using var harness = new ServiceHarness();
        var (_, anime, episodes) = harness.AddShokoSeries(1, 100, 12, new DateOnly(2019, 4, 6));
        Undate(episodes);
        anime.SetupGet(a => a.Titles).Returns(
        [
            new TitleStub { Source = MetadataSource.AniDB, Language = TitleLanguage.Romaji, LanguageCode = "x-jat", Value = "Romaji Title", Type = TitleType.Main },
            new TitleStub { Source = MetadataSource.AniDB, Language = TitleLanguage.English, LanguageCode = "en", Value = "English Title", Type = TitleType.Official },
        ]);
        var calls = harness.JudgeSeries((candidate, options) => options?.Query is "Romaji Title" && candidate.Title is "Found" ? MatchRating.TitleMatches : MatchRating.None);
        harness
            .Respond(SearchPage((1, "Unrelated", null, 2019, 12, "TV")))
            .Respond(EmptyPage)
            .Respond(SearchPage((2, "Found", null, 2019, 12, "TV")))
            .Respond(EmptyPage);

        var matches = await harness.Get<AnilistSearchService>().FindAutoMatches(anime.Object, TestContext.Current.CancellationToken);

        Assert.Equal(["English Title", "Romaji Title"], calls.Select(call => call.Options?.Query));
        Assert.Equal([2, 1], matches.Select(match => match.Anime.ID));
        Assert.Null(matches[0].Rejection);
        Assert.Equal("Searched for \"English Title\".", matches[1].Rejection?.Details);
    }

    [Fact]
    public async Task FindAutoMatches_KeepsAnAnimeAsTheQueryThatRatedItBest_AndListsTheRestBestFirst()
    {
        using var harness = new ServiceHarness();
        var (_, anime, episodes) = harness.AddShokoSeries(1, 100, 12, new DateOnly(2019, 4, 6));
        Undate(episodes);
        anime.SetupGet(a => a.Titles).Returns(
        [
            new TitleStub { Source = MetadataSource.AniDB, Language = TitleLanguage.Romaji, LanguageCode = "x-jat", Value = "Romaji Title", Type = TitleType.Main },
            new TitleStub { Source = MetadataSource.AniDB, Language = TitleLanguage.English, LanguageCode = "en", Value = "English Title", Type = TitleType.Official },
        ]);
        harness.JudgeSeries((candidate, options) => (options?.Query, candidate.Title) switch
        {
            ("Romaji Title", "Found") => MatchRating.TitleMatches,
            ("Romaji Title", "Close") => MatchRating.DateAndTitleKindaMatches,
            _ => MatchRating.None,
        });
        var weak = (3, "Weak", (string?)null, 2019, (int?)12, "TV");
        var close = (1, "Close", (string?)null, 2019, (int?)12, "TV");
        harness
            .Respond(SearchPage(weak, close))
            .Respond(EmptyPage)
            .Respond(SearchPage((2, "Found", null, 2019, 12, "TV"), weak, close))
            .Respond(EmptyPage);

        var matches = await harness.Get<AnilistSearchService>().FindAutoMatches(anime.Object, TestContext.Current.CancellationToken);

        Assert.Equal([2, 1, 3], matches.Select(match => match.Anime.ID));
        Assert.Null(matches[0].Rejection);
        Assert.Equal(MatchRating.DateAndTitleKindaMatches, matches[1].MatchRating);
        Assert.Equal("Searched for \"Romaji Title\".", matches[1].Rejection?.Details);
        Assert.Equal(MatchRating.None, matches[2].MatchRating);
        Assert.Equal("Searched for \"English Title\".", matches[2].Rejection?.Details);
    }

    [Fact]
    public async Task FindAutoMatches_PrefersItsOwnDatedMatchOverThePrequelsTitle()
    {
        using var harness = new ServiceHarness();
        var (_, anime, episodes) = harness.AddShokoSeries(1, 100, 12, new DateOnly(2026, 7, 17));
        Undate(episodes);
        anime.SetupGet(a => a.Titles).Returns(
        [
            new TitleStub { Source = MetadataSource.AniDB, Language = TitleLanguage.Romaji, LanguageCode = "x-jat", Value = "Wakagimi (2026)", Type = TitleType.Main },
            new TitleStub { Source = MetadataSource.AniDB, Language = TitleLanguage.Japanese, LanguageCode = "ja", Value = "若君 (2026)", Type = TitleType.Official },
        ]);
        var (_, prequel, _) = harness.AddShokoSeries(2, 200, 12, new DateOnly(2024, 7, 6));
        prequel.SetupGet(a => a.Titles).Returns(
        [
            new TitleStub { Source = MetadataSource.AniDB, Language = TitleLanguage.Romaji, LanguageCode = "x-jat", Value = "Wakagimi", Type = TitleType.Main },
            new TitleStub { Source = MetadataSource.AniDB, Language = TitleLanguage.Japanese, LanguageCode = "ja", Value = "若君", Type = TitleType.Official },
        ]);
        var relation = new Mock<IRelatedMetadata<ISeries, ISeries>>();
        relation.SetupGet(r => r.RelationType).Returns(RelationType.Prequel);
        relation.SetupGet(r => r.Related).Returns(prequel.Object);
        anime.SetupGet(a => a.RelatedSeries).Returns([relation.Object]);
        var sequel = (182616, "若君 Season 2", (string?)null, 2026, (int?)12, "TV");
        var original = (162896, "若君", (string?)null, 2024, (int?)12, "TV");
        var calls = harness.JudgeSeries((candidate, options) => (options?.Query, candidate.Title) switch
        {
            ("若君", "若君") => MatchRating.TitleMatches,
            ("若君", _) => MatchRating.TitleKindaMatches,
            ("若君 (2026)", "若君 Season 2") => MatchRating.DateAndTitleKindaMatches,
            _ => MatchRating.None,
        });

        // The prequel's title first, in the year and without it, then the
        // anime's own, whose suffix-free query was asked already.
        harness
            .Respond(SearchPage(sequel))
            .Respond(SearchPage(original, sequel))
            .Respond(EmptyPage)
            .Respond(EmptyPage);

        var matches = await harness.Get<AnilistSearchService>().FindAutoMatches(anime.Object, TestContext.Current.CancellationToken);

        Assert.Equal([182616, 162896], matches.Select(match => match.Anime.ID));
        Assert.Null(matches[0].Rejection);
        Assert.Equal(MatchRating.DateAndTitleKindaMatches, matches[0].MatchRating);
        Assert.Equal(MatchRejectionReason.Outranked, matches[1].Rejection?.Reason);
        Assert.Equal(["若君", "若君 (2026)"], calls.Select(call => call.Options?.Query));
        Assert.All(calls, call => Assert.Equal(TitleLanguage.Japanese, call.Options?.QueryLanguage));
        Assert.Equal(4, harness.Http.Bodies.Count);
    }

    [Fact]
    public async Task FindAutoMatches_AsksAniListEachQueryOnce()
    {
        using var harness = new ServiceHarness();
        var (_, anime, _) = harness.AddShokoSeries(1, 100, 12, new DateOnly(2026, 7, 17));
        anime.SetupGet(a => a.Titles).Returns(
        [
            new TitleStub { Source = MetadataSource.AniDB, Language = TitleLanguage.Romaji, LanguageCode = "x-jat", Value = "Nanimonai (2026)", Type = TitleType.Main },
            new TitleStub { Source = MetadataSource.AniDB, Language = TitleLanguage.Japanese, LanguageCode = "ja", Value = "Nanimonai (2026)", Type = TitleType.Official },
        ]);
        harness.Respond(EmptyPage).Respond(EmptyPage).Respond(EmptyPage).Respond(EmptyPage);

        var matches = await harness.Get<AnilistSearchService>().FindAutoMatches(anime.Object, TestContext.Current.CancellationToken);

        Assert.Empty(matches);
        Assert.Equal(4, harness.Http.Bodies.Count);
        harness.MatchingEngine.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FindAutoMatches_LeavesOutTheAnimeAlreadyLinked()
    {
        using var harness = new ServiceHarness();
        var (_, anime, _) = harness.AddShokoSeries(1, 100, 26, new DateOnly(2019, 4, 6));
        anime.SetupGet(a => a.Titles).Returns([new TitleStub { Source = MetadataSource.AniDB, Language = TitleLanguage.Romaji, LanguageCode = "x-jat", Value = "Nothing Matches Here", Type = TitleType.Main }]);
        AnilistStoreTests.StoreAnime(harness.Stores, 555, mainTitle: "Linked");
        harness.Stores.CrossReferences.AddSeries(100, 555);
        harness.Respond(EmptyPage).Respond(EmptyPage);

        Assert.Empty(await harness.Get<AnilistSearchService>().FindAutoMatches(anime.Object, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FindAutoMatches_SearchesTheYearOfTheRegularBroadcast()
    {
        using var harness = new ServiceHarness();
        var (_, anime, episodes) = harness.AddShokoSeries(1, 100, 12, new DateOnly(2018, 12, 20));
        anime.SetupGet(a => a.Titles).Returns([new TitleStub { Source = MetadataSource.AniDB, Language = TitleLanguage.Romaji, LanguageCode = "x-jat", Value = "Early Showing", Type = TitleType.Main }]);
        var first = Mock.Get(episodes[0]);
        first.SetupGet(e => e.AirDate).Returns(new DateOnly(2019, 1, 10));
        first.SetupGet(e => e.EarlyAirDate).Returns(new DateOnly(2018, 12, 20));
        harness.Respond(EmptyPage).Respond(EmptyPage);

        await harness.Get<AnilistSearchService>().FindAutoMatches(anime.Object, TestContext.Current.CancellationToken);

        Assert.Contains("\"startAfter\":20181231", harness.Http.Bodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindAutoMatches_AllowsRestrictedAnime_WhenTheCoreSaysSo()
    {
        using var harness = new ServiceHarness();
        var (_, anime, _) = harness.AddShokoSeries(1, 100, 12, new DateOnly(2019, 4, 6));
        anime.SetupGet(a => a.Titles).Returns([new TitleStub { Source = MetadataSource.AniDB, Language = TitleLanguage.Romaji, LanguageCode = "x-jat", Value = "Adult Title", Type = TitleType.Main }]);
        var info = (MetadataProviderInfo)RuntimeHelpers.GetUninitializedObject(typeof(MetadataProviderInfo));
        info.AutoLinkRestricted = true;
        harness.ProviderManager.Setup(manager => manager.GetProviderInfo(It.IsAny<Type>())).Returns(info);
        var calls = harness.JudgeSeries((_, _) => MatchRating.None);
        harness.Respond(SearchPage((1, "Adult Title", null, 2019, 12, "TV"))).Respond(EmptyPage);

        await harness.Get<AnilistSearchService>().FindAutoMatches(anime.Object, TestContext.Current.CancellationToken);

        Assert.True(Assert.Single(calls).Options?.IncludeRestricted);
        Assert.DoesNotContain("$isAdult", harness.Http.Bodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindAutoMatches_SkipsMusicVideosAndFutureAnime()
    {
        using var harness = new ServiceHarness();
        var (_, musicVideo, _) = harness.AddShokoSeries(1, 100, 1, new DateOnly(2019, 4, 6));
        musicVideo.SetupGet(a => a.Type).Returns(AnimeType.MusicVideo);
        var (_, future, _) = harness.AddShokoSeries(2, 200, 1, DateOnly.FromDateTime(DateTime.Today.AddYears(1)));

        Assert.Empty(await harness.Get<AnilistSearchService>().FindAutoMatches(musicVideo.Object, TestContext.Current.CancellationToken));
        Assert.Empty(await harness.Get<AnilistSearchService>().FindAutoMatches(future.Object, TestContext.Current.CancellationToken));
        Assert.Empty(harness.Http.Bodies);
    }

    [Fact]
    public async Task FindAutoMatches_LeavesMusicVideosOutOfTheCandidates()
    {
        using var harness = new ServiceHarness();
        var (_, anime, episodes) = harness.AddShokoSeries(1, 100, 12, new DateOnly(2019, 4, 6));
        Undate(episodes);
        anime.SetupGet(a => a.Titles).Returns([new TitleStub { Source = MetadataSource.AniDB, Language = TitleLanguage.Romaji, LanguageCode = "x-jat", Value = "Song", Type = TitleType.Main }]);
        var calls = harness.JudgeSeries((_, _) => MatchRating.TitleMatches);
        harness.Respond(SearchPage((1, "Song", null, 2019, 1, "MUSIC"), (2, "Song", null, 2019, 12, "TV"))).Respond(EmptyPage);

        await harness.Get<AnilistSearchService>().FindAutoMatches(anime.Object, TestContext.Current.CancellationToken);

        Assert.Equal([AnilistUtility.SeriesGuid(2)], Assert.Single(calls).Candidates.Select(candidate => candidate.ID));
    }

    [Fact]
    public async Task FindAutoMatches_JudgesTheBestCandidatesAgainWithTheirEpisodes()
    {
        using var harness = new ServiceHarness();
        var (_, anime, _) = harness.AddShokoSeries(1, 100, 12, new DateOnly(2019, 4, 6));
        anime.SetupGet(a => a.Titles).Returns([new TitleStub { Source = MetadataSource.AniDB, Language = TitleLanguage.Romaji, LanguageCode = "x-jat", Value = "Split Cour", Type = TitleType.Main }]);
        var calls = harness.JudgeSeries((candidate, _) => candidate.Title switch
        {
            "Best" => MatchRating.DateAndTitleMatches,
            "Second" => MatchRating.TitleMatches,
            "Third" => MatchRating.DateMatches,
            "Fourth" => MatchRating.TitleKindaMatches,
            _ => MatchRating.None,
        });
        harness
            .Respond(SearchPage((303, "Unrated", null, 2019, 12, "TV"), (202, "Fourth", null, 2019, 12, "TV"), (404, "Third", null, 2019, 12, "TV"), (505, "Second", null, 2019, 24, "TV"), (101, "Best", null, 2019, 12, "TV")))
            .Respond(EmptyPage)
            .Respond(Fixture.Read("airing-schedules-page.json"));

        var matches = await harness.Get<AnilistSearchService>().FindAutoMatches(anime.Object, TestContext.Current.CancellationToken);

        // One request for the three best rated, over a month either side of the anime's run.
        Assert.Equal(3, harness.Http.Bodies.Count);
        var request = JsonNode.Parse(harness.Http.Bodies[2])!["variables"]!;
        Assert.Equal([101, 505, 404], request["ids"]!.AsArray().Select(id => id!.GetValue<int>()));
        Assert.Equal(new DateTimeOffset(2019, 3, 7, 0, 0, 0, TimeSpan.FromHours(9)).ToUnixTimeSeconds(), request["after"]!.GetValue<long>());
        Assert.Equal(new DateTimeOffset(2019, 7, 22, 0, 0, 0, TimeSpan.FromHours(9)).ToUnixTimeSeconds(), request["before"]!.GetValue<long>());

        // Judged again, each with its slots as one season dated in Japan.
        Assert.Equal(2, calls.Count);
        var judged = calls[1].Candidates.ToDictionary(candidate => candidate.Title);
        var best = Assert.Single(judged["Best"].Seasons!);
        Assert.Equal(1, best.SeasonNumber);
        Assert.Equal(12, best.EpisodeCount);
        Assert.Equal(new DateOnly(2019, 4, 7), best.FirstEpisodeAiredAt);
        Assert.Equal([(1, new DateOnly(2019, 4, 7)), (2, new DateOnly(2019, 4, 14)), (3, new DateOnly(2019, 4, 21))], best.Episodes!.Select(episode => (episode.EpisodeNumber, episode.AiredAt!.Value)));
        var second = Assert.Single(judged["Second"].Seasons!);
        Assert.Equal([13, 14], second.Episodes!.Select(episode => episode.EpisodeNumber));
        Assert.Equal(new DateOnly(2019, 4, 6), second.FirstEpisodeAiredAt);
        Assert.Null(judged["Third"].Seasons);
        Assert.Null(judged["Fourth"].Seasons);
        Assert.Null(judged["Unrated"].Seasons);

        Assert.Equal(101, matches[0].Anime.ID);
        Assert.NotNull(matches[0].Candidate.Seasons);
    }

    [Fact]
    public async Task FindAutoMatches_FetchesNoEpisodes_ForAnAnimeWithNoDatedEpisode()
    {
        using var harness = new ServiceHarness();
        var (_, anime, episodes) = harness.AddShokoSeries(1, 100, 12, new DateOnly(2019, 4, 6));
        Undate(episodes);
        anime.SetupGet(a => a.Titles).Returns([new TitleStub { Source = MetadataSource.AniDB, Language = TitleLanguage.Romaji, LanguageCode = "x-jat", Value = "Undated", Type = TitleType.Main }]);
        var calls = harness.JudgeSeries((_, _) => MatchRating.DateAndTitleMatches);
        harness.Respond(SearchPage((101, "Undated", null, 2019, 12, "TV"))).Respond(EmptyPage);

        await harness.Get<AnilistSearchService>().FindAutoMatches(anime.Object, TestContext.Current.CancellationToken);

        Assert.Equal(2, harness.Http.Bodies.Count);
        Assert.Null(Assert.Single(Assert.Single(calls).Candidates).Seasons);
    }

    [Fact]
    public async Task FindAutoMatches_FetchesEachCandidatesEpisodesOnce_AcrossTitles()
    {
        using var harness = new ServiceHarness();
        var (_, anime, _) = harness.AddShokoSeries(1, 100, 12, new DateOnly(2019, 4, 6));
        anime.SetupGet(a => a.Titles).Returns(
        [
            new TitleStub { Source = MetadataSource.AniDB, Language = TitleLanguage.Romaji, LanguageCode = "x-jat", Value = "Romaji Title", Type = TitleType.Main },
            new TitleStub { Source = MetadataSource.AniDB, Language = TitleLanguage.English, LanguageCode = "en", Value = "English Title", Type = TitleType.Official },
        ]);
        // The English title rates the anime but takes nothing, so the romaji one is tried too.
        var calls = new List<(IReadOnlyList<MetadataSeriesSearchResult> Candidates, SeriesMatchOptions? Options)>();
        harness.MatchingEngine
            .Setup(engine => engine.MatchSeries(It.IsAny<IAnidbAnime>(), It.IsAny<IReadOnlyList<MetadataSeriesSearchResult>>(), It.IsAny<SeriesMatchOptions?>()))
            .Returns((IAnidbAnime judgedAnime, IReadOnlyList<MetadataSeriesSearchResult> candidates, SeriesMatchOptions? options) =>
            {
                calls.Add((candidates, options));
                var taken = options?.Query is "Romaji Title";
                return [.. candidates.Select(candidate => new SeriesMatch
                {
                    AnidbAnime = judgedAnime,
                    Candidate = candidate,
                    Rating = taken ? MatchRating.TitleMatches : MatchRating.TitleKindaMatches,
                    Rejection = taken ? MatchRejectionReason.None : MatchRejectionReason.TitleMismatch,
                })];
            });
        var best = (101, "Best", (string?)null, 2019, (int?)12, "TV");
        harness
            .Respond(SearchPage(best))
            .Respond(EmptyPage)
            .Respond(Fixture.Read("airing-schedules-page.json"))
            .Respond(SearchPage(best))
            .Respond(EmptyPage);

        await harness.Get<AnilistSearchService>().FindAutoMatches(anime.Object, TestContext.Current.CancellationToken);

        // English first, then romaji; the second title reuses the slots.
        Assert.Equal(5, harness.Http.Bodies.Count);
        Assert.Equal(4, calls.Count);
        Assert.NotNull(Assert.Single(calls[3].Candidates).Seasons);
    }

    [Fact]
    public async Task FindAutoMatches_TakesTheAiringSequel_OnceItsScheduleTellsItsLength()
    {
        // AniDB 19779 ("Gachiakuta 2"), its episodes not yet dated: AniList
        // knows season 1's length but not season 2's, which is airing, so
        // season 1 came first on an equal count.
        using var harness = new ServiceHarness();
        var (_, anime, episodes) = harness.AddShokoSeries(1, 19779, 12, new DateOnly(2026, 9, 5));
        Undate(episodes);
        anime.SetupGet(a => a.Titles).Returns([new TitleStub { Source = MetadataSource.AniDB, Language = TitleLanguage.Romaji, LanguageCode = "x-jat", Value = "Gachiakuta 2", Type = TitleType.Main }]);
        var calls = harness.JudgeSeriesWithTies((_, _) => MatchRating.TitleMatches);
        var firstAiring = new DateTimeOffset(2026, 9, 5, 15, 0, 0, TimeSpan.Zero);
        harness
            .Respond(MediaPage(
                AiringMedia(178025, "Gachiakuta", (2025, 7, 6), 24),
                AiringMedia(204436, "Gachiakuta Season 2", (2026, 9, 5), null, 5, firstAiring.AddDays(28))
            ))
            .Respond(EmptyPage)
            .Respond(SchedulePage(false, 204436, Enumerable.Range(1, 12), firstAiring));

        var matches = await harness.Get<AnilistSearchService>().FindAutoMatches(anime.Object, TestContext.Current.CancellationToken);

        Assert.Equal([204436, 178025], matches.Select(match => match.Anime.ID));
        Assert.Null(matches[0].Rejection);
        Assert.Equal(MatchRejectionReason.EpisodeCountMismatch, matches[1].Rejection?.Reason);

        // Four aired proves nothing against twelve, so the count stayed
        // unknown until the whole schedule, upcoming slots included, told it.
        Assert.Equal(2, calls.Count);
        Assert.Null(calls[0].Candidates.Single(candidate => candidate.Title is "Gachiakuta Season 2").EpisodeCount);
        var sequel = calls[1].Candidates.Single(candidate => candidate.Title is "Gachiakuta Season 2");
        Assert.Equal(12, sequel.EpisodeCount);
        var season = Assert.Single(sequel.Seasons!);
        Assert.Equal(12, season.EpisodeCount);
        Assert.Equal(12, season.Episodes!.Count);
        Assert.Equal(new DateOnly(2026, 9, 6), season.FirstEpisodeAiredAt);

        Assert.Equal(3, harness.Http.Bodies.Count);
        var request = JsonNode.Parse(harness.Http.Bodies[2])!;
        Assert.Equal([204436], request["variables"]!["ids"]!.AsArray().Select(id => id!.GetValue<int>()));
        Assert.DoesNotContain("airingAt_greater", request["query"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain("notYetAired", request["query"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(15, 12, 14)]
    [InlineData(13, 12, null)]
    [InlineData(15, 24, null)]
    [InlineData(15, 0, null)]
    [InlineData(null, 12, null)]
    public void ToCandidate_TakesTheAiredCount_OnlyWhenItProvesTheAnimeLonger(int? nextEpisode, int anidbEpisodeCount, int? expected)
    {
        var result = Result(AiringMedia(1, "Airing", (2026, 1, 9), null, nextEpisode, new DateTimeOffset(2026, 4, 17, 15, 0, 0, TimeSpan.Zero)));

        Assert.Equal(expected, AnilistSearchService.ToCandidate(result, anidbEpisodeCount).EpisodeCount);
    }

    [Fact]
    public void ToCandidate_KeepsAKnownCount()
    {
        var result = Result(AiringMedia(1, "Airing", (2026, 1, 9), 12, 15, new DateTimeOffset(2026, 4, 17, 15, 0, 0, TimeSpan.Zero)));

        Assert.Equal(12, AnilistSearchService.ToCandidate(result, 12).EpisodeCount);
    }

    [Theory]
    [InlineData(null, null, 1, "2026-01-10")]
    [InlineData(1, null, 1, "2026-01-10")]
    [InlineData(1, 5, 1, "2026-01-05")]
    [InlineData(null, null, 2, null)]
    public void ToCandidate_DatesAnUndatedStart_ByItsFirstEpisodeInJapan(int? month, int? day, int nextEpisode, string? expected)
    {
        // 15:30 UTC on the 9th is half past midnight on the 10th in Japan.
        var result = Result(AiringMedia(1, "Upcoming", (2026, month, day), null, nextEpisode, new DateTimeOffset(2026, 1, 9, 15, 30, 0, TimeSpan.Zero)));

        var started = AnilistSearchService.ToCandidate(result, 12).FirstAiredAt;

        if (expected is null)
            Assert.False(started?.IsComplete);
        else
            Assert.Equal(new PartialDateOnly(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture)), started);
    }

    [Fact]
    public async Task SearchAnime_AsksForTheNextEpisode_WhereTheFullFetchDoesNot()
    {
        using var harness = new ServiceHarness();
        harness
            .Respond(MediaPage(AiringMedia(1, "Airing", (2026, 1, 9), null, 5, new DateTimeOffset(2026, 2, 6, 15, 0, 0, TimeSpan.Zero))))
            .Respond(Fixture.Read("media-21.json"));

        var (page, _) = await harness.Get<AnilistSearchService>().SearchAnime(new() { Query = "Airing" }, TestContext.Current.CancellationToken);
        await harness.Get<AnilistApiClient>().GetAnimeAsync(21, TestContext.Current.CancellationToken);

        var anime = Assert.Single(page).Anime;
        Assert.Equal(5, anime.NextEpisodeNumber);
        Assert.Equal(new DateTime(2026, 2, 6, 15, 0, 0, DateTimeKind.Utc), anime.NextEpisodeAiringAt);
        Assert.Contains("nextAiringEpisode", harness.Http.Bodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("nextAiringEpisode", harness.Http.Bodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindAutoMatches_ReadsAPagePerTiedAnime_AndTakesACutOffCountOnlyAsALowerBound()
    {
        using var harness = new ServiceHarness();
        var (_, anime, episodes) = harness.AddShokoSeries(1, 100, 12, new DateOnly(2026, 1, 9));
        Undate(episodes);
        anime.SetupGet(a => a.Titles).Returns([new TitleStub { Source = MetadataSource.AniDB, Language = TitleLanguage.Romaji, LanguageCode = "x-jat", Value = "Tied", Type = TitleType.Main }]);
        var calls = harness.JudgeSeriesWithTies((_, _) => MatchRating.TitleMatches);
        var firstAiring = new DateTimeOffset(2024, 1, 5, 15, 0, 0, TimeSpan.Zero);
        harness
            .Respond(MediaPage(AiringMedia(10, "Long Runner", (2026, 1, 9), null), AiringMedia(20, "Tied", (2026, 1, 9), null)))
            .Respond(EmptyPage)
            .Respond(SchedulePage(true, 10, Enumerable.Range(1, 50), firstAiring))
            .Respond(SchedulePage(true, 10, Enumerable.Range(51, 50), firstAiring));

        await harness.Get<AnilistSearchService>().FindAutoMatches(anime.Object, TestContext.Current.CancellationToken);

        // Two anime, two pages, and the read ended inside the first: its
        // hundred episodes prove it longer, the second was never reached.
        Assert.Equal(4, harness.Http.Bodies.Count);
        Assert.Equal([1, 2], harness.Http.Bodies.Skip(2).Select(body => JsonNode.Parse(body)!["variables"]!["page"]!.GetValue<int>()));
        var judged = calls[1].Candidates.ToDictionary(candidate => candidate.Title);
        Assert.Equal(100, judged["Long Runner"].EpisodeCount);
        Assert.Equal(100, Assert.Single(judged["Long Runner"].Seasons!).Episodes!.Count);
        Assert.Null(judged["Tied"].EpisodeCount);
        Assert.Null(judged["Tied"].Seasons);
    }

    [Theory]
    [InlineData(null, MatchRating.DateMatches, null)]
    [InlineData(12, MatchRating.TitleMatches, 24)]
    public async Task FindAutoMatches_FetchesNoWholeSchedule_WhenNoUnknownCountIsTied(int? firstEpisodes, MatchRating secondRating, int? secondEpisodes)
    {
        using var harness = new ServiceHarness();
        var (_, anime, episodes) = harness.AddShokoSeries(1, 100, 12, new DateOnly(2026, 1, 9));
        Undate(episodes);
        anime.SetupGet(a => a.Titles).Returns([new TitleStub { Source = MetadataSource.AniDB, Language = TitleLanguage.Romaji, LanguageCode = "x-jat", Value = "First", Type = TitleType.Main }]);
        var calls = harness.JudgeSeriesWithTies((candidate, _) => candidate.Title is "First" ? MatchRating.TitleMatches : secondRating);
        harness
            .Respond(MediaPage(AiringMedia(1, "First", (2026, 1, 9), firstEpisodes), AiringMedia(2, "Second", (2026, 1, 9), secondEpisodes)))
            .Respond(EmptyPage);

        var matches = await harness.Get<AnilistSearchService>().FindAutoMatches(anime.Object, TestContext.Current.CancellationToken);

        Assert.Equal(1, matches[0].Anime.ID);
        Assert.Equal(2, harness.Http.Bodies.Count);
        Assert.Single(calls);
    }

    [Fact]
    public async Task FindAutoMatches_TurnsDownAnEarlierSeasonFoundByThePrequelsTitle()
    {
        using var harness = new ServiceHarness();
        var (_, anime, episodes) = harness.AddShokoSeries(1, 19779, 12, new DateOnly(2026, 4, 6));
        Undate(episodes);
        anime.SetupGet(a => a.Titles).Returns(
        [
            new TitleStub { Source = MetadataSource.AniDB, Language = TitleLanguage.Romaji, LanguageCode = "x-jat", Value = "Gachiakuta 2", Type = TitleType.Main },
            new TitleStub { Source = MetadataSource.AniDB, Language = TitleLanguage.Japanese, LanguageCode = "ja", Value = "ガチアクタ 2", Type = TitleType.Official },
        ]);
        var (_, prequel, _) = harness.AddShokoSeries(2, 18000, 24, new DateOnly(2025, 4, 6));
        prequel.SetupGet(a => a.Titles).Returns(
        [
            new TitleStub { Source = MetadataSource.AniDB, Language = TitleLanguage.Romaji, LanguageCode = "x-jat", Value = "Gachiakuta", Type = TitleType.Main },
            new TitleStub { Source = MetadataSource.AniDB, Language = TitleLanguage.Japanese, LanguageCode = "ja", Value = "ガチアクタ", Type = TitleType.Official },
        ]);
        var relation = new Mock<IRelatedMetadata<ISeries, ISeries>>();
        relation.SetupGet(r => r.RelationType).Returns(RelationType.Prequel);
        relation.SetupGet(r => r.Related).Returns(prequel.Object);
        anime.SetupGet(a => a.RelatedSeries).Returns([relation.Object]);
        var sequel = (204436, "ガチアクタ 第2クール", (string?)null, 2026, (int?)null, "TV");
        var original = (178025, "ガチアクタ", (string?)null, 2025, (int?)24, "TV");

        // The prequel's title finds season 1 by its title alone, rated above
        // what the anime's own title finds, which only kinda matches.
        harness.JudgeSeries((candidate, options) => (options?.Query, candidate.Title) switch
        {
            ("ガチアクタ", "ガチアクタ") => MatchRating.TitleMatches,
            ("ガチアクタ", _) => MatchRating.TitleKindaMatches,
            ("ガチアクタ 2", "ガチアクタ 第2クール") => MatchRating.TitleKindaMatches,
            _ => MatchRating.None,
        });
        harness
            .Respond(SearchPage(sequel))
            .Respond(SearchPage(original, sequel))
            .Respond(SearchPage(sequel))
            .Respond(EmptyPage);

        var matches = await harness.Get<AnilistSearchService>().FindAutoMatches(anime.Object, TestContext.Current.CancellationToken);

        Assert.Equal([204436, 178025], matches.Select(match => match.Anime.ID));
        Assert.Null(matches[0].Rejection);
        Assert.Equal(MatchRejectionReason.Outranked, matches[1].Rejection?.Reason);
        Assert.StartsWith("It began long before the anime", matches[1].Rejection?.Details, StringComparison.Ordinal);
        Assert.Equal(4, harness.Http.Bodies.Count);
    }

    [Fact]
    public void MatchPriority_RanksTitleAndDateAboveEitherAlone()
        => Assert.True(AnilistSearchService.MatchPriority(MatchRating.DateAndTitleMatches) < AnilistSearchService.MatchPriority(MatchRating.TitleMatches)
            && AnilistSearchService.MatchPriority(MatchRating.TitleMatches) < AnilistSearchService.MatchPriority(MatchRating.DateMatches));
}
