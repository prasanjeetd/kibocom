using InventoryHold.Domain.Exceptions;

namespace InventoryHold.Domain.Entities;

/// <summary>
/// One line of a hold. <see cref="NameSnapshot"/> is stored deliberately so the holds list
/// renders without an N+1 lookup back into inventory.
/// </summary>
public sealed class HoldItem
{
    public string Sku { get; }
    public int Quantity { get; }
    public string NameSnapshot { get; }

    public HoldItem(string sku, int quantity, string? nameSnapshot = null)
    {
        if (string.IsNullOrWhiteSpace(sku))
            throw new InvalidHoldRequestException("SKU must not be empty.");
        if (quantity <= 0)
            throw new InvalidHoldRequestException($"Quantity for '{sku}' must be at least 1.");

        Sku = sku.Trim();
        Quantity = quantity;
        NameSnapshot = string.IsNullOrWhiteSpace(nameSnapshot) ? Sku : nameSnapshot.Trim();
    }
}
