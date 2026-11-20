using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// Where the size of a PDF lies (for compression): images, fonts, page content (content streams and forms) and the rest
/// (structure, object streams, metadata …). The encoded data of the streams is counted; the rest is the file size minus these.
/// </summary>
/// <param name="Total">File size in bytes.</param>
/// <param name="Images">Encoded size of the image streams in bytes.</param>
/// <param name="Fonts">Encoded size of the embedded font programs in bytes.</param>
/// <param name="Content">Encoded size of the page content streams and form XObjects in bytes.</param>
public sealed record SizeReport(long Total, long Images, long Fonts, long Content)
{
    private static readonly PdfName ImageName = PdfName.Get("Image"), Form = PdfName.Get("Form"), Length1 = PdfName.Get("Length1");
    private static readonly HashSet<string> FontSubtypes = ["Type1C", "CIDFontType0C", "OpenType"];

    /// <summary>Everything else in bytes: <see cref="Total"/> minus the three counted parts, at least 0.</summary>
    public long Other => Math.Max(0, Total - Images - Fonts - Content);

    /// <summary>Count at most this many objects (crafted /Size).</summary>
    private const int MaxObjects = 5_000_000;

    /// <summary>Measures where the size of a document lies.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="cancel">Cancels the count.</param>
    /// <returns>The report.</returns>
    /// <remarks>Reads every object up to /Size (at most 5,000,000), so the cost grows with the number of objects.</remarks>
    public static SizeReport Of(PdfDocument doc, CancellationToken cancel = default)
    {
        var contents = new HashSet<int>();
        foreach (var page in doc.Pages)
            switch (page.Dictionary.GetRaw(PdfName.Contents))
            {
                case PdfReference r:
                    contents.Add(r.Id.Number);
                    if (doc.Resolve(r.Id) is PdfArray referenced) AddParts(referenced);
                    break;
                case PdfArray direct:
                    AddParts(direct);
                    break;
            }
        void AddParts(PdfArray parts)
        {
            for (var i = 0; i < parts.Count; i++)
                if (parts[i] is PdfReference part) contents.Add(part.Id.Number);
        }

        long images = 0, fonts = 0, content = 0;
        var size = (int)Math.Clamp(doc.Trailer.GetInteger(PdfName.Size) ?? 0, 0, MaxObjects);
        for (var n = 1; n < size; n++)
        {
            if ((n & 4095) == 0) cancel.ThrowIfCancellationRequested();
            PdfObject obj;
            try
            {
                obj = doc.GetObject(n);
            }
            catch (PdfException)
            {
                continue;
            }
            if (obj is not PdfStream stream) continue;
            var length = stream.RawData?.Length ?? stream.DataLength;
            var dict = stream.Dictionary;
            var subtype = dict.GetName(PdfName.Subtype);
            if (subtype == ImageName) images += length;
            else if (dict.ContainsKey(Length1) || subtype is not null && FontSubtypes.Contains(subtype.Value)) fonts += length;
            else if (subtype == Form || contents.Contains(n)) content += length;
        }
        return new SizeReport(doc.FileSize, images, fonts, content);
    }

    /// <summary>Returns the encoded size of the images a page draws (also in its forms and patterns).</summary>
    /// <param name="doc">The document.</param>
    /// <param name="page">The page.</param>
    /// <returns>The size in bytes.</returns>
    public static long PageImages(PdfDocument doc, PdfPage page)
    {
        var used = new HashSet<int>();
        ResourceUsage.PageObjects(doc, page, used);
        long sum = 0;
        foreach (var n in used)
            if (doc.GetObject(n) is PdfStream { } s && s.Dictionary.GetName(PdfName.Subtype) == ImageName) sum += s.RawData?.Length ?? s.DataLength;
        return sum;
    }
}
