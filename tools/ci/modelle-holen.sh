#!/usr/bin/env bash
# Holt die Modelle der Texterkennung und des Feld-Erkenners (Modelle/ocr, 282 MB, nicht im Git) für den Bau auf GitHub: aus dem Release
# „modelle-1“ dieses Repositorys (Datei modelle-ocr.zip, einmal von Hand hochgeladen mit tools/ci/modelle-hochladen.sh) und prüft jede Datei
# gegen tools/ci/modelle-ocr.sha256 — eine fehlende oder veränderte Datei bricht den Bau ab (wie publish.sh/publish.ps1 lokal).
#
#   tools/ci/modelle-holen.sh        braucht gh mit Anmeldung und Schreibrecht (Entwurf); im Workflow MODELLE_ZIP aus dem Job „modelle“
set -euo pipefail
cd "$(dirname "$0")/../.."
tag="modelle-1"
work="${RUNNER_TEMP:-${TMPDIR:-/tmp}}/modelle"
rm -rf "${work:?}" && mkdir -p "$work"
# the release is a draft (not shown to visitors, 20.11.2026): only a token with push access sees it. In the workflow a job of its own
# downloads it and hands the archive on (MODELLE_ZIP), so the build jobs keep read access only
if [ -n "${MODELLE_ZIP:-}" ]; then
  cp "$MODELLE_ZIP" "$work/modelle-ocr.zip"
else
  gh release download "$tag" --pattern modelle-ocr.zip --dir "$work"
fi
rm -rf Modelle/ocr && mkdir -p Modelle
unzip -q "$work/modelle-ocr.zip" -d Modelle
cp tools/ci/modelle-ocr.sha256 Modelle/ocr/SHA256SUMS
(cd Modelle/ocr && sha256sum --quiet -c SHA256SUMS) || { echo "Fehler: Modelle/ocr stimmen nicht mit tools/ci/modelle-ocr.sha256 überein." >&2; exit 1; }
echo "Modelle geholt und geprüft: $(cd Modelle/ocr && wc -l < SHA256SUMS) Dateien"
