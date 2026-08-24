using InventoryHold.Contracts;
using InventoryHold.Domain.Services;
using Microsoft.AspNetCore.Mvc;

namespace InventoryHold.WebApi.Controllers;

[ApiController]
[Route("api/holds")]
[Produces("application/json")]
public sealed class HoldsController(HoldService holds) : ControllerBase
{
    /// <summary>
    /// Places a hold. Stock is deducted atomically; if any line loses its race the whole
    /// request fails and nothing is deducted.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<HoldResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<HoldResponse>> Create(
        [FromBody] CreateHoldRequest request, CancellationToken cancellationToken)
    {
        var hold = await holds.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { holdId = hold.HoldId }, hold);
    }

    /// <summary>
    /// Returns a hold in any state. A hold past its deadline reports Expired rather than 404,
    /// so the caller can tell "your hold timed out" from "that id never existed".
    /// </summary>
    [HttpGet("{holdId:guid}")]
    [ProducesResponseType<HoldResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<HoldResponse>> GetById(Guid holdId, CancellationToken cancellationToken)
        => Ok(await holds.GetAsync(holdId, cancellationToken));

    /// <summary>Active holds, for the dashboard.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<HoldResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<HoldResponse>>> GetActive(CancellationToken cancellationToken)
        => Ok(await holds.GetActiveAsync(cancellationToken));

    /// <summary>Releases a hold and restores its stock. Already-resolved holds return 409.</summary>
    [HttpDelete("{holdId:guid}")]
    [ProducesResponseType<HoldResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<HoldResponse>> Release(Guid holdId, CancellationToken cancellationToken)
        => Ok(await holds.ReleaseAsync(holdId, cancellationToken));
}
