using InventoryHold.Contracts;
using InventoryHold.Domain.Services;
using Microsoft.AspNetCore.Mvc;

namespace InventoryHold.WebApi.Controllers;

[ApiController]
[Route("api/inventory")]
[Produces("application/json")]
public sealed class InventoryController(InventoryService inventory) : ControllerBase
{
    /// <summary>
    /// Current stock levels. This is the high-frequency read path, so it is served through the
    /// Redis caching decorator and invalidated on every hold mutation.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<InventoryItemResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<InventoryItemResponse>>> GetAll(
        CancellationToken cancellationToken)
        => Ok(await inventory.GetAllAsync(cancellationToken));
}
