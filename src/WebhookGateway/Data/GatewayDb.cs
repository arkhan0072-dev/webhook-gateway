using Microsoft.EntityFrameworkCore;

namespace WebhookGateway.Data;

public class GatewayDb(DbContextOptions<GatewayDb> options) : DbContext(options)
{
    public DbSet<WebhookEvent> Events => Set<WebhookEvent>();

    public DbSet<Delivery> Deliveries => Set<Delivery>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<WebhookEvent>(e =>
        {
            e.ToTable("webhook_events");
            // A sender retrying the same event must not create a second copy.
            e.HasIndex(x => new { x.Source, x.ExternalId }).IsUnique();
        });

        model.Entity<Delivery>(d =>
        {
            d.ToTable("deliveries");
            d.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            // The worker's query: due, pending deliveries in order.
            d.HasIndex(x => new { x.Status, x.NextAttemptAt });
        });
    }
}

public class WebhookEvent
{
    public long Id { get; set; }

    public required string Source { get; set; }

    /// <summary>The sender's own id for the event, used to drop duplicates.</summary>
    public required string ExternalId { get; set; }

    /// <summary>The request body exactly as received, so destinations can verify it byte for byte.</summary>
    public required byte[] Body { get; set; }

    public required string ContentType { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    public List<Delivery> Deliveries { get; set; } = [];
}

public enum DeliveryStatus
{
    Pending,
    Delivered,
    Dead,
}

public class Delivery
{
    public long Id { get; set; }

    public long EventId { get; set; }

    public WebhookEvent Event { get; set; } = null!;

    /// <summary>Destination name from configuration; its URL and secret are looked up at send time.</summary>
    public required string Destination { get; set; }

    public DeliveryStatus Status { get; set; } = DeliveryStatus.Pending;

    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    public DateTimeOffset? DeliveredAt { get; set; }

    public int? LastStatusCode { get; set; }

    public string? LastError { get; set; }
}
