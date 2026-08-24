namespace InventoryHold.Contracts;

public sealed record InventoryItemResponse
{
    public required string Sku { get; init; }
    public required string Name { get; init; }
    public required int AvailableQuantity { get; init; }
    public required int TotalQuantity { get; init; }

    /// <summary>Quantity currently reserved by active holds. Derived: Total - Available.</summary>
    public int HeldQuantity => TotalQuantity - AvailableQuantity;
}
