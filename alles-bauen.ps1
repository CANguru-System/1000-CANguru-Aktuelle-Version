<#
.SYNOPSIS
    Builds all CANguru components: the four decoder variants, the bridge and the GUI.

.DESCRIPTION
    Uses the PlatformIO installation of the VS Code extension and the .NET SDK.
    Before building, the generated protocol constants from 0300-Constants and the
    protocol library from 0400-Protocol are copied into all projects (run
    constants_manager.py first after changes). The protocol tests run on the PC
    first (C# always, C++ when Visual Studio is installed).
    The OTA WLAN password comes from 0300-Constants\ota_wlan.local.txt (not in git; created
    with a random password on the first run).
    The finished firmware files are copied to _build\ with clear names
    (e.g. decoder-switch.bin, bridge.bin) so they can be used for OTA directly.
    In addition, a complete image per target (e.g. decoder-switch-komplett.bin) with
    bootloader, partition table and OTA reset is written for flashing over USB at 0x0.

.PARAMETER Nur
    Optional: build only one part - Decoder, Bridge or GUI - or run only the protocol Test.

.EXAMPLE
    .\alles-bauen.ps1
    .\alles-bauen.ps1 -Nur Decoder
    .\alles-bauen.ps1 -Nur Test
#>
param(
    [ValidateSet('Decoder', 'Bridge', 'GUI', 'Test')]
    [string]$Nur
)

$ErrorActionPreference = 'Stop'
$wurzel = $PSScriptRoot
$ausgabe = Join-Path $wurzel '_build'
$python = Join-Path $env:USERPROFILE '.platformio\penv\Scripts\python.exe'

$decoderUmgebungen = @('switch', 'signal', 'ledsignal', 'power', 'feedback')
$ergebnisse = [System.Collections.Generic.List[object]]::new()

function Pruefe-Werkzeuge {
    if ($Nur -in @($null, '', 'Decoder', 'Bridge') -and -not (Test-Path $python)) {
        throw "PlatformIO nicht gefunden ($python). Bitte VS Code mit der PlatformIO-Erweiterung einmal starten."
    }
    if ($Nur -in @($null, '', 'GUI', 'Test') -and -not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw '.NET SDK (dotnet) nicht gefunden.'
    }
}

# Chip of each PlatformIO environment (for esptool merge_bin)
$chipJeUmgebung = @{ switch = 'esp32c3'; signal = 'esp32c3'; power = 'esp32c3'; ledsignal = 'esp32'; feedback = 'esp32'; 'esp32-evb' = 'esp32' }

# Writes _build\<zielname>-komplett.bin: bootloader, partition table, boot_app0 and
# application merged into one image that is flashed at address 0x0. It works on an
# erased chip and resets the OTA selection (otadata), so the new firmware really starts
# even if the board last ran from the second app slot after an OTA update.
# The offsets come from PlatformIO itself (project metadata).
function Erzeuge-Komplettimage([string]$projekt, [string]$umgebung, [string]$zielname) {
    Push-Location (Join-Path $wurzel $projekt)
    try {
        $json = & $python -m platformio project metadata -e $umgebung --json-output 2>$null
        $meta = ($json | ConvertFrom-Json).$umgebung
    }
    finally {
        Pop-Location
    }
    if (-not $meta) {
        Write-Host "Komplettimage ${zielname}: PlatformIO-Metadaten nicht lesbar." -ForegroundColor Red
        return $false
    }
    $teile = @()
    foreach ($img in $meta.extra.flash_images) {
        $teile += @($img.offset, $img.path)
    }
    $teile += @($meta.extra.application_offset, (Join-Path $wurzel "$projekt\.pio\build\$umgebung\firmware.bin"))

    $esptool = Join-Path $env:USERPROFILE '.platformio\packages\tool-esptoolpy\esptool.py'
    $ziel = Join-Path $ausgabe "$zielname-komplett.bin"
    & $python $esptool --chip $chipJeUmgebung[$umgebung] merge_bin -o $ziel @teile | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Komplettimage $zielname konnte nicht erzeugt werden." -ForegroundColor Red
        return $false
    }
    Write-Host "Komplettimage: $ziel"
    return $true
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
        $ok = Erzeuge-Komplettimage $projekt $umgebung $zielname
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

# Copies the generated protocol constants (0300-Constants) and the protocol library
# (0400-Protocol) into all projects, so the copies can never drift from the source.
function Verteile-Konstanten {
    $kopien = @(
        @{ Von = '0300-Constants\generated_constants\protocol_constants_core.h'; Nach = '1000-CANguru-Decoder\src\decoder\core\generated_constants\protocol_constants_core.h' },
        @{ Von = '0300-Constants\generated_constants\protocol_constants_core.h'; Nach = '2000-CANguru-Bridge\include\generated_constants\protocol_constants_core.h' },
        @{ Von = '0300-Constants\ProtocolConstants.cs';                          Nach = '3000-CANguru-GUI\Models\ProtocolConstants.cs' },
        @{ Von = '0400-Protocol\cpp\canguru_protocol.h';                         Nach = '1000-CANguru-Decoder\src\decoder\core\generated_constants\canguru_protocol.h' },
        @{ Von = '0400-Protocol\cpp\canguru_protocol.h';                         Nach = '2000-CANguru-Bridge\include\generated_constants\canguru_protocol.h' },
        @{ Von = '0400-Protocol\cs\CanguruProtocol.cs';                          Nach = '3000-CANguru-GUI\Protocol\CanguruProtocol.cs' }
    )
    foreach ($k in $kopien) {
        Copy-Item (Join-Path $wurzel $k.Von) (Join-Path $wurzel $k.Nach) -Force
    }
    Write-Host 'Protokollkonstanten und -bibliothek in alle Projekte kopiert.'
}

# OTA WLAN of the bridge (WPA2). The password must not be in the public repository, so it
# lives in 0300-Constants\ota_wlan.local.txt (ignored by git). On the first run that file is
# created with a random password. From it, ota_wlan.h is written into the decoder and the
# bridge (also ignored by git). Bridge and decoders must be built with the same file.
function Erzeuge-OtaWlan {
    $datei = Join-Path $wurzel '0300-Constants\ota_wlan.local.txt'
    if (-not (Test-Path $datei)) {
        $zeichen = [char[]]'abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789'
        # cryptographic random bytes; values above the largest multiple of the alphabet size
        # are skipped, so every character is equally likely (works in Windows PowerShell 5.1)
        $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
        $grenze = 256 - (256 % $zeichen.Length)
        $passwort = ''
        $byte = New-Object byte[] 1
        while ($passwort.Length -lt 16) {
            $rng.GetBytes($byte)
            if ($byte[0] -lt $grenze) { $passwort += $zeichen[$byte[0] % $zeichen.Length] }
        }
        $rng.Dispose()
        @(
            '# OTA WLAN of the CANguru bridge (WPA2). Local only - never commit this file.',
            '# After a change: rebuild, update ALL decoders by OTA first, then flash the bridge.',
            'SSID=BridgeOTA',
            "PASSWORD=$passwort"
        ) | Set-Content -Encoding utf8 $datei
        Write-Host "OTA-WLAN: neues Passwort erzeugt in $datei" -ForegroundColor Yellow
    }
    $werte = @{}
    foreach ($zeile in Get-Content $datei) {
        if ($zeile -match '^\s*(SSID|PASSWORD)\s*=\s*(.*?)\s*$') { $werte[$Matches[1]] = $Matches[2] }
    }
    $ssid = $werte['SSID']; $pass = $werte['PASSWORD']
    if (-not $ssid -or $ssid.Length -gt 32) { throw "OTA-WLAN: SSID in $datei fehlt oder ist laenger als 32 Zeichen." }
    if (-not $pass -or $pass.Length -lt 8 -or $pass.Length -gt 63) { throw "OTA-WLAN: Passwort in $datei muss 8 bis 63 Zeichen haben (WPA2)." }
    if (($ssid + $pass) -match '["\\]') { throw "OTA-WLAN: SSID und Passwort duerfen keine Anfuehrungszeichen oder Backslashes enthalten." }
    $header = @(
        '#pragma once',
        '// Generated by alles-bauen.ps1 from 0300-Constants/ota_wlan.local.txt - DO NOT COMMIT',
        "#define CG2_OTA_WLAN_SSID `"$ssid`"",
        "#define CG2_OTA_WLAN_PASS `"$pass`""
    )
    foreach ($ziel in @('1000-CANguru-Decoder\src\decoder\core\generated_constants\ota_wlan.h',
                        '2000-CANguru-Bridge\include\generated_constants\ota_wlan.h')) {
        $pfad = Join-Path $wurzel $ziel
        # only rewrite on a change, so PlatformIO does not rebuild everything each time
        $neu = ($header -join "`n") + "`n"
        if (-not (Test-Path $pfad) -or [IO.File]::ReadAllText($pfad) -ne $neu) {
            [IO.File]::WriteAllText($pfad, $neu, (New-Object Text.UTF8Encoding($false)))
        }
    }
    Write-Host "OTA-WLAN: $ssid (WPA2, Passwort aus ota_wlan.local.txt)"
}

# Runs the protocol tests on the PC: the same test vectors for C# and C++
function Teste-Protokoll {
    $vektoren = Join-Path $wurzel '0400-Protocol\test\testvektoren.txt'

    Write-Host "`n=== Protokolltest C# ===" -ForegroundColor Cyan
    $start = Get-Date
    $ErrorActionPreference = 'Continue'
    & dotnet run --project (Join-Path $wurzel '0400-Protocol\test\cs\ProtokollTest.csproj') -c Release -- $vektoren
    $ok = ($LASTEXITCODE -eq 0)
    $ergebnisse.Add([pscustomobject]@{
        Teil   = 'test-protokoll-cs'
        Status = if ($ok) { 'OK' } else { 'FEHLER' }
        Dauer  = '{0:mm\:ss}' -f ((Get-Date) - $start)
    })

    Write-Host "`n=== Protokolltest C++ ===" -ForegroundColor Cyan
    $start = Get-Date
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    $vsPfad = if (Test-Path $vswhere) { & $vswhere -latest -products * -property installationPath } else { $null }
    $vcvars = if ($vsPfad) { Join-Path $vsPfad 'VC\Auxiliary\Build\vcvars64.bat' } else { $null }
    if (-not $vcvars -or -not (Test-Path $vcvars)) {
        Write-Host 'Visual Studio (C++) nicht gefunden - C++-Test uebersprungen.' -ForegroundColor Yellow
        $ergebnisse.Add([pscustomobject]@{ Teil = 'test-protokoll-cpp'; Status = 'uebersprungen'; Dauer = '-' })
        return
    }
    $tmp = Join-Path $ausgabe 'test'
    New-Item -ItemType Directory -Force $tmp | Out-Null
    $exe = Join-Path $tmp 'test_protokoll.exe'
    $quelle = Join-Path $wurzel '0400-Protocol\test\cpp\test_protokoll.cpp'
    $inc1 = Join-Path $wurzel '0400-Protocol\cpp'
    $inc2 = Join-Path $wurzel '0300-Constants\generated_constants'
    cmd /c "call `"$vcvars`" >nul && cl /nologo /EHsc /std:c++17 /W4 /WX /I `"$inc1`" /I `"$inc2`" `"$quelle`" /Fe:`"$exe`" /Fo:`"$tmp\\`""
    $ok = ($LASTEXITCODE -eq 0)
    if ($ok) {
        & $exe $vektoren
        $ok = ($LASTEXITCODE -eq 0)
    }
    $ergebnisse.Add([pscustomobject]@{
        Teil   = 'test-protokoll-cpp'
        Status = if ($ok) { 'OK' } else { 'FEHLER' }
        Dauer  = '{0:mm\:ss}' -f ((Get-Date) - $start)
    })
}

Pruefe-Werkzeuge
Verteile-Konstanten
if ($Nur -in @($null, '', 'Decoder', 'Bridge')) { Erzeuge-OtaWlan }
New-Item -ItemType Directory -Force $ausgabe | Out-Null

if (-not $Nur -or $Nur -eq 'Test') {
    Teste-Protokoll
}

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

$fehler = @($ergebnisse | Where-Object Status -eq 'FEHLER').Count
if ($fehler -gt 0) {
    Write-Host "$fehler Teil(e) fehlgeschlagen." -ForegroundColor Red
    exit 1
}
Write-Host 'Alles erfolgreich gebaut.' -ForegroundColor Green
exit 0
