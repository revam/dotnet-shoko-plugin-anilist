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

        // An expression tree can't read an extension property, so take it first.
        var anilist = MetadataSource.AniList;
        harness.RefreshService.Verify(service => service.RefreshAllLinked(anilist, false, null, MetadataEntityType.Series, null, It.IsAny<CancellationToken>()), Times.Once);
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

        // An expression tree can't read an extension property, so take it first.
        var anilist = MetadataSource.AniList;
        harness.PurgeService.Verify(service => service.PurgeUnused(anilist, It.Is<DateTime?>(cutoff => cutoff < DateTime.Now.AddDays(-13) && cutoff > DateTime.Now.AddDays(-15)), MetadataEntityType.Series, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TheLibraryActions_AskTheCore()
    {
        using var harness = new ServiceHarness();
        var progress = new Progress<decimal>();

        await new UpdateAllAnilistAnimeWithImagesAction(harness.RefreshService.Object).Execute(progress, TestContext.Current.CancellationToken);
        await new SearchForAnilistMatchesAction(harness.RefreshService.Object).Execute(progress, TestContext.Current.CancellationToken);
        await new PurgeAllUnusedAnilistAnimeAction(harness.PurgeService.Object).Execute(progress, TestContext.Current.CancellationToken);
        await new PurgeAllAnilistLinksAction(harness.LinkingService.Object).Execute(progress, TestContext.Current.CancellationToken);

        // An expression tree can't read an extension property, so take it first.
        var anilist = MetadataSource.AniList;
        harness.RefreshService.Verify(service => service.RefreshAllLinked(anilist, true, It.Is<MetadataRefreshOptions?>(options => options!.DownloadImages && options.Reason == MetadataRefreshReason.Requested), MetadataEntityType.Series, progress, It.IsAny<CancellationToken>()), Times.Once);
        harness.RefreshService.Verify(service => service.AutoSearchAll(anilist, false, progress, It.IsAny<CancellationToken>()), Times.Once);
        harness.PurgeService.Verify(service => service.PurgeUnused(anilist, null, MetadataEntityType.Series, progress, It.IsAny<CancellationToken>()), Times.Once);
        harness.LinkingService.Verify(service => service.RemoveAllLinks(anilist, true, true, false, progress, It.IsAny<CancellationToken>()), Times.Once);
        harness.LinkingService.Verify(service => service.ResetAutoLinkingState(It.IsAny<MetadataSource>(), It.IsAny<bool>()), Times.Never);
    }
}
