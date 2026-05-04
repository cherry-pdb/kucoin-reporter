namespace KuCoinFuturesReporter.Models;

public sealed record FuturesAccountOverview(
    string? Currency,
    decimal? AccountEquity,
    decimal? UnrealisedPnl,
    decimal? MarginBalance,
    decimal? PositionMargin,
    decimal? OrderMargin,
    decimal? AvailableBalance,
    decimal? AvailableMargin,
    decimal? MaxWithdrawAmount);

public sealed record SpotTradeAccountLine(string Currency, decimal Balance, decimal Available, decimal Holds);
