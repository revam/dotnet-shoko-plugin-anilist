using Shoko.Plugin.Anilist.Mapping;
using Xunit;

namespace Shoko.Plugin.Anilist.Tests;

[Collection(nameof(ImageServerCollection))]
public class AnilistImagesTests
{
    [Theory]
    [InlineData("https://s4.anilist.co/file/anilistcdn/media/anime/cover/large/bx21-YCDoj1EkAxFn.jpg", "media/anime/cover/large/bx21-YCDoj1EkAxFn.jpg")]
    [InlineData("https://s4.anilist.co/file/anilistcdn/media/anime/banner/21-wf37VakJmZqs.jpg", "media/anime/banner/21-wf37VakJmZqs.jpg")]
    [InlineData("https://s4.anilist.co/file/anilistcdn/character/large/b40-Bn6Bmf3LLIf3.png", "character/large/b40-Bn6Bmf3LLIf3.png")]
    [InlineData("https://s4.anilist.co/file/anilistcdn/staff/large/n95269-3Yw3ycwWjc0j.jpg", "staff/large/n95269-3Yw3ycwWjc0j.jpg")]
    public void ToResourceID_StripsTheCdnBase(string url, string expected)
        => Assert.Equal(expected, AnilistImages.ToResourceID(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ToResourceID_EmptyUrl_ReturnsNull(string? url)
        => Assert.Null(AnilistImages.ToResourceID(url));

    [Fact]
    public void ToResourceID_UnknownHost_KeepsTheFullUrl()
        => Assert.Equal("https://example.org/image.png", AnilistImages.ToResourceID("https://example.org/image.png"));

    [Fact]
    public void ToResourceID_RecordsTheObservedCdnBase()
    {
        AnilistImages.ToResourceID("https://s9.anilist.co/file/anilistcdn/media/anime/cover/large/bx1-x.jpg");
        Assert.Equal("https://s9.anilist.co/file/anilistcdn/", AnilistImages.ImageServerUrl);
        Assert.Equal("https://s9.anilist.co/file/anilistcdn/{0}", AnilistImages.GetTemplateUrl(null));

        AnilistImages.ToResourceID(AnilistImages.DefaultImageServerUrl + "media/anime/cover/large/bx1-x.jpg");
        Assert.Equal(AnilistImages.DefaultImageServerUrl, AnilistImages.ImageServerUrl);
    }

    [Fact]
    public void ToImageUrl_PrefixesTheCdn_UnlessAlreadyAbsolute()
    {
        Assert.Equal(AnilistImages.DefaultImageServerUrl + "character/large/b40.png", AnilistImages.ToImageUrl("character/large/b40.png"));
        Assert.Equal("https://example.org/image.png", AnilistImages.ToImageUrl("https://example.org/image.png"));
        Assert.Null(AnilistImages.ToImageUrl(null));
    }

    [Theory]
    [InlineData("https://mirror.example/anilist/", "https://mirror.example/anilist/{0}")]
    [InlineData("https://mirror.example/anilist", "https://mirror.example/anilist/{0}")]
    [InlineData("https://mirror.example/img?path={0}", "https://mirror.example/img?path={0}")]
    public void GetTemplateUrl_TakesABaseOrATemplate(string configured, string expected)
        => Assert.Equal(expected, AnilistImages.GetTemplateUrl(configured));

    [Fact]
    public void GetTemplateUrl_IgnoresSomethingThatIsNotAUrl()
        => Assert.Equal(AnilistImages.ImageServerUrl + "{0}", AnilistImages.GetTemplateUrl("not a url"));
}

/// <summary>
/// Serialises the tests that read or move the remembered CDN base, which is
/// process-wide.
/// </summary>
[CollectionDefinition(nameof(ImageServerCollection), DisableParallelization = true)]
public class ImageServerCollection;
