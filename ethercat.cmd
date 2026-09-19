@echo off
cd /d "%~dp0"
if not exist ".tools\ethercat-python\Scripts\python.exe" (
  echo Missing EtherCAT Python environment. See docs\ETHERCAT_DIRECT.md.
  exit /b 2
)
if "%~1"=="" (
  ".tools\ethercat-python\Scripts\python.exe" tools\ethercat_probe.py adapters --output artifacts\ethercat\adapters.json
) else (
  ".tools\ethercat-python\Scripts\python.exe" tools\ethercat_probe.py %*
)
exit /b %errorlevel%
