using System.Globalization;
using System.Net;
using System.Text;
using KuCoinFuturesReporter.Models;
using KuCoinFuturesReporter.Options;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace KuCoinFuturesReporter.Services;

public sealed class TelegramReportService(IOptions<TelegramOptions> options, ILogger<TelegramReportService> logger)
{
    internal static readonly TimeSpan ReportTimeZoneOffset = TimeSpan.FromHours(3);

    private const string CustomEmojiLongArrowId = "5449683594425410231";
    private const string CustomEmojiShortArrowId = "5447183459602669338";
    private const string CustomEmojiNeutralCircleId = "5451882707875276247";
    private const string CustomEmojiMoneyId = "5409048419211682843";

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private readonly TelegramOptions _options = options.Value;

    public async Task SendPositionReportAsync(ClosedPosition position, CancellationToken cancellationToken)
    {
        var text = BuildPositionText(position);
        await SendHtmlReportAsync(text, cancellationToken, logContextKey: $"CloseId {position.CloseId}");
    }

    public async Task SendHtmlReportAsync(string html, CancellationToken cancellationToken, string? logContextKey = null)
    {
        if (string.IsNullOrWhiteSpace(_options.BotToken) || string.IsNullOrWhiteSpace(_options.ChatId))
            throw new InvalidOperationException("Telegram settings are empty. Fill Telegram__BotToken and Telegram__ChatId.");

        var bot = new TelegramBotClient(_options.BotToken);

        if (!string.IsNullOrWhiteSpace(_options.ForwardRelayPrivateUserId))
        {
            var privateRelay = _options.ForwardRelayPrivateUserId.Trim();
            var inbox = await bot.SendMessage(
                chatId: privateRelay,
                text: html,
                parseMode: ParseMode.Html,
                disableNotification: true,
                cancellationToken: cancellationToken);

            await bot.ForwardMessage(
                chatId: _options.ChatId,
                fromChatId: privateRelay,
                messageId: inbox.MessageId,
                cancellationToken: cancellationToken);

            if (logContextKey is null)
                logger.LogInformation("Telegram relay DM {DmMessageId} forwarded", inbox.MessageId);
            else
                logger.LogInformation("Telegram ({Context}) relay DM {DmMessageId} forwarded", logContextKey, inbox.MessageId);
        }
        else
        {
            await bot.SendMessage(
                chatId: _options.ChatId,
                text: html,
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);

            if (logContextKey is null)
                logger.LogInformation("Sent Telegram HTML report");
            else
                logger.LogInformation("Sent Telegram HTML ({Context})", logContextKey);
        }
    }

    internal static string TgEmojiDirectionForSign(decimal signedTotal)
    {
        if (signedTotal > 0)
            return TgEmoji(CustomEmojiLongArrowId, "🔼");

        if (signedTotal < 0)
            return TgEmoji(CustomEmojiShortArrowId, "🔽");

        return TgEmoji(CustomEmojiNeutralCircleId, "⚪");
    }

    internal static string TgEmojiMoneyMarkup() => TgEmoji(CustomEmojiMoneyId, "💵");

    internal static string FormatPnlWeeklyMagnitudeRu(decimal value)
    {
        var v = Math.Abs(value);

        if (v == 0m)
            return 0m.ToString("00.00", Ru.NumberFormat);

        return v.ToString("0.00", Ru.NumberFormat);
    }

    private static string BuildPositionText(ClosedPosition p)
    {
        var baseSymbol = StripContractSuffix((p.Symbol ?? string.Empty).Trim());
        var pnlText = FormatPnlRu(p.Pnl);

        var closeLocal = p.CloseTime.ToOffset(ReportTimeZoneOffset);

        var lev = FormatLeverage(p.Leverage);
        var sideLine = string.IsNullOrWhiteSpace(p.Side) ? $"Side: ? {lev}x" : $"Side: {p.Side.Trim()} {lev}x";

        var arrow = p.Pnl is { } pnlSigned
            ? TgEmojiDirectionForSign(pnlSigned)
            : TgEmoji(CustomEmojiNeutralCircleId, "⚪");
        var money = TgEmoji(CustomEmojiMoneyId, "💵");

        var sb = new StringBuilder();
        sb.Append(arrow);
        sb.Append('$');
        sb.Append(WebUtility.HtmlEncode(baseSymbol));
        sb.Append(' ');
        sb.Append(WebUtility.HtmlEncode(pnlText));
        sb.Append(money);
        sb.Append('\n');
        sb.Append(WebUtility.HtmlEncode(sideLine));
        sb.Append('\n');
        sb.Append("Close: ");
        sb.Append(WebUtility.HtmlEncode(closeLocal.ToString("dd.MM.yyyy HH:mm:ss", Ru.DateTimeFormat)));

        return sb.ToString();
    }

    private static string TgEmoji(string emojiId, string fallbackGlyph) =>
        $"<tg-emoji emoji-id=\"{emojiId}\">{fallbackGlyph}</tg-emoji>";

    private static string FormatPnlRu(decimal? pnl)
    {
        if (pnl is null)
            return "n/a";

        return pnl.Value.ToString("+0.00;-0.00;0", Ru.NumberFormat);
    }

    private static string FormatLeverage(decimal? leverage)
    {
        if (leverage is null)
            return "?";

        var v = leverage.Value;

        return v % 1m == 0 ? v.ToString("0", CultureInfo.InvariantCulture) : v.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string StripContractSuffix(string symbol)
    {
        if (string.IsNullOrEmpty(symbol))
            return symbol;

        var upper = symbol.ToUpperInvariant();

        foreach (var suffix in new[] { "USDTM", "USDCM", "USDM", "USDT", "USDC", "USD" })
            if (upper.EndsWith(suffix, StringComparison.Ordinal) && upper.Length > suffix.Length)
                return symbol[..^suffix.Length].TrimEnd();

        return symbol;
    }
}
