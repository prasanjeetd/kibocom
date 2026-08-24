using System.ComponentModel.DataAnnotations;

namespace InventoryHold.Contracts;

public sealed record CreateHoldRequest
{
    [Required]
    [MinLength(1, ErrorMessage = "At least one item is required.")]
    public required IReadOnlyList<CreateHoldItem> Items { get; init; }

    [Required]
    [StringLength(64, MinimumLength = 1)]
    public required string CustomerId { get; init; }
}

public sealed record CreateHoldItem
{
    [Required]
    [StringLength(64, MinimumLength = 1)]
    public required string Sku { get; init; }

    [Range(1, 10_000, ErrorMessage = "Quantity must be at least 1.")]
    public required int Quantity { get; init; }
}
