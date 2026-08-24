using InventoryHold.Domain.Repositories;
using InventoryHold.Domain.Services;
using InventoryHold.Infrastructure.Messaging;
using InventoryHold.Infrastructure.Mongo;
using InventoryHold.Infrastructure.Options;
using InventoryHold.Infrastructure.Redis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace InventoryHold.Infrastructure;

/// <summary>
/// Holds the Redis connection. Connecting never throws into startup: if the server is unreachable
/// the multiplexer stays null and every cache operation becomes a no-op miss.
/// </summary>
public sealed class RedisConnection : IDisposable
{
    public IConnectionMultiplexer? Multiplexer { get; }

    public RedisConnection(IOptions<RedisOptions> options, ILogger<RedisConnection> logger)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("Redis disabled by configuration; caching is off");
            return;
        }

        try
        {
            var configuration = ConfigurationOptions.Parse(settings.ConnectionString);
            configuration.AbortOnConnectFail = false;
            configuration.ConnectRetry = 3;
            configuration.ConnectTimeout = 5000;

            Multiplexer = ConnectionMultiplexer.Connect(configuration);
            logger.LogInformation("Redis connected");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Redis unavailable at startup; continuing without cache");
        }
    }

    public void Dispose() => Multiplexer?.Dispose();
}

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInventoryHoldInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MongoOptions>(configuration.GetSection(MongoOptions.SectionName));
        services.Configure<RedisOptions>(configuration.GetSection(RedisOptions.SectionName));
        services.Configure<RabbitMqOptions>(configuration.GetSection(RabbitMqOptions.SectionName));
        services.Configure<HoldOptions>(configuration.GetSection(HoldOptions.SectionName));

        // Injected clock: every expiry decision reads from this, so tests can move time at will.
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<MongoContext>();
        services.AddSingleton<MongoSeeder>();
        services.AddSingleton<IHoldRepository, MongoHoldRepository>();

        services.AddSingleton<RedisConnection>();
        services.AddSingleton<ICacheService, RedisCacheService>();

        // Decorator: MongoDB underneath, Redis in front. The domain never sees the difference.
        services.AddSingleton<MongoInventoryRepository>();
        services.AddSingleton<IInventoryRepository>(sp => new CachedInventoryRepository(
            sp.GetRequiredService<MongoInventoryRepository>(),
            sp.GetRequiredService<ICacheService>(),
            sp.GetRequiredService<IOptions<RedisOptions>>()));

        // Registered concretely as well, so the health check can read the live connection
        // state instead of opening a throwaway connection of its own.
        services.AddSingleton<RabbitMqEventPublisher>();
        services.AddSingleton<IEventPublisher>(sp => sp.GetRequiredService<RabbitMqEventPublisher>());

        // Domain policy built from configuration, so Domain itself stays framework-free.
        services.AddSingleton(sp => new HoldPolicy(TimeSpan.FromMinutes(
            sp.GetRequiredService<IOptions<HoldOptions>>().Value.ExpirationMinutes)));

        services.AddSingleton<HoldService>();
        services.AddSingleton<InventoryService>();

        return services;
    }
}
