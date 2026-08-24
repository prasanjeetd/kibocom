using InventoryHold.Domain.Entities;

namespace InventoryHold.Domain.Repositories;

public interface IHoldRepository
{
    /// <summary>
    /// Atomically deducts stock for every item and inserts the hold, or changes nothing at all.
    /// Each deduction carries its quantity precondition in the filter, so oversell is impossible;
    /// the surrounding transaction prevents a partially-deducted cart when one line fails.
    /// Throws <c>InsufficientStockException</c> or <c>UnknownSkuException</c>.
    /// </summary>
    Task CreateWithStockDeductionAsync(Hold hold, CancellationToken cancellationToken = default);

    Task<Hold?> GetAsync(Guid holdId, CancellationToken cancellationToken = default);

    /// <summary>Holds still marked Active in storage, newest first. Callers apply StatusAt(now).</summary>
    Task<IReadOnlyList<Hold>> GetActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims the hold (Active -> Released) and restores its stock in one transaction.
    /// Returns null when the claim matched nothing, which means a concurrent release or the
    /// expiry sweeper won the race. Stock is therefore restored exactly once.
    /// </summary>
    Task<Hold?> ReleaseAndRestoreStockAsync(
        Guid holdId, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims up to <paramref name="maxBatch"/> holds whose deadline has passed
    /// (Active -> Expired) and restores their stock. Safe to run on many replicas at once.
    /// </summary>
    Task<IReadOnlyList<Hold>> ExpireDueAndRestoreStockAsync(
        DateTimeOffset now, int maxBatch, CancellationToken cancellationToken = default);
}
