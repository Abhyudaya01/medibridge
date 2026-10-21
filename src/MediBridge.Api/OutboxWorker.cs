using System.Threading.Channels;
using MediBridge.Infrastructure;
using Microsoft.EntityFrameworkCore;

public sealed class OutboxSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    public void Wake() => _channel.Writer.TryWrite(true);
    public async Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(delay);
        try { await _channel.Reader.ReadAsync(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
    }
}
public sealed class OutboxWorker(OutboxDispatcher dispatcher, OutboxSignal signal, IServiceScopeFactory scopes,
    IConfiguration config, ILogger<OutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Outbox:Enabled", true)) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (await dispatcher.DispatchOneAsync(stoppingToken)) { }
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ClaimsDbContext>();
                var rows = await db.Outbox.AsNoTracking().Where(m => m.State == "Pending" || m.State == "Processing" || m.State == "AwaitingAck").ToListAsync(stoppingToken);
                var next = rows.Select(m => m.State == "Pending" ? m.NextAttemptAt : m.LeaseUntil ?? DateTime.UtcNow).DefaultIfEmpty(DateTime.UtcNow.AddMinutes(65)).Min();
                await signal.WaitAsync(TimeSpan.FromMilliseconds(Math.Clamp((next - DateTime.UtcNow).TotalMilliseconds, 100, TimeSpan.FromMinutes(65).TotalMilliseconds)), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                // No exception text/connection string is logged; durable leases recover after connectivity returns.
                logger.LogWarning("Outbox storage unavailable ({ErrorType}); retrying recovery scan.", exception.GetType().Name);
                await signal.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }
}
