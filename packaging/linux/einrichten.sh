#!/usr/bin/env bash
# Trägt PDF-Knead für den angemeldeten Benutzer ins Anwendungsmenü ein und
# meldet es im Dateimanager unter „Öffnen mit …“ für PDF-Dateien an – ohne
# Root-Rechte. Kopiert werden nur Menüeintrag und Symbol; der Eintrag zeigt auf
# die AppImage in diesem Ordner (bei der Paketform „einzeln“ auf das Programm
# im Ordner). Zum Standardprogramm wird PDF-Knead nicht – das legt man im
# Dateimanager selbst fest (Eigenschaften → Öffnen mit).
#
# GNOME zeigt das Programmsymbol in der Leiste nur mit so einem Eintrag
# (StartupWMClass = Fensterklasse). Dateiname = Fensterklasse.
#
# Wird der Ordner verschoben: hier erneut ausführen. Rückgängig: ./entfernen.sh
set -euo pipefail

HERE="$(cd "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")" && pwd)"
PROG="$HERE/PDF-Knead-x86_64.AppImage"
[ -f "$PROG" ] || PROG="$HERE/PDF-Knead"
DATA="${XDG_DATA_HOME:-$HOME/.local/share}"

[ -f "$PROG" ] || { echo "Fehler: $PROG fehlt." >&2; exit 1; }
chmod +x "$PROG"

install -Dm644 "$HERE/share/icons/hicolor/256x256/apps/pdf-knead.png" "$DATA/icons/hicolor/256x256/apps/pdf-knead.png"

# Exec in Anführungszeichen (Pfade mit Leerzeichen), Sonderzeichen so maskiert,
# wie die Desktop-Entry-Spezifikation es verlangt.
escaped="$(printf '%s' "$PROG" | sed -e 's/\\/\\\\\\\\/g' -e 's/["`$]/\\\\&/g' -e 's/%/%%/g')"

mkdir -p "$DATA/applications"

# Frühere Einträge des Programms unter anderem Dateinamen entfernen (früherer Name
# PDFSight, andere Schreibweisen): GNOME hält sonst am alten Eintrag fest und startet
# weiter das alte Programm.
for old in "$DATA/applications/"*.desktop; do
  [ -f "$old" ] && [ "$(basename "$old")" != PDF-Knead.desktop ] || continue
  if grep -qE '^StartupWMClass=(PDF-Knead|PDFSight)$|^Exec=.*(PDF-Knead-x86_64\.AppImage|PDFSight)' "$old"; then
    rm -f "$old"
    echo "Alten Menüeintrag entfernt: $(basename "$old")"
  fi
done

# Nur schreiben, wenn sich der Inhalt ändert: GNOME merkt sich Sortierung und
# Ordner am Dateinamen; ein unveränderter Eintrag löst nichts aus.
ENTRY="$DATA/applications/PDF-Knead.desktop"
NEW="$(mktemp)"
trap 'rm -f "$NEW"' EXIT
# %F: mehrere markierte PDFs kommen zusammen in ein Fenster.
EXEC_LINE="Exec=\"$escaped\" %F" awk '/^Exec=/ { print ENVIRON["EXEC_LINE"]; next } { print }' \
  "$HERE/share/applications/PDF-Knead.desktop" > "$NEW"

if cmp -s "$NEW" "$ENTRY"; then
  echo "Menüeintrag unverändert."
  exit 0
fi
install -m 644 "$NEW" "$ENTRY"

command -v update-desktop-database > /dev/null 2>&1 && update-desktop-database "$DATA/applications" || true
command -v gtk-update-icon-cache > /dev/null 2>&1 && gtk-update-icon-cache -f -t "$DATA/icons/hicolor" > /dev/null 2>&1 || true

echo "Eingetragen: PDF-Knead steht jetzt im Anwendungsmenü und bei PDF-Dateien unter „Öffnen mit …“."
echo "Rückgängig: ./entfernen.sh"
