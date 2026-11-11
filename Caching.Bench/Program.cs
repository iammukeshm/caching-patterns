using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

// Real runs for the "10 Caching Patterns" video.
// Postgres 17 on localhost:5440, Redis 8 on localhost:6390 (see README.md).
const string PgConn = "Host=localhost;Port=5440;Username=postgres;Password=postgres;Database=postgres;Maximum Pool Size=100";
const string RedisConn = "localhost:6390";

await using var db = NpgsqlDataSource.Create(PgConn);
await Seed.EnsureAsync(db);

var services = new ServiceCollection();
services.AddStackExchangeRedisCache(o => o.Configuration = RedisConn);
services.AddHybridCache(o => o.DefaultEntryOptions = new HybridCacheEntryOptions
{
    Expiration = TimeSpan.FromMinutes(5),
    LocalCacheExpiration = TimeSpan.FromMinutes(5)
});
await using var sp = services.BuildServiceProvider();
var redis = sp.GetRequiredService<IDistributedCache>();
var hybrid = sp.GetRequiredService<HybridCache>();

var repo = new ProductRepository(db);
const int productId = 4242;

Console.WriteLine($"Run date: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC | .NET {Environment.Version} | {Environment.ProcessorCount} cores");
Console.WriteLine();

// ---------- RUN 1: latency, one product page read ----------
Console.WriteLine("RUN 1: latency of one product page read (1,000 sequential reads, after 100 warm-up)");
await repo.GetProductAsync(productId);
await redis.SetStringAsync($"aside:product:{productId}", JsonSerializer.Serialize(await repo.GetProductAsync(productId)));
await hybrid.GetOrCreateAsync($"product:{productId}", async ct => await repo.GetProductAsync(productId, ct));

await Latency.MeasureAsync("Database (no cache)", () => repo.GetProductAsync(productId));
await Latency.MeasureAsync("Redis (distributed cache hit)", async () =>
    JsonSerializer.Deserialize<Product>((await redis.GetStringAsync($"aside:product:{productId}"))!)!);
await Latency.MeasureAsync("HybridCache (in-memory L1 hit)", async () =>
    await hybrid.GetOrCreateAsync($"product:{productId}", async ct => await repo.GetProductAsync(productId, ct)));
Console.WriteLine();

// ---------- RUN 2: stampede, 1,000 concurrent requests on an empty key ----------
const int concurrent = 1000;
Console.WriteLine($"RUN 2: cache stampede, {concurrent:N0} concurrent requests for one product whose cache entry just expired (5 rounds)");

for (var round = 1; round <= 5; round++)
{
    // Plain cache-aside with Redis: check, miss, query, store.
    var key = $"stampede:aside:{round}:{Guid.NewGuid()}";
    repo.ResetCount();
    var sw = Stopwatch.StartNew();
    await Task.WhenAll(Enumerable.Range(0, concurrent).Select(_ => Task.Run(async () =>
    {
        var cached = await redis.GetStringAsync(key);
        if (cached is not null) return JsonSerializer.Deserialize<Product>(cached)!;
        var product = await repo.GetProductAsync(productId);
        await redis.SetStringAsync(key, JsonSerializer.Serialize(product),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });
        return product;
    })));
    sw.Stop();
    var asideQueries = repo.Count;
    var asideMs = sw.ElapsedMilliseconds;

    // HybridCache: same request shape, one factory call per key per process.
    var hkey = $"stampede:hybrid:{round}:{Guid.NewGuid()}";
    repo.ResetCount();
    sw.Restart();
    await Task.WhenAll(Enumerable.Range(0, concurrent).Select(_ => Task.Run(async () =>
        await hybrid.GetOrCreateAsync(hkey, async ct => await repo.GetProductAsync(productId, ct)))));
    sw.Stop();

    Console.WriteLine($"  Round {round}: cache-aside -> {asideQueries,4} DB queries in {asideMs,5} ms | HybridCache -> {repo.Count,4} DB queries in {sw.ElapsedMilliseconds,5} ms");
}

public sealed record Product(int Id, string Name, decimal Price, int ReviewCount, double AverageRating);

public sealed class ProductRepository(NpgsqlDataSource db)
{
    private int _count;
    public int Count => _count;
    public void ResetCount() => Interlocked.Exchange(ref _count, 0);

    // The product page query: the product plus its review summary.
    public async Task<Product> GetProductAsync(int id, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _count);
        await using var cmd = db.CreateCommand("""
            SELECT p.id, p.name, p.price, COUNT(r.id), COALESCE(AVG(r.rating), 0)
            FROM products p
            LEFT JOIN reviews r ON r.product_id = p.id
            WHERE p.id = $1
            GROUP BY p.id, p.name, p.price
            """);
        cmd.Parameters.AddWithValue(id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new Product(reader.GetInt32(0), reader.GetString(1), reader.GetDecimal(2),
            (int)reader.GetInt64(3), (double)reader.GetDecimal(4));
    }
}

public static class Latency
{
    public static async Task MeasureAsync<T>(string label, Func<Task<T>> read)
    {
        for (var i = 0; i < 100; i++) await read();
        var samples = new double[1000];
        for (var i = 0; i < samples.Length; i++)
        {
            var start = Stopwatch.GetTimestamp();
            await read();
            samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        Array.Sort(samples);
        Console.WriteLine($"  {label,-32} median {samples[500],8:0.0000} ms | p95 {samples[950],8:0.0000} ms");
    }
}

public static class Seed
{
    // 100,000 products and 2,000,000 reviews, with an index on reviews.product_id.
    public static async Task EnsureAsync(NpgsqlDataSource db)
    {
        await using var check = db.CreateCommand("SELECT to_regclass('public.reviews') IS NOT NULL");
        if ((bool)(await check.ExecuteScalarAsync())!) return;

        Console.WriteLine("Seeding 100,000 products and 2,000,000 reviews...");
        await using var cmd = db.CreateCommand("""
            CREATE TABLE products (id int PRIMARY KEY, name text NOT NULL, price numeric(10,2) NOT NULL);
            CREATE TABLE reviews (id bigserial PRIMARY KEY, product_id int NOT NULL REFERENCES products(id), rating int NOT NULL, body text NOT NULL);
            INSERT INTO products SELECT g, 'Product ' || g, (random() * 500)::numeric(10,2) FROM generate_series(1, 100000) g;
            INSERT INTO reviews (product_id, rating, body)
              SELECT (random() * 99999)::int + 1, (random() * 4)::int + 1, md5(g::text) FROM generate_series(1, 2000000) g;
            CREATE INDEX ix_reviews_product_id ON reviews(product_id);
            ANALYZE;
            """);
        cmd.CommandTimeout = 600;
        await cmd.ExecuteNonQueryAsync();
    }
}
