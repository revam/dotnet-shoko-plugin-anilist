using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Shoko.Plugin.Anilist.Tests;

/// <summary>
/// Reads the fixtures in <c>tests/Fixtures</c>.
/// </summary>
internal static class Fixture
{
    public static string Read(string fileName)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName));
}

/// <summary>
/// An <see cref="HttpMessageHandler"/> that replays a queue of responses and
/// records the request bodies, so the client runs without the network.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode StatusCode, string Body, TimeSpan? RetryAfter)> _responses = new();

    public List<string> Bodies { get; } = [];

    public StubHttpMessageHandler Enqueue(HttpStatusCode statusCode, string body, TimeSpan? retryAfter = null)
    {
        _responses.Enqueue((statusCode, body, retryAfter));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
        if (!_responses.TryDequeue(out var response))
            throw new InvalidOperationException($"No stubbed response left for {request.RequestUri}.");

        var message = new HttpResponseMessage(response.StatusCode) { Content = new StringContent(response.Body, Encoding.UTF8, "application/json") };
        if (response.RetryAfter is { } retryAfter)
            message.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);

        return message;
    }
}
