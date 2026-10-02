using System.Net;
using Moq;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Airing;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Providers;
using Shoko.Plugin.Anilist.Airing;
using Shoko.Plugin.Anilist.Api;
using Shoko.Plugin.Anilist.Mapping;
using Shoko.Plugin.Anilist.Metadata;
using Xunit;

namespace Shoko.Plugin.Anilist.Tests;

/// <summary>
/// The provider contract: what it claims, the refresh it owns, the clean-up
/// after a purge, its images, its pause, and the search, lookup, auto-link
/// candidates and matching it keeps.
/// </summary>
[Collection(nameof(ImageServerCollection))]
public class AnilistMetadataProviderTests
{
    private static readonly MetadataGuid _series = AnilistUtility.SeriesGuid(21);

    [Fact]
    public void Provider_ClaimsTheAnilistSource_ForSeriesAndEpisodes()
    {
        using var harness = new ServiceHarness();
        var provider = harness.Get<AnilistMetadataProvider>();

        Assert.Same(AnilistSources.AniList, provider.Source);
        Assert.Equal("anilist", provider.Source.Value);
        Assert.Equal("AniList", provider.Source.Name);
        Assert.Equal("The anime and manga database at anilist.co.", provider.Source.Description);
        Assert.True(MetadataSource.TryGet("AniList", out var parsed) && ReferenceEquals(parsed, AnilistSources.AniList));
        Assert.Equal([MetadataEntityType.Series, MetadataEntityType.Episode], provider.LinkableEntityTypes.OrderBy(type => type.Value, StringComparer.Ordinal).Reverse());
        Assert.False(provider.AutoLinkByDefault);
        Assert.True(provider.AutoLinkRestrictedByDefault);
        Assert.Equal(4, provider.MaxConcurrentJobs);
        Assert.Equal("AniList", provider.Name);
    }

    [Fact]
    public async Task RefreshSeries_WritesTheAnime_AndIgnoresAnythingElse()
    {
        using var harness = new ServiceHarness().Respond(Fixture.Read("media-21.json"));
        var provider = harness.Get<AnilistMetadataProvider>();

        await provider.RefreshSeries(new MetadataGuid(MetadataSource.TMDB, MetadataEntityType.Series, "21"), new(), TestContext.Current.CancellationToken);
        await provider.RefreshSeries(_series, new(), TestContext.Current.CancellationToken);

        Assert.Single(harness.Http.Bodies);
        Assert.NotNull(harness.Stores.Store.GetSeries(21));
    }

    [Fact]
    public async Task RefreshSeries_Throws_WhenAnilistFails()
    {
        using var harness = new ServiceHarness().Respond("oops", HttpStatusCode.ServiceUnavailable);

        await Assert.ThrowsAsync<AnilistUnavailableException>(() => harness.Get<AnilistMetadataProvider>().RefreshSeries(_series, new(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CleanUp_ForgetsThePluginsOwnDocuments_AndTheSchedules()
    {
        using var harness = new ServiceHarness();
        AnilistStoreTests.StoreAnime(harness.Stores, 21);
        var provider = harness.Get<AnilistMetadataProvider>();
        var info = (AiringScheduleProviderInfo)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(AiringScheduleProviderInfo));
        typeof(AiringScheduleProviderInfo).GetProperty(nameof(AiringScheduleProviderInfo.ID))!.SetValue(info, Guid.NewGuid());
        var schedule = Mock.Of<IAiringSchedule>(s => s.SeriesID == _series);
        var other = Mock.Of<IAiringSchedule>(s => s.SeriesID == AnilistUtility.SeriesGuid(22));
        harness.AiringScheduleService.Setup(service => service.GetProviderInfo(It.IsAny<IAiringScheduleProvider>())).Returns(info);
        harness.AiringScheduleService.Setup(service => service.GetSchedulesForProvider(info.ID, It.IsAny<AiringScheduleFilteringOptions?>())).Returns([schedule, other]);
        harness.AiringScheduleService.Setup(service => service.RemoveSchedule(It.IsAny<IAiringScheduleProvider>(), It.IsAny<IAiringSchedule>())).Returns(true);

        await provider.CleanUp(_series, TestContext.Current.CancellationToken);

        Assert.Null(harness.Stores.Store.GetAnime(21));
        Assert.Empty(harness.Stores.Store.GetEpisodesForAnime(21));
        harness.AiringScheduleService.Verify(service => service.RemoveSchedule(It.IsAny<IAiringScheduleProvider>(), schedule), Times.Once);
        harness.AiringScheduleService.Verify(service => service.RemoveSchedule(It.IsAny<IAiringScheduleProvider>(), other), Times.Never);
    }

    [Fact]
    public async Task GetImages_HandsOutTheCoverAndBanner_AndThePortraits()
    {
        using var harness = new ServiceHarness(new() { AutoDownloadCharacters = true, AutoDownloadStaff = true }).Respond(Fixture.Read("media-21.json"));
        var provider = harness.Get<AnilistMetadataProvider>();
        await provider.RefreshSeries(_series, new(), TestContext.Current.CancellationToken);

        var series = await provider.GetImages(_series, TestContext.Current.CancellationToken);
        var creator = await provider.GetImages(AnilistUtility.Guid(MetadataEntityType.Creator, 95011), TestContext.Current.CancellationToken);
        var faceless = await provider.GetImages(AnilistUtility.Guid(MetadataEntityType.Creator, 95012), TestContext.Current.CancellationToken);
        var episode = await provider.GetImages(AnilistUtility.EpisodeGuid(AnilistUtility.PackEpisodeID(21, 1)), TestContext.Current.CancellationToken);
        var unknown = await provider.GetImages(AnilistUtility.SeriesGuid(22), TestContext.Current.CancellationToken);

        Assert.NotNull(series);
        Assert.Equal([ImageEntityType.Primary, ImageEntityType.Banner], series.Select(image => image.ImageType));
        Assert.Equal("media/anime/cover/large/bx21-YCDoj1EkAxFn.jpg", series[0].ResourceID);
        Assert.Equal("staff/large/n95011.jpg", Assert.Single(creator!).ResourceID);
        Assert.Empty(faceless!);
        Assert.Null(episode);
        Assert.Null(unknown);
        harness.ImageManager.Verify(manager => manager.RegisterTemplateUrl(AnilistSources.AniList, "https://s4.anilist.co/file/anilistcdn/{0}"), Times.Once);
    }

    [Fact]
    public async Task PauseStatus_FollowsTheBreaker_WithAReason()
    {
        using var harness = new ServiceHarness().Respond("oops", HttpStatusCode.BadGateway);
        var provider = harness.Get<AnilistMetadataProvider>();
        var changes = 0;
        provider.PauseStatusChanged += (_, _) => changes++;
        Assert.False(provider.PauseStatus.IsPaused);

        await Assert.ThrowsAnyAsync<Exception>(() => provider.RefreshSeries(_series, new(), TestContext.Current.CancellationToken));

        var status = provider.PauseStatus;
        Assert.True(status.IsPaused);
        Assert.Contains("server error", status.Reason, StringComparison.Ordinal);
        Assert.NotNull(status.ResumesAt);
        Assert.InRange(status.GetRemainingPauseTime()!.Value, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task FindAutoLinks_HandsBackTheTakenCandidateFirst_WithoutLinkingIt()
    {
        using var harness = new ServiceHarness().Respond(Fixture.Read("search-one-piece.json"));
        var (_, anime, _) = harness.AddShokoSeries(1, 100, 3, new DateOnly(1999, 10, 20));
        anime.SetupGet(a => a.Titles).Returns([new Shoko.Abstractions.Metadata.Stub.TitleStub { Source = MetadataSource.AniDB, Language = TitleLanguage.Romaji, LanguageCode = "x-jat", Value = "One Piece", Type = TitleType.Main }]);
        harness.JudgeSeries((candidate, _) => candidate.ID == _series ? MatchRating.DateAndTitleMatches : MatchRating.None);
        for (var i = 0; i < 8; i++)
            harness.Respond("""{ "data": { "Page": { "pageInfo": { "total": 0 }, "media": [] } } }""");

        var candidates = await harness.Get<AnilistMetadataProvider>().FindAutoLinks(100, TestContext.Current.CancellationToken);

        Assert.True(candidates.Count > 1);
        var taken = candidates[0];
        Assert.Equal(_series, taken.ID);
        Assert.Equal("ONE PIECE", taken.Result.Title);
        Assert.Equal(100, taken.AnidbAnimeID);
        Assert.Null(taken.AnidbEpisodeID);
        Assert.Equal(MatchRating.DateAndTitleMatches, taken.MatchRating);
        Assert.True(taken.IsRemote);
        Assert.False(taken.IsLocal);
        Assert.Null(taken.Rejection);
        Assert.All(candidates.Skip(1), candidate => Assert.Equal(MatchRejectionReason.TitleMismatch, candidate.Rejection?.Reason));
        Assert.Equal(candidates.Count, candidates.Select(candidate => candidate.ID).Distinct().Count());
        harness.LinkingService.Verify(service => service.AddSeriesLink(It.IsAny<MetadataSeriesLinkRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FindAutoLinks_HandsBackOnlyRejections_WhenNothingIsTaken()
    {
        using var harness = new ServiceHarness().Respond(Fixture.Read("search-one-piece.json"));
        var (_, anime, _) = harness.AddShokoSeries(1, 100, 3, new DateOnly(1999, 10, 20));
        anime.SetupGet(a => a.Titles).Returns([new Shoko.Abstractions.Metadata.Stub.TitleStub { Source = MetadataSource.AniDB, Language = TitleLanguage.Romaji, LanguageCode = "x-jat", Value = "One Piece", Type = TitleType.Main }]);
        harness.JudgeSeries((_, _) => MatchRating.None);
        for (var i = 0; i < 8; i++)
            harness.Respond("""{ "data": { "Page": { "pageInfo": { "total": 0 }, "media": [] } } }""");

        var candidates = await harness.Get<AnilistMetadataProvider>().FindAutoLinks(100, TestContext.Current.CancellationToken);

        Assert.NotEmpty(candidates);
        Assert.All(candidates, candidate => Assert.NotNull(candidate.Rejection));
    }

    [Fact]
    public async Task FindAutoLinks_FindsNothing_ForAnAnimeNotKnownLocally()
    {
        using var harness = new ServiceHarness();

        Assert.Empty(await harness.Get<AnilistMetadataProvider>().FindAutoLinks(100, TestContext.Current.CancellationToken));
        Assert.Empty(harness.Http.Bodies);
    }

    [Fact]
    public async Task LookupSeries_AnswersAStoredAnimeWithoutAskingAnilist()
    {
        using var harness = new ServiceHarness();
        AnilistStoreTests.StoreAnime(harness.Stores, 21, mainTitle: "Stored Title");

        var result = await harness.Get<AnilistMetadataProvider>().LookupSeries(_series, TestContext.Current.CancellationToken);

        Assert.Equal(_series, result?.ID);
        Assert.Equal("Stored Title", result?.Title);
        Assert.Empty(harness.Http.Bodies);
    }

    [Fact]
    public async Task LookupSeries_AsksAnilistForAnAnimeNotStored_WithoutStoringIt()
    {
        using var harness = new ServiceHarness()
            .Respond(Fixture.Read("media-21.json"))
            .Respond("""{ "errors": [ { "message": "Not Found.", "status": 404 } ], "data": { "Media": null } }""");
        var provider = harness.Get<AnilistMetadataProvider>();

        var found = await provider.LookupSeries(_series, TestContext.Current.CancellationToken);
        var missing = await provider.LookupSeries(AnilistUtility.SeriesGuid(22), TestContext.Current.CancellationToken);
        var foreign = await provider.LookupSeries(new MetadataGuid(MetadataSource.TMDB, MetadataEntityType.Series, "21"), TestContext.Current.CancellationToken);

        Assert.Equal(_series, found?.ID);
        Assert.Equal("ONE PIECE", found?.Title);
        Assert.Null(missing);
        Assert.Null(foreign);
        Assert.Equal(2, harness.Http.Bodies.Count);
        Assert.Null(harness.Stores.Store.GetSeries(21));
    }

    [Fact]
    public async Task ATransientFailure_IsTheCoresUnavailableException_WithTheWaitLeft()
    {
        using var harness = new ServiceHarness().Respond("oops", HttpStatusCode.BadGateway);

        var exception = await Assert.ThrowsAsync<AnilistUnavailableException>(() => harness.Get<AnilistMetadataProvider>().SearchSeries(new() { Query = "one piece" }, TestContext.Current.CancellationToken));

        Assert.IsAssignableFrom<MetadataProviderUnavailableException>(exception);
        Assert.Same(AnilistSources.AniList, exception.MetadataSource);
        Assert.NotNull(exception.RetryAfter);
        Assert.InRange(exception.RetryAfter.Value, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task ARejectedQuery_IsNotAnUnavailableSource()
    {
        using var harness = new ServiceHarness().Respond("""{ "errors": [ { "message": "Validation error.", "status": 400 } ], "data": null }""");

        var exception = await Assert.ThrowsAsync<AnilistApiException>(() => harness.Get<AnilistMetadataProvider>().SearchSeries(new() { Query = "one piece" }, TestContext.Current.CancellationToken));

        Assert.IsNotAssignableFrom<MetadataProviderUnavailableException>(exception);
        Assert.False(harness.RateLimiter.IsPaused);
    }

    [Fact]
    public async Task MatchEpisodes_RunsTheCoresMatcherOverTheStoredEpisodes()
    {
        using var harness = new ServiceHarness();
        AnilistStoreTests.StoreAnime(harness.Stores, 21, episodes: 2);
        var (_, anime, episodes) = harness.AddShokoSeries(1, 100, 3);
        harness.MatchByPosition();
        var provider = harness.Get<AnilistMetadataProvider>();

        var matches = await provider.MatchEpisodes(anime.Object, episodes, _series, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, matches.Count);
        Assert.Null(matches[2].Candidate);
        Assert.Empty(await provider.MatchEpisodes(anime.Object, episodes, AnilistUtility.SeriesGuid(999), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await provider.MatchEpisodes(anime.Object, episodes, new MetadataGuid(MetadataSource.TMDB, MetadataEntityType.Series, "21"), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SearchSeries_AnswersWithTheContractsSearchResults()
    {
        using var harness = new ServiceHarness().Respond(Fixture.Read("search-one-piece.json"));

        var (page, total) = await harness.Get<AnilistMetadataProvider>().SearchSeries(new() { Query = "one piece" }, TestContext.Current.CancellationToken);

        Assert.Equal(42, total);
        Assert.Equal([AnilistUtility.SeriesGuid(21), AnilistUtility.SeriesGuid(459)], page.Select(result => result.ID));
        Assert.All(page, result => Assert.Equal(AnilistSources.AniList, result.Source));
        Assert.Equal("ONE PIECE", page[0].Title);
        Assert.Equal("ONE PIECE: Movie 1", page[1].Title);
        Assert.Equal(8.8m, page[0].UserRating);
        Assert.Equal(AnimeType.Movie, page[1].Type);
        Assert.Contains("\"search\":\"one piece\"", harness.Http.Bodies[0], StringComparison.Ordinal);
    }
}
