using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media.Imaging;
using PdfKnead.Controls;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// Renders the program icon from the <see cref="AppLogo"/> drawing: Icon.png (256 px, Linux menu) and
/// app.ico (Windows, several sizes). The files are rewritten only with
/// PDFKNEAD_WRITE_ICON=1; otherwise the test only checks that rendering succeeds.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class AppIconTests
{
    private static readonly int[] IcoSizes = [16, 24, 32, 48, 64, 128, 256];

    private static string AssetsFolder([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "PdfKnead.Ui", "Assets"));

    [Fact]
    public void Logo_RendersAtAllSizes_AndCanBeWritten()
    {
        var images = UiSession.Run(() => IcoSizes.ToDictionary(s => s, Render));
        Assert.All(images.Values, png => Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, png[..4]));

        if (Environment.GetEnvironmentVariable("PDFKNEAD_WRITE_ICON") != "1") return;
        File.WriteAllBytes(Path.Combine(AssetsFolder(), "Icon.png"), images[256]);
        File.WriteAllBytes(Path.Combine(AssetsFolder(), "app.ico"), Ico(images));
    }

    private static byte[] Render(int size)
    {
        var logo = new AppLogo { Width = size, Height = size };
        logo.Measure(new Size(size, size));
        logo.Arrange(new Rect(0, 0, size, size));
        using var bitmap = new RenderTargetBitmap(new PixelSize(size, size));
        bitmap.Render(logo);
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    /// <summary>Builds an ICO file with PNG entries (supported since Windows Vista): header, directory, images.</summary>
    private static byte[] Ico(IReadOnlyDictionary<int, byte[]> pngs)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write((ushort)0);            // reserved
        w.Write((ushort)1);            // type: icon
        w.Write((ushort)pngs.Count);
        var offset = 6 + 16 * pngs.Count;
        foreach (var (size, png) in pngs.OrderBy(p => p.Key))
        {
            w.Write((byte)(size >= 256 ? 0 : size));   // 0 means 256
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);          // no palette
            w.Write((byte)0);
            w.Write((ushort)1);        // colour planes
            w.Write((ushort)32);       // bits per pixel
            w.Write(png.Length);
            w.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in pngs.OrderBy(p => p.Key)) w.Write(png);
        return stream.ToArray();
    }
}
