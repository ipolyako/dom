# FastDOM

FastDOM is a Windows execution terminal for active stock and option trading. It combines a fast depth-of-market ladder, configurable order actions, chart trading, Level 2 liquidity visualization, account positions, market movers, and broker-backed order management in one desktop application.

The primary live integration is the Schwab Trader API. Simulation, Alpaca Paper, and Alpaca Live modes are also supported.

> **Live-trading warning:** FastDOM can submit, replace, cancel, and flatten real orders. Review the risk configuration and test every action in Simulation or paper mode before enabling live trading.

## Main capabilities

- Clickable DOM ladder with live bid, ask, price, working orders, position, and P/L
- Drag-to-replace orders on the DOM and charts
- Configurable hot buttons and keyboard shortcuts
- Open-orders window with individual and bulk cancellation
- Interactive candlestick charts with volume, EMA 9/20, VWAP, liquidity, prior-session levels, premarket levels, and Camarilla levels
- Chart-based order staging, submission, cancellation, and risk actions
- L2 Heat window with expandable levels, liquidity concentration, zoom/binning, and multiple symbol tabs
- Schwab market movers: gainers, most active, and losers
- Positions panel with live open/day P/L and symbol selection
- Extended-hours order handling
- Workspace restoration for the main window, charts, L2 Heat, Movers, symbols, timeframes, tabs, sizes, and positions
- Local risk validation, order audit logging, and emergency cancel/flatten controls

## Supported environment

- Windows 10 or Windows 11, x64
- An interactive desktop session (FastDOM is a WPF application)
- A Schwab developer application for Schwab Live mode
- .NET 8 SDK only when building from source

The checked-in release under `publish\` is self-contained. A separate .NET installation is not required to run it.

## Repository layout

```text
publish/                       Canonical runnable installation and runtime configuration
FastDOM/FastDOM.App/           WPF application, views, controls, and view models
FastDOM/FastDOM.Core/          Order, account, position, and risk domain models
FastDOM/FastDOM.Broker/        Broker interfaces, runtime proxies, and shared services
FastDOM/FastDOM.Broker.Schwab/ Schwab authentication, orders, accounts, history, and streaming
FastDOM/FastDOM.Broker.Alpaca/ Alpaca broker and market-data integration
FastDOM/FastDOM.MarketData/    Market-data interfaces and models
FastDOM/FastDOM.Infrastructure Configuration, logging, and security helpers
FastDOM/FastDOM.Tests/         Automated tests
FastDOM/config-examples/       Safe configuration templates
FastDOM/docs/                  Detailed architecture and integration notes
Publish-FastDOM.ps1            Canonical self-contained publisher
```

`E:\AIWork\dom\publish` is the only runtime/publish location in the working installation. Do not create or launch a nested `FastDOM\publish` copy.

## Install and run

### Use the included self-contained build

1. Clone or download the repository on a Windows x64 machine.
2. Configure the JSON files in `publish\` as described below.
3. Run:

```powershell
Start-Process .\publish\FastDOM.exe
```

You can also double-click `publish\FastDOM.exe` in Explorer.

Do not move only the executable. Keep the entire `publish\` directory together because the application, .NET runtime, native WPF libraries, and configuration files are deployed as one folder.

### Build and publish from source

Install the .NET 8 SDK, then run from the repository root:

```powershell
.\Publish-FastDOM.ps1
```

This command:

- builds a Release `win-x64` self-contained application;
- stops the canonical running instance before copying files;
- publishes only to `publish\`;
- preserves runtime credentials, hotkeys, hot buttons, risk settings, and workspace layout;
- removes its temporary build directory; and
- starts FastDOM after publishing.

Build without restarting:

```powershell
.\Publish-FastDOM.ps1 -NoStart
```

Development commands:

```powershell
dotnet restore .\FastDOM\FastDOM.sln
dotnet build .\FastDOM\FastDOM.sln -c Release
dotnet test .\FastDOM\FastDOM.Tests\FastDOM.Tests.csproj -c Release
```

## Runtime configuration

FastDOM reads configuration from the directory containing `FastDOM.exe`. In the canonical installation, that is `publish\`.

| File | Purpose | Sensitive |
|---|---|---|
| `appsettings.json` | Mode, default symbol/account, sizes, DOM depth, and UI preferences | No |
| `broker.schwab.json` | Schwab application key, callback, and API endpoints | Treat app key as private |
| `token.source.json` | External Db2/Derby token-source connection and schema | **Yes** |
| `alpaca.json` | Alpaca key, secret, and paper/live selection | **Yes** |
| `risk.profile.json` | Live enablement, limits, whitelists, and safety rules | No |
| `hotbuttons.json` | Hot-button definitions, layouts, and scripts | Usually no |
| `hotkeys.json` | Keyboard shortcuts and dangerous-action behavior | No |
| `workspace.layout.json` | Automatically saved window layout and chart/L2 state | Machine-specific |

Never commit real broker secrets, access tokens, refresh tokens, database passwords, or account hashes. The credential-bearing runtime files are ignored by Git.

### `appsettings.json`

Important settings:

```json
{
  "mode": "SchwabLive",
  "defaultSymbol": "SPY",
  "defaultAccountId": "864",
  "defaultShareSize": 100,
  "shareSizePresets": [100, 200, 500],
  "domVisibleLevels": 140,
  "confirmFirstLiveOrder": true
}
```

Supported modes:

- `Simulation`
- `SchwabLive`
- `AlpacaPaper`
- `AlpacaLive`

`defaultAccountId` supports suffix matching. For example, `"864"` selects the available account whose ID ends in 864 without storing the full account number.

## Schwab setup

### 1. Create a Schwab developer application

At [developer.schwab.com](https://developer.schwab.com):

1. Create an application.
2. Add both Accounts and Trading Production and Market Data Production.
3. Register the callback URL used in `broker.schwab.json`; the default is `https://127.0.0.1:8182`.
4. Wait until the application is marked ready for use.

Schwab does not provide a public paper-trading API. Use Simulation or Alpaca Paper when validating order behavior.

### 2. Configure `broker.schwab.json`

Copy the example if the runtime file does not exist:

```powershell
Copy-Item .\publish\broker.schwab.example.json .\publish\broker.schwab.json
```

Set the application key and ensure the callback URL exactly matches the Schwab developer portal:

```json
{
  "appKey": "YOUR_SCHWAB_APP_KEY",
  "callbackUrl": "https://127.0.0.1:8182",
  "useSandbox": false
}
```

`useSandbox` is a local order-blocking safeguard; it is not a Schwab paper environment.

### 3. Configure the Schwab token source

The current Schwab authentication provider reads the app key, app secret, account hash, and refresh token from the configured Db2/Derby token database. Create `publish\token.source.json` from the example and supply the environment-specific connection values:

```powershell
Copy-Item .\publish\token.source.example.json .\publish\token.source.json
```

Typical structure:

```json
{
  "Host": "TRADER",
  "Port": 1527,
  "Database": "tradedb",
  "Schema": "ROCH",
  "User": "ROCH",
  "Password": "YOUR_DATABASE_PASSWORD",
  "AuthRefTable": "AUTHREF",
  "TokenTable": "TOKEN",
  "AppKeyColumn": "APPKEY",
  "AppSecretColumn": "APPSECRET",
  "AccountHashColumn": "ACCOUNTHASH",
  "RefreshTokenColumn": "TOKEN",
  "Purpose": "TRADE"
}
```

The host, database, credentials, schema, table names, and column names depend on the token database used in your environment. Do not invent these values or commit the completed file.

### 4. Configure live-trading risk

`risk.profile.json` is enforced before an order reaches the broker. `liveTradingEnabled` must be enabled for real orders, but only after testing.

Recommended controls include:

- account whitelist;
- symbol whitelist/blacklist;
- maximum shares and notional per order;
- maximum position notional;
- maximum daily loss;
- maximum order rate;
- short-selling and extended-hours permissions;
- stale-market-data protection;
- confirmation thresholds.

Start with conservative, nonzero limits. Test buy, sell, stop, cancel, replace, flatten, and hotkey paths before increasing them.

## First-run verification

1. Start FastDOM.
2. Confirm the mode in the upper-left corner.
3. Confirm the intended account is selected.
4. Enter a liquid symbol such as `SPY` and press Enter.
5. Verify bid, ask, last price, data age, and DOM updates.
6. Open Orders and reconcile working orders with the broker platform.
7. Open Chart, L2 Heat, and Movers and verify their data.
8. Test hotkeys while disconnected, in Simulation, or in paper mode first.
9. Close FastDOM normally and reopen it to verify workspace restoration.

## Trading behavior and safety

- Extended-hours equity orders must be `Limit` with `Day` duration. Stop orders are not accepted by Schwab during extended hours.
- Direct chart and DOM orders still pass through local validation, risk checks, confirmation rules, and broker validation.
- Dangerous fixed hotkeys can require a double press.
- The large kill switch cancels working orders for the active symbol and attempts to flatten its position after confirmation.
- Cancelling or flattening from FastDOM should always be verified in the broker platform.
- FastDOM is a retail execution application, not exchange-colocated HFT infrastructure.

## Window and workspace behavior

FastDOM saves `publish\workspace.layout.json` during a normal shutdown. On the next launch it restores:

- main-window size, location, and maximized state;
- every chart window, symbol, timeframe, extended-hours selection, and placement;
- L2 Heat placement, symbol tabs, and selected tab;
- Movers placement.

If monitor geometry changes, restored windows are constrained to the available virtual desktop. Force-killing the process can prevent the most recent layout changes from being saved.

## Troubleshooting

### Explorer says .NET must be installed

The application was published as framework-dependent. Rebuild the canonical self-contained installation:

```powershell
.\Publish-FastDOM.ps1
```

Verify `publish\coreclr.dll`, `publish\hostfxr.dll`, and `publish\hostpolicy.dll` exist next to `FastDOM.exe`.

### The wrong or old application opens

Launch only:

```text
<repository>\publish\FastDOM.exe
```

There should be no nested `FastDOM\publish` installation.

### Schwab authentication fails

- Confirm the token database is reachable from this machine.
- Confirm `token.source.json` credentials, schema, purpose, tables, and columns.
- Confirm the refresh token is current.
- Confirm the Schwab app key/secret used by the token source belong together.
- Confirm the Schwab application is ready for use.

### Market data appears but orders do not

Process connectivity and trading readiness are different. Check:

- selected account;
- `liveTradingEnabled`;
- account and symbol whitelists;
- risk limits;
- order type/session compatibility;
- stale-data and spread checks;
- open-order reconciliation and application logs.

### Orders appear in thinkorswim but not FastDOM

Open the Orders window and allow broker synchronization to complete. Verify that the selected account and symbol match thinkorswim. On weekends or outside the order session, submitted orders may remain working without market activity.

## Additional documentation

- [Architecture](FastDOM/docs/Architecture.md)
- [User guide](FastDOM/docs/UserGuide.md)
- [Schwab integration](FastDOM/docs/SchwabIntegration.md)
- [Alpaca integration](FastDOM/docs/AlpacaIntegration.md)
- [Hotkeys](FastDOM/docs/Hotkeys.md)
- [Risk controls](FastDOM/docs/RiskControls.md)
- [Build instructions](FastDOM/docs/BuildInstructions.md)

The root README describes the current canonical installation and publishing workflow. Some deeper documents may cover legacy or alternative configurations; when they conflict, follow this README and the current configuration examples.

## License and responsibility

No warranty is provided. Trading involves substantial risk, including the loss of principal. You are responsible for broker permissions, credentials, configuration, order review, regulatory compliance, and all activity submitted through the application.
