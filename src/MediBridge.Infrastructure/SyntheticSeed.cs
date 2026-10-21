using MediBridge.Core;

namespace MediBridge.Infrastructure;

public static class SyntheticSeed
{
    public static async Task RunAsync(ClaimStore store, CancellationToken cancellationToken = default)
    {
        var start = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        await store.SubmitAsync(Create("CLM-1001", "SYN-DEMO-001", npi: ""), Simulation.None, start, cancellationToken);
        await store.SubmitAsync(Create("CLM-1002", "SYN-DEMO-002"), Simulation.DownstreamTimeout, start.AddMinutes(1), cancellationToken);
        await store.SubmitAsync(Create("CLM-1003", "SYN-DEMO-002"), Simulation.None, start.AddMinutes(2), cancellationToken);
        await store.SubmitAsync(Create("CLM-1004", "SYN-DEMO-004", version: "phase0-v0"), Simulation.None, start.AddMinutes(3), cancellationToken);
    }

    private static Claim Create(string id, string member, string npi = "0000000006", string version = ClaimRules.Version) => new()
    {
        Id = id, ProviderNpi = npi, MemberId = member, ServiceDate = new DateOnly(2026, 1, 15),
        ProcedureCodes = ["SYN-PROC-01"], Amount = 125.50m, RuleVersion = version
    };
}
