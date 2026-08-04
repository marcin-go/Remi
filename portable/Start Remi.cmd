@echo off
setlocal
cd /d "%~dp0"

echo Starting Remi...
echo Close this window when you have finished using Remi.
Remi.exe --urls http://127.0.0.1:5243 --open-browser true
