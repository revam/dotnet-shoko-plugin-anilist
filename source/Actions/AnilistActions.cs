using System;
using System.Threading;
using System.Threading.Tasks;
using Shoko.Abstractions.Actions;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Providers;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.ScheduledActions;
using Shoko.Plugin.Anilist.Metadata;

namespace Shoko.Plugin.Anilist.Actions;

/// <summary>
/// Removes every AniDB to AniList link, leaving every series' auto-linking
/// veto as it is.
/// </summary>
/// <param name="linkingService">The core's linking service, which does the removing.</param>
public sealed class PurgeAllAnilistLinksAction(IMetadataLinkingService linkingService) : IScheduledAction
{
    /// <inheritdoc/>
    public string Name => "Purge All AniList Links";

    /// <inheritdoc/>
    public string? Description => "Remove all AniDB-AniList links.";

    /// <inheritdoc/>
    public ActionCategory Category => ActionCategory.PluginInferred;

    /// <inheritdoc/>
    public bool RequiresConfirmation => true;

    /// <inheritdoc/>
    public string? ConfirmationMessage => "Are you sure you want to remove all AniDB-AniList links from the database?";

    /// <inheritdoc/>
    public Task Execute(IProgress<decimal> progress, CancellationToken token)
        => linkingService.RemoveAllLinks(AnilistSources.AniList, cancellationToken: token);
}

/// <summary>
/// Purges every stored AniList anime nothing links to.
/// </summary>
/// <param name="purgeService">The core's purge service, which queues the purges.</param>
public sealed class PurgeAllUnusedAnilistAnimeAction(IMetadataPurgeService purgeService) : IScheduledAction
{
    /// <inheritdoc/>
    public string Name => "Purge Unused AniList Anime";

    /// <inheritdoc/>
    public string? Description => "Remove all AniList anime that are not linked to any AniDB anime.";

    /// <inheritdoc/>
    public ActionCategory Category => ActionCategory.PluginInferred;

    /// <inheritdoc/>
    public bool RequiresConfirmation => true;

    /// <inheritdoc/>
    public string? ConfirmationMessage => "Are you sure you want to remove all unused AniList anime from the database?";

    /// <inheritdoc/>
    public Task Execute(IProgress<decimal> progress, CancellationToken token)
        => purgeService.PurgeUnused(AnilistSources.AniList, entityType: MetadataEntityType.Series, cancellationToken: token);
}

/// <summary>
/// Searches AniList for every series without a link, where the auto-linking
/// settings allow it.
/// </summary>
/// <param name="refreshService">The core's refresh service, which queues the searches.</param>
public sealed class SearchForAnilistMatchesAction(IMetadataRefreshService refreshService) : IScheduledAction
{
    /// <inheritdoc/>
    public string Name => "Search for AniList Matches";

    /// <inheritdoc/>
    public string? Description => "Scan for AniList anime matches for all unlinked AniDB anime.";

    /// <inheritdoc/>
    public ActionCategory Category => ActionCategory.PluginInferred;

    /// <inheritdoc/>
    public Task Execute(IProgress<decimal> progress, CancellationToken token)
        => refreshService.AutoSearchAll(AnilistSources.AniList, cancellationToken: token);
}

/// <summary>
/// Refreshes every linked AniList anime, without the images.
/// </summary>
/// <param name="refreshService">The core's refresh service, which queues the refreshes.</param>
public sealed class UpdateAllAnilistAnimeAction(IMetadataRefreshService refreshService) : IScheduledAction
{
    /// <inheritdoc/>
    public string Name => "Update All AniList Anime";

    /// <inheritdoc/>
    public string? Description => "Update all AniList anime metadata without downloading images.";

    /// <inheritdoc/>
    public ActionCategory Category => ActionCategory.PluginInferred;

    /// <inheritdoc/>
    public Task Execute(IProgress<decimal> progress, CancellationToken token)
        => refreshService.RefreshAllLinked(
            AnilistSources.AniList,
            force: true,
            new() { DownloadImages = false, Reason = MetadataRefreshReason.Requested },
            MetadataEntityType.Series,
            token
        );
}

/// <summary>
/// Refreshes every linked AniList anime, and downloads any missing images.
/// </summary>
/// <param name="refreshService">The core's refresh service, which queues the refreshes.</param>
public sealed class UpdateAllAnilistAnimeWithImagesAction(IMetadataRefreshService refreshService) : IScheduledAction
{
    /// <inheritdoc/>
    public string Name => "Update All AniList Anime (with Images)";

    /// <inheritdoc/>
    public string? Description => "Update all AniList anime metadata and download any missing images.";

    /// <inheritdoc/>
    public ActionCategory Category => ActionCategory.PluginInferred;

    /// <inheritdoc/>
    public Task Execute(IProgress<decimal> progress, CancellationToken token)
        => refreshService.RefreshAllLinked(
            AnilistSources.AniList,
            force: true,
            new() { DownloadImages = true, Reason = MetadataRefreshReason.Requested },
            MetadataEntityType.Series,
            token
        );
}
