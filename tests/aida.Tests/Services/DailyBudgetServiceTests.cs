using aida.Services.Usage;
using aida.Tests.Components;
using Microsoft.Extensions.Logging.Abstractions;

namespace aida.Tests.Services;

public class DailyBudgetServiceTests
{
    /// <summary>
    /// The real day-window and threshold arithmetic on top of a simulated spend ledger, so the queries the
    /// service would send are visible instead of needing a database.
    /// </summary>
    private class TestBudget(BudgetOptions options) : DailyBudgetService(
        new NoScopeFactory(), options, NullLogger<DailyBudgetService>.Instance)
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

        public Dictionary<string, List<(DateTimeOffset At, decimal Cost)>> Ledger { get; } = [];

        public List<DateTimeOffset> Queries { get; } = [];

        internal override DateTimeOffset UtcNow => Now;

        public void Spend(string userId, DateTimeOffset at, decimal cost)
        {
            if (!Ledger.TryGetValue(userId, out var rows))
                Ledger[userId] = rows = [];
            rows.Add((at, cost));
        }

        internal override Task<decimal> SumSinceAsync(string userId, DateTimeOffset from, CancellationToken ct)
        {
            Queries.Add(from);
            var spent = Ledger.TryGetValue(userId, out var rows)
                ? rows.Where(r => r.At >= from).Sum(r => r.Cost)
                : 0m;
            return Task.FromResult(spent);
        }
    }

    private static BudgetOptions Options(double dailyUsd, double warningPercent = 80) =>
        new() { DailyUsd = dailyUsd, WarningPercent = warningPercent };

    [Fact]
    public async Task A_disabled_budget_reads_no_spend_and_reports_disabled()
    {
        var budget = new TestBudget(Options(dailyUsd: 0));
        budget.Spend("user-1", budget.Now, 100m);

        var status = await budget.CheckAsync("user-1");

        Assert.Equal(BudgetState.Disabled, status.State);
        Assert.Empty(budget.Queries);
    }

    [Fact]
    public async Task Spend_below_the_warning_share_is_within_limit()
    {
        var budget = new TestBudget(Options(dailyUsd: 2));
        budget.Spend("user-1", budget.Now, 1.59m);

        var status = await budget.CheckAsync("user-1");

        Assert.Equal(BudgetState.WithinLimit, status.State);
        Assert.Equal(1.59m, status.SpentUsd);
        Assert.Equal(2m, status.DailyUsd);
    }

    [Fact]
    public async Task Spend_at_the_warning_share_warns()
    {
        var budget = new TestBudget(Options(dailyUsd: 2, warningPercent: 80));
        budget.Spend("user-1", budget.Now, 1.6m);

        Assert.Equal(BudgetState.NearLimit, (await budget.CheckAsync("user-1")).State);
    }

    [Fact]
    public async Task Spend_at_the_limit_is_exceeded()
    {
        var budget = new TestBudget(Options(dailyUsd: 2));
        budget.Spend("user-1", budget.Now, 2m);

        Assert.Equal(BudgetState.Exceeded, (await budget.CheckAsync("user-1")).State);
    }

    [Fact]
    public async Task Spend_over_the_limit_is_exceeded()
    {
        var budget = new TestBudget(Options(dailyUsd: 2));
        budget.Spend("user-1", budget.Now, 2.5m);

        Assert.Equal(BudgetState.Exceeded, (await budget.CheckAsync("user-1")).State);
    }

    [Fact]
    public async Task The_window_starts_at_midnight_utc_of_the_current_day()
    {
        var budget = new TestBudget(Options(dailyUsd: 2));
        // Yesterday's spending must not count, even a minute before midnight.
        budget.Spend("user-1", new DateTimeOffset(2026, 11, 25, 23, 59, 0, TimeSpan.Zero), 5m);
        budget.Spend("user-1", new DateTimeOffset(2026, 11, 26, 0, 10, 0, TimeSpan.Zero), 0.5m);

        var status = await budget.CheckAsync("user-1");

        Assert.Equal([new DateTimeOffset(2026, 11, 26, 0, 0, 0, TimeSpan.Zero)], budget.Queries);
        Assert.Equal(BudgetState.WithinLimit, status.State);
        Assert.Equal(0.5m, status.SpentUsd);
    }

    [Fact]
    public async Task A_new_utc_day_resets_the_budget()
    {
        var budget = new TestBudget(Options(dailyUsd: 2));
        budget.Spend("user-1", new DateTimeOffset(2026, 11, 26, 23, 59, 0, TimeSpan.Zero), 2.5m);

        Assert.Equal(BudgetState.Exceeded, (await budget.CheckAsync("user-1")).State);

        budget.Now = new DateTimeOffset(2026, 11, 27, 0, 0, 1, TimeSpan.Zero);

        var status = await budget.CheckAsync("user-1");

        Assert.Equal(BudgetState.WithinLimit, status.State);
        Assert.Equal(0m, status.SpentUsd);
    }

    [Fact]
    public async Task One_users_spend_does_not_count_for_another()
    {
        var budget = new TestBudget(Options(dailyUsd: 2));
        budget.Spend("user-1", budget.Now, 3m);
        budget.Spend("user-2", budget.Now, 0.1m);

        Assert.Equal(BudgetState.Exceeded, (await budget.CheckAsync("user-1")).State);
        Assert.Equal(BudgetState.WithinLimit, (await budget.CheckAsync("user-2")).State);
    }

    [Fact]
    public async Task A_failed_spend_read_allows_the_turn()
    {
        var budget = new ThrowingBudget(Options(dailyUsd: 2));

        Assert.Equal(BudgetState.Disabled, (await budget.CheckAsync("user-1")).State);
    }

    private sealed class ThrowingBudget(BudgetOptions options) : TestBudget(options)
    {
        internal override Task<decimal> SumSinceAsync(string userId, DateTimeOffset from, CancellationToken ct) =>
            throw new InvalidOperationException("database is down");
    }}
