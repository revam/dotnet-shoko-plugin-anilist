using System;
using System.Collections.Generic;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;

namespace Shoko.Plugin.Anilist.Mapping;

/// <summary>
/// An AniList anime as read off a <c>Media</c> node, before any of it is
/// written anywhere.
/// </summary>
/// <remarks>
/// The refresh turns this into the core's write models and the plugin's own
/// document; a search hands it back as a result.
/// </remarks>
public sealed record AnilistMedia
{
    #region Identity

    /// <summary>AniList's ID for the anime.</summary>
    public required int ID { get; init; }

    /// <summary>The MyAnimeList ID AniList links the anime to, if any.</summary>
    public int? MalID { get; init; }

    #endregion

    #region Text

    /// <summary>The English title, empty when AniList has none.</summary>
    public string EnglishTitle { get; init; } = string.Empty;

    /// <summary>
    /// The main title, a transcription of the native one, which AniList treats
    /// as canonical.
    /// </summary>
    public string MainTitle { get; init; } = string.Empty;

    /// <summary>The title in the original script.</summary>
    public string NativeTitle { get; init; } = string.Empty;

    /// <summary>Alternative titles, in no language in particular.</summary>
    public IReadOnlyList<string> Synonyms { get; init; } = [];

    /// <summary>The English description, as plain text.</summary>
    public string EnglishOverview { get; init; } = string.Empty;

    /// <summary>The language the anime was made in, from its country of origin.</summary>
    public string OriginalLanguageCode { get; init; } = string.Empty;

    #endregion

    #region Classification

    /// <summary>The kind of release.</summary>
    public AnimeType Type { get; init; } = AnimeType.Unknown;

    /// <summary>Where the anime is in its release.</summary>
    public ReleaseStatus Status { get; init; }

    /// <summary>What the anime was adapted from.</summary>
    public SourceMaterial Source { get; init; }

    /// <summary>The season it was released in.</summary>
    public YearlySeason? Season { get; init; }

    /// <summary>The year of that season.</summary>
    public int? SeasonYear { get; init; }

    /// <summary>The genres, by name.</summary>
    public IReadOnlyList<string> Genres { get; init; } = [];

    /// <summary>Whether it is adult content.</summary>
    public bool IsRestricted { get; init; }

    /// <summary>Whether it is licensed outside its country of origin.</summary>
    public bool IsLicensed { get; init; }

    #endregion

    #region Numbers

    /// <summary>How many episodes AniList says there are, or zero while it does not know.</summary>
    public int EpisodeCount { get; init; }

    /// <summary>How long an episode runs, in minutes.</summary>
    public int? EpisodeDuration { get; init; }

    /// <summary>The weighted average score, on a 0-100 scale.</summary>
    public double AverageScore { get; init; }

    /// <summary>The plain mean score, on a 0-100 scale.</summary>
    public double MeanScore { get; init; }

    /// <summary>How many users scored it, summed from the score distribution.</summary>
    public int ScoreVotes { get; init; }

    /// <summary>How many users have it on a list.</summary>
    public int Popularity { get; init; }

    /// <summary>How many users marked it a favorite.</summary>
    public int FavoriteCount { get; init; }

    #endregion

    #region Dates

    /// <summary>When it started airing, as far as AniList knows.</summary>
    public PartialDateOnly? StartDate { get; init; }

    /// <summary>When it finished airing, as far as AniList knows.</summary>
    public PartialDateOnly? EndDate { get; init; }

    /// <summary>
    /// The number of the next episode to air, or <c>null</c> when none is
    /// scheduled or it was not asked for. Only a search asks for it.
    /// </summary>
    public int? NextEpisodeNumber { get; init; }

    /// <summary>When the next episode airs, in UTC, or <c>null</c>.</summary>
    public DateTime? NextEpisodeAiringAt { get; init; }

    #endregion

    #region Images & Links

    /// <summary>The cover image's resource ID.</summary>
    public string CoverImagePath { get; init; } = string.Empty;

    /// <summary>The banner image's resource ID.</summary>
    public string BannerImagePath { get; init; } = string.Empty;

    /// <summary>The cover image's main colour, as a hex string.</summary>
    public string Color { get; init; } = string.Empty;

    /// <summary>The site hosting the trailer, e.g. "youtube".</summary>
    public string? TrailerSite { get; init; }

    /// <summary>The trailer's ID on that site.</summary>
    public string? TrailerID { get; init; }

    /// <summary>The external links AniList lists.</summary>
    public IReadOnlyList<AnilistExternalLink> ExternalLinks { get; init; } = [];

    #endregion
}

/// <summary>
/// An external link AniList lists for an anime.
/// </summary>
public sealed record AnilistExternalLink
{
    /// <summary>AniList's ID for the link.</summary>
    public required int ID { get; init; }

    /// <summary>The URL.</summary>
    public required string Url { get; init; }

    /// <summary>The site's display name.</summary>
    public string Site { get; init; } = string.Empty;

    /// <summary>AniList's link type: <c>INFO</c>, <c>STREAMING</c> or <c>SOCIAL</c>.</summary>
    public string LinkType { get; init; } = string.Empty;

    /// <summary>The language the linked page is in, as a language code.</summary>
    public string? LanguageCode { get; init; }
}
