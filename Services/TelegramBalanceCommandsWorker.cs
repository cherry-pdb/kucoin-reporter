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
                new BotCommand { Command = "spot", Description = "Spot trade balance" }
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
        "/spot — KuCoin spot trade account";

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
