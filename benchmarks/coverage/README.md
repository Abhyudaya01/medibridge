# Measured Core line coverage

The xUnit project already references `coverlet.collector` 6.0.4. The committed
`coverage.runsettings` includes the Core assembly only and excludes generated
`obj` files. It does not exclude uncovered hand-written Core logic.

From the repository root, choose a fresh results directory:

```sh
dotnet build -c Release
dotnet test --no-build -c Release --collect:"XPlat Code Coverage" --settings coverage.runsettings --results-directory benchmarks/work/coverage
python3 benchmarks/coverage/report.py benchmarks/work/coverage/*/coverage.cobertura.xml
```

The glob must identify one collector report; use the exact attachment path printed
by `dotnet test` if multiple runs exist. A TRX logger may copy the same attachment
under its own `In` directory; use the original collector GUID directory, not both
copies. The reporter intentionally does not merge overlapping test reports.

`report.py` validates that the export contains only `MediBridge.Core`, checks its
line-rate/count agreement and independently deduplicates physical filename/line
entries. It computes covered lines / executable lines, writes actual counts and a
percentage to `benchmarks/results/coverage.json`, and appends a ledger row. Invalid
exports produce a failed artifact/row and a nonzero exit. `--output` supports
isolated test or CI paths. Source-content and XML hashes identify the measurement;
raw XML and test logs remain in ignored `benchmarks/work/`.

The local recorded run can skip the SQL Server integration test when its CI-only
connection is absent. CI runs that test against disposable SQL Server, collects
Core-only coverage and validates a separate report in the runner temp directory;
it does not overwrite committed local measurements. This measures Core C# lines,
not JS/UI coverage. Crash-child processes killed by the chaos tests are not included
by this in-process collector. Coverage is a measured test-reachability statistic,
not a claim that every behavior or branch is proven correct.

Reporter tests:

```sh
python3 -m unittest discover -s benchmarks/coverage -p 'test_*.py'
```
