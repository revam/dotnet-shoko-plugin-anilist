using System;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Shoko.Abstractions.Extensions;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Plugin.Anilist.Metadata;

namespace Shoko.Plugin.Anilist.Mapping;

/// <summary>
/// The small translations every part of the plugin shares: AniList's enum
/// strings into Shoko's enums, the synthesized episode IDs, and text cleanup.
/// </summary>
public static partial class AnilistUtility
{
    #region Regexes

    [GeneratedRegex(@"<br\s*/?>|</p>|</div>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTagRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"[ \t]*\r?\n[ \t]*(?:\r?\n[ \t]*)+")]
    private static partial Regex BlankLinesRegex();

    [GeneratedRegex(@"^(?<role>.*?)\s*\((?<qualifier>[^()]+)\)\s*$")]
    private static partial Regex RoleQualifierRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    #endregion

    #region Site URLs

    /// <summary>
    /// The site every AniList page is under.
    /// </summary>
    public const string SiteUrl = "https://anilist.co";

    /// <summary>
    /// An anime's page on AniList.
    /// </summary>
    /// <param name="anilistAnimeID">The AniList anime ID.</param>
    /// <returns>The URL.</returns>
    public static string AnimeUrl(int anilistAnimeID)
        => $"{SiteUrl}/anime/{FormatID(anilistAnimeID)}";

    /// <summary>
    /// A staff member's page on AniList.
    /// </summary>
    /// <param name="anilistStaffID">The AniList staff ID.</param>
    /// <returns>The URL.</returns>
    public static string StaffUrl(int anilistStaffID)
        => $"{SiteUrl}/staff/{FormatID(anilistStaffID)}";

    /// <summary>
    /// A character's page on AniList.
    /// </summary>
    /// <param name="anilistCharacterID">The AniList character ID.</param>
    /// <returns>The URL.</returns>
    public static string CharacterUrl(int anilistCharacterID)
        => $"{SiteUrl}/character/{FormatID(anilistCharacterID)}";

    /// <summary>
    /// A studio's page on AniList.
    /// </summary>
    /// <param name="anilistStudioID">The AniList studio ID.</param>
    /// <returns>The URL.</returns>
    public static string StudioUrl(int anilistStudioID)
        => $"{SiteUrl}/studio/{FormatID(anilistStudioID)}";

    #endregion

    #region Episode IDs

    /// <summary>
    /// Bits reserved for the episode number in a synthesized episode ID,
    /// leaving 19 bits for the anime ID, which fills the positive
    /// <see cref="int"/> range exactly.
    /// </summary>
    private const int EpisodeBits = 12;

    /// <summary>
    /// The highest AniList anime ID a synthesized episode ID can hold.
    /// </summary>
    public const int MaxAnimeID = (1 << (31 - EpisodeBits)) - 1;

    /// <summary>
    /// The highest episode number a synthesized episode ID can hold.
    /// </summary>
    public const int MaxEpisodeNumber = (1 << EpisodeBits) - 1;

    /// <summary>
    /// Whether an anime ID and episode number fit in a synthesized episode ID.
    /// </summary>
    /// <param name="anilistAnimeID">The AniList anime ID.</param>
    /// <param name="episodeNumber">The episode number.</param>
    /// <returns><see langword="true"/> when the pair can be packed.</returns>
    public static bool CanPackEpisodeID(int anilistAnimeID, int episodeNumber)
        => anilistAnimeID is > 0 and <= MaxAnimeID && episodeNumber is >= 0 and <= MaxEpisodeNumber;

    /// <summary>
    /// Builds a stable episode ID out of an anime ID and an episode number.
    /// AniList has no episode entity, so the ID is a bijection of the pair.
    /// </summary>
    /// <param name="anilistAnimeID">The AniList anime ID, at most <see cref="MaxAnimeID"/>.</param>
    /// <param name="episodeNumber">The episode number, at most <see cref="MaxEpisodeNumber"/>.</param>
    /// <returns>The synthesized episode ID.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Either value is outside the packable range.</exception>
    public static int PackEpisodeID(int anilistAnimeID, int episodeNumber)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(anilistAnimeID);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(anilistAnimeID, MaxAnimeID);
        ArgumentOutOfRangeException.ThrowIfNegative(episodeNumber);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(episodeNumber, MaxEpisodeNumber);
        return (anilistAnimeID << EpisodeBits) | episodeNumber;
    }

    /// <summary>
    /// Takes a synthesized episode ID apart again.
    /// </summary>
    /// <param name="anilistEpisodeID">The synthesized episode ID.</param>
    /// <returns>The anime ID and the episode number.</returns>
    public static (int AnilistAnimeID, int EpisodeNumber) UnpackEpisodeID(int anilistEpisodeID)
        => (anilistEpisodeID >> EpisodeBits, anilistEpisodeID & MaxEpisodeNumber);

    #endregion

    #region IDs

    /// <summary>
    /// Formats an AniList ID the one way every ID in this plugin is formatted.
    /// </summary>
    /// <param name="id">The ID.</param>
    /// <returns>The ID as the contract wants it.</returns>
    public static string FormatID(int id)
        => id.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads an AniList ID back out of the string form the contract hands
    /// around.
    /// </summary>
    /// <param name="providerID">The ID.</param>
    /// <param name="id">The parsed ID, or zero.</param>
    /// <returns>Whether it parsed into a usable ID.</returns>
    public static bool TryParseID(string? providerID, out int id)
        => int.TryParse(providerID, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;

    /// <summary>
    /// The identifier of an AniList entry of one kind.
    /// </summary>
    /// <param name="entityType">The kind of entry.</param>
    /// <param name="id">AniList's ID for it, or the packed ID of an episode.</param>
    /// <returns>The identifier, e.g. <c>anilist://creator/95</c>.</returns>
    public static MetadataGuid Guid(MetadataEntityType entityType, int id)
        => new(MetadataSource.AniList, entityType, FormatID(id));

    /// <summary>
    /// The identifier of an AniList anime.
    /// </summary>
    /// <param name="anilistAnimeID">The AniList anime ID.</param>
    /// <returns>The identifier, e.g. <c>anilist://series/21</c>.</returns>
    public static MetadataGuid SeriesGuid(int anilistAnimeID)
        => Guid(MetadataEntityType.Series, anilistAnimeID);

    /// <summary>
    /// The identifier of an AniList episode.
    /// </summary>
    /// <param name="anilistEpisodeID">The packed episode ID.</param>
    /// <returns>The identifier, e.g. <c>anilist://episode/86017</c>.</returns>
    public static MetadataGuid EpisodeGuid(int anilistEpisodeID)
        => Guid(MetadataEntityType.Episode, anilistEpisodeID);

    /// <summary>
    /// The identifier of a genre, which AniList keys on its name alone.
    /// </summary>
    /// <param name="genre">The genre's name.</param>
    /// <returns>The identifier, e.g. <c>anilist://tag/Action</c>.</returns>
    public static MetadataGuid GenreGuid(string genre)
    {
        ArgumentNullException.ThrowIfNull(genre);

        return new(MetadataSource.AniList, MetadataEntityType.Tag, genre.Trim());
    }

    /// <summary>
    /// Reads AniList's own numeric ID off an identifier that names an AniList
    /// entry of the kind asked for.
    /// </summary>
    /// <param name="id">The identifier, or <see langword="null"/>.</param>
    /// <param name="entityType">The kind of entry it has to name.</param>
    /// <param name="anilistID">AniList's ID, or zero.</param>
    /// <returns>Whether it named one.</returns>
    public static bool TryGetID(MetadataGuid? id, MetadataEntityType entityType, out int anilistID)
    {
        anilistID = 0;
        return id is not null && id.Source == MetadataSource.AniList && id.EntityType == entityType && TryParseID(id.ID, out anilistID);
    }

    #endregion

    #region Text

    /// <summary>
    /// Turns AniList's HTML descriptions into plain text: line-break tags into
    /// newlines, every other tag dropped, entities decoded and runs of blank
    /// lines collapsed.
    /// </summary>
    /// <param name="html">The HTML, or <see langword="null"/>.</param>
    /// <returns>The plain text, empty when there was none.</returns>
    public static string StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var text = LineBreakTagRegex().Replace(html, "\n");
        text = TagRegex().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = BlankLinesRegex().Replace(text.Replace("\r\n", "\n", StringComparison.Ordinal), "\n\n");
        return text.Trim();
    }

    /// <summary>
    /// Folds a title for comparing: accents and punctuation dropped, lower
    /// case, and whitespace collapsed.
    /// </summary>
    /// <param name="input">The title.</param>
    /// <returns>The folded title, empty for a blank one.</returns>
    public static string NormalizeForIndex(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var decomposed = input.Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) is UnicodeCategory.NonSpacingMark)
                continue;

            builder.Append(character is '-' or '_' or '.' or ':' or ',' or '!' or ';' or '/' or '\\' or '(' or ')' or '[' or ']' or '〜' or '~' or '|' ? ' ' : character);
        }

        var folded = builder.ToString().Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        return WhitespaceRegex().Replace(folded, " ").Trim();
    }

    /// <summary>
    /// Splits the language AniList appends to dub staff roles, such as
    /// "ADR Director (English)", off the role. Other qualifiers stay put.
    /// </summary>
    /// <param name="role">The role as AniList reports it.</param>
    /// <returns>The role without the language, and the language when there was one.</returns>
    public static (string Role, TitleLanguage? Language) SplitRoleLanguage(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
            return (string.Empty, null);

        role = role.Trim();
        if (RoleQualifierRegex().Match(role) is { Success: true } match && match.Groups["qualifier"].Value.Trim().TryGetTitleLanguage(out var language))
            return (match.Groups["role"].Value.Trim(), language);

        return (role, null);
    }

    #endregion

    #region Languages

    /// <summary>
    /// The transcription the main title is written in. AniList calls it
    /// "romaji", but a Chinese anime's is pinyin and a Korean one's a Korean
    /// transcription.
    /// </summary>
    /// <param name="originalLanguageCode">The original language's code.</param>
    /// <returns>The transcription language.</returns>
    public static TitleLanguage GetMainTitleLanguage(string? originalLanguageCode)
        => originalLanguageCode?.Split('-')[0].ToLowerInvariant() switch
        {
            "zh" => TitleLanguage.Pinyin,
            "ko" => TitleLanguage.KoreanTranscription,
            "th" => TitleLanguage.ThaiTranscription,
            _ => TitleLanguage.Romaji,
        };

    /// <summary>
    /// Maps AniList's <c>countryOfOrigin</c> to the language the native title
    /// is in.
    /// </summary>
    /// <param name="countryOfOrigin">An ISO 3166-1 alpha-2 country code.</param>
    /// <returns>A language code.</returns>
    public static string ParseOriginalLanguage(string? countryOfOrigin) => countryOfOrigin?.ToUpperInvariant() switch
    {
        null or "" or "JP" => "ja",
        "CN" => "zh",
        "TW" => "zh-TW",
        "KR" => "ko",
        var other => other.ToLowerInvariant(),
    };

    /// <summary>
    /// A language name or code AniList hands out, as a language code, or
    /// <see langword="null"/> when it is not one Shoko knows.
    /// </summary>
    /// <param name="language">The language, e.g. "Japanese".</param>
    /// <returns>The language code, or <see langword="null"/>.</returns>
    public static string? ToLanguageCode(string? language)
        => !string.IsNullOrWhiteSpace(language) && language.Trim().TryGetTitleLanguage(out var titleLanguage)
            ? titleLanguage.GetString()
            : null;

    #endregion

    #region Enums

    /// <summary>
    /// Maps an AniList <c>MediaFormat</c> to an <see cref="AnimeType"/>.
    /// </summary>
    /// <param name="format">The format.</param>
    /// <returns>The anime type, or <see cref="AnimeType.Unknown"/>.</returns>
    public static AnimeType ParseFormat(string? format) => format?.ToUpperInvariant() switch
    {
        "TV" => AnimeType.TV,
        "TV_SHORT" => AnimeType.TVShort,
        "MOVIE" => AnimeType.Movie,
        "OVA" => AnimeType.OVA,
        "ONA" => AnimeType.Web,
        "SPECIAL" => AnimeType.TVSpecial,
        "MUSIC" => AnimeType.MusicVideo,
        _ => AnimeType.Unknown,
    };

    /// <summary>
    /// The AniList <c>MediaFormat</c> values an <see cref="AnimeType"/> covers,
    /// for search filters.
    /// </summary>
    /// <param name="type">The anime type.</param>
    /// <returns>The formats, empty when there is none.</returns>
    public static string[] ToFormats(AnimeType type) => type switch
    {
        AnimeType.TV => ["TV"],
        AnimeType.TVShort => ["TV_SHORT"],
        AnimeType.Movie => ["MOVIE"],
        AnimeType.OVA => ["OVA"],
        AnimeType.Web => ["ONA"],
        AnimeType.TVSpecial => ["SPECIAL"],
        AnimeType.MusicVideo => ["MUSIC"],
        _ => [],
    };

    /// <summary>
    /// Maps an AniList <c>MediaStatus</c> to a <see cref="ReleaseStatus"/>.
    /// </summary>
    /// <param name="status">The status.</param>
    /// <returns>The release status, or <see cref="ReleaseStatus.Unknown"/>.</returns>
    public static ReleaseStatus ParseReleaseStatus(string? status) => status?.ToUpperInvariant() switch
    {
        "FINISHED" => ReleaseStatus.Finished,
        "RELEASING" => ReleaseStatus.Releasing,
        "NOT_YET_RELEASED" => ReleaseStatus.NotYetReleased,
        "CANCELLED" => ReleaseStatus.Cancelled,
        "HIATUS" => ReleaseStatus.Hiatus,
        _ => ReleaseStatus.Unknown,
    };

    /// <summary>
    /// Maps an AniList <c>MediaSource</c> to a <see cref="SourceMaterial"/>.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <returns>
    /// The source material; <see cref="SourceMaterial.Unknown"/> when there is
    /// none, and <see cref="SourceMaterial.Other"/> for a value not known yet.
    /// </returns>
    public static SourceMaterial ParseSourceMaterial(string? source) => source?.ToUpperInvariant() switch
    {
        "ORIGINAL" => SourceMaterial.Original,
        "MANGA" => SourceMaterial.Manga,
        "LIGHT_NOVEL" => SourceMaterial.LightNovel,
        "VISUAL_NOVEL" => SourceMaterial.VisualNovel,
        "VIDEO_GAME" => SourceMaterial.VideoGame,
        "NOVEL" => SourceMaterial.Novel,
        "DOUJINSHI" => SourceMaterial.Doujinshi,
        "ANIME" => SourceMaterial.Anime,
        "WEB_NOVEL" => SourceMaterial.WebNovel,
        "LIVE_ACTION" => SourceMaterial.LiveAction,
        "GAME" => SourceMaterial.Game,
        "COMIC" => SourceMaterial.Comic,
        "MULTIMEDIA_PROJECT" => SourceMaterial.MultimediaProject,
        "PICTURE_BOOK" => SourceMaterial.PictureBook,
        "OTHER" => SourceMaterial.Other,
        null or "" => SourceMaterial.Unknown,
        _ => SourceMaterial.Other,
    };

    /// <summary>
    /// Works out what an anime was adapted from, going past AniList's
    /// <c>source</c> where it is too coarse. AniList files Korean and Chinese
    /// comics as <c>MANGA</c> or <c>COMIC</c>, so for those the country the
    /// comic comes from decides, and an adult anime adapted from a visual
    /// novel or a video game is an eroge. An all-ages anime with eroge roots
    /// stays a visual novel, since AniList flags no eroge.
    /// </summary>
    /// <param name="source">The anime's <c>MediaSource</c>.</param>
    /// <param name="isAdult">Whether the anime is adult content.</param>
    /// <param name="sourceCountry">
    /// The <c>countryOfOrigin</c> of the comic the anime was adapted from,
    /// when AniList relates the two.
    /// </param>
    /// <param name="animeCountry">
    /// The anime's own <c>countryOfOrigin</c>, used for a comic only when
    /// <paramref name="sourceCountry"/> is missing.
    /// </param>
    /// <returns>
    /// <see cref="SourceMaterial.Manhwa"/> for a Korean comic,
    /// <see cref="SourceMaterial.Manhua"/> for a Chinese or Taiwanese one,
    /// <see cref="SourceMaterial.Manga"/> for a Japanese one and
    /// <see cref="SourceMaterial.Comic"/> for any other;
    /// <see cref="SourceMaterial.Eroge"/> for an adult game; otherwise what
    /// <see cref="ParseSourceMaterial"/> maps <paramref name="source"/> to.
    /// </returns>
    public static SourceMaterial InferSourceMaterial(string? source, bool isAdult, string? sourceCountry, string? animeCountry)
    {
        var sourceMaterial = ParseSourceMaterial(source);
        switch (sourceMaterial)
        {
            case SourceMaterial.Manga or SourceMaterial.Comic:
            {
                var country = string.IsNullOrWhiteSpace(sourceCountry) ? animeCountry : sourceCountry;
                return country?.Trim().ToUpperInvariant() switch
                {
                    null or "" => sourceMaterial,
                    "KR" => SourceMaterial.Manhwa,
                    "CN" or "TW" => SourceMaterial.Manhua,
                    "JP" => SourceMaterial.Manga,
                    _ => SourceMaterial.Comic,
                };
            }

            case SourceMaterial.VisualNovel or SourceMaterial.VideoGame when isAdult:
                return SourceMaterial.Eroge;

            default:
                return sourceMaterial;
        }
    }

    /// <summary>
    /// Maps an AniList <c>MediaSeason</c> to a <see cref="YearlySeason"/>.
    /// </summary>
    /// <param name="season">The season.</param>
    /// <returns>The yearly season, or <see langword="null"/>.</returns>
    public static YearlySeason? ParseSeason(string? season) => season?.ToUpperInvariant() switch
    {
        "WINTER" => YearlySeason.Winter,
        "SPRING" => YearlySeason.Spring,
        "SUMMER" => YearlySeason.Summer,
        "FALL" => YearlySeason.Fall,
        _ => null,
    };

    /// <summary>
    /// Maps a <see cref="YearlySeason"/> to AniList's <c>MediaSeason</c>.
    /// </summary>
    /// <param name="season">The yearly season.</param>
    /// <returns>The AniList value.</returns>
    public static string ToSeason(YearlySeason season) => season switch
    {
        YearlySeason.Winter => "WINTER",
        YearlySeason.Spring => "SPRING",
        YearlySeason.Summer => "SUMMER",
        _ => "FALL",
    };

    /// <summary>
    /// Maps an AniList <c>MediaRelation</c> to a <see cref="RelationType"/>.
    /// </summary>
    /// <param name="relationType">The relation, e.g. <c>SIDE_STORY</c>.</param>
    /// <returns>The relation type, or <see cref="RelationType.Other"/>.</returns>
    public static RelationType ParseRelationType(string? relationType) => relationType?.ToUpperInvariant() switch
    {
        "PREQUEL" => RelationType.Prequel,
        "SEQUEL" => RelationType.Sequel,
        "PARENT" => RelationType.MainStory,
        "SIDE_STORY" => RelationType.SideStory,
        "SPIN_OFF" => RelationType.SideStory,
        "SUMMARY" => RelationType.Summary,
        "COMPILATION" => RelationType.Summary,
        "ALTERNATIVE" => RelationType.AlternativeVersion,
        "CHARACTER" => RelationType.SharedCharacters,
        "SAME_UNIVERSE" => RelationType.SameSetting,
        _ => RelationType.Other,
    };

    /// <summary>
    /// Maps a character's role in an anime to a <see cref="CastRoleType"/>.
    /// </summary>
    /// <param name="role"><c>MAIN</c>, <c>SUPPORTING</c> or <c>BACKGROUND</c>.</param>
    /// <returns>The cast role type, or <see cref="CastRoleType.None"/>.</returns>
    public static CastRoleType ParseCastRoleType(string? role) => role?.ToUpperInvariant() switch
    {
        "MAIN" => CastRoleType.MainCharacter,
        "SUPPORTING" => CastRoleType.MinorCharacter,
        "BACKGROUND" => CastRoleType.BackgroundCharacter,
        _ => CastRoleType.None,
    };

    /// <summary>
    /// Maps AniList's free-text staff role to a <see cref="CrewRoleType"/>,
    /// keyed off the words that turn up consistently in their data.
    /// </summary>
    /// <param name="role">The role, e.g. "Director (eps 1-12)".</param>
    /// <returns>The crew role type, or <see cref="CrewRoleType.None"/>.</returns>
    public static CrewRoleType ParseCrewRoleType(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
            return CrewRoleType.None;

        var index = role.IndexOf('(', StringComparison.Ordinal);
        var normalized = (index > 0 ? role[..index] : role).Trim();
        if (ContainsAny(normalized, "Original Creator", "Original Story", "Original Work", "Original Character Design"))
            return CrewRoleType.SourceWork;
        if (ContainsAny(normalized, "Character Design"))
            return CrewRoleType.CharacterDesign;
        if (ContainsAny(normalized, "Series Composition", "Script", "Screenplay"))
            return CrewRoleType.SeriesComposer;
        if (ContainsAny(normalized, "Music", "Theme Song", "Composer"))
            return CrewRoleType.Music;
        if (ContainsAny(normalized, "Producer"))
            return CrewRoleType.Producer;
        if (ContainsAny(normalized, "Director"))
            return CrewRoleType.Director;
        return CrewRoleType.None;
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        foreach (var needle in needles)
            if (value.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    #endregion
}
