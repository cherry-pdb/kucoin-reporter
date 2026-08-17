using KuCoinFuturesReporter.Models;

namespace KuCoinFuturesReporter.Services;

internal static class FuturesPositionMath
{
    public static decimal? LeveragedRoiPercent(OpenFuturesPosition p)
    {
        if (p.PosMargin is > 0m && p.UnrealisedPnl is not null)
            return p.UnrealisedPnl.Value / p.PosMargin.Value * 100m;

        if (p.Leverage is null)
            return null;

        if (p.UnrealisedPnlPcnt is not null)
            return p.UnrealisedPnlPcnt.Value * p.Leverage.Value * 100m;

        if (p.UnrealisedRoePcnt is not null)
            return p.UnrealisedRoePcnt.Value * p.Leverage.Value;

        return null;
    }

    public static string EffectiveSide(OpenFuturesPosition p)
    {
        var raw = (p.PositionSide ?? string.Empty).Trim().ToUpperInvariant();

        switch (raw)
        {
            case "LONG" or "SHORT":
                return raw;
            case "BOTH" when p.CurrentQty is not null:
            {
                switch (p.CurrentQty.Value)
                {
                    case > 0m:
                        return "LONG";
                    case < 0m:
                        return "SHORT";
                }

                break;
            }
        }

        return string.IsNullOrWhiteSpace(raw) ? "?" : raw;
    }

    public static decimal? PriceAtRoi(OpenFuturesPosition p, decimal roiPercent)
    {
        if (p.AvgEntryPrice is not > 0m || p.Leverage is not > 0m)
            return null;

        var move = roiPercent / 100m / p.Leverage.Value;
        var side = EffectiveSide(p);
        var price = side == "SHORT"
            ? p.AvgEntryPrice.Value * (1m - move)
            : p.AvgEntryPrice.Value * (1m + move);

        return price > 0m ? price : null;
    }

    public static string PositionKey(OpenFuturesPosition p)
    {
        var entry = p.AvgEntryPrice is null
            ? "na"
            : p.AvgEntryPrice.Value.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture);

        return $"{p.Symbol}:{EffectiveSide(p)}:{entry}";
    }

    public static string StripContractSuffix(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return symbol;

        var upper = symbol.ToUpperInvariant();

        foreach (var suffix in new[] { "USDTM", "USDCM", "USDM", "USDT", "USDC", "USD" })
            if (upper.EndsWith(suffix, StringComparison.Ordinal) && upper.Length > suffix.Length)
                return symbol[..^suffix.Length].TrimEnd();

        return symbol;
    }
}
