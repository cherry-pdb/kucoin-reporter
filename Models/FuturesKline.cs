namespace KuCoinFuturesReporter.Models;

public sealed record FuturesKline(
    DateTimeOffset OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);
