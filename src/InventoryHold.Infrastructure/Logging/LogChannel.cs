using System.Threading.Channels;

namespace InventoryHold.Infrastructure.Logging;

/// <summary>One captured log line, on its way to MongoDB.</summary>
public sealed record LogEntry
{
    public required DateTime Timestamp { get; init; }
    public required string Level { get; init; }
    public required string Category { get; init; }
    public required string Message { get; init; }

    /// <summary>Ties every line produced while handling one request back together.</summary>
    public string? TraceId { get; init; }

    /// <summary>Identifies the individual operation within that request.</summary>
    public string? SpanId { get; init; }

    /// <summary>
    /// The EventId the call site passed, when it passed one. This is what names a specific log
    /// statement rather than merely the class it lives in.
    /// </summary>
    public int EventId { get; init; }
    public string? EventName { get; init; }

    public Dictionary<string, string>? Properties { get; init; }
    public string? Exception { get; init; }
}

/// <summary>
/// Hands log lines from the logging pipeline to the background writer.
///
/// Bounded and drop-oldest on purpose: logging must never block a request or grow without limit.
/// If the writer falls behind, losing the oldest diagnostics is strictly better than stalling the
/// API or exhausting memory.
/// </summary>
public sealed class LogChannel
{
    private readonly Channel<LogEntry> _channel = Channel.CreateBounded<LogEntry>(
        new BoundedChannelOptions(2_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

    public ChannelReader<LogEntry> Reader => _channel.Reader;

    public void Publish(LogEntry entry) => _channel.Writer.TryWrite(entry);
}
