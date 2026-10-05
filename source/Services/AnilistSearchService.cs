using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Anidb;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Matching;
using Shoko.Abstractions.Metadata.Search;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Plugin.Anilist.Api;
using Shoko.Plugin.Anilist.Mapping;
using Shoko.Plugin.Anilist.Metadata;
using Shoko.Plugin.Anilist.Storage;

namespace Shoko.Plugin.Anilist.Services;

/// <summary>
/// One AniList anime the automatic search scored for an AniDB anime, taken or
/// turned down.
/// </summary>
/// <param name="AnidbAnimeID">The AniDB anime searched for.</param>
/// <param name="Anime">The AniList anime found.</param>
/// <param name="Candidate">The anime as the matching engine judged it.</param>
/// <param name="MatchRating">How well it matched.</param>
public sealed record AnilistAutoMatch(int AnidbAnimeID, AnilistSearchResult Anime, MetadataSeriesSearchResult Candidate, MatchRating MatchRating)
{
    /// <summary>
    /// Why it was not taken, or <see langword="null"/> for the one taken.
    /// </summary>
    public MetadataAutoLinkRejection? Rejection { get; set; }

    /// <summary>
    /// Whether its episodes lined up with the anime's by their air dates.
    /// </summary>
    public bool IsAligned { get; init; }
}

/// <summary>
/// Searches AniList, and works out which anime an AniDB anime is.
/// </summary>
/// <remarks>
/// The automatic search tries the original title, the English one and the
/// main one, each with and without a sequel suffix and a subtitle, with and
/// without the year, and has the core's <see cref="IMetadataMatchingEngine"/>
/// judge the candidates on their titles, their start date and their episode
/// count. AniList's search results carry all of that, and the next episode
/// to air, which tells how far an airing anime of unknown length has got.
/// Only the best few candidates cost a request more, one for all of them,
/// for the air dates of their episodes, which the engine lines up with the
/// anime's. Candidates of unknown length left tied for the top cost one more,
/// for their whole schedules, which tell their length.
/// </remarks>
public sealed partial class AnilistSearchService
{
    /// <summary>
    /// How far into the future an anime may start and still be searched for.
    /// </summary>
    private static readonly TimeSpan _maxTimeIntoTheFuture = TimeSpan.FromDays(15);

    /// <summary>
    /// How far either side of the anime's dated episodes a candidate's airing
    /// schedule is read.
    /// </summary>
    private static readonly TimeSpan _alignmentMargin = TimeSpan.FromDays(30);

    /// <summary>
    /// Japan's offset from UTC, AniDB dating episodes by their Japanese
    /// broadcast.
    /// </summary>
    private static readonly TimeSpan _japanOffset = TimeSpan.FromHours(9);

    /// <summary>
    /// How many pages of airing schedules one fetch for the candidates reads
    /// at the most.
    /// </summary>
    private const int MaxSchedulePages = 4;

    /// <summary>
    /// How many days before the anime's regular broadcast a candidate may
    /// begin and still be the anime, as the matching engine counts them.
    /// </summary>
    private const int MaxDaysStartedBeforeStart = 180;

    /// <summary>
    /// How many of the anime found for one AniDB anime, over every title it
    /// is searched by, have their episodes' air dates fetched so the matching
    /// engine can line them up with the anime's.
    /// </summary>
    internal const int AlignedCandidateCount = 3;

    private readonly AnilistApiClient _apiClient;

    private readonly AnilistStore _store;

    private readonly IMetadataMatchingEngine _matchingEngine;

    private readonly IMetadataProviderManager _providerManager;

    private readonly ConfigurationProvider<AnilistConfiguration> _configurationProvider;

    private readonly ILogger<AnilistSearchService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnilistSearchService"/> class.
    /// </summary>
    /// <param name="apiClient">The AniList client.</param>
    /// <param name="store">The plugin's store, for the anime already stored.</param>
    /// <param name="matchingEngine">The core's matching engine, which judges the candidates.</param>
    /// <param name="providerManager">The provider registry, asked whether restricted anime may be linked.</param>
    /// <param name="configurationProvider">The plugin's configuration.</param>
    /// <param name="logger">The logger.</param>
    public AnilistSearchService(
        AnilistApiClient apiClient,
        AnilistStore store,
        IMetadataMatchingEngine matchingEngine,
        IMetadataProviderManager providerManager,
        ConfigurationProvider<AnilistConfiguration> configurationProvider,
        ILogger<AnilistSearchService> logger
    )
    {
        _apiClient = apiClient;
        _store = store;
        _matchingEngine = matchingEngine;
        _providerManager = providerManager;
        _configurationProvider = configurationProvider;
        _logger = logger;
    }

    // Catches a sequel suffix the title carries even when nothing local says
    // the anime is a sequel. The same expression the core's matching engine
    // cuts the query with, so a search and its judging agree.
    [GeneratedRegex(@"\(\d{4}\)$|\bs(?:eason)? (?:\d+|(?=[MDCLXVI])M*(?:C[MD]|D?C{0,3})(X[CL]|L?X{0,3})(I[XV]|V?I{0,3}))$|\bs\d+$|第(零〇一二三四五六七八九十百千萬億兆京垓點)+[季期]$|\b(?:second|2nd|third|3rd|fourth|4th|fifth|5th|sixth|6th) season$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex SequelSuffixRegex();

    #region Search

    /// <summary>
    /// Searches AniList for anime.
    /// </summary>
    /// <param name="options">What to search for.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The page asked for, and how many results there are in total.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="AnilistApiException">AniList answered with something unexpected.</exception>
    /// <exception cref="AnilistUnavailableException">AniList cannot be reached for now.</exception>
    public async Task<(IReadOnlyList<AnilistSearchResult> Page, int TotalCount)> SearchAnime(AnilistSearchOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        _logger.LogDebug("Searching AniList for '{Query}' (Year: {Year}, Season: {Season} {SeasonYear}, Restricted: {Restricted}, Page: {Page}, Size: {PageSize})", options.Query, options.Year, options.Season, options.SeasonYear, options.IncludeRestricted, options.Page, options.PageSize);
        if (string.IsNullOrWhiteSpace(options.Query))
            return ([], 0);

        var result = await _apiClient.SearchAnimeAsync(options, cancellationToken).ConfigureAwait(false);
        if (result is null)
            return ([], 0);

        var totalCount = AnilistMediaMapper.GetInt(result["pageInfo"]?["total"]) ?? 0;

        // A page size of zero asks for the total alone. AniList's page size has
        // a floor of one, so the smallest page is fetched and dropped.
        if (options.PageSize <= 0)
            return ([], totalCount);

        return result["media"] is JsonArray media
            ? ([.. media.OfType<JsonNode>().Select(node => new AnilistSearchResult(node))], totalCount)
            : ([], totalCount);
    }

    /// <summary>
    /// Fetches one anime without storing it, for a lookup by ID.
    /// </summary>
    /// <param name="anilistAnimeID">The AniList anime ID.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The anime, or <see langword="null"/> when AniList has none by that ID.</returns>
    /// <exception cref="AnilistApiException">AniList answered with something unexpected.</exception>
    /// <exception cref="AnilistUnavailableException">AniList cannot be reached for now.</exception>
    public async Task<AnilistSearchResult?> GetRemoteAnime(int anilistAnimeID, CancellationToken cancellationToken = default)
        => await _apiClient.GetAnimeAsync(anilistAnimeID, cancellationToken).ConfigureAwait(false) is { } media ? new AnilistSearchResult(media) : null;

    /// <summary>
    /// An anime the core's series store holds, in the shape a search hands out.
    /// </summary>
    /// <param name="anilistAnimeID">The AniList anime ID.</param>
    /// <returns>The anime, or <see langword="null"/> when it is not stored.</returns>
    public AnilistSearchResult? GetStoredAnime(int anilistAnimeID)
        => _store.GetSeries(anilistAnimeID) is { } series ? AnilistSearchResult.FromStored(series, _store.GetAnime(anilistAnimeID)) : null;

    #endregion

    #region Automatic Search

    /// <summary>
    /// Works out which AniList anime an AniDB anime is, and hands back every
    /// anime the search scored, the one taken first.
    /// </summary>
    /// <remarks>
    /// Links are never consulted: the anime's own and its prequels' links are
    /// the core's to weigh, so a forced search answers as if there were none.
    /// </remarks>
    /// <param name="anime">The AniDB anime.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>
    /// Each anime scored once, the one taken first and the rest with why they
    /// were turned down; empty when nothing was searched or found.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="anime"/> is <see langword="null"/>.</exception>
    /// <exception cref="AnilistApiException">AniList answered with something unexpected.</exception>
    /// <exception cref="AnilistUnavailableException">AniList cannot be reached for now.</exception>
    public async Task<IReadOnlyList<AnilistAutoMatch>> FindAutoMatches(IAnidbAnime anime, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(anime);

        // Music videos and unknown types are hard to map.
        if (anime.Type is AnimeType.MusicVideo or AnimeType.Other or AnimeType.Unknown)
            return [];

        var secondEpisode = anime.Episodes
            .Where(episode => episode.Type is EpisodeType.Episode)
            .OrderBy(episode => episode.EpisodeNumber)
            .Take(2)
            .LastOrDefault();
        var storedAirDate = anime.AirDate?.ToDateTime() ?? (secondEpisode?.EarlyAirDate ?? secondEpisode?.AirDate)?.ToDateTime(TimeOnly.MinValue);

        // Nothing to search for when it has not aired and is not about to.
        var now = DateTime.Now;
        if (storedAirDate is not { } aired || (aired > now && aired - now > _maxTimeIntoTheFuture))
            return [];

        // The regular broadcast is what AniList dates, the anime or its first
        // episodes perhaps being dated by an early showing.
        var airDate = RegularStartOf(anime)?.ToDateTime() ?? secondEpisode?.AirDate?.ToDateTime(TimeOnly.MinValue) ?? aired;

        var allTitles = anime.Titles.Where(title => title.Type is TitleType.Main or TitleType.Official).ToList();
        if (allTitles.Count is 0)
            return [];

        var mainTitle = allTitles.FirstOrDefault(title => title.Type is TitleType.Main) ?? allTitles[0];
        var language = mainTitle.Language switch
        {
            TitleLanguage.Romaji => TitleLanguage.Japanese,
            TitleLanguage.Pinyin => TitleLanguage.ChineseSimplified,
            TitleLanguage.KoreanTranscription => TitleLanguage.Korean,
            TitleLanguage.ThaiTranscription => TitleLanguage.Thai,
            _ => mainTitle.Language,
        };

        // Follow the prequels back to the root, whose title a sequel often
        // shares on AniList.
        ISeries series = anime;
        var currentDate = airDate;
        var relations = anime.RelatedSeries;
        var visited = new HashSet<MetadataGuid> { anime.ID };
        while (relations.Count > 0)
        {
            var prequel = relations
                .Where(relation => relation.RelationType is RelationType.Prequel)
                .Select(relation => relation.Related)
                .FirstOrDefault(related => related?.AirDate is { } prequelDate && prequelDate.ToDateTime() <= currentDate && visited.Add(related.ID));
            if (prequel is null)
                break;

            series = prequel;
            currentDate = prequel.AirDate!.Value.ToDateTime();
            relations = prequel.RelatedSeries;
        }

        var prequelFollowed = series is not IAnidbAnime root || root.AnidbID != anime.AnidbID;

        // The titles tried below often reduce to the same query, which is
        // asked of AniList only once.
        var searched = new Dictionary<(string Query, int? Year, bool IncludeRestricted), IReadOnlyList<AnilistSearchResult>>();
        var schedules = new Dictionary<int, IReadOnlyList<MetadataSearchResultEpisode>?>();
        var wholeSchedules = new Dictionary<int, WholeSchedule?>();
        var scored = new List<AnilistAutoMatch>();

        var originalTitle = language == mainTitle.Language
            ? mainTitle.Value
            : (prequelFollowed ? series.Titles : allTitles).FirstOrDefault(title => title.Type is TitleType.Official && title.Language == language)?.Value;
        var match = !string.IsNullOrEmpty(originalTitle)
            ? await SearchUsingTitle(
                scored,
                anime,
                originalTitle,
                airDate,
                language is TitleLanguage.Japanese,
                searched,
                schedules,
                wholeSchedules,
                cancellationToken
            ).ConfigureAwait(false)
            : null;

        if (match is null)
        {
            var englishTitle = (prequelFollowed ? series.Titles : allTitles).FirstOrDefault(title => title is { Type: TitleType.Official, Language: TitleLanguage.English })?.Value;
            if (!string.IsNullOrEmpty(englishTitle) && !string.Equals(englishTitle, originalTitle, StringComparison.Ordinal))
            {
                match = await SearchUsingTitle(
                    scored,
                    anime,
                    englishTitle,
                    airDate,
                    false,
                    searched,
                    schedules,
                    wholeSchedules,
                    cancellationToken
                ).ConfigureAwait(false);
            }
        }

        match ??= await SearchUsingTitle(
            scored,
            anime,
            mainTitle.Value,
            airDate,
            false,
            searched,
            schedules,
            wholeSchedules,
            cancellationToken
        ).ConfigureAwait(false);

        // After following a prequel, the anime's own titles may still fit
        // better; they win when they rate at least as well, when they agree
        // on the date where the prequel's title found only a title, or when
        // the prequel's title found an anime begun long before this one and
        // they did not. Either of the last two is then an earlier season,
        // most often the prequel itself, whatever it rated.
        if (prequelFollowed && match is not null)
        {
            var ownTitle = allTitles.FirstOrDefault(title => title.Type is TitleType.Official && title.Language == language)?.Value
                ?? allTitles.FirstOrDefault(title => title is { Type: TitleType.Official, Language: TitleLanguage.English })?.Value
                ?? mainTitle.Value;
            var ownMatch = await SearchUsingTitle(
                scored,
                anime,
                ownTitle,
                airDate,
                language is TitleLanguage.Japanese,
                searched,
                schedules,
                wholeSchedules,
                cancellationToken
            ).ConfigureAwait(false);
            var earlierSeason = ownMatch is not null && StartedLongBefore(match, airDate) && !StartedLongBefore(ownMatch, airDate);
            if (earlierSeason)
                match.Rejection = new()
                {
                    Reason = MatchRejectionReason.Outranked,
                    Details = "It began long before the anime, as an earlier season does, and the anime's own title matched one that did not.",
                };
            else if (ownMatch is not null && (MatchPriority(ownMatch.MatchRating) <= MatchPriority(match.MatchRating)
                || (ownMatch.MatchRating is MatchRating.DateAndTitleKindaMatches && match.MatchRating is MatchRating.TitleMatches)))
                match.Rejection = new() { Reason = MatchRejectionReason.Outranked, Details = "The anime's own title matched as well or better than its first prequel's." };
            else
                ownMatch?.Rejection = new() { Reason = MatchRejectionReason.Outranked, Details = "The title of its first prequel matched better." };
        }

        // An anime scored more than once is kept once, as taken when it was
        // taken any of the times and otherwise as the query that rated it
        // best judged it. The one taken leads, the rest follow best rated
        // first, in the order they were found when rated alike.
        return
        [
            .. scored
                .GroupBy(result => result.Anime.ID)
                .Select(group => group.FirstOrDefault(result => result.Rejection is null) ?? group.MinBy(result => MatchPriority(result.MatchRating))!)
                .OrderBy(result => result.Rejection is null ? 0 : 1)
                .ThenBy(result => MatchPriority(result.MatchRating)),
        ];
    }

    /// <summary>
    /// Searches AniList by one title and has the matching engine judge what
    /// came back.
    /// </summary>
    /// <param name="scored">Where every judged candidate is added.</param>
    /// <param name="anime">The AniDB anime.</param>
    /// <param name="originalTitle">The title to search by.</param>
    /// <param name="airDate">When the anime's regular broadcast started.</param>
    /// <param name="isJapanese">Whether the title is written in Japanese.</param>
    /// <param name="searched">The queries asked so far, with their results.</param>
    /// <param name="schedules">
    /// The episodes fetched so far for the best candidates, by AniList anime
    /// ID, <see langword="null"/> for one AniList had none for.
    /// </param>
    /// <param name="wholeSchedules">
    /// The whole schedules fetched so far for candidates of unknown length,
    /// by AniList anime ID, <c>null</c> for one AniList had none for.
    /// </param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The candidate taken, or <see langword="null"/> when none was.</returns>
    /// <exception cref="AnilistApiException">AniList answered with something unexpected.</exception>
    /// <exception cref="AnilistUnavailableException">AniList cannot be reached for now.</exception>
    private async Task<AnilistAutoMatch?> SearchUsingTitle(
        List<AnilistAutoMatch> scored,
        IAnidbAnime anime,
        string originalTitle,
        DateTime airDate,
        bool isJapanese,
        Dictionary<(string Query, int? Year, bool IncludeRestricted), IReadOnlyList<AnilistSearchResult>> searched,
        Dictionary<int, IReadOnlyList<MetadataSearchResultEpisode>?> schedules,
        Dictionary<int, WholeSchedule?> wholeSchedules,
        CancellationToken cancellationToken
    )
    {
        var candidateCount = Math.Max(_configurationProvider.Load().AutoSearchCandidateCount, 1);
        var includeRestricted = anime.Restricted || MayAutoLinkRestricted();
        var seen = new HashSet<int>();
        var candidates = new List<AnilistSearchResult>();

        // #1: the whole title, in the year.
        Collect(candidates, await SearchRaw(originalTitle, includeRestricted, airDate.Year, searched, cancellationToken).ConfigureAwait(false), seen, candidateCount);

        // #2: without a sequel suffix, in the year.
        var strippedTitle = SequelSuffixRegex().Match(originalTitle) is { Success: true } suffix ? originalTitle[..^suffix.Length].TrimEnd() : null;
        if (!string.IsNullOrEmpty(strippedTitle) && candidates.Count < candidateCount)
            Collect(candidates, await SearchRaw(strippedTitle, includeRestricted, airDate.Year, searched, cancellationToken).ConfigureAwait(false), seen, candidateCount);

        // #3: without a subtitle, in the year. The engine only ever takes it
        // as a close match, so a parent entry cannot outrank the entry
        // matching the full title.
        var baseForSubtitle = strippedTitle ?? originalTitle;
        var colonIndex = baseForSubtitle.IndexOf(isJapanese ? ' ' : ':', StringComparison.Ordinal);
        var titleWithoutSubtitle = colonIndex > 0 ? baseForSubtitle[..colonIndex] : null;
        if (!string.IsNullOrEmpty(titleWithoutSubtitle) && candidates.Count < candidateCount)
            Collect(candidates, await SearchRaw(titleWithoutSubtitle, includeRestricted, airDate.Year, searched, cancellationToken).ConfigureAwait(false), seen, candidateCount);

        // #4 to #6: the same without the year, which a late-December premiere
        // or a wrong AniDB date would otherwise never reach.
        var yearFreeCap = candidateCount * 2;
        Collect(candidates, await SearchRaw(originalTitle, includeRestricted, null, searched, cancellationToken).ConfigureAwait(false), seen, yearFreeCap);
        if (!string.IsNullOrEmpty(strippedTitle) && candidates.Count < yearFreeCap)
            Collect(candidates, await SearchRaw(strippedTitle, includeRestricted, null, searched, cancellationToken).ConfigureAwait(false), seen, yearFreeCap);
        if (!string.IsNullOrEmpty(titleWithoutSubtitle) && candidates.Count < yearFreeCap)
            Collect(candidates, await SearchRaw(titleWithoutSubtitle, includeRestricted, null, searched, cancellationToken).ConfigureAwait(false), seen, yearFreeCap);

        if (candidates.Count is 0)
            return null;

        // AniList keeps every season as an anime of its own, so a title found
        // only once its sequel suffix is dropped names another season unless
        // the dates agree too.
        var anidbEpisodeCount = anime.EpisodeCounts.Episodes;
        var mapped = candidates.Select(candidate => (Anime: candidate, Result: ToCandidate(candidate, anidbEpisodeCount))).ToList();
        var options = new SeriesMatchOptions
        {
            Query = originalTitle,
            QueryLanguage = isJapanese ? TitleLanguage.Japanese : null,
            IncludeRestricted = includeRestricted,
            SeasonsAreSeparateEntries = true,
        };
        var judged = _matchingEngine.MatchSeries(anime, [.. mapped.Select(pair => pair.Result)], options);

        // The best few are judged again with their episodes' air dates, which
        // tell a split cour or a remake apart where the years alone cannot.
        await FetchSchedules(anime, judged, schedules, cancellationToken).ConfigureAwait(false);
        if (mapped.Any(pair => schedules.GetValueOrDefault(pair.Anime.ID) is { Count: > 0 }))
        {
            mapped = [.. mapped.Select(pair => schedules.GetValueOrDefault(pair.Anime.ID) is { Count: > 0 } episodes ? (pair.Anime, WithEpisodes(pair.Result, episodes)) : pair)];
            judged = _matchingEngine.MatchSeries(anime, [.. mapped.Select(pair => pair.Result)], options);
        }

        // Those of unknown length still tied for the top are judged once
        // more with their whole schedules, whose last episode tells their
        // length and whose dates line up wherever the window missed.
        var tied = TiedOfUnknownLength(judged);
        if (tied.Count > 0)
        {
            await FetchWholeSchedules(anime, tied, wholeSchedules, cancellationToken).ConfigureAwait(false);
            if (tied.Any(id => wholeSchedules.GetValueOrDefault(id) is not null))
            {
                mapped =
                [
                    .. mapped.Select(pair => tied.Contains(pair.Anime.ID) && wholeSchedules.GetValueOrDefault(pair.Anime.ID) is { } schedule
                        ? (pair.Anime, WithWholeSchedule(pair.Result, schedule))
                        : pair),
                ];
                judged = _matchingEngine.MatchSeries(anime, [.. mapped.Select(pair => pair.Result)], options);
            }
        }

        AnilistAutoMatch? taken = null;
        foreach (var match in judged)
        {
            var result = new AnilistAutoMatch(anime.AnidbID, mapped.First(pair => ReferenceEquals(pair.Result, match.Candidate)).Anime, match.Candidate, match.Rating)
            {
                Rejection = match.Rejection is MatchRejectionReason.None ? null : new() { Reason = match.Rejection, Details = $"Searched for \"{originalTitle}\"." },
                IsAligned = match.EpisodeAlignment is { IsConclusive: true },
            };
            _logger.LogTrace("Candidate anime {AnimeName} ({ID}): rating={Rating}, rejection={Rejection}", result.Anime.Title, result.Anime.ID, result.MatchRating, match.Rejection);
            scored.Add(result);
            if (result.Rejection is null)
                taken ??= result;
        }

        if (taken is not null)
            _logger.LogInformation("Best match for \"{Query}\": {AnimeName} ({ID}) rating={Rating}", originalTitle, taken.Anime.Title, taken.Anime.ID, taken.MatchRating);

        return taken;
    }

    /// <summary>
    /// Fetches the episodes' air dates of the best candidates rated anything,
    /// until <see cref="AlignedCandidateCount"/> anime have been asked for
    /// over the whole search, in one request for all of them.
    /// </summary>
    /// <remarks>
    /// Only the slots within a month of the anime's own dated episodes are
    /// read, which is all the lining up can use. Nothing is fetched for an
    /// anime with no dated regular episode, and a failed fetch leaves the
    /// candidates as they were.
    /// </remarks>
    /// <param name="anime">The AniDB anime.</param>
    /// <param name="judged">The candidates as the engine judged them, best first.</param>
    /// <param name="schedules">
    /// The episodes fetched so far, by AniList anime ID, added to;
    /// <see langword="null"/> for one AniList had none for.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes once the episodes are fetched.</returns>
    /// <exception cref="AnilistUnavailableException">AniList cannot be reached for now.</exception>
    private async Task FetchSchedules(
        IAnidbAnime anime,
        IReadOnlyList<SeriesMatch> judged,
        Dictionary<int, IReadOnlyList<MetadataSearchResultEpisode>?> schedules,
        CancellationToken cancellationToken
    )
    {
        var wanted = new List<int>();
        foreach (var match in judged)
        {
            if (schedules.Count + wanted.Count >= AlignedCandidateCount)
                break;

            if (match.Rating is not MatchRating.None && AnilistUtility.TryGetID(match.Candidate.ID, MetadataEntityType.Series, out var id) && !schedules.ContainsKey(id) && !wanted.Contains(id))
                wanted.Add(id);
        }

        var dates = anime.Episodes
            .Where(episode => episode.Type is EpisodeType.Episode && episode.AirDate is not null)
            .Select(episode => episode.AirDate!.Value)
            .ToList();
        if (wanted.Count is 0 || dates.Count is 0)
            return;

        var after = new DateTimeOffset(dates.Min().ToDateTime(TimeOnly.MinValue), _japanOffset) - _alignmentMargin;
        var before = new DateTimeOffset(dates.Max().ToDateTime(TimeOnly.MinValue), _japanOffset) + _alignmentMargin;
        var slots = new Dictionary<int, Dictionary<int, DateTime>>();
        try
        {
            for (var page = 1; page <= MaxSchedulePages; page++)
            {
                var node = await _apiClient.GetAiringSchedulesPageAsync(wanted, after, before, page, cancellationToken).ConfigureAwait(false);
                AnilistMediaMapper.ReadAiringSchedulesPage(node, slots);
                if (!HasNextPage(node))
                    break;
            }
        }
        catch (AnilistApiException ex)
        {
            _logger.LogDebug(ex, "Unable to fetch the airing schedules of AniList anime {AnimeIDs}. Judging them without.", string.Join(", ", wanted));
        }

        foreach (var id in wanted)
            schedules[id] = slots.TryGetValue(id, out var episodes) ? ToEpisodes(episodes) : null;
    }

    /// <summary>
    /// The AniList anime of unknown length among the candidates still tied
    /// for the top: rated as well as the one taken, and taken or turned down
    /// as outranked or for their episode count.
    /// </summary>
    /// <param name="judged">The candidates as the engine judged them, best first.</param>
    /// <returns>
    /// Their AniList anime IDs; empty when none was taken or nothing ties
    /// with the one taken.
    /// </returns>
    internal static IReadOnlyList<int> TiedOfUnknownLength(IReadOnlyList<SeriesMatch> judged)
    {
        if (judged.FirstOrDefault(match => match.Rejection is MatchRejectionReason.None) is not { Rating: not MatchRating.None } winner)
            return [];

        var tied = judged
            .Where(match => MatchPriority(match.Rating) == MatchPriority(winner.Rating)
                && match.Rejection is MatchRejectionReason.None or MatchRejectionReason.Outranked or MatchRejectionReason.EpisodeCountMismatch)
            .ToList();
        if (tied.Count < 2)
            return [];

        return
        [
            .. tied
                .Where(match => match.Candidate.EpisodeCount is null)
                .Select(match => AnilistUtility.TryGetID(match.Candidate.ID, MetadataEntityType.Series, out var id) ? id : 0)
                .Where(id => id > 0),
        ];
    }

    /// <summary>
    /// Fetches the whole schedules, aired and upcoming, of the candidates of
    /// unknown length still tied for the top, in one query for all of them
    /// reading at most a page per anime.
    /// </summary>
    /// <remarks>
    /// The schedule is read by anime, so a read cut off by the page budget
    /// ends inside the last anime read and never reaches the rest. That
    /// anime's last episode read is only a lower bound, used as its length
    /// only when it proves it longer than the AniDB anime. A failed fetch
    /// leaves the candidates as they were.
    /// </remarks>
    /// <param name="anime">The AniDB anime.</param>
    /// <param name="tied">The tied candidates' AniList anime IDs.</param>
    /// <param name="wholeSchedules">
    /// The whole schedules fetched so far, by AniList anime ID, added to;
    /// <c>null</c> for one AniList had none for.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes once the schedules are fetched.</returns>
    /// <exception cref="AnilistUnavailableException">AniList cannot be reached for now.</exception>
    private async Task FetchWholeSchedules(
        IAnidbAnime anime,
        IReadOnlyList<int> tied,
        Dictionary<int, WholeSchedule?> wholeSchedules,
        CancellationToken cancellationToken
    )
    {
        var wanted = tied.Where(id => !wholeSchedules.ContainsKey(id)).ToList();
        if (wanted.Count is 0)
            return;

        var slots = new Dictionary<int, Dictionary<int, DateTime>>();
        var cutOff = false;
        try
        {
            for (var page = 1; page <= wanted.Count; page++)
            {
                var node = await _apiClient.GetWholeAiringSchedulesPageAsync(wanted, page, cancellationToken).ConfigureAwait(false);
                AnilistMediaMapper.ReadAiringSchedulesPage(node, slots);
                cutOff = HasNextPage(node);
                if (!cutOff)
                    break;
            }
        }
        catch (AnilistApiException ex)
        {
            _logger.LogDebug(ex, "Unable to fetch the whole airing schedules of AniList anime {AnimeIDs}. Judging them without.", string.Join(", ", wanted));
            foreach (var id in wanted)
                wholeSchedules[id] = null;
            return;
        }

        var lastRead = cutOff && slots.Count > 0 ? slots.Keys.Max() : (int?)null;
        var anidbEpisodeCount = anime.EpisodeCounts.Episodes;
        foreach (var id in wanted)
        {
            if (!slots.TryGetValue(id, out var episodes) || episodes.Count is 0)
            {
                wholeSchedules[id] = null;
                continue;
            }

            var lastEpisode = episodes.Keys.Max();
            var complete = lastRead is not { } cut || id < cut;
            wholeSchedules[id] = new(
                ToEpisodes(episodes),
                complete || (anidbEpisodeCount > 0 && lastEpisode > anidbEpisodeCount) ? lastEpisode : null
            );
        }
    }

    /// <summary>
    /// A search result as the matching engine judges it for an AniDB anime.
    /// </summary>
    /// <remarks>
    /// An airing anime of unknown length is as long as it has aired, but only
    /// when that is longer than the AniDB anime: a lower bound can prove it
    /// too long, never right. One whose start AniList has not fully dated
    /// began when its first episode airs, as dated in Japan.
    /// </remarks>
    /// <param name="result">The search result.</param>
    /// <param name="anidbEpisodeCount">The AniDB anime's episode count, <c>0</c> when unknown.</param>
    /// <returns>The candidate.</returns>
    internal static MetadataSeriesSearchResult ToCandidate(AnilistSearchResult result, int anidbEpisodeCount)
    {
        var candidate = result.ToMetadataSearchResult();
        var media = result.Anime;
        if (candidate.EpisodeCount is null && anidbEpisodeCount > 0 && media.NextEpisodeNumber - 1 is { } aired && aired > anidbEpisodeCount)
            candidate = candidate with { EpisodeCount = aired };

        if (candidate.FirstAiredAt is not { IsComplete: true } && media is { NextEpisodeNumber: 1, NextEpisodeAiringAt: { } airingAt })
            candidate = candidate with { FirstAiredAt = new PartialDateOnly(DateOnly.FromDateTime(airingAt + _japanOffset)) };

        return candidate;
    }

    /// <summary>
    /// A candidate with its whole schedule: its episodes, and its length
    /// when the schedule tells it and nothing else did.
    /// </summary>
    /// <param name="candidate">The candidate.</param>
    /// <param name="schedule">Its whole schedule.</param>
    /// <returns>The candidate.</returns>
    internal static MetadataSeriesSearchResult WithWholeSchedule(MetadataSeriesSearchResult candidate, WholeSchedule schedule)
        => WithEpisodes(candidate with { EpisodeCount = candidate.EpisodeCount ?? schedule.EpisodeCount }, schedule.Episodes);

    /// <summary>
    /// A candidate with its episodes, as the one season of its own an anime
    /// without seasons is to the matching engine.
    /// </summary>
    /// <param name="candidate">The candidate.</param>
    /// <param name="episodes">Its episodes, with their air dates.</param>
    /// <returns>The candidate.</returns>
    internal static MetadataSeriesSearchResult WithEpisodes(MetadataSeriesSearchResult candidate, IReadOnlyList<MetadataSearchResultEpisode> episodes)
        => candidate with
        {
            Seasons =
            [
                new()
                {
                    SeasonNumber = 1,
                    EpisodeCount = candidate.EpisodeCount,
                    FirstAiredAt = candidate.FirstAiredAt,
                    FirstEpisodeAiredAt = episodes.FirstOrDefault(episode => episode.EpisodeNumber is 1)?.AiredAt
                        ?? (candidate.FirstAiredAt is { IsComplete: true } started ? started.ToDateOnly() : null),
                    Episodes = episodes,
                },
            ],
        };

    /// <summary>
    /// An anime's slots as episodes dated in Japan, in order.
    /// </summary>
    /// <param name="slots">Its slots, by episode number, in UTC.</param>
    /// <returns>The episodes.</returns>
    private static IReadOnlyList<MetadataSearchResultEpisode> ToEpisodes(Dictionary<int, DateTime> slots)
        =>
        [
            .. slots
                .OrderBy(pair => pair.Key)
                .Select(pair => new MetadataSearchResultEpisode { EpisodeNumber = pair.Key, AiredAt = DateOnly.FromDateTime(pair.Value + _japanOffset) }),
        ];

    /// <summary>
    /// Whether a page of airing schedules says another follows.
    /// </summary>
    /// <param name="page">The <c>Page</c> node.</param>
    /// <returns><c>true</c> when another page follows.</returns>
    private static bool HasNextPage(JsonNode? page)
        => page?["pageInfo"]?["hasNextPage"] is JsonValue more && more.TryGetValue<bool>(out var hasNextPage) && hasNextPage;

    /// <summary>
    /// Whether a match began long before the anime's regular broadcast,
    /// as an earlier season does, its episodes not lining up with the
    /// anime's.
    /// </summary>
    /// <param name="match">The match.</param>
    /// <param name="airDate">When the anime's regular broadcast started.</param>
    /// <returns><c>true</c> when it began too long before to be the anime.</returns>
    private static bool StartedLongBefore(AnilistAutoMatch match, DateTime airDate)
    {
        if (match.IsAligned)
            return false;

        var started = match.Candidate.FirstAiredAt is { IsComplete: true } aired
            ? aired.ToDateOnly()
            : match.Candidate.Seasons?.FirstOrDefault()?.FirstEpisodeAiredAt;
        return started is { } date && DateOnly.FromDateTime(airDate).DayNumber - date.DayNumber > MaxDaysStartedBeforeStart;
    }

    private async Task<IReadOnlyList<AnilistSearchResult>> SearchRaw(
        string query,
        bool includeRestricted,
        int? year,
        Dictionary<(string Query, int? Year, bool IncludeRestricted), IReadOnlyList<AnilistSearchResult>> searched,
        CancellationToken cancellationToken
    )
    {
        if (searched.TryGetValue((query, year, includeRestricted), out var cached))
            return cached;

        var (results, _) = await SearchAnime(new()
        {
            Query = query,
            IncludeRestricted = includeRestricted,
            Year = year,
            Page = 1,
            PageSize = AnilistApiClient.MaxPageSize / 2,
        }, cancellationToken).ConfigureAwait(false);
        searched[(query, year, includeRestricted)] = results;
        return results;
    }

    private static void Collect(List<AnilistSearchResult> candidates, IReadOnlyList<AnilistSearchResult> results, HashSet<int> seen, int limit)
    {
        foreach (var result in results)
        {
            if (candidates.Count >= limit)
                break;

            // Music videos are not worth linking automatically.
            if (!seen.Add(result.ID) || result.Type is AnimeType.MusicVideo)
                continue;

            candidates.Add(result);
        }
    }

    /// <summary>
    /// When the anime's regular broadcast started: its first normal episode's
    /// <see cref="IEpisode.AirDate"/> when that episode was shown early, and
    /// the anime's own date otherwise.
    /// </summary>
    /// <param name="anime">The anime.</param>
    /// <returns>The date, or <c>null</c> when neither is dated.</returns>
    private static PartialDateOnly? RegularStartOf(IAnidbAnime anime)
        => anime.Episodes.FirstOrDefault(episode => episode is { Type: EpisodeType.Episode, EpisodeNumber: 1 }) is { EarlyAirDate: not null, AirDate: { } regular }
            ? new PartialDateOnly(regular)
            : anime.AirDate;

    private bool MayAutoLinkRestricted()
    {
        try
        {
            return _providerManager.GetProviderInfo(typeof(AnilistMetadataProvider)).AutoLinkRestricted;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Where a rating sits when the anime's own title and its prequel's are
    /// weighed against each other, lower first.
    /// </summary>
    /// <param name="rating">The rating.</param>
    /// <returns>Its priority.</returns>
    internal static int MatchPriority(MatchRating rating) => rating switch
    {
        MatchRating.UserVerified => 0,
        MatchRating.DateAndTitleMatches => 1,
        MatchRating.TitleMatches => 2,
        MatchRating.DateAndTitleKindaMatches => 3,
        MatchRating.DateMatches => 4,
        MatchRating.TitleKindaMatches => 5,
        _ => 6,
    };

    #endregion
}

/// <summary>
/// The whole airing schedule of a candidate of unknown length.
/// </summary>
/// <param name="Episodes">Its scheduled episodes, aired and upcoming, dated in Japan.</param>
/// <param name="EpisodeCount">
/// Its last scheduled episode as its length, or <c>null</c> when the
/// schedule was cut off short of telling it.
/// </param>
internal sealed record WholeSchedule(IReadOnlyList<MetadataSearchResultEpisode> Episodes, int? EpisodeCount);
