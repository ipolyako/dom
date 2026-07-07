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
dotnet publish FastDOM.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Output: `FastDOM.App/bin/Release/net8.0-windows/win-x64/publish/FastDOM.exe`

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

## Release Checklist

- [ ] All tests pass: `dotnet test`
- [ ] No hardcoded secrets in any source file
- [ ] `liveTradingEnabled: false` in default risk profile
- [ ] Default mode is `Simulation` in appsettings.json
- [ ] README.md and docs are up to date
- [ ] Version number updated in csproj
