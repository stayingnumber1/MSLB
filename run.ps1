param([switch]$EtherCatScan)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_ROOT = 'C:\Program Files\dotnet'
$env:DOTNET_ROOT_X64 = 'C:\Program Files\dotnet'
$env:DOTNET_MULTILEVEL_LOOKUP = '1'
$app = Join-Path $PSScriptRoot 'src/MotorLoadBench.UI/bin/Release/net8.0-windows/MotorLoadBench.UI.exe'
if (Test-Path -LiteralPath $app) {
    $arguments = if ($EtherCatScan) { @('--ethercat-connect') } else { @() }
    Start-Process -FilePath $app -ArgumentList $arguments -WorkingDirectory $PSScriptRoot
    exit
}
$dotnet = Join-Path $PSScriptRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = 'dotnet' }
if ($EtherCatScan) {
    & $dotnet run --project src/MotorLoadBench.UI -c Release -- --ethercat-connect
} else {
    & $dotnet run --project src/MotorLoadBench.UI -c Release
}
