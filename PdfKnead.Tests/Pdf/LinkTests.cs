using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The links of a page (ISO 32000-2, 12.5.6.5 and 12.6.4): areas, targets and the check of addresses — what the view follows and what it
/// only shows (docs/ABLAUFPLAN.md, 7.3 c; docs/FEHLERGEDAECHTNIS.md, No. 565).
/// </summary>
public sealed class LinkTests
{
    /// <summary>Two pages (objects 3 and 4); page 1 has the link annotation 10 with <paramref name="link"/>; <paramref name="catalog"/> goes into the catalogue.</summary>
    private static PdfDocument WithLink(string link, string catalog = "")
    {
        var objects = new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R" + catalog + ">>",
            [2] = "<</Type/Pages/Kids[3 0 R 4 0 R]/Count 2/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Annots[10 0 R]>>",
            [4] = "<</Type/Page/Parent 2 0 R>>",
            [10] = "<</Type/Annot/Subtype/Link/Rect[100 700 300 720]" + link + ">>",
        };
        return PdfDocument.Open(CraftedFileTests.Build(objects));
    }

    private static PageLink Link(PdfDocument doc) => Assert.Single(PageLinks.Of(doc, 0));

    [Fact]
    public void LinkWithGoTo_LeadsToThePageAndView()
    {
        using var doc = WithLink("/A<</S/GoTo/D[4 0 R/XYZ 72 500 null]>>");
        var target = Assert.IsType<InternalLink>(Link(doc).Target);
        Assert.Equal(new PdfDestination(1, DestinationKind.Xyz, 72, 500, null), target.Destination);
        Assert.True(Link(doc).Contains(150, 710));
        Assert.False(Link(doc).Contains(150, 650));
    }

    /// <summary>/A and /Dest together: the action counts.</summary>
    [Fact]
    public void ActionBeatsDest()
    {
        using var doc = WithLink("/Dest[3 0 R/Fit]/A<</S/GoTo/D[4 0 R/Fit]>>");
        Assert.Equal(1, Assert.IsType<InternalLink>(Link(doc).Target).Destination.PageIndex);
    }

    [Fact]
    public void UriWithBase_IsResolved()
    {
        using var doc = WithLink("/A<</S/URI/URI(kapitel/2.html)>>", "/URI<</Base(https://example.org/handbuch/)>>");
        var address = Assert.IsType<AddressLink>(Link(doc).Target);
        Assert.Equal("https://example.org/handbuch/kapitel/2.html", address.Uri);
        Assert.Equal("https://example.org/handbuch/kapitel/2.html", address.OpenTarget);
    }

    /// <summary>A relative address without /Base names a file beside the document: never opened, only shown.</summary>
    [Fact]
    public void RelativeUriWithoutBase_IsNeverOpened()
    {
        using var doc = WithLink("/A<</S/URI/URI(anlage.pdf)>>");
        var address = Assert.IsType<AddressLink>(Link(doc).Target);
        Assert.Null(address.OpenTarget);
        Assert.NotEmpty(address.Check.Warnings);
    }

    [Theory]
    [InlineData("file:///C:/Windows/system32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    public void FileOrScriptUri_IsNeverOpened(string uri)
    {
        using var doc = WithLink("/A<</S/URI/URI(" + uri + ")>>");
        Assert.Null(Assert.IsType<AddressLink>(Link(doc).Target).OpenTarget);
    }

    /// <summary>A domain with umlauts, stored as UTF-8 (PDF 2.0): read for display, the Punycode form warned about.</summary>
    [Fact]
    public void UriInUtf8_DecodedForDisplay()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("https://müller.example/");
        var hex = Convert.ToHexString(bytes);
        using var doc = WithLink("/A<</S/URI/URI<" + hex + ">>>");
        var address = Assert.IsType<AddressLink>(Link(doc).Target);
        Assert.Equal("https://müller.example/", address.Uri);
        Assert.Contains(address.Check.Warnings, w => w.Contains("xn--", StringComparison.Ordinal));
    }

    /// <summary>/QuadPoints outside /Rect: the rectangle is used (Table 176).</summary>
    [Fact]
    public void QuadPointsOutsideRect_RectUsed()
    {
        using var doc = WithLink("/QuadPoints[0 0 50 0 50 50 0 50]/A<</S/GoTo/D[4 0 R/Fit]>>");
        var link = Link(doc);
        Assert.True(link.Contains(200, 710));
        Assert.False(link.Contains(25, 25));
    }

    [Fact]
    public void QuadPointsInsideRect_Used()
    {
        using var doc = WithLink("/QuadPoints[110 705 150 705 150 715 110 715]/A<</S/GoTo/D[4 0 R/Fit]>>");
        var link = Link(doc);
        Assert.True(link.Contains(120, 710));
        Assert.False(link.Contains(250, 710));
    }

    [Fact]
    public void LaunchAction_IsOther()
    {
        using var doc = WithLink("/A<</S/Launch/F(programm.exe)>>");
        var other = Assert.IsType<OtherLink>(Link(doc).Target);
        Assert.Equal("Launch", other.Kind);
        Assert.Equal("programm.exe", other.Detail);
    }

    [Fact]
    public void NamedAction_NextPage()
    {
        using var doc = WithLink("/A<</S/Named/N/NextPage>>");
        Assert.Equal("NextPage", Assert.IsType<NamedLink>(Link(doc).Target).Action);
    }
}
