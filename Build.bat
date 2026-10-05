@echo off
setlocal
cd /d "%~dp0"
title Building Glasspane

rem ---- Check for the .NET 8 (or newer) SDK
where dotnet >nul 2>nul
if errorlevel 1 goto nosdk
dotnet --list-sdks | findstr /b /l "8. 9. 10. 11." >nul
if errorlevel 1 goto nosdk

rem ---- Stop a running copy so its files can be replaced
taskkill /im Glasspane.exe /f >nul 2>nul

echo Building Glasspane...
dotnet publish Glasspane.csproj -c Release -r win-x64 --self-contained false -o "%~dp0App"
if errorlevel 1 (
    echo.
    echo Build failed - the error messages above say why.
    pause
    exit /b 1
)

echo.
echo Done. Glasspane is in the "App" folder. Starting it now...
start "" "%~dp0App\Glasspane.exe"
exit /b 0

:nosdk
echo The free .NET 8 SDK from Microsoft is needed to build Glasspane.
echo Installing it with winget (Windows may ask for permission)...
echo.
winget install --id Microsoft.DotNet.SDK.8 -e --accept-source-agreements --accept-package-agreements
echo.
echo When the install finishes, close this window and run Build.bat again.
pause
exit /b 1
