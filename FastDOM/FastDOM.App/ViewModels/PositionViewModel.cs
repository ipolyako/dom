using CommunityToolkit.Mvvm.ComponentModel;
using FastDOM.Broker.Interfaces;
using FastDOM.Core.Enums;
using FastDOM.Core.Models;
using Microsoft.Extensions.Logging;

namespace FastDOM.App.ViewModels;

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

    public Position? CurrentPosition { get; private set; }
    public AccountSummary? CurrentAccount { get; private set; }

    public PositionViewModel(ILogger<PositionViewModel> logger, IBrokerClient broker)
    {
        _logger = logger;
        _broker = broker;
    }

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
        PnLDisplay = pnl >= 0 ? $"+${pnl:F2}" : $"-${Math.Abs(pnl):F2}";
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
        }
        else
        {
            Quantity = Math.Abs(pos.Quantity);
            Side = pos.Side == PositionSide.Long ? "LONG" : "SHORT";
            AverageCost = pos.AverageCost;
            HasPosition = true;
            UnrealizedPnL = pos.UnrealizedPnL;
            PnLDisplay = pos.UnrealizedPnL.HasValue
                ? (pos.UnrealizedPnL >= 0 ? $"+${pos.UnrealizedPnL:F2}" : $"-${Math.Abs(pos.UnrealizedPnL.Value):F2}")
                : "—";
        }
    }
}
