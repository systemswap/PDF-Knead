using System.Buffers.Binary;
using PdfKnead.Core.Fonts;

namespace PdfKnead.Core.Pdf.Fonts;

/// <summary>
/// The licence an embedded font program states for itself (OpenType OS/2 fsType, 31.2.5): ISO 32000-2, 9.9.1 sentences 8–10 — an embedded
/// program serves viewing and printing; who sets new or changed text with it keeps to its licence. Viewing and printing never ask; editing gives
/// a notice (docs/FEHLERGEDAECHTNIS.md, No. 1165).
/// </summary>
public static class FontLicence
{
    /// <summary>
    /// Whether the licence of the embedded program forbids new or changed text with it: "restricted licence" (2) or "preview and print" (4)
    /// without "editable" (8) — of several usage bits the least restrictive applies; 0 is installable. Programs without OS/2 (Type 1, bare CFF)
    /// state nothing and forbid nothing.
    /// </summary>
    /// <param name="font">The font of the file.</param>
    /// <returns>True if the licence allows only viewing and printing.</returns>
    public static bool ForbidsEditing(PdfFont font) =>
        (font switch { SimpleFont simple => simple.EmbeddedOpenType, CompositeFont composite => composite.EmbeddedOpenType, _ => null }) is { } program
        && ForbidsEditing(program);

    /// <summary>The licence bits of an OpenType program (see <see cref="ForbidsEditing(PdfFont)"/>).</summary>
    internal static bool ForbidsEditing(OpenTypeFont program)
    {
        var os2 = program.TableData("OS/2");
        if (os2.Length < 10) return false;
        var usage = BinaryPrimitives.ReadUInt16BigEndian(os2[8..]) & 0x000F;
        return (usage & 0x0006) != 0 && (usage & 0x0008) == 0;
    }

    /// <summary>
    /// Whether the licence of an embedded program forbids changing it (OpenType OS/2 fsType, 31.2.5): "restricted licence" (2) as the only usage
    /// bit — it may not be modified — or "no subsetting" (bit 8, from OS/2 version 2 on; versions 0 and 1 know only bits 0–3). Compressing and
    /// rebuilding for PDF/A leave such a program as it is and say so; redaction scrubs it all the same, as nothing of the redacted text may stay
    /// (docs/FEHLERGEDAECHTNIS.md, No. 1164).
    /// </summary>
    /// <param name="program">The embedded TrueType or OpenType program.</param>
    /// <returns>True if the program is to stay as it is.</returns>
    internal static bool KeepsProgram(OpenTypeFont program)
    {
        var os2 = program.TableData("OS/2");
        if (os2.Length < 10) return false;
        int fsType = BinaryPrimitives.ReadUInt16BigEndian(os2[8..]);
        if (BinaryPrimitives.ReadUInt16BigEndian(os2) < 2) fsType &= 0x000F;
        return (fsType & 0x000F) == 0x0002 || (fsType & 0x0100) != 0;
    }

    /// <summary>Whether the licence of the font's embedded program forbids changing it (see <see cref="KeepsProgram(OpenTypeFont)"/>).</summary>
    /// <param name="font">The font of the file.</param>
    /// <returns>True if its program is to stay as it is.</returns>
    internal static bool KeepsProgram(PdfFont font) =>
        (font switch { SimpleFont simple => simple.EmbeddedOpenType, CompositeFont composite => composite.EmbeddedOpenType, _ => null }) is { } program
        && KeepsProgram(program);
}

internal sealed partial class SimpleFont
{
    /// <summary>The embedded TrueType or OpenType program; null for another or none.</summary>
    internal OpenTypeFont? EmbeddedOpenType => _program is { IsEmbedded: true, OpenType: { } program } ? program : null;
}

internal sealed partial class CompositeFont
{
    /// <summary>The embedded TrueType or OpenType program of the CIDFont; null for another or none.</summary>
    internal OpenTypeFont? EmbeddedOpenType => _program is { IsEmbedded: true, OpenType: { } program } ? program : null;
}
