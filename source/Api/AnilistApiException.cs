using System;
using System.Collections.Generic;
using System.Net;

namespace Shoko.Plugin.Anilist.Api;

/// <summary>
/// Thrown when AniList answers with something trying again will not mend: a
/// rejected query, a response that is not JSON, or a client error.
/// </summary>
/// <remarks>
/// A failure that passes, such as a server error or an exhausted rate-limit
/// budget, is an <see cref="AnilistUnavailableException"/> instead.
/// </remarks>
public class AnilistApiException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AnilistApiException"/> class.
    /// </summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="statusCode">The HTTP status of the response, when the failure was HTTP-level.</param>
    /// <param name="errors">The GraphQL error messages, when the response carried any.</param>
    /// <param name="innerException">The exception behind this one, if any.</param>
    public AnilistApiException(string message, HttpStatusCode? statusCode = null, IReadOnlyList<string>? errors = null, Exception? innerException = null)
        : base(message, innerException)
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
