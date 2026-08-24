using System.Text.RegularExpressions;
using InventoryHold.Infrastructure.Mongo;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace InventoryHold.Infrastructure.Logging;

public sealed class LogDocument
{
    [BsonId] public ObjectId Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string Level { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? TraceId { get; set; }
    public string? SpanId { get; set; }
    public int EventId { get; set; }
    public string? EventName { get; set; }
    public Dictionary<string, string>? Properties { get; set; }
    public string? Exception { get; set; }
}

/// <summary>
/// Reads and writes the log feed.
///
/// Storage is a <b>capped collection</b>: a fixed-size ring buffer that evicts the oldest document
/// automatically. No cleanup job, no unbounded growth, and a hard ceiling on how much of the
/// 512 MB Atlas free tier diagnostics can ever consume.
/// </summary>
public sealed class MongoLogStore(MongoContext context)
{
    public const string CollectionName = "app_logs";
    private const long MaxSizeBytes = 4L * 1024 * 1024;
    private const long MaxDocuments = 5_000;

    public IMongoCollection<LogDocument> Collection =>
        context.Database.GetCollection<LogDocument>(CollectionName);

    public async Task EnsureCollectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await context.Database.CreateCollectionAsync(
                CollectionName,
                new CreateCollectionOptions
                {
                    Capped = true,
                    MaxSize = MaxSizeBytes,
                    MaxDocuments = MaxDocuments
                },
                cancellationToken);
        }
        catch (MongoCommandException ex) when (ex.CodeName == "NamespaceExists")
        {
            // Already created by a previous run, which is the normal case.
        }
    }

    public async Task<(IReadOnlyList<LogDocument> Items, long Total)> QueryAsync(
        string? level, string? traceId, string? search, int skip, int limit,
        CancellationToken cancellationToken = default)
    {
        var builder = Builders<LogDocument>.Filter;
        var filter = builder.Empty;

        if (!string.IsNullOrWhiteSpace(level))
            filter &= builder.Eq(d => d.Level, level);

        if (!string.IsNullOrWhiteSpace(traceId))
            filter &= builder.Eq(d => d.TraceId, NormaliseTraceId(traceId));

        if (!string.IsNullOrWhiteSpace(search))
            filter &= builder.Regex(d => d.Message,
                new BsonRegularExpression(Regex.Escape(search), "i"));

        // The collection is capped at 5,000 documents, so counting and skipping stay cheap.
        // On an uncapped collection this would need a different approach entirely.
        var total = await Collection.CountDocumentsAsync(filter, cancellationToken: cancellationToken);

        var items = await Collection
            .Find(filter)
            .SortByDescending(d => d.Timestamp)
            .Skip(Math.Max(0, skip))
            .Limit(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    /// <summary>
    /// Accepts either a bare 32-character trace id or a full W3C traceparent
    /// (<c>00-{trace}-{span}-01</c>) and returns the trace id.
    ///
    /// This matters because the two places a trace id is handed to a human disagree: the feed
    /// shows the bare id, while a ProblemDetails error body returns the whole traceparent. Pasting
    /// the one from a failed response into the filter has to find the request it describes.
    /// </summary>
    public static string NormaliseTraceId(string value)
    {
        var trimmed = value.Trim();
        var parts = trimmed.Split('-');

        return parts.Length == 4 && parts[1].Length == 32 ? parts[1] : trimmed;
    }
}
