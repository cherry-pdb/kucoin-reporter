namespace KuCoinFuturesReporter.Models;

public sealed record FuturesContractSnapshot(
    string Symbol,
    string? Status,
    string? Type,
    string? QuoteCurrency,
    string? SettleCurrency,
    string? MarketStage,
    bool IsInverse,
    decimal? Multiplier,
    decimal? TickSize,
    decimal? LotSize,
    decimal? MarkPrice,
    decimal? LastTradePrice,
    decimal? TurnoverOf24h,
    decimal? FundingFeeRate,
    decimal? MaxLeverage,
    DateTimeOffset? FirstOpenDate);
