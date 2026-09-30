using Moq;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Config.Services;
using Shoko.Plugin.Anilist.Services;
using Shoko.Plugin.Anilist.Storage;

namespace Shoko.Plugin.Anilist.Tests;

/// <summary>
/// Builds the pieces most tests need, over in-memory fakes of the core's
/// stores and the plugin's own database in memory.
/// </summary>
internal sealed class TestHarness : IDisposable
{
    public TestHarness(AnilistConfiguration? configuration = null)
    {
        Configuration = configuration ?? new AnilistConfiguration();
        ConfigurationProvider = CreateConfigurationProvider(Configuration);
        Series = new FakeSeriesStore(Tags, Studios, People);
        Store = new AnilistStore(Database, Series, People, Tags, Studios, Relations, Suggestions);
        Links = new AnilistLinks(CrossReferences);
    }

    public AnilistConfiguration Configuration { get; }

    public ConfigurationProvider<AnilistConfiguration> ConfigurationProvider { get; }

    public TestDatabase Database { get; } = new();

    public FakeSeriesStore Series { get; }

    public FakePeopleStore People { get; } = new();

    public FakeTagStore Tags { get; } = new();

    public FakeStudioStore Studios { get; } = new();

    public FakeRelationStore Relations { get; } = new();

    public FakeSuggestionStore Suggestions { get; } = new();

    public FakeCrossReferenceStore CrossReferences { get; } = new();

    public AnilistStore Store { get; }

    public AnilistLinks Links { get; }

    public void Dispose()
        => Database.Dispose();

    /// <summary>
    /// A configuration provider that hands back one configuration instance.
    /// </summary>
    public static ConfigurationProvider<AnilistConfiguration> CreateConfigurationProvider(AnilistConfiguration configuration)
    {
        var service = new Mock<IConfigurationService>();
        service.Setup(s => s.GetConfigurationInfo<AnilistConfiguration>()).Returns((ConfigurationInfo)null!);
        service.Setup(s => s.Load(It.IsAny<ConfigurationInfo>(), It.IsAny<bool>())).Returns(configuration);
        service.Setup(s => s.Load<AnilistConfiguration>(It.IsAny<bool>())).Returns(configuration);
        return new ConfigurationProvider<AnilistConfiguration>(service.Object);
    }
}
