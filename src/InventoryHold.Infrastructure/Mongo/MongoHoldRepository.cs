using System.Diagnostics;
using InventoryHold.Contracts;
using InventoryHold.Domain.Entities;
using InventoryHold.Domain.Exceptions;
using InventoryHold.Domain.Repositories;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace InventoryHold.Infrastructure.Mongo;

/// <summary>
/// All stock movement lives here. Every mutation carries its precondition in the filter, so the
/// check and the write are a single indivisible operation - there is no window for a race.
///
/// This is also where the decision trail is recorded. The domain stays framework-free, so the
/// adapters are the layer that can report what actually happened to stock and how long it took.
/// </summary>
public sealed class MongoHoldRepository(
    MongoContext context,
    TimeProvider clock,
    ILogger<MongoHoldRepository> logger) : IHoldRepository
{
    private static FilterDefinitionBuilder<InventoryDocument> InvFilter => Builders<InventoryDocument>.Filter;
    private static FilterDefinitionBuilder<HoldDocument> HoldFilter => Builders<HoldDocument>.Filter;

    public async Task CreateWithStockDeductionAsync(Hold hold, CancellationToken cancellationToken = default)
    {
        var startedAt = Stopwatch.GetTimestamp();

        // A scope rather than a message: every line below - each deduction, the insert - inherits
        // these fields, so filtering the feed by one hold shows that entire hold's story.
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["HoldId"] = hold.Id,
            ["CustomerId"] = hold.CustomerId
        });

        logger.LogDebug(
            "Creating hold {HoldId}: {LineCount} line(s), expires {ExpiresAt:O}, transactional {Transactional}",
            hold.Id, hold.Items.Count, hold.ExpiresAt, context.UseTransactions);

        using var session = await context.Client.StartSessionAsync(cancellationToken: cancellationToken);

        if (context.UseTransactions)
        {
            // WithTransactionAsync retries TransientTransactionError automatically, which is the
            // expected outcome when two guarded deductions collide on the same document.
            await session.WithTransactionAsync(async (s, token) =>
            {
                await DeductAllAsync(s, hold, token);
                await context.Holds.InsertOneAsync(
                    s, HoldDocument.FromDomain(hold), cancellationToken: token);
                return true;
            }, cancellationToken: cancellationToken);

            LogCommitted(hold, startedAt);
            return;
        }

        // Standalone-server fallback: compensate whatever already succeeded (see ADR-002).
        var deducted = new List<HoldItem>();
        try
        {
            foreach (var item in hold.Items)
            {
                await DeductOneAsync(session, item, cancellationToken);
                deducted.Add(item);
            }
            await context.Holds.InsertOneAsync(
                session, HoldDocument.FromDomain(hold), cancellationToken: cancellationToken);
        }
        catch
        {
            logger.LogWarning(
                "Hold {HoldId} failed after {DeductedCount} of {LineCount} deduction(s); compensating",
                hold.Id, deducted.Count, hold.Items.Count);

            foreach (var item in deducted)
            {
                await RestoreOneAsync(session, item.Sku, item.Quantity, cancellationToken);
                logger.LogDebug("Compensated {Quantity} of {Sku} back to stock", item.Quantity, item.Sku);
            }
            throw;
        }

        LogCommitted(hold, startedAt);
    }

    private void LogCommitted(Hold hold, long startedAt) => logger.LogInformation(
        "Hold {HoldId} committed for {CustomerId}: {LineCount} line(s) deducted in {ElapsedMs:0.0}ms",
        hold.Id, hold.CustomerId, hold.Items.Count,
        Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);

    private async Task DeductAllAsync(IClientSessionHandle session, Hold hold, CancellationToken ct)
    {
        foreach (var item in hold.Items)
        {
            await DeductOneAsync(session, item, ct);
        }
    }

    /// <summary>The guarded decrement. The filter holds the precondition; null means the race was lost.</summary>
    private async Task DeductOneAsync(IClientSessionHandle session, HoldItem item, CancellationToken ct)
    {
        var startedAt = Stopwatch.GetTimestamp();

        var filter = InvFilter.And(
            InvFilter.Eq(d => d.Sku, item.Sku),
            InvFilter.Gte(d => d.AvailableQty, item.Quantity));

        var update = Builders<InventoryDocument>.Update
            .Inc(d => d.AvailableQty, -item.Quantity)
            .Set(d => d.UpdatedAt, clock.GetUtcNow().UtcDateTime);

        var updated = await context.Inventory.FindOneAndUpdateAsync(
            session, filter, update,
            new FindOneAndUpdateOptions<InventoryDocument> { ReturnDocument = ReturnDocument.After },
            ct);

        var elapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

        if (updated is not null)
        {
            // The single most useful line in the feed: what moved, and what it left behind.
            logger.LogDebug(
                "Deducted {Quantity} of {Sku}: available {PreviousAvailable} -> {Available} in {ElapsedMs:0.0}ms",
                item.Quantity, item.Sku, updated.AvailableQty + item.Quantity, updated.AvailableQty, elapsedMs);
            return;
        }

        // Nothing matched. Separate an unknown product from insufficient stock for an accurate code.
        var current = await context.Inventory
            .Find(session, InvFilter.Eq(d => d.Sku, item.Sku))
            .FirstOrDefaultAsync(ct);

        if (current is null)
        {
            logger.LogDebug("Deduction rejected: {Sku} does not exist", item.Sku);
            throw new UnknownSkuException(item.Sku);
        }

        // Not an error: this is the guard doing its job under contention.
        logger.LogInformation(
            "Deduction lost the race for {Sku}: requested {Requested}, available {Available} ({ElapsedMs:0.0}ms)",
            item.Sku, item.Quantity, current.AvailableQty, elapsedMs);

        throw new InsufficientStockException(item.Sku, item.Quantity, current.AvailableQty);
    }

    private Task RestoreOneAsync(IClientSessionHandle session, string sku, int quantity, CancellationToken ct)
        => context.Inventory.UpdateOneAsync(
            session,
            InvFilter.Eq(d => d.Sku, sku),
            Builders<InventoryDocument>.Update
                .Inc(d => d.AvailableQty, quantity)
                .Set(d => d.UpdatedAt, clock.GetUtcNow().UtcDateTime),
            cancellationToken: ct);

    public async Task<Hold?> GetAsync(Guid holdId, CancellationToken cancellationToken = default)
    {
        var startedAt = Stopwatch.GetTimestamp();

        var document = await context.Holds
            .Find(HoldFilter.Eq(d => d.Id, holdId))
            .FirstOrDefaultAsync(cancellationToken);

        // Trace, not Debug: a plain read changed nothing. Debug is reserved for lines that
        // explain a decision, so that tier stays readable while the dashboard polls.
        logger.LogTrace(
            "Loaded hold {HoldId}: {Outcome} in {ElapsedMs:0.0}ms",
            holdId, document is null ? "not found" : document.Status.ToString(),
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);

        return document?.ToDomain();
    }

    public async Task<IReadOnlyList<Hold>> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        var startedAt = Stopwatch.GetTimestamp();

        var documents = await context.Holds
            .Find(HoldFilter.Eq(d => d.Status, HoldStatus.Active))
            .SortByDescending(d => d.CreatedAt)
            .Limit(200)
            .ToListAsync(cancellationToken);

        // The dashboard polls this every two seconds, so it is Trace by definition.
        logger.LogTrace(
            "Queried active holds: {Count} row(s) in {ElapsedMs:0.0}ms",
            documents.Count, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);

        return [.. documents.Select(d => d.ToDomain())];
    }

    public Task<Hold?> ReleaseAndRestoreStockAsync(
        Guid holdId, DateTimeOffset now, CancellationToken cancellationToken = default)
        => ClaimAndRestoreAsync(
            HoldFilter.And(
                HoldFilter.Eq(d => d.Id, holdId),
                HoldFilter.Eq(d => d.Status, HoldStatus.Active)),
            HoldStatus.Released, now, cancellationToken);

    public async Task<IReadOnlyList<Hold>> ExpireDueAndRestoreStockAsync(
        DateTimeOffset now, int maxBatch, CancellationToken cancellationToken = default)
    {
        var due = await context.Holds
            .Find(HoldFilter.And(
                HoldFilter.Eq(d => d.Status, HoldStatus.Active),
                HoldFilter.Lt(d => d.ExpiresAt, now.UtcDateTime)))
            .Limit(maxBatch)
            .ToListAsync(cancellationToken);

        if (due.Count > 0)
        {
            logger.LogDebug("Sweeper found {Count} hold(s) past their deadline", due.Count);
        }

        var expired = new List<Hold>(due.Count);
        foreach (var candidate in due)
        {
            // Claim each one individually. Only the replica whose filter matches restores the
            // stock; every other one gets null and does nothing. Exactly-once, no coordination.
            var claimed = await ClaimAndRestoreAsync(
                HoldFilter.And(
                    HoldFilter.Eq(d => d.Id, candidate.Id),
                    HoldFilter.Eq(d => d.Status, HoldStatus.Active),
                    HoldFilter.Lt(d => d.ExpiresAt, now.UtcDateTime)),
                HoldStatus.Expired, now, cancellationToken);

            if (claimed is not null) expired.Add(claimed);
        }

        return expired;
    }

    /// <summary>
    /// Compare-and-swap the hold into a terminal state and restore its stock in one transaction,
    /// so a crash can never leave a resolved hold whose stock was never returned.
    /// </summary>
    private async Task<Hold?> ClaimAndRestoreAsync(
        FilterDefinition<HoldDocument> claim, HoldStatus target, DateTimeOffset now, CancellationToken ct)
    {
        var startedAt = Stopwatch.GetTimestamp();

        var update = Builders<HoldDocument>.Update
            .Set(d => d.Status, target)
            .Set(d => d.ResolvedAt, now.UtcDateTime);

        var options = new FindOneAndUpdateOptions<HoldDocument> { ReturnDocument = ReturnDocument.After };

        using var session = await context.Client.StartSessionAsync(cancellationToken: ct);

        async Task<HoldDocument?> WorkAsync(IClientSessionHandle s, CancellationToken token)
        {
            var claimed = await context.Holds.FindOneAndUpdateAsync(s, claim, update, options, token);
            if (claimed is null) return null;   // another caller already claimed it

            foreach (var item in claimed.Items)
            {
                await RestoreOneAsync(s, item.Sku, item.Quantity, token);
                logger.LogDebug(
                    "Restored {Quantity} of {Sku} to stock for hold {HoldId}",
                    item.Quantity, item.Sku, claimed.Id);
            }
            return claimed;
        }

        HoldDocument? result;
        if (context.UseTransactions)
        {
            result = await session.WithTransactionAsync(WorkAsync, cancellationToken: ct);
        }
        else
        {
            result = await WorkAsync(session, ct);
        }

        var elapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

        if (result is null)
        {
            // The compare-and-swap matched nothing: someone else got there first. This is the
            // release-versus-sweeper race resolving correctly, not a fault.
            logger.LogDebug(
                "Claim for {Status} matched no hold - already resolved by another caller ({ElapsedMs:0.0}ms)",
                target, elapsedMs);
            return null;
        }

        logger.LogInformation(
            "Hold {HoldId} transitioned to {Status}, {LineCount} line(s) restored in {ElapsedMs:0.0}ms",
            result.Id, target, result.Items.Count, elapsedMs);

        return result.ToDomain();
    }
}
