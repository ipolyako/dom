using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows;
using FastDOM.App.Services;
using FastDOM.MarketData.Interfaces;
using FastDOM.MarketData.Models;
using FastDOM.Core.Enums;
using FastDOM.Core.Models;

namespace FastDOM.App.ViewModels;

public record ChartTimeframe(string Label, PriceHistoryRequest Request, int AggregateMinutes = 0)
{
    public override string ToString() => Label;
}

public partial class ChartViewModel : ObservableObject, IDisposable
{
    private readonly IPriceHistoryClient _history;
    private readonly IMarketDataClient _marketData;
    private readonly OrderService _orders;
    private readonly AccountSummaryCache _accounts;
    private HotButtonsViewModel? _hotButtons;
    private readonly IDisposable _quoteSubscription;
    private readonly IDisposable _depthSubscription;
    private CancellationTokenSource? _loadCts;
    private string? _subscribedSymbol;
    private int _disposed;

    [ObservableProperty] private string _symbol = "SPY";
    [ObservableProperty] private ChartTimeframe _selectedTimeframe;
    [ObservableProperty] private string _status = "Ready";
    [ObservableProperty] private bool _includeExtendedHours = true;
    [ObservableProperty] private OrderSide _tradeSide = OrderSide.Buy;
    [ObservableProperty] private OrderType _tradeOrderType = OrderType.Limit;
    [ObservableProperty] private int _tradeQuantity = 100;
    [ObservableProperty] private decimal? _stagedPrice;
    [ObservableProperty] private string _tradeStatus = "Ready";
    public string AccountId { get; private set; } = "";
    public Quote? CurrentQuote { get; private set; }
    public Position? CurrentPosition { get; private set; }
    public IReadOnlyList<OrderState> WorkingOrders { get; private set; } = [];
    public IReadOnlyList<PriceCandle> Candles { get; private set; } = [];
    public MarketDepth? CurrentDepth { get; private set; }
    public event Action? ChartChanged;

    public IReadOnlyList<ChartTimeframe> Timeframes { get; } =
    [
        new("1m · 1D", new("day", 1, "minute", 1)),
        new("5m · 5D", new("day", 5, "minute", 5)),
        new("15m · 10D", new("day", 10, "minute", 15)),
        new("30m · 10D", new("day", 10, "minute", 30)),
        new("1H · 10D", new("day", 10, "minute", 30), 60),
        new("4H · 10D", new("day", 10, "minute", 30), 240),
        new("1D · 1M", new("month", 1, "daily", 1)),
        new("1D · 3M", new("month", 3, "daily", 1)),
        new("1D · 1Y", new("year", 1, "daily", 1)),
        new("1D · 5Y", new("year", 5, "daily", 1))
    ];

    public ChartViewModel(IPriceHistoryClient history, IMarketDataClient marketData, OrderService orders, AccountSummaryCache accounts)
    {
        _history = history;
        _marketData = marketData;
        _orders = orders;
        _accounts = accounts;
        _selectedTimeframe = Timeframes[1];
        _quoteSubscription = marketData.QuoteStream.Subscribe(OnQuote);
        _depthSubscription = marketData.DepthStream.Subscribe(OnDepth);
        _orders.OrderStateChanged += OnOrderStateChanged;
    }

    public void ConfigureTrading(string accountId, int quantity, HotButtonsViewModel hotButtons)
    {
        AccountId = accountId;
        TradeQuantity = Math.Max(1, quantity);
        _hotButtons = hotButtons;
        RefreshTradingState();
    }

    public async Task LoadAsync(string? symbol = null)
    {
        var symbolChanged = !string.IsNullOrWhiteSpace(symbol)
            && !string.Equals(Symbol, SymbolClassifier.NormalizeDisplaySymbol(symbol), StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(symbol)) Symbol = SymbolClassifier.NormalizeDisplaySymbol(symbol);
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        Status = $"Loading {Symbol} {SelectedTimeframe.Label}…";
        if (symbolChanged)
        {
            // Never leave the previous ticker's candles visible under the new
            // symbol while Schwab history is in flight.
            Candles = [];
            CurrentDepth = null;
            ChartChanged?.Invoke();
        }
        try
        {
            if (!string.Equals(_subscribedSymbol, Symbol, StringComparison.OrdinalIgnoreCase))
            {
                if (_subscribedSymbol != null)
                {
                    await _marketData.UnsubscribeQuotesAsync(_subscribedSymbol, _loadCts.Token);
                    await _marketData.UnsubscribeDepthAsync(_subscribedSymbol, _loadCts.Token);
                }
                CurrentDepth = null;
                await _marketData.SubscribeQuotesAsync(Symbol, _loadCts.Token);
                await _marketData.SubscribeDepthAsync(Symbol, _loadCts.Token);
                _subscribedSymbol = Symbol;
            }
            var request = SelectedTimeframe.Request with { IncludeExtendedHours = IncludeExtendedHours };
            var candles = await _history.GetPriceHistoryAsync(Symbol, request, _loadCts.Token);
            Candles = SelectedTimeframe.AggregateMinutes > 0
                ? Aggregate(candles, SelectedTimeframe.AggregateMinutes)
                : candles;
            Status = Candles.Count == 0 ? $"No history returned for {Symbol}" :
                $"{Symbol} · {SelectedTimeframe.Label} · {Candles.Count:N0} candles · {Candles[^1].Timestamp:g}";
            RefreshTradingState();
            ChartChanged?.Invoke();
            await RefreshPositionAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status = ex.Message; }
    }

    private static IReadOnlyList<PriceCandle> Aggregate(IReadOnlyList<PriceCandle> source, int minutes)
    {
        if (source.Count == 0 || minutes <= 0) return source;
        return source
            .GroupBy(c =>
            {
                var bucket = (c.Timestamp.Hour * 60 + c.Timestamp.Minute) / minutes * minutes;
                return new DateTime(c.Timestamp.Year, c.Timestamp.Month, c.Timestamp.Day,
                    bucket / 60, bucket % 60, 0, c.Timestamp.Kind);
            })
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var ordered = group.OrderBy(c => c.Timestamp).ToArray();
                return new PriceCandle
                {
                    Timestamp = group.Key,
                    Open = ordered[0].Open,
                    High = ordered.Max(c => c.High),
                    Low = ordered.Min(c => c.Low),
                    Close = ordered[^1].Close,
                    Volume = ordered.Sum(c => c.Volume)
                };
            })
            .ToArray();
    }

    private void OnQuote(Quote quote)
    {
        if (!string.Equals(quote.Symbol, Symbol, StringComparison.OrdinalIgnoreCase) || Candles.Count == 0 || quote.Last <= 0) return;
        CurrentQuote = quote;
        var list = Candles.ToList();
        var last = list[^1];
        var bucket = LiveCandleBucket(quote.TimestampUtc == default ? DateTime.UtcNow : quote.TimestampUtc);
        if (bucket > last.Timestamp)
        {
            // Price history can lag the live stream. Start the current live bar
            // immediately instead of mutating the last REST candle forever.
            last = new PriceCandle
            {
                Timestamp = bucket,
                Open = quote.Last,
                High = quote.Last,
                Low = quote.Last,
                Close = quote.Last,
                Volume = Math.Max(0, quote.LastSize)
            };
            list.Add(last);
        }
        else if (bucket == last.Timestamp)
        {
            last.Close = quote.Last;
            last.High = Math.Max(last.High, quote.Last);
            last.Low = last.Low <= 0 ? quote.Last : Math.Min(last.Low, quote.Last);
            if (quote.LastSize > 0) last.Volume += quote.LastSize;
        }
        else
        {
            // Ignore an older/out-of-order tick rather than painting it into a
            // newer candle.
            return;
        }
        Candles = list;
        ChartChanged?.Invoke();
    }

    private DateTime LiveCandleBucket(DateTime timestampUtc)
    {
        var local = timestampUtc.Kind == DateTimeKind.Utc
            ? timestampUtc.ToLocalTime()
            : timestampUtc;
        if (SelectedTimeframe.Request.FrequencyType.Equals("daily", StringComparison.OrdinalIgnoreCase))
            return local.Date;

        var minutes = SelectedTimeframe.AggregateMinutes > 0
            ? SelectedTimeframe.AggregateMinutes
            : Math.Max(1, SelectedTimeframe.Request.Frequency);
        var minuteOfDay = local.Hour * 60 + local.Minute;
        var bucket = minuteOfDay / minutes * minutes;
        return new DateTime(local.Year, local.Month, local.Day,
            bucket / 60, bucket % 60, 0, local.Kind);
    }

    private void OnOrderStateChanged(OrderState _)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            RefreshTradingState();
            ChartChanged?.Invoke();
            return;
        }

        if (dispatcher.CheckAccess())
        {
            RefreshTradingState();
            ChartChanged?.Invoke();
        }
        else
        {
            dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
            {
                RefreshTradingState();
                ChartChanged?.Invoke();
            }));
        }
    }

    private void RefreshTradingState()
    {
        var allOrders = _orders.ActiveOrders.Values.ToArray();
        var symbolMatched = allOrders
            .Where(o => o.IsWorking && OrdersMatchSymbol(Symbol, o.Symbol));

        var scoped = string.IsNullOrWhiteSpace(AccountId)
            ? symbolMatched
            : symbolMatched.Where(o => string.Equals(o.AccountId, AccountId, StringComparison.OrdinalIgnoreCase));

        var matching = scoped.ToArray();
        if (matching.Length == 0 && !string.IsNullOrWhiteSpace(AccountId))
            matching = symbolMatched.ToArray();

        WorkingOrders = matching
            .DistinctBy(o => o.BrokerOrderId ?? o.ClientOrderId).ToArray();
    }

    private async Task RefreshPositionAsync()
    {
        if (string.IsNullOrWhiteSpace(AccountId)) return;
        var account = await _accounts.GetAsync(AccountId);
        account.Positions.TryGetValue(Symbol, out var position);
        CurrentPosition = position;
        RefreshTradingState();
        ChartChanged?.Invoke();
    }

    public void StagePrice(decimal price) { StagedPrice = Math.Round(price, 2); TradeStatus = $"Staged {TradeSide} {TradeQuantity:N0} @ {StagedPrice:F2}"; }

    public async Task<(bool ok, string message)> SubmitAsync(bool market = false)
    {
        if (TradeQuantity <= 0) return (false, "Quantity must be positive");
        if (DateTime.Now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return (false, "Market is closed");
        var type = market ? OrderType.Market : TradeOrderType;
        if (!market && !StagedPrice.HasValue) return (false, "Double-click the chart to stage a price");
        var extended = IsExtendedSession();
        if (extended && type != OrderType.Limit) return (false, "Extended-hours chart orders must be Limit orders");
        var request = new OrderRequest
        {
            AccountId = AccountId, Symbol = Symbol, AssetType = SymbolClassifier.AssetTypeFor(Symbol), Side = TradeSide,
            Quantity = TradeQuantity, OrderType = type,
            LimitPrice = type is OrderType.Limit or OrderType.StopLimit or OrderType.MarketableLimit ? StagedPrice : null,
            StopPrice = type is OrderType.StopMarket or OrderType.StopLimit ? StagedPrice : null,
            TimeInForce = TimeInForce.Day, ExtendedHours = extended, Source = OrderSource.DomClick
        };
        var account = await _accounts.GetAsync(AccountId);
        var result = await _orders.SubmitOrderAsync(request, account, CurrentQuote);
        TradeStatus = result.success ? $"{TradeSide} {TradeQuantity:N0} {Symbol} sent" : $"Rejected: {result.message}";
        RefreshTradingState(); ChartChanged?.Invoke(); return result;
    }

    public async Task CancelAllAsync() { await _orders.CancelAllForSymbolFastAsync(AccountId, Symbol); TradeStatus = $"Cancel sent for {Symbol}"; RefreshTradingState(); ChartChanged?.Invoke(); }
    public async Task CancelOrderAsync(OrderState order) { if (order.BrokerOrderId != null) await _orders.CancelOrderAsync(AccountId, order.BrokerOrderId); RefreshTradingState(); ChartChanged?.Invoke(); }
    public async Task CancelOrdersAsync(IReadOnlyList<OrderState> orders)
    {
        foreach (var order in orders)
            if (order.BrokerOrderId != null) await _orders.CancelOrderAsync(AccountId, order.BrokerOrderId);
        RefreshTradingState(); ChartChanged?.Invoke();
    }

    public async Task MoveOrdersAsync(IReadOnlyList<OrderState> orders, decimal newPrice)
    {
        foreach (var order in orders)
        {
            if (order.BrokerOrderId == null) continue;
            var isStop = order.OrderType is OrderType.StopMarket or OrderType.StopLimit;
            var oldStop = order.StopPrice;
            var replacement = new OrderReplace
            {
                OriginalClientOrderId = order.ClientOrderId, BrokerOrderId = order.BrokerOrderId,
                NewStopPrice = isStop ? newPrice : null,
                NewLimitPrice = order.OrderType == OrderType.StopLimit && oldStop.HasValue && order.LimitPrice.HasValue
                    ? order.LimitPrice + (newPrice - oldStop.Value)
                    : isStop ? null : newPrice,
                Source = OrderSource.DomClick
            };
            var result = await _orders.ReplaceOrderAsync(AccountId, replacement);
            if (!result.success) { TradeStatus = $"Move rejected: {result.message}"; break; }
        }
        RefreshTradingState(); ChartChanged?.Invoke();
    }

    public async Task ExecuteConfiguredButtonAsync(string id)
    {
        var button = _hotButtons?.Buttons.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));
        if (button == null) { TradeStatus = $"Configured action not found: {id}"; return; }
        await ExecuteHotButtonAsync(button);
    }

    public async Task ExecuteHotButtonAsync(HotButtonConfig button)
    {
        if (_hotButtons == null) return;
        await RefreshPositionAsync();
        IReadOnlyDictionary<string, decimal>? variables = StagedPrice is > 0
            ? new Dictionary<string, decimal> { ["STOP"] = StagedPrice.Value }
            : null;
        await _hotButtons.ExecuteButtonAsync(button, Symbol, AccountId, TradeQuantity, CurrentQuote, CurrentPosition, variables);
        TradeStatus = $"{button.Label} completed"; RefreshTradingState(); ChartChanged?.Invoke();
    }

    public async Task ExecuteHotkeyActionAsync(string actionType)
    {
        if (_hotButtons == null) return;
        await RefreshPositionAsync();
        await _hotButtons.ExecuteActionAsync(actionType, Symbol, AccountId, TradeQuantity, CurrentQuote, CurrentPosition);
        TradeStatus = $"Hotkey {actionType} completed"; RefreshTradingState(); ChartChanged?.Invoke();
    }

    private static bool OrdersMatchSymbol(string displayedSymbol, string orderSymbol)
    {
        if (string.IsNullOrWhiteSpace(displayedSymbol) || string.IsNullOrWhiteSpace(orderSymbol)) return false;

        var left = NormalizeOrderSymbol(displayedSymbol);
        var right = NormalizeOrderSymbol(orderSymbol);
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return true;

        if (!TryParseOption(left, out var leftOption) || !TryParseOption(right, out var rightOption))
            return false;

        if (!string.Equals(leftOption.Root, rightOption.Root, StringComparison.OrdinalIgnoreCase)
            || leftOption.Expiry != rightOption.Expiry
            || leftOption.Side != rightOption.Side)
            return false;

        if (leftOption.Strike.HasValue && rightOption.Strike.HasValue)
            return leftOption.Strike == rightOption.Strike;

        return true;
    }

    private static string NormalizeOrderSymbol(string symbol) =>
        new string(symbol.Trim().Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();

    private static bool TryParseOption(string symbol, out (string Root, string Expiry, char Side, int? Strike) value)
    {
        value = default;
        symbol = NormalizeOrderSymbol(symbol);
        if (string.IsNullOrWhiteSpace(symbol) || symbol.Length < 8) return false;

        // OPTION format:
        // ROOT + YYMMDD + [C|P] + optional STRIKE
        // STRIKE may be compact (variable width) or OCC 8-digit fixed width.
        for (var rootLength = 1; rootLength <= Math.Max(1, symbol.Length - 7); rootLength++)
        {
            var suffix = symbol[rootLength..];
            if (suffix.Length < 7 || !char.IsDigit(suffix[0]) || !char.IsDigit(suffix[5])) continue;
            if (suffix[6] is not ('C' or 'P')) continue;

            if (!IsDigits(suffix.AsSpan(0, 6))) continue;
            if (suffix.Length > 7 && !IsDigits(suffix.AsSpan(7))) continue;

            var expiry = suffix[..6];
            var side = suffix[6];
            int? strike = null;
            if (suffix.Length > 7 && int.TryParse(suffix[7..], out var strikeParsed))
                strike = strikeParsed;

            value = (symbol[..rootLength], expiry, side, strike);
            return true;
        }

        return false;
    }

    private static bool IsDigits(ReadOnlySpan<char> value)
    {
        foreach (var c in value)
            if (!char.IsDigit(c))
                return false;
        return true;
    }

    private static bool IsExtendedSession()
    {
        var now = DateTime.Now;
        return now.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && (now.TimeOfDay < TimeSpan.FromHours(8.5) || now.TimeOfDay >= TimeSpan.FromHours(15));
    }

    private void OnDepth(MarketDepth depth)
    {
        if (!string.Equals(depth.Symbol, Symbol, StringComparison.OrdinalIgnoreCase)) return;
        CurrentDepth = depth;
        ChartChanged?.Invoke();
    }

    public void Dispose()
    {
        // Transient view models created by Microsoft DI are tracked and
        // disposed again when the root provider shuts down. The chart window
        // also disposes its view model when it closes, so teardown must be
        // safe when both paths run during application shutdown.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        var loadCts = Interlocked.Exchange(ref _loadCts, null);
        loadCts?.Cancel();
        loadCts?.Dispose();
        _quoteSubscription.Dispose();
        _depthSubscription.Dispose();
        _orders.OrderStateChanged -= OnOrderStateChanged;
        if (_subscribedSymbol != null)
        {
            _ = _marketData.UnsubscribeQuotesAsync(_subscribedSymbol);
            _ = _marketData.UnsubscribeDepthAsync(_subscribedSymbol);
        }
    }
}
