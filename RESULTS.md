# RESULTS - caching-patterns (run 2026-11-10 11:46 UTC)

Machine: Windows 11, 24 cores, .NET 10.0.14 (SDK 10.0.403). Postgres 17 and Redis 8 in Docker on the same machine (localhost, no network hop).
Data: 100,000 products, 2,000,000 reviews, index on reviews.product_id. Query = one product + review count + average rating.
Packages: Npgsql 10.0.3, Microsoft.Extensions.Caching.Hybrid 10.11.0, Microsoft.Extensions.Caching.StackExchangeRedis 10.0.14.

## Run 1 - latency of one product page read (1,000 sequential reads after 100 warm-up)

| Source | Median | p95 |
|---|---|---|
| Database (no cache) | 0.4155 ms | 0.5697 ms |
| Redis (distributed cache hit) | 0.2541 ms | 0.3976 ms |
| HybridCache in-memory L1 hit | 0.0047 ms | 0.0072 ms |

Read: L1 memory hit is ~88x faster than the DB read here. Redis is only ~1.6x faster than this local, indexed DB query because both are a network round-trip on localhost. In production the DB is usually a separate server and the query heavier, so the gap is bigger; this run does NOT measure that.

## Run 2 - stampede: 1,000 concurrent requests for one product whose cache entry just expired (5 rounds)

| Round | Plain cache-aside (Redis) | HybridCache (Redis L2) |
|---|---|---|
| 1 | 1000 DB queries, 187 ms | 1 DB query, 5 ms |
| 2 | 1000 DB queries, 38 ms | 1 DB query, 2 ms |
| 3 | 1000 DB queries, 45 ms | 1 DB query, 2 ms |
| 4 | 1000 DB queries, 44 ms | 1 DB query, 2 ms |
| 5 | 1000 DB queries, 45 ms | 1 DB query, 1 ms |

Read: every one of the 1,000 requests missed and queried the DB with plain cache-aside, in all 5 rounds. HybridCache ran the factory once per key (in-process request coalescing). Timings are small because the query is cheap and local; the point is the 1,000x query count, not the ms.
