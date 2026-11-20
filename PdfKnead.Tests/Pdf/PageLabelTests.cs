using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using static PdfKnead.Tests.Pdf.TestPdf;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Page labels follow their pages when pages are deleted, reordered or merged (ISO 32000-2, 12.4.2; ISO 14289-2, 8.12.3;
/// docs/ABLAUFPLAN.md, 3.4; docs/FEHLERGEDAECHTNIS.md, No. 494). Before, they were dropped as soon as the page order changed.
/// </summary>
public sealed class PageLabelTests
{
    /// <summary>A file with <paramref name="pages"/> pages and these ranges (start index, style, prefix, start number).</summary>
    private static byte[] Labelled(int pages, params (int Start, string? Style, string Prefix, int First)[] ranges)
    {
        var pdf = Simple(pages);
        if (ranges.Length > 0)
        {
            var nums = new PdfArray();
            foreach (var (start, style, prefix, first) in ranges)
            {
                var range = new PdfDictionary();
                if (style is not null) range.Set(PdfName.Get("S"), N(style));
                if (prefix.Length > 0) range.Set(PdfName.Get("P"), new PdfString(PdfTextEncoding.Encode(prefix)));
                if (first != 1) range.Set(PdfName.Get("St"), PdfInteger.Of(first));
                nums.Add(PdfInteger.Of(start));
                nums.Add(range);
            }
            ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("PageLabels"), Ref(pdf.Add(Dict(("Nums", nums)))));
        }
        return pdf.Build();
    }

    private static PdfDocument Saved(PdfDocument doc, IReadOnlyList<PageItem> pages)
    {
        using var output = new MemoryStream();
        PdfWriter.Write(doc, pages, output);
        return PdfDocument.Open(output.ToArray());
    }

    /// <summary>The labels as a reader shows them: „i“, „ii“, „1“, „Anlage-1“.</summary>
    private static List<string> Shown(PdfDocument doc) =>
        PageLabels.Of(doc)?.Select(l => l.Prefix + l.Style switch
        {
            "D" => l.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "r" => Roman(l.Number),
            "R" => Roman(l.Number).ToUpperInvariant(),
            _ => "",
        }).ToList() ?? [];

    private static string Roman(int n) => n switch { 1 => "i", 2 => "ii", 3 => "iii", 4 => "iv", _ => "?" };

    [Fact]
    public void DeletedFirstPage_LabelsFollow()
    {
        using var doc = PdfDocument.Open(Labelled(5, (0, "r", "", 1), (2, "D", "", 1)));
        var pages = PageItem.From(doc);
        using var saved = Saved(doc, pages.Skip(1).ToList());
        Assert.Equal(["ii", "1", "2", "3"], Shown(saved));
        var nums = saved.Catalog!.GetDictionary(PdfName.Get("PageLabels"))!.GetArray(PdfName.Get("Nums"))!;
        Assert.Equal(2, ((PdfDictionary)nums.Get(1)).GetInteger(PdfName.Get("St")));
    }

    [Fact]
    public void DeletePage_PageLabelsFollowTheRemainingPages()
    {
        using var doc = PdfDocument.Open(Labelled(4, (0, "r", "", 1), (2, "D", "", 1)));
        var pages = PageItem.From(doc);
        using var saved = Saved(doc, [pages[0], pages[2], pages[3]]);
        Assert.Equal(["i", "1", "2"], Shown(saved));
    }

    [Fact]
    public void Reordered_LabelsMoveWithPages()
    {
        using var doc = PdfDocument.Open(Labelled(4, (0, "r", "", 1), (2, "D", "", 1)));
        var pages = PageItem.From(doc);
        using var saved = Saved(doc, [pages[3], pages[0], pages[2], pages[1]]);
        Assert.Equal(["2", "i", "1", "ii"], Shown(saved));
    }

    [Fact]
    public void Merged_UnlabelledSource_GetsDecimalRangeOfItsOwn()
    {
        using var doc = PdfDocument.Open(Labelled(2, (0, "r", "", 1)));
        using var other = PdfDocument.Open(Labelled(2));
        using var saved = Saved(doc, [.. PageItem.From(doc), .. PageItem.From(other)]);
        Assert.Equal(["i", "ii", "1", "2"], Shown(saved));
    }

    [Fact]
    public void Prefix_IsKept()
    {
        using var doc = PdfDocument.Open(Labelled(4, (0, "D", "", 1), (2, "D", "Anlage-", 1)));
        var pages = PageItem.From(doc);
        using var saved = Saved(doc, [pages[0], pages[2], pages[3]]);
        Assert.Equal(["1", "Anlage-1", "Anlage-2"], Shown(saved));
    }

    [Fact]
    public void Unlabelled_NothingWritten()
    {
        using var doc = PdfDocument.Open(Labelled(3));
        var pages = PageItem.From(doc);
        using var saved = Saved(doc, [pages[2], pages[0]]);
        Assert.Null(saved.Catalog!.Get(PdfName.Get("PageLabels")) as PdfDictionary);
    }

    /// <summary>Merging a labelled file says nothing about page numbering any more (it is carried over).</summary>
    [Fact]
    public void MergeNotes_NoLongerAboutPageLabels()
    {
        using var doc = PdfDocument.Open(Labelled(2, (0, "r", "", 1)));
        Assert.DoesNotContain(MergeNotes.For(doc, false, doc.Pages.ToList(), true, new HashSet<string>()), n => n.Contains("Seitennummerierung", StringComparison.Ordinal));
    }
}
