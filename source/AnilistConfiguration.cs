using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Config.Attributes;
using Shoko.Abstractions.Config.Enums;

namespace Shoko.Plugin.Anilist;

/// <summary>
/// Configuration for the AniList metadata plugin.
/// </summary>
/// <remarks>
/// Whether the provider answers at all is not a setting here. That belongs to
/// <see cref="Shoko.Abstractions.Metadata.Services.IMetadataProviderManager"/>,
/// which turns a provider on and off per source and per entity type, and whose
/// auto-link switches decide whether new anime are searched for on their own.
/// Which images are downloaded is the core's too, set in the image settings
/// for the AniList source.
/// </remarks>
[Display(Name = "AniList")]
public class AnilistConfiguration : IConfiguration
{
    #region Linking

    /// <summary>
    /// Whether AniList episodes already linked to another AniDB anime are left
    /// out when episodes are matched automatically.
    /// </summary>
    [Display(Name = "Consider Existing Other Links", Description = "Leave out AniList episodes another anime is already linked to when matching episodes automatically.")]
    public bool ConsiderExistingOtherLinks { get; set; }

    /// <summary>
    /// How many search results to score when searching for an anime to link.
    /// <c>1</c> considers only the first result.
    /// </summary>
    [Display(Name = "Auto-Search Candidate Count")]
    [Range(1, 20)]
    [DefaultValue(5)]
    [Visibility(Size = DisplayElementSize.Small)]
    public int AutoSearchCandidateCount { get; set; } = 5;

    /// <summary>
    /// How many days an anime can stay stored without anything linked to it
    /// or refreshing it before it is purged, once a day. <c>0</c> turns the
    /// purge off.
    /// </summary>
    [Display(Name = "Purge Unlinked After (Days)")]
    [Range(0, 365)]
    [DefaultValue(14)]
    [Visibility(Size = DisplayElementSize.Small)]
    public int AutoPurgeUnlinkedAfterDays { get; set; } = 14;

    #endregion

    #region Downloads

    /// <summary>
    /// Whether to fetch the characters and their voice actors for an anime.
    /// </summary>
    [Display(Name = "Download Characters")]
    public bool AutoDownloadCharacters { get; set; }

    /// <summary>
    /// Whether to fetch the production staff for an anime.
    /// </summary>
    [Display(Name = "Download Staff")]
    public bool AutoDownloadStaff { get; set; }

    /// <summary>
    /// Whether to fetch the studios for an anime.
    /// </summary>
    [Display(Name = "Download Studios")]
    public bool AutoDownloadStudios { get; set; }

    /// <summary>
    /// How far down AniList's recommendations to read. The first page costs
    /// nothing extra; the other two spend requests on weaker entries.
    /// </summary>
    [Display(Name = "Recommendation Depth")]
    [DefaultValue(AnilistRecommendationDepth.WhileWellRated)]
    [Visibility(Size = DisplayElementSize.Small)]
    public AnilistRecommendationDepth RecommendationDepth { get; set; } = AnilistRecommendationDepth.WhileWellRated;

    /// <summary>
    /// The base URL, or a URL template with <c>{0}</c>, for the image CDN.
    /// Left empty, the CDN AniList itself hands out is used.
    /// </summary>
    [Display(Name = "Image CDN URL", Description = "Optional. A base URL or a URL template with {0} for the AniList image CDN.")]
    [EnvironmentVariable("ANILIST_IMAGE_CDN_URL")]
    [Url]
    [Visibility(Size = DisplayElementSize.Large)]
    public string? ImageCdnUrl { get; set; }

    #endregion

    #region Rate Limit

    /// <summary>
    /// How fast the plugin may talk to AniList.
    /// </summary>
    [Display(Name = "Rate Limit")]
    [Visibility(Advanced = true)]
    public AnilistRateLimitConfiguration RateLimit { get; set; } = new();

    #endregion
}

/// <summary>
/// How fast the plugin may talk to AniList.
/// </summary>
/// <remarks>
/// The defaults come to one request every four seconds, the same pace as
/// AniDB and far below what AniList allows. The limiter never goes above what
/// AniList advertises in its response headers, whatever is set here.
/// </remarks>
public class AnilistRateLimitConfiguration
{
    /// <summary>
    /// How many requests may be made within one window.
    /// </summary>
    [Badge("Debug", Theme = DisplayColorTheme.Warning)]
    [Visibility(Size = DisplayElementSize.Small, Advanced = true)]
    [Display(Name = "Max Requests Per Window")]
    [Range(1, 90)]
    [DefaultValue(1)]
    [EnvironmentVariable("ANILIST_RATE_LIMIT_MAX_REQUESTS_PER_WINDOW")]
    public int MaxRequestsPerWindow { get; set; } = 1;

    /// <summary>
    /// How long one window lasts, in milliseconds.
    /// </summary>
    [Badge("Debug", Theme = DisplayColorTheme.Warning)]
    [Visibility(Size = DisplayElementSize.Small, Advanced = true)]
    [Display(Name = "Window Duration (ms)")]
    [Range(1000, 120000)]
    [DefaultValue(4000)]
    [EnvironmentVariable("ANILIST_RATE_LIMIT_WINDOW_DURATION_MS")]
    public int WindowDurationMs { get; set; } = 4000;
}

/// <summary>
/// How far down AniList's recommendations to read for an anime. They come back
/// best first, so the tail is weakly rated, and each extra page is another
/// request against the rate limit.
/// </summary>
public enum AnilistRecommendationDepth
{
    /// <summary>
    /// Only the first page, which rides along on the anime's own request.
    /// </summary>
    FirstPage = 0,

    /// <summary>
    /// Keep reading while a page still ends on a well rated entry.
    /// </summary>
    WhileWellRated = 1,

    /// <summary>
    /// Every page, however weakly rated the tail is.
    /// </summary>
    Everything = 2,
}
