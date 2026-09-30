using Moq;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Providers;
using Shoko.Plugin.Anilist.Actions;
using Shoko.Plugin.Anilist.Mapping;
using Shoko.Plugin.Anilist.Metadata;
using Shoko.Plugin.Anilist.Services;
using Shoko.Plugin.Anilist.Storage;
using Xunit;

namespace Shoko.Plugin.Anilist.Tests;

/// <summary>
/// What the plugin asks the core for on its own schedule, and what its
/// actions ask the core for.
/// </summary>
public class AnilistBackgroundServiceTests
{
    [Fact]
    public async Task RefreshIfNothingStored_RefreshesEveryLinkedAnime_Once()
    {
        using var harness = new ServiceHarness();
        harness.Stores.CrossReferences.AddSeries(100, 21);
        var background = harness.Get<AnilistBackgroundService>();

        await background.RefreshIfNothingStored(TestContext.Current.CancellationToken);
        AnilistStoreTests.StoreAnime(harness.Stores, 21);
        await background.RefreshIfNothingStored(TestContext.Current.CancellationToken);

        harness.RefreshService.Verify(service => service.RefreshAllLinked(AnilistSources.AniList, false, null, MetadataEntityType.Series, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RefreshIfNothingStored_DoesNothing_WithNothingLinked()
    {
        using var harness = new ServiceHarness();
        harness.Stores.CrossReferences.AddEpisode(100, 100001, 0);

        Assert.Equal(0, await harness.Get<AnilistBackgroundService>().RefreshIfNothingStored(TestContext.Current.CancellationToken));

        harness.RefreshService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PurgeUnused_GoesByTheSetting()
    {
        using var harness = new ServiceHarness();
        var background = harness.Get<AnilistBackgroundService>();

        await background.PurgeUnused(TestContext.Current.CancellationToken);
        harness.Stores.Configuration.AutoPurgeUnlinkedAfterDays = 0;
        await background.PurgeUnused(TestContext.Current.CancellationToken);

        harness.PurgeService.Verify(service => service.PurgeUnused(AnilistSources.AniList, It.Is<DateTime?>(cutoff => cutoff < DateTime.Now.AddDays(-13) && cutoff > DateTime.Now.AddDays(-15)), MetadataEntityType.Series, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TheLibraryActions_AskTheCore()
    {
        using var harness = new ServiceHarness();

        await new UpdateAllAnilistAnimeWithImagesAction(harness.RefreshService.Object).Execute(new Progress<decimal>(), TestContext.Current.CancellationToken);
        await new SearchForAnilistMatchesAction(harness.RefreshService.Object).Execute(new Progress<decimal>(), TestContext.Current.CancellationToken);
        await new PurgeAllUnusedAnilistAnimeAction(harness.PurgeService.Object).Execute(new Progress<decimal>(), TestContext.Current.CancellationToken);
        await new PurgeAllAnilistLinksAction(harness.LinkingService.Object).Execute(new Progress<decimal>(), TestContext.Current.CancellationToken);

        harness.RefreshService.Verify(service => service.RefreshAllLinked(AnilistSources.AniList, true, It.Is<MetadataRefreshOptions?>(options => options!.DownloadImages && options.Reason == MetadataRefreshReason.Requested), MetadataEntityType.Series, It.IsAny<CancellationToken>()), Times.Once);
        harness.RefreshService.Verify(service => service.AutoSearchAll(AnilistSources.AniList, false, It.IsAny<CancellationToken>()), Times.Once);
        harness.PurgeService.Verify(service => service.PurgeUnused(AnilistSources.AniList, null, MetadataEntityType.Series, It.IsAny<CancellationToken>()), Times.Once);
        harness.LinkingService.Verify(service => service.RemoveAllLinks(AnilistSources.AniList, true, true, false, It.IsAny<CancellationToken>()), Times.Once);
        harness.LinkingService.Verify(service => service.ResetAutoLinkingState(It.IsAny<MetadataSource>(), It.IsAny<bool>()), Times.Never);
    }
}
