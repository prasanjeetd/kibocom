using InventoryHold.Contracts;
using InventoryHold.Infrastructure.Logging;
using Microsoft.AspNetCore.Mvc;

namespace InventoryHold.WebApi.Controllers;

[ApiController]
[Route("api/logs")]
[Produces("application/json")]
public sealed class LogsController(MongoLogStore store) : ControllerBase
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    /// <summary>
    /// A page of log lines, newest first. Only this service's own categories are captured, so
    /// framework and driver diagnostics — which echo connection strings — never reach here.
    /// </summary>
    /// <param name="page">1-based. Values below 1 are treated as 1.</param>
    /// <param name="pageSize">1-100, default 20.</param>
    /// <param name="level">Debug, Information, Warning, Error.</param>
    /// <param name="traceId">Return only the lines emitted while handling one request.</param>
    /// <param name="search">Case-insensitive substring match on the message.</param>
    [HttpGet]
    [ProducesResponseType<LogPageResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<LogPageResponse>> Get(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize,
        [FromQuery] string? level = null,
        [FromQuery] string? traceId = null,
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var (entries, total) = await store.QueryAsync(
            level, traceId, search, skip: (page - 1) * pageSize, limit: pageSize, cancellationToken);

        return Ok(new LogPageResponse
        {
            Page = page,
            PageSize = pageSize,
            Total = total,
            Items = [.. entries.Select(e => new LogEntryResponse
            {
                Timestamp = new DateTimeOffset(DateTime.SpecifyKind(e.Timestamp, DateTimeKind.Utc)),
                Level = e.Level,
                Category = e.Category,
                Message = e.Message,
                TraceId = e.TraceId,
                SpanId = e.SpanId,
                EventId = e.EventId,
                EventName = e.EventName,
                Properties = e.Properties,
                Exception = e.Exception
            })]
        });
    }
}
