using MediBridge.Infrastructure.PriorAuth;

public sealed class PriorAuthSlaWorker(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<PriorAuthSlaWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("PriorAuthSla:Enabled", true)) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<PriorAuthLifecycleStore>();
                while (await store.SweepAsync(stoppingToken) > 0) { stoppingToken.ThrowIfCancellationRequested(); }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { log.LogError(exception, "Prior authorization SLA scan failed; next scan will retry durable work."); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
