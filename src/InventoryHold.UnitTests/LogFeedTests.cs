using System.Diagnostics;
using InventoryHold.Infrastructure.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace InventoryHold.UnitTests;

/// <summary>
/// The feed the UI reads is only as good as what reaches it. These tests pin the three things
/// that decide that: which categories are captured, which levels survive, and whether the
/// structured fields that make a line traceable actually arrive.
/// </summary>
[TestFixture]
public sealed class MongoLoggerProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 15, 0, 0, TimeSpan.Zero);

    private LogChannel _channel = null!;
    private FakeTimeProvider _clock = null!;

    [SetUp]
    public void SetUp()
    {
        _channel = new LogChannel();
        _clock = new FakeTimeProvider(Now);
    }

    private ILogger Logger(
        string category = "InventoryHold.Infrastructure.Mongo.MongoHoldRepository",
        LogLevel minimum = LogLevel.Debug)
    {
        var provider = new MongoLoggerProvider(_channel, _clock, minimum);
        provider.SetScopeProvider(new LoggerExternalScopeProvider());
        return provider.CreateLogger(category);
    }

    private List<LogEntry> Drain()
    {
        var entries = new List<LogEntry>();
        while (_channel.Reader.TryRead(out var entry)) entries.Add(entry);
        return entries;
    }

    [Test]
    public void ScopeValues_ReachTheFeed_SoALineCanBeTiedToItsHold()
    {
        // The regression this guards: a scope is a Dictionary, which is not an IReadOnlyList.
        // Matching only the narrower type drops every scope value in silence, and the feed loses
        // the one field that ties a repository line back to the hold it belongs to.
        var logger = Logger();

        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["HoldId"] = "01a03426-35d3-7976-97f3-6d56ee310da0",
            ["CustomerId"] = "cust-42"
        }))
        {
            logger.LogDebug("Deducted {Quantity} of {Sku}", 2, "SKU-1001");
        }

        var entry = Drain().Single();

        Assert.Multiple(() =>
        {
            Assert.That(entry.Properties, Is.Not.Null);
            Assert.That(entry.Properties!["HoldId"], Is.EqualTo("01a03426-35d3-7976-97f3-6d56ee310da0"));
            Assert.That(entry.Properties!["CustomerId"], Is.EqualTo("cust-42"));
            Assert.That(entry.Properties!["Sku"], Is.EqualTo("SKU-1001"), "line values arrive too");
            Assert.That(entry.Message, Is.EqualTo("Deducted 2 of SKU-1001"));
        });
    }

    [Test]
    public void AValueOnTheLine_WinsOverTheSameKeyInTheScope()
    {
        var logger = Logger();

        using (logger.BeginScope(new Dictionary<string, object> { ["Sku"] = "SKU-OUTER" }))
        {
            logger.LogInformation("Restored {Sku}", "SKU-INNER");
        }

        Assert.That(Drain().Single().Properties!["Sku"], Is.EqualTo("SKU-INNER"));
    }

    [Test]
    public void ScopeValues_DoNotLeakOutOfTheirScope()
    {
        var logger = Logger();

        using (logger.BeginScope(new Dictionary<string, object> { ["HoldId"] = "h-1" }))
        {
            logger.LogDebug("inside");
        }
        logger.LogDebug("outside");

        var entries = Drain();

        Assert.Multiple(() =>
        {
            Assert.That(entries[0].Properties!["HoldId"], Is.EqualTo("h-1"));
            Assert.That(entries[1].Properties, Is.Null);
        });
    }

    [Test]
    public void Debug_IsCaptured_BecauseTracingIsThePointOfTheFeed()
    {
        Logger(minimum: LogLevel.Debug).LogDebug("Cache INVALIDATE {Key}", "inventory:all");

        Assert.That(Drain().Single().Level, Is.EqualTo("Debug"));
    }

    [Test]
    public void TheDefaultFloor_ExcludesTrace_SoPollingDoesNotFillTheFeed()
    {
        // Read-path chatter - the dashboard polling holds and inventory every couple of seconds -
        // is logged at Trace precisely so the default Debug floor leaves it out.
        var logger = Logger(minimum: LogLevel.Debug);

        logger.LogTrace("Queried active holds: 6 row(s)");
        logger.LogDebug("Deducted 2 of SKU-1001");

        var entries = Drain();

        Assert.Multiple(() =>
        {
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].Message, Is.EqualTo("Deducted 2 of SKU-1001"));
        });
    }

    [Test]
    public void LoweringTheFloorToTrace_LetsThePollingBackIn()
    {
        Logger(minimum: LogLevel.Trace).LogTrace("Queried active holds: 6 row(s)");

        Assert.That(Drain().Single().Level, Is.EqualTo("Trace"));
    }

    [Test]
    public void RaisingTheFloor_DropsTheChattierLevels()
    {
        var logger = Logger(minimum: LogLevel.Information);

        logger.LogDebug("chatty");
        logger.LogInformation("worth keeping");

        var entries = Drain();

        Assert.Multiple(() =>
        {
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].Message, Is.EqualTo("worth keeping"));
        });
    }

    [TestCase("Microsoft.AspNetCore.Hosting.Diagnostics")]
    [TestCase("MongoDB.Driver.Core.Connections")]
    public void ForeignCategories_AreNeverCaptured(string category)
    {
        // Driver and framework diagnostics echo connection strings, and this feed is served
        // over an unauthenticated endpoint.
        Logger(category).LogError("connection string {Uri}", "mongodb://user:secret@host");

        Assert.That(Drain(), Is.Empty);
    }

    [TestCase("InventoryHold.Domain.Services.HoldService")]
    [TestCase("Http")]
    public void OwnedCategories_AreCaptured_WithTheFullCategoryPreserved(string category)
    {
        // Stored in full. Shortening is a display concern, and doing it at write time throws the
        // namespace away permanently - which is the part that says where in the codebase a line
        // came from when two classes share a name.
        Logger(category).LogInformation("something happened");

        Assert.That(Drain().Single().Category, Is.EqualTo(category));
    }

    [Test]
    public void AnEventId_IsCarried_SoALineNamesItsExactCallSite()
    {
        Logger().Log(
            LogLevel.Information, new EventId(42, "StockDeducted"), "state", null, (s, _) => s);

        var entry = Drain().Single();

        Assert.Multiple(() =>
        {
            Assert.That(entry.EventId, Is.EqualTo(42));
            Assert.That(entry.EventName, Is.EqualTo("StockDeducted"));
        });
    }

    [Test]
    public void TheSpanId_IsCapturedAlongsideTheTraceId()
    {
        using var activity = new Activity("hold-create").Start();

        Logger().LogInformation("inside an activity");

        var entry = Drain().Single();

        Assert.Multiple(() =>
        {
            Assert.That(entry.TraceId, Is.EqualTo(activity.TraceId.ToString()));
            Assert.That(entry.SpanId, Is.EqualTo(activity.SpanId.ToString()));
        });
    }

    [Test]
    public void Timestamps_ComeFromTheInjectedClock_NotTheWallClock()
    {
        Logger().LogInformation("stamped");

        Assert.That(Drain().Single().Timestamp, Is.EqualTo(Now.UtcDateTime));
    }

    [Test]
    public void Exceptions_AreCarried_SoAFailureIsDiagnosableFromTheFeedAlone()
    {
        Logger().LogError(new InvalidOperationException("redis down"), "Cache write failed");

        var entry = Drain().Single();

        Assert.Multiple(() =>
        {
            Assert.That(entry.Level, Is.EqualTo("Error"));
            Assert.That(entry.Exception, Does.Contain("redis down"));
        });
    }
}

/// <summary>
/// The feed shows a bare trace id; a ProblemDetails error body returns the whole traceparent.
/// Pasting the one from a failed response into the filter has to find the request it describes.
/// </summary>
[TestFixture]
public sealed class TraceIdNormalisationTests
{
    [Test]
    public void AFullTraceparent_IsReducedToItsTraceId()
    {
        var normalised = MongoLogStore.NormaliseTraceId(
            "00-90bf9570e9db63b92d853bd6752aa07a-1db2c2966106889f-00");

        Assert.That(normalised, Is.EqualTo("90bf9570e9db63b92d853bd6752aa07a"));
    }

    [Test]
    public void ABareTraceId_IsLeftAlone()
    {
        const string bare = "90bf9570e9db63b92d853bd6752aa07a";

        Assert.That(MongoLogStore.NormaliseTraceId(bare), Is.EqualTo(bare));
    }

    [TestCase("not-a-trace")]
    [TestCase("00-short-1db2c2966106889f-00")]
    public void AnythingElse_IsPassedThroughUnchanged(string value)
    {
        // Better to return it unchanged than to guess: an unrecognised value simply matches no rows.
        Assert.That(MongoLogStore.NormaliseTraceId(value), Is.EqualTo(value));
    }
}
