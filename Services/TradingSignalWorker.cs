using KuCoinFuturesReporter.Options;
using Microsoft.Extensions.Options;

namespace KuCoinFuturesReporter.Services;

public sealed class TradingSignalWorker(
    TradingSignalService signals,
    TelegramReportService telegram,
    IOptionsMonitor<SignalOptions> signalOptions,
    ILogger<TradingSignalWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = signalOptions.CurrentValue;

        if (!opts.Enabled)
        {
            logger.LogInformation("Trading signals disabled (Signals:Enabled=false).");
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(25), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var first = true;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!first)
            {
                try
                {
                    await Task.Delay(DelayUntilNextHourScan(signalOptions.CurrentValue), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            first = false;

            try
            {
                var fresh = await signals.ScanAndAlertCandidatesAsync(stoppingToken);
                
                if (fresh.Count == 0)
                    continue;

                await telegram.SendPrivateHtmlAsync(
                    TradingSignalService.BuildAlertHtml(fresh),
                    stoppingToken,
                    logContextKey: $"signals {string.Join(",", fresh.Select(s => s.Symbol))}");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Trading signal scan failed");
            }
        }
    }

    private static TimeSpan DelayUntilNextHourScan(SignalOptions options)
    {
        var afterClose = Math.Clamp(options.ScanAfterHourCloseMinutes, 1, 20);
        var now = DateTimeOffset.UtcNow;
        var nextHour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero).AddHours(1);
        var target = nextHour.AddMinutes(afterClose);
        var wait = target - now;
        
        return wait < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : wait;
    }
}
