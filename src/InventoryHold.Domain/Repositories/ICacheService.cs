namespace InventoryHold.Domain.Repositories;

/// <summary>
/// Cache port. Implementations must fail open: a cache outage degrades performance,
/// never availability.
/// </summary>
public interface ICacheService
{
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class;

    Task SetAsync<T>(string key, T value, TimeSpan timeToLive, CancellationToken cancellationToken = default)
        where T : class;

    /// <summary>Invalidate by deletion, never by writing the new value - see ADR-005.</summary>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}

public static class CacheKeys
{
    public const string AllInventory = "inventory:all";
}
