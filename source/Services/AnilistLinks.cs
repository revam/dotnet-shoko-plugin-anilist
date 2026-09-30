using System.Collections.Generic;
using System.Linq;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.CrossReferences;
using Shoko.Abstractions.Metadata.Storage;
using Shoko.Plugin.Anilist.Mapping;
using Shoko.Plugin.Anilist.Metadata;

namespace Shoko.Plugin.Anilist.Services;

/// <summary>
/// Reads the links between AniDB and AniList out of the core's
/// cross-reference store, by the IDs the plugin works with.
/// </summary>
/// <param name="crossReferences">The core's store of links.</param>
public sealed class AnilistLinks(IMetadataCrossReferenceStore crossReferences)
{
    #region From AniDB

    /// <summary>
    /// The AniList anime an AniDB anime is linked to.
    /// </summary>
    /// <param name="anidbAnimeID">The AniDB anime ID.</param>
    /// <returns>The links, in order.</returns>
    public IReadOnlyList<IMetadataSeriesCrossReference> GetSeriesLinks(int anidbAnimeID)
        => anidbAnimeID <= 0 ? [] : crossReferences.GetSeriesLinks(anidbAnimeID, AnilistSources.AniList);

    /// <summary>
    /// The AniList anime IDs an AniDB anime is linked to.
    /// </summary>
    /// <param name="anidbAnimeID">The AniDB anime ID.</param>
    /// <returns>The IDs, each once, in link order.</returns>
    public IReadOnlyList<int> GetLinkedAnimeIDs(int anidbAnimeID)
        => [.. GetSeriesLinks(anidbAnimeID)
            .Select(link => AnilistUtility.TryGetID(link.ProviderID, MetadataEntityType.Series, out var id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()];

    #endregion

    #region From AniList

    /// <summary>
    /// The series links claiming an AniList anime, read from its end.
    /// </summary>
    /// <param name="anilistAnimeID">The AniList anime ID.</param>
    /// <returns>The links.</returns>
    public IReadOnlyList<IMetadataSeriesCrossReference> GetLinksToAnime(int anilistAnimeID)
        => anilistAnimeID <= 0 ? [] : [.. crossReferences.GetLinksTo(AnilistUtility.SeriesGuid(anilistAnimeID)).OfType<IMetadataSeriesCrossReference>()];

    /// <summary>
    /// The episode links claiming an AniList episode, read from its end.
    /// </summary>
    /// <param name="anilistEpisodeID">The packed episode ID.</param>
    /// <returns>The links.</returns>
    public IReadOnlyList<IMetadataEpisodeCrossReference> GetLinksToEpisode(int anilistEpisodeID)
        => anilistEpisodeID <= 0 ? [] : [.. crossReferences.GetLinksTo(AnilistUtility.EpisodeGuid(anilistEpisodeID)).OfType<IMetadataEpisodeCrossReference>()];

    /// <summary>
    /// The AniDB anime linked to an AniList anime as a whole.
    /// </summary>
    /// <param name="anilistAnimeID">The AniList anime ID.</param>
    /// <returns>The AniDB anime IDs, each once.</returns>
    public IReadOnlyList<int> GetLinkedAnidbAnimeIDs(int anilistAnimeID)
        => [.. GetLinksToAnime(anilistAnimeID).Select(link => link.AnidbAnimeID).Distinct()];

    #endregion
}
