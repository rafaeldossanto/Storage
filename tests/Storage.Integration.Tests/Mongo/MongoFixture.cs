using MongoDB.Driver;
using Storage.Infrastructure.Persistence;
using Testcontainers.MongoDb;

[assembly: AssemblyFixture(typeof(Storage.Integration.Tests.Mongo.MongoFixture))]

namespace Storage.Integration.Tests.Mongo;

/// <summary>
/// One disposable MongoDB for the whole test run, started in a container.
/// </summary>
/// <remarks>
/// A replica set, like production: without one, multi-document transactions do not exist
/// and a test would pass here while the real thing fails. The version is pinned so a new
/// MongoDB release never changes the result of an old commit. Each test gets a database of
/// its own, which isolates tests without paying for a container per test.
/// </remarks>
public sealed class MongoFixture : IAsyncLifetime
{
    private readonly MongoDbContainer _container = new MongoDbBuilder("mongo:8.0")
        .WithReplicaSet()
        .Build();

    private MongoClient? _client;

    public MongoClient Client => _client ?? throw new InvalidOperationException("The container has not started.");

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        _client = new MongoClient(_container.GetConnectionString());
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        await _container.DisposeAsync();
    }

    /// <summary>A fresh, empty database with every index the application creates at start-up.</summary>
    public async Task<MongoStorageContext> NewDatabaseAsync(CancellationToken cancellationToken)
    {
        var context = new MongoStorageContext(Client, $"test_{Guid.CreateVersion7():N}");
        await context.EnsureIndexesAsync(cancellationToken);
        return context;
    }
}

internal sealed class FixedTenant(Guid tenantId) : Storage.Application.Abstractions.ITenantContext
{
    public Guid TenantId { get; } = tenantId;
}

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
