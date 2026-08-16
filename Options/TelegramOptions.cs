namespace KuCoinFuturesReporter.Options;

public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public string BotToken { get; set; } = string.Empty;
    public string ChatId { get; set; } = string.Empty;
    public string ForwardRelayPrivateUserId { get; set; } = string.Empty;
    public bool DisableNotification { get; set; } = true;
    public string AllowedUserIds { get; set; } = string.Empty;
    public string BalanceCommandAllowedUserIds { get; set; } = string.Empty;
}
