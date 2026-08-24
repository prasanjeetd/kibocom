using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace InventoryHold.Infrastructure.Logging;

/// <summary>
/// Captures this service's own log lines so the UI can show them.
///
/// Deliberately narrow: only categories this solution owns are captured. Framework and MongoDB
/// driver logs are excluded because driver diagnostics echo connection strings, and this feed is
/// served over an unauthenticated endpoint.
/// </summary>
public sealed class MongoLoggerProvider(LogChannel channel) : ILoggerProvider
{
    private static readonly string[] CapturedPrefixes = ["InventoryHold.", "Http"];

    public ILogger CreateLogger(string categoryName)
        => CapturedPrefixes.Any(p => categoryName.StartsWith(p, StringComparison.Ordinal))
            ? new MongoLogger(categoryName, channel)
            : NullLogger.Instance;

    public void Dispose() { }

    private sealed class NullLogger : ILogger
    {
        public static readonly NullLogger Instance = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) { }
    }

    private sealed class MongoLogger(string category, LogChannel channel) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            // Structured placeholders such as {HoldId} and {StatusCode} are kept as real fields
            // rather than being flattened into the message, so the UI can filter on them.
            Dictionary<string, string>? properties = null;
            if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
            {
                foreach (var pair in values)
                {
                    if (pair.Key == "{OriginalFormat}" || pair.Value is null) continue;
                    (properties ??= [])[pair.Key] = pair.Value.ToString() ?? string.Empty;
                }
            }

            channel.Publish(new LogEntry
            {
                Timestamp = DateTime.UtcNow,
                Level = logLevel.ToString(),
                Category = category.Split('.')[^1],
                Message = formatter(state, exception),
                TraceId = Activity.Current?.TraceId.ToString(),
                Properties = properties,
                Exception = exception?.ToString()
            });
        }
    }
}
