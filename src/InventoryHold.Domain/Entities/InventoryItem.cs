namespace InventoryHold.Domain.Entities;

/// <summary>
/// Stock for one product. The invariant this service exists to protect:
/// AvailableQuantity + sum(quantities of all active holds) == TotalQuantity.
/// </summary>
public sealed class InventoryItem(string sku, string name, int totalQuantity, int availableQuantity)
{
    public string Sku { get; } = sku;
    public string Name { get; } = name;
    public int TotalQuantity { get; } = totalQuantity;
    public int AvailableQuantity { get; } = availableQuantity;

    /// <summary>Quantity currently reserved by active holds.</summary>
    public int HeldQuantity => TotalQuantity - AvailableQuantity;

    public bool CanSatisfy(int quantity) => AvailableQuantity >= quantity;
}
