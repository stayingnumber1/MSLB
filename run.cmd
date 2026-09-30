@echo off
cd /d "%~dp0"
set "DOTNET_ROOT=C:\Program Files\dotnet"
set "DOTNET_ROOT_X64=C:\Program Files\dotnet"
set "DOTNET_MULTILEVEL_LOOKUP=1"
set "APP_DIR=%~dp0artifacts\app-current"
set "APP=%APP_DIR%\MotorLoadBench.UI.exe"

echo Publishing current Motor Load Bench sources...
"C:\Program Files\dotnet\dotnet.exe" publish "%~dp0src\MotorLoadBench.UI\MotorLoadBench.UI.csproj" -c Release --no-self-contained -o "%APP_DIR%" --nologo -m:1
if errorlevel 1 (
  echo Build failed. Close any running MotorLoadBench.UI instance and retry.
  pause
  exit /b 2
)

if /I "%~1"=="scan" (
  start "" "%APP%" --ethercat-connect
) else (
  start "" "%APP%"
)
exit /b 0


