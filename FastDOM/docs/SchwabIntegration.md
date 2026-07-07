# Schwab Trader API Integration

## Confirmed API Details

All endpoints and schemas confirmed from official Schwab developer portal and verified community implementations.

### Base URLs

| Purpose | URL |
|---------|-----|
| OAuth Authorize | `https://api.schwabapi.com/v1/oauth/authorize` |
| OAuth Token | `https://api.schwabapi.com/v1/oauth/token` |
| Trader API | `https://api.schwabapi.com/trader/v1/` |
| Market Data | `https://api.schwabapi.com/marketdata/v1/` |

### OAuth 2.0 Flow

1. **Authorization**: redirect user browser to authorize URL with `client_id`, `redirect_uri`, `response_type=code`, `scope=api`
2. **Callback**: Schwab redirects to `https://127.0.0.1:8182?code=CO.{code}`
3. **Token exchange**: POST to token URL with `grant_type=authorization_code`, basic auth header (`Base64(appKey:appSecret)`)
4. **Access token**: valid for **30 minutes** (1800 seconds)
5. **Refresh token**: valid for **7 days** — after which full re-auth is required
6. **Token refresh**: POST to token URL with `grant_type=refresh_token`

### Accounts

```
GET /trader/v1/accounts/accountNumbers
→ Returns array of { accountNumber, hashValue }

GET /trader/v1/accounts/{hashValue}?fields=positions
→ Returns account balances and positions
```

All order and account endpoints use the **hashValue** (encrypted account ID), not the plain account number.

### Order Placement

```
POST /trader/v1/accounts/{hashValue}/orders
Content-Type: application/json
Authorization: Bearer {access_token}

→ Returns HTTP 201 on success
→ Order ID is in the Location response header
```

### Confirmed Order Schemas

**Market order:**
```json
{
  "orderType": "MARKET",
  "session": "NORMAL",
  "duration": "DAY",
  "orderStrategyType": "SINGLE",
  "orderLegCollection": [{ "instruction": "BUY", "quantity": 10, "instrument": { "symbol": "SPY", "assetType": "EQUITY" } }]
}
```

**Limit order:** add `"price": "550.00"`

**Stop market:** `"orderType": "STOP"`, add `"stopPrice": "540.00"`

**Stop limit:** `"orderType": "STOP_LIMIT"`, add both `"stopPrice"` and `"price"`

**Bracket (entry + OCO):**
```json
{
  "orderStrategyType": "TRIGGER",
  "orderType": "LIMIT",
  "price": "550.00",
  ...,
  "childOrderStrategies": [{
    "orderStrategyType": "OCO",
    "childOrderStrategies": [
      { target LIMIT sell },
      { stop STOP_LIMIT sell }
    ]
  }]
}
```

### Order Instructions

| Action | Instruction |
|--------|-------------|
| Buy to open long | `BUY` |
| Sell long position | `SELL` |
| Sell short | `SELL_SHORT` |
| Buy to cover short | `BUY_TO_COVER` |

### Order Type Values

`MARKET`, `LIMIT`, `STOP`, `STOP_LIMIT`, `TRAILING_STOP`, `TRAILING_STOP_LIMIT`, `MARKET_ON_CLOSE`, `LIMIT_ON_CLOSE`

### Duration Values

`DAY`, `GOOD_TILL_CANCEL`, `FILL_OR_KILL`, `IMMEDIATE_OR_CANCEL`

### Session Values

`NORMAL`, `AM`, `PM`, `SEAMLESS`

### Market Data

**REST snapshot (L1):**
```
GET /marketdata/v1/quotes?symbols=SPY,NVDA&fields=quote,fundamental
Authorization: Bearer {token}
```

**Streaming (L1 + L2):**
- URL from: `GET /trader/v1/userPreference` → `streamerInfo[0].streamerSocketUrl`
- WebSocket protocol: JSON commands (`SUBS`, `ADD`, `UNSUBS`)
- Must send `ADMIN/LOGIN` first
- L1: `LEVELONE_EQUITIES` service
- L2: `NYSE_BOOK`, `NASDAQ_BOOK` services
- Streaming connection closes after ~30 seconds if no active subscriptions

### Rate Limits

- REST API: **120 requests/minute** (general)
- Orders: configurable at app registration, max **120 orders/minute**
- Do NOT poll market data via REST — use streaming
- Streaming: max **500 symbol subscriptions** per connection

### Sandbox

Schwab does NOT offer a publicly accessible paper trading API. The `schwab-py` library explicitly states "paper trading is not supported." FastDOM's "SchwabSandbox" mode connects to the live API for account/market data but blocks all order submissions locally.

### Setup Checklist

- [ ] Create account at developer.schwab.com
- [ ] Register app, add **both** "Accounts and Trading Production" AND "Market Data Production"
- [ ] Set callback URL to `https://127.0.0.1:8182` (exactly — no trailing slash)
- [ ] Set order rate limit (recommend 120)
- [ ] Wait for "Ready for use" status (may take several days)
- [ ] Set `appKey` in `broker.schwab.json`
- [ ] Store App Secret: run `FastDOM --setup-secret` or enter in Settings dialog
- [ ] Test connection in SchwabSandbox mode first
- [ ] Verify account discovery works
- [ ] Verify market data streaming works
- [ ] Test order placement in SchwabSandbox (orders blocked, but full validation runs)
- [ ] Set `liveTradingEnabled: true` in risk profile only when ready

### Token Management

FastDOM stores tokens using DPAPI (Windows Data Protection API):
- Raw tokens are never written to plaintext files
- Tokens are scoped to the Windows user account
- Access token auto-refreshes 5 minutes before expiry
- Refresh token expiry (7 days) is tracked; prompts for re-auth before it expires
- All token operations are logged (without revealing token values)
