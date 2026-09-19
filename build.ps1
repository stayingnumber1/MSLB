param([switch]$Publish)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$dotnet = Join-Path $PSScriptRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = 'dotnet' }
& $dotnet build MotorLoadBench.sln -c Release --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet run --project tests/MotorLoadBench.Tests -c Release --no-build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($Publish) {
 & $dotnet publish src/MotorLoadBench.UI -c Release -r win-x64 --self-contained true -o artifacts/app-ethercat --nologo
 if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
