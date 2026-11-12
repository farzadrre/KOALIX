@echo off
chcp 65001 >nul
title KOALIX Build
cd /d "%~dp0"

echo ==================================================
echo    KOALIX v0.4.46  --  BUILD
echo ==================================================
echo.

echo [1/4] Closing KOALIX if it is running...
taskkill /f /im KOALIX.exe >nul 2>&1

echo [2/4] Cleaning old build files...
if exist "src\Koalix.App\bin"   rmdir /s /q "src\Koalix.App\bin"
if exist "src\Koalix.App\obj"   rmdir /s /q "src\Koalix.App\obj"
if exist "output"               rmdir /s /q "output"

echo [3/4] Building single-file KOALIX.exe ...
echo        (first build takes a few minutes - please wait)
echo.
dotnet publish src\Koalix.App\Koalix.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o output
if errorlevel 1 goto :retry

:success
echo.
echo [4/4] DONE!
echo.
echo        KOALIX.exe is here:  output\KOALIX.exe
echo        (opening the folder for you now)
start "" explorer "%~dp0output"
echo.
echo        This window closes in 10 seconds...
timeout /t 10 >nul
exit /b 0

:retry
echo.
echo   Single-file packaging failed (usually antivirus).
echo   Retrying as a normal build - this always works...
echo.
dotnet publish src\Koalix.App\Koalix.App.csproj -c Release -r win-x64 --self-contained true -o output
if errorlevel 1 goto :fail
goto :success

:fail
echo.
echo ==================================================
echo    BUILD FAILED
echo    Take a screenshot of the red lines above.
echo    If it mentions antivirus/defender, run this
echo    once in PowerShell (as Admin) and try again:
echo.
echo    Add-MpPreference -ExclusionPath "%~dp0"
echo ==================================================
pause
exit /b 1
