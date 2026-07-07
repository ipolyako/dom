using System.Collections.ObjectModel;
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

    public PositionViewModel(ILogger<PositionViewModel> logger, IBrokerClient broker)
    {
        _logger = logger;
        _broker = broker;
    }

    [RelayCommand]
    private void SelectSymbol(string symbol) => SymbolSelected?.Invoke(symbol);

    public async Task RefreshAsync(string accountId, string symbol)
    {
        if (string.IsNullOrEmpty(accountId) || string.IsNullOrEmpty(symbol)) return;

        try
        {
            CurrentAccount = await _broker.GetAccountSummaryAsync(accountId);
            BuyingPower = CurrentAccount.BuyingPower;
            RealizedPnL = CurrentAccount.DailyRealizedPnL;

            CurrentAccount.Positions.TryGetValue(symbol.ToUpperInvariant(), out var pos);
            CurrentPosition = pos;
            Symbol = symbol;
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
        if (CurrentPosition == null || CurrentPosition.IsFlat) return;
        CurrentPosition.CurrentPrice = lastPrice;
        var pnl = (lastPrice - CurrentPosition.AverageCost) * CurrentPosition.Quantity;
        UnrealizedPnL = pnl;
        PnLDisplay = FormatPnL(pnl);
        RefreshDayPnL();

        // Update live row in AllPositions without rebuilding the whole collection
        var row = AllPositions.FirstOrDefault(r => r.Symbol == Symbol);
        if (row != null)
        {
            row.OpenPnL = FormatPnL(pnl);
            row.OpenPnLValue = pnl;
            var realized = CurrentAccount?.DailyRealizedPnL ?? 0m;
            var dayVal = realized + pnl;
            row.DayPnL = dayVal == 0m ? "—" : FormatPnL(dayVal);
            row.DayPnLValue = dayVal == 0m ? null : dayVal;
        }
    }

    private void RebuildAllPositions()
    {
        AllPositions.Clear();
        if (CurrentAccount == null) return;
        var realized = CurrentAccount.DailyRealizedPnL ?? 0m;
        foreach (var pos in CurrentAccount.Positions.Values.Where(p => !p.IsFlat)
                                .OrderBy(p => p.Symbol))
        {
            var pnl = pos.UnrealizedPnL;
            var dayVal = realized + (pnl ?? 0m);
            AllPositions.Add(new PositionRow
            {
                Symbol      = pos.Symbol,
                Side        = pos.Side == PositionSide.Long ? "LONG" : "SHORT",
                Qty         = pos.Quantity,  // signed: +100 long, -50 short
                OpenPnL     = pnl.HasValue ? FormatPnL(pnl.Value) : "—",
                OpenPnLValue = pnl,
                DayPnL      = dayVal == 0m ? "—" : FormatPnL(dayVal),
                DayPnLValue = dayVal == 0m ? null : dayVal,
            });
        }
    }

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
            UnrealizedPnL = pos.UnrealizedPnL;
            PnLDisplay = pos.UnrealizedPnL.HasValue ? FormatPnL(pos.UnrealizedPnL.Value) : "—";
        }

        RealizedPnLDisplay = CurrentAccount?.DailyRealizedPnL.HasValue == true
            ? FormatPnL(CurrentAccount.DailyRealizedPnL.Value) : "—";
        RefreshDayPnL();
    }

    private void RefreshDayPnL()
    {
        var realized = CurrentAccount?.DailyRealizedPnL ?? 0m;
        var unrealized = UnrealizedPnL ?? 0m;
        var day = realized + unrealized;
        DayPnL = day == 0m ? (decimal?)null : day;
        DayPnLDisplay = day == 0m ? "—" : FormatPnL(day);
    }

    private static string FormatPnL(decimal v) =>
        v >= 0 ? $"+${v:F2}" : $"-${Math.Abs(v):F2}";
}
