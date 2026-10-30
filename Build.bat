@echo off
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
  echo .NET 8 SDK is not installed. The download page will open.
  echo Install it once, then run Build.bat again.
  start https://dotnet.microsoft.com/download/dotnet/8.0
  pause
  exit /b 1
)
echo Building VEXYON (needs internet once to restore packages)...
dotnet publish VEXYON.csproj -c Release -r win-x64 --self-contained false -o "%~dp0App"
if errorlevel 1 (
  echo Build failed. Copy the error text above and send it to Claude.
  pause
  exit /b 1
)
start "" "%~dp0App\VEXYON.exe"
