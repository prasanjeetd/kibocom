using System.Text.Json;
using InventoryHold.Domain.Entities;
using InventoryHold.Domain.Repositories;
using InventoryHold.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace InventoryHold.Infrastructure.Redis;

/// <summary>
/// Redis adapter that fails open. Every operation is wrapped: if Redis is unreachable the caller
/// gets a cache miss and falls through to MongoDB. A cache outage degrades latency, never uptime.
/// </summary>
public sealed class RedisCacheService(
    RedisConnection connection,
    ILogger<RedisCacheService> logger) : ICacheService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private IConnectionMultiplexer? multiplexer => connection.Multiplexer;

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        where T : class
    {
        if (multiplexer is not { IsConnected: true }) return null;

        try
        {
            var value = await multiplexer.GetDatabase().StringGetAsync(key);
            return value.IsNullOrEmpty ? null : JsonSerializer.Deserialize<T>((string)value!, Json);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Redis GET failed for {Key}; serving from source", key);
            return null;
        }
    }

    public async Task SetAsync<T>(
        string key, T value, TimeSpan timeToLive, CancellationToken cancellationToken = default)
        where T : class
    {
        if (multiplexer is not { IsConnected: true }) return;

        try
        {
            var payload = JsonSerializer.Serialize(value, Json);
            await multiplexer.GetDatabase().StringSetAsync(key, payload, timeToLive);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Redis SET failed for {Key}; continuing uncached", key);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        if (multiplexer is not { IsConnected: true }) return;

        try
        {
            await multiplexer.GetDatabase().KeyDeleteAsync(key);
        }
        catch (Exception ex)
        {
            // Worst case the stale entry survives until its TTL expires - which is exactly why
            // the TTL exists. Never fail a mutation because invalidation could not be delivered.
            logger.LogWarning(ex, "Redis DEL failed for {Key}; entry will lapse via TTL", key);
        }
    }
}

/// <summary>
/// Caching decorator over the real inventory repository. The domain service never learns that
/// Redis exists, which keeps cache behaviour out of the hold-lifecycle tests entirely.
/// </summary>
public sealed class CachedInventoryRepository(
    IInventoryRepository inner,
    ICacheService cache,
    IOptions<RedisOptions> options,
    ILogger<CachedInventoryRepository>? logger = null) : IInventoryRepository
{
    private TimeSpan Ttl => TimeSpan.FromSeconds(options.Value.InventoryTtlSeconds);

    public async Task<IReadOnlyList<InventoryItem>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var cached = await TryGetAsync(cancellationToken);
        if (cached is not null)
        {
            return [.. cached.Items.Select(i => new InventoryItem(i.Sku, i.Name, i.TotalQuantity, i.AvailableQuantity))];
        }

        var fresh = await inner.GetAllAsync(cancellationToken);

        var snapshot = new CachedInventory(
            [.. fresh.Select(i => new CachedInventoryItem(i.Sku, i.Name, i.TotalQuantity, i.AvailableQuantity))]);

        await TrySetAsync(snapshot, cancellationToken);
        return fresh;
    }

    // Fail open at this layer too, not only inside the Redis adapter. The decorator must not
    // depend on its collaborator being well behaved: whatever the cache does, the caller still
    // gets inventory from MongoDB.
    private async Task<CachedInventory?> TryGetAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await cache.GetAsync<CachedInventory>(CacheKeys.AllInventory, cancellationToken);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Inventory cache read failed; falling back to the database");
            return null;
        }
    }

    private async Task TrySetAsync(CachedInventory snapshot, CancellationToken cancellationToken)
    {
        try
        {
            await cache.SetAsync(CacheKeys.AllInventory, snapshot, Ttl, cancellationToken);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Inventory cache write failed; continuing uncached");
        }
    }

    /// <summary>
    /// Deliberately uncached. This read feeds the stock-deduction decision, and a stale value
    /// there would produce a misleading error message on a hold that was going to fail anyway.
    /// </summary>
    public Task<InventoryItem?> GetBySkuAsync(string sku, CancellationToken cancellationToken = default)
        => inner.GetBySkuAsync(sku, cancellationToken);
}

public sealed record CachedInventory(IReadOnlyList<CachedInventoryItem> Items);

public sealed record CachedInventoryItem(string Sku, string Name, int TotalQuantity, int AvailableQuantity);
