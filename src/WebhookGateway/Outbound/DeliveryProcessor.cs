using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebhookGateway.Data;

namespace WebhookGateway.Outbound;

/// <summary>Sends due deliveries to their destinations and records the outcome.</summary>
public class DeliveryProcessor(GatewayDb db, IHttpClientFactory http, IOptions<GatewayOptions> options, TimeProvider clock)
{
    public const string HttpClientName = "destinations";

    /// <summary>
    /// Claims up to <paramref name="batchSize"/> due deliveries and sends them. Rows are locked with
    /// FOR UPDATE SKIP LOCKED, so several workers can run at once without sending anything twice.
    /// </summary>
    /// <returns>How many deliveries were attempted.</returns>
    public async Task<int> DeliverDueAsync(int batchSize, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        // shortcut: row locks are held while the batch's HTTP calls run; keep batches small, or claim rows first and send outside the transaction for slow destinations.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var due = await db.Deliveries
            .FromSql($"""
                SELECT * FROM deliveries
                WHERE status = 'Pending' AND next_attempt_at <= {now}
                ORDER BY next_attempt_at
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .Include(d => d.Event)
            .ToListAsync(ct);

        foreach (var delivery in due)
        {
            await SendAsync(delivery, now, ct);
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return due.Count;
    }

    /// <summary>Wait before the next attempt: base, 2x base, 4x base ... capped at <paramref name="max"/>.</summary>
    public static TimeSpan Backoff(int attempt, TimeSpan baseDelay, TimeSpan max) =>
        TimeSpan.FromTicks((long)Math.Min(max.Ticks, baseDelay.Ticks * Math.Pow(2, attempt - 1)));

    private async Task SendAsync(Data.Delivery delivery, DateTimeOffset now, CancellationToken ct)
    {
        var settings = options.Value;
        if (!settings.Destinations.TryGetValue(delivery.Destination, out var destination))
        {
            delivery.Status = DeliveryStatus.Dead;
            delivery.LastError = $"Destination '{delivery.Destination}' is not configured.";
            return;
        }

        delivery.Attempts++;
        var body = delivery.Event.Body;
        var request = new HttpRequestMessage(HttpMethod.Post, destination.Url) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(delivery.Event.ContentType);
        request.Headers.Add("X-Gateway-Event-Id", delivery.EventId.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("X-Gateway-Source", delivery.Event.Source);
        request.Headers.Add("X-Gateway-Attempt", delivery.Attempts.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("X-Gateway-Signature-256",
            Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(destination.Secret), body)));

        try
        {
            using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
            delivery.LastStatusCode = (int)response.StatusCode;
            delivery.LastError = null;
            if (response.IsSuccessStatusCode)
            {
                delivery.Status = DeliveryStatus.Delivered;
                delivery.DeliveredAt = now;
                return;
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // Network errors and HttpClient timeouts are worth retrying; our own shutdown is not.
            delivery.LastStatusCode = null;
            delivery.LastError = e.Message;
        }

        if (delivery.Attempts >= settings.MaxAttempts)
        {
            delivery.Status = DeliveryStatus.Dead;
        }
        else
        {
            delivery.NextAttemptAt = now + Backoff(delivery.Attempts, settings.RetryBaseDelay, settings.MaxRetryDelay);
        }
    }
}
