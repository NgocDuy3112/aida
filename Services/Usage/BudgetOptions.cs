namespace aida.Services.Usage;

public sealed class BudgetOptions
{
    public const string SectionName = "Budget";
    public const double DefaultWarningPercent = 80;

    /// <summary>
    /// What one user may spend per UTC day, in USD. <c>0</c> (or unset) turns the budget off: no spend is
    /// read and no turn is blocked. There is no default amount — the deployment sets its own.
    /// </summary>
    public double DailyUsd { get; init; }

    /// <summary>
    /// Spend at or above this share of <see cref="DailyUsd"/>, in percent, warns the user but still runs
    /// the turn. Values outside 1..100 fall back to <see cref="DefaultWarningPercent"/>.
    /// </summary>
    public double WarningPercent { get; init; } = DefaultWarningPercent;

    public bool Enabled => DailyUsd > 0;

    public static BudgetOptions FromConfiguration(IConfiguration? configuration)
    {
        var section = configuration?.GetSection(SectionName);
        if (section is null || !section.Exists())
        {
            return new BudgetOptions();
        }

        var warningPercent = section.GetValue("WarningPercent", DefaultWarningPercent);

        return new BudgetOptions
        {
            DailyUsd = Math.Max(0, section.GetValue("DailyUsd", 0d)),
            WarningPercent = warningPercent is > 0 and <= 100 ? warningPercent : DefaultWarningPercent,
        };
    }
}
