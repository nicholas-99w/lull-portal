namespace Lull.Portal.Data;

/// <summary>
/// Deletes audit entries older than the days the owner chose (<see cref="Domain.AccountSettings.AuditRetentionDays"/>,
/// at first from the .env), at start-up and then once a day, reading that afresh each time. A failed run is logged and tried again the
/// next day; it never stops the portal.
/// </summary>
public sealed class AuditRetention(PortalStore store, PortalSettings settings, TimeProvider clock, ILogger<AuditRetention> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var daily = new PeriodicTimer(TimeSpan.FromDays(1), clock);
        do
        {
            try
            {
                var days = (await settings.GetAsync(stoppingToken)).AuditRetentionDays;
                if (days <= 0) continue; // kept forever, for now
                var before = clock.GetUtcNow().AddDays(-days);
                var deleted = await store.PruneAuditAsync(before, stoppingToken);
                if (deleted > 0) log.LogInformation("audit log: deleted {Count} entries older than {Days} days", deleted, days);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "audit log: couldn't delete old entries; trying again tomorrow");
            }
        } while (await daily.WaitForNextTickAsync(stoppingToken));
    }
}
