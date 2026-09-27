@echo off
cd /d "%~dp0"
set "DOTNET_ROOT=C:\Program Files\dotnet"
set "DOTNET_ROOT_X64=C:\Program Files\dotnet"
set "DOTNET_MULTILEVEL_LOOKUP=1"
set "APP=%~dp0artifacts\app-v145\MotorLoadBench.UI.exe"

if not exist "%APP%" (
  echo Building Motor Load Bench...
  "C:\Program Files\dotnet\dotnet.exe" publish "%~dp0src\MotorLoadBench.UI\MotorLoadBench.UI.csproj" -c Release --no-self-contained -o "%~dp0artifacts\app-v145" --nologo -m:1
  if errorlevel 1 (
    echo Build failed.
    pause
    exit /b 2
  )
)

if /I "%~1"=="scan" (
  start "" "%APP%" --ethercat-connect
) else (
  start "" "%APP%"
)
exit /b 0

