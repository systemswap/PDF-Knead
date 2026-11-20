using PdfKnead.Core.Imaging;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Images at the edge of the page (docs/FEHLERGEDAECHTNIS.md, No. 622; decided on 8.11.2026): the part beyond the visible area is cut
/// off for real — its pixels leave the image, the rectangle shrinks to what shows, the page looks as before; with a rotation of 90° the right
/// columns of the page are the bottom rows of the picture; with a locked aspect ratio the margin in the rectangle counts.
/// </summary>
public sealed class PageEdgeTests
{
    private static readonly PdfRectangle A4 = new(0, 0, 595, 842);

    /// <summary>An image whose every pixel tells its row and column (red = column, green = row).</summary>
    private static RgbaImage Numbered(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                (pixels[(y * width + x) * 4], pixels[(y * width + x) * 4 + 1], pixels[(y * width + x) * 4 + 3]) = ((byte)x, (byte)y, 255);
        return new RgbaImage(width, height, pixels);
    }

    private static (int Column, int Row) At(RgbaImage image, int x, int y) => (image.Pixels[(y * image.Width + x) * 4], image.Pixels[(y * image.Width + x) * 4 + 1]);

    [Fact]
    public void Overhangs_OnlyBeyondTheVisibleArea()
    {
        var inside = new ImageElement { Rect = new PdfRectangle(10, 10, 100, 100), Image = Numbered(2, 2) };
        Assert.False(PageEdge.Overhangs(inside, A4));
        Assert.False(PageEdge.Overhangs(inside with { Rect = new PdfRectangle(0, 0, 595.005, 842) }, A4));   // to a thousandth of a point
        Assert.True(PageEdge.Overhangs(inside with { Rect = new PdfRectangle(-5, 10, 100, 100) }, A4));
        Assert.True(PageEdge.Overhangs(inside with { Rect = new PdfRectangle(500, 800, 600, 850) }, A4));
        Assert.Null(PageEdge.CropToPage(inside, A4));
    }

    /// <summary>100 × 50 px on 100 × 50 pt, 5 pt beyond the right edge: 95 columns stay, the rectangle ends at the edge.</summary>
    [Fact]
    public void CropToPage_RightEdge_ColumnsBeyondItLeave()
    {
        var image = new ImageElement { Rect = new PdfRectangle(500, 700, 600, 750), Image = Numbered(100, 50) };
        var cut = PageEdge.CropToPage(image, A4)!;
        Assert.Equal((95, 50), (cut.Image.Width, cut.Image.Height));
        Assert.Equal(new PdfRectangle(500, 700, 595, 750), cut.Rect);
        Assert.Equal((0, 0), At(cut.Image, 0, 0));
        Assert.Equal((94, 49), At(cut.Image, 94, 49));
        Assert.False(PageEdge.Overhangs(cut, A4));
    }

    /// <summary>
    /// Turned by 90° (the picture's x upwards on the page, its y to the left): beyond the right edge of the page lie the bottom rows of the
    /// picture — they leave, the top rows stay.
    /// </summary>
    [Fact]
    public void CropToPage_Rotated90_TheBottomRowsLeave()
    {
        var narrow = A4 with { Right = 545 };
        var image = new ImageElement { Rect = new PdfRectangle(500, 700, 550, 800), Image = Numbered(100, 50), Rotation = 90 };
        Assert.Equal((100.0, 50.0), image.UprightSize);
        var cut = PageEdge.CropToPage(image, narrow)!;
        Assert.Equal((100, 45), (cut.Image.Width, cut.Image.Height));
        Assert.Equal((0, 0), At(cut.Image, 0, 0));    // the top row stays
        Assert.Equal((99, 44), At(cut.Image, 99, 44));
        Assert.Equal(new PdfRectangle(500, 700, 545, 800), cut.Rect);
    }

    /// <summary>
    /// A square picture in a rectangle twice as wide, aspect ratio locked: it sits in the middle (25 pt margins). The edge through its middle
    /// cuts it in half; the new rectangle is only the half that shows — no margin beyond the edge.
    /// </summary>
    [Fact]
    public void CropToPage_LockedAspect_TheMarginCounts()
    {
        var visible = new PdfRectangle(0, 0, 200, 842);
        var image = new ImageElement { Rect = new PdfRectangle(100, 100, 300, 200), Image = Numbered(100, 100) };   // shown 100 × 100 at 150…250
        var cut = PageEdge.CropToPage(image, visible)!;
        Assert.Equal((50, 100), (cut.Image.Width, cut.Image.Height));
        Assert.Equal(new PdfRectangle(150, 100, 200, 200), cut.Rect);
        Assert.Equal((0, 0), At(cut.Image, 0, 0));
        // with a line of origin or entirely beside the page: nothing to cut
        Assert.Null(PageEdge.CropToPage(image with { Rect = new PdfRectangle(700, 100, 900, 200) }, A4));
    }
}
