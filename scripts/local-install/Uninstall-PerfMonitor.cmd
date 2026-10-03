@echo off
setlocal
cd /d "%TEMP%"
(
    "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0Uninstall-PerfMonitor.ps1"
    exit /b
)
