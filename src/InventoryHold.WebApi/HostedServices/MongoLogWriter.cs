using InventoryHold.Infrastructure.Logging;

namespace InventoryHold.WebApi.HostedServices;

/// <summary>
/// Drains the log channel into MongoDB in batches.
///
/// Batching matters on the free tier: Atlas M0 allows roughly 100 operations per second, and one
/// insert per log line would spend that budget on diagnostics instead of on holds.
/// </summary>
public sealed class MongoLogWriter(LogChannel channel, MongoLogStore store) : BackgroundService
{
    private const int MaxBatch = 100;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await store.EnsureCollectionAsync(stoppingToken);
        }
        catch (Exception)
        {
            // If the collection cannot be created the API must still serve; the feed is a
            // diagnostic convenience, never a dependency.
        }

        var batch = new List<LogDocument>(MaxBatch);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await channel.Reader.WaitToReadAsync(stoppingToken)) break;

                while (batch.Count < MaxBatch && channel.Reader.TryRead(out var entry))
                {
                    batch.Add(ToDocument(entry));
                }

                if (batch.Count > 0)
                {
                    await store.Collection.InsertManyAsync(
                        batch, cancellationToken: stoppingToken);
                    batch.Clear();
                }

                await Task.Delay(FlushInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // Never let a logging failure take down the process, and never log about a
                // logging failure - that is how feedback loops start.
                batch.Clear();
                try { await Task.Delay(FlushInterval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private static LogDocument ToDocument(LogEntry entry) => new()
    {
        Timestamp = entry.Timestamp,
        Level = entry.Level,
        Category = entry.Category,
        Message = entry.Message,
        TraceId = entry.TraceId,
        SpanId = entry.SpanId,
        EventId = entry.EventId,
        EventName = entry.EventName,
        Properties = entry.Properties,
        Exception = entry.Exception
    };
}
