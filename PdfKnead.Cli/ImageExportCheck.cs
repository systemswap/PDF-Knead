using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Cli;

/// <summary>
/// Cross-check for <c>pdf-knead bilder … --speichern</c> (docs/PRUEFUNGEN.md, section 6h): a saved PNG is read by an independent reader
/// (Skia's) and, where its colours are meant to be the drawn ones (no ICC profile, nothing converted, not reduced), compared pixel by
/// pixel with what the program draws.
/// </summary>
internal static class ImageExportCheck
{
    /// <summary>The verdict: null if fine, otherwise what is wrong.</summary>
    public static string? Check(PdfDocument doc, ImageSource source, ExportedImage exported)
    {
        if (exported.Format != ImageExportFormat.Png) return null;
        using var codec = SKCodec.Create(new SKMemoryStream(exported.Data));
        if (codec is null) return "PNG unlesbar";
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        if (codec.GetPixels(info, bitmap.GetPixels()) != SKCodecResult.Success) return "PNG unvollständig";
        var withProfile = exported.Data.AsSpan().IndexOf("iCCP"u8) >= 0;
        if (withProfile || exported.ColorsConverted || exported.Stencil) return null;
        if (PdfImageDecoder.Decode(source, doc) is not { } drawn) return null;
        if (drawn.Width != info.Width || drawn.Height != info.Height) return null;   // reduced for drawing, or the mask is finer
        var expected = drawn.Rgba;
        var actual = bitmap.Bytes;
        var worst = 0;
        for (var i = 0; i < expected.Length; i += 4)
        {
            // colours only count where something can be seen
            var a = expected[i + 3];
            worst = Math.Max(worst, Math.Abs(a - actual[i + 3]));
            if (a == 0 || exported.MaskPng is not null) continue;
            for (var c = 0; c < 3; c++) worst = Math.Max(worst, Math.Abs(expected[i + c] - actual[i + c]));
        }
        // 16 bits: the reader keeps the high byte, drawing rounds
        return worst <= 1 ? null : $"weicht um bis zu {worst} ab";
    }
}
