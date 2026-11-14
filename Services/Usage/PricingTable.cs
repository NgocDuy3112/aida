namespace aida.Services.Usage;

public sealed class PricingTable
{
    private volatile IReadOnlyDictionary<string, (decimal In, decimal Out)> _prices =
        new Dictionary<string, (decimal In, decimal Out)>(StringComparer.Ordinal);

    internal void Replace(IReadOnlyDictionary<string, (decimal In, decimal Out)> prices)
        => _prices = new Dictionary<string, (decimal In, decimal Out)>(prices, StringComparer.Ordinal);

    internal int Count => _prices.Count;

    public decimal? CostUsd(string model, long? promptTokens, long? completionTokens)
    {
        if (promptTokens is null && completionTokens is null) return null;
        if (!_prices.TryGetValue(model, out var price)) return null;

        var cost = ((promptTokens ?? 0) * price.In
                  + (completionTokens ?? 0) * price.Out) / 1_000_000m;
        return cost;
    }

    public bool IsPriced(string model) => _prices.ContainsKey(model);
}
