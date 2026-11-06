<#
.SYNOPSIS
    Flashes one decoder or the bridge over USB with PlatformIO.

.DESCRIPTION
    Builds the firmware of the selected type and writes it over USB, including
    bootloader and partition table (PlatformIO does this itself). With -Loeschen
    the whole flash is erased first: firmware, stored settings, ID, bridge MAC
    and OTA flags. Afterwards the serial monitor can be opened with -Monitor.

    The board must be connected over USB. Without -Port, PlatformIO picks the port
    itself; with several boards connected, give the port (e.g. COM5).

.PARAMETER Typ
    switch, signal, ledsignal, power, feedback (decoders) or bridge.

.PARAMETER Port
    Optional: serial port, e.g. COM5.

.PARAMETER Loeschen
    Erase the whole flash before writing (decoder settings are lost).

.PARAMETER Monitor
    Open the serial monitor (115200 baud) after flashing. End it with Ctrl+C.

.EXAMPLE
    .\flashen.ps1 switch -Loeschen -Monitor
    .\flashen.ps1 feedback -Port COM7
    .\flashen.ps1 bridge
#>
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('switch', 'signal', 'ledsignal', 'power', 'feedback', 'bridge')]
    [string]$Typ,
    [string]$Port,
    [switch]$Loeschen,
    [switch]$Monitor
)

$wurzel = $PSScriptRoot
$python = Join-Path $env:USERPROFILE '.platformio\penv\Scripts\python.exe'
if (-not (Test-Path $python)) {
    Write-Host "PlatformIO nicht gefunden ($python). Bitte VS Code mit der PlatformIO-Erweiterung einmal starten." -ForegroundColor Red
    exit 1
}

if ($Typ -eq 'bridge') {
    $projekt = '2000-CANguru-Bridge'
    $umgebung = 'esp32-evb'
}
else {
    $projekt = '1000-CANguru-Decoder'
    $umgebung = $Typ
}

# Same constants and protocol library as alles-bauen.ps1 uses
$kopien = @(
    @{ Von = '0300-Constants\generated_constants\protocol_constants_core.h'; Nach = '1000-CANguru-Decoder\src\decoder\core\generated_constants\protocol_constants_core.h' },
    @{ Von = '0300-Constants\generated_constants\protocol_constants_core.h'; Nach = '2000-CANguru-Bridge\include\generated_constants\protocol_constants_core.h' },
    @{ Von = '0400-Protocol\cpp\canguru_protocol.h';                         Nach = '1000-CANguru-Decoder\src\decoder\core\generated_constants\canguru_protocol.h' },
    @{ Von = '0400-Protocol\cpp\canguru_protocol.h';                         Nach = '2000-CANguru-Bridge\include\generated_constants\canguru_protocol.h' }
)
foreach ($k in $kopien) {
    Copy-Item (Join-Path $wurzel $k.Von) (Join-Path $wurzel $k.Nach) -Force
}

$portArgs = @()
if ($Port) { $portArgs = @('--upload-port', $Port) }

Push-Location (Join-Path $wurzel $projekt)
try {
    if ($Loeschen) {
        Write-Host "`n=== Flash vollständig löschen ($Typ) ===" -ForegroundColor Cyan
        & $python -m platformio run -e $umgebung -t erase @portArgs
        if ($LASTEXITCODE -ne 0) {
            Write-Host 'Löschen fehlgeschlagen. Ist das Board per USB verbunden (ggf. -Port angeben)?' -ForegroundColor Red
            exit 1
        }
    }

    Write-Host "`n=== Bauen und flashen ($Typ, Umgebung $umgebung) ===" -ForegroundColor Cyan
    & $python -m platformio run -e $umgebung -t upload @portArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'Flashen fehlgeschlagen. Ist das Board per USB verbunden (ggf. -Port angeben)?' -ForegroundColor Red
        exit 1
    }
    Write-Host "`n$Typ erfolgreich geflasht." -ForegroundColor Green

    if ($Monitor) {
        Write-Host 'Serieller Monitor (Ende mit Strg+C) ...' -ForegroundColor Cyan
        $monArgs = @('-b', '115200')
        if ($Port) { $monArgs += @('-p', $Port) }
        & $python -m platformio device monitor @monArgs
    }
}
finally {
    Pop-Location
}
exit 0
