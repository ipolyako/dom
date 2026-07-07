# FastDOM — User Guide

FastDOM is a WPF/.NET 8 trading execution terminal built around a Depth of Market (DOM) price ladder. It connects to the Schwab Trader API in live or sandbox mode, or runs entirely in simulation (SIM) mode with a mock broker and market data generator. It is an execution-only terminal — no strategies, no automation beyond the features described here.

---

## Table of Contents

1. [Application Layout](#1-application-layout)
2. [Modes: SIM, Sandbox, Live](#2-modes-sim-sandbox-live)
3. [Symbol and Account Selection](#3-symbol-and-account-selection)
4. [DOM Price Ladder](#4-dom-price-ladder)
5. [One-Click DOM Trading](#5-one-click-dom-trading)
6. [Order Ticket](#6-order-ticket)
7. [Position Panel](#7-position-panel)
8. [Share Size](#8-share-size)
9. [Hot Buttons](#9-hot-buttons)
10. [Hotkeys](#10-hotkeys)
11. [Hotkey Configuration Dialog](#11-hotkey-configuration-dialog)
12. [Kill Switch](#12-kill-switch)
13. [Risk Manager](#13-risk-manager)
14. [Activity Log and Toast](#14-activity-log-and-toast)
15. [Configuration Files](#15-configuration-files)
16. [Schwab API Integration](#16-schwab-api-integration)
17. [Architecture Overview](#17-architecture-overview)

---

## 1. Application Layout

```
┌─────────────────────────────────────────────────────────────────────────────┐
│  [SIM]  [Symbol▼]  [Account▼]     Quote display      [Age] [Connect] [HK] [⚙]│  ← Top bar
├──────────────────┬────────────────────────────────┬────────────────────────┤
│  POSITION        │  BUY  │BID│  PRICE  │ASK│  SELL│  HOT BUTTONS           │
│  P&L display     ├───────┼───┼─────────┼───┼──────┤                        │
│                  │       │   │  DOM    │   │      │  [Buy MKT] [Sell MKT]  │
│  ORDER TICKET    │  rows │   │  price  │   │      │  [Buy Ask] [Sell Bid]  │
│  Side/Qty/Type   │       │   │ ladder  │   │      │  [Flatten] [Reverse]   │
│  Limit/Stop/TIF  │       │   │         │   │      │  [CancelSym] [CancelAll]│
│  [SUBMIT][Clear] │       │   │         │   │      │                        │
│                  │       │   │         │   │      │                        │
│  ⚠ KILL SWITCH   │                                │                        │
├──────────────────┴────────────────────────────────┴────────────────────────┤
│  Toast line                                                                  │
│  Activity log (scrollable, last 500 entries)                                 │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## 2. Modes: SIM, Sandbox, Live

The trading mode is set in `%APPDATA%\FastDOM\appsettings.json`:

| `Mode` value    | Broker                        | Market data              | Badge      |
|-----------------|-------------------------------|--------------------------|------------|
| `Sim` (default) | MockBrokerClient              | MockMarketDataClient     | **SIM**    |
| `SchwabSandbox` | Schwab API (paper trading)    | Schwab streaming         | **SANDBOX**|
| `SchwabLive`    | Schwab API (real money)       | Schwab streaming         | **⚠ LIVE** |

Live trading requires additional safety gates — see [Section 13](#13-risk-manager) and [Section 16](#16-schwab-api-integration).

---

## 3. Symbol and Account Selection

**Symbol** — dropdown in the top bar (default list: SPY, QQQ, NVDA, TSLA, TQQQ, SQQQ, AAPL, MSFT).
- Pick from the list: DOM resubscribes immediately.
- Type a symbol and press **Enter**: DOM resubscribes and normalises to uppercase.

**Account** — dropdown shows accounts retrieved from the broker at connect time.
- Changing account updates the DOM, order ticket, and position panel.

---

## 4. DOM Price Ladder

The DOM shows a price ladder centred on the last traded price. Each row is one tick wide.

| Column | Content |
|--------|---------|
| **BUY**   | Your working buy orders at that price (qty shown, green). Click `×` to cancel. |
| **BID**   | NBBO bid size with a blue depth bar. |
| **PRICE** | Price level. Bold = last traded price. |
| **ASK**   | NBBO ask size with a red depth bar. |
| **SELL**  | Your working sell orders at that price (qty shown, red). Click `×` to cancel. |

**Row highlights:**
- Yellow tint = last traded price
- Blue tint = NBBO bid
- Red tint = NBBO ask
- Purple tint = your average cost / position price

**L2 badge** — appears in the DOM header when real Level 2 depth data is available from the broker.

**Lock / Center buttons** — Lock prevents the DOM from re-centering on price updates; Center scrolls back to the last price.

---

## 5. One-Click DOM Trading

Clicking a DOM row places an order immediately — no confirmation dialog.

| Click location | Action |
|----------------|--------|
| BUY column, price **below** ask | Place buy limit order at that price |
| BUY column, price **at/above** ask | Place buy marketable-limit order |
| SELL column, price **above** bid | Place sell limit order at that price |
| SELL column, price **at/below** bid | Place sell marketable-limit order |
| BUY column — order already exists at that price | **Cancel** that order instead |
| SELL column — order already exists at that price | **Cancel** that order instead |
| BUY/SELL `×` button | Cancel all orders at that price level |

The order uses the current **Share Size** (see Section 8). The toast line and activity log show the result.

Right-click on any row opens a context menu with additional order options.

---

## 6. Order Ticket

The order ticket on the left panel lets you construct and submit orders manually.

| Field | Options |
|-------|---------|
| **Side** | Buy / Sell |
| **Qty** | Number of shares |
| **Type** | Market, Limit, Stop Market, Stop Limit, Marketable Limit, Bracket |
| **Limit** | Limit price (used for Limit, Stop Limit, Marketable Limit) |
| **Stop** | Stop trigger price (used for Stop Market, Stop Limit) |
| **TIF** | Day, GTC, IOC, FOK, Extended Hours |

DOM clicks auto-populate the ticket fields in addition to submitting the order, so you can review the last clicked price.

**SUBMIT** sends the order through the risk manager. The status line below the button shows the result or rejection reason.

---

## 7. Position Panel

Displays the current position for the selected symbol and account:

- **Side** — LONG / SHORT / FLAT
- **Qty** — number of shares held
- **Avg Cost** — average fill price of the position
- **Unrealized P&L** — mark-to-market against last price (green/red)
- **Realized P&L** — closed P&L for this session

---

## 8. Share Size

The **Size** field in the left panel sets the default quantity for DOM clicks and hot buttons.

- Type directly in the text box, or
- Click the **100 / 200 / 500** preset buttons
- Hotkeys `Ctrl+D1`, `Ctrl+D2`, `Ctrl+D3` switch to size presets defined in `appsettings.json`

---

## 9. Hot Buttons

The right panel shows configurable action buttons. Default layout:

| Button | Action |
|--------|--------|
| Buy MKT | Market buy at current share size |
| Sell MKT | Market sell at current share size |
| Buy Ask | Limit buy at current ask price |
| Sell Bid | Limit sell at current bid price |
| Buy Bid | Limit buy at current bid price |
| Sell Ask | Limit sell at current ask price |
| Flatten | Cancel all orders for symbol, then market-close position |
| Reverse | Cancel all orders for symbol, then market-flip position (2× qty) |
| Cancel Sym | Cancel all working orders for the current symbol |
| Cancel All | Cancel all working orders across all symbols |

Hot button layout, colors, quantities, and price rules are configured in `%APPDATA%\FastDOM\appsettings.json` under the `HotButtons` array. Each button supports:
- `QuantityRule` — Fixed shares, dollar amount, % of position, or risk-based sizing
- `PriceRule` — Bid, Ask, Last, Mid, offset from bid/ask, or manual price
- `RequireConfirmation` — show a dialog before executing

---

## 10. Hotkeys

Keyboard shortcuts fire hot button actions without clicking. The hotkey system has an armed/disarmed toggle — the **HK ARMED / HK OFF** indicator in the top bar shows the state. Click it to toggle, or press the binding for any action while armed.

**Hotkeys are suppressed when a text box has focus**, preventing accidental orders while typing prices.

Default bindings:

| Gesture | Action | Notes |
|---------|--------|-------|
| `Ctrl+B` | Buy Marketable Limit | |
| `Ctrl+S` | Sell Marketable Limit | |
| `Ctrl+F` | Flatten Symbol | Double-press required |
| `Ctrl+R` | Reverse Position | Double-press required |
| `Ctrl+X` | Cancel All Symbol | |
| `Ctrl+Shift+X` | Cancel All Account | Requires confirmation |
| `Ctrl+D1` | Size Preset 1 | |
| `Ctrl+D2` | Size Preset 2 | |
| `Ctrl+D3` | Size Preset 3 | |
| `Ctrl+Up` | Increase price by one tick | |
| `Ctrl+Down` | Decrease price by one tick | |
| `Ctrl+Shift+E` | Emergency Flatten + Cancel | Double-press required |

**Double-press** — dangerous actions (marked ⚠) require two presses within 500 ms. The first press is silently acknowledged; the second fires the action.

---

## 11. Hotkey Configuration Dialog

Open with the **⚙** button in the top bar (next to the HK indicator).

| Column | Meaning |
|--------|---------|
| **On** | Enable/disable this binding |
| **Action** | Human-readable action name |
| **Key Gesture** | Current key combination |
| **2× Press** | Require double-press to fire |
| **⚠** | Dangerous action indicator |

**To rebind a key:**
1. Click the `…` button on any row — the gesture cell turns yellow and shows "Press key…"
2. Press the new key combination (any modifier + key)
3. Press **Escape** to cancel without changing

**Reset Defaults** restores all 12 factory bindings.

**Save** writes changes to `%APPDATA%\FastDOM\hotkeys.json` immediately. Changes take effect on the next keypress without restarting.

---

## 12. Kill Switch

The red **⚠ KILL SWITCH — CANCEL + FLATTEN** button at the bottom of the left panel is a two-click safety mechanism:

1. First click: button turns dark red and shows "CONFIRM: Click again to KILL". Expires after 3 seconds.
2. Second click within 3 seconds: executes atomically —
   - Cancels all working orders for the current symbol
   - Submits a market order to flatten the position (bypasses confirmation threshold)
   - Activates the internal kill switch flag, blocking further orders until reset
   - Writes an entry to the audit log

After activation, a modal dialog confirms the action. Orders and position must be verified manually.

---

## 13. Risk Manager

Every order — regardless of source (DOM click, hot button, hotkey, order ticket) — passes through the risk manager before being sent to the broker. Validation runs in this order:

1. **Kill switch** — blocks all orders if active
2. **Account whitelist** — (live mode only) rejects orders from non-whitelisted accounts
3. **Symbol blacklist / whitelist** — configurable per-symbol rules
4. **Quantity** — `MaxSharesPerOrder` (0 = unlimited)
5. **Market data staleness** — rejects opening orders if data age exceeds `MarketDataStaleMs` and `DisableOpeningOrdersWhenMarketDataStale` is true
6. **Notional size** — `MaxNotionalPerOrder` in dollars (0 = unlimited, default 1,000,000)
7. **Spread** — `MaxSpreadForMarketOrders` rejects market orders when bid-ask spread is too wide (0 = disabled)
8. **Short selling** — controlled by `AllowShortSelling` (default false)
9. **Extended hours** — controlled by `AllowExtendedHours` (default false)
10. **Daily loss limit** — `MaxDailyLoss` in dollars; once hit, only flattening/cancelling is allowed
11. **Order rate limit** — `MaxOrdersPerMinute` (default 20)
12. **Confirmation threshold** — `RequireConfirmationAboveNotional` prompts the user for large orders (0 = disabled)

**Editing risk limits** — edit `%APPDATA%\FastDOM\risk.profile.json`. The file is loaded at startup and overrides all C# defaults. A restart is required for changes to take effect.

Default SIM values (all permissive):

```json
{
  "MaxSharesPerOrder": 0,
  "MaxNotionalPerOrder": 1000000,
  "RequireConfirmationAboveNotional": 0,
  "MaxSpreadForMarketOrders": 0,
  "DisableOpeningOrdersWhenMarketDataStale": false,
  "MaxDailyLoss": 500,
  "MaxOrdersPerMinute": 20
}
```

---

## 14. Activity Log and Toast

**Toast** — single-line status at the top of the log panel. Shows the result of the last order or action.

**Activity log** — scrollable list of timestamped events (last 500 entries):
- Order submitted / filled / cancelled / rejected
- Hot button and hotkey actions
- Connection state changes
- Symbol subscription changes
- Kill switch activation

---

## 15. Configuration Files

All configuration lives in `%APPDATA%\FastDOM\` and is saved when the app closes.

| File | Contents |
|------|----------|
| `appsettings.json` | Mode, default symbol, share size presets, hot button definitions |
| `risk.profile.json` | All risk limits (see Section 13) |
| `hotkeys.json` | Hotkey bindings (see Section 11) |
| `broker.schwab.json` | Schwab client ID — **never commit this file** |

The `.gitignore` excludes `broker.schwab.json` and `appsettings.local.json`.

---

## 16. Schwab API Integration

**Authentication** — OAuth 2.0 PKCE flow:
1. The app opens a browser to the Schwab authorisation URL.
2. After login, Schwab redirects to `https://127.0.0.1:8182/callback` (local HTTPS server started by the app).
3. The auth code is exchanged for access + refresh tokens.
4. Tokens are stored encrypted in Windows Credential Manager (DPAPI).

**Requirements to place a live order:**
- `Mode` = `SchwabLive` in `appsettings.json`
- OAuth login completed successfully
- Account listed in `AccountWhitelist` in `risk.profile.json`
- Symbol not in `SymbolBlacklist`
- Valid market data (not stale, if `DisableOpeningOrdersWhenMarketDataStale` = true)
- Order passes all risk checks
- First live order after app launch confirmed by user (if `RequireConfirmAllLiveOrders` = true)

**Client ID setup:**
1. Register an app at developer.schwab.com.
2. Set the callback URL to `https://127.0.0.1:8182/callback`.
3. Copy the client ID into `%APPDATA%\FastDOM\broker.schwab.json`:
   ```json
   { "ClientId": "your-client-id-here" }
   ```

---

## 17. Architecture Overview

```
FastDOM.sln
├── FastDOM.Core          — Models, enums, interfaces (no dependencies)
├── FastDOM.Broker        — RiskManager, broker interfaces, MockBrokerClient
├── FastDOM.Broker.Schwab — Schwab REST/streaming client, OAuth provider
├── FastDOM.MarketData    — Market data interfaces, MockMarketDataClient
├── FastDOM.Infrastructure— ConfigManager, AuditLogger, SecureStorage (DPAPI)
├── FastDOM.App           — WPF application (MVVM, DI, Views, ViewModels, Services)
└── FastDOM.Tests         — 46 unit and integration tests (xUnit)
```

**Key patterns:**
- **MVVM** — ViewModels use `CommunityToolkit.Mvvm` (`[ObservableProperty]`, `[RelayCommand]`)
- **Dependency Injection** — `Microsoft.Extensions.DependencyInjection`; all services registered in `App.xaml.cs`
- **Reactive streams** — `IObservable<T>` for quote, depth, and connection state updates
- **Order state machine** — `OrderState.Transition()` enforces valid status transitions; terminal states (Filled, Cancelled, Rejected) cannot be overwritten
- **DOM service** — `DomService` maintains the price ladder, merges L2 depth with working orders; `DomLadderRow` is an `ObservableObject` so WPF bindings refresh in-place without rebuilding the list

**Build and run:**
```powershell
# Build
& "C:\Program Files\dotnet\dotnet.exe" build FastDOM/FastDOM.sln

# Run tests
& "C:\Program Files\dotnet\dotnet.exe" test FastDOM/FastDOM.sln

# Publish self-contained single exe
& "C:\Program Files\dotnet\dotnet.exe" publish FastDOM/FastDOM.App `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -o publish
```
