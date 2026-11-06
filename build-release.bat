@echo off
rem Builds MutagenMon in Release mode.
rem Output: MutagenMon.App\bin\Release\net10.0-windows\
cd /d "%~dp0"

dotnet build MutagenMon.App\MutagenMon.App.csproj -c Release
if errorlevel 1 (
    echo.
    echo BUILD FAILED
    pause
    exit /b 1
)

echo.
echo Build succeeded: %~dp0MutagenMon.App\bin\Release\net10.0-windows\
timeout /t 3
