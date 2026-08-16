namespace KuCoinFuturesReporter.Options;

public sealed class SignalOptions
{
    public const string SectionName = "Signals";

    public bool Enabled { get; set; } = true;
    public int ScanIntervalMinutes { get; set; } = 60;
    public int ScanAfterHourCloseMinutes { get; set; } = 3;
    public int MaxSymbols { get; set; } = 40;
    public decimal MinTurnover24hUsd { get; set; } = 12_000_000m;
    public int MinListingAgeDays { get; set; } = 21;
    public decimal RiskPercent { get; set; } = 2m;
    public decimal AtrStopMultiplier { get; set; } = 2.0m;
    public decimal RewardRisk { get; set; } = 2.0m;
    public int DonchianPeriod { get; set; } = 20;
    public int MaxActiveSignals { get; set; } = 2;
    public decimal MinQualityScore { get; set; } = 62m;
    public int CooldownHours { get; set; } = 24;
    public int MaxLeverage { get; set; } = 7;
    public decimal MaxFundingAbs { get; set; } = 0.0008m;
    public decimal MinAtrPercent { get; set; } = 0.35m;
    public decimal MaxAtrPercent { get; set; } = 5.5m;
}
