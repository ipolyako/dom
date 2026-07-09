using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FastDOM.MarketData.Interfaces;
using FastDOM.MarketData.Models;
using Microsoft.Extensions.Logging;

namespace FastDOM.App.ViewModels;

public partial class WatchlistItem : ObservableObject
{
    public required string Symbol { get; init; }
    public bool IsOption { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastDisplay))]
    private decimal? _last;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChangeDisplay))]
    private decimal? _change;

    public string LastDisplay   => Last.HasValue   ? $"{Last.Value:F2}"             : "—";
    public string ChangeDisplay => Change.HasValue ? $"{Change.Value:+0.00;-0.00}" : "";
    // For options show abbreviated symbol in the list
    public string DisplaySymbol => IsOption ? AbbrevOcc(Symbol) : Symbol;

    private static string AbbrevOcc(string occ)
    {
        // AAPL240119C00150000 → AAPL C150 Jan19
        var m = Regex.Match(occ, @"^([A-Z]+)(\d{2})(\d{2})(\d{2})([CP])(\d{8})$");
        if (!m.Success) return occ;
        var und  = m.Groups[1].Value;
        var yr   = m.Groups[2].Value;
        var mo   = m.Groups[3].Value;
        var day  = m.Groups[4].Value;
        var type = m.Groups[5].Value;
        var str  = decimal.Parse(m.Groups[6].Value) / 1000m;
        var mon  = new DateTime(2000 + int.Parse(yr), int.Parse(mo), 1).ToString("MMM");
        return $"{und} {type}{str:0.##} {mon}{day}";
    }
}

public partial class WatchlistViewModel : ObservableObject
{
    private readonly IMarketDataClient _marketData;
    private readonly ILogger<WatchlistViewModel> _logger;
    private readonly string _savePath;
    private IDisposable? _quoteSub;
    private readonly DispatcherTimer _refreshTimer;
    private bool _refreshInFlight;

    public ObservableCollection<WatchlistItem> Items { get; } = [];
    public event Action<string>? SymbolSelected;

    [ObservableProperty] private string _newSymbol = "";

    public WatchlistViewModel(IMarketDataClient marketData, ILogger<WatchlistViewModel> logger)
    {
        _marketData = marketData;
        _logger = logger;
        _savePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FastDOM", "watchlist.json");

        Load();
        _quoteSub = marketData.QuoteStream.Subscribe(OnQuote);

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _refreshTimer.Tick += async (_, _) => await RefreshSnapshotsAsync();
        _refreshTimer.Start();

        _ = ResubscribeAsync();
    }

    [RelayCommand]
    private async Task AddSymbolAsync()
    {
        var sym = NewSymbol.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(sym)) return;
        if (Items.Any(i => i.Symbol == sym)) { NewSymbol = ""; return; }

        var item = new WatchlistItem { Symbol = sym };
        Items.Add(item);
        NewSymbol = "";
        Save();

        try
        {
            await _marketData.SubscribeQuotesAsync(sym);
            var snap = await _marketData.GetSnapshotAsync(sym);
            if (snap != null)
                Application.Current.Dispatcher.Invoke(() =>
                {
                    item.Last   = snap.Last;
                    item.Change = snap.NetChange;
                });
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Watchlist snapshot failed: {Sym}", sym); }
    }

    [RelayCommand]
    private void RemoveSymbol(WatchlistItem item)
    {
        Items.Remove(item);
        Save();
        _ = _marketData.UnsubscribeQuotesAsync(item.Symbol);
    }

    [RelayCommand]
    private void SelectSymbol(WatchlistItem item)
        => SymbolSelected?.Invoke(item.Symbol);

    // Called from OptionsChainViewModel to add an option contract.
    public void AddOccSymbol(string occSymbol, SelectedOptionContract contract)
    {
        if (Items.Any(i => i.Symbol == occSymbol)) return;
        var item = new WatchlistItem { Symbol = occSymbol, IsOption = true };
        item.Last = contract.Ask ?? contract.Bid;   // seed with current ask/bid
        Items.Add(item);
        Save();
    }

    // Called after DOM symbol changes to keep watch subscriptions alive.
    public async Task ResubscribeAsync()
    {
        foreach (var item in Items.ToList())
        {
            try
            {
                await _marketData.SubscribeQuotesAsync(item.Symbol);
                await RefreshSnapshotAsync(item);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Resubscribe failed: {Sym}", item.Symbol); }
        }
    }

    private async Task RefreshSnapshotsAsync()
    {
        if (_refreshInFlight) return;
        _refreshInFlight = true;
        try
        {
            foreach (var item in Items.ToList())
                await RefreshSnapshotAsync(item);
        }
        finally
        {
            _refreshInFlight = false;
        }
    }

    private async Task RefreshSnapshotAsync(WatchlistItem item)
    {
        var snap = await _marketData.GetSnapshotAsync(item.Symbol);
        if (snap == null) return;
        Application.Current.Dispatcher.Invoke(() =>
        {
            item.Last = snap.Last;
            item.Change = snap.NetChange;
        });
    }

    private void OnQuote(FastDOM.MarketData.Models.Quote q)
    {
        var item = Items.FirstOrDefault(i => i.Symbol == q.Symbol);
        if (item == null) return;
        Application.Current.Dispatcher.Invoke(() =>
        {
            item.Last   = q.Last;
            item.Change = q.NetChange;
        });
    }

    private record SavedItem(string Symbol, bool IsOption = false);

    private void Load()
    {
        try
        {
            if (!File.Exists(_savePath)) return;
            var raw = File.ReadAllText(_savePath);
            // Support old format (list of strings) and new format (list of objects)
            List<SavedItem> saved;
            try   { saved = JsonSerializer.Deserialize<List<SavedItem>>(raw) ?? []; }
            catch { saved = (JsonSerializer.Deserialize<List<string>>(raw) ?? [])
                            .Select(s => new SavedItem(s)).ToList(); }
            foreach (var s in saved.Where(x => !string.IsNullOrWhiteSpace(x.Symbol)))
                Items.Add(new WatchlistItem { Symbol = s.Symbol.ToUpperInvariant(), IsOption = s.IsOption });
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Watchlist load failed"); }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_savePath)!);
            File.WriteAllText(_savePath,
                JsonSerializer.Serialize(Items.Select(i => new SavedItem(i.Symbol, i.IsOption)).ToList()));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Watchlist save failed"); }
    }
}
