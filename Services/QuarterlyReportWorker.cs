using System.Net;
using System.Text;
using KuCoinFuturesReporter.Data;
using KuCoinFuturesReporter.Models;
using Microsoft.EntityFrameworkCore;

namespace KuCoinFuturesReporter.Services;

public sealed class QuarterlyReportWorker(
    IServiceScopeFactory scopeFactory,
    TelegramReportService telegram,
    ILogger<QuarterlyReportWorker> logger) : BackgroundService
{
    private const string QuarterlySyncStateId = "quarterly_pnl";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TrySendQuarterlyReport(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Quarterly report check failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
        }
    }

    private async Task TrySendQuarterlyReport(CancellationToken stoppingToken)
    {
        var mskNow = DateTimeOffset.UtcNow.ToOffset(TelegramReportService.ReportTimeZoneOffset);
        var today = DateOnly.FromDateTime(mskNow.DateTime);

        if (today.Day != 1 || mskNow.Hour >= 2)
            return;

        if (!TryGetCompletedQuarterStart(today, out var quarterStart, out var quarterIndex))
            return;

        var quarterAnchor = MidnightMskOffset(quarterStart);
        var quarterAnchorUtc = quarterAnchor.ToUniversalTime();
        var quarterEndExclusive = quarterStart.AddMonths(3);
        var quarterEndExclusiveUtc = MidnightMskOffset(quarterEndExclusive).ToUniversalTime();

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var stateRow = await db.SyncStates.SingleOrDefaultAsync(x => x.Id == QuarterlySyncStateId, stoppingToken);
        if (stateRow is not null && AreSameUtcInstant(stateRow.LastCloseTime, quarterAnchorUtc))
            return;

        var quarterTotal = await db.ClosedPositions
            .Where(p => p.CloseTime >= quarterAnchorUtc && p.CloseTime < quarterEndExclusiveUtc)
            .SumAsync(p => p.Pnl ?? 0m, stoppingToken);

        var html = BuildQuarterlyHtml(quarterStart, quarterIndex, quarterTotal);
        await telegram.SendHtmlReportAsync(
            html,
            stoppingToken,
            logContextKey: $"quarter Q{quarterIndex} {quarterStart:yyyy-MM}");

        if (stateRow is null)
        {
            db.SyncStates.Add(new SyncState
            {
                Id = QuarterlySyncStateId,
                LastCloseTime = quarterAnchorUtc,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            stateRow.LastCloseTime = quarterAnchorUtc;
            stateRow.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(stoppingToken);
        logger.LogInformation("Quarterly PN report sent for Q{Quarter} {Start:yyyy-MM}", quarterIndex, quarterStart);
    }

    private static bool TryGetCompletedQuarterStart(DateOnly triggerFirstOfMonth, out DateOnly quarterStart, out int quarterIndex)
    {
        quarterStart = default;
        quarterIndex = 0;

        switch (triggerFirstOfMonth.Month)
        {
            case 4:
                quarterStart = new DateOnly(triggerFirstOfMonth.Year, 1, 1);
                quarterIndex = 1;
                return true;
            case 7:
                quarterStart = new DateOnly(triggerFirstOfMonth.Year, 4, 1);
                quarterIndex = 2;
                return true;
            case 10:
                quarterStart = new DateOnly(triggerFirstOfMonth.Year, 7, 1);
                quarterIndex = 3;
                return true;
            case 1:
                quarterStart = new DateOnly(triggerFirstOfMonth.Year - 1, 10, 1);
                quarterIndex = 4;
                return true;
            default:
                return false;
        }
    }

    private static DateTimeOffset MidnightMskOffset(DateOnly date) =>
        new(date.Year, date.Month, date.Day, 0, 0, 0, TelegramReportService.ReportTimeZoneOffset);

    private static bool AreSameUtcInstant(DateTimeOffset a, DateTimeOffset b) =>
        a.ToUniversalTime() == b.ToUniversalTime();

    private static string BuildQuarterlyHtml(DateOnly quarterStart, int quarterIndex, decimal quarterTotal)
    {
        var title = $"Q{quarterIndex} {quarterStart.Year}";
        var sb = new StringBuilder();
        sb.Append(WebUtility.HtmlEncode(title));
        sb.Append("\n\n");
        sb.Append(TelegramReportService.TgEmojiDirectionForSign(quarterTotal));
        sb.Append(WebUtility.HtmlEncode(TelegramReportService.FormatPnlWeeklyMagnitude(quarterTotal)));
        sb.Append(TelegramReportService.TgEmojiMoneyMarkup());
        sb.Append("\n\n#overall #quarter");

        return sb.ToString();
    }
}
