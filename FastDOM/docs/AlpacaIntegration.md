# Alpaca Integration

FastDOM supports Alpaca Markets as a broker in two modes:

| Mode | Description |
|------|-------------|
| **Alpaca Paper** | Paper trading via Alpaca's paper environment. Free, no real money. |
| **Alpaca Live** | Live trading against real Alpaca brokerage account. |

---

## Prerequisites

- An Alpaca account at [alpaca.markets](https://alpaca.markets)
- API key and secret from the Alpaca dashboard
  - Paper keys: **Dashboard → Paper Trading → API Keys**
  - Live keys: **Dashboard → Live Trading → API Keys**

---

## Configuration

Edit `alpaca.json` next to `FastDOM.exe`:

```json
{
  "ApiKey": "PKxxxxxxxxxxxxxxxxxxxxxxxx",
  "ApiSecret": "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
  "IsPaper": true
}
```

| Field | Description |
|-------|-------------|
| `ApiKey` | Alpaca API key ID |
| `ApiSecret` | Alpaca API secret key |
| `IsPaper` | `true` for paper trading, `false` for live |

The `IsPaper` flag controls which Alpaca endpoint is used:

| `IsPaper` | Trader API base | Stream base |
|-----------|----------------|-------------|
| `true` | `https://paper-api.alpaca.markets/v2` | `wss://paper-api.alpaca.markets/stream` |
| `false` | `https://api.alpaca.markets/v2` | `wss://api.alpaca.markets/stream` |

Market data is always sourced from `https://data.alpaca.markets/v2` regardless of mode.

---

## Switching Modes at Runtime

1. In the top bar, click the **mode dropdown** (leftmost control).
2. Select **Alpaca Paper** or **Alpaca Live**.
3. The app reconnects automatically — no restart needed.

The account number displayed in the account dropdown and badge comes directly from Alpaca's
`account_number` field (e.g., `PA3U1FYFPMLI` for paper accounts).

---

## API Endpoints Used

### Authentication

All requests use HTTP header authentication:
```
APCA-API-KEY-ID: <ApiKey>
APCA-API-SECRET-KEY: <ApiSecret>
```

No OAuth flow is required. Keys are read directly from `alpaca.json`.

### Broker (Trader API)

| Operation | Endpoint |
|-----------|----------|
| Verify connection / get account info | `GET /v2/account` |
| List open positions | `GET /v2/positions` |
| Place order | `POST /v2/orders` |
| Cancel order | `DELETE /v2/orders/{order_id}` |
| Replace order | `PATCH /v2/orders/{order_id}` |
| Get order status | `GET /v2/orders/{order_id}` |
| List open orders | `GET /v2/orders?status=open` |

### Order Schema

```json
{
  "symbol": "SPY",
  "qty": "100",
  "side": "buy",
  "type": "market",
  "time_in_force": "day"
}
```

For limit orders, add `"limit_price": "549.50"`.

### Trade Update Stream (WebSocket)

FastDOM subscribes to Alpaca's account trade update stream for real-time order fills:

```
wss://paper-api.alpaca.markets/stream
```

Authentication message:
```json
{ "action": "authenticate", "data": { "key_id": "...", "secret_key": "..." } }
```

Listen message:
```json
{ "action": "listen", "data": { "streams": ["trade_updates"] } }
```

### Market Data (REST + WebSocket)

**Snapshot (used on symbol load):**
```
GET /v2/stocks/{symbol}/snapshot
```

Returns latest trade, quote, minute bar, daily bar, and previous daily bar.

**Streaming quotes:**
```
wss://stream.data.alpaca.markets/v2/iex
```

Subscribe message:
```json
{ "action": "subscribe", "quotes": ["SPY"], "trades": ["SPY"] }
```

---

## Security Notes

- API keys are stored in plaintext in `alpaca.json`. Secure the file using filesystem
  permissions — restrict read access to your Windows user account.
- Alpaca keys do **not** expire unless manually regenerated. Rotate them if compromised.
- Paper and live keys are separate credentials — switching from `IsPaper: true` to `false`
  requires updating both `ApiKey` and `ApiSecret` in the config.
- FastDOM never logs the raw API key or secret. Only the first 4 characters of the key are
  shown in debug logs for identification purposes.

---

## Setup Checklist

- [ ] Create Alpaca account at alpaca.markets
- [ ] Generate API keys for the desired environment (paper or live)
- [ ] Set `ApiKey`, `ApiSecret`, and `IsPaper` in `alpaca.json`
- [ ] Select **Alpaca Paper** or **Alpaca Live** from the mode dropdown
- [ ] Click **Connect** — account number should appear in the account badge
- [ ] Load a symbol and verify DOM price levels appear
- [ ] Test order placement in **Alpaca Paper** before switching to live
- [ ] For live: set `liveTradingEnabled: true` in `risk.profile.json`
- [ ] For live: review all limits in `risk.profile.json` before submitting any order

---

## Known Limitations

- Alpaca does not provide Level 2 order book data through their standard API. The DOM shows
  only best bid/ask (Level 1) data.
- Market data for `IEX` feed (free tier) may be delayed by ~15 minutes for some symbols.
  Subscribe to the SIP feed for real-time data (requires funded live account).
- Short selling via Alpaca requires the symbol to be available for borrowing — check
  availability in the Alpaca dashboard before placing short orders.
- `Vol:0` on the quote strip is normal for paper accounts when the market data snapshot
  returns zero volume for the current session.
