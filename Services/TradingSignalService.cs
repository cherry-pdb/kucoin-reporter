using System.Net;
using System.Text;
using KuCoinFuturesReporter.Models;
using KuCoinFuturesReporter.Options;
using Microsoft.Extensions.Options;

namespace KuCoinFuturesReporter.Services;

public sealed class TradingSignalScanResult
{
    public DateTimeOffset ScannedAt { get; init; }
    public IReadOnlyList<TradeSignal> Signals { get; init; } = [];
    public string? Error { get; init; }
}

public sealed class TradingSignalService(
    KuCoinFuturesClient futuresClient,
    IOptionsMonitor<SignalOptions> signalOptions,
    ILogger<TradingSignalService> logger)
{
    private readonly object _gate = new();
    private TradingSignalScanResult _latest = new() { ScannedAt = DateTimeOffset.MinValue };
    private readonly Dictionary<string, DateTimeOffset> _alerted = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<TradeSignal>> ScanAndAlertCandidatesAsync(CancellationToken cancellationToken)
    {
        var options = signalOptions.CurrentValue;
        var result = await ScanAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var cooldown = TimeSpan.FromHours(Math.Max(1, options.CooldownHours));
        var fresh = new List<TradeSignal>();

        lock (_gate)
        {
            foreach (var key in _alerted.Where(kv => now - kv.Value > cooldown).Select(kv => kv.Key).ToList())
                _alerted.Remove(key);

            foreach (var signal in result.Signals)
            {
                if (signal.HasOpenPosition)
                    continue;

                var key = AlertKey(signal);

                if (_alerted.ContainsKey(key))
                    continue;

                _alerted[key] = now;
                fresh.Add(signal);
            }
        }

        return fresh;
    }

    public async Task<TradingSignalScanResult> ScanAsync(CancellationToken cancellationToken)
    {
        var options = signalOptions.CurrentValue;

        try
        {
            var contractsTask = futuresClient.GetActiveContractsAsync(cancellationToken);
            var positionsTask = futuresClient.GetOpenPositionsAsync(cancellationToken);
            var overviewTask = futuresClient.GetFuturesAccountOverviewAsync(cancellationToken);
            await Task.WhenAll(contractsTask, positionsTask, overviewTask);

            var openSymbols = positionsTask.Result
                .Select(p => p.Symbol)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var minAge = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, options.MinListingAgeDays));
            var universe = contractsTask.Result
                .Where(c => IsTradablePerp(c, options, minAge))
                .OrderByDescending(c => c.TurnoverOf24h ?? 0m)
                .Take(Math.Max(8, options.MaxSymbols))
                .ToList();

            var found = new List<TradeSignal>();
            using var limiter = new SemaphoreSlim(3);

            var tasks = universe.Select(async contract =>
            {
                await limiter.WaitAsync(cancellationToken);

                try
                {
                    var h1 = await futuresClient.GetKlinesAsync(contract.Symbol, 60, 80, cancellationToken);
                    var h4 = await futuresClient.GetKlinesAsync(contract.Symbol, 240, 240, cancellationToken);

                    var signal = TrendBreakoutSignalEngine.Evaluate(
                        contract,
                        h1,
                        h4,
                        overviewTask.Result.AccountEquity,
                        openSymbols.Contains(contract.Symbol),
                        options);

                    if (signal is not null)
                        lock (found)
                            found.Add(signal);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Signal scan skipped {Symbol}", contract.Symbol);
                }
                finally
                {
                    limiter.Release();
                }
            });

            await Task.WhenAll(tasks);

            var ranked = found
                .OrderByDescending(s => s.QualityScore)
                .ThenByDescending(s => s.Strength)
                .Take(Math.Max(1, options.MaxActiveSignals))
                .ToList();

            var snapshot = new TradingSignalScanResult
            {
                ScannedAt = DateTimeOffset.UtcNow,
                Signals = ranked
            };

            lock (_gate)
                _latest = snapshot;

            logger.LogInformation(
                "Signal scan done: {Universe} contracts, {Found} qualified, {Kept} kept",
                universe.Count,
                found.Count,
                ranked.Count);

            return snapshot;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Signal scan failed");
            var failed = new TradingSignalScanResult
            {
                ScannedAt = DateTimeOffset.UtcNow,
                Error = ex.Message
            };

            lock (_gate)
                _latest = failed;
            
            return failed;
        }
    }

    public static string BuildAlertHtml(IReadOnlyList<TradeSignal> signals)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<b>Setup</b>");
        sb.AppendLine("Best ranked breakout(s) this hour. Not a guaranteed win. You open it on KuCoin; stop is mandatory.");
        sb.AppendLine();

        var money = TelegramReportService.TgEmojiMoneyMarkup();
        var rank = 1;

        foreach (var s in signals)
        {
            var arrow = s.Side == "LONG"
                ? TelegramReportService.TgEmojiDirectionForSign(1m)
                : TelegramReportService.TgEmojiDirectionForSign(-1m);

            sb.Append(rank);
            sb.Append(". ");
            sb.Append(arrow);
            sb.Append('$');
            sb.Append(WebUtility.HtmlEncode(StripContractSuffix(s.Symbol)));
            sb.Append(' ');
            sb.Append(WebUtility.HtmlEncode(s.Side));
            sb.Append(' ');
            sb.Append(WebUtility.HtmlEncode(s.Leverage.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            sb.AppendLine("x isolated");

            sb.Append("Trust: <b>");
            sb.Append(WebUtility.HtmlEncode(s.TrustLevel));
            sb.Append("</b> ");
            sb.Append(WebUtility.HtmlEncode(s.TrustMarker));
            sb.Append(" (");
            sb.Append(WebUtility.HtmlEncode(s.QualityScore.ToString("0", System.Globalization.CultureInfo.InvariantCulture)));
            sb.AppendLine("/100)");
            sb.Append("Entry: ");
            sb.AppendLine(WebUtility.HtmlEncode(FormatPrice(s.Entry)));
            sb.Append("SL: ");
            sb.AppendLine(WebUtility.HtmlEncode(FormatPrice(s.Stop)));
            sb.Append("TP: ");
            sb.AppendLine(WebUtility.HtmlEncode(FormatPrice(s.TakeProfit)));

            if (s.RiskUsd > 0m)
            {
                sb.Append("Risk: ");
                sb.Append(WebUtility.HtmlEncode(s.RiskUsd.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)));
                sb.Append(money);
                sb.AppendLine();
            }

            if (s.NotionalUsd > 0m)
            {
                sb.Append("Notional: ");
                sb.Append(WebUtility.HtmlEncode(s.NotionalUsd.ToString("0", System.Globalization.CultureInfo.InvariantCulture)));
                sb.Append(money);
                sb.AppendLine();
            }

            sb.Append(WebUtility.HtmlEncode(s.Reason));
            sb.AppendLine();
            sb.Append("KuCoin: ");
            sb.AppendLine(WebUtility.HtmlEncode(s.Symbol));
            sb.AppendLine();
            rank++;
        }

        return sb.ToString().TrimEnd();
    }

    private static bool IsTradablePerp(FuturesContractSnapshot c, SignalOptions options, DateTimeOffset minAge)
    {
        if (c.IsInverse)
            return false;

        if (!string.Equals(c.Status, "Open", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.Equals(c.SettleCurrency, "USDT", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(c.QuoteCurrency, "USDT", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(c.MarketStage)
            && !c.MarketStage.Equals("NORMAL", StringComparison.OrdinalIgnoreCase))
            return false;

        var type = c.Type ?? string.Empty;

        if (!type.Equals("FFWCSX", StringComparison.OrdinalIgnoreCase) && !type.Contains("FFW", StringComparison.OrdinalIgnoreCase))
            return false;

        if (c.FirstOpenDate is null || c.FirstOpenDate > minAge)
            return false;

        if ((c.TurnoverOf24h ?? 0m) < options.MinTurnover24hUsd)
            return false;

        return true;
    }

    private static string AlertKey(TradeSignal signal) => $"{signal.Symbol}:{signal.Side}";

    private static string StripContractSuffix(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return symbol;

        var upper = symbol.ToUpperInvariant();

        foreach (var suffix in new[] { "USDTM", "USDCM", "USDM", "USDT", "USDC", "USD" })
            if (upper.EndsWith(suffix, StringComparison.Ordinal) && upper.Length > suffix.Length)
                return symbol[..^suffix.Length].TrimEnd();

        return symbol;
    }

    private static string FormatPrice(decimal v)
    {
        var abs = Math.Abs(v);

        if (abs >= 1000m)
            return v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        
        if (abs >= 1m)
            return v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        
        return v.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture);
    }
}
