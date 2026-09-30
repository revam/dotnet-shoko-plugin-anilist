using Shoko.Abstractions.Metadata.Enums;
using Shoko.Plugin.Anilist.Mapping;
using Xunit;

namespace Shoko.Plugin.Anilist.Tests;

public class AnilistUtilityTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(21, 1150)]
    [InlineData(200_000, 4095)]
    [InlineData(AnilistUtility.MaxAnimeID, AnilistUtility.MaxEpisodeNumber)]
    public void PackEpisodeID_RoundTrips_AndStaysPositive(int animeID, int episodeNumber)
    {
        var packed = AnilistUtility.PackEpisodeID(animeID, episodeNumber);

        Assert.True(packed > 0, $"Expected a positive id, got {packed}");
        Assert.Equal((animeID, episodeNumber), AnilistUtility.UnpackEpisodeID(packed));
    }

    [Fact]
    public void PackEpisodeID_LargestPair_IsIntMaxValue()
        => Assert.Equal(int.MaxValue, AnilistUtility.PackEpisodeID(AnilistUtility.MaxAnimeID, AnilistUtility.MaxEpisodeNumber));

    [Fact]
    public void PackEpisodeID_OrdersByAnimeThenEpisode()
    {
        Assert.True(AnilistUtility.PackEpisodeID(21, 2) > AnilistUtility.PackEpisodeID(21, 1));
        Assert.True(AnilistUtility.PackEpisodeID(22, 1) > AnilistUtility.PackEpisodeID(21, AnilistUtility.MaxEpisodeNumber));
    }

    [Theory]
    [InlineData(AnilistUtility.MaxAnimeID + 1, 1)]
    [InlineData(0, 1)]
    [InlineData(1, AnilistUtility.MaxEpisodeNumber + 1)]
    [InlineData(1, -1)]
    public void PackEpisodeID_Throws_OutsideRange(int animeID, int episodeNumber)
    {
        Assert.False(AnilistUtility.CanPackEpisodeID(animeID, episodeNumber));
        Assert.Throws<ArgumentOutOfRangeException>(() => AnilistUtility.PackEpisodeID(animeID, episodeNumber));
    }

    [Theory]
    [InlineData("TV", AnimeType.TV)]
    [InlineData("tv_short", AnimeType.TVShort)]
    [InlineData("MOVIE", AnimeType.Movie)]
    [InlineData("ONA", AnimeType.Web)]
    [InlineData("SOMETHING_NEW", AnimeType.Unknown)]
    [InlineData(null, AnimeType.Unknown)]
    public void ParseFormat_MapsKnownValues_AndFallsBackToUnknown(string? format, AnimeType expected)
        => Assert.Equal(expected, AnilistUtility.ParseFormat(format));

    [Theory]
    [InlineData("RELEASING", ReleaseStatus.Releasing)]
    [InlineData("not_yet_released", ReleaseStatus.NotYetReleased)]
    [InlineData("HIATUS", ReleaseStatus.Hiatus)]
    [InlineData("SOMETHING_NEW", ReleaseStatus.Unknown)]
    [InlineData(null, ReleaseStatus.Unknown)]
    public void ParseReleaseStatus_MapsKnownValues_AndFallsBackToUnknown(string? status, ReleaseStatus expected)
        => Assert.Equal(expected, AnilistUtility.ParseReleaseStatus(status));

    [Theory]
    [InlineData("LIGHT_NOVEL", SourceMaterial.LightNovel)]
    [InlineData("PICTURE_BOOK", SourceMaterial.PictureBook)]
    [InlineData("SOMETHING_NEW", SourceMaterial.Other)]
    [InlineData(null, SourceMaterial.Unknown)]
    public void ParseSourceMaterial_MapsKnownValues_UnknownStringsBecomeOther(string? source, SourceMaterial expected)
        => Assert.Equal(expected, AnilistUtility.ParseSourceMaterial(source));

    [Theory]
    [InlineData("MANGA", false, "KR", "JP", SourceMaterial.Manhwa)]
    [InlineData("MANGA", false, "CN", "JP", SourceMaterial.Manhua)]
    [InlineData("COMIC", false, "TW", null, SourceMaterial.Manhua)]
    [InlineData("MANGA", false, "JP", "KR", SourceMaterial.Manga)]
    [InlineData("COMIC", false, "US", "JP", SourceMaterial.Comic)]
    [InlineData("COMIC", false, "jp", null, SourceMaterial.Manga)]
    [InlineData("MANGA", false, null, "KR", SourceMaterial.Manhwa)]
    [InlineData("COMIC", false, "", "CN", SourceMaterial.Manhua)]
    [InlineData("MANGA", false, null, null, SourceMaterial.Manga)]
    [InlineData("COMIC", false, null, null, SourceMaterial.Comic)]
    [InlineData("VISUAL_NOVEL", true, null, "JP", SourceMaterial.Eroge)]
    [InlineData("VIDEO_GAME", true, null, "JP", SourceMaterial.Eroge)]
    [InlineData("VISUAL_NOVEL", false, null, "JP", SourceMaterial.VisualNovel)]
    [InlineData("VIDEO_GAME", false, null, "JP", SourceMaterial.VideoGame)]
    [InlineData("LIGHT_NOVEL", true, "KR", "KR", SourceMaterial.LightNovel)]
    [InlineData(null, false, "KR", "KR", SourceMaterial.Unknown)]
    public void InferSourceMaterial_SplitsComicsByCountry_AndAdultGamesOff(
        string? source,
        bool isAdult,
        string? sourceCountry,
        string? animeCountry,
        SourceMaterial expected
    )
        => Assert.Equal(expected, AnilistUtility.InferSourceMaterial(source, isAdult, sourceCountry, animeCountry));

    [Theory]
    [InlineData("FALL", YearlySeason.Fall)]
    [InlineData("winter", YearlySeason.Winter)]
    [InlineData(null, null)]
    [InlineData("MONSOON", null)]
    public void ParseSeason_RoundTripsWithToSeason(string? season, YearlySeason? expected)
    {
        Assert.Equal(expected, AnilistUtility.ParseSeason(season));
        if (expected is { } value)
            Assert.Equal(season!.ToUpperInvariant(), AnilistUtility.ToSeason(value));
    }

    [Theory]
    [InlineData("PREQUEL", RelationType.Prequel)]
    [InlineData("SEQUEL", RelationType.Sequel)]
    [InlineData("PARENT", RelationType.MainStory)]
    [InlineData("SPIN_OFF", RelationType.SideStory)]
    [InlineData("COMPILATION", RelationType.Summary)]
    [InlineData("SAME_UNIVERSE", RelationType.SameSetting)]
    [InlineData("CONTAINS", RelationType.Other)]
    [InlineData(null, RelationType.Other)]
    public void ParseRelationType_MapsAnilistRelations(string? relation, RelationType expected)
        => Assert.Equal(expected, AnilistUtility.ParseRelationType(relation));

    [Theory]
    [InlineData("Original Creator", CrewRoleType.SourceWork)]
    [InlineData("Character Design", CrewRoleType.CharacterDesign)]
    [InlineData("Series Composition", CrewRoleType.SeriesComposer)]
    [InlineData("Theme Song Performance (OP)", CrewRoleType.Music)]
    [InlineData("Director (eps 1-12)", CrewRoleType.Director)]
    [InlineData("Key Animation", CrewRoleType.None)]
    [InlineData("", CrewRoleType.None)]
    public void ParseCrewRoleType_KeysOffTheWordsInTheRole(string role, CrewRoleType expected)
        => Assert.Equal(expected, AnilistUtility.ParseCrewRoleType(role));

    [Theory]
    [InlineData("ADR Director (English)", "ADR Director", TitleLanguage.English)]
    [InlineData("ADR Director (Brazilian Portuguese)", "ADR Director", TitleLanguage.BrazilianPortuguese)]
    [InlineData("ADR Script (Spanish)", "ADR Script", TitleLanguage.Spanish)]
    [InlineData("Theme Song Performance (ED)", "Theme Song Performance (ED)", null)]
    [InlineData("2nd Key Animation (eps 2, 8)", "2nd Key Animation (eps 2, 8)", null)]
    [InlineData("Director", "Director", null)]
    [InlineData("", "", null)]
    [InlineData(null, "", null)]
    public void SplitRoleLanguage_StripsOnlyLanguageQualifiers(string? role, string expectedRole, TitleLanguage? expectedLanguage)
        => Assert.Equal((expectedRole, expectedLanguage), AnilistUtility.SplitRoleLanguage(role));

    [Theory]
    [InlineData("ja", TitleLanguage.Romaji)]
    [InlineData("JA", TitleLanguage.Romaji)]
    [InlineData("zh", TitleLanguage.Pinyin)]
    [InlineData("zh-TW", TitleLanguage.Pinyin)]
    [InlineData("ko", TitleLanguage.KoreanTranscription)]
    [InlineData("th", TitleLanguage.ThaiTranscription)]
    [InlineData("", TitleLanguage.Romaji)]
    [InlineData(null, TitleLanguage.Romaji)]
    public void GetMainTitleLanguage_FollowsTheOriginalLanguage(string? originalLanguageCode, TitleLanguage expected)
        => Assert.Equal(expected, AnilistUtility.GetMainTitleLanguage(originalLanguageCode));

    [Theory]
    [InlineData("JP", "ja")]
    [InlineData(null, "ja")]
    [InlineData("TW", "zh-TW")]
    [InlineData("KR", "ko")]
    public void ParseOriginalLanguage_MapsTheCountryToALanguage(string? country, string expected)
        => Assert.Equal(expected, AnilistUtility.ParseOriginalLanguage(country));

    [Fact]
    public void StripHtml_ConvertsBreaks_DropsTags_DecodesEntities_AndCollapsesBlankLines()
        => Assert.Equal("First line.\nSecond & third.\n\nAfter gap.", AnilistUtility.StripHtml("First line.<br>Second &amp; <i>third</i>.<br><br><br>\n\n\nAfter gap."));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void StripHtml_EmptyInput_ReturnsEmpty(string? html)
        => Assert.Equal(string.Empty, AnilistUtility.StripHtml(html));

    [Theory]
    [InlineData("Shingeki no Kyojin: The Final Season", "shingeki no kyojin the final season")]
    [InlineData("  Café  ~ Latte ~ ", "cafe latte")]
    [InlineData(null, "")]
    public void NormalizeForIndex_FoldsCaseAccentsAndPunctuation(string? input, string expected)
        => Assert.Equal(expected, AnilistUtility.NormalizeForIndex(input));

    [Theory]
    [InlineData("21", true, 21)]
    [InlineData("0", false, 0)]
    [InlineData("-3", false, 0)]
    [InlineData("abc", false, 0)]
    [InlineData(null, false, 0)]
    public void TryParseID_OnlyAcceptsPositiveIntegers(string? input, bool expected, int expectedID)
    {
        Assert.Equal(expected, AnilistUtility.TryParseID(input, out var id));
        Assert.Equal(expectedID, expected ? id : 0);
    }
}
