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
    public void MatchPriority_RanksTitleAndDateAboveEitherAlone()
        => Assert.True(AnilistSearchService.MatchPriority(MatchRating.DateAndTitleMatches) < AnilistSearchService.MatchPriority(MatchRating.TitleMatches)
            && AnilistSearchService.MatchPriority(MatchRating.TitleMatches) < AnilistSearchService.MatchPriority(MatchRating.DateMatches));
}
