param(
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'
$workspace = $PSScriptRoot
$project = Join-Path $workspace 'FastDOM\FastDOM.App\FastDOM.App.csproj'
$stage = Join-Path $workspace '.publish_build'
$destination = Join-Path $workspace 'publish'
$exe = Join-Path $destination 'FastDOM.exe'

if (Test-Path -LiteralPath $stage) {
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    if (-not $resolvedStage.StartsWith($workspace + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Temporary publish path escaped workspace: $resolvedStage"
    }
    Remove-Item -LiteralPath $resolvedStage -Recurse -Force
}

dotnet publish $project -c Release -r win-x64 --self-contained true -o $stage

foreach ($required in @('FastDOM.exe', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $stage $required))) {
        throw "Self-contained publish output is missing $required"
    }
}

Get-CimInstance Win32_Process |
    Where-Object { $_.Name -eq 'FastDOM.exe' -and $_.ExecutablePath -eq $exe } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }

$exitDeadline = [DateTime]::UtcNow.AddSeconds(5)
do {
    $remaining = Get-CimInstance Win32_Process |
        Where-Object { $_.Name -eq 'FastDOM.exe' -and $_.ExecutablePath -eq $exe }
    if (-not $remaining) { break }
    Start-Sleep -Milliseconds 100
} while ([DateTime]::UtcNow -lt $exitDeadline)

if ($remaining) {
    throw "FastDOM did not fully exit before publishing to $destination"
}

New-Item -ItemType Directory -Path $destination -Force | Out-Null
$preserve = @(
    'appsettings.json', 'broker.schwab.json', 'alpaca.json', 'risk.profile.json',
    'hotkeys.json', 'hotbuttons.json', 'token.source.json', 'workspace.layout.json'
)

Get-ChildItem -LiteralPath $stage -File -Recurse |
    Where-Object { $_.Name -notin $preserve } |
    ForEach-Object {
        $relative = $_.FullName.Substring($stage.Length).TrimStart('\')
        $target = Join-Path $destination $relative
        $targetDirectory = Split-Path -Parent $target
        New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $target -Force
    }

Remove-Item -LiteralPath $stage -Recurse -Force

if (-not $NoStart) {
    Start-Process -FilePath $exe -WorkingDirectory $destination
}

Write-Host "Published self-contained FastDOM to $destination"
