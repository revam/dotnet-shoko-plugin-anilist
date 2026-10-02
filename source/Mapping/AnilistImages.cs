using System;
using System.Collections.Generic;
using Shoko.Abstractions.Metadata.Enums;

namespace Shoko.Plugin.Anilist.Mapping;

/// <summary>
/// Turns AniList's absolute image URLs into the resource IDs Shoko stores,
/// and back again.
/// </summary>
/// <remarks>
/// A resource ID is the part of the URL after the CDN base, so the base can be
/// swapped for a mirror through the template URL without touching any stored
/// image. The base last seen on a URL AniList handed out is remembered, so the
/// template follows AniList if it ever moves its CDN.
/// </remarks>
public static class AnilistImages
{
    /// <summary>
    /// The CDN base every AniList image URL starts with until another is seen.
    /// </summary>
    public const string DefaultImageServerUrl = "https://s4.anilist.co/file/anilistcdn/";

    /// <summary>
    /// The longest resource ID the image table holds. One longer is dropped
    /// rather than truncated, a truncated ID being a URL that fetches nothing.
    /// </summary>
    public const int MaxResourceIDLength = 128;

    private const string CdnPathMarker = "/file/anilistcdn/";

    private static string _imageServerUrl = DefaultImageServerUrl;

    /// <summary>
    /// The CDN base seen on the most recent AniList image URL.
    /// </summary>
    public static string ImageServerUrl => _imageServerUrl;

    /// <summary>
    /// The resource ID for an absolute AniList image URL, remembering the CDN
    /// base it was served from.
    /// </summary>
    /// <param name="url">The URL, or <see langword="null"/>.</param>
    /// <returns>
    /// The path after the CDN base, the whole URL when it is not on the CDN,
    /// or <see langword="null"/> when there was no URL.
    /// </returns>
    public static string? ToResourceID(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var index = url.IndexOf(CdnPathMarker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return url;

        var baseUrl = url[..(index + CdnPathMarker.Length)];
        if (!string.Equals(baseUrl, _imageServerUrl, StringComparison.Ordinal))
            _imageServerUrl = baseUrl;

        return url[(index + CdnPathMarker.Length)..];
    }

    /// <summary>
    /// The absolute URL for a stored resource ID.
    /// </summary>
    /// <param name="resourceID">The resource ID, or <see langword="null"/>.</param>
    /// <returns>The URL, or <see langword="null"/> when there was no resource ID.</returns>
    public static string? ToImageUrl(string? resourceID)
    {
        if (string.IsNullOrWhiteSpace(resourceID))
            return null;

        // Anything not on the CDN was stored whole, so it is already absolute.
        if (resourceID.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || resourceID.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return resourceID;

        return _imageServerUrl + resourceID;
    }

    /// <summary>
    /// Whether a resource ID can be stored: present, and no longer than the
    /// image table holds.
    /// </summary>
    /// <param name="resourceID">The resource ID, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the image can be linked.</returns>
    public static bool IsStorable(string? resourceID)
        => !string.IsNullOrEmpty(resourceID) && resourceID.Length <= MaxResourceIDLength;

    /// <summary>
    /// The default images of an entry, the ones AniList names on it, for the
    /// store to pin. Only storable resource IDs are kept.
    /// </summary>
    /// <param name="images">The resource ID AniList names for each image type, or <see langword="null"/>.</param>
    /// <returns>The defaults by image type, empty when AniList names none.</returns>
    public static Dictionary<ImageEntityType, string> ToDefaultImages(params ReadOnlySpan<(ImageEntityType ImageType, string? ResourceID)> images)
    {
        var defaults = new Dictionary<ImageEntityType, string>();
        foreach (var (imageType, resourceID) in images)
        {
            if (IsStorable(resourceID))
                defaults[imageType] = resourceID!;
        }

        return defaults;
    }

    /// <summary>
    /// The template URL to register for AniList images.
    /// </summary>
    /// <param name="imageCdnUrl">
    /// The configured CDN, either a base URL or a template with <c>{0}</c>.
    /// Left empty, the CDN AniList itself hands out is used.
    /// </param>
    /// <returns>A template URL containing <c>{0}</c>.</returns>
    public static string GetTemplateUrl(string? imageCdnUrl)
    {
        if (!string.IsNullOrWhiteSpace(imageCdnUrl) && (imageCdnUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || imageCdnUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            if (imageCdnUrl.Contains("{0}", StringComparison.Ordinal))
                return imageCdnUrl;

            return imageCdnUrl.EndsWith('/') ? $"{imageCdnUrl}{{0}}" : $"{imageCdnUrl}/{{0}}";
        }

        return $"{_imageServerUrl}{{0}}";
    }
}
