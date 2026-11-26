using aida.Data;
using Microsoft.EntityFrameworkCore;

namespace aida.Services.Usage;

public enum BudgetState
{
    /// <summary>The budget is off, or its spend could not be read: the turn runs and nothing is said.</summary>
    Disabled,
    WithinLimit,
    NearLimit,
    Exceeded,
}

/// <param name="SpentUsd">What the user spent since the start of the current UTC day.</param>
/// <param name="DailyUsd">The configured limit for that day.</param>
public sealed record DailyBudgetStatus(BudgetState State, decimal SpentUsd, decimal DailyUsd)
{
    public static readonly DailyBudgetStatus Disabled = new(BudgetState.Disabled, 0m, 0m);
}

public interface IDailyBudget
{
    Task<DailyBudgetStatus> CheckAsync(string userId, CancellationToken ct = default);
}

/// <summary>
/// Compares what a user spent today against <see cref="BudgetOptions.DailyUsd"/>. The spend is the sum of
/// the recorded <see cref="UsageEvent.CostUsd"/> rows, i.e. the same dollars <c>/admin</c> reports and the
/// only cost figure the app knows, priced from the OpenRouter catalog when the call is recorded.
/// </summary>
/// <remarks>
/// The day is a UTC day, the same boundary <see cref="UsageReport"/> groups its days by, so the budget and
/// the admin report roll over together. The sum is recomputed on every check instead of cached: it is one
/// indexed aggregate over the user's rows of the day, negligible next to the model calls it guards, while a
/// cache would let a turn through after the budget was already spent.
/// </remarks>
internal class DailyBudgetService(
    IServiceScopeFactory scopeFactory,
    BudgetOptions options,
    ILogger<DailyBudgetService> logger) : IDailyBudget
{
    internal virtual DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public async Task<DailyBudgetStatus> CheckAsync(string userId, CancellationToken ct = default)
    {
        if (!options.Enabled) return DailyBudgetStatus.Disabled;

        var limit = (decimal)options.DailyUsd;
        var startOfDay = new DateTimeOffset(UtcNow.UtcDateTime.Date, TimeSpan.Zero);

        decimal spent;
        try
        {
            spent = await SumSinceAsync(userId, startOfDay, ct);
        }
        catch (Exception ex)
        {
            // A budget nobody can read must not stop the chat; the failure is logged instead of the turn.
            logger.LogWarning(ex, "Could not read today's spend for {UserId}; allowing the turn.", userId);
            return DailyBudgetStatus.Disabled;
        }

        var warningFrom = limit * (decimal)options.WarningPercent / 100m;
        var state = spent >= limit ? BudgetState.Exceeded
            : spent >= warningFrom ? BudgetState.NearLimit
            : BudgetState.WithinLimit;

        return new DailyBudgetStatus(state, spent, limit);
    }

    internal virtual async Task<decimal> SumSinceAsync(
        string userId, DateTimeOffset from, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.UsageEvents
            .Where(e => e.UserId == userId && e.Timestamp >= from)
            .SumAsync(e => e.CostUsd ?? 0m, ct);
    }
}
