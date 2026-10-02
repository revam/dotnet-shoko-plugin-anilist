using Shoko.Abstractions.Metadata;

namespace Shoko.Plugin.Anilist.Metadata;

/// <summary>
/// The metadata source the plugin owns, registered once.
/// </summary>
/// <remarks>
/// The static constructor registers the source, and
/// <see cref="Plugin.RegisterServices(Microsoft.Extensions.DependencyInjection.IServiceCollection, Shoko.Abstractions.Plugin.IApplicationPaths)"/>
/// touches the class so that it runs before the core closes registration after
/// plugin setup. The old <c>DataSource</c> enum spelled it <c>AniList</c>,
/// which the value already matches ignoring case, so it needs no alias.
/// </remarks>
public static class AnilistSources
{
    static AnilistSources()
    {
        Source = MetadataSource.Register("AniList", "anilist", description: "The anime and manga database at anilist.co.");
    }

    /// <summary>
    /// The registered source, behind the extension member.
    /// </summary>
    private static MetadataSource Source { get; }

    extension(MetadataSource)
    {
        /// <summary>
        /// AniList, as <c>anilist</c>.
        /// </summary>
        public static MetadataSource AniList => Source;
    }
}
