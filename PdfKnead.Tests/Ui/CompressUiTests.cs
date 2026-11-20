using Avalonia.Controls;
using Avalonia.Media.Imaging;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>UI tests for the compression window (trial run, sizes, before/after with zoom, saving) and for "Save as" with compression.</summary>
[Collection(nameof(UiCollection))]
public sealed class CompressUiTests
{
    private static void WaitUntil(Window window, Func<bool> condition, int seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < until)
        {
            UiSession.Capture(window);
            Thread.Sleep(20);
        }
        Assert.True(condition(), "Zeitüberschreitung");
    }

    private static void Shoot(Window window, string name)
    {
        var frame = UiSession.Capture(window);
        if (UiSession.ScreenshotFolder() is { } folder)
            frame.Save(Path.Combine(folder, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    /// <summary>Builds a page with a photo at 1200 dpi: it gets downsampled and smaller, the sliders switch to Custom, and zooming redraws sharply.</summary>
    private static byte[] Photo() => CompressionWriterTests.WithImage(2400, 1600, 3,
        (x, y, c) => (byte)(128 + 100 * Math.Sin(x / 37.0 + c) * Math.Cos(y / 23.0)), inches: 2, caption: "Foto");

    /// <summary>
    /// A digitally signed file compressed into a new file: the copy is written anew, so its signature is broken. Asked first as with
    /// „Speichern unter“ (phase 9.1; before, the copy was written without a word): without a view and on „Abbrechen“ nothing is written,
    /// on „Speichern“ the copy is written and the original stays signed byte for byte.
    /// </summary>
    [Fact]
    public void SignedFile_AsksBeforeTheCompressedCopy()
    {
        var bytes = SignedTestPdf.Build();
        var pdf = TestFiles.Write(bytes, "unterschrieben-komprimieren.pdf");
        var target = Path.Combine(Path.GetDirectoryName(pdf)!, "unterschrieben-komprimieren (komprimiert).pdf");
        File.Delete(target);
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            CompressWindow? dialog = null;
            try
            {
                await vm.OpenAsync([pdf]);
                CompressViewModel? compress = null;
                Task<string?>? result = null;
                vm.ShowCompress = c =>
                {
                    compress = c;
                    dialog = new CompressWindow(c);
                    result = dialog.ShowDialog<string?>(window);
                    return Task.FromResult<string?>(null);
                };
                await vm.CompressCommand.ExecuteAsync(null);
                WaitUntil(window, () => compress is { IsReady: true });
                var c = compress!;
                c.PickSavePath = (_, _, _) => Task.FromResult<string?>(target);

                // without an answer (no view): nothing is written
                c.ConfirmSignedSave = null;
                await c.SaveCommand.ExecuteAsync(null);
                Assert.False(File.Exists(target));

                // cancel: the question names the file and the signature, for a new file; nothing is written
                var questions = new List<SignedSaveQuestion>();
                c.ConfirmSignedSave = question =>
                {
                    questions.Add(question);
                    return Task.FromResult(SignedSaveAnswer.Cancel);
                };
                await c.SaveCommand.ExecuteAsync(null);
                var asked = Assert.Single(questions);
                Assert.Equal(("unterschrieben-komprimieren.pdf", 1, false), (asked.FileName, asked.Count, asked.OverwritesOriginal));
                Assert.False(File.Exists(target));

                // save: the copy is written, the original stays as it was
                c.ConfirmSignedSave = _ => Task.FromResult(SignedSaveAnswer.Save);
                await c.SaveCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(60));
                await result!.WaitAsync(TimeSpan.FromSeconds(60));
                Assert.True(File.Exists(target));
                Assert.Equal(bytes, File.ReadAllBytes(pdf));
            }
            finally
            {
                dialog?.Close();
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Protected file (AES, open password): the predicted size matches the saved file to the byte, because the trial run writes with
    /// encryption just like saving does; without encryption the prediction is up to 55 % too small for small files. The preview opens
    /// the trial result without a password.
    /// </summary>
    [Fact]
    public void ProtectedFile_PredictedSizeIsExact()
    {
        byte[] protectedPdf;
        using (var plain = PdfDocument.Open(Photo()))
        using (var output = new MemoryStream())
        {
            PdfWriter.Write(plain, output, new PdfSaveOptions { Protection = new PdfProtection("abc", "besitzer", PdfPermissions.All) });
            protectedPdf = output.ToArray();
        }
        var pdf = TestFiles.Write(protectedPdf, "geschuetzt.pdf");
        var target = Path.Combine(Path.GetDirectoryName(pdf)!, "geschuetzt (komprimiert).pdf");
        UiSession.Run(async () =>
        {
            Services.UserSettings.Update(s => s with { Compression = null, CompressWindow = null });
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            // set after creating the window, which otherwise installs its own password dialog; answer only once (no endless asking)
            var asked = 0;
            vm.AskPassword = (_, _) => Task.FromResult<string?>(++asked == 1 ? "abc" : null);
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                Assert.NotNull(vm.SelectedDocument);
                CompressViewModel? compress = null;
                Task<string?>? result = null;
                vm.ShowCompress = c =>
                {
                    compress = c;
                    result = new CompressWindow(c).ShowDialog<string?>(window);
                    return Task.FromResult<string?>(null);
                };
                await vm.CompressCommand.ExecuteAsync(null);
                WaitUntil(window, () => compress is { IsReady: true });
                var c = compress!;
                var predicted = c.After!.Total;
                Assert.True(c.PageCount == 1 && c.Problem == "", c.Problem);
                c.PickSavePath = (_, _, _) => Task.FromResult<string?>(target);
                await c.SaveCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(60));
                await result!.WaitAsync(TimeSpan.FromSeconds(60));
                Assert.Equal(predicted, new FileInfo(target).Length);
                using var saved = PdfDocument.Open(File.ReadAllBytes(target), "abc");
                Assert.NotNull(saved.Security);
            }
            finally
            {
                Services.UserSettings.Update(s => s with { Compression = null, CompressWindow = null });
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// A remembered height taller than the screen would put the title bar buttons out of reach, so the window opens at most 800 pixels
    /// high while the width stays; a remembered height below 750 becomes 750 (the minimum height).
    /// </summary>
    [Theory]
    [InlineData(2400, 800)]
    [InlineData(600, 750)]
    [InlineData(770, 770)]
    public void Window_OpensAtMost800High(double remembered, double expected)
    {
        var pdf = TestFiles.Write(Photo(), "foto-hoch.pdf");
        UiSession.Run(async () =>
        {
            Services.UserSettings.Update(s => s with { Compression = null, CompressWindow = new Core.Platform.WindowSize(1300, remembered) });
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                CompressWindow? dialog = null;
                vm.ShowCompress = c =>
                {
                    dialog = new CompressWindow(c);
                    _ = dialog.ShowDialog<string?>(window);
                    return Task.FromResult<string?>(null);
                };
                await vm.CompressCommand.ExecuteAsync(null);
                Assert.NotNull(dialog);
                Assert.Equal(1300, dialog!.Width);
                Assert.Equal(expected, dialog.Height);
                Assert.Equal(750, dialog.MinHeight);
                dialog.Close();
            }
            finally
            {
                Services.UserSettings.Update(s => s with { Compression = null, CompressWindow = null });
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    [Fact]
    public void Window_PreviewSizesZoomSave()
    {
        var pdf = TestFiles.Write(Photo(), "foto.pdf");
        var target = Path.Combine(Path.GetDirectoryName(pdf)!, "foto (komprimiert).pdf");
        UiSession.Run(async () =>
        {
            Services.UserSettings.Update(s => s with { Compression = null, CompressWindow = null });
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                CompressViewModel? compress = null;
                CompressWindow? dialog = null;
                Task<string?>? result = null;
                vm.ShowCompress = c =>
                {
                    compress = c;
                    dialog = new CompressWindow(c);
                    result = dialog.ShowDialog<string?>(window);
                    return Task.FromResult<string?>(null);
                };
                Assert.True(vm.CompressCommand.CanExecute(null));
                await vm.CompressCommand.ExecuteAsync(null);
                WaitUntil(window, () => compress is { IsReady: true });
                var c = compress!;
                var d = dialog!;

                // default E-book: the photo is downsampled
                Assert.Equal(CompressionPreset.EBook, c.SelectedPreset.Value);
                Assert.True(c.Downsample);
                var (before, after) = (c.Before!, c.After!);
                Assert.True(after.Total < before.Total / 2, $"{before.Total} → {after.Total}");
                Assert.True(after.Images < before.Images / 2);
                Assert.Equal(4, c.Rows.Count);
                Assert.True(c.PageImagesAfter >= 0 && c.PageImagesAfter < c.PageImagesBefore);
                Assert.Contains("→", c.TotalText);
                Shoot(d, "85-komprimieren");

                // zoom in: the visible region is redrawn sharply
                var preview = d.FindControl<PdfKnead.Controls.ComparePreview>("Preview")!;
                var page = preview.PageRect;
                preview.ZoomAt(10, new Avalonia.Point(page.Left + page.Width * 0.24, page.Top + page.Height * 0.56));
                Assert.True(preview.ZoomPercent > 100);
                WaitUntil(d, () => c.Region is not null);
                Assert.True(c.Region!.Width < c.PageWidth / 4);
                Shoot(d, "86-komprimieren-zoom");
                preview.Fit();

                // slider: switches to Custom, new trial run, smaller file
                c.Quality = 40;
                Assert.Equal(CompressionPreset.Custom, c.SelectedPreset.Value);
                WaitUntil(d, () => c.IsReady && !ReferenceEquals(c.After, after));
                Assert.True(c.After!.Images < after.Images, $"{after.Images} → {c.After.Images}");

                // lossless only: images stay unchanged
                c.SelectedPreset = c.Presets.Single(p => p.Value == CompressionPreset.Lossless);
                Assert.False(c.Downsample);
                var custom = c.After;
                WaitUntil(d, () => c.IsReady && !ReferenceEquals(c.After, custom));
                Assert.True(c.After!.Images > after.Images);

                // Screen as the starting point, then adjusted by hand: Custom with 120 dpi and 50 % is saved
                c.SelectedPreset = c.Presets.Single(p => p.Value == CompressionPreset.Screen);
                c.Quality = 50;
                WaitUntil(d, () => c.IsReady);
                var predicted = c.After!.Total;

                c.PickSavePath = (_, name, _) =>
                {
                    Assert.Equal("foto (komprimiert).pdf", name);
                    return Task.FromResult<string?>(target);
                };
                await c.SaveCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(60));
                var message = await result!.WaitAsync(TimeSpan.FromSeconds(60));
                Assert.Contains("foto (komprimiert).pdf", message);
                using (var saved = PdfDocument.Open(File.ReadAllBytes(target)))
                    Assert.Equal(1, saved.PageCount);
                Assert.True(new FileInfo(target).Length < new FileInfo(pdf).Length / 2);
                Assert.Equal(predicted, new FileInfo(target).Length);   // the prediction is exact to the byte
                var remembered = Services.UserSettings.Current.Compression!;
                Assert.Equal(CompressionPreset.Custom, remembered.Preset);
                Assert.Equal(50, remembered.JpegQuality);
                Assert.Equal(120, remembered.ImageDpi);
                Assert.NotNull(Services.UserSettings.Current.CompressWindow);
                await c.Released.WaitAsync(TimeSpan.FromSeconds(30));
                // the open file stays as it is
                Assert.False(vm.SelectedDocument!.IsModified);
                Assert.Equal(pdf, vm.SelectedDocument.Path);
            }
            finally
            {
                Services.UserSettings.Update(s => s with { Compression = null, CompressWindow = null });
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Zoomed in: elements and annotations from other programs sit at the same place in the redrawn region before and after
    /// compression; otherwise they end up offset by the position of the region (in a drawing, the note icons and circles). Here a red
    /// square away from the page corner. See docs/FEHLERGEDAECHTNIS.md, No. 82.
    /// </summary>
    [Fact]
    public void Window_ZoomedRegion_ElementsStayInPlace()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 1).Build(), "zoom-elemente.pdf");
        UiSession.Run(async () =>
        {
            Services.UserSettings.Update(s => s with { Compression = null, CompressWindow = null });
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                var square = new Core.Pdf.Editing.ShapeElement
                {
                    Kind = Core.Pdf.Editing.ShapeKind.Rectangle, Rect = new PdfRectangle(420, 150, 480, 210), StrokeColor = null, FillColor = 0xFF0000,
                };
                doc.AddElement(0, square, "Form");
                CompressViewModel? compress = null;
                CompressWindow? dialog = null;
                vm.ShowCompress = c =>
                {
                    compress = c;
                    dialog = new CompressWindow(c);
                    _ = dialog.ShowDialog<string?>(window);
                    return Task.FromResult<string?>(null);
                };
                await vm.CompressCommand.ExecuteAsync(null);
                WaitUntil(window, () => compress is { IsReady: true });
                var c = compress!;
                var d = dialog!;
                var preview = d.FindControl<PdfKnead.Controls.ComparePreview>("Preview")!;
                var page = preview.PageRect;
                var height = doc.Pages[0].Page.Height;
                preview.ZoomAt(6, new Avalonia.Point(page.Left + page.Width * 450 / doc.Pages[0].Page.Width, page.Top + page.Height * (height - 180) / height));
                WaitUntil(d, () => c.Region is not null);
                var region = c.Region!;
                using var before = SkiaSharp.SKBitmap.FromImage(region.Before);
                using var after = SkiaSharp.SKBitmap.FromImage(region.After);
                var ppp = before.Width / region.Width;
                SkiaSharp.SKColor At(SkiaSharp.SKBitmap bitmap, double x, double y) =>
                    bitmap.GetPixel((int)((x - region.Left) * ppp), (int)((height - y - region.Top) * ppp));
                Assert.True(region.Left > 100 && region.Top > 100, $"{region.Left} {region.Top}");   // a region far from the corner
                foreach (var bitmap in new[] { before, after })
                {
                    var inside = At(bitmap, 450, 180);
                    Assert.True(inside.Red > 230 && inside.Green < 30 && inside.Blue < 30, inside.ToString());
                    var outside = At(bitmap, 490, 180);
                    Assert.True(outside.Green > 230, outside.ToString());
                }
                d.Close();
            }
            finally
            {
                Services.UserSettings.Update(s => s with { Compression = null, CompressWindow = null });
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// The stored preset Print carries values that differ from its current definition (MRC, palette): the window takes the preset
    /// fresh and keeps only Brotli; Custom is restored as stored. A corrupted value (preset 9) falls back to E-book instead of crashing.
    /// </summary>
    [Fact]
    public void StoredPreset_IsTakenFresh()
    {
        var pdf = TestFiles.Write(Photo(), "voreinstellung.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            await vm.OpenAsync([pdf]);
            var doc = vm.SelectedDocument!;
            try
            {
                var print = CompressionSettings.For(CompressionPreset.Print);
                Services.UserSettings.Update(s => s with { Compression = print with { Scans = ScanMethod.Mrc, Graphics = GraphicsMethod.Palette, Brotli = true } });
                var c = new CompressViewModel(doc);
                Assert.Equal(print with { Brotli = true }, c.Settings());
                c.Dispose();
                var custom = CompressionSettings.Default with { Preset = CompressionPreset.Custom, JpegQuality = 55, MrcColors = 2 };
                Services.UserSettings.Update(s => s with { Compression = custom });
                c = new CompressViewModel(doc);
                Assert.Equal(custom, c.Settings());
                c.Dispose();
                Services.UserSettings.Update(s => s with { Compression = custom with { Preset = (CompressionPreset)9 } });
                c = new CompressViewModel(doc);
                Assert.Equal(CompressionPreset.EBook, c.SelectedPreset.Value);
                c.Dispose();
            }
            finally
            {
                Services.UserSettings.Update(s => s with { Compression = null, CompressWindow = null });
            }
        });
    }

    /// <summary>
    /// Methods per image kind: methods not yet available are greyed out and cannot be chosen, the palette sliders appear only with
    /// Palette (never those of another method), the info box shows the standard, common readers and PDF/A without naming products, and the method is remembered.
    /// See KONZEPT.md, section 5.12.
    /// </summary>
    [Fact]
    public void Methods_PerKind_InfoBox()
    {
        var pdf = TestFiles.Write(Photo(), "verfahren.pdf");
        UiSession.Run(async () =>
        {
            Services.UserSettings.Update(s => s with { Compression = null, CompressWindow = null });
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                CompressViewModel? compress = null;
                CompressWindow? dialog = null;
                vm.ShowCompress = c =>
                {
                    compress = c;
                    dialog = new CompressWindow(c);
                    _ = dialog.ShowDialog<string?>(window);
                    return Task.FromResult<string?>(null);
                };
                await vm.CompressCommand.ExecuteAsync(null);
                WaitUntil(window, () => compress is { IsReady: true });
                var c = compress!;
                var d = dialog!;

                Assert.Equal([true, false], c.PhotoMethods.Select(m => m.Available));
                Assert.Equal(Tr.T("JPEG XL") + " — " + Tr.T("geplant – die PDF-Norm dafür ist noch nicht veröffentlicht"), c.PhotoMethods[1].Label);   // JPEG 2000 is not offered

                // black and white: JBIG2 by default, G4 selectable
                Assert.Equal([true, true], c.BilevelMethods.Select(m => m.Available));
                Assert.Equal(BilevelMethod.Jbig2, c.Settings().BilevelCodec);
                c.SelectedBilevelMethod = c.BilevelMethods[0];
                Assert.Equal(BilevelMethod.G4, c.Settings().BilevelCodec);
                Assert.Equal(CompressionPreset.Custom, c.SelectedPreset.Value);
                c.SelectedPreset = c.Presets.First(p => p.Value == CompressionPreset.EBook);
                Assert.Same(c.BilevelMethods[1], c.SelectedBilevelMethod);

                // colour scans: MRC by default with E-book, only two sliders; resolutions and quality come from the preset
                Assert.True(c.IsMrc);
                Assert.Equal(ScanMethod.Mrc, c.Settings().Scans);
                Assert.Equal((600, 100, 60), (c.Settings().MrcTextDpi, c.Settings().MrcBackgroundDpi, c.Settings().MrcBackgroundQuality));
                Assert.True(c.MrcSmoothText);   // default 600 dpi, can be set to 300 by hand
                c.MrcSmoothText = false;
                Assert.Equal(300, c.Settings().MrcTextDpi);
                Assert.Equal(CompressionPreset.Custom, c.SelectedPreset.Value);
                c.MrcSmoothText = true;
                Assert.True(c.MrcFlattenPaper);   // smooth paper: on by default, can be switched off by hand
                c.MrcFlattenPaper = false;
                Assert.False(c.Settings().MrcFlattenPaper);
                c.MrcFlattenPaper = true;
                c.MrcColors = 2;
                c.MrcSensitivity = 70;
                Assert.Equal(CompressionPreset.Custom, c.SelectedPreset.Value);
                Assert.Equal((2, 70, 100), (c.Settings().MrcColors, c.Settings().MrcSensitivity, c.Settings().MrcBackgroundDpi));
                WaitUntil(d, () => c.IsReady);
                UiSession.Capture(d);
                Shoot(d, "85a-mrc");
                c.SelectedScanMethod = c.ScanMethods[0];
                Assert.False(c.IsMrc);
                Assert.Equal(ScanMethod.AsPhotos, c.Settings().Scans);
                c.SelectedPreset = c.Presets.First(p => p.Value == CompressionPreset.Print);
                Assert.False(c.IsMrc);
                c.SelectedPreset = c.Presets.First(p => p.Value == CompressionPreset.EBook);
                Assert.True(c.IsMrc);
                WaitUntil(d, () => c.IsReady);

                // Reset: everything changed, including Brotli and the methods → back to the default E-book
                c.Dpi = 300;
                c.Brotli = true;
                c.SelectedGraphicsMethod = c.GraphicsMethods[0];
                c.SelectedBilevelMethod = c.BilevelMethods[0];
                c.SelectedScanMethod = c.ScanMethods[0];
                c.MrcColors = 1;
                Assert.Equal(CompressionPreset.Custom, c.SelectedPreset.Value);
                Assert.NotEqual(CompressionSettings.Default, c.Settings());
                d.FindControl<Button>("ResetButton")!.Command!.Execute(null);
                Assert.Equal(CompressionSettings.Default, c.Settings());
                Assert.Equal(CompressionPreset.EBook, c.SelectedPreset.Value);
                Assert.Same(c.PackMethods[0], c.SelectedPackMethod);
                WaitUntil(d, () => c.IsReady);

                // Flate – strong: its own entry between Flate and Brotli, kept when the preset changes, and it does not make the file
                // larger
                var plain = c.After!;
                var plainSize = plain.Total;
                c.SelectedPackMethod = c.PackMethods[1];
                Assert.True(c.StrongFlate);
                Assert.False(c.Brotli);
                Assert.True(c.Settings().StrongFlate);
                Assert.Equal(CompressionPreset.EBook, c.SelectedPreset.Value);
                // the trial run started; wait until it is done — not for another report: SizeReport is a record, and on this small file
                // „stark“ may come out exactly as large as plain Flate (then the property keeps the equal value)
                Assert.False(c.IsReady);
                WaitUntil(d, () => c.IsReady);
                // a few bytes of leeway: the random file identifier is packed with the cross-reference stream, its length varies by a byte
                Assert.True(c.After!.Total <= plainSize + 8, $"{plainSize} → {c.After.Total}");
                c.SelectedPreset = c.Presets.Single(p => p.Value == CompressionPreset.Screen);
                Assert.True(c.Settings().StrongFlate);
                c.SelectedPackMethod = c.PackMethods[2];
                Assert.True(c.Brotli);
                Assert.False(c.StrongFlate);
                d.FindControl<Button>("ResetButton")!.Command!.Execute(null);
                Assert.Same(c.PackMethods[0], c.SelectedPackMethod);
                WaitUntil(d, () => c.IsReady);
                Assert.Equal([true, true], c.GraphicsMethods.Select(m => m.Available));
                Assert.True(c.IsPalette);   // default E-book

                // greyed out in the list and not selectable
                var photoBox = d.FindControl<ComboBox>("PhotoBox")!;
                photoBox.IsDropDownOpen = true;
                UiSession.Capture(d);
                Assert.False(photoBox.ContainerFromIndex(1)!.IsEnabled);
                Assert.True(photoBox.ContainerFromIndex(0)!.IsEnabled);
                Shoot(d, "85b-verfahren-liste");
                photoBox.IsDropDownOpen = false;
                c.SelectedPhotoMethod = c.PhotoMethods[1];
                Assert.Same(c.PhotoMethods[0], c.SelectedPhotoMethod);

                // Flate: no palette sliders; Palette: all of them
                c.SelectedGraphicsMethod = c.GraphicsMethods[0];
                Assert.Equal(CompressionPreset.Custom, c.SelectedPreset.Value);
                Assert.Equal(GraphicsMethod.Flate, c.Settings().Graphics);
                Assert.False(c.IsPalette);
                c.SelectedGraphicsMethod = c.GraphicsMethods[1];
                c.PaletteColors = 64;
                Assert.Equal(64, c.Settings().PaletteColors);
                WaitUntil(d, () => c.IsReady);

                // info box: description and readers with their support level
                var info = d.FindControl<Button>("GraphicsInfo")!;
                info.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                UiSession.Capture(d);
                Shoot(d, "85c-verfahren-infobox");
                var palette = c.SelectedGraphicsMethod;
                // three rows — the PDF standard, common readers, PDF/A — and no product names: a statement about one product could go stale
                // when its maker changes it (decided on 20.11.2026)
                Assert.Equal(3, palette.Viewers.Count);
                Assert.All(palette.Viewers, v => Assert.Equal(Support.Full, v.Support));
                Assert.Contains(CodecCatalog.Brotli.Viewers, v => v.Support == Support.None);
                string[] products = ["Acrobat", "Reader", "Edge", "Chrome", "Firefox", "MuPDF", "Sumatra", "PDF24", "PDFgear", "Xodo", "Vorschau", "Windows"];
                foreach (var method in new[] { CodecCatalog.Jpeg, CodecCatalog.JpegXl, CodecCatalog.Flate, CodecCatalog.Palette, CodecCatalog.Ccitt, CodecCatalog.Jbig2, CodecCatalog.Mrc, CodecCatalog.FlatePack, CodecCatalog.FlateStrong, CodecCatalog.Brotli })
                foreach (var text in method.Viewers.SelectMany(v => new[] { v.Viewer, v.Text }).Append(method.Description))
                    Assert.DoesNotContain(products, name => text.Contains(name, StringComparison.Ordinal));
            }
            finally
            {
                Services.UserSettings.Update(s => s with { Compression = null, CompressWindow = null });
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Marked redactions are never silently lost: before the window opens, the redact-first gate asks whether to redact now; afterwards
    /// the document is redacted and so is the compressed copy; the file itself stays untouched until it is saved.
    /// </summary>
    [Fact]
    public void Window_AppliesMarkedRedactions()
    {
        var pdf = TestFiles.Write(Photo(), "foto-geschwaerzt.pdf");
        var target = Path.Combine(Path.GetDirectoryName(pdf)!, "foto-geschwaerzt (komprimiert).pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                doc.AddElement(0, new Core.Pdf.Editing.RedactionElement { Rect = new Core.Pdf.Document.PdfRectangle(60, 190, 200, 230) }, Tr.T("Bereich schwärzen"));
                var asked = 0;
                vm.ConfirmRedaction = _ =>
                {
                    asked++;
                    return Task.FromResult(new RedactionReply(RedactionAnswer.Apply));
                };
                CompressViewModel? compress = null;
                Task<string?>? result = null;
                vm.ShowCompress = c =>
                {
                    compress = c;
                    result = new CompressWindow(c).ShowDialog<string?>(window);
                    return Task.FromResult<string?>(null);
                };
                await vm.CompressCommand.ExecuteAsync(null);
                WaitUntil(window, () => compress is { IsReady: true });
                Assert.Equal(1, asked);
                Assert.Equal(0, doc.RedactionCount);
                Assert.True(doc.HasAppliedRedactions);
                compress!.PickSavePath = (_, _, _) => Task.FromResult<string?>(target);
                await compress.SaveCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(60));
                await result!.WaitAsync(TimeSpan.FromSeconds(60));
                using var saved = PdfDocument.Open(File.ReadAllBytes(target));
                var list = Core.Pdf.Content.PageInterpreter.Build(saved, saved.Pages[0]);
                Assert.DoesNotContain("Foto", Core.Pdf.Text.PageText.Create(list.Text).Text);
                // the file itself is not redacted yet (only when saved)
                using var file = PdfDocument.Open(File.ReadAllBytes(pdf));
                Assert.Contains("Foto", Core.Pdf.Text.PageText.Create(Core.Pdf.Content.PageInterpreter.Build(file, file.Pages[0]).Text).Text);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// The open file as the target: it is saved as with "Save", and the document then shows the compressed file; otherwise the next
    /// Ctrl+S would write the uncompressed version back.
    /// </summary>
    [Fact]
    public void Window_SameFile_SavesTheOpenDocument()
    {
        var pdf = TestFiles.Write(Photo(), "foto-selbst.pdf");
        var length = new FileInfo(pdf).Length;
        UiSession.Run(async () =>
        {
            Services.UserSettings.Update(s => s with { Compression = null });
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                CompressViewModel? compress = null;
                vm.ShowCompress = c =>
                {
                    compress = c;
                    return new CompressWindow(c).ShowDialog<string?>(window);
                };
                var run = vm.CompressCommand.ExecuteAsync(null);
                WaitUntil(window, () => compress is { IsReady: true });
                var id = doc.History.Pages[0].Id;
                compress!.PickSavePath = (_, _, _) => Task.FromResult<string?>(pdf);
                await compress.SaveCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(60));
                await run.WaitAsync(TimeSpan.FromSeconds(60));
                Assert.NotNull(compress.ReplacesOpenFile);
                Assert.True(new FileInfo(pdf).Length < length / 2, $"{length} → {new FileInfo(pdf).Length}");
                Assert.Same(doc, vm.SelectedDocument);
                Assert.False(doc.IsModified);
                Assert.NotEqual(id, doc.History.Pages[0].Id);
            }
            finally
            {
                Services.UserSettings.Update(s => s with { Compression = null, CompressWindow = null });
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>A pending "Secure as image" also applies to the compressed copy: it contains no text.</summary>
    [Fact]
    public void Window_RasterPending_CopyHasNoText()
    {
        var pdf = TestFiles.Write(Photo(), "foto-alsbild.pdf");
        var target = Path.Combine(Path.GetDirectoryName(pdf)!, "foto-alsbild (komprimiert).pdf");
        UiSession.Run(async () =>
        {
            Services.UserSettings.Update(s => s with { Compression = null });
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                vm.SelectedDocument!.RasterOnSave = new Rendering.RasterSettings(150);
                CompressViewModel? compress = null;
                Task<string?>? result = null;
                vm.ShowCompress = c =>
                {
                    compress = c;
                    result = new CompressWindow(c).ShowDialog<string?>(window);
                    return Task.FromResult<string?>(null);
                };
                await vm.CompressCommand.ExecuteAsync(null);
                WaitUntil(window, () => compress is { IsReady: true });
                Assert.True(compress!.AsImages);
                compress.PickSavePath = (_, _, _) => Task.FromResult<string?>(target);
                await compress.SaveCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(60));
                await result!.WaitAsync(TimeSpan.FromSeconds(60));
                using var saved = PdfDocument.Open(File.ReadAllBytes(target));
                Assert.Empty(Core.Pdf.Content.PageInterpreter.Build(saved, saved.Pages[0]).Text);
                // the original stays as it is ("Secure as image" still pending)
                Assert.NotNull(vm.SelectedDocument.RasterOnSave);
            }
            finally
            {
                Services.UserSettings.Update(s => s with { Compression = null, CompressWindow = null });
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>"Save as" with the Screen compression preset: the new file is smaller, and the tab shows it.</summary>
    [Fact]
    public void SaveAs_WithCompression()
    {
        var pdf = TestFiles.Write(Photo(), "foto-unter.pdf");
        var target = Path.Combine(Path.GetDirectoryName(pdf)!, "foto-unter klein.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                vm.ShowBackstageCommand.Execute(BackstagePage.SaveAs);
                Assert.Null(vm.SelectedCompression.Settings);
                vm.SelectedCompression = vm.CompressionOptions.Single(o => o.Preset == CompressionPreset.Screen);
                Shoot(window, "87-speichern-unter-komprimierung");
                vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(target);
                var id = vm.SelectedDocument!.History.Pages[0].Id;
                await vm.SaveAsCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(60));
                Assert.True(new FileInfo(target).Length < new FileInfo(pdf).Length / 2);
                Assert.Equal(target, vm.SelectedDocument!.Path);
                // images re-encoded lossily: new page keys, otherwise the old tiles would still show the sharp original
                Assert.NotEqual(id, vm.SelectedDocument.History.Pages[0].Id);
            }
            finally
            {
                vm.SelectedCompression = vm.CompressionOptions[0];
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
}
