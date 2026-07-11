using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FastDOM.MarketData.Interfaces;
using FastDOM.MarketData.Models;

namespace FastDOM.App.ViewModels;

public partial class MoversViewModel : ObservableObject
{
    private readonly IMarketMoversClient _client;
    private CancellationTokenSource? _refreshCts;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _status = "Ready";
    [ObservableProperty] private string _sessionLabel = SessionName();
    [ObservableProperty] private DateTime _lastUpdated;

    public ObservableCollection<MarketMover> Gainers { get; } = [];
    public ObservableCollection<MarketMover> Active { get; } = [];
    public ObservableCollection<MarketMover> Losers { get; } = [];

    public MoversViewModel(IMarketMoversClient client) => _client = client;

    public async Task RefreshAsync()
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource();
        var ct = _refreshCts.Token;
        IsLoading = true;
        SessionLabel = SessionName();
        Status = $"Loading Schwab {SessionLabel.ToLowerInvariant()} movers…";
        try
        {
            var gainersTask = _client.GetMoversAsync("EQUITY_ALL", MoverSort.PercentChangeUp, 0, ct);
            var activeTask = _client.GetMoversAsync("EQUITY_ALL", MoverSort.Volume, 0, ct);
            var losersTask = _client.GetMoversAsync("EQUITY_ALL", MoverSort.PercentChangeDown, 0, ct);
            await Task.WhenAll(gainersTask, activeTask, losersTask);
            Replace(Gainers, gainersTask.Result);
            Replace(Active, activeTask.Result);
            Replace(Losers, losersTask.Result);
            LastUpdated = DateTime.Now;
            Status = $"{SessionLabel} · {Gainers.Count} gainers · {Active.Count} active · {Losers.Count} losers · updated {LastUpdated:h:mm:ss tt}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static void Replace(ObservableCollection<MarketMover> target, IEnumerable<MarketMover> source)
    {
        target.Clear();
        foreach (var item in source) target.Add(item);
    }

    private static string SessionName()
    {
        var now = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "Central Standard Time");
        if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return "Market closed";
        var time = now.TimeOfDay;
        if (time >= TimeSpan.FromHours(3) && time < TimeSpan.FromHours(8.5)) return "Premarket";
        if (time >= TimeSpan.FromHours(8.5) && time < TimeSpan.FromHours(15)) return "Regular session";
        if (time >= TimeSpan.FromHours(15) && time < TimeSpan.FromHours(19)) return "After hours";
        return "Market closed";
    }
}
