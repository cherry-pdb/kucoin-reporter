using System.ComponentModel.DataAnnotations;

namespace KuCoinFuturesReporter.Models;

public sealed class SyncState
{
    [Key]
    public string Id { get; set; } = "kucoin_positions";

    public DateTimeOffset LastCloseTime { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
