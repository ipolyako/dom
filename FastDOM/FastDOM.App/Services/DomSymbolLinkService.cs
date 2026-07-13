namespace FastDOM.App.Services;

/// <summary>Thread-safe symbol channel shared by the main DOM and chart UI thread.</summary>
public sealed class DomSymbolLinkService
{
    private readonly object _gate = new();
    private string _currentSymbol = "SPY";
    private Action<string>? _changed;

    public string CurrentSymbol
    {
        get { lock (_gate) return _currentSymbol; }
    }

    public void Publish(string symbol)
    {
        symbol = SymbolClassifier.NormalizeDisplaySymbol(symbol);
        Action<string>? handlers;
        lock (_gate)
        {
            _currentSymbol = symbol;
            handlers = _changed;
        }
        handlers?.Invoke(symbol);
    }

    public IDisposable Subscribe(Action<string> handler)
    {
        lock (_gate) _changed += handler;
        return new Subscription(this, handler);
    }

    private void Unsubscribe(Action<string> handler)
    {
        lock (_gate) _changed -= handler;
    }

    private sealed class Subscription(DomSymbolLinkService owner, Action<string> handler) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Unsubscribe(handler);
        }
    }
}
