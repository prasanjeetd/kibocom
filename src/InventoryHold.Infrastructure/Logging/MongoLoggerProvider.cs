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
public sealed class MongoLoggerProvider(
    LogChannel channel,
    TimeProvider clock,
    LogLevel minimumLevel = LogLevel.Debug) : ILoggerProvider, ISupportExternalScope
{
    private static readonly string[] CapturedPrefixes = ["InventoryHold.", "Http"];

    private IExternalScopeProvider? _scopes;

    /// <summary>
    /// Lets the host hand us the ambient scope stack. Without this, anything attached with
    /// <c>BeginScope</c> - the request's method and path, the hold being worked on - is
    /// discarded, and each line reads as an isolated fact instead of part of a story.
    /// </summary>
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public ILogger CreateLogger(string categoryName)
        => CapturedPrefixes.Any(p => categoryName.StartsWith(p, StringComparison.Ordinal))
            ? new MongoLogger(categoryName, channel, clock, minimumLevel, () => _scopes)
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

    private sealed class MongoLogger(
        string category,
        LogChannel channel,
        TimeProvider clock,
        LogLevel minimumLevel,
        Func<IExternalScopeProvider?> scopes) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => scopes()?.Push(state);

        public bool IsEnabled(LogLevel logLevel)
            => logLevel != LogLevel.None && logLevel >= minimumLevel;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            // Structured placeholders such as {HoldId} and {StatusCode} are kept as real fields
            // rather than being flattened into the message, so the UI can filter on them.
            var bag = new PropertyBag();

            // Ambient scope values first, so a key set on the line itself wins over the scope.
            scopes()?.ForEachScope(static (scope, b) => b.Collect(scope), bag);
            bag.Collect(state);

            var activity = Activity.Current;

            channel.Publish(new LogEntry
            {
                Timestamp = clock.GetUtcNow().UtcDateTime,
                Level = logLevel.ToString(),
                // The full category, not just the trailing segment. Truncating at write time is
                // lossy and unrecoverable: "MongoHoldRepository" does not say which assembly or
                // namespace it came from, and the UI can shorten it for display whenever it likes.
                Category = category,
                Message = formatter(state, exception),
                TraceId = activity?.TraceId.ToString(),
                SpanId = activity?.SpanId.ToString(),
                EventId = eventId.Id,
                EventName = eventId.Name,
                Properties = bag.Properties,
                Exception = exception?.ToString()
            });
        }

        /// <summary>Accumulates structured fields from the scope stack and the log line itself.</summary>
        private sealed class PropertyBag
        {
            public Dictionary<string, string>? Properties { get; private set; }

            /// <summary>
            /// Keys the framework pushes on every request that earn nothing here. TraceId and
            /// SpanId are already first-class columns; the rest identify the same request by
            /// four more names. Every one of them is copied onto every document in a capped
            /// collection, so dropping them is what decides how many lines actually fit.
            /// </summary>
            private static readonly HashSet<string> Redundant = new(StringComparer.Ordinal)
            {
                "TraceId", "SpanId", "ParentId", "ConnectionId", "RequestId",
                "RequestPath", "ActionId", "ActionName"
            };

            public void Collect(object? state)
            {
                // IEnumerable, not IReadOnlyList. The log line itself arrives as FormattedLogValues
                // (a list), but a scope is typically a Dictionary, which is not a list - matching
                // on the narrower type silently drops every scope value.
                if (state is not IEnumerable<KeyValuePair<string, object?>> values) return;

                foreach (var pair in values)
                {
                    if (pair.Key == "{OriginalFormat}" || pair.Value is null) continue;
                    if (Redundant.Contains(pair.Key)) continue;

                    (Properties ??= [])[pair.Key] = pair.Value.ToString() ?? string.Empty;
                }
            }
        }
    }
}
