using System.Runtime.CompilerServices;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// Inserted images: aspect lock (proportional or stretched), opacity, sharpening. The display shows the original marked with
/// <see cref="DisplayKey"/>; the viewer downsamples such images with Lanczos and sharpens them as they will be saved (images in
/// the page content are not treated this way). Saving writes a separate image without the marker: downsampled with Lanczos to
/// <see cref="SavedDpi"/> at the final size and then sharpened — or the original's pixels if neither is necessary.
/// </summary>
/// <remarks>See KONZEPT.md, section 5.13.</remarks>
public sealed partial class ElementAppearance
{
    /// <summary>Resolution in dpi to which inserted images are downsampled on saving (print quality).</summary>
    public const double SavedDpi = 300;

    /// <summary>
    /// Marker in the display image of an inserted image; its value is the sharpening strength (0 = off). Never written to a file.
    /// </summary>
    public static readonly PdfName DisplayKey = PdfName.Get("PDFK_Anzeige");

    /// <summary>Sharpening: strength with the slider at maximum, and radius (sigma) in pixels at <see cref="SavedDpi"/>.</summary>
    public const double SharpenAmount = 1.5, SharpenSigma = 1.0;

    private static readonly ConditionalWeakTable<Imaging.RgbaImage, Dictionary<double, PdfStream>> DisplayStreams = new();
    private static readonly ConditionalWeakTable<PdfStream, Dictionary<double, PdfDictionary>> OpacityResources = new();
    private static readonly ConditionalWeakTable<PageElement, ElementAppearance> SavedPictures = new();

    private static ElementAppearance BuildImage(ImageElement image) =>
        BuildPicture(image, DisplayStream(image), image.Image.Width, image.Image.Height, centered: true, stretch: !image.KeepAspect, opacity: image.Opacity);

    /// <summary>Display image: the original's pixels with <see cref="DisplayKey"/> (a separate stream per sharpening strength).</summary>
    private static PdfStream DisplayStream(ImageElement image)
    {
        var original = ImageStream(image.Image);
        var amount = Math.Round(Math.Clamp(image.Sharpness, 0, 1), 2);
        var streams = DisplayStreams.GetValue(image.Image, _ => []);
        lock (streams)
        {
            if (streams.TryGetValue(amount, out var stream)) return stream;
            var dict = new PdfDictionary();
            foreach (var (key, value) in original.Dictionary.Entries) dict.Set(key, value);
            dict.Set(DisplayKey, new PdfReal(amount));
            var shown = streams[amount] = new PdfStream(dict, original.RawData!);
            OwnDisplayStreams.AddOrUpdate(shown, shown);
            DisplayPixels.AddOrUpdate(shown, image.Image);
            return shown;
        }
    }

    // Only display images built here: the same marker in a PDF from elsewhere must not start Lanczos versions
    private static readonly ConditionalWeakTable<PdfStream, PdfStream> OwnDisplayStreams = new();

    // The pixels a display image was made of: the viewer's Lanczos versions read them instead of a copy of the decoded image
    private static readonly ConditionalWeakTable<PdfStream, Imaging.RgbaImage> DisplayPixels = new();

    /// <summary>
    /// The pixels of a display image built by this class (the inserted picture itself), so that its Lanczos versions need no copy of the
    /// decoded image (48 MB for a photo of 12 megapixels, at every step of a drag; docs/FEHLERGEDAECHTNIS.md, No. 672); null for any other
    /// stream.
    /// </summary>
    /// <param name="stream">An image stream; may be <see langword="null"/>.</param>
    public static Imaging.RgbaImage? DisplayImage(PdfStream? stream) => stream is not null && DisplayPixels.TryGetValue(stream, out var image) ? image : null;

    /// <summary>Gets the sharpening strength of a display image of an inserted image.</summary>
    /// <param name="stream">An image stream; may be <see langword="null"/>.</param>
    /// <returns>
    /// The value of <see cref="DisplayKey"/> (0 … 1) if <paramref name="stream"/> is a display image built by this class;
    /// otherwise <see langword="null"/>.
    /// </returns>
    public static double? DisplaySharpness(PdfStream? stream) =>
        stream is not null && OwnDisplayStreams.TryGetValue(stream, out _) ? stream.Dictionary.GetNumber(DisplayKey) : null;

    /// <summary>Gets the size of the image on the page in reading orientation.</summary>
    /// <param name="image">The image element.</param>
    /// <returns>Width and height in points: fitted into the rectangle when the aspect ratio is locked, otherwise the whole rectangle.</returns>
    public static (double Width, double Height) ShownSize(ImageElement image)
    {
        var (w, h) = image.UprightSize;
        if (!image.KeepAspect) return (w, h);
        var s = Math.Min(w / image.Image.Width, h / image.Image.Height);
        return (image.Image.Width * s, image.Image.Height * s);
    }

    /// <summary>Gets the pixel size in which the image is saved.</summary>
    /// <param name="image">The image element.</param>
    /// <returns>
    /// The size the image has on the page at <see cref="SavedDpi"/>, at least 1 × 1 and never larger than the original in either
    /// direction; the original size if neither direction would shrink.
    /// </returns>
    public static (int Width, int Height) SavedPixels(ImageElement image)
    {
        var (iw, ih) = ShownSize(image);
        var tw = (int)Math.Ceiling(iw / 72 * SavedDpi);
        var th = (int)Math.Ceiling(ih / 72 * SavedDpi);
        if (tw >= image.Image.Width && th >= image.Image.Height) return (image.Image.Width, image.Image.Height);
        return (Math.Clamp(tw, 1, image.Image.Width), Math.Clamp(th, 1, image.Image.Height));
    }

    /// <summary>Gets the image stream that is saved (without the display marker).</summary>
    /// <param name="image">The image element.</param>
    /// <returns>The downsampled and sharpened image, or the original's pixels if neither is necessary; computed once per element.</returns>
    public static PdfStream SavedImageStream(ImageElement image) =>
        SavedPicture(image).Form.Dictionary.GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("XObject"))!.GetStream(PdfName.Get("Im1"))!;

    /// <summary>Appearance for saving, exporting and printing (computed once per element).</summary>
    private static ElementAppearance SavedPicture(ImageElement image) => SavedPictures.GetValue(image, _ =>
    {
        var (tw, th) = SavedPixels(image);
        var pixels = tw != image.Image.Width || th != image.Image.Height ? Imaging.Lanczos.Resize(image.Image, tw, th) : image.Image;
        if (image.Sharpness > 0)
        {
            // Radius in pixels of the saved resolution (a smaller original has fewer pixels per inch, so the radius is narrower)
            var dpi = pixels.Width / Math.Max(1e-6, ShownSize(image).Width / 72);
            pixels = Imaging.Sharpen.UnsharpMask(pixels, Math.Clamp(image.Sharpness, 0, 1) * SharpenAmount, Math.Max(0.5, SharpenSigma * dpi / SavedDpi));
        }
        return BuildPicture(image, ImageStream(pixels), pixels.Width, pixels.Height, centered: true, stretch: !image.KeepAspect, opacity: image.Opacity);
    });

    /// <summary>Resources with the image and, for opacity below 1, a graphics state /GO (built once per image and opacity).</summary>
    private static PdfDictionary Resources(PdfStream image, double opacity)
    {
        if (opacity >= 1) return Resources(image);
        var byOpacity = OpacityResources.GetValue(image, _ => []);
        lock (byOpacity)
        {
            var key = Math.Round(Math.Clamp(opacity, 0, 1), 3);
            if (byOpacity.TryGetValue(key, out var found)) return found;
            var images = new PdfDictionary();
            images.Set(PdfName.Get("Im1"), image);
            var state = new PdfDictionary();
            state.Set(PdfName.Get("ca"), new PdfReal(key));
            state.Set(PdfName.Get("CA"), new PdfReal(key));
            var states = new PdfDictionary();
            states.Set(PdfName.Get("GO"), state);
            var resources = new PdfDictionary();
            resources.Set(PdfName.Get("XObject"), images);
            resources.Set(PdfName.Get("ExtGState"), states);
            return byOpacity[key] = resources;
        }
    }

    // For BuildPicture: content prefix of an image with opacity
    private static string OpacityPrefix(double opacity) => opacity < 1 ? "/GO gs " : "";
}
