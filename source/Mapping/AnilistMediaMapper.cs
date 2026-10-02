using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Shoko.Abstractions.Extensions;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Storage;
using Shoko.Abstractions.Metadata.Stub;
using Shoko.Plugin.Anilist.Metadata;
using Shoko.Plugin.Anilist.Storage;

namespace Shoko.Plugin.Anilist.Mapping;

/// <summary>
/// One slot in an anime's airing schedule.
/// </summary>
/// <param name="ScheduleID">AniList's ID for the schedule entry.</param>
/// <param name="AiredAt">When it aired, in UTC.</param>
public readonly record struct AnilistScheduleEntry(int ScheduleID, DateTime? AiredAt);

/// <summary>
/// Reads AniList's GraphQL responses and turns them into the core's write
/// models and the plugin's own documents. Nothing here talks to AniList or to
/// a store; every method is a translation.
/// </summary>
public static partial class AnilistMediaMapper
{
    #region Regexes

    [GeneratedRegex(@"crunchyroll\.com/(?:[a-z]{2}(?:-[a-z]{2})?/)?series/(?<id>[A-Z0-9]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CrunchyrollRegex();

    [GeneratedRegex(@"netflix\.com/(?:[a-z]{2}(?:-[a-z]{2})?/)?title/(?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex NetflixRegex();

    [GeneratedRegex(@"(?:amazon|primevideo)\.[a-z.]+/(?:.*/)?(?:dp|detail|gp/video/detail)/(?<id>[A-Z0-9]{10})", RegexOptions.IgnoreCase)]
    private static partial Regex AmazonRegex();

    [GeneratedRegex(@"hidive\.com/(?:tv|movies|season)/(?<id>[^/?#]+)", RegexOptions.IgnoreCase)]
    private static partial Regex HidiveRegex();

    [GeneratedRegex(@"hulu\.com/series/(?<id>[^/?#]+)", RegexOptions.IgnoreCase)]
    private static partial Regex HuluRegex();

    [GeneratedRegex(@"disneyplus\.com/(?:[a-z]{2}(?:-[a-z]{2})?/)?(?:series|movies)/[^/?#]+/(?<id>[^/?#]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DisneyPlusRegex();

    [GeneratedRegex(@"bilibili\.(?:com|tv)/(?:[a-z]{2}/)?(?:bangumi/media/md|media/|play/)(?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex BilibiliRegex();

    [GeneratedRegex(@"youtube\.com/(?:channel/(?<id>[^/?#]+)|@(?<id>[^/?#]+)|playlist\?list=(?<id>[^&#]+))", RegexOptions.IgnoreCase)]
    private static partial Regex YoutubeRegex();

    [GeneratedRegex(@"^https?://(?:www\.|mobile\.)?(?:twitter|x|instagram|tiktok|threads)\.(?:com|net)/@?(?<id>[A-Za-z0-9_.]+)/?(?:[?#].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex SocialHandleRegex();

    #endregion

    #region Anime

    /// <summary>
    /// Reads a <c>Media</c> node.
    /// </summary>
    /// <param name="media">The <c>Media</c> node.</param>
    /// <returns>The anime.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="media"/> is <see langword="null"/>.</exception>
    public static AnilistMedia ReadMedia(JsonNode media)
    {
        ArgumentNullException.ThrowIfNull(media);

        var title = media["title"];
        return new()
        {
            ID = GetInt(media["id"]) ?? 0,
            MalID = GetInt(media["idMal"]) is > 0 and var malID ? malID : null,
            EnglishTitle = GetString(title?["english"])?.Trim() ?? string.Empty,
            MainTitle = GetString(title?["romaji"])?.Trim() ?? string.Empty,
            NativeTitle = GetString(title?["native"])?.Trim() ?? string.Empty,
            Synonyms = ReadStringList(media["synonyms"]),
            EnglishOverview = AnilistUtility.StripHtml(GetString(media["description"])),
            OriginalLanguageCode = AnilistUtility.ParseOriginalLanguage(GetString(media["countryOfOrigin"])),
            Type = AnilistUtility.ParseFormat(GetString(media["format"])),
            Status = AnilistUtility.ParseReleaseStatus(GetString(media["status"])),
            Source = AnilistUtility.InferSourceMaterial(
                GetString(media["source"]),
                GetBool(media["isAdult"]) ?? false,
                ReadSourceComicCountry(media),
                GetString(media["countryOfOrigin"])
            ),
            Season = AnilistUtility.ParseSeason(GetString(media["season"])),
            SeasonYear = GetInt(media["seasonYear"]),
            Genres = ReadStringList(media["genres"]),
            IsRestricted = GetBool(media["isAdult"]) ?? false,
            IsLicensed = GetBool(media["isLicensed"]) ?? true,
            CoverImagePath = AnilistImages.ToResourceID(GetString(media["coverImage"]?["extraLarge"])) ?? string.Empty,
            BannerImagePath = AnilistImages.ToResourceID(GetString(media["bannerImage"])) ?? string.Empty,
            Color = GetString(media["coverImage"]?["color"]) ?? string.Empty,
            EpisodeCount = GetInt(media["episodes"]) ?? 0,
            EpisodeDuration = GetInt(media["duration"]),
            AverageScore = GetInt(media["averageScore"]) ?? 0,
            MeanScore = GetInt(media["meanScore"]) ?? 0,
            ScoreVotes = SumScoreDistribution(media["stats"]?["scoreDistribution"]),
            FavoriteCount = GetInt(media["favourites"]) ?? 0,
            Popularity = GetInt(media["popularity"]) ?? 0,
            TrailerSite = GetString(media["trailer"]?["site"]),
            TrailerID = GetString(media["trailer"]?["id"]),
            StartDate = ToPartialDate(ReadDate(media["startDate"])),
            EndDate = ToPartialDate(ReadDate(media["endDate"])),
            ExternalLinks = ReadExternalLinks(media),
        };
    }

    /// <summary>
    /// The external links on a <c>Media</c> node.
    /// </summary>
    /// <param name="media">The <c>Media</c> node.</param>
    /// <returns>The links, skipping any without an ID or a URL.</returns>
    public static IReadOnlyList<AnilistExternalLink> ReadExternalLinks(JsonNode media)
    {
        ArgumentNullException.ThrowIfNull(media);

        List<AnilistExternalLink> links = [];
        if (media["externalLinks"] is not JsonArray array)
            return links;

        foreach (var node in array)
        {
            if (node is null || GetInt(node["id"]) is not { } id || id is 0 || GetString(node["url"]) is not { Length: > 0 } url)
                continue;

            links.Add(new()
            {
                ID = id,
                Url = url,
                Site = GetString(node["site"]) ?? string.Empty,
                LinkType = GetString(node["type"]) ?? string.Empty,
                LanguageCode = AnilistUtility.ToLanguageCode(GetString(node["language"])),
            });
        }

        return links;
    }

    /// <summary>
    /// The anime as the core's series store takes it, with its episodes.
    /// </summary>
    /// <remarks>
    /// AniList has no seasons, so the episodes sit in none. Films come back
    /// as series too, since AniList files them as anime of their own format.
    /// </remarks>
    /// <param name="media">The anime.</param>
    /// <param name="episodes">The episodes, as <see cref="BuildEpisodes"/> made them.</param>
    /// <returns>The series to save.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static MetadataSeriesData ToSeriesData(AnilistMedia media, IReadOnlyList<AnilistStoredEpisode> episodes)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(episodes);

        return new()
        {
            ID = AnilistUtility.SeriesGuid(media.ID),
            Titles = Titles(media),
            Overviews = Overviews(media),
            Type = media.Type,
            AirDate = media.StartDate,
            EndDate = media.EndDate,
            Rating = media.AverageScore / 10.0,
            RatingVotes = media.ScoreVotes,
            Restricted = media.IsRestricted,
            ReleaseStatus = media.Status,
            SourceMaterial = media.Source,
            OriginalLanguageCode = string.IsNullOrEmpty(media.OriginalLanguageCode) ? null : media.OriginalLanguageCode,
            Popularity = media.Popularity,
            FavoriteCount = media.FavoriteCount,
            Resources = Resources(media),
            CrossSourceIDs = CrossSourceIDs(media),
            Episodes = [.. episodes.Select(ToEpisodeData)],
            DefaultImageResourceIDs = AnilistImages.ToDefaultImages((ImageEntityType.Primary, media.CoverImagePath), (ImageEntityType.Banner, media.BannerImagePath)),
        };
    }

    /// <summary>
    /// The IDs other sources gave the anime, as AniList lists them: its
    /// MyAnimeList ID, under the <c>mal</c> source whether or not anything
    /// registered it.
    /// </summary>
    /// <param name="media">The anime.</param>
    /// <returns>The IDs, or an empty list when AniList lists none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="media"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<MetadataGuid> CrossSourceIDs(AnilistMedia media)
    {
        ArgumentNullException.ThrowIfNull(media);

        return media.MalID is { } malID
            ? [new MetadataGuid(MetadataSource.Parse("mal"), MetadataEntityType.Series, AnilistUtility.FormatID(malID))]
            : [];
    }

    /// <summary>
    /// The main, native and English titles, and the synonyms, which AniList
    /// does not give a language.
    /// </summary>
    /// <param name="media">The anime.</param>
    /// <returns>The titles, the main one first.</returns>
    public static IReadOnlyList<ITitle> Titles(AnilistMedia media)
    {
        ArgumentNullException.ThrowIfNull(media);

        List<ITitle> titles = [];
        if (!string.IsNullOrEmpty(media.MainTitle))
        {
            var language = AnilistUtility.GetMainTitleLanguage(media.OriginalLanguageCode);
            titles.Add(new TitleStub
            {
                Source = MetadataSource.AniList,
                Language = language,
                LanguageCode = language.GetString(),
                Value = media.MainTitle,
                Type = TitleType.Main,
            });
        }

        if (!string.IsNullOrEmpty(media.NativeTitle))
            titles.Add(Title(media.NativeTitle, media.OriginalLanguageCode, TitleType.Official));
        if (!string.IsNullOrEmpty(media.EnglishTitle))
            titles.Add(Title(media.EnglishTitle, "en", TitleType.Official));
        foreach (var synonym in media.Synonyms)
            titles.Add(Title(synonym, "unk", TitleType.Synonym));

        return titles;
    }

    /// <summary>
    /// The one English overview AniList carries, when it has one.
    /// </summary>
    /// <param name="media">The anime.</param>
    /// <returns>The overviews.</returns>
    public static IReadOnlyList<IText> Overviews(AnilistMedia media)
    {
        ArgumentNullException.ThrowIfNull(media);

        return string.IsNullOrEmpty(media.EnglishOverview)
            ? []
            : [new TextStub { Source = MetadataSource.AniList, Language = TitleLanguage.English, LanguageCode = "en", Value = media.EnglishOverview }];
    }

    /// <summary>
    /// The AniList page, the MyAnimeList cross-reference, the trailer and
    /// every external link AniList lists, each with its bare ID where the
    /// site has IDs of its own.
    /// </summary>
    /// <param name="media">The anime.</param>
    /// <returns>The resources.</returns>
    public static IReadOnlyList<Resource> Resources(AnilistMedia media)
    {
        ArgumentNullException.ThrowIfNull(media);

        var id = AnilistUtility.FormatID(media.ID);
        List<Resource> resources =
        [
            new() { Type = ResourceType.Metadata, Name = "AniList", Url = AnilistUtility.AnimeUrl(media.ID), ID = id },
        ];
        if (media.MalID is { } malID)
        {
            var mal = AnilistUtility.FormatID(malID);
            resources.Add(new() { Type = ResourceType.CrossReference, Name = "MyAnimeList", Url = $"https://myanimelist.net/anime/{mal}", ID = mal });
        }

        switch (media.TrailerSite?.ToLowerInvariant(), media.TrailerID)
        {
            case ("youtube", { Length: > 0 } trailerID):
                resources.Add(new() { Type = ResourceType.Trailer, Name = "YouTube", Url = $"https://www.youtube.com/watch?v={trailerID}", ID = trailerID });
                break;
            case ("dailymotion", { Length: > 0 } trailerID):
                resources.Add(new() { Type = ResourceType.Trailer, Name = "Dailymotion", Url = $"https://www.dailymotion.com/video/{trailerID}", ID = trailerID });
                break;
        }

        foreach (var link in media.ExternalLinks)
        {
            resources.Add(new()
            {
                Type = link.LinkType.ToUpperInvariant() switch
                {
                    "STREAMING" => ResourceType.Streaming,
                    "SOCIAL" => ResourceType.Social,
                    _ => ResourceType.Website,
                },
                Name = link.Site,
                Url = link.Url,
                ID = ReadExternalID(link.Url),
                LanguageCode = link.LanguageCode,
            });
        }

        return resources;
    }

    /// <summary>
    /// The site's own ID for what an external link points at, for the sites
    /// whose URLs carry one.
    /// </summary>
    /// <param name="url">The link's URL.</param>
    /// <returns>The ID, or <see langword="null"/> for a site without IDs or a URL of another shape.</returns>
    public static string? ReadExternalID(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        foreach (var regex in (Regex[])[CrunchyrollRegex(), NetflixRegex(), AmazonRegex(), HidiveRegex(), HuluRegex(), DisneyPlusRegex(), BilibiliRegex(), YoutubeRegex(), SocialHandleRegex()])
        {
            if (regex.Match(url) is { Success: true } match && match.Groups["id"].Value is { Length: > 0 } id)
                return Uri.UnescapeDataString(id);
        }

        return null;
    }

    /// <summary>
    /// What the plugin keeps of an anime besides the core's stores.
    /// </summary>
    /// <param name="media">The anime.</param>
    /// <param name="existing">The document stored before, if any.</param>
    /// <param name="now">The time to stamp a new or fully refreshed document with.</param>
    /// <param name="quick">Whether this was a quick refresh, which leaves the document looking newly added.</param>
    /// <returns>The document.</returns>
    public static AnilistStoredAnime ToStoredAnime(AnilistMedia media, AnilistStoredAnime? existing, DateTime now, bool quick)
    {
        ArgumentNullException.ThrowIfNull(media);

        var createdAt = existing?.CreatedAt ?? now;
        return new()
        {
            ID = media.ID,
            MalID = media.MalID,
            Season = media.Season,
            SeasonYear = media.SeasonYear,
            IsLicensed = media.IsLicensed,
            EpisodeCount = media.EpisodeCount,
            EpisodeDuration = media.EpisodeDuration,
            MeanScore = media.MeanScore,
            CoverImagePath = media.CoverImagePath,
            BannerImagePath = media.BannerImagePath,
            Color = media.Color,
            CreatedAt = createdAt,
            LastUpdatedAt = quick ? existing?.LastUpdatedAt ?? createdAt : now,
        };
    }

    private static TitleStub Title(string value, string? languageCode, TitleType type)
        => new()
        {
            Source = MetadataSource.AniList,
            Language = string.IsNullOrEmpty(languageCode) ? TitleLanguage.Unknown : languageCode.GetTitleLanguage(),
            LanguageCode = string.IsNullOrEmpty(languageCode) ? "unk" : languageCode,
            Value = value,
            Type = type,
        };

    #endregion

    #region Episodes

    /// <summary>
    /// Adds one page of an airing schedule to what was read so far.
    /// </summary>
    /// <param name="page">The <c>airingSchedule</c> node.</param>
    /// <param name="schedule">The slots read so far, by episode number.</param>
    public static void ReadSchedulePage(JsonNode? page, Dictionary<int, AnilistScheduleEntry> schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        if (page?["nodes"] is not JsonArray nodes)
            return;

        foreach (var node in nodes)
        {
            if (node is null)
                continue;

            var scheduleID = GetInt(node["id"]) ?? 0;
            var episodeNumber = GetInt(node["episode"]) ?? 0;
            var airingAt = GetLong(node["airingAt"]) ?? 0;
            if (scheduleID is 0 || episodeNumber is 0)
                continue;

            schedule[episodeNumber] = new(scheduleID, airingAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(airingAt).UtcDateTime : null);
        }
    }

    /// <summary>
    /// Adds one page of several anime's airing schedules to what was read so
    /// far.
    /// </summary>
    /// <param name="page">The <c>Page</c> node of an <c>airingSchedules</c> query.</param>
    /// <param name="slots">The slots read so far, in UTC, by anime and then by episode number.</param>
    public static void ReadAiringSchedulesPage(JsonNode? page, Dictionary<int, Dictionary<int, DateTime>> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);

        if (page?["airingSchedules"] is not JsonArray nodes)
            return;

        foreach (var node in nodes)
        {
            if (node is null)
                continue;

            var animeID = GetInt(node["mediaId"]) ?? 0;
            var episodeNumber = GetInt(node["episode"]) ?? 0;
            var airingAt = GetLong(node["airingAt"]) ?? 0;
            if (animeID is 0 || episodeNumber is 0 || airingAt <= 0)
                continue;

            if (!slots.TryGetValue(animeID, out var episodes))
                slots[animeID] = episodes = [];

            episodes[episodeNumber] = DateTimeOffset.FromUnixTimeSeconds(airingAt).UtcDateTime;
        }
    }

    /// <summary>
    /// Builds the episodes of an anime: one per number up to the larger of the
    /// episode count and the last scheduled episode, each carrying its slot
    /// when the schedule has one.
    /// </summary>
    /// <param name="media">The anime.</param>
    /// <param name="schedule">The slots, by episode number.</param>
    /// <param name="existing">The episode documents stored before.</param>
    /// <param name="now">The time to stamp new and changed documents with.</param>
    /// <returns>Every episode the anime has now, and whether any differ from before.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static (IReadOnlyList<AnilistStoredEpisode> Episodes, bool Changed) BuildEpisodes(
        AnilistMedia media,
        IReadOnlyDictionary<int, AnilistScheduleEntry> schedule,
        IReadOnlyList<AnilistStoredEpisode> existing,
        DateTime now
    )
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(existing);

        if (!AnilistUtility.CanPackEpisodeID(media.ID, 0))
            return ([], existing.Count > 0);

        var count = Math.Min(Math.Max(media.EpisodeCount, schedule.Count > 0 ? schedule.Keys.Max() : 0), AnilistUtility.MaxEpisodeNumber);
        var byNumber = existing.GroupBy(episode => episode.EpisodeNumber).ToDictionary(group => group.Key, group => group.First());
        var changed = existing.Any(episode => episode.EpisodeNumber > count || episode.EpisodeNumber < 1);
        List<AnilistStoredEpisode> episodes = [];
        for (var number = 1; number <= count; number++)
        {
            var slot = schedule.TryGetValue(number, out var entry) ? entry : (AnilistScheduleEntry?)null;
            if (!byNumber.TryGetValue(number, out var episode))
            {
                changed = true;
                episodes.Add(new()
                {
                    ID = AnilistUtility.PackEpisodeID(media.ID, number),
                    AnimeID = media.ID,
                    EpisodeNumber = number,
                    ScheduleID = slot?.ScheduleID,
                    RuntimeMinutes = media.EpisodeDuration,
                    AiredAt = slot?.AiredAt,
                    CreatedAt = now,
                    LastUpdatedAt = now,
                });
                continue;
            }

            if (episode.ScheduleID != slot?.ScheduleID || episode.RuntimeMinutes != media.EpisodeDuration || episode.AiredAt != slot?.AiredAt)
            {
                changed = true;
                episode = new()
                {
                    ID = episode.ID,
                    AnimeID = episode.AnimeID,
                    EpisodeNumber = episode.EpisodeNumber,
                    ScheduleID = slot?.ScheduleID,
                    RuntimeMinutes = media.EpisodeDuration,
                    AiredAt = slot?.AiredAt,
                    CreatedAt = episode.CreatedAt,
                    LastUpdatedAt = now,
                };
            }

            episodes.Add(episode);
        }

        return (episodes, changed);
    }

    /// <summary>
    /// An episode as the core's series store takes it.
    /// </summary>
    /// <remarks>
    /// AniList does not name its episodes, so each gets "Episode" and its
    /// number, in English.
    /// </remarks>
    /// <param name="episode">The episode.</param>
    /// <returns>The episode to save.</returns>
    public static MetadataEpisodeData ToEpisodeData(AnilistStoredEpisode episode)
    {
        ArgumentNullException.ThrowIfNull(episode);

        return new()
        {
            ID = AnilistUtility.EpisodeGuid(episode.ID),
            EpisodeNumber = episode.EpisodeNumber,
            Type = EpisodeType.Episode,
            Runtime = episode.RuntimeMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : TimeSpan.Zero,
            AirDateWithTime = episode.AiredAt is { } airedAt ? DateTime.SpecifyKind(airedAt, DateTimeKind.Utc) : null,
            Titles =
            [
                new TitleStub
                {
                    Source = MetadataSource.AniList,
                    Language = TitleLanguage.English,
                    LanguageCode = "en",
                    Value = $"Episode {episode.EpisodeNumber.ToString(CultureInfo.InvariantCulture)}",
                    Type = TitleType.Main,
                },
            ],
        };
    }

    #endregion

    #region Tags & Studios

    /// <summary>
    /// The tags on a <c>Media</c> node, and how each applies to the anime.
    /// </summary>
    /// <param name="media">The <c>Media</c> node.</param>
    /// <returns>The tags to store, and the anime's entries for them, most relevant first.</returns>
    public static (List<MetadataTagData> Tags, List<MetadataEntryTagData> Entries) ReadTags(JsonNode media)
    {
        ArgumentNullException.ThrowIfNull(media);

        List<MetadataTagData> tags = [];
        List<(int ID, MetadataEntryTagData Entry)> entries = [];
        if (media["tags"] is not JsonArray array)
            return (tags, []);

        foreach (var node in array)
        {
            if (node is null || GetInt(node["id"]) is not { } id || id is 0 || entries.Any(entry => entry.ID == id))
                continue;

            var tagID = AnilistUtility.Guid(MetadataEntityType.Tag, id);
            tags.Add(new()
            {
                ID = tagID,
                Name = GetString(node["name"]) ?? string.Empty,
                Overview = GetString(node["description"]) ?? string.Empty,
                Category = GetString(node["category"]) is { Length: > 0 } category ? category : null,
                Kind = TagKind.Tag,
                IsSpoiler = GetBool(node["isGeneralSpoiler"]) ?? false,
                IsRestricted = GetBool(node["isAdult"]) ?? false,
            });
            entries.Add((id, new() { TagID = tagID, Weight = GetInt(node["rank"]) ?? 0, IsSpoiler = GetBool(node["isMediaSpoiler"]) ?? false }));
        }

        return (tags, [.. entries.OrderByDescending(entry => entry.Entry.Weight).ThenBy(entry => entry.ID).Select(entry => entry.Entry)]);
    }

    /// <summary>
    /// The genres of an anime as tags of the genre kind. AniList's genres
    /// have no IDs, so each is keyed on its name.
    /// </summary>
    /// <param name="genres">The genre names.</param>
    /// <returns>The genres to store.</returns>
    public static List<MetadataTagData> GenresAsTags(IEnumerable<string> genres)
    {
        ArgumentNullException.ThrowIfNull(genres);

        return [.. genres
            .Where(genre => !string.IsNullOrWhiteSpace(genre))
            .Select(genre => genre.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(genre => new MetadataTagData { ID = AnilistUtility.GenreGuid(genre), Name = genre, Kind = TagKind.Genre })];
    }

    /// <summary>
    /// How an anime's genres apply to it, in AniList's order.
    /// </summary>
    /// <param name="genres">The genre names.</param>
    /// <returns>The entry's genres.</returns>
    public static List<MetadataEntryTagData> GenreEntries(IEnumerable<string> genres)
        => [.. GenresAsTags(genres).Select(genre => new MetadataEntryTagData { TagID = genre.ID })];

    /// <summary>
    /// The studios on a <c>Media</c> node.
    /// </summary>
    /// <param name="media">The <c>Media</c> node.</param>
    /// <returns>The studios to store, and what each did for the anime, main studios first.</returns>
    public static (List<MetadataStudioData> Studios, List<MetadataEntryStudioData> Entries) ReadStudios(JsonNode media)
    {
        ArgumentNullException.ThrowIfNull(media);

        List<(MetadataStudioData Studio, StudioType Type, bool IsMain)> read = [];
        if (media["studios"]?["edges"] is JsonArray edges)
        {
            foreach (var edge in edges)
            {
                if (edge?["node"] is not { } node || GetInt(node["id"]) is not { } id || id is 0)
                    continue;

                var studioID = AnilistUtility.Guid(MetadataEntityType.Studio, id);
                if (read.Any(entry => entry.Studio.ID == studioID))
                    continue;

                read.Add((
                    new() { ID = studioID, Name = GetString(node["name"]) ?? string.Empty },
                    // AniList sorts its studios into the ones that animate and
                    // the rest, which it calls producers.
                    GetBool(node["isAnimationStudio"]) is true ? StudioType.Animation : StudioType.Production,
                    GetBool(edge["isMain"]) ?? false
                ));
            }
        }

        var ordered = read.OrderByDescending(entry => entry.IsMain).ToList();
        return (
            [.. ordered.Select(entry => entry.Studio)],
            [.. ordered.Select(entry => new MetadataEntryStudioData { StudioID = entry.Studio.ID, Type = entry.Type })]
        );
    }

    #endregion

    #region Relations & Suggestions

    /// <summary>
    /// The relations on a <c>Media</c> node, to other anime only: AniList
    /// relates anime to manga and novels as well, which nothing here can hold.
    /// </summary>
    /// <param name="media">The <c>Media</c> node.</param>
    /// <returns>The relations.</returns>
    public static List<MetadataRelationData> ReadRelations(JsonNode media)
    {
        ArgumentNullException.ThrowIfNull(media);

        List<MetadataRelationData> relations = [];
        if (media["relations"]?["edges"] is not JsonArray edges)
            return relations;

        foreach (var edge in edges)
        {
            if (edge?["node"] is not { } node || GetInt(node["id"]) is not { } id || id is 0)
                continue;

            if (GetString(edge["relationType"]) is not { Length: > 0 } relationType)
                continue;

            if (!string.Equals(GetString(node["type"]), "ANIME", StringComparison.OrdinalIgnoreCase))
                continue;

            var relation = new MetadataRelationData
            {
                RelatedID = AnilistUtility.SeriesGuid(id),
                RelationType = AnilistUtility.ParseRelationType(relationType),
            };
            if (!relations.Contains(relation))
                relations.Add(relation);
        }

        return relations;
    }

    /// <summary>
    /// The country the comic an anime was adapted from comes from: the
    /// <c>countryOfOrigin</c> of the manga-format media the anime relates to
    /// as its <c>SOURCE</c>, or as an <c>ADAPTATION</c> when there is no
    /// <c>SOURCE</c> one.
    /// </summary>
    /// <param name="media">The <c>Media</c> node.</param>
    /// <returns>
    /// The country code, or <see langword="null"/> when the anime relates to
    /// no such comic, or the comic has no country.
    /// </returns>
    public static string? ReadSourceComicCountry(JsonNode media)
    {
        ArgumentNullException.ThrowIfNull(media);

        if (media["relations"]?["edges"] is not JsonArray edges)
            return null;

        string? adaptationCountry = null;
        foreach (var edge in edges)
        {
            if (edge?["node"] is not { } node)
                continue;

            if (GetString(node["format"])?.ToUpperInvariant() is not ("MANGA" or "ONE_SHOT"))
                continue;

            if (GetString(node["countryOfOrigin"]) is not { Length: > 0 } country)
                continue;

            switch (GetString(edge["relationType"])?.ToUpperInvariant())
            {
                case "SOURCE":
                    return country;
                case "ADAPTATION":
                    adaptationCountry ??= country;
                    break;
            }
        }

        return adaptationCountry;
    }

    /// <summary>
    /// Adds one page of recommendations to what was read so far.
    /// </summary>
    /// <param name="page">The <c>recommendations</c> node.</param>
    /// <param name="anilistAnimeID">The anime the recommendations are for, which never recommends itself.</param>
    /// <param name="suggestions">The suggestions read so far, in AniList's order.</param>
    /// <returns>The lowest rating on the page, or <see cref="int.MaxValue"/> for an empty one.</returns>
    public static int ReadRecommendationPage(JsonNode? page, int anilistAnimeID, List<MetadataSuggestionData> suggestions)
    {
        ArgumentNullException.ThrowIfNull(suggestions);

        var lowestRating = int.MaxValue;
        if (page?["nodes"] is not JsonArray nodes)
            return lowestRating;

        foreach (var entry in nodes)
        {
            // A recommendation whose target was deleted upstream comes back null.
            if (entry?["mediaRecommendation"] is not { } node)
                continue;

            var rating = GetInt(entry["rating"]) ?? 0;
            if (rating < lowestRating)
                lowestRating = rating;

            if (GetInt(node["id"]) is not { } id || id is 0 || id == anilistAnimeID)
                continue;

            // AniList recommends manga and novels from an anime as well.
            if (!string.Equals(GetString(node["type"]), "ANIME", StringComparison.OrdinalIgnoreCase))
                continue;

            var suggestedID = AnilistUtility.SeriesGuid(id);
            if (suggestions.Any(suggestion => suggestion.SuggestedID == suggestedID))
                continue;

            suggestions.Add(new()
            {
                SuggestedID = suggestedID,
                Kind = SuggestionKind.Recommended,
                Order = suggestions.Count,
                Score = rating,
            });
        }

        return lowestRating;
    }

    /// <summary>
    /// Merges an anime's own recommendations with the ones stored the other
    /// way round, best scored first.
    /// </summary>
    /// <remarks>
    /// AniList holds one undirected recommendation and serves it from both
    /// sides with the same score, so an edge stored while refreshing the other
    /// anime is this one's too. How far recommendations are paged means an
    /// edge can be above the cutoff on one side and below it on the other, so
    /// merging recovers entries rather than only deduplicating them. The
    /// anime's own copy wins a duplicate, and the merged list is numbered
    /// again in its new order.
    /// </remarks>
    /// <param name="anilistAnimeID">The anime the list is for.</param>
    /// <param name="direct">What AniList just said the anime recommends.</param>
    /// <param name="reverse">What the other stored anime recommend of their own accord naming this one, with the score.</param>
    /// <returns>The merged list.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static List<MetadataSuggestionData> MergeSuggestions(
        int anilistAnimeID,
        IEnumerable<MetadataSuggestionData> direct,
        IEnumerable<(MetadataGuid SuggestedBy, int? Score)> reverse
    )
    {
        ArgumentNullException.ThrowIfNull(direct);
        ArgumentNullException.ThrowIfNull(reverse);

        var self = AnilistUtility.SeriesGuid(anilistAnimeID);
        return [.. direct
            .Concat(reverse
                .Where(edge => edge.SuggestedBy.Source == MetadataSource.AniList && edge.SuggestedBy.EntityType == MetadataEntityType.Series)
                .Select(edge => new MetadataSuggestionData { SuggestedID = edge.SuggestedBy, Kind = SuggestionKind.Recommended, Score = edge.Score }))
            .Where(suggestion => suggestion.SuggestedID != self)
            .DistinctBy(suggestion => suggestion.SuggestedID)
            .OrderByDescending(suggestion => suggestion.Score ?? int.MinValue)
            .ThenBy(suggestion => suggestion.SuggestedID.TryGetNumericID<int>(out var id) ? id : int.MaxValue)
            .Select((suggestion, index) => suggestion with { Order = index })];
    }

    #endregion

    #region People

    /// <summary>
    /// Adds one page of characters, and the people voicing them, to what was
    /// read so far.
    /// </summary>
    /// <param name="page">The <c>characters</c> node.</param>
    /// <param name="people">Where the characters, people, credits and portraits are gathered.</param>
    public static void ReadCharacterPage(JsonNode? page, AnilistPeople people)
    {
        ArgumentNullException.ThrowIfNull(people);

        if (page?["edges"] is not JsonArray edges)
            return;

        foreach (var edge in edges)
        {
            if (edge?["node"] is not { } characterNode || GetInt(characterNode["id"]) is not { } characterID || characterID is 0)
                continue;

            var character = ReadCharacter(characterID, characterNode);
            people.Characters[characterID] = character;
            AddPortrait(people, character.ID, characterNode);

            var roleType = AnilistUtility.ParseCastRoleType(GetString(edge["role"]));
            var voiced = false;
            if (edge["voiceActorRoles"] is JsonArray voiceActorRoles)
            {
                foreach (var voiceActorRole in voiceActorRoles)
                {
                    if (voiceActorRole?["voiceActor"] is not { } staffNode || GetInt(staffNode["id"]) is not { } creatorID || creatorID is 0)
                        continue;

                    var creator = ReadCreator(creatorID, staffNode);
                    people.Creators[creatorID] = creator;
                    AddPortrait(people, creator.ID, staffNode);
                    if (people.Cast.Any(cast => cast.CharacterID == character.ID && cast.CreatorID == creator.ID))
                        continue;

                    voiced = true;
                    people.Cast.Add(new()
                    {
                        CharacterID = character.ID,
                        CreatorID = creator.ID,
                        Name = character.Name,
                        RoleType = roleType,
                        LanguageCode = AnilistUtility.ToLanguageCode(GetString(staffNode["languageV2"])),
                        RoleNotes = GetString(voiceActorRole["roleNotes"]) is { Length: > 0 } roleNotes ? roleNotes : null,
                        DubGroup = GetString(voiceActorRole["dubGroup"]) is { Length: > 0 } dubGroup ? dubGroup : null,
                    });
                }
            }

            // A character nobody voices still appears in the anime.
            if (!voiced && !people.Cast.Any(cast => cast.CharacterID == character.ID))
                people.Cast.Add(new() { CharacterID = character.ID, Name = character.Name, RoleType = roleType });
        }
    }

    /// <summary>
    /// Adds one page of staff to what was read so far.
    /// </summary>
    /// <remarks>
    /// The language AniList appends to a dub role, such as "ADR Director
    /// (English)", is split off into the credit's language; a role without
    /// one is the original production's, and takes the anime's language. A
    /// credit is known by its person and its job, so when the same person
    /// holds the same job in two languages, the second keeps AniList's whole
    /// wording as its job.
    /// </remarks>
    /// <param name="page">The <c>staff</c> node.</param>
    /// <param name="people">Where the people, credits and portraits are gathered.</param>
    /// <param name="originalLanguageCode">The anime's original language.</param>
    public static void ReadStaffPage(JsonNode? page, AnilistPeople people, string? originalLanguageCode)
    {
        ArgumentNullException.ThrowIfNull(people);

        if (page?["edges"] is not JsonArray edges)
            return;

        foreach (var edge in edges)
        {
            if (edge?["node"] is not { } staffNode || GetInt(staffNode["id"]) is not { } creatorID || creatorID is 0)
                continue;

            var creator = ReadCreator(creatorID, staffNode);
            people.Creators[creatorID] = creator;
            AddPortrait(people, creator.ID, staffNode);

            var rawRole = GetString(edge["role"])?.Trim() ?? string.Empty;
            if (!people.SeenCrew.Add((creator.ID, rawRole)))
                continue;

            var (role, language) = AnilistUtility.SplitRoleLanguage(rawRole);
            var name = people.Crew.Any(crew => crew.CreatorID == creator.ID && string.Equals(crew.Name, role, StringComparison.Ordinal)) ? rawRole : role;
            if (people.Crew.Any(crew => crew.CreatorID == creator.ID && string.Equals(crew.Name, name, StringComparison.Ordinal)))
                continue;

            people.Crew.Add(new()
            {
                CreatorID = creator.ID,
                Name = name,
                RoleType = AnilistUtility.ParseCrewRoleType(rawRole),
                LanguageCode = language?.GetString() ?? (string.IsNullOrEmpty(originalLanguageCode) ? null : originalLanguageCode),
            });
        }
    }

    /// <summary>
    /// Maps AniList's free-text gender to a <see cref="PersonGender"/>.
    /// </summary>
    /// <param name="gender">The gender, e.g. "Female".</param>
    /// <returns>The gender, or <see cref="PersonGender.Unknown"/>.</returns>
    public static PersonGender ParseGender(string? gender)
        => gender?.Trim().ToLowerInvariant() switch
        {
            "female" => PersonGender.Female,
            "male" => PersonGender.Male,
            "non-binary" or "nonbinary" or "non binary" => PersonGender.NonBinary,
            _ => PersonGender.Unknown,
        };

    private static MetadataCharacterData ReadCharacter(int characterID, JsonNode node)
    {
        var id = AnilistUtility.FormatID(characterID);
        var name = GetString(node["name"]?["full"])?.Trim() ?? string.Empty;
        return new()
        {
            ID = AnilistUtility.Guid(MetadataEntityType.Character, characterID),
            Name = name,
            OriginalName = GetString(node["name"]?["native"])?.Trim() is { Length: > 0 } native ? native : null,
            Overview = AnilistUtility.StripHtml(GetString(node["description"])) is { Length: > 0 } description ? description : null,
            Type = CharacterType.Character,
            AlternativeNames = AlternativeNames(node, name),
            Gender = ParseGender(GetString(node["gender"])),
            BirthDay = ToFuzzyDate(ReadDateParts(node["dateOfBirth"])),
            Resources = [new() { Type = ResourceType.Metadata, Name = "AniList", Url = GetString(node["siteUrl"]) is { Length: > 0 } url ? url : AnilistUtility.CharacterUrl(characterID), ID = id }],
            DefaultImageResourceIDs = PortraitDefault(node),
        };
    }

    private static MetadataCreatorData ReadCreator(int creatorID, JsonNode node)
    {
        var id = AnilistUtility.FormatID(creatorID);
        var name = GetString(node["name"]?["full"])?.Trim() ?? string.Empty;
        return new()
        {
            ID = AnilistUtility.Guid(MetadataEntityType.Creator, creatorID),
            Name = name,
            OriginalName = GetString(node["name"]?["native"])?.Trim() is { Length: > 0 } native ? native : null,
            Overview = AnilistUtility.StripHtml(GetString(node["description"])) is { Length: > 0 } description ? description : null,
            Type = CreatorType.Person,
            AlternativeNames = AlternativeNames(node, name),
            Gender = ParseGender(GetString(node["gender"])),
            BirthDay = ToFuzzyDate(ReadDateParts(node["dateOfBirth"])),
            DeathDay = ToFuzzyDate(ReadDateParts(node["dateOfDeath"])),
            Resources = [new() { Type = ResourceType.Metadata, Name = "AniList", Url = GetString(node["siteUrl"]) is { Length: > 0 } url ? url : AnilistUtility.StaffUrl(creatorID), ID = id }],
            DefaultImageResourceIDs = PortraitDefault(node),
        };
    }

    private static List<MetadataNameData> AlternativeNames(JsonNode node, string name)
        => [.. ReadStringList(node["name"]?["alternative"])
            .Where(alternative => !string.Equals(alternative, name, StringComparison.Ordinal))
            .Select(alternative => new MetadataNameData { Name = alternative })];

    /// <summary>
    /// The portrait a <c>Character</c> or <c>Staff</c> node names, as the
    /// entry's default image.
    /// </summary>
    /// <param name="node">The node.</param>
    /// <returns>
    /// The default, or <see langword="null"/> when the node names no portrait,
    /// which keeps the stored one as the portrait itself is kept.
    /// </returns>
    private static Dictionary<ImageEntityType, string>? PortraitDefault(JsonNode node)
        => AnilistImages.ToDefaultImages((ImageEntityType.Primary, AnilistImages.ToResourceID(GetString(node["image"]?["large"])))) is { Count: > 0 } defaults
            ? defaults
            : null;

    private static void AddPortrait(AnilistPeople people, MetadataGuid id, JsonNode node)
    {
        if (AnilistImages.ToResourceID(GetString(node["image"]?["large"])) is not { } path)
            return;

        people.Portraits[id] = path;
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Whether a page says there is another after it, and which page it was.
    /// </summary>
    /// <param name="page">A paged node.</param>
    /// <returns>Whether to keep going, and the page to ask for next.</returns>
    public static (bool HasNextPage, int NextPage) ReadPageInfo(JsonNode? page)
    {
        var pageInfo = page?["pageInfo"];
        return (GetBool(pageInfo?["hasNextPage"]) ?? false, (GetInt(pageInfo?["currentPage"]) ?? 1) + 1);
    }

    /// <summary>
    /// Reads a fuzzy date node.
    /// </summary>
    /// <param name="node">A node with <c>year</c>, <c>month</c> and <c>day</c>.</param>
    /// <returns>The parts; all <see langword="null"/> when there is no year.</returns>
    public static (int? Year, int? Month, int? Day) ReadDate(JsonNode? node)
    {
        if (GetInt(node?["year"]) is not { } year)
            return (null, null, null);

        return (year, GetInt(node?["month"]), GetInt(node?["day"]));
    }

    /// <summary>
    /// Reads a fuzzy date node as it is, with any part left out.
    /// </summary>
    /// <param name="node">A node with <c>year</c>, <c>month</c> and <c>day</c>.</param>
    /// <returns>The parts, each <see langword="null"/> when unknown.</returns>
    public static (int? Year, int? Month, int? Day) ReadDateParts(JsonNode? node)
        => (GetInt(node?["year"]), GetInt(node?["month"]), GetInt(node?["day"]));

    /// <summary>
    /// Builds a person's date out of whatever parts AniList knows, so a
    /// birthday without a year is kept.
    /// </summary>
    /// <param name="parts">The year, the month and the day, each if known.</param>
    /// <returns>
    /// The date, or <see langword="null"/> when no part is usable. A part that
    /// is out of range is dropped rather than thrown over, and so is a day
    /// once its month is gone.
    /// </returns>
    public static FuzzyDateOnly? ToFuzzyDate((int? Year, int? Month, int? Day) parts)
    {
        var year = parts.Year is >= 1 and <= 9999 ? parts.Year : null;
        var month = parts.Month is >= 1 and <= 12 ? parts.Month : null;
        var day = month.HasValue ? parts.Day : null;
        if (!FuzzyDateOnly.IsValid(year, month, day))
            day = null;

        return FuzzyDateOnly.IsValid(year, month, day) ? new FuzzyDateOnly(year, month, day) : null;
    }

    /// <summary>
    /// Builds a partial date out of its parts.
    /// </summary>
    /// <param name="parts">The year, the month if known and the day if known.</param>
    /// <returns>
    /// The date, or <see langword="null"/> without a usable year. A part that
    /// is out of range is dropped rather than thrown over.
    /// </returns>
    public static PartialDateOnly? ToPartialDate((int? Year, int? Month, int? Day) parts)
    {
        var (year, month, day) = parts;
        if (year is not (>= 1 and <= 9999))
            return null;

        if (month is not (>= 1 and <= 12))
            return new PartialDateOnly(year.Value);

        if (day is not { } value || value < 1 || value > DateTime.DaysInMonth(year.Value, month.Value))
            return new PartialDateOnly(year.Value, month);

        return new PartialDateOnly(year.Value, month, value);
    }

    /// <summary>
    /// Reads a string value, treating anything else as absent.
    /// </summary>
    /// <param name="node">The node.</param>
    /// <returns>The string, or <see langword="null"/>.</returns>
    public static string? GetString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>
    /// Reads an integer value, treating anything else as absent.
    /// </summary>
    /// <param name="node">The node.</param>
    /// <returns>The integer, or <see langword="null"/>.</returns>
    public static int? GetInt(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    private static long? GetLong(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

    private static bool? GetBool(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    private static List<string> ReadStringList(JsonNode? node)
        => node is JsonArray array
            ? [.. array.Select(GetString).Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item!.Trim()).Distinct()]
            : [];

    private static int SumScoreDistribution(JsonNode? node)
        => node is JsonArray array ? array.Sum(entry => GetInt(entry?["amount"]) ?? 0) : 0;

    #endregion
}

/// <summary>
/// The characters, people and credits read off an anime's pages, gathered
/// before they are written.
/// </summary>
public sealed class AnilistPeople
{
    /// <summary>The characters, by AniList ID.</summary>
    public Dictionary<int, MetadataCharacterData> Characters { get; } = [];

    /// <summary>The people, by AniList ID.</summary>
    public Dictionary<int, MetadataCreatorData> Creators { get; } = [];

    /// <summary>The cast credits, in AniList's order.</summary>
    public List<MetadataCastData> Cast { get; } = [];

    /// <summary>The crew credits, in AniList's order.</summary>
    public List<MetadataCrewData> Crew { get; } = [];

    /// <summary>The portraits' resource IDs, by the character or person.</summary>
    public Dictionary<MetadataGuid, string> Portraits { get; } = [];

    /// <summary>The staff credits already read, by person and AniList's wording.</summary>
    internal HashSet<(MetadataGuid CreatorID, string Role)> SeenCrew { get; } = [];
}
