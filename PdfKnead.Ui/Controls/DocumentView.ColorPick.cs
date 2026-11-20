using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace PdfKnead.Controls;

/// <summary>
/// Eyedropper: on the page, a circle next to the mouse pointer shows the colour underneath, and a click takes it
/// (right-click and Esc cancel). What is picked up is what is visible: the page with its elements, without the frames, handles and
/// tints of the view; for this, the view is rendered at that spot into a one-pixel image.
/// </summary>
public sealed partial class DocumentView
{
    private const double SwatchRadius = 11;

    private Point? _pickAt;
    private int? _pickColor;
    private bool _sampling;

    private static Cursor? _pipetteCursor;

    private bool ColorPickPressed(PointerPressedEventArgs e, Point position)
    {
        if (_doc is not { IsPickingColor: true } doc) return false;
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed)
        {
            doc.EndColorPick(null);
            return true;
        }
        if (!props.IsLeftButtonPressed) return false;   // middle button: pan the view as usual
        if (PageAt(position) is not null) doc.EndColorPick(SampleColor(position));
        return true;
    }

    /// <summary>Mouse moved with the eyedropper on: colour under the pointer for the circle. false = eyedropper not active.</summary>
    private bool ColorPickMoved(Point position)
    {
        if (_doc is not { IsPickingColor: true }) return false;
        var onPage = PageAt(position) is not null;
        Cursor = onPage ? PipetteCursor() : Cursor.Default;
        _pickAt = onPage ? position : null;
        _pickColor = onPage ? SampleColor(position) : null;
        InvalidateVisual();
        return true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_pickAt is null) return;
        _pickAt = null;
        InvalidateVisual();
    }

    /// <summary>Eyedropper switched on or off (button, click, Esc): removes the circle and restores the usual pointer.</summary>
    private void ColorPickChanged()
    {
        _pickAt = null;
        _pickColor = null;
        if (_doc is not { IsPickingColor: true }) Cursor = HandCursor;
        InvalidateVisual();
    }

    private void DrawColorPick(DrawingContext context)
    {
        if (_sampling || _pickAt is not { } at || _pickColor is not { } rgb) return;
        var center = new Point(at.X + 20, at.Y + 20);
        var fill = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)), 1), center, SwatchRadius + 2.5, SwatchRadius + 2.5);
        context.DrawEllipse(fill, new Pen(Brushes.White, 3), center, SwatchRadius, SwatchRadius);
    }

    /// <summary>Colour visible at the given spot (0xRRGGBB).</summary>
    internal int? SampleColor(Point position)
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        using var bitmap = new RenderTargetBitmap(new PixelSize(1, 1), new Vector(96 * scaling, 96 * scaling));
        _sampling = true;
        try
        {
            using var context = bitmap.CreateDrawingContext();
            context.FillRectangle(Brushes.White, new Rect(0, 0, 1, 1));   // page background in case nothing covers it
            using (context.PushTransform(Matrix.CreateTranslation(-position.X, -position.Y)))
                Render(context);
        }
        finally
        {
            _sampling = false;
        }
        var pixel = new byte[4];
        var handle = GCHandle.Alloc(pixel, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, 1, 1), handle.AddrOfPinnedObject(), 4, 4);
        }
        finally
        {
            handle.Free();
        }
        // RGBA or BGRA depending on the system; opaque (white background underneath), so no premultiplication needs to be undone.
        var (r, g, b) = bitmap.Format == PixelFormat.Rgba8888 ? (pixel[0], pixel[1], pixel[2]) : (pixel[2], pixel[1], pixel[0]);
        return (r << 16) | (g << 8) | b;
    }

    /// <summary>Eyedropper mouse pointer: the icon in black with a white outline (visible on any background); the tip at the bottom left is the hot spot.</summary>
    private Cursor PipetteCursor()
    {
        if (_pipetteCursor is not null) return _pipetteCursor;
        try
        {
            if (Application.Current?.TryGetResource("Icon.Eyedropper", null, out var data) != true || data is not Geometry icon) return CrossCursor;
            const int size = 32;
            using var bitmap = new RenderTargetBitmap(new PixelSize(size, size));
            using (var context = bitmap.CreateDrawingContext())
            using (context.PushTransform(Matrix.CreateScale(1.4, 1.4)))
            {
                context.DrawGeometry(Brushes.White, new Pen(Brushes.White, 2.2, lineJoin: PenLineJoin.Round), icon);
                context.DrawGeometry(Brushes.Black, null, icon);
            }
            // Tip of the icon at (2.2 | 17.8) in the 20-unit grid
            _pipetteCursor = new Cursor(bitmap, new PixelPoint((int)Math.Round(2.2 * 1.4), (int)Math.Round(17.8 * 1.4)));
        }
        catch (Exception)
        {
            _pipetteCursor = CrossCursor;   // custom pointer not possible (platform): crosshair
        }
        return _pipetteCursor;
    }
}
