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
