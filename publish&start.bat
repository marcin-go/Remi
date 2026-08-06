@echo off
setlocal
cd /d "%~dp0"

set "PROJECT=%~dp0src\Remi.Web\Remi.Web.csproj"
set "OUTPUT=%~dp0publish\Remi"
set "BUILD_ARTIFACTS=%TEMP%\Remi-publish-artifacts"
set "BUILD_OUTPUT=%BUILD_ARTIFACTS%\bin\Remi.Web\release_win-x64"
set "STATIC_ASSETS=%~dp0src\Remi.Web\wwwroot"

netstat -ano | findstr /C:":5243" | findstr /I /C:"LISTENING" >nul
if not errorlevel 1 (
    echo Remi appears to be running on http://127.0.0.1:5243.
    echo Close that Remi window, then run this script again.
    exit /b 1
)

echo Building self-contained Remi for Windows x64...
dotnet build "%PROJECT%" --configuration Release --runtime win-x64 --self-contained true --artifacts-path "%BUILD_ARTIFACTS%" --ignore-failed-sources --disable-build-servers -m:1 -p:NuGetAudit=false -p:DebugType=None
if errorlevel 1 (
    echo.
    echo Build failed. Remi was not started.
    exit /b 1
)

echo Updating the portable application files...
robocopy "%BUILD_OUTPUT%" "%OUTPUT%" /E /XD data /COPY:DAT /R:1 /W:1 /NFL /NDL /NJH /NJS
if errorlevel 8 (
    echo.
    echo Remi could not be updated. Remi was not started.
    exit /b 1
)

robocopy "%STATIC_ASSETS%" "%OUTPUT%\wwwroot" /E /COPY:DAT /R:1 /W:1 /NFL /NDL /NJH /NJS
if errorlevel 8 (
    echo.
    echo Remi's static assets could not be updated. Remi was not started.
    exit /b 1
)

echo.
echo Publish complete: %OUTPUT%
echo Starting Remi...

if not exist "%OUTPUT%\Start Remi.cmd" (
    echo Start script was not included in the published output.
    exit /b 1
)

start "Remi" /D "%OUTPUT%" "%OUTPUT%\Start Remi.cmd"
