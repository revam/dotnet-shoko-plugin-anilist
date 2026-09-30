using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shoko.Abstractions.Core.Services;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Airing;
using Shoko.Abstractions.Metadata.Anidb;
using Shoko.Abstractions.Metadata.CrossReferences;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Matching;
using Shoko.Abstractions.Metadata.Providers;
using Shoko.Abstractions.Metadata.Search;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Metadata.Shoko;
using Shoko.Abstractions.Metadata.Storage;
using Shoko.Plugin.Anilist.Airing;
using Shoko.Plugin.Anilist.Api;
using Shoko.Plugin.Anilist.Metadata;
using Shoko.Plugin.Anilist.Services;
using Shoko.Plugin.Anilist.Storage;

namespace Shoko.Plugin.Anilist.Tests;

/// <summary>
/// The plugin's services wired together over fakes of the core's stores and
/// mocks of its services, with a stub HTTP handler in place of AniList.
/// </summary>
internal sealed class ServiceHarness : IDisposable
{
    private readonly ServiceProvider _services;

    public ServiceHarness(AnilistConfiguration? configuration = null)
    {
        Stores = new TestHarness(configuration ?? new AnilistConfiguration { RateLimit = { MaxRequestsPerWindow = 90, WindowDurationMs = 1000 } });
        RateLimiter = new AnilistRateLimiter(NullLogger<AnilistRateLimiter>.Instance, Stores.ConfigurationProvider);

        // The core's linking service records the link and queues nothing;
        // the mock does the recording.
        LinkingService
            .Setup(service => service.AddSeriesLink(It.IsAny<MetadataSeriesLinkRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (MetadataSeriesLinkRequest request, CancellationToken token) =>
            {
                await Stores.CrossReferences.MergeSeriesLinks([new() { Source = request.Source, AnidbAnimeID = request.AnidbAnimeID, ProviderID = request.ProviderID, MatchRating = request.MatchRating }], options: new() { ReplaceExisting = !request.Additive }, cancellationToken: token);
                return true;
            });
        LinkingService
            .Setup(service => service.RemoveSeriesLink(It.IsAny<MetadataSeriesLinkRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (MetadataSeriesLinkRequest request, CancellationToken token) =>
            {
                var removals = Stores.CrossReferences.GetSeriesLinks(request.AnidbAnimeID, request.Source).Where(link => link.ProviderID == request.ProviderID).ToList();
                await Stores.CrossReferences.MergeSeriesLinks([], removals, cancellationToken: token);
                return removals.Count > 0;
            });
        LinkingService
            .Setup(service => service.MatchEpisodes(It.IsAny<int>(), It.IsAny<MetadataGuid>(), It.IsAny<MetadataGuid?>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        ProviderManager.Setup(manager => manager.GetProviderInfo(It.IsAny<Type>())).Throws(new ArgumentException("Not registered."));
        AiringScheduleService.Setup(service => service.GetProviderInfo(It.IsAny<IAiringScheduleProvider>())).Throws(new ArgumentException("Not registered."));
        ImageManager.Setup(manager => manager.GetImagesForEntity(It.IsAny<Shoko.Abstractions.Metadata.Containers.IWithImages>(), It.IsAny<Shoko.Abstractions.Metadata.Image.Options.ImageFilteringOptions?>())).Returns([]);
        SystemService.SetupGet(service => service.IsStarted).Returns(true);

        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton(Stores.ConfigurationProvider);
        collection.AddSingleton(RateLimiter);
        collection.AddSingleton(Stores.Store);
        collection.AddSingleton(Stores.Links);
        collection.AddSingleton<IMetadataSeriesStore>(Stores.Series);
        collection.AddSingleton<IMetadataCrossReferenceStore>(Stores.CrossReferences);
        collection.AddSingleton(LinkingService.Object);
        collection.AddSingleton(MatchingEngine.Object);
        collection.AddSingleton(MetadataService.Object);
        collection.AddSingleton(ProviderManager.Object);
        collection.AddSingleton(ImageManager.Object);
        collection.AddSingleton(AiringScheduleService.Object);
        collection.AddSingleton(RefreshService.Object);
        collection.AddSingleton(PurgeService.Object);
        collection.AddSingleton(SystemService.Object);
        collection.AddSingleton(new AnilistApiClient(new HttpClient(Http) { BaseAddress = new Uri(AnilistApiClient.GraphQLUrl) }, RateLimiter, NullLogger<AnilistApiClient>.Instance));
        collection.AddSingleton<AnilistImageService>();
        collection.AddSingleton<AnilistLinkingService>();
        collection.AddSingleton<AnilistSearchService>();
        collection.AddSingleton<AnilistRefreshService>();
        collection.AddSingleton<AnilistMetadataProvider>();
        collection.AddSingleton<AnilistAiringScheduleProvider>();
        collection.AddSingleton<AnilistBackgroundService>();
        _services = collection.BuildServiceProvider();
    }

    public TestHarness Stores { get; }

    public AnilistRateLimiter RateLimiter { get; }

    public StubHttpMessageHandler Http { get; } = new();

    public Mock<IMetadataLinkingService> LinkingService { get; } = new();

    public Mock<IMetadataMatchingEngine> MatchingEngine { get; } = new();

    public Mock<IMetadataService> MetadataService { get; } = new();

    public Mock<IMetadataProviderManager> ProviderManager { get; } = new();

    public Mock<IImageManager> ImageManager { get; } = new();

    public Mock<IAiringScheduleService> AiringScheduleService { get; } = new();

    public Mock<IMetadataRefreshService> RefreshService { get; } = new();

    public Mock<IMetadataPurgeService> PurgeService { get; } = new();

    public Mock<ISystemService> SystemService { get; } = new();

    public T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    /// <summary>
    /// Queues AniList's answer to one request.
    /// </summary>
    public ServiceHarness Respond(string body, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        Http.Enqueue(statusCode, body);
        return this;
    }

    /// <summary>
    /// Adds a Shoko series over an AniDB anime with some normal episodes.
    /// </summary>
    public (Mock<IShokoSeries> Series, Mock<IAnidbAnime> Anime, List<IAnidbEpisode> Episodes) AddShokoSeries(int seriesID, int anidbAnimeID, int episodeCount, DateOnly? firstAired = null, bool restricted = false)
    {
        var anime = new Mock<IAnidbAnime>();
        var episodes = new List<IAnidbEpisode>();
        var shokoEpisodes = new List<IShokoEpisode>();
        var series = new Mock<IShokoSeries>();
        var shokoSeriesID = new MetadataGuid(MetadataSource.Shoko, MetadataEntityType.Series, seriesID.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var anidbID = new MetadataGuid(MetadataSource.AniDB, MetadataEntityType.Series, anidbAnimeID.ToString(System.Globalization.CultureInfo.InvariantCulture));
        for (var number = 1; number <= episodeCount; number++)
        {
            var anidbEpisodeID = (anidbAnimeID * 1000) + number;
            var episode = new Mock<IAnidbEpisode>();
            episode.SetupGet(e => e.ID).Returns(new MetadataGuid(MetadataSource.AniDB, MetadataEntityType.Episode, anidbEpisodeID.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            episode.SetupGet(e => e.AnidbID).Returns(anidbEpisodeID);
            episode.SetupGet(e => e.AnidbAnimeID).Returns(anidbAnimeID);
            episode.SetupGet(e => e.SeriesID).Returns(anidbID);
            episode.SetupGet(e => e.Type).Returns(EpisodeType.Episode);
            episode.SetupGet(e => e.EpisodeNumber).Returns(number);
            episode.SetupGet(e => e.AirDate).Returns(firstAired?.AddDays(7 * (number - 1)));
            episodes.Add(episode.Object);

            var shokoEpisode = new Mock<IShokoEpisode>();
            shokoEpisode.SetupGet(e => e.ID).Returns(new MetadataGuid(MetadataSource.Shoko, MetadataEntityType.Episode, anidbEpisodeID.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            shokoEpisode.SetupGet(e => e.LocalID).Returns(anidbEpisodeID);
            shokoEpisode.SetupGet(e => e.AnidbEpisodeID).Returns(anidbEpisodeID);
            shokoEpisode.SetupGet(e => e.SeriesID).Returns(shokoSeriesID);
            shokoEpisode.SetupGet(e => e.AnidbEpisode).Returns(episode.Object);
            shokoEpisode.SetupGet(e => e.Series).Returns(series.Object);
            shokoEpisodes.Add(shokoEpisode.Object);
            MetadataService.Setup(service => service.GetShokoEpisodeByAnidbID(anidbEpisodeID)).Returns(shokoEpisode.Object);
            MetadataService.Setup(service => service.GetShokoEpisodeByID(anidbEpisodeID)).Returns(shokoEpisode.Object);
        }

        anime.SetupGet(a => a.ID).Returns(anidbID);
        anime.SetupGet(a => a.AnidbID).Returns(anidbAnimeID);
        anime.SetupGet(a => a.Type).Returns(AnimeType.TV);
        anime.SetupGet(a => a.Restricted).Returns(restricted);
        anime.SetupGet(a => a.Episodes).Returns(episodes);
        anime.SetupGet(a => a.EpisodeCounts).Returns(new EpisodeCounts { Episodes = episodeCount });
        anime.SetupGet(a => a.AirDate).Returns(firstAired is { } date ? new PartialDateOnly(date) : null);
        anime.SetupGet(a => a.RelatedSeries).Returns([]);
        anime.SetupGet(a => a.Titles).Returns([]);
        series.SetupGet(s => s.ID).Returns(shokoSeriesID);
        series.SetupGet(s => s.LocalID).Returns(seriesID);
        series.SetupGet(s => s.AnidbAnimeID).Returns(anidbAnimeID);
        series.SetupGet(s => s.AnidbAnime).Returns(anime.Object);
        series.SetupGet(s => s.Episodes).Returns(shokoEpisodes);
        MetadataService.Setup(service => service.GetShokoSeriesByAnidbID(anidbAnimeID)).Returns(series.Object);
        MetadataService.Setup(service => service.GetShokoSeriesByID(seriesID)).Returns(series.Object);
        MetadataService.Setup(service => service.GetAllShokoSeries()).Returns(() => [series.Object]);
        MetadataService.Setup(service => service.GetSeries(anidbID)).Returns(anime.Object);
        return (series, anime, episodes);
    }

    /// <summary>
    /// Makes the matching engine pair the AniDB episodes with the provider's
    /// by position, recording what it was handed.
    /// </summary>
    public List<(IReadOnlyList<IAnidbEpisode> Anidb, IReadOnlyList<IEpisode> Provider, EpisodeMatchOptions? Options)> MatchByPosition()
    {
        var calls = new List<(IReadOnlyList<IAnidbEpisode>, IReadOnlyList<IEpisode>, EpisodeMatchOptions?)>();
        MatchingEngine
            .Setup(engine => engine.MatchEpisodes(It.IsAny<IReadOnlyList<IAnidbEpisode>>(), It.IsAny<IReadOnlyList<IEpisode>>(), It.IsAny<IReadOnlyList<IMetadataEpisodeCrossReference>?>(), It.IsAny<EpisodeMatchOptions?>()))
            .Returns((IReadOnlyList<IAnidbEpisode> anidb, IReadOnlyList<IEpisode> provider, IReadOnlyList<IMetadataEpisodeCrossReference>? _, EpisodeMatchOptions? options) =>
            {
                calls.Add((anidb, provider, options));
                return [.. anidb.Select((episode, index) => new EpisodeMatch
                {
                    AnidbEpisode = episode,
                    Candidate = index < provider.Count ? provider[index] : null,
                    Rating = index < provider.Count ? MatchRating.DateMatches : MatchRating.None,
                })];
            });
        return calls;
    }

    /// <summary>
    /// Makes the matching engine judge series by a rating the test gives each
    /// candidate, the best one taken as the engine would, recording what it
    /// was handed.
    /// </summary>
    public List<(IReadOnlyList<MetadataSeriesSearchResult> Candidates, SeriesMatchOptions? Options)> JudgeSeries(Func<MetadataSeriesSearchResult, SeriesMatchOptions?, MatchRating> rate)
    {
        var calls = new List<(IReadOnlyList<MetadataSeriesSearchResult>, SeriesMatchOptions?)>();
        MatchingEngine
            .Setup(engine => engine.MatchSeries(It.IsAny<IAnidbAnime>(), It.IsAny<IReadOnlyList<MetadataSeriesSearchResult>>(), It.IsAny<SeriesMatchOptions?>()))
            .Returns((IAnidbAnime anime, IReadOnlyList<MetadataSeriesSearchResult> candidates, SeriesMatchOptions? options) =>
            {
                calls.Add((candidates, options));
                var ranked = candidates
                    .Select(candidate => (Candidate: candidate, Rating: rate(candidate, options)))
                    .OrderBy(pair => AnilistSearchService.MatchPriority(pair.Rating))
                    .ToList();
                return [.. ranked.Select((pair, index) => new SeriesMatch
                {
                    AnidbAnime = anime,
                    Candidate = pair.Candidate,
                    Rating = pair.Rating,
                    Rejection = index is 0 && pair.Rating is not MatchRating.None ? MatchRejectionReason.None : MatchRejectionReason.TitleMismatch,
                })];
            });
        return calls;
    }

    public void Dispose()
    {
        _services.Dispose();
        RateLimiter.Dispose();
        Stores.Dispose();
    }
}
