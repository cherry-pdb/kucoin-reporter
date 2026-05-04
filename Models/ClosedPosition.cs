using System.ComponentModel.DataAnnotations;

namespace KuCoinFuturesReporter.Models;

public sealed class ClosedPosition
{
    [Key]
    public string CloseId { get; set; } = string.Empty;

    public string Symbol { get; set; } = string.Empty;
    public string SettleCurrency { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public string MarginMode { get; set; } = string.Empty;
    public string? PositionSide { get; set; }

    public decimal? Leverage { get; set; }
    public decimal? Pnl { get; set; }
    public decimal? RealisedGrossCost { get; set; }
    public decimal? TradeFee { get; set; }
    public decimal? FundingFee { get; set; }
    public decimal? OpenPrice { get; set; }
    public decimal? ClosePrice { get; set; }
    public decimal? Roe { get; set; }

    public DateTimeOffset OpenTime { get; set; }
    public DateTimeOffset CloseTime { get; set; }

    public bool TelegramSent { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? TelegramSentAt { get; set; }
}
