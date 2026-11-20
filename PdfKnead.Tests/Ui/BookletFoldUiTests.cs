using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using PdfKnead.Controls;
using SkiaSharp;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// The booklet shown as a book (print preview „Als Buch“, decided on 16.11.2026): when the book lies open, both pages darken softly
/// towards the fold, like paper bending into the gutter; the outer part of the pages stays white. The closed book (front cover alone) has no
/// shadow in the middle. Before: only a thin line marked the fold, and the two pages looked like one flat sheet.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class BookletFoldUiTests
{
    /// <summary>Four white A5 pages, ready at once.</summary>
    private sealed class WhitePages : IBookPages
    {
        private readonly SKImage _white;

        public WhitePages()
        {
            using var surface = SKSurface.Create(new SKImageInfo(148, 210));
            surface.Canvas.Clear(SKColors.White);
            _white = surface.Snapshot();
        }

        public int PageCount => 4;

        public (double Width, double Height) PageSize => (420, 595);

        public SKImage? Get(int page) => _white;

        public event Action? Changed { add { } remove { } }
    }

    private static int Brightness(WriteableBitmap frame, double x, double y)
    {
        using var buffer = frame.Lock();
        var bytes = new byte[buffer.RowBytes * buffer.Size.Height];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        var i = (int)y * buffer.RowBytes + (int)x * 4;
        return (bytes[i] + bytes[i + 1] + bytes[i + 2]) / 3;
    }

    [Fact]
    public void OpenBook_BothPagesDarkenTowardsTheFold_OuterPartsStayWhite()
    {
        UiSession.Run(() =>
        {
            var book = new BookletPreview { Pages = new WhitePages(), Spread = 1 };
            var window = new Window { Width = 1000, Height = 700, Content = book, Background = Avalonia.Media.Brushes.Gray };
            window.Show();
            try
            {
                var frame = UiSession.Capture(window);
                // the book: two pages of 420 × 595 pt, scaled into the control with a margin of 28
                var scale = Math.Min((1000 - 56) / 840.0, (700 - 56) / 595.0);
                var (w, center, middle) = (420 * scale, 500.0, 350.0);
                var reach = w * BookletPreview.FoldShadowReach;
                var leftNearFold = Brightness(frame, center - 3, middle);
                var rightNearFold = Brightness(frame, center + 3, middle);
                var leftHalfway = Brightness(frame, center - reach / 2, middle);
                var leftOuter = Brightness(frame, center - w / 2, middle);
                var rightOuter = Brightness(frame, center + w / 2, middle);

                Assert.True(leftOuter >= 250 && rightOuter >= 250, $"outer parts {leftOuter}, {rightOuter}");
                Assert.True(leftNearFold < 240 && rightNearFold < 240, $"at the fold {leftNearFold}, {rightNearFold}");
                Assert.True(leftNearFold < leftHalfway && leftHalfway < leftOuter, $"falling off {leftNearFold} < {leftHalfway} < {leftOuter}");
                Assert.True(Math.Abs(leftNearFold - rightNearFold) <= 6, $"both sides alike {leftNearFold}, {rightNearFold}");

                // closed: the front cover alone, lying centred, white across its middle
                window.Content = new BookletPreview { Pages = new WhitePages(), Spread = 0 };
                frame = UiSession.Capture(window);
                Assert.True(Brightness(frame, center, middle) >= 250, "no fold on the closed book");
            }
            finally
            {
                window.Close();
            }
        });
    }
}
