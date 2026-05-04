using System.Globalization;
using System.Text;
using KuCoinFuturesReporter.Models;
using KuCoinFuturesReporter.Options;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace KuCoinFuturesReporter.Services;

public sealed class TelegramReportService(IOptions<TelegramOptions> options, ILogger<TelegramReportService> logger)
{
    private readonly TelegramOptions _options = options.Value;

    public async Task SendPositionReportAsync(ClosedPosition position, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.BotToken) || string.IsNullOrWhiteSpace(_options.ChatId))
            throw new InvalidOperationException("Telegram settings are empty. Fill Telegram__BotToken and Telegram__ChatId.");

        var bot = new TelegramBotClient(_options.BotToken);
        var text = BuildPositionText(position);

        await bot.SendMessage(
            chatId: _options.ChatId,
            text: text,
            parseMode: ParseMode.Html,
            cancellationToken: cancellationToken);

        logger.LogInformation("Sent Telegram report for {CloseId}", position.CloseId);
    }

    private static string BuildPositionText(ClosedPosition p)
    {
        var pnlEmoji = (p.Pnl ?? 0) >= 0 ? "🟢" : "🔴";
        var duration = p.CloseTime - p.OpenTime;
        var netPnl = p.Pnl;
        var gross = p.RealisedGrossCost;
        var fees = p.TradeFee;
        var funding = p.FundingFee;

        var sb = new StringBuilder();
        sb.AppendLine($"{pnlEmoji} <b>{Escape(p.Symbol)} closed</b>");
        sb.AppendLine();
        sb.AppendLine($"Side: <b>{Escape(p.Side)}</b>");
        sb.AppendLine($"Type: {Escape(p.Type)}");
        sb.AppendLine($"Margin: {Escape(p.MarginMode)} | Leverage: {Fmt(p.Leverage)}x");
        sb.AppendLine();
        sb.AppendLine($"Entry: <code>{Fmt(p.OpenPrice)}</code>");
        sb.AppendLine($"Exit: <code>{Fmt(p.ClosePrice)}</code>");
        sb.AppendLine();
        sb.AppendLine($"Net PnL: <b>{FmtMoney(netPnl)} {Escape(p.SettleCurrency)}</b>");
        sb.AppendLine($"Gross: {FmtMoney(gross)} {Escape(p.SettleCurrency)}");
        sb.AppendLine($"Fee: -{FmtAbs(fees)} {Escape(p.SettleCurrency)}");
        sb.AppendLine($"Funding: {FmtMoney(funding)} {Escape(p.SettleCurrency)}");
        sb.AppendLine();
        sb.AppendLine($"Open: {p.OpenTime:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"Close: {p.CloseTime:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"Duration: {FormatDuration(duration)}");
        sb.AppendLine();
        sb.AppendLine($"ID: <code>{Escape(p.CloseId)}</code>");
        return sb.ToString();
    }

    private static string Fmt(decimal? value) => value?.ToString("0.########", CultureInfo.InvariantCulture) ?? "n/a";
    private static string FmtMoney(decimal? value) => value?.ToString("+0.########;-0.########;0", CultureInfo.InvariantCulture) ?? "n/a";
    private static string FmtAbs(decimal? value) => value is null ? "n/a" : Math.Abs(value.Value).ToString("0.########", CultureInfo.InvariantCulture);
    private static string Escape(string? value) => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds < 0) return "n/a";
        if (duration.TotalDays >= 1) return $"{(int)duration.TotalDays}d {duration.Hours}h {duration.Minutes}m";
        if (duration.TotalHours >= 1) return $"{duration.Hours}h {duration.Minutes}m";
        return $"{duration.Minutes}m {duration.Seconds}s";
    }
}
