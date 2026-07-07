# Risk Controls

FastDOM implements a mandatory multi-layer risk system. Every order passes through all layers before reaching the broker.

## Architecture

```
User Action (click/hotkey/button)
    ↓
HotkeyService / OrderService
    ↓
RiskManager.ValidateOrder()  ← MANDATORY, cannot be bypassed
    ↓
OrderService.SubmitOrderAsync()
    ↓
MockBrokerClient / SchwabBrokerClient
    ↓
AuditLogger (logs everything regardless of outcome)
```

## Risk Settings (risk.profile.json)

| Setting | Default | Purpose |
|---------|---------|---------|
| `liveTradingEnabled` | `false` | Master live-mode switch |
| `accountWhitelist` | `[]` | Empty = allow all accounts; non-empty = only listed |
| `symbolWhitelist` | `["SPY","QQQ",...]` | Only these symbols allowed |
| `symbolBlacklist` | `[]` | These symbols always blocked |
| `maxSharesPerOrder` | `100` | Hard cap on shares per single order |
| `maxNotionalPerOrder` | `25000` | Hard cap on $ per order |
| `maxPositionNotionalPerSymbol` | `50000` | Max open $ exposure per symbol |
| `maxDailyLoss` | `500` | Daily realized P/L floor — blocks opens below |
| `maxOrdersPerMinute` | `20` | Rate limit (local, not broker-side) |
| `allowShortSelling` | `false` | Short selling disabled by default |
| `allowExtendedHours` | `false` | Pre/post market disabled by default |
| `requireConfirmationAboveNotional` | `10000` | Prompt for orders above this size |
| `marketDataStaleMs` | `2500` | Treat quote older than this as stale |
| `disableOpeningOrdersWhenMarketDataStale` | `true` | Block opens on stale data |
| `maxSpreadForMarketOrders` | `0.05` | Reject market orders if spread > this |

## Kill Switch

The large red button in the lower-left panel:
1. First click: shows "CONFIRM: Click again to KILL"
2. Second click within 3 seconds: executes
3. Execution: cancels all working orders for the symbol, then flattens position at market
4. Everything is logged to the audit trail
5. Keyboard: `Ctrl+Shift+E` (double-press required)

## Daily Loss Lockout

When realized P/L drops below `-maxDailyLoss`:
- All new **opening** orders are blocked
- Existing working orders remain
- **Closing** orders (flatten, cover) are always allowed
- Displayed as a red warning in the status bar
- Reset via `RiskManager.Reset()` (only at start of new trading session)

Note: P/L tracking is best-effort based on fill data from the broker. If the broker API doesn't provide reliable P/L data, enable `requireConfirmAllLiveOrders` as a manual safety net.

## Order Validation Sequence

1. Kill switch active? → Block
2. No account selected? → Block
3. Live mode: account whitelisted? → Check
4. Symbol blacklisted? → Block
5. Symbol whitelisted? → Check
6. Quantity > 0? → Check
7. Quantity ≤ maxSharesPerOrder? → Check
8. Market data stale? → Check (if configured)
9. Notional ≤ maxNotionalPerOrder? → Check
10. Spread OK for market orders? → Check
11. Short selling allowed? → Check
12. Extended hours allowed? → Check
13. Daily loss limit? → Check
14. Order rate limit? → Check
15. Above confirmation threshold? → Request confirmation

## Before Live Trading Checklist

- [ ] Read all of this document
- [ ] Tested all order types in Simulation mode
- [ ] Set conservative `maxSharesPerOrder` (start with 10-25)
- [ ] Set `maxDailyLoss` to an amount you can accept losing
- [ ] Symbol whitelist contains only symbols you intend to trade
- [ ] Kill switch key binding tested and memorized
- [ ] Emergency flatten button tested in simulation
- [ ] Hotkeys tested and confirmed working
- [ ] Market data stale threshold set appropriately for your connection
- [ ] `liveTradingEnabled: true` only after all above done
- [ ] `confirmFirstLiveOrder: true` in appsettings.json
- [ ] Account whitelist set to your specific account
- [ ] Confirmed refresh token not expired (7-day window)
- [ ] Running on a stable network connection (VPS near exchange is ideal)
