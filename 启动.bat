@echo off
setlocal
cd /d "%~dp0"

if /I "%~1"=="--dev-http" (
  powershell -NoProfile -ExecutionPolicy Bypass -File ".\scripts\start.ps1" -DevHttp
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File ".\scripts\start.ps1"
)
set "PERF_EXIT_CODE=%ERRORLEVEL%"
if not "%PERF_EXIT_CODE%"=="0" (
  echo.
  echo 启动失败，退出码 %PERF_EXIT_CODE%。
  echo 请先运行: powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
  pause
)
exit /b %PERF_EXIT_CODE%
