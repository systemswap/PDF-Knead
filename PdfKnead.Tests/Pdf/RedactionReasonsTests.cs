using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Reasons of redactions (KONZEPT.md, section 5.6, B-14): which bars carry which reason, the reason in the bar as outlines (no text in the bar),
/// the note, the protocol pages (never the content), the warning about reasons that name what is redacted, and the reason kept with a mark.
/// </summary>
public sealed class RedactionReasonsTests
{
    private static PageItem Page(params PageElement[] marks)
    {
        var page = BlankPage.Create(595, 842);
        return page.WithElements(marks);
    }

    [Fact]
    public void Bars_OwnReasonOrTheDefault_HiddenMarksAndMarksWithoutAnyLeftOut()
    {
        var own = new RedactionElement { Rect = new PdfRectangle(100, 700, 200, 712), Reason = "Schutz Dritter" };
        var plain = new RedactionElement { Rect = new PdfRectangle(100, 600, 200, 612) };
        var hidden = new RedactionElement { Rect = new PdfRectangle(100, 500, 200, 512), Hidden = true, Reason = "x" };
        var bars = RedactionReasons.Bars([Page(own, plain, hidden)], "Art. 6 DSGVO");
        Assert.Equal(["Schutz Dritter", "Art. 6 DSGVO"], bars.Select(b => b.Reason));
        Assert.Single(RedactionReasons.Bars([Page(own, plain)], null));
        // a mark over several lines: one bar, its largest area
        var lines = RedactionElement.FromPageAreas([new PdfRectangle(100, 700, 300, 712), new PdfRectangle(100, 686, 150, 698)]) with { Reason = "R" };
        Assert.Equal(new PdfRectangle(100, 700, 300, 712), Assert.Single(RedactionReasons.Bars([Page(lines)], null)).Area);
    }

    [Fact]
    public void Clean_OneLine_NoControlCharacters_Limited()
    {
        Assert.Null(RedactionReasons.Clean("  \n "));
        Assert.Equal("a b", RedactionReasons.Clean("a\r\nb\u0007"));
        Assert.Equal(RedactionReasons.MaxLength, RedactionReasons.Clean(new string('x', 1000))!.Length);
    }

    [Fact]
    public void Labels_OutlinesInTheBar_NoText_TooSmallBarsLeftOut()
    {
        var box = RectD.FromLtrb(0, 0, 595, 842);
        var form = RedactionReasons.Labels([new ReasonedBar(0, new PdfRectangle(100, 700, 300, 714), "Art. 6 DSGVO")], box, 0xFFFFFF);
        Assert.NotNull(form);
        var content = System.Text.Encoding.ASCII.GetString(form.RawData!);
        Assert.Contains("1 1 1 rg", content);
        Assert.DoesNotContain("Tj", content);
        Assert.DoesNotContain("BT", content);
        // drawn inside the bar
        var numbers = content.Split((char[])[' ', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(t => double.TryParse(t, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : double.NaN).ToList();
        Assert.Contains(numbers, v => v is > 100 and < 300);
        Assert.Null(RedactionReasons.Labels([new ReasonedBar(0, new PdfRectangle(100, 700, 103, 703), "Art. 6 DSGVO")], box, 0xFFFFFF));
    }

    [Fact]
    public void Revealing_WordsOfTheRedactedText_Found()
    {
        Assert.Equal(["Mustermann"], RedactionReasons.Revealing(["Gehalt Mustermann", "Art. 6 DSGVO"], ["Max Mustermann", "4711"]));
        Assert.Empty(RedactionReasons.Revealing(["Art. 6 DSGVO", null], ["Max Mustermann"]));
    }

    [Fact]
    public void Note_ClosedTextAnnotationWithTheReason()
    {
        var note = RedactionReasons.Note(new ReasonedBar(0, new PdfRectangle(100, 700, 200, 712), "Schutz Dritter"), "MM", DateTimeOffset.Now);
        Assert.Equal("Text", note.GetName(PdfName.Subtype)!.Value);
        Assert.Contains("Schutz Dritter", note.GetString(PdfName.Get("Contents"))!.ToText());
        Assert.Equal("MM", note.GetString(PdfName.Get("T"))!.ToText());
        Assert.False(note.GetBoolean(PdfName.Get("Open")));
    }

    /// <summary>
    /// A long protocol tagged (6.6 i, No. 658): one Table over all its pages with a row per bar after the head row, the head repeated on a
    /// further page as an artifact; nothing untagged on any page.
    /// </summary>
    [Fact]
    public void Protocol_Tagged_SeveralPages_OneTableNothingUntagged()
    {
        var bars = Enumerable.Range(0, 120).Select(i => new ReasonedBar(i % 3, new PdfRectangle(100, 700 - i % 40 * 15, 200, 712 - i % 40 * 15), $"Grund {i}")).ToList();
        using var pdf = RedactionReasons.Protocol(bars, [(595, 842), (595, 842), (842, 595)], "akte.pdf", null, new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero),
            595, 842, tagged: true);
        using var doc = pdf.OpenDocument();
        Assert.True(doc.PageCount >= 2);
        Assert.All(Core.Pdf.Accessibility.TaggedContent.Analyse(doc).Pages, p => Assert.Equal(0, p.Untagged + p.McidsWithoutElement));
        var section = doc.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot"))!.Get(PdfName.Get("K")) switch
        {
            PdfArray one => (PdfDictionary)one.Get(0)!,
            var single => (PdfDictionary)single!,
        };
        if (section.GetName(PdfName.Get("S"))!.Value == "Document") section = (PdfDictionary)((PdfArray)section.Get(PdfName.Get("K"))!).Get(0)!;
        var parts = (PdfArray)section.Get(PdfName.Get("K"))!;
        var tables = Enumerable.Range(0, parts.Count).Select(i => (PdfDictionary)parts.Get(i)!).Where(e => e.GetName(PdfName.Get("S"))!.Value == "Table").ToList();
        Assert.Equal(121, ((PdfArray)Assert.Single(tables).Get(PdfName.Get("K"))!).Count);
    }

    [Fact]
    public void Protocol_PagesWithPagePlaceAndReason_SeveralPagesWhenLong()
    {
        var bars = Enumerable.Range(0, 120).Select(i => new ReasonedBar(i % 3, new PdfRectangle(100, 700 - i % 40 * 15, 200, 712 - i % 40 * 15), $"Grund {i}")).ToList();
        using var pdf = RedactionReasons.Protocol(bars, [(595, 842), (595, 842), (842, 595)], "akte.pdf", "MM", new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero), 595, 842);
        using var doc = pdf.OpenDocument();
        Assert.True(doc.PageCount >= 2);
        var all = string.Join("\n", doc.Pages.Select(p => PageText.Create(PageInterpreter.BuildText(doc, p), 0).Text));
        Assert.Contains("Schwärzungsprotokoll", all);
        Assert.Contains("akte.pdf", all);
        Assert.Contains("MM", all);
        Assert.Contains("Grund 0", all);
        Assert.Contains("Grund 119", all);
        Assert.Contains("Seite 3", all);
        Assert.Contains("oben links", all);
        Assert.Equal(595, doc.Pages[0].Width, 0);
    }

    [Fact]
    public void Reason_KeptWithTheMarkWhenSaved()
    {
        var page = Page(new RedactionElement { Rect = new PdfRectangle(100, 700, 200, 712), Reason = "Schutz Dritter" },
            new RedactionElement { Rect = new PdfRectangle(100, 600, 200, 612) });
        using var output = new MemoryStream();
        PdfWriter.Write(page.Source, [page], output, new PdfSaveOptions());
        using var doc = PdfDocument.Open(output.ToArray());
        var marks = PageItem.From(doc)[0].Elements.OfType<RedactionElement>().OrderByDescending(m => m.Rect.Top).ToList();
        Assert.Equal(["Schutz Dritter", null], marks.Select(m => m.Reason));
    }

    /// <summary>
    /// Redaction codes (wish of 4.11.2026): kept with the mark — code, legal basis, „Code im Balken“ — and in the mark other editors read
    /// (/OverlayText the code, /Contents code and reason); an own reason without a code.
    /// </summary>
    [Fact]
    public void Code_KeptWithTheMark_AndAsOverlayTextForOtherEditors()
    {
        var code = RedactionCodes.Load(Path.Combine(Path.GetTempPath(), "pdfknead-keine-codes")).Single(s => s.Id == "de").Codes.Single(c => c.Code == "§ 5 IFG");
        var coded = ReasonChoice.Of(code, inBar: true).On(new RedactionElement { Rect = new PdfRectangle(100, 700, 200, 712) });
        var quiet = ReasonChoice.Of(code, inBar: false).On(new RedactionElement { Rect = new PdfRectangle(100, 650, 200, 662) });
        var own = new ReasonChoice("Schutz Dritter").On(new RedactionElement { Rect = new PdfRectangle(100, 600, 200, 612) });
        var page = Page(coded, quiet, own);
        using var output = new MemoryStream();
        PdfWriter.Write(page.Source, [page], output, new PdfSaveOptions());
        using var doc = PdfDocument.Open(output.ToArray());
        var marks = PageItem.From(doc)[0].Elements.OfType<RedactionElement>().OrderByDescending(m => m.Rect.Top).ToList();
        Assert.Equal(["§ 5 IFG", "§ 5 IFG", null], marks.Select(m => m.ReasonCode));
        Assert.Equal("Informationsfreiheitsgesetz des Bundes (IFG), § 5", marks[0].ReasonBasis);
        Assert.Equal([true, false, true], marks.Select(m => m.ReasonInBar));
        Assert.Equal(["§ 5 IFG", null, "Schutz Dritter"], marks.Select(m => m.BarText));
        Assert.StartsWith("Schutz personenbezogener Daten Dritter: ", marks[0].Reason);
        var annots = doc.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!;
        var redacts = Enumerable.Range(0, annots.Count).Select(i => annots.Get(i)).OfType<PdfDictionary>()
            .Where(a => a.GetName(PdfName.Subtype)?.Value == "Redact").OrderByDescending(a => a.Get(PdfName.Get("Rect")) is { } r ? PdfRectangle.From(r)?.Top ?? 0 : 0).ToList();
        Assert.Equal(["§ 5 IFG", null, "Schutz Dritter"], redacts.Select(a => a.GetString(PdfName.Get("OverlayText"))?.ToText()));
        // what it is, then code and reason (docs/FEHLERGEDAECHTNIS.md, No. 631: before, code and reason alone)
        Assert.Matches("^(Zum Schwärzen markiert|Marked for redaction) — § 5 IFG — Schutz personenbezogener Daten Dritter", redacts[0].GetString(PdfName.Contents)!.ToText());
    }

    /// <summary>
    /// The bar gets the code, not the whole reason; where it does not fit even at the smallest size it is left out there and the bar is
    /// reported (the warning, wish of 4.11.2026); the note and the protocol name code, reason and legal basis.
    /// </summary>
    [Fact]
    public void Code_InTheBar_TooSmallReported_ProtocolWithReasonAndBasis()
    {
        var mark = new RedactionElement
        {
            Rect = new PdfRectangle(100, 700, 300, 714), Reason = "Schutz personenbezogener Daten Dritter: …", ReasonCode = "§ 5 IFG",
            ReasonBasis = "Informationsfreiheitsgesetz des Bundes (IFG), § 5",
        };
        var tiny = mark with { Rect = new PdfRectangle(100, 600, 110, 604) };
        var bars = RedactionReasons.Bars([Page(mark, tiny)], null);
        Assert.Equal(["§ 5 IFG", "§ 5 IFG"], bars.Select(b => b.BarText));
        Assert.True(RedactionReasons.FitsInBar("§ 5 IFG", mark.Rect));
        Assert.False(RedactionReasons.FitsInBar("§ 5 IFG", tiny.Rect));
        var tooSmall = new List<ReasonedBar>();
        Assert.NotNull(RedactionReasons.Labels(bars, RectD.FromLtrb(0, 0, 595, 842), 0xFFFFFF, tooSmall: tooSmall));
        Assert.Equal(tiny.Rect, Assert.Single(tooSmall).Area);
        // not in the bar: nothing written there, nothing reported
        Assert.Null(RedactionReasons.Labels([bars[0] with { InBar = false }], RectD.FromLtrb(0, 0, 595, 842), 0xFFFFFF));
        Assert.Contains("§ 5 IFG — Schutz personenbezogener Daten Dritter", RedactionReasons.Note(bars[0], null, DateTimeOffset.Now).GetString(PdfName.Contents)!.ToText());
        using var pdf = RedactionReasons.Protocol(bars, [(595, 842)], "akte.pdf", null, DateTimeOffset.Now, 595, 842);
        using var doc = pdf.OpenDocument();
        var all = string.Join("\n", doc.Pages.Select(p => PageText.Create(PageInterpreter.BuildText(doc, p), 0).Text)).Replace("\n", " ");
        Assert.Contains("§ 5 IFG", all);
        Assert.Contains("Informationsfreiheitsgesetz", all);
    }
}

/// <summary>The code sets (wish of 4.11.2026): built in for four countries, replaced or added by the user's own files, read leniently.</summary>
public sealed class RedactionCodesTests
{
    [Fact]
    public void BuiltIn_FourSets_WithMeaningBasisAndDefaultsByKindOfHit()
    {
        var sets = RedactionCodes.Load(Path.Combine(Path.GetTempPath(), "pdfknead-keine-codes"));
        Assert.Equal(["de", "eu", "at", "ch"], sets.Select(s => s.Id));
        Assert.Equal(["Deutschland (Bund)", "Europäische Union", "Österreich", "Schweiz (Bund)"], sets.Select(s => s.Country));
        foreach (var set in sets)
        {
            Assert.True(set.Codes.Count >= 9, set.Id);
            Assert.All(set.Codes, c => Assert.True(c.Short.Length > 0 && c.Meaning.Length > 0 && c.Basis.Length > 0, $"{set.Id} {c.Code}"));
            Assert.Equal(set.Codes.Count, set.Codes.Select(c => c.Code).Distinct().Count());
            // e-mail, phone, IBAN, card and date have their default code in every set
            foreach (var kind in (RedactionFinds[])[RedactionFinds.Email, RedactionFinds.Phone, RedactionFinds.Iban, RedactionFinds.Card, RedactionFinds.Date])
                Assert.NotNull(set.For(kind));
            Assert.Null(set.For(RedactionFinds.None));
        }
        Assert.Equal("Art. 6 DSGVO", sets[0].For(RedactionFinds.Iban)!.Code);
        Assert.Equal("§ 6 Z 7a IFG", sets[2].For(RedactionFinds.Email)!.Code);
    }

    [Fact]
    public void OwnFiles_ReplaceTheBuiltInOne_AndAddSets_ReadLeniently()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pdfknead-codes-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "de.txt"), "# eigene\nLand\tDeutschland (Behörde X)\nStand\t1/2027\nA1\tKurz\tBedeutung\tGrundlage\tIBAN; Telefon\n\nB2\n\tohne Code\n");
            File.WriteAllText(Path.Combine(folder, "firma.txt"), "Land\tFirma\nK1\tIntern\n");
            var sets = RedactionCodes.Load(folder);
            Assert.Equal(["de", "eu", "at", "ch", "firma"], sets.Select(s => s.Id));
            var de = sets[0];
            Assert.True(de.Own);
            Assert.Equal(("Deutschland (Behörde X)", "1/2027"), (de.Country, de.Version));
            Assert.Equal(["A1", "B2"], de.Codes.Select(c => c.Code));
            Assert.Equal(RedactionFinds.Iban | RedactionFinds.Phone, de.Codes[0].Finds);
            Assert.Equal(("", "", ""), (de.Codes[1].Short, de.Codes[1].Meaning, de.Codes[1].Basis));
            Assert.Equal("Firma", sets[4].Country);
            // copied for editing: the built-in text, an existing file is kept
            var copied = RedactionCodes.CopyForEditing("at", folder);
            Assert.Contains("Österreich", File.ReadAllText(copied));
            Assert.Equal(Path.Combine(folder, "de.txt"), RedactionCodes.CopyForEditing("de", folder));
            Assert.Contains("Behörde X", File.ReadAllText(Path.Combine(folder, "de.txt")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("de-DE", "de")]
    [InlineData("de-AT", "at")]
    [InlineData("de-CH", "ch")]
    [InlineData("fr-CH", "ch")]
    [InlineData("en-US", "eu")]
    [InlineData(null, "eu")]
    public void DefaultSet_ByTheLanguageAndRegion(string? culture, string id) => Assert.Equal(id, RedactionCodes.DefaultFor(culture));

    /// <summary>
    /// A long reason with its code in brackets, a short bar (one over a phone number in a footer: 39 × 7.6 pt): the code goes into the bar,
    /// drawn exactly as the code alone — before nothing was written there, though the code fits (report of 5.11.2026 with an offer: the bars
    /// over phone and e-mail at the bottom stayed empty; docs/FEHLERGEDAECHTNIS.md, No. 529). A reason without a code in brackets that does not
    /// fit is still left out and counted.
    /// </summary>
    [Fact]
    public void LongReasonInAShortBar_ItsCodeWritten()
    {
        var box = RectD.FromLtrb(0, 0, 595, 842);
        var bar = new PdfRectangle(86, 20.7, 124.7, 28.3);
        var whole = RedactionReasons.Labels([new ReasonedBar(0, bar, "Personenbezogene Daten (Art. 6 DSGVO)")], box, 0xFFFFFF);
        var code = RedactionReasons.Labels([new ReasonedBar(0, bar, "Art. 6 DSGVO")], box, 0xFFFFFF);
        Assert.NotNull(code);
        Assert.NotNull(whole);
        Assert.Equal(code.RawData, whole.RawData);
        Assert.True(RedactionReasons.FitsInBar("Personenbezogene Daten (Art. 6 DSGVO)", bar));
        // a wide bar keeps the reason as it is
        var wide = new PdfRectangle(100, 700, 400, 714);
        Assert.NotEqual(RedactionReasons.Labels([new ReasonedBar(0, wide, "Art. 6 DSGVO")], box, 0xFFFFFF)!.RawData,
            RedactionReasons.Labels([new ReasonedBar(0, wide, "Personenbezogene Daten (Art. 6 DSGVO)")], box, 0xFFFFFF)!.RawData);
        // without a code in brackets: left out and counted
        var tooSmall = new List<ReasonedBar>();
        Assert.Null(RedactionReasons.Labels([new ReasonedBar(0, bar, "Schutz der Rechte Dritter nach dem Vertrag")], box, 0xFFFFFF, tooSmall: tooSmall));
        Assert.Single(tooSmall);
        Assert.Equal("§ 6 IFG", RedactionReasons.ShortForm("Betriebs- und Geschäftsgeheimnis (§ 6 IFG)"));
        Assert.Null(RedactionReasons.ShortForm("Schutz Dritter"));
        Assert.Null(RedactionReasons.ShortForm("(Art. 6 DSGVO)"));
    }

    /// <summary>
    /// Bars over a footer in small print, 6 pt high: the code goes in at the smallest size (up to 80 % of the bar's height), a long reason as
    /// its code — before 62 % of the height gave less than 4 pt and nothing was written, not even the code (report of 5.11.2026 with a second
    /// offer: 9 of its 12 bars stayed empty; docs/FEHLERGEDAECHTNIS.md, No. 530). Lower still (4 pt) stays left out.
    /// </summary>
    [Fact]
    public void LowBarInAFooter_CodeWritten()
    {
        var box = RectD.FromLtrb(0, 0, 595, 842);
        var low = new PdfRectangle(221.7, 103.7, 266.5, 109.7);
        Assert.NotNull(RedactionReasons.Labels([new ReasonedBar(0, low, "Art. 6 DSGVO")], box, 0xFFFFFF));
        Assert.True(RedactionReasons.FitsInBar("Personenbezogene Daten (Art. 6 DSGVO)", low));
        // drawn within the bar
        var form = RedactionReasons.Labels([new ReasonedBar(0, low, "Art. 6 DSGVO")], box, 0xFFFFFF)!;
        var lines = System.Text.Encoding.ASCII.GetString(form.RawData!).Split('\n');
        var ys = lines.Where(l => l.EndsWith(" m") || l.EndsWith(" l"))
            .Select(l => double.Parse(l.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.All(ys, y => Assert.InRange(y, low.Bottom, low.Top));
        Assert.False(RedactionReasons.FitsInBar("Art. 6 DSGVO", new PdfRectangle(100, 600, 160, 604)));
    }
}
