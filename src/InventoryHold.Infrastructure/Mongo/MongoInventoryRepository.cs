using InventoryHold.Domain.Entities;
using InventoryHold.Domain.Repositories;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace InventoryHold.Infrastructure.Mongo;

public sealed class MongoInventoryRepository(MongoContext context) : IInventoryRepository
{
    public async Task<IReadOnlyList<InventoryItem>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var documents = await context.Inventory
            .Find(FilterDefinition<InventoryDocument>.Empty)
            .SortBy(d => d.Name)
            .ToListAsync(cancellationToken);

        return [.. documents.Select(d => d.ToDomain())];
    }

    public async Task<InventoryItem?> GetBySkuAsync(string sku, CancellationToken cancellationToken = default)
    {
        var document = await context.Inventory
            .Find(Builders<InventoryDocument>.Filter.Eq(d => d.Sku, sku))
            .FirstOrDefaultAsync(cancellationToken);

        return document?.ToDomain();
    }
}

/// <summary>
/// Seeds the catalogue on startup. Idempotent by design: quantities are only written on insert,
/// so restarting the API never resets stock in the middle of a demo.
/// </summary>
public sealed class MongoSeeder(MongoContext context, TimeProvider clock, ILogger<MongoSeeder> logger)
{
    private static readonly (string Sku, string Name, int Quantity)[] Catalogue =
    [
        ("SKU-1001", "Aeron Ergonomic Chair",        25),
        ("SKU-1002", "Standing Desk 160cm",          12),
        ("SKU-1003", "27-inch 4K Monitor",           40),
        ("SKU-1004", "Mechanical Keyboard",          75),
        ("SKU-1005", "Noise Cancelling Headphones",   8)
    ];

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        await context.EnsureIndexesAsync(cancellationToken);

        foreach (var (sku, name, quantity) in Catalogue)
        {
            await context.Inventory.UpdateOneAsync(
                Builders<InventoryDocument>.Filter.Eq(d => d.Sku, sku),
                Builders<InventoryDocument>.Update
                    .SetOnInsert(d => d.Name, name)
                    .SetOnInsert(d => d.TotalQty, quantity)
                    .SetOnInsert(d => d.AvailableQty, quantity)
                    .SetOnInsert(d => d.UpdatedAt, clock.GetUtcNow().UtcDateTime),
                new UpdateOptions { IsUpsert = true },
                cancellationToken);
        }

        logger.LogInformation("Inventory seed complete: {Count} products ensured", Catalogue.Length);
    }
}
