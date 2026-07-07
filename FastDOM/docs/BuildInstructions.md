# Build Instructions

## Prerequisites

- .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
- Windows 10/11 or Windows Server 2019+ (WPF requires Windows)

## Development Build

```
cd c:\temp\dom\FastDOM
dotnet restore
dotnet build FastDOM.sln -c Debug
```

## Run Tests

```
dotnet test FastDOM.Tests/FastDOM.Tests.csproj -v normal
```

## Release Build

```
dotnet build FastDOM.sln -c Release
dotnet run --project FastDOM.App
```

## Self-Contained Portable Executable

```
dotnet publish FastDOM.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Output: `publish\FastDOM.exe` (and supporting config/data files in the same folder)

`IncludeNativeLibrariesForSelfExtract=true` is required. Without it, WPF's native components
(`PresentationNative_cor3.dll`) are not bundled inside the exe and the app crashes on startup
with a `DllNotFoundException` during window initialization.

The `clidriver\` folder (IBM DB2 CLI driver, needed for Schwab thinkorswim Derby token access)
is automatically copied to the output directory by the build. It must remain next to
`FastDOM.exe` — do not move or delete it even when not using Schwab mode.

This single EXE contains the .NET runtime. Can run on machines without .NET installed.

## Framework-Dependent Build (smaller, requires .NET 8 Runtime installed)

```
dotnet publish FastDOM.App -c Release -r win-x64 --self-contained false
```

## HTTPS Certificate for OAuth Callback

Schwab requires `https://127.0.0.1:{port}` for the OAuth callback. For the local HTTPS listener to work, a certificate must be bound to the port:

```powershell
# Generate self-signed cert (PowerShell, run as admin)
$cert = New-SelfSignedCertificate -DnsName "127.0.0.1" -CertStoreLocation "Cert:\LocalMachine\My"
$thumbprint = $cert.Thumbprint

# Bind to port 8182
netsh http add sslcert ipport=127.0.0.1:8182 certhash=$thumbprint appid="{12345678-1234-1234-1234-123456789012}"
```

Or set the callback URL to `https://127.0.0.1:8182` in `broker.schwab.json` and perform the same binding.

## IBM DB2 CLI Driver (clidriver)

The Schwab integration reads OAuth tokens from an Apache Derby database created by thinkorswim.
It does this via `IBM.Data.Db2`, which requires IBM's native CLI driver (`db2app64.dll` and
related files) bundled in the `clidriver\` folder.

**Where it comes from:** The `Net.IBM.Data.Db2` NuGet package (referenced by
`FastDOM.Broker.Schwab`) places the `clidriver\` folder in the project output directory
automatically during build/publish. No separate IBM install is required.

**Why it must be present even in Alpaca/SIM mode:** `IBM.Data.Db2` loads its static
initializer when any code in `FastDOM.Broker.Schwab` is JIT-compiled, regardless of whether
Schwab mode is selected. FastDOM sets `IBM_DB_HOME` and adds `clidriver\bin` to `PATH` at
startup so the native DLL is findable when the runtime's GC eventually runs the IBM finalizer.
If `clidriver\` is missing, the app will crash on exit (or earlier) with:
```
DllNotFoundException: Dll was not found.
   at IBM.Data.Db2.ConnSettingsFromXmlConfig.Finalize()
```

**Do not** deploy `FastDOM.exe` alone — always include the full publish output folder.

## Release Checklist

- [ ] All tests pass: `dotnet test`
- [ ] No hardcoded secrets in any source file
- [ ] `liveTradingEnabled: false` in default risk profile
- [ ] Default mode is `Simulation` in appsettings.json
- [ ] README.md and docs are up to date
- [ ] Version number updated in csproj
