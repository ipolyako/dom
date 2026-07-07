# FastDOM Architecture

## Layer Diagram

```
┌─────────────────────────────────────────────────────────┐
│                    FastDOM.App (WPF)                     │
│   MainWindow │ DomView │ HotButtonsView │ OrderTicket    │
│   ─────────────────────────────────────────────────      │
│   MainViewModel │ DomViewModel │ HotButtonsViewModel     │
│   ─────────────────────────────────────────────────      │
│   OrderService │ DomService │ HotkeyService              │
└──────────────────────────┬──────────────────────────────┘
                            │
┌───────────────────────────▼─────────────────────────────┐
│              FastDOM.Broker / FastDOM.MarketData         │
│   IBrokerClient │ IMarketDataClient │ IRiskManager       │
│   IAuthProvider                                          │
└──────┬────────────────────────────────────────┬──────────┘
       │                                        │
┌──────▼──────────┐                   ┌─────────▼──────────┐
│  MockBroker     │                   │  FastDOM.Broker.    │
│  MockMarketData │                   │  Schwab             │
│  (Simulation)   │                   │  SchwabBrokerClient │
└─────────────────┘                   │  SchwabMarketData   │
                                      │  SchwabAuthProvider │
                                      │  SchwabOrderMapper  │
                                      └─────────────────────┘
                                               │
                                      ┌────────▼────────────┐
                                      │  FastDOM.            │
                                      │  Infrastructure      │
                                      │  ConfigManager       │
                                      │  SecureStorage(DPAPI)│
                                      │  AuditLogger         │
                                      └─────────────────────┘
```

## Key Design Decisions

### Broker Abstraction
`IBrokerClient` and `IMarketDataClient` are the only interfaces the app layer uses. Schwab is one implementation. Mock is another. Swap by changing DI registration in `App.xaml.cs`.

### Observable State
Market data and order updates flow as `IObservable<T>` streams (Reactive Extensions). The UI subscribes on the dispatcher thread. No polling.

### DOM Throttling
The DOM rebuilds at most ~30fps via a `DispatcherTimer`. Raw quote ticks may arrive 200ms apart or faster. The timer batches visual updates to avoid excessive redraws.

### Order Safety
`OrderService.SubmitOrderAsync` is the single chokepoint for all order submissions — DOM clicks, hot buttons, hotkeys, and the order ticket all go through it. The `RiskManager` is called first, always.

### Config System
JSON files in `%APPDATA%\FastDOM\`. Human-readable. The `ConfigManager` loads all on startup and saves all on exit. Secrets are separate (DPAPI). No migration system — breaking schema changes require manual migration.

### Logging
- Human-readable rotated log: Serilog → File sink
- Structured JSONL audit log: `AuditLogger` → append-only per-day file
- Error log: Serilog restricted sink

### Mode System
`TradingMode` enum controls:
- `Simulation`: MockBroker + MockMarketData, no API calls
- `SchwabSandbox`: Real Schwab API for data, risk manager blocks all orders
- `SchwabLive`: Full live trading, requires `liveTradingEnabled: true` in risk profile

## Data Flow: DOM Click → Filled Order

```
1. User clicks price level in DomView
2. DomView.xaml.cs → DomViewModel.OnBuyColumnClicked(price, modifiers)
3. DomViewModel fires PriceLevelClicked event
4. MainWindow handles → populates OrderTicketViewModel
5. [If one-click mode]: directly calls OrderService.SubmitOrderAsync()
6. RiskManager.ValidateOrder() → pass or reject
7. Creates OrderState(Submitting), fires OrderStateChanged event
8. Broker.PlaceOrderAsync(request) → async to network
9. On success: OrderState → Accepted, BrokerOrderId set
10. Broker.OrderUpdateStream fires when order state changes
11. OrderService.OnBrokerOrderUpdate → updates OrderState, fires event
12. DomViewModel.RefreshOrders() → rebuilds DOM markers
13. AuditLogger writes JSONL entry for every step
```
