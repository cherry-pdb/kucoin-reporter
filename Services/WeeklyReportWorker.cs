using System.Net;
using System.Text;
using KuCoinFuturesReporter.Data;
using KuCoinFuturesReporter.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KuCoinFuturesReporter.Services;

public sealed class WeeklyReportWorker(
    IServiceScopeFactory scopeFactory,
    TelegramReportService telegram,
    ILogger<WeeklyReportWorker> logger) : BackgroundService
{
    private const string WeeklySyncStateId = "weekly_pnl";

    private static readonly string[] EnDowShort =
        ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TrySendWeeklyReport(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Weekly report check failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
        }
    }

    private async Task TrySendWeeklyReport(CancellationToken stoppingToken)
    {
        var mskNow = DateTimeOffset.UtcNow.ToOffset(TelegramReportService.ReportTimeZoneOffset);
        
        if (mskNow.DayOfWeek != DayOfWeek.Monday)
            return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var triggerMonday = DateOnly.FromDateTime(mskNow.Date);
        var weekStartMonday = triggerMonday.AddDays(-7);
        var weekAnchor = MidnightMskOffset(weekStartMonday);
        var stateRow = await db.SyncStates.SingleOrDefaultAsync(x => x.Id == WeeklySyncStateId, stoppingToken);
        
        if (stateRow is not null && AreSameUtcInstant(stateRow.LastCloseTime, weekAnchor))
            return;

        var weekEndExclusive = weekAnchor.AddDays(7);
        var weekAnchorUtc = weekAnchor.ToUniversalTime();
        var weekEndExclusiveUtc = weekEndExclusive.ToUniversalTime();

        var rows = await db.ClosedPositions
            .Where(p => p.CloseTime >= weekAnchorUtc && p.CloseTime < weekEndExclusiveUtc)
            .Select(p => new { p.CloseTime, p.Pnl })
            .ToListAsync(stoppingToken);
        var totalsByDay = new Dictionary<DateOnly, decimal>();
        
        foreach (var row in rows)
        {
            var day = DateOnly.FromDateTime(row.CloseTime.ToOffset(TelegramReportService.ReportTimeZoneOffset).DateTime);
            var pnlPart = row.Pnl ?? 0m;
            totalsByDay.TryGetValue(day, out var acc);
            totalsByDay[day] = acc + pnlPart;
        }

        var weekTotal = totalsByDay.Values.Sum();
        var sunday = weekStartMonday.AddDays(6);
        var html = BuildWeeklyHtml(weekStartMonday, sunday, totalsByDay, weekTotal);
        await telegram.SendHtmlReportAsync(
            html,
            stoppingToken,
            logContextKey: $"неделя {weekStartMonday:dd.MM}-{sunday:dd.MM}");

        if (stateRow is null)
        {
            db.SyncStates.Add(new SyncState
            {
                Id = WeeklySyncStateId,
                LastCloseTime = weekAnchorUtc,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            stateRow.LastCloseTime = weekAnchorUtc;
            stateRow.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(stoppingToken);
        logger.LogInformation("Weekly PN report sent for Mon {Monday}", weekStartMonday);
    }

    private static DateTimeOffset MidnightMskOffset(DateOnly date) =>
        new(date.Year, date.Month, date.Day, 0, 0, 0, TelegramReportService.ReportTimeZoneOffset);

    private static bool AreSameUtcInstant(DateTimeOffset a, DateTimeOffset b) =>
        a.ToUniversalTime() == b.ToUniversalTime();

    private static string BuildWeeklyHtml(
        DateOnly weekStartMonday,
        DateOnly sundayDate,
        IReadOnlyDictionary<DateOnly, decimal> totalsByDay,
        decimal weekTotal)
    {
        var sb = new StringBuilder();

        for (var i = 0; i < 7; i++)
        {
            var day = weekStartMonday.AddDays(i);
            totalsByDay.TryGetValue(day, out var daySum);
            sb.Append(EnDowShort[i]);
            sb.Append(" - ");
            sb.Append($"{day:dd.MM}");
            sb.Append(TelegramReportService.TgEmojiDirectionForSign(daySum));
            sb.Append(WebUtility.HtmlEncode(TelegramReportService.FormatPnlWeeklyMagnitude(daySum)));
            sb.Append(TelegramReportService.TgEmojiMoneyMarkup());
            sb.Append('\n');
        }

        sb.Append($"\nOverall {weekStartMonday:dd.MM}-{sundayDate:dd.MM}");
        sb.Append(TelegramReportService.TgEmojiDirectionForSign(weekTotal));
        sb.Append(WebUtility.HtmlEncode(TelegramReportService.FormatPnlWeeklyMagnitude(weekTotal)));
        sb.Append(TelegramReportService.TgEmojiMoneyMarkup());
        sb.Append("\n\n#overall #week");

        return sb.ToString();
    }
}
