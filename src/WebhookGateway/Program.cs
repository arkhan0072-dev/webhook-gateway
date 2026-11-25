using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebhookGateway;
using WebhookGateway.Data;
using WebhookGateway.Ingest;
using WebhookGateway.Outbound;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection("Gateway"));
builder.Services.AddDbContext<GatewayDb>(options => options
    .UseNpgsql(builder.Configuration.GetConnectionString("Default"))
    .UseSnakeCaseNamingConvention());
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddHttpClient(DeliveryProcessor.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddScoped<DeliveryProcessor>();
builder.Services.AddHostedService<DeliveryWorker>();

var app = builder.Build();

// shortcut: migrating on startup suits a single instance; run migrations as a deploy step when several instances start together.
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<GatewayDb>().Database.Migrate();
}

app.MapPost("/webhooks/{source}", IngestEndpoint.Handle);
app.MapGet("/health", async (GatewayDb db) => await db.Database.CanConnectAsync() ? Results.Ok() : Results.StatusCode(503));

app.Run();

/// <summary>Visible to the integration tests' WebApplicationFactory.</summary>
public partial class Program;
