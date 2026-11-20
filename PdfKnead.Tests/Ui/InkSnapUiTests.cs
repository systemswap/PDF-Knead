using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// „Suchen und schwärzen“ on a scan (KONZEPT.md, section 5.6): the recognised word's box is smaller than its ink — the bar grows to cover
/// all the ink, also the part beyond the box; a neighbouring mark a space away stays (docs/FEHLERGEDAECHTNIS.md, No. 351). The hit is not
/// counted as hidden: the ink it stands for shows (No. 352).
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class InkSnapUiTests
{
    [Fact]
    public void RecognisedWordOverAScan_BarCoversAllItsInk()
    {
        // a scan of 200 × 100 pixels drawn as 200 × 100 pt at (100, 500): paper, the word's ink from x 20 to 120, y 30 to 70 (pixels from the
        // top) — in the page from (120, 530) to (220, 570) —, and a neighbour's ink 30 pixels to the right
        var pdf = TestPdf.Simple(1);
        var pixels = new byte[200 * 100 * 3];
        for (var y = 0; y < 100; y++)
            for (var x = 0; x < 200; x++)
            {
                var ink = y is >= 30 and < 70 && (x is >= 20 and < 120 && (x / 6 % 2 == 0 || y is < 34 or >= 66) || x is >= 150 and < 190);
                (pixels[(y * 200 + x) * 3], pixels[(y * 200 + x) * 3 + 1], pixels[(y * 200 + x) * 3 + 2]) = ink ? ((byte)25, (byte)30, (byte)110) : ((byte)245, (byte)242, (byte)235);
            }
        var image = pdf.Add(TestPdf.FlateStream(pixels, TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
            ("Width", PdfInteger.Of(200)), ("Height", PdfInteger.Of(100)), ("ColorSpace", TestPdf.N("DeviceRGB")), ("BitsPerComponent", PdfInteger.Of(8)))));
        // the recognised text, invisible, its box inside the ink: from x 128 to 212, height of a 20-point font from the baseline 540
        pdf.Replace(4, TestPdf.FlateStream("q 200 0 0 100 100 500 cm /Im0 Do Q BT 3 Tr /F1 20 Tf 76 Tz 128 540 Td (Geheimwort) Tj ET"u8.ToArray()));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("Im0", TestPdf.Ref(image)))),
            ("Font", TestPdf.Dict(("F1", TestPdf.Ref(3))))));
        var file = TestFiles.Write(pdf.Build(), "scan-wort.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([file]);
                var doc = vm.SelectedDocument!;
                var hits = await doc.FindForRedactionAsync(["Geheimwort"], RedactionPatterns.None, false);
                // not „(versteckt)“: the ink shows — redacting only the invisible text would leave the word readable in the scan
                Assert.False(Assert.Single(hits).Invisible);
                await doc.RedactHitsAsync(hits);
                var areas = doc.ElementsOn(0).OfType<RedactionElement>().SelectMany(e => e.PageAreas).ToList();
                // every corner of the word's ink lies under a bar
                foreach (var (x, y) in new[] { (120.5, 530.5), (219.5, 530.5), (120.5, 569.5), (219.5, 569.5) })
                    Assert.True(areas.Any(a => x >= a.Left && x <= a.Right && y >= a.Bottom && y <= a.Top), $"Tinte bei ({x}, {y}) nicht unter dem Balken: {string.Join(" ", areas)}");
                // the neighbour (from x 250) stays outside
                Assert.All(areas, a => Assert.True(a.Right < 249, $"Balken reicht bis {a.Right}"));
            }
            finally
            {
                window.Close();
            }
        });
    }
}
