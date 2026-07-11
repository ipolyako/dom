using System.Collections.ObjectModel;
using FastDOM.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FastDOM.Broker.Interfaces;
using FastDOM.Core.Enums;
using FastDOM.Core.Models;
using Microsoft.Extensions.Logging;

namespace FastDOM.App.ViewModels;

public partial class PositionRow : ObservableObject
{
    public string Symbol { get; init; } = "";
    public string Side { get; init; } = "";   // "LONG" / "SHORT"
    public int Qty { get; init; }
    [ObservableProperty] private string _openPnL = "—";
    [ObservableProperty] private decimal? _openPnLValue;
    [ObservableProperty] private string _dayPnL = "—";
    [ObservableProperty] private decimal? _dayPnLValue;
}

public partial class PositionViewModel : ObservableObject
{
    private readonly ILogger<PositionViewModel> _logger;
    private readonly IBrokerClient _broker;
    private readonly OrderService _orderService;
    private readonly Dictionary<string, decimal> _lastPricesBySymbol = new(StringComparer.OrdinalIgnoreCase);

    public string AccountId { get; set; } = "";

    [ObservableProperty] private string _symbol = "";
    [ObservableProperty] private int _quantity;
    [ObservableProperty] private string _side = "FLAT";
    [ObservableProperty] private decimal _averageCost;
    [ObservableProperty] private decimal? _unrealizedPnL;
    [ObservableProperty] private decimal? _realizedPnL;
    [ObservableProperty] private decimal? _buyingPower;
    [ObservableProperty] private bool _hasPosition;
    [ObservableProperty] private string _pnLDisplay = "—";
    [ObservableProperty] private string _realizedPnLDisplay = "—";
    [ObservableProperty] private string _dayPnLDisplay = "—";
    [ObservableProperty] private decimal? _dayPnL;

    public ObservableCollection<PositionRow> AllPositions { get; } = [];
    public Position? CurrentPosition { get; private set; }
    public AccountSummary? CurrentAccount { get; private set; }

    public event Action<string>? SymbolSelected;

    public PositionViewModel(ILogger<PositionViewModel> logger, IBrokerClient broker, OrderService orderService)
    {
        _logger = logger;
        _broker = broker;
        _orderService = orderService;
    }

    [RelayCommand]
    private void SelectSymbol(string symbol) => SymbolSelected?.Invoke(symbol);

    public async Task RefreshAsync(string accountId, string symbol)
    {
        if (string.IsNullOrEmpty(accountId) || string.IsNullOrEmpty(symbol)) return;
        var selectedSymbol = SymbolClassifier.NormalizeDisplaySymbol(symbol);

        try
        {
            CurrentAccount = await _broker.GetAccountSummaryAsync(accountId);
            BuyingPower = CurrentAccount.BuyingPower;
            RealizedPnL = CurrentAccount.DailyRealizedPnL;

            var normalizedSymbol = selectedSymbol.ToUpperInvariant();
            CurrentAccount.Positions.TryGetValue(normalizedSymbol, out var pos);
            pos ??= CurrentAccount.Positions.Values.FirstOrDefault(p =>
                string.Equals(SymbolClassifier.NormalizeDisplaySymbol(p.Symbol), normalizedSymbol, StringComparison.OrdinalIgnoreCase));
            if (pos == null)
            {
                var realizedDayPnl = _orderService.GetTodayRealizedPnL(accountId, normalizedSymbol);
                if (realizedDayPnl.HasValue)
                {
                    pos = new Position
                    {
                        AccountId = accountId,
                        Symbol = normalizedSymbol,
                        Quantity = 0,
                        AverageCost = 0m,
                        DayPnL = realizedDayPnl
                    };
                }
            }
            CurrentPosition = pos;
            Symbol = selectedSymbol;
            if (CurrentPosition != null &&
                _lastPricesBySymbol.TryGetValue(Symbol, out var cachedPrice))
            {
                CurrentPosition.CurrentPrice = cachedPrice;
            }
            UpdateDisplay(pos);
            RebuildAllPositions();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh position for {Account}/{Symbol}", accountId, symbol);
        }
    }

    public void UpdateFromQuote(decimal lastPrice)
    {
        _lastPricesBySymbol[Symbol] = lastPrice;
        if (CurrentPosition == null || CurrentPosition.IsFlat) return;
        CurrentPosition.CurrentPrice = lastPrice;
        var pnl = CalculateOpenPnL(CurrentPosition) ?? 0m;
        UnrealizedPnL = pnl;
        PnLDisplay = FormatPnL(pnl);
        RefreshDayPnL();

        // Update live row in AllPositions without rebuilding the whole collection
        var row = AllPositions.FirstOrDefault(r =>
            string.Equals(SymbolClassifier.NormalizeDisplaySymbol(r.Symbol), Symbol, StringComparison.OrdinalIgnoreCase));
        if (row != null)
        {
            row.OpenPnL = FormatPnL(pnl);
            row.OpenPnLValue = pnl;
            var dayVal = CurrentPosition.DayPnL;
            row.DayPnL = dayVal.HasValue ? FormatPnL(dayVal.Value) : "—";
            row.DayPnLValue = dayVal;
        }
    }

    private void RebuildAllPositions()
    {
        AllPositions.Clear();
        if (CurrentAccount == null) return;
        foreach (var pos in CurrentAccount.Positions.Values.Where(p => !p.IsFlat)
                                .OrderBy(p => p.Symbol))
        {
            var normalizedSymbol = SymbolClassifier.NormalizeDisplaySymbol(pos.Symbol);
            if (_lastPricesBySymbol.TryGetValue(normalizedSymbol, out var lastPrice))
                pos.CurrentPrice = lastPrice;

            var pnl = CalculateOpenPnL(pos);
            var dayVal = pos.DayPnL;
            AllPositions.Add(new PositionRow
            {
                Symbol      = pos.Symbol,
                Side        = pos.Side == PositionSide.Long ? "LONG" : "SHORT",
                Qty         = pos.Quantity,  // signed: +100 long, -50 short
                OpenPnL     = pnl.HasValue ? FormatPnL(pnl.Value) : "—",
                OpenPnLValue = pnl,
                DayPnL      = dayVal.HasValue ? FormatPnL(dayVal.Value) : "—",
                DayPnLValue = dayVal,
            });
        }
    }

    private static decimal? CalculateOpenPnL(Position pos) =>
        pos.CurrentPrice.HasValue && !pos.IsFlat
            ? (pos.CurrentPrice.Value - pos.AverageCost) * pos.Quantity
            : pos.UnrealizedPnL;

    private void UpdateDisplay(Position? pos)
    {
        if (pos == null || pos.IsFlat)
        {
            Quantity = 0;
            Side = "FLAT";
            AverageCost = 0;
            HasPosition = false;
            PnLDisplay = "—";
            UnrealizedPnL = null;
        }
        else
        {
            Quantity = Math.Abs(pos.Quantity);
            Side = pos.Side == PositionSide.Long ? "LONG" : "SHORT";
            AverageCost = pos.AverageCost;
            HasPosition = true;
            UnrealizedPnL = CalculateOpenPnL(pos);
            PnLDisplay = UnrealizedPnL.HasValue ? FormatPnL(UnrealizedPnL.Value) : "—";
        }

        RealizedPnLDisplay = CurrentAccount?.DailyRealizedPnL.HasValue == true
            ? FormatPnL(CurrentAccount.DailyRealizedPnL.Value) : "—";
        RefreshDayPnL();
    }

    private void RefreshDayPnL()
    {
        var day = CurrentPosition?.DayPnL;
        DayPnL = day;
        DayPnLDisplay = day.HasValue ? FormatPnL(day.Value) : "—";
    }

    private static string FormatPnL(decimal v) =>
        v >= 0 ? $"+${v:F2}" : $"-${Math.Abs(v):F2}";
}
