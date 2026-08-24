using InventoryHold.Domain.Entities;

namespace InventoryHold.Domain.Events;

/// <summary>
/// An integration event published when a hold changes state. Payloads are self-contained:
/// a consumer can act without calling back into this service. EventId lets consumers dedupe,
/// because delivery is at-least-once.
/// </summary>
public sealed record HoldEvent
{
    public const string Created = "HoldCreated";
    public const string Released = "HoldReleased";
    public const string Expired = "HoldExpired";

    public Guid EventId { get; init; } = Guid.CreateVersion7();
    public required string EventType { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required Guid HoldId { get; init; }
    public required string CustomerId { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required IReadOnlyList<HoldEventItem> Items { get; init; }

    /// <summary>Topic routing key: hold.created / hold.released / hold.expired.</summary>
    public string RoutingKey => EventType switch
    {
        Created => "hold.created",
        Released => "hold.released",
        Expired => "hold.expired",
        _ => "hold.unknown"
    };

    private static HoldEvent For(string type, Hold hold, DateTimeOffset now) => new()
    {
        EventType = type,
        OccurredAt = now,
        HoldId = hold.Id,
        CustomerId = hold.CustomerId,
        ExpiresAt = hold.ExpiresAt,
        Items = [.. hold.Items.Select(i => new HoldEventItem(i.Sku, i.Quantity))]
    };

    public static HoldEvent HoldCreated(Hold hold, DateTimeOffset now) => For(Created, hold, now);
    public static HoldEvent HoldReleased(Hold hold, DateTimeOffset now) => For(Released, hold, now);
    public static HoldEvent HoldExpired(Hold hold, DateTimeOffset now) => For(Expired, hold, now);
}

public sealed record HoldEventItem(string Sku, int Quantity);
