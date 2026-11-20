using Avalonia;
using System.Security.Cryptography.X509Certificates;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Platform;
using PdfKnead.Services;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;
using Avalonia.Headless;
using Avalonia.VisualTree;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// Signing digitally on saving (KONZEPT.md, section 5.17, step S3): the certificate from a file asks for its password, the first
/// signature is written with the document, a second one is only appended (no question, the first stays valid), and a signature
/// together with a change that rewrites the file asks the question about breaking the signature. The certificate is a file in the temporary
/// folder, so the tests never create one in the user's data folder.
/// </summary>
[Collection(nameof(UiCollection))]   // changes the shared settings (certificate, first start): not in parallel
public sealed class SigningUiTests
{
    private static InkElement Signature(double left) => new()
    {
        Rect = new PdfRectangle(left, 100, left + 200, 180),
        Strokes = [[0, 20, 40, 70, 90, 10, 140, 60, 190, 30]],
        StrokeWidth = 3,
        Color = 0x1B3A8C,
        Digital = new DigitalSigning("Max Mustermann · 25.10.2026 14:30"),
    };

    [Fact]
    public void Save_SignsThenAppends_FirstStaysValid()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 1).Build(), "vertrag.pdf");
        var pfx = Path.Combine(Path.GetDirectoryName(pdf)!, "zertifikat.pfx");
        SigningCertificateTests.WriteOwnPfx("Max Mustermann", pfx, "geheim");
        var before = UserSettings.Current.SigningCertificate;
        UserSettings.Update(s => s with { SigningCertificate = new SigningCertificateChoice(SigningCertificateSource.File, Path: pfx) });
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                var passwords = new List<bool>();
                vm.AskPassword = (_, wrong) =>
                {
                    passwords.Add(wrong);
                    return Task.FromResult<string?>(wrong ? "geheim" : "falsch");
                };
                var fixing = 0;
                vm.ConfirmFixing = (_, _, _) =>
                {
                    fixing++;
                    return Task.FromResult(FixingReply.Save);
                };
                var questions = 0;
                var fixedSignature = false;
                vm.ConfirmSignedSave = question =>
                {
                    questions++;
                    fixedSignature = question.FixedSignature;
                    return Task.FromResult(SignedSaveAnswer.Cancel);
                };

                // 1. first signature: password asked again after a wrong one, written with the document, nothing pending afterwards
                doc.AddElement(0, Signature(50), Tr.T("Unterschrift einfügen"));
                Assert.True(await vm.SaveDocumentAsync(doc, doc.Path, doc.SaveVersion));
                Assert.Equal([false, true], passwords);
                Assert.Equal(1, fixing);
                Assert.Equal(0, questions);
                Assert.False(doc.IsModified);
                Assert.DoesNotContain(doc.ElementsOn(0), e => e.Digital is not null);
                var once = File.ReadAllBytes(pdf);
                using (var saved = PdfDocument.Open(once))
                    Assert.Equal(1, DigitalSignatures.CountIntact(saved));

                // 2. a second signature is only appended: no question, the first signature stays valid
                doc.AddElement(0, Signature(300), Tr.T("Unterschrift einfügen"));
                Assert.True(doc.AppendsToSigned(null));
                Assert.True(await vm.SaveDocumentAsync(doc, doc.Path, doc.SaveVersion));
                Assert.Equal(0, questions);
                var twice = File.ReadAllBytes(pdf);
                Assert.Equal(once, twice.AsSpan(0, once.Length).ToArray());
                using (var saved = PdfDocument.Open(twice))
                    Assert.Equal(2, DigitalSignatures.CountIntact(saved));

                // 3. a signature together with a change that rewrites the file (a signature fixed into the page): the question comes,
                // cancelled nothing is written
                doc.AddElement(0, new SignatureElement { Rect = new PdfRectangle(50, 700, 250, 760), Name = "Max Mustermann", FontFile = "PatrickHand-Regular.ttf" },
                    Tr.T("Unterschrift einfügen"));
                doc.AddElement(0, Signature(300), Tr.T("Unterschrift einfügen"));
                Assert.False(doc.AppendsToSigned(null));
                Assert.False(await vm.SaveDocumentAsync(doc, doc.Path, doc.SaveVersion));
                Assert.Equal(1, questions);
                Assert.True(fixedSignature);   // the question offers „Digital signieren“ as the way out
                Assert.Equal(twice, File.ReadAllBytes(pdf));

                // 4. password dialog cancelled: nothing is written
                doc.History.Undo();
                doc.History.Undo();
                doc.AddElement(0, Signature(300), Tr.T("Unterschrift einfügen"));
                vm.AskPassword = (_, _) => Task.FromResult<string?>(null);
                Assert.False(await vm.SaveDocumentAsync(doc, doc.Path, doc.SaveVersion));
                Assert.Equal(twice, File.ReadAllBytes(pdf));
            }
            finally
            {
                UserSettings.Update(s => s with { SigningCertificate = before });
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Sign what you see (wish of 29.10.2026): a document with invisible text asks before signing — cancelled nothing is written, „Trotzdem
    /// signieren“ signs it as it is, „Erst bereinigen“ removes what was chosen in the cleanup window and then signs. Without anything hidden
    /// no question comes (the other tests here).
    /// </summary>
    [Fact]
    public void HiddenContent_AskedBeforeSigning_CleanFirstOrSignAnyway()
    {
        var source = TestPdf.Simple(pages: 1);
        source.Replace(4, TestPdf.FlateStream("BT /F1 12 Tf 72 700 Td (Sichtbar) Tj ET BT /F1 12 Tf 3 Tr 72 650 Td (UNSICHTBAR) Tj ET"u8.ToArray()));
        var pdf = TestFiles.Write(source.Build(), "verborgen-signieren.pdf");
        var pfx = Path.Combine(Path.GetDirectoryName(pdf)!, "zertifikat-verborgen.pfx");
        SigningCertificateTests.WriteOwnPfx("Max Mustermann", pfx, "geheim");
        var before = UserSettings.Current.SigningCertificate;
        UserSettings.Update(s => s with { SigningCertificate = new SigningCertificateChoice(SigningCertificateSource.File, Path: pfx) });
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.AskPassword = (_, _) => Task.FromResult<string?>("geheim");
                vm.ConfirmFixing = (_, _, _) => Task.FromResult(FixingReply.Save);
                var original = File.ReadAllBytes(pdf);
                var questions = new List<(string Heading, string Message)>();
                var answer = 0;
                vm.AskChoice = (heading, message, _, _) =>
                {
                    questions.Add((heading, message));
                    return Task.FromResult(answer);
                };
                var cleanups = 0;
                vm.PickCleanup = (found, _) =>
                {
                    cleanups++;
                    Assert.Contains(found, f => f.Item == Core.Pdf.Writing.CleanupItems.InvisibleText);
                    return Task.FromResult<Core.Pdf.Writing.CleanupItems?>(Core.Pdf.Writing.CleanupItems.InvisibleText);
                };
                string Text(byte[] data)
                {
                    using var saved = PdfDocument.Open(data);
                    return string.Concat(Core.Pdf.Content.PageInterpreter.Build(saved, saved.Pages[0]).Text.Select(g => g.Text));
                }

                // cancelled: nothing is written
                doc.AddElement(0, Signature(50), Tr.T("Unterschrift einfügen"));
                Assert.False(await vm.SaveDocumentAsync(doc, doc.Path, doc.SaveVersion));
                Assert.Equal(Tr.T("Das Dokument enthält Verborgenes"), Assert.Single(questions).Heading);
                Assert.Contains(Tr.T("Unsichtbarer Text (etwa die Texterkennung eines Scans)"), questions[0].Message);
                Assert.Equal(original, File.ReadAllBytes(pdf));

                // „Erst bereinigen“: the invisible text goes, then the document is signed
                answer = 1;
                Assert.True(await vm.SaveDocumentAsync(doc, doc.Path, doc.SaveVersion));
                Assert.Equal(1, cleanups);
                var cleaned = File.ReadAllBytes(pdf);
                Assert.DoesNotContain("UNSICHTBAR", Text(cleaned));
                Assert.Contains("Sichtbar", Text(cleaned));
                using (var saved = PdfDocument.Open(cleaned))
                    Assert.Equal(1, DigitalSignatures.CountIntact(saved));

                // nothing hidden any more: the next signature comes without a question
                doc.AddElement(0, Signature(300), Tr.T("Unterschrift einfügen"));
                Assert.True(await vm.SaveDocumentAsync(doc, doc.Path, doc.SaveVersion));
                Assert.Equal(2, questions.Count);

                // „Trotzdem signieren“ (another copy): signed as it is, the invisible text stays
                var other = TestFiles.Write(source.Build(), "verborgen-trotzdem.pdf");
                await vm.OpenAsync([other]);
                var second = vm.SelectedDocument!;
                second.AddElement(0, Signature(50), Tr.T("Unterschrift einfügen"));
                answer = 2;
                Assert.True(await vm.SaveDocumentAsync(second, second.Path, second.SaveVersion));
                Assert.Equal(3, questions.Count);
                Assert.Equal(1, cleanups);
                var signed = File.ReadAllBytes(other);
                Assert.Contains("UNSICHTBAR", Text(signed));
                using (var saved = PdfDocument.Open(signed))
                    Assert.Equal(1, DigitalSignatures.CountIntact(saved));
            }
            finally
            {
                UserSettings.Update(s => s with { SigningCertificate = before });
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// A signed form filled in and saved (KONZEPT.md, section 5.17, step S5): no hint at the change, no question when saving, the filling
    /// is appended and the signature stays valid; the mark in the tab stays green.
    /// </summary>
    [Fact]
    public void SignedForm_FilledAndSaved_SignatureStaysValid()
    {
        byte[] signed;
        using (var certificate = SigningCertificates.CreateOwn("Max Mustermann", Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"formular-{Guid.NewGuid():N}.p12")))
        using (var form = PdfDocument.Open(FormFillTests.AllKinds()))
            signed = PdfSigner.Sign(form, 0, Signature(350), certificate, DateTimeOffset.Now);
        var pdf = TestFiles.Write(signed, "formular.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                var questions = 0;
                vm.ConfirmSignedSave = _ =>
                {
                    questions++;
                    return Task.FromResult(SignedSaveAnswer.Cancel);
                };
                vm.Message = "";
                var name = doc.ElementsOn(0).OfType<ForeignAnnotation>().First(f => FormField.Of(f)?.Name == "Name");
                Assert.True(doc.ReplaceElement(new ElementRef(0, name), name with { Value = "Erika Mustermann" }, Tr.T("Feld ausfüllen")));
                Assert.Equal("", vm.Message);   // no hint: filling in is appended
                Assert.True(doc.AppendsToSigned(null));

                Assert.True(await vm.SaveDocumentAsync(doc, doc.Path, doc.SaveVersion));
                Assert.Equal(0, questions);
                var saved = File.ReadAllBytes(pdf);
                Assert.Equal(signed, saved.AsSpan(0, signed.Length).ToArray());
                await doc.CheckSignaturesAsync();
                Assert.True(doc.SignatureMarkGood);
                var check = Assert.Single(doc.SignatureChecks);
                Assert.Equal(LaterChanges.FormsAndSignatures, check.Later);
                Assert.Equal("Erika Mustermann", FormField.Of(doc.ElementsOn(0).OfType<ForeignAnnotation>().First(f => FormField.Of(f)?.Name == "Name"))!.StoredValue);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// A certificate file that cannot be opened (S7-12): after three wrong passwords the question stops with the reason; a file without
    /// a private key is reported at once instead of asking again and again.
    /// </summary>
    [Fact]
    public void CertificateFile_WrongPasswordsOrNoKey_StopsWithReason()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 1).Build(), "vertrag.pdf");
        var folder = Path.GetDirectoryName(pdf)!;
        var pfx = Path.Combine(folder, "zertifikat.pfx");
        var withoutKey = Path.Combine(folder, "ohne-schluessel.pfx");
        var raw = SigningCertificateTests.WriteOwnPfx("Max Mustermann", pfx, "geheim");
        using (var publicOnly = X509CertificateLoader.LoadCertificate(raw))
            File.WriteAllBytes(withoutKey, new X509Certificate2Collection { publicOnly }.ExportPkcs12(Pkcs12ExportPbeParameters.Pbes2Aes256Sha256, "geheim")!);
        var before = UserSettings.Current.SigningCertificate;
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                doc.AddElement(0, Signature(50), Tr.T("Unterschrift einfügen"));
                vm.ConfirmFixing = (_, _, _) => Task.FromResult(FixingReply.Save);

                UserSettings.Update(s => s with { SigningCertificate = new SigningCertificateChoice(SigningCertificateSource.File, Path: pfx) });
                var asked = 0;
                vm.AskPassword = (_, _) =>
                {
                    asked++;
                    return Task.FromResult<string?>("falsch");
                };
                Assert.False(await vm.SaveDocumentAsync(doc, doc.Path, doc.SaveVersion));
                Assert.Equal(MainViewModel.MaxPasswordAttempts, asked);
                Assert.Equal(Tr.T("Die Zertifikatsdatei lässt sich nicht öffnen: falsches Passwort, oder die Datei ist beschädigt."), vm.Message);

                UserSettings.Update(s => s with { SigningCertificate = new SigningCertificateChoice(SigningCertificateSource.File, Path: withoutKey) });
                asked = 0;
                vm.AskPassword = (_, _) =>
                {
                    asked++;
                    return Task.FromResult<string?>("geheim");
                };
                Assert.False(await vm.SaveDocumentAsync(doc, doc.Path, doc.SaveVersion));
                Assert.Equal(1, asked);
                Assert.Equal(Tr.T("In der Datei ist kein Zertifikat mit privatem Schlüssel."), vm.Message);
            }
            finally
            {
                UserSettings.Update(s => s with { SigningCertificate = before });
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// An encrypted document (S7-10, since 4.4 e signed): new protection and a digital signature in one save — written encrypted, signed in an
    /// encrypted update, the signature valid with the password (docs/FEHLERGEDAECHTNIS.md, No. 543; before refused). A file encrypted with
    /// RC4: refused with the reason before any certificate or password is asked for, „Digital signieren“ greyed out with that reason.
    /// </summary>
    [Fact]
    public void ProtectedDocument_SignedDigitally_Rc4RefusedBeforeAsking()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 1).Build(), "geschuetzt.pdf");
        var rc4 = TestFiles.Write(TestPdf.Simple(pages: 1).Build(cipher: Core.Pdf.Security.PdfCipher.Rc4, user: "",
            permissions: Core.Pdf.Security.PdfPermissions.All), "geschuetzt-rc4.pdf");
        using var ca = new LongTermValidationTests.TestCa();
        var file = Path.Combine(Path.GetDirectoryName(pdf)!, "muster-geschuetzt.p12");
        File.WriteAllBytes(file, ca.Pkcs12("geheim-2026"));
        var before = UserSettings.Current.SigningCertificate;
        UserSettings.Update(s => s with { SigningCertificate = new SigningCertificateChoice(SigningCertificateSource.File, Path: file) });
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
            window.Show();
            try
            {
                // new protection and a digital signature in one save: written encrypted, then signed in an encrypted update (No. 543)
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                doc.PendingProtection = new ProtectionChange(new Core.Pdf.Writing.PdfProtection("geheim", "besitzer", Core.Pdf.Security.PdfPermissions.Print));
                Assert.Null(doc.DigitalSigningRefusal);
                vm.ConfirmFixing = (_, _, _) => Task.FromResult(FixingReply.Save);
                doc.AddElement(0, Signature(50), Tr.T("Unterschrift einfügen"));
                vm.AskPassword = (_, _) => Task.FromResult<string?>("geheim-2026");
                Assert.True(await vm.SaveDocumentAsync(doc, doc.Path, doc.SaveVersion), vm.Message);
                using (var saved = PdfDocument.Open(File.ReadAllBytes(pdf), "geheim"))
                {
                    Assert.True(saved.IsEncrypted);
                    var check = Assert.Single(SignatureVerifier.Verify(saved, null, [X509CertificateLoader.LoadCertificate(ca.Root.RawData)], CancellationToken.None));
                    Assert.True(SignatureTexts.IsValid(check), check.Problem);
                }

                // RC4: refused before any certificate or password is asked for; the signature window offers „Digital signieren“ greyed out
                await vm.OpenAsync([rc4]);
                var old = vm.SelectedDocument!;
                var expected = Tr.T("Mit RC4 verschlüsselte Dateien lassen sich nicht signieren. Bitte zuerst mit neuem Schutz (AES) speichern.");
                Assert.Equal(expected, old.DigitalSigningRefusal);
                old.AddElement(0, Signature(50), Tr.T("Unterschrift einfügen"));
                var asked = 0;
                vm.AskPassword = (_, _) =>
                {
                    asked++;
                    return Task.FromResult<string?>(null);
                };
                Assert.False(await vm.SaveDocumentAsync(old, old.Path, old.SaveVersion));
                Assert.Equal(0, asked);
                Assert.Equal(expected, vm.Message);
                var dialog = new SignatureWindow(Signature(50)) { DigitalRefusal = expected };
                Assert.False(dialog.CanSignDigitally);
                Assert.Equal(expected, dialog.DigitalRefusal);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
                UserSettings.Update(s => s with { SigningCertificate = before });
            }
        });
    }

    /// <summary>
    /// Certification with several signatures (S7-9): „no changes“ together with a further signature is refused before saving; a digital
    /// signature chosen anew keeps its field and its level (it may certify, its own certification does not count against it).
    /// </summary>
    [Fact]
    public void Certification_NoChangesWithMore_Refused_ChosenAnewKeepsFieldAndLevel()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 1).Build(), "vertrag.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                var certifying = Signature(50) with { Digital = new DigitalSigning("Max Mustermann", "Kunde", CertificationLevel.NoChanges) };
                doc.AddElement(0, certifying, Tr.T("Unterschrift einfügen"));
                Assert.True(doc.CanCertifyWith(certifying));   // its own certification does not count
                Assert.False(doc.CanCertify);                  // a new one may not certify as well
                doc.AddElement(0, Signature(300), Tr.T("Unterschrift einfügen"));
                var asked = 0;
                vm.AskPassword = (_, _) =>
                {
                    asked++;
                    return Task.FromResult<string?>(null);
                };
                Assert.False(await vm.SaveDocumentAsync(doc, doc.Path, doc.SaveVersion));
                Assert.Equal(0, asked);
                Assert.Equal(Tr.T("Eine Unterschrift zertifiziert mit „keine Änderungen“ — weitere Unterschriften sind dann nicht möglich. Bitte eine andere Stufe wählen oder die übrigen Unterschriften entfernen."), vm.Message);

                // chosen anew in the signature window: field and level stay
                var dialog = new SignatureWindow(certifying) { CanCertify = doc.CanCertifyWith(certifying) };
                var chosen = dialog.ShowDialog<PageElement?>(window);
                UiSession.Capture(dialog);
                var ok = dialog.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Single(b => b.Name == "OkButton");
                var at = ok.TranslatePoint(new Avalonia.Point(10, ok.Bounds.Height / 2), dialog)!.Value;
                dialog.MouseDown(at, Avalonia.Input.MouseButton.Left);
                dialog.MouseUp(at, Avalonia.Input.MouseButton.Left);
                var again = await chosen;
                Assert.Equal("Kunde", again!.Digital!.FieldName);
                Assert.Equal(CertificationLevel.NoChanges, again.Digital.Certify);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// The own certificate with a password (KONZEPT.md, section 5.17, „Passwort“): asked once per session — again after a wrong one —, the
    /// signature is made with it; the second signature asks nothing; cancelled, nothing is written and no new certificate replaces it.
    /// </summary>
    [Fact]
    public void OwnCertificateWithPassword_AskedOncePerSession_NeverReplaced()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 1).Build(), "mit passwort.pdf");
        var own = Path.Combine(Path.GetDirectoryName(pdf)!, "eigen.p12");
        string fingerprint;
        using (var created = SigningCertificates.CreateOwn("Max Mustermann", own)) fingerprint = created.Fingerprint;
        Assert.True(SigningCertificates.SetOwnPassword(null, "geheim-123", own));
        var before = UserSettings.Current.SigningCertificate;
        UserSettings.Update(s => s with { SigningCertificate = new SigningCertificateChoice(SigningCertificateSource.Own) });
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel { OwnCertificateFile = own };
            var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                var asked = new List<bool>();
                vm.AskOwnCertificatePassword = wrong =>
                {
                    asked.Add(wrong);
                    return Task.FromResult<string?>(wrong ? "geheim-123" : "falsch-123");
                };
                vm.ConfirmFixing = (_, _, _) => Task.FromResult(FixingReply.Save);

                doc.AddElement(0, Signature(50), Tr.T("Unterschrift einfügen"));
                Assert.True(await vm.SaveDocumentAsync(doc, doc.Path, doc.SaveVersion));
                Assert.Equal([false, true], asked);
                using (var saved = PdfDocument.Open(File.ReadAllBytes(pdf)))
                {
                    Assert.Equal(1, DigitalSignatures.CountIntact(saved));
                    Assert.Equal(fingerprint, Assert.Single(SignatureVerifier.Verify(saved)).Certificate!.Fingerprint);
                }

                // the second signature of the session: not asked again
                doc.AddElement(0, Signature(300), Tr.T("Unterschrift einfügen"));
                Assert.True(await vm.SaveDocumentAsync(doc, doc.Path, doc.SaveVersion));
                Assert.Equal(2, asked.Count);

                // another session, cancelled: nothing written, the certificate stays the one with the password
                var other = new MainViewModel { OwnCertificateFile = own, AskOwnCertificatePassword = _ => Task.FromResult<string?>(null) };
                await other.OpenAsync([pdf]);
                var length = new FileInfo(pdf).Length;
                other.SelectedDocument!.AddElement(0, Signature(100), Tr.T("Unterschrift einfügen"));
                other.ConfirmFixing = (_, _, _) => Task.FromResult(FixingReply.Save);
                Assert.False(await other.SaveDocumentAsync(other.SelectedDocument, pdf, other.SelectedDocument.SaveVersion));
                Assert.Equal(length, new FileInfo(pdf).Length);
                Assert.Equal(OwnCertificateState.Locked, SigningCertificates.OwnState(own));
                using var shown = SigningCertificates.OwnPublic(own);
                Assert.Equal(fingerprint, shown!.Fingerprint);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
                UserSettings.Update(s => s with { SigningCertificate = before });
            }
        });
    }

    /// <summary>
    /// Several own certificates (decided on 11.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 733): the chosen one signs, each with its own
    /// password, each asked once per session — the password of one is never tried on the other.
    /// </summary>
    [Fact]
    public void SeveralOwnCertificates_TheChosenSigns_EachItsPassword()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 1).Build(), "mehrere eigene.pdf");
        var own = Path.Combine(Path.GetDirectoryName(pdf)!, "eigen.p12");
        string first, second;
        using (var created = SigningCertificates.CreateOwn("Max Mustermann", own)) first = created.Fingerprint;
        var secondFile = SigningCertificates.NextOwnFile(own);
        using (var created = SigningCertificates.CreateOwn("Erika Mustermann", secondFile)) second = created.Fingerprint;
        Assert.True(SigningCertificates.SetOwnPassword(null, "geheim-123", own));
        Assert.True(SigningCertificates.SetOwnPassword(null, "anders-123", secondFile));
        var before = UserSettings.Current.SigningCertificate;
        UserSettings.Update(s => s with { SigningCertificate = new SigningCertificateChoice(SigningCertificateSource.Own, secondFile) });
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel { OwnCertificateFile = own };
            var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                var asked = new List<string>();
                var answer = "anders-123";
                vm.AskOwnCertificatePassword = _ =>
                {
                    asked.Add(answer);
                    return Task.FromResult<string?>(answer);
                };
                vm.ConfirmFixing = (_, _, _) => Task.FromResult(FixingReply.Save);
                string Last()
                {
                    using var saved = PdfDocument.Open(File.ReadAllBytes(pdf));
                    return SignatureVerifier.Verify(saved).Last().Certificate!.Fingerprint;
                }

                // the chosen second one, with its password
                doc.AddElement(0, Signature(50), Tr.T("Unterschrift einfügen"));
                Assert.True(await vm.SaveDocumentAsync(doc, doc.Path, doc.SaveVersion));
                Assert.Single(asked);
                Assert.Equal(second, Last());

                // the first one chosen: its own password is asked, not the one of the second tried
                UserSettings.Update(s => s with { SigningCertificate = new SigningCertificateChoice(SigningCertificateSource.Own) });
                answer = "geheim-123";
                doc.AddElement(0, Signature(200), Tr.T("Unterschrift einfügen"));
                Assert.True(await vm.SaveDocumentAsync(doc, doc.Path, doc.SaveVersion));
                Assert.Equal(["anders-123", "geheim-123"], asked);
                Assert.Equal(first, Last());

                // back to the second one: its password is still known in this session
                UserSettings.Update(s => s with { SigningCertificate = new SigningCertificateChoice(SigningCertificateSource.Own, secondFile) });
                doc.AddElement(0, Signature(350), Tr.T("Unterschrift einfügen"));
                Assert.True(await vm.SaveDocumentAsync(doc, doc.Path, doc.SaveVersion));
                Assert.Equal(2, asked.Count);
                Assert.Equal(second, Last());
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
                UserSettings.Update(s => s with { SigningCertificate = before });
            }
        });
    }

    private sealed class Unreachable : ITimestampAuthority
    {
        public byte[] Request(byte[] request, CancellationToken cancel = default) => throw new TimestampException("Der Zeitstempeldienst ist nicht erreichbar (zeitstempel.example).");
    }

    /// <summary>
    /// Time stamps when saving (KONZEPT.md, section 5.17, „Zeitstempel“): with a service the signature carries its token; when it does not
    /// answer, the question — „Nicht speichern“ writes nothing, „Ohne Zeitstempel signieren“ signs without.
    /// </summary>
    [Fact]
    public void Timestamps_WithService_Stamped_Unreachable_Asked()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 1).Build(), "zeitstempel.pdf");
        var own = Path.Combine(Path.GetDirectoryName(pdf)!, "eigen.p12");
        using (SigningCertificates.CreateOwn("Max Mustermann", own)) { }
        var before = UserSettings.Current.SigningCertificate;
        UserSettings.Update(s => s with { SigningCertificate = new SigningCertificateChoice(SigningCertificateSource.Own) });
        UiSession.Run(async () =>
        {
            using var authority = new SignatureTimestampTests.TestAuthority();
            var vm = new MainViewModel { OwnCertificateFile = own, Timestamps = () => authority };
            var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.ConfirmFixing = (_, _, _) => Task.FromResult(FixingReply.Save);
                doc.AddElement(0, Signature(50), Tr.T("Unterschrift einfügen"));
                Assert.True(await vm.SaveDocumentAsync(doc, doc.Path, doc.SaveVersion));
                using (var saved = PdfDocument.Open(File.ReadAllBytes(pdf)))
                    Assert.Equal(SignatureTimestampTests.StampTime, Assert.Single(SignatureVerifier.Verify(saved)).Timestamp!.Time);

                // the service does not answer: asked with one answer besides „Abbrechen“ (a second would only repeat it); cancelled, nothing written
                vm.Timestamps = () => new Unreachable();
                var questions = new List<string>();
                vm.AskChoice = (heading, _, _, second) =>
                {
                    questions.Add(heading);
                    Assert.Null(second);
                    return Task.FromResult(0);
                };
                var length = new FileInfo(pdf).Length;
                doc.AddElement(0, Signature(300), Tr.T("Unterschrift einfügen"));
                Assert.False(await vm.SaveDocumentAsync(doc, doc.Path, doc.SaveVersion));
                Assert.Equal([Tr.T("Kein Zeitstempel")], questions);
                Assert.Equal(length, new FileInfo(pdf).Length);

                // „Ohne Zeitstempel signieren“: signed, the second signature without a token
                vm.AskChoice = (_, _, _, _) => Task.FromResult(1);
                Assert.True(await vm.SaveDocumentAsync(doc, doc.Path, doc.SaveVersion));
                using (var saved = PdfDocument.Open(File.ReadAllBytes(pdf)))
                {
                    var checks = SignatureVerifier.Verify(saved);
                    Assert.Equal(2, checks.Count);
                    Assert.NotNull(checks[0].Timestamp);
                    Assert.Null(checks[1].Timestamp);
                }
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
                UserSettings.Update(s => s with { SigningCertificate = before });
            }
        });
    }

    /// <summary>
    /// Long-term validation (KONZEPT.md, section 5.17, „Langzeitprüfung“) with a certificate from a file: the status is embedded; a revoked
    /// certificate signs nothing and says why; when nothing can be obtained, the file is signed and the message names the certificate.
    /// </summary>
    [Fact]
    public void LongTerm_Embedded_Revoked_Missing()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 1).Build(), "langzeit.pdf");
        using var ca = new LongTermValidationTests.TestCa();
        var file = Path.Combine(Path.GetDirectoryName(pdf)!, "muster.p12");
        File.WriteAllBytes(file, ca.Pkcs12("geheim-2026"));
        var before = UserSettings.Current.SigningCertificate;
        UserSettings.Update(s => s with { SigningCertificate = new SigningCertificateChoice(SigningCertificateSource.File, Path: file) });
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel { Revocation = () => ca };
            var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
            window.Show();
            vm.AskPassword = (_, _) => Task.FromResult<string?>("geheim-2026");   // after the window, which sets its own
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.ConfirmFixing = (_, _, _) => Task.FromResult(FixingReply.Save);
                doc.AddElement(0, Signature(50), Tr.T("Unterschrift einfügen"));
                Assert.True(await vm.SaveDocumentAsync(doc, doc.Path, doc.SaveVersion));
                Assert.True(string.IsNullOrEmpty(vm.Message), vm.Message);
                using (var saved = PdfDocument.Open(File.ReadAllBytes(pdf)))
                {
                    Assert.Single(LongTermValidation.Read(saved)!.Ocsps);
                    Assert.Equal(RevocationInfo.Coverage.Complete, Assert.Single(SignatureVerifier.Verify(saved)).LongTerm);
                }

                // revoked: not signed, the file stays as it was
                ca.OcspMode = "gesperrt";
                var length = new FileInfo(pdf).Length;
                doc.AddElement(0, Signature(300), Tr.T("Unterschrift einfügen"));
                Assert.False(await vm.SaveDocumentAsync(doc, doc.Path, doc.SaveVersion));
                Assert.StartsWith(Tr.T("Nicht unterschrieben: {0}", "").TrimEnd(), vm.Message);
                Assert.Equal(length, new FileInfo(pdf).Length);

                // nothing obtainable: signed, the message names the certificate
                ca.OcspMode = "keine";
                ca.CrlMode = "keine";
                vm.Message = "";
                Assert.True(await vm.SaveDocumentAsync(doc, doc.Path, doc.SaveVersion));
                Assert.Contains("Max Mustermann", vm.Message);
                using (var saved = PdfDocument.Open(File.ReadAllBytes(pdf)))
                    Assert.Equal(2, SignatureVerifier.Verify(saved).Count(SignatureTexts.IsValid));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
                UserSettings.Update(s => s with { SigningCertificate = before });
            }
        });
    }

    /// <summary>
    /// A time-stamping authority with a login (KONZEPT.md, section 5.17, „Zeitstempel“): the password is asked at the first signature of the
    /// session and kept; refused later (changed at the provider), it is asked again after „Nicht speichern“.
    /// </summary>
    [Fact]
    public void Timestamps_Login_AskedOncePerSession_RefusedAskedAgain()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 1).Build(), "anmeldung.pdf");
        var own = Path.Combine(Path.GetDirectoryName(pdf)!, "eigen.p12");
        using (SigningCertificates.CreateOwn("Max Mustermann", own)) { }
        var before = UserSettings.Current;
        UserSettings.Update(s => s with
        {
            SigningCertificate = new SigningCertificateChoice(SigningCertificateSource.Own),
            SignatureTimestamps = true,
            TimestampServer = "https://zeitstempel.example/tsa",
            TimestampUser = "max",
        });
        UiSession.Run(async () =>
        {
            using var authority = new SignatureTimestampTests.TestAuthority();
            const string accepted = "geheim";
            var handler = new SignatureTimestampTests.Answer(r =>
                r.Headers.Authorization?.Parameter == Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("max:" + accepted))
                    ? new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.ByteArrayContent(authority.Request(r.Content!.ReadAsByteArrayAsync().Result)) }
                    : new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized));
            var vm = new MainViewModel { OwnCertificateFile = own, TimestampHandler = handler };
            var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
            window.Show();
            var asked = new List<string>();
            vm.AskSessionPassword = (heading, _, _, _) =>
            {
                asked.Add(heading);
                return Task.FromResult<string?>(accepted);
            };
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.ConfirmFixing = (_, _, _) => Task.FromResult(FixingReply.Save);
                doc.AddElement(0, Signature(50), Tr.T("Unterschrift einfügen"));
                Assert.True(await vm.SaveDocumentAsync(doc, doc.Path, doc.SaveVersion));
                doc.AddElement(0, Signature(300), Tr.T("Unterschrift einfügen"));
                Assert.True(await vm.SaveDocumentAsync(doc, doc.Path, doc.SaveVersion));
                Assert.Equal([Tr.T("Zeitstempeldienst")], asked);   // once per session

                // the password changed at the provider: refused, „Nicht speichern“, asked again the next time
                var answer = "neu-2026";
                vm.AskSessionPassword = (heading, _, _, _) =>
                {
                    asked.Add(heading);
                    return Task.FromResult<string?>(answer);
                };
                var refusedOnce = false;
                vm.AskChoice = (heading, _, _, _) =>
                {
                    refusedOnce = true;
                    return Task.FromResult(2);
                };
                vm.TimestampHandler = new SignatureTimestampTests.Answer(r =>
                    !refusedOnce || r.Headers.Authorization?.Parameter != Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("max:neu-2026"))
                        ? new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
                        : new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.ByteArrayContent(authority.Request(r.Content!.ReadAsByteArrayAsync().Result)) });
                var length = new FileInfo(pdf).Length;
                doc.AddElement(0, Signature(150), Tr.T("Unterschrift einfügen"));
                Assert.False(await vm.SaveDocumentAsync(doc, doc.Path, doc.SaveVersion));
                Assert.True(refusedOnce);
                Assert.Equal(length, new FileInfo(pdf).Length);
                Assert.True(await vm.SaveDocumentAsync(doc, doc.Path, doc.SaveVersion));
                Assert.Equal([Tr.T("Zeitstempeldienst"), Tr.T("Zeitstempeldienst")], asked);
                using var saved = PdfDocument.Open(File.ReadAllBytes(pdf));
                var checks = SignatureVerifier.Verify(saved);
                Assert.Equal(3, checks.Count);
                Assert.All(checks, c => Assert.True(c.Timestamp?.Valid));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
                UserSettings.Update(_ => before);
            }
        });
    }

    /// <summary>
    /// A signature card (KONZEPT.md, section 5.17, „Signaturkarte“; SoftHSM as the card): the PIN is asked at the first signature, a wrong one
    /// again, then kept for the session; the signature is made on the card. Skipped without SoftHSM.
    /// </summary>
    [Fact]
    public void Card_PinAskedOnce_WrongAgain_SignedOnTheCard()
    {
        if (SmartCardTests.Available is not { } tokens) return;
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 1).Build(), "karte.pdf");
        var before = UserSettings.Current.SigningCertificate;
        UserSettings.Update(s => s with
        {
            SigningCertificate = new SigningCertificateChoice(SigningCertificateSource.Token, Path: tokens.Module, Fingerprint: SigningCertificate.FingerprintOf(tokens.Rsa)),
        });
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
            window.Show();
            var asked = new List<bool>();
            vm.AskSessionPassword = (heading, _, _, wrong) =>
            {
                Assert.Equal(Tr.T("Signaturkarte"), heading);
                asked.Add(wrong);
                return Task.FromResult<string?>(wrong ? SmartCardTests.Pin : "000000");
            };
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.ConfirmFixing = (_, _, _) => Task.FromResult(FixingReply.Save);
                doc.AddElement(0, Signature(50), Tr.T("Unterschrift einfügen"));
                Assert.True(await vm.SaveDocumentAsync(doc, doc.Path, doc.SaveVersion), vm.Message);
                Assert.Equal([false, true], asked);
                doc.AddElement(0, Signature(300), Tr.T("Unterschrift einfügen"));
                Assert.True(await vm.SaveDocumentAsync(doc, doc.Path, doc.SaveVersion), vm.Message);
                Assert.Equal(2, asked.Count);   // kept for the session
                using var saved = PdfDocument.Open(File.ReadAllBytes(pdf));
                var checks = SignatureVerifier.Verify(saved);
                Assert.Equal(2, checks.Count);
                Assert.All(checks, c => Assert.True(SignatureTexts.IsValid(c), c.Problem));
                Assert.All(checks, c => Assert.Equal("Max Mustermann", c.SignerName));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
                UserSettings.Update(s => s with { SigningCertificate = before });
            }
        });
    }

    /// <summary>
    /// Archive time stamps (KONZEPT.md, section 5.17, „Archiv-Zeitstempel“): signing appends validation data and a document time stamp; in
    /// „Dokument Signaturen“ a second one is added later — all signatures and time stamps stay valid.
    /// </summary>
    [Fact]
    public void Archive_AtSigning_AndRenewed_AllValid()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 1).Build(), "archiv.pdf");
        using var ca = new LongTermValidationTests.TestCa();
        var file = Path.Combine(Path.GetDirectoryName(pdf)!, "muster-archiv.p12");
        File.WriteAllBytes(file, ca.Pkcs12("geheim-2026"));
        var before = UserSettings.Current.SigningCertificate;
        UserSettings.Update(s => s with { SigningCertificate = new SigningCertificateChoice(SigningCertificateSource.File, Path: file) });
        UiSession.Run(async () =>
        {
            using var authority = new SignatureTimestampTests.TestAuthority();
            var vm = new MainViewModel { Revocation = () => ca, Timestamps = () => authority, ArchiveForTests = true };
            var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
            window.Show();
            vm.AskPassword = (_, _) => Task.FromResult<string?>("geheim-2026");
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.ConfirmFixing = (_, _, _) => Task.FromResult(FixingReply.Save);
                doc.AddElement(0, Signature(50), Tr.T("Unterschrift einfügen"));
                Assert.True(await vm.SaveDocumentAsync(doc, doc.Path, doc.SaveVersion), vm.Message);
                using (var saved = PdfDocument.Open(File.ReadAllBytes(pdf)))
                {
                    var checks = SignatureVerifier.Verify(saved, null, [X509CertificateLoader.LoadCertificate(ca.Root.RawData)], CancellationToken.None);
                    Assert.Equal(2, checks.Count);
                    Assert.Single(checks, c => c.IsDocumentTimestamp);
                    Assert.All(checks, c => Assert.True(SignatureTexts.IsValid(c), c.Problem));
                }

                Assert.True(await vm.AddArchiveTimestampAsync(doc), vm.Message);
                using (var renewed = PdfDocument.Open(File.ReadAllBytes(pdf)))
                {
                    var checks = SignatureVerifier.Verify(renewed, null, [X509CertificateLoader.LoadCertificate(ca.Root.RawData)], CancellationToken.None);
                    Assert.Equal(3, checks.Count);
                    Assert.Equal(2, checks.Count(c => c.IsDocumentTimestamp));
                    Assert.All(checks, c => Assert.True(SignatureTexts.IsValid(c), c.Problem));
                }
                Assert.Contains(doc.SignatureChecks, c => c.IsDocumentTimestamp);
                Assert.Contains(new SignatureRow(doc.SignatureChecks.First(c => c.IsDocumentTimestamp)).Title, Tr.T("Dokument-Zeitstempel von {0}", "Zeitstempeldienst Test"));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
                UserSettings.Update(s => s with { SigningCertificate = before });
            }
        });
    }

    /// <summary>
    /// A file changed after signing (page content replaced in a later revision): „Archiv-Zeitstempel …“ refuses with the reason and asks no
    /// service — a stamp would only make the broken file look renewed (TR-ESOR, Annex M.3, 4.7; decision D; docs/FEHLERGEDAECHTNIS.md,
    /// No. 713). Before it appended.
    /// </summary>
    [Fact]
    public void Archive_FileChangedAfterSigning_Refused()
    {
        using var ca = new LongTermValidationTests.TestCa();
        byte[] signed;
        using (var plain = PdfDocument.Open(TestPdf.Simple(pages: 1).Build()))
            signed = PdfSigner.Sign(plain, 0, Signature(50), ca.Signer, DateTimeOffset.Now);
        byte[] changed;
        using (var doc = PdfDocument.Open(signed))
        {
            var update = new IncrementalUpdate(doc);
            var content = ((PdfReference)doc.Pages[0].Dictionary.GetRaw(PdfName.Contents)!).Id;
            update.Replace(content, TestPdf.FlateStream("BT /F1 24 Tf 72 700 Td (Erika Mustermann) Tj ET"u8.ToArray()));
            using var output = new MemoryStream();
            update.WriteTo(output);
            changed = output.ToArray();
        }
        var pdf = TestFiles.Write(changed, "archiv-geaendert.pdf");
        UiSession.Run(async () =>
        {
            using var authority = new SignatureTimestampTests.TestAuthority();
            var vm = new MainViewModel { Revocation = () => ca, Timestamps = () => authority, ArchiveForTests = true };
            await vm.OpenAsync([pdf]);
            var doc = vm.SelectedDocument!;
            Assert.False(await vm.AddArchiveTimestampAsync(doc));
            Assert.Equal(Tr.T("Kein Archiv-Zeitstempel: er belegt nur den heutigen Zustand, und die Datei enthält Signaturen, die nicht gültig sind ({0}).",
                Assert.Single(doc.SignatureChecks).FieldName), vm.Message);
            Assert.Equal(0, authority.Requests);
            Assert.Equal(changed, File.ReadAllBytes(pdf));
        });
    }

    /// <summary>
    /// Archive time stamp switched on, long-term validation off (a setting from before, or no source given): the validation data come before the
    /// document time stamp all the same — B-LTA needs B-LT (EN 319 142-1, 6.3; docs/ABLAUFPLAN.md, 4.1, H1; docs/FEHLERGEDAECHTNIS.md,
    /// No. 527). Before the document time stamp followed the bare signature: the file looked archived and was B-T.
    /// </summary>
    [Fact]
    public void Archive_WithoutLongTerm_ValidationDataEmbedded()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 1).Build(), "archiv-ohne-langzeit.pdf");
        using var ca = new LongTermValidationTests.TestCa();
        var file = Path.Combine(Path.GetDirectoryName(pdf)!, "muster-archiv-ohne.p12");
        File.WriteAllBytes(file, ca.Pkcs12("geheim-2026"));
        var before = UserSettings.Current.SigningCertificate;
        UserSettings.Update(s => s with { SigningCertificate = new SigningCertificateChoice(SigningCertificateSource.File, Path: file) });
        UiSession.Run(async () =>
        {
            using var authority = new SignatureTimestampTests.TestAuthority();
            var vm = new MainViewModel { Revocation = () => null, ArchiveRevocation = () => ca, Timestamps = () => authority, ArchiveForTests = true };
            var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
            window.Show();
            vm.AskPassword = (_, _) => Task.FromResult<string?>("geheim-2026");
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.ConfirmFixing = (_, _, _) => Task.FromResult(FixingReply.Save);
                doc.AddElement(0, Signature(50), Tr.T("Unterschrift einfügen"));
                Assert.True(await vm.SaveDocumentAsync(doc, doc.Path, doc.SaveVersion), vm.Message);
                using var saved = PdfDocument.Open(File.ReadAllBytes(pdf));
                Assert.NotNull(LongTermValidation.Read(saved));
                var checks = SignatureVerifier.Verify(saved, null, [X509CertificateLoader.LoadCertificate(ca.Root.RawData)], CancellationToken.None);
                Assert.Single(checks, c => c.IsDocumentTimestamp);
                Assert.All(checks, c => Assert.True(SignatureTexts.IsValid(c), c.Problem));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
                UserSettings.Update(s => s with { SigningCertificate = before });
            }
        });
    }

    /// <summary>
    /// A certificate file whose certificate expired: saving with a signature says so before anything is asked or written, the file stays as it
    /// was (docs/ABLAUFPLAN.md, 4.1, H2; docs/FEHLERGEDAECHTNIS.md, No. 526). Before the signature was written and then judged invalid.
    /// </summary>
    [Fact]
    public void ExpiredFileCertificate_ToldBeforeSaving()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 1).Build(), "abgelaufen-signieren.pdf");
        var original = File.ReadAllBytes(pdf);
        var file = Path.Combine(Path.GetDirectoryName(pdf)!, "muster-abgelaufen.pfx");
        using (var key = System.Security.Cryptography.RSA.Create(2048))
        {
            var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=Max Mustermann", key,
                System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddYears(-2), DateTimeOffset.UtcNow.AddDays(-1));
            File.WriteAllBytes(file, created.Export(X509ContentType.Pfx, "geheim-2026"));
        }
        var before = UserSettings.Current.SigningCertificate;
        UserSettings.Update(s => s with { SigningCertificate = new SigningCertificateChoice(SigningCertificateSource.File, Path: file) });
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
            window.Show();
            vm.AskPassword = (_, _) => Task.FromResult<string?>("geheim-2026");
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.ConfirmFixing = (_, _, _) => Task.FromResult(FixingReply.Save);
                doc.AddElement(0, Signature(50), Tr.T("Unterschrift einfügen"));
                Assert.False(await vm.SaveDocumentAsync(doc, doc.Path, doc.SaveVersion));
                Assert.Contains(Tr.T("Mit diesem Zertifikat lässt sich nicht unterschreiben: {0}", ""), vm.Message);
                Assert.Equal(original, File.ReadAllBytes(pdf));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
                UserSettings.Update(s => s with { SigningCertificate = before });
            }
        });
    }
}
