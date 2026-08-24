using InventoryHold.Infrastructure.Messaging;
using InventoryHold.Infrastructure.Mongo;
using InventoryHold.Infrastructure.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using RabbitMQ.Client;

namespace InventoryHold.Infrastructure;

/// <summary>Pings MongoDB. This is what proves the API is actually wired to its database.</summary>
public sealed class MongoHealthCheck(MongoContext mongo) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await mongo.Database.RunCommandAsync<BsonDocument>(
                new BsonDocument("ping", 1), cancellationToken: cancellationToken);
            return HealthCheckResult.Healthy("MongoDB reachable");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("MongoDB unreachable", ex);
        }
    }
}

/// <summary>
/// Reports Degraded rather than Unhealthy when Redis is down: the API still serves correctly
/// from MongoDB, it is simply slower. A cache outage is not an outage.
/// </summary>
public sealed class RedisHealthCheck(RedisConnection connection) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(connection.Multiplexer is { IsConnected: true }
            ? HealthCheckResult.Healthy("Redis connected")
            : HealthCheckResult.Degraded("Redis unavailable; serving uncached"));
}

public sealed class RabbitMqHealthCheck(IOptions<RabbitMqOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled) return HealthCheckResult.Healthy("RabbitMQ disabled");

        try
        {
            var factory = new ConnectionFactory { Uri = new Uri(options.Value.Uri) };
            await using var connection = await factory.CreateConnectionAsync(cancellationToken);
            return connection.IsOpen
                ? HealthCheckResult.Healthy("RabbitMQ reachable")
                : HealthCheckResult.Unhealthy("RabbitMQ connection closed");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("RabbitMQ unreachable", ex);
        }
    }
}
