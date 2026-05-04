using System.Globalization;
using System.Text.Json;
using KuCoinFuturesReporter.Models;
using Microsoft.Extensions.Options;
using KuCoinFuturesReporter.Options;

namespace KuCoinFuturesReporter.Services;

public sealed class KuCoinFuturesClient(
    HttpClient httpClient,
    IOptions<KuCoinOptions> kuCoinOptions,
    ILogger<KuCoinFuturesClient> logger)
{
    private readonly KuCoinOptions _kuCoinOptions = kuCoinOptions.Value;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<IReadOnlyList<ClosedPosition>> GetClosedPositionsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var result = new List<ClosedPosition>();
        var pageId = 1;
        const int limit = 100;

        while (true)
        {
            var endpoint = $"/api/v1/history-positions?from={from.ToUnixTimeMilliseconds()}&to={to.ToUnixTimeMilliseconds()}&limit={limit}&pageId={pageId}";
            using var response = await httpClient.GetAsync(endpoint, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"KuCoin request failed: HTTP {(int)response.StatusCode}. Body: {json}");

            var payload = JsonSerializer.Deserialize<KuCoinEnvelope<KuCoinPage<KuCoinClosedPositionDto>>>(json, JsonOptions)
                ?? throw new InvalidOperationException("KuCoin returned empty response.");

            if (payload.Code != "200000")
                throw new InvalidOperationException($"KuCoin returned code {payload.Code}. Body: {json}");

            var items = payload.Data?.Items ?? [];
            result.AddRange(items.Select(Map));

            var totalPage = payload.Data?.TotalPage ?? pageId;
            if (pageId >= totalPage || items.Count == 0)
                break;

            pageId++;
        }

        logger.LogInformation("Loaded {Count} KuCoin closed positions from {From} to {To}", result.Count, from, to);
        return result;
    }

    public async Task<FuturesAccountOverview> GetFuturesAccountOverviewAsync(CancellationToken cancellationToken)
    {
        var currency = string.IsNullOrWhiteSpace(_kuCoinOptions.FuturesOverviewCurrency)
            ? "USDT"
            : _kuCoinOptions.FuturesOverviewCurrency.Trim();
        var q = Uri.EscapeDataString(currency);
        using var response = await httpClient.GetAsync($"/api/v1/account-overview?currency={q}", cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"KuCoin futures request failed: HTTP {(int)response.StatusCode}. Body: {json}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        
        if (root.TryGetProperty("code", out var codeEl) && codeEl.GetString() != "200000")
            throw new InvalidOperationException($"KuCoin futures returned code {codeEl}. Body: {json}");

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("KuCoin futures account-overview: missing data object.");

        return new FuturesAccountOverview(
            Currency: ReadString(data, "currency"),
            AccountEquity: ReadDecimal(data, "accountEquity"),
            UnrealisedPnl: ReadDecimal(data, "unrealisedPNL") ?? ReadDecimal(data, "unrealisedPnl"),
            MarginBalance: ReadDecimal(data, "marginBalance"),
            PositionMargin: ReadDecimal(data, "positionMargin"),
            OrderMargin: ReadDecimal(data, "orderMargin"),
            AvailableBalance: ReadDecimal(data, "availableBalance"),
            AvailableMargin: ReadDecimal(data, "availableMargin"),
            MaxWithdrawAmount: ReadDecimal(data, "maxWithdrawAmount"));
    }

    private static string? ReadString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

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

    private static ClosedPosition Map(KuCoinClosedPositionDto x) => new()
    {
        CloseId = x.CloseId ?? throw new InvalidOperationException("KuCoin position has empty closeId."),
        Symbol = x.Symbol ?? string.Empty,
        SettleCurrency = x.SettleCurrency ?? string.Empty,
        Type = x.Type ?? string.Empty,
        Side = x.Side ?? string.Empty,
        MarginMode = x.MarginMode ?? string.Empty,
        PositionSide = x.PositionSide,
        Leverage = ParseDecimal(x.Leverage),
        Pnl = ParseDecimal(x.Pnl),
        RealisedGrossCost = ParseDecimal(x.RealisedGrossCostNew ?? x.RealisedGrossCost),
        TradeFee = ParseDecimal(x.TradeFee),
        FundingFee = ParseDecimal(x.FundingFee),
        OpenPrice = ParseDecimal(x.OpenPrice),
        ClosePrice = ParseDecimal(x.ClosePrice),
        Roe = ParseDecimal(x.Roe),
        OpenTime = FromUnixMs(x.OpenTime),
        CloseTime = FromUnixMs(x.CloseTime)
    };

    private static decimal? ParseDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static DateTimeOffset FromUnixMs(long? value) =>
        value is null or <= 0 ? DateTimeOffset.UnixEpoch : DateTimeOffset.FromUnixTimeMilliseconds(value.Value);

    private sealed class KuCoinEnvelope<T>
    {
        public string? Code { get; set; }
        public T? Data { get; set; }
    }

    private sealed class KuCoinPage<T>
    {
        public int CurrentPage { get; set; }
        public int PageSize { get; set; }
        public int TotalNum { get; set; }
        public int TotalPage { get; set; }
        public List<T> Items { get; set; } = [];
    }

    private sealed class KuCoinClosedPositionDto
    {
        public string? CloseId { get; set; }
        public string? Symbol { get; set; }
        public string? SettleCurrency { get; set; }
        public string? Leverage { get; set; }
        public string? Type { get; set; }
        public string? Pnl { get; set; }
        public string? RealisedGrossCost { get; set; }
        public string? RealisedGrossCostNew { get; set; }
        public string? TradeFee { get; set; }
        public string? FundingFee { get; set; }
        public long? OpenTime { get; set; }
        public long? CloseTime { get; set; }
        public string? OpenPrice { get; set; }
        public string? ClosePrice { get; set; }
        public string? MarginMode { get; set; }
        public string? PositionSide { get; set; }
        public string? Roe { get; set; }
        public string? Side { get; set; }
    }
}
