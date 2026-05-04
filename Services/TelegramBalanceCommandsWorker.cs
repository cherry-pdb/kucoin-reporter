using System.Globalization;
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
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

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
                            await bot.SendMessage(chatId, FormatFutures(o), cancellationToken: stoppingToken);
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
                            var rows = await spotClient.GetTradeAccountsAsync(stoppingToken);
                            await bot.SendMessage(chatId, FormatSpot(rows), cancellationToken: stoppingToken);
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

    private static string FormatFutures(FuturesAccountOverview o)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Futures ({o.Currency ?? "?"})");
        sb.AppendLine($"Equity: {Fmt(o.AccountEquity)}");
        sb.AppendLine($"Unrealised PnL: {Fmt(o.UnrealisedPnl)}");
        sb.AppendLine($"Margin balance: {Fmt(o.MarginBalance)}");
        sb.AppendLine($"Position margin: {Fmt(o.PositionMargin)}");
        sb.AppendLine($"Order margin: {Fmt(o.OrderMargin)}");
        sb.AppendLine($"Available: {Fmt(o.AvailableBalance)}");
        sb.AppendLine($"Available margin: {Fmt(o.AvailableMargin)}");
        sb.Append($"Max withdraw: {Fmt(o.MaxWithdrawAmount)}");

        return sb.ToString();
    }

    private static string FormatSpot(IReadOnlyList<SpotTradeAccountLine> rows)
    {
        if (rows.Count == 0)
            return "Spot (trade): no non-zero balances.";

        var sb = new StringBuilder();
        sb.AppendLine("Spot (trade):");

        foreach (var r in rows)
            sb.AppendLine(
                $"{r.Currency}: balance {Fmt(r.Balance)}, available {Fmt(r.Available)}, holds {Fmt(r.Holds)}");

        return sb.ToString().TrimEnd();
    }

    private static string Fmt(decimal? v) => v is null ? "n/a" : v.Value.ToString("0.########", En.NumberFormat);
}
