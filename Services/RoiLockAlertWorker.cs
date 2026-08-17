using System.Globalization;
using System.Net;
using System.Text;
using KuCoinFuturesReporter.Models;
using KuCoinFuturesReporter.Options;
using Microsoft.Extensions.Options;

namespace KuCoinFuturesReporter.Services;

public sealed class RoiLockAlertWorker(
    KuCoinFuturesClient futuresClient,
    TelegramReportService telegram,
    IOptionsMonitor<SignalOptions> signalOptions,
    ILogger<RoiLockAlertWorker> logger) : BackgroundService
{
    private readonly HashSet<string> _alerted = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = signalOptions.CurrentValue;
        
        if (!opts.RoiLockAlertEnabled)
        {
            logger.LogInformation("ROI lock alerts disabled (Signals:RoiLockAlertEnabled=false).");
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(40), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckOpenPositionsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "ROI lock alert check failed");
            }

            var seconds = Math.Max(15, signalOptions.CurrentValue.RoiLockPollSeconds);
            
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task CheckOpenPositionsAsync(CancellationToken cancellationToken)
    {
        var options = signalOptions.CurrentValue;

        if (!options.RoiLockAlertEnabled)
            return;

        var positions = await futuresClient.GetOpenPositionsAsync(cancellationToken);
        var liveKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in positions)
        {
            var roi = FuturesPositionMath.LeveragedRoiPercent(p);
            var key = FuturesPositionMath.PositionKey(p);
            liveKeys.Add(key);
            
            if (roi is null || roi.Value < options.RoiAlertPercent)
                continue;

            if (!_alerted.Add(key))
                continue;

            await telegram.SendPrivateHtmlAsync(
                BuildAlertHtml(p, roi.Value, options),
                cancellationToken,
                logContextKey: $"roi-lock {p.Symbol}");
        }

        _alerted.RemoveWhere(k => !liveKeys.Contains(k));
    }

    private static string BuildAlertHtml(OpenFuturesPosition p, decimal roi, SignalOptions options)
    {
        var side = FuturesPositionMath.EffectiveSide(p);
        var arrow = TelegramReportService.TgEmojiDirectionForSign(1m);
        var lockFrom = FuturesPositionMath.PriceAtRoi(p, options.RoiLockFromPercent);
        var lockTo = FuturesPositionMath.PriceAtRoi(p, options.RoiLockToPercent);
        var fromPct = options.RoiLockFromPercent.ToString("0", CultureInfo.InvariantCulture);
        var toPct = options.RoiLockToPercent.ToString("0", CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        sb.AppendLine("<b>Discipline</b>");
        sb.Append(arrow);
        sb.Append('$');
        sb.Append(WebUtility.HtmlEncode(FuturesPositionMath.StripContractSuffix(p.Symbol)));
        sb.Append(' ');
        sb.Append(WebUtility.HtmlEncode(side));
        sb.Append(" is <b>+");
        sb.Append(WebUtility.HtmlEncode(roi.ToString("0", CultureInfo.InvariantCulture)));
        sb.AppendLine("% ROI</b>.");
        sb.AppendLine();
        sb.Append("Move SL now to lock <b>+");
        sb.Append(WebUtility.HtmlEncode(fromPct));
        sb.Append("–");
        sb.Append(WebUtility.HtmlEncode(toPct));
        sb.AppendLine("% ROI</b>.");
        sb.AppendLine("Stay guaranteed in profit. Do not give the winner back.");
        sb.AppendLine();

        if (lockFrom is not null && lockTo is not null)
        {
            var lo = Math.Min(lockFrom.Value, lockTo.Value);
            var hi = Math.Max(lockFrom.Value, lockTo.Value);
            sb.Append("Suggested SL: ");
            sb.Append(WebUtility.HtmlEncode(FormatPrice(lo)));
            sb.Append(" – ");
            sb.AppendLine(WebUtility.HtmlEncode(FormatPrice(hi)));
        }

        if (p.AvgEntryPrice is not null)
        {
            sb.Append("Entry: ");
            sb.AppendLine(WebUtility.HtmlEncode(FormatPrice(p.AvgEntryPrice.Value)));
        }

        if (p.MarkPrice is not null)
        {
            sb.Append("Mark: ");
            sb.AppendLine(WebUtility.HtmlEncode(FormatPrice(p.MarkPrice.Value)));
        }

        if (p.Leverage is not null)
        {
            var lev = p.Leverage.Value;
            sb.Append("Lev: ");
            sb.Append(WebUtility.HtmlEncode(
                lev % 1m == 0
                    ? lev.ToString("0", CultureInfo.InvariantCulture)
                    : lev.ToString("0.##", CultureInfo.InvariantCulture)));
            sb.AppendLine("x");
        }

        sb.Append("KuCoin: ");
        sb.Append(WebUtility.HtmlEncode(p.Symbol));

        return sb.ToString();
    }

    private static string FormatPrice(decimal v) =>
        v.ToString("0.########", CultureInfo.InvariantCulture);
}
