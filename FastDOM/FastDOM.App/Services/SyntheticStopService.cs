using System.Collections.Concurrent;
using FastDOM.Broker.Interfaces;
using FastDOM.Core.Enums;
using FastDOM.Core.Models;
using FastDOM.MarketData.Interfaces;
using FastDOM.MarketData.Models;
using Microsoft.Extensions.Logging;

namespace FastDOM.App.Services;

/// <summary>
/// Provides one-shot protective stops for positions opened during sessions in
/// which the broker accepts limit orders only. Protection exists only while
/// FastDOM is running and receiving quotes.
/// </summary>
public sealed class SyntheticStopService : IDisposable
{
    private readonly ILogger<SyntheticStopService> _logger;
    private readonly IBrokerClient _broker;
    private readonly OrderService _orders;
    private readonly IDisposable _quoteSubscription;
    private readonly ConcurrentDictionary<string, SyntheticStop> _stops = new(StringComparer.OrdinalIgnoreCase);

    public SyntheticStopService(
        ILogger<SyntheticStopService> logger,
        IMarketDataClient marketData,
        IBrokerClient broker,
        OrderService orders)
    {
        _logger = logger;
        _broker = broker;
        _orders = orders;
        _quoteSubscription = marketData.QuoteStream.Subscribe(OnQuote);
    }

    public void Arm(string accountId, string symbol, OrderSide exitSide, decimal stopPrice,
        Action<string> toast)
    {
        var stop = new SyntheticStop(accountId, symbol.Trim().ToUpperInvariant(), exitSide, stopPrice, toast);
        _stops[Key(accountId, symbol)] = stop;
        _logger.LogWarning(
            "Synthetic stop armed account={Account} symbol={Symbol} side={Side} stop={Stop}; protection requires FastDOM quotes",
            accountId, stop.Symbol, exitSide, stopPrice);
    }

    public void Disarm(string accountId, string symbol) => _stops.TryRemove(Key(accountId, symbol), out _);

    private void OnQuote(Quote quote)
    {
        // Never trigger a local protective exit from a closed-market or stale
        // REST fallback snapshot. Synthetic protection requires a current tick.
        if (quote.IsStale(10_000)) return;
        foreach (var pair in _stops.Where(x => x.Value.Symbol.Equals(quote.Symbol, StringComparison.OrdinalIgnoreCase)))
        {
            var stop = pair.Value;
            var triggerPrice = stop.ExitSide == OrderSide.Sell
                ? (quote.Bid > 0 ? quote.Bid : quote.Last)
                : (quote.Ask > 0 ? quote.Ask : quote.Last);
            var triggered = triggerPrice > 0 && (stop.ExitSide == OrderSide.Sell
                ? triggerPrice <= stop.StopPrice
                : triggerPrice >= stop.StopPrice);

            if (triggered && _stops.TryRemove(pair.Key, out var claimed))
                _ = TriggerAsync(claimed, quote);
        }
    }

    private async Task TriggerAsync(SyntheticStop stop, Quote quote)
    {
        try
        {
            stop.Toast($"SYNTHETIC STOP TRIGGERED: {stop.Symbol} @ {stop.StopPrice:F2}");
            await _orders.CancelAllForSymbolFastAsync(stop.AccountId, stop.Symbol);

            var account = await _broker.GetAccountSummaryAsync(stop.AccountId);
            if (!account.Positions.TryGetValue(stop.Symbol, out var position) || position.IsFlat)
            {
                _logger.LogInformation("Synthetic stop found no live {Symbol} position; exit skipped", stop.Symbol);
                return;
            }

            var actualExitSide = position.Side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;
            if (actualExitSide != stop.ExitSide)
            {
                stop.Toast($"SYNTHETIC STOP ABORTED: {stop.Symbol} position side changed");
                _logger.LogError("Synthetic stop side mismatch for {Symbol}; expected {Expected}, actual {Actual}",
                    stop.Symbol, stop.ExitSide, actualExitSide);
                return;
            }

            var outsideRegularHours = IsOutsideRegularHours(DateTime.UtcNow);
            decimal? limitPrice = null;
            if (outsideRegularHours)
            {
                limitPrice = stop.ExitSide == OrderSide.Sell
                    ? Math.Max(0.01m, stop.StopPrice - 0.10m)
                    : stop.StopPrice + 0.10m;
            }

            var request = new OrderRequest
            {
                AccountId = stop.AccountId,
                Symbol = stop.Symbol,
                AssetType = SymbolClassifier.AssetTypeFor(stop.Symbol),
                Side = stop.ExitSide,
                Quantity = Math.Abs(position.Quantity),
                OrderType = outsideRegularHours ? OrderType.Limit : OrderType.Market,
                LimitPrice = limitPrice,
                ExtendedHours = outsideRegularHours,
                Session = outsideRegularHours ? OrderSession.Seamless : OrderSession.Normal,
                Source = OrderSource.HotButton,
            };

            var (ok, message) = await _orders.SubmitOrderAsync(request, account, quote, bypassConfirmation: true);
            stop.Toast(ok
                ? $"Synthetic stop exit submitted: {request.Side} {request.Quantity} {stop.Symbol}"
                : $"SYNTHETIC STOP EXIT REJECTED: {message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Synthetic stop exit failed for {Symbol}", stop.Symbol);
            stop.Toast($"SYNTHETIC STOP EXIT FAILED: {ex.Message}");
        }
    }

    private static bool IsOutsideRegularHours(DateTime utcNow)
    {
        var etZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var et = TimeZoneInfo.ConvertTimeFromUtc(utcNow, etZone);
        if (et.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return true;
        return et.TimeOfDay < new TimeSpan(9, 30, 0) || et.TimeOfDay >= new TimeSpan(16, 0, 0);
    }

    private static string Key(string accountId, string symbol) =>
        $"{accountId.Trim().ToUpperInvariant()}|{symbol.Trim().ToUpperInvariant()}";

    public void Dispose() => _quoteSubscription.Dispose();

    private sealed record SyntheticStop(
        string AccountId,
        string Symbol,
        OrderSide ExitSide,
        decimal StopPrice,
        Action<string> Toast);
}
