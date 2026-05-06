namespace KuCoinFuturesReporter.Models;

public sealed record OpenFuturesPosition(
    string Symbol,
    string? PositionSide,
    decimal? CurrentQty,
    decimal? Leverage,
    decimal? UnrealisedPnl,
    decimal? RealisedPnl,
    decimal? UnrealisedPnlPcnt,
    decimal? UnrealisedRoePcnt,
    decimal? PosMargin,
    decimal? AvgEntryPrice,
    decimal? MarkPrice,
    decimal? LiquidationPrice);

