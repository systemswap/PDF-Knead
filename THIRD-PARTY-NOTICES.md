# Fremde Bestandteile und ihre Lizenzen

PDF-Knead selbst steht unter einer eigenen Lizenz (Datei `LICENSE`, „Source
Available“). Das PDF-Spezifische (Parser, Renderer, Schriften, Verschlüsselung,
Kompression) ist eigener Code bis auf die einzelnen Stellen, die unter
„Übertragener Code“ aufgeführt sind. Mit dem Programm werden die folgenden
Bestandteile anderer Projekte weitergegeben; ihre Rechte liegen bei den
jeweiligen Autoren. Die Lizenztexte liegen im Ordner `licenses/` neben dem
Programm; Ergebnis der Lizenzprüfung: `docs/LIZENZPRUEFUNG.md`.

## Im Programm (AppImage bzw. PDF-Knead.exe)

| Bestandteil | Version | Lizenz | Projekt |
|---|---|---|---|
| .NET-Laufzeit (auch Native AOT), darin u. a. Brotli, zlib-ng | 10.0.14 | MIT und die Lizenzen der enthaltenen Bestandteile – `licenses/dotnet-runtime-*-THIRD-PARTY-NOTICES.txt` | https://github.com/dotnet/runtime |
| Avalonia (UI, Fluent-Design, Skia-, X11-, Win32- und FreeDesktop-Anbindung) | 12.1.1 | MIT | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Fonts.Inter | 12.1.1 | MIT | https://github.com/AvaloniaUI/Avalonia |
| Schrift Inter (in Avalonia.Fonts.Inter) | | SIL Open Font License 1.1 – `licenses/inter-OFL.txt` | https://github.com/rsms/inter |
| Schrift Pacifico (nur als Umrisse des Schriftzugs „PDF-Knead“ im Unterstützen-Fenster; die Schriftdatei liegt nicht bei) | 2018 | SIL Open Font License 1.1 – `licenses/Pacifico-OFL.txt` | https://github.com/googlefonts/Pacifico |
| ANGLE (nur Windows, über Avalonia.Angle.Windows.Natives) | 2.1.27548 | BSD-3-Clause – `licenses/ANGLE-BSD-3.txt` | https://chromium.googlesource.com/angle/angle |
| Abseil (in der ANGLE-Bibliothek `av_libglesv2.dll`, nur Windows) | | Apache License 2.0 – `licenses/Abseil-Apache-2.0.txt` | https://github.com/abseil/abseil-cpp |
| SkiaSharp / Skia, darin u. a. libjpeg-turbo, libpng, zlib, FreeType, expat, ICU, libwebp, skcms, Adobe DNG SDK | 3.119.4 | MIT / BSD-3-Clause und die Lizenzen der enthaltenen Bibliotheken – `licenses/SkiaSharp-HarfBuzzSharp-THIRD-PARTY-NOTICES.txt` | https://github.com/mono/SkiaSharp |
| HarfBuzzSharp / HarfBuzz | 8.3.1.3 | MIT / „Old MIT“ – `licenses/HarfBuzz-OldMIT.txt` | https://github.com/mono/SkiaSharp, https://github.com/harfbuzz/harfbuzz |
| CommunityToolkit.Mvvm | 8.4.2 | MIT – `licenses/CommunityToolkit-ThirdPartyNotices.txt` | https://github.com/CommunityToolkit/dotnet |
| System.Security.Cryptography.Pkcs (CMS-Signaturen für digitale Unterschriften, `PdfKnead.Core/Pdf/Signatures/`) | 10.0.14 | MIT – wie die .NET-Laufzeit, `licenses/dotnet-runtime-*-THIRD-PARTY-NOTICES.txt` | https://github.com/dotnet/runtime |
| System.Numerics.Tensors (kommt mit ONNX Runtime) | 9.0.0 | MIT – wie die .NET-Laufzeit, `licenses/dotnet-runtime-*-THIRD-PARTY-NOTICES.txt` | https://github.com/dotnet/runtime |
| Tmds.DBus.Protocol, MicroCom.Runtime (über Avalonia) | | MIT | https://github.com/tmds/Tmds.DBus, https://github.com/kekekeks/MicroCom |
| Fluent UI System Icons (Vektorpfade der Icons) | | MIT – `licenses/fluentui-system-icons-MIT.txt` | https://github.com/microsoft/fluentui-system-icons |
| Liberation Sans, Serif, Mono (Ersatz für Helvetica, Times, Courier) | 2.1.5 | SIL Open Font License 1.1 – `licenses/Liberation-OFL.txt` | https://github.com/liberationfonts/liberation-fonts |
| Foxit Symbol, Foxit Dingbats (Ersatz für Symbol, ZapfDingbats; aus PDFium) | | BSD-3-Clause – `licenses/Foxit-BSD.txt` | https://pdfium.googlesource.com/pdfium, https://github.com/mozilla/pdf.js |
| Adobe Glyph List, ZapfDingbats-Namen (Glyphenname → Unicode) | 2.0 | BSD-3-Clause – `licenses/AdobeGlyphList-BSD-3.txt` (auch im Kopf der Dateien `PdfKnead.Core/Fonts/Data/*.txt`) | https://github.com/adobe-type-tools/agl-aglfn |
| Vordefinierte CMaps für Chinesisch, Japanisch, Koreanisch (Adobe cmap-resources: Adobe-CNS1-7, -GB1-6, -Japan1-7, -Korea1-2, -KR-9) | 2023 | BSD-3-Clause – `licenses/Adobe-cmap-resources-BSD.txt` (gepackt in `PdfKnead.Core/Fonts/Data/cmaps.zip`) | https://github.com/adobe-type-tools/cmap-resources |
| CID → Unicode (Adobe mapping-resources-pdf: Adobe-*-UCS2) | 2023 | BSD-3-Clause – `licenses/Adobe-mapping-resources-pdf-BSD.txt` (ebenfalls in `cmaps.zip`) | https://github.com/adobe-type-tools/mapping-resources-pdf |
| Handschriften für Unterschriften: Amatic SC, Architects Daughter, Gochi Hand, Patrick Hand (OFL), Coming Soon, Schoolbell (Apache 2.0) | | SIL Open Font License 1.1 bzw. Apache License 2.0 – je eine Datei in `licenses/` | Google Fonts |
| ONNX Runtime (Ausführung der Modelle der Texterkennung, `PdfKnead.Ai`; Telemetrie abgeschaltet: `OnnxRuntimeSetup.cs`) | 1.31.1 | MIT – `licenses/onnxruntime-MIT.txt`, enthaltene Bestandteile `licenses/onnxruntime-ThirdPartyNotices.txt` | https://github.com/microsoft/onnxruntime |
| Farbprofil FOGRA39L coated (CMYK ohne eigenes Profil, Ordner `Farbprofile/`) | 2018-11-05 | zlib/libpng – `licenses/FOGRA39L-zlib.txt` (Kai-Uwe Behrmann, Marti Maria, Photogamut, Graeme Gill, ColorSolutions/basICColor; ICC-Datei von Peter Selinger mit colord erzeugt) | https://ctan.org/pkg/colorprofiles |
| Nachschlagedatei der Erkennung von Personen, Firmen und Anschriften (`PdfKnead.Core/Pdf/Text/Entities/Data/lexikon.br`, gebaut mit `pdf-knead lexikon`, KONZEPT §5.23): Vor- und Nachnamen, gewöhnliche Wörter (Lexeme) | Stand 13.11.2026 | CC0 1.0 – `licenses/Wikidata-CC0.txt` | https://www.wikidata.org |
| – darin Ortsnamen und Länder (cities1000, countryInfo) | Stand 13.11.2026 | **CC BY 4.0, Namensnennung: GeoNames** – `licenses/GeoNames-CC-BY-4.0.txt` | https://www.geonames.org |
| – darin Ländernamen in 29 Sprachen | Stand 13.11.2026 | Unicode License v3 – `licenses/Unicode-CLDR-License-v3.txt` | https://github.com/unicode-org/cldr-json |
| – darin Rechtsformen von Firmen | | MIT, Copyright 2023-2025 OpenSanctions Datenbanken GmbH – `licenses/rigour-MIT.txt` | https://github.com/opensanctions/rigour |
| – darin Titel, Namenszusätze, akademische Grade, Straßenwörter, Wörter für Postfach | | MIT, Copyright 2015 openvenues – `licenses/libpostal-MIT.txt` | https://github.com/openvenues/libpostal |
| E-Rechnung prüfen (Baustein `PdfKnead.EInvoice`, KONZEPT §5.24): Zuordnung der Geschäftsbegriffe der EN 16931 (BT/BG) zu den XML-Pfaden von CII und UBL und ihre Bezeichnungen (`PdfKnead.EInvoice/Zuordnung/*.tsv`, erzeugt mit `erzeugen.py`) | Release 2026-10-13 | Apache License 2.0, Koordinierungsstelle für IT-Standards (KoSIT) – `licenses/xrechnung-visualization-Apache-2.0.txt` | https://github.com/itplr-kosit/xrechnung-visualization |
| – darin welche Geschäftsregel in welchem Profil von ZUGFeRD / Factur-X gilt (`PdfKnead.EInvoice/Zuordnung/profile.tsv`, erzeugt mit `profile.py` aus der Tabelle der Spezifikation) und welche Elemente, Attribute und Codes ein Profil verlangt (`profilstruktur.tsv`, `profilcodes.tsv`, erzeugt mit `profilstruktur.py` aus den Schematron-Prüfregeln der Profile; darin auch die empfohlenen Codes der Artikeleigenschaften von EXTENDED); die Codewerte teils aus UNTDID und UNECE-Empfehlungen (CC BY 4.0, Namensnennung: UNECE, UN/CEFACT) | Factur-X 1.09.2 / ZUGFeRD 2.5.2 (2026-08-04) | Apache License 2.0 (technische Artefakte nach FeRD, Spezifikation laut ihrer Seite 13), FNFE-MPE und FeRD / AWV e.V.; kein Text der Spezifikation wiedergegeben – `licenses/ZUGFeRD-Factur-X-Apache-2.0.txt` | https://www.ferd-net.de |
| – darin die Codelisten der EN 16931 (`PdfKnead.EInvoice/Zuordnung/codes.tsv`, erzeugt mit `codelisten.py`) | 2026-11-15 | **CC BY 4.0, Namensnennung: Europäische Kommission** – `licenses/EU-Codelisten-CC-BY-4.0.txt`; Werte teils aus UNTDID und UNECE Rec 20/21 (**CC BY 4.0, Namensnennung: UNECE, UN/CEFACT**), ISO 3166 und ISO 4217 (frei nutzbar nach der Erklärung der ISO von 2003), ISO/IEC 6523, IANA | https://ec.europa.eu/digital-building-blocks/sites/display/DIGITAL/Registry+of+supporting+artefacts+to+implement+EN16931 |
| – darin die Länder- und Währungsnamen auf Deutsch und Englisch (`PdfKnead.EInvoice/Zuordnung/laender.tsv`, `waehrungen.tsv`, erzeugt mit `codelisten.py --cldr`) | CLDR 49.0.0 | Unicode License v3 – `licenses/Unicode-CLDR-License-v3.txt` | https://github.com/unicode-org/cldr-json |
| – darin die XML-Schemata von UBL 2.1 für die Schemaprüfung (`PdfKnead.EInvoice/Schemata/ubl-2.1/`, Laufzeitfassung, unverändert, ohne die XAdES-Schemata von ETSI) | os-UBL-2.1 (2013-11-04) | OASIS: Kopien erlaubt, unverändert, mit dem Hinweis der OASIS – `licenses/OASIS-UBL-2.1.txt` | http://docs.oasis-open.org/ubl/os-UBL-2.1/ |
| – darin das Schema der XML-Signatur des W3C in der Kopie von UBL 2.1 (`…/ubl-2.1/common/UBL-xmldsig-core-schema-2.1.xsd`) | 2002 | W3C Software License – `licenses/W3C-XML-Signatur-Schema.txt` | https://www.w3.org/TR/xmldsig-core/ |
| – darin die XML-Schemata der Cross Industry Invoice D16B für die Schemaprüfung (`PdfKnead.EInvoice/Schemata/cii-d16b/`, SCRDM-Teilmenge, unverändert) | D16B (2016-10-10) | UN/CEFACT: Kopien erlaubt, unverändert, mit Hinweis – `licenses/UN-CEFACT-CII-Schemata.txt` | https://unece.org/trade/uncefact |
| – darin die Profilschemata von Factur-X / ZUGFeRD für die Schemaprüfung (`PdfKnead.EInvoice/Schemata/facturx-1.09.2/`, unverändert) | Factur-X 1.09.2 / ZUGFeRD 2.5.2 (2026-08-04) | Apache License 2.0, FNFE-MPE und FeRD / AWV e.V. – `licenses/ZUGFeRD-Factur-X-Apache-2.0.txt` | https://www.ferd-net.de |

Die Straßenwörter der US-Post im Code der Erkennung (`EntityRecognizer.cs`, `UsSuffixes`) stammen aus der USPS Publication 28,
Anhang C1 (gemeinfrei). Die Rechtsformen-Liste ISO 20275 (GLEIF) ist nicht enthalten: ihre Lizenz ist ungeklärt, und ohne sie wurde
gleich viel gefunden bei weniger Fehltreffern (tools/ner-messung/LIESMICH.md).

Die Tabelle `PdfKnead.Core/Fonts/Data/glyphorder.bin` (Glyphennummer → Unicode für 15 verbreitete Windows-Schriften, damit
nicht eingebettete Schriften mit Glyphennummern lesbar werden — wie die entsprechenden Tabellen in pdf.js) ist aus den
cmap-Tabellen dieser Schriften abgeleitet; Schriftdaten selbst enthält sie nicht. Installierte Schriften des Rechners werden nur
gelesen, nicht weitergegeben.

Die Tabellen zur Bedeutung von Codes in `PdfKnead.Core/Barcodes/Gs1Tables.cs` (GS1-Nummernkreise der Länderorganisationen,
GS1-Datenbezeichner mit Länge und Bedeutung) sind Tatsachenangaben aus den öffentlichen Spezifikationen von GS1
(https://www.gs1.org/standards/id-keys/company-prefix, GS1 General Specifications); sie sind hier selbst zusammengestellt, keine Datei
und kein Code wurde übernommen. Ebenso folgen die Auswertungen von GiroCode (EPC069-12 des European Payments Council) und
Schweizer QR-Rechnung (Swiss Payment Standards) den veröffentlichten Beschreibungen, ohne fremden Code.

Die Prüfzertifikate der EU-Liste der Vertrauenslisten in `PdfKnead.Core/Pdf/Signatures/TrustLists/lotl-anker.crt` (Stand: Liste Nr. 397
vom 15.10.2026, veröffentlicht im Amtsblatt der EU, C/2026/1944) sind öffentliche Angaben der Europäischen Kommission
(https://ec.europa.eu/tools/lotl/eu-lotl.xml); Weiterverwendung nach dem Beschluss 2011/833/EU über die Weiterverwendung von
Kommissionsdokumenten, Quelle: Europäische Kommission. Das Lesen der Vertrauenslisten (ETSI TS 119 612) und die Prüfung ihrer
XML-Signatur (Exclusive XML Canonicalization, W3C) sind eigener Code nach den Normen, ohne übernommenen Code.

## Übertragener Code

Diese Stellen sind aus frei lizenzierten Projekten übertragen (nach C# übersetzt und angepasst). Alle Lizenzen
erlauben die Nutzung in einem Programm unter eigener Lizenz; verlangt werden Copyright-Vermerk und Lizenztext.

| Stelle | Herkunft | Lizenz |
|---|---|---|
| Starke Flate-Kompression (`PdfKnead.Core/Pdf/Filters/StrongDeflate.cs`: Blockteilung, Kostenanpassung, Mindestlänge, Startkosten, Parameter, Binärbaum-Suche) | libdeflate, Copyright 2016 Eric Biggers, Copyright 2024 Google LLC – https://github.com/ebiggers/libdeflate | MIT – `licenses/libdeflate-MIT.txt` |
| Farbraum Oklab für Sättigung und Dynamik beim Scannen (`PdfKnead.Core/Scanning/Processing/Oklab.cs`, Matrizen) | B. Ottosson, „A perceptual color space for image processing“, 2020 – https://bottosson.github.io/posts/oklab/ | gemeinfrei, wahlweise MIT – `licenses/Oklab-MIT.txt` |

Bis zum 16.11.2026 waren außerdem fünf Stellen übertragen; sie sind seitdem eigener Code, aus der Norm bzw. selbst hergeleitet:
der MQ-Decoder für JBIG2 und JPEG 2000 nach ITU-T T.800, Anhang C.3
(früher aus pdf.js), die Tag-Tree-Dekodierung nach ITU-T T.800, B.10.2 (früher aus OpenJPEG), die CCITT-G4-Kodierung nach ITU-T T.4,
4.2, und T.6 (früher aus libtiff), die Cursive Attachment nach der OpenType-Spezifikation, GPOS (früher aus HarfBuzz) und das
Kantenverfahren der Flächenrasterung als eigene Herleitung (früher aus font-rs). Die Versionsgeschichte wurde am 16.11.2026 bereinigt:
auch ältere Stände enthalten die übertragenen Fassungen nicht mehr, sondern den eigenen Code.

## Nur im Linux-Paket (AppImage-Laufzeit)

Die AppImage beginnt mit der AppImage-Laufzeit (type2-runtime, aus appimagetool). Sie enthält Code aus runtime
(MIT), musl libc (MIT), **libfuse (LGPL-2.1)**, squashfuse (BSD-2-Clause), libzstd (BSD-3-Clause) und zlib;
Einzelheiten, Quellcode-Adressen und der LGPL-Text: `licenses/AppImage-Laufzeit.txt`. Die Laufzeit ist ein
eigenes Programm im Paket; PDF Knead selbst ist nicht gegen libfuse gebunden.

## Hinweise, die die Lizenzen verlangen

- This software is based in part on the work of the Independent JPEG Group (libjpeg-turbo in SkiaSharp).
- Portions of this software are copyright © The FreeType Project (www.freetype.org). All rights reserved.
- This product includes DNG technology under license by Adobe Systems Incorporated (Adobe DNG SDK in SkiaSharp).

## Nur auf Klick geladen: EU-Vertrauensliste (Benutzerordner `eu-vertrauensliste`)

Nicht im Programm und in keinem Paket: die Liste der Vertrauenslisten der EU-Kommission und die Vertrauenslisten der Mitgliedstaaten
(und von Island, Liechtenstein, Norwegen), unverändert von den Adressen, die die Liste der Listen nennt; jede vor der Verwendung an ihrer
Signatur geprüft (`TrustListStore.cs`). Öffentliche Angaben der Kommission und der nationalen Aufsichtsstellen nach Art. 22 der
Verordnung (EU) Nr. 910/2014.

## Im Paket neben dem Programm (Ordner `ocr`)

Seit 13.11.2026 liegen die Modelle der Texterkennung und der Feld-Erkenner im Paket, unverändert so, wie ihre
Herausgeber sie veröffentlicht haben; die Publish-Skripte prüfen jede Datei gegen ihre SHA-256-Prüfsumme (`PdfKnead.Core/Ocr/OcrModels.cs`,
`Modelle/ocr/SHA256SUMS`). PDF-Knead lädt nichts mehr nach.

| Bestandteil | Version | Lizenz | Projekt |
|---|---|---|---|
| PP-OCRv6 small, medium (Erkennen der Textstellen und Lesen, ONNX, je mit Zeichenliste `inference.yml`) | Stand 30.10.2026 (Prüfsummen in `OcrModels.cs`) | Apache License 2.0 – `licenses/PP-OCRv6-Apache-2.0.txt` | https://huggingface.co/PaddlePaddle, https://github.com/PaddlePaddle/PaddleOCR |
| FFDetr, Feld-Erkenner für „Felder erkennen“ (RF-DETR Medium, trainiert auf CommonForms; ONNX in 1024 × 1024, `ffdetr-1024.onnx`; Modell und Trainingsdaten von Joe Barrow, für ONNX umgewandelt von DocSpring) | Stand 31.10.2026 (Prüfsumme in `OcrModels.cs`) | Apache License 2.0 – `licenses/FFDetr-Apache-2.0.txt` | https://huggingface.co/docspring/ffdetr-onnx, https://huggingface.co/jbarrow/FFDetr |

Die Vor- und Nachverarbeitung (Bild skalieren, Kästen aus der Wahrscheinlichkeitskarte, CTC-Dekodierung) ist eigener Code nach den
veröffentlichten Verfahren (DB-Nachbearbeitung, CTC); aus PaddleOCR wurde kein Code übernommen. Ebenso beim Feld-Erkenner (`FfdetrDetector.cs`):
Skalieren, Normieren und Auswerten der Antworten nach der Beschreibung auf der Modellseite, ohne übernommenen Code.

## Neben dem Programm (Ordner `Schriften-CJK`)

| Bestandteil | Version | Lizenz | Projekt |
|---|---|---|---|
| Noto Sans CJK, Noto Serif CJK (Regular, Sammeldateien jp/kr/sc/tc/hk; Ersatz für nicht eingebettete chinesische, japanische, koreanische Schriften) | Sans 2.004, Serif 2.003 | SIL Open Font License 1.1 – `Schriften-CJK/LICENSE-Noto-CJK.txt` | https://github.com/notofonts/noto-cjk |
| Noto Sans Arabic, Noto Naskh Arabic (Regular, Bold; in der Programmdatei, für arabischen Text in Ansichten, die das Programm baut) | Sans 2.013, Naskh 2.021 | SIL Open Font License 1.1 – `licenses/Noto-Arabic-OFL.txt` | https://github.com/notofonts/arabic |
| Unicode Character Database: ArabicShaping.txt (Verbindungsarten arabischer Zeichen, `PdfKnead.Core/Fonts/Data/ArabicShaping.txt`) | 16.0.0 | Unicode License v3 – `licenses/Unicode-CLDR-License-v3.txt` | https://www.unicode.org/Public/16.0.0/ucd/ |

Für die MIT-lizenzierten Bestandteile gilt der folgende Text, jeweils mit dem
Copyright-Vermerk des Projekts (Liste der Vermerke: `licenses/MIT-Bestandteile.txt`):

> Permission is hereby granted, free of charge, to any person obtaining a copy
> of this software and associated documentation files (the "Software"), to deal
> in the Software without restriction, including without limitation the rights
> to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
> copies of the Software, and to permit persons to whom the Software is
> furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all
> copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
> IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
> FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
> AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
> LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
> OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
> SOFTWARE.

## Nicht im Programm: Scanner-Schnittstelle des Systems (zur Laufzeit geladen)

Zum Scannen lädt PDF-Knead unter Linux die SANE-Bibliothek des Systems (`libsane.so.1`, sane-backends, https://sane-project.gitlab.io/,
GPL-2.0 oder neuer mit der Ausnahme der SANE-Autoren, die das Laden durch andere Programme erlaubt). Sie wird **nicht mitgeliefert**; fehlt sie,
nennt das Programm die Installationsbefehle. Angesprochen wird sie nach dem SANE Standard 1.06 (eigener Code: `PdfKnead.Core/Scanning/Sane/`).
Unter Windows spricht PDF-Knead die Scanner-Schnittstelle WIA an, die zu Windows gehört (Dienst „Windows-Bilderfassung“, zur Laufzeit über
COM erreicht, nichts davon mitgeliefert); Schnittstellen und Werte nach den Headern des Windows SDK, eigener Code: `PdfKnead.Core/Scanning/Wia/`.
Das Scanner-Symbol im Menüband stammt aus den Fluent UI System Icons (siehe oben, MIT).

## Vor einer Veröffentlichung prüfen

Die Handschrift-Schriften für Unterschriften (ab M5) sind teils kommerziell
oder nur für den privaten Gebrauch freigegeben — Liste und Lizenzangaben in
`KONZEPT.md` §6.1, Lizenztexte der freien unter `licenses/`. Im Programm stecken
seit 22.10.2026 nur die freien (OFL/Apache 2.0); die übrigen liegen im Ordner
`Schriften-Unterschrift` neben dem Programm und lassen sich dort entfernen. Im
Repository sind sie seit 16.11.2026 nicht mehr, auch nicht in der Versionsgeschichte
(nur lokal beim Entwickler in `Schriften/Handschriften-lizenziert`); Pakete zum
Weitergeben baut der Workflow auf GitHub, wo der Ordner fehlt — ohne sie.
