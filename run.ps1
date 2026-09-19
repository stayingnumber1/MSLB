$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$app = Join-Path $PSScriptRoot 'artifacts/app-ethercat/MotorLoadBench.UI.exe'
if (Test-Path -LiteralPath $app) { & $app; exit }
$dotnet = Join-Path $PSScriptRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = 'dotnet' }
& $dotnet run --project src/MotorLoadBench.UI -c Release
