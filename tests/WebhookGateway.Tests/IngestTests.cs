using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WebhookGateway.Data;

namespace WebhookGateway.Tests;

[Collection("database")]
public class IngestTests(GatewayFactory factory) : IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private Task<HttpResponseMessage> PostStripe(string json, string? signature = null)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var timestamp = factory.Clock.GetUtcNow().ToUnixTimeSeconds();
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/stripe") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("Stripe-Signature", signature
            ?? $"t={timestamp},v1={SignatureTests.Hex("whsec_stripe", [.. Encoding.UTF8.GetBytes($"{timestamp}."), .. body])}");
        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> PostSigned(string source, string secret, byte[] body, string header, string prefix = "", params (string Name, string Value)[] extra)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/webhooks/{source}") { Content = new ByteArrayContent(body) };
        request.Headers.Add(header, prefix + SignatureTests.Hex(secret, body));
        foreach (var (name, value) in extra)
        {
            request.Headers.Add(name, value);
        }
        return _client.SendAsync(request);
    }

    [Fact]
    public async Task Stores_a_verified_event_and_queues_one_delivery_per_destination()
    {
        var response = await PostStripe("""{"id":"evt_100","type":"invoice.paid"}""");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var stored = factory.WithDb(db => db.Events.Include(e => e.Deliveries).Single());
        Assert.Equal("stripe", stored.Source);
        Assert.Equal("evt_100", stored.ExternalId);
        Assert.Equal("application/json", stored.ContentType);
        Assert.Equal(factory.Clock.GetUtcNow(), stored.ReceivedAt);
        Assert.Equal(["analytics", "billing"], stored.Deliveries.Select(d => d.Destination).Order());
        Assert.All(stored.Deliveries, d =>
        {
            Assert.Equal(DeliveryStatus.Pending, d.Status);
            Assert.Equal(factory.Clock.GetUtcNow(), d.NextAttemptAt);
        });
        Assert.Equal(stored.Id, (await response.Content.ReadFromJsonAsync<IdResponse>())!.Id);
    }

    [Fact]
    public async Task Keeps_the_body_byte_for_byte()
    {
        const string json = "{ \"id\" : \"evt_101\",\n  \"amount\": 1050 }";

        await PostStripe(json);

        Assert.Equal(Encoding.UTF8.GetBytes(json), factory.WithDb(db => db.Events.Single().Body));
    }

    [Fact]
    public async Task Rejects_a_forged_signature_and_stores_nothing()
    {
        var response = await PostStripe("""{"id":"evt_102"}""", signature: $"t={factory.Clock.GetUtcNow().ToUnixTimeSeconds()},v1={new string('0', 64)}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, factory.WithDb(db => db.Events.Count()));
    }

    [Fact]
    public async Task Answers_404_for_an_unknown_source()
    {
        var response = await _client.PostAsync("/webhooks/nobody", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Accepts_a_redelivered_event_once()
    {
        await PostStripe("""{"id":"evt_103"}""");
        var again = await PostStripe("""{"id":"evt_103"}""");

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.True((await again.Content.ReadFromJsonAsync<DuplicateResponse>())!.Duplicate);
        Assert.Equal(1, factory.WithDb(db => db.Events.Count()));
        Assert.Equal(2, factory.WithDb(db => db.Deliveries.Count()));
    }

    [Fact]
    public async Task Rejects_a_Stripe_event_without_an_id()
    {
        var response = await PostStripe("""{"type":"invoice.paid"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_a_Stripe_body_that_is_not_json()
    {
        var response = await PostStripe("not json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Uses_the_GitHub_delivery_id_to_spot_duplicates()
    {
        var body = Encoding.UTF8.GetBytes("""{"action":"opened"}""");

        var first = await PostSigned("github", "gh_secret", body, "X-Hub-Signature-256", "sha256=", ("X-GitHub-Delivery", "72d3162e"));
        var second = await PostSigned("github", "gh_secret", body, "X-Hub-Signature-256", "sha256=", ("X-GitHub-Delivery", "72d3162e"));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("72d3162e", factory.WithDb(db => db.Events.Single().ExternalId));
    }

    [Fact]
    public async Task Requires_a_GitHub_delivery_id()
    {
        var body = Encoding.UTF8.GetBytes("{}");

        var response = await PostSigned("github", "gh_secret", body, "X-Hub-Signature-256", "sha256=");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Generic_sources_use_their_webhook_id_or_else_the_body_hash()
    {
        var withId = await PostSigned("shop", "shop_secret", Encoding.UTF8.GetBytes("a"), "X-Signature-256", "", ("X-Webhook-Id", "order-9"));
        var withoutId = await PostSigned("shop", "shop_secret", Encoding.UTF8.GetBytes("b"), "X-Signature-256");

        Assert.Equal(HttpStatusCode.Accepted, withId.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, withoutId.StatusCode);
        var ids = factory.WithDb(db => db.Events.OrderBy(e => e.Id).Select(e => e.ExternalId).ToList());
        Assert.Equal("order-9", ids[0]);
        Assert.Equal("sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("b"u8)), ids[1]);
    }

    [Fact]
    public async Task Refuses_bodies_over_one_megabyte()
    {
        var body = new byte[1_048_577];

        var response = await PostSigned("shop", "shop_secret", body, "X-Signature-256");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, factory.WithDb(db => db.Events.Count()));
    }

    [Fact]
    public async Task Reports_health()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private record IdResponse(long Id);

    private record DuplicateResponse(bool Duplicate);
}
