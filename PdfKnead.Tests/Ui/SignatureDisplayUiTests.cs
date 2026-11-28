using System.Security.Cryptography.X509Certificates;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Platform;
using PdfKnead.Services;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// Showing digital signatures (KONZEPT.md, section 5.17, step S4): the mark in the tab after opening (a click opens the „Signaturen“ window),
/// the hint at the first change that is not only signing, and a changed file shown as invalid.
/// </summary>
[Collection(nameof(UiCollection))]   // changes the shared settings (certificate, first start): not in parallel
public sealed class SignatureDisplayUiTests
{
    private static InkElement Signature(double left, bool digital = true) => new()
    {
        Rect = new PdfRectangle(left, 100, left + 200, 180),
        Strokes = [[0, 20, 40, 70, 90, 10, 140, 60, 190, 30]],
        StrokeWidth = 3,
        Color = 0x1B3A8C,
        Digital = digital ? new DigitalSigning("Max Mustermann · 25.10.2026 14:30") : null,
    };

    private static byte[] Signed()
    {
        using var certificate = SigningCertificates.CreateOwn("Max Mustermann", Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"anzeige-{Guid.NewGuid():N}.p12"));
        using var doc = PdfDocument.Open(TestPdf.Simple(pages: 2).Build());
        return PdfSigner.Sign(doc, 1, Signature(100), certificate, DateTimeOffset.Now);
    }

    [Fact]
    public void SignedFile_BarWindowAndHint()
    {
        var bytes = Signed();
        var pdf = TestFiles.Write(bytes, "unterschrieben.pdf");
        var changedBytes = bytes.ToArray();
        var at = changedBytes.AsSpan().IndexOf("Testdokument"u8);
        changedBytes[at + 4] = (byte)'X';
        var changed = TestFiles.Write(changedBytes, "veraendert.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
            window.Show();
            SignaturesViewModel? shown = null;
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                await doc.CheckSignaturesAsync();
                Assert.True(doc.HasSignatures);
                Assert.True(doc.SignaturesValid);
                Assert.True(doc.SignatureMarkGood);
                Assert.False(doc.SignatureMarkBad);
                Assert.StartsWith(doc.SignatureSummary, doc.SignatureMarkTip);
                Assert.Equal(Tr.T("Digital unterschrieben – {0}", Tr.T("{0} gültig", 1)) + " · " + Tr.T("Identität nicht bestätigt"), doc.SignatureSummary);
                Shoot(window, "71-signiert-reiter");

                // the window lists the signature; „Anzeigen“ goes to its page and closes
                vm.ShowSignatures = model =>
                {
                    shown = model;
                    var dialog = new SignaturesWindow(model);
                    dialog.Show();
                    Shoot(dialog, model.Rows.Count > 0 ? "72-signaturen" : "74-signaturen-keine");
                    dialog.Close();
                    return Task.CompletedTask;
                };
                // the mark in the tab opens the window for its document. Its command is run directly: a simulated click on the
                // 16-pixel mark missed now and then under the load of the whole test run (hit testing is the framework's job)
                UiSession.Capture(window);
                var mark = window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("signatureMark") && b.IsEffectivelyVisible);
                Assert.Same(doc, mark.CommandParameter);
                Assert.True(mark.Bounds.Width > 0);
                await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)mark.Command!).ExecuteAsync(mark.CommandParameter);
                var row = Assert.Single(shown!.Rows);
                Assert.Equal("Max Mustermann", row.Title);
                Assert.True(row.IsWarn);
                Assert.True(row.HasPage);
                shown.ShowPageCommand.Execute(row);

                // only a further digital signature: appended, so no hint
                vm.Message = "";
                doc.AddElement(0, Signature(300), Tr.T("Unterschrift einfügen"));
                Assert.Equal("", vm.Message);
                doc.History.Undo();

                // any other change: the hint once
                doc.AddElement(0, Signature(300, digital: false), Tr.T("Unterschrift einfügen"));
                Assert.Equal(Tr.T("Das Dokument ist digital unterschrieben: Speichern macht die Unterschrift ungültig. Mit „Speichern unter“ bleibt das unterschriebene Original erhalten."), vm.Message);
                vm.Message = "";
                doc.AddElement(0, new TextElement { Rect = new PdfRectangle(50, 700, 250, 720), Text = "Notiz" }, Tr.T("Text einfügen"));
                Assert.Equal("", vm.Message);

                // a changed byte: the mark says invalid
                await vm.OpenAsync([changed]);
                var other = vm.SelectedDocument!;
                await other.CheckSignaturesAsync();
                Assert.False(other.SignaturesValid);
                Assert.True(other.SignatureMarkBad);
                Assert.False(other.SignatureMarkGood);
                Assert.Equal(Tr.T("Digital unterschrieben – {0}", Tr.T("{0} ungültig", 1)), other.SignatureSummary);
                Shoot(window, "73-signiert-ungueltig");

                // a file without signatures: no mark, the window (from the ribbon) says so
                await vm.OpenAsync([TestFiles.Write(TestPdf.Simple(pages: 1).Build(), "ohne.pdf")]);
                Assert.False(vm.SelectedDocument!.HasSignatures);
                Assert.False(vm.SelectedDocument.SignatureMarkGood || vm.SelectedDocument.SignatureMarkBad);
                await vm.ShowSignatureDetailsCommand.ExecuteAsync(null);
                Assert.Equal(Tr.T("Dieses Dokument hat keine digitalen Signaturen."), shown!.EmptyText);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// The user's way: in the signature window tick „Digital signieren“ and „Einsetzen“ (clicks in the window), place the signature,
    /// save; afterwards the tab shows the green mark. The certificate is a file in the temporary folder.
    /// </summary>
    [Fact]
    public void SignatureWindow_TickDigital_SaveShowsGreenMark()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 1).Build(), "vertrag.pdf");
        var pfx = Path.Combine(Path.GetDirectoryName(pdf)!, "zertifikat.pfx");
        SigningCertificateTests.WriteOwnPfx("Max Mustermann", pfx, "geheim");
        var before = UserSettings.Current;
        UserSettings.Update(s => s with { SigningCertificate = new SigningCertificateChoice(SigningCertificateSource.File, Path: pfx), SignatureDigital = false });
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;

                var dialog = new SignatureWindow(new SignatureElement { Rect = default, Name = "Max Mustermann", FontFile = "PatrickHand-Regular.ttf" });
                var chosen = dialog.ShowDialog<PageElement?>(window);
                UiSession.Capture(dialog);
                var digital = dialog.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "DigitalBox");
                Assert.False(digital.IsChecked);
                var rights = dialog.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "RightsRow");
                Assert.False(rights.IsVisible);   // only with „Digital signieren“
                Click(dialog, digital);
                Assert.True(digital.IsChecked);
                Assert.True(rights.IsVisible);
                // certify: only filling in and signing afterwards
                dialog.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "RightsBox").SelectedIndex = 1;
                UiSession.Capture(dialog);
                Shoot(dialog, "75-unterschrift-digital");
                Click(dialog, dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "OkButton"));
                var signature = await chosen;
                Assert.NotNull(signature!.Digital);
                Assert.Equal(CertificationLevel.FormsAndSigning, signature.Digital!.Certify);
                Assert.False(signature.Digital.LockFields);
                Assert.Null(signature.Seal);
                Assert.True(UserSettings.Current.SignatureDigital);

                doc.AddElement(0, signature with { Rect = new PdfRectangle(100, 100, 300, 170) }, Tr.T("Unterschrift einfügen"));
                vm.AskPassword = (_, _) => Task.FromResult<string?>("geheim");
                // no time-stamping authority, whatever the settings say: with time stamps switched on (a service with a login), saving
                // would ask for its password in a window nobody answers, and without a login it would go to the network
                vm.Timestamps = () => null;
                vm.ConfirmFixing = (_, _, _) => Task.FromResult(FixingReply.Save);
                Assert.True(await vm.SaveDocumentAsync(doc, doc.Path, doc.SaveVersion));
                await doc.CheckSignaturesAsync();
                Assert.True(doc.SignatureMarkGood);
                Assert.Equal(CertificationLevel.FormsAndSigning, Assert.Single(doc.SignatureChecks).Certification);
                Assert.False(doc.CanCertify);   // signed now: a further signature cannot certify
                var next = new SignatureWindow(signature) { CanCertify = doc.CanCertify };
                Assert.Equal(0, next.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Name == "RightsBox").SelectedIndex);
                Assert.False(next.GetLogicalDescendants().OfType<ComboBoxItem>().Single(c => c.Name == "CertifyForms").IsEnabled);
                // the signed field is no longer tinted like a field still to be filled in
                Assert.True(PdfKnead.Controls.DocumentView.IsSignedSignature(doc.ElementsOn(0).OfType<ForeignAnnotation>().Single(f => f.Kind == "Widget")));
                UiSession.Capture(window);
                Shoot(window, "76-reiter-signiert");
            }
            finally
            {
                UserSettings.Update(s => s with { SigningCertificate = before.SigningCertificate, SignatureDigital = before.SignatureDigital, SignatureName = before.SignatureName });
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    private static void Click(Window window, Control control)
    {
        var at = control.TranslatePoint(new Point(Math.Min(10, control.Bounds.Width / 2), control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
    }

    private static void Shoot(Window window, string name)
    {
        var frame = UiSession.Capture(window);
        if (UiSession.ScreenshotFolder() is { } folder)
            frame.Save(Path.Combine(folder, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    /// <summary>
    /// One line per signature (docs/ABLAUFPLAN.md, 9.3.5, I-10): „Wer — Urteil · Art/Stufe · warum nicht“ for the cases of the table — valid with
    /// revocation status, an own certificate nobody vouches for, changed after signing, a changed byte; the row shows the same without who.
    /// </summary>
    [Fact]
    public void OneLine_PerCase()
    {
        using var ca = new LongTermValidationTests.TestCa();
        X509Certificate2Collection roots = [X509CertificateLoader.LoadCertificate(ca.Root.RawData)];
        SignatureCheck Check(byte[] file)
        {
            using var doc = PdfDocument.Open(file);
            return Assert.Single(SignatureVerifier.Verify(doc, null, roots, CancellationToken.None));
        }
        string Kind(SignatureCheck c) => SignatureTexts.Kind(c)!;

        byte[] valid;
        using (var doc = PdfDocument.Open(TestPdf.Simple(pages: 1).Build()))
        {
            var pages = PageItem.From(doc);
            var path = Path.Combine(Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pdf-knead-tests")).FullName, $"zeile-{Guid.NewGuid():N}.pdf");
            SignedSaving.Save(doc, [pages[0].WithElements([Signature(50)])], path, new Core.Pdf.Writing.PdfSaveOptions(), false, ca.Signer, DateTimeOffset.Now, revocation: ca);
            valid = File.ReadAllBytes(path);
            File.Delete(path);
        }
        var good = Check(valid);
        Assert.Equal(SignatureOutcome.Valid, good.Outcome);
        Assert.Equal($"Max Mustermann — {Tr.T("Gültig")} · {Kind(good)} · {Tr.T("Sperrstatus eingebettet")}, " +
                     Tr.T("unterschrieben am {0}", good.SigningTime!.Value.LocalDateTime.ToString("g", Tr.Culture)), SignatureTexts.OneLine(good));
        Assert.Equal(SignatureTexts.OneLine(good)["Max Mustermann — ".Length..], new SignatureRow(good).Line);

        var own = Check(Signed());
        Assert.Equal($"Max Mustermann — {Tr.T("Nicht bestätigt")} · {Kind(own)} · {Tr.T("Wem das Zertifikat gehört, bestätigt niemand.")}", SignatureTexts.OneLine(own));

        byte[] changed;
        using (var doc = PdfDocument.Open(valid))
        {
            var update = new Core.Pdf.Writing.IncrementalUpdate(doc);
            var content = ((Core.Pdf.Objects.PdfReference)doc.Pages[0].Dictionary.GetRaw(Core.Pdf.Objects.PdfName.Contents)!).Id;
            update.Replace(content, TestPdf.FlateStream("BT /F1 24 Tf 72 700 Td (Erika Mustermann) Tj ET"u8.ToArray()));
            using var output = new MemoryStream();
            update.WriteTo(output);
            changed = output.ToArray();
        }
        var later = Check(changed);
        Assert.Equal($"Max Mustermann — {Tr.T("Nach dem Unterschreiben geändert")} · {Kind(later)} · {SignatureTexts.Later(later)}", SignatureTexts.OneLine(later));

        var broken = (byte[])valid.Clone();
        var at = System.Text.Encoding.ASCII.GetString(broken).IndexOf("stream", StringComparison.Ordinal) + 12;
        broken[at] ^= 0x01;
        var invalid = Check(broken);
        Assert.Equal(SignatureOutcome.Invalid, invalid.Outcome);
        // the check stops before the certificate: the field names the signature, no level
        Assert.Null(SignatureTexts.Kind(invalid));
        Assert.Equal($"{invalid.FieldName} — {Tr.T("Ungültig")} · {invalid.Problem}", SignatureTexts.OneLine(invalid));
    }

    /// <summary>
    /// „Unterschreiben mit … [ändern]“ (docs/ABLAUFPLAN.md, 9.4.0, step 2; docs/FEHLERGEDAECHTNIS.md, No. 722): another certificate chosen without
    /// „als Standard merken“ holds for this signature only — the default stays; with it ticked, it becomes the default and the signature names none
    /// of its own.
    /// </summary>
    [Fact]
    public void SigningWith_ChosenForThisSignatureOnly_DefaultUnchanged()
    {
        var before = UserSettings.Current.SigningCertificate;
        var own = new SigningCertificateChoice(SigningCertificateSource.Own);
        var file = new SigningCertificateChoice(SigningCertificateSource.File, "C:/muster/firma.pfx", "AA", "Max Mustermann (Firma)");
        CertificateRow[] rows =
        [
            new(CertificateGroup.Own, "Max Mustermann", "auf diesem Rechner erzeugt", null, null, null, own),
            new(CertificateGroup.File, "Max Mustermann (Firma)", "C:/muster/firma.pfx", null, null, null, file),
        ];
        UserSettings.Update(s => s with { SigningCertificate = own });
        try
        {
            UiSession.Run(() =>
            {
                foreach (var remember in new[] { false, true })
                {
                    UserSettings.Update(s => s with { SigningCertificate = own });
                    var window = new SignatureWindow(new SignatureElement { Rect = default, Name = "Max Mustermann", FontFile = "PatrickHand-Regular.ttf" })
                    {
                        Certificates = rows,
                    };
                    window.Show();
                    try
                    {
                        window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "DigitalBox").IsChecked = true;
                        var shown = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "WithText");
                        Assert.StartsWith("Max Mustermann", shown.Text);
                        window.Choose(rows[1]);
                        Assert.StartsWith("Max Mustermann (Firma)", shown.Text);
                        var box = window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "RememberBox");
                        Assert.True(box.IsVisible);
                        box.IsChecked = remember;
                        window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "OkButton")
                            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                        var digital = window.LastResult?.Digital;
                        Assert.NotNull(digital);
                        if (remember)
                        {
                            Assert.Null(digital.With);
                            Assert.Equal(file, UserSettings.Current.SigningCertificate);
                        }
                        else
                        {
                            Assert.Equal(file, digital.With);
                            Assert.Equal(own, UserSettings.Current.SigningCertificate);
                        }
                    }
                    finally
                    {
                        if (window.IsVisible) window.Close();
                    }
                }
            });
        }
        finally
        {
            UserSettings.Update(s => s with { SigningCertificate = before });
        }
    }

    /// <summary>
    /// Several own certificates in „Unterschreiben mit …“ (docs/FEHLERGEDAECHTNIS.md, No. 733): the chosen one is named, not the first; another
    /// own one is a different certificate (offered to remember). Before every own certificate counted as the same, so the first was named.
    /// </summary>
    [Fact]
    public void SigningWith_SeveralOwn_TheChosenOneNamed()
    {
        var before = UserSettings.Current.SigningCertificate;
        var first = new SigningCertificateChoice(SigningCertificateSource.Own);
        var second = new SigningCertificateChoice(SigningCertificateSource.Own, "C:/muster/signatur-2.p12");
        CertificateRow[] rows =
        [
            new(CertificateGroup.Own, "Max Mustermann", "auf diesem Rechner erzeugt", null, null, null, first),
            new(CertificateGroup.Own, "Erika Mustermann", "auf diesem Rechner erzeugt", null, null, null, second),
        ];
        UserSettings.Update(s => s with { SigningCertificate = second });
        try
        {
            UiSession.Run(() =>
            {
                var window = new SignatureWindow(new SignatureElement { Rect = default, Name = "Erika Mustermann", FontFile = "PatrickHand-Regular.ttf" })
                {
                    Certificates = rows,
                };
                window.Show();
                try
                {
                    window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "DigitalBox").IsChecked = true;
                    var shown = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "WithText");
                    var box = window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "RememberBox");
                    Assert.StartsWith("Erika Mustermann", shown.Text);
                    Assert.False(box.IsVisible);
                    window.Choose(rows[0]);
                    Assert.StartsWith("Max Mustermann", shown.Text);
                    Assert.True(box.IsVisible);
                }
                finally
                {
                    window.Close();
                }
            });
        }
        finally
        {
            UserSettings.Update(s => s with { SigningCertificate = before });
        }
    }
}
