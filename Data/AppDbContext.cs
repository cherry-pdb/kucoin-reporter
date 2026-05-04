using KuCoinFuturesReporter.Models;
using Microsoft.EntityFrameworkCore;

namespace KuCoinFuturesReporter.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<ClosedPosition> ClosedPositions => Set<ClosedPosition>();
    public DbSet<SyncState> SyncStates => Set<SyncState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ClosedPosition>(entity =>
        {
            entity.HasKey(x => x.CloseId);
            entity.HasIndex(x => x.CloseTime);
            entity.HasIndex(x => x.TelegramSent);
            entity.Property(x => x.Pnl).HasPrecision(28, 12);
            entity.Property(x => x.RealisedGrossCost).HasPrecision(28, 12);
            entity.Property(x => x.TradeFee).HasPrecision(28, 12);
            entity.Property(x => x.FundingFee).HasPrecision(28, 12);
            entity.Property(x => x.OpenPrice).HasPrecision(28, 12);
            entity.Property(x => x.ClosePrice).HasPrecision(28, 12);
            entity.Property(x => x.Leverage).HasPrecision(18, 6);
            entity.Property(x => x.Roe).HasPrecision(18, 6);
        });

        modelBuilder.Entity<SyncState>(entity =>
        {
            entity.HasKey(x => x.Id);
        });
    }
}
