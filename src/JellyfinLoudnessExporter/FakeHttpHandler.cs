using System.Net;
using System.Text;

namespace JellyfinLoudnessExporter;

/// <summary>Offline fake transport for API client selftests — never opens a real network socket.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage>? Responder { get; set; }
    public TimeSpan? ArtificialDelay { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (ArtificialDelay is { } d)
        {
            await Task.Delay(d, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (Responder is null)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        return Responder(request);
    }

    public static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
}
