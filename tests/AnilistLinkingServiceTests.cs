using Moq;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Anidb;
using Shoko.Abstractions.Metadata.CrossReferences;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Matching;
using Shoko.Plugin.Anilist.Mapping;
using Shoko.Plugin.Anilist.Metadata;
using Shoko.Plugin.Anilist.Services;
using Xunit;

namespace Shoko.Plugin.Anilist.Tests;

/// <summary>
/// The links go through the core's linking service; the plugin decides what
/// to ask for and matches over the episodes the series store holds.
/// </summary>
public class AnilistLinkingServiceTests
{
    private static MetadataGuid EpisodeID(int anime, int number) => AnilistUtility.EpisodeGuid(AnilistUtility.PackEpisodeID(anime, number));

    private static ServiceHarness WithAnime(int anilistAnimeID = 21, int episodes = 3)
    {
        var harness = new ServiceHarness();
        AnilistStoreTests.StoreAnime(harness.Stores, anilistAnimeID, episodes);
        return harness;
    }

    [Fact]
    public async Task ResetEpisodeLinks_IsTheCoresBulkCall()
    {
        using var harness = WithAnime();

        await harness.Get<AnilistLinkingService>().ResetEpisodeLinks(100, allowAutoMatch: false, TestContext.Current.CancellationToken);

        harness.LinkingService.Verify(service => service.ResetEpisodeLinks(AnilistSources.AniList, 100, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MatchAndSave_HandsTheCoreTheSeriesAndTheFlags()
    {
        using var harness = WithAnime();

        await harness.Get<AnilistLinkingService>().MatchAndSave(100, 21, useExisting: false, save: false, considerOtherLinks: true, TestContext.Current.CancellationToken);

        harness.LinkingService.Verify(service => service.MatchEpisodes(100, AnilistUtility.SeriesGuid(21), null, false, false, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MatchLinkedEpisodes_SwallowsAProviderTheCoreTurnedOff()
    {
        using var harness = WithAnime();
        harness.Stores.CrossReferences.AddSeries(100, 21);
        harness.LinkingService
            .Setup(service => service.MatchEpisodes(It.IsAny<int>(), It.IsAny<MetadataGuid>(), It.IsAny<MetadataGuid?>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotSupportedException("Off."));

        await harness.Get<AnilistLinkingService>().MatchLinkedEpisodes(21, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Match_RunsTheCoresMatcherOverTheStoredEpisodes_DateFirst()
    {
        using var harness = WithAnime(episodes: 2);
        var (_, anime, episodes) = harness.AddShokoSeries(1, 100, 3);
        var calls = harness.MatchByPosition();

        var matches = harness.Get<AnilistLinkingService>().Match(anime.Object, episodes, 21);

        Assert.Equal(3, matches.Count);
        Assert.Equal([AnilistUtility.SeriesGuid(21), AnilistUtility.SeriesGuid(21)], matches.Take(2).Select(match => match.Candidate!.SeriesID));
        Assert.Null(matches[2].Candidate);
        Assert.Equal(EpisodeMatchStrategy.DateThenNumber, Assert.Single(calls).Options?.Strategy);
        Assert.Empty(harness.Get<AnilistLinkingService>().Match(anime.Object, episodes, 999));
    }

    [Fact]
    public void Match_LeavesOutEpisodesAnotherAnimeClaims_WhenAsked()
    {
        using var harness = WithAnime();
        var (_, anime, episodes) = harness.AddShokoSeries(1, 100, 3);
        harness.Stores.CrossReferences.AddEpisode(200, 200001, AnilistUtility.PackEpisodeID(21, 1));
        var calls = harness.MatchByPosition();
        var linking = harness.Get<AnilistLinkingService>();

        linking.Match(anime.Object, episodes, 21, considerOtherLinks: true);
        linking.Match(anime.Object, episodes, 21, considerOtherLinks: false);

        Assert.Equal([EpisodeID(21, 2), EpisodeID(21, 3)], calls[0].Provider.Select(episode => episode.ID));
        Assert.Equal(3, calls[1].Provider.Count);
        Assert.All(calls[0].Provider, episode => Assert.Null(episode.SeasonNumber));
    }

    [Fact]
    public void Match_LeavesEpisodesLinkedIntoAnotherAnilistAnimeAlone()
    {
        using var harness = WithAnime();
        AnilistStoreTests.StoreAnime(harness.Stores, 22, 3);
        var (_, anime, episodes) = harness.AddShokoSeries(1, 100, 4);
        harness.Stores.CrossReferences
            .AddEpisode(100, 100001, AnilistUtility.PackEpisodeID(21, 1), MatchRating.DateAndNumberMatches)
            .AddEpisode(100, 100003, AnilistUtility.PackEpisodeID(22, 1), MatchRating.UserVerified)
            .AddEpisode(100, 100004, AnilistUtility.PackEpisodeID(22, 2), MatchRating.FirstAvailable);
        var existing = harness.Stores.CrossReferences.GetEpisodeLinksForSeries(100, AnilistSources.AniList);
        IReadOnlyList<IMetadataEpisodeCrossReference>? handed = null;
        harness.MatchingEngine
            .Setup(engine => engine.MatchEpisodes(It.IsAny<IReadOnlyList<IAnidbEpisode>>(), It.IsAny<IReadOnlyList<IEpisode>>(), It.IsAny<IReadOnlyList<IMetadataEpisodeCrossReference>?>(), It.IsAny<EpisodeMatchOptions?>()))
            .Callback((IReadOnlyList<IAnidbEpisode> _, IReadOnlyList<IEpisode> _, IReadOnlyList<IMetadataEpisodeCrossReference>? links, EpisodeMatchOptions? _) => handed = links)
            .Returns((IReadOnlyList<IAnidbEpisode> anidb, IReadOnlyList<IEpisode> _, IReadOnlyList<IMetadataEpisodeCrossReference>? _, EpisodeMatchOptions? _) => [.. anidb.Select(episode => new EpisodeMatch { AnidbEpisode = episode, Candidate = null, Rating = MatchRating.None })]);

        var matches = harness.Get<AnilistLinkingService>().Match(anime.Object, episodes, 21, existing);

        Assert.Equal([100001, 100002], matches.Select(match => match.AnidbEpisode.AnidbID));
        Assert.Equal([EpisodeID(21, 1)], Assert.IsAssignableFrom<IReadOnlyList<IMetadataEpisodeCrossReference>>(handed).Select(link => link.ProviderID));
    }

    [Fact]
    public void Match_MatchesAgain_AnEpisodeLinkedToOneAnilistNoLongerLists()
    {
        using var harness = WithAnime(episodes: 2);
        var (_, anime, episodes) = harness.AddShokoSeries(1, 100, 3);
        harness.Stores.CrossReferences
            .AddEpisode(100, 100001, AnilistUtility.PackEpisodeID(21, 1), MatchRating.DateAndNumberMatches)
            .AddEpisode(100, 100002, AnilistUtility.PackEpisodeID(21, 5), MatchRating.UserVerified);
        var existing = harness.Stores.CrossReferences.GetEpisodeLinksForSeries(100, AnilistSources.AniList);
        IReadOnlyList<IMetadataEpisodeCrossReference>? handed = null;
        harness.MatchingEngine
            .Setup(engine => engine.MatchEpisodes(It.IsAny<IReadOnlyList<IAnidbEpisode>>(), It.IsAny<IReadOnlyList<IEpisode>>(), It.IsAny<IReadOnlyList<IMetadataEpisodeCrossReference>?>(), It.IsAny<EpisodeMatchOptions?>()))
            .Callback((IReadOnlyList<IAnidbEpisode> _, IReadOnlyList<IEpisode> _, IReadOnlyList<IMetadataEpisodeCrossReference>? links, EpisodeMatchOptions? _) => handed = links)
            .Returns((IReadOnlyList<IAnidbEpisode> anidb, IReadOnlyList<IEpisode> _, IReadOnlyList<IMetadataEpisodeCrossReference>? _, EpisodeMatchOptions? _) => [.. anidb.Select(episode => new EpisodeMatch { AnidbEpisode = episode, Candidate = null, Rating = MatchRating.None })]);

        var matches = harness.Get<AnilistLinkingService>().Match(anime.Object, episodes, 21, existing);

        Assert.Equal([100001, 100002, 100003], matches.Select(match => match.AnidbEpisode.AnidbID));
        Assert.Equal([EpisodeID(21, 1)], Assert.IsAssignableFrom<IReadOnlyList<IMetadataEpisodeCrossReference>>(handed).Select(link => link.ProviderID));
    }

    [Fact]
    public void Match_KeepsTheEpisodesTheUserSaidHaveNoAnilistEpisode()
    {
        using var harness = WithAnime();
        var (_, anime, episodes) = harness.AddShokoSeries(1, 100, 3);
        harness.Stores.CrossReferences
            .AddEpisode(100, 100001, 0, MatchRating.UserVerified)
            .AddEpisode(100, 100002, 0, MatchRating.None);
        var existing = harness.Stores.CrossReferences.GetEpisodeLinksForSeries(100, AnilistSources.AniList);
        var calls = harness.MatchByPosition();

        var matches = harness.Get<AnilistLinkingService>().Match(anime.Object, episodes, 21, existing);

        Assert.Equal([100002, 100003], matches.Select(match => match.AnidbEpisode.AnidbID));
        Assert.Equal([100002, 100003], Assert.Single(calls).Anidb.Select(episode => episode.AnidbID));
    }

    [Fact]
    public void Match_WithoutExistingLinks_MatchesEveryEpisodeAfresh()
    {
        using var harness = WithAnime();
        var (_, anime, episodes) = harness.AddShokoSeries(1, 100, 3);
        harness.Stores.CrossReferences.AddEpisode(100, 100001, AnilistUtility.PackEpisodeID(22, 1));
        var calls = harness.MatchByPosition();

        var matches = harness.Get<AnilistLinkingService>().Match(anime.Object, episodes, 21);

        Assert.Equal(3, matches.Count);
        Assert.Equal(3, Assert.Single(calls).Anidb.Count);
    }
}
