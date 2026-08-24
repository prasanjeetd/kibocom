using InventoryHold.Contracts;
using InventoryHold.Domain.Exceptions;

namespace InventoryHold.Domain.Entities;

/// <summary>
/// A temporary reservation of stock with a deadline. Expiry is what makes reserving safe:
/// an abandoned checkout returns its stock automatically instead of stranding it forever.
/// </summary>
public sealed class Hold
{
    private readonly List<HoldItem> _items;

    public Guid Id { get; }
    public string CustomerId { get; }
    public IReadOnlyList<HoldItem> Items => _items;
    public HoldStatus Status { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset ExpiresAt { get; }
    public DateTimeOffset? ResolvedAt { get; }

    private Hold(
        Guid id, string customerId, List<HoldItem> items, HoldStatus status,
        DateTimeOffset createdAt, DateTimeOffset expiresAt, DateTimeOffset? resolvedAt)
    {
        Id = id;
        CustomerId = customerId;
        _items = items;
        Status = status;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        ResolvedAt = resolvedAt;
    }

    /// <summary>Creates a new Active hold, enforcing every invariant. An invalid Hold cannot exist.</summary>
    public static Hold Create(
        string customerId, IEnumerable<HoldItem> items, DateTimeOffset now, TimeSpan timeToLive)
    {
        if (string.IsNullOrWhiteSpace(customerId))
            throw new InvalidHoldRequestException("CustomerId is required.");
        if (timeToLive <= TimeSpan.Zero)
            throw new InvalidHoldRequestException("Hold expiration must be greater than zero.");

        var list = items?.ToList() ?? [];
        if (list.Count == 0)
            throw new InvalidHoldRequestException("A hold must contain at least one item.");

        var duplicate = list.GroupBy(i => i.Sku, StringComparer.OrdinalIgnoreCase)
                            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidHoldRequestException(
                $"SKU '{duplicate.Key}' appears more than once. Combine it into a single line.");

        // UUIDv7 is time-ordered, which keeps the MongoDB _id index append-friendly.
        return new Hold(Guid.CreateVersion7(), customerId.Trim(), list,
                        HoldStatus.Active, now, now + timeToLive, resolvedAt: null);
    }

    /// <summary>Rebuilds a hold from storage. No invariant checks: persisted state is already valid.</summary>
    public static Hold Rehydrate(
        Guid id, string customerId, IEnumerable<HoldItem> items, HoldStatus status,
        DateTimeOffset createdAt, DateTimeOffset expiresAt, DateTimeOffset? resolvedAt)
        => new(id, customerId, items.ToList(), status, createdAt, expiresAt, resolvedAt);

    /// <summary>
    /// Status as of <paramref name="now"/>. The stored value is never trusted on its own: a hold
    /// whose deadline has passed reads as Expired even before the sweeper has claimed it.
    /// </summary>
    public HoldStatus StatusAt(DateTimeOffset now)
        => Status == HoldStatus.Active && now >= ExpiresAt ? HoldStatus.Expired : Status;

    public bool IsActiveAt(DateTimeOffset now) => StatusAt(now) == HoldStatus.Active;

    public TimeSpan TimeRemainingAt(DateTimeOffset now)
    {
        if (!IsActiveAt(now)) return TimeSpan.Zero;
        var remaining = ExpiresAt - now;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>
    /// Fast-fail check with a precise error before attempting the release. The atomic
    /// compare-and-swap in the repository remains the real guard against a concurrent expiry.
    /// </summary>
    public void EnsureReleasableAt(DateTimeOffset now)
    {
        var effective = StatusAt(now);
        if (effective != HoldStatus.Active)
            throw new HoldNotActiveException(Id, effective);
    }
}
