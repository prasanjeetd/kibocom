using InventoryHold.Contracts;
using InventoryHold.Domain.Entities;
using InventoryHold.Domain.Exceptions;

namespace InventoryHold.UnitTests;

/// <summary>
/// Pure domain rules. No mocks needed: an invalid Hold must be impossible to construct.
/// </summary>
[TestFixture]
public sealed class HoldEntityTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 15, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    private static HoldItem Chair(int quantity = 1) => new("SKU-1001", quantity, "Aeron Chair");

    [Test]
    public void Create_WithNoItems_IsRejected()
    {
        var act = () => Hold.Create("cust-1", [], Now, Ttl);

        Assert.That(act, Throws.TypeOf<InvalidHoldRequestException>()
            .With.Message.Contains("at least one item"));
    }

    [Test]
    public void Create_WithDuplicateSku_IsRejected()
    {
        // Two lines for the same product would deduct twice and confuse release.
        var act = () => Hold.Create("cust-1", [Chair(), Chair(2)], Now, Ttl);

        Assert.That(act, Throws.TypeOf<InvalidHoldRequestException>()
            .With.Message.Contains("more than once"));
    }

    [TestCase(0)]
    [TestCase(-3)]
    public void HoldItem_WithNonPositiveQuantity_IsRejected(int quantity)
    {
        var act = () => new HoldItem("SKU-1001", quantity);

        Assert.That(act, Throws.TypeOf<InvalidHoldRequestException>());
    }

    [Test]
    public void Create_WithoutCustomer_IsRejected()
    {
        var act = () => Hold.Create("  ", [Chair()], Now, Ttl);

        Assert.That(act, Throws.TypeOf<InvalidHoldRequestException>());
    }

    [Test]
    public void Create_DerivesExpiryFromTheConfiguredLifetime()
    {
        var hold = Hold.Create("cust-1", [Chair()], Now, Ttl);

        Assert.Multiple(() =>
        {
            Assert.That(hold.Status, Is.EqualTo(HoldStatus.Active));
            Assert.That(hold.ExpiresAt, Is.EqualTo(Now.Add(Ttl)));
            Assert.That(hold.ResolvedAt, Is.Null);
            Assert.That(hold.Id, Is.Not.EqualTo(Guid.Empty));
        });
    }

    [Test]
    public void StatusAt_PastTheDeadline_ReportsExpired_EvenWhileStorageStillSaysActive()
    {
        // Lazy expiry: the sweeper may not have run yet, but a read must never claim
        // a timed-out hold is still Active.
        var hold = Hold.Create("cust-1", [Chair()], Now, Ttl);

        Assert.Multiple(() =>
        {
            Assert.That(hold.Status, Is.EqualTo(HoldStatus.Active), "stored state is untouched");
            Assert.That(hold.StatusAt(Now.AddMinutes(16)), Is.EqualTo(HoldStatus.Expired));
            Assert.That(hold.StatusAt(Now.AddMinutes(14)), Is.EqualTo(HoldStatus.Active));
        });
    }

    [Test]
    public void StatusAt_ExactlyOnTheDeadline_IsExpired()
    {
        var hold = Hold.Create("cust-1", [Chair()], Now, Ttl);

        Assert.That(hold.StatusAt(hold.ExpiresAt), Is.EqualTo(HoldStatus.Expired));
    }

    [Test]
    public void TimeRemaining_CountsDown_ThenClampsToZero()
    {
        var hold = Hold.Create("cust-1", [Chair()], Now, Ttl);

        Assert.Multiple(() =>
        {
            Assert.That(hold.TimeRemainingAt(Now.AddMinutes(5)), Is.EqualTo(TimeSpan.FromMinutes(10)));
            Assert.That(hold.TimeRemainingAt(Now.AddMinutes(99)), Is.EqualTo(TimeSpan.Zero));
        });
    }

    [Test]
    public void EnsureReleasable_OnAnExpiredHold_ReportsItAsNoLongerActive()
    {
        var hold = Hold.Create("cust-1", [Chair()], Now, Ttl);

        var act = () => hold.EnsureReleasableAt(Now.AddMinutes(16));

        Assert.That(act, Throws.TypeOf<HoldNotActiveException>()
            .With.Property(nameof(HoldNotActiveException.Status)).EqualTo(HoldStatus.Expired));
    }

    [Test]
    public void InventoryItem_DerivesHeldQuantityFromTheInvariant()
    {
        var item = new InventoryItem("SKU-1001", "Aeron Chair", totalQuantity: 25, availableQuantity: 18);

        Assert.Multiple(() =>
        {
            Assert.That(item.HeldQuantity, Is.EqualTo(7));
            Assert.That(item.CanSatisfy(18), Is.True);
            Assert.That(item.CanSatisfy(19), Is.False);
        });
    }
}
