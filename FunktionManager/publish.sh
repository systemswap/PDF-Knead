#!/usr/bin/env bash
# Baut den FunktionManager nach ./published/ - Gegenstueck zu publish.ps1 (Aufruf und Ablauf gleich).
#
# Eine Datei mit .NET und Avalonia (laeuft ohne installiertes .NET):
#   published/windows-x64/PDF-Knead-FunktionManager.exe
#   published/linux-x64/PDF-Knead-FunktionManager
#
# # published/ ist von git ausgenommen.
#
# Aufruf: ./publish.sh [windows|linux|beides]   (ohne Argument: beides)
# Benoetigt: .NET SDK 10.
set -euo pipefail
ordner="$(cd "$(dirname "$0")" && pwd)"
ziel="${1:-beides}"
case "$ziel" in
  windows|linux|beides) ;;
  *) echo "Unbekanntes Ziel: $ziel (windows, linux oder beides)" >&2; exit 2 ;;
esac

baue() {
  local rid="$1" name="$2" datei="$3"
  local aus="$ordner/published/$name"
  # a running FunktionManager holds its file: say so instead of a long build error
  if command -v pgrep >/dev/null 2>&1 && pgrep -f "$aus/$datei" >/dev/null 2>&1; then
    echo "Der FunktionManager in $aus laeuft noch - bitte zuerst schliessen, dann neu bauen." >&2; exit 1
  fi
  echo "Baue $name ..."
  # nur das Programm ersetzen; was daneben liegt, bleibt
  dotnet publish "$ordner/FunktionManager.csproj" -c Release -r "$rid" -o "$aus" --nologo -v quiet
  [ -f "$aus/$datei" ] || { echo "Programm fehlt nach dem Bau: $aus/$datei" >&2; exit 1; }
  [ "$rid" = "win-x64" ] || chmod +x "$aus/$datei"
  echo "  fertig: $aus/$datei ($(du -h "$aus/$datei" | cut -f1))"
}

if [ "$ziel" = windows ] || [ "$ziel" = beides ]; then baue win-x64 windows-x64 PDF-Knead-FunktionManager.exe; fi
if [ "$ziel" = linux ] || [ "$ziel" = beides ]; then baue linux-x64 linux-x64 PDF-Knead-FunktionManager; fi
