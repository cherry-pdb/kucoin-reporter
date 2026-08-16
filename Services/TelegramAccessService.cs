using System.Globalization;
using KuCoinFuturesReporter.Options;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types;

namespace KuCoinFuturesReporter.Services;

public sealed class TelegramAccessService(IOptionsMonitor<TelegramOptions> options)
{
    public IReadOnlySet<long> GetAllowedUserIds()
    {
        var opts = options.CurrentValue;
        var raw = string.IsNullOrWhiteSpace(opts.AllowedUserIds)
            ? opts.BalanceCommandAllowedUserIds
            : opts.AllowedUserIds;

        return ParseUserIds(raw);
    }

    public bool IsAllowed(User? user) =>
        user is not null && GetAllowedUserIds().Contains(user.Id);

    public static HashSet<long> ParseUserIds(string? raw)
    {
        var set = new HashSet<long>();

        if (string.IsNullOrWhiteSpace(raw))
            return set;

        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                set.Add(id);

        return set;
    }
}
