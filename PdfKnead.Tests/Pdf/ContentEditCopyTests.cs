using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using SkiaSharp;
using static PdfKnead.Tests.Pdf.ContentImageTests;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// „Bilder bearbeiten“ copies what an edit does not touch as it is instead of writing all of the content anew (docs/FEHLERGEDAECHTNIS.md,
/// No. 255): every object moved and deleted on its own and all together must look exactly as with everything written anew — paths with
/// text inside their construction, a path that clips and paints, patterns in a moved block, a shading, a form, an inline image, a broken
/// path construction, comments, a Q too many and a q left open.
/// </summary>
public sealed class ContentEditCopyTests
{
    private static byte[] Tricky()
    {
        var pdf = TestPdf.Simple(1);
        var cell = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("Pattern")), ("PatternType", PdfInteger.Of(1)), ("PaintType", PdfInteger.Of(1)),
            ("TilingType", PdfInteger.Of(1)), ("BBox", TestPdf.Box(0, 0, 20, 20)), ("XStep", PdfInteger.Of(20)), ("YStep", PdfInteger.Of(20)),
            ("Resources", new PdfDictionary())), "1 0 0 rg 0 0 10 20 re f 0 0 1 rg 10 0 10 20 re f"u8.ToArray()));
        var form = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 50, 50))),
            "0 0.6 0 rg 0 0 30 30 re f 0.6 0 0.6 rg 20 20 30 30 re f"u8.ToArray()));
        var function = TestPdf.Dict(("FunctionType", PdfInteger.Of(2)), ("Domain", TestPdf.Box(0, 1)), ("C0", TestPdf.Box(0.9, 0, 0)),
            ("C1", TestPdf.Box(0, 0, 0.9)), ("N", PdfInteger.Of(1)));
        var shading = pdf.Add(TestPdf.Dict(("ShadingType", PdfInteger.Of(2)), ("ColorSpace", TestPdf.N("DeviceRGB")), ("Coords", TestPdf.Box(100, 0, 200, 0)),
            ("Function", function), ("Extend", new PdfArray([PdfBoolean.True, PdfBoolean.True]))));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(
            ("Pattern", TestPdf.Dict(("P0", TestPdf.Ref(cell)))),
            ("XObject", TestPdf.Dict(("F", TestPdf.Ref(form)))),
            ("Shading", TestPdf.Dict(("Sh0", TestPdf.Ref(shading)))),
            ("Font", TestPdf.Dict(("F1", TestPdf.Ref(3))))));
        pdf.Replace(4, TestPdf.FlateStream(Encoding.Latin1.GetBytes(
            "% ein Kommentar\n" +
            "Q\n" +
            "1 0 0 rg 10 10 m 100 10 l BT /F1 12 Tf 20 30 Td (mitten im Pfad) Tj ET 100 100 l h f\n" +
            "0 0 1 RG 4 w 200 200 m 300 260 l S\n" +
            "q 100 400 200 100 re W n 0 1 0 rg 50 350 300 300 re f Q\n" +
            "q 0 0 0 rg 400 400 80 80 re W f 1 1 0 rg 380 380 120 120 re f Q\n" +
            "/Pattern cs /P0 scn 450 100 100 100 re f\n" +
            "q 100 520 100 60 re W n /Pattern cs /P0 scn 0 0 595 842 re f Q\n" +
            "q 1 0 0 1 300 250 cm /F Do Q\n" +
            "q 100 600 100 100 re W n /Sh0 sh Q\n" +
            "q 30 0 0 30 450 600 cm BI /W 2 /H 2 /CS /G /BPC 8 ID \u0000ÿÿ\u0000 EI Q\n" +
            "0.5 g 20 700 m 60 760 l /F Do 30 30 40 40 re f % noch einer\n" +
            "q 0 0 1 rg 500 700 50 50 re f")));
        return pdf.Build();
    }

    public static TheoryData<string> Pages => ["gemischt", "Bilder"];

    [Theory]
    [MemberData(nameof(Pages))]
    public void EveryObject_MovedAndDeleted_LooksAsWrittenAnew(string page)
    {
        using var doc = PdfDocument.Open(page == "gemischt" ? Tricky() : ContentImageTests.Build());
        var item = PageItem.From(doc)[0];
        var found = ContentObjectEditor.ObjectsOf(item);
        Assert.True(found.Count >= 6, $"{found.Count} Objekte");
        var all = Enumerable.Range(0, found.Count).Select(found.KeyOf).ToList();
        foreach (var keys in all.Select(k => new List<ContentKey> { k }).Append(all))
        {
            Same(doc, ContentObjectEditor.Transform(item, keys, Matrix.Translate(13, -7), found), $"{string.Join(",", keys)} verschoben");
            Same(doc, ContentObjectEditor.Transform(item, keys, new Matrix(1.5, 0.2, -0.1, 0.8, 4, 9), found), $"{string.Join(",", keys)} verzerrt");
            Same(doc, ContentObjectEditor.Delete(item, keys, found), $"{string.Join(",", keys)} gelöscht");
        }
    }

    [Fact]
    public void UnchangedOperators_KeepTheirBytes()
    {
        using var doc = PdfDocument.Open(Tricky());
        var item = PageItem.From(doc)[0];
        var found = ContentObjectEditor.ObjectsOf(item);
        var blue = Enumerable.Range(0, found.Count).Single(i => found.VisibleOf(i) is { Left: > 195 and < 205, Top: > 195 and < 205 });
        var moved = ContentObjectEditor.Transform(item, [found.KeyOf(blue)], Matrix.Translate(13, -7), found);
        var content = Encoding.Latin1.GetString(((PdfStream)moved.Page.Dictionary.Get(PdfName.Contents)!).RawData!);
        // the comments and the text inside the path stay as written; the moved line is wrapped with its move
        Assert.Contains("% ein Kommentar", content);
        Assert.Contains("1 0 0 rg 10 10 m 100 10 l BT /F1 12 Tf 20 30 Td (mitten im Pfad) Tj ET 100 100 l h f", content);
        Assert.Contains("30 30 40 40 re f % noch einer", content);
        Assert.DoesNotContain("200 200 m 300 260 l S", content.Replace("\n", " ").Split("q 1 0 0 1 13 -7 cm")[0]);
    }

    /// <summary>The page of <paramref name="edited"/> (unchanged operators copied) looks exactly as with everything written anew.</summary>
    private static void Same(PdfDocument doc, PageItem edited, string what)
    {
        var (dict, resources, _) = ContentObjectEditor.Apply(doc, edited.ObjectEdits!, copyUnchanged: false);
        var page = edited.Page;
        var anew = new PdfPage(page.Index, dict, page.Id, resources, page.MediaBox, page.CropBox, page.Rotate);
        using var copied = Render(doc, page);
        using var written = Render(doc, anew);
        var differing = 0;
        for (var y = 0; y < copied.Height; y++)
            for (var x = 0; x < copied.Width; x++)
                if (Distance(copied.GetPixel(x, y), written.GetPixel(x, y)) > 2) differing++;
        Assert.True(differing == 0, $"{what}: {differing} Pixel anders");
    }

    private static int Distance(SKColor a, SKColor b) =>
        Math.Max(Math.Max(Math.Abs(a.Red - b.Red), Math.Abs(a.Green - b.Green)), Math.Max(Math.Abs(a.Blue - b.Blue), Math.Abs(a.Alpha - b.Alpha)));

    // ------------------------------------------------------------ B7: copying chosen objects as a vector and pasting them

    /// <summary>A copy, saved and opened again: the page with the pasted form, rendered.</summary>
    private static SKBitmap Pasted(PdfDocument from, PdfStream form, Matrix place, out int found)
    {
        var blank = TestPdf.Simple(1, null);
        blank.Replace(4, TestPdf.FlateStream([]));
        using var other = PdfDocument.Open(blank.Build());
        var (pasted, op) = ContentObjectEditor.Insert(PageItem.From(other)[0], (PdfStream)ObjectCopy.Standalone(from, form), place);
        var objects = ContentObjectEditor.ObjectsOf(pasted);
        found = Enumerable.Range(0, objects.Count).Count(i => objects.KeyOf(i).Scope.Split('/')[0] == op.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var output = new MemoryStream();
        Core.Pdf.Writing.PdfWriter.Write(other, [pasted], output);
        using var saved = PdfDocument.Open(output.ToArray());   // the source document need not be open any more: nothing refers to it
        return Render(saved, saved.Pages[0]);
    }

    /// <summary>
    /// Pasting onto a page whose content closes a state it never opened after setting the font (a Q readers pass over): its text stays as it
    /// was. The old content went into one q … Q of its own, and that Q closed the own q — the text after it lost its font, and the pasted
    /// object was drawn in what the content left behind (docs/FEHLERGEDAECHTNIS.md, No. 148, 383).
    /// </summary>
    [Fact]
    public void Paste_OntoContentClosingMoreThanItOpens_TextStaysAsItWas()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream("BT /F1 20 Tf ET Q BT 100 500 Td (Kopf) Tj ET 0.1 0 0 0.1 0 0 cm"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        var before = RedactionTests.Read(doc, doc.Pages[0]).Glyphs;
        var dict = new PdfDictionary();
        dict.Set(PdfName.Type, PdfName.Get("XObject"));
        dict.Set(PdfName.Subtype, PdfName.Get("Form"));
        dict.Set(PdfName.Get("BBox"), new PdfArray([PdfInteger.Of(0), PdfInteger.Of(0), PdfInteger.Of(50), PdfInteger.Of(50)]));
        var (pasted, _) = ContentObjectEditor.Insert(PageItem.From(doc)[0], new PdfStream(dict, "0 0 1 rg 0 0 50 50 re f"u8.ToArray()), Matrix.Translate(300, 300));
        using var output = new MemoryStream();
        Core.Pdf.Writing.PdfWriter.Write(doc, [pasted], output);
        using var saved = PdfDocument.Open(output.ToArray());
        Assert.Equal(before, RedactionTests.Read(saved, saved.Pages[0]).Glyphs);
        // the object where it was placed, full size
        using var image = Render(saved, saved.Pages[0]);
        Assert.True(image.GetPixel(340, image.Height - 340).Blue > 200 && image.GetPixel(340, image.Height - 340).Red < 60, $"{image.GetPixel(340, image.Height - 340)}");
    }

    [Fact]
    public void Copy_OnlyTheChosen_PastedIntoAnotherDocument()
    {
        using var doc = PdfDocument.Open(Tricky());
        var item = PageItem.From(doc)[0];
        var found = ContentObjectEditor.ObjectsOf(item);
        int line = found.At(250, 230, 1)!.Value, inline = found.At(472, 607, 0.5)!.Value;
        var (form, box) = ContentObjectEditor.Copy(item, [found.KeyOf(line), found.KeyOf(inline)], found)!.Value;
        Assert.True(box.Left < 200 && box.Right >= 480 && box.Top < 200 && box.Bottom >= 630, box.ToString());
        var content = Encoding.Latin1.GetString(form.RawData!);
        Assert.DoesNotContain("mitten im Pfad", content);   // text never comes along
        Assert.DoesNotContain("380 380 120 120 re f", content);
        using var bitmap = Pasted(doc, form, Matrix.Identity, out var objects);
        var page = PdfDocument.Open(TestPdf.Simple(1, null).Build()).Pages[0];
        Assert.Equal(2, objects);
        Assert.True(At(bitmap, page, 250, 230) is { Blue: > 180, Red: < 80 }, "die blaue Linie");
        Assert.True(At(bitmap, page, 472, 607) is { Red: < 60, Green: < 60, Blue: < 60 }, "das eingebettete Bild");
        Assert.Equal(SKColors.White, At(bitmap, page, 60, 30));      // the red triangle stays behind
        Assert.Equal(SKColors.White, At(bitmap, page, 440, 440));    // so does the square in its clip
    }

    [Fact]
    public void Copy_Everything_LooksAsTheOriginal_AlsoMoved()
    {
        using var doc = PdfDocument.Open(Tricky());
        var item = PageItem.From(doc)[0];
        var found = ContentObjectEditor.ObjectsOf(item);
        var all = Enumerable.Range(0, found.Count).Select(found.KeyOf).ToList();
        // the blue line moved first: the copy takes the page as it is now
        var line = found.At(250, 230, 1)!.Value;
        var edited = ContentObjectEditor.Transform(item, [found.KeyOf(line)], Matrix.Translate(0, 100), found);
        var (form, _) = ContentObjectEditor.Copy(edited, all)!.Value;
        using var original = Render(doc, edited.Page);
        using var copied = Pasted(doc, form, Matrix.Identity, out var objects);
        Assert.Equal(found.Count, objects);
        var differing = 0;
        for (var y = 0; y < original.Height; y++)
            for (var x = 0; x < original.Width; x++)
            {
                // the text in the red triangle is not copied
                var p = PdfKnead.Rendering.PageGeometry.PageToDevice(edited.Page, 1).Invert().Transform(x, y);
                if (p.X is > 15 and < 150 && p.Y is > 20 and < 50) continue;
                if (Distance(original.GetPixel(x, y), copied.GetPixel(x, y)) > 2) differing++;
            }
        Assert.True(differing == 0, $"{differing} Pixel anders");
        // placed elsewhere: moved as a whole
        using var moved = Pasted(doc, form, Matrix.Translate(-150, 0), out _);
        var page = edited.Page;
        Assert.True(At(moved, page, 100, 330) is { Blue: > 180, Red: < 80 }, "die blaue Linie, verschoben");
    }

    /// <summary>A "Q" too many in a form restores nothing outside it (as in other readers): the form stays where its invocation put it.</summary>
    [Fact]
    public void StrayRestoreInAForm_StaysInside()
    {
        var pdf = TestPdf.Simple(1, null);
        var form = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 50, 50))),
            "Q Q 1 0 0 rg 0 0 50 50 re f"u8.ToArray()));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("F", TestPdf.Ref(form))))));
        pdf.Replace(4, TestPdf.FlateStream("q 1 0 0 1 200 300 cm /F Do Q"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        using var bitmap = Render(doc, doc.Pages[0]);
        Assert.True(Red(At(bitmap, doc.Pages[0], 225, 325)), "das Formular an seinem Platz");
        Assert.Equal(SKColors.White, At(bitmap, doc.Pages[0], 25, 25));
        Assert.Equal(225, ContentObjectEditor.ObjectsOf(PageItem.From(doc)[0]).VisibleOf(0) is var v ? (v.Left + v.Right) / 2 : 0, 1);
    }

    [Fact]
    public void Compact_DropsWhatPaintsNothing_KeepsMarkedContentBalanced()
    {
        var content = "q 1 0 0 rg 0 0 5 5 re W n Q q 0 0 1 rg 10 10 m 20 20 l S Q /Span BMC q 0.5 g Q EMC q /P BMC Q EMC 0 g 1 w"u8.ToArray();
        var compact = Encoding.ASCII.GetString(Redactor.Compact(content)).Replace("\n", " ").Trim();
        Assert.Equal("q 0 0 1 rg 10 10 m 20 20 l S Q /Span BMC EMC q /P BMC Q EMC", System.Text.RegularExpressions.Regex.Replace(compact, " +", " "));
    }
}
