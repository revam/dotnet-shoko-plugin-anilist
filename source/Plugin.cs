using System;
using System.Net;
using System.Net.Http.Headers;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Plugin;
using Shoko.Plugin.Anilist.Airing;
using Shoko.Plugin.Anilist.Api;
using Shoko.Plugin.Anilist.Metadata;
using Shoko.Plugin.Anilist.Services;
using Shoko.Plugin.Anilist.Storage;

namespace Shoko.Plugin.Anilist;

/// <summary>
/// Plugin supplying AniList anime metadata through Shoko's metadata provider
/// contract.
/// </summary>
/// <remarks>
/// This class carries the plugin's identity. It is built during discovery
/// before any container exists, so it keeps a public parameterless constructor
/// and takes no dependencies; everything the plugin needs is registered in
/// <see cref="RegisterServices(IServiceCollection, IApplicationPaths)"/>.
/// </remarks>
public class Plugin : IPlugin, IPluginServiceRegistration
{
    /// <summary>
    /// The embedded resource of the plugin's thumbnail.
    /// </summary>
    internal const string ThumbnailResourceName = "Shoko.Plugin.Anilist.Assets.thumbnail.svg";

    /// <summary>
    /// The embedded resource of the plugin's icon, which is the source's icon
    /// too.
    /// </summary>
    internal const string IconResourceName = "Shoko.Plugin.Anilist.Assets.icon.svg";

    /// <summary>
    /// The plugin's ID, the same as the one in <c>manifest.json</c>.
    /// </summary>
    public static readonly Guid PluginID = new("e73c1e23-111a-4cf0-bab8-054b738c5101");

    /// <inheritdoc/>
    public Guid ID { get; private init; } = PluginID;

    /// <inheritdoc/>
    public string Name { get; private set; } = "AniList";

    /// <inheritdoc/>
    public string Description { get; private set; } = """
        Supplies AniList anime metadata for the anilist metadata source through Shoko's metadata
        provider contract: titles, overviews, images, cast and crew, studios, tags and
        genres, relations, recommendations and broadcast times.
    """;

    /// <inheritdoc/>
    public string? EmbeddedThumbnailResourceName => ThumbnailResourceName;

    /// <inheritdoc/>
    public string? EmbeddedIconResourceName => IconResourceName;

    /// <inheritdoc/>
    public static void RegisterServices(IServiceCollection serviceCollection, IApplicationPaths applicationPaths)
    {
        // Touching the class runs its static constructor, which registers the
        // source before the core closes registration after plugin setup.
        _ = MetadataSource.AniList;

        // Concrete singletons, because the plugin's own code resolves them.
        // The providers are also discovered by the server, which prefers a
        // registered instance, so registering them here is what makes the
        // server and the plugin share one object; the airing schedule service
        // checks every write against that instance.
        serviceCollection.AddPluginDbContext<Plugin, AnilistDbContext>(AnilistDbContext.DatabaseName);
        serviceCollection.AddSingleton<AnilistRateLimiter>();
        serviceCollection.AddSingleton<AnilistStore>();
        serviceCollection.AddSingleton<AnilistLinks>();
        serviceCollection.AddSingleton<AnilistImageService>();
        serviceCollection.AddSingleton<AnilistLinkingService>();
        serviceCollection.AddSingleton<AnilistSearchService>();
        serviceCollection.AddSingleton<AnilistRefreshService>();
        serviceCollection.AddSingleton<AnilistMetadataProvider>();
        serviceCollection.AddSingleton<AnilistAiringScheduleProvider>();
        serviceCollection.AddHostedService<AnilistBackgroundService>();

        serviceCollection
            .AddHttpClient<AnilistApiClient>((provider, client) =>
            {
                // The contact URL is read from the plugin's registered info
                // rather than written here, so it names wherever this build
                // was published from.
                var info = provider.GetRequiredService<IPluginManager>().GetPluginInfo<Plugin>();
                client.BaseAddress = new Uri(AnilistApiClient.GraphQLUrl);
                client.DefaultRequestHeaders.UserAgent.Add(
                    new ProductInfoHeaderValue("Shoko.Plugin.Anilist", info?.Version.Version.ToString(3) ?? typeof(Plugin).Assembly.GetName().Version?.ToString(3) ?? "1.0.0")
                );
                if (info?.RepositoryUrl is { Length: > 0 } repositoryUrl)
                    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue($"(+{repositoryUrl})"));
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
            .UseSocketsHttpHandler((handler, _) =>
            {
                // AniList compresses what it can, and the handler unpacks it
                // before the JSON parser sees it.
                handler.AutomaticDecompression = DecompressionMethods.All;
                handler.PooledConnectionLifetime = TimeSpan.FromMinutes(2);
            });
    }
}
