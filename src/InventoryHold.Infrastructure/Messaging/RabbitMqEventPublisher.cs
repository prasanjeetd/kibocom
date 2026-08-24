using System.Text.Json;
using InventoryHold.Domain.Events;
using InventoryHold.Domain.Repositories;
using InventoryHold.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace InventoryHold.Infrastructure.Messaging;

/// <summary>
/// Publishes hold lifecycle events to a durable topic exchange. The connection is established
/// lazily and re-established on demand, so the API starts successfully even when the broker is
/// still booting.
/// </summary>
public sealed class RabbitMqEventPublisher(
    IOptions<RabbitMqOptions> options,
    ILogger<RabbitMqEventPublisher> logger) : IEventPublisher, IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly RabbitMqOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IConnection? _connection;
    private IChannel? _channel;

    public async Task PublishAsync(HoldEvent domainEvent, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return;

        try
        {
            var channel = await EnsureChannelAsync(cancellationToken);

            var properties = new BasicProperties
            {
                Persistent = true,
                ContentType = "application/json",
                MessageId = domainEvent.EventId.ToString(),
                Type = domainEvent.EventType,
                Timestamp = new AmqpTimestamp(domainEvent.OccurredAt.ToUnixTimeSeconds())
            };

            await channel.BasicPublishAsync(
                exchange: _options.Exchange,
                routingKey: domainEvent.RoutingKey,
                mandatory: false,
                basicProperties: properties,
                body: JsonSerializer.SerializeToUtf8Bytes(domainEvent, Json),
                cancellationToken: cancellationToken);

            logger.LogInformation(
                "Published {EventType} for hold {HoldId}", domainEvent.EventType, domainEvent.HoldId);
        }
        catch (Exception ex)
        {
            // The database change is already committed and is the source of truth. Failing the
            // request here would report an error for work that actually succeeded. This is the
            // dual-write gap noted in the README; a transactional outbox is the production fix.
            logger.LogError(ex,
                "Failed to publish {EventType} for hold {HoldId}. State is committed; event is lost.",
                domainEvent.EventType, domainEvent.HoldId);
        }
    }

    private async Task<IChannel> EnsureChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true }) return _channel;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_channel is { IsOpen: true }) return _channel;

            var factory = new ConnectionFactory
            {
                Uri = new Uri(_options.Uri),
                AutomaticRecoveryEnabled = true,
                ClientProvidedName = "inventory-hold-api"
            };

            _connection = await factory.CreateConnectionAsync(cancellationToken);
            _channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);

            await DeclareTopologyAsync(_channel, cancellationToken);
            logger.LogInformation("Connected to RabbitMQ; exchange {Exchange} ready", _options.Exchange);

            return _channel;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Topic exchange with a hold.# audit queue and a dead-letter exchange, so a consumer that
    /// keeps rejecting a message parks it instead of spinning on it forever.
    /// </summary>
    private async Task DeclareTopologyAsync(IChannel channel, CancellationToken cancellationToken)
    {
        var deadLetter = $"{_options.Exchange}.dlx";

        await channel.ExchangeDeclareAsync(
            _options.Exchange, ExchangeType.Topic, durable: true, autoDelete: false,
            cancellationToken: cancellationToken);

        await channel.ExchangeDeclareAsync(
            deadLetter, ExchangeType.Topic, durable: true, autoDelete: false,
            cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(
            _options.AuditQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-dead-letter-exchange"] = deadLetter },
            cancellationToken: cancellationToken);

        await channel.QueueBindAsync(
            _options.AuditQueue, _options.Exchange, "hold.#", cancellationToken: cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null) await _channel.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
        _gate.Dispose();
    }
}
