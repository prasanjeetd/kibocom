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

    /// <summary>Identifies the individual operation within that request.</summary>
    public string? SpanId { get; init; }

    /// <summary>
    /// Names the specific log statement that produced this line, when the call site supplied an
    /// EventId. Category alone only identifies the class.
    /// </summary>
    public int EventId { get; init; }
    public string? EventName { get; init; }

    /// <summary>Structured fields such as Method, Path, StatusCode, ElapsedMs, HoldId.</summary>
    public IReadOnlyDictionary<string, string>? Properties { get; init; }

    public string? Exception { get; init; }
}

/// <summary>
/// One page of the log feed. The total is returned alongside the items so the UI can render page
/// numbers without a second round trip.
/// </summary>
public sealed record LogPageResponse
{
    public required IReadOnlyList<LogEntryResponse> Items { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
    public required long Total { get; init; }

    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < TotalPages;
}
