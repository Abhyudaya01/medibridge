using MediBridge.Core;

namespace MediBridge.Infrastructure;

public sealed class FaultOptions
{
    public bool Enabled { get; set; }
    public double TransientFailureRate { get; set; }
    public int AddedLatencyMs { get; set; }
    public string? CrashAfterStep { get; set; }
}
public interface IFaultInjector
{
    Task<FailureDescriptor?> BeforeAttemptAsync(string messageId, int attempt, CancellationToken cancellationToken);
    Task CheckpointAsync(string step, CancellationToken cancellationToken);
}
public sealed class ConfigurableFaultInjector(FaultOptions options) : IFaultInjector
{
    public async Task<FailureDescriptor?> BeforeAttemptAsync(string messageId, int attempt, CancellationToken cancellationToken)
    {
        if (!options.Enabled) return null;
        if (options.AddedLatencyMs > 0) await Task.Delay(options.AddedLatencyMs, cancellationToken);
        return Random.Shared.NextDouble() < options.TransientFailureRate ? new("Timeout", "Injected synthetic timeout.") : null;
    }
    public Task CheckpointAsync(string step, CancellationToken cancellationToken)
    {
        if (options.Enabled && options.CrashAfterStep == step) Environment.FailFast($"Synthetic fault checkpoint: {step}");
        return Task.CompletedTask;
    }
}
public static class FailureAdapters
{
    public static FailureDescriptor Describe(Exception exception) => exception switch
    {
        OperationCanceledException => new("Timeout", "Downstream operation timed out."),
        TimeoutException => new("Timeout", "Downstream timeout."),
        Microsoft.Data.SqlClient.SqlException sql => new("Sql", "SQL operation failed.", SqlError: sql.Number),
        HttpRequestException http => new("Http", "Downstream HTTP failure.", (int?)http.StatusCode),
        _ when exception.InnerException is not null => Describe(exception.InnerException),
        _ => new("Unknown", "Unhandled processing failure requires manual review.")
    };
}
