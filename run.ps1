param([switch]$EtherCatScan)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_ROOT = 'C:\Program Files\dotnet'
$env:DOTNET_ROOT_X64 = 'C:\Program Files\dotnet'
$env:DOTNET_MULTILEVEL_LOOKUP = '1'
$dotnet = Join-Path $PSScriptRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = 'dotnet' }
$appDir = Join-Path $PSScriptRoot 'artifacts/app-current'
$app = Join-Path $appDir 'MotorLoadBench.UI.exe'
& $dotnet publish src/MotorLoadBench.UI/MotorLoadBench.UI.csproj -c Release --no-self-contained -o $appDir --nologo -m:1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$arguments = if ($EtherCatScan) { @('--ethercat-connect') } else { @() }
Start-Process -FilePath $app -ArgumentList $arguments -WorkingDirectory $PSScriptRoot


