using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>Elements on pages: text fixed in the content or as a form field, display, saving.</summary>
public sealed class ElementTests
{
    private static TextElement Text(string text, double left = 100, double bottom = 600, double width = 300, double height = 60, int rotation = 0) =>
        new() { Rect = new PdfRectangle(left, bottom, left + width, bottom + height), Text = text, FontSize = 14, Rotation = rotation };

    private static PdfDocument Save(PdfDocument main, IReadOnlyList<PageItem> pages, PdfSaveOptions? options = null, string? password = null,
        [System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        using var output = new MemoryStream();
        PdfWriter.Write(main, pages, output, options);
        // Store for checking with other programs (local only, e.g. another PDF reader)
        if (Environment.GetEnvironmentVariable("PDFKNEAD_ABLAGE") is { Length: > 0 } folder)
            File.WriteAllBytes(Path.Combine(folder, $"{name}-{options?.Version ?? "1.7"}.pdf"), output.ToArray());
        var doc = PdfDocument.Open(output.ToArray(), password);
        Assert.False(doc.WasRepaired);
        return doc;
    }

    private static string TextOf(PdfDocument doc, int page) =>
        string.Concat(PageInterpreter.Build(doc, doc.Pages[page]).Text.Select(t => t.Text));

    /// <summary>Counts dark pixels (text) in an area of the rendered page (device pixels at 1 px/pt).</summary>
    private static int DarkPixels(SKBitmap bitmap, int left, int top, int right, int bottom)
    {
        var count = 0;
        for (var y = Math.Max(0, top); y < Math.Min(bitmap.Height, bottom); y++)
            for (var x = Math.Max(0, left); x < Math.Min(bitmap.Width, right); x++)
                if (bitmap.GetPixel(x, y).Red < 100) count++;
        return count;
    }

    [Fact]
    public void Flat_TextIsInContent_WithSubsetFont()
    {
        using var original = PdfDocument.Open(TestPdf.Simple().Build());
        var pages = PageItem.From(original);
        pages[0] = pages[0].WithElements([Text("Grüße von Max Mustermann")]);
        using var copy = Save(original, pages, new PdfSaveOptions { Flatten = true });   // fixed in the content: when flattening

        Assert.Contains("Grüße von Max Mustermann", TextOf(copy, 0));
        Assert.Contains("Seite 1", TextOf(copy, 0));                       // the old content stays
        Assert.Null(copy.Pages[0].Dictionary.Get(PdfName.Get("Annots")).AsDictionary());
        // Embedded as a subset: small, with a tag before the name
        var xobjects = copy.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!;
        var form = (PdfStream)xobjects.Get(PdfName.Get("PKn1"));
        var font = form.Dictionary.GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("F1"))!;
        Assert.Matches("^[A-Z]{6}\\+LiberationSans$", font.GetName(PdfName.Get("BaseFont"))!.Value);
        var file = font.GetArray(PdfName.Get("DescendantFonts"))!.Get(0).AsDictionary()!.GetDictionary(PdfName.Get("FontDescriptor"))!
            .GetStream(PdfName.Get("FontFile2"))!;
        Assert.True(file.DataLength < 40_000, $"Schrift {file.DataLength} Bytes");
        Assert.NotNull(font.GetStream(PdfName.Get("ToUnicode")));
        // a composite font with the identity encoding (ISO 32000-2, 9.5 S1: /Subtype and /Encoding of the written font)
        Assert.Equal("Type0", font.GetName(PdfName.Subtype)?.Value);
        Assert.Equal("Identity-H", font.GetName(PdfName.Get("Encoding"))?.Value);
        // Other pages untouched
        Assert.DoesNotContain("Mustermann", TextOf(copy, 1));
    }

    [Fact]
    public void Flat_SameLookAsShownBeforeSaving()
    {
        using var original = PdfDocument.Open(TestPdf.Simple(1).Build());
        var item = PageItem.From(original)[0].WithElements([Text("Hallo Welt", 100, 600, 300, 60) with { FontSize = 30, Color = 0xC00000 }]);
        using var copy = Save(original, [item]);

        // Before saving: the page plus the elements as a separate list
        var page = original.Pages[0];
        using var before = SkiaRenderer.RenderPage(PageInterpreter.Build(original, page), original, page, 1, new ImageCache());
        var overlay = PageInterpreter.BuildAnnotations(ElementAppearance.Document, page, item.Elements.Select(e => ElementAppearance.For(e).Annotation));
        using (var canvas = new SKCanvas(before))
            SkiaRenderer.RenderTile(canvas, overlay, ElementAppearance.Document, PageGeometry.PageToDevice(page, 1), SKRectI.Create(0, 0, before.Width, before.Height), new ImageCache());
        using var after = SkiaRenderer.RenderPage(PageInterpreter.Build(copy, copy.Pages[0]), copy, copy.Pages[0], 1, new ImageCache());

        var top = (int)(841.89 - 660);
        Assert.True(DarkPixels(after, 100, top, 400, top + 60) > 200);
        var differing = 0;
        for (var y = 0; y < after.Height; y++)
            for (var x = 0; x < after.Width; x++)
                if (Math.Abs(before.GetPixel(x, y).Red - after.GetPixel(x, y).Red) > 8) differing++;
        Assert.True(differing < 20, $"{differing} Pixel weichen ab");
    }

    [Fact]
    public void Field_IsAddedToForm_WithValueAndAppearance()
    {
        using var original = PdfDocument.Open(TestPdf.Simple().Build());
        var pages = PageItem.From(original);
        pages[1] = pages[1].WithElements([Text("Max Mustermann") with { IsField = true }, Text("zweites", 100, 500) with { IsField = true, FieldName = "Ort" }]);
        using var copy = Save(original, pages);

        var form = copy.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!;
        var fields = form.GetArray(PdfName.Get("Fields"))!;
        Assert.Equal(2, fields.Count);
        var first = fields.Get(0).AsDictionary()!;
        Assert.Equal("Textfeld1", first.GetString(PdfName.Get("T"))!.ToText());
        Assert.Equal("Max Mustermann", first.GetString(PdfName.Get("V"))!.ToText());
        Assert.Equal("/Helv 14 Tf 0 0 0 rg", System.Text.Encoding.ASCII.GetString(first.GetString(PdfName.Get("DA"))!.Bytes));
        Assert.Equal("Ort", fields.Get(1).AsDictionary()!.GetString(PdfName.Get("T"))!.ToText());
        Assert.NotNull(form.GetDictionary(PdfName.Get("DR"))!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("Helv")));
        Assert.Same(copy.Pages[1].Dictionary, first.GetDictionary(PdfName.Get("P")));
        Assert.Equal(2, copy.Pages[1].Dictionary.GetArray(PdfName.Get("Annots"))!.Count);
        // The appearance is drawn
        Assert.Contains("Max Mustermann", TextOf(copy, 1));
    }

    /// <summary>
    /// Every font descriptor of a saved file is an object of its own (ISO 32000-2, 9.8.1: an indirect reference) — also those of the fonts
    /// built in memory for text, fields and the fonts written into appearances (docs/FEHLERGEDAECHTNIS.md, No. 362; found with the
    /// reference model of ISO 32000-2).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FontDescriptors_AreIndirect(bool flatten)
    {
        using var original = PdfDocument.Open(TestPdf.Simple().Build());
        var pages = PageItem.From(original);
        pages[0] = pages[0].WithElements([Text("Grüße von Max Mustermann"), Text("Feldwert", 100, 500) with { IsField = true }]);
        using var copy = Save(original, pages, new PdfSaveOptions { Flatten = flatten });
        var direct = new List<string>();
        var seen = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        void Walk(PdfObject? obj, string path, int depth)
        {
            if (obj is null || depth > 40 || !seen.Add(obj)) return;
            var dict = obj as PdfDictionary ?? (obj as PdfStream)?.Dictionary;
            if (obj is PdfArray array)
            {
                for (var i = 0; i < array.Count; i++) Walk(array.Get(i), $"{path}[{i}]", depth + 1);
                return;
            }
            if (dict is null) return;
            if (dict.ContainsKey(PdfName.Get("FontDescriptor")) && dict.GetRaw(PdfName.Get("FontDescriptor")) is not PdfReference) direct.Add(path);
            foreach (var (key, _) in dict.Entries.ToList()) Walk(dict.Get(key), $"{path}/{key.Value}", depth + 1);
        }
        for (var n = 1; n <= copy.HighestObjectNumber; n++)
            if (copy.HasObject(n)) Walk(copy.GetObject(n), $"{n} 0 obj", 0);
        Assert.Empty(direct);
    }

    /// <summary>
    /// Saved as PDF 2.0, a standard font that is not embedded — of the page and of the form (/DR) — has /FirstChar, /LastChar, /Widths and a
    /// /FontDescriptor naming it, without a font program (ISO 32000-2, 9.6.2.1, Table 109); the widths are those the display sets the text
    /// with, so the page stays as it is. As PDF 1.7 the font stays as read (docs/FEHLERGEDAECHTNIS.md, No. 372).
    /// </summary>
    [Fact]
    public void StandardFonts_SavedAsPdf20_AreDescribed()
    {
        using var original = PdfDocument.Open(TestPdf.Simple(1).Build());
        var pages = PageItem.From(original);
        pages[0] = pages[0].WithElements([Text("Feldwert", 100, 500) with { IsField = true }]);
        var shownBefore = PdfFont.Get((PdfDictionary)original.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.Get(PdfName.Get("F1")), original);
        foreach (var (version, described) in (ReadOnlySpan<(string, bool)>)[("2.0", true), ("1.7", false)])
        {
            using var copy = Save(original, pages, new PdfSaveOptions { Version = version });
            var form = copy.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.GetDictionary(PdfName.Get("DR"))!.GetDictionary(PdfName.Get("Font"))!;
            var fonts = new List<PdfDictionary> { (PdfDictionary)copy.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.Get(PdfName.Get("F1")) };
            fonts.AddRange(form.Entries.Select(e => (PdfDictionary)form.Get(e.Key)));
            foreach (var font in fonts)
            {
                var descriptor = font.GetDictionary(PdfName.Get("FontDescriptor"));
                Assert.Equal(described, descriptor is not null);
                if (descriptor is null) continue;
                Assert.IsType<PdfReference>(font.GetRaw(PdfName.Get("FontDescriptor")));
                Assert.Equal(font.GetName(PdfName.Get("BaseFont"))?.Value, descriptor.GetName(PdfName.Get("FontName"))?.Value);
                Assert.False(descriptor.ContainsKey(PdfName.Get("FontFile")) || descriptor.ContainsKey(PdfName.Get("FontFile2")) || descriptor.ContainsKey(PdfName.Get("FontFile3")));
                var first = (int)font.GetInteger(PdfName.Get("FirstChar"))!;
                var last = (int)font.GetInteger(PdfName.Get("LastChar"))!;
                Assert.Equal(last - first + 1, font.GetArray(PdfName.Get("Widths"))!.Count);
            }
            // the page's font sets every letter as before
            var shown = PdfFont.Get(fonts[0], copy);
            foreach (var c in "Testdokument Seite 1 äöü"u8.ToArray())
                Assert.Equal(shownBefore.Glyph(c, 1).Width, shown.Glyph(c, 1).Width, 12);
        }
    }

    [Fact]
    public void Field_NameClash_WithExistingField()
    {
        var pdf = TestPdf.Simple(1);
        var field = pdf.Add(TestPdf.Dict(("FT", TestPdf.N("Tx")), ("T", new PdfString("Textfeld1"u8.ToArray())), ("Type", TestPdf.N("Annot")),
            ("Subtype", TestPdf.N("Widget")), ("Rect", TestPdf.Box(10, 10, 50, 30)), ("P", TestPdf.Ref(5))));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(field)]));
        ((PdfDictionary)pdf.Get(1)).Set(PdfName.Get("AcroForm"), TestPdf.Dict(("Fields", new PdfArray([TestPdf.Ref(field)]))));
        using var original = PdfDocument.Open(pdf.Build());
        var page = PageItem.From(original)[0];
        var item = page.WithElements([.. page.Elements, Text("neu") with { IsField = true }]);   // the existing field stays
        using var copy = Save(original, [item]);

        var fields = copy.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.GetArray(PdfName.Get("Fields"))!;
        Assert.Equal(["Textfeld1", "Textfeld2"], fields.Select((_, i) => fields.Get(i).AsDictionary()!.GetString(PdfName.Get("T"))!.ToText()));
        Assert.Equal(2, copy.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Count);
    }

    [Fact]
    public void Rotated_ElementOnRotatedPage_IsUpright()
    {
        // Page 2 is rotated by 90° (Letter landscape): an element with rotation 90 stands upright when displayed
        using var original = PdfDocument.Open(TestPdf.Simple().Build());
        var pages = PageItem.From(original);
        var element = Text("Aufrecht", 300, 200, 60, 250, rotation: 90) with { FontSize = 24 };
        Assert.Equal((250, 60), element.UprightSize);
        pages[1] = pages[1].WithElements([element]);
        using var copy = Save(original, pages);

        var list = PageInterpreter.Build(copy, copy.Pages[1]);
        var glyphs = list.Text.Where(t => t.X0 is > 250 and < 400).ToList();
        Assert.Equal("Aufrecht", string.Concat(glyphs.Select(g => g.Text)));
        // In page space the text runs upwards (x fixed, y increasing), inside the rectangle
        Assert.True(glyphs[^1].Y0 > glyphs[0].Y0 + 50);
        Assert.All(glyphs, g => Assert.InRange(g.X0, 300, 360));
    }

    [Fact]
    public void Encrypted_ElementsAreEncryptedToo()
    {
        var bytes = TestPdf.Simple(1).Build(XRefStyle.Stream, PdfCipher.Aes128, user: "geheim", owner: "chef");
        using var original = PdfDocument.Open(bytes, "geheim");
        var item = PageItem.From(original)[0].WithElements([Text("vertraulich"), Text("Feld", 100, 400) with { IsField = true }]);
        using var output = new MemoryStream();
        PdfWriter.Write(original, [item], output);
        Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(output.ToArray()));
        using var copy = PdfDocument.Open(output.ToArray(), "geheim");
        Assert.Contains("vertraulich", TextOf(copy, 0));
        Assert.Contains("Feld", TextOf(copy, 0));
        Assert.Equal("Feld", copy.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.GetArray(PdfName.Get("Fields"))!.Get(0).AsDictionary()!
            .GetString(PdfName.Get("V"))!.ToText());
    }

    [Theory]
    [InlineData("1.4")]
    [InlineData("2.0")]
    public void Versions_AndInheritedResources(string version)
    {
        using var original = PdfDocument.Open(TestPdf.Simple().Build());
        var pages = PageItem.From(original);
        for (var i = 0; i < 3; i++) pages[i] = pages[i].WithElements([Text($"Element {i + 1}")]);
        using var copy = Save(original, pages, new PdfSaveOptions { Version = version, Flatten = true });
        for (var i = 0; i < 3; i++)
        {
            Assert.Contains($"Element {i + 1}", TextOf(copy, i));
            Assert.Contains($"Seite {i + 1}", TextOf(copy, i));   // font F1 from the inherited resources still present
        }
        // One font for all pages
        var fonts = Enumerable.Range(0, 3).Select(i =>
            ((PdfStream)copy.Pages[i].Resources!.GetDictionary(PdfName.Get("XObject"))!.Get(PdfName.Get("PKn1"))).Dictionary
            .GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("F1"))).ToList();
        Assert.Same(fonts[0], fonts[1]);
        Assert.Same(fonts[0], fonts[2]);
    }

    [Fact]
    public void Layout_WrapsAtWordsAndKeepsLineBreaks()
    {
        var element = Text("eins zwei drei vier fünf sechs\nsieben", width: 90) with { FontSize = 12 };
        var lines = ElementAppearance.Layout(element, ElementFace.For(ElementFont.Sans));
        Assert.True(lines.Count >= 3);
        Assert.Equal("sieben", string.Concat(lines[^1].Text));
        Assert.All(lines, l => Assert.True(l.Width <= 90 - 2 * ElementAppearance.Padding + 0.01));
        Assert.DoesNotContain(lines, l => l.Text.Count > 0 && l.Text[0] == " ");

        var single = ElementAppearance.Layout(element with { Multiline = false }, ElementFace.For(ElementFont.Sans));
        Assert.Equal(2, single.Count);   // only at the line break
    }

    [Fact]
    public void Text_IsKerned_LikeTheShaperSaysAndStaysSearchable()
    {
        using var original = PdfDocument.Open(TestPdf.Simple(1).Build());
        var element = Text("AVATAR", 100, 600, 400, 60) with { FontSize = 40 };
        using var copy = Save(original, [PageItem.From(original)[0].WithElements([element])]);
        var glyphs = PageInterpreter.Build(copy, copy.Pages[0]).Text.Where(t => t.Y0 < 700).ToList();   // without "Seite 1" at the top
        Assert.Equal("AVATAR", string.Concat(glyphs.Select(g => g.Text)));
        // Distance of the last character from the first = sum of the set advances (with kerning)
        var font = ElementFace.For(ElementFont.Sans).Font;
        var shaped = Core.Fonts.Layout.TextShaper.For(font).Shape("AVATAR");
        var expected = shaped[..^1].Sum(g => g.XAdvance) * 40 / font.UnitsPerEm;
        var plain = "AVATA".Sum(c => font.AdvanceWidth(font.GlyphForUnicode(c))) * 40 / font.UnitsPerEm;
        Assert.True(plain - expected > 5, "Liberation Sans unterschneidet AV/VA/TA");
        Assert.Equal(expected, glyphs[^1].X0 - glyphs[0].X0, 0.2);
    }

    [Fact]
    public void Signature_AllHandwritings_AsPathsWithoutText()
    {
        // A page with the name in every handwriting font, one below the other
        using var original = PdfDocument.Open(TestPdf.Simple(1).Build());
        var fonts = EmbeddedFonts.Handwriting;
        // Rows of 48 pt, closer together when there are more fonts than fit onto the page
        var row = Math.Min(48, 760 / fonts.Count);
        var elements = fonts.Select((f, i) => (PageElement)new SignatureElement
        {
            Rect = new PdfRectangle(60, 780 - (i + 1) * row, 360, 780 - i * row - 4),
            Name = "Max Mustermann",
            FontFile = f,
        }).ToList();
        var item = PageItem.From(original)[0].WithElements(elements);
        using var copy = Save(original, [item]);

        // No text added (only "Seite 1"), but ink everywhere
        Assert.Equal("Seite 1", TextOf(copy, 0).Trim());
        using var bitmap = SkiaRenderer.RenderPage(PageInterpreter.Build(copy, copy.Pages[0]), copy, copy.Pages[0], 1, new ImageCache());
        for (var i = 0; i < fonts.Count; i++)
        {
            var top = (int)(841.89 - (780 - i * row - 4));
            var ink = 0;
            for (var y = top; y < top + row - 4; y++)
                for (var x = 60; x < 360; x++)
                {
                    var p = bitmap.GetPixel(x, y);
                    if (p.Blue > p.Red + 40) ink++;   // ballpoint blue
                }
            Assert.True(ink > 150, $"{fonts[i]}: nur {ink} Pixel Tinte");
        }
        if (Environment.GetEnvironmentVariable("PDFKNEAD_ABLAGE") is { Length: > 0 } folder)
        {
            using var data = SkiaRenderer.RenderPage(PageInterpreter.Build(copy, copy.Pages[0]), copy, copy.Pages[0], 2, new ImageCache())
                .Encode(SKEncodedImageFormat.Png, 90);
            File.WriteAllBytes(Path.Combine(folder, "unterschriften.png"), data.ToArray());
        }
    }

    [Fact]
    public void Signature_FitsItsBox_KeepingTheAspect()
    {
        var signature = new SignatureElement { Rect = new PdfRectangle(0, 0, 400, 50), Name = "Max Mustermann", FontFile = "PatrickHand-Regular.ttf" };
        var aspect = ElementAppearance.SignatureAspect(signature);
        Assert.InRange(aspect, 3, 12);
        var (glyphs, bounds) = ElementAppearance.SignatureOutlines(signature);
        Assert.Equal(13, glyphs.Count);   // without spaces
        Assert.True(bounds.Width > 5 * bounds.Height / 2);
        Assert.Empty(ElementAppearance.SignatureOutlines(signature with { Name = "  " }).Glyphs);
    }

    [Fact]
    public void Png_DecodesWhatSkiaEncodes()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(37, 21, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (var y = 0; y < 21; y++)
            for (var x = 0; x < 37; x++)
                bitmap.SetPixel(x, y, new SKColor((byte)(x * 7), (byte)(y * 12), (byte)(x + y), (byte)(255 - x * 3)));
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var image = Core.Imaging.PngDecoder.Decode(png.ToArray());
        Assert.Equal((37, 21), (image.Width, image.Height));
        for (var y = 0; y < 21; y += 5)
            for (var x = 0; x < 37; x += 6)
            {
                var d = (y * 37 + x) * 4;
                Assert.Equal(new[] { (byte)(x * 7), (byte)(y * 12), (byte)(x + y), (byte)(255 - x * 3) }, image.Pixels[d..(d + 4)]);
            }
    }

    /// <summary>Broken PNGs give an InvalidDataException: no crash, no huge allocation.</summary>
    [Fact]
    public void Png_BrokenOrHostile_IsRejectedCheaply()
    {
        static byte[] Chunk(string type, byte[] body)
        {
            var bytes = new byte[12 + body.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)body.Length);
            System.Text.Encoding.ASCII.GetBytes(type).CopyTo(bytes, 4);
            body.CopyTo(bytes, 8);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8 + body.Length),
                Core.Imaging.Crc32.Update(Core.Imaging.Crc32.Update(0xFFFFFFFF, bytes.AsSpan(4, 4)), body) ^ 0xFFFFFFFF);
            return bytes;
        }
        static byte[] Header(uint width, uint height, byte depth, byte colorType)
        {
            var b = new byte[13];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b, width);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), height);
            b[8] = depth;
            b[9] = colorType;
            return b;
        }
        static byte[] Zlib(byte[] data)
        {
            using var output = new MemoryStream();
            using (var z = new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionLevel.SmallestSize)) z.Write(data);
            return output.ToArray();
        }
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        byte[] Png(byte[] header, byte[] idat) => [.. signature, .. Chunk("IHDR", header), .. Chunk("IDAT", idat), .. Chunk("IEND", [])];

        Assert.Throws<InvalidDataException>(() => Core.Imaging.PngDecoder.Decode([.. signature, .. Chunk("IHDR", new byte[5]), .. Chunk("IEND", [])]));
        Assert.Throws<InvalidDataException>(() => Core.Imaging.PngDecoder.Decode(Png(Header(4, 4, 0, 0), Zlib(new byte[20]))));   // bit depth 0
        Assert.Throws<InvalidDataException>(() => Core.Imaging.PngDecoder.Decode(Png(Header(60_000, 60_000, 8, 6), Zlib(new byte[10]))));   // 3.6 billion pixels

        // Compression bomb: 10 × 10 pixels, but 200 MB of zeros; only what the image needs is inflated
        var bomb = Zlib(new byte[200 << 20]);
        var alloc = GC.GetAllocatedBytesForCurrentThread();
        var image = Core.Imaging.PngDecoder.Decode(Png(Header(10, 10, 8, 6), bomb));
        Assert.Equal((10, 10), (image.Width, image.Height));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - alloc < 4 << 20, $"{(GC.GetAllocatedBytesForCurrentThread() - alloc) >> 20} MB belegt");
    }

    /// <summary>
    /// tRNS with 16-bit samples (ISO/IEC 15948, 11.3.2.1): the key colour is transparent in RGB too, and only the exact value — a grey that
    /// shares only the upper byte with the key stays opaque (docs/FEHLERGEDAECHTNIS.md, No. 269).
    /// </summary>
    [Fact]
    public void Png_Transparency16Bit_ExactKeyInRgbAndGrey()
    {
        static byte[] Chunk(string type, byte[] body)
        {
            var bytes = new byte[12 + body.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)body.Length);
            System.Text.Encoding.ASCII.GetBytes(type).CopyTo(bytes, 4);
            body.CopyTo(bytes, 8);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8 + body.Length),
                Core.Imaging.Crc32.Update(Core.Imaging.Crc32.Update(0xFFFFFFFF, bytes.AsSpan(4, 4)), body) ^ 0xFFFFFFFF);
            return bytes;
        }
        static byte[] Png(byte colorType, byte[] rows, byte[] key)
        {
            var header = new byte[13];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header, 2);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 1);
            header[8] = 16;
            header[9] = colorType;
            using var output = new MemoryStream();
            using (var z = new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionLevel.SmallestSize)) z.Write(rows);
            return [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Chunk("IHDR", header), .. Chunk("tRNS", key), .. Chunk("IDAT", output.ToArray()), .. Chunk("IEND", [])];
        }
        // RGB: the first pixel is the key 0x1234 0x5678 0x9ABC, the second differs in the lower byte of red
        var rgb = Core.Imaging.PngDecoder.Decode(Png(2, [0, 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0x12, 0x35, 0x56, 0x78, 0x9A, 0xBC], [0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC]));
        Assert.Equal(0, rgb.Pixels[3]);
        Assert.Equal(255, rgb.Pixels[7]);
        // grey: key 0x8001; 0x8001 transparent, 0x80FF (same upper byte) opaque
        var grey = Core.Imaging.PngDecoder.Decode(Png(0, [0, 0x80, 0x01, 0x80, 0xFF], [0x80, 0x01]));
        Assert.Equal(0, grey.Pixels[3]);
        Assert.Equal(255, grey.Pixels[7]);
    }

    [Fact]
    public void WhiteBackground_BecomesTransparent_InkKeepsItsColor()
    {
        // 10 × 10 paper, at the top five pixels of ink and five pixels of ink half over white (anti-aliasing)
        var image = Core.Imaging.RgbaImage.Blank(10, 10);
        for (var i = 0; i < 100; i++)
        {
            byte[] p = i < 5 ? [20, 30, 120, 255] : i < 10 ? [138, 143, 188, 255] : [255, 255, 255, 255];
            p.CopyTo(image.Pixels, i * 4);
        }
        var result = Core.Imaging.ImageTools.RemoveWhiteBackground(image);
        Assert.Equal(0, result.Pixels[50 * 4 + 3]);                  // paper gone
        Assert.Equal(255, result.Pixels[3]);                          // ink opaque …
        Assert.Equal(new byte[] { 20, 30, 120 }, result.Pixels[0..3]); // … in its colour
        Assert.InRange(result.Pixels[5 * 4 + 3], 110, 145);           // half transparent …
        Assert.InRange(result.Pixels[5 * 4], 10, 35);                 // … and the same ink, no light fringe
        var trimmed = Core.Imaging.ImageTools.Trim(result, margin: 0)!;
        Assert.Equal((10, 1), (trimmed.Width, trimmed.Height));
    }

    [Fact]
    public void Ink_AndImage_AreSavedAsPathsAndImage()
    {
        using var original = PdfDocument.Open(TestPdf.Simple(1).Build());
        // A tick as a freehand stroke and a red square with a transparent border as an image
        var ink = new InkElement { Rect = new PdfRectangle(100, 300, 300, 400), Strokes = [[0, 50, 30, 90, 100, 0]], StrokeWidth = 8, Color = 0x000000 };
        var picture = Core.Imaging.RgbaImage.Blank(20, 20);
        for (var y = 5; y < 15; y++)
            for (var x = 5; x < 15; x++)
                new byte[] { 200, 0, 0, 255 }.CopyTo(picture.Pixels, (y * 20 + x) * 4);
        var image = new ImageElement { Rect = new PdfRectangle(350, 300, 450, 400), Image = picture };
        Assert.Equal(1, ElementAppearance.Aspect(image));
        Assert.InRange(ElementAppearance.Aspect(ink), 1.05, 1.15);
        using var copy = Save(original, [PageItem.From(original)[0].WithElements([ink, image])]);

        var list = PageInterpreter.Build(copy, copy.Pages[0]);
        Assert.Equal("Seite 1", string.Concat(list.Text.Select(t => t.Text)).Trim());
        Assert.Contains(list.Items.ToArray(), i => i.Kind == Core.Pdf.Graphics.DisplayItemKind.Image);
        using var bitmap = SkiaRenderer.RenderPage(list, copy, copy.Pages[0], 1, new ImageCache());
        // Image: red in the middle, the (transparent) border shows the page (white)
        var mid = bitmap.GetPixel(400, (int)(841.89 - 350));
        Assert.True(mid.Red > 180 && mid.Green < 40, $"Mitte {mid}");
        var edge = bitmap.GetPixel(355, (int)(841.89 - 350));
        Assert.True(edge.Red > 240 && edge.Green > 240, $"Rand {edge}");
        // Stroke: dark pixels in the rectangle
        var dark = 0;
        for (var y = (int)(841.89 - 400); y < (int)(841.89 - 300); y++)
            for (var x = 100; x < 300; x++)
                if (bitmap.GetPixel(x, y).Red < 80) dark++;
        Assert.True(dark > 500, $"{dark} dunkle Pixel");
    }

    private static string TempKeyFile() =>
        Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"geraet-{Guid.NewGuid():N}.key");

    [Fact]
    public void DeviceCode_PcPartPerPc_AccountPartFollowsTheAccount()
    {
        const string pc1 = "1f6b3c2e-8a47-4d19-b5e0-7c2a9d4e6f81", pc2 = "9d2f7a10-5b3e-4c11-9a77-0e8f1c2d3b4a";
        const string max = "S-1-12-1-1111111111-2222222222-3333333333-4444444444", erika = "S-1-12-1-5555555555-6666666666-7777777777-8888888888";
        var maxAtPc1 = Core.Platform.DeviceCode.Compute(pc1, max, userIsGlobal: true);
        var maxAtPc2 = Core.Platform.DeviceCode.Compute(pc2, max, userIsGlobal: true);
        var erikaAtPc1 = Core.Platform.DeviceCode.Compute(pc1, erika, userIsGlobal: true);
        Assert.Matches("^[0-9A-F]{6}-[0-9A-F]{10}$", maxAtPc1.ToString());
        Assert.Equal(maxAtPc1.User, maxAtPc2.User);   // Organisation account: the same user part on every PC …
        Assert.NotEqual(maxAtPc1.Pc, maxAtPc2.Pc);     // … the PC part shows where
        Assert.Equal(maxAtPc1.Pc, erikaAtPc1.Pc);
        Assert.NotEqual(maxAtPc1.User, erikaAtPc1.User);
        Assert.Equal(maxAtPc1, Core.Platform.DeviceCode.Compute(pc1.ToUpperInvariant() + " ", max, userIsGlobal: true));
        // Linux: uid 1000 exists everywhere, so there it is combined with the device ID
        Assert.NotEqual(Core.Platform.DeviceCode.Compute(pc1, "uid:1000", userIsGlobal: false).User,
            Core.Platform.DeviceCode.Compute(pc2, "uid:1000", userIsGlobal: false).User);
        // On this computer: stable, without computer or user name
        var current = Core.Platform.DeviceCode.Current.ToString();
        Assert.Matches("^[0-9A-F]{6}-[0-9A-F]{10}$", current);
        Assert.DoesNotContain(Environment.MachineName, current, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeviceIdentity_IsCreatedOnce_ProtectedAndSigns()
    {
        var file = TempKeyFile();
        byte[] publicKey;
        byte[] signature;
        var code = Core.Platform.DeviceCode.Compute("geraet", "benutzer", userIsGlobal: true);
        using (var device = Core.Platform.DeviceIdentity.LoadOrCreate(file, code))
        {
            Assert.Equal(code, device.Code);
            publicKey = device.PublicKey;
            signature = device.Sign("Hallo"u8.ToArray());
            Assert.True(Core.Platform.DeviceIdentity.Verify(device.PublicKey, "Hallo"u8.ToArray(), signature));
            Assert.False(Core.Platform.DeviceIdentity.Verify(device.PublicKey, "Hallo!"u8.ToArray(), signature));
        }
        using (var again = Core.Platform.DeviceIdentity.LoadOrCreate(file, code))
            Assert.Equal(publicKey, again.PublicKey);   // the same key at the next start
        // Under Windows bound to the user: the file contains no readable PKCS#8 key
        if (OperatingSystem.IsWindows())
        {
            using var probe = System.Security.Cryptography.ECDsa.Create();
            Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => probe.ImportPkcs8PrivateKey(File.ReadAllBytes(file), out _));
        }
    }

    [Fact]
    public void Seal_SignsOriginAndContent_SurvivesMovingButNotChanging()
    {
        using var device = Core.Platform.DeviceIdentity.LoadOrCreate(TempKeyFile());
        var signature = new SignatureElement { Rect = new PdfRectangle(100, 100, 300, 160), Name = "Max Mustermann", FontFile = "PatrickHand-Regular.ttf" };
        var seal = SignatureSeal.Create(signature, $"Max Mustermann · 16.10.2026 12:00 · Kennung {device.Code}", device);
        Assert.True(seal.Verify());
        Assert.Equal(device.Code.ToString(), seal.Code);
        Assert.True(seal.Matches(signature with { Rect = new PdfRectangle(0, 0, 50, 20) }));   // moved: still matches
        Assert.False(seal.Matches(signature with { Name = "Max Muster" }));
        Assert.False((seal with { Origin = seal.Origin.Replace("12:00", "13:00") }).Verify());   // line changed: invalid
    }

    /// <summary>
    /// A seal over strokes 3.5 pt wide, made under German, checked under English: before, the stroke width went into the hash
    /// with the decimal separator of the language („3,5“ / „3.5“), and the seal no longer matched. Seals from before, with the
    /// German comma, still match; another width does not.
    /// </summary>
    [Fact]
    public void Seal_MatchesIndependentOfTheLanguage()
    {
        using var device = Core.Platform.DeviceIdentity.LoadOrCreate(TempKeyFile());
        var ink = new InkElement { Rect = new PdfRectangle(100, 400, 300, 460), Strokes = [[0, 0, 10, 10, 20, 0]], StrokeWidth = 3.5 };
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
            var seal = SignatureSeal.Create(ink, $"16.10.2026 12:05 · Kennung {device.Code}", device);
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            Assert.True(seal.Matches(ink));
            Assert.False(seal.Matches(ink with { StrokeWidth = 4.5 }));

            var before = seal with { ContentHash = SignatureSeal.ContentHashOf(ink, System.Globalization.CultureInfo.GetCultureInfo("de-DE")) };
            Assert.True(before.Matches(ink));
            Assert.False(before.Matches(ink with { StrokeWidth = 4.5 }));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void Sealed_OriginLineAndProvenanceAreSaved_AndAccumulate()
    {
        using var device = Core.Platform.DeviceIdentity.LoadOrCreate(TempKeyFile());
        using var original = PdfDocument.Open(TestPdf.Simple(1).Build());
        var signature = new SignatureElement { Rect = new PdfRectangle(100, 600, 300, 660), Name = "Max Mustermann", FontFile = "PatrickHand-Regular.ttf" };
        var origin = $"Max Mustermann · 16.10.2026 12:00 · Kennung {device.Code}";
        signature = signature with { Seal = SignatureSeal.Create(signature, origin, device) };
        using var first = Save(original, [PageItem.From(original)[0].WithElements([signature])]);

        Assert.Contains(origin, TextOf(first, 0));   // origin line as text below the signature
        // … but not editable with "Edit text"; the rest of the text on the page stays editable
        var blocks = Core.Pdf.Text.TextBlocks.Find(first, first.Pages[0]);
        Assert.Equal(Core.Pdf.Text.TextBlocks.SignatureLock, Assert.Single(blocks, b => b.Text.Contains(device.Code.ToString())).Locked);
        Assert.Contains(blocks, b => b.Locked is null);
        var record = Assert.Single(SealRecord.Read(first));
        Assert.Equal(1, record.Page);
        Assert.Equal(origin, record.Seal.Origin);
        Assert.Equal(device.Code.ToString(), record.Seal.Code);
        Assert.True(record.Seal.Verify());
        // Neither computer nor user name in the file (not in the metadata either)
        var info = first.Trailer.GetDictionary(PdfName.Info)!.GetString(PdfWriter.SignaturesKey)!.ToText();
        Assert.DoesNotContain(Environment.MachineName, info, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, info, StringComparison.OrdinalIgnoreCase);

        // Sign again and save: the first record stays
        var ink = new InkElement { Rect = new PdfRectangle(100, 400, 300, 460), Strokes = [[0, 0, 10, 10, 20, 0]] };
        ink = ink with { Seal = SignatureSeal.Create(ink, $"16.10.2026 12:05 · Kennung {device.Code}", device) };
        using var second = Save(first, [PageItem.From(first)[0].WithElements([ink])]);
        var records = SealRecord.Read(second);
        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.True(r.Seal.Verify()));
    }

    /// <summary>
    /// Provenance records follow their page (reordered, deleted, from an inserted file), and a
    /// broken entry does not spoil the others.
    /// </summary>
    [Fact]
    public void Provenance_FollowsPages_BrokenEntriesAreDropped()
    {
        using var device = Core.Platform.DeviceIdentity.LoadOrCreate(TempKeyFile());
        SignatureElement Signed(string name)
        {
            var s = new SignatureElement { Rect = new PdfRectangle(100, 600, 300, 660), Name = name, FontFile = "PatrickHand-Regular.ttf" };
            return s with { Seal = SignatureSeal.Create(s, $"{name} · 16.10.2026 12:00 · Kennung {device.Code}", device) };
        }
        using var original = PdfDocument.Open(TestPdf.Simple(3).Build());
        var items = PageItem.From(original);
        using var signed = Save(original, [items[0], items[1].WithElements([Signed("Max")]), items[2].WithElements([Signed("Mustermann")])]);
        Assert.Equal([2, 3], SealRecord.Read(signed).Select(r => r.Page));

        // Page 1 deleted, the others swapped, plus a page from a second signed file
        using var other = Save(original, [items[0].WithElements([Signed("Musterstadt")])]);
        var again = PageItem.From(signed);
        using var rearranged = Save(signed, [again[2], again[1], PageItem.From(other)[0]]);
        var records = SealRecord.Read(rearranged);
        Assert.Equal(["Mustermann", "Max", "Musterstadt"], records.Select(r => r.Seal.Origin.Split(' ')[0]));
        Assert.Equal([1, 2, 3], records.Select(r => r.Page));
        Assert.All(records, r => Assert.True(r.Seal.Verify()));

        // Without the signed pages: no records left
        using var none = Save(signed, [PageItem.From(signed)[0]]);
        Assert.Empty(SealRecord.Read(none));
        Assert.Null(none.Trailer.GetDictionary(PdfName.Info)?.GetString(PdfWriter.SignaturesKey));

        // Broken entry next to a valid one
        var pdf = TestPdf.Simple(1);
        var json = $"[{{\"seite\":1,\"herkunft\":\"x\"}},{SealJson(Signed("Max").Seal!)}]";
        pdf.InfoNumber = pdf.Add(TestPdf.Dict(("PDFKneadSignatures", new PdfString(System.Text.Encoding.UTF8.GetBytes(json)))));
        using var broken = PdfDocument.Open(pdf.Build());
        Assert.Single(SealRecord.Read(broken));
        using var resaved = Save(broken, PageItem.From(broken));
        Assert.Single(SealRecord.Read(resaved));

        static string SealJson(SignatureSeal s) =>
            $"{{\"seite\":1,\"herkunft\":\"{s.Origin}\",\"kennung\":\"{s.Code}\",\"schluessel\":\"{Convert.ToBase64String(s.PublicKey)}\"," +
            $"\"inhalt\":\"{Convert.ToBase64String(s.ContentHash)}\",\"signatur\":\"{Convert.ToBase64String(s.Signature)}\"}}";
    }

    /// <summary>A locked key file stays; an unreadable one is backed up with a timestamp (never overwritten).</summary>
    [Fact]
    public void DeviceKey_LockedIsKept_UnreadableIsBackedUp()
    {
        var file = TempKeyFile();
        byte[] saved;
        using (Core.Platform.DeviceIdentity.LoadOrCreate(file)) saved = File.ReadAllBytes(file);
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        using (var temporary = Core.Platform.DeviceIdentity.LoadOrCreate(file))
            Assert.NotNull(temporary.PublicKey);   // locked: a transient key …
        Assert.Equal(saved, File.ReadAllBytes(file));   // … the file stays as it was
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(file)!, Path.GetFileName(file) + ".unlesbar*"));

        File.WriteAllBytes(file, [1, 2, 3]);   // broken
        using (Core.Platform.DeviceIdentity.LoadOrCreate(file)) { }
        var backups = Directory.GetFiles(Path.GetDirectoryName(file)!, Path.GetFileName(file) + ".unlesbar*");
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Assert.Single(backups)));
        Assert.NotEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(file));   // new key saved

        // Broken again right away (the same second): the first backup stays and a second one is added
        File.WriteAllBytes(file, [4, 5, 6]);
        using (Core.Platform.DeviceIdentity.LoadOrCreate(file)) { }
        backups = Directory.GetFiles(Path.GetDirectoryName(file)!, Path.GetFileName(file) + ".unlesbar*");
        Assert.Equal(2, backups.Length);
        Assert.Contains(backups, b => File.ReadAllBytes(b).SequenceEqual(new byte[] { 1, 2, 3 }));
        Assert.Contains(backups, b => File.ReadAllBytes(b).SequenceEqual(new byte[] { 4, 5, 6 }));
    }

    [Fact]
    public void Flatten_FieldsAndAnnotationsBecomeContent_LinksStay()
    {
        // First a file with a form field (with appearance stream) and a link
        var pdf = TestPdf.Simple(1);
        var link = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Link")), ("Rect", TestPdf.Box(0, 0, 50, 50)),
            ("Dest", new PdfArray([TestPdf.Ref(5), TestPdf.N("Fit")]))));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(link)]));
        using var original = PdfDocument.Open(pdf.Build());
        var field = Text("Max Mustermann", 100, 600, 300, 40) with { IsField = true, FontSize = 20 };
        using var withField = Save(original, [PageItem.From(original)[0].WithElements([field])]);
        Assert.Equal(2, withField.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Count);

        // Flattening: field in the content, no form, the link stays; plus a new field that becomes flat right away
        var extra = Text("Musterstadt", 100, 500, 300, 40) with { IsField = true };
        var reopened = PageItem.From(withField)[0];   // the saved field is an element again
        Assert.IsType<TextElement>(Assert.Single(reopened.Elements));
        using var flat = Save(withField, [reopened.WithElements([.. reopened.Elements, extra])], new PdfSaveOptions { Flatten = true });
        Assert.Null(flat.Catalog!.GetDictionary(PdfName.Get("AcroForm")));
        var annots = flat.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!;
        Assert.Single(annots);
        Assert.Equal("Link", annots.Get(0).AsDictionary()!.GetName(PdfName.Subtype)!.Value);
        var text = TextOf(flat, 0);
        Assert.Contains("Max Mustermann", text);
        Assert.Contains("Musterstadt", text);

        // Looks as before (field drawn as an annotation), except for the new field
        using var before = SkiaRenderer.RenderPage(PageInterpreter.Build(withField, withField.Pages[0]), withField, withField.Pages[0], 1, new ImageCache());
        using var after = SkiaRenderer.RenderPage(PageInterpreter.Build(flat, flat.Pages[0]), flat, flat.Pages[0], 1, new ImageCache());
        var differing = 0;
        for (var y = 0; y < (int)(841.89 - 545); y++)   // above the new field
            for (var x = 0; x < after.Width; x++)
                if (Math.Abs(before.GetPixel(x, y).Red - after.GetPixel(x, y).Red) > 8) differing++;
        Assert.True(differing < 20, $"{differing} Pixel weichen ab");
    }

    /// <summary>
    /// Texts, text fields, images and signature fields stay editable after saving (stamp or field
    /// with /PDFKneadElement) and look the same in other programs; signatures become fixed; flattening fixes everything.
    /// </summary>
    [Fact]
    public void Saved_ElementsStayEditable_SignaturesBecomeFixed()
    {
        using var device = Core.Platform.DeviceIdentity.LoadOrCreate(TempKeyFile());
        using var original = PdfDocument.Open(TestPdf.Simple(1).Build());
        var pixels = Core.Imaging.RgbaImage.Blank(40, 20);
        for (var i = 0; i < 40 * 20; i++) ((byte[])[(byte)(i % 40 * 6), 80, (byte)(i / 40 * 12), (byte)(i % 3 == 0 ? 0 : 255)]).CopyTo(pixels.Pixels, i * 4);
        var text = Text("Max Mustermann\nMusterstadt", 60, 700, 250, 50) with { Font = ElementFont.Serif, FontSize = 14, Color = 0x1A4FA0, Alignment = ElementAlignment.Center };
        var field = Text("Musterstraße 12", 60, 600, 200, 24) with { IsField = true, FieldName = "Strasse", Multiline = false };
        var picture = new ImageElement { Rect = new PdfRectangle(320, 600, 400, 640), Image = pixels, Rotation = 90 };
        var sigField = new SignatureFieldElement { Rect = new PdfRectangle(60, 450, 260, 500), Label = "Unterschrift", FieldName = "Kunde" };
        var signature = new SignatureElement { Rect = new PdfRectangle(300, 450, 500, 500), Name = "Max Mustermann", FontFile = "PatrickHand-Regular.ttf" };
        var photo = new ImageElement { Rect = new PdfRectangle(300, 380, 400, 420), Image = pixels, IsSignature = true };
        var sealedText = text with { Rect = new PdfRectangle(60, 300, 250, 340), Seal = SignatureSeal.Create(text, "Max Mustermann · 16.10.2026", device) };
        var page = PageItem.From(original)[0].WithElements([text, field, picture, sigField, signature, photo, sealedText]);
        using var saved = Save(original, [page]);

        // Reopened: the editable ones come back as elements, in their order and with all settings
        var back = PageItem.From(saved)[0].Elements;
        Assert.Equal(4, back.Count);
        var t = Assert.IsType<TextElement>(back[0]);
        Assert.Equal((text.Text, text.Font, text.FontSize, text.Color, text.Alignment, text.Multiline, false), (t.Text, t.Font, t.FontSize, t.Color, t.Alignment, t.Multiline, t.IsField));
        Assert.Equal(text.Rect.Left, t.Rect.Left, 3);
        Assert.Equal(text.Rect.Top, t.Rect.Top, 3);
        var f = Assert.IsType<TextElement>(back[1]);
        Assert.Equal(("Musterstraße 12", true, "Strasse", false), (f.Text, f.IsField, f.FieldName, f.Multiline));
        var p = Assert.IsType<ImageElement>(back[2]);
        Assert.Equal((90, false), (p.Rotation, p.IsSignature));
        Assert.Equal(pixels.Pixels, p.Image.Pixels);
        var s = Assert.IsType<SignatureFieldElement>(back[3]);
        Assert.Equal(("Unterschrift", "Kunde"), (s.Label, s.FieldName));

        // Other programs (all annotations drawn) see the same as PDF Knead (page without them + elements on top)
        using var other = SkiaRenderer.RenderPage(PageInterpreter.Build(saved, saved.Pages[0]), saved, saved.Pages[0], 1, new ImageCache());
        using var own = SkiaRenderer.RenderPage(PageInterpreter.Build(saved, saved.Pages[0], withoutOwnElements: true), saved, saved.Pages[0], 1, new ImageCache());
        using (var canvas = new SKCanvas(own)) ElementOverlay.Draw(canvas, saved.Pages[0], back, 1.0);
        var differing = 0;
        for (var y = 0; y < other.Height; y++)
            for (var x = 0; x < other.Width; x++)
                if (Math.Abs(other.GetPixel(x, y).Red - own.GetPixel(x, y).Red) > 24) differing++;
        Assert.True(differing < 30, $"{differing} Pixel weichen ab");
        Assert.Contains("Max Mustermann", TextOf(saved, 0));   // signature and sealed text fixed in the content

        // Another program has filled in the field: this value applies
        using var filled = Save(original, [page]);
        var widget = filled.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(1).AsDictionary()!;
        widget.Set(PdfName.Get("V"), new PdfString(PdfTextEncoding.Encode("Musterweg 3")));
        Assert.Equal("Musterweg 3", ((TextElement)PageItem.From(filled)[0].Elements[1]).Text);

        // Flattening: everything fixed, no marking left
        using var flat = Save(saved, PageItem.From(saved), new PdfSaveOptions { Flatten = true });
        Assert.Empty(PageItem.From(flat)[0].Elements);
        Assert.Null(flat.Catalog!.GetBoolean(ElementStore.CatalogKey));
        Assert.Contains("Musterstadt", TextOf(flat, 0));
    }

    /// <summary>Foreign files: without the mark in the catalog nothing is taken over as an element (and nothing is searched for).</summary>
    [Fact]
    public void ForeignFile_WithoutCatalogMark_KeepsAnnotations()
    {
        using var original = PdfDocument.Open(TestPdf.Simple(1).Build());
        using var saved = Save(original, [PageItem.From(original)[0].WithElements([Text("Max Mustermann")])]);
        Assert.Single(PageItem.From(saved)[0].Elements);
        // Mark removed (like a program that rewrites the catalog): the annotation stays an annotation
        var pdf = TestPdf.Simple(1);
        var data = TestPdf.Dict(("Art", TestPdf.N("Text")), ("Text", new PdfString("x"u8.ToArray())));
        var stamp = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Stamp")), ("Rect", TestPdf.Box(10, 10, 50, 50)), ("PDFKneadElement", data)));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(stamp)]));
        using var foreign = PdfDocument.Open(pdf.Build());
        Assert.Empty(PageItem.From(foreign)[0].Elements);
        using var resaved = Save(foreign, PageItem.From(foreign));
        Assert.Single(resaved.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!);
    }

    /// <summary>
    /// Text as one string per line (widths in /W exact, no adjustment after almost every
    /// glyph); pages with fields only keep the shared resources (one object, not one copy per page).
    /// </summary>
    [Fact]
    public void SavedText_OneStringPerLine_FieldPagesShareResources()
    {
        var form = ElementAppearance.For(Text("Max Mustermann Musterstadt")).Form;
        var content = System.Text.Encoding.ASCII.GetString(form.RawData!);
        var adjustments = System.Text.RegularExpressions.Regex.Matches(content, @">\s*-?[0-9.]+\s*<").Count;
        Assert.True(adjustments <= 2, content);

        using var original = PdfDocument.Open(TestPdf.Simple(3).Build());
        var field = Text("Max Mustermann", 100, 600, 200, 30) with { IsField = true };
        using var saved = Save(original, [.. PageItem.From(original).Select(p => p.WithElements([field with { }]))]);
        var shared = saved.Pages.Select(p => (p.Dictionary.GetRaw(PdfName.Resources) as PdfReference)?.Id).ToList();
        Assert.All(shared, id => Assert.NotNull(id));
        Assert.Single(shared.Distinct());
        Assert.Equal(3, saved.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.GetArray(PdfName.Get("Fields"))!.Count);
    }

    /// <summary>
    /// A moved element takes over the form XObject of the original (dragging typesets nothing anew)
    /// and looks exactly like one newly built there, both in the display and in the saved file.
    /// </summary>
    [Fact]
    public void Moved_SharesForm_LooksLikeNewlyBuilt()
    {
        using var original = PdfDocument.Open(TestPdf.Simple(1).Build());
        var page = original.Pages[0];
        foreach (var element in (PageElement[])[Text("Max Mustermann", 100, 600, 200, 40, rotation: 90),
                     new SignatureElement { Rect = new PdfRectangle(100, 400, 300, 450), Name = "Max Mustermann", FontFile = EmbeddedFonts.Handwriting[0] }])
        {
            var target = new PdfRectangle(element.Rect.Left + 37.3, element.Rect.Bottom - 12.1, element.Rect.Right + 37.3, element.Rect.Top - 12.1);
            var moved = ElementAppearance.Moved(element, target);
            Assert.Same(ElementAppearance.For(element).Form, ElementAppearance.For(moved).Form);
            Assert.Equal(target.Width, moved.Rect.Width, 9);
            var fresh = element with { Rect = moved.Rect };

            // Display: via the display list of the original, moved
            SKBitmap Draw(PageElement e)
            {
                var bitmap = new SKBitmap(600, 842);
                using var canvas = new SKCanvas(bitmap);
                canvas.Clear(SKColors.White);
                ElementOverlay.Draw(canvas, page, [e], 1.0);
                return bitmap;
            }
            using (var a = Draw(moved))
            using (var b = Draw(fresh))
                Assert.Equal(b.Bytes, a.Bytes);

            // Saved
            using var savedMoved = Save(original, [PageItem.From(original)[0].WithElements([moved])]);
            using var savedFresh = Save(original, [PageItem.From(original)[0].WithElements([fresh])]);
            using var c = SkiaRenderer.RenderPage(PageInterpreter.Build(savedMoved, savedMoved.Pages[0]), savedMoved, savedMoved.Pages[0], 1, new ImageCache());
            using var d = SkiaRenderer.RenderPage(PageInterpreter.Build(savedFresh, savedFresh.Pages[0]), savedFresh, savedFresh.Pages[0], 1, new ImageCache());
            Assert.Equal(d.Bytes, c.Bytes);
        }
    }

    /// <summary>
    /// Fields without an appearance stream (or with /NeedAppearances) must not lose their value when flattening: the value
    /// is typeset like a text element; password fields stay empty, and the value of the parent field counts.
    /// </summary>
    [Fact]
    public void Flatten_FieldsWithoutAppearance_KeepTheirValue()
    {
        var pdf = TestPdf.Simple(1);
        var plain = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Widget")), ("Rect", TestPdf.Box(100, 600, 400, 630)),
            ("FT", TestPdf.N("Tx")), ("T", new PdfString("Name"u8.ToArray())), ("V", new PdfString("Max Mustermann"u8.ToArray())),
            ("DA", new PdfString("/Helv 0 Tf 0 0 1 rg"u8.ToArray()))));
        var parent = pdf.Add(TestPdf.Dict(("FT", TestPdf.N("Tx")), ("T", new PdfString("Ort"u8.ToArray())), ("V", new PdfString("Musterstadt"u8.ToArray())),
            ("Q", PdfInteger.Of(1))));
        var kid = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Widget")), ("Rect", TestPdf.Box(100, 500, 400, 530)),
            ("Parent", TestPdf.Ref(parent))));
        var password = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Widget")), ("Rect", TestPdf.Box(100, 400, 400, 430)),
            ("FT", TestPdf.N("Tx")), ("Ff", PdfInteger.Of(1 << 13)), ("T", new PdfString("Geheim"u8.ToArray())), ("V", new PdfString("Kennwort"u8.ToArray()))));
        ((PdfDictionary)pdf.Get(parent)).Set(PdfName.Get("Kids"), new PdfArray([TestPdf.Ref(kid)]));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(plain), TestPdf.Ref(kid), TestPdf.Ref(password)]));
        var catalog = (PdfDictionary)pdf.Get(1);
        catalog.Set(PdfName.Get("AcroForm"), TestPdf.Dict(("Fields", new PdfArray([TestPdf.Ref(plain), TestPdf.Ref(parent), TestPdf.Ref(password)])),
            ("DA", new PdfString("/Helv 12 Tf 0 g"u8.ToArray())), ("NeedAppearances", PdfBoolean.True)));
        using var original = PdfDocument.Open(pdf.Build());

        using var flat = Save(original, PageItem.From(original), new PdfSaveOptions { Flatten = true });
        Assert.Null(flat.Catalog!.GetDictionary(PdfName.Get("AcroForm")));
        var text = TextOf(flat, 0);
        Assert.Contains("Max Mustermann", text);
        Assert.Contains("Musterstadt", text);
        Assert.DoesNotContain("Kennwort", text);

        // "/Helv 0 Tf … rg": automatic size, blue
        var (font, size, color) = Core.Pdf.Writing.FieldAppearance.ParseDefaultAppearance("/Helv 0 Tf 0 0 1 rg");
        Assert.Equal((ElementFont.Sans, 0d, 0x0000FF), (font, size, color));
        Assert.Equal(ElementFont.Mono, Core.Pdf.Writing.FieldAppearance.ParseDefaultAppearance("/Cour 9 Tf").Font);
    }

    /// <summary>
    /// Sample sheet: every handwriting font, drawn and as a photo, each with descenders and origin line,
    /// one below the other, for inspecting the spacing (stored with PDFKNEAD_ABLAGE).
    /// </summary>
    [Fact]
    public void SignatureSampleSheet_AllKindsWithDescenders()
    {
        using var device = Core.Platform.DeviceIdentity.LoadOrCreate(TempKeyFile());
        var origin = $"Max Mustermann · 16.10.2026 12:00 · Kennung {device.Code}";
        var kinds = new List<(string Label, PageElement Element)>();
        foreach (var font in EmbeddedFonts.Handwriting)
            kinds.Add((font, new SignatureElement { Rect = default, Name = "Max Mustermann gqjpy", FontFile = font }));
        // Drawn: a flourish with a loop downwards
        var stroke = new List<float>();
        for (var t = 0.0; t <= 1.0001; t += 0.01)
            stroke.AddRange([(float)(t * 300), (float)(40 + 25 * Math.Sin(t * 14) + (t is > 0.55 and < 0.75 ? 45 * Math.Sin((t - 0.55) / 0.2 * Math.PI) : 0))]);
        kinds.Add(("Gezeichnet", new InkElement { Rect = default, Strokes = [[.. stroke], [310, 20, 320, 95]], StrokeWidth = 3 }));
        // Photo: dark writing with a descender on light paper, background removed
        using (var bitmap = new SKBitmap(new SKImageInfo(500, 160, SKColorType.Rgba8888, SKAlphaType.Unpremul)))
        {
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(new SKColor(248, 246, 240));
                using var paint = new SKPaint { Color = new SKColor(30, 45, 120), StrokeWidth = 5, IsAntialias = true, Style = SKPaintStyle.Stroke };
                canvas.DrawOval(new SKRect(40, 30, 160, 100), paint);
                canvas.DrawLine(150, 95, 260, 60, paint);
                canvas.DrawLine(260, 60, 250, 150, paint);   // descender
                canvas.DrawLine(250, 150, 450, 70, paint);
            }
            var image = new Core.Imaging.RgbaImage(500, 160, bitmap.Bytes);
            image = Core.Imaging.ImageTools.Trim(Core.Imaging.ImageTools.RemoveWhiteBackground(image))!;
            kinds.Add(("Foto", new ImageElement { Rect = default, Image = image }));
        }

        // 9 rows per page: the signature on the left (180 × matching height, as on a click), the label on the right
        var pages = new List<PageItem>();
        for (var start = 0; start < kinds.Count; start += 9)
        {
            var page = BlankPage.Create(595.28, 841.89);
            var elements = new List<PageElement>();
            for (var i = start; i < Math.Min(kinds.Count, start + 9); i++)
            {
                var (label, template) = kinds[i];
                var sealedTemplate = template with { Seal = SignatureSeal.Create(template, origin, device) };
                var aspect = ElementAppearance.Aspect(sealedTemplate);
                double width = 180, height = width / aspect;
                if (height > 60) (height, width) = (60, 60 * aspect);
                height = ElementAppearance.HeightFor(sealedTemplate, height);
                width += 2 * ElementAppearance.Padding;
                var top = 800 - (i - start) * 88;
                elements.Add(sealedTemplate with { Rect = new PdfRectangle(60, top - height, 60 + width, top) });
                elements.Add(new TextElement { Rect = new PdfRectangle(330, top - 20, 560, top), Text = label, FontSize = 9, Color = 0x707070 });
            }
            pages.Add(page.WithElements(elements));
        }
        using var sheet = Save(pages[0].Source, pages);
        Assert.Equal(pages.Count, sheet.PageCount);
        Assert.Equal(kinds.Count, SealRecord.Read(sheet).Count);
        Assert.All(SealRecord.Read(sheet), r => Assert.True(r.Seal.Verify()));
    }

    [Fact]
    public void Subset_KeepsUsedGlyphs_AndCompositeParts()
    {
        var data = EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf");
        var full = OpenTypeFont.Parse(data);
        var a = full.GlyphForUnicode('A');
        var umlaut = full.GlyphForUnicode('Ä');   // composite in Liberation (A + dots)
        var b = full.GlyphForUnicode('B');
        var subset = OpenTypeFont.Parse(TrueTypeSubsetter.Subset(data, [a, umlaut]));

        Assert.Equal(full.GlyphCount, subset.GlyphCount);
        Assert.True(data.Length > 10 * TrueTypeSubsetter.Subset(data, [a, umlaut]).Length);
        Assert.Equal(full.Outline(a).Bounds, subset.Outline(a).Bounds);
        Assert.Equal(full.Outline(umlaut).Bounds, subset.Outline(umlaut).Bounds);
        Assert.True(subset.Outline(b).IsEmpty);
        Assert.Equal(full.AdvanceWidth(b), subset.AdvanceWidth(b));
    }

    [Fact]
    public void History_ElementsUndoAndRedo()
    {
        using var doc = PdfDocument.Open(TestPdf.Simple().Build());
        var history = new PageHistory(PageItem.From(doc));
        var first = Text("eins");
        history.AddElement(0, first, "Text einfügen");
        var id = history.Pages[0].Id;
        var changed = first with { Text = "zwei" };
        Assert.True(history.ReplaceElement(0, first, changed, "Text ändern"));
        Assert.Equal(id, history.Pages[0].Id);   // the tiles of the page stay valid; the view draws the elements on top
        Assert.Same(changed, history.Pages[0].Elements[0]);
        Assert.False(history.ReplaceElement(0, first, changed, "nochmal"));   // the old one is no longer there
        history.Undo();
        Assert.Same(first, history.Pages[0].Elements[0]);
        history.Undo();
        Assert.Empty(history.Pages[0].Elements);
        history.Redo();
        history.Redo();
        Assert.True(history.RemoveElements(0, [changed], "Löschen"));
        Assert.Empty(history.Pages[0].Elements);
        // Rotating keeps the elements
        history.Undo();
        history.Rotate([0], 90);
        Assert.Same(changed, history.Pages[0].Elements[0]);
    }

    [Fact]
    public void Overlay_DrawsElementsOverThePage()
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var page = doc.Pages[0];
        var element = Text("XXXXXXXX", 100, 600, 300, 60) with { FontSize = 40 };
        using var plain = SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page), doc, page, 1, new ImageCache());
        using var withElement = SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page), doc, page, 1, new ImageCache());
        using (var canvas = new SKCanvas(withElement)) ElementOverlay.Draw(canvas, page, [element], 1);
        var top = (int)(841.89 - 660);
        Assert.True(DarkPixels(withElement, 100, top, 400, top + 60) > DarkPixels(plain, 100, top, 400, top + 60) + 300);
        Assert.Same(ElementOverlay.List(page, element), ElementOverlay.List(page, element));   // built once
    }

    [Fact]
    public void Reloaded_AfterSaving_NewKeyOnlyWithElements()
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(2).Build());
        var items = PageItem.From(doc);
        var withText = items[0].WithElements([Text("x")]);
        Assert.Equal(items[0].Id, withText.Id);
        Assert.Equal(items[1].Id, items[1].Reloaded(doc, doc.Pages[1]).Id);
        var reloaded = withText.Reloaded(doc, doc.Pages[0]);
        Assert.NotEqual(withText.Id, reloaded.Id);
        Assert.Empty(reloaded.Elements);
    }
    /// <summary>
    /// A redaction mark stands out on any ground — over black and over a coloured image too, not only on white paper: inside it a fair
    /// share of the pixels differs clearly from what lies under it. Only a light red tint with a one-pixel border was hardly to be seen
    /// over images (docs/FEHLERGEDAECHTNIS.md, No. 191).
    /// </summary>
    [Fact]
    public void RedactionMark_StandsOutOnAnyGround()
    {
        var pdf = TestPdf.Simple(1);
        var image = pdf.Add(TestPdf.FlateStream(Enumerable.Repeat(new byte[] { 0, 180, 0 }, 4).SelectMany(b => b).ToArray(), TestPdf.Dict(
            ("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(2)), ("Height", PdfInteger.Of(2)),
            ("ColorSpace", TestPdf.N("DeviceRGB")), ("BitsPerComponent", PdfInteger.Of(8)))));
        pdf.Replace(4, TestPdf.FlateStream("0 g 100 500 200 100 re f q 200 0 0 100 100 300 cm /Im0 Do Q 1 1 1 rg 100 100 200 100 re f"u8.ToArray()));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("Im0", TestPdf.Ref(image))))));
        using var doc = PdfDocument.Open(pdf.Build());
        var page = doc.Pages[0];
        using var plain = SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page), doc, page, 1, new ImageCache());
        using var marked = plain.Copy();
        using (var canvas = new SKCanvas(marked))
            ElementOverlay.Draw(canvas, page, [
                new RedactionElement { Rect = new PdfRectangle(110, 510, 290, 590) },   // over black
                new RedactionElement { Rect = new PdfRectangle(110, 310, 290, 390) },   // over the green image
                new RedactionElement { Rect = new PdfRectangle(110, 110, 290, 190) },   // over white paper
            ], 1);
        var top = page.MediaBox.Top;
        foreach (var bottom in new[] { 510, 310, 110 })
        {
            // inside, two points away from the border
            int clear = 0, all = 0;
            for (var y = (int)(top - bottom - 78); y < (int)(top - bottom - 2); y++)
                for (var x = 112; x < 288; x++)
                {
                    var (a, b) = (plain.GetPixel(x, y), marked.GetPixel(x, y));
                    all++;
                    if (Math.Max(Math.Abs(a.Red - b.Red), Math.Max(Math.Abs(a.Green - b.Green), Math.Abs(a.Blue - b.Blue))) > 60) clear++;
                }
            Assert.True(clear >= all / 12, $"Markierung bei y = {bottom}: nur {clear} von {all} Pixeln heben sich ab");
        }
    }
}
