using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfMatrix = PdfKnead.Core.Pdf.Graphics.Matrix;

namespace PdfKnead.Controls;

/// <summary>
/// Draws an element without a page, exactly as it will later appear on the page (choice of the
/// handwriting font for the signature). The element is fitted into the control.
/// </summary>
public sealed class ElementPreview : Control
{
    public static readonly StyledProperty<PageElement?> ElementProperty =
        AvaloniaProperty.Register<ElementPreview, PageElement?>(nameof(Element));

    /// <summary>Large blank page serving as the space for the display list (the elements lie within it).</summary>
    private static readonly PdfPage Space = BlankPage.Create(2000, 2000).Page;

    /// <summary>
    /// Separate, small image cache (16 MB): while typing, every intermediate state produces a ballpoint-pen image, which should
    /// neither evict the page images nor remain after the window closes (<see cref="ReleaseImages"/>).
    /// </summary>
    private static readonly Rendering.ImageCache Images = new(16L << 20);

    /// <summary>Releases the decoded preview images and pen textures (call when the window has closed).</summary>
    public static void ReleaseImages()
    {
        Images.Clear();
        InkTextures.Release();
    }

    static ElementPreview() => AffectsRender<ElementPreview>(ElementProperty);

    public PageElement? Element
    {
        get => GetValue(ElementProperty);
        set => SetValue(ElementProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        InkTextures.Improved += OnInkImproved;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        InkTextures.Improved -= OnInkImproved;
    }

    // Ballpoint-pen texture finished (on a background thread): redraw on the UI thread.
    private void OnInkImproved() => Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);

    public override void Render(DrawingContext context)
    {
        if (Element is not { } element || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var r = element.Rect;
        if (r.Width <= 0 || r.Height <= 0) return;
        // Page space (y upwards) to control space, fitted and centred.
        var scale = Math.Min(Bounds.Width / r.Width, Bounds.Height / r.Height);
        var dx = (Bounds.Width - r.Width * scale) / 2;
        var dy = (Bounds.Height - r.Height * scale) / 2;
        var matrix = PdfMatrix.Translate(-r.Left, -r.Top) * PdfMatrix.Scale(scale, -scale) * PdfMatrix.Translate(dx, dy);
        context.Custom(new ElementDrawing(new Rect(Bounds.Size), [new ElementLayer(Space, [element], matrix)], Images));
    }
}
