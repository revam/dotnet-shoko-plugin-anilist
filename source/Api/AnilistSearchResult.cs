using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Search;
using Shoko.Plugin.Anilist.Mapping;
using Shoko.Plugin.Anilist.Storage;

namespace Shoko.Plugin.Anilist.Api;

/// <summary>
/// One anime a search turned up, or one already stored, in the shape the
/// search and linking code compares.
/// </summary>
public sealed class AnilistSearchResult
{
    /// <summary>
    /// Reads a search result off a <c>Media</c> node.
    /// </summary>
    /// <param name="media">The <c>Media</c> node.</param>
    /// <exception cref="ArgumentNullException"><paramref name="media"/> is <see langword="null"/>.</exception>
    public AnilistSearchResult(JsonNode media)
        : this(AnilistMediaMapper.ReadMedia(media)) { }

    /// <summary>
    /// Wraps an anime already read.
    /// </summary>
    /// <param name="anime">The anime.</param>
    /// <exception cref="ArgumentNullException"><paramref name="anime"/> is <see langword="null"/>.</exception>
    public AnilistSearchResult(AnilistMedia anime)
    {
        ArgumentNullException.ThrowIfNull(anime);

        Anime = anime;
    }

    /// <summary>
    /// An anime the core's series store holds, put back into the shape a
    /// search hands out.
    /// </summary>
    /// <param name="series">The stored series.</param>
    /// <param name="document">What the plugin keeps of it besides, if anything.</param>
    /// <returns>The search result, or <see langword="null"/> when the series is not an AniList anime.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="series"/> is <see langword="null"/>.</exception>
    public static AnilistSearchResult? FromStored(ISeries series, AnilistStoredAnime? document)
    {
        ArgumentNullException.ThrowIfNull(series);

        if (!AnilistUtility.TryGetID(series.ID, MetadataEntityType.Series, out var id))
            return null;

        var titles = series.Titles;
        var mainTitle = titles.FirstOrDefault(title => title.Type is TitleType.Main)?.Value ?? string.Empty;
        var englishTitle = titles.FirstOrDefault(title => title.Type is TitleType.Official && title.Language is TitleLanguage.English)?.Value ?? string.Empty;
        var nativeTitle = titles.FirstOrDefault(title => title.Type is TitleType.Official && title.Language is not TitleLanguage.English)?.Value ?? string.Empty;
        return new(new AnilistMedia
        {
            ID = id,
            MalID = document?.MalID,
            EnglishTitle = englishTitle,
            MainTitle = mainTitle,
            NativeTitle = nativeTitle,
            Synonyms = [.. titles.Where(title => title.Type is TitleType.Synonym).Select(title => title.Value)],
            EnglishOverview = series.Overviews.FirstOrDefault(description => description.Language is TitleLanguage.English)?.Value ?? string.Empty,
            OriginalLanguageCode = series.OriginalLanguageCode ?? string.Empty,
            Type = series.Type,
            Status = series.ReleaseStatus,
            Source = series.SourceMaterial,
            Season = document?.Season,
            SeasonYear = document?.SeasonYear,
            Genres = [.. series.Tags.Where(tag => tag.Kind is TagKind.Genre).Select(tag => tag.Name)],
            IsRestricted = series.Restricted,
            IsLicensed = document?.IsLicensed ?? true,
            EpisodeCount = document?.EpisodeCount ?? 0,
            EpisodeDuration = document?.EpisodeDuration,
            AverageScore = Math.Round(series.Rating * 10, 2),
            MeanScore = document?.MeanScore ?? 0,
            ScoreVotes = series.RatingVotes,
            Popularity = series.Popularity is { } popularity ? (int)popularity : 0,
            FavoriteCount = series.FavoriteCount ?? 0,
            StartDate = series.AirDate,
            EndDate = series.EndDate,
            CoverImagePath = document?.CoverImagePath ?? string.Empty,
            BannerImagePath = document?.BannerImagePath ?? string.Empty,
            Color = document?.Color ?? string.Empty,
        });
    }

    /// <summary>
    /// The anime, as read.
    /// </summary>
    public AnilistMedia Anime { get; }

    /// <summary>AniList's ID for the anime.</summary>
    public int ID => Anime.ID;

    /// <summary>The English title when there is one, otherwise the main title.</summary>
    public string Title => !string.IsNullOrEmpty(Anime.EnglishTitle) ? Anime.EnglishTitle : Anime.MainTitle;

    /// <summary>The title in the original script.</summary>
    public string OriginalTitle => Anime.NativeTitle;

    /// <summary>The original language's code.</summary>
    public string OriginalLanguage => Anime.OriginalLanguageCode;

    /// <summary>The English description.</summary>
    public string Overview => Anime.EnglishOverview;

    /// <summary>The kind of release.</summary>
    public AnimeType Type => Anime.Type;

    /// <summary>Whether it is adult content.</summary>
    public bool IsRestricted => Anime.IsRestricted;

    /// <summary>How many episodes AniList says there are, when it knows.</summary>
    public int? EpisodeCount => Anime.EpisodeCount > 0 ? Anime.EpisodeCount : null;

    /// <summary>The genres.</summary>
    public IReadOnlyList<string> Genres => Anime.Genres;

    /// <summary>The season it was released in.</summary>
    public YearlySeason? Season => Anime.Season;

    /// <summary>The year of that season.</summary>
    public int? SeasonYear => Anime.SeasonYear;

    /// <summary>When it started airing.</summary>
    public PartialDateOnly? FirstAiredAt => Anime.StartDate;

    /// <summary>The cover image's URL.</summary>
    public string? CoverImageUrl => AnilistImages.ToImageUrl(Anime.CoverImagePath);

    /// <summary>The banner image's URL.</summary>
    public string? BannerImageUrl => AnilistImages.ToImageUrl(Anime.BannerImagePath);

    /// <summary>
    /// Every title and synonym, for matching.
    /// </summary>
    public IReadOnlySet<string> AllTitles
        => new[] { Anime.EnglishTitle, Anime.MainTitle, Anime.NativeTitle }
            .Concat(Anime.Synonyms)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The result in the shape the provider contract hands back, with every
    /// other title it goes by, such as the romaji one and the synonyms, as its
    /// alternate titles, which matching reads beside the main ones.
    /// </summary>
    /// <returns>The search result.</returns>
    public MetadataSeriesSearchResult ToMetadataSearchResult()
        => new()
        {
            ID = AnilistUtility.SeriesGuid(ID),
            Title = Title,
            OriginalTitle = string.IsNullOrEmpty(OriginalTitle) ? null : OriginalTitle,
            AlternateTitles = [.. AllTitles.Where(title => !string.Equals(title, Title, StringComparison.OrdinalIgnoreCase) && !string.Equals(title, OriginalTitle, StringComparison.OrdinalIgnoreCase))],
            OriginalLanguageCode = string.IsNullOrEmpty(OriginalLanguage) ? null : OriginalLanguage,
            Overview = string.IsNullOrEmpty(Overview) ? null : Overview,
            IsRestricted = IsRestricted,
            UserRating = Anime.AverageScore > 0 ? Math.Round((decimal)Anime.AverageScore / 10, 2) : null,
            UserVotes = Anime.ScoreVotes,
            PosterUrl = CoverImageUrl,
            BackdropUrl = BannerImageUrl,
            Genres = Genres,
            FirstAiredAt = FirstAiredAt,
            Type = Type,
            Season = Season,
            SeasonYear = SeasonYear,
            EpisodeCount = EpisodeCount,
        };
}
