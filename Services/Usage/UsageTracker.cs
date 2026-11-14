using aida.Data;

namespace aida.Services.Usage;

public interface IUsageTracker
{
    Task RecordAsync(UsageEvent evt, CancellationToken ct = default);
}

internal sealed class UsageTracker(
    IServiceScopeFactory scopeFactory,
    ILogger<UsageTracker> logger) : IUsageTracker
{
    public async Task RecordAsync(UsageEvent evt, CancellationToken ct = default)
    {
        try
        {
            if (evt.Error is { Length: > 500 })
                evt.Error = evt.Error[..500];

            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.UsageEvents.Add(evt);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not record usage event for {Model}.", evt.Model);
        }
    }
}
