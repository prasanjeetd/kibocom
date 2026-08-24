using InventoryHold.Contracts;
using InventoryHold.Domain.Entities;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace InventoryHold.Infrastructure.Mongo;

/// <summary>
/// Persistence shapes. They exist separately from the domain entities so that Domain carries no
/// MongoDB attributes and therefore no MongoDB dependency - the rule the architecture test enforces.
/// </summary>
public sealed class InventoryDocument
{
    [BsonId] public string Sku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int TotalQty { get; set; }
    public int AvailableQty { get; set; }
    public DateTime UpdatedAt { get; set; }

    public InventoryItem ToDomain() => new(Sku, Name, TotalQty, AvailableQty);
}

public sealed class HoldDocument
{
    [BsonId] public Guid Id { get; set; }
    public string CustomerId { get; set; } = string.Empty;
    public List<HoldItemDocument> Items { get; set; } = [];

    [BsonRepresentation(BsonType.String)]
    public HoldStatus Status { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ResolvedAt { get; set; }

    public Hold ToDomain() => Hold.Rehydrate(
        Id,
        CustomerId,
        Items.Select(i => new HoldItem(i.Sku, i.Quantity, i.NameSnapshot)),
        Status,
        new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)),
        new DateTimeOffset(DateTime.SpecifyKind(ExpiresAt, DateTimeKind.Utc)),
        ResolvedAt is null ? null : new DateTimeOffset(DateTime.SpecifyKind(ResolvedAt.Value, DateTimeKind.Utc)));

    public static HoldDocument FromDomain(Hold hold) => new()
    {
        Id = hold.Id,
        CustomerId = hold.CustomerId,
        Items = [.. hold.Items.Select(i => new HoldItemDocument
        {
            Sku = i.Sku,
            Quantity = i.Quantity,
            NameSnapshot = i.NameSnapshot
        })],
        Status = hold.Status,
        CreatedAt = hold.CreatedAt.UtcDateTime,
        ExpiresAt = hold.ExpiresAt.UtcDateTime,
        ResolvedAt = hold.ResolvedAt?.UtcDateTime
    };
}

public sealed class HoldItemDocument
{
    public string Sku { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public string NameSnapshot { get; set; } = string.Empty;
}
