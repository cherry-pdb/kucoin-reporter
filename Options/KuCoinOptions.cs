namespace KuCoinFuturesReporter.Options;

public sealed class KuCoinOptions
{
    public const string SectionName = "KuCoin";

    public string BaseUrl { get; set; } = "https://api-futures.kucoin.com";
    public string SpotBaseUrl { get; set; } = "https://api.kucoin.com";
    public string FuturesOverviewCurrency { get; set; } = "USDT";
    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;
    public string ApiPassphrase { get; set; } = string.Empty;
    public int PollIntervalSeconds { get; set; } = 60;
    public int LookbackHoursOnFirstRun { get; set; } = 24;
    public int RequestWindowDays { get; set; } = 7;
}
