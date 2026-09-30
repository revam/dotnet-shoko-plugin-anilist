using System;
using System.Collections.Generic;
using System.Net;
using Shoko.Abstractions.Metadata.Providers;
using Shoko.Plugin.Anilist.Metadata;

namespace Shoko.Plugin.Anilist.Api;

/// <summary>
/// Thrown when AniList cannot be reached for now: it answered with a server
/// error, timed out, kept limiting the rate, or could not be reached at all.
/// </summary>
/// <remarks>
/// A <see cref="MetadataProviderUnavailableException"/>, so the core answers
/// a request that talked to AniList with <c>502 Bad Gateway</c> and a
/// <c>Retry-After</c> header, and a caller backs off.
/// </remarks>
public sealed class AnilistUnavailableException : MetadataProviderUnavailableException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AnilistUnavailableException"/> class.
    /// </summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="retryAfter">How long to wait before trying again, if known.</param>
    /// <param name="statusCode">The HTTP status of the response, when the failure was HTTP-level.</param>
    /// <param name="errors">The GraphQL error messages, when the response carried any.</param>
    /// <param name="innerException">The exception behind this one, if any.</param>
    public AnilistUnavailableException(string message, TimeSpan? retryAfter = null, HttpStatusCode? statusCode = null, IReadOnlyList<string>? errors = null, Exception? innerException = null)
        : base(AnilistSources.AniList, message, retryAfter, innerException)
    {
        StatusCode = statusCode;
        Errors = errors ?? [];
    }

    /// <summary>
    /// The HTTP status of the response, when the failure was HTTP-level.
    /// </summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>
    /// The GraphQL error messages, when the response carried any.
    /// </summary>
    public IReadOnlyList<string> Errors { get; }
}
