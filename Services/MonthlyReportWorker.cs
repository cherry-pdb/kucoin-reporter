using System.Globalization;
using System.Net;
using System.Text;
using KuCoinFuturesReporter.Data;
using KuCoinFuturesReporter.Models;
using Microsoft.EntityFrameworkCore;

namespace KuCoinFuturesReporter.Services;

public sealed class MonthlyReportWorker(
    IServiceScopeFactory scopeFactory,
    TelegramReportService telegram,
    ILogger<MonthlyReportWorker> logger) : BackgroundService
{
    private const string MonthlySyncStateId = "monthly_pnl";

    private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TrySendMonthlyReport(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Monthly report check failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
        }
    }

    private async Task TrySendMonthlyReport(CancellationToken stoppingToken)
    {
        var mskNow = DateTimeOffset.UtcNow.ToOffset(TelegramReportService.ReportTimeZoneOffset);
        var today = DateOnly.FromDateTime(mskNow.DateTime);

        if (today.Day != 1 || mskNow.Hour >= 2)
            return;

        var firstOfThisMonth = new DateOnly(today.Year, today.Month, 1);
        var completedMonthStart = firstOfThisMonth.AddMonths(-1);

        var monthAnchor = MidnightMskOffset(completedMonthStart);
        var monthAnchorUtc = monthAnchor.ToUniversalTime();
        var monthEndExclusiveUtc = MidnightMskOffset(firstOfThisMonth).ToUniversalTime();

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var stateRow = await db.SyncStates.SingleOrDefaultAsync(x => x.Id == MonthlySyncStateId, stoppingToken);
        if (stateRow is not null && AreSameUtcInstant(stateRow.LastCloseTime, monthAnchorUtc))
            return;

        var monthTotal = await db.ClosedPositions
            .Where(p => p.CloseTime >= monthAnchorUtc && p.CloseTime < monthEndExclusiveUtc)
            .SumAsync(p => p.Pnl ?? 0m, stoppingToken);

        var html = BuildMonthlyHtml(completedMonthStart, monthTotal);
        await telegram.SendHtmlReportAsync(
            html,
            stoppingToken,
            logContextKey: $"месяц {completedMonthStart:yyyy-MM}");

        if (stateRow is null)
        {
            db.SyncStates.Add(new SyncState
            {
                Id = MonthlySyncStateId,
                LastCloseTime = monthAnchorUtc,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            stateRow.LastCloseTime = monthAnchorUtc;
            stateRow.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(stoppingToken);
        logger.LogInformation("Monthly PN report sent for {Month}", completedMonthStart.ToString("yyyy-MM"));
    }

    private static DateTimeOffset MidnightMskOffset(DateOnly date) =>
        new(date.Year, date.Month, date.Day, 0, 0, 0, TelegramReportService.ReportTimeZoneOffset);

    private static bool AreSameUtcInstant(DateTimeOffset a, DateTimeOffset b) =>
        a.ToUniversalTime() == b.ToUniversalTime();

    private static string BuildMonthlyHtml(DateOnly completedMonthYearMonth, decimal monthTotal)
    {
        var title = completedMonthYearMonth.ToString("MMMM yyyy", EnUs);

        var sb = new StringBuilder();
        sb.Append(WebUtility.HtmlEncode(title));
        sb.Append("\n\n");
        sb.Append(TelegramReportService.TgEmojiDirectionForSign(monthTotal));
        sb.Append(WebUtility.HtmlEncode(TelegramReportService.FormatPnlWeeklyMagnitudeRu(monthTotal)));
        sb.Append(TelegramReportService.TgEmojiMoneyMarkup());
        sb.Append("\n\n#overall #month");
        
        return sb.ToString();
    }
}
