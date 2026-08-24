namespace InventoryHold.Contracts;

/// <summary>One line from the service's own log feed, as shown in the UI.</summary>
public sealed record LogEntryResponse
{
    public required DateTimeOffset Timestamp { get; init; }
    public required string Level { get; init; }
    public required string Category { get; init; }
    public required string Message { get; init; }

    /// <summary>Groups every line produced while handling one request.</summary>
    public string? TraceId { get; init; }

    /// <summary>Structured fields such as Method, Path, StatusCode, ElapsedMs, HoldId.</summary>
    public IReadOnlyDictionary<string, string>? Properties { get; init; }

    public string? Exception { get; init; }
}
