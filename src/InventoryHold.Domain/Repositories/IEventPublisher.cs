using InventoryHold.Domain.Events;

namespace InventoryHold.Domain.Repositories;

public interface IEventPublisher
{
    Task PublishAsync(HoldEvent domainEvent, CancellationToken cancellationToken = default);
}
