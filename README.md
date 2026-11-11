# caching-patterns

Real runs behind the "10 Caching Patterns Every Developer Should Know" video. .NET 10.

```bash
docker run -d --name cp-postgres -e POSTGRES_PASSWORD=postgres -p 5440:5432 postgres:17
docker run -d --name cp-redis -p 6390:6379 redis:8
dotnet run -c Release --project Caching.Bench
```

First run seeds 100,000 products and 2,000,000 reviews. Results: `RESULTS.md`.
