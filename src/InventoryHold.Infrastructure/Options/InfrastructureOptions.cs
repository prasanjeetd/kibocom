namespace InventoryHold.Infrastructure.Options;

public sealed class MongoOptions
{
    public const string SectionName = "Mongo";

    public string ConnectionString { get; set; } = "mongodb://localhost:27017/?replicaSet=rs0";
    public string Database { get; set; } = "inventoryhold";

    /// <summary>
    /// Multi-document transactions require a replica set. Compose runs one; Atlas M0 is one.
    /// Set false only when pointing at a standalone server, which falls back to compensating
    /// rollback - correct, but with a crash window (see ADR-002).
    /// </summary>
    public bool UseTransactions { get; set; } = true;
}

public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    public string ConnectionString { get; set; } = "localhost:6379";
    public int InventoryTtlSeconds { get; set; } = 30;
    public bool Enabled { get; set; } = true;
}

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string Uri { get; set; } = "amqp://guest:guest@localhost:5672/";
    public string Exchange { get; set; } = "inventory.holds";
    public string AuditQueue { get; set; } = "inventory.holds.audit";
    public bool Enabled { get; set; } = true;
}

public sealed class HoldOptions
{
    public const string SectionName = "Hold";

    /// <summary>Hold lifetime in minutes. Fractional values are allowed so demos can use seconds.</summary>
    public double ExpirationMinutes { get; set; } = 15;

    public int SweeperIntervalSeconds { get; set; } = 15;
    public int SweeperBatchSize { get; set; } = 100;
}
