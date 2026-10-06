using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Airing;
using Shoko.Abstractions.Metadata.Anidb;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Plugin.Anilist.Mapping;
using Shoko.Plugin.Anilist.Metadata;
using Shoko.Plugin.Anilist.Services;

namespace Shoko.Plugin.Anilist.Airing;

/// <summary>
/// Supplies the broadcast times AniList reports. AniList names neither a
/// station nor a platform, so everything it knows lands on one channel-less
/// schedule per anime, with a single original-language track.
/// </summary>
/// <remarks>
/// A refresh only asks the core to refresh the anime. The schedule is written
/// by the refresh itself, through <see cref="WriteSchedule"/>, since that is
/// where the airing schedule arrives. Registered as a singleton so the
/// refresh writes through the very instance the core discovered, which is
/// what every write is checked against.
/// </remarks>
/// <param name="airingScheduleService">The core's airing schedule service.</param>
/// <param name="refreshService">The core's refresh service, asked to refresh the anime.</param>
/// <param name="links">Reads the links.</param>
/// <param name="logger">The logger.</param>
public sealed class AnilistAiringScheduleProvider(
    IAiringScheduleService airingScheduleService,
    IMetadataRefreshService refreshService,
    AnilistLinks links,
    ILogger<AnilistAiringScheduleProvider> logger
) : IAiringScheduleProvider
{
    /// <summary>
    /// The key of the one schedule AniList supplies per anime.
    /// </summary>
    public const string ScheduleKey = "original";

    #region Provider

    /// <inheritdoc/>
    public string Name => "AniList";

    /// <inheritdoc/>
    public string Description => "Broadcast times from AniList's airing schedule, as one channel-less schedule per anime.";

    /// <inheritdoc/>
    public IReadOnlySet<AiringKind> AvailableKinds { get; } = new HashSet<AiringKind> { AiringKind.Original };

    #endregion

    #region Refreshing

    /// <summary>
    /// Asks the core to refresh every AniList anime the series can be keyed
    /// to, which writes the schedule as it goes.
    /// </summary>
    /// <param name="series">The series: an AniList anime, or anything linked to one.</param>
    /// <param name="cancellationToken">Cancels the refresh.</param>
    /// <returns>Whether there was anything to refresh.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="series"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<bool> RefreshAsync(ISeries series, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(series);
        cancellationToken.ThrowIfCancellationRequested();

        var anilistAnimeIDs = GetAnilistAnimeIDs(series);
        if (anilistAnimeIDs.Count is 0)
        {
            logger.LogDebug("No AniList anime is linked to series {SeriesID}. Nothing to refresh.", series.ID);
            return false;
        }

        foreach (var anilistAnimeID in anilistAnimeIDs)
        {
            // A refresh in flight rewrites the schedule when it lands.
            var seriesID = AnilistUtility.SeriesGuid(anilistAnimeID);
            if (refreshService.IsRefreshing(seriesID))
            {
                logger.LogDebug("AniList anime {AnimeID} is already refreshing. Leaving the schedule to it.", anilistAnimeID);
                continue;
            }

            await refreshService.RefreshEntry(seriesID, options: new() { DownloadImages = false }, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// The AniList anime a series can be keyed to: itself when it is one, and
    /// otherwise everything linked to the AniDB anime behind it.
    /// </summary>
    /// <param name="series">The series.</param>
    /// <returns>The AniList anime IDs, empty when the series cannot be keyed.</returns>
    public IReadOnlyList<int> GetAnilistAnimeIDs(ISeries series)
    {
        ArgumentNullException.ThrowIfNull(series);

        if (series.Source == MetadataSource.AniList)
            return AnilistUtility.TryGetID(series.ID, MetadataEntityType.Series, out var id) ? [id] : [];

        var anidbAnimeIDs = new HashSet<int>();
        if (series is IAnidbAnime anidbAnime)
            anidbAnimeIDs.Add(anidbAnime.AnidbID);
        foreach (var shokoSeries in series is IShokoSeries own ? [own] : series.ShokoSeries)
            anidbAnimeIDs.Add(shokoSeries.AnidbAnimeID);

        return [.. anidbAnimeIDs.SelectMany(links.GetLinkedAnimeIDs).Distinct()];
    }

    #endregion

    #region Writing

    /// <summary>
    /// Pushes an anime's broadcast times as this provider's schedule for it,
    /// with one airing per episode AniList knows a slot for.
    /// </summary>
    /// <remarks>
    /// A failure is logged and swallowed. The schedule is a view of the
    /// metadata, so losing it must not sink the refresh that produced it.
    /// </remarks>
    /// <param name="series">The anime, as the core's series store reads it back.</param>
    /// <param name="media">The anime as AniList just described it.</param>
    /// <param name="schedule">The slots, by episode number.</param>
    public void WriteSchedule(ISeries series, AnilistMedia media, IReadOnlyDictionary<int, AnilistScheduleEntry> schedule)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(schedule);

        try
        {
            if (!IsRegistered())
                return;

            // An anime AniList knows no broadcast times for gets no schedule,
            // unless it already has one to empty out.
            if (schedule.Count is 0 && GetOwnSchedules(series.ID).Count is 0)
                return;

            int? lastEpisodeNumber = media.EpisodeCount > 0 ? media.EpisodeCount : null;
            var languageCode = string.IsNullOrWhiteSpace(media.OriginalLanguageCode) ? "unk" : media.OriginalLanguageCode;
            var view = airingScheduleService.AddOrUpdateSchedule(this, new()
            {
                Series = series,
                Key = ScheduleKey,
                Tracks = [new AiringTrackData(AiringKind.Original, languageCode)],
                FirstEpisodeNumber = 1,
                LastEpisodeNumber = lastEpisodeNumber,
                IsFinished = media.Status is ReleaseStatus.Finished,
            });

            // The episode number is the airing's place on the line, which the
            // core resolves to an episode when it reads the airing.
            var airings = schedule
                .OrderBy(entry => entry.Key)
                .Where(entry => entry.Key >= 1 && (lastEpisodeNumber is null || entry.Key <= lastEpisodeNumber))
                .Select(entry => new EpisodeAiringData { SequenceNumber = entry.Key, AiredAt = entry.Value.AiredAt })
                .ToList();
            try
            {
                airingScheduleService.SetAirings(this, view, airings);
            }
            catch (AiringScheduleValidationException ex) when (ex.ValidationErrors.Keys.All(key => key is "#schedule"))
            {
                // The whole run aged out of the retention window. An empty
                // line is what clears the schedule rather than being refused.
                logger.LogDebug("The airing schedule for AniList anime {AnimeID} is past the retention window. Clearing it.", media.ID);
                airingScheduleService.SetAirings(this, view, []);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to update the airing schedule for AniList anime {AnimeID}.", media.ID);
        }
    }

    /// <summary>
    /// Removes the schedules this provider owns for an anime being purged.
    /// </summary>
    /// <param name="seriesID">The anime.</param>
    /// <returns>How many schedules were removed.</returns>
    public int RemoveSchedules(MetadataGuid seriesID)
    {
        ArgumentNullException.ThrowIfNull(seriesID);

        try
        {
            if (!IsRegistered())
                return 0;

            var removed = 0;
            foreach (var schedule in GetOwnSchedules(seriesID))
                if (airingScheduleService.RemoveSchedule(this, schedule))
                    removed++;

            return removed;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to remove the airing schedules for {SeriesID}.", seriesID);
            return 0;
        }
    }

    private IReadOnlyList<IAiringSchedule> GetOwnSchedules(MetadataGuid seriesID)
        => [.. airingScheduleService.GetSchedulesForProvider(airingScheduleService.GetProviderInfo(this).ID, new() { IncludeDisabled = true })
            .Where(schedule => schedule.SeriesID == seriesID)];

    // Until the plugins are initialised, and in a server that refused this
    // provider, there is no registered instance to write as.
    private bool IsRegistered()
    {
        try
        {
            _ = airingScheduleService.GetProviderInfo(this);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            logger.LogDebug(ex, "The AniList airing schedule provider isn't registered. Skipping the airing schedule write.");
            return false;
        }
    }

    #endregion
}
