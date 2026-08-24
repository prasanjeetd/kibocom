using InventoryHold.Domain.Entities;
using InventoryHold.Domain.Repositories;
using InventoryHold.Infrastructure.Options;
using InventoryHold.Infrastructure.Redis;
using Microsoft.Extensions.Options;
using NetArchTest.Rules;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace InventoryHold.UnitTests;

[TestFixture]
public sealed class CachedInventoryRepositoryTests
{
    private IInventoryRepository _inner = null!;
    private ICacheService _cache = null!;
    private CachedInventoryRepository _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _inner = Substitute.For<IInventoryRepository>();
        _cache = Substitute.For<ICacheService>();
        _sut = new CachedInventoryRepository(
            _inner, _cache, Options.Create(new RedisOptions { InventoryTtlSeconds = 30 }));
    }

    private static InventoryItem Chair(int available = 18)
        => new("SKU-1001", "Aeron Chair", 25, available);

    [Test]
    public async Task GetAll_OnACacheHit_NeverTouchesTheDatabase()
    {
        _cache.GetAsync<CachedInventory>(CacheKeys.AllInventory, Arg.Any<CancellationToken>())
            .Returns(new CachedInventory([new CachedInventoryItem("SKU-1001", "Aeron Chair", 25, 18)]));

        var result = await _sut.GetAllAsync();

        Assert.That(result.Single().AvailableQuantity, Is.EqualTo(18));
        await _inner.DidNotReceiveWithAnyArgs().GetAllAsync(default);
    }

    [Test]
    public async Task GetAll_OnACacheMiss_ReadsThroughAndPopulatesTheCache()
    {
        _cache.GetAsync<CachedInventory>(CacheKeys.AllInventory, Arg.Any<CancellationToken>())
            .Returns((CachedInventory?)null);
        _inner.GetAllAsync(Arg.Any<CancellationToken>()).Returns([Chair()]);

        var result = await _sut.GetAllAsync();

        Assert.That(result.Single().Sku, Is.EqualTo("SKU-1001"));
        await _cache.Received(1).SetAsync(
            CacheKeys.AllInventory, Arg.Any<CachedInventory>(),
            TimeSpan.FromSeconds(30), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetAll_WhenRedisIsDown_StillServesFromTheDatabase()
    {
        // Fail open. A cache outage must degrade latency, never availability.
        _cache.GetAsync<CachedInventory>(CacheKeys.AllInventory, Arg.Any<CancellationToken>())
            .Returns((CachedInventory?)null);
        _cache.SetAsync(Arg.Any<string>(), Arg.Any<CachedInventory>(), Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("redis down"));
        _inner.GetAllAsync(Arg.Any<CancellationToken>()).Returns([Chair()]);

        Assert.That(async () => await _sut.GetAllAsync(), Throws.Nothing);
    }

    [Test]
    public async Task GetBySku_IsNeverCached_BecauseItFeedsTheDeductionDecision()
    {
        _inner.GetBySkuAsync("SKU-1001", Arg.Any<CancellationToken>()).Returns(Chair());

        await _sut.GetBySkuAsync("SKU-1001");

        await _inner.Received(1).GetBySkuAsync("SKU-1001", Arg.Any<CancellationToken>());
        await _cache.DidNotReceiveWithAnyArgs().GetAsync<CachedInventory>(default!, default);
    }
}

/// <summary>
/// Makes the layering claim enforceable rather than aspirational: if anyone ever imports a driver
/// type into the domain, the build fails here instead of in code review.
/// </summary>
[TestFixture]
public sealed class ArchitectureTests
{
    [Test]
    public void Domain_DependsOnNoInfrastructureTechnology()
    {
        var result = Types.InAssembly(typeof(Hold).Assembly)
            .Should()
            .NotHaveDependencyOnAny("MongoDB", "StackExchange.Redis", "RabbitMQ", "Microsoft.AspNetCore")
            .GetResult();

        Assert.That(result.IsSuccessful, Is.True,
            $"Domain leaked infrastructure types: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Test]
    public void Contracts_DependOnNothingButTheFramework()
    {
        var result = Types.InAssembly(typeof(Contracts.HoldStatus).Assembly)
            .Should()
            .NotHaveDependencyOnAny("MongoDB", "StackExchange.Redis", "RabbitMQ", "InventoryHold.Domain")
            .GetResult();

        Assert.That(result.IsSuccessful, Is.True,
            $"Contracts leaked dependencies: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }
}
