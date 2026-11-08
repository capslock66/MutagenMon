@echo off

cd /d "%~dp0"

echo build in release mode
echo ---------------------
dotnet build MutagenMon.App\MutagenMon.App.csproj -c Release
if errorlevel 1 (
    echo.
    echo BUILD FAILED
    pause
    exit /b 1
)


echo Copy to prod-build
echo ---------------------
copy c:\sources\mutagenMon\MutagenMon.App\bin\Release\net10.0-windows\*.* \\prod-build\DATA\install\Mutagen-mon


echo kill MutagenMon.exe
echo ---------------------
taskkill /IM MutagenMon.exe /F >nul 2>&1
timeout /t 3


echo copy to c:\tools\Mutagen-Mon
echo ---------------------
copy c:\sources\mutagenMon\MutagenMon.App\bin\Release\net10.0-windows\*.* c:\tools\Mutagen-Mon\


echo restart c:\tools\Mutagen-Mon\MutagenMon.exe
echo ---------------------
start "" "c:\tools\Mutagen-Mon\MutagenMon.exe"


echo press any to quit
pause