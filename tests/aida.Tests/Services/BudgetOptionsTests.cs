using aida.Services.Usage;
using Microsoft.Extensions.Configuration;

namespace aida.Tests.Services;

public class BudgetOptionsTests
{
    [Fact]
    public void Defaults_when_configuration_is_missing()
    {
        var options = BudgetOptions.FromConfiguration(null);

        Assert.False(options.Enabled);
        Assert.Equal(0, options.DailyUsd);
        Assert.Equal(80, options.WarningPercent);
    }

    [Fact]
    public void Defaults_when_section_is_absent()
    {
        var options = BudgetOptions.FromConfiguration(new ConfigurationBuilder().Build());

        Assert.False(options.Enabled);
        Assert.Equal(0, options.DailyUsd);
        Assert.Equal(80, options.WarningPercent);
    }

    [Fact]
    public void Reads_values_from_budget_section()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Budget:DailyUsd"] = "1.5",
                ["Budget:WarningPercent"] = "60",
            })
            .Build();

        var options = BudgetOptions.FromConfiguration(configuration);

        Assert.True(options.Enabled);
        Assert.Equal(1.5, options.DailyUsd);
        Assert.Equal(60, options.WarningPercent);
    }

    [Fact]
    public void Zero_daily_usd_turns_the_budget_off()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Budget:DailyUsd"] = "0",
            })
            .Build();

        Assert.False(BudgetOptions.FromConfiguration(configuration).Enabled);
    }

    [Fact]
    public void A_negative_daily_usd_is_treated_as_off()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Budget:DailyUsd"] = "-3",
            })
            .Build();

        var options = BudgetOptions.FromConfiguration(configuration);

        Assert.False(options.Enabled);
        Assert.Equal(0, options.DailyUsd);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("120")]
    public void An_out_of_range_warning_percent_falls_back_to_the_default(string configured)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Budget:DailyUsd"] = "2",
                ["Budget:WarningPercent"] = configured,
            })
            .Build();

        Assert.Equal(80, BudgetOptions.FromConfiguration(configuration).WarningPercent);
    }

    [Fact]
    public void Warning_percent_alone_does_not_enable_the_budget()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Budget:WarningPercent"] = "50",
            })
            .Build();

        Assert.False(BudgetOptions.FromConfiguration(configuration).Enabled);
    }
}
