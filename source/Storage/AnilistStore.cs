using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Storage;
using Shoko.Plugin.Anilist.Mapping;

namespace Shoko.Plugin.Anilist.Storage;

/// <summary>
/// Everything the plugin writes: the anime, its episodes, tags, studios,
/// people, relations and suggestions in the core's typed stores, and the
/// little only AniList has in the plugin's own database.
/// </summary>
/// <remarks>
/// Every call opens a context of its own. Writes are serialized, so two
/// refreshes sharing a portrait never race to insert it.
/// </remarks>
public sealed class AnilistStore
{
    private readonly IDbContextFactory<AnilistDbContext> _contexts;

    private readonly Lock _writeLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="AnilistStore"/> class.
    /// </summary>
    /// <param name="contexts">Creates contexts on the plugin's own database.</param>
    /// <param name="series">The core's store of series and their episodes.</param>
    /// <param name="people">The core's store of characters, people and credits.</param>
    /// <param name="tags">The core's store of tags and genres.</param>
    /// <param name="studios">The core's store of studios.</param>
    /// <param name="relations">The core's store of relations.</param>
    /// <param name="suggestions">The core's store of suggestions.</param>
    /// <exception cref="ArgumentNullException"><paramref name="contexts"/> is <see langword="null"/>.</exception>
    public AnilistStore(
        IDbContextFactory<AnilistDbContext> contexts,
        IMetadataSeriesStore series,
        IMetadataPeopleStore people,
        IMetadataTagStore tags,
        IMetadataStudioStore studios,
        IMetadataRelationStore relations,
        IMetadataSuggestionStore suggestions
    )
    {
        ArgumentNullException.ThrowIfNull(contexts);

        _contexts = contexts;
        Series = series;
        People = people;
        Tags = tags;
        Studios = studios;
        Relations = relations;
        Suggestions = suggestions;
    }

    #region Typed Stores

    /// <summary>The core's store of series and their episodes.</summary>
    public IMetadataSeriesStore Series { get; }

    /// <summary>The core's store of characters, people and credits.</summary>
    public IMetadataPeopleStore People { get; }

    /// <summary>The core's store of tags and genres.</summary>
    public IMetadataTagStore Tags { get; }

    /// <summary>The core's store of studios.</summary>
    public IMetadataStudioStore Studios { get; }

    /// <summary>The core's store of relations.</summary>
    public IMetadataRelationStore Relations { get; }

    /// <summary>The core's store of suggestions.</summary>
    public IMetadataSuggestionStore Suggestions { get; }

    /// <summary>
    /// A stored anime, as the core reads it back.
    /// </summary>
    /// <param name="anilistAnimeID">The AniList anime ID.</param>
    /// <returns>The series, or <see langword="null"/> when it is not stored.</returns>
    public ISeries? GetSeries(int anilistAnimeID)
        => anilistAnimeID <= 0 ? null : Series.GetSeries(AnilistUtility.SeriesGuid(anilistAnimeID));

    #endregion

    #region Anime

    /// <summary>
    /// What the plugin keeps of one anime besides the core's stores.
    /// </summary>
    /// <param name="anilistAnimeID">The AniList anime ID.</param>
    /// <returns>The record, or <see langword="null"/> when there is none.</returns>
    public AnilistStoredAnime? GetAnime(int anilistAnimeID)
    {
        if (anilistAnimeID <= 0)
            return null;

        using var context = _contexts.CreateDbContext();
        return context.Anime.AsNoTracking().FirstOrDefault(anime => anime.ID == anilistAnimeID);
    }

    /// <summary>
    /// Every anime record.
    /// </summary>
    /// <returns>The records, by ID.</returns>
    public IReadOnlyList<AnilistStoredAnime> GetAllAnime()
    {
        using var context = _contexts.CreateDbContext();
        return [.. context.Anime.AsNoTracking().OrderBy(anime => anime.ID)];
    }

    /// <summary>
    /// Stores an anime record, replacing what was stored before.
    /// </summary>
    /// <param name="anime">The record.</param>
    /// <exception cref="ArgumentNullException"><paramref name="anime"/> is <see langword="null"/>.</exception>
    public void SaveAnime(AnilistStoredAnime anime)
    {
        ArgumentNullException.ThrowIfNull(anime);

        lock (_writeLock)
        {
            using var context = _contexts.CreateDbContext();
            if (context.Anime.Any(row => row.ID == anime.ID))
                context.Anime.Update(anime);
            else
                context.Anime.Add(anime);
            context.SaveChanges();
        }
    }

    /// <summary>
    /// Forgets what the plugin keeps of an anime: its record and its
    /// episodes' records. The core's stores are the core's to purge.
    /// </summary>
    /// <param name="anilistAnimeID">The AniList anime ID.</param>
    /// <returns>Whether there was anything to forget.</returns>
    public bool RemoveAnime(int anilistAnimeID)
    {
        lock (_writeLock)
        {
            using var context = _contexts.CreateDbContext();
            using var transaction = context.Database.BeginTransaction();
            var removed = context.Anime.Where(anime => anime.ID == anilistAnimeID).ExecuteDelete();
            removed += context.Episodes.Where(episode => episode.AnimeID == anilistAnimeID).ExecuteDelete();
            transaction.Commit();
            return removed > 0;
        }
    }

    #endregion

    #region Episodes

    /// <summary>
    /// What the plugin keeps of one episode besides the core's store.
    /// </summary>
    /// <param name="anilistEpisodeID">The packed episode ID.</param>
    /// <returns>The record, or <see langword="null"/> when there is none.</returns>
    public AnilistStoredEpisode? GetEpisode(int anilistEpisodeID)
    {
        if (anilistEpisodeID <= 0)
            return null;

        using var context = _contexts.CreateDbContext();
        return context.Episodes.AsNoTracking().FirstOrDefault(episode => episode.ID == anilistEpisodeID);
    }

    /// <summary>
    /// The episode records of one anime.
    /// </summary>
    /// <param name="anilistAnimeID">The AniList anime ID.</param>
    /// <returns>The records, by number.</returns>
    public IReadOnlyList<AnilistStoredEpisode> GetEpisodesForAnime(int anilistAnimeID)
    {
        if (anilistAnimeID <= 0)
            return [];

        using var context = _contexts.CreateDbContext();
        return [.. context.Episodes.AsNoTracking().Where(episode => episode.AnimeID == anilistAnimeID).OrderBy(episode => episode.EpisodeNumber)];
    }

    /// <summary>
    /// Makes an anime's episode records exactly the ones given.
    /// </summary>
    /// <param name="anilistAnimeID">The AniList anime ID.</param>
    /// <param name="episodes">The records, each of that anime.</param>
    /// <returns>How many records were removed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="episodes"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A record is <see langword="null"/>, of another anime, or shares its ID with another.</exception>
    public int ReplaceEpisodes(int anilistAnimeID, IEnumerable<AnilistStoredEpisode> episodes)
    {
        ArgumentNullException.ThrowIfNull(episodes);

        var kept = new Dictionary<int, AnilistStoredEpisode>();
        foreach (var episode in episodes)
        {
            if (episode is null)
                throw new ArgumentException("An episode is null.", nameof(episodes));
            if (episode.AnimeID != anilistAnimeID)
                throw new ArgumentException($"Episode {episode.ID} does not belong to anime {anilistAnimeID}.", nameof(episodes));
            if (!kept.TryAdd(episode.ID, episode))
                throw new ArgumentException($"Episode {episode.ID} is given twice.", nameof(episodes));
        }

        lock (_writeLock)
        {
            using var context = _contexts.CreateDbContext();
            using var transaction = context.Database.BeginTransaction();
            var ids = kept.Keys.ToList();
            var removed = context.Episodes.Where(episode => episode.AnimeID == anilistAnimeID && !ids.Contains(episode.ID)).ExecuteDelete();
            var existing = context.Episodes.Where(episode => ids.Contains(episode.ID)).Select(episode => episode.ID).ToHashSet();
            foreach (var episode in kept.Values)
            {
                if (existing.Contains(episode.ID))
                    context.Episodes.Update(episode);
                else
                    context.Episodes.Add(episode);
            }

            context.SaveChanges();
            transaction.Commit();
            return removed;
        }
    }

    #endregion

    #region Portraits

    /// <summary>
    /// The key a portrait is filed under: the person's or character's
    /// identifier without its source.
    /// </summary>
    /// <param name="id">The creator or character.</param>
    /// <returns>The key, e.g. <c>creator/95</c>.</returns>
    public static string PortraitKey(MetadataGuid id)
    {
        ArgumentNullException.ThrowIfNull(id);

        return $"{id.EntityType.Value}/{id.ID}";
    }

    /// <summary>
    /// The portrait of a creator or a character, when AniList had one.
    /// </summary>
    /// <param name="id">The creator or character.</param>
    /// <returns>The image's resource ID, or <see langword="null"/>.</returns>
    public string? GetPortrait(MetadataGuid id)
    {
        var key = PortraitKey(id);
        using var context = _contexts.CreateDbContext();
        return context.Portraits.AsNoTracking().Where(portrait => portrait.Key == key).Select(portrait => portrait.ImagePath).FirstOrDefault() is { Length: > 0 } path
            ? path
            : null;
    }

    /// <summary>
    /// Stores the portraits of creators and characters, replacing the ones
    /// stored for them before.
    /// </summary>
    /// <param name="portraits">The portraits, by the creator or character.</param>
    public void SavePortraits(IEnumerable<KeyValuePair<MetadataGuid, string>> portraits)
    {
        ArgumentNullException.ThrowIfNull(portraits);

        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, path) in portraits)
        {
            if (!string.IsNullOrEmpty(path))
                paths[PortraitKey(id)] = path;
        }

        if (paths.Count == 0)
            return;

        lock (_writeLock)
        {
            using var context = _contexts.CreateDbContext();
            var keys = paths.Keys.ToList();
            foreach (var existing in context.Portraits.Where(portrait => keys.Contains(portrait.Key)))
            {
                existing.ImagePath = paths[existing.Key];
                paths.Remove(existing.Key);
            }

            context.Portraits.AddRange(paths.Select(pair => new AnilistStoredPortrait { Key = pair.Key, ImagePath = pair.Value }));
            context.SaveChanges();
        }
    }

    /// <summary>
    /// Forgets the portraits of the creators and characters the core's people
    /// store no longer holds, since it purges them on its own schedule.
    /// </summary>
    /// <returns>How many portraits were forgotten.</returns>
    public int RemoveOrphanedPortraits()
    {
        lock (_writeLock)
        {
            using var context = _contexts.CreateDbContext();
            var gone = context.Portraits.Select(portrait => portrait.Key).AsEnumerable().Where(key => !IsHeld(key)).ToList();
            return gone.Count == 0 ? 0 : context.Portraits.Where(portrait => gone.Contains(portrait.Key)).ExecuteDelete();
        }
    }

    // Whether the core's people store still holds the creator or character a
    // portrait key names.
    private bool IsHeld(string key)
    {
        if (key.Split('/', 2) is not [var kind, var id] || !MetadataEntityType.TryGet(kind, out var entityType) || string.IsNullOrEmpty(id))
            return false;

        var guid = new MetadataGuid(Metadata.AnilistSources.AniList, entityType, id);
        return entityType == MetadataEntityType.Creator ? People.GetCreator(guid) is not null
            : entityType == MetadataEntityType.Character && People.GetCharacter(guid) is not null;
    }

    #endregion
}
