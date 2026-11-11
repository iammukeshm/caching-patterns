using Microsoft.Extensions.Caching.Hybrid;
using Npgsql;

// The code shown on screen in the video. Compiles with the bench; not called by Program.cs.
public sealed class ProductService(HybridCache cache, ProductRepository repo, NpgsqlDataSource db)
{
    private static readonly HybridCacheEntryOptions Options = new()
    {
        Expiration = TimeSpan.FromMinutes(5),          // Redis (L2)
        LocalCacheExpiration = TimeSpan.FromMinutes(1) // in-memory (L1)
    };

    // Cache-aside through HybridCache: one call, one factory run per key per process.
    public ValueTask<Product> GetAsync(int id, CancellationToken ct = default) =>
        cache.GetOrCreateAsync(
            $"product:{id}",
            async token => await repo.GetProductAsync(id, token),
            Options,
            tags: ["products"],
            cancellationToken: ct);

    // Write-through: save to the database, then put the new value in the cache.
    public async Task UpdatePriceWriteThroughAsync(int id, decimal price, CancellationToken ct = default)
    {
        await SavePriceAsync(id, price, ct);
        var updated = await repo.GetProductAsync(id, ct);
        await cache.SetAsync($"product:{id}", updated, Options, tags: ["products"], cancellationToken: ct);
    }

    // Invalidation: save to the database, then remove the cached copy.
    public async Task UpdatePriceAndInvalidateAsync(int id, decimal price, CancellationToken ct = default)
    {
        await SavePriceAsync(id, price, ct);
        await cache.RemoveAsync($"product:{id}", ct);
    }

    // A bulk price change: drop every product entry at once.
    public ValueTask InvalidateAllProductsAsync(CancellationToken ct = default) =>
        cache.RemoveByTagAsync("products", ct);

    private async Task SavePriceAsync(int id, decimal price, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("UPDATE products SET price = $1 WHERE id = $2");
        cmd.Parameters.AddWithValue(price);
        cmd.Parameters.AddWithValue(id);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
