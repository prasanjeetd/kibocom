using InventoryHold.Domain.Events;
using InventoryHold.Domain.Repositories;
using InventoryHold.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace InventoryHold.WebApi.HostedServices;

/// <summary>
/// Returns stock from holds whose deadline has passed.
///
/// Lazy expiry on the read path keeps answers honest, but a hold nobody ever reads would keep its
/// stock forever. This sweeper is what actually gives it back, and it is what publishes
/// HoldExpired. Each hold is claimed with a compare-and-swap, so running several API replicas
/// cannot expire the same hold twice.
/// </summary>
public sealed class ExpirySweeper(
    IHoldRepository holds,
    IEventPublisher events,
    ICacheService cache,
    TimeProvider clock,
    IOptions<HoldOptions> options,
    ILogger<ExpirySweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.SweeperIntervalSeconds));
        logger.LogInformation("Expiry sweeper started; interval {Interval}", interval);

        using var timer = new PeriodicTimer(interval, clock);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed sweep must never kill the loop: the next tick retries.
                logger.LogError(ex, "Expiry sweep failed; will retry on the next tick");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Expiry sweeper stopped");
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        var expired = await holds.ExpireDueAndRestoreStockAsync(
            now, options.Value.SweeperBatchSize, cancellationToken);

        if (expired.Count == 0) return;

        // Stock changed, so the cached inventory snapshot is stale.
        await cache.RemoveAsync(CacheKeys.AllInventory, cancellationToken);

        foreach (var hold in expired)
        {
            await events.PublishAsync(HoldEvent.HoldExpired(hold, now), cancellationToken);
        }

        logger.LogInformation("Expired {Count} hold(s) and restored their stock", expired.Count);
    }
}
