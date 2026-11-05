@echo off
rem Startet alles-bauen.ps1 ohne Aenderung der PowerShell-Ausfuehrungsrichtlinie.
rem Parameter werden durchgereicht, z. B.: alles-bauen.cmd -Nur Decoder
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0alles-bauen.ps1" %*
set FEHLER=%ERRORLEVEL%
rem Ohne Parameter (z. B. Doppelklick) das Fenster offen lassen, damit das Ergebnis lesbar bleibt
if "%~1"=="" pause
exit /b %FEHLER%
