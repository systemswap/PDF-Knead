using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Hiding the length of redacted words (docs/RECHERCHE-VERSTECKEN.md, B-13): the gap of a redacted word and its bar grow to the next step of
/// 2 em (24 pt at 12 pt), the words after it move up, the line takes the room from its word spaces or from free room after its end — and no
/// letter that stays ever comes under a bar. Where the room is not enough, the gap grows only as far as it goes, and the page is named.
/// </summary>
public sealed class RedactionLengthTests
{
    private static readonly double Step = Redactor.LengthStepEm * 12;   // the step in em of Helvetica 12 pt

    private static PdfDocument Page(string content) => PdfDocument.Open(File(content));

    /// <summary>A page with the content (Helvetica 12 pt as /F1).</summary>
    internal static byte[] File(string content)
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, new PdfStream(new PdfDictionary(), System.Text.Encoding.ASCII.GetBytes(content)));
        return pdf.Build();
    }

    /// <summary>A long line above, so that a ragged line below may grow to its end.</summary>
    private const string Above = "BT /F1 12 Tf 72 712 Td (Eine lange Zeile darueber, die weit nach rechts reicht) Tj ET ";

    /// <summary>A justified line (word spacing 6 pt) with the name „Mustermann“ in it.</summary>
    internal const string JustifiedLine = "BT /F1 12 Tf 72 700 Td 6 Tw (Herr Mustermann sagt heute zu allen hier noch etwas) Tj ET";

    /// <summary>The area of a word (its line rectangle, as „Text schwärzen“ marks it).</summary>
    private static PdfRectangle AreaOf(PdfDocument doc, string word)
    {
        var text = PageText.Create(PageInterpreter.Build(doc, doc.Pages[0]).Text);
        var q = text.Quads(Assert.Single(text.Find(word, matchCase: true)));
        double left = q.Min(x => Math.Min(x.X0, x.X3)), right = q.Max(x => Math.Max(x.X1, x.X2));
        double bottom = q.Min(x => Math.Min(x.Y0, x.Y1)), top = q.Max(x => Math.Max(x.Y2, x.Y3));
        return new PdfRectangle(left, bottom, right, top);
    }

    private static (byte[] File, PdfSaveResult Result) Redact(PdfDocument doc, bool hide, params string[] words)
    {
        var items = PageItem.From(doc);
        items[0] = items[0].WithElements([.. words.Select(w => new RedactionElement { Rect = AreaOf(doc, w) })]);
        using var output = new MemoryStream();
        var result = PdfWriter.Write(doc, items, output, new PdfSaveOptions { ApplyRedactions = true, HideRedactedLengths = hide, CompressStreams = false });
        return (output.ToArray(), result);
    }

    /// <summary>Where a word starts on the page (left edge of its first character); null if it is not there.</summary>
    private static double? StartOf(byte[] file, string word)
    {
        using var doc = PdfDocument.Open(file);
        var text = PageText.Create(PageInterpreter.Build(doc, doc.Pages[0]).Text);
        return text.Find(word, matchCase: true).ToList() is [var hit] ? text.Quads(hit)[0].X0 : null;
    }

    /// <summary>Whether the page is dark at the point (y upwards).</summary>
    private static bool Dark(byte[] file, double x, double y)
    {
        using var doc = PdfDocument.Open(file);
        var page = doc.Pages[0];
        using var bitmap = SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page), doc, page, 2, new ImageCache());
        var p = PageGeometry.PageToDevice(page, 2).Transform(x, y);
        return bitmap.GetPixel((int)p.X, (int)p.Y).Red < 100;
    }

    /// <summary>Share of dark pixels in the box of a word: letters stay well below one half, under a bar it is all dark.</summary>
    private static double Ink(byte[] file, string word)
    {
        using var doc = PdfDocument.Open(file);
        var page = doc.Pages[0];
        var text = PageText.Create(PageInterpreter.Build(doc, page).Text);
        var q = text.Quads(Assert.Single(text.Find(word, matchCase: true)));
        using var bitmap = SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page), doc, page, 2, new ImageCache());
        var toDevice = PageGeometry.PageToDevice(page, 2);
        var a = toDevice.Transform(q.Min(x => x.X0), q.Max(x => x.Y2));
        var b = toDevice.Transform(q.Max(x => x.X1), q.Min(x => x.Y0));
        int dark = 0, all = 0;
        for (var y = (int)Math.Min(a.Y, b.Y); y < (int)Math.Max(a.Y, b.Y); y++)
            for (var x = (int)Math.Min(a.X, b.X); x < (int)Math.Max(a.X, b.X); x++, all++)
                if (bitmap.GetPixel(x, y).Red < 100) dark++;
        return (double)dark / Math.Max(1, all);
    }

    /// <summary>Whether a distance is a whole number of steps (a word starts on a grid of 0.5 pt).</summary>
    private static bool WholeSteps(double gap) => Math.Abs(gap - Math.Round(gap / Step) * Step) <= 0.51;

    /// <summary>
    /// Justified text (word spacing): the gap from the start of the name to the next word becomes a multiple of the step, the bar fills it,
    /// the end of the line stays where it was (the word spaces gave the room), every other word is still there.
    /// </summary>
    [Fact]
    public void Justified_GapToStep_LineEndStays()
    {
        using var doc = Page(JustifiedLine);
        var name = AreaOf(doc, "Mustermann");
        var (plain, _) = Redact(doc, false, "Mustermann");
        var (hidden, result) = Redact(doc, true, "Mustermann");
        // the gap grows to its step, but the spacing before the name was set with the name: partly shown (docs/FEHLERGEDAECHTNIS.md, No. 354)
        Assert.NotEmpty(result.LengthsPartlyShown);
        var gap = StartOf(hidden, "sagt")!.Value - name.Left;
        Assert.True(WholeSteps(gap), $"Lücke {gap}");
        Assert.True(gap > StartOf(plain, "sagt")!.Value - name.Left + 1, "die Lücke ist gewachsen");
        Assert.Equal(StartOf(plain, "etwas")!.Value, StartOf(hidden, "etwas")!.Value, 0);   // the end of the line stays
        foreach (var word in new[] { "Herr", "sagt", "heute", "zu", "allen", "hier", "noch", "etwas" }) Assert.NotNull(StartOf(hidden, word));
        Assert.True(Dark(hidden, name.Right + 4, 703), "der Balken wächst mit der Lücke");
        foreach (var word in new[] { "Herr", "sagt", "heute" }) Assert.InRange(Ink(hidden, word), 0, 0.5);
    }

    /// <summary>Two names of different length within one step leave the same gap: their length cannot be told apart.</summary>
    [Fact]
    public void NamesOfDifferentLength_SameGap()
    {
        static double Gap(string name)
        {
            using var doc = Page($"BT /F1 12 Tf 72 700 Td 6 Tw (Herr {name} sagt heute zu allen hier noch etwas) Tj ET");
            var start = AreaOf(doc, name).Left;
            return StartOf(Redact(doc, true, name).File, "sagt")!.Value - start;
        }
        Assert.Equal(Gap("Mustermann"), Gap("Mustermanns"), 0);
    }

    /// <summary>
    /// The attack on glyph positions (Bland, Iyer, Levchenko, PoPETs 2023): from where the remaining characters stand — and from the numbers
    /// in the content that put them there — the width of what was removed can be computed and a name guessed from a list. Two names of
    /// different length within one step, in justified and in ragged lines: after redacting with hidden lengths every remaining character
    /// stands exactly where it stands for the other name, the bars are the same, and the page content is the same byte for byte
    /// (docs/FEHLERGEDAECHTNIS.md, No. 354). Ragged lines with a longer line above (room to grow into); justified lines are the next test.
    /// </summary>
    [Theory]
    [InlineData(Above + "BT /F1 12 Tf 72 700 Td (Herr {0} sagt heute) Tj ET", "Musterfrau", "Mustermann")]
    [InlineData(Above + "BT /F1 12 Tf 72 700 Td [(Herr )-20({0})30( sagt heute)] TJ ET", "Musterfrau", "Mustermann")]
    public void NamesOfDifferentLength_NothingLeftToMeasure(string line, string first, string second)
    {
        static (string Positions, byte[] Content) After(string line, string name)
        {
            using var doc = Page(string.Format(System.Globalization.CultureInfo.InvariantCulture, line, name));
            var (file, _) = Redact(doc, true, name);
            using var after = PdfDocument.Open(file);
            var list = PageInterpreter.Build(after, after.Pages[0]);
            var positions = string.Join(" ", list.Text.Select(g => FormattableString.Invariant($"{g.Text}@{g.X0:0.00},{g.Y0:0.00}")));
            // the bars are part of the content: the same content means the same bars
            return (positions, after.GetStreamData((PdfStream)after.Pages[0].Dictionary.Get(PdfName.Contents)));
        }
        var (positionsA, contentA) = After(line, first);
        var (positionsB, contentB) = After(line, second);
        Assert.Equal(positionsA, positionsB);
        Assert.Equal(System.Text.Encoding.Latin1.GetString(contentA), System.Text.Encoding.Latin1.GetString(contentB));
    }

    /// <summary>
    /// A line cut to a box (as the text of a form field is cut to the field): the room after it ends at the box. Its words never move out
    /// of it — beyond the edge they would no longer show, a visible word turned hidden; the gap grows as far as the box allows, and the page
    /// is named (docs/FEHLERGEDAECHTNIS.md, No. 358).
    /// </summary>
    [Fact]
    public void LineCutToABox_WordsStayInside()
    {
        using var natural = Page("BT /F1 12 Tf 72 700 Td (Herr Lutz sagt) Tj ET");
        var end = PageInterpreter.Build(natural, natural.Pages[0]).Text.Max(g => Math.Max(g.X1, g.X2));
        using var doc = Page(Above + FormattableString.Invariant($"q 70 690 {end + 3 - 70:0.###} 30 re W n BT /F1 12 Tf 72 700 Td (Herr Lutz sagt) Tj ET Q"));
        var (file, result) = Redact(doc, true, "Lutz");
        using var after = PdfDocument.Open(file);
        var sagt = PageInterpreter.Build(after, after.Pages[0]).Text.Where(g => g.Text is "s" or "a" or "g" or "t" && Math.Abs(g.Y0 - 700) < 6).ToList();
        Assert.Equal(4, sagt.Count);
        Assert.True(sagt.Max(g => Math.Max(g.X1, g.X2)) <= end + 3.01, $"„sagt“ ragt über den Zuschnitt: {sagt.Max(g => Math.Max(g.X1, g.X2))} > {end + 3}");
        Assert.True(result.LengthsPartlyShown.Count > 0, $"nicht genannt; „sagt“ bei {sagt.Min(g => g.X0)}, vorher {StartOf(Redact(doc, false, "Lutz").File, "sagt")}");
    }

    /// <summary>
    /// The check after moving (Redactor.MoveCheck.cs, docs/FEHLERGEDAECHTNIS.md, No. 359) compares the characters of a line before and after:
    /// the letters of the removed name may be gone, the rest must follow in order. A name whose letters recur in the word after it („Marta
    /// tanzt“): matched greedily, the name's „t“ and „a“ took the place of the next word's and the line counted as broken — it is not, the
    /// gap grows and nothing is named.
    /// </summary>
    [Fact]
    public void NameWhoseLettersRecurInTheNextWord_LineStillGrows()
    {
        using var doc = Page(Above + "BT /F1 12 Tf 72 700 Td (Herr Marta tanzt heute) Tj ET");
        var name = AreaOf(doc, "Marta");
        var (plain, _) = Redact(doc, false, "Marta");
        var (hidden, result) = Redact(doc, true, "Marta");
        Assert.Empty(result.LengthsPartlyShown);
        Assert.True(StartOf(hidden, "tanzt")!.Value > StartOf(plain, "tanzt")!.Value + 1, "„tanzt“ rückt nach rechts");
        Assert.True(WholeSteps(StartOf(hidden, "tanzt")!.Value - name.Left));
    }

    /// <summary>
    /// One bar over the names of three lines (an area drawn over a block): the first line grows into the room after it, the others have none.
    /// The piece of the first line is drawn beside the bar — joining it to the tall bar widened the whole area, which covered what lies
    /// beside it on the other lines and between them (docs/FEHLERGEDAECHTNIS.md, No. 357).
    /// </summary>
    [Fact]
    public void TallBarOverSeveralLines_PieceOfOneLine_AreaKeepsItsWidth()
    {
        using var doc = Page("BT /F1 12 Tf 72 740 Td (Eine lange Zeile darueber, die weit nach rechts reicht) Tj ET " +
                             "BT /F1 12 Tf 20 TL 72 700 Td (Frau Erika sagt) Tj T* (Herr Kurz sagt heute zu allen hier noch etwas mehr als genug) Tj T* " +
                             "(Herr Lang kommt morgen frueh mit allen anderen hierher zurueck) Tj ET");
        var names = new[] { "Erika", "Kurz", "Lang" }.Select(n => AreaOf(doc, n)).ToList();
        var area = new PdfRectangle(names.Min(n => n.Left) - 1, names.Min(n => n.Bottom) - 1, names.Max(n => n.Right) + 1, names.Max(n => n.Top) + 1);
        var items = PageItem.From(doc);
        items[0] = items[0].WithElements([new RedactionElement { Rect = area }]);
        using var output = new MemoryStream();
        var result = PdfWriter.Write(doc, items, output, new PdfSaveOptions { ApplyRedactions = true, HideRedactedLengths = true, CompressStreams = false });
        var file = output.ToArray();
        Assert.NotEmpty(result.BarExtensions);   // the first line grew
        // beside the area, between the first and the second line, and on the lines without room: paper
        Assert.False(Dark(file, area.Right + 2, 690), "die Fläche ist zwischen den Zeilen breiter geworden");
        Assert.False(Dark(file, area.Right + 2, 670), "die Fläche ist zwischen den Zeilen breiter geworden");
        foreach (var word in new[] { "Frau", "heute", "kommt", "morgen" }) Assert.InRange(Ink(file, word), 0, 0.5);
    }

    /// <summary>
    /// As above, the name of the first line now narrower than those below („Max“ over „Kurz“ and „Lang“): here the second line grows, its piece
    /// beside the tall area covered the generous line box — 18 pt for a line of 12 pt, reaching into the space between the lines (with lines
    /// closer than 1.5 heights, into the next line). The piece covers its line as the text layer boxes the characters; between the lines the
    /// area keeps its width (found while renaming the test names, 12.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 783).
    /// </summary>
    [Fact]
    public void TallBarOverSeveralLines_FirstNameNarrower_AreaKeepsItsWidth()
    {
        using var doc = Page("BT /F1 12 Tf 72 740 Td (Eine lange Zeile darueber, die weit nach rechts reicht) Tj ET " +
                             "BT /F1 12 Tf 20 TL 72 700 Td (Herr Max sagt) Tj T* (Herr Kurz sagt heute zu allen hier noch etwas mehr als genug) Tj T* " +
                             "(Herr Lang kommt morgen frueh mit allen anderen hierher zurueck) Tj ET");
        var names = new[] { "Max", "Kurz", "Lang" }.Select(n => AreaOf(doc, n)).ToList();
        var area = new PdfRectangle(names.Min(n => n.Left) - 1, names.Min(n => n.Bottom) - 1, names.Max(n => n.Right) + 1, names.Max(n => n.Top) + 1);
        var items = PageItem.From(doc);
        items[0] = items[0].WithElements([new RedactionElement { Rect = area }]);
        using var output = new MemoryStream();
        var result = PdfWriter.Write(doc, items, output, new PdfSaveOptions { ApplyRedactions = true, HideRedactedLengths = true, CompressStreams = false });
        var file = output.ToArray();
        Assert.NotEmpty(result.BarExtensions);   // a line grew (here the second: „Kurz“ ends nearer the edge of the area)
        // between the lines (outside the characters' boxes 657.6–669.6, 677.6–689.6, 697.6–709.6): paper beside the area — the piece covered
        // the generous line box before (2.4 pt below the second line, No. 783)
        foreach (var y in (double[])[671, 673, 675, 676.5, 691, 693, 695, 697])
            Assert.False(Dark(file, area.Right + 2, y), FormattableString.Invariant($"die Fläche ist bei y = {y} breiter geworden"));
        foreach (var word in new[] { "heute", "kommt", "morgen" }) Assert.InRange(Ink(file, word), 0, 0.5);
    }

    /// <summary>
    /// A name in small capitals, its first letter in one text operator and the rest smaller in the next, each placed by its own Td from the
    /// one before (as typesetting programs write them):
    /// the operators that lose all their characters stay as empty ones — they start where the bar starts, not where their characters stood,
    /// which told the width of the first letter (docs/FEHLERGEDAECHTNIS.md, No. 356). Two names of different length within one step: the same
    /// page content byte for byte.
    /// </summary>
    [Fact]
    public void NameInSmallCapitals_EmptyOperatorsTellNothing()
    {
        static double Width(string text, double size)
        {
            using var doc = Page(FormattableString.Invariant($"BT /F1 {size} Tf 72 700 Td ({text}) Tj ET"));
            return PageInterpreter.Build(doc, doc.Pages[0]).Text.Max(g => Math.Max(g.X1, g.X2)) - 72;
        }
        static string After(string first, string rest)
        {
            using var doc = Page(Above + FormattableString.Invariant(
                $"BT /F1 12 Tf 72 700 Td (Herr) Tj {Width("Herr ", 12):0.###} 0 Td ({first}) Tj {Width(first, 12):0.###} 0 Td /F1 9 Tf ({rest}) Tj {Width(rest, 9):0.###} 0 Td /F1 12 Tf ( sagt heute) Tj ET"));
            var (file, _) = Redact(doc, true, first + rest);
            using var after = PdfDocument.Open(file);
            return System.Text.Encoding.Latin1.GetString(after.GetStreamData((PdfStream)after.Pages[0].Dictionary.Get(PdfName.Contents)));
        }
        Assert.Equal(After("L", "UTZ"), After("M", "EIER"));
    }

    /// <summary>
    /// A justified line with words before the redacted name: their spacing was set so that the line with the name reaches the margin and gives
    /// its width away; it stays as it is, so the page is named — the length counts as partly shown, not as hidden
    /// (docs/FEHLERGEDAECHTNIS.md, No. 354).
    /// </summary>
    [Fact]
    public void JustifiedLine_WordsBeforeTheName_LengthCountsAsPartlyShown()
    {
        using var natural = Page("BT /F1 12 Tf 72 700 Td (Herr Kurz sagt heute zu allen hier noch etwas) Tj ET");
        var end = PageInterpreter.Build(natural, natural.Pages[0]).Text.Max(g => Math.Max(g.X1, g.X2));
        using var doc = Page(FormattableString.Invariant($"BT /F1 12 Tf 72 700 Td {(400 - end) / 8:0.###} Tw (Herr Kurz sagt heute zu allen hier noch etwas) Tj ET"));
        Assert.NotEmpty(Redact(doc, true, "Kurz").Result.LengthsPartlyShown);
        // the name opening a justified line: its gap cannot grow to a step there either
        using var natural2 = Page("BT /F1 12 Tf 72 700 Td (Kurz sagt heute zu allen hier noch etwas) Tj ET");
        var end2 = PageInterpreter.Build(natural2, natural2.Pages[0]).Text.Max(g => Math.Max(g.X1, g.X2));
        using var first = Page(FormattableString.Invariant($"BT /F1 12 Tf 72 700 Td {(400 - end2) / 7:0.###} Tw (Kurz sagt heute zu allen hier noch etwas) Tj ET"));
        Assert.NotEmpty(Redact(first, true, "Kurz").Result.LengthsPartlyShown);
        // a ragged line with the same words before the name: its spacing is the spaces' own, nothing to measure there
        using var ragged = Page(Above + "BT /F1 12 Tf 72 700 Td (Herr Kurz sagt heute) Tj ET");
        Assert.Empty(Redact(ragged, true, "Kurz").Result.LengthsPartlyShown);
    }

    /// <summary>The lines of <see cref="Paragraph"/>, {0} for the name.</summary>
    private static readonly string[] ParagraphLines = ["Eine Zeile im Blocksatz reicht genau bis an den Rand", "Herr {0} sagt heute zu allen hier noch etwas",
        "und die dritte Zeile reicht ebenso bis an den Rand", "Ende."];

    /// <summary>The right margin of <see cref="Paragraph"/>: a little after its longest line.</summary>
    private static readonly double Margin = Math.Ceiling(ParagraphLines[..3].Max(l => Natural(string.Format(l, "Mustermann")))) + 6;

    /// <summary>Where a text in Helvetica 12 pt from x = 72 ends.</summary>
    private static double Natural(string text)
    {
        using var doc = Page($"BT /F1 12 Tf 72 700 Td ({text}) Tj ET");
        return PageInterpreter.Build(doc, doc.Pages[0]).Text.Max(g => Math.Max(g.X1, g.X2));
    }

    /// <summary>
    /// A justified paragraph (Helvetica 12 pt, three lines to the margin at <see cref="Margin"/>, a short last line) with a name in its second
    /// line, written with word spacing (Tw), with the numbers of TJ or with each word placed by its own Td.
    /// </summary>
    private static string Paragraph(string name, string how)
    {
        var lines = ParagraphLines.Select(l => string.Format(l, name)).ToArray();
        var content = new System.Text.StringBuilder("BT /F1 12 Tf ");
        for (var l = 0; l < lines.Length; l++)
        {
            var words = lines[l].Split(' ');
            var extra = l < lines.Length - 1 ? (Margin - Natural(lines[l])) / (words.Length - 1) : 0;
            Assert.InRange(extra, 0, 9);   // spacing of a justified line, not a gap between columns
            content.Append(FormattableString.Invariant($"1 0 0 1 72 {712 - 14 * l} Tm "));
            switch (how)
            {
                case "Tw":
                    content.Append(FormattableString.Invariant($"{extra:0.####} Tw ({lines[l]}) Tj "));
                    break;
                case "TJ":
                    content.Append('[').Append(string.Join(FormattableString.Invariant($" {-extra * 1000 / 12:0.####} "), words.Select((w, i) => $"({w}{(i < words.Length - 1 ? " " : "")})"))).Append("] TJ ");
                    break;
                default:
                    // each word by its own Td from the start of the one before: its width, a space and the extra room
                    for (var i = 0; i < words.Length; i++)
                        content.Append(i == 0 ? $"({words[i]}) Tj " : FormattableString.Invariant($"{Natural(words[i - 1] + " ") - 72 + extra:0.####} 0 Td ({words[i]}) Tj "));
                    break;
            }
        }
        return content.Append("ET").ToString();
    }

    /// <summary>
    /// The attack on glyph positions in a justified paragraph (Bland, Iyer, Levchenko, PoPETs 2023): the spacing of the line was chosen so
    /// that the line with the name reaches the margin — the words before the name, measured, give its width away. Set anew, two names of
    /// different length within one step leave every remaining character exactly where it stands for the other, the same bars and the same
    /// page content byte for byte; the line still starts and ends where it did, every word is there and clear of the bars, and no page is
    /// named as partly shown (docs/FEHLERGEDAECHTNIS.md, No. 356).
    /// </summary>
    [Theory]
    [InlineData("Tw")]
    [InlineData("TJ")]
    [InlineData("Td")]
    public void JustifiedParagraph_NamesOfDifferentLength_NothingLeftToMeasure(string how)
    {
        (string Positions, string Content, PdfSaveResult Result, byte[] File) After(string name)
        {
            using var doc = Page(Paragraph(name, how));
            var (file, result) = Redact(doc, true, name);
            using var after = PdfDocument.Open(file);
            var list = PageInterpreter.Build(after, after.Pages[0]);
            var positions = string.Join(" ", list.Text.Select(g => FormattableString.Invariant($"{g.Text}@{g.X0:0.00},{g.Y0:0.00}")));
            return (positions, System.Text.Encoding.Latin1.GetString(after.GetStreamData((PdfStream)after.Pages[0].Dictionary.Get(PdfName.Contents))), result, file);
        }
        var a = After("Lutz");
        var b = After("Ott");
        Assert.Empty(a.Result.LengthsPartlyShown);
        Assert.Empty(b.Result.LengthsPartlyShown);
        Assert.Equal(a.Positions, b.Positions);
        Assert.Equal(a.Content, b.Content);
        using var original = Page(Paragraph("Ott", how));
        var (plain, _) = Redact(original, false, "Ott");
        Assert.Equal(StartOf(plain, "Herr")!.Value, StartOf(b.File, "Herr")!.Value, 0);   // the line starts where it did
        foreach (var word in new[] { "Herr", "sagt", "heute", "zu", "allen", "hier", "noch", "etwas" })
        {
            Assert.NotNull(StartOf(b.File, word));
            Assert.InRange(Ink(b.File, word), 0, 0.5);
        }
        // … and ends at the margin
        using var check = PdfDocument.Open(b.File);
        var line = PageInterpreter.Build(check, check.Pages[0]).Text.Where(g => Math.Abs(g.Y0 - 698) < 6 && g.Text.Trim().Length > 0).ToList();
        Assert.Equal(Margin, line.Max(g => Math.Max(g.X1, g.X2)), 0);
    }

    /// <summary>The gaps between consecutive words on the page (start of the next minus the right edge of the one before).</summary>
    private static List<double> Gaps(byte[] file, params string[] words)
    {
        using var doc = PdfDocument.Open(file);
        var text = PageText.Create(PageInterpreter.Build(doc, doc.Pages[0]).Text);
        var bounds = words.Select(w => text.Quads(Assert.Single(text.Find(w, matchCase: true)))).Select(q => (Left: q[0].X0, Right: q[^1].X1)).ToList();
        return [.. Enumerable.Range(1, bounds.Count - 1).Select(i => bounds[i].Left - bounds[i - 1].Right)];
    }

    private static string WordsApart(int tj, double x = 72) =>
        $"BT /F1 12 Tf {x.ToString(System.Globalization.CultureInfo.InvariantCulture)} 700 Td [(Heute){tj}(hat){tj}(Max){tj}(Mustermann){tj}(gesagt){tj}(dass){tj}(alles){tj}(gut){tj}(ist)] TJ ET";

    /// <summary>
    /// Words set apart only by numbers in TJ (no space characters), 0.29 and 0.30 em: after hiding the length of „Mustermann“, every gap after
    /// the bar is still 0.28 em — the gap from which the strictest reference reader keeps two words apart — but for the rounding of the word
    /// starts (at most half a step). With 0.2 em the line made room down to 2.4 pt and that reader wrote the words together; searching,
    /// copying and reading aloud failed there (decided on 3.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 451).
    /// </summary>
    [Theory]
    [InlineData(-290)]
    [InlineData(-300)]
    public void WordsSetApartOnlyByTj_GapsAfterTheWidenedGap_StayAReadersSpace(int tj)
    {
        using var doc = Page(WordsApart(tj));
        var (hidden, _) = Redact(doc, true, "Mustermann");
        Assert.All(Gaps(hidden, "gesagt", "dass", "alles", "gut", "ist"), gap => Assert.True(gap >= 0.28 * 12 - Redactor.PositionGrid / 2, $"Abstand {gap:0.###} pt"));
    }

    /// <summary>
    /// Words 0.34 em apart (set apart only by numbers in TJ), a word redacted: rounding the word starts to the grid keeps every gap at a third
    /// of an em but for half a step, with and without hiding lengths, wherever the line starts. With 0.28 em as the minimum a gap of 0.34 em
    /// could come out at 0.30 em — right at the limit of the strictest reference reader, with nothing to spare (design decision,
    /// 4.11.2026: a somewhat larger value, so that no program writes two words as one; docs/FEHLERGEDAECHTNIS.md, No. 458).
    /// </summary>
    [Theory]
    [InlineData(72.0, false)]
    [InlineData(72.13, false)]
    [InlineData(72.26, false)]
    [InlineData(72.39, false)]
    [InlineData(72.0, true)]
    [InlineData(72.26, true)]
    public void GapsOfAThirdOfAnEmOrMore_StayAtLeastAThird(double x, bool hide)
    {
        using var doc = Page(WordsApart(-340, x));
        var (redacted, _) = Redact(doc, hide, "Max");
        foreach (var gap in Gaps(redacted, "Heute", "hat").Concat(Gaps(redacted, "Mustermann", "gesagt", "dass", "alles", "gut", "ist")))
            Assert.True(gap >= 12.0 / 3 - Redactor.PositionGrid / 2, $"Abstand {gap:0.###} pt");
    }

    /// <summary>
    /// A gap already below 0.28 em (0.25 em) gives no room: it is not narrowed by the line making room; only the rounding of the two word
    /// starts moves it, by less than a step of the grid (No. 451). Before, the line made room down to 0.2 em (2.4 pt).
    /// </summary>
    [Fact]
    public void GapAlreadyBelowTheReadersSpace_NeitherShrunkNorWidened()
    {
        using var doc = Page(WordsApart(-250));
        var (hidden, result) = Redact(doc, true, "Mustermann");
        Assert.Equal([0], result.LengthsPartlyShown);
        Assert.All(Gaps(hidden, "gesagt", "dass", "alles", "gut", "ist"), gap => Assert.True(gap > 0.25 * 12 - Redactor.PositionGrid, $"Abstand {gap:0.###} pt"));
    }

    /// <summary>
    /// A line that loses a word sets each word start on the grid of 0.5 pt (No. 236) — also without hiding lengths. Rounded to the nearest
    /// point, a gap of 0.29 em could come out 0.5 pt smaller, below 0.28 em; now a word start goes to a point further right where the
    /// nearest would narrow the gap below that (1a-R, docs/FEHLERGEDAECHTNIS.md, No. 451), at most one step beyond the next. Gaps the line
    /// does not change keep 0.28 em but for at most half a step: along a chain of gaps just above it the moves to the right would add up
    /// without end, and the limit takes that little. Several starts of the line, so that the words fall on every place between two points.
    /// </summary>
    [Theory]
    [InlineData(72.0, false)]
    [InlineData(72.13, false)]
    [InlineData(72.26, false)]
    [InlineData(72.39, false)]
    [InlineData(72.13, true)]
    [InlineData(72.39, true)]
    public void EvenedLine_GapsJustAboveTheReadersSpace_NotRoundedBelowIt(double x, bool hide)
    {
        using var doc = Page(WordsApart(-290, x));
        var (redacted, _) = Redact(doc, hide, "Max");
        foreach (var gap in Gaps(redacted, "Heute", "hat"))
            Assert.True(gap >= 0.28 * 12 - Redactor.PositionGrid / 2, $"Abstand {gap:0.###} pt vor dem Balken");
        foreach (var gap in Gaps(redacted, "Mustermann", "gesagt", "dass", "alles", "gut", "ist"))
            Assert.True(gap >= 0.28 * 12 - Redactor.PositionGrid / 2, $"Abstand {gap:0.###} pt nach dem Balken");
    }

    /// <summary>
    /// Three labels on a line set apart by runs of spaces, each widened by a number in TJ — as a word processor writes tab stops —, the line
    /// ending where the line above ends: it is not justified. Taken for justified, it was set anew as a whole, and with „Medium“ redacted the
    /// labels around it moved by up to 32 pt (the public form test file text_field.pdf; docs/FEHLERGEDAECHTNIS.md, No. 453). The words
    /// before the bar stay where they were.
    /// </summary>
    [TestPdfsFact]
    public void LabelsSetApartByRunsOfSpaces_NotSetAnewAsJustified()
    {
        static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
        {
            for (var dir = new DirectoryInfo(Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "TestPDFs"))) return dir.FullName;
            return "";
        }
        var file = System.IO.File.ReadAllBytes(Path.Combine(Root(), "TestPDFs", "pdfium", "fx", "form", "text_field.pdf"));
        using var doc = PdfDocument.Open(file);
        var before = StartOf(file, "Thin")!.Value;
        var (hidden, _) = Redact(doc, true, "Medium");
        Assert.Null(StartOf(hidden, "Medium"));
        var after = StartOf(hidden, "Thin")!.Value;
        Assert.True(Math.Abs(after - before) <= Redactor.PositionGrid, $"„Thin“ von {before:0.##} nach {after:0.##} pt");
    }

    /// <summary>
    /// A single line with plain spaces and no room after it: the gap grows only as far as the spaces allow (here: hardly), nothing comes under
    /// the bar, and the page is named as one where the length is partly shown.
    /// </summary>
    [Fact]
    public void NoRoom_NothingCovered_PageNamed()
    {
        using var doc = Page("BT /F1 12 Tf 72 700 Td (Herr Mustermann sagt) Tj ET");
        var (hidden, result) = Redact(doc, true, "Mustermann");
        Assert.Equal([0], result.LengthsPartlyShown);
        Assert.InRange(Ink(hidden, "sagt"), 0, 0.5);
        Assert.InRange(Ink(hidden, "Herr"), 0, 0.5);
    }

    /// <summary>Ragged text with a longer line below: the first line grows into the free room after its end; its word spaces stay.</summary>
    [Fact]
    public void Ragged_LineGrowsIntoFreeRoom()
    {
        using var doc = Page("BT /F1 12 Tf 72 700 Td 20 TL (Herr Mustermann sagt heute) Tj T* (Dies ist eine viel laengere zweite Zeile im Absatz) Tj ET");
        var name = AreaOf(doc, "Mustermann");
        var (plain, _) = Redact(doc, false, "Mustermann");
        var (hidden, result) = Redact(doc, true, "Mustermann");
        Assert.Empty(result.LengthsPartlyShown);
        var moved = StartOf(hidden, "sagt")!.Value - StartOf(plain, "sagt")!.Value;
        Assert.True(WholeSteps(StartOf(hidden, "sagt")!.Value - name.Left));
        // the space between „sagt“ and „heute“ is as before: the line grew instead
        Assert.Equal(moved, StartOf(hidden, "heute")!.Value - StartOf(plain, "heute")!.Value, 0);
        Assert.Equal(StartOf(plain, "zweite")!.Value, StartOf(hidden, "zweite")!.Value, 3);   // the other line stays
    }

    /// <summary>
    /// Two redacted names in one line: the room for the first comes from the spaces before the second, so the second stays where its bar
    /// is; the words between them stay visible.
    /// </summary>
    [Fact]
    public void TwoNamesInALine_SecondBarStillRight_NothingCovered()
    {
        const string content = "BT /F1 12 Tf 72 700 Td 8 Tw (Herr Mustermann und Frau Schulz sagen heute zu allen hier) Tj ET";
        using var doc = Page(content);
        var (plain, _) = Redact(doc, false, "Mustermann", "Schulz");
        var (hidden, _) = Redact(doc, true, "Mustermann", "Schulz");
        foreach (var word in new[] { "Herr", "und", "Frau", "sagen", "heute", "zu", "allen", "hier" }) Assert.NotNull(StartOf(hidden, word));
        Assert.Null(StartOf(hidden, "Mustermann"));
        Assert.Null(StartOf(hidden, "Schulz"));
        Assert.Equal(StartOf(plain, "Frau")!.Value, StartOf(hidden, "Frau")!.Value, 0);   // the word before the second bar stays
        foreach (var word in new[] { "und", "Frau", "sagen", "hier" }) Assert.InRange(Ink(hidden, word), 0, 0.5);
    }

    /// <summary>
    /// Each word placed by its own Td (as typesetting programs write justified lines): the words after the name move by the same room as in one
    /// string — the move is added to where the content puts them, not counted twice.
    /// </summary>
    [Fact]
    public void WordsPlacedByTd_MoveRight_NotLeft()
    {
        const string content = "BT /F1 12 Tf 72 700 Td (Herr) Tj 33 0 Td (Mustermann) Tj 77 0 Td (sagt) Tj 33 0 Td (heute) Tj 40 0 Td (zu) Tj 22 0 Td (allen) Tj " +
                               "35 0 Td (hier) Tj 30 0 Td (noch) Tj 36 0 Td (etwas) Tj ET";
        using var doc = Page(content);
        var name = AreaOf(doc, "Mustermann");
        var (plain, _) = Redact(doc, false, "Mustermann");
        var (hidden, result) = Redact(doc, true, "Mustermann");
        // the gap grows to its step; the words before the name stand wider apart than a space — set with the name, as in a justified line,
        // they may give its width away: partly shown (docs/FEHLERGEDAECHTNIS.md, No. 354)
        Assert.NotEmpty(result.LengthsPartlyShown);
        var sagt = StartOf(hidden, "sagt")!.Value;
        Assert.True(sagt > StartOf(plain, "sagt")!.Value + 1, $"„sagt“ rückt nach rechts ({StartOf(plain, "sagt")} → {sagt})");
        Assert.True(WholeSteps(sagt - name.Left), $"Lücke {sagt - name.Left}");
        foreach (var word in new[] { "Herr", "sagt", "heute", "zu", "allen", "hier", "noch", "etwas" })
        {
            Assert.InRange(Ink(hidden, word), 0, 0.5);
            Assert.True(StartOf(hidden, word) >= StartOf(plain, word) - 0.3, $"„{word}“ nicht nach links");
        }
    }

    /// <summary>
    /// Two columns in one text object (as typesetting programs write them): a line of the left column grows only as far as the lines of its
    /// column reach — never into the gap towards the other column. Here the line with the name is the longest of its column: it has no room
    /// after its end, so the gap grows only by what its word spaces give, and the page is named.
    /// </summary>
    [Fact]
    public void TwoColumns_LineDoesNotGrowIntoTheGap()
    {
        using var doc = Page("BT /F1 12 Tf 72 720 Td 14 TL (Kurze Zeile) Tj T* (Herr Mustermann sagt heute) Tj " +
                            "300 6 Td (Rechts daneben hier) Tj ET");   // the other column: its baseline a little higher, as typesetting programs place it
        var (plain, _) = Redact(doc, false, "Mustermann");
        var (hidden, result) = Redact(doc, true, "Mustermann");
        Assert.Equal([0], result.LengthsPartlyShown);
        double End(byte[] file)
        {
            using var copy = PdfDocument.Open(file);
            var text = PageText.Create(PageInterpreter.Build(copy, copy.Pages[0]).Text);
            return text.Quads(Assert.Single(text.Find("heute", matchCase: true))).Max(q => q.X1);
        }
        Assert.True(End(hidden) <= End(plain) + 0.5, $"„heute“ endet bei {End(hidden)} statt {End(plain)}");
        Assert.Equal(StartOf(plain, "Rechts")!.Value, StartOf(hidden, "Rechts")!.Value, 1);
    }

    /// <summary>
    /// A character of other content where the bar would grow (a raised mark in a text object of its own, beside the name): the gap stays as it
    /// is, the mark stays visible, and the page is named.
    /// </summary>
    [Fact]
    public void OtherContentWhereTheBarWouldGrow_GapStays_PageNamed()
    {
        using var doc = Page(JustifiedLine + " BT /F1 7 Tf 175 708 Td (X) Tj ET");
        var (hidden, result) = Redact(doc, true, "Mustermann");
        Assert.Equal([0], result.LengthsPartlyShown);
        Assert.InRange(Ink(hidden, "X"), 0, 0.5);
    }

    /// <summary>
    /// Justified text: the next line takes over the character spacing of a line that loses a word (set to 0 there), ends its first text
    /// operator with a space and goes on with spacing of its own — the rest of that line stays where it was (it stood 2 pt to the left,
    /// docs/FEHLERGEDAECHTNIS.md, No. 260). With and without hiding the lengths.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NextLineTakesOverTheSpacing_ItsRestStaysInPlace(bool hide)
    {
        using var doc = Page("BT /F1 12 Tf 72 700 Td -0.4 Tc 1 Tw (Herr Mustermann sagt heute) Tj 0 -14 Td (dazu noch etwas ) Tj 0.3 Tc 0.5 Tw (zum Thema hier) Tj ET");
        var before = StartOf(File("BT /F1 12 Tf 72 700 Td -0.4 Tc 1 Tw (Herr Mustermann sagt heute) Tj 0 -14 Td (dazu noch etwas ) Tj 0.3 Tc 0.5 Tw (zum Thema hier) Tj ET"), "Thema")!.Value;
        var (redacted, _) = Redact(doc, hide, "Mustermann");
        Assert.Null(StartOf(redacted, "Mustermann"));
        Assert.Equal(before, StartOf(redacted, "Thema")!.Value, 1);
        Assert.Equal(StartOf(File("BT /F1 12 Tf 72 686 Td (x) Tj ET"), "x")!.Value, StartOf(redacted, "dazu")!.Value, 1);
    }

    /// <summary>
    /// Invisible text marked as hidden on the line of a redacted word, far beside it (no bar over that part of the line): its characters go,
    /// the redacted word too, the rest stays. (The case of docs/FEHLERGEDAECHTNIS.md, No. 257 — a removed run just beside the stepped strips
    /// of a mark — is checked by `schwaerzpruefung --laengen` on its file from DocLayNet.)
    /// </summary>
    [Fact]
    public void HiddenTextBesideARedactedWord_SameLine_Applied()
    {
        using var doc = Page("BT /F1 12 Tf 72 700 Td (Herr Mustermann sagt heute) Tj ET BT /F1 12 Tf 3 Tr 420 700 Td (unsichtbar steht hier) Tj ET");
        var spot = Assert.Single(HiddenText.Find(doc, doc.Pages[0]));
        var items = PageItem.From(doc);
        items[0] = items[0].WithElements([new RedactionElement { Rect = AreaOf(doc, "Mustermann") },
            RedactionElement.FromPageAreas(HiddenText.MarkAreas(spot)) with { Hidden = true }]);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, items, output, new PdfSaveOptions { ApplyRedactions = true, HideRedactedLengths = true, CompressStreams = false });
        Assert.Null(StartOf(output.ToArray(), "unsichtbar"));
        Assert.Null(StartOf(output.ToArray(), "Mustermann"));
        Assert.NotNull(StartOf(output.ToArray(), "heute"));
    }

    /// <summary>
    /// A font whose characters are drawn far smaller than its size says (Tf 58, squares of 0.2 em — files of some mainframe printing systems
    /// look so): the step follows the characters as drawn, not the 58 pt em (docs/FEHLERGEDAECHTNIS.md, No. 263) — before, the words after
    /// the name moved some 30 pt.
    /// </summary>
    [Fact]
    public void SmallCharactersOfALargeFont_StepFollowsWhatIsDrawn()
    {
        var pdf = TestPdf.Simple(1);
        var square = pdf.Add(new PdfStream(new PdfDictionary(), "200 0 0 0 200 200 d1 0 0 200 200 re f"u8.ToArray()));
        var space = pdf.Add(new PdfStream(new PdfDictionary(), "250 0 0 0 0 0 d1"u8.ToArray()));
        var widths = new PdfArray { PdfInteger.Of(250) };
        for (var c = 33; c <= 70; c++) widths.Add(PdfInteger.Of(c >= 65 ? 200 : 0));
        var font = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type3")), ("FontBBox", TestPdf.Box(0, 0, 250, 200)),
            ("FontMatrix", new PdfArray { new PdfReal(0.001), PdfInteger.Of(0), PdfInteger.Of(0), new PdfReal(0.001), PdfInteger.Of(0), PdfInteger.Of(0) }),
            ("CharProcs", TestPdf.Dict(("A", TestPdf.Ref(square)), ("B", TestPdf.Ref(square)), ("C", TestPdf.Ref(square)), ("D", TestPdf.Ref(square)),
                ("E", TestPdf.Ref(square)), ("F", TestPdf.Ref(square)), ("space", TestPdf.Ref(space)))),
            ("Encoding", TestPdf.Dict(("Type", TestPdf.N("Encoding")), ("Differences", new PdfArray { PdfInteger.Of(32), TestPdf.N("space"),
                PdfInteger.Of(65), TestPdf.N("A"), TestPdf.N("B"), TestPdf.N("C"), TestPdf.N("D"), TestPdf.N("E"), TestPdf.N("F") }))),
            ("FirstChar", PdfInteger.Of(32)), ("LastChar", PdfInteger.Of(70)), ("Widths", widths)));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("Font", TestPdf.Dict(("T3", TestPdf.Ref(font))))));
        pdf.Replace(4, new PdfStream(new PdfDictionary(), "BT /T3 58 Tf 72 700 Td 20 Tw (AAA B CD EF FE DC) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var (plain, _) = Redact(doc, false, "B");
        var (hidden, _) = Redact(doc, true, "B");
        var moved = StartOf(hidden, "CD")!.Value - StartOf(plain, "CD")!.Value;
        Assert.InRange(moved, 0.5, 40);   // the step from squares 11.6 pt high (2 em of at most 1.6 × 11.6 pt: some 28 pt), not from the 58 pt em (116 pt: some 70 pt)
    }

    /// <summary>
    /// A name in a line of 50 000 words (as a damaged file can bring it, where all its text lies on one baseline), lengths hidden: the check of
    /// the moves runs along the line with one row of the characters left out — a table of every character before against every one after
    /// ran out of memory, and the page could not be saved (docs/FEHLERGEDAECHTNIS.md, No. 376). The name goes, the rest stays.
    /// </summary>
    [Fact]
    public void NameInALineOfFiftyThousandWords_RedactedWithHiddenLengths()
    {
        using var doc = Page($"BT /F1 12 Tf 72 700 Td (Herr Mustermann sagt {string.Concat(Enumerable.Repeat("a ", 50_000))}) Tj ET");
        var (file, _) = Redact(doc, true, "Mustermann");
        using var saved = PdfDocument.Open(file);
        var text = PageText.Create(PageInterpreter.Build(saved, saved.Pages[0]).Text).Text;
        Assert.DoesNotContain("Mustermann", text);
        Assert.Equal(50_000 + 1, text.Count(c => c == 'a'));   // and the one of „sagt“
    }

    /// <summary>
    /// Two columns in one text object, the right one reached by its own line start (Td across the line) onto the baseline of the left one:
    /// when the left line loses a word, the words of the right column stay exactly where they were — their place does not follow from the
    /// removed word; rounded to the grid as words of the line, they moved by up to a quarter point (DocLayNet 002e8522;
    /// docs/FEHLERGEDAECHTNIS.md, No. 874).
    /// </summary>
    [Fact]
    public void SecondColumnOnTheSameBaseline_OwnLineStart_StaysExactly()
    {
        const string content = "BT /F1 12 Tf 72 700 Td 14 TL (Herr Mustermann sagt heute) Tj T* (Zweite Zeile links) Tj " +
                               "250.37 14 Td (Rechts oben steht) Tj T* (Rechts unten hier) Tj ET";
        using var doc = Page(content);
        var original = File(content);
        var (hidden, _) = Redact(doc, true, "Mustermann");
        Assert.Null(StartOf(hidden, "Mustermann"));
        foreach (var word in new[] { "oben", "steht", "unten", "hier" })
            Assert.Equal(StartOf(original, word)!.Value, StartOf(hidden, word)!.Value, 3);
    }

    /// <summary>
    /// A tall bracket set from three glyphs one above the other in the text object of a line (as typesetting programs set it beside a
    /// formula), its lower piece on the baseline of the line, just after its last word: when the line loses a word, the pieces stay
    /// together where they were — the lower one moved with the words of the line, by the grid, and broke away from the two above it
    /// (DocLayNet 5b5658d7; docs/FEHLERGEDAECHTNIS.md, No. 872).
    /// </summary>
    [Fact]
    public void TallBracketOfStackedGlyphs_LowerPieceOnTheLine_StaysWithTheOthers()
    {
        // „Herr Mustermann sagt“ ends at 191.36 pt; the bracket begins half an em after it, off the grid of 0.5 pt
        using var doc = Page("BT /F1 12 Tf 72 700 Td (Herr Mustermann sagt) Tj 125.737 22 Td 11 TL (|) Tj T* (|) Tj T* (|) Tj ET");
        static List<(double X, double Y)> Bars(byte[] file)
        {
            using var d = PdfDocument.Open(file);
            return [.. PageInterpreter.Build(d, d.Pages[0]).Text.Where(g => g.Text == "|").Select(g => (X: g.X0, Y: g.Y0)).OrderByDescending(p => p.Y)];
        }
        var before = Bars(File("BT /F1 12 Tf 72 700 Td (Herr Mustermann sagt) Tj 125.737 22 Td 11 TL (|) Tj T* (|) Tj T* (|) Tj ET"));
        var (hidden, _) = Redact(doc, true, "Mustermann");
        var after = Bars(hidden);
        Assert.Equal(3, before.Count);
        Assert.Equal(3, after.Count);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(before[i].X, after[i].X, 3);
            Assert.Equal(before[i].Y, after[i].Y, 3);
        }
        Assert.Null(StartOf(hidden, "Mustermann"));
    }
}
