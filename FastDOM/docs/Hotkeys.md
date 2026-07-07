# FastDOM Hotkey Reference

## Default Bindings

| Hotkey | Action | Notes |
|--------|--------|-------|
| `Ctrl+B` | Buy Marketable Limit | Current symbol, default share size |
| `Ctrl+S` | Sell Marketable Limit | Current symbol, default share size |
| `Ctrl+F` | Flatten Symbol | **Double-press required** |
| `Ctrl+R` | Reverse Position | **Double-press required** |
| `Ctrl+X` | Cancel All Symbol Orders | |
| `Ctrl+Shift+X` | Cancel All Account Orders | Requires confirmation dialog |
| `Ctrl+1` | Share Size Preset 1 | Default: 100 |
| `Ctrl+2` | Share Size Preset 2 | Default: 200 |
| `Ctrl+3` | Share Size Preset 3 | Default: 500 |
| `Ctrl+Up` | Increase Selected Order Price by Tick | |
| `Ctrl+Down` | Decrease Selected Order Price by Tick | |
| `Ctrl+Shift+E` | Emergency: Flatten + Cancel All | **Double-press required** |
| `Esc` | Cancel pending UI action | Does NOT cancel live orders |

## Safety Rules

1. **Local hotkeys only by default.** Global hotkeys must be explicitly enabled in `hotkeys.json` (`globalHotkeysEnabled: true`). Global hotkeys fire even when FastDOM is not in focus.
2. **Hotkeys disabled while typing.** If focus is on a TextBox, hotkeys are suppressed.
3. **Double-press detection.** Dangerous actions (Flatten, Reverse, Emergency) require two keypresses within `dangerousActionDoublePressMs` (default 500ms).
4. **Armed/Disarmed indicator.** The blue "HK ARMED" badge in the top bar shows hotkey state. Click it or press the toggle to disarm. All hotkeys are ignored when disarmed.
5. **All hotkey actions pass through RiskManager.** A hotkey cannot bypass risk limits.
6. **Toast feedback.** Every hotkey action shows a toast notification with the result (accepted/rejected and reason).
7. **Cooldown per action.** Each binding has a configurable `cooldownMs` (default 0). Set to e.g. 500ms for accidental repeat protection.

## DOM Click Modifiers

| Click + Modifier | Action |
|-----------------|--------|
| Left click (buy column) | Buy Limit |
| Left click (sell column) | Sell Limit |
| `Shift` + click | Stop Market |
| `Ctrl` + click | Stop Limit |
| `Alt` + click | Bracket entry |
| Right click | Context menu |

## Configuration (hotkeys.json)

```json
{
  "globalHotkeysEnabled": false,
  "debounceMs": 250,
  "dangerousActionDoublePressMs": 500,
  "bindings": [
    {
      "id": "buy_mkt",
      "label": "Buy Marketable Limit",
      "keyGesture": "Ctrl+B",
      "actionType": "BuyMarketableLimit",
      "requireDoublePress": false,
      "requireConfirmation": false,
      "cooldownMs": 0,
      "isEnabled": true,
      "isDangerous": false
    }
  ]
}
```
