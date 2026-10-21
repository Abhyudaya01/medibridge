# MediBridge

Healthcare Claims & Prior Authorization Exception and Recovery Engine. Portfolio simulation on **synthetic data only**. Not affiliated with any payer. Never use real PHI, real member IDs, or real payer names or branding.

## Run locally

Requires the locally installed .NET 10 LTS SDK.

```sh
dotnet build
dotnet test
dotnet run --project src/MediBridge.Api --no-launch-profile --urls http://localhost:5080
```

Open [the landing page](http://localhost:5080/) or [GET /health](http://localhost:5080/health), which returns `{"status":"ok","version":"0.1.0"}`. The health endpoint is a process liveness check; persistence is not configured in this scaffold.

## Structure

- `src/MediBridge.Api`: ASP.NET Core minimal API, dependency composition, and static HTML landing page in `wwwroot`. References Core and Infrastructure.
- `src/MediBridge.Core`: framework-independent domain, deterministic rules, and classification (foundation only).
- `src/MediBridge.Infrastructure`: persistence and adapters (foundation only). References Core.
- `tests/MediBridge.Tests`: xUnit integration tests using `WebApplicationFactory<Program>`. References Api.
- `benchmarks/`: reserved for load tests, fault injection, generators, and committed measured results.

## Roadmap

1. **Phase 1 — Foundation:** solution scaffold, health endpoint, landing page, and integration test baseline.
2. **Phase 2 — Exceptions & recovery:** deterministic classification, recovery workflows, and append-only auditing.
3. **Phase 3 — Reliability & measurement:** synthetic workloads, fault injection, and measured benchmarks.

Future persistence will use EF Core with SQLite by default and SQL Server/Azure SQL when `ConnectionStrings:Sql` is set. Every state change must write an append-only audit record. No business state changes or persistence exist in this phase. Secrets must come from configuration or environment variables.

Every benchmark must generate machine-readable `benchmarks/results/*.json` and a summary row in `benchmarks/RESULTS.md` from an actual harness run. No benchmark has been run in this scaffold.

Source: [Abhyudaya01/medibridge](https://github.com/Abhyudaya01/medibridge).
