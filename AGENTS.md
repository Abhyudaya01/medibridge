# MediBridge – agent rules
Healthcare Claims & Prior Authorization Exception and Recovery Engine. Portfolio simulation on SYNTHETIC data only.
Not affiliated with any payer. Never use real PHI, real member IDs, or real payer names or branding.
## Stack
C#/.NET (installed LTS), ASP.NET Core minimal APIs, EF Core, xUnit. SQLite by default; SQL Server when
ConnectionStrings:Sql is set (config only). Demo UI: static HTML + vanilla JS in wwwroot, clean and professional.
## Structure
src/MediBridge.Api (endpoints, DI, wwwroot), src/MediBridge.Core (domain, rules, no framework deps),
src/MediBridge.Infrastructure (EF Core, adapters), tests/MediBridge.Tests, benchmarks/ (harnesses + committed results).
## Rules
- Core logic deterministic and unit-tested. Every state change writes to an append-only audit table.
- Never commit secrets. Benchmarks write results to benchmarks/results/*.json and a row to benchmarks/RESULTS.md.
- Never hardcode or fabricate benchmark numbers.
- Run dotnet build and dotnet test before finishing; all tests must pass. Update README.md when behavior changes.

## Phase workflow (follow for every chunk)
1. Create a branch phase-<chunk-id>-<short-name> from an up-to-date main.
2. Propose a plan (files, design decisions, tests) and WAIT for my approval before writing code.
3. Implement; run dotnet build and dotnet test until green. Bump the minor version shown by /health.
4. Update README.md and the demo UI for the new behavior.
5. Commit, push, open a PR with `gh pr create --fill`, wait with `gh pr checks --watch`,
   merge with `gh pr merge --squash --delete-branch`.
6. On main, watch the deploy with `gh run watch`, then curl the live /health (URL in infra/azure.env)
   and confirm the new version is live.
7. Summarize what changed and list any benchmark results from benchmarks/RESULTS.md.
Azure guard: before any az command that creates or changes resources, confirm
`az account show --query id -o tsv` returns 36637271-a6f8-4110-9be1-55f112612c8c. Use the region in infra/azure.env.
