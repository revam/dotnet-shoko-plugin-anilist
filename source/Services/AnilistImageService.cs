using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Config.Events;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Enums;
using Shoko.Abstractions.Metadata.Image;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Plugin.Anilist.Mapping;
using Shoko.Plugin.Anilist.Metadata;
using Shoko.Plugin.Anilist.Storage;

namespace Shoko.Plugin.Anilist.Services;

/// <summary>
/// Hands the core AniList's images and keeps its default template URL
/// registered.
/// </summary>
/// <remarks>
/// AniList has one image per type per entity: a cover and a banner for an
/// anime, a portrait for a character or a person. The core decides which of
/// them to download from the admin's image settings for the source.
/// </remarks>
public sealed class AnilistImageService : IDisposable
{
    private readonly IImageManager _imageManager;

    private readonly AnilistStore _store;

    private readonly ConfigurationProvider<AnilistConfiguration> _configurationProvider;

    private readonly ILogger<AnilistImageService> _logger;

    private string? _registeredTemplate;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnilistImageService"/> class.
    /// </summary>
    /// <param name="imageManager">The core's image manager.</param>
    /// <param name="store">The plugin's store, which knows where the images are.</param>
    /// <param name="configurationProvider">The plugin's configuration, read for the CDN URL.</param>
    /// <param name="logger">The logger.</param>
    public AnilistImageService(IImageManager imageManager, AnilistStore store, ConfigurationProvider<AnilistConfiguration> configurationProvider, ILogger<AnilistImageService> logger)
    {
        _imageManager = imageManager;
        _store = store;
        _configurationProvider = configurationProvider;
        _logger = logger;
        _configurationProvider.Saved += OnConfigurationSaved;
    }

    #region Template

    /// <summary>
    /// Registers AniList's default template URL with the core, the configured
    /// CDN when there is one and AniList's own otherwise.
    /// </summary>
    /// <remarks>
    /// The core keeps a registration in memory only and lets the user set their
    /// own over it, which the plugin never touches. It is registered again
    /// whenever the default it would register changes, such as when AniList is
    /// seen serving its images from another CDN.
    /// </remarks>
    public void RegisterTemplateUrl()
    {
        var template = AnilistImages.GetTemplateUrl(_configurationProvider.Load().ImageCdnUrl);
        if (string.Equals(template, _registeredTemplate, StringComparison.Ordinal))
            return;

        _imageManager.RegisterTemplateUrl(AnilistSources.AniList, template);
        _registeredTemplate = template;
    }

    private void OnConfigurationSaved(object? sender, ConfigurationSavedEventArgs<AnilistConfiguration> eventArgs)
    {
        try
        {
            RegisterTemplateUrl();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to register the AniList image template URL.");
        }
    }

    #endregion

    #region Images

    /// <summary>
    /// The images AniList has for one of its entities.
    /// </summary>
    /// <param name="entityID">The entity.</param>
    /// <returns>
    /// The cover and banner of an anime, the portrait of a character or a
    /// person, or <see langword="null"/> for anything the plugin keeps no
    /// images for, which leaves its linked images alone.
    /// </returns>
    public IReadOnlyList<ImageCandidate>? GetImages(MetadataGuid entityID)
    {
        ArgumentNullException.ThrowIfNull(entityID);

        if (entityID.Source != AnilistSources.AniList)
            return null;

        RegisterTemplateUrl();
        if (AnilistUtility.TryGetID(entityID, MetadataEntityType.Series, out var anilistAnimeID))
        {
            if (_store.GetAnime(anilistAnimeID) is not { } anime)
                return null;

            List<ImageCandidate> candidates = [];
            if (Candidate(anime.CoverImagePath, ImageEntityType.Primary) is { } cover)
                candidates.Add(cover);
            if (Candidate(anime.BannerImagePath, ImageEntityType.Banner) is { } banner)
                candidates.Add(banner);
            return candidates;
        }

        if (entityID.EntityType == MetadataEntityType.Creator || entityID.EntityType == MetadataEntityType.Character)
            return Candidate(_store.GetPortrait(entityID), ImageEntityType.Primary) is { } portrait ? [portrait] : [];

        return null;
    }

    private ImageCandidate? Candidate(string? resourceID, ImageEntityType imageType)
    {
        if (string.IsNullOrEmpty(resourceID))
            return null;

        // One longer than the image table holds is dropped rather than
        // truncated, a truncated ID being a URL that fetches nothing.
        if (resourceID.Length > AnilistImages.MaxResourceIDLength)
        {
            _logger.LogDebug("Skipping an AniList {ImageType} image whose resource ID is too long to store. (ResourceID={ResourceID})", imageType, resourceID);
            return null;
        }

        return new() { ResourceID = resourceID, ImageType = imageType, IsDefault = true };
    }

    #endregion

    /// <summary>
    /// Stops listening for configuration changes.
    /// </summary>
    public void Dispose()
        => _configurationProvider.Saved -= OnConfigurationSaved;
}
