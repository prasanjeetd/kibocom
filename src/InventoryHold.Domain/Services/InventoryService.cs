using InventoryHold.Contracts;
using InventoryHold.Domain.Repositories;

namespace InventoryHold.Domain.Services;

public sealed class InventoryService(IInventoryRepository inventory)
{
    public async Task<IReadOnlyList<InventoryItemResponse>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        var items = await inventory.GetAllAsync(cancellationToken);
        return [.. items.Select(i => i.ToResponse())];
    }
}
