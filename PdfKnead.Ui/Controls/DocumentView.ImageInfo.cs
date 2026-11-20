using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using PdfKnead.ViewModels;

namespace PdfKnead.Controls;

/// <summary>
/// Image info while reading (KONZEPT.md, section 5.15): with „Text markieren“ or „Verschieben“, a click on an image — not on text, without
/// dragging — shows its details on the right; another click at the same spot shows the image underneath, a click beside it or Esc closes
/// them. In „Bearbeiten“ the details are part of the „Bild“ panel instead.
/// </summary>
public sealed partial class DocumentView
{
    private static readonly IBrush ImageInfoBrush = new SolidColorBrush(Color.FromArgb(0x1E, 0x3D, 0x7E, 0xFF));
    private static readonly IPen ImageInfoPen = new Pen(new SolidColorBrush(Color.FromRgb(0x3D, 0x7E, 0xFF)), 2);
    private static readonly IPen ImageInfoHalo = new Pen(Brushes.White, 4);

    private bool IsReadingTool => _doc?.Tool is ViewTool.SelectText or ViewTool.Pan;

    /// <summary>A click without dragging beside the text in a reading tool: the image there, or nothing (closes the details).</summary>
    private void ReadingClick(Point position)
    {
        if (!IsReadingTool || _doc is not { } doc || PageAt(position) is not { } page) return;
        var (x, y) = ToPage(page, position);
        _ = doc.ShowImageInfoAtAsync(page, x, y);
    }

    /// <summary>Esc while the details of an image are shown in a reading tool: closes them. true = handled.</summary>
    private bool ReadingImageInfoKey(KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !IsReadingTool || _doc is not { InspectedImage: not null } doc) return false;
        doc.EndImageInfo();
        InvalidateVisual();
        return true;
    }

    /// <summary>Draws the frame around the image whose details are shown (through its four corners, so rotated images are framed correctly).</summary>
    private void DrawImageInfo(DrawingContext context)
    {
        if (!IsReadingTool || _doc is not { InspectedImage: { } image } || image.Page >= _pageRects.Count || !_shown[image.Page]) return;
        var corners = image.Info.Corners;
        if (corners.Length != 4) return;
        var toView = PageToView(image.Page);
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
        context.DrawGeometry(null, ImageInfoHalo, geometry);
        context.DrawGeometry(ImageInfoBrush, ImageInfoPen, geometry);
    }
}
