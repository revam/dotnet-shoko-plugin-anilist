using System.Runtime.CompilerServices;
using Moq;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Airing;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Providers;
using Shoko.Plugin.Anilist.Airing;
using Shoko.Plugin.Anilist.Mapping;
using Shoko.Plugin.Anilist.Metadata;
using Xunit;

namespace Shoko.Plugin.Anilist.Tests;

public class AnilistAiringScheduleProviderTests
{
    private static readonly Guid _providerID = Guid.NewGuid();

    private static readonly AnilistMedia _media = new() { ID = 21, OriginalLanguageCode = "ja", EpisodeCount = 2, Status = ReleaseStatus.Finished };

    private static (ServiceHarness Harness, AnilistAiringScheduleProvider Provider, Mock<IAiringSchedule> Schedule) Registered()
    {
        var harness = new ServiceHarness();
        var provider = harness.Get<AnilistAiringScheduleProvider>();
        var info = (AiringScheduleProviderInfo)RuntimeHelpers.GetUninitializedObject(typeof(AiringScheduleProviderInfo));
        typeof(AiringScheduleProviderInfo).GetProperty(nameof(AiringScheduleProviderInfo.ID))!.SetValue(info, _providerID);
        var schedule = new Mock<IAiringSchedule>();
        harness.AiringScheduleService.Setup(service => service.GetProviderInfo(provider)).Returns(info);
        harness.AiringScheduleService.Setup(service => service.GetSchedulesForProvider(_providerID, It.IsAny<AiringScheduleFilteringOptions?>())).Returns([]);
        harness.AiringScheduleService.Setup(service => service.AddOrUpdateSchedule(provider, It.IsAny<AiringScheduleData>())).Returns(schedule.Object);
        AnilistStoreTests.StoreAnime(harness.Stores, 21, episodes: 2);
        return (harness, provider, schedule);
    }

    [Fact]
    public void WriteSchedule_SubmitsOneChannelLessOriginalSchedule()
    {
        var (harness, provider, schedule) = Registered();
        using var _ = harness;
        var airedAt = new DateTime(2019, 4, 6, 15, 0, 0, DateTimeKind.Utc);

        provider.WriteSchedule(harness.Stores.Store.GetSeries(21)!, _media, new Dictionary<int, AnilistScheduleEntry> { [1] = new(1001, airedAt), [5] = new(1005, airedAt) });

        harness.AiringScheduleService.Verify(service => service.AddOrUpdateSchedule(provider, It.Is<AiringScheduleData>(data =>
            data.Key == AnilistAiringScheduleProvider.ScheduleKey
            && data.ChannelID == null
            && data.Series.ID == AnilistUtility.SeriesGuid(21)
            && data.Tracks.Single() == new AiringTrackData(AiringKind.Original, "ja", null)
            && data.FirstEpisodeNumber == 1
            && data.LastEpisodeNumber == 2
            && data.IsFinished)), Times.Once);
        harness.AiringScheduleService.Verify(service => service.SetAirings(provider, schedule.Object, It.Is<IEnumerable<EpisodeAiringData>>(airings => airings.Single().AiredAt == airedAt && airings.Single().SequenceNumber == 1 && airings.Single().Episode == null), null), Times.Once);
    }

    [Fact]
    public void WriteSchedule_ClearsARunPastTheRetentionWindow()
    {
        var (harness, provider, schedule) = Registered();
        using var _ = harness;
        harness.AiringScheduleService
            .Setup(service => service.SetAirings(provider, schedule.Object, It.Is<IEnumerable<EpisodeAiringData>>(airings => airings.Any()), null))
            .Throws(new AiringScheduleValidationException("Too old.", new Dictionary<string, IReadOnlyList<string>> { ["#schedule"] = ["Too old."] }));

        provider.WriteSchedule(harness.Stores.Store.GetSeries(21)!, _media, new Dictionary<int, AnilistScheduleEntry> { [1] = new(1001, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)) });

        harness.AiringScheduleService.Verify(service => service.SetAirings(provider, schedule.Object, It.Is<IEnumerable<EpisodeAiringData>>(airings => !airings.Any()), null), Times.Once);
    }

    [Fact]
    public void WriteSchedule_DoesNothing_WhileTheProviderIsNotRegistered()
    {
        using var harness = new ServiceHarness();
        AnilistStoreTests.StoreAnime(harness.Stores, 21);

        harness.Get<AnilistAiringScheduleProvider>().WriteSchedule(harness.Stores.Store.GetSeries(21)!, _media, new Dictionary<int, AnilistScheduleEntry> { [1] = new(1, DateTime.UtcNow) });

        harness.AiringScheduleService.Verify(service => service.AddOrUpdateSchedule(It.IsAny<IAiringScheduleProvider>(), It.IsAny<AiringScheduleData>()), Times.Never);
    }

    [Fact]
    public void WriteSchedule_WritesNothing_ForAnAnimeWithNoTimesAndNoScheduleYet()
    {
        var (harness, provider, _) = Registered();
        using var _ = harness;

        provider.WriteSchedule(harness.Stores.Store.GetSeries(21)!, _media, new Dictionary<int, AnilistScheduleEntry>());

        harness.AiringScheduleService.Verify(service => service.AddOrUpdateSchedule(It.IsAny<IAiringScheduleProvider>(), It.IsAny<AiringScheduleData>()), Times.Never);
    }

    [Fact]
    public async Task RefreshAsync_AsksTheCoreToRefreshTheLinkedAnime()
    {
        var (harness, provider, _) = Registered();
        using var _ = harness;
        var (series, _, _) = harness.AddShokoSeries(1, 100, 1);
        harness.Stores.CrossReferences.AddSeries(100, 21);
        harness.RefreshService.Setup(service => service.IsRefreshing(AnilistUtility.SeriesGuid(22))).Returns(true);

        Assert.True(await provider.RefreshAsync(series.Object, TestContext.Current.CancellationToken));
        Assert.True(await provider.RefreshAsync(harness.Stores.Store.GetSeries(21)!, TestContext.Current.CancellationToken));
        harness.Stores.CrossReferences.AddSeries(100, 22);
        Assert.True(await provider.RefreshAsync(series.Object, TestContext.Current.CancellationToken));

        harness.RefreshService.Verify(service => service.RefreshEntry(AnilistUtility.SeriesGuid(21), false, It.Is<MetadataRefreshOptions?>(options => options != null && !options.DownloadImages), false, false, It.IsAny<CancellationToken>()), Times.Exactly(3));
        harness.RefreshService.Verify(service => service.RefreshEntry(AnilistUtility.SeriesGuid(22), It.IsAny<bool>(), It.IsAny<MetadataRefreshOptions?>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RefreshAsync_AnswersFalse_ForASeriesWithNothingLinked()
    {
        var (harness, provider, _) = Registered();
        using var _ = harness;
        var (series, _, _) = harness.AddShokoSeries(1, 100, 1);

        Assert.False(await provider.RefreshAsync(series.Object, TestContext.Current.CancellationToken));
        Assert.Equal([AiringKind.Original], provider.AvailableKinds);
    }

    [Fact]
    public void GetAnilistAnimeIDs_ReadsAnAnilistSeriesByItsOwnID()
    {
        var (harness, provider, _) = Registered();
        using var _ = harness;
        var other = new Mock<ISeries>();
        other.SetupGet(s => s.Source).Returns(MetadataSource.AniList);
        other.SetupGet(s => s.ID).Returns(AnilistUtility.SeriesGuid(77));

        Assert.Equal([77], provider.GetAnilistAnimeIDs(other.Object));
    }

    [Fact]
    public void RemoveSchedules_TakesOnlyTheAnimesOwn()
    {
        var (harness, provider, _) = Registered();
        using var _ = harness;
        var own = Mock.Of<IAiringSchedule>(schedule => schedule.SeriesID == AnilistUtility.SeriesGuid(21));
        var other = Mock.Of<IAiringSchedule>(schedule => schedule.SeriesID == AnilistUtility.SeriesGuid(22));
        harness.AiringScheduleService.Setup(service => service.GetSchedulesForProvider(_providerID, It.IsAny<AiringScheduleFilteringOptions?>())).Returns([own, other]);
        harness.AiringScheduleService.Setup(service => service.RemoveSchedule(provider, own)).Returns(true);

        Assert.Equal(1, provider.RemoveSchedules(AnilistUtility.SeriesGuid(21)));
        harness.AiringScheduleService.Verify(service => service.RemoveSchedule(provider, other), Times.Never);
    }
}
