using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebhookGateway.Data;

namespace WebhookGateway.Admin;

/// <summary>Operator endpoints under /admin, protected by the X-Api-Key header.</summary>
public static class AdminEndpoints
{
    public static void MapAdmin(this WebApplication app)
    {
        var admin = app.MapGroup("/admin").AddEndpointFilter(RequireApiKey);

        admin.MapGet("/deliveries", ListDeliveries);
        admin.MapPost("/deliveries/{id:long}/replay", Replay);
        admin.MapPost("/destinations/{destination}/replay-dead", ReplayDead);
    }

    /// <summary>Constant-time key check. With no key configured the admin API stays closed.</summary>
    private static async ValueTask<object?> RequireApiKey(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var expected = context.HttpContext.RequestServices.GetRequiredService<IOptions<GatewayOptions>>().Value.AdminApiKey;
        var given = context.HttpContext.Request.Headers["X-Api-Key"].ToString();

        var valid = expected.Length > 0
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(given));

        return valid ? await next(context) : Results.Unauthorized();
    }

    private static async Task<IResult> ListDeliveries(GatewayDb db, string? status, int? limit, CancellationToken ct)
    {
        var filter = DeliveryStatus.Dead;
        if (status is not null && !Enum.TryParse(status, ignoreCase: true, out filter))
        {
            return Results.BadRequest(new { error = "status must be pending, delivered or dead." });
        }

        var items = await db.Deliveries
            .Where(d => d.Status == filter)
            .OrderByDescending(d => d.Id)
            .Take(Math.Clamp(limit ?? 50, 1, 500))
            .Select(d => new
            {
                d.Id,
                d.EventId,
                d.Event.Source,
                d.Destination,
                Status = d.Status.ToString(),
                d.Attempts,
                d.LastStatusCode,
                d.LastError,
                d.NextAttemptAt,
                d.DeliveredAt,
            })
            .ToListAsync(ct);

        return Results.Ok(items);
    }

    /// <summary>Queues one delivery to be sent again now. Works for dead and delivered ones.</summary>
    private static async Task<IResult> Replay(long id, GatewayDb db, TimeProvider clock, CancellationToken ct)
    {
        var delivery = await db.Deliveries.FindAsync([id], ct);
        if (delivery is null)
        {
            return Results.NotFound();
        }
        if (delivery.Status == DeliveryStatus.Pending)
        {
            return Results.Conflict(new { error = "This delivery is already queued." });
        }

        Requeue(delivery, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return Results.Accepted();
    }

    /// <summary>After a destination outage: queue every dead delivery for it again.</summary>
    private static async Task<IResult> ReplayDead(string destination, GatewayDb db, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var replayed = await db.Deliveries
            .Where(d => d.Destination == destination && d.Status == DeliveryStatus.Dead)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, DeliveryStatus.Pending)
                .SetProperty(d => d.Attempts, 0)
                .SetProperty(d => d.NextAttemptAt, now), ct);

        return Results.Ok(new { replayed });
    }

    private static void Requeue(Data.Delivery delivery, DateTimeOffset now)
    {
        delivery.Status = DeliveryStatus.Pending;
        delivery.Attempts = 0;
        delivery.NextAttemptAt = now;
    }
}
