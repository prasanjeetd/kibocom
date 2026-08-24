using InventoryHold.Contracts;
using InventoryHold.Domain.Entities;
using InventoryHold.Domain.Events;
using InventoryHold.Domain.Exceptions;
using InventoryHold.Domain.Repositories;
using InventoryHold.Domain.Services;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace InventoryHold.UnitTests;

/// <summary>
/// Lifecycle orchestration with every port mocked. These tests require no MongoDB, no Redis and
/// no RabbitMQ - which is the point of defining the ports in the domain in the first place.
/// </summary>
[TestFixture]
public sealed class HoldServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 15, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    private IHoldRepository _holds = null!;
    private IInventoryRepository _inventory = null!;
    private IEventPublisher _events = null!;
    private ICacheService _cache = null!;
    private FakeTimeProvider _clock = null!;
    private HoldService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _holds = Substitute.For<IHoldRepository>();
        _inventory = Substitute.For<IInventoryRepository>();
        _events = Substitute.For<IEventPublisher>();
        _cache = Substitute.For<ICacheService>();
        _clock = new FakeTimeProvider(Now);

        _sut = new HoldService(_holds, _inventory, _events, _cache, _clock, new HoldPolicy(Ttl));
    }

    private void GivenProduct(string sku = "SKU-1001", int available = 25)
        => _inventory.GetBySkuAsync(sku, Arg.Any<CancellationToken>())
            .Returns(new InventoryItem(sku, "Aeron Chair", 25, available));

    private static CreateHoldRequest Request(string sku = "SKU-1001", int quantity = 2) => new()
    {
        CustomerId = "cust-42",
        Items = [new CreateHoldItem { Sku = sku, Quantity = quantity }]
    };

    private static Hold ActiveHold(DateTimeOffset createdAt, TimeSpan ttl) => Hold.Rehydrate(
        Guid.CreateVersion7(), "cust-42", [new HoldItem("SKU-1001", 2, "Aeron Chair")],
        HoldStatus.Active, createdAt, createdAt.Add(ttl), null);

    // ---------------------------------------------------------------- create

    [Test]
    public async Task Create_OnSuccess_DeductsStock_InvalidatesCache_AndPublishes()
    {
        GivenProduct();

        var response = await _sut.CreateAsync(Request());

        Assert.Multiple(() =>
        {
            Assert.That(response.Status, Is.EqualTo(HoldStatus.Active));
            Assert.That(response.ExpiresAt, Is.EqualTo(Now.Add(Ttl)));
            Assert.That(response.SecondsRemaining, Is.EqualTo(900));
            Assert.That(response.Items.Single().Name, Is.EqualTo("Aeron Chair"),
                "the product name is snapshotted so the holds list needs no join");
        });

        await _holds.Received(1).CreateWithStockDeductionAsync(
            Arg.Any<Hold>(), Arg.Any<CancellationToken>());
        await _cache.Received(1).RemoveAsync(CacheKeys.AllInventory, Arg.Any<CancellationToken>());
        await _events.Received(1).PublishAsync(
            Arg.Is<HoldEvent>(e => e.EventType == HoldEvent.Created), Arg.Any<CancellationToken>());
    }

    [Test]
    public void Create_WithUnknownSku_IsRejectedBeforeAnyStockIsTouched()
    {
        _inventory.GetBySkuAsync("SKU-NOPE", Arg.Any<CancellationToken>())
            .Returns((InventoryItem?)null);

        Assert.That(async () => await _sut.CreateAsync(Request("SKU-NOPE")),
            Throws.TypeOf<UnknownSkuException>());

        _holds.DidNotReceiveWithAnyArgs().CreateWithStockDeductionAsync(default!, default);
    }

    [Test]
    public async Task Create_WhenStockRunsOut_PublishesNothingAndLeavesTheCacheAlone()
    {
        // The guarded deduction lost its race, so nothing changed - announcing a HoldCreated
        // here would tell downstream systems about a hold that does not exist.
        GivenProduct();
        _holds.CreateWithStockDeductionAsync(Arg.Any<Hold>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InsufficientStockException("SKU-1001", 2, 1));

        Assert.That(async () => await _sut.CreateAsync(Request()),
            Throws.TypeOf<InsufficientStockException>()
                .With.Property(nameof(InsufficientStockException.Available)).EqualTo(1));

        await _events.DidNotReceiveWithAnyArgs().PublishAsync(default!, default);
        await _cache.DidNotReceiveWithAnyArgs().RemoveAsync(default!, default);
    }

    // ------------------------------------------------------------------- get

    [Test]
    public void Get_WhenTheHoldNeverExisted_IsNotFound()
    {
        _holds.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Hold?)null);

        Assert.That(async () => await _sut.GetAsync(Guid.CreateVersion7()),
            Throws.TypeOf<HoldNotFoundException>());
    }

    [Test]
    public async Task Get_PastTheDeadline_ReportsExpired_WithoutWaitingForTheSweeper()
    {
        var hold = ActiveHold(Now, Ttl);
        _holds.GetAsync(hold.Id, Arg.Any<CancellationToken>()).Returns(hold);

        _clock.Advance(TimeSpan.FromMinutes(16));
        var response = await _sut.GetAsync(hold.Id);

        Assert.Multiple(() =>
        {
            Assert.That(response.Status, Is.EqualTo(HoldStatus.Expired));
            Assert.That(response.SecondsRemaining, Is.Zero);
        });
    }

    // --------------------------------------------------------------- release

    [Test]
    public async Task Release_OnSuccess_RestoresStock_InvalidatesCache_AndPublishes()
    {
        var hold = ActiveHold(Now, Ttl);
        _holds.GetAsync(hold.Id, Arg.Any<CancellationToken>()).Returns(hold);
        _holds.ReleaseAndRestoreStockAsync(hold.Id, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Hold.Rehydrate(hold.Id, hold.CustomerId, hold.Items,
                HoldStatus.Released, hold.CreatedAt, hold.ExpiresAt, Now));

        var response = await _sut.ReleaseAsync(hold.Id);

        Assert.That(response.Status, Is.EqualTo(HoldStatus.Released));
        await _cache.Received(1).RemoveAsync(CacheKeys.AllInventory, Arg.Any<CancellationToken>());
        await _events.Received(1).PublishAsync(
            Arg.Is<HoldEvent>(e => e.EventType == HoldEvent.Released), Arg.Any<CancellationToken>());
    }

    [Test]
    public void Release_OnAHoldThatNeverExisted_IsNotFound()
    {
        _holds.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Hold?)null);

        Assert.That(async () => await _sut.ReleaseAsync(Guid.CreateVersion7()),
            Throws.TypeOf<HoldNotFoundException>());
    }

    [Test]
    public void Release_OnAnAlreadyReleasedHold_IsAConflict()
    {
        var hold = Hold.Rehydrate(Guid.CreateVersion7(), "cust-42",
            [new HoldItem("SKU-1001", 2, "Aeron Chair")],
            HoldStatus.Released, Now, Now.Add(Ttl), Now);

        _holds.GetAsync(hold.Id, Arg.Any<CancellationToken>()).Returns(hold);

        Assert.That(async () => await _sut.ReleaseAsync(hold.Id),
            Throws.TypeOf<HoldNotActiveException>());

        _holds.DidNotReceiveWithAnyArgs().ReleaseAndRestoreStockAsync(default, default, default);
    }

    /// <summary>
    /// The sharpest race in the system. A customer hits Release at the same instant the sweeper
    /// expires the hold. Both paths restore stock, so if both succeeded the inventory invariant
    /// would break silently. The repository claim is a compare-and-swap: the loser gets null,
    /// and must not publish an event or restore stock a second time.
    /// </summary>
    [Test]
    public async Task Release_WhenTheSweeperWinsTheRace_IsAConflict_AndRestoresStockOnlyOnce()
    {
        var hold = ActiveHold(Now, Ttl);

        // Read says Active...
        _holds.GetAsync(hold.Id, Arg.Any<CancellationToken>()).Returns(
            hold,
            Hold.Rehydrate(hold.Id, hold.CustomerId, hold.Items,
                HoldStatus.Expired, hold.CreatedAt, hold.ExpiresAt, Now));

        // ...but by the time we claim it, the sweeper already has it: no rows matched.
        _holds.ReleaseAndRestoreStockAsync(hold.Id, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns((Hold?)null);

        Assert.That(async () => await _sut.ReleaseAsync(hold.Id),
            Throws.TypeOf<HoldNotActiveException>()
                .With.Property(nameof(HoldNotActiveException.Status)).EqualTo(HoldStatus.Expired));

        await _events.DidNotReceiveWithAnyArgs().PublishAsync(default!, default);
        await _cache.DidNotReceiveWithAnyArgs().RemoveAsync(default!, default);
    }
}
