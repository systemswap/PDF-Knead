using System.Text;
using PdfKnead.Core.Localization;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Cli;
using PdfKnead.Rendering;
using SkiaSharp;

// Command line: inspecting PDF files.
//   pdf-knead info <file> [--passwort <p>]
//   pdf-knead seiten <file> [--passwort <p>]
//   pdf-knead objekt <file> <number> [--daten] [--passwort <p>]
//   pdf-knead bild <file> <page> [--dpi <n>] [--ausgabe <file.png>] [--passwort <p>]
//   pdf-knead text <file> [<page>] [--passwort <p>]   — text of the pages (as used for search and copying)
//   pdf-knead lesbarkeit <file|folder> [--seiten <n>]   — pages with text that cannot be read or reads suspiciously, as „Suchen und schwärzen“ names them
//   pdf-knead uebersprungen <folder>                    — the files the checks pass over, each with its reason (unreadable, encrypted, repaired, no pages; S-C C01)
//   pdf-knead codes <file> [<page>] [--zeit]   — every QR and bar code of the document or page (as the redaction check finds them)
//   pdf-knead code <file> <page> <x> <y>   — the QR or bar code at a point (page space, points): kind, content, meaning, size
//   pdf-knead bilder <file> [<page>] [--ablegen <folder>] [--speichern <folder>] [--passwort <p>]   — image info: format, pixels, colour space, bit depth, resolution on the page, EXIF, origin (--ablegen: JPEGs and data for comparison; --speichern: every image as „Bild speichern“ writes it)
//   pdf-knead verborgen <file> [<page>] [--passwort <p>]   — spots of hidden text as "redact hidden content" finds them
//   pdf-knead bloecke <file> [<page>] [--ohne-text] [--passwort <p>]   — text blocks as "edit text" detects them (--ohne-text: content masked)
//   pdf-knead felder <file> [--passwort <p>]   — form fields and labels per page (kind, name, value, position, properties)
//   pdf-knead messen <file> [--dpi <n>] [--passwort <p>]   — times per page
//   pdf-knead leistung <file|folder> [--seiten <n>] [--dpi <n>] [--csv <file>] [--operatoren] [--allokationen] [--pruefsummen <file>] — whole chain over many files
//   pdf-knead speichern <file> <output> [--version 1.7] [--seiten 1-3,7] [--passwort <p>] — rewrite (also a page selection)
//   pdf-knead zusammenfuegen <output> <file>[:1-3] … [--neu] [--version 2.0] — files (or pages from them) one after another; --neu: on an
//     empty document, as „als neue Datei“ without an open document does (else on the first file); --version: the PDF version written (1.7)
//   pdf-knead speichertest <file|folder> [--version 1.7] [--seiten <n>] [--ablegen <folder>] [--rueckwaerts] [--allokationen] [--zweimal] [--flachlegen] [--bereinigen [--hinweise] [--struktur]] [--schwaerzen [--als-bild]] [--verborgen] [--anmerkungen] [--formulare [--signiert]] [--entsperren] [--textbearbeiten] [--verborgen-schwaerzen] [--ocr-bereinigen] [--codes-aendern] [--zusammenfuehren] [--bilder] [--bildteile] [--feldwerte] [--pdfa] [--komprimieren verlustfrei|bildschirm|ebook|druck[-fotos|-mrc][-g4][-stark][-brotli]] [--alsbild jpeg|kompakt] [--datenerhalt [--arlington <tsv folder>]] — write, read back, compare (--datenerhalt: what PDF-Knead does not know — keys outside ISO 32000 by the Arlington tables, /Private — must stand in the copy unchanged, keys of the standard that differ are counted by kind; --als-bild: the redacted pages saved as their images as „zusätzlich als Bild“ does it, the pages not compared; --hinweise: clean up the notices in fonts and colour profiles as well; --struktur: „Bereinigen: Struktur“ as well (tree, replacement texts and MCIDs in the content); --anmerkungen: move and delete foreign annotations; --formulare: fill all fields, with --signiert signed first and the filling appended; --entsperren: protected files only, remove restrictions; --textbearbeiten: replace blocks, the old text must be gone from the file; --verborgen-schwaerzen: mark and remove all hidden text, nothing of it may remain; --ocr-bereinigen: remove the invisible text as „Dokument bereinigen“ does, nothing of it may remain; --codes-aendern: change every code found as „Code ändern …“ does, the old contents must be gone; --zusammenfuehren: the files in pairs merged into the first and as a new file, pages, fields, bookmarks and the notes „Danach nicht mehr da“ checked; --bilder: every picture and graphic moved, moved back, saved, one group deleted, one picture cut, as „Bilder bearbeiten“ does; --bildteile: clean up „Verdeckte Teile von Bildern“, the pages must look the same and no hidden part remain; --feldwerte: name the files with form fields that show something other than their value, clean them up, none may remain and every value stay; --pdfa [--teil 1a|1b|2a|2b|2u|3a|3b|3u|4|4f|4e]: written as PDF/A (2b without --teil), the pages must look the same, conformance counted by rule)
//   pdf-knead schrift <file.ttf|name|alle> [<text>] — tables, OpenType features and shaped text
//   pdf-knead herkunft <file> — verify the provenance records of signatures (device identity)
//   pdf-knead schwaerzpruefung erzeugt <count> [--start <n>] | bildfallen <count> [--start <n>] [--modelle <folder>] | docpii <count> [--start <n>] [--daten <json>] | <folder> [--seiten <n>] [--ablegen <folder>] [--laengen] — redact generated cases or real
//                                files as the program does and check the rules of redaction (RedactionCheck; docs/PRUEFUNGEN.md, section 6j);
//                                <folder> --mausplan: plans to redact its cases with the mouse; <folder> --ergebnisse <folder>: check their results;
//                                --verborgenes: hidden content and the old text recognition go along; --neue-ocr <models> [--groesse …]: then a new
//                                text recognition from what stays visible (no redacted word may come back); --gruende: with reasons in the bars;
//                                --struktur: generated cases tagged, with descriptions in the structure tree (on a page with a bar they go);
//                                --fremd [<folder>]: every result also through other readers (pdftotext, gs, mutool, pdftoppm, pdffonts from the
//                                folder or the PATH): no word that must go, the same picture outside the bars, dark bars, fonts as before
//                                and a protocol after the last page (B-14)
//   pdf-knead signaturen <file|folder> [--passwort <p>] — check the digital signatures (byte ranges, CMS, certificate, later revisions)
//   pdf-knead signiertest <file|folder> [--ablegen <folder>] [--dateien <n>] — sign in three steps as the program saves, check each result
//   pdf-knead lecksuche <file> <word> … [--passwort <p>] — does a word still appear anywhere in the file (objects, raw streams, page text)?
//   pdf-knead texterkennung <file> [<page>] [--groesse small|medium] [--modelle <folder>] [--dpi <n>] [--woerter] [--alle] [--ablegen <file>]
//                                [--threads <n>] [--stapel <n>] [--arena] [--maxseite <n>] — recognise the text of pages (docs/PRUEFUNGEN.md, section 6o): lines, times,
//                                peak memory; --ablegen writes the document with the new text layers
//   pdf-knead formularerkennung <file|folder> [--einzeln] [--modelle <folder>] [--ohne-erkenner] [--scan] [--schwelle <s>] — the fields the
//                                form recognition proposes; for files that are forms, their own fields (which the recognition does not see)
//                                are the expectation: found, too many, kind right; with the models also text recognition of scans and the
//                                field detector; --scan: an image copy of each form instead (JPEG, 200 dpi)
//   pdf-knead pdfa <file> <output> [--teil 1a|1b|2a|2b|2u|3a|3b|3u|4|4f|4e] [--trotzdem] [--passwort <p>] [--sprache <tag>] [--schmuck] [--alt <key>=<text>] — save as PDF/A (level a: language, figures as decoration or with a text; the keys as „pdfa-bilder“ lists them); written only if the check finds nothing (fonts without a readable program are embedded as their substitute and named)
//                                (--trotzdem: always, for analysis), the violations are listed
//   pdf-knead pdfa-bilder <file> — level a: the proposed language and the figures that need an alternative text (key, page, place)
//   pdf-knead pdfa-struktur <file> — the structure tree of a file (types, pages, marked contents, alternative texts)
//   pdf-knead pdfa-schriften <file> — the fonts of the pages as PDF/A looks at them: program, cmaps, encoding, codes whose glyph is missing or whose widths differ
//   pdf-knead barrierefrei <file|folder> [--profil ua1|ua2|behoerde|hinweise] [--json] [--einzeln] — the check „barrierefrei?“ (stages built so far); a folder of the reference test files is measured per rule
//   pdf-knead pdfa-pruefen <file|folder> [--einzeln] — check against the part the file declares (PDF/A-1b … 4e; 2b without one); a folder of test files with „pass“/„fail“ in their names is measured
//   pdf-knead codemuster <folder> — every kind of code PDF Knead makes, in four turns, with „erwartet.tsv“ (check with an independent reader)
//   pdf-knead modelle [<folder>|programm] — which models of the text recognition and the field detector are there, each checked by its
//                                checksum (they come with the program; nothing is loaded)
//   pdf-knead lexikon <listen> <ausgabe.br> — the lexicon of the recognition of persons, companies and addresses from the source lists
//   pdf-knead namen <datei.txt> [--orte-ueberall] [--gruendlich] — persons, companies, parts of addresses in a text file
//   pdf-knead namen-messen <saetze.txt|docpii.json> [--sicher] [--orte-ueberall] [--gruendlich] [--einzeln] — measured as tools/ner-messung/messen.py

Tr.Use(Languages.ForSystem(Languages.All(out _)));
TextVisibility.DefaultProbe = VisibilityProbe.Instance;   // hidden text is measured by drawing
PdfKnead.Core.Pdf.Writing.PdfAFlattening.Rasterizer = AreaRasterizer.Instance;   // PDF/A-1: what is translucent is drawn as an image
Console.OutputEncoding = Encoding.UTF8;
// Text from untrusted files (page text, title, provenance, objects) can contain control characters — escape sequences such as
// OSC 52 would otherwise set the clipboard in the terminal or garble the display
Console.SetOut(new SafeConsoleWriter(Console.Out));

if (args.Length < 2)
{
    Console.WriteLine(Tr.T("Aufruf:"));
    Console.WriteLine("  pdf-knead info <datei> [--passwort <p>]");
    Console.WriteLine("  pdf-knead seiten <datei> [--passwort <p>]");
    Console.WriteLine("  pdf-knead objekt <datei> <nummer> [--daten] [--passwort <p>]");
    Console.WriteLine("  pdf-knead bild <datei> <seite> [--dpi <n>] [--ausgabe <datei.png>] [--passwort <p>]");
    Console.WriteLine("  pdf-knead text <datei> [<seite>] [--passwort <p>]");
    Console.WriteLine("  pdf-knead bilder <datei> [<seite>] [--ablegen <ordner>] [--speichern <ordner>] [--passwort <p>]");
    Console.WriteLine("  pdf-knead code <datei> <seite> <x> <y> [--anteil] [--bild <png>]");
    Console.WriteLine("  pdf-knead codes <datei> [<seite>] [--zeit] [--bild <png>]");
    Console.WriteLine("  pdf-knead verborgen <datei> [<seite>] [--passwort <p>]");
    Console.WriteLine("  pdf-knead bloecke <datei> [<seite>] [--ohne-text] [--passwort <p>]");
    Console.WriteLine("  pdf-knead messen <datei> [--dpi <n>] [--passwort <p>]");
    Console.WriteLine("  pdf-knead leistung <datei|ordner> [--seiten <n>] [--dpi <n>] [--csv <datei>] [--operatoren] [--allokationen]");
    Console.WriteLine("  pdf-knead speichern <datei> <ausgabe> [--version 1.7] [--seiten 1-3,7] [--passwort <p>]");
    Console.WriteLine("  pdf-knead zusammenfuegen <ausgabe> <datei>[:1-3] … [--neu] [--version 2.0]");
    Console.WriteLine("  pdf-knead speichertest <datei|ordner> [--version 1.7] [--seiten <n>]");
    Console.WriteLine("  pdf-knead schwaerzpruefung erzeugt <anzahl> [--start <n>] | bildfallen <anzahl> [--modelle <ordner>] | <ordner> [--seiten <n>]  [--ablegen <ordner>] [--laengen]");
    Console.WriteLine("  pdf-knead laengenangriff <anzahl> [--start <n>] [--ohne-laengen] [--stufe <geviert>] [--ablegen <ordner>]");
    Console.WriteLine("  pdf-knead schwaerzpruefung <ordner> --mausplan | --ergebnisse <ordner> [--laengen] [--nur-balken]");
    Console.WriteLine("  pdf-knead schrift <datei.ttf|Name|alle> [<text>]");
    Console.WriteLine("  pdf-knead herkunft <datei> [--passwort <p>]");
    Console.WriteLine("  pdf-knead signaturen <datei|ordner> [--passwort <p>] [--bericht text|html]");
    Console.WriteLine("  pdf-knead release-schluessel <ordner>   (einmalig: Schlüsselpaar für die Updates)");
    Console.WriteLine("  pdf-knead release-signieren <paketordner> --schluessel <geheim.pem> [--hinweise <datei>] [--einfuehrung-bis <yyyy-MM-dd>]");
    Console.WriteLine("  pdf-knead release-pruefen <ordner>             version.json, Signatur und Pakete prüfen wie die Update-Prüfung");
    Console.WriteLine("  pdf-knead signiertest <datei|ordner> [--ablegen <ordner>] [--dateien <n>]");
    return 2;
}

var command = args[0].ToLowerInvariant();
var pdfAPart = PdfAParts.FromName(Array.IndexOf(args, "--teil") is var teilAt and >= 0 && teilAt + 1 < args.Length ? args[teilAt + 1] : null) ?? PdfAConformance.A2b;
PdfKnead.Cli.SaveCheck.PdfAPart = pdfAPart;
PdfKnead.Cli.SaveCheck.Protect = args.Contains("--schuetzen");
PdfKnead.Cli.PdfAToolCheck.Accessibility = PdfKnead.Cli.PdfAToolCheck.AccessibilityFrom(args);
var file = args[1];

// Performance over many files: handled before opening a single file (the path may be a folder)
if (command is "leistung" or "benchmark")
{
    string? Arg(string name) => Array.IndexOf(args, name) is var k and >= 0 && k + 1 < args.Length ? args[k + 1] : null;
    var pages = int.TryParse(Arg("--seiten"), out var p) && p > 0 ? p : 20;
    var benchDpi = double.TryParse(Arg("--dpi"), System.Globalization.CultureInfo.InvariantCulture, out var bd) && bd > 0 ? bd : 96;
    return PdfKnead.Cli.Benchmark.Run(file, pages, benchDpi, Arg("--csv"), args.Contains("--operatoren"), args.Contains("--allokationen"), Arg("--pruefsummen"));
}
if (command is "lesbarkeit")
{
    string? Arg(string name) => Array.IndexOf(args, name) is var k and >= 0 && k + 1 < args.Length ? args[k + 1] : null;
    return PdfKnead.Cli.ReadabilityCheck.Run(file, int.TryParse(Arg("--seiten"), out var rp) && rp > 0 ? rp : 20);
}
if (command is "uebersprungen" or "skipped") return PdfKnead.Cli.SkippedFiles.Run(file);
// persons, companies and addresses (KONZEPT.md, section 5.23): build the lexicon, recognise a text, measure as tools/ner-messung/messen.py
if (command is "lexikon" && args.Length >= 3)
{
    PdfKnead.Cli.EntityTools.WithPostcodeFormats = args.Contains("--plz-formate");
    return PdfKnead.Cli.EntityTools.BuildLexicon(file, args[2]);
}
if (command is "namen" or "namen-messen")
{
    var entityOptions = new PdfKnead.Core.Pdf.Text.Entities.EntityOptions(args.Contains("--orte-ueberall"), args.Contains("--gruendlich"));
    if (command is "namen") return PdfKnead.Cli.EntityTools.Recognize(file, entityOptions);
    var isJson = file.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    return PdfKnead.Cli.EntityTools.Measure(isJson ? null : file, isJson ? file : null, args.Contains("--sicher"), entityOptions, args.Contains("--einzeln"));
}
if (command is "jpegvergleich" or "jpegcompare") return PdfKnead.Cli.JpegCompare.Run(file, Array.IndexOf(args, "--zeigen") is var zi and >= 0 && zi + 1 < args.Length && int.TryParse(args[zi + 1], out var zn) ? zn : 20);
if (command is "speichertest")
{
    string? Arg(string name) => Array.IndexOf(args, name) is var k and >= 0 && k + 1 < args.Length ? args[k + 1] : null;
    return PdfKnead.Cli.SaveCheck.Run(file, Arg("--version") ?? "1.7", int.TryParse(Arg("--seiten"), out var sp) && sp > 0 ? sp : 20, args.Contains("--rueckwaerts"), Arg("--ablegen"), args.Contains("--allokationen"), args.Contains("--zweimal"), args.Contains("--flachlegen"), args.Contains("--bereinigen"), args.Contains("--schwaerzen"), args.Contains("--verborgen"), args.Contains("--anmerkungen"), args.Contains("--formulare"), Arg("--komprimieren"), Arg("--alsbild"), args.Contains("--entsperren"), args.Contains("--textbearbeiten"), args.Contains("--signiert"), args.Contains("--verborgen-schwaerzen"), args.Contains("--ocr-bereinigen"), args.Contains("--codes-aendern"), args.Contains("--zusammenfuehren"), args.Contains("--hinweise"), args.Contains("--bilder"), args.Contains("--bildteile"), args.Contains("--feldwerte"), args.Contains("--pdfa"), args.Contains("--seiten-loeschen"), args.Contains("--neue-anmerkungen"), args.Contains("--struktur"), args.Contains("--als-bild"), args.Contains("--datenerhalt"), Arg("--arlington"));
}
if (command is "vorgetaeuscht" or "fakeredaction")
{
    // pdf-knead vorgetaeuscht <datei|ordner> [--seiten <n>]: text that only looks redacted (KONZEPT.md, section 5.6, „Vorgetäuschte Schwärzung“)
    string? Arg(string name) => Array.IndexOf(args, name) is var k and >= 0 && k + 1 < args.Length ? args[k + 1] : null;
    var maxPages = int.TryParse(Arg("--seiten"), out var vp) && vp > 0 ? vp : int.MaxValue;
    var files = Directory.Exists(file) ? Directory.EnumerateFiles(file, "*.pdf", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList() : [file];
    var watch = System.Diagnostics.Stopwatch.StartNew();
    int documents = 0, pagesChecked = 0, spotsFound = 0, failed = 0;
    var images = new PdfKnead.Rendering.ImageCache();
    foreach (var path in files)
    {
        try
        {
            using var checkedDoc = PdfDocument.Open(path);
            documents++;
            for (var i = 0; i < Math.Min(checkedDoc.PageCount, maxPages); i++)
            {
                pagesChecked++;
                foreach (var spot in PdfKnead.Rendering.FakeRedactions.Find(checkedDoc, checkedDoc.Pages[i], images))
                {
                    spotsFound++;
                    var text = spot.Text.ReplaceLineEndings(" ");
                    Console.WriteLine($"{Path.GetFileName(path)}\t{i + 1}\t{spot.Kind}\t{(text.Length > 80 ? text[..80] + "…" : text)}");
                }
            }
        }
        catch (Exception e) when (e is PdfException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            failed++;
        }
    }
    Console.WriteLine(Tr.T("{0} Dateien, {1} Seiten, {2} Stellen, {3} nicht lesbar, {4:0.0} s", documents, pagesChecked, spotsFound, failed, watch.Elapsed.TotalSeconds));
    return spotsFound > 0 ? 3 : 0;
}
if (command is "laengenangriff")
{
    // pdf-knead laengenangriff <anzahl> [--start <n>] [--ohne-laengen] [--ablegen <ordner>]: the attack on glyph positions against hiding the
    // lengths of redacted words, measured on generated pages (docs/PRUEFUNGEN.md, section 6j)
    string? Arg(string name) => Array.IndexOf(args, name) is var k and >= 0 && k + 1 < args.Length ? args[k + 1] : null;
    return PdfKnead.Cli.LengthAttack.Run(int.TryParse(file, out var cases) && cases > 0 ? cases : 200, int.TryParse(Arg("--start"), out var from) ? from : 1,
        !args.Contains("--ohne-laengen"), Arg("--ablegen"), double.TryParse(Arg("--stufe"), System.Globalization.CultureInfo.InvariantCulture, out var stepEm) && stepEm > 0 ? stepEm : null);
}
if (command is "schwaerzpruefung")
{
    // pdf-knead schwaerzpruefung erzeugt <anzahl> [--start <n>] | docpii <anzahl> [--start <n>] [--daten <json>] | <ordner> [--seiten <n>] — [--ablegen <ordner>]
    string? Arg(string name) => Array.IndexOf(args, name) is var k and >= 0 && k + 1 < args.Length ? args[k + 1] : null;
    var generated = file == "erzeugt";
    // with the defaults of the program: hidden content and the old text recognition go along (KONZEPT.md, section 5.6)
    PdfKnead.Cli.RedactionCheck.CleanHidden = args.Contains("--verborgenes");
    PdfKnead.Cli.RedactionCheck.Reasons = args.Contains("--gruende");
    // generated cases tagged, with descriptions in the structure (docs/FEHLERGEDAECHTNIS.md, No. 618)
    PdfKnead.Cli.RedactionCheck.Structure = args.Contains("--struktur");
    // other readers check every result as well (local tools, never part of the program): text, pictures, fonts
    if (args.Contains("--fremd")) PdfKnead.Cli.ForeignReaders.Folder = Arg("--fremd") is { } tools && !tools.StartsWith("--", StringComparison.Ordinal) ? tools : "";
    // and then a new text recognition from what stays visible (KONZEPT.md, section 5.6, step c): --neue-ocr <models> [--groesse small|medium]
    if (Array.IndexOf(args, "--neue-ocr") is var no and >= 0 && no + 1 < args.Length)
    {
        PdfKnead.Cli.RedactionCheck.CleanHidden = true;
        var ocrSize = Array.IndexOf(args, "--groesse") is var gi and >= 0 && gi + 1 < args.Length &&
                      Enum.TryParse<PdfKnead.Core.Ocr.OcrModelSize>(args[gi + 1], true, out var gs) ? gs : PdfKnead.Core.Ocr.OcrModelSize.Small;
        var key = PdfKnead.Core.Ocr.OcrModels.Key(ocrSize);
        PdfKnead.Cli.RedactionCheck.NewOcr = new PdfKnead.Ai.PaddleOcr(Path.Combine(args[no + 1], $"PP-OCRv6_{key}_det_onnx"), Path.Combine(args[no + 1], $"PP-OCRv6_{key}_rec_onnx"));
    }
    // scanned pages and the copies an image leaves behind (docs/PRUEFUNGEN.md, section 6j „Bildfallen“); with --modelle the bars come from a
    // text recognition, as when a user marks recognised text
    if (file == "bildfallen")
    {
        PdfKnead.Core.Ocr.ITextRecognizer? read = null;
        if (Arg("--modelle") is { } models)
        {
            var size = Enum.TryParse<PdfKnead.Core.Ocr.OcrModelSize>(Arg("--groesse"), true, out var bs) ? bs : PdfKnead.Core.Ocr.OcrModelSize.Small;
            var key = PdfKnead.Core.Ocr.OcrModels.Key(size);
            read = new PdfKnead.Ai.PaddleOcr(Path.Combine(models, $"PP-OCRv6_{key}_det_onnx"), Path.Combine(models, $"PP-OCRv6_{key}_rec_onnx"));
        }
        using (read)
            return PdfKnead.Cli.ImageTrapCases.Run(args.Length > 2 && int.TryParse(args[2], out var traps) ? traps : 100,
                int.TryParse(Arg("--start"), out var trapStart) ? trapStart : 1, Arg("--ablegen"), read);
    }
    // cases from the DocPII texts (docs/PRUEFUNGEN.md, section 6j „DocPII“)
    if (file == "docpii")
        return PdfKnead.Cli.DocPiiCases.Run(Arg("--daten") ?? Path.Combine("TestPDFs", "docpii", "docPII-contextual-redaction-benchmark.json"),
            args.Length > 2 && int.TryParse(args[2], out var pii) ? pii : 100, int.TryParse(Arg("--start"), out var piiStart) ? piiStart : 1,
            Arg("--ablegen"), args.Contains("--laengen"));
    var amount = generated && args.Length > 2 && int.TryParse(args[2], out var n) ? n : 200;
    var start = int.TryParse(Arg("--start"), out var st) ? st : 1;
    var redactPasswords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var passwordList = generated ? "" : Path.Combine(file, "_passwoerter.txt");
    if (!generated && File.Exists(passwordList))
        foreach (var line in File.ReadAllLines(passwordList))
            if (line.Split('\t') is [var pdfName, var pw]) redactPasswords[pdfName] = pw;
    // only the bars of a case (comparing with other programs, which know no marks over hidden content)
    PdfKnead.Cli.RedactionCheck.OnlyBars = args.Contains("--nur-balken");
    // the same cases redacted in the program with the mouse: plans for the driver, then their results checked (docs/PRUEFUNGEN.md, section 6j)
    if (!generated && args.Contains("--mausplan")) return PdfKnead.Cli.RedactionMousePlan.Run(file, redactPasswords);
    if (!generated && Arg("--ergebnisse") is { } results) return PdfKnead.Cli.RedactionCheck.RunResults(file, results, redactPasswords, args.Contains("--laengen"));
    return PdfKnead.Cli.RedactionCheck.Run(generated ? null : file, amount, start, Arg("--ablegen"), redactPasswords,
        int.TryParse(Arg("--seiten"), out var rp) && rp > 0 ? rp : 3, args.Contains("--laengen"));
}
if (command is "formularerkennung")
{
    string? Arg(string name) => Array.IndexOf(args, name) is var k and >= 0 && k + 1 < args.Length ? args[k + 1] : null;
    return PdfKnead.Cli.FormRecognitionCheck.Run(file, args.Contains("--einzeln"), new PdfKnead.Cli.FormRecognitionCheck.Settings(
        Arg("--modelle"), !args.Contains("--ohne-erkenner"), args.Contains("--scan"),
        Arg("--schwelle") is { } threshold ? double.Parse(threshold, System.Globalization.CultureInfo.InvariantCulture) : new PdfKnead.Ai.FfdetrOptions().MinScore));
}
if (command is "pdfa-pruefen")
    return PdfKnead.Cli.PdfAToolCheck.Check(file, args.Contains("--einzeln"));
if (command is "barrierefrei-vergleich" && args.Length >= 3)
{
    var compareProfile = Array.IndexOf(args, "--profil") is var cp and >= 0 && cp + 1 < args.Length && args[cp + 1] == "ua2"
        ? PdfKnead.Core.Pdf.Accessibility.AccessibilityProfile.PdfUa2
        : PdfKnead.Core.Pdf.Accessibility.AccessibilityProfile.PdfUa1;
    return PdfKnead.Cli.AccessibilityRoundTrip.Run(args[1], args[2], compareProfile, args.Contains("--einzeln"));
}
if (command is "barrierefrei")
{
    var profileName = Array.IndexOf(args, "--profil") is var bp and >= 0 && bp + 1 < args.Length ? args[bp + 1] : null;
    PdfKnead.Core.Pdf.Accessibility.AccessibilityProfile? profile = profileName switch
    {
        "ua1" => PdfKnead.Core.Pdf.Accessibility.AccessibilityProfile.PdfUa1,
        "ua2" => PdfKnead.Core.Pdf.Accessibility.AccessibilityProfile.PdfUa2,
        "behoerde" => PdfKnead.Core.Pdf.Accessibility.AccessibilityProfile.Authority,
        "hinweise" => PdfKnead.Core.Pdf.Accessibility.AccessibilityProfile.HintsOnly,
        _ => null,
    };
    return PdfKnead.Cli.AccessibilityToolCheck.Run(file, profile, args.Contains("--json"), args.Contains("--einzeln"));
}
if (command is "pdfa-schriften")
    return PdfKnead.Cli.PdfAFontReport.Run(file);
if (command is "pdfa-bilder")
    return PdfKnead.Cli.PdfAToolCheck.Figures(file);
if (command is "pdfa-struktur")
    return PdfKnead.Cli.PdfAToolCheck.Structure(file);
if (command is "pdfa" && args.Length >= 3)
    return PdfKnead.Cli.PdfAToolCheck.Convert(file, args[2],
        pdfAPart,
        args.Contains("--trotzdem"), Array.IndexOf(args, "--passwort") is var pp and >= 0 && pp + 1 < args.Length ? args[pp + 1] : null);
if (command is "lecksuche")
{
    var leakPassword = Array.IndexOf(args, "--passwort") is var lp and >= 0 && lp + 1 < args.Length ? args[lp + 1] : null;
    var words = new List<string>();
    for (var w = 2; w < args.Length; w++)
        if (args[w] == "--passwort") w++;
        else words.Add(args[w]);
    return PdfKnead.Cli.LeakSearch.Run(file, words, leakPassword);
}
if (command is "release-schluessel") return PdfKnead.Cli.ReleaseTools.CreateKey(file);
if (command is "release-pruefen") return PdfKnead.Cli.ReleaseTools.Verify(file);
if (command is "release-signieren")
{
    string? ReleaseArg(string name) => Array.IndexOf(args, name) is var k and >= 0 && k + 1 < args.Length ? args[k + 1] : null;
    return PdfKnead.Cli.ReleaseTools.Sign(file, ReleaseArg("--schluessel"), ReleaseArg("--hinweise"), ReleaseArg("--einfuehrung-bis"));
}
if (command is "signiertest")
{
    string? Arg(string name) => Array.IndexOf(args, name) is var k and >= 0 && k + 1 < args.Length ? args[k + 1] : null;
    return PdfKnead.Cli.SignCheck.Run(file, Arg("--ablegen"), int.TryParse(Arg("--dateien"), out var count) && count > 0 ? count : int.MaxValue);
}
if (command is "signaturen" or "signatures")
{
    var sigPassword = Array.IndexOf(args, "--passwort") is var sigPw and >= 0 && sigPw + 1 < args.Length ? args[sigPw + 1] : null;
    if (Array.IndexOf(args, "--bericht") is var report and >= 0)
    {
        Console.Write(PdfKnead.Cli.SignatureList.Report(file, sigPassword, report + 1 < args.Length && args[report + 1] == "html"));
        return 0;
    }
    return PdfKnead.Cli.SignatureList.Run(file, sigPassword);
}
if (command is "codemuster")
    return PdfKnead.Cli.CodeSamples.Run(file);
if (command is "schrift" or "font")
    return FontInfo(file, args.Length >= 3 && !args[2].StartsWith("--", StringComparison.Ordinal) ? args[2] : null);
if (command is "zusammenfuegen" or "merge")
{
    var versionAt = Array.IndexOf(args, "--version");
    var mergeVersion = versionAt >= 0 && versionAt + 1 < args.Length ? args[versionAt + 1] : "1.7";
    return Merge(file, args.Skip(2).Where((a, i) => !a.StartsWith("--", StringComparison.Ordinal) && (versionAt < 0 || i + 2 != versionAt + 1)).ToList(),
        args.Contains("--neu"), mergeVersion);
}
if (command is "modelle" or "models")
    return PdfKnead.Cli.ModelCheck.Run(file == "programm" ? null : file);
if (command is "erechnung") return PdfKnead.EInvoice.EInvoiceCommand.Run(file, args);   // Andockstelle E-Rechnung (KONZEPT §5.24)
var passwordIndex = Array.IndexOf(args, "--passwort");
var password = passwordIndex >= 0 && passwordIndex + 1 < args.Length ? args[passwordIndex + 1] : null;
string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
var dpi = double.TryParse(Option("--dpi"), System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0 ? d : 96;

try
{
    using var doc = PdfDocument.Open(file, password);
    switch (command)
    {
        case "info":
            Info(doc);
            return 0;
        case "seiten" or "pages":
            foreach (var page in doc.Pages)
                Console.WriteLine($"{page.Number,5}  {PageFormat.Describe(page.Width, page.Height)}{(page.Rotate != 0 ? $"  ↻{page.Rotate}°" : "")}");
            return 0;
        case "objekt" or "object" when args.Length >= 3 && int.TryParse(args[2], out var number):
            var obj = doc.GetObject(number);
            Console.WriteLine(obj);
            if (args.Contains("--daten") && obj is PdfStream stream)
            {
                var result = doc.DecodeStream(stream);
                Console.WriteLine();
                Console.WriteLine(result.ImageFilter is { } image
                    ? Tr.T("({0} Bytes im Format {1})", result.Data.Length, image.Value)
                    : Encoding.Latin1.GetString(result.Data));
            }
            return 0;
        case "herkunft" or "provenance":
            var seals = SealRecord.Read(doc);
            if (seals.Count == 0) Console.WriteLine(Tr.T("Keine Herkunftsnachweise von Unterschriften."));
            // Stated honestly: what is verified is that the signature matches the key stored in the file. Who owns that key is
            // shown only by comparing its fingerprint with known keys (here: the key of this device).
            var mine = PdfKnead.Core.Platform.DeviceIdentity.ExistingPublicKey();
            foreach (var (page, seal) in seals)
            {
                var ok = seal.Verify();
                var fromHere = mine is not null && mine.AsSpan().SequenceEqual(seal.PublicKey);
                Console.WriteLine($"{Tr.T("Seite")} {page}: {seal.Origin}");
                Console.WriteLine($"   {Tr.T("Kennung")} {seal.Code} – {(ok ? Tr.T("Signatur passt zum mitgelieferten Schlüssel") : Tr.T("Signatur UNGÜLTIG"))}");
                Console.WriteLine($"   {Tr.T("Schlüssel {0}", PdfKnead.Core.Platform.DeviceIdentity.Fingerprint(seal.PublicKey))} – {(fromHere ? Tr.T("von diesem Gerät") : Tr.T("nicht von diesem Gerät"))}");
            }
            if (seals.Count > 0)
                Console.WriteLine(Tr.T("Wem ein Schlüssel gehört, zeigt der Vergleich seines Fingerabdrucks mit bekannten Schlüsseln."));
            return seals.All(s => s.Seal.Verify()) ? 0 : 3;
        case "felder" or "fields":
            // Form fields and labels per page, one line per widget (for test runs): order = tab order
            var acroForm = doc.Catalog?.GetDictionary(PdfName.Get("AcroForm"));
            static string Hex(int? rgb) => rgb is { } c ? $"#{c:X6}" : "-";
            static string Clean(string s) => s.Replace("\n", "\\n").Replace("\r", "\\r");
            var formPages = PageItem.From(doc);
            for (var i = 0; i < formPages.Length; i++)
                foreach (var element in formPages[i].Elements)
                {
                    var r = element.Rect;
                    var at = $"[{r.Left:0.#} {r.Bottom:0.#} {r.Right:0.#} {r.Top:0.#}]";
                    if (element is ForeignAnnotation foreign && FormField.Of(foreign) is { } field && FieldSpec.Of(foreign) is { } spec)
                        Console.WriteLine($"S{i + 1} {spec.Kind} \"{Clean(field.Name)}\" wert=\"{Clean(field.StoredValue)}\" zustand={field.State} {at} " +
                                          $"rahmen={Hex(spec.BorderColor)} fuellung={Hex(spec.FillColor)} schrift={spec.Font}/{spec.FontSize:0.#}/{Hex(spec.TextColor)} " +
                                          $"pflicht={spec.Required} nurlesen={spec.ReadOnly} tip=\"{Clean(spec.Tooltip)}\" format={spec.Format.Kind}{(spec.Format.Pattern.Length > 0 ? ":" + spec.Format.Pattern : "")} " +
                                          $"berechnung={(spec.Calculation is { Formula: { } f } ? "Formel(" + f.Notation + ")" : spec.Calculation is { } c ? c.Operation + "(" + string.Join(",", c.Fields) + ")" : "-")} " +
                                          $"export={(spec.Kind is FormFieldKind.CheckBox or FormFieldKind.Radio ? field.OnState : "-")} optionen={spec.Options.Count} " +
                                          $"mehrfach={spec.MultiSelect} zeichen={spec.CheckStyle}" +
                                          (spec.Code is { } code ? $" code={code.Format}{(code.Gs1 ? "/GS1" : "")}" : ""));
                    else if (element is TextElement { LabelFor.Length: > 0 } label)
                        Console.WriteLine($"S{i + 1} Beschriftung \"{Clean(label.Text)}\" fuer=\"{Clean(label.LabelFor)}\"{(label.LabelState.Length > 0 ? "/" + label.LabelState : "")} {at} " +
                                          $"schrift={label.Font}/{label.FontSize:0.#}/{Hex(label.Color)}");
                }
            Console.WriteLine($"AcroForm: Felder={acroForm?.GetArray(PdfName.Get("Fields"))?.Count ?? 0} CO={acroForm?.GetArray(PdfName.Get("CO"))?.Count ?? 0} XFA={acroForm?.ContainsKey(PdfName.Get("XFA")) == true}");
            return 0;
        case "text":
            var onlyPage = args.Length >= 3 && int.TryParse(args[2], out var tp) ? tp : 0;
            if (onlyPage != 0 && (onlyPage < 1 || onlyPage > doc.PageCount))
            {
                Console.Error.WriteLine(Tr.T("Seite {0} gibt es nicht (1 bis {1}).", onlyPage, doc.PageCount));
                return 1;
            }
            for (var i = 0; i < doc.PageCount; i++)
            {
                if (onlyPage != 0 && i + 1 != onlyPage) continue;
                if (onlyPage == 0) Console.WriteLine(Tr.T("--- Seite {0} ---", i + 1));
                Console.WriteLine(PdfKnead.Core.Pdf.Text.PageText.Create(PageInterpreter.BuildText(doc, doc.Pages[i])).Text);
            }
            return 0;
        case "codes":
        {
            // every QR and bar code of the document (or of one page), as the redaction check finds them
            var onlyCodePage = args.Length >= 3 && int.TryParse(args[2], out var cp) ? cp : 0;
            var codeWatch = System.Diagnostics.Stopwatch.StartNew();
            var codeCount = 0;
            for (var i = 0; i < doc.PageCount; i++)
            {
                if (onlyCodePage != 0 && i + 1 != onlyCodePage) continue;
                var pageWatch = System.Diagnostics.Stopwatch.StartNew();
                // --bild <png>: the page as the search sees it (grey, up to 4 pixels per point)
                if (Option("--bild") is { } pageImage)
                {
                    var (pw, ph) = PageGeometry.DeviceSize(doc.Pages[i], 1);
                    var box = doc.Pages[i].CropBox;
                    if (PageBarcodes.Render(doc, doc.Pages[i], PageInterpreter.Build(doc, doc.Pages[i]), [], new ImageCache(),
                            new PdfKnead.Core.Pdf.Graphics.RectD(box.Left, box.Bottom, box.Right, box.Top), Math.Min(4, Math.Sqrt(16_000_000.0 / Math.Max(1.0, (double)pw * ph)))) is var (gray, _))
                        using (var png = File.Create(pageImage))
                            PdfKnead.Core.Imaging.PngEncoder.Encode(gray.Width, gray.Height, 8, PdfKnead.Core.Imaging.PngColorType.Gray, (row, line) => gray.Pixels.AsSpan(row * gray.Width, gray.Width).CopyTo(line), png);
                }
                var codesOnPage = PageBarcodes.FindAll(doc, doc.Pages[i], PageInterpreter.Build(doc, doc.Pages[i]), [], new ImageCache());
                foreach (var code in codesOnPage)
                {
                    codeCount++;
                    Console.WriteLine($"=== Seite {i + 1} ===");
                    Console.WriteLine(CodeReport.Describe(code));
                }
                if (args.Contains("--zeit")) Console.WriteLine(FormattableString.Invariant($"zeit	Seite {i + 1}	{pageWatch.Elapsed.TotalMilliseconds:0} ms"));
            }
            Console.WriteLine(FormattableString.Invariant($"codes	{codeCount}	{codeWatch.Elapsed.TotalMilliseconds:0} ms"));
            return 0;
        }
        case "code":
        {
            // the QR or bar code at a point of a page (x, y in points of the page space), as the tool „QR-/Barcode“ reads it
            if (args.Length < 5 || !int.TryParse(args[2], out var codePage) || codePage < 1 || codePage > doc.PageCount
                || !double.TryParse(args[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var codeX)
                || !double.TryParse(args[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var codeY))
            {
                Console.Error.WriteLine("pdf-knead code <datei> <seite> <x> <y> [--anteil] [--bild <png>]");
                return 1;
            }
            // --anteil: x and y as fractions (0 … 1) of the displayed page from its top left corner, as a rendered page image gives them
            if (args.Contains("--anteil"))
            {
                var shown = PageGeometry.PageToDevice(doc.Pages[codePage - 1], 1);
                var (shownWidth, shownHeight) = PageGeometry.DeviceSize(doc.Pages[codePage - 1], 1);
                (codeX, codeY) = shown.Invert().Transform(codeX * shownWidth, codeY * shownHeight);
            }
            var codeList = PageInterpreter.Build(doc, doc.Pages[codePage - 1]);
            // --bild <png>: the area of the first look as the reader sees it (grey, 4 pixels per point), for diagnosis
            if (Option("--bild") is { } codeImage
                && PageBarcodes.Render(doc, doc.Pages[codePage - 1], codeList, [], new ImageCache(), new PdfKnead.Core.Pdf.Graphics.RectD(codeX - 110, codeY - 110, codeX + 110, codeY + 110), 4) is var (gray, toGray))
            {
                using (var png = File.Create(codeImage))
                    PdfKnead.Core.Imaging.PngEncoder.Encode(gray.Width, gray.Height, 8, PdfKnead.Core.Imaging.PngColorType.Gray, (row, line) => gray.Pixels.AsSpan(row * gray.Width, gray.Width).CopyTo(line), png);
                var (gx, gy) = toGray.Transform(codeX, codeY);
                Console.WriteLine(FormattableString.Invariant($"bild	{gray.Width}x{gray.Height}	klick {gx:0},{gy:0}"));
            }
            if (PageBarcodes.ReadAt(doc, doc.Pages[codePage - 1], codeList, [], new ImageCache(), codeX, codeY) is not { } code)
            {
                Console.WriteLine(Tr.T("Kein Code an dieser Stelle."));
                return 2;
            }
            Console.WriteLine(CodeReport.Describe(code));
            return 0;
        }
        case "verborgen" or "hidden":
            // the spots "redact hidden content" finds: kind, bounds, character count and text
            for (var i = 0; i < doc.PageCount; i++)
            {
                if (args.Length >= 3 && int.TryParse(args[2], out var hp) && hp != i + 1) continue;
                var scan = PdfKnead.Core.Pdf.Editing.HiddenText.Scan(doc, doc.Pages[i]);
                Console.WriteLine(Tr.T("--- Seite {0} ---", i + 1) + (scan.Complete ? "" : " " + Tr.T("Seite {0} nicht vollständig geprüft", i + 1)));
                foreach (var spot in scan.Spots)
                    Console.WriteLine(FormattableString.Invariant($"{spot.Kind}\t({spot.Bounds.Left:0.#}, {spot.Bounds.Bottom:0.#}) – ({spot.Bounds.Right:0.#}, {spot.Bounds.Top:0.#})\t{spot.Glyphs}\t{spot.Text.ReplaceLineEndings(" ")}"));
            }
            return 0;
        case "bilder" or "images":
            // Image info as in the program ("image info" tool): per image format, pixels, colours, size and resolution on the page, EXIF, origin
            var imagePage = args.Length >= 3 && int.TryParse(args[2], out var ip) ? ip : 0;
            if (imagePage != 0 && (imagePage < 1 || imagePage > doc.PageCount))
            {
                Console.Error.WriteLine(Tr.T("Seite {0} gibt es nicht (1 bis {1}).", imagePage, doc.PageCount));
                return 1;
            }
            var imageCount = 0;
            // an image drawn several times is saved once (the program saves the one clicked)
            var savedImages = new Dictionary<PdfStream, int>(ReferenceEqualityComparer.Instance);
            for (var i = 0; i < doc.PageCount; i++)
            {
                if (imagePage != 0 && i + 1 != imagePage) continue;
                var imageList = PageInterpreter.Build(doc, doc.Pages[i]);
                var imageItems = imageList.Items.Span;
                for (var k = 0; k < imageItems.Length; k++)
                {
                    if (imageItems[k].Kind != PdfKnead.Core.Pdf.Graphics.DisplayItemKind.Image) continue;
                    imageCount++;
                    Console.WriteLine(Tr.T("=== Seite {0}, Bild {1} ===", i + 1, imageCount));
                    var inspected = PdfKnead.Core.Pdf.Graphics.ImageInspector.Inspect(doc, doc.Pages[i], imageList, k);
                    Console.WriteLine(PdfKnead.Core.Pdf.Graphics.ImageInspector.AsText(PdfKnead.Core.Pdf.Graphics.ImageInspector.Sections(inspected)));
                    // --ablegen <folder>: every JPEG as a file, next to it the program's data in a fixed format — for comparison with the
                    // Windows image reader (tools/messen/bildinfo-abgleich.ps1)
                    if (Option("--ablegen") is { } dump && inspected.Codec == PdfKnead.Core.Pdf.Graphics.ImageCodec.Jpeg
                        && imageList.Images[imageItems[k].Image].Stream is { } jpegStream)
                    {
                        Directory.CreateDirectory(dump);
                        var stem = Path.Combine(dump, $"{Path.GetFileNameWithoutExtension(file)}-s{i + 1}-b{imageCount}");
                        File.WriteAllBytes(stem + ".jpg", doc.DecodeStream(jpegStream).Data);
                        var e = inspected.Embedded;
                        File.WriteAllLines(stem + ".txt",
                        [
                            $"breite={inspected.Width}", $"hoehe={inspected.Height}", $"hersteller={e?.Make}", $"modell={e?.Model}", $"software={e?.Software}",
                            $"datum={e?.Taken:yyyy-MM-dd HH:mm:ss}", $"ausrichtung={e?.Orientation}",
                            $"dpi={(e?.StoredDpi is { } stored ? $"{stored.X:0.##};{stored.Y:0.##}" : "")}".Replace(',', '.'),
                            $"progressiv={e?.Progressive}", $"qualitaet={e?.Quality}",
                        ]);
                    }
                    // --speichern <folder>: every image as „Bild speichern“ in the image info writes it (original quality)
                    if (Option("--speichern") is { } saveTo)
                    {
                        if (imageList.Images[imageItems[k].Image].Stream is { } once)
                        {
                            if (savedImages.TryGetValue(once, out var first))
                            {
                                Console.WriteLine($"→ wie Bild {first}");
                                continue;
                            }
                            savedImages[once] = imageCount;
                        }
                        Directory.CreateDirectory(saveTo);
                        var stem = Path.Combine(saveTo, $"{Path.GetFileNameWithoutExtension(file)}-s{i + 1}-b{imageCount}");
                        try
                        {
                            var imageSource = imageList.Images[imageItems[k].Image];
                            var exported = PdfKnead.Core.Pdf.Graphics.ImageExport.Export(doc, imageSource);
                            File.WriteAllBytes(stem + exported.Extension, exported.Data);
                            if (exported.MaskPng is { } maskPng) File.WriteAllBytes(stem + "-maske.png", maskPng);
                            var verdict = ImageExportCheck.Check(doc, imageSource, exported);
                            Console.WriteLine($"→ {Path.GetFileName(stem + exported.Extension)} ({exported.Data.Length:N0} Bytes" +
                                              (exported.MaskPng is null ? "" : ", Maske daneben") + (exported.ColorsConverted ? ", umgerechnet" : "") + ")" +
                                              (verdict is null ? "" : " FEHLER: " + verdict));
                        }
                        catch (Exception ex) when (ex is PdfException or IOException)
                        {
                            Console.WriteLine($"→ nicht gespeichert: {ex.Message}");
                        }
                    }
                }
            }
            if (imageCount == 0) Console.WriteLine(Tr.T("Keine Bilder."));
            return 0;
        case "texterkennung" or "ocr":
            return PdfKnead.Cli.OcrCheck.Run(doc, args.Length >= 3 && int.TryParse(args[2], out var op) ? op : 0,
                Enum.TryParse<PdfKnead.Core.Ocr.OcrModelSize>(Option("--groesse"), true, out var ocrSize) ? ocrSize : PdfKnead.Core.Ocr.OcrModelSize.Small,
                Option("--modelle"), int.TryParse(Option("--dpi"), out var ocrDpi) && ocrDpi is >= 50 and <= 600 ? ocrDpi : PdfKnead.Rendering.PageRecognition.Dpi,
                args.Contains("--woerter"), new PdfKnead.Ai.PaddleOcrOptions(Threads: int.TryParse(Option("--threads"), out var ot) ? ot : 0,
                    BatchSize: int.TryParse(Option("--stapel"), out var ob) && ob > 0 ? ob : 6, Arena: args.Contains("--arena"),
                    MaxSide: int.TryParse(Option("--maxseite"), out var om) && om >= 64 ? om : 1600),
                Option("--ablegen"), args.Contains("--alle"));
        case "bloecke" or "blocks":
            return BlockCheck.Run(doc, args.Length >= 3 && int.TryParse(args[2], out var bp) ? bp : 0, args.Contains("--ohne-text"));
        case "bild" or "image" when args.Length >= 3 && int.TryParse(args[2], out var pageNumber) && pageNumber >= 1 && pageNumber <= doc.PageCount:
            var selected = doc.Pages[pageNumber - 1];
            var list = PageInterpreter.Build(doc, selected);
            using (var bitmap = SkiaRenderer.RenderPage(list, doc, selected, dpi / 72, new ImageCache()))
            {
                var target = Option("--ausgabe") ?? Path.ChangeExtension(file, null) + $"-{pageNumber}.png";
                using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(target, data.ToArray());
                Console.WriteLine(Tr.T("Gespeichert: {0} ({1} × {2} Pixel)", target, bitmap.Width, bitmap.Height));
            }
            return 0;
        case "speichern" or "save" when args.Length >= 3:
            var items = PageItem.From(doc);
            if (Option("--seiten") is { } range)
            {
                if (PageRange.Parse(range, doc.PageCount) is not { Count: > 0 } chosen)
                {
                    Console.Error.WriteLine(Tr.T("Ungültiger Seitenbereich: {0}", range));
                    return 1;
                }
                items = [.. chosen.Select(i => items[i])];
            }
            using (var output = File.Create(args[2]))
            {
                var saved = PdfWriter.Write(doc, items, output, new PdfSaveOptions { Version = Option("--version") ?? "1.7" });
                Console.WriteLine(Tr.T("Gespeichert: {0} (PDF {1}, {2} Seiten, {3} Bytes)", args[2], saved.Version, items.Length, saved.Length));
            }
            return 0;
        case "messen" or "measure":
            Measure(doc, dpi);
            return 0;
        default:
            Console.Error.WriteLine(Tr.T("Unbekannter Befehl: {0}", args[0]));
            return 2;
    }
}
catch (PdfException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
catch (IOException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

/// <summary>Inspects a font: a file, the name of a built-in font or "alle" (all handwriting fonts).</summary>
static int FontInfo(string which, string? text)
{
    var files = new List<(string Name, byte[] Data)>();
    if (which is "alle" or "all")
        files.AddRange(PdfKnead.Core.Fonts.EmbeddedFonts.Handwriting.Select(n => (n, PdfKnead.Core.Fonts.EmbeddedFonts.LoadHandwriting(n))));
    else if (File.Exists(which))
        files.Add((Path.GetFileName(which), File.ReadAllBytes(which)));
    else if (PdfKnead.Core.Fonts.EmbeddedFonts.Handwriting.FirstOrDefault(n => n.StartsWith(which, StringComparison.OrdinalIgnoreCase)) is { } hw)
        files.Add((hw, PdfKnead.Core.Fonts.EmbeddedFonts.LoadHandwriting(hw)));
    else if (PdfKnead.Core.Fonts.EmbeddedFonts.Standard.FirstOrDefault(n => n.StartsWith(which, StringComparison.OrdinalIgnoreCase)) is { } std)
        files.Add((std, PdfKnead.Core.Fonts.EmbeddedFonts.LoadStandard(std)));
    else
    {
        Console.Error.WriteLine(Tr.T("Datei nicht gefunden: {0}", which));
        return 1;
    }
    foreach (var (name, data) in files)
    {
        Console.WriteLine($"== {name} ({data.Length / 1024} KB)");
        try
        {
            var font = PdfKnead.Core.Fonts.OpenTypeFont.Parse(data);
            Console.WriteLine($"   {font.FamilyName} / {font.PostScriptName}, {font.GlyphCount} Glyphen, {font.UnitsPerEm} Einheiten, " +
                              $"{(font.Cff is null ? "TrueType" : "CFF")}, Tabellen: {string.Join(" ", new[] { "GSUB", "GPOS", "GDEF", "kern", "sbix", "SVG ", "CBDT", "COLR" }.Where(font.HasTable))}");
            var shaper = PdfKnead.Core.Fonts.Layout.TextShaper.For(font);
            foreach (var line in shaper.Describe()) Console.WriteLine("   " + line);
            if (text is null) continue;
            var glyphs = shaper.Shape(text);
            var plain = text.EnumerateRunes().Select(r => font.GlyphForUnicode(r.Value)).ToArray();
            Console.WriteLine($"   cmap:   {string.Join(" ", plain)}");
            Console.WriteLine($"   gesetzt: {string.Join(" ", glyphs.Select(g => g.XOffset == 0 && g.YOffset == 0 ? $"{g.Glyph}" : $"{g.Glyph}@{g.XOffset:0},{g.YOffset:0}"))}");
            Console.WriteLine($"   Breite: {glyphs.Sum(g => g.XAdvance) / font.UnitsPerEm:0.000} Geviert (ohne Shaper {plain.Sum(g => font.AdvanceWidth(g)) / font.UnitsPerEm:0.000})");
        }
        catch (PdfKnead.Core.Fonts.FontFormatException e)
        {
            Console.WriteLine("   " + e.Message);
        }
    }
    return 0;
}

// Writes files (name, optionally ":range") one after another into a new file — on the first file, or (asNewFile) on an empty document as
// „als neue Datei“ without an open document does
static int Merge(string target, List<string> inputs, bool asNewFile, string version)
{
    if (inputs.Count == 0)
    {
        Console.Error.WriteLine(Tr.T("Keine Dateien angegeben."));
        return 2;
    }
    var docs = new List<PdfDocument>();
    try
    {
        var items = new List<PageItem>();
        foreach (var input in inputs)
        {
            // "file.pdf:1-3" — but not the "C:" of a Windows path
            var colon = input.LastIndexOf(':');
            var (path, range) = colon > 1 && !File.Exists(input) ? (input[..colon], input[(colon + 1)..]) : (input, null);
            var doc = PdfDocument.Open(path);
            docs.Add(doc);
            var pages = PageItem.From(doc);
            if (range is null)
            {
                items.AddRange(pages);
                continue;
            }
            if (PageRange.Parse(range, doc.PageCount) is not { Count: > 0 } chosen)
            {
                Console.Error.WriteLine(Tr.T("Ungültiger Seitenbereich: {0}", range));
                return 1;
            }
            items.AddRange(chosen.Select(i => pages[i]));
        }
        using var blank = asNewFile ? BlankPage.CreateDocument(595.28, 841.89, 1) : null;
        using var output = File.Create(target);
        var saved = PdfWriter.Write(blank ?? docs[0], items, output, new PdfSaveOptions { Version = version });
        Console.WriteLine(Tr.T("Gespeichert: {0} (PDF {1}, {2} Seiten, {3} Bytes)", target, saved.Version, items.Count, saved.Length));
        return 0;
    }
    catch (Exception e) when (e is PdfException or IOException)
    {
        Console.Error.WriteLine(e.Message);
        return 1;
    }
    finally
    {
        foreach (var doc in docs) doc.Dispose();
    }
}

static void Info(PdfDocument doc)
{
    var info = doc.Info;
    void Row(string label, object? value)
    {
        if (value is null || value is string { Length: 0 }) return;
        Console.WriteLine($"{label + ":",-22}{value}");
    }

    Row(Tr.T("PDF-Version"), doc.Version);
    Row(Tr.T("Seiten"), doc.PageCount);
    Row(Tr.T("Objekte"), doc.ObjectCount);
    Row(Tr.T("Dateigröße"), doc.FileSize.ToString("N0", Tr.Culture) + " B");
    Row(Tr.T("Titel"), info.Title);
    Row(Tr.T("Autor"), info.Author);
    Row(Tr.T("Erstellt mit"), info.Creator);
    Row(Tr.T("PDF erzeugt von"), info.Producer);
    Row(Tr.T("Erstellt am"), info.Created);
    Row(Tr.T("Geändert am"), info.Modified);
    if (PdfKnead.Core.Pdf.Writing.ConformanceClaims.Read(doc) is { } claims)
    {
        Row(Tr.T("Kennzeichnungen"), Tr.T("{0} — Angaben der Datei, nicht geprüft", claims.Summary));
        foreach (var uri in claims.Declarations) Row(Tr.T("Erklärung"), uri);
    }
    if (doc.Security is { } s)
        Row(Tr.T("Schutz"), $"R{s.Revision}, {s.StreamCipher}, {s.KeyBits} Bit, {(s.IsOwner ? Tr.T("Besitzer") : Tr.T("Benutzer"))}, {s.Permissions}");
    if (doc.Security is not null && doc.Integrity is { State: not PdfKnead.Core.Pdf.Security.MacState.None } mac)
        Row("MAC", $"{mac.State}{(mac.Detail is null ? "" : " — " + mac.Detail)}{(mac.EmptyUserPassword ? " (leeres Benutzerpasswort)" : "")}");
    if (doc.Pages.Count > 0) Row(Tr.T("Seitengröße"), PageFormat.Describe(doc.Pages[0].Width, doc.Pages[0].Height));
    foreach (var problem in doc.Problems) Row(Tr.T("Hinweis"), problem);
}

// Times per page: build the display list, render the whole page; plus, as in the program, the coarse overview and all tiles of
// a 1920×1080 window at page width and at 400 % (first page only)
static void Measure(PdfDocument doc, double dpi)
{
    MeasureViewer(doc);
    var images = new ImageCache();
    foreach (var page in doc.Pages)
    {
        var t = System.Diagnostics.Stopwatch.StartNew();
        var list = PageInterpreter.Build(doc, page);
        var build = t.ElapsedMilliseconds;
        t.Restart();
        using (SkiaRenderer.RenderPage(list, doc, page, dpi / 72, images)) { }
        var render = t.ElapsedMilliseconds;
        Console.WriteLine(Tr.T("Seite {0}: {1} Einträge, Liste {2} ms, Zeichnen {3} ms ({4} dpi), {5} MB",
            page.Number, list.Count.ToString("N0", Tr.Culture), build, render, dpi, list.EstimatedBytes >> 20));
    }
}

static void MeasureViewer(PdfDocument doc)
{
    using var renderer = new DocumentRenderer(doc);
    var page = doc.Pages[0];
    var t = System.Diagnostics.Stopwatch.StartNew();
    renderer.GetDisplayList(0).GetAwaiter().GetResult();
    Console.WriteLine($"Zeichenliste: {t.ElapsedMilliseconds} ms");

    void Run(string name, List<TileKey> keys)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        renderer.Request("ansicht", keys);
        while (keys.Any(k => renderer.TryGetTile(k) is null)) Thread.Sleep(5);
        Console.WriteLine($"{name}: {keys.Count} Kacheln in {watch.ElapsedMilliseconds} ms");
    }

    var overview = renderer.OverviewLevel(0);
    Run("Gesamtbild", [new TileKey(0, overview, 0, 0, 0)]);
    foreach (var (name, pixelsPerPoint) in new[] { ("Seitenbreite", 1920.0 / page.Width), ("400 %", 4 * 96.0 / 72) })
    {
        var level = DocumentRenderer.LevelFor(pixelsPerPoint);
        var (w, h) = renderer.SizeAt(0, level, 0);
        // 1920 × 1080 window in the middle of the page
        int x0 = Math.Max(0, (w - 1920) / 2) / DocumentRenderer.TileSize, x1 = Math.Min(w - 1, (w + 1920) / 2) / DocumentRenderer.TileSize;
        int y0 = Math.Max(0, (h - 1080) / 2) / DocumentRenderer.TileSize, y1 = Math.Min(h - 1, (h + 1080) / 2) / DocumentRenderer.TileSize;
        var keys = new List<TileKey>();
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++) keys.Add(new TileKey(0, level, x, y, 0));
        Run(name, keys);
    }
}
