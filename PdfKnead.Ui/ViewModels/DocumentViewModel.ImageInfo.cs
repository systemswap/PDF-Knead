using CommunityToolkit.Mvvm.ComponentModel;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf;
using PdfKnead.Rendering;

namespace PdfKnead.ViewModels;

/// <summary>The image selected with the image info tool: page, details (for the frame) and the translated sections; <see cref="Number"/> and <see cref="Of"/> give „Bild 2 von 3“.
/// <see cref="Source"/> (an image of the page) or <see cref="Element"/> (inserted in PDF Knead) is what „Bild kopieren“ and „Bild speichern“ take.</summary>
public sealed record InspectedImage(int Page, ImageInfo Info, IReadOnlyList<ImageInfoSection> Sections, int Number, int Of, ImageSource? Source = null,
    ImageElement? Element = null);

/// <summary>Details of an image of the page, shown in the „Bild“ panel of „Bearbeiten“ (KONZEPT.md, sections 5.15 and 5.22).
/// Only images count; text, shapes and annotations on top do not get in the way.</summary>
public sealed partial class DocumentViewModel
{
    /// <summary>The selected image; null if none is selected or there is none at the clicked spot (<see cref="ImageInfoMissed"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImageInfoHint), nameof(ImageInfoPosition), nameof(IsReadingImageInfo))]
    public partial InspectedImage? InspectedImage { get; set; }

    /// <summary>Whether the last click hit no image.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImageInfoHint))]
    public partial bool ImageInfoMissed { get; set; }

    /// <summary>Whether the image hit could not be analysed (damaged file).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImageInfoHint))]
    public partial bool ImageInfoUnreadable { get; set; }

    /// <summary>Hint in the „Bild“ section while no image is selected; empty when one is selected.</summary>
    public string ImageInfoHint => InspectedImage is not null ? ""
        : ImageInfoUnreadable ? Tr.T("Dieses Bild lässt sich nicht auswerten — die Datei ist an dieser Stelle beschädigt.")
        : ImageInfoMissed ? Tr.T("An dieser Stelle liegt kein Bild.")
        : Tr.T("Auf ein Bild in der Seite klicken.");

    /// <summary>With several images at the spot: which one is shown; otherwise empty.</summary>
    public string ImageInfoPosition => InspectedImage is not { Of: > 1 } i ? ""
        : Tool == ViewTool.Edit ? Tr.T("Bild {0} von {1} an dieser Stelle — Alt+Klick wählt das nächste darunter", i.Number, i.Of)
        : Tr.T("Bild {0} von {1} an dieser Stelle — noch einmal klicken für das nächste darunter", i.Number, i.Of);

    /// <summary>
    /// Whether the details of an image are shown on their own on the right: while reading („Text markieren“, „Verschieben“), after a click on
    /// an image (KONZEPT.md, section 5.15). In „Bearbeiten“ they are part of the „Bild“ panel.
    /// </summary>
    public bool IsReadingImageInfo => Tool is ViewTool.SelectText or ViewTool.Pan && InspectedImage is not null && SelectedElement is null;

    /// <summary>
    /// A click on the page while reading: shows the details of the image at the spot (another click at the same spot: the one underneath);
    /// without an image there, the details close. The image info takes the place of other panels on the right (cleanup, notes, proposals).
    /// </summary>
    /// <param name="page">Page index.</param>
    /// <param name="x">X in page space.</param>
    /// <param name="y">Y in page space.</param>
    public async Task ShowImageInfoAtAsync(int page, double x, double y)
    {
        await InspectImageAsync(page, x, y);
        if (InspectedImage is null)
        {
            EndImageInfo();
            return;
        }
        if (IsCleanupOpen) CloseCleanup();
        if (IsFieldProposalsOpen) CloseFieldProposals();
        if (IsNotesOpen) CloseNotes();
        if (IsAccessibilityOpen) CloseAccessibility();
    }

    // Last click: page, position and how many images were there; the same click again moves on to the next one
    private (int Page, double X, double Y, int Count, int Index)? _lastImageClick;
    private int _imageInfoStamp;

    /// <summary>Reads the details of the image at a spot of the page (for the „Bild“ panel of „Bearbeiten“).</summary>
    /// <param name="page">Page index.</param>
    /// <param name="x">X coordinate in page space.</param>
    /// <param name="y">Y coordinate in page space.</param>
    /// <param name="source">The image chosen there, if known: its details are shown rather than the topmost image's; null for the topmost or the next one at a repeated spot.</param>
    public async Task InspectImageAsync(int page, double x, double y, ImageSource? source = null)
    {
        if (page < 0 || page >= Pages.Count) return;
        var stamp = ++_imageInfoStamp;
        var item = Pages[page].Item;
        ImageInfo? info;
        ImageElement? element = null;
        int index, count;
        try
        {
            // The images inserted in PDF-Knead (elements) lie on top, the page's own images underneath
            var elements = ElementsOn(page).OfType<ImageElement>().Where(e => Contains(e, x, y)).Reverse().ToList();
            var list = Renderer.TryGetDisplayList(item.Id) ?? await Renderer.GetDisplayList(item.Id);
            if (stamp != _imageInfoStamp) return;
            var content = list is null ? [] : ImageInspector.ImagesAt(list, x, y);
            count = elements.Count + content.Count;
            if (count == 0)
            {
                _lastImageClick = null;
                InspectedImage = null;
                ImageInfoUnreadable = false;
                ImageInfoMissed = true;
                return;
            }

            // The image chosen; otherwise the same click (within a few points) with the same images gives the next one underneath
            var chosen = source is null ? -1 : content.FindIndex(c => Equals(list!.Images[list.Items.Span[c].Image], source));
            index = chosen >= 0 ? elements.Count + chosen
                : _lastImageClick is { } last && last.Page == page && Math.Abs(last.X - x) < 3 && Math.Abs(last.Y - y) < 3 && last.Count == count
                ? (last.Index + 1) % count
                : 0;
            _lastImageClick = (page, x, y, count, index);
            var at = index;
            if (at < elements.Count) element = elements[at];
            else source = list!.Images[list.Items.Span[content[at - elements.Count]].Image];
            info = await Task.Run(() =>
            {
                if (at < elements.Count) return ImageInspector.Inspect(elements[at], item.Page);
                using var use = Renderer.Use(item.Source);
                return use is null ? null : ImageInspector.Inspect(item.Source, item.Page, list!, content[at - elements.Count]);
            });
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A damaged file must not abort the image info; it is then shown without details
            AppLog.Write("Bildinfo", e.ToString());
            info = null;
            index = 0;
            count = 1;
        }
        if (stamp != _imageInfoStamp) return;
        ImageInfoMissed = false;
        ImageInfoUnreadable = info is null;
        InspectedImage = info is null ? null : new InspectedImage(page, info, ImageInspector.Sections(info), index + 1, count, source, element);
    }

    /// <summary>
    /// Why the selected image must not leave the document — null if it may: the file forbids extracting content (only for images
    /// of the page; those inserted in PDF Knead belong to the user), or a bar that has not been applied yet lies over it.
    /// </summary>
    public string? ImageExportBlocked()
    {
        if (InspectedImage is not { } image) return Tr.T("Kein Bild ausgewählt.");
        var corners = image.Info.Corners;
        RectD? box = corners.Length == 0 ? null
            : RectD.FromLtrb(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Max(c => c.X), corners.Max(c => c.Y));
        return ExportBlocked(image.Page, box, image.Element is not null);
    }

    /// <summary>
    /// Why an image of the page may not be copied or saved: the file forbids taking content out (not for an image inserted in PDF Knead),
    /// or a redaction not yet applied lies over it.
    /// </summary>
    /// <param name="page">Page index.</param>
    /// <param name="box">Where the image lies (page space, y upwards); null if unknown.</param>
    /// <param name="ownElement">An image inserted in PDF Knead.</param>
    /// <returns>The translated reason; null if allowed.</returns>
    private string? ExportBlocked(int page, RectD? box, bool ownElement)
    {
        if (!ownElement && !CopyAllowed)
            return Tr.T("Dieses Dokument erlaubt kein Herauslösen von Inhalten als Bild (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen.");
        if (box is not { } b) return null;
        // the marks of hidden content take away only invisible characters, never image pixels
        foreach (var bar in ElementsOn(page).OfType<RedactionElement>().Where(r => !r.Hidden))
            if (bar.Rect.Left < b.Right && bar.Rect.Right > b.Left && bar.Rect.Bottom < b.Bottom && bar.Rect.Top > b.Top)
                return Tr.T("Über diesem Bild liegt eine Schwärzung, die noch nicht angewandt ist — erst „Schwärzungen anwenden“, dann ist das Bild geschwärzt.");
        return null;
    }

    /// <summary>What saving the selected image will write (format, extension, mask beside), without decoding it.</summary>
    /// <returns>The plan; null if no image is selected or the document was closed.</returns>
    /// <exception cref="PdfException">The image cannot be saved.</exception>
    public Task<ImageExportPlan?> PlanInspectedImageAsync()
    {
        if (InspectedImage is not { } image) return Task.FromResult<ImageExportPlan?>(null);
        if (image.Element is not null) return Task.FromResult<ImageExportPlan?>(new ImageExportPlan(ImageExportFormat.Png, ".png", false, false, false));
        var item = Pages[image.Page].Item;
        return Task.Run(() =>
        {
            using var use = Renderer.Use(item.Source);
            return use is null || image.Source is null ? null : ImageExport.Plan(item.Source, image.Source);
        });
    }

    /// <summary>
    /// Saves the selected image in its original quality (see <see cref="ImageExport"/>), row by row straight into the file, in the background.
    /// Written first to a temporary file beside it, which then takes its place — an export that fails leaves no half file.
    /// </summary>
    /// <param name="path">The image file.</param>
    /// <param name="maskPath">The file for an opacity that does not fit into the image file.</param>
    /// <returns>What was written, and whether the mask file was written; null if the document was closed.</returns>
    /// <exception cref="PdfException">The image data cannot be read.</exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    public Task<(ImageExportPlan Plan, bool Mask)?> SaveInspectedImageAsync(string path, string maskPath)
    {
        if (InspectedImage is not { } image) return Task.FromResult<(ImageExportPlan, bool)?>(null);
        var item = Pages[image.Page].Item;
        return Task.Run(() => WriteImageFile(path, maskPath, (output, openMask) =>
        {
            if (image.Element is { } element)
            {
                var exported = ImageExport.Export(element.Image);
                output.Write(exported.Data);
                return new ImageExportPlan(exported.Format, exported.Extension, false, false, false);
            }
            using var use = Renderer.Use(item.Source);
            if (use is null || image.Source is null) return null;
            // the drawn size gives the resolution of a file that notes one (TIFF, FG 1019)
            return ImageExport.Write(item.Source, image.Source, output, openMask, (image.Info.WidthPoints, image.Info.HeightPoints));
        }));
    }

    /// <summary>
    /// Writes an image file through a temporary file beside it, which then takes its place — an export that fails leaves no half file.
    /// The mask file is created only if <paramref name="write"/> asks for it.
    /// </summary>
    /// <param name="path">The image file.</param>
    /// <param name="maskPath">The file for an opacity that does not fit into the image file.</param>
    /// <param name="write">Writes the image into the stream given; null = nothing to write (document closed).</param>
    /// <returns>What was written and whether the mask file was written; null if nothing was written.</returns>
    private static (ImageExportPlan Plan, bool Mask)? WriteImageFile(string path, string maskPath, Func<Stream, Func<Stream>, ImageExportPlan?> write)
    {
        var temporary = path + ".pdfknead-tmp";
        var maskTemporary = maskPath + ".pdfknead-tmp";
        try
        {
            ImageExportPlan? plan;
            FileStream? mask = null;
            using (var output = File.Create(temporary))
            {
                try
                {
                    plan = write(output, () => mask = File.Create(maskTemporary));
                }
                finally
                {
                    mask?.Dispose();
                }
            }
            if (plan is null) return null;
            File.Move(temporary, path, overwrite: true);
            if (mask is not null) File.Move(maskTemporary, maskPath, overwrite: false);
            return (plan, mask is not null);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(maskTemporary)) File.Delete(maskTemporary);
        }
    }

    /// <summary>
    /// The selected image as it is drawn (colours on the screen, mask applied), RGBA without premultiplied alpha — for the clipboard,
    /// which only knows such images; very large images arrive reduced to 16 megapixels.
    /// </summary>
    /// <returns>Width, height and pixels; null if the image cannot be read.</returns>
    public Task<(int Width, int Height, byte[] Rgba)?> InspectedImagePixelsAsync()
    {
        if (InspectedImage is not { } image) return Task.FromResult<(int, int, byte[])?>(null);
        if (image.Element is { } element) return Task.FromResult<(int, int, byte[])?>((element.Image.Width, element.Image.Height, element.Image.Pixels));
        var item = Pages[image.Page].Item;
        return Task.Run<(int, int, byte[])?>(() =>
        {
            using var use = Renderer.Use(item.Source);
            if (use is null || image.Source is null) return null;
            // stencil masks in black, as „Bild speichern“ writes them
            var source = image.Source with { StencilColor = 0xFF000000 };
            return PdfImageDecoder.Decode(source, item.Source) is { } decoded ? (decoded.Width, decoded.Height, decoded.Rgba) : null;
        });
    }

    /// <summary>„x“ in the head of the image details while reading (Esc does the same).</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void CloseImageInfo() => EndImageInfo();

    /// <summary>Clears the image whose details are shown and its frame.</summary>
    public void EndImageInfo()
    {
        _imageInfoStamp++;
        _lastImageClick = null;
        InspectedImage = null;
        ImageInfoMissed = false;
        ImageInfoUnreadable = false;
    }

    /// <summary>Whether the point lies within the element's (rotated) rectangle.</summary>
    private static bool Contains(ImageElement element, double x, double y)
    {
        var size = element.UprightSize;
        var toPage = Matrix.Scale(size.Width, size.Height) * ElementAppearance.For(element).UprightToPage();
        if (!toPage.IsInvertible) return false;
        var (u, v) = toPage.Invert().Transform(x, y);
        return u is >= 0 and <= 1 && v is >= 0 and <= 1;
    }
}
