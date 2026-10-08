using Moq;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Config.Services;
using Shoko.Abstractions.Connectivity.Suspensions;
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

/// <summary>
/// Keeps the suspensions reported to it as the core would, without their
/// ends running out, so a test can read back what the plugin reported.
/// </summary>
internal sealed class FakeSuspensionReporter<TProvider> : ISuspensionReporter<TProvider>
    where TProvider : ISuspensionProvider
{
    private readonly Dictionary<SuspensionKind, Suspension> _active = [];

    public IReadOnlyDictionary<SuspensionKind, Suspension> Active => _active;

    public SuspensionStatus Current => new()
    {
        Provider = null!,
        Suspensions = [.. _active.Values],
        IsSuspended = _active.Count > 0,
        ResumesAt = _active.Count > 0 && _active.Values.All(suspension => suspension.ResumesAt is not null) ? _active.Values.Max(suspension => suspension.ResumesAt) : null,
    };

    public void Suspend(SuspensionKind kind, string? reason = null, DateTime? resumesAt = null, bool isLiftable = false)
        => _active[kind] = new() { Kind = kind, Reason = reason, RaisedAt = DateTime.UtcNow, ResumesAt = resumesAt, IsLiftable = isLiftable };

    public void Resume(SuspensionKind kind) => _active.Remove(kind);

    public void ResumeAll() => _active.Clear();
}
