using Microsoft.EntityFrameworkCore;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Plugin.Anilist.Mapping;
using Shoko.Plugin.Anilist.Metadata;
using Shoko.Plugin.Anilist.Storage;
using Xunit;

namespace Shoko.Plugin.Anilist.Tests;

/// <summary>
/// The plugin's own database: what it keeps besides the core's stores, and
/// what it forgets after a purge, read back from an in-memory SQLite database
/// migrated as the server migrates it.
/// </summary>
public class AnilistStoreTests
{
    [Fact]
    public void Anime_RoundTripsThroughTheAnimeTable()
    {
        using var harness = new TestHarness();
        harness.Store.SaveAnime(new() { ID = 21, MalID = 21, Color = "#e4a15d" });

        Assert.Equal("#e4a15d", harness.Store.GetAnime(21)?.Color);
        using (var context = harness.Database.CreateDbContext())
            Assert.NotNull(context.Anime.Find(21));
        Assert.Null(harness.Store.GetAnime(0));
        Assert.Single(harness.Store.GetAllAnime());
    }

    [Fact]
    public void Episodes_AreFiledUnderTheirAnime()
    {
        using var harness = new TestHarness();
        harness.Store.ReplaceEpisodes(21, [Episode(21, 2), Episode(21, 1)]);
        harness.Store.ReplaceEpisodes(22, [Episode(22, 1)]);

        Assert.Equal([1, 2], harness.Store.GetEpisodesForAnime(21).Select(episode => episode.EpisodeNumber));
        Assert.Equal(21, harness.Store.GetEpisode(AnilistUtility.PackEpisodeID(21, 2))?.AnimeID);
        Assert.Single(harness.Store.GetEpisodesForAnime(22));
    }

    [Fact]
    public void ReplaceEpisodes_RemovesWhatIsNoLongerThere()
    {
        using var harness = new TestHarness();
        harness.Store.ReplaceEpisodes(21, [Episode(21, 1), Episode(21, 2)]);

        var removed = harness.Store.ReplaceEpisodes(21, [Episode(21, 1)]);

        Assert.Equal(1, removed);
        Assert.Single(harness.Store.GetEpisodesForAnime(21));
    }

    [Fact]
    public void RemoveAnime_ForgetsOnlyThePluginsOwnRecords()
    {
        using var harness = new TestHarness();
        harness.Store.SaveAnime(new() { ID = 21 });
        harness.Store.ReplaceEpisodes(21, [Episode(21, 1)]);
        harness.Series.SaveSeries(new() { ID = AnilistUtility.SeriesGuid(21) });

        Assert.True(harness.Store.RemoveAnime(21));
        Assert.False(harness.Store.RemoveAnime(21));

        Assert.Null(harness.Store.GetAnime(21));
        Assert.Empty(harness.Store.GetEpisodesForAnime(21));
        // The core's stores are the core's to purge.
        Assert.NotNull(harness.Store.GetSeries(21));
    }

    [Fact]
    public void Portraits_AreKeptByPersonAndForgottenOnceThePersonIsGone()
    {
        using var harness = new TestHarness();
        var creator = AnilistUtility.Guid(MetadataEntityType.Creator, 95);
        var character = AnilistUtility.Guid(MetadataEntityType.Character, 40);
        harness.People.SaveCreators([new() { ID = creator, Name = "Mayumi Tanaka" }]);
        harness.Store.SavePortraits([new(creator, "staff/large/n95.jpg"), new(character, "character/large/b40.png")]);

        Assert.Equal("creator/95", AnilistStore.PortraitKey(creator));
        Assert.Equal("staff/large/n95.jpg", harness.Store.GetPortrait(creator));
        Assert.Equal("character/large/b40.png", harness.Store.GetPortrait(character));

        // The character was never stored in the people store, so it goes.
        Assert.Equal(1, harness.Store.RemoveOrphanedPortraits());
        Assert.NotNull(harness.Store.GetPortrait(creator));
        Assert.Null(harness.Store.GetPortrait(character));
    }

    [Fact]
    public void TheMigrationsMatchTheModel()
    {
        using var harness = new TestHarness();
        using var context = harness.Database.CreateDbContext();

        Assert.Empty(context.Database.GetPendingMigrations());
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public void AnAnime_KeepsItsSeasonRecommendationsAndDates_AndIsReplacedWhole()
    {
        using var harness = new TestHarness();
        var createdAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        harness.Store.SaveAnime(new()
        {
            ID = 21,
            Season = YearlySeason.Autumn,
            SeasonYear = 1999,
            MeanScore = 88.5,
            Recommendations = [new() { ID = 20, Score = 12 }, new() { ID = 30 }],
            CreatedAt = createdAt,
            LastUpdatedAt = createdAt,
        });

        var anime = harness.Store.GetAnime(21);
        Assert.NotNull(anime);
        Assert.Equal(YearlySeason.Autumn, anime.Season);
        Assert.Equal(88.5, anime.MeanScore);
        Assert.Equal([(20, (int?)12), (30, null)], anime.Recommendations.Select(recommendation => (recommendation.ID, recommendation.Score)));
        Assert.Equal(createdAt, anime.CreatedAt);
        Assert.Equal(DateTimeKind.Utc, anime.CreatedAt.Kind);
        using (var context = harness.Database.CreateDbContext())
            Assert.Equal(YearlySeason.Autumn.ToString(), context.Database.SqlQueryRaw<string>("SELECT Season AS Value FROM Anime").Single());

        harness.Store.SaveAnime(new() { ID = 21, Color = "#fff" });

        anime = harness.Store.GetAnime(21);
        Assert.NotNull(anime);
        Assert.Null(anime.Season);
        Assert.Empty(anime.Recommendations);
        Assert.Equal("#fff", anime.Color);
    }

    [Fact]
    public void ReplaceEpisodes_RefusesAnotherAnimesEpisode()
    {
        using var harness = new TestHarness();

        Assert.Throws<ArgumentException>(() => harness.Store.ReplaceEpisodes(21, [Episode(21, 1), Episode(22, 1)]));
        Assert.Throws<ArgumentException>(() => harness.Store.ReplaceEpisodes(21, [Episode(21, 1), Episode(21, 1)]));
        Assert.Empty(harness.Store.GetEpisodesForAnime(21));
    }

    [Fact]
    public void ReplaceEpisodes_UpdatesTheOnesKept()
    {
        using var harness = new TestHarness();
        var airedAt = new DateTime(2026, 9, 1, 15, 0, 0, DateTimeKind.Utc);
        harness.Store.ReplaceEpisodes(21, [Episode(21, 1)]);

        harness.Store.ReplaceEpisodes(21, [Episode(21, 1, airedAt)]);

        var episode = Assert.Single(harness.Store.GetEpisodesForAnime(21));
        Assert.Equal(airedAt, episode.AiredAt);
        Assert.Equal(DateTimeKind.Utc, episode.AiredAt!.Value.Kind);
    }

    [Fact]
    public void APortrait_IsReplacedByALaterOne()
    {
        using var harness = new TestHarness();
        var creator = AnilistUtility.Guid(MetadataEntityType.Creator, 95);

        harness.Store.SavePortraits([new(creator, "staff/large/old.jpg")]);
        harness.Store.SavePortraits([new(creator, "staff/large/new.jpg")]);

        Assert.Equal("staff/large/new.jpg", harness.Store.GetPortrait(creator));
    }

    [Fact]
    public void GetSeries_ReadsTheCoresSeriesStore()
    {
        using var harness = new TestHarness();
        harness.Series.SaveSeries(new() { ID = AnilistUtility.SeriesGuid(21), Episodes = [AnilistMediaMapper.ToEpisodeData(Episode(21, 1))] });

        Assert.Equal(AnilistUtility.SeriesGuid(21), harness.Store.GetSeries(21)?.ID);
        Assert.Null(harness.Store.GetSeries(0));
        Assert.Equal(1, harness.Store.GetSeries(21)?.Episodes.Single().EpisodeNumber);
        Assert.Equal(MetadataSource.AniList, harness.Store.GetSeries(21)?.Source);
    }

    /// <summary>
    /// Stores an anime the way a refresh would: in the series store with its
    /// episodes, and as the plugin's own records.
    /// </summary>
    internal static void StoreAnime(TestHarness harness, int animeID, int episodes = 3, string mainTitle = "ONE PIECE", DateTime? firstAiredAt = null)
    {
        var media = new AnilistMedia { ID = animeID, MainTitle = mainTitle, OriginalLanguageCode = "ja" };
        var stored = Enumerable.Range(1, episodes).Select(number => Episode(animeID, number, firstAiredAt?.AddDays(7 * (number - 1)))).ToList();
        harness.Series.SaveSeries(AnilistMediaMapper.ToSeriesData(media, stored));
        harness.Store.SaveAnime(AnilistMediaMapper.ToStoredAnime(media, null, DateTime.UtcNow, quick: false));
        harness.Store.ReplaceEpisodes(animeID, stored);
    }

    internal static AnilistStoredEpisode Episode(int animeID, int number, DateTime? airedAt = null)
        => new() { ID = AnilistUtility.PackEpisodeID(animeID, number), AnimeID = animeID, EpisodeNumber = number, AiredAt = airedAt, RuntimeMinutes = 24 };
}
