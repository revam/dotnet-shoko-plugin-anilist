using System;
using System.Collections.Generic;
using Shoko.Abstractions.Metadata.Enums;

namespace Shoko.Plugin.Anilist.Storage;

/// <summary>
/// What the plugin keeps of an AniList anime besides what the core's stores
/// hold: the few fields only AniList has, and where its two images are.
/// </summary>
/// <remarks>
/// The anime itself, its titles, description, dates, ratings, links, tags,
/// studios, cast, crew, relations and recommendations live in the core's
/// stores. Kept as one row per anime in the <c>Anime</c> table of the
/// plugin's own <see cref="AnilistDbContext"/>.
/// </remarks>
public sealed class AnilistStoredAnime
{
    #region Identity

    /// <summary>AniList's ID for the anime.</summary>
    public int ID { get; set; }

    /// <summary>The MyAnimeList ID AniList links the anime to, if any.</summary>
    public int? MalID { get; set; }

    #endregion

    #region Classification

    /// <summary>The season it was released in.</summary>
    public YearlySeason? Season { get; set; }

    /// <summary>The year of that season.</summary>
    public int? SeasonYear { get; set; }

    /// <summary>Whether it is licensed outside its country of origin.</summary>
    public bool IsLicensed { get; set; }

    #endregion

    #region Numbers

    /// <summary>How many episodes AniList says there are, or zero while it does not know.</summary>
    public int EpisodeCount { get; set; }

    /// <summary>How long an episode runs, in minutes.</summary>
    public int? EpisodeDuration { get; set; }

    /// <summary>The plain mean score, on a 0-100 scale.</summary>
    public double MeanScore { get; set; }

    #endregion

    #region Images

    /// <summary>The cover image's resource ID.</summary>
    public string CoverImagePath { get; set; } = string.Empty;

    /// <summary>The banner image's resource ID.</summary>
    public string BannerImagePath { get; set; } = string.Empty;

    /// <summary>The cover image's main colour, as a hex string.</summary>
    public string Color { get; set; } = string.Empty;

    #endregion

    #region Recommendations

    /// <summary>
    /// The anime AniList itself recommends from this one, as read, before
    /// the recommendations naming it from the other side are merged in.
    /// </summary>
    /// <remarks>
    /// Kept apart from the merged list the suggestion store holds, so an edge
    /// merged in from the other side is never read back as this anime's own
    /// and kept alive after AniList dropped it.
    /// </remarks>
    public List<AnilistStoredRecommendation> Recommendations { get; set; } = [];

    #endregion

    #region Dates

    /// <summary>When the plugin first stored it, in UTC.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When it was last refreshed in full, in UTC. Equal to
    /// <see cref="CreatedAt"/> while only a quick refresh has been done.
    /// </summary>
    public DateTime LastUpdatedAt { get; set; }

    #endregion
}

/// <summary>
/// One anime AniList recommends from another, with its score.
/// </summary>
public sealed class AnilistStoredRecommendation
{
    /// <summary>The recommended anime's ID.</summary>
    public int ID { get; set; }

    /// <summary>AniList's net score for the recommendation.</summary>
    public int? Score { get; set; }
}

/// <summary>
/// What the plugin keeps of an AniList episode besides the core's series
/// store: the airing schedule's own ID for its slot, and when it changed.
/// </summary>
/// <remarks>
/// AniList has no episode entity, so the episodes are made up from the
/// anime's episode count and its airing schedule, one per number, each with
/// a packed ID (<see cref="Mapping.AnilistUtility.PackEpisodeID"/>).
/// </remarks>
public sealed class AnilistStoredEpisode
{
    /// <summary>The packed episode ID.</summary>
    public int ID { get; set; }

    /// <summary>The anime's ID.</summary>
    public int AnimeID { get; set; }

    /// <summary>The episode number.</summary>
    public int EpisodeNumber { get; set; }

    /// <summary>The airing schedule entry's ID, when there is one.</summary>
    public int? ScheduleID { get; set; }

    /// <summary>How long it runs, in minutes.</summary>
    public int? RuntimeMinutes { get; set; }

    /// <summary>When it aired, in UTC, from the airing schedule.</summary>
    public DateTime? AiredAt { get; set; }

    /// <summary>When the plugin first stored it, in UTC.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When its slot in the schedule or its runtime last changed, in UTC.</summary>
    public DateTime LastUpdatedAt { get; set; }
}

/// <summary>
/// The portrait of a character or a person, which the image provider hands
/// the core when it asks for the images of one.
/// </summary>
public sealed class AnilistStoredPortrait
{
    /// <summary>
    /// The person's or character's identifier without the source, e.g.
    /// <c>creator/95</c>, which is the document's key.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The image's resource ID.</summary>
    public string ImagePath { get; set; } = string.Empty;
}
