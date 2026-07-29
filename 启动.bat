@echo off
setlocal
cd /d "%~dp0"

set "PERF_PYTHON=%CD%\.venv\Scripts\python.exe"
if not exist "%PERF_PYTHON%" set "PERF_PYTHON=python"

"%PERF_PYTHON%" app.py %*
set "PERF_EXIT_CODE=%ERRORLEVEL%"
if not "%PERF_EXIT_CODE%"=="0" (
  echo.
  echo 启动失败，退出码 %PERF_EXIT_CODE%。
  echo 请先运行: powershell -ExecutionPolicy Bypass -File .\scripts\setup.ps1
  pause
)
exit /b %PERF_EXIT_CODE%

