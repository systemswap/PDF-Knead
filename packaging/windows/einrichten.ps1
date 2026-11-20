<#
.SYNOPSIS
    Registriert PDF-Knead im Windows-Rechtsklick-Menue, unter "Oeffnen mit" und in den
    Standard-Apps fuer PDF-Dateien.

.DESCRIPTION
    Schreibt nur nach HKCU (aktueller Benutzer) - keine Adminrechte noetig. Macht PDF-Knead
    nicht zum Standardprogramm; das legt man in Windows selbst fest (Oeffnen mit ->
    Andere App auswaehlen -> Immer, oder Einstellungen -> Apps -> Standard-Apps).

    Eintraege:
    - Rechtsklick auf .pdf: "Mit PDF-Knead oeffnen" (SystemFileAssociations)
    - "Oeffnen mit": eigener Dateityp PDFKnead.Document, eingetragen unter .pdf\OpenWithProgids
      (der Eintrag unter Applications allein zeigt Windows 11 dort nicht an)
    - Standard-Apps: Capabilities + RegisteredApplications

.PARAMETER Uninstall
    Entfernt alle Eintraege wieder.

.EXAMPLE
    .\einrichten.ps1
    .\einrichten.ps1 -Uninstall
#>
[CmdletBinding()]
param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'

$appId       = 'PDF-Knead'
$exeName     = 'PDF-Knead.exe'
$exePath     = Join-Path $PSScriptRoot $exeName
$progId      = 'PDFKnead.Document'
$classes     = 'HKCU:\Software\Classes'
$shellKey    = "$classes\SystemFileAssociations\.pdf\shell\$appId"
$appsKey     = "$classes\Applications\$exeName"
$progKey     = "$classes\$progId"
$openWithKey = "$classes\.pdf\OpenWithProgids"
$capsPath    = "Software\$appId\Capabilities"
$capsKey     = "HKCU:\$capsPath"
$appKey      = "HKCU:\Software\$appId"
$registered  = 'HKCU:\Software\RegisteredApplications'

# Explorer ueber geaenderte Zuordnungen informieren (sonst erst nach Neuanmeldung sichtbar)
function Notify-Shell {
    Add-Type -Namespace PdfKnead -Name Shell -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("shell32.dll")]
public static extern void SHChangeNotify(int eventId, uint flags, System.IntPtr item1, System.IntPtr item2);
'@
    [PdfKnead.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)   # SHCNE_ASSOCCHANGED
}

if ($Uninstall) {
    foreach ($key in @($shellKey, $appsKey, $progKey, $appKey)) {
        if (Test-Path $key) { Remove-Item $key -Recurse -Force; Write-Host "entfernt: $key" }
    }
    if ((Test-Path $openWithKey) -and (Get-ItemProperty $openWithKey).PSObject.Properties[$progId]) {
        Remove-ItemProperty -Path $openWithKey -Name $progId; Write-Host "entfernt: $openWithKey\$progId"
    }
    if ((Test-Path $registered) -and (Get-ItemProperty $registered).PSObject.Properties[$appId]) {
        Remove-ItemProperty -Path $registered -Name $appId; Write-Host "entfernt: $registered\$appId"
    }
    Notify-Shell
    Write-Host 'Fertig.'
    return
}

if (-not (Test-Path $exePath)) {
    throw "$exeName liegt nicht neben diesem Skript ($PSScriptRoot)."
}

Write-Host "Registriere $exePath ..."
$command = "`"$exePath`" `"%1`""
$icon    = "`"$exePath`",0"

# Kontextmenue von .pdf
New-Item -Path "$shellKey\command" -Force | Out-Null
Set-ItemProperty -Path $shellKey -Name 'MUIVerb' -Value 'Mit PDF-Knead oeffnen'
Set-ItemProperty -Path $shellKey -Name 'Icon'    -Value $icon
Set-ItemProperty -Path "$shellKey\command" -Name '(default)' -Value $command

# Eigener Dateityp (ProgID) - ihn zeigt "Oeffnen mit"
New-Item -Path "$progKey\shell\open\command" -Force | Out-Null
New-Item -Path "$progKey\DefaultIcon" -Force | Out-Null
Set-ItemProperty -Path $progKey -Name '(default)' -Value 'PDF-Dokument'
Set-ItemProperty -Path $progKey -Name 'FriendlyTypeName' -Value 'PDF-Dokument'
Set-ItemProperty -Path "$progKey\DefaultIcon" -Name '(default)' -Value $icon
Set-ItemProperty -Path "$progKey\shell\open\command" -Name '(default)' -Value $command
New-Item -Path "$progKey\Application" -Force | Out-Null
Set-ItemProperty -Path "$progKey\Application" -Name 'ApplicationName' -Value 'PDF-Knead'
Set-ItemProperty -Path "$progKey\Application" -Name 'ApplicationIcon' -Value $icon

# .pdf -> OpenWithProgids (leerer Wert vom Typ REG_NONE, wie Windows es erwartet)
New-Item -Path $openWithKey -Force | Out-Null
New-ItemProperty -Path $openWithKey -Name $progId -PropertyType None -Value ([byte[]]@()) -Force | Out-Null

# Anwendung (fuer "Andere App auswaehlen" und den Programmnamen)
New-Item -Path "$appsKey\shell\open\command" -Force | Out-Null
Set-ItemProperty -Path $appsKey -Name 'FriendlyAppName' -Value 'PDF-Knead'
Set-ItemProperty -Path "$appsKey\shell\open\command" -Name '(default)' -Value $command
New-Item -Path "$appsKey\SupportedTypes" -Force | Out-Null
Set-ItemProperty -Path "$appsKey\SupportedTypes" -Name '.pdf' -Value ''
New-Item -Path "$appsKey\DefaultIcon" -Force | Out-Null
Set-ItemProperty -Path "$appsKey\DefaultIcon" -Name '(default)' -Value $icon

# Standard-Apps (Einstellungen -> Apps -> Standard-Apps)
New-Item -Path "$capsKey\FileAssociations" -Force | Out-Null
Set-ItemProperty -Path $capsKey -Name 'ApplicationName' -Value 'PDF-Knead'
Set-ItemProperty -Path $capsKey -Name 'ApplicationDescription' -Value 'PDF ansehen und bearbeiten'
Set-ItemProperty -Path $capsKey -Name 'ApplicationIcon' -Value $icon
Set-ItemProperty -Path "$capsKey\FileAssociations" -Name '.pdf' -Value $progId
New-Item -Path $registered -Force | Out-Null
Set-ItemProperty -Path $registered -Name $appId -Value $capsPath

Notify-Shell

Write-Host ''
Write-Host 'Fertig. PDF-Knead steht jetzt im Rechtsklick-Menue von PDF-Dateien, unter "Oeffnen mit"'
Write-Host 'und in den Standard-Apps.'
Write-Host 'Rueckgaengig: .\einrichten.ps1 -Uninstall'
