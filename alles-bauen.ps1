<#
.SYNOPSIS
    Builds all CANguru components: the four decoder variants, the bridge and the GUI.

.DESCRIPTION
    Uses the PlatformIO installation of the VS Code extension and the .NET SDK.
    The finished firmware files are copied to _build\ with clear names
    (e.g. decoder-switch.bin, bridge.bin) so they can be used for OTA directly.

.PARAMETER Nur
    Optional: build only one part - Decoder, Bridge or GUI.

.EXAMPLE
    .\alles-bauen.ps1
    .\alles-bauen.ps1 -Nur Decoder
#>
param(
    [ValidateSet('Decoder', 'Bridge', 'GUI')]
    [string]$Nur
)

$ErrorActionPreference = 'Stop'
$wurzel = $PSScriptRoot
$ausgabe = Join-Path $wurzel '_build'
$python = Join-Path $env:USERPROFILE '.platformio\penv\Scripts\python.exe'

$decoderUmgebungen = @('switch', 'signal', 'power', 'feedback')
$ergebnisse = [System.Collections.Generic.List[object]]::new()

function Pruefe-Werkzeuge {
    if ($Nur -ne 'GUI' -and -not (Test-Path $python)) {
        throw "PlatformIO nicht gefunden ($python). Bitte VS Code mit der PlatformIO-Erweiterung einmal starten."
    }
    if ((-not $Nur -or $Nur -eq 'GUI') -and -not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw '.NET SDK (dotnet) nicht gefunden.'
    }
}

# Runs a PlatformIO environment and copies firmware.bin to _build\<zielname>.bin
function Baue-Firmware([string]$projekt, [string]$umgebung, [string]$zielname) {
    Write-Host "`n=== $zielname ($projekt, Umgebung $umgebung) ===" -ForegroundColor Cyan
    $start = Get-Date
    Push-Location (Join-Path $wurzel $projekt)
    # Compiler output on stderr must not abort the script; success is judged by the exit code
    $ErrorActionPreference = 'Continue'
    try {
        & $python -m platformio run -e $umgebung
        $ok = ($LASTEXITCODE -eq 0)
    }
    finally {
        Pop-Location
    }
    if ($ok) {
        $bin = Join-Path $wurzel "$projekt\.pio\build\$umgebung\firmware.bin"
        Copy-Item $bin (Join-Path $ausgabe "$zielname.bin") -Force
    }
    $ergebnisse.Add([pscustomobject]@{
        Teil   = $zielname
        Status = if ($ok) { 'OK' } else { 'FEHLER' }
        Dauer  = '{0:mm\:ss}' -f ((Get-Date) - $start)
    })
}

function Baue-GUI {
    Write-Host "`n=== GUI (3000-CANguru-GUI, Release) ===" -ForegroundColor Cyan
    $start = Get-Date
    $ErrorActionPreference = 'Continue'
    & dotnet build (Join-Path $wurzel '3000-CANguru-GUI\CANguru-Server.csproj') -c Release -nologo -v minimal
    $ok = ($LASTEXITCODE -eq 0)
    $ergebnisse.Add([pscustomobject]@{
        Teil   = 'gui'
        Status = if ($ok) { 'OK' } else { 'FEHLER' }
        Dauer  = '{0:mm\:ss}' -f ((Get-Date) - $start)
    })
}

Pruefe-Werkzeuge
New-Item -ItemType Directory -Force $ausgabe | Out-Null

if (-not $Nur -or $Nur -eq 'Decoder') {
    foreach ($umgebung in $decoderUmgebungen) {
        Baue-Firmware '1000-CANguru-Decoder' $umgebung "decoder-$umgebung"
    }
}
if (-not $Nur -or $Nur -eq 'Bridge') {
    Baue-Firmware '2000-CANguru-Bridge' 'esp32-evb' 'bridge'
}
if (-not $Nur -or $Nur -eq 'GUI') {
    Baue-GUI
}

Write-Host "`n=== Zusammenfassung ===" -ForegroundColor Cyan
$ergebnisse | Format-Table -AutoSize | Out-String | Write-Host
Write-Host "Firmware-Dateien: $ausgabe"

$fehler = @($ergebnisse | Where-Object Status -ne 'OK').Count
if ($fehler -gt 0) {
    Write-Host "$fehler Teil(e) fehlgeschlagen." -ForegroundColor Red
    exit 1
}
Write-Host 'Alles erfolgreich gebaut.' -ForegroundColor Green
exit 0
