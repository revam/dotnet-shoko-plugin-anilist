using System.Text.Json.Nodes;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Storage;
using Shoko.Plugin.Anilist.Mapping;
using Shoko.Plugin.Anilist.Metadata;
using Shoko.Plugin.Anilist.Storage;
using Xunit;

namespace Shoko.Plugin.Anilist.Tests;

[Collection(nameof(ImageServerCollection))]
public class AnilistMediaMapperTests
{
    private static JsonNode Media() => JsonNode.Parse(Fixture.Read("media-21.json"))!["data"]!["Media"]!;

    [Fact]
    public void ReadMedia_ReadsEveryField()
    {
        var anime = AnilistMediaMapper.ReadMedia(Media());

        Assert.Equal(21, anime.ID);
        Assert.Equal("ONE PIECE", anime.EnglishTitle);
        Assert.Equal("ONE PIECE", anime.MainTitle);
        Assert.Equal(["OP", "ワンピース"], anime.Synonyms);
        Assert.Equal("Gold Roger was known as the Pirate King.\n\nNow Monkey D. Luffy & his crew sail.", anime.EnglishOverview);
        Assert.Equal("ja", anime.OriginalLanguageCode);
        Assert.Equal(AnimeType.TV, anime.Type);
        Assert.Equal(ReleaseStatus.Releasing, anime.Status);
        Assert.Equal(SourceMaterial.Manga, anime.Source);
        Assert.Equal(YearlySeason.Fall, anime.Season);
        Assert.Equal(1999, anime.SeasonYear);
        Assert.Equal(0, anime.EpisodeCount);
        Assert.Equal(24, anime.EpisodeDuration);
        Assert.Equal(88, anime.AverageScore);
        Assert.Equal(89, anime.MeanScore);
        Assert.Equal(150, anime.ScoreVotes);
        Assert.Equal(600000, anime.Popularity);
        Assert.Equal(90000, anime.FavoriteCount);
        Assert.Equal(21, anime.MalID);
        Assert.Equal(["Action", "Adventure"], anime.Genres);
        Assert.Equal("media/anime/cover/large/bx21-YCDoj1EkAxFn.jpg", anime.CoverImagePath);
        Assert.Equal("media/anime/banner/21-wf37VakJmZqs.jpg", anime.BannerImagePath);
        Assert.Equal(new PartialDateOnly(1999, 10, 20), anime.StartDate);
        Assert.Null(anime.EndDate);
        Assert.Equal("youtube", anime.TrailerSite);
        Assert.Equal(2, anime.ExternalLinks.Count);
        Assert.Equal("en", anime.ExternalLinks[0].LanguageCode);
        Assert.Null(anime.ExternalLinks[1].LanguageCode);
    }

    [Theory]
    [InlineData("media-21.json", SourceMaterial.Manga)]
    [InlineData("media-source-manhwa.json", SourceMaterial.Manhwa)]
    [InlineData("media-source-manhua.json", SourceMaterial.Manhua)]
    [InlineData("media-source-adult-visual-novel.json", SourceMaterial.Eroge)]
    [InlineData("media-source-no-relation.json", SourceMaterial.Manhwa)]
    public void ReadMedia_TellsComicsAndErogeApart(string fixture, SourceMaterial expected)
    {
        var media = JsonNode.Parse(Fixture.Read(fixture))!["data"]!["Media"]!;

        Assert.Equal(expected, AnilistMediaMapper.ReadMedia(media).Source);
    }

    [Fact]
    public void ReadSourceComicCountry_ReadsTheSourceComic_NotTheAnime()
    {
        var manhwa = JsonNode.Parse(Fixture.Read("media-source-manhwa.json"))!["data"]!["Media"]!;
        var noRelation = JsonNode.Parse(Fixture.Read("media-source-no-relation.json"))!["data"]!["Media"]!;

        Assert.Equal("KR", AnilistMediaMapper.ReadSourceComicCountry(manhwa));
        Assert.Null(AnilistMediaMapper.ReadSourceComicCountry(noRelation));
    }

    [Fact]
    public void ReadSourceComicCountry_FallsBackToAnAdaptation()
    {
        var media = JsonNode.Parse("""
            { "relations": { "edges": [
              { "relationType": "SIDE_STORY", "node": { "id": 1, "type": "MANGA", "format": "MANGA", "countryOfOrigin": "JP" } },
              { "relationType": "ADAPTATION", "node": { "id": 2, "type": "MANGA", "format": "ONE_SHOT", "countryOfOrigin": "CN" } },
              { "relationType": "SOURCE", "node": { "id": 3, "type": "MANGA", "format": "NOVEL", "countryOfOrigin": "KR" } }
            ] } }
            """)!;

        Assert.Equal("CN", AnilistMediaMapper.ReadSourceComicCountry(media));
    }

    [Fact]
    public void ToSeriesData_NamesEverythingUnderTheAnilistSource()
    {
        var anime = AnilistMediaMapper.ReadMedia(Media());
        var (episodes, _) = AnilistMediaMapper.BuildEpisodes(anime, new Dictionary<int, AnilistScheduleEntry> { [1] = new(1001, null) }, [], DateTime.UnixEpoch);

        var series = AnilistMediaMapper.ToSeriesData(anime, episodes);

        Assert.Equal("anilist://series/21", series.ID.ToString());
        Assert.All(series.Titles, title => Assert.Equal(MetadataSource.AniList, title.Source));
        Assert.Equal(TitleLanguage.Romaji, series.Titles[0].Language);
        Assert.Equal("en", series.Titles[2].LanguageCode);
        Assert.Equal("unk", series.Titles[3].LanguageCode);
        Assert.Equal(8.8, series.Rating, 3);
        Assert.Equal("anilist://episode/86017", Assert.Single(series.Episodes).ID.ToString());
        Assert.Null(series.Episodes[0].SeasonID);
        Assert.Empty(series.Seasons);
        Assert.Equal(["mal://series/21"], series.CrossSourceIDs.Select(id => id.ToString()));
        Assert.Empty(series.Episodes[0].CrossSourceIDs);
        Assert.Equal("media/anime/cover/large/bx21-YCDoj1EkAxFn.jpg", series.DefaultImageResourceIDs![ImageEntityType.Primary]);
        Assert.Equal("media/anime/banner/21-wf37VakJmZqs.jpg", series.DefaultImageResourceIDs[ImageEntityType.Banner]);
    }

    [Fact]
    public void CrossSourceIDs_AreEmpty_WithoutAMyAnimeListID()
        => Assert.Empty(AnilistMediaMapper.CrossSourceIDs(new AnilistMedia { ID = 21 }));

    [Fact]
    public void ToStoredAnime_KeepsOnlyWhatAnilistAlone_Has()
    {
        var anime = AnilistMediaMapper.ReadMedia(Media());
        var created = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var fresh = AnilistMediaMapper.ToStoredAnime(anime, null, now, quick: true);
        var full = AnilistMediaMapper.ToStoredAnime(anime, new() { ID = 21, CreatedAt = created, LastUpdatedAt = created }, now, quick: false);

        Assert.Equal((now, now), (fresh.CreatedAt, fresh.LastUpdatedAt));
        Assert.Equal((created, now), (full.CreatedAt, full.LastUpdatedAt));
        Assert.Equal(YearlySeason.Fall, full.Season);
        Assert.Equal(89, full.MeanScore);
        Assert.True(full.IsLicensed);
        Assert.Equal("#e4a15d", full.Color);
    }

    [Theory]
    [InlineData("https://www.crunchyroll.com/series/GRMG8ZQZR/one-piece", "GRMG8ZQZR")]
    [InlineData("https://www.netflix.com/title/80107103", "80107103")]
    [InlineData("https://www.hidive.com/tv/one-piece", "one-piece")]
    [InlineData("https://www.amazon.com/gp/video/detail/B0ABCDEFGH/ref=x", "B0ABCDEFGH")]
    [InlineData("https://twitter.com/opanimation", "opanimation")]
    [InlineData("https://www.youtube.com/@ONEPIECEofficial", "ONEPIECEofficial")]
    [InlineData("https://www.bilibili.com/bangumi/media/md28229002", "28229002")]
    [InlineData("https://www.toei-anim.co.jp/tv/onep/", null)]
    [InlineData("https://twitter.com/opanimation/status/1", null)]
    [InlineData(null, null)]
    public void ReadExternalID_ReadsTheSitesOwnID(string? url, string? expected)
        => Assert.Equal(expected, AnilistMediaMapper.ReadExternalID(url));

    [Fact]
    public void BuildEpisodes_CoversTheLastScheduledEpisode_WhenTheCountIsUnknown()
    {
        var anime = new AnilistMedia { ID = 21, EpisodeDuration = 24 };
        var schedule = new Dictionary<int, AnilistScheduleEntry>();
        AnilistMediaMapper.ReadSchedulePage(Media()["airingSchedule"], schedule);

        var (episodes, changed) = AnilistMediaMapper.BuildEpisodes(anime, schedule, [], DateTime.UnixEpoch);

        Assert.True(changed);
        Assert.Equal([1, 2, 3], episodes.Select(episode => episode.EpisodeNumber));
        Assert.Equal(AnilistUtility.PackEpisodeID(21, 2), episodes[1].ID);
        Assert.Equal(1002, episodes[1].ScheduleID);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(940982400).UtcDateTime, episodes[1].AiredAt);
        Assert.All(episodes, episode => Assert.Equal(24, episode.RuntimeMinutes));
        Assert.Equal(DateTimeKind.Utc, AnilistMediaMapper.ToEpisodeData(episodes[1]).AirDateWithTime?.Kind);
    }

    [Fact]
    public void BuildEpisodes_DropsEpisodesPastTheCount_AndKeepsUnchangedRows()
    {
        var anime = new AnilistMedia { ID = 5, EpisodeCount = 2 };
        var kept = new AnilistStoredEpisode { ID = AnilistUtility.PackEpisodeID(5, 1), AnimeID = 5, EpisodeNumber = 1, CreatedAt = DateTime.UnixEpoch, LastUpdatedAt = DateTime.UnixEpoch };
        var extra = new AnilistStoredEpisode { ID = AnilistUtility.PackEpisodeID(5, 3), AnimeID = 5, EpisodeNumber = 3 };

        var (episodes, changed) = AnilistMediaMapper.BuildEpisodes(anime, new Dictionary<int, AnilistScheduleEntry>(), [kept, extra], DateTime.UtcNow);

        Assert.True(changed);
        Assert.Equal([1, 2], episodes.Select(episode => episode.EpisodeNumber));
        Assert.Same(kept, episodes[0]);
    }

    [Fact]
    public void ReadTags_TakesTheTags_AndGenresBecomeGenreTags()
    {
        var (tags, entries) = AnilistMediaMapper.ReadTags(Media());

        Assert.Equal(["anilist://tag/94", "anilist://tag/150"], tags.Select(tag => tag.ID.ToString()));
        Assert.All(tags, tag => Assert.Equal(TagKind.Tag, tag.Kind));
        Assert.Equal("Theme-Other", tags[0].Category);
        Assert.True(tags[1].IsSpoiler);
        Assert.Equal([96, 70], entries.Select(entry => entry.Weight));
        Assert.True(entries[1].IsSpoiler);

        var genres = AnilistMediaMapper.GenresAsTags(["Action", "Slice of Life", "action", " "]);
        Assert.All(genres, genre => Assert.Equal(TagKind.Genre, genre.Kind));
        Assert.Equal(["anilist://tag/Action", "anilist://tag/Slice of Life"], genres.Select(genre => genre.ID.ToString()));
        Assert.All(AnilistMediaMapper.GenreEntries(["Action"]), entry => Assert.Null(entry.Weight));
    }

    [Fact]
    public void ReadStudios_PutsTheMainStudioFirst_AndTellsAnimatorsFromProducers()
    {
        var (studios, entries) = AnilistMediaMapper.ReadStudios(Media());

        Assert.Equal(["18", "16"], studios.Select(studio => studio.ID.ID));
        Assert.Equal([StudioType.Animation, StudioType.Production], entries.Select(studio => studio.Type));
        Assert.Equal(studios.Select(studio => studio.ID), entries.Select(entry => entry.StudioID));
    }

    [Fact]
    public void ReadRelations_KeepsOnlyAnime()
    {
        var relations = AnilistMediaMapper.ReadRelations(Media());

        Assert.Equal(
            [
                new MetadataRelationData { RelatedID = AnilistUtility.SeriesGuid(459), RelationType = RelationType.SideStory },
                new MetadataRelationData { RelatedID = AnilistUtility.SeriesGuid(466), RelationType = RelationType.Prequel },
            ],
            relations
        );
    }

    [Fact]
    public void ReadRecommendationPage_SkipsMangaAndDeletedEntries_AndKeepsTheScore()
    {
        var suggestions = new List<MetadataSuggestionData>();

        var lowest = AnilistMediaMapper.ReadRecommendationPage(Media()["recommendations"], 21, suggestions);

        Assert.Equal(-3, lowest);
        Assert.Equal(["1735", "6702"], suggestions.Select(suggestion => suggestion.SuggestedID.ID));
        Assert.Equal([0, 1], suggestions.Select(suggestion => suggestion.Order!.Value));
        Assert.Equal([500, -3], suggestions.Select(suggestion => suggestion.Score!.Value));
        Assert.All(suggestions, suggestion => Assert.Equal(SuggestionKind.Recommended, suggestion.Kind));
        Assert.All(suggestions, suggestion => Assert.Null(suggestion.Votes));
    }

    [Fact]
    public void ReadCharacterPage_CreditsEveryVoiceActor_AndKeepsTheUnvoicedCharacter()
    {
        var people = new AnilistPeople();

        AnilistMediaMapper.ReadCharacterPage(Media()["characters"], people);

        Assert.Equal([40, 41], people.Characters.Keys.Order());
        Assert.Equal([95011, 95012], people.Creators.Keys.Order());
        Assert.Equal(3, people.Cast.Count);
        Assert.Equal("ja", people.Cast[0].LanguageCode);
        Assert.Equal("en", people.Cast[1].LanguageCode);
        Assert.Equal("(young)", people.Cast[1].RoleNotes);
        Assert.Equal("Funimation", people.Cast[1].DubGroup);
        Assert.Null(people.Cast[2].CreatorID);
        Assert.Equal(CastRoleType.BackgroundCharacter, people.Cast[2].RoleType);
        Assert.Equal("Captain of the Straw Hat Pirates.", people.Characters[40].Overview);
        Assert.Equal(new FuzzyDateOnly(1955, 1, 15), people.Creators[95011].BirthDay);
        Assert.Equal(new FuzzyDateOnly(null, 6, 13), people.Creators[95012].BirthDay);
        Assert.Equal(new FuzzyDateOnly(null, 5, 5), people.Characters[40].BirthDay);
        Assert.Null(people.Characters[41].BirthDay);
        Assert.Equal(PersonGender.Female, people.Creators[95012].Gender);
        Assert.Contains(AnilistUtility.Guid(MetadataEntityType.Character, 40), people.Portraits.Keys);
        Assert.DoesNotContain(AnilistUtility.Guid(MetadataEntityType.Character, 41), people.Portraits.Keys);
        Assert.Equal("character/large/b40-Bn6Bmf3LLIf3.png", people.Characters[40].DefaultImageResourceIDs![ImageEntityType.Primary]);
        Assert.Null(people.Characters[41].DefaultImageResourceIDs);
        Assert.Equal(AnilistUtility.Guid(MetadataEntityType.Character, 40), people.Cast[0].CharacterID);
        Assert.Equal(CastRoleType.MainCharacter, people.Cast[0].RoleType);
    }

    [Fact]
    public void ReadStaffPage_SplitsTheDubLanguageOffTheRole()
    {
        var people = new AnilistPeople();

        AnilistMediaMapper.ReadStaffPage(Media()["staff"], people, "ja");

        var crew = people.Crew;
        Assert.Equal(CrewRoleType.SourceWork, crew[0].RoleType);
        Assert.Equal("ja", crew[0].LanguageCode);
        Assert.Equal("ADR Director", crew[1].Name);
        Assert.Equal(CrewRoleType.Director, crew[1].RoleType);
        Assert.Equal("en", crew[1].LanguageCode);
    }

    [Fact]
    public void ReadStaffPage_KeepsOnePersonsSameJobInTwoLanguages()
    {
        var page = JsonNode.Parse("""
            { "edges": [
              { "role": "ADR Director (English)", "node": { "id": 7, "name": { "full": "A" } } },
              { "role": "ADR Director (Spanish)", "node": { "id": 7, "name": { "full": "A" } } },
              { "role": "ADR Director (Spanish)", "node": { "id": 7, "name": { "full": "A" } } }
            ] }
            """);
        var people = new AnilistPeople();

        AnilistMediaMapper.ReadStaffPage(page, people, "ja");

        Assert.Equal(["ADR Director", "ADR Director (Spanish)"], people.Crew.Select(crew => crew.Name));
    }

    [Fact]
    public void ReadStaffPage_ReadsTheDateOfDeath_FromWhateverPartsAreKnown()
    {
        var page = JsonNode.Parse("""
            { "edges": [
              { "role": "Director", "node": { "id": 1, "name": { "full": "A" }, "dateOfDeath": { "year": 2021, "month": 4, "day": 9 } } },
              { "role": "Director", "node": { "id": 2, "name": { "full": "B" }, "dateOfDeath": { "year": 2021, "month": null, "day": null } } },
              { "role": "Director", "node": { "id": 3, "name": { "full": "C" }, "dateOfDeath": { "year": null, "month": 4, "day": 9 } } },
              { "role": "Director", "node": { "id": 4, "name": { "full": "D" }, "dateOfDeath": { "year": null, "month": null, "day": null } } },
              { "role": "Director", "node": { "id": 5, "name": { "full": "E" } } }
            ] }
            """);
        var people = new AnilistPeople();

        AnilistMediaMapper.ReadStaffPage(page, people, "ja");

        Assert.Equal(new FuzzyDateOnly(2021, 4, 9), people.Creators[1].DeathDay);
        Assert.Equal(new FuzzyDateOnly(2021, null, null), people.Creators[2].DeathDay);
        Assert.Equal(new FuzzyDateOnly(null, 4, 9), people.Creators[3].DeathDay);
        Assert.Null(people.Creators[4].DeathDay);
        Assert.Null(people.Creators[5].DeathDay);
    }

    [Theory]
    [InlineData("Female", PersonGender.Female)]
    [InlineData("male", PersonGender.Male)]
    [InlineData("Non-binary", PersonGender.NonBinary)]
    [InlineData("Other", PersonGender.Unknown)]
    [InlineData(null, PersonGender.Unknown)]
    public void ParseGender_ReadsAnilistsWording(string? gender, PersonGender expected)
        => Assert.Equal(expected, AnilistMediaMapper.ParseGender(gender));

    [Theory]
    [InlineData(2020, 2, 30, "2020-02")]
    [InlineData(2020, 13, 1, "2020")]
    [InlineData(2020, null, 5, "2020")]
    [InlineData(null, 1, 1, null)]
    [InlineData(2020, 2, 29, "2020-02-29")]
    public void ToPartialDate_DropsWhatIsOutOfRange(int? year, int? month, int? day, string? expected)
        => Assert.Equal(expected, AnilistMediaMapper.ToPartialDate((year, month, day)) is { } date ? date.ToString() : null);

    [Theory]
    [InlineData(1955, 1, 15, "1955-01-15")]
    [InlineData(1955, 1, null, "1955-01")]
    [InlineData(1955, null, null, "1955")]
    [InlineData(null, 6, 13, "--06-13")]
    [InlineData(null, 6, null, "--06")]
    [InlineData(null, 2, 29, "--02-29")]
    [InlineData(2021, 2, 29, "2021-02")]
    [InlineData(null, null, 13, null)]
    [InlineData(2020, null, 5, "2020")]
    [InlineData(null, 13, 1, null)]
    [InlineData(0, 6, 13, "--06-13")]
    [InlineData(null, null, null, null)]
    public void ToFuzzyDate_KeepsEveryUsablePart(int? year, int? month, int? day, string? expected)
        => Assert.Equal(expected, AnilistMediaMapper.ToFuzzyDate((year, month, day)) is { } date ? date.ToString() : null);

    [Fact]
    public void ReadDateParts_KeepsAYearlessDate()
    {
        var node = JsonNode.Parse("""{ "year": null, "month": 5, "day": 5 }""");

        Assert.Equal((null, 5, 5), AnilistMediaMapper.ReadDateParts(node));
        Assert.Equal((null, null, null), AnilistMediaMapper.ReadDate(node));
        Assert.Equal((null, null, null), AnilistMediaMapper.ReadDateParts(null));
    }
}
