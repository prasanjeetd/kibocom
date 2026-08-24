using InventoryHold.Domain.Entities;

namespace InventoryHold.Domain.Repositories;

public interface IInventoryRepository
{
    Task<IReadOnlyList<InventoryItem>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<InventoryItem?> GetBySkuAsync(string sku, CancellationToken cancellationToken = default);
}
