# FastDOM — Execution Terminal

A lightweight Windows execution panel for active trading. Supports Schwab Trader API and Alpaca Markets. Replaces DAS Trader / TOS Active Trader / NinjaTrader DOM for fast order entry.

**This is an execution tool, not a charting platform.**

---

## Features

- Visual DOM (Depth of Market) price ladder with clickable order entry
- Hot buttons for common actions (Buy MKT, Sell MKT, Flatten, Reverse, etc.) — fully configurable
- Global hotkeys for fast keyboard trading
- **Alpaca Paper and Live** trading (key/secret auth, no OAuth required)
- **Schwab Trader API** integration (OAuth 2.0 via thinkorswim Derby token source)
- Four modes selectable at runtime: Simulation, Schwab Live, Alpaca Paper, Alpaca Live
- Account positions table with live P/L, clickable rows load symbol into DOM
- Full risk management: max shares, max notional, daily loss lockout, kill switch
- Order lifecycle tracking: draft → validating → submitting → accepted → filled
- DPAPI-protected token storage for Schwab (never stores raw secrets in plaintext)
- Rotating audit log (JSONL) + human-readable activity log
- Dark theme, compact layout

---

## Requirements

**To run** — Windows 10/11 x64 only. No .NET installation needed; the published exe is fully self-contained.

**To build from source** — .NET 8 SDK required.
- Download: https://dotnet.microsoft.com/download/dotnet/8.0
- If `dotnet` is not on PATH after install, use the full path:
  ```
  & "C:\Program Files\dotnet\dotnet.exe" publish ...
  ```

**For live trading** — a Schwab developer account at [developer.schwab.com](https://developer.schwab.com)

---

## Quick Start

```bash
git clone https://github.com/ipolyako/dom
cd dom/FastDOM
dotnet publish FastDOM.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
publish\FastDOM.exe
```

> **Note:** `IncludeNativeLibrariesForSelfExtract=true` is required or the app crashes at startup.
> The `clidriver\` folder (IBM DB2 CLI driver for Schwab Derby tokens) is included automatically in
> the publish output and must stay next to `FastDOM.exe`.

The app starts in **Simulation mode** on first launch — no broker connection or config setup required. Config files are auto-created next to the exe from the bundled examples.

To switch to Alpaca or Schwab, edit the JSON files next to `FastDOM.exe` (see **Config Files** below).

---

## Alpaca Setup

See `docs/AlpacaIntegration.md` for the complete guide.

**Summary:**
1. Create an account at [alpaca.markets](https://alpaca.markets)
2. Generate API keys (paper or live) from the Alpaca dashboard
3. Set `ApiKey`, `ApiSecret`, and `IsPaper` in `alpaca.json`
4. Select **Alpaca Paper** or **Alpaca Live** from the mode dropdown and click Connect

No OAuth flow, no certificate setup — key/secret auth only.

## Schwab Live Setup

See `docs/SchwabIntegration.md` for the complete setup guide.

**Summary:**
1. Register app at developer.schwab.com (select both API products)
2. Set callback URL to `https://127.0.0.1:8182`
3. Wait for "Ready for use" status (may take several days)
4. Set `appKey` in `broker.schwab.json`
5. Ensure thinkorswim is installed and logged in (Derby token source)
6. Configure `token.source.json` with the Derby DB path
7. Run the app and select **Schwab Live** from the mode dropdown
8. App Secret is stored via DPAPI (Windows Credential protection)
9. Load a risk profile with `liveTradingEnabled: true`

---

## Project Structure

```
FastDOM.App/            WPF UI, views, viewmodels, app shell
FastDOM.Core/           Domain models, order types, state machines
FastDOM.Broker/         Broker interfaces, MockBrokerClient, runtime proxies
FastDOM.Broker.Schwab/  Schwab OAuth + Derby token source + order placement + streaming
FastDOM.Broker.Alpaca/  Alpaca broker client + market data client
FastDOM.MarketData/     Market data interfaces + MockMarketDataClient
FastDOM.Infrastructure/ Config, logging, DPAPI secure storage
FastDOM.Tests/          Unit + integration tests
config-examples/        Sample JSON config files
docs/                   Architecture, integration guides, risk docs
```

---

## Config Files

Stored next to `FastDOM.exe` (auto-created on first run from `*.example.json` templates):

| File | Purpose |
|------|---------|
| `appsettings.json` | App mode, default symbol, default share size |
| `alpaca.json` | Alpaca API key, secret, paper/live flag |
| `broker.schwab.json` | Schwab App Key and callback URL |
| `token.source.json` | Derby DB path for Schwab thinkorswim token reading |
| `risk.profile.json` | Risk limits and safety settings |
| `hotkeys.json` | Keyboard shortcut bindings |
| `hotbuttons.json` | Hot button panel configuration |

Schwab App Secret and OAuth tokens are stored via DPAPI — never in JSON files.
Alpaca API keys are stored in `alpaca.json` — secure the file with filesystem permissions.

---

## Known Limitations

- Schwab does NOT offer a public sandbox/paper trading API. Use Alpaca Paper for paper trading.
- Schwab Level 2 depth (`NYSE_BOOK`, `NASDAQ_BOOK`) availability depends on your account data permissions.
- Schwab refresh tokens expire after 7 days — re-authentication required via thinkorswim login.
- Alpaca market data uses the IEX feed by default (free tier); may be delayed ~15 min. SIP feed requires a funded live account.
- Alpaca does not provide Level 2 order book data — DOM shows Level 1 (best bid/ask) only.
- DOM drag-and-drop order moving requires the order marker to be clicked precisely.
- This is a retail execution terminal — network latency to broker servers dominates. Not HFT infrastructure.
- `clidriver\` must remain next to `FastDOM.exe` — see `docs/BuildInstructions.md`.

---

## Safety

**Never run FastDOM in live mode without:**
- Reading `RiskControls.md`
- Setting a tested risk profile with conservative limits
- Testing all order types in simulation first
- Setting up the kill switch binding
- Understanding the 7-day OAuth refresh window

The kill switch (big red button) cancels all working orders and flattens the position. It requires two clicks to confirm.
