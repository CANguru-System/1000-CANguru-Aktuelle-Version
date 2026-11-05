@echo off
rem Startet flashen.ps1 ohne Aenderung der PowerShell-Ausfuehrungsrichtlinie.
rem Beispiel: flashen.cmd switch -Loeschen -Monitor
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0flashen.ps1" %*
exit /b %ERRORLEVEL%
