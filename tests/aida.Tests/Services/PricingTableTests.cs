using aida.Services.Usage;

namespace aida.Tests.Services;

public class PricingTableTests
{
    private static PricingTable Table()
    {
        var table = new PricingTable();
        table.Replace(new Dictionary<string, (decimal In, decimal Out)>
        {
            ["openai/gpt-4o"] = (2.5m, 10m),
            ["openai/gpt-4o-mini"] = (0.15m, 0.6m),
        });
        return table;
    }

    [Fact]
    public void Known_model_costs_tokens_at_configured_rates()
    {
        var table = Table();

        Assert.Equal(0.0075m, table.CostUsd("openai/gpt-4o", 1000, 500));
    }

    [Fact]
    public void Unknown_model_returns_null_never_zero()
    {
        var table = Table();

        Assert.Null(table.CostUsd("mystery/model", 1000, 500));
        Assert.False(table.IsPriced("mystery/model"));
    }

    [Fact]
    public void Missing_usage_returns_null_even_for_priced_models()
    {
        var table = Table();

        Assert.Null(table.CostUsd("openai/gpt-4o", null, null));
    }

    [Fact]
    public void One_sided_usage_still_costs()
    {
        var table = Table();

        Assert.Equal(0.00015m, table.CostUsd("openai/gpt-4o-mini", 1000, null));
    }

    [Fact]
    public void Priced_models_count_excludes_nothing_valid()
        => Assert.Equal(2, Table().Count);

    [Fact]
    public void Replace_swaps_the_whole_snapshot_atomically()
    {
        var table = Table();
        Assert.Equal(2, table.Count);

        table.Replace(new Dictionary<string, (decimal In, decimal Out)>()); 

        Assert.Equal(0, table.Count);
        Assert.False(table.IsPriced("openai/gpt-4o"));
        Assert.Null(table.CostUsd("openai/gpt-4o", 1000, 500));
    }
}
