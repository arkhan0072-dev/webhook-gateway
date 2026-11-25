using Microsoft.Extensions.Options;

namespace WebhookGateway.Outbound;

/// <summary>
/// Runs <see cref="DeliveryProcessor"/> in a loop, idling for a second when nothing is due.
/// Off when Gateway:RunDeliveryWorker is false, e.g. for an API-only instance.
/// </summary>
public class DeliveryWorker(IServiceScopeFactory scopes, IOptions<GatewayOptions> options, TimeProvider clock, ILogger<DeliveryWorker> logger) : BackgroundService
{
    private const int BatchSize = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunDeliveryWorker)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            int sent;
            try
            {
                using var scope = scopes.CreateScope();
                sent = await scope.ServiceProvider.GetRequiredService<DeliveryProcessor>().DeliverDueAsync(BatchSize, stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                // A database blip must not stop the worker for good.
                logger.LogError(e, "Delivery pass failed; retrying shortly.");
                sent = 0;
            }

            if (sent == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken);
            }
        }
    }
}
