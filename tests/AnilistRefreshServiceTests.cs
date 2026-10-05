using System.Net;
using System.Text.Json.Nodes;
using Moq;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Plugin.Anilist.Api;
using Shoko.Plugin.Anilist.Mapping;
using Shoko.Plugin.Anilist.Metadata;
using Shoko.Plugin.Anilist.Services;
using Xunit;

namespace Shoko.Plugin.Anilist.Tests;

/// <summary>
/// A refresh, end to end: a stubbed AniList answer in, the core's stores,
/// the plugin's documents and the links out.
/// </summary>
[Collection(nameof(ImageServerCollection))]
public class AnilistRefreshServiceTests
{
    private static readonly string _media = Fixture.Read("media-21.json");

    private static readonly MetadataGuid _series = AnilistUtility.SeriesGuid(21);

    private const string CharactersPage2 = """
        { "data": { "Media": { "characters": {
          "pageInfo": { "currentPage": 2, "hasNextPage": false },
          "edges": [ { "role": "SUPPORTING", "voiceActorRoles": [], "node": { "id": 42, "name": { "full": "Roronoa Zoro" } } } ]
        } } } }
        """;

    private const string StaffPage2 = """
        { "data": { "Media": { "staff": {
          "pageInfo": { "currentPage": 2, "hasNextPage": false },
          "edges": [ { "role": "Director", "node": { "id": 97000, "name": { "full": "Konosuke Uda" }, "languageV2": "Japanese" } } ]
        } } } }
        """;

    // The anime with a second page of characters and of staff.
    private static string MediaWithMorePeople()
    {
        var node = JsonNode.Parse(_media)!;
        node["data"]!["Media"]!["characters"]!["pageInfo"]!["hasNextPage"] = true;
        node["data"]!["Media"]!["staff"]!["pageInfo"]!["hasNextPage"] = true;
        return node.ToJsonString();
    }

    private static AnilistConfiguration Unthrottled() => new()
    {
        RateLimit = { MaxRequestsPerWindow = 90, WindowDurationMs = 1000 },
    };

    [Fact]
    public async Task RefreshAnime_WritesTheSeriesAndEverythingAroundIt()
    {
        using var harness = new ServiceHarness(Unthrottled()).Respond(_media);

        Assert.True(await harness.Get<AnilistRefreshService>().RefreshAnime(21, new(), TestContext.Current.CancellationToken));

        var stores = harness.Stores;
        var series = stores.Series.Series[_series];
        Assert.Equal(["ONE PIECE", "ONE PIECE", "ONE PIECE", "OP", "ワンピース"], series.Titles.Select(title => title.Value));
        Assert.Equal([TitleType.Main, TitleType.Official, TitleType.Official, TitleType.Synonym, TitleType.Synonym], series.Titles.Select(title => title.Type));
        Assert.All(series.Titles, title => Assert.Equal(MetadataSource.AniList, title.Source));
        Assert.StartsWith("Gold Roger", Assert.Single(series.Overviews).Value, StringComparison.Ordinal);
        Assert.Equal(8.8, series.Rating, 3);
        Assert.Equal(150, series.RatingVotes);
        Assert.False(series.Restricted);
        Assert.Equal(ReleaseStatus.Releasing, series.ReleaseStatus);
        Assert.Equal(SourceMaterial.Manga, series.SourceMaterial);
        Assert.Equal("ja", series.OriginalLanguageCode);
        Assert.Equal(600000, series.Popularity);
        Assert.Equal(90000, series.FavoriteCount);
        Assert.Equal(new PartialDateOnly(1999, 10, 20), series.AirDate);
        Assert.Empty(series.Seasons);
        Assert.Empty(series.ContentRatings);
        Assert.Equal(3, series.Episodes.Count);
        Assert.Equal(AnilistUtility.EpisodeGuid(AnilistUtility.PackEpisodeID(21, 1)), series.Episodes[0].ID);
        Assert.Equal(TimeSpan.FromMinutes(24), series.Episodes[0].Runtime);
        Assert.Equal("Episode 1", Assert.Single(series.Episodes[0].Titles).Value);

        var document = stores.Store.GetAnime(21)!;
        Assert.Equal(21, document.MalID);
        Assert.Equal("media/anime/cover/large/bx21-YCDoj1EkAxFn.jpg", document.CoverImagePath);
        Assert.NotEqual(document.CreatedAt, default);
        Assert.Equal(3, stores.Store.GetEpisodesForAnime(21).Count);

        Assert.Equal(["94", "150", "Action", "Adventure"], stores.Tags.Entries[_series].Select(tag => tag.TagID.ID));
        Assert.Equal([TagKind.Tag, TagKind.Tag, TagKind.Genre, TagKind.Genre], stores.Tags.GetTags(_series).Select(tag => tag.Kind));
        Assert.Equal(["18", "16"], stores.Studios.Entries[_series].Select(studio => studio.StudioID.ID));
        Assert.Equal(["459", "466"], stores.Relations.Entries[_series].Select(relation => relation.RelatedID.ID));
        Assert.Equal(["1735", "6702"], stores.Suggestions.Entries[_series].Select(suggestion => suggestion.SuggestedID.ID));
        Assert.Equal(3, stores.People.Cast[_series].Count);
        Assert.Equal(2, stores.People.Crew[_series].Count);
    }

    [Fact]
    public async Task RefreshAnime_WritesThePeopleWithTheirNewFields()
    {
        using var harness = new ServiceHarness(Unthrottled()).Respond(_media);

        await harness.Get<AnilistRefreshService>().RefreshAnime(21, new(), TestContext.Current.CancellationToken);

        var people = harness.Stores.People;
        var luffy = people.Characters.Single(character => character.ID.ID == "40").Data;
        Assert.Equal(PersonGender.Male, luffy.Gender);
        Assert.Equal(new FuzzyDateOnly(null, 5, 5), luffy.BirthDay);
        Assert.Equal("Straw Hat", Assert.Single(luffy.AlternativeNames).Name);
        Assert.Equal("40", Assert.Single(luffy.Resources).ID);
        var tanaka = people.Creators.Single(creator => creator.ID.ID == "95011").Data;
        Assert.Equal(new FuzzyDateOnly(1955, 1, 15), tanaka.BirthDay);
        Assert.Equal(PersonGender.Female, tanaka.Gender);

        var dub = people.Cast[_series].Single(cast => cast.CreatorID?.ID == "95012");
        Assert.Equal("(young)", dub.RoleNotes);
        Assert.Equal("Funimation", dub.DubGroup);
        Assert.Equal("en", dub.LanguageCode);
        Assert.Equal(CastRoleType.MainCharacter, dub.RoleType);

        var adr = people.Crew[_series].Single(crew => crew.CreatorID.ID == "95012");
        Assert.Equal("ADR Director", adr.Name);
        Assert.Equal("en", adr.LanguageCode);
        Assert.Equal("ja", people.Crew[_series].Single(crew => crew.CreatorID.ID == "96880").LanguageCode);

        Assert.Equal("staff/large/n95011.jpg", harness.Stores.Store.GetPortrait(tanaka.ID));
    }

    [Fact]
    public async Task RefreshAnime_WritesTheExternalLinksWithTheirBareIDs()
    {
        using var harness = new ServiceHarness().Respond(_media);

        await harness.Get<AnilistRefreshService>().RefreshAnime(21, new(), TestContext.Current.CancellationToken);

        var resources = harness.Stores.Series.Series[_series].Resources;
        Assert.Equal(("AniList", "21"), (resources[0].Name, resources[0].ID));
        Assert.Equal(("MyAnimeList", "21"), (resources[1].Name, resources[1].ID));
        Assert.Equal((ResourceType.Trailer, "abc123"), (resources[2].Type, resources[2].ID));
        Assert.Equal(ResourceType.Streaming, resources[3].Type);
        Assert.Equal("en", resources[3].LanguageCode);
        Assert.Null(resources[4].ID);
    }

    [Fact]
    public async Task QuickRefresh_LeavesTheAnimeLookingNew_AndLeavesThePeopleAndMatchingOut()
    {
        using var harness = new ServiceHarness(Unthrottled()).Respond(_media);
        harness.Stores.CrossReferences.AddSeries(100, 21);

        await harness.Get<AnilistRefreshService>().RefreshAnime(21, new() { QuickRefresh = true }, TestContext.Current.CancellationToken);

        var document = harness.Stores.Store.GetAnime(21)!;
        Assert.Equal(document.CreatedAt, document.LastUpdatedAt);
        Assert.False(harness.Stores.People.Cast.ContainsKey(_series));
        harness.LinkingService.Verify(service => service.MatchEpisodes(It.IsAny<int>(), It.IsAny<MetadataGuid>(), It.IsAny<MetadataGuid?>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RefreshAnime_MatchesTheEpisodesOfEveryLinkedAnime_ThroughTheCore()
    {
        using var harness = new ServiceHarness().Respond(_media);
        harness.Stores.CrossReferences.AddSeries(100, 21).AddSeries(200, 21);

        await harness.Get<AnilistRefreshService>().RefreshAnime(21, new(), TestContext.Current.CancellationToken);

        foreach (var anidbAnimeID in new[] { 100, 200 })
            harness.LinkingService.Verify(service => service.MatchEpisodes(anidbAnimeID, _series, null, true, true, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RefreshAnime_DropsTheEpisodesThatWentAway_AndLeavesTheirLinksToTheCore()
    {
        using var harness = new ServiceHarness().Respond(_media);
        var gone = AnilistUtility.PackEpisodeID(21, 9);
        harness.Stores.Store.ReplaceEpisodes(21, [AnilistStoreTests.Episode(21, 9)]);
        harness.Stores.CrossReferences.AddEpisode(100, 100001, gone).AddEpisode(100, 100002, AnilistUtility.PackEpisodeID(21, 1));

        await harness.Get<AnilistRefreshService>().RefreshAnime(21, new(), TestContext.Current.CancellationToken);

        // The core's series store removes the links to the episodes a save
        // drops, so the plugin leaves them be.
        Assert.Equal(2, harness.Stores.CrossReferences.Links.Count);
        Assert.Null(harness.Stores.Store.GetEpisode(gone));
    }

    [Fact]
    public async Task QuickRefresh_KeepsTheStoredCastAndCrew()
    {
        using var harness = new ServiceHarness(Unthrottled()).Respond(_media).Respond(_media);
        var refresh = harness.Get<AnilistRefreshService>();
        await refresh.RefreshAnime(21, new(), TestContext.Current.CancellationToken);

        await refresh.RefreshAnime(21, new() { QuickRefresh = true }, TestContext.Current.CancellationToken);

        Assert.Equal(3, harness.Stores.People.Cast[_series].Count);
        Assert.Equal(2, harness.Stores.People.Crew[_series].Count);
    }

    [Fact]
    public async Task RefreshAnime_WithTheKindsOff_AsksForNoMoreCastOrCrew_AndWritesTheFirstPages()
    {
        using var harness = new ServiceHarness(Unthrottled()).Respond(MediaWithMorePeople());

        await harness.Get<AnilistRefreshService>().RefreshAnime(21, new(), TestContext.Current.CancellationToken);

        Assert.Single(harness.Http.Bodies);
        Assert.Equal(3, harness.Stores.People.Cast[_series].Count);
        Assert.Equal(2, harness.Stores.People.Crew[_series].Count);
    }

    [Fact]
    public async Task RefreshAnime_WithTheKindsOff_KeepsTheStoredCastAndCrew_OverTheFirstPages()
    {
        using var harness = new ServiceHarness(Unthrottled()).Respond(MediaWithMorePeople());
        var creator = new MetadataGuid(MetadataSource.AniList, MetadataEntityType.Creator, "1");
        harness.Stores.People.Cast[_series] = [new() { CharacterID = new MetadataGuid(MetadataSource.AniList, MetadataEntityType.Character, "1"), CreatorID = creator, Name = "Stored" }];
        harness.Stores.People.Crew[_series] = [new() { CreatorID = creator, Name = "Director" }];

        await harness.Get<AnilistRefreshService>().RefreshAnime(21, new(), TestContext.Current.CancellationToken);

        Assert.Single(harness.Http.Bodies);
        Assert.Equal("Stored", Assert.Single(harness.Stores.People.Cast[_series]).Name);
        Assert.Equal("Director", Assert.Single(harness.Stores.People.Crew[_series]).Name);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RefreshAnime_PagesThroughEachList_OnlyWhileItsKindIsOn(bool characters, bool staff)
    {
        using var harness = new ServiceHarness(Unthrottled()).Respond(MediaWithMorePeople());
        var kinds = new List<MetadataEntityType>();
        if (characters)
            kinds.Add(MetadataEntityType.Character);
        if (staff)
            kinds.Add(MetadataEntityType.Creator);
        harness.EnableKinds([.. kinds]);
        if (characters)
            harness.Respond(CharactersPage2);
        if (staff)
            harness.Respond(StaffPage2);

        await harness.Get<AnilistRefreshService>().RefreshAnime(21, new(), TestContext.Current.CancellationToken);

        Assert.Equal(1 + (characters ? 1 : 0) + (staff ? 1 : 0), harness.Http.Bodies.Count);
        Assert.Equal(characters, harness.Http.Bodies.Any(body => body.Contains("\"characterPage\":2", StringComparison.Ordinal)));
        Assert.Equal(staff, harness.Http.Bodies.Any(body => body.Contains("\"staffPage\":2", StringComparison.Ordinal)));
        Assert.Equal(characters ? 4 : 3, harness.Stores.People.Cast[_series].Count);
        Assert.Equal(staff ? 3 : 2, harness.Stores.People.Crew[_series].Count);
    }

    [Fact]
    public async Task QuickRefresh_AsksForNoMoreCastOrCrew_EvenWithTheKindsOn()
    {
        using var harness = new ServiceHarness(Unthrottled()).EnableKinds(MetadataEntityType.Character, MetadataEntityType.Creator).Respond(MediaWithMorePeople());

        await harness.Get<AnilistRefreshService>().RefreshAnime(21, new() { QuickRefresh = true }, TestContext.Current.CancellationToken);

        Assert.Single(harness.Http.Bodies);
        Assert.False(harness.Stores.People.Cast.ContainsKey(_series));
    }

    [Fact]
    public async Task RefreshAnime_KeepsWhatIsStored_WhenAnilistHasNoSuchAnime()
    {
        using var harness = new ServiceHarness().Respond("""{ "errors": [ { "message": "Not Found.", "status": 404 } ], "data": { "Media": null } }""");
        harness.Stores.Series.SaveSeries(new() { ID = _series });

        Assert.False(await harness.Get<AnilistRefreshService>().RefreshAnime(21, new(), TestContext.Current.CancellationToken));
        Assert.True(harness.Stores.Series.Series.ContainsKey(_series));
        Assert.Equal(1, harness.Stores.Series.Saves);
    }

    [Fact]
    public async Task RefreshAnime_OnAServerError_TripsTheBreaker_AndThrowsTransiently()
    {
        using var harness = new ServiceHarness().Respond("oops", HttpStatusCode.BadGateway);

        var exception = await Assert.ThrowsAsync<AnilistUnavailableException>(() => harness.Get<AnilistRefreshService>().RefreshAnime(21, new(), TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
        Assert.True(harness.RateLimiter.IsPaused);
        Assert.Empty(harness.Stores.Series.Series);
    }
}
