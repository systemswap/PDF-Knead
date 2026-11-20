using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;

namespace PdfKnead.ViewModels;

/// <summary>What the resolution and format are chosen for.</summary>
public enum RasterPurpose
{
    /// <summary>„Sicher“ tab: save as images on the next save.</summary>
    SaveAsImage,

    /// <summary>File → Export: a new PDF made of images.</summary>
    ExportPdf,
}

/// <summary>File → Export: the open file and its tab stay as they are.</summary>
public sealed partial class DocumentViewModel
{
    /// <summary>
    /// Writes a new PDF in which each page is an image (like „Flachlegen als Bild“, but without replacing the file in the tab). Password
    /// and permissions are the same as for the next save.
    /// </summary>
    /// <param name="path">Target file.</param>
    /// <param name="settings">Resolution and image format.</param>
    /// <returns>The result of the save.</returns>
    public async Task<PdfSaveResult> ExportImagePdfAsync(string path, RasterSettings settings)
    {
        var pages = History.Pages.ToArray();
        var main = Document;
        var options = SaveOptions(SaveVersion);
        var full = System.IO.Path.GetFullPath(path);
        return await WorkAsync(() => Task.Run(() => SaveAsImages(main, pages, full, options, settings)));
    }

    /// <summary>
    /// Writes and checks a PDF/A copy in memory (KONZEPT.md, section 5.21), with what the next save would write (cleaned items, flattening);
    /// redactions were applied beforehand („Schwärzen zuerst“).
    /// </summary>
    /// <param name="part">The part and level.</param>
    /// <param name="accessibility">Level a: the language and the answers for the figures.</param>
    /// <returns>The copy and the rules it does not meet (fonts without a readable program embedded as their substitute: <see cref="PdfAResult.SubstitutedFonts"/>).</returns>
    /// <summary>
    /// The document has attached files (/Names /EmbeddedFiles in the main file): the PDF/A parts that keep them fit it best
    /// (<see cref="PdfAViewModel"/>).
    /// </summary>
    public bool HasAttachments =>
        Document.Catalog?.GetDictionary(Core.Pdf.Objects.PdfName.Get("Names"))?.ContainsKey(Core.Pdf.Objects.PdfName.Get("EmbeddedFiles")) == true;

    public async Task<PdfAResult> WritePdfAAsync(PdfAConformance part, PdfAAccessibility? accessibility)
    {
        var pages = History.Pages.ToArray();
        InDocumentRedaction.EnsureApplied(pages);
        var main = Document;
        var options = SaveOptions("1.7");
        return await WorkAsync(() => Task.Run(() => PdfAExport.Write(main, pages, part, options, accessibility: accessibility)));
    }

    /// <summary>
    /// What a tagged copy (PDF/A level a) asks the user: the language its text appears to be in, and the figures that need an alternative text,
    /// each with a small picture of it (KONZEPT.md, section 5.21, „Stufe a“).
    /// </summary>
    /// <returns>The proposed language (empty if none stands out) and the figures with their pictures (RGB, null where none could be drawn).</returns>
    public async Task<(string Language, IReadOnlyList<(PdfAFigure Figure, RasterArea? Picture)> Figures)> PdfAFiguresAsync()
    {
        var pages = History.Pages.ToArray();
        var list = pages.Select(p => (p.Source, p.Page)).ToList();
        return await Task.Run(() =>
        {
            var language = PdfATagging.ProposeLanguage(list);
            var figures = PdfATagging.Figures(list).Take(500)
                .Select(f => (f, f.Bounds is { } b && f.Page >= 0 && f.Page < list.Count ? PdfATagging.Thumbnail(list[f.Page].Source, list[f.Page].Page, b, 96) : null))
                .ToList();
            return (language, (IReadOnlyList<(PdfAFigure, RasterArea?)>)figures);
        });
    }

    /// <summary>
    /// Writes pages as image files into <paramref name="folder"/> (named after the file, „Name_001“ …). Redactions were applied
    /// beforehand („Schwärzen zuerst“); a mark left over is refused, since the red mark and the content underneath would end up in the image.
    /// </summary>
    /// <param name="folder">Target folder.</param>
    /// <param name="settings">Format, quality and resolution.</param>
    /// <param name="indices">The pages to write (indices in the page list), or null for all.</param>
    /// <returns>The paths of the written files.</returns>
    public async Task<List<string>> ExportImagesAsync(string folder, PdfKnead.Core.Imaging.PageImageSettings settings, IReadOnlyList<int>? indices = null)
    {
        var all = History.Pages;
        var pages = indices is null ? all.ToArray() : indices.Where(i => (uint)i < (uint)all.Count).Select(i => all[i]).ToArray();
        InDocumentRedaction.EnsureApplied(pages);
        var baseName = System.IO.Path.GetFileNameWithoutExtension(FileName);
        return await WorkAsync(() => Task.Run(() => RasterExport.WriteImages(pages, folder, baseName, settings)));
    }
}
