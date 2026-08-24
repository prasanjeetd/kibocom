using InventoryHold.Contracts;
using InventoryHold.Infrastructure.Logging;
using Microsoft.AspNetCore.Mvc;

namespace InventoryHold.WebApi.Controllers;

[ApiController]
[Route("api/logs")]
[Produces("application/json")]
public sealed class LogsController(MongoLogStore store) : ControllerBase
{
    /// <summary>
    /// Recent log lines, newest first. Only this service's own categories are captured, so
    /// framework and driver diagnostics — which echo connection strings — never reach here.
    /// </summary>
    /// <param name="level">Information, Warning, Error.</param>
    /// <param name="traceId">Return only the lines emitted while handling one request.</param>
    /// <param name="search">Case-insensitive substring match on the message.</param>
    /// <param name="limit">1-500, default 100.</param>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<LogEntryResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<LogEntryResponse>>> Get(
        [FromQuery] string? level,
        [FromQuery] string? traceId,
        [FromQuery] string? search,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var entries = await store.QueryAsync(level, traceId, search, limit, cancellationToken);

        return Ok(entries.Select(e => new LogEntryResponse
        {
            Timestamp = new DateTimeOffset(DateTime.SpecifyKind(e.Timestamp, DateTimeKind.Utc)),
            Level = e.Level,
            Category = e.Category,
            Message = e.Message,
            TraceId = e.TraceId,
            Properties = e.Properties,
            Exception = e.Exception
        }).ToList());
    }
}
