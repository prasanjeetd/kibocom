using InventoryHold.Contracts;
using InventoryHold.Domain.Entities;

namespace InventoryHold.Domain.Services;

public static class HoldMapper
{
    public static HoldResponse ToResponse(this Hold hold, DateTimeOffset now) => new()
    {
        HoldId = hold.Id,
        CustomerId = hold.CustomerId,
        Status = hold.StatusAt(now),
        Items = [.. hold.Items.Select(i => new HoldItemResponse
        {
            Sku = i.Sku,
            Name = i.NameSnapshot,
            Quantity = i.Quantity
        })],
        CreatedAt = hold.CreatedAt,
        ExpiresAt = hold.ExpiresAt,
        ResolvedAt = hold.ResolvedAt,
        SecondsRemaining = (int)Math.Ceiling(hold.TimeRemainingAt(now).TotalSeconds)
    };

    public static InventoryItemResponse ToResponse(this InventoryItem item) => new()
    {
        Sku = item.Sku,
        Name = item.Name,
        AvailableQuantity = item.AvailableQuantity,
        TotalQuantity = item.TotalQuantity
    };
}
