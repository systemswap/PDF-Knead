# FunktionManager – Berechtigungen verwalten

Schreibt die Funktionsdatei einer Organisation (`berechtigungen.json`): welche Funktionen von PDF-Knead ihre Nutzer verwenden dürfen,
die Nutzungsart (gewerblich oder Behörde) und den Namen der Organisation. Getrennt von Lizenzschlüsseln; es wird nichts gesendet.
Hintergrund und Regeln: `KONZEPT.md` § 5.27 („Funktionsdatei einer Organisation“), Modell `PdfKnead.Core/Policy/FeaturePolicy.cs`.

Eigenes Programm. Es liegt in jedem Paket von PDF-Knead neben dem Programm (`PDF-Knead-FunktionManager.exe` unter Windows,
`PDF-Knead-FunktionManager` unter Linux); die Bau-Skripte im Projektordner bauen es mit.

## Bauen

Eine Datei mit .NET und Avalonia, läuft ohne installiertes .NET. Hier im Ordner:

```
.\publish.ps1 [windows|linux|beides]     (Windows)
./publish.sh  [windows|linux|beides]     (Linux)
```

Ohne Argument werden beide gebaut: `published/windows-x64/PDF-Knead-FunktionManager.exe` und
`published/linux-x64/PDF-Knead-FunktionManager`. Zum Ausprobieren: `dotnet run --project FunktionManager` im Projektordner.

## Benutzen

1. **Organisation** (wird in PDF-Knead angezeigt) und **Nutzungsart** eintragen.
2. **Funktionen:** je Reiter des Programms eine Karte, Häkchen = erlaubt. Das Häkchen am Reiter schaltet alle seine Funktionen
   (ein Strich: nur einige erlaubt); dazu „Alle erlauben“ und „Alle sperren“. Formular ist ein einziger Schalter.
3. **„Funktionsdatei speichern …“** schlägt `berechtigungen.json` vor — nur unter diesem Namen liest PDF-Knead sie.
4. Die Datei **neben das Programm** bringen: in PDF-Knead unter Datei → Einstellungen → Nutzung und Lizenz → „Funktionsdatei
   einspielen …“ (prüft die Datei und legt sie neben die Programmdatei; braucht Schreibrechte im Programmordner, meist
   Administratorrechte) — oder selbst bzw. per Softwareverteilung ablegen: unter Windows in den Ordner mit `PDF-Knead.exe`, unter Linux
   neben die AppImage-Datei. Nutzer dürfen dort nicht schreiben können. Sie gilt ab dem nächsten Start von PDF-Knead; nach dem
   Einspielen startet PDF-Knead von selbst neu und öffnet die offenen Dateien wieder (bei ungespeicherten Änderungen erst nach dem
   Speichern, von Hand).
   Mit „Funktionsdatei öffnen …“ lässt sich eine vorhandene Datei weiter bearbeiten. Der FunktionManager zeigt diese Schritte selbst
   unter „So kommt die Datei an ihren Platz“.

## Was PDF-Knead daraus macht

- Die Nutzungsart der Datei gilt; die Frage beim ersten Start entfällt, „Nutzungsart ändern“ ist aus. Datei weg = die gewählte gilt wieder.
- Gesperrte Funktionen sind ausgegraut und sagen, warum; auch Tastenkürzel und Menüs lehnen ab. Öffnen, Ansehen und Prüfen gehen immer.
- Eine beschädigte Datei sperrt alle einschränkbaren Funktionen, statt alles zu erlauben.
- Jeder sieht unter Einstellungen → Nutzung und Lizenz → „Berechtigungen anzeigen“, was gilt.
- Ältere Fassungen von PDF-Knead lesen neuere Dateien: unbekannte Funktionsnamen werden übergangen (bleiben erlaubt).
