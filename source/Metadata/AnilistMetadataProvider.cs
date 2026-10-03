using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Anidb;
using Shoko.Abstractions.Metadata.CrossReferences;
using Shoko.Abstractions.Metadata.Image;
using Shoko.Abstractions.Metadata.Matching;
using Shoko.Abstractions.Metadata.Providers;
using Shoko.Abstractions.Metadata.Search;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Plugin.Anilist.Airing;
using Shoko.Plugin.Anilist.Api;
using Shoko.Plugin.Anilist.Mapping;
using Shoko.Plugin.Anilist.Services;
using Shoko.Plugin.Anilist.Storage;

namespace Shoko.Plugin.Anilist.Metadata;

/// <summary>
/// Supplies AniList anime and their episodes to Shoko, under the
/// <c>MetadataSource.AniList</c> source.
/// </summary>
/// <remarks>
/// <para>
///   Series-shaped only: AniList files films as anime with a format set, so
///   they come back as series, and it has no seasons at all.
/// </para>
/// <para>
///   The core runs the refresh, search, image and purge jobs and calls in
///   here; the provider fetches from AniList and writes into the core's
///   stores, and the core reads the anime back from them. While the rate
///   limiter's breaker is tripped the provider says it is paused, and the
///   core holds its jobs back.
/// </para>
/// <para>
///   It also refreshes AniList's staff, characters and studios one at a
///   time, for the ones a refresh named without writing them.
/// </para>
/// </remarks>
public sealed class AnilistMetadataProvider : IMetadataSeriesLinkingProvider, IMetadataAutoLinkingProvider, IMetadataImageProvider, IMetadataEntityProvider, IPausableMetadataProvider, IMetadataProvider<AnilistConfiguration>
{
    private readonly AnilistRefreshService _refreshService;

    private readonly AnilistSearchService _searchService;

    private readonly AnilistLinkingService _linkingService;

    private readonly AnilistImageService _imageService;

    private readonly AnilistAiringScheduleProvider _airingScheduleProvider;

    private readonly AnilistStore _store;

    private readonly AnilistRateLimiter _rateLimiter;

    private readonly IMetadataService _metadataService;

    private readonly ILogger<AnilistMetadataProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnilistMetadataProvider"/> class.
    /// </summary>
    /// <param name="refreshService">Fetches an anime and writes it into the stores.</param>
    /// <param name="searchService">Searches AniList.</param>
    /// <param name="linkingService">Matches episodes and records links.</param>
    /// <param name="imageService">Hands out the images.</param>
    /// <param name="airingScheduleProvider">Removes the broadcast times of a purged anime.</param>
    /// <param name="store">The plugin's store, cleaned up after a purge.</param>
    /// <param name="rateLimiter">The rate limiter, whose breaker is the provider's pause.</param>
    /// <param name="metadataService">The core's metadata service, for the AniDB anime to link.</param>
    /// <param name="logger">The logger.</param>
    public AnilistMetadataProvider(
        AnilistRefreshService refreshService,
        AnilistSearchService searchService,
        AnilistLinkingService linkingService,
        AnilistImageService imageService,
        AnilistAiringScheduleProvider airingScheduleProvider,
        AnilistStore store,
        AnilistRateLimiter rateLimiter,
        IMetadataService metadataService,
        ILogger<AnilistMetadataProvider> logger
    )
    {
        _refreshService = refreshService;
        _searchService = searchService;
        _linkingService = linkingService;
        _imageService = imageService;
        _airingScheduleProvider = airingScheduleProvider;
        _store = store;
        _rateLimiter = rateLimiter;
        _metadataService = metadataService;
        _logger = logger;
        _rateLimiter.PauseStateChanged += OnPauseStateChanged;
    }

    #region Provider

    /// <inheritdoc/>
    public string Name => "AniList";

    /// <inheritdoc/>
    public string? Description => "AniList, a community database of anime and manga.";

    /// <inheritdoc/>
    public MetadataSource Source => MetadataSource.AniList;

    /// <inheritdoc/>
    public string? EmbeddedIconResourceName => Plugin.IconResourceName;

    /// <summary>
    /// Off: installing the plugin does not on its own start linking anime.
    /// </summary>
    public bool AutoLinkByDefault => false;

    /// <summary>
    /// On, once auto-linking is turned on: AniList flags adult entries itself,
    /// and the core's AniList linked them before.
    /// </summary>
    public bool AutoLinkRestrictedByDefault => true;

    /// <summary>
    /// Four of each job at once. Every request is paced by the rate limiter
    /// whatever runs it, so this only keeps a library-wide refresh from
    /// crowding out the rest of the queue.
    /// </summary>
    public int? MaxConcurrentJobs => 4;

    /// <inheritdoc/>
    public IReadOnlySet<MetadataEntityType> LinkableEntityTypes { get; } = FrozenSet.ToFrozenSet([MetadataEntityType.Series, MetadataEntityType.Episode]);

    /// <summary>
    /// Forgets what the plugin keeps of a purged anime besides the core's
    /// stores: its document, its episodes' documents, its broadcast times,
    /// and the portraits of people the core no longer holds.
    /// </summary>
    /// <param name="entryID">The purged anime.</param>
    /// <param name="cancellationToken">Unused; the work is local.</param>
    /// <returns>A task that completes once it is forgotten.</returns>
    public Task CleanUp(MetadataGuid entryID, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entryID);

        if (!AnilistUtility.TryGetID(entryID, MetadataEntityType.Series, out var anilistAnimeID))
            return Task.CompletedTask;

        _logger.LogInformation("Cleaning up after AniList anime {AnimeID}", anilistAnimeID);
        _store.RemoveAnime(anilistAnimeID);
        _airingScheduleProvider.RemoveSchedules(entryID);
        _store.RemoveOrphanedPortraits();
        return Task.CompletedTask;
    }

    #endregion

    #region Site URLs

    /// <summary>
    /// The page on AniList of an anime, made from its ID. Episodes are
    /// synthesized from the anime and have no page of their own.
    /// </summary>
    /// <param name="entry">The entry, of the AniList source.</param>
    /// <returns>The URL, or <see langword="null"/> for any other kind.</returns>
    string? IMetadataSeriesProvider.GetSiteUrl(IMetadata entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return AnilistUtility.TryGetID(entry.ID, MetadataEntityType.Series, out var anilistAnimeID) ? AnilistUtility.AnimeUrl(anilistAnimeID) : null;
    }

    /// <summary>
    /// The page on AniList of a staff member, a character or a studio, made
    /// from its ID. AniList has no networks.
    /// </summary>
    /// <param name="entry">The entry, of the AniList source.</param>
    /// <returns>The URL, or <see langword="null"/> for any other kind.</returns>
    string? IMetadataEntityProvider.GetSiteUrl(IMetadata entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (AnilistUtility.TryGetID(entry.ID, MetadataEntityType.Creator, out var anilistStaffID))
            return AnilistUtility.StaffUrl(anilistStaffID);
        if (AnilistUtility.TryGetID(entry.ID, MetadataEntityType.Character, out var anilistCharacterID))
            return AnilistUtility.CharacterUrl(anilistCharacterID);
        if (AnilistUtility.TryGetID(entry.ID, MetadataEntityType.Studio, out var anilistStudioID))
            return AnilistUtility.StudioUrl(anilistStudioID);

        return null;
    }

    #endregion

    #region Pausing

    /// <summary>
    /// Paused while the rate limiter's breaker is tripped, which it is after
    /// AniList answered with a server error, until the pause runs out.
    /// </summary>
    public MetadataProviderPauseStatus PauseStatus
        => _rateLimiter.IsPaused
            ? new()
            {
                IsPaused = true,
                Reason = _rateLimiter.PauseReason ?? "AniList is temporarily unavailable.",
                ResumesAt = _rateLimiter.RemainingPauseTime is { } remaining ? DateTime.UtcNow + remaining : null,
            }
            : MetadataProviderPauseStatus.NotPaused;

    /// <inheritdoc/>
    public event EventHandler? PauseStatusChanged;

    private void OnPauseStateChanged(object? sender, EventArgs eventArgs)
        => PauseStatusChanged?.Invoke(this, EventArgs.Empty);

    #endregion

    #region Refresh

    /// <summary>
    /// Fetches an anime from AniList and writes it, its episodes and what the
    /// other stores hold for it.
    /// </summary>
    /// <param name="seriesID">The anime.</param>
    /// <param name="options">What kind of refresh it is.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the anime is written.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="AnilistApiException">AniList answered with something unexpected.</exception>
    /// <exception cref="AnilistUnavailableException">AniList cannot be reached for now.</exception>
    public async Task RefreshSeries(MetadataGuid seriesID, MetadataRefreshOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seriesID);
        ArgumentNullException.ThrowIfNull(options);

        if (!AnilistUtility.TryGetID(seriesID, MetadataEntityType.Series, out var anilistAnimeID))
            return;

        await _refreshService.RefreshAnime(anilistAnimeID, options, cancellationToken).ConfigureAwait(false);
    }

    #endregion

    #region Staff, Characters & Studios

    /// <summary>
    /// AniList's staff, characters and studios. It has no networks.
    /// </summary>
    public MetadataEntityScope EntityScope { get; } = MetadataEntityScope.ForSource(
        MetadataSource.AniList,
        MetadataEntityType.Creator,
        MetadataEntityType.Character,
        MetadataEntityType.Studio
    );

    /// <summary>
    /// Never stale: an anime's refresh writes its staff, characters and
    /// studios in full, so only the stubs it left are asked for.
    /// </summary>
    public TimeSpan? EntityStaleAfter => null;

    /// <summary>
    /// Fetches one staff member, character or studio from AniList and
    /// writes it into the stores.
    /// </summary>
    /// <param name="entityID">The entry, of a kind in <see cref="EntityScope"/>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether AniList had the entry.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entityID"/> is <see langword="null"/>.</exception>
    /// <exception cref="AnilistApiException">AniList answered with something unexpected.</exception>
    /// <exception cref="AnilistUnavailableException">AniList cannot be reached for now.</exception>
    public Task<bool> RefreshEntity(MetadataGuid entityID, CancellationToken cancellationToken = default)
        => _refreshService.RefreshEntity(entityID, cancellationToken);

    #endregion

    #region Images

    /// <inheritdoc/>
    public Task<IReadOnlyList<ImageCandidate>?> GetImages(MetadataGuid entityID, CancellationToken cancellationToken = default)
        => Task.FromResult(_imageService.GetImages(entityID));

    #endregion

    #region Search & Matching

    /// <summary>
    /// Searches AniList for anime.
    /// </summary>
    /// <param name="options">What to search for.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The page asked for, and how many results there are in total.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="AnilistApiException">AniList answered with something unexpected.</exception>
    /// <exception cref="AnilistUnavailableException">AniList cannot be reached for now.</exception>
    public async Task<(IReadOnlyList<MetadataSeriesSearchResult> Page, int TotalCount)> SearchSeries(MetadataSearchOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var (page, total) = await _searchService.SearchAnime(new()
        {
            Query = options.Query,
            IncludeRestricted = options.IncludeRestricted,
            Year = options.Year,
            Season = options.Season,
            SeasonYear = options.SeasonYear,
            Types = options.Types,
            Page = options.Page,
            PageSize = options.PageSize,
        }, cancellationToken).ConfigureAwait(false);
        return ([.. page.Select(result => result.ToMetadataSearchResult())], total);
    }

    /// <summary>
    /// Looks one anime up by its ID, from the stored copy when there is one
    /// and from AniList otherwise, without storing anything.
    /// </summary>
    /// <param name="seriesID">The AniList anime.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// The anime, or <see langword="null"/> when AniList has none by that ID
    /// or the ID names no AniList anime.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="seriesID"/> is <see langword="null"/>.</exception>
    /// <exception cref="AnilistApiException">AniList answered with something unexpected.</exception>
    /// <exception cref="AnilistUnavailableException">AniList cannot be reached for now.</exception>
    public async Task<MetadataSeriesSearchResult?> LookupSeries(MetadataGuid seriesID, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seriesID);

        if (!AnilistUtility.TryGetID(seriesID, MetadataEntityType.Series, out var anilistAnimeID))
            return null;

        var anime = _searchService.GetStoredAnime(anilistAnimeID)
            ?? await _searchService.GetRemoteAnime(anilistAnimeID, cancellationToken).ConfigureAwait(false);
        return anime?.ToMetadataSearchResult();
    }

    /// <summary>
    /// Works out which AniList episodes an anime's episodes line up with,
    /// through the core's matcher, without writing anything.
    /// </summary>
    /// <remarks>
    /// The season is ignored, AniList having none. The AniList anime has to be
    /// stored already; one that is not has no episodes to match against.
    /// </remarks>
    /// <param name="anime">The AniDB anime.</param>
    /// <param name="anidbEpisodes">The episodes in scope.</param>
    /// <param name="providerSeriesID">The AniList anime.</param>
    /// <param name="providerSeasonID">Ignored.</param>
    /// <param name="existing">The links to honour, but for one naming an episode AniList no longer lists.</param>
    /// <param name="considerOtherLinks">Whether to leave out the episodes other anime are linked to; <see langword="null"/> follows the settings.</param>
    /// <param name="cancellationToken">Unused; the matching is local.</param>
    /// <returns>
    /// One match per AniDB episode in scope, including the ones nothing
    /// matched, but for an episode a link settles outside the candidates (into
    /// another AniList anime, or by the user to nothing or to an episode left
    /// out), which is left out so that saving the result keeps its links.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="anime"/>, <paramref name="anidbEpisodes"/> or <paramref name="providerSeriesID"/> is <see langword="null"/>.</exception>
    public Task<IReadOnlyList<EpisodeMatch>> MatchEpisodes(
        IAnidbAnime anime,
        IReadOnlyList<IAnidbEpisode> anidbEpisodes,
        MetadataGuid providerSeriesID,
        MetadataGuid? providerSeasonID = null,
        IReadOnlyList<IMetadataEpisodeCrossReference>? existing = null,
        bool? considerOtherLinks = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(anime);
        ArgumentNullException.ThrowIfNull(anidbEpisodes);
        ArgumentNullException.ThrowIfNull(providerSeriesID);

        if (!AnilistUtility.TryGetID(providerSeriesID, MetadataEntityType.Series, out var anilistAnimeID))
            return Task.FromResult<IReadOnlyList<EpisodeMatch>>([]);

        return Task.FromResult(_linkingService.Match(anime, anidbEpisodes, anilistAnimeID, existing, considerOtherLinks));
    }

    /// <summary>
    /// Searches AniList for the anime and hands back every anime it scored,
    /// the one taken first, for the core to link.
    /// </summary>
    /// <remarks>
    /// Nothing is written here. The anime's current links are not weighed, so
    /// a forced search answers as if it had none.
    /// </remarks>
    /// <param name="anidbAnimeID">The AniDB anime ID.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>
    /// The candidates, the one taken first and the others with why they were
    /// turned down, or an empty list when the AniDB anime is not stored or
    /// nothing was found.
    /// </returns>
    /// <exception cref="AnilistApiException">AniList answered with something unexpected.</exception>
    /// <exception cref="AnilistUnavailableException">AniList cannot be reached for now.</exception>
    public async Task<IReadOnlyList<MetadataAutoLinkCandidate>> FindAutoLinks(int anidbAnimeID, CancellationToken cancellationToken = default)
    {
        if (GetAnidbAnime(anidbAnimeID) is not { } anime)
            return [];

        var matches = await _searchService.FindAutoMatches(anime, cancellationToken).ConfigureAwait(false);
        if (!matches.Any(match => match.Rejection is null))
            _logger.LogInformation("No AniList match found for AniDB anime {AnimeID}.", anidbAnimeID);

        return [.. matches.Select(match => new MetadataAutoLinkCandidate
        {
            Result = match.Candidate,
            AnidbAnimeID = match.AnidbAnimeID,
            MatchRating = match.MatchRating,
            IsRemote = true,
            Rejection = match.Rejection,
        })];
    }

    /// <summary>
    /// The AniDB anime to search for, from the core's stores.
    /// </summary>
    /// <param name="anidbAnimeID">The AniDB anime ID.</param>
    /// <returns>The anime, or <see langword="null"/> when it is not stored.</returns>
    private IAnidbAnime? GetAnidbAnime(int anidbAnimeID)
    {
        if (anidbAnimeID <= 0)
            return null;

        var anime = _metadataService.GetSeries(new MetadataGuid(MetadataSource.AniDB, MetadataEntityType.Series, AnilistUtility.FormatID(anidbAnimeID))) as IAnidbAnime
            ?? _metadataService.GetShokoSeriesByAnidbID(anidbAnimeID)?.AnidbAnime;
        if (anime is null)
            _logger.LogWarning("AniDB anime {AnimeID} is not available locally.", anidbAnimeID);

        return anime;
    }

    #endregion
}
