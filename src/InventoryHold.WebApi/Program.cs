using System.Text.Json.Serialization;
using InventoryHold.Infrastructure;
using InventoryHold.Infrastructure.Mongo;
using InventoryHold.WebApi.HostedServices;
using InventoryHold.WebApi.Middleware;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Scalar.AspNetCore;

// Local development reads connection strings from a gitignored .env at the repository root.
// In Docker and in the cloud the same keys arrive as real environment variables instead.
DotEnv.LoadFromAncestors();

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services.AddOpenApi();

builder.Services.AddInventoryHoldInfrastructure(builder.Configuration);
builder.Services.AddHostedService<ExpirySweeper>();

builder.Services.AddHealthChecks()
    .AddCheck<MongoHealthCheck>("mongodb", tags: ["ready"])
    .AddCheck<RedisHealthCheck>("redis", tags: ["ready"])
    .AddCheck<RabbitMqHealthCheck>("rabbitmq", tags: ["ready"]);

// The SPA is served from a different origin in development (Vite) and in the cloud.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                     ?? ["http://localhost:5173", "http://localhost:4173", "http://localhost:8080"];

builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

app.UseExceptionHandler();
app.UseCors();

app.MapOpenApi();
app.MapScalarApiReference(options => options.WithTitle("Inventory Hold API"));

app.MapControllers();

// Liveness: is this process able to answer at all? No dependency checks, because a platform
// probe that fails on a degraded dependency will restart a service that is serving correctly.
// This is the path orchestrators should watch.
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = (context, _) =>
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync("""{"status":"Alive"}""");
    }
});

// Readiness: the full dependency picture, for humans and dashboards.
app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            durationMs = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.ToDictionary(
                e => e.Key,
                e => new { status = e.Value.Status.ToString(), description = e.Value.Description })
        });
    }
});

// Seed the catalogue and ensure indexes. A failure here must not stop the API from starting -
// /health is what reports the problem, and the sweeper retries on its own schedule.
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        await scope.ServiceProvider.GetRequiredService<MongoSeeder>().SeedAsync();
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Startup seeding failed. Check /health for dependency status.");
    }
}

app.Run();

/// <summary>Minimal .env reader so local development needs no extra package.</summary>
internal static class DotEnv
{
    public static void LoadFromAncestors(string fileName = ".env")
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(candidate))
            {
                Load(candidate);
                return;
            }
            directory = directory.Parent;
        }
    }

    private static void Load(string path)
    {
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var separator = line.IndexOf('=');
            if (separator <= 0) continue;

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim().Trim('"');

            // Real environment variables always win, so Docker and the cloud override the file.
            if (Environment.GetEnvironmentVariable(key) is null)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }
}

public partial class Program;
