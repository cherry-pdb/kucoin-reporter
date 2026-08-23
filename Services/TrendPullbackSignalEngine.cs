using KuCoinFuturesReporter.Models;
using KuCoinFuturesReporter.Options;

namespace KuCoinFuturesReporter.Services;

public static class TrendPullbackSignalEngine
{
    public static TradeSignal? Evaluate(
        FuturesContractSnapshot contract,
        IReadOnlyList<FuturesKline> h1,
        IReadOnlyList<FuturesKline> h4,
        decimal? equityUsd,
        bool hasOpenPosition,
        SignalOptions options)
    {
        if (h1.Count < 50 || h4.Count < 210)
            return null;

        var h4Ema50 = Ema(h4, 50);
        var h4Ema200 = Ema(h4, 200);
        var h1Ema20 = Ema(h1, 20);

        if (h4Ema50 is null || h4Ema200 is null || h1Ema20 is null)
            return null;

        var ema50Now = h4Ema50[^1];
        var ema200Now = h4Ema200[^1];
        var ema50Prev = h4Ema50[^6];
        var longBias = ema50Now > ema200Now && ema50Now > ema50Prev;
        var shortBias = ema50Now < ema200Now && ema50Now < ema50Prev;

        if (longBias == shortBias)
            return null;

        var atr = Atr(h1, 14);

        if (atr is null or <= 0m)
            return null;

        var last = h1[^1];
        var entry = last.Close;

        if (entry <= 0m)
            return null;

        var atrPct = atr.Value / entry * 100m;

        if (atrPct < options.MinAtrPercent || atrPct > options.MaxAtrPercent)
            return null;

        var trendSpread = ema200Now == 0m ? 0m : Math.Abs(ema50Now - ema200Now) / ema200Now * 100m;
        
        if (trendSpread < 0.25m)
            return null;

        var funding = contract.FundingFeeRate ?? 0m;
        
        if (longBias && funding > options.MaxFundingAbs)
            return null;

        if (shortBias && funding < -options.MaxFundingAbs)
            return null;

        var donchian = Math.Max(10, options.DonchianPeriod);
        var prior = h1.Skip(h1.Count - donchian - 1).Take(donchian).ToList();
        
        if (prior.Count < donchian)
            return null;

        var channelHigh = prior.Max(x => x.High);
        var channelLow = prior.Min(x => x.Low);

        if (longBias && last.Close > channelHigh)
            return null;

        if (shortBias && last.Close < channelLow)
            return null;

        const int pullbackBars = 8;
        const int extensionLookback = 16;
        var emaNow = h1Ema20[^1];
        var tagged = false;
        var nearestAtr = 4m;

        for (var i = h1.Count - pullbackBars; i < h1.Count; i++)
        {
            if (i < 0)
                continue;

            var bar = h1[i];
            var ema = h1Ema20[i];
            var dist = longBias
                ? (bar.Low - ema) / atr.Value
                : (ema - bar.High) / atr.Value;

            if (longBias && bar.Low <= ema + 0.25m * atr.Value)
                tagged = true;

            if (shortBias && bar.High >= ema - 0.25m * atr.Value)
                tagged = true;

            if (dist < nearestAtr)
                nearestAtr = dist;
        }

        if (!tagged)
            return null;

        var wasExtended = false;
        var extStart = Math.Max(20, h1.Count - extensionLookback);
        var extEnd = h1.Count - pullbackBars;

        for (var i = extStart; i < extEnd; i++)
        {
            var ema = h1Ema20[i];

            if (longBias && h1[i].Close >= ema + 0.8m * atr.Value)
                wasExtended = true;

            if (shortBias && h1[i].Close <= ema - 0.8m * atr.Value)
                wasExtended = true;
        }

        if (!wasExtended)
            return null;

        var range = last.High - last.Low;

        if (range <= 0m)
            return null;

        var closeLoc = longBias
            ? (last.Close - last.Low) / range
            : (last.High - last.Close) / range;

        if (closeLoc < 0.5m)
            return null;

        if (longBias)
        {
            if (last.Close <= last.Open)
                return null;

            if (last.Close < emaNow)
                return null;

            if (last.Low > emaNow + 0.35m * atr.Value)
                return null;
        }
        else
        {
            if (last.Close >= last.Open)
                return null;

            if (last.Close > emaNow)
                return null;

            if (last.High < emaNow - 0.35m * atr.Value)
                return null;
        }

        var volStart = Math.Max(0, h1.Count - 21);
        var volBars = h1.Skip(volStart).Take(20).ToList();
        var avgVol = volBars.Count == 0 ? 0m : volBars.Average(x => x.Volume);
        var relVol = avgVol > 0m ? last.Volume / avgVol : 1m;
        
        if (avgVol > 0m && relVol < 0.9m)
            return null;

        var swing = longBias
            ? h1.Skip(h1.Count - 6).Min(x => x.Low)
            : h1.Skip(h1.Count - 6).Max(x => x.High);

        var stop = longBias
            ? Math.Min(swing, emaNow - 0.3m * atr.Value)
            : Math.Max(swing, emaNow + 0.3m * atr.Value);

        var stopDist = Math.Abs(entry - stop);
        
        if (stopDist < 0.8m * atr.Value)
            stop = longBias ? entry - 1.2m * atr.Value : entry + 1.2m * atr.Value;
        
        stopDist = Math.Abs(entry - stop);
        
        if (stopDist > 2.5m * atr.Value || stopDist / entry < 0.003m)
            return null;

        var reward = options.RewardRisk <= 0m ? 2m : options.RewardRisk;
        var takeProfit = longBias ? entry + stopDist * reward : entry - stopDist * reward;
        var tick = contract.TickSize is > 0m ? contract.TickSize.Value : GuessTick(entry);
        entry = RoundToTick(entry, tick);
        stop = RoundToTick(stop, tick);
        takeProfit = RoundToTick(takeProfit, tick);

        if (stop <= 0m || takeProfit <= 0m || entry <= 0m)
            return null;
        
        if (longBias && (stop >= entry || takeProfit <= entry))
            return null;
        
        if (shortBias && (stop <= entry || takeProfit >= entry))
            return null;

        var quality = ScoreQuality(trendSpread, ema50Now, ema50Prev, nearestAtr, closeLoc, relVol, contract.TurnoverOf24h);
        
        if (quality < options.MinQualityScore)
            return null;

        var equity = equityUsd is > 0m ? equityUsd.Value : 0m;
        var riskUsd = equity > 0m ? equity * (options.RiskPercent / 100m) : 0m;
        var actualStopDist = Math.Abs(entry - stop);
        var notional = actualStopDist > 0m && riskUsd > 0m
            ? riskUsd * entry / actualStopDist
            : 0m;

        var maxLev = Math.Max(2, options.MaxLeverage);
        
        if (contract.MaxLeverage is > 0m)
            maxLev = Math.Min(maxLev, (int)Math.Floor(contract.MaxLeverage.Value));
        
        maxLev = Math.Clamp(maxLev, 2, 20);

        var leverage = 3;
        
        if (notional > 0m && equity > 0m)
        {
            var marginBudget = equity * 0.15m;
            
            if (marginBudget > 0m)
                leverage = (int)Math.Round(notional / marginBudget, MidpointRounding.AwayFromZero);
        }

        leverage = Math.Clamp(leverage, 2, maxLev);

        var side = longBias ? "LONG" : "SHORT";
        var reason = longBias
            ? $"4h uptrend, 1h pullback to EMA20, bounce vol {relVol.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}x."
            : $"4h downtrend, 1h pullback to EMA20, bounce vol {relVol.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}x.";

        return new TradeSignal(
            Symbol: contract.Symbol,
            Side: side,
            BarTime: last.OpenTime,
            Entry: entry,
            Stop: stop,
            TakeProfit: takeProfit,
            Atr: atr.Value,
            RiskUsd: riskUsd,
            NotionalUsd: notional,
            Leverage: leverage,
            Strength: Math.Max(0m, 1m - Math.Max(0m, nearestAtr)),
            QualityScore: quality,
            Reason: reason,
            Kind: "PULLBACK",
            HasOpenPosition: hasOpenPosition);
    }

    private static decimal ScoreQuality(
        decimal trendSpreadPct,
        decimal ema50Now,
        decimal ema50Prev,
        decimal nearestAtr,
        decimal closeLoc,
        decimal relVol,
        decimal? turnover24h)
    {
        var trendPts = Clamp(trendSpreadPct * 10m, 0m, 22m);
        var slopePct = ema50Prev == 0m ? 0m : Math.Abs(ema50Now - ema50Prev) / Math.Abs(ema50Prev) * 100m;
        var slopePts = Clamp(slopePct * 8m, 0m, 14m);
        var touchPts = Clamp(18m - Math.Max(0m, nearestAtr) * 20m, 0m, 18m);
        var closePts = Clamp(closeLoc * 16m, 0m, 16m);
        var volPts = Clamp((relVol - 0.8m) * 12m, 0m, 14m);
        var liq = turnover24h ?? 0m;
        var liqPts = liq <= 0m ? 0m : Clamp((decimal)Math.Log10((double)Math.Max(liq, 1m)) - 6.5m, 0m, 8m) * 4m;
        liqPts = Clamp(liqPts, 0m, 8m);

        return Clamp(trendPts + slopePts + touchPts + closePts + volPts + liqPts, 0m, 100m);
    }

    private static decimal Clamp(decimal v, decimal min, decimal max)
    {
        if (v < min)
            return min;

        if (v > max)
            return max;

        return v;
    }

    private static decimal[]? Ema(IReadOnlyList<FuturesKline> bars, int period)
    {
        if (bars.Count < period)
            return null;

        var k = 2m / (period + 1m);
        var result = new decimal[bars.Count];
        decimal sum = 0m;

        for (var i = 0; i < period; i++)
            sum += bars[i].Close;

        var ema = sum / period;

        for (var i = 0; i < period; i++)
            result[i] = ema;

        for (var i = period; i < bars.Count; i++)
        {
            ema = bars[i].Close * k + ema * (1m - k);
            result[i] = ema;
        }

        return result;
    }

    private static decimal? Atr(IReadOnlyList<FuturesKline> bars, int period)
    {
        if (bars.Count < period + 2)
            return null;

        var trs = new decimal[bars.Count];
        trs[0] = bars[0].High - bars[0].Low;

        for (var i = 1; i < bars.Count; i++)
        {
            var h = bars[i].High;
            var l = bars[i].Low;
            var prev = bars[i - 1].Close;
            var tr = h - l;
            var hc = Math.Abs(h - prev);
            var lc = Math.Abs(l - prev);

            if (hc > tr)
                tr = hc;

            if (lc > tr)
                tr = lc;
                
            trs[i] = tr;
        }

        decimal atr = 0m;

        for (var i = 1; i <= period; i++)
            atr += trs[i];
            
        atr /= period;

        for (var i = period + 1; i < bars.Count; i++)
            atr = (atr * (period - 1) + trs[i]) / period;

        return atr;
    }

    private static decimal GuessTick(decimal price) =>
        price >= 1000m ? 0.1m : price >= 100m ? 0.01m : price >= 1m ? 0.0001m : 0.000001m;

    private static decimal RoundToTick(decimal value, decimal tick)
    {
        if (tick <= 0m)
            return value;

        return Math.Round(value / tick, MidpointRounding.AwayFromZero) * tick;
    }
}
