using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Storage;
using Shoko.Plugin.Anilist.Mapping;
using Shoko.Plugin.Anilist.Metadata;
using Xunit;

namespace Shoko.Plugin.Anilist.Tests;

/// <summary>
/// An AniList anime's suggestions merge both stored directions when it is
/// written.
/// </summary>
/// <remarks>
/// AniList holds one undirected recommendation and serves it from both sides
/// with the same score, so an edge stored while refreshing one anime belongs
/// to the other as well. These pin what relying on that means, including
/// which copy wins when both were stored.
/// </remarks>
public class AnilistSuggestionMergeTests
{
    private const int Subject = 100;

    private static MetadataGuid Series(int id) => AnilistUtility.SeriesGuid(id);

    private static MetadataSuggestionData Direct(int id, int score, int order)
        => new() { SuggestedID = Series(id), Kind = SuggestionKind.Recommended, Score = score, Order = order };

    [Fact]
    public void AnEdgeStoredFromTheOtherSideIsStillASuggestion()
    {
        var merged = AnilistMediaMapper.MergeSuggestions(Subject, [], [(Series(300), 50)]);

        var only = Assert.Single(merged);
        Assert.Equal(Series(300), only.SuggestedID);
        Assert.Equal(50, only.Score);
        Assert.Equal(SuggestionKind.Recommended, only.Kind);
        Assert.Null(only.Votes);
        Assert.Equal(0, only.Order);
    }

    [Fact]
    public void BothDirectionsAreMergedIntoOneListOrderedByScore()
    {
        var merged = AnilistMediaMapper.MergeSuggestions(Subject, [Direct(200, 10, 0)], [(Series(300), 90)]);

        Assert.Equal([Series(300), Series(200)], merged.Select(suggestion => suggestion.SuggestedID));
        Assert.Equal([0, 1], merged.Select(suggestion => suggestion.Order));
    }

    [Fact]
    public void TheDirectCopyWinsWhenBothWereStored()
    {
        var merged = AnilistMediaMapper.MergeSuggestions(Subject, [Direct(200, 40, 1)], [(Series(200), 35)]);

        var only = Assert.Single(merged);
        Assert.Equal(Series(200), only.SuggestedID);
        Assert.Equal(40, only.Score);
    }

    [Fact]
    public void TheAnimeNeverSuggestsItselfNorAnotherSourcesEntry()
    {
        var other = new MetadataGuid(MetadataSource.AniDB, MetadataEntityType.Series, "1");

        var merged = AnilistMediaMapper.MergeSuggestions(Subject, [], [(Series(Subject), 90), (other, 80)]);

        Assert.Empty(merged);
    }

    [Fact]
    public async Task ARefreshWritesTheMergedList()
    {
        using var harness = new ServiceHarness().Respond(Fixture.Read("media-21.json"));
        harness.Stores.Store.SaveAnime(new() { ID = 999, Recommendations = [new() { ID = 21, Score = 1000 }] });

        await harness.Get<Shoko.Plugin.Anilist.Services.AnilistRefreshService>().RefreshAnime(21, new(), TestContext.Current.CancellationToken);

        var stored = harness.Stores.Suggestions.Entries[Series(21)];
        Assert.Equal(Series(999), stored[0].SuggestedID);
        Assert.Contains(stored, suggestion => suggestion.SuggestedID == Series(1735));
        Assert.All(stored, suggestion => Assert.Equal(AnilistSources.AniList, suggestion.SuggestedID.Source));
        Assert.Equal([1735, 6702], harness.Stores.Store.GetAnime(21)!.Recommendations.Select(recommendation => recommendation.ID));
    }

    [Fact]
    public async Task AnEdgeOnlyMergedInIsNotKeptAlive()
    {
        using var harness = new ServiceHarness().Respond(Fixture.Read("media-21.json"));
        // 999 lists 21 only because 21 once recommended it, which AniList no
        // longer does; 999's own recommendations do not name 21.
        harness.Stores.Store.SaveAnime(new() { ID = 999, Recommendations = [new() { ID = 1735, Score = 5 }] });
        harness.Stores.Suggestions.SetSuggestions(Series(999), [Direct(21, 80, 0)]);

        await harness.Get<Shoko.Plugin.Anilist.Services.AnilistRefreshService>().RefreshAnime(21, new(), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(harness.Stores.Suggestions.Entries[Series(21)], suggestion => suggestion.SuggestedID == Series(999));
    }
}
