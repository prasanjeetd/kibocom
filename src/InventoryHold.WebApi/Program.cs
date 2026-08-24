using System.Diagnostics;
using System.Text.Json.Serialization;
using InventoryHold.Infrastructure;
using InventoryHold.Infrastructure.Logging;
using InventoryHold.Infrastructure.Mongo;
using InventoryHold.WebApi.HostedServices;
using InventoryHold.WebApi.Middleware;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Scalar.AspNetCore;

// Local development reads connection strings from a gitignored .env at the repository root.
// In Docker and in the cloud the same keys arrive as real environment variables instead.
DotEnv.LoadFromAncestors();

var builder = WebApplication.CreateBuilder(args);

// Stamp every log line with the trace id, so all the work done for one request can be pulled
// back together - and so a traceId returned in a ProblemDetails body can be looked up directly.
builder.Logging.Configure(options => options.ActivityTrackingOptions =
    ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);

if (!builder.Environment.IsDevelopment())
{
    // Plain text is for humans at a terminal. Anything shipping to an aggregator needs to be
    // parseable without regex, and the named placeholders already used throughout survive as
    // real fields rather than being flattened into a sentence.
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
}

// Mirror this service's own log lines into a capped MongoDB collection so the UI can show them.
// Registered after the ClearProviders branch above, or Production would drop this provider along
// with the console one and the feed would silently stay empty.
//
// The channel is created up front because logging providers are constructed before the service
// provider exists; the background writer resolves MongoDB later and drains it.
var logChannel = new LogChannel();
builder.Services.AddSingleton(logChannel);
builder.Logging.AddProvider(new MongoLoggerProvider(logChannel));
builder.Services.AddHostedService<InventoryHold.WebApi.HostedServices.MongoLogWriter>();

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

// One line per API request: method, path, outcome, duration. Health probes are skipped because
// an orchestrator polling every few seconds would otherwise drown everything else out.
//
// Registered ahead of UseExceptionHandler on purpose. Middleware registered first sits outermost,
// so this only sees the final status code if the exception handler has already run and written
// it. Placed after, every handled domain failure would be logged as a 200.
var requestLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Http");
app.Use(async (context, next) =>
{
    // Skip the noise floor. Health probes fire every few seconds, CORS preflights carry no
    // information, and /api/logs is the log viewer itself - logging it would fill the feed with
    // records of the feed being read.
    if (context.Request.Path.StartsWithSegments("/health")
        || context.Request.Path.StartsWithSegments("/api/logs")
        || HttpMethods.IsOptions(context.Request.Method))
    {
        await next();
        return;
    }

    var startedAt = Stopwatch.GetTimestamp();
    try
    {
        await next();
    }
    finally
    {
        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        requestLogger.Log(
            context.Response.StatusCode >= 500 ? LogLevel.Error : LogLevel.Information,
            "{Method} {Path} responded {StatusCode} in {ElapsedMs:0.0}ms",
            context.Request.Method,
            context.Request.Path.Value,
            context.Response.StatusCode,
            elapsed.TotalMilliseconds);
    }
});

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
