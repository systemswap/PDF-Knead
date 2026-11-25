<#
.SYNOPSIS
    Baut PDF-Knead unter Windows nach .\published\ - Gegenstueck zu publish.sh (Vorbild; Aufruf und Ablauf gleich).

.DESCRIPTION
    Erstellt die fertigen Programme in published\:

        published\windows-x64\PDF-Knead.exe    Programm (AOT: daneben Skia, HarfBuzz, ANGLE als .dll)
        daneben jeweils der FunktionManager (PDF-Knead-FunktionManager[.exe]): die Funktionsdatei fuer Admins, eine Datei
        published\linux-x64\PDF-Knead          Linux-Programm als eine Datei (ohne AppImage - die baut nur publish.sh unter Linux)
        published\PDF-Knead-<Version>-windows-x64.zip (aeltere Pakete werden ersetzt)

    Neben jedes Programm kommen LICENSE, THIRD-PARTY-NOTICES.md und licenses\, unter Windows einrichten.ps1, unter Linux
    einrichten.sh / entfernen.sh samt Menueintrag.

    Paketform (Bauart):
      aot      Eine Datei, AOT-Binary (Standard): Maschinencode, Start ~0,3 s statt ~1 s. Geht nur fuer das eigene System und
               braucht die Visual Studio Build Tools mit "Desktopentwicklung mit C++". Die Linux-Fassung wird hier deshalb als
               .NET-Build gebaut (AOT fuer Linux: publish.sh).
      dotnet   .NET-Build: eine Datei mit .NET und ReadyToRun (Start ~1 s), ueberall baubar.
      einzeln  .NET-Build mit allen Bibliotheken lose im Ordner (Fehlersuche).

    Eigene Sprachdateien (languages\), das Log und selbst importierte Handschriften neben dem Programm bleiben bei jedem
    Bau stehen. Ein Linux-Bau laesst den Windows-Stand stehen und umgekehrt.

    Aufruf ohne Argumente fragt erst nach der Paketform, dann nach dem Ziel. Mit Argumenten ohne Rueckfrage, Reihenfolge
    egal; was fehlt, gilt als Standard (Paketform aot, Ziel beides):
      .\publish.ps1 [linux|windows|beides] [aot|dotnet|einzeln] [ohnetests]
      z. B. .\publish.ps1 windows   .\publish.ps1 windows einzeln   .\publish.ps1 beides dotnet

    ohnetests: die Testsuite vor dem Bau auslassen (etwa 8 Minuten weniger). Nur fuer einen Stand, dessen Suite gerade gruen
    gelaufen ist (vor jedem Commit laeuft sie ohnehin); ohne den Schalter wird wie bisher vorher getestet.

    Benoetigt: .NET SDK 10; fuer AOT die Visual Studio Build Tools.

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 beides aot
#>
# Bewusst ohne param-Block: so kommt auch die fruehere Form "-Target beides -Bauart aot" als Woerter in $args an
$Argumente = @($args | ForEach-Object { [string]$_ })

$ErrorActionPreference = 'Stop'

# dotnet mit Ausgabe in eine Logdatei; liefert den Rueckgabewert. Waehrenddessen eine Statuszeile mit der Laufzeit, bei Tests
# (-Tests) dazu die Zahl der fertigen, fehlgeschlagenen und uebersprungenen Tests - sonst saehe man minutenlang nichts.
function Invoke-Logged([string]$log, [string[]]$arguments, [switch]$Tests, [int]$Total = 0) {
    $line = ($arguments | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' '
    $errors = "$log.stderr"
    $process = Start-Process dotnet -ArgumentList $line -NoNewWindow -PassThru -RedirectStandardOutput $log -RedirectStandardError $errors
    $null = $process.Handle   # sonst liefert Windows PowerShell 5.1 danach keinen ExitCode
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    while (-not $process.WaitForExit(1000)) {
        $text = ''
        try {
            $stream = [System.IO.File]::Open($log, 'Open', 'Read', 'ReadWrite')
            try { $text = (New-Object System.IO.StreamReader($stream)).ReadToEnd() } finally { $stream.Dispose() }
        }
        catch { }
        # Bauen: der letzte Schritt aus dem Log (Projekt fertig, Maschinencode erzeugen ...), damit man sieht, dass es weitergeht
        $last = @($text -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -and $_ -notmatch '^(warning|Warnung)' })
        $step = if ($last.Count -gt 0) { $last[-1] -replace '^.*\\([^\\]+\.(dll|exe))$', '$1' } else { 'startet' }
        if ($step.Length -gt 52) { $step = $step.Substring(0, 49) + '...' }
        $status = '   {0:mm\:ss}  {1}' -f $watch.Elapsed, $step
        if ($Tests) {
            $passed = [regex]::Matches($text, '(?m)^\s+(Bestanden|Passed)\s').Count
            $failed = [regex]::Matches($text, '(?m)^\s+(Fehler|Failed)\s').Count
            $skipped = [regex]::Matches($text, '(?m)^\s+(.bersprungen|Skipped)\s').Count
            $done = $passed + $failed + $skipped
            # Mit bekannter Gesamtzahl: wie weit der Lauf ist (die Zahl stammt aus der Testliste; waechst der Lauf darueber, zaehlt er)
            $of = if ($Total -gt 0) { ' - {0} / {1}' -f $done, [Math]::Max($Total, $done) } else { '' }
            $status = if ($done -eq 0) { '   startet die Tests ... {0:mm\:ss}' -f $watch.Elapsed }
                      else { '   {0} bestanden, {1} fehlgeschlagen, {2} uebersprungen{3} ... {4:mm\:ss}' -f $passed, $failed, $skipped, $of, $watch.Elapsed }
        }
        Write-Host -NoNewline ("`r" + $status.PadRight(78))
    }
    $process.WaitForExit()
    Write-Host -NoNewline ("`r" + ''.PadRight(78) + "`r")
    if (Test-Path $errors) {
        Get-Content $errors | Add-Content $log
        Remove-Item $errors -Force
    }
    return $process.ExitCode
}

# Keine Nutzungsdaten an Microsoft (.NET) oder Avalonia senden.
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:DO_NOT_TRACK = '1'
$env:DOTNET_NOLOGO = '1'

$root       = $PSScriptRoot
$project    = Join-Path $root 'PdfKnead.Desktop\PdfKnead.Desktop.csproj'
$solution   = Join-Path $root 'PdfKnead.sln'
$outDir     = Join-Path $root 'published'
$linuxDir   = Join-Path $outDir 'linux-x64'
$windowsDir = Join-Path $outDir 'windows-x64'
$cacheDir   = Join-Path $env:LOCALAPPDATA 'pdf-knead-build'
$workDir    = Join-Path $cacheDir 'publish'
$artifacts  = Join-Path $cacheDir 'artifacts'
$tarExe     = Join-Path $env:SystemRoot 'System32\tar.exe'

$Target = ''
$Bauart = ''
$OhneTests = $false
foreach ($argument in @($Argumente | Where-Object { $_ })) {
    switch ($argument.ToLowerInvariant()) {
        { $_ -in 'linux', 'windows' } { $Target = $_; break }
        { $_ -in 'beides', 'all' } { $Target = 'all'; break }
        { $_ -in 'aot', 'eine-datei', 'datei' } { $Bauart = 'aot'; break }
        { $_ -in 'dotnet', 'net' } { $Bauart = 'dotnet'; break }
        { $_ -in 'einzeln', 'ordner' } { $Bauart = 'einzeln'; break }
        { $_ -in 'ohnetests', 'ohne-tests', 'notests' } { $OhneTests = $true; break }
        # Fruehere Form .\publish.ps1 -Target beides -Bauart aot: die Namen ueberspringen
        { $_ -in '-target', '-bauart' } { break }
        default { throw "Aufruf: .\publish.ps1 [linux|windows|beides] [aot|dotnet|einzeln] [ohnetests]" }
    }
}

if (@($Argumente | Where-Object { $_ -and $_ -notin 'ohnetests', 'ohne-tests', 'notests' }).Count -gt 0) {
    # Mit Argumenten nie nachfragen
    if (-not $Target) { $Target = 'all' }
    if (-not $Bauart) { $Bauart = 'aot' }
}

function Read-Choice([string]$question, [string[]]$options, [string[]]$values) {
    Write-Host ''
    Write-Host $question
    for ($i = 0; $i -lt $options.Count; $i++) { Write-Host ("  {0}) {1}" -f ($i + 1), $options[$i]) }
    Write-Host ''
    $answer = Read-Host 'Auswahl [1]'
    if (-not $answer) { $answer = '1' }
    $index = 0
    if (-not [int]::TryParse($answer, [ref]$index) -or $index -lt 1 -or $index -gt $values.Count) { throw 'Ungueltige Auswahl.' }
    return $values[$index - 1]
}

if (-not $Bauart) {
    $Bauart = Read-Choice 'Welche Paketform?' `
        @('Eine Datei, AOT-Binary (Standard) - Maschinencode, schneller Start (nur fuer Windows; Linux dann als .NET-Build)',
          'Eine Datei, .NET-Build            - mit .NET (ReadyToRun)',
          'Einzeln                           - .NET-Build, alle Bibliotheken lose im Ordner (Fehlersuche)') `
        @('aot', 'dotnet', 'einzeln')
}
if (-not $Target) {
    $Target = Read-Choice 'Wofuer soll gebaut werden?' @('beides (Standard)', 'Linux', 'Windows') @('all', 'linux', 'windows')
}

$single = if ($Bauart -eq 'einzeln') { 'false' } else { 'true' }
Write-Host "Ziel: $Target, Bauart: $Bauart"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Fehler: .NET SDK fehlt.' }
# Nicht, solange das Programm aus published\ laeuft.
function Assert-NotRunning {
    $running = Get-Process PDF-Knead, PDF-Knead-FunktionManager -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($outDir, [StringComparison]::OrdinalIgnoreCase) }
    if ($running) { throw "Fehler: PDF-Knead oder der FunktionManager laeuft gerade (aus published\) - bitte erst beenden, dann neu bauen." }
}
Assert-NotRunning

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = ([string]($props.Project.PropertyGroup.Version | Where-Object { $_ })).Trim()
Write-Host "PDF-Knead $version"

# Alte Zwischenstaende weg; was noch gesperrt ist (Build-Server von dotnet halten Logs eines frueheren Laufs offen), bleibt liegen -
# die Logs tragen deshalb einen Zeitstempel
if (Test-Path $workDir) { Get-ChildItem $workDir -Force | ForEach-Object { try { Remove-Item $_.FullName -Recurse -Force -ErrorAction Stop } catch { } } }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
New-Item -ItemType Directory -Force -Path $workDir, $outDir | Out-Null

# Ein kaputter Stand soll nicht in published\ landen.
if ($OhneTests) {
    Write-Host '== Tests ausgelassen (ohnetests) - nur fuer einen Stand, dessen Suite gerade gruen gelaufen ist'
}
else {
Write-Host '== Tests'
$testLog = Join-Path $workDir "test-$stamp.log"
# Erst bauen, dann die Tests zaehlen (die Liste kommt ohne neuen Bau in wenigen Sekunden), dann laufen lassen - so zeigt der
# Fortschritt, wie viele von wie vielen schon durch sind
$buildLog = Join-Path $workDir "test-build-$stamp.log"
$code = Invoke-Logged $buildLog @('build', $solution, '-c', 'Release', '-p:UseArtifactsOutput=true', "-p:ArtifactsPath=$artifacts")
if ($code -ne 0) {
    Get-Content $buildLog -Tail 30 | Write-Host
    throw 'Fehler: Bauen fuer die Tests fehlgeschlagen - nichts veroeffentlicht.'
}
$listLog = Join-Path $workDir "test-liste-$stamp.log"
$total = 0
# Tests mit fester Zeitgrenze laufen nicht beim Veroeffentlichen: ein langsamer oder ausgelasteter Rechner soll den Bau eines
# funktionierenden Programms nicht stoppen; sie laufen einzeln mit "dotnet test --filter Art=Zeit"
if ((Invoke-Logged $listLog @('test', $solution, '-c', 'Release', '--no-build', '-p:UseArtifactsOutput=true', "-p:ArtifactsPath=$artifacts",
        '--list-tests', '--filter', 'Art!=Zeit')) -eq 0) {
    $total = @(Get-Content $listLog | Where-Object { $_ -match '^    \S' }).Count
}
$timeLog = Join-Path $workDir "test-zeit-$stamp.log"
$timeTests = 0
if ((Invoke-Logged $timeLog @('test', $solution, '-c', 'Release', '--no-build', '-p:UseArtifactsOutput=true', "-p:ArtifactsPath=$artifacts",
        '--list-tests', '--filter', 'Art=Zeit')) -eq 0) {
    $timeTests = @(Get-Content $timeLog | Where-Object { $_ -match '^    \S' }).Count
}
$code = Invoke-Logged $testLog @('test', $solution, '-c', 'Release', '--no-build', '--filter', 'Art!=Zeit', '-p:UseArtifactsOutput=true', "-p:ArtifactsPath=$artifacts",
    '--logger', 'console;verbosity=normal') -Tests -Total $total
if ($code -ne 0) {
    Get-Content $testLog -Tail 30 | Write-Host
    throw 'Fehler: Tests fehlgeschlagen - nichts veroeffentlicht.'
}
Select-String -Path $testLog -Pattern 'Bestanden!|Passed!' | ForEach-Object { Write-Host "   $($_.Line.Trim())" }
Write-Host "   $timeTests Zeittests ausgelassen (einzeln: dotnet test --filter Art=Zeit)"
}

# Eigene Lizenz, Hinweise auf fremde Bestandteile und deren Lizenztexte neben das Programm.
function Copy-Notices([string]$dest) {
    Copy-Item (Join-Path $root 'LICENSE') $dest -Force
    Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $dest -Force
    $licenses = Join-Path $dest 'licenses'
    if (Test-Path $licenses) { Remove-Item $licenses -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $licenses | Out-Null
    Copy-Item (Join-Path $root 'packaging\licenses\*') $licenses -Force
    # Lizenzen der mitgelieferten Schriften
    Copy-Item (Join-Path $root 'Schriften\Standard\Lizenzen\*') $licenses -Force
    Copy-Item (Join-Path $root 'Schriften\Handschriften-frei\Lizenzen\*') $licenses -Force
}

# CJK-Ersatzschriften (Noto CJK, ~44 MB) neben das Programm, nicht hinein (Entscheidung vom 19.10.2026). Sie liegen im
# Repository unter Schriften\CJK (Wunsch vom 19.10.2026: kommen mit dem Klonen); sonst aus dem Cache von
# tools\schriften\cjk-holen.ps1. Fehlen sie, meldet das Programm es erst, wenn eine PDF sie braucht.
function Copy-CjkFonts([string]$dest) {
    $source = Join-Path $root 'Schriften\CJK'
    if (-not (Test-Path (Join-Path $source 'NotoSansCJK-Regular.ttc'))) { $source = Join-Path $cacheDir 'cjk\Schriften-CJK' }
    if (-not (Test-Path (Join-Path $source 'NotoSansCJK-Regular.ttc'))) {
        Write-Host '   Schriften-CJK fehlt (Schriften\CJK im Repository, sonst tools\schriften\cjk-holen.ps1) - ohne: kein CJK-Ersatz'
        return
    }
    $target = Join-Path $dest 'Schriften-CJK'
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    Copy-Item $source $target -Recurse -Force
}

# Schriften-Unterschrift neben dem Programm: dorthin importiert das Programm eigene Schriften (Datei -> Schrift importieren); sie bleiben bei
# jedem Bau stehen (Fehlergedaechtnis 96) und kommen nie ins Paket. Lizenzierte Handschriften liefert PDF-Knead nicht mehr mit (seit
# 25.11.2026): was ein frueherer Bau mitgeliefert hat (Schriften-Unterschrift\.mitgeliefert, eine Zeile je Datei), entfernt ein neuer Bau.
function Get-ShippedSignatureFonts([string]$target) {
    $manifest = Join-Path $target '.mitgeliefert'
    if (-not (Test-Path -LiteralPath $manifest)) { return @() }
    return @([System.IO.File]::ReadAllLines($manifest) | ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and -not $_.Contains('/') -and -not $_.Contains('\') })
}
function Copy-SignatureFonts([string]$dest) {
    $target = Join-Path $dest 'Schriften-Unterschrift'
    $manifest = Join-Path $target '.mitgeliefert'
    foreach ($name in (Get-ShippedSignatureFonts $target)) {
        $old = Join-Path $target $name
        if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Force }
    }
    if (Test-Path -LiteralPath $manifest) { Remove-Item -LiteralPath $manifest -Force }
    # Leer (nichts importiert): weg
    if ((Test-Path -LiteralPath $target) -and -not (Get-ChildItem -LiteralPath $target -Force)) { Remove-Item -LiteralPath $target -Force }
}

# Modelle der Texterkennung und des Feld-Erkenners (PP-OCRv6 small und medium, FFDetr; Festlegung 13.11.2026: im Paket,
# kein Download mehr) aus Modelle\ocr neben das Programm (Ordner ocr), jede Datei gegen Modelle\ocr\SHA256SUMS. Fehlt eine oder stimmt
# eine Pruefsumme nicht: Abbruch - ohne Modelle wird kein Paket gebaut (die Modelle aus einem Paket von PDF-Knead oder von den Quellen in THIRD-PARTY-NOTICES.md nach Modelle\ocr legen; Pruefsummen: tools/ci/modelle-ocr.sha256).
function Copy-Models([string]$dest) {
    $source = Join-Path $root 'Modelle\ocr'
    $sums = Join-Path $source 'SHA256SUMS'
    if (-not (Test-Path -LiteralPath $sums)) { throw 'Fehler: Modelle\ocr\SHA256SUMS fehlt (die Modelle aus einem Paket von PDF-Knead oder von den Quellen in THIRD-PARTY-NOTICES.md nach Modelle\ocr legen; Pruefsummen: tools/ci/modelle-ocr.sha256).' }
    $target = Join-Path $dest 'ocr'
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    foreach ($line in Get-Content -LiteralPath $sums) {
        if ($line -notmatch '^([0-9a-f]{64})\s+\*?([^*].*)$') { continue }
        $hash = $Matches[1]
        $rel = $Matches[2].Trim()
        if ($rel.StartsWith('/') -or $rel.Contains('..')) { continue }
        $file = Join-Path $source $rel
        if (-not (Test-Path -LiteralPath $file)) { throw "Fehler: Modell fehlt: Modelle\ocr\$rel" }
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) { throw "Fehler: Pruefsumme stimmt nicht: Modelle\ocr\$rel" }
        $out = Join-Path $target $rel
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $out) | Out-Null
        Copy-Item -LiteralPath $file -Destination $out -Force
    }
}

# Was das Programm neben sich schreibt; bleibt bei jedem Bau stehen (Schriften-Unterschrift: siehe Copy-SignatureFonts). Der Ordner ocr
# gehoert seit 13.11.2026 zum Paket (Copy-Models) und wird bei jedem Bau neu geschrieben. Dazu die Dateien der IT neben dem Programm:
# die Funktionsdatei (berechtigungen.json) und updates-aus - sie bleiben stehen und kommen nie ins Paket.
$userFiles = @('languages', 'PDF-Knead.log', 'PDF-Knead.log.alt', 'Schriften-Unterschrift', 'berechtigungen.json', 'updates-aus')

function Clear-ProgramFolder([string]$dest) {
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Get-ChildItem $dest -Force | Where-Object { $_.Name -notin $userFiles } | Remove-Item -Recurse -Force
}

function Invoke-Publish([string]$rid, [string]$dest, [string[]]$properties) {
    $log = Join-Path $workDir "publish-$rid-$stamp.log"
    $code = Invoke-Logged $log (@('publish', $project, '-c', 'Release', '-r', $rid, '--self-contained', 'true') + $properties +
        @('-p:DebugType=None', '-p:DebugSymbols=false', '-p:UseArtifactsOutput=true', "-p:ArtifactsPath=$artifacts", '-o', $dest))
    if ($code -ne 0) {
        Get-Content $log -Tail 20 | Write-Host
        throw "Fehler: dotnet publish fuer $rid fehlgeschlagen."
    }
    # Compiler-Warnungen aus dem Protokoll zeigen, je Stelle einmal - ein erfolgreicher Bau zeigte sonst keine (Fehlergedaechtnis 641).
    # Projekte, die nicht neu uebersetzt werden, warnen nicht erneut: vollstaendig sieht man sie mit --no-incremental.
    $warnings = @(Get-Content $log | Where-Object { $_ -match ': (warning|Warnung) [A-Z]+\d+' } |
        ForEach-Object { $_.Trim() -replace ' \[[^\]]*\]$', '' } | Sort-Object -Unique)
    if ($warnings.Count -gt 0) {
        Write-Host ('Warnungen beim Bau ({0}):' -f $warnings.Count) -ForegroundColor Yellow
        $warnings | ForEach-Object { Write-Host "   $_" }
    }
    Get-ChildItem $dest -Recurse -Include *.pdb, *.dbg | Remove-Item -Force
}

function Publish-Runtime([string]$rid, [string]$dest) {
    Invoke-Publish $rid $dest @("-p:PublishSingleFile=$single", "-p:IncludeNativeLibrariesForSelfExtract=$single", "-p:EnableCompressionInSingleFile=$single")
}

function Publish-Aot([string]$rid, [string]$dest) { Invoke-Publish $rid $dest @('-p:PublishAot=true') }

# Der FunktionManager (Funktionsdatei fuer Admins, KONZEPT 5.27) liegt in jedem Paket neben dem Programm: eine Datei mit .NET und Avalonia.
function Publish-FunktionManager([string]$rid, [string]$dest) {
    $out = Join-Path $workDir "funktionmanager-$rid"
    $log = Join-Path $workDir "funktionmanager-$rid-$stamp.log"
    $code = Invoke-Logged $log @('publish', (Join-Path $root 'FunktionManager\FunktionManager.csproj'), '-c', 'Release', '-r', $rid, '-o', $out)
    if ($code -ne 0) {
        Get-Content $log -Tail 20 | Write-Host
        throw "Fehler: FunktionManager fuer $rid fehlgeschlagen."
    }
    $name = if ($rid -like 'win-*') { 'PDF-Knead-FunktionManager.exe' } else { 'PDF-Knead-FunktionManager' }
    Copy-Item (Join-Path $out $name) $dest -Force
    Write-Host "   FunktionManager: $name"
}

# Native AOT nur fuer das eigene System; braucht den C++-Linker der Visual Studio Build Tools (sucht vswhere.exe im Pfad).
function Test-AotPossible {
    $installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
    $vswhere = Join-Path $installer 'vswhere.exe'
    if (-not (Test-Path $vswhere)) { return $false }
    $vc = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vc) { return $false }
    if (($env:PATH -split ';') -notcontains $installer) { $env:PATH += ";$installer" }
    return $true
}

# Paket nur mit dem Mitgelieferten: ohne Log, eigene Sprachdateien und selbst importierte Schriften (wie publish.sh: der Ordner
# windows-x64\ steht im Zip). tar.exe von Windows schreibt Zip mit -a.
function New-Package([string]$folder, [string]$archive) {
    $skip = @()
    foreach ($name in $userFiles) { if ($name -ne 'Schriften-Unterschrift') { $skip += "$folder/$name" } }
    $fonts = Join-Path $outDir "$folder\Schriften-Unterschrift"
    if (Test-Path -LiteralPath $fonts) {
        $skip += "$folder/Schriften-Unterschrift/.mitgeliefert"
        $shipped = Get-ShippedSignatureFonts $fonts
        foreach ($file in Get-ChildItem -LiteralPath $fonts -File -Force) {
            if ($file.Name -ne '.mitgeliefert' -and $shipped -notcontains $file.Name) { $skip += "$folder/Schriften-Unterschrift/$($file.Name)" }
        }
    }
    $path = Join-Path $outDir $archive
    if (Test-Path $path) { Remove-Item $path -Force }
    # die Pakete tragen die Version im Namen: die aelteren desselben Ziels gehen (auch die ohne Version)
    Get-ChildItem -LiteralPath $outDir -File | Where-Object { $_.Name -like "PDF-Knead-*$folder.zip" } | Remove-Item -Force
    $excludes = @($skip | ForEach-Object { "--exclude=$_" })
    # Die frisch kopierte EXE haelt kurz ein anderer Prozess fest (vermutlich der Virenscanner)
    for ($try = 1; ; $try++) {
        & $tarExe -a -c -f $path @excludes -C $outDir $folder
        if ($LASTEXITCODE -eq 0) { break }
        if ($try -ge 6) { throw "Fehler: Paket $archive fehlgeschlagen." }
        Write-Host "   Paket: Datei noch gesperrt, neuer Versuch in 5 s ($try/5) ..."
        Start-Sleep -Seconds 5
    }
}

# ---------------------------------------------------------------- Linux
if ($Target -in @('all', 'linux')) {
    Write-Host '== Linux'
    if ($Bauart -eq 'aot') {
        # Native AOT uebersetzt nicht ueber Betriebssysteme hinweg
        Write-Host '   AOT fuer Linux geht nur unter Linux (publish.sh) - hier .NET-Build'
    }
    else { Write-Host '   .NET-Build' }
    $build = Join-Path $workDir 'linux'
    Publish-Runtime 'linux-x64' $build
    Assert-NotRunning
    Clear-ProgramFolder $linuxDir
    # Keine AppImage und kein tar.gz: appimagetool laeuft nur unter Linux, und Windows kann das Ausfuehrrecht nicht setzen
    Copy-Item (Join-Path $build '*') $linuxDir -Recurse -Force

    Copy-Notices $linuxDir
    Copy-CjkFonts $linuxDir
    Copy-SignatureFonts $linuxDir
    Copy-Models $linuxDir
    Publish-FunktionManager 'linux-x64' $linuxDir

    $share = Join-Path $linuxDir 'share'
    New-Item -ItemType Directory -Force -Path "$share\icons\hicolor\256x256\apps", "$share\applications" | Out-Null
    Copy-Item (Join-Path $root 'PdfKnead.Ui\Assets\Icon.png') "$share\icons\hicolor\256x256\apps\pdf-knead.png" -Force
    # LF-Zeilenenden erzwingen, sonst scheitern Skript und Menueintrag unter Linux an \r
    foreach ($name in @('einrichten.sh', 'entfernen.sh', 'PDF-Knead.desktop')) {
        $text = [System.IO.File]::ReadAllText((Join-Path $root "packaging\linux\$name")) -replace "`r`n", "`n"
        $dest = if ($name -like '*.desktop') { "$share\applications\$name" } else { Join-Path $linuxDir $name }
        [System.IO.File]::WriteAllText($dest, $text, (New-Object System.Text.UTF8Encoding $false))
    }
    Write-Host '   Unter Linux: chmod +x PDF-Knead einrichten.sh entfernen.sh; ./einrichten.sh'
}

# ---------------------------------------------------------------- Windows
if ($Target -in @('all', 'windows')) {
    Write-Host '== Windows'
    $build = Join-Path $workDir 'windows'
    if ($Bauart -eq 'aot' -and (Test-AotPossible)) {
        Write-Host '   AOT-Binary'
        Publish-Aot 'win-x64' $build
    }
    else {
        if ($Bauart -eq 'aot') { Write-Host '   Kein AOT moeglich (Visual Studio Build Tools mit "Desktopentwicklung mit C++" fehlen) - .NET-Build' }
        else { Write-Host '   .NET-Build' }
        Publish-Runtime 'win-x64' $build
    }
    Assert-NotRunning
    Clear-ProgramFolder $windowsDir
    # AOT legt die nativen Bibliotheken neben die exe: alles uebernehmen
    Copy-Item (Join-Path $build '*') $windowsDir -Recurse -Force
    Copy-Notices $windowsDir
    Copy-CjkFonts $windowsDir
    Copy-SignatureFonts $windowsDir
    Copy-Models $windowsDir
    Copy-Item (Join-Path $root 'packaging\windows\einrichten.ps1') $windowsDir -Force
    Publish-FunktionManager 'win-x64' $windowsDir
    New-Package 'windows-x64' "PDF-Knead-$version-windows-x64.zip"
}

Write-Host ''
Write-Host "Fertig (PDF-Knead $version):"
Get-ChildItem $outDir -Recurse -File -Depth 2 |
    Where-Object { $_.Extension -in @('.exe', '.zip', '.AppImage', '.gz') -or $_.Name -eq 'PDF-Knead' } |
    Sort-Object FullName |
    ForEach-Object { Write-Host ("  {0}  ({1:N0} Bytes)" -f $_.FullName.Substring($outDir.Length + 1).Replace('\', '/'), $_.Length) }
