using MediBridge.Core.PriorAuth;

namespace MediBridge.Sla;

public sealed record PlannedRequest(string Id, PriorAuthPriority Priority, DateTimeOffset Receipt,
    DateTimeOffset? PendingAt, DateTimeOffset DecisionAt, string Decision, string? Reason);
public sealed class SimulationClock : IClock { public DateTimeOffset UtcNow { get; set; } }
public static class Scenario
{
    public static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public static PlannedRequest[] Generate(int count = 10000, int seed = 278837)
    {
        var random = new Random(seed);
        double Normal() => Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
        return Enumerable.Range(1, count).Select(i =>
        {
            DateTimeOffset arrival;
            while (true)
            {
                arrival = Start.AddSeconds(random.Next(30 * 86400));
                var weekday = arrival.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
                var weight = weekday ? (arrival.Hour is >= 9 and < 17 ? 1 : .2) : .15;
                if (random.NextDouble() < weight) break;
            }
            var expedited = random.NextDouble() < .15;
            var pending = random.NextDouble() < .20;
            var baselineHours = Math.Clamp((expedited ? 20 : 48) * Math.Exp(.8 * Normal()), 1, expedited ? 240 : 480);
            var extraHours = pending ? (expedited ? 12 + 36 * random.NextDouble() : 48 + 96 * random.NextDouble()) : 0;
            var decisionAt = arrival.AddSeconds(Math.Round((baselineHours + extraHours) * 3600));
            var approved = random.NextDouble() < .75;
            return new PlannedRequest($"PA-SLA-{i:D5}", expedited ? PriorAuthPriority.Expedited : PriorAuthPriority.Standard, arrival,
                pending ? arrival.AddSeconds(Math.Round(Math.Min(baselineHours * .35, 12) * 3600)) : null,
                decisionAt, approved ? "Approved" : "Denied", approved ? null : Enum.GetNames<DenialReason>()[random.Next(4)]);
        }).OrderBy(p => p.Receipt).ThenBy(p => p.Id, StringComparer.Ordinal).ToArray();
    }
    // Independent deadline oracle: deliberately does not call the production lifecycle rules.
    public static DateTimeOffset Due(PlannedRequest request) => request.Receipt.AddHours(request.Priority == PriorAuthPriority.Standard ? 168 : 72);
    public static DateTimeOffset Risk(PlannedRequest request) => request.Receipt.AddHours(request.Priority == PriorAuthPriority.Standard ? 134.4 : 57.6);
    public static DateTimeOffset Tick(DateTimeOffset instant) => new((instant.UtcTicks + TimeSpan.TicksPerMinute - 1) / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute, TimeSpan.Zero);
}
