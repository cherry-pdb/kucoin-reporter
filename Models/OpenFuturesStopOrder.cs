namespace KuCoinFuturesReporter.Models;

public sealed record OpenFuturesStopOrder(
    string Symbol,
    string? Side,
    decimal? StopPrice,
    decimal? Price,
    bool? CloseOrder,
    bool? ReduceOnly);

