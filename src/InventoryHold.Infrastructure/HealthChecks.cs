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

/// <summary>
/// Reports on the publisher's existing connection rather than dialling the broker.
///
/// Two reasons. A probe that opens a fresh TLS connection every few seconds exhausts the
/// connection quota of a capped plan, so the healthcheck itself becomes the outage. And publishing
/// is deliberately fail-open - a broker outage costs events, not availability - so a missing
/// connection is Degraded, never Unhealthy. Reporting Unhealthy here would invite an orchestrator
/// to restart a service that is answering requests perfectly.
/// </summary>
public sealed class RabbitMqHealthCheck(
    RabbitMqEventPublisher publisher, IOptions<RabbitMqOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled)
            return Task.FromResult(HealthCheckResult.Healthy("RabbitMQ disabled by configuration"));

        return Task.FromResult(publisher.IsConnected
            ? HealthCheckResult.Healthy("RabbitMQ connected")
            // The connection is opened lazily on the first publish, so "not yet connected" is a
            // normal state for a freshly started instance that has had no writes.
            : HealthCheckResult.Degraded("RabbitMQ not currently connected; publishing fails open"));
    }
}
