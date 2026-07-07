# FastDOM — Execution Terminal

A lightweight Windows execution panel for active trading via the Schwab Trader API. Replaces DAS Trader / TOS Active Trader / NinjaTrader DOM for fast order entry.

**This is an execution tool, not a charting platform.**

---

## Features

- Visual DOM (Depth of Market) price ladder with clickable order entry
- Hot buttons for common actions (Buy MKT, Sell MKT, Flatten, Reverse, etc.)
- Global/local hotkeys for fast keyboard trading
- Schwab Trader API integration (OAuth 2.0, live order placement)
- Three modes: Simulation, SchwabSandbox (blocks orders), SchwabLive
- Full risk management: max shares, max notional, daily loss lockout, kill switch
- Order lifecycle tracking: draft → validating → submitting → accepted → filled
- DPAPI-protected token storage (never stores raw secrets in plaintext)
- Rotating audit log (JSONL) + human-readable activity log
- Dark theme, always-on-top, compact mode

---

## Requirements

- Windows 10/11 or Windows Server 2019+
- .NET 8 SDK (for building) or .NET 8 Runtime (for running published build)
- A Schwab developer account at [developer.schwab.com](https://developer.schwab.com) (for live mode)

---

## Quick Start (Simulation Mode)

1. Clone the repository
2. Build: `dotnet build FastDOM.sln -c Release`
3. Run: `dotnet run --project FastDOM.App`
4. The app starts in Simulation mode — no broker connection required
5. Select a symbol, click price levels to place simulated orders

---

## Building a Self-Contained Executable

```
dotnet publish FastDOM.App -c Release -r win-x64 --self-contained true
```

Output in `FastDOM.App/bin/Release/net8.0-windows/win-x64/publish/`

---

## Schwab Live Setup

See `SchwabIntegration.md` for the complete setup guide.

**Summary:**
1. Register app at developer.schwab.com (select both API products)
2. Set callback URL to `https://127.0.0.1:8182`
3. Wait for "Ready for use" status (can take several days)
4. Set `appKey` in `broker.schwab.json`
5. Run the app and click Connect — it opens a browser for OAuth login
6. App Secret is stored via DPAPI (Windows Credential protection)
7. Change `mode` in `appsettings.json` to `SchwabLive`
8. Load a risk profile with `liveTradingEnabled: true`
9. Read the **Before Live Trading** safety checklist

---

## Project Structure

```
FastDOM.App/            WPF UI, views, viewmodels, app shell
FastDOM.Core/           Domain models, order types, state machines
FastDOM.Broker/         Broker interfaces + MockBrokerClient
FastDOM.Broker.Schwab/  Schwab OAuth + order placement + streaming
FastDOM.MarketData/     Market data interfaces + MockMarketDataClient
FastDOM.Infrastructure/ Config, logging, DPAPI secure storage
FastDOM.Tests/          Unit + integration tests
config-examples/        Sample JSON config files
docs/                   Architecture, Schwab integration, risk docs
```

---

## Config Files

All stored in `%APPDATA%\FastDOM\`:

| File | Purpose |
|------|---------|
| `appsettings.json` | App mode, UI settings, defaults |
| `broker.schwab.json` | Schwab App Key and callback URL |
| `risk.profile.json` | Risk limits and safety settings |
| `hotkeys.json` | Keyboard shortcut bindings |
| `hotbuttons.json` | Hot button panel configuration |
| `layout.json` | Window position and layout |

Secrets (App Secret, tokens) are stored via DPAPI — never in JSON files.

---

## Known Limitations

- Schwab does NOT offer a public sandbox/paper trading API. "SchwabSandbox" mode in FastDOM connects to the live API but blocks all order submissions.
- Level 2 depth is available via Schwab's streaming WebSocket (`NYSE_BOOK`, `NASDAQ_BOOK`), but availability depends on your Schwab account data permissions.
- Refresh tokens expire after 7 days — you must re-authenticate after that.
- DOM drag-and-drop order moving is functional but drag detection requires the order marker to be clicked precisely.
- This is a retail execution terminal. Network latency to Schwab's servers dominates — this is not HFT infrastructure.
- Bracket orders, OCO, and OSO are confirmed supported by the Schwab API.
- `previewOrder` endpoint exists (`POST /trader/v1/accounts/{hash}/previewOrder`) — not yet wired in UI.

---

## Safety

**Never run FastDOM in live mode without:**
- Reading `RiskControls.md`
- Setting a tested risk profile with conservative limits
- Testing all order types in simulation first
- Setting up the kill switch binding
- Understanding the 7-day OAuth refresh window

The kill switch (big red button) cancels all working orders and flattens the position. It requires two clicks to confirm.
