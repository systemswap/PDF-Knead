using System.Diagnostics;
using System.Runtime.CompilerServices;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using SkiaSharp;
using Matrix = PdfKnead.Core.Pdf.Graphics.Matrix;

namespace PdfKnead.Rendering;

/// <summary>
/// Draws placed elements — over the tiles, not inside them: changing, moving and editing then cost only the element, never the
/// (possibly huge) page underneath. One small display list per element, built on first drawing. Thread-safe.
/// </summary>
public static class ElementOverlay
{
    private sealed class Entry(DisplayList? list)
    {
        public DisplayList? List { get; } = list;
    }

    // One list per form XObject (ElementAppearance.Shape): moved elements share it
    private static readonly ConditionalWeakTable<ElementAppearance, Entry> Lists = new();
    // Only the fallback for callers without their own image cache (command line, tests): the viewer, thumbnails, export and
    // compression pass the cache of their document — otherwise this static cache would keep images of closed documents
    private static readonly ImageCache Images = new(CachePool.Images);

    /// <summary>
    /// Display list of an element in the space of its page — at the position of the element for which the form was first built
    /// (<see cref="ElementAppearance.Shape"/>); <see cref="Offset"/> moves it to where the element is now. Cached per appearance.
    /// </summary>
    /// <param name="page">The page the element belongs to.</param>
    /// <param name="element">The element.</param>
    /// <returns>The display list, or null if the element cannot be drawn.</returns>
    public static DisplayList? List(PdfPage page, PageElement element) =>
        Lists.GetValue(ElementAppearance.For(element).Shape, shape => new Entry(TryBuild(shape, page))).List;

    /// <summary>Builds the display list of an appearance; never throws for unreadable or crafted data.</summary>
    /// <param name="shape">The appearance (form XObject).</param>
    /// <param name="page">The page the appearance belongs to.</param>
    /// <param name="cancel">Cancels building.</param>
    /// <returns>The display list, or null if the appearance cannot be read.</returns>
    /// <exception cref="OperationCanceledException">Building was cancelled.</exception>
    public static DisplayList? TryBuild(ElementAppearance shape, PdfPage page, CancellationToken cancel = default)
    {
        try
        {
            return PageInterpreter.BuildAnnotations(shape.Source, page, [shape.Annotation], cancel, PageInterpreter.MaxAnnotationOperations);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex is not (PdfException or NotSupportedException)) DocumentRenderer.Broken("Element nicht gezeichnet", ex);
            return null;
        }
    }

    /// <summary>Translation from the list (<see cref="List"/>) to the element's current position.</summary>
    /// <param name="element">The element.</param>
    /// <returns>The translation matrix in page space.</returns>
    public static Matrix Offset(PageElement element)
    {
        var from = ElementAppearance.For(element).Shape.Element.Rect;
        return Matrix.Translate(element.Rect.Left - from.Left, element.Rect.Bottom - from.Bottom);
    }

    /// <summary>Draws elements onto a canvas of the UI.</summary>
    /// <param name="canvas">Target canvas.</param>
    /// <param name="page">The page the elements belong to.</param>
    /// <param name="elements">Elements in drawing order.</param>
    /// <param name="pageToCanvas">Matrix from page space to canvas coordinates.</param>
    /// <param name="images">Own image cache (previews in the signature window); otherwise the shared fallback of the elements.</param>
    /// <param name="foreignLists">
    /// Display lists of foreign annotations built in the background (<see cref="DocumentRenderer.TryGetElementList"/>; null while
    /// not ready) — their appearance comes from the file and can be arbitrarily expensive, and the display must not wait for it.
    /// Without it, they are built here (export, tests).
    /// </param>
    /// <param name="until">Point in time (<see cref="Stopwatch.GetTimestamp"/>) after which no further elements are drawn — the
    /// viewer passes one frame (<see cref="FrameTime"/>); the first element is always drawn.</param>
    /// <param name="overprintPreview">Simulate overprinting.</param>
    public static void Draw(SKCanvas canvas, PdfPage page, IEnumerable<PageElement> elements, Matrix pageToCanvas, ImageCache? images = null,
        Func<PdfPage, ElementAppearance, DisplayList?>? foreignLists = null, long until = long.MaxValue, bool overprintPreview = false)
    {
        var first = true;
        foreach (var element in elements)
        {
            if (!first && Stopwatch.GetTimestamp() > until) return;
            // Appearances read from the file come from the background; generated ones (own elements, filled fields) are built right here
            DisplayList? list;
            ElementAppearance appearance;
            try
            {
                appearance = ElementAppearance.For(element);
                list = element is ForeignAnnotation && foreignLists is not null && !ReferenceEquals(appearance.Shape.Source, ElementAppearance.Document)
                    ? foreignLists(page, appearance.Shape)
                    : List(page, element);
            }
            catch (Exception ex)
            {
                // Crafted field (values from the file): this element is missing, the display carries on (render thread)
                if (element is ForeignAnnotation) DocumentRenderer.Broken("Element nicht gezeichnet", ex);
                else throw;
                continue;
            }
            if (list is null) continue;
            first = false;
            SkiaRenderer.RenderOnCanvas(canvas, list, appearance.Source, Offset(element) * pageToCanvas, images ?? Images, overprintPreview);
        }
    }

    /// <summary>
    /// Time limit per frame of the display. Normal pages need 0.1–1.5 ms; crafted files with many expensive annotations would
    /// otherwise freeze the UI (found by fuzzing: 1 000 annotations with one inflated image, 6 s per frame). Measured in time, not in
    /// entries: a list with one entry but thousands of clips cost 3–7 ms.
    /// </summary>
    public static readonly TimeSpan FrameTime = TimeSpan.FromMilliseconds(200);

    /// <summary>Draws elements into a finished image of the page (thumbnails, export); the canvas matrix is ignored.</summary>
    /// <param name="canvas">Canvas of the page image, origin at its top left corner.</param>
    /// <param name="page">The page.</param>
    /// <param name="elements">Elements in drawing order.</param>
    /// <param name="pixelsPerPoint">Resolution of the page image (1 = 72 dpi).</param>
    /// <param name="until">Point in time (<see cref="Stopwatch.GetTimestamp"/>) after which no further elements are drawn.</param>
    /// <param name="images">Own image cache, or null for the shared fallback.</param>
    public static void Draw(SKCanvas canvas, PdfPage page, IEnumerable<PageElement> elements, double pixelsPerPoint, long until = long.MaxValue,
        ImageCache? images = null)
    {
        canvas.Save();
        canvas.ResetMatrix();
        Draw(canvas, page, elements, PageGeometry.PageToDevice(page, pixelsPerPoint), images, until: until);
        canvas.Restore();
    }
}
