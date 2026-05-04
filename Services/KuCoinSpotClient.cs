using System.Globalization;
using System.Text.Json;
using KuCoinFuturesReporter.Models;

namespace KuCoinFuturesReporter.Services;

public sealed class KuCoinSpotClient(HttpClient httpClient, ILogger<KuCoinSpotClient> logger)
{
    public async Task<IReadOnlyDictionary<string, decimal>> GetSpotUsdtPricesAsync(CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync("/api/v1/prices", cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"KuCoin spot prices failed: HTTP {(int)response.StatusCode}. Body: {json}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("code", out var codeEl) && codeEl.GetString() != "200000")
            throw new InvalidOperationException($"KuCoin prices returned code {codeEl}. Body: {json}");

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        var dict = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        foreach (var prop in data.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(prop.Name))
                continue;

            var raw = prop.Value.ValueKind == JsonValueKind.String
                ? prop.Value.GetString()
                : prop.Value.ToString();

            if (decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var price))
                dict[prop.Name] = price;
        }

        return dict;
    }

    public async Task<IReadOnlyList<SpotTradeAccountLine>> GetTradeAccountsAsync(CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync("/api/v1/accounts?type=trade", cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"KuCoin spot request failed: HTTP {(int)response.StatusCode}. Body: {json}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("code", out var codeEl) && codeEl.GetString() != "200000")
            throw new InvalidOperationException($"KuCoin spot returned code {codeEl}. Body: {json}");

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<SpotTradeAccountLine>();

        foreach (var item in data.EnumerateArray())
        {
            var currency = item.TryGetProperty("currency", out var c) ? c.GetString() : null;
            
            if (string.IsNullOrWhiteSpace(currency))
                continue;

            var balance = ReadDecimal(item, "balance") ?? 0m;
            var available = ReadDecimal(item, "available") ?? 0m;
            var holds = ReadDecimal(item, "holds") ?? 0m;

            if (balance == 0m && available == 0m && holds == 0m)
                continue;

            list.Add(new SpotTradeAccountLine(currency, balance, available, holds));
        }

        list.Sort((a, b) => string.Compare(a.Currency, b.Currency, StringComparison.OrdinalIgnoreCase));
        logger.LogInformation("Loaded {Count} KuCoin spot trade account rows", list.Count);
        
        return list;
    }

    private static decimal? ReadDecimal(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var p))
            return null;

        return p.ValueKind switch
        {
            JsonValueKind.String => decimal.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
                ? d
                : null,
            JsonValueKind.Number => p.TryGetDecimal(out var x) ? x : null,
            _ => null
        };
    }
}
