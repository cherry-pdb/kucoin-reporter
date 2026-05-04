using System.Globalization;
using System.Text;
using KuCoinFuturesReporter.Models;
using KuCoinFuturesReporter.Options;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace KuCoinFuturesReporter.Services;

public sealed class TelegramReportService(IOptions<TelegramOptions> options, ILogger<TelegramReportService> logger)
{
    private const char CustomEmojiPlaceholder = '\u2063';

    private const string CustomEmojiLongArrowId = "5449683594425410231";
    private const string CustomEmojiShortArrowId = "5447183459602669338";
    private const string CustomEmojiNeutralCircleId = "5451882707875276247";
    private const string CustomEmojiMoneyId = "5409048419211682843";

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private static readonly TimeSpan CloseDisplayOffset = TimeSpan.FromHours(3);

    private readonly TelegramOptions _options = options.Value;

    public async Task SendPositionReportAsync(ClosedPosition position, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.BotToken) || string.IsNullOrWhiteSpace(_options.ChatId))
            throw new InvalidOperationException("Telegram settings are empty. Fill Telegram__BotToken and Telegram__ChatId.");

        var bot = new TelegramBotClient(_options.BotToken);
        var (text, entities) = BuildPositionMessage(position);

        await bot.SendMessage(
            chatId: _options.ChatId,
            text: text,
            entities: entities,
            cancellationToken: cancellationToken);

        logger.LogInformation("Sent Telegram report for {CloseId}", position.CloseId);
    }

    private static (string Text, IEnumerable<MessageEntity> Entities) BuildPositionMessage(ClosedPosition p)
    {
        var sideUpper = (p.Side ?? string.Empty).ToUpperInvariant();
        var directionEmojiId =
            sideUpper.Contains("LONG", StringComparison.Ordinal) ? CustomEmojiLongArrowId
            : sideUpper.Contains("SHORT", StringComparison.Ordinal) ? CustomEmojiShortArrowId
            : CustomEmojiNeutralCircleId;

        var baseSymbol = StripContractSuffix((p.Symbol ?? string.Empty).Trim());
        var pnlText = FormatPnlRu(p.Pnl);

        var closeLocal = p.CloseTime.ToOffset(CloseDisplayOffset);

        var lev = FormatLeverage(p.Leverage);
        var sideLine = string.IsNullOrWhiteSpace(p.Side) ? $"Side: ? {lev}x" : $"Side: {p.Side.Trim()} {lev}x";

        var entities = new List<MessageEntity>();

        var sb = new StringBuilder();
        sb.Append(CustomEmojiPlaceholder);
        sb.Append('$');
        sb.Append(baseSymbol);
        sb.Append(' ');
        sb.Append(pnlText);
        sb.Append(CustomEmojiPlaceholder);
        var moneyPlaceholderOffset = sb.Length - 1;

        entities.Add(new MessageEntity
        {
            Type = MessageEntityType.CustomEmoji,
            Offset = 0,
            Length = 1,
            CustomEmojiId = directionEmojiId
        });
        entities.Add(new MessageEntity
        {
            Type = MessageEntityType.CustomEmoji,
            Offset = moneyPlaceholderOffset,
            Length = 1,
            CustomEmojiId = CustomEmojiMoneyId
        });

        sb.Append('\n');
        sb.Append(sideLine);
        sb.Append('\n');
        sb.Append($"Close: {closeLocal:dd.MM.yyyy HH:mm:ss}");

        return (sb.ToString(), entities);
    }

    private static string FormatPnlRu(decimal? pnl)
    {
        if (pnl is null) return "n/a";
        return pnl.Value.ToString("+0.00;-0.00;0", Ru.NumberFormat);
    }

    private static string FormatLeverage(decimal? leverage)
    {
        if (leverage is null) return "?";
        var v = leverage.Value;
        return v % 1m == 0 ? v.ToString("0", CultureInfo.InvariantCulture) : v.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string StripContractSuffix(string symbol)
    {
        if (string.IsNullOrEmpty(symbol)) return symbol;
        var upper = symbol.ToUpperInvariant();

        foreach (var suffix in new[] { "USDTM", "USDCM", "USDM", "USDT", "USDC", "USD" })
        {
            if (upper.EndsWith(suffix, StringComparison.Ordinal) && upper.Length > suffix.Length)
                return symbol[..^suffix.Length].TrimEnd();
        }

        return symbol;
    }
}
