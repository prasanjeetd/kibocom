using InventoryHold.Contracts;
using InventoryHold.Domain.Entities;
using InventoryHold.Domain.Events;
using InventoryHold.Domain.Exceptions;
using InventoryHold.Domain.Repositories;

namespace InventoryHold.Domain.Services;

/// <summary>
/// Orchestrates the hold lifecycle. Business rules live on the entities; this type coordinates
/// the ports and keeps the ordering right: mutate storage, invalidate cache, then publish.
/// </summary>
public sealed class HoldService(
    IHoldRepository holds,
    IInventoryRepository inventory,
    IEventPublisher events,
    ICacheService cache,
    TimeProvider clock,
    HoldPolicy policy)
{
    public async Task<HoldResponse> CreateAsync(
        CreateHoldRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = clock.GetUtcNow();

        // Resolve names and reject unknown SKUs up front so the caller gets a precise 422.
        // This is a convenience, not the safety mechanism: the atomic deduction below is.
        var items = new List<HoldItem>(request.Items.Count);
        foreach (var line in request.Items)
        {
            var product = await inventory.GetBySkuAsync(line.Sku, cancellationToken)
                          ?? throw new UnknownSkuException(line.Sku);
            items.Add(new HoldItem(product.Sku, line.Quantity, product.Name));
        }

        var hold = Hold.Create(request.CustomerId, items, now, policy.TimeToLive);

        // Throws InsufficientStockException if any line loses its race.
        await holds.CreateWithStockDeductionAsync(hold, cancellationToken);

        await cache.RemoveAsync(CacheKeys.AllInventory, cancellationToken);
        await events.PublishAsync(HoldEvent.HoldCreated(hold, now), cancellationToken);

        return hold.ToResponse(now);
    }

    public async Task<HoldResponse> GetAsync(Guid holdId, CancellationToken cancellationToken = default)
    {
        var hold = await holds.GetAsync(holdId, cancellationToken)
                   ?? throw new HoldNotFoundException(holdId);

        // StatusAt applies lazy expiry, so a hold past its deadline never reads as Active.
        return hold.ToResponse(clock.GetUtcNow());
    }

    public async Task<IReadOnlyList<HoldResponse>> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var active = await holds.GetActiveAsync(cancellationToken);
        return [.. active.Select(h => h.ToResponse(now))];
    }

    public async Task<HoldResponse> ReleaseAsync(Guid holdId, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();

        var existing = await holds.GetAsync(holdId, cancellationToken)
                       ?? throw new HoldNotFoundException(holdId);

        // Fast fail with a precise message. The compare-and-swap below is the real guard.
        existing.EnsureReleasableAt(now);

        var released = await holds.ReleaseAndRestoreStockAsync(holdId, now, cancellationToken);
        if (released is null)
        {
            // The claim matched nothing: the sweeper expired this hold between our read and our
            // write. Stock has already been restored exactly once - do not restore it again.
            var current = await holds.GetAsync(holdId, cancellationToken);
            throw new HoldNotActiveException(holdId, current?.StatusAt(now) ?? HoldStatus.Expired);
        }

        await cache.RemoveAsync(CacheKeys.AllInventory, cancellationToken);
        await events.PublishAsync(HoldEvent.HoldReleased(released, now), cancellationToken);

        return released.ToResponse(now);
    }
}
