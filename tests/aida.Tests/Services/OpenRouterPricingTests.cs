using System.Text.Json;
using aida.Services.Usage;

namespace aida.Tests.Services;

public class OpenRouterPricingTests
{
    private const string Catalog = """
        {
          "data": [
            { "id": "anthropic/claude-sonnet-5.5",
              "pricing": { "prompt": "0.000002", "completion": "0.00001", "web_search": "0.01" } },
            { "id": "openai/gpt-5-nano",
              "pricing": { "prompt": "0.00000005", "completion": "0.0000004" } },
            { "id": "numeric/pricing", "pricing": { "prompt": 0.000001, "completion": 0.000002 } },
            { "id": "no-pricing" },
            { "id": "broken/price", "pricing": { "prompt": "abc", "completion": "0.000001" } },
            { "id": "half/price", "pricing": { "prompt": "0.000001" } },
            { "pricing": { "prompt": "0.000001", "completion": "0.000001" } },
            { "id": "   ", "pricing": { "prompt": "0.000001", "completion": "0.000001" } }
          ]
        }
        """;

    [Fact]
    public void Parses_catalog_scales_per_token_to_per_1m_and_skips_invalid_rows()
    {
        var prices = OpenRouterPricingService.ParseCatalog(Catalog);

        Assert.Equal((2m, 10m), prices["anthropic/claude-sonnet-5.5"]);
        Assert.Equal((0.05m, 0.4m), prices["openai/gpt-5-nano"]);
        Assert.Equal((1m, 2m), prices["numeric/pricing"]);

        Assert.Equal(3, prices.Count);
        Assert.DoesNotContain("no-pricing", prices.Keys);
        Assert.DoesNotContain("broken/price", prices.Keys);
        Assert.DoesNotContain("half/price", prices.Keys);
    }

    [Fact]
    public void Missing_or_non_array_data_yields_empty_table()
    {
        Assert.Empty(OpenRouterPricingService.ParseCatalog("""{ "data": null }"""));
        Assert.Empty(OpenRouterPricingService.ParseCatalog("""{ "error": "rate limited" }"""));
        Assert.Empty(OpenRouterPricingService.ParseCatalog("""{ "data": { "id": "x" } }"""));
    }

    [Fact]
    public void Malformed_json_throws_for_the_service_to_log_and_retry()
        => Assert.ThrowsAny<JsonException>(   
            () => OpenRouterPricingService.ParseCatalog("{ not json"));

    [Fact]
    public void Refresh_interval_is_six_hours()
        => Assert.Equal(TimeSpan.FromHours(6), OpenRouterPricingService.RefreshInterval);
}
