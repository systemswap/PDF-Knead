using System.Globalization;
using System.Text;
using PdfKnead.Core.Barcodes;
using PdfKnead.Core.Barcodes.DataMatrix;
using PdfKnead.Core.Barcodes.Linear;
using PdfKnead.Core.Barcodes.Qr;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;
using static PdfKnead.Tests.Pdf.TestPdf;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// Check of redaction with QR and bar codes (not part of the normal test run; enable with PDFKNEAD_PRUEFUNG=codes): codes of four kinds,
/// drawn as vector rectangles, as a 1-bit image or a greyscale image, upright, turned by 90° or by 30°, covered by a bar not at all, in
/// part (the left 40 %, or the upper half — along which a bar code stays readable), or wholly; beside each a second code nobody touches. Redacted through the question as in the program (reported codes checked),
/// saved, then searched: a covered code must not be readable any more, the other one must be. One line per case, in pdf-knead-codes.txt
/// in the temporary folder.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class CodeRedactionMatrix
{
    private static bool[,] Grid(string kind, string text) => kind switch
    {
        "qr" => Barcodes.BarcodeScannerTests.Grid(QrEncoder.Encode(text, QrErrorCorrection.H)),
        "dm" => Barcodes.BarcodeScannerTests.Grid(DataMatrixCodec.Encode(text)),
        "ean" => Barcodes.BarcodeScannerTests.Grid(LinearCodes.Encode(LinearSymbology.Ean13, text)),
        _ => Barcodes.BarcodeScannerTests.Grid(LinearCodes.Encode(LinearSymbology.Code128, text)),
    };

    /// <summary>A code as content: vector rectangles or an image, placed by <paramref name="placement"/> (unit square → page).</summary>
    private static string Draw(bool[,] grid, string representation, string name, (double A, double B, double C, double D, double E, double F) placement,
        TestPdf pdf, PdfDictionary xobjects)
    {
        int columns = grid.GetLength(0), rows = grid.GetLength(1);
        string M((double A, double B, double C, double D, double E, double F) m) =>
            FormattableString.Invariant($"{m.A:0.####} {m.B:0.####} {m.C:0.####} {m.D:0.####} {m.E:0.###} {m.F:0.###} cm");
        if (representation == "vektor")
        {
            var s = new StringBuilder("q ").Append(M(placement)).Append(" 0 g\n");
            for (var r = 0; r < rows; r++)
                for (var c = 0; c < columns; c++)
                    if (grid[c, r])
                        s.Append(FormattableString.Invariant($"{(double)c / columns:0.######} {(double)(rows - 1 - r) / rows:0.######} {1.0 / columns:0.######} {1.0 / rows:0.######} re\n"));
            return s.Append("f Q\n").ToString();
        }
        // 1-bit (1 = white) or 8-bit grey, three pixels per module
        const int k = 3;
        int w = columns * k, h = rows * k;
        PdfStream image;
        if (representation == "bild1")
        {
            var rowBytes = (w + 7) / 8;
            var data = new byte[rowBytes * h];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    if (!grid[x / k, rows == 1 ? 0 : y / k]) data[y * rowBytes + x / 8] |= (byte)(0x80 >> (x % 8));
            image = FlateStream(data, Dict(("Type", N("XObject")), ("Subtype", N("Image")), ("Width", PdfInteger.Of(w)), ("Height", PdfInteger.Of(h)),
                ("BitsPerComponent", PdfInteger.Of(1)), ("ColorSpace", N("DeviceGray"))));
        }
        else
        {
            var data = new byte[w * h];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++) data[y * w + x] = grid[x / k, rows == 1 ? 0 : y / k] ? (byte)20 : (byte)240;
            image = FlateStream(data, Dict(("Type", N("XObject")), ("Subtype", N("Image")), ("Width", PdfInteger.Of(w)), ("Height", PdfInteger.Of(h)),
                ("BitsPerComponent", PdfInteger.Of(8)), ("ColorSpace", N("DeviceGray"))));
        }
        xobjects.Set(N(name), Ref(pdf.Add(image)));
        return $"q {M(placement)} /{name} Do Q\n";
    }

    [Fact]
    public void Run()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_PRUEFUNG") != "codes") return;
        var report = new List<string>();
        var failures = 0;
        var texts = new Dictionary<string, string> { ["qr"] = "Lieferschein 4711-A", ["dm"] = "Charge 2026/17", ["ean"] = "4006381333931", ["c128"] = "RE-2026-0815" };
        foreach (var kind in new[] { "qr", "dm", "ean", "c128" })
            foreach (var representation in new[] { "vektor", "bild1", "bild8" })
                foreach (var angle in new[] { 0, 90, 30 })
                    foreach (var cover in new[] { "kein", "teil", "oben", "ganz" })
                    {
                        var name = $"{kind}-{representation}-{angle}-{cover}";
                        var (line, ok) = Case(kind, texts[kind], representation, angle, cover, name);
                        report.Add(line);
                        if (!ok) failures++;
                    }
        report.Add($"Fälle {report.Count}, Fehler {failures}");
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "pdf-knead-codes.txt"), report);
        Console.WriteLine(string.Join("\n", report));
        Assert.Equal(0, failures);
    }

    private static (string Line, bool Ok) Case(string kind, string text, string representation, int angle, string cover, string name)
    {
        var pdf = new TestPdf();
        var xobjects = new PdfDictionary();
        var grid = Grid(kind, text);
        var linear = grid.GetLength(1) == 1;
        // size on the page: 2D codes 1.2 pt per module, bar codes 1 pt per module and 30 pt high
        double width = grid.GetLength(0) * (linear ? 1.0 : 1.2), height = linear ? 30 : grid.GetLength(1) * 1.2;
        double rad = angle * Math.PI / 180, cos = Math.Cos(rad), sin = Math.Sin(rad);
        const double cx = 220, cy = 500;
        // unit square → page: scaled, turned about its centre
        var placement = (width * cos, width * sin, -height * sin, height * cos,
            cx - (width * cos - height * sin) / 2, cy - (width * sin + height * cos) / 2);
        double bx = Math.Abs(width * cos) + Math.Abs(height * sin), by = Math.Abs(width * sin) + Math.Abs(height * cos);
        var bounds = new PdfRectangle(cx - bx / 2, cy - by / 2, cx + bx / 2, cy + by / 2);
        var other = QrEncoder.Encode("Unbeteiligt 99", QrErrorCorrection.M);
        var content = Draw(grid, representation, "Ziel", placement, pdf, xobjects)
                  + Draw(Barcodes.BarcodeScannerTests.Grid(other), "vektor", "Anders", (60, 0, 0, 60, 420, 650), pdf, xobjects)
                  + "BT /F1 10 Tf 72 780 Td (Codes) Tj ET\n";
        var font = pdf.Add(Dict(("Type", N("Font")), ("Subtype", N("Type1")), ("BaseFont", N("Helvetica"))));
        var pages = pdf.Add(new PdfDictionary());
        var stream = pdf.Add(new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(content)));
        var page = pdf.Add(Dict(("Type", N("Page")), ("Parent", Ref(pages)), ("Contents", Ref(stream)), ("MediaBox", Box(0, 0, 595.28, 841.89)),
            ("Resources", Dict(("XObject", xobjects), ("Font", Dict(("F1", Ref(font))))))));
        pdf.Replace(pages, Dict(("Type", N("Pages")), ("Kids", new PdfArray([Ref(page)])), ("Count", PdfInteger.Of(1))));
        pdf.RootNumber = pdf.Add(Dict(("Type", N("Catalog")), ("Pages", Ref(pages))));
        var file = TestFiles.Write(pdf.Build(), name + ".pdf");
        var target = Path.Combine(Path.GetDirectoryName(file)!, name + "-geschwaerzt.pdf");

        // the bar: none (then one in an empty corner, so that the redaction runs), the left 40 % of the code's bounds, or all of it
        var bar = cover switch
        {
            "teil" => new PdfRectangle(bounds.Left - 2, bounds.Bottom - 2, bounds.Left + bounds.Width * 0.4, bounds.Top + 2),
            // the upper half: a bar code stays fully readable along the bars below it
            "oben" => new PdfRectangle(bounds.Left - 2, bounds.Bottom + bounds.Height * 0.5, bounds.Right + 2, bounds.Top + 2),
            "ganz" => new PdfRectangle(bounds.Left - 3, bounds.Bottom - 3, bounds.Right + 3, bounds.Top + 3),
            _ => new PdfRectangle(20, 20, 60, 40),
        };
        RedactionQuestion? asked = null;
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1200, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([file]);
                var doc = vm.SelectedDocument!;
                doc.AddElement(0, new RedactionElement { Rect = bar }, "Test");
                vm.ConfirmRedaction = question =>
                {
                    asked = question;
                    return Task.FromResult(new RedactionReply(RedactionAnswer.Apply, AlsoUnredacted: true, Codes: question.Codes));
                };
                vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(target);
                await vm.ApplyRedactionsCommand.ExecuteAsync(null);
                await vm.SaveAsCommand.ExecuteAsync(null);
            }
            finally
            {
                window.Close();
            }
        });
        if (!File.Exists(target)) return ($"FEHLER {name}: nicht gespeichert", false);
        using var saved = PdfDocument.Open(File.ReadAllBytes(target));
        var list = Core.Pdf.Content.PageInterpreter.Build(saved, saved.Pages[0]);
        var found = PdfKnead.Rendering.PageBarcodes.FindAll(saved, saved.Pages[0], list, [], new PdfKnead.Rendering.ImageCache()).Select(c => c.Code.Text).ToList();
        var reported = asked?.Codes.Count ?? 0;
        var targetReadable = found.Contains(text);
        var otherReadable = found.Contains("Unbeteiligt 99");
        var ok = cover switch
        {
            "kein" => targetReadable && otherReadable && reported == 0,
            "teil" or "oben" => !targetReadable && otherReadable && reported == 1,
            _ => !targetReadable && otherReadable && reported == 0,
        };
        return ($"{(ok ? "OK    " : "FEHLER")} {name}: gemeldet {reported}, Ziel lesbar {targetReadable}, anderer lesbar {otherReadable}", ok);
    }
}
