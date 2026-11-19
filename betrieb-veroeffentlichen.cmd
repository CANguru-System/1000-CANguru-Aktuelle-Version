@echo off
rem Startet betrieb-veroeffentlichen.ps1 ohne Aenderung der PowerShell-Ausfuehrungsrichtlinie.
rem Beispiel: betrieb-veroeffentlichen.cmd -Hochladen
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0betrieb-veroeffentlichen.ps1" %*
set FEHLER=%ERRORLEVEL%
if "%~1"=="" pause
exit /b %FEHLER%
