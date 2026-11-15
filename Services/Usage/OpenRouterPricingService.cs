using System.Globalization;
using System.Text.Json;

namespace aida.Services.Usage;

internal sealed class OpenRouterPricingService(
    PricingTable table,
    IHttpClientFactory httpClientFactory,
    IConfiguration config,
    ILogger<OpenRouterPricingService> logger) : BackgroundService
{
    internal const string HttpClientName = "openrouter-catalog";
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RefreshOnceAsync(stoppingToken);
            try
            {
                await Task.Delay(RefreshInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break; 
            }
        }
    }

    internal async Task RefreshOnceAsync(CancellationToken ct)
    {
        var baseUrl = (config["OpenRouter:BaseUrl"] ?? "https://openrouter.ai/api/v1").TrimEnd('/');
        try
        {
            using var response = await httpClientFactory.CreateClient(HttpClientName)
                .GetAsync($"{baseUrl}/models", ct);
            response.EnsureSuccessStatusCode();

            var prices = ParseCatalog(await response.Content.ReadAsStringAsync(ct));
            table.Replace(prices);
            logger.LogInformation(
                "OpenRouter pricing refreshed: {Count} models priced", prices.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "OpenRouter pricing refresh failed — costs stay unpriced until the next attempt ({Interval})",
                RefreshInterval);
        }
    }

    internal static Dictionary<string, (decimal In, decimal Out)> ParseCatalog(string json)
    {
        var prices = new Dictionary<string, (decimal In, decimal Out)>(StringComparer.Ordinal);

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
            return prices;

        foreach (var model in data.EnumerateArray())
        {
            if (!model.TryGetProperty("id", out var idElement)) continue;
            var id = idElement.GetString();
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (!model.TryGetProperty("pricing", out var pricing)) continue;
            if (!TryPrice(pricing, "prompt", out var perTokenIn)) continue;
            if (!TryPrice(pricing, "completion", out var perTokenOut)) continue;

            prices[id] = (perTokenIn * 1_000_000m, perTokenOut * 1_000_000m);
        }

        return prices;
    }

    private static bool TryPrice(JsonElement pricing, string name, out decimal perToken)
    {
        perToken = 0;
        if (!pricing.TryGetProperty(name, out var element)) return false;

        return element.ValueKind switch
        {
            JsonValueKind.String => decimal.TryParse(
                element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out perToken),
            JsonValueKind.Number => element.TryGetDecimal(out perToken),
            _ => false,
        };
    }
}
