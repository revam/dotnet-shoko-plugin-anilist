using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Shoko.Abstractions.Config.Attributes;
using Xunit;

namespace Shoko.Plugin.Anilist.Tests;

public class AnilistConfigurationTests
{
    [Fact]
    public void TheDefaults_MatchTheCoresSettings()
    {
        var configuration = new AnilistConfiguration();

        Assert.False(configuration.ConsiderExistingOtherLinks);
        Assert.Equal(AnilistRecommendationDepth.WhileWellRated, configuration.RecommendationDepth);
        Assert.Equal(5, configuration.AutoSearchCandidateCount);
        Assert.Equal(14, configuration.AutoPurgeUnlinkedAfterDays);
        Assert.Null(configuration.ImageCdnUrl);
    }

    [Theory]
    [InlineData(typeof(AnilistConfiguration), nameof(AnilistConfiguration.ImageCdnUrl), "ANILIST_IMAGE_CDN_URL")]
    [InlineData(typeof(AnilistRateLimitConfiguration), nameof(AnilistRateLimitConfiguration.MaxRequestsPerWindow), "ANILIST_RATE_LIMIT_MAX_REQUESTS_PER_WINDOW")]
    [InlineData(typeof(AnilistRateLimitConfiguration), nameof(AnilistRateLimitConfiguration.WindowDurationMs), "ANILIST_RATE_LIMIT_WINDOW_DURATION_MS")]
    public void TheEnvironmentVariableOverrides_AreKept(Type type, string property, string variable)
        => Assert.Equal(variable, type.GetProperty(property)!.GetCustomAttribute<EnvironmentVariableAttribute>()?.Name);

    [Fact]
    public void TheDownloadSwitches_AreTheCoresNow()
    {
        Assert.Null(typeof(AnilistConfiguration).GetProperty("AutoDownloadPosters"));
        Assert.Null(typeof(AnilistConfiguration).GetProperty("AutoDownloadBanners"));
        Assert.Null(typeof(AnilistConfiguration).GetProperty("AutoDownloadCharacters"));
        Assert.Null(typeof(AnilistConfiguration).GetProperty("AutoDownloadStaff"));
        Assert.Null(typeof(AnilistConfiguration).GetProperty("AutoDownloadStudios"));
    }

    [Fact]
    public void NothingIsMarkedRequired()
        => Assert.DoesNotContain(
            typeof(AnilistConfiguration).GetProperties().Concat(typeof(AnilistRateLimitConfiguration).GetProperties()),
            property => property.GetCustomAttribute<RequiredAttribute>() is not null
        );
}
