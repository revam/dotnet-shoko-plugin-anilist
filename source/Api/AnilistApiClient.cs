using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Plugin.Anilist.Mapping;

namespace Shoko.Plugin.Anilist.Api;

/// <summary>
/// Talks to the AniList GraphQL API. Every request goes through
/// <see cref="AnilistRateLimiter"/>, honours the quota headers, and retries a
/// 429 or a timeout only after the wait the server asked for.
/// </summary>
/// <remarks>
/// A server error is never retried here. It trips the breaker and surfaces as
/// an <see cref="AnilistUnavailableException"/>, and the provider reports the
/// pause, so the core holds its jobs back until it lifts instead of adding load
/// to a struggling upstream.
/// </remarks>
/// <param name="httpClient">The HTTP client, with its base address set to the GraphQL endpoint.</param>
/// <param name="rateLimiter">The rate limiter every request goes through.</param>
/// <param name="logger">The logger.</param>
public class AnilistApiClient(HttpClient httpClient, AnilistRateLimiter rateLimiter, ILogger<AnilistApiClient> logger)
{
    /// <summary>
    /// The GraphQL endpoint.
    /// </summary>
    public const string GraphQLUrl = "https://graphql.anilist.co";

    /// <summary>
    /// The largest page AniList hands out.
    /// </summary>
    public const int MaxPageSize = 50;

    private const int MaxRateLimitRetries = 3;

    private const int MaxTimeoutRetries = 2;

    #region Fragments

    private const string MediaFragment = """
        fragment MediaFields on Media {
          id
          idMal
          type
          format
          title { english native romaji }
          synonyms
          description(asHtml: false)
          coverImage { color extraLarge }
          bannerImage
          startDate { year month day }
          endDate { year month day }
          status
          source
          isAdult
          isLicensed
          season
          seasonYear
          countryOfOrigin
          averageScore
          meanScore
          favourites
          popularity
          episodes
          duration
          genres
          updatedAt
          trailer { id site }
          externalLinks { id url site siteId type language }
          stats { scoreDistribution { score amount } }
        }
        """;

    private const string CharacterFragment = """
        fragment CharacterFields on Character {
          id
          name { full native alternative }
          image { large }
          description(asHtml: false)
          gender
          dateOfBirth { year month day }
          age
          favourites
          siteUrl
        }
        """;

    private const string StaffFragment = """
        fragment StaffFields on Staff {
          id
          name { full native alternative }
          image { large }
          description(asHtml: false)
          languageV2
          primaryOccupations
          gender
          dateOfBirth { year month day }
          dateOfDeath { year month day }
          homeTown
          favourites
          siteUrl
        }
        """;

    private const string StudioFragment = """
        fragment StudioFields on Studio {
          id
          name
          isAnimationStudio
          favourites
          siteUrl
        }
        """;

    private const string SchedulePage = """
        airingSchedule(notYetAired: false, perPage: 50, page: $schedulePage) {
          nodes { id episode airingAt }
          pageInfo { currentPage hasNextPage }
        }
        """;

    private const string RecommendationsPage = """
        recommendations(sort: [RATING_DESC, ID], perPage: 50, page: $recommendationPage) {
          pageInfo { currentPage hasNextPage }
          nodes {
            rating
            mediaRecommendation { id type }
          }
        }
        """;

    private const string CharactersPage = """
        characters(perPage: 25, page: $characterPage, sort: [ROLE, RELEVANCE, ID]) {
          pageInfo { currentPage hasNextPage }
          edges {
            role
            voiceActorRoles(sort: [RELEVANCE, ID]) {
              roleNotes
              dubGroup
              voiceActor { ...StaffFields }
            }
            node { ...CharacterFields }
          }
        }
        """;

    private const string StaffPage = """
        staff(perPage: 25, page: $staffPage, sort: [RELEVANCE, ID]) {
          pageInfo { currentPage hasNextPage }
          edges {
            role
            node { ...StaffFields }
          }
        }
        """;

    /// <summary>
    /// Everything about an anime in one round trip: the metadata, the first
    /// page of the schedule, recommendations, characters and staff, and the
    /// tags, studios and relations, which are not paged.
    /// </summary>
    private static readonly string _animeQuery = $$"""
        query ($id: Int, $schedulePage: Int, $recommendationPage: Int, $characterPage: Int, $staffPage: Int) {
          Media(id: $id, type: ANIME) {
            ...MediaFields
            {{SchedulePage}}
            studios(sort: [ID]) {
              edges {
                isMain
                node { ...StudioFields }
              }
            }
            tags { id name category description isAdult isGeneralSpoiler isMediaSpoiler rank }
            relations {
              edges {
                relationType(version: 2)
                node { id type format countryOfOrigin title { romaji } }
              }
            }
            {{RecommendationsPage}}
            {{CharactersPage}}
            {{StaffPage}}
          }
        }
        {{MediaFragment}}
        {{StudioFragment}}
        {{CharacterFragment}}
        {{StaffFragment}}
        """;

    private static readonly string _airingScheduleQuery = $$"""
        query ($id: Int, $schedulePage: Int) {
          Media(id: $id, type: ANIME) {
            {{SchedulePage}}
          }
        }
        """;

    private static readonly string _airingSchedulesQuery = """
        query ($ids: [Int], $after: Int, $before: Int, $page: Int) {
          Page(page: $page, perPage: 50) {
            pageInfo { currentPage hasNextPage }
            airingSchedules(mediaId_in: $ids, airingAt_greater: $after, airingAt_lesser: $before, sort: [MEDIA_ID, EPISODE]) {
              mediaId episode airingAt
            }
          }
        }
        """;

    private static readonly string _recommendationsQuery = $$"""
        query ($id: Int, $recommendationPage: Int) {
          Media(id: $id, type: ANIME) {
            {{RecommendationsPage}}
          }
        }
        """;

    private static readonly string _charactersQuery = $$"""
        query ($id: Int, $characterPage: Int) {
          Media(id: $id, type: ANIME) {
            {{CharactersPage}}
          }
        }
        {{CharacterFragment}}
        {{StaffFragment}}
        """;

    private static readonly string _staffQuery = $$"""
        query ($id: Int, $staffPage: Int) {
          Media(id: $id, type: ANIME) {
            {{StaffPage}}
          }
        }
        {{StaffFragment}}
        """;

    private static readonly string _staffMemberQuery = $$"""
        query ($id: Int) {
          Staff(id: $id) { ...StaffFields }
        }
        {{StaffFragment}}
        """;

    private static readonly string _characterQuery = $$"""
        query ($id: Int) {
          Character(id: $id) { ...CharacterFields }
        }
        {{CharacterFragment}}
        """;

    private static readonly string _studioQuery = $$"""
        query ($id: Int) {
          Studio(id: $id) { ...StudioFields }
        }
        {{StudioFragment}}
        """;

    #endregion

    #region Queries

    /// <summary>
    /// Fetches an anime, with the first page of its schedule, recommendations,
    /// characters and staff.
    /// </summary>
    /// <param name="anilistAnimeID">The AniList anime ID.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The <c>Media</c> node, or <see langword="null"/> when AniList has no such anime.</returns>
    /// <exception cref="AnilistApiException">AniList answered with something unexpected.</exception>
    /// <exception cref="AnilistUnavailableException">AniList cannot be reached for now.</exception>
    public Task<JsonNode?> GetAnimeAsync(int anilistAnimeID, CancellationToken cancellationToken = default)
        => ExecuteAndSelectAsync(_animeQuery, new() { ["id"] = anilistAnimeID, ["schedulePage"] = 1, ["recommendationPage"] = 1, ["characterPage"] = 1, ["staffPage"] = 1 }, "Media", $"Get anime {anilistAnimeID}", cancellationToken);

    /// <summary>
    /// Fetches one page of an anime's airing schedule.
    /// </summary>
    /// <param name="anilistAnimeID">The AniList anime ID.</param>
    /// <param name="page">The page, from 1.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The <c>airingSchedule</c> node, or <see langword="null"/>.</returns>
    /// <exception cref="AnilistApiException">AniList answered with something unexpected.</exception>
    /// <exception cref="AnilistUnavailableException">AniList cannot be reached for now.</exception>
    public async Task<JsonNode?> GetAiringSchedulePageAsync(int anilistAnimeID, int page, CancellationToken cancellationToken = default)
        => (await ExecuteAndSelectAsync(_airingScheduleQuery, new() { ["id"] = anilistAnimeID, ["schedulePage"] = page }, "Media", $"Get airing schedule page {page} for anime {anilistAnimeID}", cancellationToken).ConfigureAwait(false))?["airingSchedule"];

    /// <summary>
    /// Fetches one page of the airing schedules of several anime at once,
    /// within a window.
    /// </summary>
    /// <param name="anilistAnimeIDs">The AniList anime IDs.</param>
    /// <param name="airingAfter">Only slots after this instant.</param>
    /// <param name="airingBefore">Only slots before this instant.</param>
    /// <param name="page">The page, from 1.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The <c>Page</c> node, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="anilistAnimeIDs"/> is <see langword="null"/>.</exception>
    /// <exception cref="AnilistApiException">AniList answered with something unexpected.</exception>
    /// <exception cref="AnilistUnavailableException">AniList cannot be reached for now.</exception>
    public Task<JsonNode?> GetAiringSchedulesPageAsync(IReadOnlyCollection<int> anilistAnimeIDs, DateTimeOffset airingAfter, DateTimeOffset airingBefore, int page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(anilistAnimeIDs);

        var variables = new Dictionary<string, object?>
        {
            ["ids"] = anilistAnimeIDs,
            ["after"] = airingAfter.ToUnixTimeSeconds(),
            ["before"] = airingBefore.ToUnixTimeSeconds(),
            ["page"] = Math.Max(page, 1),
        };
        return ExecuteAndSelectAsync(_airingSchedulesQuery, variables, "Page", $"Get airing schedules page {page} for anime {string.Join(", ", anilistAnimeIDs)}", cancellationToken);
    }

    /// <summary>
    /// Fetches one page of an anime's recommendations.
    /// </summary>
    /// <param name="anilistAnimeID">The AniList anime ID.</param>
    /// <param name="page">The page, from 1.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The <c>recommendations</c> node, or <see langword="null"/>.</returns>
    /// <exception cref="AnilistApiException">AniList answered with something unexpected.</exception>
    /// <exception cref="AnilistUnavailableException">AniList cannot be reached for now.</exception>
    public async Task<JsonNode?> GetRecommendationsPageAsync(int anilistAnimeID, int page, CancellationToken cancellationToken = default)
        => (await ExecuteAndSelectAsync(_recommendationsQuery, new() { ["id"] = anilistAnimeID, ["recommendationPage"] = page }, "Media", $"Get recommendations page {page} for anime {anilistAnimeID}", cancellationToken).ConfigureAwait(false))?["recommendations"];

    /// <summary>
    /// Fetches one page of an anime's characters, with their voice actors.
    /// </summary>
    /// <param name="anilistAnimeID">The AniList anime ID.</param>
    /// <param name="page">The page, from 1.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The <c>characters</c> node, or <see langword="null"/>.</returns>
    /// <exception cref="AnilistApiException">AniList answered with something unexpected.</exception>
    /// <exception cref="AnilistUnavailableException">AniList cannot be reached for now.</exception>
    public async Task<JsonNode?> GetCharactersPageAsync(int anilistAnimeID, int page, CancellationToken cancellationToken = default)
        => (await ExecuteAndSelectAsync(_charactersQuery, new() { ["id"] = anilistAnimeID, ["characterPage"] = page }, "Media", $"Get characters page {page} for anime {anilistAnimeID}", cancellationToken).ConfigureAwait(false))?["characters"];

    /// <summary>
    /// Fetches one page of an anime's staff.
    /// </summary>
    /// <param name="anilistAnimeID">The AniList anime ID.</param>
    /// <param name="page">The page, from 1.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The <c>staff</c> node, or <see langword="null"/>.</returns>
    /// <exception cref="AnilistApiException">AniList answered with something unexpected.</exception>
    /// <exception cref="AnilistUnavailableException">AniList cannot be reached for now.</exception>
    public async Task<JsonNode?> GetStaffPageAsync(int anilistAnimeID, int page, CancellationToken cancellationToken = default)
        => (await ExecuteAndSelectAsync(_staffQuery, new() { ["id"] = anilistAnimeID, ["staffPage"] = page }, "Media", $"Get staff page {page} for anime {anilistAnimeID}", cancellationToken).ConfigureAwait(false))?["staff"];

    /// <summary>
    /// Fetches one staff member on their own.
    /// </summary>
    /// <param name="anilistStaffID">The AniList staff ID.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The <c>Staff</c> node, or <see langword="null"/> when AniList has no such person.</returns>
    /// <exception cref="AnilistApiException">AniList answered with something unexpected.</exception>
    /// <exception cref="AnilistUnavailableException">AniList cannot be reached for now.</exception>
    public Task<JsonNode?> GetStaffAsync(int anilistStaffID, CancellationToken cancellationToken = default)
        => ExecuteAndSelectAsync(_staffMemberQuery, new() { ["id"] = anilistStaffID }, "Staff", $"Get staff {anilistStaffID}", cancellationToken);

    /// <summary>
    /// Fetches one character on their own.
    /// </summary>
    /// <param name="anilistCharacterID">The AniList character ID.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The <c>Character</c> node, or <see langword="null"/> when AniList has no such character.</returns>
    /// <exception cref="AnilistApiException">AniList answered with something unexpected.</exception>
    /// <exception cref="AnilistUnavailableException">AniList cannot be reached for now.</exception>
    public Task<JsonNode?> GetCharacterAsync(int anilistCharacterID, CancellationToken cancellationToken = default)
        => ExecuteAndSelectAsync(_characterQuery, new() { ["id"] = anilistCharacterID }, "Character", $"Get character {anilistCharacterID}", cancellationToken);

    /// <summary>
    /// Fetches one studio on its own.
    /// </summary>
    /// <param name="anilistStudioID">The AniList studio ID.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The <c>Studio</c> node, or <see langword="null"/> when AniList has no such studio.</returns>
    /// <exception cref="AnilistApiException">AniList answered with something unexpected.</exception>
    /// <exception cref="AnilistUnavailableException">AniList cannot be reached for now.</exception>
    public Task<JsonNode?> GetStudioAsync(int anilistStudioID, CancellationToken cancellationToken = default)
        => ExecuteAndSelectAsync(_studioQuery, new() { ["id"] = anilistStudioID }, "Studio", $"Get studio {anilistStudioID}", cancellationToken);

    /// <summary>
    /// Searches for anime. Every filter is applied by AniList, so only the
    /// page asked for is transferred.
    /// </summary>
    /// <param name="options">What to search for.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The <c>Page</c> node, or <see langword="null"/> on an empty response.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="AnilistApiException">AniList answered with something unexpected.</exception>
    /// <exception cref="AnilistUnavailableException">AniList cannot be reached for now.</exception>
    public Task<JsonNode?> SearchAnimeAsync(AnilistSearchOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var variables = new Dictionary<string, object?>
        {
            ["search"] = options.Query,
            ["page"] = Math.Max(options.Page, 1),
            ["perPage"] = Math.Clamp(options.PageSize, 1, MaxPageSize),
        };
        var declarations = new List<string> { "$search: String", "$page: Int", "$perPage: Int" };
        var arguments = new List<string> { "search: $search", "type: ANIME", "sort: [SEARCH_MATCH]" };

        if (!options.IncludeRestricted)
        {
            variables["isAdult"] = false;
            declarations.Add("$isAdult: Boolean");
            arguments.Add("isAdult: $isAdult");
        }

        if (options.Year is > 0)
        {
            // FuzzyDateInt is YYYYMMDD and the comparisons are exclusive, so bracket the whole year.
            variables["startAfter"] = ((options.Year.Value - 1) * 10000) + 1231;
            variables["startBefore"] = ((options.Year.Value + 1) * 10000) + 101;
            declarations.Add("$startAfter: FuzzyDateInt");
            declarations.Add("$startBefore: FuzzyDateInt");
            arguments.Add("startDate_greater: $startAfter");
            arguments.Add("startDate_lesser: $startBefore");
        }

        if (options.Season is { } season)
        {
            variables["season"] = AnilistUtility.ToSeason(season);
            declarations.Add("$season: MediaSeason");
            arguments.Add("season: $season");
        }

        if (options.SeasonYear is > 0)
        {
            variables["seasonYear"] = options.SeasonYear.Value;
            declarations.Add("$seasonYear: Int");
            arguments.Add("seasonYear: $seasonYear");
        }

        if (options.Types is { Count: > 0 } types)
        {
            var formats = types.SelectMany(AnilistUtility.ToFormats).Distinct().ToList();
            if (formats.Count > 0)
            {
                variables["formats"] = formats;
                declarations.Add("$formats: [MediaFormat]");
                arguments.Add("format_in: $formats");
            }
        }

        var query = $$"""
            query ({{string.Join(", ", declarations)}}) {
              Page(page: $page, perPage: $perPage) {
                pageInfo { currentPage lastPage hasNextPage total }
                media({{string.Join(", ", arguments)}}) {
                  ...MediaFields
                }
              }
            }
            {{MediaFragment}}
            """;
        return ExecuteAndSelectAsync(query, variables, "Page", $"Search anime \"{options.Query}\" (page {Math.Max(options.Page, 1)})", cancellationToken);
    }

    #endregion

    #region Transport

    private async Task<JsonNode?> ExecuteAndSelectAsync(string query, Dictionary<string, object?> variables, string rootField, string displayName, CancellationToken cancellationToken)
    {
        var result = await ExecuteQueryAsync(query, variables, displayName, cancellationToken).ConfigureAwait(false);
        return result?["data"]?[rootField];
    }

    /// <summary>
    /// Runs a query. <see langword="null"/> comes back only when AniList says
    /// the entity does not exist; everything else is retried or thrown, so an
    /// outage is never mistaken for a missing entity.
    /// </summary>
    /// <param name="query">The GraphQL query.</param>
    /// <param name="variables">Its variables.</param>
    /// <param name="displayName">What to call the call in the log.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The whole response, or <see langword="null"/> for a missing entity.</returns>
    /// <exception cref="AnilistApiException">AniList answered with something unexpected.</exception>
    /// <exception cref="AnilistUnavailableException">AniList cannot be reached for now.</exception>
    private async Task<JsonNode?> ExecuteQueryAsync(string query, Dictionary<string, object?> variables, string displayName, CancellationToken cancellationToken)
    {
        var scheduledAt = DateTime.UtcNow;
        var attempts = 0;
        var waitTime = TimeSpan.Zero;
        logger.LogTrace("Scheduled call: {DisplayName}", displayName);
        var result = await rateLimiter.EnsureRateAsync(async () =>
        {
            waitTime = DateTime.UtcNow - scheduledAt;
            logger.LogTrace("Executing call: {DisplayName} (Waited {Waited}ms)", displayName, waitTime.TotalMilliseconds);
            var rateLimitRetries = 0;
            var timeoutRetries = 0;
            while (true)
            {
                try
                {
                    ++attempts;
                    var (response, retry) = await SendAsync(query, variables, cancellationToken).ConfigureAwait(false);
                    if (!retry)
                        return response;

                    if (++rateLimitRetries > MaxRateLimitRetries)
                        throw new AnilistUnavailableException("AniList rate limit retry budget exhausted.", rateLimiter.RemainingPauseTime, HttpStatusCode.TooManyRequests);

                    // The limiter knows about the backoff; wait out what the server asked for first.
                    await Task.Delay((rateLimiter.RemainingPauseTime ?? TimeSpan.FromSeconds(60)) + AnilistRateLimiter.Jitter(), cancellationToken).ConfigureAwait(false);
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    if (++timeoutRetries > MaxTimeoutRetries)
                        throw new AnilistUnavailableException("AniList request timed out.", rateLimiter.RemainingPauseTime);

                    logger.LogTrace("AniList request timed out. Retrying ({Retry}/{Max}).", timeoutRetries, MaxTimeoutRetries);
                }
                catch (HttpRequestException ex)
                {
                    throw new AnilistUnavailableException("AniList could not be reached.", rateLimiter.RemainingPauseTime, ex.StatusCode, innerException: ex);
                }
            }
        }, cancellationToken).ConfigureAwait(false);
        var executed = DateTime.UtcNow - scheduledAt - waitTime;
        logger.LogTrace("Completed call: {DisplayName} (Waited {Waited}ms, Executed: {Delta}ms, {Attempts} attempts)", displayName, waitTime.TotalMilliseconds, executed.TotalMilliseconds, attempts);
        return result;
    }

    private async Task<(JsonNode? Result, bool Retry)> SendAsync(string query, Dictionary<string, object?> variables, CancellationToken cancellationToken)
    {
        using var content = new StringContent(JsonSerializer.Serialize(new { query, variables }), Encoding.UTF8, "application/json");
        using var response = await httpClient.PostAsync(string.Empty, content, cancellationToken).ConfigureAwait(false);

        RecordQuotaHeaders(response);

        var retryAfter = response.Headers.RetryAfter?.Delta
            ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null)
            ?? (TryGetHeaderInt(response, "X-RateLimit-Reset", out var resetSeconds) ? DateTimeOffset.FromUnixTimeSeconds(resetSeconds) - DateTimeOffset.UtcNow : (TimeSpan?)null);
        if (retryAfter is { } negative && negative < TimeSpan.Zero)
            retryAfter = null;

        if (response.StatusCode is HttpStatusCode.TooManyRequests)
        {
            logger.LogWarning("AniList rate limit hit. Retrying after {RetryAfter}s.", (retryAfter ?? TimeSpan.FromSeconds(60)).TotalSeconds);
            rateLimiter.NotifyRateLimitExceeded(retryAfter);
            return (null, true);
        }

        if ((int)response.StatusCode >= 500)
        {
            logger.LogWarning("AniList returned {StatusCode} {Reason}. Pausing AniList work.", (int)response.StatusCode, response.ReasonPhrase);
            rateLimiter.Notify5xxError();
            if (retryAfter is { } serverRetryAfter)
                rateLimiter.NotifyRateLimitExceeded(serverRetryAfter);
            throw new AnilistUnavailableException($"AniList returned {(int)response.StatusCode} {response.ReasonPhrase}.", rateLimiter.RemainingPauseTime, response.StatusCode);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        JsonNode? result;
        try
        {
            result = JsonNode.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new AnilistApiException("AniList returned a response that isn't valid JSON.", response.StatusCode, innerException: ex);
        }

        if (result?["errors"] is JsonArray { Count: > 0 } errorArray)
        {
            var errors = errorArray
                .Select(error => error?["message"]?.GetValue<string>())
                .Where(message => !string.IsNullOrEmpty(message))
                .Select(message => message!)
                .ToList();
            var statuses = errorArray
                .Select(error => error?["status"]?.GetValue<int?>())
                .Where(status => status.HasValue)
                .Select(status => status!.Value)
                .ToList();

            // A missing entity is a normal outcome, not a failure.
            if (statuses.Count > 0 && statuses.All(status => status == 404))
                return (null, false);

            if (statuses.Any(status => status == 429))
            {
                rateLimiter.NotifyRateLimitExceeded(retryAfter);
                return (null, true);
            }

            if (statuses.Any(status => status >= 500))
            {
                logger.LogWarning("AniList reported a server error: {Errors}. Pausing AniList work.", string.Join("; ", errors));
                rateLimiter.Notify5xxError();
                throw new AnilistUnavailableException($"AniList reported a server error: {string.Join("; ", errors)}", rateLimiter.RemainingPauseTime, response.StatusCode, errors);
            }

            throw new AnilistApiException($"AniList rejected the request: {string.Join("; ", errors)}", response.StatusCode, errors);
        }

        if (!response.IsSuccessStatusCode)
            throw new AnilistApiException($"AniList returned {(int)response.StatusCode} {response.ReasonPhrase}.", response.StatusCode);

        rateLimiter.NotifySuccess();
        return (result, false);
    }

    private void RecordQuotaHeaders(HttpResponseMessage response)
    {
        if (!TryGetHeaderInt(response, "X-RateLimit-Remaining", out var remaining))
            return;

        DateTimeOffset? resetAt = TryGetHeaderInt(response, "X-RateLimit-Reset", out var reset)
            ? DateTimeOffset.FromUnixTimeSeconds(reset)
            : null;
        int? limit = TryGetHeaderInt(response, "X-RateLimit-Limit", out var limitValue) ? limitValue : null;
        rateLimiter.NotifyQuota(limit, remaining, resetAt);
    }

    private static bool TryGetHeaderInt(HttpResponseMessage response, string name, out int value)
    {
        value = 0;
        return response.Headers.TryGetValues(name, out var values)
            && int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    #endregion
}

/// <summary>
/// What to search AniList for.
/// </summary>
public sealed record AnilistSearchOptions
{
    /// <summary>
    /// The text to search for.
    /// </summary>
    public required string Query { get; init; }

    /// <summary>
    /// Whether adult entries are included.
    /// </summary>
    public bool IncludeRestricted { get; init; }

    /// <summary>
    /// Only entries that started airing in this year.
    /// </summary>
    public int? Year { get; init; }

    /// <summary>
    /// Only entries released in this season.
    /// </summary>
    public YearlySeason? Season { get; init; }

    /// <summary>
    /// Only entries in this season year, which can differ from the start date
    /// for a late-December premiere.
    /// </summary>
    public int? SeasonYear { get; init; }

    /// <summary>
    /// Only entries of these types.
    /// </summary>
    public IReadOnlyList<AnimeType>? Types { get; init; }

    /// <summary>
    /// The page, from 1.
    /// </summary>
    public int Page { get; init; } = 1;

    /// <summary>
    /// The page size. Zero asks for the total alone.
    /// </summary>
    public int PageSize { get; init; } = 10;
}
