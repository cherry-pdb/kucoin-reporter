using KuCoinFuturesReporter.Data;
using KuCoinFuturesReporter.Models;
using KuCoinFuturesReporter.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KuCoinFuturesReporter.Services;

public sealed class KuCoinSyncWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<KuCoinOptions> options,
    ILogger<KuCoinSyncWorker> logger) : BackgroundService
{
    private const string SyncStateId = "kucoin_positions";
    private readonly KuCoinOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("KuCoin sync worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "KuCoin sync iteration failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(15, _options.PollIntervalSeconds)), stoppingToken);
        }
    }

    private async Task SyncOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var client = scope.ServiceProvider.GetRequiredService<KuCoinFuturesClient>();
        var telegram = scope.ServiceProvider.GetRequiredService<TelegramReportService>();

        await db.Database.MigrateAsync(cancellationToken);

        var state = await db.SyncStates.FirstOrDefaultAsync(x => x.Id == SyncStateId, cancellationToken);
        if (state is null)
        {
            state = new SyncState
            {
                Id = SyncStateId,
                LastCloseTime = DateTimeOffset.UtcNow.AddHours(-Math.Max(1, _options.LookbackHoursOnFirstRun))
            };
            db.SyncStates.Add(state);
            await db.SaveChangesAsync(cancellationToken);
        }

        var from = state.LastCloseTime.AddMinutes(-5); // небольшой overlap, чтобы не пропустить закрытие на границе окна
        var to = DateTimeOffset.UtcNow;
        var maxWindow = TimeSpan.FromDays(Math.Clamp(_options.RequestWindowDays, 1, 7));

        while (from < to)
        {
            var windowTo = from + maxWindow;
            if (windowTo > to) windowTo = to;

            var positions = await client.GetClosedPositionsAsync(from, windowTo, cancellationToken);
            foreach (var position in positions.OrderBy(x => x.CloseTime))
            {
                var exists = await db.ClosedPositions.AnyAsync(x => x.CloseId == position.CloseId, cancellationToken);
                if (exists) continue;

                db.ClosedPositions.Add(position);
                await db.SaveChangesAsync(cancellationToken);

                await telegram.SendPositionReportAsync(position, cancellationToken);
                position.TelegramSent = true;
                position.TelegramSentAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }

            var maxCloseTime = positions
                .Where(x => x.CloseTime > state.LastCloseTime)
                .Select(x => (DateTimeOffset?)x.CloseTime)
                .Max();

            if (maxCloseTime is not null)
                state.LastCloseTime = maxCloseTime.Value;
            else if (windowTo > state.LastCloseTime)
                state.LastCloseTime = windowTo;

            state.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            from = windowTo;
        }
    }
}
