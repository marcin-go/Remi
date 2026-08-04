@echo off
setlocal
cd /d "%~dp0"

set "PROJECT=%~dp0src\Remi.Web\Remi.Web.csproj"
set "OUTPUT=%~dp0publish\Remi"

tasklist /FI "IMAGENAME eq Remi.exe" /NH 2>nul | findstr /I /C:"Remi.exe" >nul
if not errorlevel 1 (
    echo Close the running Remi application before publishing a replacement.
    exit /b 1
)

echo Publishing self-contained Remi for Windows x64...
dotnet publish "%PROJECT%" --configuration Release --runtime win-x64 --self-contained true --output "%OUTPUT%" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None
if errorlevel 1 (
    echo.
    echo Publish failed. The existing publish folder has not been removed.
    exit /b 1
)

echo.
echo Publish complete: %OUTPUT%
echo Run "Start Remi.cmd" from that folder.
