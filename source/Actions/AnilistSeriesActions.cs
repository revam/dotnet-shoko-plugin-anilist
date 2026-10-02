using System.Threading;
using System.Threading.Tasks;
using Shoko.Abstractions.Actions;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Plugin.Anilist.Metadata;
using Shoko.Plugin.Anilist.Services;

namespace Shoko.Plugin.Anilist.Actions;

/// <summary>
/// Matches the series' episodes against its first linked AniList anime,
/// keeping the links already there.
/// </summary>
/// <param name="links">Reads the links.</param>
/// <param name="linkingService">Does the matching.</param>
public sealed class AutoMatchAnilistEpisodesSeriesAction(AnilistLinks links, AnilistLinkingService linkingService) : SeriesAction
{
    /// <inheritdoc/>
    public override string Name => "Auto-Match AniList Episodes";

    /// <inheritdoc/>
    public override string? Description => "Automatically matches Shoko episodes with AniList episodes.";

    /// <inheritdoc/>
    public override ActionCategory Category => ActionCategory.PluginInferred;

    /// <inheritdoc/>
    public override ActionPermission Permission => ActionPermission.Admin;

    /// <inheritdoc/>
    public override async Task Execute(CancellationToken token = default)
    {
        if (links.GetLinkedAnimeIDs(Series.AnidbAnimeID) is [var anilistAnimeID, ..])
            await linkingService.MatchAndSave(Series.AnidbAnimeID, anilistAnimeID, useExisting: true, save: true, cancellationToken: token).ConfigureAwait(false);
    }
}

/// <summary>
/// Queues a search for an AniList anime to link the series to, however it is
/// linked already.
/// </summary>
/// <param name="refreshService">The core's refresh service, which queues the search.</param>
public sealed class AutoSearchAnilistSeriesAction(IMetadataRefreshService refreshService) : SeriesAction
{
    /// <inheritdoc/>
    public override string Name => "Auto-Search AniList Match";

    /// <inheritdoc/>
    public override string? Description => "Automatically searches for an AniList match.";

    /// <inheritdoc/>
    public override ActionCategory Category => ActionCategory.PluginInferred;

    /// <inheritdoc/>
    public override ActionPermission Permission => ActionPermission.User;

    /// <inheritdoc/>
    public override Task Execute(CancellationToken token = default)
        => refreshService.AutoSearch(MetadataSource.AniList, Series.AnidbAnimeID, force: true, token);
}

/// <summary>
/// Resets the series' AniList episode links, leaving them for automatic
/// matching to fill in again.
/// </summary>
/// <param name="linkingService">Does the resetting.</param>
public sealed class ResetAnilistEpisodeMappingsSeriesAction(AnilistLinkingService linkingService) : SeriesAction
{
    /// <inheritdoc/>
    public override string Name => "Reset AniList Episode Mappings";

    /// <inheritdoc/>
    public override string? Description => "Reset all AniList episode mappings for the series.";

    /// <inheritdoc/>
    public override ActionCategory Category => ActionCategory.PluginInferred;

    /// <inheritdoc/>
    public override ActionPermission Permission => ActionPermission.Admin;

    /// <inheritdoc/>
    public override bool RequiresConfirmation => true;

    /// <inheritdoc/>
    public override string? ConfirmationMessage => "Are you sure you want to reset all AniList episode mappings for this series?";

    /// <inheritdoc/>
    public override Task Execute(CancellationToken token = default)
        => linkingService.ResetEpisodeLinks(Series.AnidbAnimeID, allowAutoMatch: true, token);
}

/// <summary>
/// Downloads every image of the series' AniList anime again.
/// </summary>
/// <param name="refreshService">The core's refresh service, which queues the image jobs.</param>
public sealed class UpdateAnilistImagesForceSeriesAction(IMetadataRefreshService refreshService) : SeriesAction
{
    /// <inheritdoc/>
    public override string Name => "Update AniList Images - Force";

    /// <inheritdoc/>
    public override string? Description => "Forces a complete redownload of images from AniList.";

    /// <inheritdoc/>
    public override ActionCategory Category => ActionCategory.PluginInferred;

    /// <inheritdoc/>
    public override ActionPermission Permission => ActionPermission.Admin;

    /// <inheritdoc/>
    public override Task Execute(CancellationToken token = default)
        => refreshService.DownloadImagesForAnime(Series.AnidbAnimeID, MetadataSource.AniList, force: true, token);
}

/// <summary>
/// Refreshes the series' AniList anime.
/// </summary>
/// <param name="refreshService">The core's refresh service, which queues the refreshes.</param>
public sealed class UpdateAnilistInfoSeriesAction(IMetadataRefreshService refreshService) : SeriesAction
{
    /// <inheritdoc/>
    public override string Name => "Update AniList Info";

    /// <inheritdoc/>
    public override string? Description => "Gets the latest series information from AniList.";

    /// <inheritdoc/>
    public override ActionCategory Category => ActionCategory.PluginInferred;

    /// <inheritdoc/>
    public override ActionPermission Permission => ActionPermission.Admin;

    /// <inheritdoc/>
    public override Task Execute(CancellationToken token = default)
        => refreshService.RefreshForAnime(Series.AnidbAnimeID, MetadataSource.AniList, force: true, cancellationToken: token);
}
