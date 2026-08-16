namespace KuCoinFuturesReporter.Models;

public sealed record TradeSignal(
    string Symbol,
    string Side,
    DateTimeOffset BarTime,
    decimal Entry,
    decimal Stop,
    decimal TakeProfit,
    decimal Atr,
    decimal RiskUsd,
    decimal NotionalUsd,
    int Leverage,
    decimal Strength,
    decimal QualityScore,
    string Reason,
    bool HasOpenPosition);
