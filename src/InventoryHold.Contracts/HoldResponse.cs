namespace InventoryHold.Contracts;

public sealed record HoldResponse
{
    public required Guid HoldId { get; init; }
    public required string CustomerId { get; init; }
    public required HoldStatus Status { get; init; }
    public required IReadOnlyList<HoldItemResponse> Items { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }

    /// <summary>Seconds until expiry; zero once the hold is no longer active.</summary>
    public required int SecondsRemaining { get; init; }
}

public sealed record HoldItemResponse
{
    public required string Sku { get; init; }
    public required string Name { get; init; }
    public required int Quantity { get; init; }
}
