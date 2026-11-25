using System.Collections.Concurrent;
using System.Net;

namespace WebhookGateway.Tests;

/// <summary>Stands in for the services webhooks are forwarded to: records requests and answers as told.</summary>
public class FakeDestinations : HttpMessageHandler
{
    public record Received(Uri Url, string? ContentType, byte[] Body, Dictionary<string, string> Headers);

    public ConcurrentQueue<Received> Requests { get; } = new();

    public Func<HttpRequestMessage, Task<HttpResponseMessage>> Respond { get; set; } = Ok;

    public static Task<HttpResponseMessage> Ok(HttpRequestMessage _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

    public void Reset()
    {
        Requests.Clear();
        Respond = Ok;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
        Requests.Enqueue(new Received(
            request.RequestUri!,
            request.Content.Headers.ContentType?.ToString(),
            body,
            request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value))));

        return await Respond(request);
    }
}
