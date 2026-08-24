namespace InventoryHold.Domain.Exceptions;

/// <summary>Base for expected business failures. Mapped to HTTP status codes centrally.</summary>
public abstract class DomainException(string message) : Exception(message);

/// <summary>Request is structurally invalid (empty items, non-positive quantity, duplicate SKU). -> 400</summary>
public sealed class InvalidHoldRequestException(string message) : DomainException(message);

/// <summary>Request is well-formed but references a product that does not exist. -> 422</summary>
public sealed class UnknownSkuException(string sku)
    : DomainException($"Product '{sku}' does not exist.")
{
    public string Sku { get; } = sku;
}

/// <summary>Not enough stock at the moment of the atomic deduction. -> 409</summary>
public sealed class InsufficientStockException(string sku, int requested, int available)
    : DomainException($"Insufficient stock for '{sku}': requested {requested}, available {available}.")
{
    public string Sku { get; } = sku;
    public int Requested { get; } = requested;
    public int Available { get; } = available;
}

/// <summary>No hold with this id has ever existed. -> 404</summary>
public sealed class HoldNotFoundException(Guid holdId)
    : DomainException($"Hold '{holdId}' was not found.")
{
    public Guid HoldId { get; } = holdId;
}

/// <summary>The hold exists but is already Released or Expired, so it cannot be released. -> 409</summary>
public sealed class HoldNotActiveException(Guid holdId, Contracts.HoldStatus status)
    : DomainException($"Hold '{holdId}' is {status} and cannot be released.")
{
    public Guid HoldId { get; } = holdId;
    public Contracts.HoldStatus Status { get; } = status;
}
