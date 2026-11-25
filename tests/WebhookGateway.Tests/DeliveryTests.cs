using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebhookGateway.Data;
using WebhookGateway.Outbound;

namespace WebhookGateway.Tests;

[Collection("database")]
public class DeliveryTests(GatewayFactory factory) : IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Sends a signed webhook from the "shop" source, which forwards to the "orders" destination.</summary>
    private async Task ReceiveShopEvent(string json = """{"order":42}""", string id = "order-42")
    {
        var body = Encoding.UTF8.GetBytes(json);
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/shop") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("X-Signature-256", SignatureTests.Hex("shop_secret", body));
        request.Headers.Add("X-Webhook-Id", id);
        (await _client.SendAsync(request)).EnsureSuccessStatusCode();
    }

    private Data.Delivery TheDelivery() => factory.WithDb(db => db.Deliveries.AsNoTracking().Single());

    private void Fail(HttpStatusCode status) =>
        factory.Destinations.Respond = _ => Task.FromResult(new HttpResponseMessage(status));

    [Fact]
    public async Task Forwards_the_original_body_with_the_gateway_signature()
    {
        await ReceiveShopEvent("""{"order":42, "total": 99.5}""");

        Assert.Equal(1, await factory.DeliverDueAsync());

        var sent = Assert.Single(factory.Destinations.Requests);
        var body = Encoding.UTF8.GetBytes("""{"order":42, "total": 99.5}""");
        Assert.Equal(new Uri("https://orders.internal.test/hooks"), sent.Url);
        Assert.Equal(body, sent.Body);
        Assert.Equal("application/json", sent.ContentType);
        Assert.Equal(SignatureTests.Hex("orders_signing_secret", body), sent.Headers["X-Gateway-Signature-256"]);
        Assert.Equal("shop", sent.Headers["X-Gateway-Source"]);
        Assert.Equal("1", sent.Headers["X-Gateway-Attempt"]);
        Assert.Equal(factory.WithDb(db => db.Events.Single().Id.ToString()), sent.Headers["X-Gateway-Event-Id"]);

        var delivery = TheDelivery();
        Assert.Equal(DeliveryStatus.Delivered, delivery.Status);
        Assert.Equal(factory.Clock.GetUtcNow(), delivery.DeliveredAt);
        Assert.Equal(200, delivery.LastStatusCode);
        Assert.Equal(1, delivery.Attempts);
    }

    [Fact]
    public async Task Sends_each_destination_its_own_copy()
    {
        var body = Encoding.UTF8.GetBytes("""{"id":"evt_7"}""");
        var timestamp = factory.Clock.GetUtcNow().ToUnixTimeSeconds();
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/stripe") { Content = new ByteArrayContent(body) };
        request.Headers.Add("Stripe-Signature", $"t={timestamp},v1={SignatureTests.Hex("whsec_stripe", [.. Encoding.UTF8.GetBytes($"{timestamp}."), .. body])}");
        await _client.SendAsync(request);

        Assert.Equal(2, await factory.DeliverDueAsync());

        Assert.Equal(["analytics.internal.test", "billing.internal.test"], factory.Destinations.Requests.Select(r => r.Url.Host).Order());
    }

    [Fact]
    public async Task Retries_a_failing_destination_with_exponential_backoff()
    {
        await ReceiveShopEvent();
        Fail(HttpStatusCode.ServiceUnavailable);
        var start = factory.Clock.GetUtcNow();

        await factory.DeliverDueAsync();
        var first = TheDelivery();
        Assert.Equal(DeliveryStatus.Pending, first.Status);
        Assert.Equal(1, first.Attempts);
        Assert.Equal(503, first.LastStatusCode);
        Assert.Equal(start.AddSeconds(30), first.NextAttemptAt);

        Assert.Equal(0, await factory.DeliverDueAsync());

        factory.Clock.Advance(TimeSpan.FromSeconds(30));
        await factory.DeliverDueAsync();
        Assert.Equal(start.AddSeconds(30 + 60), TheDelivery().NextAttemptAt);
        Assert.Equal("2", factory.Destinations.Requests.Last().Headers["X-Gateway-Attempt"]);
    }

    [Fact]
    public async Task Gives_up_after_the_maximum_attempts()
    {
        await ReceiveShopEvent();
        Fail(HttpStatusCode.InternalServerError);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await factory.DeliverDueAsync();
            factory.Clock.Advance(TimeSpan.FromHours(1));
        }

        var delivery = TheDelivery();
        Assert.Equal(DeliveryStatus.Dead, delivery.Status);
        Assert.Equal(3, delivery.Attempts);
        Assert.Equal(0, await factory.DeliverDueAsync());
    }

    [Fact]
    public async Task Counts_a_network_error_as_a_failed_attempt()
    {
        await ReceiveShopEvent();
        factory.Destinations.Respond = _ => throw new HttpRequestException("Connection refused");

        await factory.DeliverDueAsync();

        var delivery = TheDelivery();
        Assert.Equal(DeliveryStatus.Pending, delivery.Status);
        Assert.Null(delivery.LastStatusCode);
        Assert.Equal("Connection refused", delivery.LastError);
    }

    [Fact]
    public async Task Counts_a_timeout_as_a_failed_attempt()
    {
        await ReceiveShopEvent();
        factory.Destinations.Respond = _ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout");

        await factory.DeliverDueAsync();

        Assert.StartsWith("The request was canceled", TheDelivery().LastError);
    }

    [Fact]
    public async Task Dead_letters_a_delivery_whose_destination_was_removed_from_config()
    {
        await ReceiveShopEvent();
        factory.WithDb(db => db.Database.ExecuteSqlRaw("UPDATE deliveries SET destination = 'retired'"));

        await factory.DeliverDueAsync();

        var delivery = TheDelivery();
        Assert.Equal(DeliveryStatus.Dead, delivery.Status);
        Assert.Equal("Destination 'retired' is not configured.", delivery.LastError);
        Assert.Empty(factory.Destinations.Requests);
    }

    [Fact]
    public async Task Two_workers_never_send_the_same_delivery_twice()
    {
        await ReceiveShopEvent();
        factory.Destinations.Respond = async _ =>
        {
            await Task.Delay(300); // keep the first worker's row lock held while the second one looks
            return new HttpResponseMessage(HttpStatusCode.OK);
        };

        var results = await Task.WhenAll(factory.DeliverDueAsync(), factory.DeliverDueAsync());

        Assert.Equal(1, results.Sum());
        Assert.Single(factory.Destinations.Requests);
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(20, 600)]
    public void Backoff_doubles_up_to_the_cap(int attempt, int expectedSeconds) =>
        Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            DeliveryProcessor.Backoff(attempt, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10)));

    [Fact]
    public async Task The_background_worker_delivers_without_being_asked() =>
        await RunWorkerAndDeliver(factory.WithWebHostBuilder(b => b.UseSetting("Gateway:RunDeliveryWorker", "true")));

    [Fact]
    public async Task The_background_worker_survives_a_failed_pass()
    {
        var passes = 0;
        await RunWorkerAndDeliver(factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Gateway:RunDeliveryWorker", "true");
            // The first pass fails as if the database were briefly unreachable.
            b.ConfigureServices(services => services.AddScoped(sp => Interlocked.Increment(ref passes) == 1
                ? throw new InvalidOperationException("database unavailable")
                : ActivatorUtilities.CreateInstance<DeliveryProcessor>(sp)));
        }));

        Assert.True(passes > 1);
    }

    private async Task RunWorkerAndDeliver(WebApplicationFactory<Program> withWorker)
    {
        using var app = withWorker;
        var client = app.CreateClient();
        var body = Encoding.UTF8.GetBytes("{}");
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/shop") { Content = new ByteArrayContent(body) };
        request.Headers.Add("X-Signature-256", SignatureTests.Hex("shop_secret", body));
        await client.SendAsync(request);

        for (var i = 0; i < 50 && factory.Destinations.Requests.IsEmpty; i++)
        {
            factory.Clock.Advance(TimeSpan.FromSeconds(1)); // wakes the worker from its idle wait
            await Task.Delay(100);
        }

        Assert.Single(factory.Destinations.Requests);
    }
}
