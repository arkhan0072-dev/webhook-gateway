using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using WebhookGateway.Data;
using WebhookGateway.Signatures;

namespace WebhookGateway.Ingest;

/// <summary>POST /webhooks/{source}: verify, store, queue deliveries, answer fast.</summary>
public static class IngestEndpoint
{
    public const int MaxBodyBytes = 1_048_576;

    public static async Task<IResult> Handle(
        string source, HttpRequest request, GatewayDb db, IOptions<GatewayOptions> options, TimeProvider clock, CancellationToken ct)
    {
        if (!options.Value.Sources.TryGetValue(source, out var config))
        {
            return Results.NotFound();
        }

        var body = await ReadBody(request, ct);
        if (body is null)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var now = clock.GetUtcNow();
        var verified = config.Scheme switch
        {
            SignatureScheme.Stripe => SignatureVerifier.Stripe(Header(request, "Stripe-Signature"), body, config.Secret, now),
            SignatureScheme.GitHub => SignatureVerifier.GitHub(Header(request, "X-Hub-Signature-256"), body, config.Secret),
            _ => SignatureVerifier.Hmac(Header(request, "X-Signature-256"), body, config.Secret),
        };
        if (!verified)
        {
            return Results.Unauthorized();
        }

        var externalId = ExternalId(config.Scheme, request, body);
        if (externalId is null)
        {
            return Results.BadRequest(new { error = "The event has no id to deduplicate it by." });
        }

        var webhook = new WebhookEvent
        {
            Source = source,
            ExternalId = externalId,
            Body = body,
            ContentType = request.ContentType ?? "application/octet-stream",
            ReceivedAt = now,
            Deliveries = [.. config.Destinations.Select(name => new Delivery { Destination = name, NextAttemptAt = now })],
        };
        db.Events.Add(webhook);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Senders retry when they miss our answer; the first copy is already queued.
            return Results.Ok(new { duplicate = true });
        }

        return Results.Accepted(value: new { id = webhook.Id });
    }

    /// <summary>Reads the body, or returns null once it passes the size limit.</summary>
    private static async Task<byte[]?> ReadBody(HttpRequest request, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxBodyBytes)
            {
                return null;
            }
        }

        return buffer.ToArray();
    }

    private static string? ExternalId(SignatureScheme scheme, HttpRequest request, byte[] body) => scheme switch
    {
        SignatureScheme.Stripe => StripeEventId(body),
        SignatureScheme.GitHub => Header(request, "X-GitHub-Delivery"),
        _ => Header(request, "X-Webhook-Id") ?? "sha256:" + Convert.ToHexStringLower(SHA256.HashData(body)),
    };

    private static string? StripeEventId(byte[] body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Header(HttpRequest request, string name) =>
        request.Headers.TryGetValue(name, out var value) ? value.ToString() : null;
}
