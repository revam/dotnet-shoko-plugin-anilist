using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Anidb;
using Shoko.Abstractions.Metadata.CrossReferences;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Matching;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Plugin.Anilist.Mapping;
using Shoko.Plugin.Anilist.Metadata;
using Shoko.Plugin.Anilist.Storage;

namespace Shoko.Plugin.Anilist.Services;

/// <summary>
/// Manages the links between AniDB and AniList. The links are the core's,
/// kept in its cross-reference store and written through its
/// <see cref="IMetadataLinkingService"/>; this decides what to write.
/// </summary>
/// <remarks>
/// Episode matching is the core's <see cref="IMetadataMatchingEngine"/>, run
/// with the flat date-then-number strategy AniList's episodes call for, over
/// the episodes the core's series store holds for the anime.
/// </remarks>
/// <param name="store">The plugin's store.</param>
/// <param name="links">Reads the links.</param>
/// <param name="linkingService">The core's linking service.</param>
/// <param name="matchingEngine">The core's episode matcher.</param>
/// <param name="configurationProvider">The plugin's configuration.</param>
/// <param name="logger">The logger.</param>
public sealed class AnilistLinkingService(
    AnilistStore store,
    AnilistLinks links,
    IMetadataLinkingService linkingService,
    IMetadataMatchingEngine matchingEngine,
    ConfigurationProvider<AnilistConfiguration> configurationProvider,
    ILogger<AnilistLinkingService> logger
)
{
    #region Episode Links

    /// <summary>
    /// Clears an anime's episode links, leaving them for automatic matching
    /// to fill in again or marking every episode as linked to nothing.
    /// </summary>
    /// <param name="anidbAnimeID">The AniDB anime ID.</param>
    /// <param name="allowAutoMatch">Whether automatic matching may fill the episodes in again.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the links are reset.</returns>
    public Task ResetEpisodeLinks(int anidbAnimeID, bool allowAutoMatch, CancellationToken cancellationToken = default)
        => linkingService.ResetEpisodeLinks(AnilistSources.AniList, anidbAnimeID, allowAutoMatch, cancellationToken);

    #endregion

    #region Matching

    /// <summary>
    /// Works out which AniList episodes an AniDB anime's episodes line up
    /// with, without writing anything.
    /// </summary>
    /// <param name="anime">The AniDB anime.</param>
    /// <param name="anidbEpisodes">The episodes to match, already narrowed to the ones in scope.</param>
    /// <param name="anilistAnimeID">The AniList anime to match into.</param>
    /// <param name="existing">
    /// The anime's links to honour, or <see langword="null"/> to match
    /// everything afresh. A link naming an episode AniList no longer lists is
    /// not honoured.
    /// </param>
    /// <param name="considerOtherLinks">
    /// Whether to leave out AniList episodes another AniDB anime already
    /// claims; <see langword="null"/> follows the settings.
    /// </param>
    /// <returns>
    /// One match per AniDB episode in scope, including the ones nothing
    /// matched, but for an episode a link settles outside the candidates (into
    /// another AniList anime, or by the user to nothing or to an episode left
    /// out), which is left out so that saving the result keeps its links.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public IReadOnlyList<EpisodeMatch> Match(
        IAnidbAnime anime,
        IReadOnlyList<IAnidbEpisode> anidbEpisodes,
        int anilistAnimeID,
        IReadOnlyList<IMetadataEpisodeCrossReference>? existing = null,
        bool? considerOtherLinks = null
    )
    {
        ArgumentNullException.ThrowIfNull(anime);
        ArgumentNullException.ThrowIfNull(anidbEpisodes);

        if (store.GetSeries(anilistAnimeID) is not { } series)
            return [];

        IReadOnlyList<IEpisode> candidates = series.Episodes;
        if (considerOtherLinks ?? configurationProvider.Load().ConsiderExistingOtherLinks)
        {
            var claimed = candidates
                .Where(episode => AnilistUtility.TryGetID(episode.ID, MetadataEntityType.Episode, out var id) && links.GetLinksToEpisode(id).Any(link => link.AnidbAnimeID != anime.AnidbID))
                .Select(episode => episode.ID)
                .ToHashSet();
            candidates = [.. candidates.Where(episode => !claimed.Contains(episode.ID))];
        }

        if (existing is not null)
        {
            // A link naming an episode AniList no longer lists is not
            // honoured, so its AniDB episode is matched again rather than
            // left alone for good.
            var storedEpisodeIDs = series.Episodes.Select(episode => episode.ID).ToHashSet();
            var ownLinks = existing.Where(link => ParentOf(link) == anilistAnimeID && storedEpisodeIDs.Contains(link.ProviderID!)).ToList();
            var settled = existing
                .GroupBy(link => link.AnidbEpisodeID)
                .Where(group => IsSettledElsewhere(group, anilistAnimeID))
                .Select(group => group.Key)
                .ToHashSet();
            anidbEpisodes = [.. anidbEpisodes.Where(episode => !settled.Contains(episode.AnidbID))];
            existing = ownLinks;
        }

        return matchingEngine.MatchEpisodes(anidbEpisodes, candidates, existing, new() { Strategy = EpisodeMatchStrategy.DateThenNumber });
    }

    /// <summary>
    /// Whether an AniDB episode's links leave it outside a match into one
    /// AniList anime: it is linked into another one, or the user said it has
    /// no AniList episode at all.
    /// </summary>
    /// <param name="links">The episode's links on the source.</param>
    /// <param name="anilistAnimeID">The AniList anime matched into.</param>
    /// <returns>Whether to leave the episode and its links alone.</returns>
    private static bool IsSettledElsewhere(IEnumerable<IMetadataEpisodeCrossReference> links, int anilistAnimeID)
        => links.Any(link => link.ProviderID is null
            ? link.MatchRating is MatchRating.UserVerified
            : ParentOf(link) is { } parent && parent != anilistAnimeID);

    /// <summary>
    /// The AniList anime an episode link points into.
    /// </summary>
    /// <param name="link">The link.</param>
    /// <returns>The AniList anime ID, or <see langword="null"/> for a link to nothing or to no AniList episode.</returns>
    private static int? ParentOf(IMetadataEpisodeCrossReference link)
        => AnilistUtility.TryGetID(link.ProviderID, MetadataEntityType.Episode, out var episodeID)
            ? AnilistUtility.UnpackEpisodeID(episodeID).AnilistAnimeID
            : null;

    /// <summary>
    /// Matches an AniDB anime's episodes against an AniList anime through the
    /// core, and has it write the result unless only a preview was asked for.
    /// </summary>
    /// <param name="anidbAnimeID">The AniDB anime ID.</param>
    /// <param name="anilistAnimeID">The AniList anime ID.</param>
    /// <param name="useExisting">Whether to honour the links already there and only fill the gaps.</param>
    /// <param name="save">Whether to write the result.</param>
    /// <param name="considerOtherLinks">Whether to leave out episodes other anime claim; <see langword="null"/> follows the settings.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The links the matching stands for, written or not.</returns>
    public async Task<IReadOnlyList<IMetadataEpisodeCrossReference>> MatchAndSave(
        int anidbAnimeID,
        int anilistAnimeID,
        bool useExisting = true,
        bool save = true,
        bool? considerOtherLinks = null,
        CancellationToken cancellationToken = default
    )
    {
        var result = await linkingService.MatchEpisodes(
            anidbAnimeID,
            AnilistUtility.SeriesGuid(anilistAnimeID),
            useExisting: useExisting,
            save: save,
            considerOtherLinks: considerOtherLinks,
            cancellationToken: cancellationToken
        ).ConfigureAwait(false);
        if (save)
            logger.LogDebug("Matched {Count} AniList episode links for AniDB anime {AnidbID} against AniList anime {AnilistID}.", result.Count, anidbAnimeID, anilistAnimeID);

        return result;
    }

    /// <summary>
    /// Matches the episodes of every AniDB anime linked to an AniList anime
    /// again, keeping the links already there, as a refresh does.
    /// </summary>
    /// <remarks>
    /// A failure is logged and swallowed: the anime is stored by then, and a
    /// matching that could not run is no reason to fail its refresh.
    /// </remarks>
    /// <param name="anilistAnimeID">The AniList anime ID.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once every anime is matched.</returns>
    public async Task MatchLinkedEpisodes(int anilistAnimeID, CancellationToken cancellationToken = default)
    {
        foreach (var anidbAnimeID in links.GetLinkedAnidbAnimeIDs(anilistAnimeID))
            await TryMatchAndSave(anidbAnimeID, anilistAnimeID, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Matches and writes one AniDB anime's episodes against an AniList
    /// anime, keeping the links already there, and logs and swallows a
    /// matching that could not run.
    /// </summary>
    /// <param name="anidbAnimeID">The AniDB anime ID.</param>
    /// <param name="anilistAnimeID">The AniList anime ID.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the episodes are matched.</returns>
    private async Task TryMatchAndSave(int anidbAnimeID, int anilistAnimeID, CancellationToken cancellationToken)
    {
        try
        {
            await MatchAndSave(anidbAnimeID, anilistAnimeID, useExisting: true, save: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            logger.LogDebug(ex, "Unable to match the episodes of AniDB anime {AnidbID} against AniList anime {AnilistID}.", anidbAnimeID, anilistAnimeID);
        }
    }

    #endregion
}
