@echo off
setlocal
rem Builds MutagenMon in Release mode, copies it to the prod-build share, then restarts the local copy
rem in c:\tools\Mutagen-Mon.
rem Only the files of the output folder and its Assets folder are copied: the config and mutagen
rem folders hold each developer's own settings and must not be overwritten.
rem No label / goto on purpose: git stores .bat files with LF endings (.gitattributes), and cmd.exe
rem can fail to find a label in a script with LF-only line endings.

cd /d "%~dp0"

set "MM_SRC=%~dp0MutagenMon.App\bin\Release\net10.0-windows"
set "MM_PROD=\\prod-build\DATA\install\Mutagen-mon"
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


echo Copy to prod-build
echo ---------------------
copy /Y "%MM_SRC%\*.*" "%MM_PROD%\"
if errorlevel 1 (
    echo.
    echo ERROR: COPY OF THE FILES TO %MM_PROD% FAILED
    pause
    exit /b 1
)
xcopy "%MM_SRC%\Assets" "%MM_PROD%\Assets" /E /I /Y /Q
if errorlevel 1 (
    echo.
    echo ERROR: COPY OF THE ASSETS TO %MM_PROD%\Assets FAILED
    pause
    exit /b 1
)


rem echo kill MutagenMon.exe
rem echo ---------------------
rem taskkill /IM MutagenMon.exe /F >nul 2>&1
rem timeout /t 3


rem echo copy to %MM_DST%
rem echo ---------------------
rem copy /Y "%MM_SRC%\*.*" "%MM_DST%\"
rem if errorlevel 1 (
rem     echo.
rem     echo ERROR: COPY OF THE FILES TO %MM_DST% FAILED. MutagenMon was stopped and not restarted.
rem     pause
rem     exit /b 1
rem )
rem xcopy "%MM_SRC%\Assets" "%MM_DST%\Assets" /E /I /Y /Q
rem if errorlevel 1 (
rem     echo.
rem     echo ERROR: COPY OF THE ASSETS TO %MM_DST%\Assets FAILED. MutagenMon was stopped and not restarted.
rem     pause
rem     exit /b 1
rem )


rem echo restart %MM_DST%\MutagenMon.exe
rem echo ---------------------
rem start "" "%MM_DST%\MutagenMon.exe"


echo press any to quit
pause
