using System.Runtime.CompilerServices;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Rendering;

namespace PdfKnead.Tests;

/// <summary>
/// Hidden text is measured by drawing, as in the program (<see cref="TextVisibility.DefaultProbe"/>), and the transparency of PDF/A-1 copies
/// is drawn (<see cref="Core.Pdf.Writing.PdfAFlattening.Rasterizer"/>).
/// </summary>
internal static class VisibilitySetup
{
    [ModuleInitializer]
    internal static void UseRenderer()
    {
        TextVisibility.DefaultProbe = VisibilityProbe.Instance;
        // the transparency of PDF/A-1 copies is drawn by the renderer as well
        Core.Pdf.Writing.PdfAFlattening.Rasterizer = AreaRasterizer.Instance;
    }
}
