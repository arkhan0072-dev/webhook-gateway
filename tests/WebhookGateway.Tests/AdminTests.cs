using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using WebhookGateway.Data;

namespace WebhookGateway.Tests;

[Collection("database")]
public class AdminTests(GatewayFactory factory) : IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        _client.DefaultRequestHeaders.Add("X-Api-Key", "test-admin-key");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<long> ReceiveShopEvent(string id)
    {
        var body = Encoding.UTF8.GetBytes($$"""{"order":"{{id}}"}""");
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/shop") { Content = new ByteArrayContent(body) };
        request.Headers.Add("X-Signature-256", SignatureTests.Hex("shop_secret", body));
        request.Headers.Add("X-Webhook-Id", id);
        (await _client.SendAsync(request)).EnsureSuccessStatusCode();
        return factory.WithDb(db => db.Deliveries.Single(d => d.Event.ExternalId == id).Id);
    }

    private void MarkDead(long deliveryId, string destination = "orders") =>
        factory.WithDb(db => db.Database.ExecuteSql(
            $"UPDATE deliveries SET status = 'Dead', attempts = 3, last_status_code = 500, destination = {destination} WHERE id = {deliveryId}"));

    private Data.Delivery Load(long id) => factory.WithDb(db => db.Deliveries.AsNoTracking().Single(d => d.Id == id));

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-key")]
    public async Task Requires_the_admin_api_key(string? key)
    {
        var client = factory.CreateClient();
        if (key is not null)
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", key);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/deliveries")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/admin/deliveries/1/replay", null)).StatusCode);
    }

    [Fact]
    public async Task Stays_locked_when_no_admin_key_is_configured()
    {
        using var noKey = factory.WithWebHostBuilder(b => b.UseSetting("Gateway:AdminApiKey", ""));
        var client = noKey.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/deliveries")).StatusCode);
    }

    [Fact]
    public async Task Lists_dead_deliveries_newest_first()
    {
        var older = await ReceiveShopEvent("a");
        var newer = await ReceiveShopEvent("b");
        await ReceiveShopEvent("still-pending");
        MarkDead(older);
        MarkDead(newer);

        var items = await _client.GetFromJsonAsync<List<DeliveryItem>>("/admin/deliveries?status=dead");

        Assert.Equal([newer, older], items!.Select(i => i.Id));
        Assert.Equal(new DeliveryItem(newer, "shop", "orders", "Dead", 3, 500, null), items![0] with { EventId = 0 });
    }

    [Fact]
    public async Task Rejects_an_unknown_status_filter()
    {
        var response = await _client.GetAsync("/admin/deliveries?status=lost");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Replays_a_dead_delivery()
    {
        var id = await ReceiveShopEvent("a");
        MarkDead(id);
        factory.Clock.Advance(TimeSpan.FromHours(2));

        var response = await _client.PostAsync($"/admin/deliveries/{id}/replay", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var replayed = Load(id);
        Assert.Equal(DeliveryStatus.Pending, replayed.Status);
        Assert.Equal(0, replayed.Attempts);
        Assert.Equal(factory.Clock.GetUtcNow(), replayed.NextAttemptAt);

        Assert.Equal(1, await factory.DeliverDueAsync());
        Assert.Equal(DeliveryStatus.Delivered, Load(id).Status);
    }

    [Fact]
    public async Task Can_resend_a_delivered_webhook()
    {
        var id = await ReceiveShopEvent("a");
        await factory.DeliverDueAsync();

        Assert.Equal(HttpStatusCode.Accepted, (await _client.PostAsync($"/admin/deliveries/{id}/replay", null)).StatusCode);
        Assert.Equal(DeliveryStatus.Pending, Load(id).Status);
    }

    [Fact]
    public async Task Does_not_replay_a_delivery_that_is_already_queued()
    {
        var id = await ReceiveShopEvent("a");

        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync($"/admin/deliveries/{id}/replay", null)).StatusCode);
    }

    [Fact]
    public async Task Answers_404_for_an_unknown_delivery()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync("/admin/deliveries/999999/replay", null)).StatusCode);
    }

    [Fact]
    public async Task Replays_every_dead_delivery_for_one_destination()
    {
        var first = await ReceiveShopEvent("a");
        var second = await ReceiveShopEvent("b");
        var other = await ReceiveShopEvent("c");
        MarkDead(first);
        MarkDead(second);
        MarkDead(other, destination: "billing");

        var response = await _client.PostAsync("/admin/destinations/orders/replay-dead", null);

        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<CountResponse>())!.Replayed);
        Assert.Equal(DeliveryStatus.Pending, Load(first).Status);
        Assert.Equal(DeliveryStatus.Pending, Load(second).Status);
        Assert.Equal(DeliveryStatus.Dead, Load(other).Status);
    }

    private record DeliveryItem(long Id, string Source, string Destination, string Status, int Attempts, int? LastStatusCode, string? LastError)
    {
        public long EventId { get; init; }
    }

    private record CountResponse(int Replayed);
}
