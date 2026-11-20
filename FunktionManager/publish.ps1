<#
.SYNOPSIS
    Baut den FunktionManager nach .\published\ - Gegenstueck zu publish.sh (Aufruf und Ablauf gleich).

.DESCRIPTION
    Erstellt den FunktionManager als eine Datei mit .NET und Avalonia (laeuft ohne installiertes .NET):

        published\windows-x64\PDF-Knead-FunktionManager.exe
        published\linux-x64\PDF-Knead-FunktionManager

        published\ ist von git ausgenommen.

    Aufruf: .\publish.ps1 [windows|linux|beides]   (ohne Argument: beides)

    Benoetigt: .NET SDK 10.
#>
$ErrorActionPreference = 'Stop'
$Ziel = if ($args.Count -gt 0) { [string]$args[0] } else { 'beides' }
if ($Ziel -notin @('windows', 'linux', 'beides')) {
    Write-Host "Unbekanntes Ziel: $Ziel (windows, linux oder beides)" -ForegroundColor Red
    exit 2
}

$Ordner = $PSScriptRoot
$Projekt = Join-Path $Ordner 'FunktionManager.csproj'
$Faelle = @()
if ($Ziel -in @('windows', 'beides')) { $Faelle += , @('win-x64', 'windows-x64', 'PDF-Knead-FunktionManager.exe') }
if ($Ziel -in @('linux', 'beides')) { $Faelle += , @('linux-x64', 'linux-x64', 'PDF-Knead-FunktionManager') }

foreach ($Fall in $Faelle) {
    $Rid, $Name, $Datei = $Fall
    $Aus = Join-Path $Ordner "published\$Name"
    # a running FunktionManager holds its file: say so instead of a long build error
    $Laeuft = Get-Process -Name 'PDF-Knead-FunktionManager' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $Aus $Datei) }
    if ($Laeuft) {
        Write-Host "Der FunktionManager in $Aus laeuft noch - bitte zuerst schliessen, dann neu bauen." -ForegroundColor Yellow
        exit 1
    }
    Write-Host "Baue $Name ..." -ForegroundColor Cyan
    # nur das Programm ersetzen; was daneben liegt, bleibt
    & dotnet publish $Projekt -c Release -r $Rid -o $Aus --nologo -v quiet
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Bau fuer $Name fehlgeschlagen." -ForegroundColor Red
        exit $LASTEXITCODE
    }
    $Programm = Join-Path $Aus $Datei
    if (-not (Test-Path $Programm)) {
        Write-Host "Programm fehlt nach dem Bau: $Programm" -ForegroundColor Red
        exit 1
    }
    $Groesse = [math]::Round((Get-Item $Programm).Length / 1MB, 1)
    Write-Host "  fertig: $Programm ($Groesse MB)" -ForegroundColor Green
}
