using InventoryHold.Domain.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace InventoryHold.WebApi.Middleware;

/// <summary>
/// Maps domain exceptions to RFC 9457 ProblemDetails in one place, which is why no controller in
/// this solution contains a try/catch. Every failure gets a meaningful status code, never a bare 500.
/// </summary>
public sealed class DomainExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<DomainExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var problem = Map(exception);

        if (problem.Status >= 500)
        {
            logger.LogError(exception, "Unhandled exception on {Path}", httpContext.Request.Path);
        }
        else
        {
            logger.LogInformation(
                "{Status} on {Path}: {Detail}", problem.Status, httpContext.Request.Path, problem.Detail);
        }

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = problem
        });
    }

    private static ProblemDetails Map(Exception exception) => exception switch
    {
        HoldNotFoundException e => new ProblemDetails
        {
            Status = StatusCodes.Status404NotFound,
            Title = "Hold not found",
            Detail = e.Message,
            Extensions = { ["holdId"] = e.HoldId }
        },

        // Expected business outcome under contention, not a server fault.
        InsufficientStockException e => new ProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Title = "Insufficient stock",
            Detail = e.Message,
            Extensions =
            {
                ["sku"] = e.Sku,
                ["requested"] = e.Requested,
                ["available"] = e.Available
            }
        },

        HoldNotActiveException e => new ProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Title = "Hold is no longer active",
            Detail = e.Message,
            Extensions = { ["holdId"] = e.HoldId, ["status"] = e.Status.ToString() }
        },

        // Well-formed request naming a product that does not exist.
        UnknownSkuException e => new ProblemDetails
        {
            Status = StatusCodes.Status422UnprocessableEntity,
            Title = "Unknown product",
            Detail = e.Message,
            Extensions = { ["sku"] = e.Sku }
        },

        InvalidHoldRequestException e => new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Invalid hold request",
            Detail = e.Message
        },

        // 499 Client Closed Request: the caller went away, so this is not a server fault.
        OperationCanceledException => new ProblemDetails
        {
            Status = 499,
            Title = "Request cancelled"
        },

        _ => new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "Unexpected error",
            Detail = "An unexpected error occurred. The incident has been logged."
        }
    };
}
