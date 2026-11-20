@echo off
setlocal
rem Builds MutagenMon in Release mode, then restarts the local copy in c:\tools\Mutagen-Mon.
rem Output: MutagenMon.App\bin\Release\net10.0-windows\
rem Only the files of the output folder and its Assets folder are copied: the config and mutagen
rem folders hold each developer's own settings and must not be overwritten.
rem No label / goto on purpose: git stores .bat files with LF endings (.gitattributes), and cmd.exe
rem can fail to find a label in a script with LF-only line endings.
cd /d "%~dp0"

set "MM_SRC=%~dp0MutagenMon.App\bin\Release\net10.0-windows"
set "MM_DST=c:\tools\Mutagen-Mon"

echo build in release mode
echo ---------------------
dotnet build MutagenMon.App\MutagenMon.App.csproj -c Release
if errorlevel 1 (
    echo.
    echo ERROR: BUILD FAILED
    pause
    exit /b 1
)

echo kill MutagenMon.exe
echo ---------------------
taskkill /IM MutagenMon.exe /F >nul 2>&1
timeout /t 3


echo copy to %MM_DST%
echo ---------------------
copy /Y "%MM_SRC%\*.*" "%MM_DST%\"
if errorlevel 1 (
    echo.
    echo ERROR: COPY OF THE FILES TO %MM_DST% FAILED. MutagenMon was stopped and not restarted.
    pause
    exit /b 1
)
xcopy "%MM_SRC%\Assets" "%MM_DST%\Assets" /E /I /Y /Q
if errorlevel 1 (
    echo.
    echo ERROR: COPY OF THE ASSETS TO %MM_DST%\Assets FAILED. MutagenMon was stopped and not restarted.
    pause
    exit /b 1
)

echo restart %MM_DST%\MutagenMon.exe
echo ---------------------
start "" "%MM_DST%\MutagenMon.exe"


echo.
echo Build succeeded: %~dp0MutagenMon.App\bin\Release\net10.0-windows\
pause
