using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using PdfKnead.Core.Platform;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>The „Zertifikat“ window: own certificate, file with password, the stored choice (KONZEPT.md, section 5.17, step S2).</summary>
[Collection(nameof(UiCollection))]   // changes the shared settings (certificate, first start): not in parallel
public sealed class CertificateUiTests
{
    private static string TempFile(string extension = "p12") =>
        Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"zertifikat-{Guid.NewGuid():N}.{extension}");

    [Fact]
    public void Own_CreatedOnRequest_NameAndFingerprintShown()
    {
        var file = TempFile();
        using var model = new CertificateViewModel(null, "Max Mustermann", file);
        Assert.False(model.HasCertificate);
        Assert.False(model.HasOwn);
        Assert.Equal(Tr.T("Wird beim ersten Signieren erzeugt"), model.SourceText);

        model.CreateOwnCommand.Execute(null);
        Assert.True(model.HasCertificate);
        Assert.True(model.HasOwn);
        Assert.False(model.IsError);
        Assert.Equal("Max Mustermann", model.Name);
        Assert.Equal(16, model.Fingerprint.Split(' ').Length);
        Assert.Equal(SigningCertificateSource.Own, model.Choice.Source);

        // reopened: the same certificate
        using var again = new CertificateViewModel(model.Choice, null, file);
        Assert.Equal(model.Fingerprint, again.Fingerprint);
    }

    /// <summary>The password of the own certificate in the window: set (twice the same, long enough), shown while locked, changed only with the old one, removed.</summary>
    [Fact]
    public void Own_Password_SetShownChangedRemoved()
    {
        var file = TempFile();
        using (var model = new CertificateViewModel(null, "Max Mustermann", file))
        {
            model.CreateOwnCommand.Execute(null);
            var fingerprint = model.Fingerprint;
            model.AskPasswordCommand.Execute(null);
            Assert.True(model.ChangingPassword);
            model.PasswordNew = "geheim-123";
            model.PasswordRepeat = "geheim-124";
            model.SavePasswordCommand.Execute(null);
            Assert.True(model.IsError);
            model.PasswordNew = "kurz";
            model.PasswordRepeat = "kurz";
            model.SavePasswordCommand.Execute(null);
            Assert.True(model.IsError);
            model.PasswordNew = model.PasswordRepeat = "geheim-123";
            model.SavePasswordCommand.Execute(null);
            Assert.False(model.IsError);
            Assert.True(model.HasPassword);
            Assert.False(model.ChangingPassword);
            Assert.True(model.OwnChanged);
            Assert.Equal(OwnCertificateState.Locked, SigningCertificates.OwnState(file));
            Assert.Equal(fingerprint, model.Fingerprint);
        }
        // reopened while locked: still shown with name and fingerprint
        using var again = new CertificateViewModel(null, null, file);
        Assert.True(again.HasOwn);
        Assert.True(again.HasPassword);
        Assert.Equal("Max Mustermann", again.Name);
        again.AskPasswordCommand.Execute(null);
        again.PasswordCurrent = "falsch-123";
        again.PasswordNew = again.PasswordRepeat = "anders-123";
        again.SavePasswordCommand.Execute(null);
        Assert.True(again.IsError);
        again.PasswordCurrent = "geheim-123";
        again.SavePasswordCommand.Execute(null);
        Assert.False(again.IsError);
        Assert.NotNull(SigningCertificates.LoadOwn(file, "anders-123"));
        again.AskPasswordCommand.Execute(null);
        again.PasswordCurrent = "anders-123";
        again.RemovePasswordCommand.Execute(null);
        Assert.False(again.HasPassword);
        Assert.Equal(OwnCertificateState.Ready, SigningCertificates.OwnState(file));
    }

    /// <summary>Time stamps in the window: an invalid address is said, „Dienst prüfen“ names who answers.</summary>
    [Fact]
    public async Task Timestamps_AddressChecked_ServiceAnswers()
    {
        using var authority = new PdfKnead.Tests.Pdf.SignatureTimestampTests.TestAuthority("Zeitstempel Musterdienst");
        using var model = new CertificateViewModel(null, null, TempFile(), timestamps: true, timestampService: new TimestampService("zeitstempel.example"))
        {
            OpenTimestampAuthority = _ => Task.FromResult<PdfKnead.Core.Pdf.Signatures.ITimestampAuthority?>(authority),
        };
        Assert.NotEmpty(model.TimestampHint);
        await model.CheckTimestampServerCommand.ExecuteAsync(null);
        Assert.True(model.IsError);
        model.TimestampServer = "https://zeitstempel.example/tsa";
        Assert.Empty(model.TimestampHint);
        await model.CheckTimestampServerCommand.ExecuteAsync(null);
        Assert.False(model.IsError);
        Assert.Contains("Zeitstempel Musterdienst", model.Message);
        Assert.Equal(1, authority.Requests);
    }

    /// <summary>
    /// „Bekannte Dienste …“: the suggested free service is entered but switched off; choosing an entry of the list in the window enters its
    /// address and switches time stamps on. A login over http is pointed out.
    /// </summary>
    [Fact]
    public void Timestamps_KnownServices_SuggestedButOff_LoginOnlyHttps()
    {
        var file = TempFile();
        UiSession.Run(() =>
        {
            using var model = new CertificateViewModel(null, "Max Mustermann", file);
            Assert.False(model.UseTimestamps);
            Assert.Equal(HttpTimestampAuthority.Suggested, model.TimestampServer);
            Assert.Empty(model.TimestampHint);
            var window = new CertificateWindow(model);
            window.Show();
            try
            {
                model.OptionsExpanded = true;   // the area „Optionen“ opened, as a user does
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                var button = window.GetVisualDescendants().OfType<Button>()
                    .Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == Tr.T("Bekannte Zeitstempeldienste"));
                var items = ((MenuFlyout)button.Flyout!).Items.OfType<MenuItem>().ToList();
                Assert.Equal(2, items.Count);
                foreach (var item in items)
                {
                    Assert.True(HttpTimestampAuthority.TryParse((string)item.CommandParameter!, out _));
                    model.UseTimestampServerCommand.Execute(item.CommandParameter);
                    Assert.Equal(item.CommandParameter, model.TimestampServer);
                }
                Assert.True(model.UseTimestamps);
                Assert.Empty(model.TimestampHint);
                model.TimestampUser = "max";
                Assert.Contains("https", model.TimestampHint);
                model.TimestampServer = "https://zeitstempel.example/tsa";
                Assert.Empty(model.TimestampHint);
                Assert.Equal(new TimestampService("https://zeitstempel.example/tsa", "max"), model.Timestamp);
            }
            finally
            {
                window.Close();
            }
            return Task.CompletedTask;
        });
    }

    /// <summary>„Signaturkarte oder Token“: the driver found, „Karte lesen“ lists the certificates without a PIN, „Verwenden“ chooses one.</summary>
    [Fact]
    public async Task Card_ReadAndChosen()
    {
        if (PdfKnead.Tests.Pdf.SmartCardTests.Available is not { } tokens) return;
        using var model = new CertificateViewModel(null, null, TempFile());
        model.AddCardModule(tokens.Module);
        await model.ReadCardCommand.ExecuteAsync(null);
        Assert.False(model.IsError, model.Message);
        var card = Assert.Single(model.CardCertificates, c => c.Certificate.Thumbprint == tokens.Rsa.Thumbprint);
        model.UseCardCommand.Execute(card);
        Assert.Equal(SigningCertificateSource.Token, model.Choice.Source);
        Assert.Equal(tokens.Module, model.Choice.Path);
        Assert.Equal("Max Mustermann", model.Name);
        Assert.Contains(Tr.T("PIN"), model.Message);
    }

    /// <summary>
    /// The list in groups (docs/ABLAUFPLAN.md, 9.4.0, step 1; docs/FEHLERGEDAECHTNIS.md, No. 721): exactly one default; two certificate files added
    /// stay on the list (without passwords) and either can become the default; one taken off the list; opened again the list is the same. With
    /// PDFKNEAD_ABLAGE a picture of the window (the Windows store not read — its certificates are the user's own).
    /// </summary>
    [Fact]
    public void List_OneDefault_SeveralFilesRemembered()
    {
        var first = TempFile("pfx");
        var second = TempFile("pfx");
        PdfKnead.Tests.Pdf.SigningCertificateTests.WriteOwnPfx("Erika Mustermann", first, "geheim");
        PdfKnead.Tests.Pdf.SigningCertificateTests.WriteOwnPfx("Max Mustermann (Firma)", second, "geheim");
        var own = TempFile();
        UiSession.Run(() =>
        {
            CertificateViewModel.NoStoreForTests = true;
            try
            {
                using var model = new CertificateViewModel(null, "Max Mustermann", own);
                model.CreateOwnCommand.Execute(null);
                Assert.Equal(CertificateGroup.Own, Assert.Single(model.Rows, r => r.IsDefault).Group);
                foreach (var file in new[] { first, second })
                {
                    model.FilePath = file;
                    model.FilePassword = "geheim";
                    model.LoadFileCommand.Execute(null);
                }
                Assert.Equal([first, second], model.Files.Select(f => f.Path));
                Assert.Equal(second, Assert.Single(model.Rows, r => r.IsDefault).Choice.Path);
                Assert.Equal(["Max Mustermann", "Erika Mustermann", "Max Mustermann (Firma)"], model.Rows.Select(r => r.Name));

                model.ChooseCommand.Execute(model.Rows.Single(r => r.Choice.Path == first));
                Assert.Equal((SigningCertificateSource.File, first), (model.Choice.Source, model.Choice.Path));
                Assert.Equal(first, Assert.Single(model.Rows, r => r.IsDefault).Choice.Path);

                // opened again with what was saved: the same list, the same default
                using (var again = new CertificateViewModel(model.Choice, null, own, files: model.Files))
                {
                    Assert.Equal(model.Rows.Select(r => (r.Name, r.IsDefault)), again.Rows.Select(r => (r.Name, r.IsDefault)));
                    if (Environment.GetEnvironmentVariable("PDFKNEAD_ABLAGE") is { } folder)
                    {
                        var window = new CertificateWindow(again);
                        window.Show();
                        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                        Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)?.Save(Path.Combine(folder, "zertifikate-liste.png"),
                            Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                        window.Close();
                    }
                }

                model.ForgetFileCommand.Execute(model.Rows.Single(r => r.Choice.Path == second));
                Assert.Equal([first], model.Files.Select(f => f.Path));
                Assert.True(File.Exists(second));   // only off the list
                Assert.All(model.Files, f => Assert.NotNull(f.Fingerprint));
            }
            finally
            {
                CertificateViewModel.NoStoreForTests = false;
            }
        });
    }

    /// <summary>
    /// Several own certificates (decided on 11.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 733): each its own row in its group, a new one is
    /// chosen and the others stay, a password belongs to one of them, deleting the chosen one makes the first remaining one the default.
    /// </summary>
    [Fact]
    public void Own_Several_EachItsRow_RemovingTheChosenFallsBack()
    {
        var file = TempFile();
        using var model = new CertificateViewModel(null, "Max Mustermann", file);
        Assert.True(model.OwnExpanded);   // none yet: open, to create one
        model.CreateOwnCommand.Execute(null);
        model.OwnName = "Erika Mustermann";
        model.CreateOwnCommand.Execute(null);
        Assert.Equal(["Max Mustermann", "Erika Mustermann"], model.OwnRows.Select(r => r.Name));
        Assert.Equal(Tr.T("Eigene Zertifikate") + " (2)", model.OwnHeader);
        var second = model.OwnRows[1].OwnFile!;
        Assert.NotEqual(file, second);
        Assert.Equal((SigningCertificateSource.Own, second), (model.Choice.Source, model.Choice.Path));
        Assert.True(model.OwnRows[1].IsDefault);
        Assert.All(model.OwnRows, r => Assert.NotNull(r.Remove));

        // the password of the first one only
        model.AskPasswordCommand.Execute(model.OwnRows[0]);
        Assert.Equal(Tr.T("Passwort für „{0}“", "Max Mustermann"), model.PasswordTitle);
        model.PasswordNew = model.PasswordRepeat = "geheim-123";
        model.SavePasswordCommand.Execute(null);
        Assert.False(model.IsError);
        Assert.Equal(OwnCertificateState.Locked, SigningCertificates.OwnState(file));
        Assert.Equal(OwnCertificateState.Ready, SigningCertificates.OwnState(second));
        Assert.Equal([true, false], model.OwnRows.Select(r => r.Locked));
        Assert.True(model.OwnRows[1].IsDefault);   // still the chosen one

        // opened again with what was saved: the second one is the default; a choice from before (without a path) means the first one
        using (var again = new CertificateViewModel(model.Choice, null, file))
            Assert.Equal("Erika Mustermann", Assert.Single(again.OwnRows, r => r.IsDefault).Name);
        using (var older = new CertificateViewModel(new SigningCertificateChoice(SigningCertificateSource.Own), null, file))
            Assert.Equal("Max Mustermann", Assert.Single(older.OwnRows, r => r.IsDefault).Name);

        // deleting the chosen one, after the question naming it: the first one becomes the default
        model.AskRemoveOwnCommand.Execute(model.OwnRows[1]);
        Assert.Contains("Erika Mustermann", model.RemoveQuestion);
        model.RemoveOwnCommand.Execute(null);
        Assert.False(File.Exists(second));
        Assert.True(File.Exists(file));
        Assert.True(Assert.Single(model.OwnRows).IsDefault);
        Assert.Equal("Max Mustermann", model.Name);
        Assert.Equal((SigningCertificateSource.Own, (string?)null), (model.Choice.Source, model.Choice.Path));
        Assert.Equal(Tr.T("Eigene Zertifikate") + " (1)", model.OwnHeader);
    }

    /// <summary>
    /// „Schließen“ with something not complete (decided on 11.11.2026; No. 733): the first click names what is missing and keeps the
    /// window, „Zurück“ hides it, completing it hides it as well, a second click closes („Trotzdem schließen“). Nothing missing: closes at once.
    /// </summary>
    [Fact]
    public void Close_WarnsFirst_WhatIsMissing()
    {
        using var model = new CertificateViewModel(null, "Max Mustermann", TempFile());
        model.CreateOwnCommand.Execute(null);
        Assert.False(model.HasIncomplete);
        Assert.True(model.TryClose());

        model.FilePath = TempFile("pfx");
        Assert.True(model.HasIncomplete);
        Assert.True(model.IncompleteHintShown);
        Assert.False(model.TryClose());
        Assert.True(model.WarningShown);
        Assert.False(model.IncompleteHintShown);   // the warning says it, the hint not twice
        Assert.Equal([Tr.T("Eine Zertifikatsdatei ist gewählt, aber noch nicht hinzugefügt („Hinzufügen“ mit ihrem Passwort).")], model.Incomplete);
        model.BackCommand.Execute(null);
        Assert.False(model.WarningShown);

        // completed (here: the picked file cleared): the warning goes by itself
        Assert.False(model.TryClose());
        model.FilePath = null;
        Assert.False(model.WarningShown);
        Assert.True(model.TryClose());

        // several things at once, each named; the second click closes
        model.UseTimestamps = true;
        model.TimestampServer = "kein Dienst";
        model.AskPasswordCommand.Execute(null);
        model.PasswordNew = "geheim-123";
        Assert.Equal(2, model.Incomplete.Count);
        Assert.StartsWith(Tr.T("Zeitstempel: {0}", ""), model.Incomplete[0]);
        Assert.Equal(Tr.T("Das neue Passwort des eigenen Zertifikats ist noch nicht festgelegt."), model.Incomplete[1]);
        Assert.False(model.TryClose());
        Assert.True(model.TryClose());
    }

    [Fact]
    public void Own_WithoutName_Error()
    {
        using var model = new CertificateViewModel(null, "  ", TempFile());
        model.CreateOwnCommand.Execute(null);
        Assert.True(model.IsError);
        Assert.False(model.HasCertificate);
    }

    [Fact]
    public void File_WrongPasswordRejected_RightOneChosen_PasswordNotKept()
    {
        var pfx = TempFile("pfx");
        PdfKnead.Tests.Pdf.SigningCertificateTests.WriteOwnPfx("Erika Mustermann", pfx, "geheim");
        using var model = new CertificateViewModel(null, null, TempFile()) { FilePath = pfx, FilePassword = "falsch" };
        model.LoadFileCommand.Execute(null);
        Assert.True(model.IsError);
        Assert.Equal(SigningCertificateSource.Own, model.Choice.Source);

        model.FilePassword = "geheim";
        model.LoadFileCommand.Execute(null);
        Assert.False(model.IsError);
        Assert.Equal("Erika Mustermann", model.Name);
        // the file is remembered with what the list shows (holder, valid until, level), never with the password (No. 721)
        Assert.Equal((SigningCertificateSource.File, pfx), (model.Choice.Source, model.Choice.Path));
        Assert.Null(model.FilePassword);

        // the stored choice knows the path and what the list shows, not the password: that is asked when signing; the holder is named
        using var reopened = new CertificateViewModel(model.Choice, null, TempFile());
        Assert.False(reopened.HasCertificate);
        Assert.Equal("Erika Mustermann", reopened.Name);
        // a choice from before the list (only the path): the file name, as before
        using var older = new CertificateViewModel(new SigningCertificateChoice(SigningCertificateSource.File, pfx), null, TempFile());
        Assert.Equal(Path.GetFileName(pfx), older.Name);
    }

    [Fact]
    public void Own_Removed_WithEarlierOnes_AfterQuestion()
    {
        var file = TempFile();
        using (SigningCertificates.CreateOwn("Max Mustermann", file)) { }
        using (SigningCertificates.CreateOwn("Max Mustermann", file)) { }   // the first one is moved aside
        var folder = Path.GetDirectoryName(file)!;
        Assert.Equal(2, Directory.GetFiles(folder, Path.GetFileName(file) + "*").Length);

        using var model = new CertificateViewModel(null, null, file);
        Assert.True(model.HasOwn);
        model.AskRemoveOwnCommand.Execute(null);
        Assert.True(model.ConfirmingRemove);
        model.KeepOwnCommand.Execute(null);
        Assert.False(model.ConfirmingRemove);
        Assert.True(File.Exists(file));

        model.AskRemoveOwnCommand.Execute(null);
        model.RemoveOwnCommand.Execute(null);
        Assert.False(model.HasOwn);
        Assert.False(model.HasCertificate);
        Assert.False(model.IsError);
        Assert.Equal(Tr.T("Wird beim ersten Signieren erzeugt"), model.SourceText);
        Assert.Empty(Directory.GetFiles(folder, Path.GetFileName(file) + "*"));
    }

    /// <summary>Exporting the own certificate: the password is checked first (length, repeated alike), then the file is written.</summary>
    [Fact]
    public async Task Own_Exported_AfterThePasswordIsChecked()
    {
        var file = TempFile();
        var target = TempFile();
        using (SigningCertificates.CreateOwn("Max Mustermann", file)) { }
        using var model = new CertificateViewModel(null, null, file) { PickExportFile = () => Task.FromResult<string?>(target) };
        model.AskExportCommand.Execute(null);
        Assert.True(model.Exporting);

        model.ExportPassword = "kurz";
        await model.ExportOwnCommand.ExecuteAsync(null);
        Assert.True(model.IsError);
        Assert.Equal(Tr.T("Das Passwort braucht mindestens {0} Zeichen.", SigningCertificates.MinExportPassword), model.Message);
        model.ExportPassword = "geheim-123";
        model.ExportRepeat = "geheim-124";
        await model.ExportOwnCommand.ExecuteAsync(null);
        Assert.Equal(Tr.T("Die beiden Passwörter stimmen nicht überein."), model.Message);
        Assert.False(File.Exists(target));

        model.ExportRepeat = "geheim-123";
        await model.ExportOwnCommand.ExecuteAsync(null);
        Assert.False(model.IsError);
        Assert.False(model.Exporting);
        Assert.Null(model.ExportPassword);   // not kept
        using var loaded = SigningCertificates.LoadFile(target, "geheim-123");
        Assert.Equal("Max Mustermann", loaded.Name);
    }

    [Fact]
    public void Window_Shows()
    {
        var file = TempFile();
        UiSession.Run(() =>
        {
            // never the certificates of the account in a picture
            CertificateViewModel.NoStoreForTests = true;
            using var model = new CertificateViewModel(null, "Max Mustermann", file);
            model.CreateOwnCommand.Execute(null);
            model.OwnName = "Erika Mustermann";
            model.CreateOwnCommand.Execute(null);
            var window = new CertificateWindow(model) { SizeToContent = SizeToContent.Manual, Height = 760 };
            window.Show();
            try
            {
                void Save(string name)
                {
                    var frame = UiSession.Capture(window);
                    if (UiSession.ScreenshotFolder() is { } folder) frame.Save(Path.Combine(folder, name), PngBitmapEncoderOptions.Default);
                }
                Save("70-zertifikat.png");
                model.AskExportCommand.Execute(model.OwnRows[0]);
                Save("70b-zertifikat-export.png");
                model.CancelExportCommand.Execute(null);
                model.TypesExpanded = true;
                model.OwnExpanded = false;
                Save("70c-zertifikat-arten.png");
                model.TypesExpanded = false;
                model.FilePath = TempFile("pfx");
                Assert.False(model.TryClose());
                Save("70d-zertifikat-unvollstaendig.png");
            }
            finally
            {
                window.Close();
                CertificateViewModel.NoStoreForTests = false;
            }
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// „Archiv-Zeitstempel“ needs the long-term validation (B-LTA needs B-LT, EN 319 142-1, 6.3): switching it on switches that on, switching
    /// that off takes the archive time stamp with it; a setting from before with only the archive time stamp opens with both (docs/ABLAUFPLAN.md,
    /// 4.1, H1; docs/FEHLERGEDAECHTNIS.md, No. 527). Before the archive time stamp followed the bare signature: the file looked archived, was B-T.
    /// </summary>
    [Fact]
    public void Archive_RequiresLongTerm()
    {
        using var model = new CertificateViewModel(null, "Max Mustermann", TempFile(), timestamps: true);
        Assert.False(model.UseLongTerm);
        model.UseArchive = true;
        Assert.True(model.UseLongTerm);
        model.UseLongTerm = false;
        Assert.False(model.UseArchive);
        using var old = new CertificateViewModel(null, "Max Mustermann", TempFile(), timestamps: true, longTerm: false, archive: true);
        Assert.True(old.UseArchive);
        Assert.True(old.UseLongTerm);
    }

    /// <summary>
    /// A certificate file whose certificate expired: the window shows it with the reason it cannot sign (docs/ABLAUFPLAN.md, 4.1, H2 and 8f (4);
    /// docs/FEHLERGEDAECHTNIS.md, No. 526); a usable one shows nothing.
    /// </summary>
    [Fact]
    public void ExpiredFile_ShownWithTheReason()
    {
        var pfx = TempFile("pfx");
        using (var key = RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=Erika Mustermann", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddYears(-2), DateTimeOffset.UtcNow.AddDays(-3));
            Directory.CreateDirectory(Path.GetDirectoryName(pfx)!);
            File.WriteAllBytes(pfx, created.Export(X509ContentType.Pfx, "geheim"));
        }
        using var model = new CertificateViewModel(null, null, TempFile()) { FilePath = pfx, FilePassword = "geheim" };
        model.LoadFileCommand.Execute(null);
        Assert.Equal("Erika Mustermann", model.Name);
        Assert.True(model.HasProblem);
        Assert.Contains(Tr.T("Mit diesem Zertifikat lässt sich nicht unterschreiben: {0}", ""), model.Problem);
        using var own = new CertificateViewModel(null, "Max Mustermann", TempFile());
        own.CreateOwnCommand.Execute(null);
        Assert.False(own.HasProblem);
    }
}
