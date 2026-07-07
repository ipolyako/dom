using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FastDOM.MarketData.Interfaces;
using FastDOM.MarketData.Models;
using Microsoft.Extensions.Logging;

namespace FastDOM.App.ViewModels;

// Displayed row in the DataGrid — one per strike, shows call + put side.
public partial class OptionsRowViewModel : ObservableObject
{
    public decimal Strike { get; init; }
    public string CallSymbol { get; init; } = "";
    public string PutSymbol  { get; init; } = "";

    // Call side
    public string CallBid   { get; init; } = "—";
    public string CallAsk   { get; init; } = "—";
    public string CallIV    { get; init; } = "";
    public string CallDelta { get; init; } = "";
    public string CallOI    { get; init; } = "";

    // Put side
    public string PutBid    { get; init; } = "—";
    public string PutAsk    { get; init; } = "—";
    public string PutIV     { get; init; } = "";
    public string PutDelta  { get; init; } = "";
    public string PutOI     { get; init; } = "";

    public string StrikeDisplay => Strike.ToString("F2");

    // Used for ITM row highlighting
    public bool IsCallItm { get; init; }
    public bool IsPutItm  { get; init; }

    public decimal? RawCallBid { get; init; }
    public decimal? RawCallAsk { get; init; }
    public decimal? RawCallIV  { get; init; }
    public decimal? RawCallDelta { get; init; }
    public decimal? RawPutBid  { get; init; }
    public decimal? RawPutAsk  { get; init; }
    public decimal? RawPutIV   { get; init; }
    public decimal? RawPutDelta { get; init; }

    public static OptionsRowViewModel From(OptionsChainRow r, decimal lastPrice) => new()
    {
        Strike      = r.Strike,
        CallSymbol  = r.CallSymbol,
        PutSymbol   = r.PutSymbol,
        IsCallItm   = r.Strike < lastPrice,
        IsPutItm    = r.Strike > lastPrice,

        RawCallBid   = r.CallBid,
        RawCallAsk   = r.CallAsk,
        RawCallIV    = r.CallIV,
        RawCallDelta = r.CallDelta,
        RawPutBid    = r.PutBid,
        RawPutAsk    = r.PutAsk,
        RawPutIV     = r.PutIV,
        RawPutDelta  = r.PutDelta,

        CallBid   = r.CallBid?.ToString("F2")   ?? "—",
        CallAsk   = r.CallAsk?.ToString("F2")   ?? "—",
        CallIV    = r.CallIV.HasValue  ? $"{r.CallIV.Value:P1}"   : "",
        CallDelta = r.CallDelta.HasValue ? $"{r.CallDelta.Value:F2}" : "",
        CallOI    = r.CallOI.HasValue  ? $"{r.CallOI.Value:N0}"   : "",
        PutBid    = r.PutBid?.ToString("F2")    ?? "—",
        PutAsk    = r.PutAsk?.ToString("F2")    ?? "—",
        PutIV     = r.PutIV.HasValue   ? $"{r.PutIV.Value:P1}"    : "",
        PutDelta  = r.PutDelta.HasValue  ? $"{r.PutDelta.Value:F2}"  : "",
        PutOI     = r.PutOI.HasValue   ? $"{r.PutOI.Value:N0}"    : "",
    };
}

public partial class OptionsChainViewModel : ObservableObject
{
    private readonly IOptionsDataProvider _options;
    private readonly WatchlistViewModel _watchlist;
    private readonly ILogger<OptionsChainViewModel> _logger;

    [ObservableProperty] private string _underlying = "";
    [ObservableProperty] private string _selectedExpiration = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = "Enter a symbol and click Load";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedContract))]
    private SelectedOptionContract? _selectedContract;
    [ObservableProperty] private decimal _lastPrice;

    public bool HasSelectedContract => SelectedContract != null;

    public ObservableCollection<string>            Expirations { get; } = [];
    public ObservableCollection<OptionsRowViewModel> Rows      { get; } = [];

    public OptionsChainViewModel(
        IOptionsDataProvider options,
        WatchlistViewModel watchlist,
        ILogger<OptionsChainViewModel> logger)
    {
        _options   = options;
        _watchlist = watchlist;
        _logger    = logger;
    }

    [RelayCommand]
    private async Task LoadExpirationsAsync()
    {
        if (string.IsNullOrWhiteSpace(Underlying)) return;
        Underlying = Underlying.Trim().ToUpperInvariant();
        IsLoading = true;
        StatusMessage = $"Loading expirations for {Underlying}…";
        Expirations.Clear();
        Rows.Clear();
        SelectedContract = null;

        var dates = await _options.GetExpirationDatesAsync(Underlying);
        IsLoading = false;

        if (dates.Count == 0)
        {
            StatusMessage = "No expirations found — check symbol or broker connection.";
            return;
        }

        foreach (var d in dates)
            Expirations.Add(d.ToString("yyyy-MM-dd"));

        SelectedExpiration = Expirations.First();
        StatusMessage = $"{dates.Count} expirations found. Select one and click Load Chain.";
    }

    [RelayCommand]
    private async Task LoadChainAsync()
    {
        if (string.IsNullOrWhiteSpace(Underlying) || string.IsNullOrWhiteSpace(SelectedExpiration))
            return;
        if (!DateOnly.TryParse(SelectedExpiration, out var expDate)) return;

        IsLoading = true;
        StatusMessage = $"Loading {Underlying} options for {SelectedExpiration}…";
        Rows.Clear();
        SelectedContract = null;

        var chain = await _options.GetChainAsync(Underlying, expDate);
        IsLoading = false;

        if (chain.Count == 0)
        {
            StatusMessage = "No data returned — OPRA feed may require a subscription.";
            return;
        }

        foreach (var row in chain)
            Rows.Add(OptionsRowViewModel.From(row, LastPrice));

        StatusMessage = $"{chain.Count} strikes loaded.  Click a Bid/Ask to select a contract.";
    }

    public void SelectCall(OptionsRowViewModel row)
    {
        if (string.IsNullOrEmpty(row.CallSymbol)) return;
        if (!DateOnly.TryParse(SelectedExpiration, out var exp)) return;
        SelectedContract = new SelectedOptionContract
        {
            OccSymbol  = row.CallSymbol,
            Underlying = Underlying,
            Expiration = exp,
            Strike     = row.Strike,
            Type       = OptionType.Call,
            Bid        = row.RawCallBid,
            Ask        = row.RawCallAsk,
            IV         = row.RawCallIV,
            Delta      = row.RawCallDelta,
        };
    }

    public void SelectPut(OptionsRowViewModel row)
    {
        if (string.IsNullOrEmpty(row.PutSymbol)) return;
        if (!DateOnly.TryParse(SelectedExpiration, out var exp)) return;
        SelectedContract = new SelectedOptionContract
        {
            OccSymbol  = row.PutSymbol,
            Underlying = Underlying,
            Expiration = exp,
            Strike     = row.Strike,
            Type       = OptionType.Put,
            Bid        = row.RawPutBid,
            Ask        = row.RawPutAsk,
            IV         = row.RawPutIV,
            Delta      = row.RawPutDelta,
        };
    }

    [RelayCommand(CanExecute = nameof(CanAddToWatchlist))]
    private void AddToWatchlist()
    {
        if (SelectedContract == null) return;
        _watchlist.AddOccSymbol(SelectedContract.OccSymbol, SelectedContract);
        StatusMessage = $"Added {SelectedContract.OccSymbol} to watchlist.";
    }

    private bool CanAddToWatchlist() => SelectedContract != null;

    partial void OnSelectedContractChanged(SelectedOptionContract? value)
        => AddToWatchlistCommand.NotifyCanExecuteChanged();
}
