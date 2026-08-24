namespace InventoryHold.Domain.Services;

/// <summary>
/// Business policy for holds, owned by the domain so it carries no framework dependency.
/// The WebApi layer binds it from configuration (Hold__ExpirationMinutes).
/// </summary>
public sealed record HoldPolicy(TimeSpan TimeToLive)
{
    public static HoldPolicy Default { get; } = new(TimeSpan.FromMinutes(15));
}
