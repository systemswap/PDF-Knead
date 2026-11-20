namespace PdfKnead.ViewModels;

/// <summary>How well a method is supported (colour in the info box).</summary>
public enum Support
{
    Full,
    Partial,
    None,
    Unknown,
}

/// <summary>One row of the info box: what is described, support level, text („seit PDF 1.4 (2001)“, „nicht erlaubt“ …).</summary>
public sealed record ViewerSupport(string Viewer, Support Support, string Text)
{
    public bool IsFull => Support == Support.Full;
    public bool IsPartial => Support == Support.Partial;
    public bool IsNone => Support == Support.None;
    public bool IsUnknown => Support == Support.Unknown;
}

/// <summary>A method in the compressor: name, whether it is available (otherwise why not), a description for non-experts, and its support.</summary>
/// <param name="Name">Display name.</param>
/// <param name="Available">Whether it can be chosen in PDF-Knead.</param>
/// <param name="Planned">Otherwise: when or why not yet („geplant – die PDF-Norm dafür ist noch nicht veröffentlicht“); null if available.</param>
/// <param name="Description">Explanation for non-experts.</param>
/// <param name="Viewers">The rows of the info box: the PDF standard, common readers, PDF/A.</param>
public sealed record CodecInfo(string Name, bool Available, string? Planned, string Description, IReadOnlyList<ViewerSupport> Viewers)
{
    /// <summary>Text in the selection list; for methods not yet available it includes the note.</summary>
    public string Label => Available ? Name : $"{Name} — {Planned}";

    public override string ToString() => Label;

    /// <summary>Date of the research and own checks. See KONZEPT.md, section 5.12, and docs/leser-vertraeglichkeit.html.</summary>
    public static string Stand => Tr.T("(Stand Oktober 2026)");
}

/// <summary>
/// The compressor's methods: what the PDF standard says about them, how common readers handle them, and whether PDF/A allows them — without
/// naming products: a statement about one product could go stale when its maker changes it (decided on 20.11.2026). The years are those of
/// the PDF versions that brought the methods. Research and own checks: docs/leser-vertraeglichkeit.html, with an updated
/// <see cref="CodecInfo.Stand"/>. See KONZEPT.md, section 5.12.
/// </summary>
public static class CodecCatalog
{
    private static string StandardRow => Tr.T("PDF-Norm");
    private static string ReadersRow => Tr.T("Verbreitete Leser");
    private static string PdfARow => Tr.T("PDF/A");

    /// <summary>The rows of the basic methods every common reader has long read: in the standard since <paramref name="since"/>, allowed in PDF/A.</summary>
    /// <param name="since">PDF version and year („1.2 (1996)“).</param>
    private static ViewerSupport[] Classic(string since) =>
    [
        new(StandardRow, Support.Full, Tr.T("seit PDF {0}", since)),
        new(ReadersRow, Support.Full, Tr.T("lesen es")),
        new(PdfARow, Support.Full, Tr.T("erlaubt")),
    ];

    public static CodecInfo Jpeg => new(Tr.T("JPEG"), true, null,
        Tr.T("Für Fotos. Spart viel Platz, indem es feine Details weglässt, die man kaum sieht. Jedes Programm kann es lesen — darum der Standard."),
        Classic("1.0 (1993)"));

    public static CodecInfo JpegXl => new(Tr.T("JPEG XL"), false, Tr.T("geplant – die PDF-Norm dafür ist noch nicht veröffentlicht"),
        Tr.T("Ein sehr effizientes neues Bildformat. Für PDF angekündigt, aber noch nicht genormt — bisher kann es kein Programm in PDFs lesen."),
        [
            new(StandardRow, Support.None, Tr.T("noch nicht genormt")),
            new(ReadersRow, Support.None, Tr.T("bisher keiner")),
            new(PdfARow, Support.None, Tr.T("nicht erlaubt")),
        ]);

    public static CodecInfo Flate => new(Tr.T("Flate mit Prädiktor"), true, null,
        Tr.T("Verlustfrei — nichts geht verloren. Jeder Bildpunkt wird aus seinen Nachbarn vorhergesagt, gespeichert wird nur die Abweichung. Bilder mit höchstens 256 Farben werden dabei zur genauen Farbliste."),
        Classic("1.2 (1996)"));

    public static CodecInfo Palette => new(Tr.T("Palette"), true, null,
        Tr.T("Für Logos, Diagramme und Bildschirmfotos: die Datei merkt sich eine Liste von höchstens 256 Farben und je Punkt nur die Nummer. Oft deutlich kleiner und schärfer als JPEG; echte Fotos bleiben JPEG."),
        Classic("1.2 (1996)"));   // packed with Flate

    public static CodecInfo Ccitt => new(Tr.T("CCITT G4"), true, null,
        Tr.T("Für reine Schwarzweiß-Scans wie Briefe und Formulare: das Faxverfahren — alt, klein und überall lesbar."),
        Classic("1.0 (1993)"));

    public static CodecInfo Jbig2 => new(Tr.T("JBIG2"), true, null,
        Tr.T("Der moderne Nachfolger von G4: dieselben Schwarzweiß-Punkte, etwa ein Drittel kleiner gepackt. Verlustfrei — jeder Bildpunkt bleibt, wie er ist. PDF-Knead vergleicht keine Zeichen miteinander, darum kann nie eine Ziffer vertauscht werden."),
        Classic("1.4 (2001)"));

    public static CodecInfo AsPhotos => new(Tr.T("Wie Fotos"), true, null,
        Tr.T("Farbige Scans werden wie Fotos behandelt: verkleinert und als JPEG gepackt."),
        Classic("1.0 (1993)"));

    public static CodecInfo Mrc => new(Tr.T("MRC"), true, null,
        Tr.T("Für farbige Scans: Schrift wird scharf in Schwarzweiß gespeichert, der Hintergrund klein und weich. Der Text bleibt gestochen scharf, die Datei wird ein Vielfaches kleiner. Die Schrift ist mit JBIG2 gepackt (Vorgabe) — mit G4 statt JBIG2 liest es jeder Leser seit 1993."),
        Jbig2.Viewers);   // the masks are JBIG2

    public static CodecInfo FlatePack => new(Tr.T("Flate"), true, null,
        Tr.T("Das übliche Packverfahren für Text, Schriften und Zeichnungen — verlustfrei und überall lesbar."),
        Classic("1.2 (1996)"));

    public static CodecInfo FlateStrong => new(Tr.T("Flate – stark (langsamer)"), true, null,
        Tr.T("Dasselbe Flate, nur gründlicher gepackt: Text, Schriften und Zeichnungen werden rund ein Zehntel kleiner, das Speichern dauert dafür etwa fünfmal so lange. Jedes Programm kann es lesen — es ist ganz gewöhnliches Flate."),
        Classic("1.2 (1996)"));

    public static CodecInfo Brotli => new(Tr.T("Brotli"), true, null,
        Tr.T("Ein neueres Packverfahren, rund ein Fünftel kleiner als Flate. Verbreitete Leser können solche Dateien oft noch nicht öffnen — nur wählen, wenn klar ist, womit die Datei gelesen wird. Nicht für PDF/A."),
        [
            new(StandardRow, Support.Partial, Tr.T("noch nicht in der Norm (Erweiterung der PDF Association)")),
            new(ReadersRow, Support.Partial, Tr.T("viele noch nicht")),
            new(PdfARow, Support.None, Tr.T("nicht erlaubt")),
        ]);
}
