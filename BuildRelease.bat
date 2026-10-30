@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo .NET 8 SDK is not installed. The download page will open.
  echo Install it once, then run BuildRelease.bat again.
  start https://dotnet.microsoft.com/download/dotnet/8.0
  pause
  exit /b 1
)

echo [1/3] Building a self-contained single-file exe. Needs internet once.
if exist "%~dp0Release" rmdir /s /q "%~dp0Release"
dotnet publish VEXYON.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%~dp0Release"
if errorlevel 1 (
  echo Build failed. Copy the error text above and send it to Claude.
  pause
  exit /b 1
)

rem ---- Optional code signing: set SIGN_PFX (path to .pfx) and SIGN_PASS before running ----
set "SIGNING=0"
if defined SIGN_PFX (
  where signtool >nul 2>nul
  if errorlevel 1 (
    echo signtool.exe not found, install Windows SDK or run from a Developer Command Prompt. Skipping signing.
  ) else (
    set "SIGNING=1"
    echo Signing VEXYON.exe ...
    signtool sign /f "%SIGN_PFX%" /p "%SIGN_PASS%" /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 "%~dp0Release\VEXYON.exe"
    if errorlevel 1 (
      echo Signing failed. Check SIGN_PFX and SIGN_PASS.
      pause
      exit /b 1
    )
  )
) else (
  echo NOTE: SIGN_PFX is not set. The exe will be UNSIGNED and Windows SmartScreen will warn buyers.
)

set "ISCC="
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if exist "%LocalAppData%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LocalAppData%\Programs\Inno Setup 6\ISCC.exe"

if not defined ISCC (
  echo.
  echo Inno Setup 6 was not found. The download page will open.
  echo Install it, then run BuildRelease.bat again to create the installer.
  echo Meanwhile the portable exe is ready in the Release folder.
  start https://jrsoftware.org/isdl.php
  pause
  exit /b 0
)

echo [2/3] Creating the installer...
"%ISCC%" setup.iss
if errorlevel 1 (
  echo Installer creation failed. Copy the error text above and send it to Claude.
  pause
  exit /b 1
)

if "%SIGNING%"=="1" (
  echo Signing the installer ...
  signtool sign /f "%SIGN_PFX%" /p "%SIGN_PASS%" /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 "%~dp0Output\VEXYON-Setup.exe"
  if errorlevel 1 (
    echo Installer signing failed.
    pause
    exit /b 1
  )
)

rem ---- SHA-256 fingerprint to publish next to the download so buyers can verify the file ----
certutil -hashfile "%~dp0Output\VEXYON-Setup.exe" SHA256 > "%~dp0Output\VEXYON-Setup.sha256.txt"

echo.
echo Done. The installer is in the Output folder: VEXYON-Setup.exe
start "" "%~dp0Output"
pause
