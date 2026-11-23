<#
.SYNOPSIS
    Copies everything needed to run the layout into the private repository
    8000-CANguru-Betrieb and, with -Hochladen, pushes it to GitHub.

.DESCRIPTION
    The computer at the layout ("CANguru-Bahn") does not need the sources, only the
    finished files: firmware for the flasher and for OTA updates, the CANguru-GUI, the
    flasher and the sniffer. This script collects them in a local clone of the private
    repository (outside OneDrive, default C:\Users\<name>\CANguru-Betrieb):

      firmware\            decoder-*.bin, bridge.bin (OTA) and *-komplett.bin (flasher)
      programme\CANguru-GUI, programme\Flasher (with esptool.exe), programme\Sniffer
      VERSIONEN.txt        firmware versions and the source commit

    Run alles-bauen.ps1 first; the firmware files are taken from _build. The PC programs
    are published here (framework-dependent, they need the .NET 10 Desktop Runtime).
    Data files of the GUI (decoders.json, mfx_loks.json, lokomotive.cs2) are never copied,
    so an update at the layout keeps them.

    The binaries contain the OTA WLAN password - the repository must stay private.

.PARAMETER Ziel
    Local clone of 8000-CANguru-Betrieb.

.PARAMETER Hochladen
    Commit and push the result.

.EXAMPLE
    .\betrieb-veroeffentlichen.ps1 -Hochladen
#>
param(
    [string]$Ziel = (Join-Path $env:USERPROFILE 'CANguru-Betrieb'),
    [switch]$Hochladen
)

$ErrorActionPreference = 'Stop'
$wurzel = $PSScriptRoot
$build = Join-Path $wurzel '_build'

if (-not (Test-Path (Join-Path $Ziel '.git'))) {
    throw "Kein Klon von 8000-CANguru-Betrieb in $Ziel. Einmal klonen: git clone https://github.com/CANguru-System/8000-CANguru-Betrieb.git `"$Ziel`""
}
if ($Ziel -like '*OneDrive*') {
    throw 'Der Klon darf nicht in einem OneDrive-Ordner liegen (Synchronisationskonflikte).'
}

# ---- firmware ----
$fw = Join-Path $Ziel 'firmware'
New-Item -ItemType Directory -Force $fw | Out-Null
Get-ChildItem $fw -Filter '*.bin' | Remove-Item
$dateien = Get-ChildItem $build -Filter '*.bin' | Where-Object { $_.Name -like 'decoder-*' -or $_.Name -like 'bridge*' }
if ($dateien.Count -eq 0) { throw "Keine Firmware in $build - erst alles-bauen.ps1 ausführen." }
$dateien | Copy-Item -Destination $fw
Write-Host "Firmware: $($dateien.Count) Dateien"

# ---- PC programs (dotnet publish into a clean folder, data files of the GUI stay) ----
function Veroeffentliche([string]$projekt, [string]$name) {
    $ausgabe = Join-Path $Ziel "programme\$name"
    $tmp = Join-Path $env:TEMP "canguru-betrieb-$name"
    $tmpBuild = "$tmp-build"
    foreach ($o in $tmp, $tmpBuild) { if (Test-Path $o) { Remove-Item $o -Recurse -Force } }
    # own build folder (OutDir): works even while the program runs from its usual bin folder
    & dotnet publish (Join-Path $wurzel $projekt) -c Release -o $tmp "-p:OutDir=$tmpBuild\" -nologo -v quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "$name konnte nicht gebaut werden." }
    Remove-Item $tmpBuild -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $tmp '*.pdb') -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $ausgabe | Out-Null
    # remove old program files, but never the data files the GUI writes at the layout
    $daten = @('decoders.json', 'mfx_loks.json', 'lokomotive.cs2', 'mfx-Konfiguration', 'esptool.exe')
    Get-ChildItem $ausgabe | Where-Object { $daten -notcontains $_.Name } | Remove-Item -Recurse -Force
    Copy-Item (Join-Path $tmp '*') $ausgabe -Recurse -Force
    Remove-Item $tmp -Recurse -Force
    Write-Host "$name veröffentlicht"
}
Veroeffentliche '3000-CANguru-GUI\CANguru-Server.csproj' 'CANguru-GUI'
Veroeffentliche '4000-Flasher\Flasher\Flasher.csproj' 'Flasher'
Veroeffentliche '3100-CANguru-Sniffer\CANguru-Sniffer.csproj' 'Sniffer'

# esptool.exe belongs next to the flasher (it is not part of the sources)
$esptool = Join-Path $Ziel 'programme\Flasher\esptool.exe'
if (-not (Test-Path $esptool)) {
    $quelle = Join-Path $wurzel '4000-Flasher\Flasher\bin\Release\net10.0-windows\esptool.exe'
    if (Test-Path $quelle) { Copy-Item $quelle $esptool }
    else { Write-Host 'Achtung: esptool.exe fehlt neben dem Flasher.' -ForegroundColor Yellow }
}

# ---- versions ----
$c = Get-Content (Join-Path $wurzel '0300-Constants\constants_manager.py') -Encoding UTF8
function Version([string]$n) {
    $major = ($c | Select-String "name=`"FW_${n}_MAJOR`",\s*value=`"(\d+)`"").Matches[0].Groups[1].Value
    $minor = ($c | Select-String "name=`"FW_${n}_MINOR`",\s*value=`"(\d+)`"").Matches[0].Groups[1].Value
    return "$major.$minor"
}
Push-Location $wurzel
$commit = (& git log -1 --format='%h %cs %s') -join ''
Pop-Location
$stand = @(
    "CANguru - Stand der Dateien in diesem Repository",
    "",
    "Zusammengestellt: $(Get-Date -Format 'dd.MM.yyyy HH:mm')",
    "Quelltext:        $commit",
    "",
    "Firmware",
    "  Weiche und Formsignal   $(Version 'STEPPER')   decoder-switch, decoder-signal",
    "  LED-Signaldecoder       $(Version 'LEDSIGNAL')   decoder-ledsignal",
    "  Stromdecoder            $(Version 'POWER')   decoder-power",
    "  Rückmelder              $(Version 'FEEDBACK')   decoder-feedback",
    "  Gebäudebeleuchtung      $(Version 'LIGHT')   decoder-light",
    "  Bridge                  $(Version 'BRIDGE')   bridge"
)
Set-Content (Join-Path $Ziel 'VERSIONEN.txt') $stand -Encoding UTF8
Write-Host "Versionen: $(Join-Path $Ziel 'VERSIONEN.txt')"

if ($Hochladen) {
    Push-Location $Ziel
    # git writes harmless warnings (line endings) to stderr; judge by exit codes
    $ErrorActionPreference = 'Continue'
    try {
        & git add -A 2>$null
        & git diff --cached --quiet
        if ($LASTEXITCODE -eq 0) { Write-Host 'Keine Änderungen.'; return }
        & git commit -q -m "Stand $(Get-Date -Format 'yyyy-MM-dd HH:mm') ($commit)"
        & git push -q 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Hochladen fehlgeschlagen.' }
        Write-Host 'Hochgeladen.' -ForegroundColor Green
    }
    finally { Pop-Location }
}
