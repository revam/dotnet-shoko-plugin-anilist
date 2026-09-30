using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Providers;
using Shoko.Abstractions.Metadata.Storage;
using Shoko.Plugin.Anilist.Airing;
using Shoko.Plugin.Anilist.Api;
using Shoko.Plugin.Anilist.Mapping;
using Shoko.Plugin.Anilist.Storage;

namespace Shoko.Plugin.Anilist.Services;

/// <summary>
/// The half of the plugin that fetches an anime from AniList and writes it
/// into the core's stores.
/// </summary>
/// <remarks>
/// The core's refresh job calls in through the provider. It has already
/// decided the anime is due and holds its lock, so nothing here checks
/// freshness or locks anything.
/// </remarks>
/// <param name="apiClient">The AniList client.</param>
/// <param name="store">The plugin's store.</param>
/// <param name="linkingService">Matches the linked anime's episodes again after a refresh.</param>
/// <param name="airingScheduleProvider">Writes the broadcast times.</param>
/// <param name="configurationProvider">The plugin's configuration.</param>
/// <param name="logger">The logger.</param>
public sealed class AnilistRefreshService(
    AnilistApiClient apiClient,
    AnilistStore store,
    AnilistLinkingService linkingService,
    AnilistAiringScheduleProvider airingScheduleProvider,
    ConfigurationProvider<AnilistConfiguration> configurationProvider,
    ILogger<AnilistRefreshService> logger
)
{
    /// <summary>
    /// The lowest rating a page of recommendations may end on and still be
    /// worth asking for the next one under
    /// <see cref="AnilistRecommendationDepth.WhileWellRated"/>.
    /// </summary>
    private const int MinimumRatingToKeepPaging = 10;

    /// <summary>
    /// Fetches an anime and everything the settings and options ask for, and
    /// writes it into the stores.
    /// </summary>
    /// <remarks>
    /// A quick refresh leaves out the cast and crew and the matching of the
    /// linked anime's episodes, and leaves the plugin's document looking
    /// newly added. An anime AniList no longer has is left as it was stored.
    /// </remarks>
    /// <param name="anilistAnimeID">The AniList anime ID.</param>
    /// <param name="options">What kind of refresh it is.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether AniList had the anime.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="AnilistApiException">AniList answered with something unexpected.</exception>
    /// <exception cref="AnilistUnavailableException">AniList cannot be reached for now.</exception>
    public async Task<bool> RefreshAnime(int anilistAnimeID, MetadataRefreshOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (anilistAnimeID <= 0)
            return false;

        logger.LogInformation("Refreshing AniList anime {AnimeID}", anilistAnimeID);
        if (await apiClient.GetAnimeAsync(anilistAnimeID, cancellationToken).ConfigureAwait(false) is not { } node)
        {
            logger.LogWarning("AniList has no anime with ID {AnimeID}. Keeping what is stored.", anilistAnimeID);
            return false;
        }

        var configuration = configurationProvider.Load();
        var now = DateTime.UtcNow;
        var media = AnilistMediaMapper.ReadMedia(node);
        var seriesID = AnilistUtility.SeriesGuid(anilistAnimeID);

        // The anime, with its episodes.
        var schedule = await ReadSchedule(anilistAnimeID, node, cancellationToken).ConfigureAwait(false);
        var existingEpisodes = store.GetEpisodesForAnime(anilistAnimeID);
        var (episodes, episodesChanged) = AnilistMediaMapper.BuildEpisodes(media, schedule, existingEpisodes, now);
        var changes = store.Series.SaveSeries(AnilistMediaMapper.ToSeriesData(media, episodes));
        var suggestions = await ReadSuggestions(anilistAnimeID, node, configuration.RecommendationDepth, cancellationToken).ConfigureAwait(false);
        var document = AnilistMediaMapper.ToStoredAnime(media, store.GetAnime(anilistAnimeID), now, options.QuickRefresh);
        document.Recommendations = [.. suggestions
            .Select(suggestion => (Suggestion: suggestion, ID: AnilistUtility.TryGetID(suggestion.SuggestedID, MetadataEntityType.Series, out var id) ? id : 0))
            .Where(pair => pair.ID > 0)
            .Select(pair => new AnilistStoredRecommendation { ID = pair.ID, Score = pair.Suggestion.Score })];
        store.SaveAnime(document);
        store.ReplaceEpisodes(anilistAnimeID, episodes);
        if (episodesChanged)
            logger.LogDebug("Made up the episodes of AniList anime {AnimeID}: {Count} in all, {Scheduled} with a schedule entry.", anilistAnimeID, episodes.Count, schedule.Count);

        // What the other stores hold for it.
        var (tags, entryTags) = AnilistMediaMapper.ReadTags(node);
        var genres = AnilistMediaMapper.GenresAsTags(media.Genres);
        store.Tags.SaveTags([.. tags, .. genres]);
        store.Tags.SetTags(seriesID, [.. entryTags, .. AnilistMediaMapper.GenreEntries(media.Genres)]);
        UpdateStudios(seriesID, node, configuration.AutoDownloadStudios);
        store.Relations.SetRelations(seriesID, AnilistMediaMapper.ReadRelations(node));
        store.Suggestions.SetSuggestions(seriesID, AnilistMediaMapper.MergeSuggestions(anilistAnimeID, suggestions, ReverseSuggestions(anilistAnimeID)));
        if (!options.QuickRefresh)
        {
            var castAndCrew = options.DownloadCrewAndCast;
            await UpdatePeople(media, node, castAndCrew ?? configuration.AutoDownloadCharacters, castAndCrew ?? configuration.AutoDownloadStaff, cancellationToken).ConfigureAwait(false);
        }

        // The broadcast times, and the links of the anime linked to it.
        if (store.GetSeries(anilistAnimeID) is { } series)
            airingScheduleProvider.WriteSchedule(series, media, schedule);

        if (!options.QuickRefresh)
            await linkingService.MatchLinkedEpisodes(anilistAnimeID, cancellationToken).ConfigureAwait(false);

        logger.LogInformation("Refreshed AniList anime {AnimeID} ({Title}), {Changes} changes to the series and its episodes.", anilistAnimeID, string.IsNullOrEmpty(media.EnglishTitle) ? media.MainTitle : media.EnglishTitle, changes);
        return true;
    }

    private async Task<Dictionary<int, AnilistScheduleEntry>> ReadSchedule(int anilistAnimeID, JsonNode media, CancellationToken cancellationToken)
    {
        var schedule = new Dictionary<int, AnilistScheduleEntry>();
        var page = media["airingSchedule"];
        while (page is not null)
        {
            AnilistMediaMapper.ReadSchedulePage(page, schedule);
            var (hasNextPage, nextPage) = AnilistMediaMapper.ReadPageInfo(page);
            if (!hasNextPage)
                break;

            page = await apiClient.GetAiringSchedulePageAsync(anilistAnimeID, nextPage, cancellationToken).ConfigureAwait(false);
        }

        return schedule;
    }

    private void UpdateStudios(MetadataGuid seriesID, JsonNode media, bool enabled)
    {
        if (!enabled)
        {
            store.Studios.RemoveStudios(seriesID);
            return;
        }

        var (studios, entries) = AnilistMediaMapper.ReadStudios(media);
        store.Studios.SaveStudios(studios);
        store.Studios.SetStudios(seriesID, entries);
    }

    private async Task<List<MetadataSuggestionData>> ReadSuggestions(int anilistAnimeID, JsonNode media, AnilistRecommendationDepth depth, CancellationToken cancellationToken)
    {
        var suggestions = new List<MetadataSuggestionData>();
        var page = media["recommendations"];
        while (page is not null)
        {
            var lowestRating = AnilistMediaMapper.ReadRecommendationPage(page, anilistAnimeID, suggestions);
            var (hasNextPage, nextPage) = AnilistMediaMapper.ReadPageInfo(page);
            if (!hasNextPage || depth is AnilistRecommendationDepth.FirstPage)
                break;

            if (depth is AnilistRecommendationDepth.WhileWellRated && lowestRating < MinimumRatingToKeepPaging)
                break;

            page = await apiClient.GetRecommendationsPageAsync(anilistAnimeID, nextPage, cancellationToken).ConfigureAwait(false);
        }

        return suggestions;
    }

    // What the other stored anime recommend of their own accord naming this
    // one, which AniList serves from both sides with the same score. The
    // merged lists are not read back, so an edge AniList dropped goes once
    // both ends are refreshed.
    private IEnumerable<(MetadataGuid SuggestedBy, int? Score)> ReverseSuggestions(int anilistAnimeID)
        => store.GetAllAnime()
            .Where(anime => anime.ID != anilistAnimeID)
            .SelectMany(anime => anime.Recommendations
                .Where(recommendation => recommendation.ID == anilistAnimeID)
                .Select(recommendation => (AnilistUtility.SeriesGuid(anime.ID), recommendation.Score)));

    private async Task UpdatePeople(AnilistMedia media, JsonNode node, bool fetchCharacters, bool fetchStaff, CancellationToken cancellationToken)
    {
        if (!fetchCharacters && !fetchStaff)
            return;

        var seriesID = AnilistUtility.SeriesGuid(media.ID);
        var people = new AnilistPeople();
        if (fetchCharacters)
        {
            var page = node["characters"];
            while (page is not null)
            {
                AnilistMediaMapper.ReadCharacterPage(page, people);
                var (hasNextPage, nextPage) = AnilistMediaMapper.ReadPageInfo(page);
                if (!hasNextPage)
                    break;

                page = await apiClient.GetCharactersPageAsync(media.ID, nextPage, cancellationToken).ConfigureAwait(false);
            }
        }

        if (fetchStaff)
        {
            var page = node["staff"];
            while (page is not null)
            {
                AnilistMediaMapper.ReadStaffPage(page, people, media.OriginalLanguageCode);
                var (hasNextPage, nextPage) = AnilistMediaMapper.ReadPageInfo(page);
                if (!hasNextPage)
                    break;

                page = await apiClient.GetStaffPageAsync(media.ID, nextPage, cancellationToken).ConfigureAwait(false);
            }
        }

        store.People.SaveCreators(people.Creators.Values);
        store.People.SaveCharacters(people.Characters.Values);
        store.SavePortraits(people.Portraits);
        if (fetchCharacters)
            store.People.SetCast(seriesID, people.Cast);
        if (fetchStaff)
            store.People.SetCrew(seriesID, people.Crew);
    }
}
