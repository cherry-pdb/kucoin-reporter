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

    public async Task<IReadOnlyList<OpenFuturesPosition>> GetOpenPositionsAsync(CancellationToken cancellationToken)
    {
        var currency = string.IsNullOrWhiteSpace(_kuCoinOptions.FuturesOverviewCurrency)
            ? "USDT"
            : _kuCoinOptions.FuturesOverviewCurrency.Trim();
        var q = Uri.EscapeDataString(currency);

        using var response = await httpClient.GetAsync($"/api/v1/positions?currency={q}", cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"KuCoin futures request failed: HTTP {(int)response.StatusCode}. Body: {json}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("code", out var codeEl) && codeEl.GetString() != "200000")
            throw new InvalidOperationException($"KuCoin futures returned code {codeEl}. Body: {json}");

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<OpenFuturesPosition>();

        foreach (var item in data.EnumerateArray())
        {
            var isOpen = ReadBool(item, "isOpen");

            if (isOpen is not true)
                continue;

            var symbol = ReadString(item, "symbol")?.Trim();

            if (string.IsNullOrWhiteSpace(symbol))
                continue;

            list.Add(new OpenFuturesPosition(
                Symbol: symbol,
                PositionSide: ReadString(item, "positionSide"),
                CurrentQty: ReadDecimalFlexible(item, "currentQty"),
                Leverage: ReadDecimalFlexible(item, "leverage") ?? ReadDecimalFlexible(item, "realLeverage"),
                UnrealisedPnl: ReadDecimalFlexible(item, "unrealisedPnl"),
                RealisedPnl: ReadDecimalFlexible(item, "realisedPnl"),
                UnrealisedPnlPcnt: ReadDecimalFlexible(item, "unrealisedPnlPcnt"),
                UnrealisedRoePcnt: ReadDecimalFlexible(item, "unrealisedRoePcnt"),
                PosMargin: ReadDecimalFlexible(item, "posMargin"),
                AvgEntryPrice: ReadDecimalFlexible(item, "avgEntryPrice"),
                MarkPrice: ReadDecimalFlexible(item, "markPrice"),
                LiquidationPrice: ReadDecimalFlexible(item, "liquidationPrice")));
        }

        list.Sort((a, b) => string.Compare(a.Symbol, b.Symbol, StringComparison.OrdinalIgnoreCase));
        
        return list;
    }

    public async Task<IReadOnlyList<OpenFuturesStopOrder>> GetOpenStopOrdersAsync(CancellationToken cancellationToken)
    {
        var result = new List<OpenFuturesStopOrder>();
        var page = 1;
        const int pageSize = 200;

        while (true)
        {
            using var response = await httpClient.GetAsync($"/api/v1/stopOrders?currentPage={page}&pageSize={pageSize}", cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"KuCoin futures stopOrders request failed: HTTP {(int)response.StatusCode}. Body: {json}");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("code", out var codeEl) && codeEl.GetString() != "200000")
                throw new InvalidOperationException($"KuCoin futures returned code {codeEl}. Body: {json}");

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                break;

            if (!data.TryGetProperty("items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array)
                break;

            var added = 0;

            foreach (var item in itemsEl.EnumerateArray())
            {
                var symbol = ReadString(item, "symbol")?.Trim();

                if (string.IsNullOrWhiteSpace(symbol))
                    continue;

                var isActive = ReadBool(item, "isActive");
                var status = ReadString(item, "status");

                if (isActive is false)
                    continue;

                if (!string.IsNullOrWhiteSpace(status) && !status.Equals("open", StringComparison.OrdinalIgnoreCase))
                    continue;

                result.Add(new OpenFuturesStopOrder(
                    Symbol: symbol,
                    Side: ReadString(item, "side"),
                    StopPrice: ReadDecimalFlexible(item, "stopPrice"),
                    Price: ReadDecimalFlexible(item, "price"),
                    CloseOrder: ReadBool(item, "closeOrder"),
                    ReduceOnly: ReadBool(item, "reduceOnly")));

                added++;
            }

            var totalPage = ReadInt(data, "totalPage") ?? page;

            if (page >= totalPage || added == 0)
                break;

            page++;
        }

        return result;
    }

    public async Task<IReadOnlyList<FuturesContractSnapshot>> GetActiveContractsAsync(CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync("/api/v1/contracts/active", cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"KuCoin contracts/active failed: HTTP {(int)response.StatusCode}. Body: {json}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("code", out var codeEl) && codeEl.GetString() != "200000")
            throw new InvalidOperationException($"KuCoin contracts/active returned code {codeEl}. Body: {json}");

        if (!root.TryGetProperty("data", out var data))
            return [];

        var list = new List<FuturesContractSnapshot>();

        if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
                if (TryMapContract(item, out var c))
                    list.Add(c);
        }
        else if (data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                    if (TryMapContract(item, out var c))
                        list.Add(c);
            }
            else if (TryMapContract(data, out var single))
            {
                list.Add(single);
            }
        }

        return list;
    }

    public async Task<IReadOnlyList<FuturesKline>> GetKlinesAsync(
        string symbol,
        int granularityMinutes,
        int count,
        CancellationToken cancellationToken)
    {
        if (count < 10)
            count = 10;

        var to = DateTimeOffset.UtcNow;
        var from = to.AddMinutes(-granularityMinutes * (count + 8));
        var url =
            $"/api/v1/kline/query?symbol={Uri.EscapeDataString(symbol)}" +
            $"&granularity={granularityMinutes}" +
            $"&from={from.ToUnixTimeMilliseconds()}" +
            $"&to={to.ToUnixTimeMilliseconds()}";

        using var response = await httpClient.GetAsync(url, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"KuCoin kline failed for {symbol}: HTTP {(int)response.StatusCode}. Body: {json}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("code", out var codeEl) && codeEl.GetString() != "200000")
            throw new InvalidOperationException($"KuCoin kline returned code {codeEl} for {symbol}. Body: {json}");

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<FuturesKline>();

        foreach (var row in data.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 5)
                continue;

            var openMs = ReadArrayInt64(row, 0);

            if (openMs is null or <= 0)
                continue;

            var a = ReadArrayDecimal(row, 1);
            var b = ReadArrayDecimal(row, 2);
            var c = ReadArrayDecimal(row, 3);
            var d = ReadArrayDecimal(row, 4);
            var vol = row.GetArrayLength() > 5 ? ReadArrayDecimal(row, 5) : 0m;

            if (a is null || b is null || c is null || d is null)
                continue;

            decimal open = a.Value, high, low, close;

            var ochlHigh = c.Value;
            var ochlLow = d.Value;
            var ochlClose = b.Value;
            var ochlValid = ochlHigh >= Math.Max(open, ochlClose) && ochlLow <= Math.Min(open, ochlClose);

            var ohlcHigh = b.Value;
            var ohlcLow = c.Value;
            var ohlcClose = d.Value;
            var ohlcValid = ohlcHigh >= Math.Max(open, ohlcClose) && ohlcLow <= Math.Min(open, ohlcClose);

            if (ohlcValid || !ochlValid)
            {
                high = ohlcHigh;
                low = ohlcLow;
                close = ohlcClose;
            }
            else
            {
                high = ochlHigh;
                low = ochlLow;
                close = ochlClose;
            }

            if (high < low)
                (high, low) = (low, high);

            list.Add(new FuturesKline(
                OpenTime: DateTimeOffset.FromUnixTimeMilliseconds(openMs.Value),
                Open: open,
                High: high,
                Low: low,
                Close: close,
                Volume: vol ?? 0m));
        }

        list.Sort((x, y) => x.OpenTime.CompareTo(y.OpenTime));

        var tf = TimeSpan.FromMinutes(granularityMinutes);

        if (list.Count > 0 && list[^1].OpenTime + tf > DateTimeOffset.UtcNow)
            list.RemoveAt(list.Count - 1);

        if (list.Count > count)
            list = list.Skip(list.Count - count).ToList();

        return list;
    }

    private bool TryMapContract(JsonElement item, out FuturesContractSnapshot contract)
    {
        contract = null!;
        var symbol = ReadString(item, "symbol")?.Trim();

        if (string.IsNullOrWhiteSpace(symbol))
            return false;

        contract = new FuturesContractSnapshot(
            Symbol: symbol,
            Status: ReadString(item, "status"),
            Type: ReadString(item, "type"),
            QuoteCurrency: ReadString(item, "quoteCurrency"),
            SettleCurrency: ReadString(item, "settleCurrency"),
            MarketStage: ReadString(item, "marketStage"),
            IsInverse: ReadBool(item, "isInverse") is true,
            Multiplier: ReadDecimalFlexible(item, "multiplier"),
            TickSize: ReadDecimalFlexible(item, "tickSize"),
            LotSize: ReadDecimalFlexible(item, "lotSize"),
            MarkPrice: ReadDecimalFlexible(item, "markPrice"),
            LastTradePrice: ReadDecimalFlexible(item, "lastTradePrice"),
            TurnoverOf24h: ReadDecimalFlexible(item, "turnoverOf24h"),
            FundingFeeRate: ReadDecimalFlexible(item, "fundingFeeRate"),
            MaxLeverage: ReadDecimalFlexible(item, "maxLeverage"),
            FirstOpenDate: ReadUnixTime(item, "firstOpenDate"));

        return true;
    }

    private static DateTimeOffset? ReadUnixTime(JsonElement parent, string name)
    {
        var ms = ReadInt64(parent, name);

        if (ms is null or <= 0)
            return null;

        return DateTimeOffset.FromUnixTimeMilliseconds(ms.Value);
    }

    private static long? ReadInt64(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var p))
            return null;

        return p.ValueKind switch
        {
            JsonValueKind.Number when p.TryGetInt64(out var i) => i,
            JsonValueKind.Number when p.TryGetDouble(out var d) => (long)d,
            JsonValueKind.String when long.TryParse(p.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
            _ => null
        };
    }

    private static long? ReadArrayInt64(JsonElement array, int index)
    {
        if (index >= array.GetArrayLength())
            return null;

        var p = array[index];

        return p.ValueKind switch
        {
            JsonValueKind.Number when p.TryGetInt64(out var i) => i,
            JsonValueKind.Number when p.TryGetDouble(out var d) => (long)d,
            JsonValueKind.String when long.TryParse(p.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
            _ => null
        };
    }

    private static decimal? ReadArrayDecimal(JsonElement array, int index)
    {
        if (index >= array.GetArrayLength())
            return null;

        var p = array[index];

        if (p.ValueKind == JsonValueKind.Number)
        {
            if (p.TryGetDecimal(out var dec))
                return dec;

            if (p.TryGetDouble(out var dbl))
                return (decimal)dbl;

            return null;
        }

        if (p.ValueKind == JsonValueKind.String)
        {
            var s = p.GetString();

            if (string.IsNullOrWhiteSpace(s))
                return null;

            if (decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return d;
        }

        return null;
    }

    private static string? ReadString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool? ReadBool(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var p))
            return null;

        return p.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(p.GetString(), out var b) ? b : null,
            _ => null
        };
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

    private static decimal? ReadDecimalFlexible(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var p))
            return null;

        if (p.ValueKind == JsonValueKind.Number)
        {
            if (p.TryGetDecimal(out var dec))
                return dec;

            if (p.TryGetDouble(out var dbl))
                return (decimal)dbl;

            return null;
        }

        if (p.ValueKind == JsonValueKind.String)
        {
            var s = p.GetString();
            
            if (string.IsNullOrWhiteSpace(s))
                return null;

            if (decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return d;

            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var dbl))
                return (decimal)dbl;
        }

        return null;
    }

    private static int? ReadInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var p))
            return null;

        return p.ValueKind switch
        {
            JsonValueKind.Number => p.TryGetInt32(out var i) ? i : null,
            JsonValueKind.String => int.TryParse(p.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null,
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
        if (string.IsNullOrWhiteSpace(value))
            return null;
            
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
