# Schriften

Alle Schriften, die PDF-Knead mitbringt. Beim Bauen kommen sie in die Programmdatei
(ab M3); hier liegen sie nur, damit man sie sieht und austauschen kann.

## Handschriften-frei/ und Handschriften-lizenziert/ — für Unterschriften (M5)

Jede `.ttf`/`.otf` in einem der beiden Ordner erscheint im Programm als Handschrift; eine
Liste im Code gibt es nicht.

- **`Handschriften-frei/`** — frei lizenziert (OFL, Apache 2.0), kommt **in die
  Programmdatei**. Lizenztexte gehören nach `Handschriften-frei/Lizenzen/`.
- **`Handschriften-lizenziert/`** — nur privat oder kommerziell zu lizenzieren (Stand und
  Quelle: `KONZEPT.md` §6.1). **Nicht im Repository:** Der Ordner liegt nur lokal
  (`.gitignore`). In einem frischen Klon
  fehlt er, und alles geht trotzdem — es gibt dann nur die freien Handschriften. Kommt
  **nicht** ins Programm: Die Publish-Skripte legen die Dateien als `Schriften-Unterschrift`
  neben das Programm (bzw. die AppImage), beim Entwickeln landen sie neben Programm,
  Kommandozeile und Tests. Pakete zum Weitergeben baut `tools/release.sh` ohne diesen Ordner.

Eigene Schriften (gekauft oder die eigene Handschrift als Schrift) importiert man im
Programm über **Datei → Schriften** (dort lassen sie sich auch wieder entfernen); die Kopie liegt ebenfalls in
`Schriften-Unterschrift` neben dem Programm und zieht mit um, wenn man das portable
Programm samt Ordner mitnimmt. Darf das Programm dort nicht schreiben, landet sie im
Benutzerordner (`%LOCALAPPDATA%\PDF-Knead\Schriften-Unterschrift`, unter Linux
`~/.local/share/PDF-Knead/Schriften-Unterschrift`). Ein neuer Bau mit `publish` ergänzt den
Ordner um die Schriften aus `Handschriften-lizenziert/` und lässt importierte stehen; welche
mitgeliefert sind, steht in `Schriften-Unterschrift/.mitgeliefert` (nur die kommen ins Paket,
und nur von ihnen entfernt ein Bau die, die es hier nicht mehr gibt).

## CJK/ — Ersatz für chinesische, japanische, koreanische Schriften

Noto Sans CJK und Noto Serif CJK (Regular, je eine Sammeldatei für jp/kr/sc/tc/hk; SIL OFL 1.1,
https://github.com/notofonts/noto-cjk) springen ein, wenn eine PDF solche Schriften nicht einbettet.
Sie kommen **nicht** in die Programmdatei (44 MB): Die Publish-Skripte legen den Ordner als
`Schriften-CJK` neben das Programm. Fehlt er, meldet das Programm es, sobald eine PDF ihn braucht.
Neue Fassung: `tools/schriften/cjk-holen.sh` bzw. `.ps1` (URLs dort anpassen), dann die Dateien hierher kopieren.

## Arabisch/ — arabische Schrift in Ansichten, die das Programm baut

Noto Sans Arabic (2.013) und Noto Naskh Arabic (2.021), je Regular und Bold (SIL OFL 1.1, https://github.com/notofonts/arabic,
Lizenz in `Arabisch/Lizenzen/`). Sie kommen **in die Programmdatei** (zusammen etwa 1 MB) und springen ein, wenn ein Freitext ohne Ansicht
arabischen Text in einer Schrift hat, die ihn nicht kennt (Naskh für Serifenschriften, sonst Sans). Geladen am 17.11.2026 nach Freigabe des
Nutzers (docs/FEHLERGEDAECHTNIS.md, Nr. 910).

## Standard/ — Ersatz für die 14 Standardschriften (M3)

PDFs dürfen Helvetica, Times, Courier, Symbol und ZapfDingbats verwenden, ohne sie
einzubetten. Dafür springen diese Schriften ein — alle frei, auch kommerziell nutzbar:

| PDF-Schrift | Ersatz | Lizenz |
|---|---|---|
| Helvetica (auch Arial) | Liberation Sans (gleiche Zeichenbreiten) | SIL OFL 1.1 |
| Times (auch Times New Roman) | Liberation Serif | SIL OFL 1.1 |
| Courier (auch Courier New) | Liberation Mono | SIL OFL 1.1 |
| Symbol | Foxit Symbol (aus PDFium) | BSD-3-Clause |
| ZapfDingbats | Foxit Dingbats (aus PDFium) | BSD-3-Clause |

Quellen: Liberation 2.1.5 von https://github.com/liberationfonts/liberation-fonts,
Foxit-Schriften wie in pdf.js (`external/standard_fonts`, ursprünglich PDFium). Die
Foxit-Dateien sind reine CFF-Daten (deshalb `.cff`).
