using InventoryHold.Infrastructure.Options;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;

namespace InventoryHold.Infrastructure.Mongo;

public sealed class MongoContext
{
    public IMongoClient Client { get; }
    public IMongoDatabase Database { get; }
    public IMongoCollection<InventoryDocument> Inventory { get; }
    public IMongoCollection<HoldDocument> Holds { get; }
    public bool UseTransactions { get; }

    static MongoContext()
    {
        // Driver 3.x requires an explicit GUID representation.
        BsonSerializer.TryRegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));
    }

    public MongoContext(IOptions<MongoOptions> options)
    {
        var settings = options.Value;
        Client = new MongoClient(settings.ConnectionString);
        Database = Client.GetDatabase(settings.Database);
        Inventory = Database.GetCollection<InventoryDocument>("inventory");
        Holds = Database.GetCollection<HoldDocument>("holds");
        UseTransactions = settings.UseTransactions;
    }

    /// <summary>Indexes the sweeper depends on. Without this its query is a collection scan.</summary>
    public async Task EnsureIndexesAsync(CancellationToken cancellationToken = default)
    {
        var byStatusAndExpiry = new CreateIndexModel<HoldDocument>(
            Builders<HoldDocument>.IndexKeys.Ascending(h => h.Status).Ascending(h => h.ExpiresAt),
            new CreateIndexOptions { Name = "status_expiresAt" });

        await Holds.Indexes.CreateOneAsync(byStatusAndExpiry, cancellationToken: cancellationToken);
    }
}
