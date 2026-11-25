using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using WebhookGateway.Data;

namespace WebhookGateway.Tests;

/// <summary>Runs the real app against the test PostgreSQL database, with a controllable clock.</summary>
public class GatewayFactory : WebApplicationFactory<Program>
{
    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("TEST_DATABASE_URL")
        ?? "Host=localhost;Port=54330;Database=gateway_test;Username=gateway;Password=gateway";

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", ConnectionString);
        builder.UseSetting("Gateway:AdminApiKey", "test-admin-key");
        builder.UseSetting("Gateway:RunDeliveryWorker", "false");

        builder.UseSetting("Gateway:Sources:stripe:Scheme", "Stripe");
        builder.UseSetting("Gateway:Sources:stripe:Secret", "whsec_stripe");
        builder.UseSetting("Gateway:Sources:stripe:Destinations:0", "billing");
        builder.UseSetting("Gateway:Sources:stripe:Destinations:1", "analytics");

        builder.UseSetting("Gateway:Sources:github:Scheme", "GitHub");
        builder.UseSetting("Gateway:Sources:github:Secret", "gh_secret");
        builder.UseSetting("Gateway:Sources:github:Destinations:0", "ci");

        builder.UseSetting("Gateway:Sources:shop:Scheme", "Hmac");
        builder.UseSetting("Gateway:Sources:shop:Secret", "shop_secret");
        builder.UseSetting("Gateway:Sources:shop:Destinations:0", "orders");

        foreach (var name in new[] { "billing", "analytics", "ci", "orders" })
        {
            builder.UseSetting($"Gateway:Destinations:{name}:Url", $"https://{name}.internal.test/hooks");
            builder.UseSetting($"Gateway:Destinations:{name}:Secret", $"{name}_signing_secret");
        }

        builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(Clock));
    }

    /// <summary>Empties the tables so each test starts from a clean database.</summary>
    public async Task ResetDatabaseAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDb>();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE deliveries, webhook_events RESTART IDENTITY CASCADE");
    }

    public T WithDb<T>(Func<GatewayDb, T> query)
    {
        using var scope = Services.CreateScope();
        return query(scope.ServiceProvider.GetRequiredService<GatewayDb>());
    }
}

[CollectionDefinition("database")]
public class DatabaseCollection : ICollectionFixture<GatewayFactory>;
