#!/usr/bin/env bash
set -Eeuo pipefail

# Erstellt die fertigen Programme in published/:
#
#   published/linux-x64/PDF-Knead-x86_64.AppImage   eine Datei: Programm, .NET und Avalonia
#   published/windows-x64/PDF-Knead.exe             eine Datei: Programm, .NET und Avalonia
#   daneben jeweils der FunktionManager (PDF-Knead-FunktionManager[.exe]): die Funktionsdatei für Admins, eine Datei
#   published/PDF-Knead-<Version>-linux-x64.tar.gz, PDF-Knead-<Version>-windows-x64.zip (ältere Pakete werden ersetzt)
#
# Neben jedes Programm kommen THIRD-PARTY-NOTICES.md und licenses/, unter Linux
# außerdem einrichten.sh / entfernen.sh für Menüeintrag und „Öffnen mit …“, unter
# Windows einrichten.ps1. Nach einem Linux-Bau trägt das Skript das Programm im Menü ein.
#
# Paketform (Bauart):
#   aot      Eine Datei, AOT-Binary (Standard): Maschinencode, Start ~0,3 s statt ~1 s. .NET und Avalonia
#            sind hineinübersetzt, Skia/HarfBuzz stecken als Ressource darin (legt das
#            Programm beim ersten Start unter ~/.local/share/PDF-Knead/bibliotheken ab).
#            Geht nur für das eigene System und braucht clang und zlib:
#              sudo apt install clang zlib1g-dev
#            Die Windows-Fassung wird hier deshalb als .NET-Build gebaut (AOT: publish.ps1).
#   dotnet   .NET-Build: eine Datei mit .NET und ReadyToRun (Start ~1 s), überall baubar.
#   einzeln  .NET-Build mit allen Bibliotheken lose im Ordner, ohne AppImage (Fehlersuche).
#
# Eigene Sprachdateien (languages/) und das Log neben dem Programm bleiben bei
# jedem Bau stehen. Ein Linux-Bau lässt den Windows-Stand stehen und umgekehrt.
#
# Aufruf ohne Argumente fragt erst nach der Paketform, dann nach dem Ziel. Mit
# Argumenten ohne Rückfrage, Reihenfolge egal; was fehlt, gilt als Standard
# (Paketform aot, Ziel beides):
#   ./publish.sh [linux|windows|beides] [aot|dotnet|einzeln] [ohnetests]
#   z. B. ./publish.sh linux   ./publish.sh linux einzeln   ./publish.sh beides dotnet
#
# ohnetests: die Testsuite vor dem Bau auslassen (einige Minuten weniger). Nur für einen Stand, dessen
# Suite gerade grün gelaufen ist (vor jedem Commit läuft sie ohnehin); ohne den Schalter wie bisher.
#
# Zusätzliche Kopie der Windows-Fassung (optional): Steht in
#   ~/.config/pdf-knead-build/publish.conf
# eine Zeile  windows_kopie=/pfad/zum/ordner , kommt nach jedem Windows-Bau der Inhalt von
# published/windows-x64/ direkt in diesen Ordner (ohne Unterordner), z. B. in einen
# Sync-Ordner für einen anderen Rechner. Dort ersetzt werden nur die mitgelieferten Dateien.
#
# Benötigt: .NET SDK 10, wget (lädt appimagetool beim ersten Mal), zip

# Keine Nutzungsdaten an Microsoft (.NET) oder Avalonia senden.
export DOTNET_CLI_TELEMETRY_OPTOUT=1 AVALONIA_TELEMETRY_OPTOUT=1 DO_NOT_TRACK=1
export DOTNET_NOLOGO=1

PROJECT_DIR="$(cd "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")" && pwd)"
PROJECT="$PROJECT_DIR/PdfKnead.Desktop/PdfKnead.Desktop.csproj"
SOLUTION="$PROJECT_DIR/PdfKnead.sln"
OUT_DIR="$PROJECT_DIR/published"
LINUX_DIR="$OUT_DIR/linux-x64"
WINDOWS_DIR="$OUT_DIR/windows-x64"
CACHE_DIR="${XDG_CACHE_HOME:-$HOME/.cache}/pdf-knead-build"
WORK_DIR="$CACHE_DIR/publish"
ARTIFACTS="$CACHE_DIR/artifacts"
APPIMAGETOOL="$CACHE_DIR/appimagetool-x86_64.AppImage"
ICON="$PROJECT_DIR/PdfKnead.Ui/Assets/Icon.png"
LOCAL_CONF="${XDG_CONFIG_HOME:-$HOME/.config}/pdf-knead-build/publish.conf"
WINDOWS_COPY=""
[ -f "$LOCAL_CONF" ] && WINDOWS_COPY="$(sed -n 's/^windows_kopie=//p' "$LOCAL_CONF" | tail -1)"

TARGET=""
BAUART=""
OHNE_TESTS=""
ANGABEN=0
for argument in "$@"; do
  case "$argument" in
    linux|windows) TARGET="$argument" ;;
    beides|all) TARGET=all ;;
    aot|eine-datei|datei) BAUART=aot ;;
    dotnet|net) BAUART=dotnet ;;
    einzeln|ordner) BAUART=einzeln ;;
    ohnetests|ohne-tests|notests) OHNE_TESTS=1; continue ;;
    *) echo "Aufruf: $0 [linux|windows|beides] [aot|dotnet|einzeln] [ohnetests]" >&2; exit 1 ;;
  esac
  ANGABEN=$((ANGABEN + 1))
done

if [ "$ANGABEN" -gt 0 ]; then
  # Mit Argumenten nie nachfragen (z. B. nach jeder Versionsänderung: ./publish.sh linux).
  TARGET="${TARGET:-all}"
  BAUART="${BAUART:-aot}"
fi

if [ -z "$BAUART" ]; then
  echo
  echo "Welche Paketform?"
  echo "  1) Eine Datei, AOT-Binary (Standard) – Maschinencode, schneller Start (nur für Linux; Windows dann als .NET-Build)"
  echo "  2) Eine Datei, .NET-Build            – mit .NET (ReadyToRun)"
  echo "  3) Einzeln                           – .NET-Build, alle Bibliotheken lose im Ordner, ohne AppImage (Fehlersuche)"
  echo
  read -r -p "Auswahl [1]: " selection
  case "${selection:-1}" in
    1) BAUART=aot ;;
    2) BAUART=dotnet ;;
    3) BAUART=einzeln ;;
    *) echo "Fehler: Ungültige Auswahl." >&2; exit 1 ;;
  esac
fi

if [ -z "$TARGET" ]; then
  echo
  echo "Wofür soll gebaut werden?"
  echo "  1) beides (Standard)"
  echo "  2) Linux"
  echo "  3) Windows"
  echo
  read -r -p "Auswahl [1]: " selection
  case "${selection:-1}" in
    1) TARGET=all ;;
    2) TARGET=linux ;;
    3) TARGET=windows ;;
    *) echo "Fehler: Ungültige Auswahl." >&2; exit 1 ;;
  esac
fi

if [ "$BAUART" = einzeln ]; then SINGLE_FILE=false; else SINGLE_FILE=true; fi
echo "Ziel: $TARGET, Bauart: $BAUART"

command -v dotnet > /dev/null || { echo "Fehler: .NET SDK fehlt." >&2; exit 1; }
# Nicht, solange das Programm aus published/ läuft.
if pgrep -f "$LINUX_DIR/PDF-Knead(-x86_64\.AppImage|-FunktionManager)?( |$)" > /dev/null; then
  echo "Fehler: PDF-Knead oder der FunktionManager läuft gerade (aus published/) – bitte erst beenden, dann neu bauen." >&2
  exit 1
fi

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJECT_DIR/Directory.Build.props" | head -1)"
echo "PDF-Knead $VERSION"

rm -rf "$WORK_DIR"
mkdir -p "$WORK_DIR" "$OUT_DIR"

# Ein kaputter Stand soll nicht in published/ landen.
if [ -n "$OHNE_TESTS" ]; then
  echo "== Tests ausgelassen (ohnetests) – nur für einen Stand, dessen Suite gerade grün gelaufen ist"
else
echo "== Tests (ohne Zeittests)"
dotnet test "$SOLUTION" -c Release --filter "Art!=Zeit" -p:UseArtifactsOutput=true -p:ArtifactsPath="$ARTIFACTS" > "$WORK_DIR/test.log" 2>&1 \
  || { tail -30 "$WORK_DIR/test.log" >&2; echo "Fehler: Tests fehlgeschlagen – nichts veröffentlicht." >&2; exit 1; }
grep -E "Bestanden!|Passed!" "$WORK_DIR/test.log" | sed 's/^/   /' || true
# Tests mit fester Zeitgrenze laufen nicht beim Veröffentlichen (ein langsamer Rechner soll den Bau nicht stoppen): wie viele es sind
ZEIT=$(dotnet test "$SOLUTION" -c Release --no-build --list-tests --filter "Art=Zeit" -p:UseArtifactsOutput=true -p:ArtifactsPath="$ARTIFACTS" 2>/dev/null | grep -c "^    [^ ]" || true)
echo "   $ZEIT Zeittests ausgelassen (einzeln: dotnet test --filter Art=Zeit)"
fi

# Eigene Lizenz, Hinweise auf fremde Bestandteile und deren Lizenztexte neben das Programm.
notices() {
  local dest="$1"
  install -m 0644 "$PROJECT_DIR/LICENSE" "$dest/LICENSE"
  install -m 0644 "$PROJECT_DIR/THIRD-PARTY-NOTICES.md" "$dest/THIRD-PARTY-NOTICES.md"
  rm -rf "$dest/licenses"
  mkdir -p "$dest/licenses"
  install -m 0644 "$PROJECT_DIR/packaging/licenses/"* "$dest/licenses/"
  # Lizenzen der mitgelieferten Schriften
  install -m 0644 "$PROJECT_DIR/Schriften/Standard/Lizenzen/"* "$PROJECT_DIR/Schriften/Handschriften-frei/Lizenzen/"* "$dest/licenses/"
}

# CJK-Ersatzschriften (Noto CJK, ~44 MB) neben das Programm, nicht hinein (Entscheidung vom 19.10.2026). Sie liegen im
# Repository unter Schriften/CJK (Wunsch vom 19.10.2026: kommen mit dem Klonen); sonst aus dem Cache von
# tools/schriften/cjk-holen.sh. Fehlen sie, meldet das Programm es erst, wenn eine PDF sie braucht.
cjk_fonts() {
  local dest="$1" source="$PROJECT_DIR/Schriften/CJK"
  [ -f "$source/NotoSansCJK-Regular.ttc" ] || source="$CACHE_DIR/cjk/Schriften-CJK"
  if [ ! -f "$source/NotoSansCJK-Regular.ttc" ]; then
    echo "   Schriften-CJK fehlt (Schriften/CJK im Repository, sonst tools/schriften/cjk-holen.sh) – ohne: kein CJK-Ersatz"
    return
  fi
  rm -rf "$dest/Schriften-CJK"
  cp -a "$source" "$dest/Schriften-CJK"
}

# Schriften-Unterschrift neben dem Programm bzw. der AppImage: dorthin importiert das Programm eigene Schriften (Datei -> Schrift
# importieren); sie bleiben bei jedem Bau stehen (Fehlergedächtnis 96) und kommen nie ins Paket. Lizenzierte Handschriften liefert PDF-Knead
# nicht mehr mit (seit 25.11.2026): was ein früherer Bau mitgeliefert hat (Schriften-Unterschrift/.mitgeliefert), entfernt ein neuer Bau.
signature_fonts() {
  local target="$1/Schriften-Unterschrift" name
  local manifest="$target/.mitgeliefert"
  if [ -f "$manifest" ]; then
    while IFS= read -r name; do
      case "$name" in ''|*/*) continue ;; esac
      rm -f -- "$target/$name"
    done < "$manifest"
    rm -f -- "$manifest"
  fi
  [ -d "$target" ] && rmdir "$target" 2>/dev/null || true   # leer (nichts importiert): weg
}

# Modelle der Texterkennung und des Feld-Erkenners (PP-OCRv6 small und medium, FFDetr; Festlegung 13.11.2026: im Paket,
# kein Download mehr) aus Modelle/ocr neben das Programm bzw. die AppImage (Ordner ocr), jede Datei gegen Modelle/ocr/SHA256SUMS.
# Fehlt eine oder stimmt eine Prüfsumme nicht: Abbruch — ohne Modelle wird kein Paket gebaut (die Modelle aus einem Paket von PDF-Knead oder von den Quellen in THIRD-PARTY-NOTICES.md nach Modelle/ocr legen; Prüfsummen: tools/ci/modelle-ocr.sha256).
models() {
  local dest="$1" source="$PROJECT_DIR/Modelle/ocr" rel
  [ -f "$source/SHA256SUMS" ] || { echo "Fehler: Modelle/ocr/SHA256SUMS fehlt (die Modelle aus einem Paket von PDF-Knead oder von den Quellen in THIRD-PARTY-NOTICES.md nach Modelle/ocr legen; Prüfsummen: tools/ci/modelle-ocr.sha256)." >&2; exit 1; }
  (cd "$source" && sha256sum --quiet -c SHA256SUMS) || { echo "Fehler: Modelle/ocr: eine Datei fehlt oder ihre Prüfsumme stimmt nicht." >&2; exit 1; }
  rm -rf "$dest/ocr"
  while read -r _ rel; do
    rel="${rel#\*}"
    case "$rel" in ''|/*|*..*) continue ;; esac
    install -Dm0644 "$source/$rel" "$dest/ocr/$rel"
  done < "$source/SHA256SUMS"
}

# Was das Programm neben sich schreibt; bleibt bei jedem Bau stehen (Schriften-Unterschrift: siehe signature_fonts). Der Ordner ocr gehört
# seit 13.11.2026 zum Paket (models) und wird bei jedem Bau neu geschrieben. Dazu die Dateien der IT neben dem Programm: die
# Funktionsdatei (berechtigungen.json) und updates-aus — sie bleiben stehen und kommen nie ins Paket.
USER_FILES=(languages PDF-Knead.log PDF-Knead.log.alt Schriften-Unterschrift berechtigungen.json updates-aus)

clear_program_folder() {
  local dest="$1" keep=() name
  mkdir -p "$dest"
  for name in "${USER_FILES[@]}"; do keep+=(! -name "$name"); done
  find "$dest" -mindepth 1 -maxdepth 1 "${keep[@]}" -exec rm -rf -- {} +
}

# Compiler-Warnungen aus dem Protokoll zeigen, je Stelle einmal — ein erfolgreicher Bau zeigte sonst keine
# (Fehlergedächtnis 641). Projekte, die nicht neu übersetzt werden, warnen nicht erneut: vollständig sieht man sie mit --no-incremental.
show_warnings() {
  local log="$1" found
  # (grep ohne Treffer endet mit 1 — unter set -eo pipefail beendete das das Skript, gerade wenn es keine Warnungen gab: Fehlergedächtnis 644)
  found=$( { grep -E ': (warning|Warnung) [A-Z]+[0-9]+' "$log" || true; } | sed -E 's/ \[[^]]*\]$//' | sort -u)
  [ -z "$found" ] && return 0
  echo "Warnungen beim Bau ($(printf '%s\n' "$found" | wc -l)):" >&2
  printf '   %s\n' "$found" >&2
}

publish() {
  local rid="$1" dest="$2"
  dotnet publish "$PROJECT" -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=$SINGLE_FILE \
    -p:IncludeNativeLibrariesForSelfExtract=$SINGLE_FILE \
    -p:EnableCompressionInSingleFile=$SINGLE_FILE \
    -p:DebugType=None -p:DebugSymbols=false \
    -p:UseArtifactsOutput=true -p:ArtifactsPath="$ARTIFACTS" \
    -o "$dest" > "$WORK_DIR/publish-$rid.log" 2>&1 \
    || { tail -20 "$WORK_DIR/publish-$rid.log" >&2; echo "Fehler: dotnet publish für $rid fehlgeschlagen." >&2; exit 1; }
  show_warnings "$WORK_DIR/publish-$rid.log"
  find "$dest" -name '*.pdb' -delete
}

# Native AOT nur für das eigene System; braucht clang (Linker) und zlib.
aot_possible() {
  [ "$(uname -s)" = Linux ] && command -v clang > /dev/null && [ -e /usr/include/zlib.h ]
}

publish_aot() {
  local rid="$1" dest="$2"
  dotnet publish "$PROJECT" -c Release -r "$rid" --self-contained true \
    -p:PublishAot=true \
    -p:DebugType=None -p:DebugSymbols=false \
    -p:UseArtifactsOutput=true -p:ArtifactsPath="$ARTIFACTS" \
    -o "$dest" > "$WORK_DIR/publish-$rid.log" 2>&1 \
    || { tail -20 "$WORK_DIR/publish-$rid.log" >&2; echo "Fehler: dotnet publish (Native AOT) für $rid fehlgeschlagen." >&2; exit 1; }
  show_warnings "$WORK_DIR/publish-$rid.log"
  find "$dest" \( -name '*.pdb' -o -name '*.dbg' \) -delete
}

# Der FunktionManager (Funktionsdatei für Admins, KONZEPT §5.27) liegt in jedem Paket neben dem Programm: eine Datei mit .NET und Avalonia.
funktionmanager() {
  local rid="$1" dest="$2" name=PDF-Knead-FunktionManager out="$WORK_DIR/funktionmanager-$1"
  [ "${rid#win-}" != "$rid" ] && name=PDF-Knead-FunktionManager.exe
  dotnet publish "$PROJECT_DIR/FunktionManager/FunktionManager.csproj" -c Release -r "$rid" -o "$out" \
    > "$WORK_DIR/funktionmanager-$rid.log" 2>&1 \
    || { tail -20 "$WORK_DIR/funktionmanager-$rid.log" >&2; echo "Fehler: FunktionManager für $rid fehlgeschlagen." >&2; exit 1; }
  if [ "$name" = PDF-Knead-FunktionManager.exe ]; then install -m 0644 "$out/$name" "$dest/$name"; else install -m 0755 "$out/$name" "$dest/$name"; fi
  echo "   FunktionManager: $name"
}

# Paket nur mit dem Mitgelieferten: ohne Log, eigene Sprachdateien und selbst importierte Schriften.
package() {
  local folder="$1" archive="$2" name file
  local fonts="$OUT_DIR/$folder/Schriften-Unterschrift" skip=()
  for name in "${USER_FILES[@]}"; do
    [ "$name" = Schriften-Unterschrift ] || skip+=("$folder/$name")
  done
  if [ -d "$fonts" ]; then
    skip+=("$folder/Schriften-Unterschrift/.mitgeliefert")
    for file in "$fonts"/*; do
      [ -f "$file" ] || continue
      name="$(basename "$file")"
      grep -qxF -- "$name" "$fonts/.mitgeliefert" 2>/dev/null || skip+=("$folder/Schriften-Unterschrift/$name")
    done
  fi
  # die Pakete tragen die Version im Namen: die älteren desselben Ziels gehen (auch die ohne Version)
  rm -f "$OUT_DIR/$archive" "$OUT_DIR"/PDF-Knead-*"$folder".tar.gz "$OUT_DIR"/PDF-Knead-*"$folder".zip
  case "$archive" in
    *.tar.gz)
      local excludes=()
      for name in "${skip[@]}"; do excludes+=(--exclude="$name"); done
      tar -C "$OUT_DIR" "${excludes[@]}" -czf "$OUT_DIR/$archive" "$folder" ;;
    *.zip) (cd "$OUT_DIR" && zip -qr "$archive" "$folder" -x "${skip[@]}" "$folder/languages/*") ;;
  esac
}

# ---------------------------------------------------------------- Linux
if [ "$TARGET" = all ] || [ "$TARGET" = linux ]; then
  echo "== Linux"
  if [ "$BAUART" = aot ] && aot_possible; then
    echo "   AOT-Binary"
    publish_aot linux-x64 "$WORK_DIR/linux"
  else
    if [ "$BAUART" = aot ]; then
      echo "   Kein AOT möglich (clang/zlib fehlen: sudo apt install clang zlib1g-dev) – .NET-Build"
    else
      echo "   .NET-Build"
    fi
    publish linux-x64 "$WORK_DIR/linux"
  fi
  clear_program_folder "$LINUX_DIR"

  if [ "$SINGLE_FILE" = true ]; then
    APPDIR="$WORK_DIR/AppDir"
    mkdir -p "$APPDIR/usr/bin"
    # Programm und (bei AOT) die nativen Bibliotheken daneben
    cp -a "$WORK_DIR/linux/." "$APPDIR/usr/bin/"
    chmod 0755 "$APPDIR/usr/bin/PDF-Knead"
    install -m 0644 "$PROJECT_DIR/packaging/linux/PDF-Knead.desktop" "$APPDIR/PDF-Knead.desktop"
    install -m 0644 "$ICON" "$APPDIR/pdf-knead.png"

    cat > "$APPDIR/AppRun" << 'APPRUN'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/bin/PDF-Knead" "$@"
APPRUN
    chmod 0755 "$APPDIR/AppRun"

    if [ ! -x "$APPIMAGETOOL" ]; then
      mkdir -p "$CACHE_DIR"
      wget -nv -O "$APPIMAGETOOL" "https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-x86_64.AppImage"
      chmod +x "$APPIMAGETOOL"
    fi

    ARCH=x86_64 "$APPIMAGETOOL" --no-appstream "$APPDIR" "$LINUX_DIR/PDF-Knead-x86_64.AppImage" > "$WORK_DIR/appimagetool.log" 2>&1 \
      || { tail -20 "$WORK_DIR/appimagetool.log" >&2; echo "Fehler: appimagetool fehlgeschlagen." >&2; exit 1; }
  else
    cp -a "$WORK_DIR/linux/." "$LINUX_DIR/"
    chmod 0755 "$LINUX_DIR/PDF-Knead"
  fi

  notices "$LINUX_DIR"
  cjk_fonts "$LINUX_DIR"
  signature_fonts "$LINUX_DIR"
  models "$LINUX_DIR"

  install -Dm644 "$ICON" "$LINUX_DIR/share/icons/hicolor/256x256/apps/pdf-knead.png"
  install -Dm644 "$PROJECT_DIR/packaging/linux/PDF-Knead.desktop" "$LINUX_DIR/share/applications/PDF-Knead.desktop"
  install -m 0755 "$PROJECT_DIR/packaging/linux/einrichten.sh" "$PROJECT_DIR/packaging/linux/entfernen.sh" "$LINUX_DIR/"
  funktionmanager linux-x64 "$LINUX_DIR"

  package linux-x64 "PDF-Knead-$VERSION-linux-x64.tar.gz"
fi

# ---------------------------------------------------------------- Windows
if [ "$TARGET" = all ] || [ "$TARGET" = windows ]; then
  echo "== Windows"
  if [ "$BAUART" = aot ]; then
    # Native AOT übersetzt nicht über Betriebssysteme hinweg
    echo "   AOT für Windows geht nur unter Windows (publish.ps1) – hier .NET-Build"
  else
    echo "   .NET-Build"
  fi
  publish win-x64 "$WORK_DIR/windows"
  clear_program_folder "$WINDOWS_DIR"
  if [ "$SINGLE_FILE" = true ]; then
    install -m 0644 "$WORK_DIR/windows/PDF-Knead.exe" "$WINDOWS_DIR/PDF-Knead.exe"
  else
    cp -a "$WORK_DIR/windows/." "$WINDOWS_DIR/"
  fi
  notices "$WINDOWS_DIR"
  cjk_fonts "$WINDOWS_DIR"
  signature_fonts "$WINDOWS_DIR"
  models "$WINDOWS_DIR"
  install -m 0644 "$PROJECT_DIR/packaging/windows/einrichten.ps1" "$WINDOWS_DIR/einrichten.ps1"
  funktionmanager win-x64 "$WINDOWS_DIR"
  package windows-x64 "PDF-Knead-$VERSION-windows-x64.zip"

  # Kopie direkt in den eingestellten Ordner; dort nur ersetzen, was der Bau mitbringt. Die Handschriften dort
  # nur ergänzen wie in published/ – selbst importierte bleiben.
  if [ -n "$WINDOWS_COPY" ]; then
    mkdir -p "$WINDOWS_COPY"
    for entry in "$WINDOWS_DIR"/*; do
      name="$(basename "$entry")"
      case " ${USER_FILES[*]} " in *" $name "*) continue ;; esac
      rm -rf -- "$WINDOWS_COPY/$name"
      cp -a -- "$entry" "$WINDOWS_COPY/$name"
    done
    signature_fonts "$WINDOWS_COPY"
    echo "   Kopie: $WINDOWS_COPY"
  fi
fi

echo
echo "Fertig (PDF-Knead $VERSION):"
find "$OUT_DIR" -maxdepth 3 -type f \( -name "*.AppImage" -o -name "*.exe" -o -name PDF-Knead -o -name "*.tar.gz" -o -name "*.zip" \) -printf "  %P  (%s Bytes)\n" | sort

if [ "$TARGET" = all ] || [ "$TARGET" = linux ]; then
  echo
  "$LINUX_DIR/einrichten.sh"
fi
