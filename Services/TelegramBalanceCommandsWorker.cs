using System.Globalization;
using System.Net;
using System.Text;
using KuCoinFuturesReporter.Models;
using KuCoinFuturesReporter.Options;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace KuCoinFuturesReporter.Services;

public sealed class TelegramBalanceCommandsWorker(
    IOptionsMonitor<TelegramOptions> telegramOptions,
    KuCoinFuturesClient futuresClient,
    KuCoinSpotClient spotClient,
    ILogger<TelegramBalanceCommandsWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = telegramOptions.CurrentValue;
        var allowed = ParseAllowedUserIds(opts.BalanceCommandAllowedUserIds);
        
        if (string.IsNullOrWhiteSpace(opts.BotToken) || allowed.Count == 0)
        {
            logger.LogWarning("Telegram balance commands disabled: set Telegram:BotToken and Telegram:BalanceCommandAllowedUserIds (comma-separated Telegram user ids).");
            return;
        }

        var bot = new TelegramBotClient(opts.BotToken);

        try
        {
            await bot.DeleteWebhook(cancellationToken: stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "DeleteWebhook failed (safe to ignore if webhook was not set).");
        }

        try
        {
            await bot.SetMyCommands(
            [
                new BotCommand { Command = "start", Description = "Show command list" },
                new BotCommand { Command = "commands", Description = "Show command list" },
                new BotCommand { Command = "futures", Description = "KuCoin Futures balance" },
                new BotCommand { Command = "spot", Description = "Spot trade balance" },
                new BotCommand { Command = "positions", Description = "Open Futures positions" }
            ],
            cancellationToken: stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SetMyCommands failed; the command menu in Telegram may be empty.");
        }

        var offset = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updates = await bot.GetUpdates(
                    offset: offset,
                    limit: 100,
                    timeout: 30,
                    allowedUpdates: [UpdateType.Message],
                    cancellationToken: stoppingToken);

                foreach (var update in updates)
                {
                    offset = update.Id + 1;
                    
                    if (update.Message is not { } msg || msg.Text is null)
                        continue;

                    if (msg.Chat.Type != ChatType.Private)
                        continue;

                    if (msg.From?.Id is null || !allowed.Contains(msg.From.Id))
                        continue;

                    var text = msg.Text;
                    var chatId = msg.Chat.Id;

                    if (MatchesCommand(text, "/start") || MatchesCommand(text, "/commands"))
                    {
                        await bot.SendMessage(
                            chatId,
                            BuildCommandListText(),
                            cancellationToken: stoppingToken);
                        continue;
                    }

                    if (MatchesCommand(text, "/futures"))
                    {
                        try
                        {
                            var o = await futuresClient.GetFuturesAccountOverviewAsync(stoppingToken);
                            await bot.SendMessage(
                                chatId,
                                BuildFuturesBalanceHtml(o),
                                parseMode: ParseMode.Html,
                                cancellationToken: stoppingToken);
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Futures balance command failed");
                            await bot.SendMessage(chatId, $"KuCoin Futures error: {ex.Message}", cancellationToken: stoppingToken);
                        }

                        continue;
                    }

                    if (MatchesCommand(text, "/spot"))
                    {
                        try
                        {
                            var rowsTask = spotClient.GetTradeAccountsAsync(stoppingToken);
                            var pricesTask = spotClient.GetSpotUsdtPricesAsync(stoppingToken);
                            await Task.WhenAll(rowsTask, pricesTask);
                            var html = BuildSpotBalanceHtml(rowsTask.Result, pricesTask.Result);
                            await bot.SendMessage(
                                chatId,
                                html,
                                parseMode: ParseMode.Html,
                                cancellationToken: stoppingToken);
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Spot balance command failed");
                            await bot.SendMessage(chatId, $"KuCoin spot error: {ex.Message}", cancellationToken: stoppingToken);
                        }

                        continue;
                    }

                    if (MatchesCommand(text, "/positions"))
                    {
                        try
                        {
                            var positionsTask = futuresClient.GetOpenPositionsAsync(stoppingToken);
                            var stopOrdersTask = futuresClient.GetOpenStopOrdersAsync(stoppingToken);
                            await Task.WhenAll(positionsTask, stopOrdersTask);
                            var html = BuildOpenPositionsHtml(positionsTask.Result, stopOrdersTask.Result);
                            await bot.SendMessage(
                                chatId,
                                html,
                                parseMode: ParseMode.Html,
                                cancellationToken: stoppingToken);
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Positions command failed");
                            await bot.SendMessage(chatId, $"KuCoin Futures error: {ex.Message}", cancellationToken: stoppingToken);
                        }

                        continue;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Telegram GetUpdates loop failed");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private static string BuildCommandListText() =>
        "Available commands:\n" +
        "/commands — show this list\n" +
        "/futures — KuCoin futures balance\n" +
        "/spot — KuCoin spot trade account\n" +
        "/positions — open futures positions";

    private static HashSet<long> ParseAllowedUserIds(string raw)
    {
        var set = new HashSet<long>();

        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                set.Add(id);

        return set;
    }

    private static bool MatchesCommand(string text, string command)
    {
        var line = text.Trim();
        var space = line.IndexOf(' ');
        var head = space < 0 ? line : line[..space];
        var at = head.IndexOf('@');

        if (at > 0)
            head = head[..at];

        return head.Equals(command, StringComparison.OrdinalIgnoreCase);
    }

    private const decimal MinSpotUsdToShow = 1m;

    private static string BuildFuturesBalanceHtml(FuturesAccountOverview o)
    {
        var money = TelegramReportService.TgEmojiMoneyMarkup();
        var sb = new StringBuilder();
        sb.AppendLine("<b>Futures</b>");
        sb.AppendLine();
        AppendFuturesLine(sb, "Total", o.AccountEquity, money);
        AppendFuturesLine(sb, "Unrealised PnL", o.UnrealisedPnl, money);
        AppendFuturesLine(sb, "Margin balance", o.MarginBalance, money);
        AppendFuturesLine(sb, "Position margin", o.PositionMargin, money);
        AppendFuturesLine(sb, "Available", o.AvailableBalance, money);
        
        return sb.ToString().TrimEnd();
    }

    private static void AppendFuturesLine(StringBuilder sb, string label, decimal? value, string moneyMarkup)
    {
        sb.Append(WebUtility.HtmlEncode(label));
        sb.Append(": ");

        if (value is null)
        {
            sb.AppendLine("n/a");
            return;
        }

        sb.Append(WebUtility.HtmlEncode(FormatUsdNumber(value.Value)));
        sb.Append(moneyMarkup);
        sb.AppendLine();
    }

    private static string BuildSpotBalanceHtml(
        IReadOnlyList<SpotTradeAccountLine> rows,
        IReadOnlyDictionary<string, decimal> usdtPrices)
    {
        var money = TelegramReportService.TgEmojiMoneyMarkup();
        var coins = new List<(string Currency, decimal Balance, decimal Usd)>();

        foreach (var r in rows)
        {
            if (!TryGetSpotUsdPerUnit(usdtPrices, r.Currency, out var pricePerUnit))
                continue;

            var usd = r.Balance * pricePerUnit;

            if (usd < MinSpotUsdToShow)
                continue;

            coins.Add((r.Currency, r.Balance, usd));
        }

        coins.Sort((a, b) => b.Usd.CompareTo(a.Usd));

        if (coins.Count == 0)
            return "<b>Spot</b>\n\nNo coins with trade balance ≥ $1.00 USDT (approx.).";

        var totalUsd = coins.Sum(c => c.Usd);
        var sb = new StringBuilder();
        sb.AppendLine("<b>Spot</b>");
        sb.AppendLine();
        sb.Append("Total: ");
        sb.Append(WebUtility.HtmlEncode(FormatUsdNumber(totalUsd)));
        sb.Append(money);
        sb.AppendLine();
        sb.AppendLine("Coins:");

        foreach (var c in coins)
        {
            sb.Append(" • ");
            sb.Append(WebUtility.HtmlEncode(c.Currency));
            sb.Append(": ");
            sb.Append(WebUtility.HtmlEncode(FormatCoinAmount(c.Balance)));
            sb.Append(" ≈ ");
            sb.Append(WebUtility.HtmlEncode(FormatUsdNumber(c.Usd)));
            sb.Append(money);
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildOpenPositionsHtml(
        IReadOnlyList<OpenFuturesPosition> positions,
        IReadOnlyList<OpenFuturesStopOrder> stopOrders)
    {
        if (positions.Count == 0)
            return "<b>Positions (0)</b>\n\nNo open positions.";

        var money = TelegramReportService.TgEmojiMoneyMarkup();
        var sb = new StringBuilder();
        sb.AppendLine($"<b>Positions ({positions.Count})</b>");
        sb.AppendLine();

        foreach (var p in positions)
        {
            var baseSymbol = StripContractSuffix(p.Symbol);
            var side = string.IsNullOrWhiteSpace(p.PositionSide) ? "?" : p.PositionSide!.Trim().ToUpperInvariant();
            var lev = p.Leverage is null ? "?" : FormatLeverage(p.Leverage.Value);

            sb.Append('$');
            sb.Append(WebUtility.HtmlEncode(baseSymbol));
            sb.Append(' ');
            sb.Append(WebUtility.HtmlEncode(side));
            sb.Append(' ');
            sb.Append(WebUtility.HtmlEncode(lev));
            sb.AppendLine("x");

            AppendSignedMoneyLine(sb, "Unrealised PNL", p.UnrealisedPnl, money, treatNullAsEmpty: false);
            AppendSignedMoneyLine(sb, "Realised PNL", p.RealisedPnl, money, treatNullAsEmpty: false);

            var overall = (p.UnrealisedPnl ?? 0m) + (p.RealisedPnl ?? 0m);
            AppendSignedMoneyLine(sb, "Overall PNL", overall, money, treatNullAsEmpty: false);

            var roiPct = ComputeLeveragedRoiPercent(p);

            if (roiPct is not null)
            {
                sb.Append("ROI: ");
                sb.Append(WebUtility.HtmlEncode(roiPct.Value.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture)));
                sb.AppendLine("%");
            }

            AppendMoneyLine(sb, "Margin", p.PosMargin, money, showNaWhenNull: true);

            if (p.AvgEntryPrice is not null)
            {
                sb.Append("Entry price: ");
                sb.Append(WebUtility.HtmlEncode(FormatPrice(p.AvgEntryPrice.Value)));
                sb.AppendLine();
            }

            var (tp, sl) = TryInferTpSlPrices(stopOrders, p);

            if (tp is not null)
            {
                sb.Append("TP: ");
                sb.Append(WebUtility.HtmlEncode(FormatPrice(tp.Value)));
                sb.AppendLine();
                AppendCloseAtTarget(sb, "Close in TP", p, tp.Value, money);
            }

            if (sl is not null)
            {
                sb.Append("SL: ");
                sb.Append(WebUtility.HtmlEncode(FormatPrice(sl.Value)));
                sb.AppendLine();
                AppendCloseAtTarget(sb, "Close in SL", p, sl.Value, money);
            }

            if (p.MarkPrice is not null)
            {
                sb.Append("Mark price: ");
                sb.Append(WebUtility.HtmlEncode(FormatPrice(p.MarkPrice.Value)));
                sb.AppendLine();
            }

            if (p.LiquidationPrice is not null)
            {
                sb.Append("Liquidation price: ");
                sb.Append(WebUtility.HtmlEncode(FormatPrice(p.LiquidationPrice.Value)));
                sb.AppendLine();
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static (decimal? Tp, decimal? Sl) TryInferTpSlPrices(IReadOnlyList<OpenFuturesStopOrder> allStops, OpenFuturesPosition p)
    {
        if (p.AvgEntryPrice is null)
            return (null, null);

        var entry = p.AvgEntryPrice.Value;
        if (entry <= 0m)
            return (null, null);

        var side = (p.PositionSide ?? string.Empty).Trim().ToUpperInvariant();
        var closeSide = side == "SHORT" ? "BUY" : "SELL"; // long closes with sell, short closes with buy (best effort)

        decimal? tp = null;
        decimal? sl = null;

        foreach (var o in allStops)
        {
            if (!o.Symbol.Equals(p.Symbol, StringComparison.OrdinalIgnoreCase))
                continue;

            if (o.CloseOrder is false && o.ReduceOnly is false)
                continue;

            var orderSide = (o.Side ?? string.Empty).Trim().ToUpperInvariant();
            if (!string.IsNullOrWhiteSpace(orderSide) && orderSide != closeSide)
                continue;

            var trigger = o.StopPrice ?? o.Price;
            if (trigger is null || trigger.Value <= 0m)
                continue;

            if (side == "SHORT")
            {
                // For shorts: lower trigger = TP, higher trigger = SL
                if (trigger.Value < entry && (tp is null || trigger.Value < tp.Value))
                    tp = trigger.Value;
                if (trigger.Value > entry && (sl is null || trigger.Value > sl.Value))
                    sl = trigger.Value;
            }
            else
            {
                // Default to LONG-like behavior when unknown: higher trigger = TP, lower trigger = SL
                if (trigger.Value > entry && (tp is null || trigger.Value > tp.Value))
                    tp = trigger.Value;
                if (trigger.Value < entry && (sl is null || trigger.Value < sl.Value))
                    sl = trigger.Value;
            }
        }

        return (tp, sl);
    }

    private static void AppendCloseAtTarget(StringBuilder sb, string label, OpenFuturesPosition p, decimal targetPrice, string moneyMarkup)
    {
        if (p.AvgEntryPrice is null || p.Leverage is null || p.PosMargin is null)
            return;

        var entry = p.AvgEntryPrice.Value;
        
        if (entry <= 0m)
            return;

        var side = (p.PositionSide ?? string.Empty).Trim().ToUpperInvariant();
        var priceMove = side == "SHORT"
            ? (entry - targetPrice) / entry
            : (targetPrice - entry) / entry;

        var roiPct = priceMove * p.Leverage.Value * 100m;
        var pnlAtTarget = p.PosMargin.Value * (roiPct / 100m);

        sb.Append(WebUtility.HtmlEncode(label));
        sb.Append(": ");
        sb.Append(WebUtility.HtmlEncode(roiPct.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture)));
        sb.Append("% ");
        sb.Append(TelegramReportService.TgEmojiDirectionForSign(pnlAtTarget));
        sb.Append(WebUtility.HtmlEncode(FormatUsdNumber(pnlAtTarget)));
        sb.Append(moneyMarkup);
        sb.AppendLine();
    }

    private static void AppendSignedMoneyLine(StringBuilder sb, string label, decimal? value, string moneyMarkup, bool treatNullAsEmpty = true)
    {
        if (value is null && treatNullAsEmpty)
            return;

        sb.Append(WebUtility.HtmlEncode(label));
        sb.Append(": ");

        var v = value ?? 0m;
        sb.Append(TelegramReportService.TgEmojiDirectionForSign(v));
        sb.Append(WebUtility.HtmlEncode(FormatUsdNumber(v)));
        sb.Append(moneyMarkup);
        sb.AppendLine();
    }

    private static void AppendMoneyLine(StringBuilder sb, string label, decimal? value, string moneyMarkup, bool showNaWhenNull = false)
    {
        if (value is null)
        {
            if (!showNaWhenNull)
                return;

            sb.Append(WebUtility.HtmlEncode(label));
            sb.Append(": ");
            sb.AppendLine("n/a");
            
            return;
        }

        sb.Append(WebUtility.HtmlEncode(label));
        sb.Append(": ");
        sb.Append(WebUtility.HtmlEncode(FormatUsdNumber(value.Value)));
        sb.Append(moneyMarkup);
        sb.AppendLine();
    }

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

    private static string FormatLeverage(decimal v) =>
        v % 1m == 0 ? v.ToString("0", CultureInfo.InvariantCulture) : v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string FormatPrice(decimal v) =>
        v.ToString("0.########", CultureInfo.InvariantCulture);

    private static string FormatSignedPercent(decimal v) =>
        (v / 100m).ToString("+0.00%;-0.00%;0%", CultureInfo.InvariantCulture);

    private static decimal? ComputeLeveragedRoiPercent(OpenFuturesPosition p)
    {
        if (p.Leverage is null)
            return null;

        if (p.UnrealisedPnlPcnt is not null)
            return p.UnrealisedPnlPcnt.Value * p.Leverage.Value * 100m;

        if (p.UnrealisedRoePcnt is not null)
            return p.UnrealisedRoePcnt.Value * p.Leverage.Value;

        return null;
    }

    private static bool TryGetSpotUsdPerUnit(IReadOnlyDictionary<string, decimal> prices, string currency, out decimal usdPerUnit)
    {
        usdPerUnit = 0m;

        if (string.IsNullOrWhiteSpace(currency))
            return false;

        var c = currency.Trim().ToUpperInvariant();

        if (c is "USDT" or "USDC" or "USDD" or "TUSD" or "USDP" or "DAI" or "BUSD" or "USDG" or "PYUSD" or "USDS")
        {
            usdPerUnit = 1m;
            return true;
        }

        return prices.TryGetValue(c, out usdPerUnit) && usdPerUnit > 0m;
    }

    private static string FormatUsdNumber(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    private static string FormatCoinAmount(decimal amount)
    {
        var s = amount.ToString("0.########", CultureInfo.InvariantCulture);
        
        if (!s.Contains('.'))
            return s;

        return s.TrimEnd('0').TrimEnd('.');
    }
}
