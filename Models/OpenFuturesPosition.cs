namespace KuCoinFuturesReporter.Models;

public sealed record OpenFuturesPosition(
    string Symbol,
    string? PositionSide,
    decimal? Leverage,
    decimal? UnrealisedPnl,
    decimal? RealisedPnl,
    decimal? UnrealisedRoePcnt,
    decimal? PosMargin,
    decimal? AvgEntryPrice,
    decimal? MarkPrice,
    decimal? LiquidationPrice);

