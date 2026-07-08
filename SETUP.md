# FastDOM — Setup on a Fresh Machine

**Audience:** an AI assistant (Claude, GPT, Copilot, etc.) or a human following the same steps. Every command is idempotent — safe to re-run if something fails midway.

**Goal:** get `publish/FastDOM.exe` running with a valid broker connection on a new Windows machine, using only what's in the git checkout plus a few credentials the user must supply.

---

## What's already in the checkout

After `git clone`, the working tree contains:

| Path | Purpose |
|------|---------|
| `publish/FastDOM.exe` | The compiled app (folder-mode publish, ~151 KB) |
| `publish/*.dll` + `publish/coreclr.dll` etc. | Self-contained .NET 8 runtime — **no .NET install needed** |
| `publish/appsettings.json` | App preferences (mode, default symbol, share size). Safe to commit. |
| `publish/hotbuttons.json` | Your hot-button layout + scripts |
| `publish/hotkeys.json` | Global keyboard shortcut bindings |
| `publish/risk.profile.json` | Risk limits (max shares, daily loss, extended hours, etc.) |
| `publish/*.example.json` | Templates for credential files — copy and fill in |
| `FastDOM/` | Source code (only needed if rebuilding) |
| `config-examples/` | Master copies of the non-sensitive JSONs — copied to `publish/` at build time |

## What's NOT in the checkout (deliberately gitignored)

These three files hold credentials and MUST be created on each new machine:

| File | Contains | Where to get values |
|------|----------|----------------------|
| `publish/broker.schwab.json` | Schwab App Key + callback URL | [developer.schwab.com](https://developer.schwab.com) → your app |
| `publish/alpaca.json` | Alpaca API key + secret + `isPaper` flag | [alpaca.markets](https://alpaca.markets) → Paper or Live keys |
| `publish/token.source.json` | Database connection string + password used to fetch OAuth tokens | Ask the user — this is site-specific |

The Schwab **App Secret** is not in any JSON file. It is stored via Windows DPAPI (encrypted per-user, per-machine) and will be prompted for the first time you connect to Schwab.

---

## Setup steps

### 1. Prerequisites

- Windows 10 or 11, x64
- Git for Windows (only needed for `git clone` / pulling updates)
- **.NET SDK is not required** — the publish is self-contained. (SDK is only needed if you want to rebuild from source.)

Verify:

```powershell
[System.Environment]::OSVersion.Version         # Should be 10.x
[System.Environment]::Is64BitOperatingSystem    # Should be True
git --version                                   # Any recent version
```

### 2. Clone the repo

```powershell
git clone https://github.com/ipolyako/dom.git C:\temp\dom
Set-Location C:\temp\dom
```

Any path works — `C:\temp\dom` is the convention. If you use a different path, substitute it throughout.

### 3. Create the three credential files

For each file, copy the corresponding `*.example.json` and fill in real values. **The AI should ask the user for each secret — never invent placeholders and never commit these files.**

#### 3a. `publish/broker.schwab.json`

```powershell
Copy-Item C:\temp\dom\publish\broker.schwab.example.json C:\temp\dom\publish\broker.schwab.json
```

Then edit and set:
- `appKey` — from Schwab developer portal (looks like `Xy3l7DGKzAmUCN8Vh5ZmtUfDVTxLfHWe`, 32 chars)
- `callbackUrl` — must match what's registered in the Schwab app (default `https://127.0.0.1:8182`)
- `useSandbox` — leave `false` for real trading; Schwab has no public sandbox

The `_note_*` fields are documentation only. Leave them or remove them — the app ignores unknown fields.

#### 3b. `publish/alpaca.json`

```powershell
Copy-Item C:\temp\dom\publish\alpaca.example.json C:\temp\dom\publish\alpaca.json
```

Fill in:
- `apiKey` — Alpaca API key ID
- `apiSecret` — Alpaca API secret
- `isPaper` — `true` for paper trading (safest), `false` for live

For paper keys, get them at Alpaca dashboard → Paper Trading → View API Keys.

#### 3c. `publish/token.source.json`

**No example file exists** because contents are entirely site-specific. Ask the user for the JSON blob. Structure looks like:

```json
{
  "connectionString": "Server=...;Database=...;User Id=...;Password=...;",
  "query": "SELECT ... FROM ..."
}
```

If the user doesn't need database-sourced tokens (Alpaca-only setup, for example), the file can be omitted or left empty — the app will fall back to the interactive OAuth flow for Schwab. Ask before creating a stub.

### 4. Verify sensitive files are gitignored

Sanity check that git will not accidentally commit these:

```powershell
Set-Location C:\temp\dom
git check-ignore publish/broker.schwab.json publish/alpaca.json publish/token.source.json
```

Expected output: all three paths echoed back. If any line is missing, **stop** and fix `.gitignore` before proceeding — the file is not ignored and would leak if committed.

### 5. Optional: adjust risk profile

`publish/risk.profile.json` controls hard limits. Defaults are conservative. Common changes on a new machine:

- `LiveTradingEnabled: true` — required to send real (non-simulated) orders. Off by default.
- `AccountWhitelist: ["YOUR_ACCOUNT_ID"]` — only accounts in this list can receive live orders. Empty list means all allowed.
- `AllowShortSelling: true` — enables sell-to-open (short positions). Off by default.
- `AllowExtendedHours: true` — enables pre/post-market orders. Already on in the committed version.
- `MaxSharesPerOrder`, `MaxNotionalPerOrder`, `MaxDailyLoss` — self-explanatory.

Only change these if the user explicitly asks. **Never enable live trading without confirmation.**

### 6. First run

```powershell
Start-Process C:\temp\dom\publish\FastDOM.exe
```

Expected behavior:
- Window opens showing the DOM ladder, hot buttons, and top bar
- **Mode dropdown** in the top-left defaults to `SIM` (safe)
- Account ComboBox border is gray (disconnected)
- Orders button reads "Orders" on dark gray (no open orders)

To connect a real broker:
1. Switch the **Mode dropdown** to `Schwab Live` or `Alpaca Paper`
2. First-time Schwab: browser opens for OAuth. Log in, allow access. If the callback URL is `https://127.0.0.1:8182`, Windows may prompt to allow the app through the firewall — **click Allow**.
3. Schwab only: you'll be prompted once (per user, per machine) for the App Secret. Paste it — it's stored encrypted via DPAPI in the current user's profile.
4. After connect, the account border turns green and account IDs populate the dropdown.

### 7. Verify it works

- Type a symbol (e.g. `SPY`) in the symbol box → Enter. DOM should populate with bid/ask levels.
- **Do not click any Buy/Sell button unless the user explicitly asked to place an order.** These are live-fire in Live modes.

---

## Troubleshooting

### App crashes immediately on launch

Check the Windows Event Log:

```powershell
Get-EventLog -LogName Application -Source ".NET Runtime","Application Error" -Newest 3 | Format-List TimeGenerated, Message
```

Common causes:

- **`DllNotFoundException` in `SetWindowLongPtrWndProc`** when launching from a headless PowerShell shell — harmless, WPF just needs a real interactive session. Double-click the exe or use `Start-Process` from an interactive PowerShell.
- **Missing DLL next to exe** — someone deleted files from `publish/`. Re-clone or copy the `publish/` folder from another machine.
- **148 MB `FastDOM.exe`** — that's the single-file bundle from an older machine. The current build is folder-mode (~151 KB exe). If you have the old 148 MB version, either delete `publish/` and re-clone, or rebuild (step 8 below).

### "Connection refused" or Schwab OAuth loop

The Schwab callback listener binds to `127.0.0.1:8182` by default. If another app on the machine already uses that port:

```powershell
Get-NetTCPConnection -LocalPort 8182 -ErrorAction SilentlyContinue
```

Kill the conflicting process, or change `callbackUrl` in `broker.schwab.json` **and** in the Schwab developer portal to match.

### Alpaca orders rejected with "buy_limit_price" in error

The rejection is wash-trade prevention — a pre-existing opposite-side order at the same price is blocking the new one. The friendly message now reads *"existing Buy Limit @ X blocks this submission — cancel the existing order first."* Open the Orders popup (top bar), cancel the conflicting order, then retry.

### Hot Button Editor's "Apply Template" wipes a multi-line script

Known behavior: clicking "Apply" on a template **replaces** the script rather than appending. If a user complains that Risk Buy no longer prompts for a stop price, the fix is to restore the source version:

```powershell
Copy-Item C:\temp\dom\FastDOM\config-examples\hotbuttons.json C:\temp\dom\publish\hotbuttons.json -Force
```

Then close and reopen the app so it re-reads the file.

---

## 8. Rebuilding from source (optional)

Only needed if you edited `FastDOM/*.cs` or `.xaml` files. Requires .NET 8 SDK.

```powershell
# Install .NET 8 SDK if not present
winget install Microsoft.DotNet.SDK.8

# Kill any running instance so the exe isn't locked
Get-Process | Where-Object {$_.ProcessName -like "*FastDOM*"} | Stop-Process -Force -ErrorAction SilentlyContinue

# Publish as folder-mode (NOT single-file — single-file exe exceeds GitHub's 100 MB limit)
dotnet publish C:\temp\dom\FastDOM\FastDOM.App\FastDOM.App.csproj `
    -c Release -r win-x64 --self-contained true `
    -o C:\temp\dom\publish
```

Do not add `-p:PublishSingleFile=true`. That flag produces a 148 MB exe which cannot be pushed to GitHub and has known extraction failures on some Windows installs.

---

## File locations reference (for the AI)

Config files the app reads from `publish/` at startup:

- `appsettings.json` — Mode, DefaultSymbol, DomVisibleLevels, ShareSizePresets, etc.
- `broker.schwab.json` — Schwab App Key, callback URL
- `alpaca.json` — Alpaca keys + isPaper
- `token.source.json` — Database connection for token fetch (optional)
- `risk.profile.json` — Risk limits and safety toggles
- `hotbuttons.json` — Hot button definitions and scripts
- `hotkeys.json` — Global keyboard shortcuts

The app resolves its config directory via `ConfigManager.ResolveConfigDir()`:
- If `appsettings.json` sits next to the exe (publish mode) → uses the exe directory
- Otherwise → falls back to `%AppData%\FastDOM\`

On a fresh publish-mode checkout, all configs live in `publish/` and edits made through the UI (Hot Button Editor, Hotkey Settings) are saved back there via `ConfigManager.SaveAll()` when the app closes.

## Security invariants

The AI must maintain these regardless of what the user asks:

- Never commit `publish/broker.schwab.json`, `publish/alpaca.json`, or `publish/token.source.json`
- Never hardcode API keys, secrets, or the Schwab App Key in source code
- Never enable `LiveTradingEnabled` without explicit user confirmation
- The Schwab App Secret must go through DPAPI — never write it to any JSON file
