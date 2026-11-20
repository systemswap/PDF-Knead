using PdfKnead.Core.Forms.Recognition;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Recognising form fields without a model (KONZEPT.md, section 5.9a): lines, boxes, table cells, check boxes, choices, combs and signature
/// lines with their labels, kinds and names; nothing where fields lie or where text is filled in; the same head on two pages linked.
/// </summary>
public sealed class FormRecognitionTests
{
    private static byte[] Page(string content, int pages = 1, string extra = "")
    {
        var objects = new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = $"<</Type/Pages/Kids[{string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{10 + i} 0 R"))}]/Count {pages}/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>",
            [4] = ("", content),
        };
        for (var i = 0; i < pages; i++) objects[10 + i] = $"<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</F1 3 0 R>>>>{extra}>>";
        return CraftedFileTests.Build(objects);
    }

    private static List<FieldProposal> Recognize(byte[] data, params PdfRectangle[] occupied)
    {
        using var doc = PdfDocument.Open(data);
        return FormRecognizer.Recognize([.. doc.Pages.Select(p => new FormRecognizer.PageInput(doc, p, occupied))], new HashSet<string>());
    }

    [Fact]
    public void LineWithLabel_TextFieldOnTheLine_NamedAfterTheLabel()
    {
        var found = Assert.Single(Recognize(Page("BT /F1 10 Tf 72 700 Td (Name:) Tj ET 0.7 w 110 698 m 400 698 l S")));
        Assert.Equal(FormFieldKind.Text, found.Spec.Kind);
        Assert.Equal("Name", found.Spec.Name);
        Assert.Equal("Name", found.Spec.Tooltip);
        Assert.Equal(ProposalSource.Line, found.Source);
        Assert.InRange(found.Rect.Bottom, 698, 700);
        Assert.InRange(found.Rect.Left, 110, 112);
        Assert.InRange(found.Rect.Height, 12, 19);
        Assert.Null(found.Spec.BorderColor);   // the page draws the line
        Assert.Null(found.Spec.FillColor);
    }

    [Fact]
    public void Underscores_AndLabelsThatMeanAKind()
    {
        var found = Recognize(Page("BT /F1 10 Tf 72 700 Td (Geburtsdatum: ______________________) Tj ET " +
                                   "BT /F1 10 Tf 72 660 Td (Betrag:) Tj ET 0.7 w 120 658 m 300 658 l S " +
                                   "BT /F1 10 Tf 72 620 Td (Bemerkungen) Tj ET 0.7 w 72 560 200 50 re S"));
        Assert.Equal(3, found.Count);
        Assert.Equal(FieldFormatKind.Date, found[0].Spec.Format.Kind);
        Assert.Equal("dd.mm.yyyy", found[0].Spec.Format.Pattern);
        Assert.Equal(FieldFormatKind.Number, found[1].Spec.Format.Kind);
        Assert.Equal(2, found[1].Spec.Format.Decimals);
        Assert.True(found[2].Spec.Multiline);
        Assert.Equal("Bemerkungen", found[2].Spec.Name);
    }

    [Fact]
    public void BoxWithItsLabelInside_TheFieldBelowTheLabel()
    {
        var found = Assert.Single(Recognize(Page("0.7 w 72 600 200 34 re S BT /F1 7 Tf 75 626 Td (Kundennummer) Tj ET")));
        Assert.Equal("Kundennummer", found.Spec.Name);
        Assert.True(found.Rect.Top < 626, found.Rect.Top.ToString());
        Assert.InRange(found.Rect.Bottom, 600, 603);
    }

    [Fact]
    public void Table_EmptyCells_NamedByColumnAndRow()
    {
        // three columns (Artikel, Menge, Preis), a head row and two rows
        var content = "0.6 w 72 700 m 372 700 l S 72 682 m 372 682 l S 72 664 m 372 664 l S 72 646 m 372 646 l S " +
                      "72 646 m 72 700 l S 172 646 m 172 700 l S 272 646 m 272 700 l S 372 646 m 372 700 l S " +
                      "BT /F1 9 Tf 75 687 Td (Artikel) Tj ET BT /F1 9 Tf 175 687 Td (Menge) Tj ET BT /F1 9 Tf 275 687 Td (Preis) Tj ET " +
                      "BT /F1 9 Tf 75 669 Td (Schrauben) Tj ET BT /F1 9 Tf 75 651 Td (Muttern) Tj ET";
        var found = Recognize(Page(content));
        Assert.Equal(["Menge – Schrauben", "Preis – Schrauben", "Menge – Muttern", "Preis – Muttern"], found.Select(f => f.Spec.Name));
        Assert.All(found, f => Assert.Equal(ProposalSource.Cell, f.Source));
        Assert.Equal(FieldFormatKind.Number, found[1].Spec.Format.Kind);
    }

    [Fact]
    public void CheckBoxes_AndAChoice_YesNoBecomesOneGroup()
    {
        var content = "0.7 w 72 700 10 10 re S BT /F1 10 Tf 88 702 Td (Newsletter) Tj ET " +
                      "BT /F1 10 Tf 72 660 Td (Sind Sie Kunde?) Tj ET 0.7 w 200 658 10 10 re S BT /F1 10 Tf 214 660 Td (ja) Tj ET " +
                      "0.7 w 250 658 10 10 re S BT /F1 10 Tf 264 660 Td (nein) Tj ET";
        var found = Recognize(Page(content));
        var box = Assert.Single(found, f => f.Spec.Kind == FormFieldKind.CheckBox);
        Assert.Equal("Newsletter", box.Spec.Name);
        var options = found.Where(f => f.Spec.Kind == FormFieldKind.Radio).ToList();
        Assert.Equal(2, options.Count);
        Assert.All(options, o => Assert.Equal("Sind Sie Kunde?", o.Spec.Name));
        Assert.Equal(["ja", "nein"], options.Select(o => o.Spec.ExportValue));
        Assert.All(options, o => Assert.True(o.Spec.NoToggleToOff));
    }

    [Fact]
    public void RowOfSmallBoxes_IsACombField()
    {
        var boxes = string.Join(" ", Enumerable.Range(0, 5).Select(i => $"{120 + i * 14} 698 14 16 re S"));
        var found = Assert.Single(Recognize(Page($"BT /F1 10 Tf 72 702 Td (PLZ) Tj ET 0.7 w {boxes}")));
        Assert.Equal(ProposalSource.Comb, found.Source);
        Assert.True(found.Spec.Comb);
        Assert.Equal(5, found.Spec.MaxLength);
        Assert.Equal("PLZ", found.Spec.Name);
    }

    /// <summary>
    /// Rows of boxes drawn as lines, as printed forms of authorities set them: each box open above with its sides rising from its bottom line,
    /// or closed by a line with the label just under it, the label at the top left inside — named after its own label, not after the label
    /// of the box below that starts right under the line (docs/FEHLERGEDAECHTNIS.md, No. 346); a short label at the left end limits the field.
    /// </summary>
    [Fact]
    public void RowsOfBoxesDrawnAsLines_NamedAfterTheLabelInside()
    {
        string Box(double left, double right, double bottom, string label) =>
            $"0.7 w {left} {bottom} m {right} {bottom} l S {left} {bottom} m {left} {bottom + 30} l S {right} {bottom} m {right} {bottom + 30} l S " +
            $"BT /F1 7 Tf {left + 3} {bottom + 21} Td ({label}) Tj ET ";
        var content = Box(72, 300, 600, "Familienname") + Box(310, 520, 600, "Titel") +
                      Box(72, 300, 570, "Vorname") + Box(310, 520, 570, "Geburtsname und Familienname") +
                      // under a heading, closed above by a line, no sides: the label just under it
                      "BT /F1 10 Tf 72 533 Td (2 Angaben zur Wohnung) Tj ET 0.7 w 72 530 m 520 530 l S BT /F1 7 Tf 75 523 Td (Anschrift) Tj ET 0.7 w 72 500 m 520 500 l S BT /F1 7 Tf 75 492 Td (Hinweis zum Datenschutz) Tj ET";
        var found = Recognize(Page(content));
        string NameAt(double left, double bottom) => found.SingleOrDefault(f => Math.Abs(f.Rect.Left - left) < 3 && Math.Abs(f.Rect.Bottom - bottom) < 3)?.Spec.Name ??
            string.Join(" | ", found.Select(f => $"{f.Spec.Name} {f.Rect.Left:0} {f.Rect.Bottom:0} {f.Rect.Right:0} {f.Rect.Top:0}"));
        Assert.Equal("Familienname", NameAt(73, 601));
        Assert.Equal("Titel", NameAt(311, 601));
        Assert.Equal("Vorname", NameAt(73, 571));
        Assert.Equal("Geburtsname und Familienname", NameAt(311, 571));
        Assert.Equal("Anschrift", NameAt(73, 501));
        Assert.True(found.Single(f => f.Spec.Name == "Titel").Rect.Top <= 621, "das Feld deckt die Beschriftung nicht");
    }

    [Fact]
    public void RowsOfCellsOverOneLine_DetectorFieldsInTheNarrowCellsKept()
    {
        // a filled-in tax form of the public test set: rows of number, amount and cents over one line across all three cells; the detector
        // sees the cents cells, the drawing alone does not (filled in) — the line under a row must not take them in and lose them with it when
        // the cells win (docs/FEHLERGEDAECHTNIS.md, No. 346)
        using var doc = PdfDocument.Open(Path.Combine(Root(), "TestPDFs", "pdfjs", "prefilled_f1040.pdf"));
        double[] rows = [252, 276, 288, 300, 336, 348, 360, 372, 384, 408];
        var scan = new PageScan([], null, [.. rows.Select(y => new PageDetection(new RectD(555, y + 1, 575, y + 11), DetectedFieldKind.Text, 0.8))], true);
        var found = FormRecognizer.Recognize([new FormRecognizer.PageInput(doc, doc.Pages[0], [], _ => scan)], new HashSet<string>());
        var missing = rows.Where(y => !found.Any(f => f.Rect.Left >= 552 && f.Rect.Right <= 578 && Math.Abs(f.Rect.Bottom - y) <= 3)).ToList();
        Assert.True(missing.Count == 0, "fehlen: " + string.Join(", ", missing));
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "TestPDFs"))) return dir.FullName;
        throw new DirectoryNotFoundException();
    }

    [Fact]
    public void SignatureLine_LabelBelow_IsASignatureField_PlaceAndDateIsText()
    {
        var content = "0.7 w 72 200 m 250 200 l S BT /F1 8 Tf 72 190 Td (Ort, Datum) Tj ET 0.7 w 320 200 m 520 200 l S BT /F1 8 Tf 320 190 Td (Unterschrift) Tj ET";
        var found = Recognize(Page(content));
        Assert.Equal(2, found.Count);
        Assert.Equal("Ort, Datum", found[0].Spec.Name);
        Assert.Equal(FieldFormatKind.None, found[0].Spec.Format.Kind);
        Assert.Equal(FormFieldKind.Signature, found[1].Spec.Kind);
        Assert.True(found[1].Rect.Height > 25);
    }

    [Fact]
    public void NothingWhereFieldsLie_NorWhereTextIsFilledIn_NorInPlainText()
    {
        var line = "BT /F1 10 Tf 72 700 Td (Name:) Tj ET 0.7 w 110 698 m 400 698 l S";
        Assert.Empty(Recognize(Page(line), new PdfRectangle(110, 698, 400, 712)));
        // filled in: text stands on the line
        Assert.Empty(Recognize(Page(line + " BT /F1 10 Tf 115 700 Td (Max Mustermann) Tj ET")));
        // a page of running text with an underlined word
        Assert.Empty(Recognize(Page("BT /F1 10 Tf 72 700 Td (Ein ganz gewöhnlicher Absatz mit Text.) Tj ET 0.5 w 72 698 m 120 698 l S")));
    }

    [Fact]
    public void SameLabelTwice_UniqueNames_SamePlaceOnAnotherPage_Linked()
    {
        var found = Recognize(Page("BT /F1 10 Tf 72 780 Td (Aktenzeichen:) Tj ET 0.7 w 150 778 m 300 778 l S " +
                                   "BT /F1 10 Tf 72 700 Td (Name:) Tj ET 0.7 w 110 698 m 400 698 l S " +
                                   "BT /F1 10 Tf 72 660 Td (Name:) Tj ET 0.7 w 110 658 m 400 658 l S", pages: 2));
        // on one page the same label twice: names of their own; on the next page at the same places: the same names, linked (KONZEPT §5.9a)
        Assert.Equal(["Aktenzeichen", "Name", "Name_2", "Aktenzeichen", "Name", "Name_2"], found.Select(f => f.Spec.Name));
    }

    [Fact]
    public void ExistingNames_AreNotTaken()
    {
        using var doc = PdfDocument.Open(Page("BT /F1 10 Tf 72 700 Td (Name:) Tj ET 0.7 w 110 698 m 400 698 l S"));
        var found = FormRecognizer.Recognize([new FormRecognizer.PageInput(doc, doc.Pages[0], [])], new HashSet<string> { "Name" });
        Assert.Equal("Name_2", Assert.Single(found).Spec.Name);
    }

    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void Hatching_ThousandsOfLines_GivesUpQuickly()
    {
        var sb = new System.Text.StringBuilder("0.2 w ");
        for (var i = 0; i < 2000; i++) sb.Append(System.FormattableString.Invariant($"50 {100 + i * 0.3:0.##} m 550 {100 + i * 0.3:0.##} l S {50 + i * 0.25:0.##} 100 m {50 + i * 0.25:0.##} 700 l S "));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Empty(Recognize(Page(sb.ToString())));
        Assert.True(watch.Elapsed.TotalSeconds < 10 * TestKind.Factor, watch.Elapsed.ToString());
    }

    /// <summary>A comb line as forms print it: a line from <paramref name="left"/> with a small stroke (2.8 points) at each cell boundary.</summary>
    private static string Comb(double left, double y, int cells, double cell = 14.17)
    {
        var sb = new System.Text.StringBuilder(System.FormattableString.Invariant($"0.5 w {left} {y} m {left + cells * cell} {y} l S "));
        for (var k = 0; k <= cells; k++) sb.Append(System.FormattableString.Invariant($"{left + k * cell:0.###} {y} m {left + k * cell:0.###} {y + 2.835} l S "));
        return sb.ToString();
    }

    [Fact]
    public void CombLine_OneCellPerStroke_NamedAfterItsLabel()
    {
        var found = Assert.Single(Recognize(Page("BT /F1 8 Tf 72 702 Td (Name*) Tj ET " + Comb(110, 700, 20))));
        Assert.Equal("Name", found.Spec.Name);
        Assert.Equal(ProposalSource.Comb, found.Source);
        Assert.True(found.Spec.Comb);
        Assert.Equal(20, found.Spec.MaxLength);
        Assert.InRange(found.Rect.Left, 109, 112);
        Assert.InRange(found.Rect.Right, 392, 395);
    }

    [Fact]
    public void CombGroups_WithGaps_AFieldPerGroup_NamedAfterTheLabelUnderThem()
    {
        // an IBAN in groups of four with small gaps, its label right under it with an explanation in brackets: a field per group, so that
        // every character stands in its printed cell; a second such line is told apart
        string Iban(double y) => string.Concat(Enumerable.Range(0, 6).Select(g => Comb(110 + g * 60, y, g < 5 ? 4 : 2))) +
                                 System.FormattableString.Invariant($"BT /F1 7 Tf 110 {y - 8} Td (IBAN* \\(Sie finden die Nummer auf dem Auszug.\\)) Tj ET ");
        var found = Recognize(Page(Iban(700) + Iban(600)));
        Assert.Equal(["IBAN-1", "IBAN-2", "IBAN-3", "IBAN-4", "IBAN-5", "IBAN-6", "IBAN_2-1", "IBAN_2-2", "IBAN_2-3", "IBAN_2-4", "IBAN_2-5", "IBAN_2-6"],
            found.Select(f => f.Spec.Name));
        Assert.Equal([4, 4, 4, 4, 4, 2], found.Take(6).Select(f => f.Spec.MaxLength));
        Assert.All(found, f => Assert.True(f.Spec.Comb));
        // each over its own cells
        Assert.InRange(found[1].Rect.Left, 169, 171);
        Assert.InRange(found[1].Rect.Right, 225, 227);
    }

    [Fact]
    public void OneLine_TwoCombRuns_TwoFields()
    {
        // postcode and town on one line: five cells, the label of the town, then 20 cells
        var line = "0.5 w 100 700 m 470 700 l S ";
        for (var k = 0; k <= 5; k++) line += System.FormattableString.Invariant($"{100 + k * 14.17:0.###} 700 m {100 + k * 14.17:0.###} 702.835 l S ");
        for (var k = 0; k <= 18; k++) line += System.FormattableString.Invariant($"{200 + k * 14.17:0.###} 700 m {200 + k * 14.17:0.###} 702.835 l S ");
        var found = Recognize(Page("BT /F1 8 Tf 72 702 Td (PLZ*) Tj ET BT /F1 8 Tf 180 702 Td (Ort*) Tj ET " + line));
        Assert.Equal(["PLZ", "Ort"], found.Select(f => f.Spec.Name));
        Assert.Equal([5, 18], found.Select(f => f.Spec.MaxLength));
    }

    [Fact]
    public void DateInEightCells_WrittenWithoutPoints_HouseNumberIsText()
    {
        var found = Recognize(Page("BT /F1 8 Tf 72 702 Td (Geburtsdatum*) Tj ET " + Comb(150, 700, 8) +
                                   "BT /F1 8 Tf 72 662 Td (Hausnummer*) Tj ET " + Comb(150, 660, 4)));
        Assert.Equal("ddmmyyyy", found[0].Spec.Format.Pattern);
        Assert.Equal("24121980", found[0].Spec.Format.Normalize("24.12.1980"));
        Assert.Equal(FieldFormatKind.None, found[1].Spec.Format.Kind);   // „12a“ must fit
    }

    [Fact]
    public void Labels_HeadingOverTheRow_RemarkLine_TurnedMarginNote()
    {
        var found = Recognize(Page(
            // a heading over the row ends nearer to the field than its own label
            "BT /F1 11 Tf 44 712 Td (Kontoinhaber des Auftrags) Tj ET BT /F1 8 Tf 190 690 Td (Titel) Tj ET " + Comb(214, 687, 8) +
            // a label in two lines: „E-Mail“ over „(privat)“
            "BT /F1 7 Tf 300 600 Td (E-Mail) Tj ET BT /F1 7 Tf 300 593 Td (\\(privat\\)) Tj ET 0.5 w 340 592 m 560 592 l S " +
            // a note along the margin, turned: no label
            "BT /F1 7 Tf 0 1 -1 0 30 340 Tm (01 00 060 00) Tj ET 0.7 w 45 365 9 9 re S"));
        Assert.Equal("Titel", found.Single(f => f.Spec.Comb).Spec.Name);
        Assert.Equal("E-Mail", found.Single(f => f.Rect.Left > 330 && f.Rect.Left < 345).Spec.Name);
        Assert.Equal("Kontrollkästchen", found.Single(f => f.Spec.Kind == FormFieldKind.CheckBox).Spec.Name);
    }

    [Fact]
    public void SameQuestionTwiceOnAPage_TwoOptionGroups()
    {
        string Row(double y) => System.FormattableString.Invariant($"BT /F1 8 Tf 44 {y + 1} Td (Anrede*) Tj ET 0.7 w 101 {y} 9 9 re S BT /F1 8 Tf 115 {y + 1} Td (Frau) Tj ET ") +
                                System.FormattableString.Invariant($"0.7 w 139 {y} 9 9 re S BT /F1 8 Tf 153 {y + 1} Td (Herr) Tj ET ");
        var found = Recognize(Page(Row(684) + Row(542)));
        Assert.Equal(4, found.Count);
        Assert.All(found, f => Assert.Equal(FormFieldKind.Radio, f.Spec.Kind));
        Assert.Equal(["Anrede", "Anrede", "Anrede_2", "Anrede_2"], found.Select(f => f.Spec.Name));
        Assert.Equal(["Frau", "Herr", "Frau", "Herr"], found.Select(f => f.Spec.ExportValue));
    }
}
