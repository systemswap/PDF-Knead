using Avalonia;
using Avalonia.Media;
using PdfKnead.ViewModels;

namespace PdfKnead.Controls;

/// <summary>The frame of the code chosen in „Bearbeiten“ (KONZEPT.md, sections 5.19 and 5.22).</summary>
public sealed partial class DocumentView
{
    private static readonly IBrush CodeFrameBrush = new SolidColorBrush(Color.FromArgb(0x1E, 0x3D, 0x7E, 0xFF));
    private static readonly IPen CodeFramePen = new Pen(new SolidColorBrush(Color.FromRgb(0x3D, 0x7E, 0xFF)), 2);
    private static readonly IPen CodeFrameHalo = new Pen(Brushes.White, 4);

    /// <summary>Draws the frame around the code that was read (through its four corners, so turned codes are framed correctly).</summary>
    private void DrawBarcode(DrawingContext context)
    {
        if (_doc is not { Tool: ViewTool.Edit, InspectedBarcode: { } code } || code.Page >= _pageRects.Count || !_shown[code.Page]) return;
        var corners = code.Code.Corners;
        var toView = PageToView(code.Page);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            for (var i = 0; i < 4; i++)
            {
                var (vx, vy) = toView.Transform(corners[i].X, corners[i].Y);
                if (i == 0) g.BeginFigure(new Point(vx, vy), true);
                else g.LineTo(new Point(vx, vy));
            }
            g.EndFigure(true);
        }
        context.DrawGeometry(null, CodeFrameHalo, geometry);
        context.DrawGeometry(CodeFrameBrush, CodeFramePen, geometry);
    }
}
