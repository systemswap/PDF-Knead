using Avalonia.Controls;
using Avalonia.VisualTree;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>UI tests for removing restrictions: takes effect at once, in the file from the next save; without the owner password only after confirmation. See KONZEPT.md, section 5.11.</summary>
[Collection(nameof(UiCollection))]
public sealed class RestrictionsUiTests
{
    private static void Shoot(TopLevel window, string name)
    {
        var frame = UiSession.Capture(window);
        if (UiSession.ScreenshotFolder() is { } folder)
            frame.Save(Path.Combine(folder, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private static string Restricted(string name, string user) =>
        TestFiles.Write(TestPdf.Simple(2).Build(cipher: PdfCipher.Aes128, user: user, owner: "besitzer", permissions: PdfPermissions.Print, version: "1.6"), name);

    /// <summary>Opened with the open password: restrictions are lifted only after confirmation, then copying and page editing work at once; after saving, the same password opens the file with all permissions.</summary>
    [Fact]
    public void WithOpenPassword_ConfirmLiftsAtOnce_PasswordStays()
    {
        var pdf = Restricted("eingeschraenkt.pdf", "lesen");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                vm.AskPassword = (_, _) => Task.FromResult<string?>("lesen");
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                Assert.False(doc.CopyAllowed);
                Assert.False(vm.RotatePagesLeftCommand.CanExecute(null));
                vm.RibbonTab = RibbonTab.Secure;
                // allowed and locked both named (wish of 4.11.2026): in „Berechtigungen“ and in „Eigenschaften“
                Assert.False(doc.Rights.Single(r => r.Label == Tr.T("Kopieren")).Allowed);
                Assert.True(doc.Rights.Single(r => r.Label == Tr.T("Drucken")).Allowed);
                Assert.Contains(Tr.T("Kopieren"), doc.Properties.Single(p => p.Label == "Gesperrt").Value);
                Assert.Contains(Tr.T("Drucken"), doc.Properties.Single(p => p.Label == "Erlaubt").Value);
                Shoot(window, "88-sicher-einschraenkungen");
                Assert.True(vm.LiftRestrictionsCommand.CanExecute(null));

                // cancelled: nothing changes
                var asked = 0;
                vm.PickRestrictions = (name, locked, hasOpen, isOwner) =>
                {
                    asked++;
                    Assert.Equal("eingeschraenkt.pdf", name);
                    Assert.Contains(Tr.T("Kopieren"), locked);
                    Assert.DoesNotContain(Tr.T("Drucken"), locked.Split(", "));
                    Assert.True(hasOpen);
                    Assert.True(isOwner("besitzer"));
                    Assert.False(isOwner("lesen"));
                    return Task.FromResult<RestrictionsReply?>(null);
                };
                await vm.LiftRestrictionsCommand.ExecuteAsync(null);
                Assert.Equal(1, asked);
                Assert.False(doc.CopyAllowed);
                Assert.False(doc.IsModified);

                vm.PickRestrictions = (_, _, _, _) => Task.FromResult<RestrictionsReply?>(new RestrictionsReply(KeepOpenPassword: true));
                await vm.LiftRestrictionsCommand.ExecuteAsync(null);
                Assert.True(doc.CopyAllowed);
                Assert.True(doc.CanEditPages);
                Assert.True(doc.IsModified);
                Assert.True(vm.RotatePagesLeftCommand.CanExecute(null));
                Assert.False(vm.LiftRestrictionsCommand.CanExecute(null));
                Assert.Contains(Tr.T("Einschränkungen entfernt — in der Datei ab dem nächsten Speichern."), doc.Properties.Select(p => p.Value));
                Assert.All(doc.Rights, r => Assert.True(r.Allowed));

                await vm.SaveCommand.ExecuteAsync(null);
                Assert.False(doc.IsModified);
                Assert.True(doc.CopyAllowed);   // reopened: the file itself now permits everything
                Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(pdf));
                using var reopened = PdfDocument.Open(pdf, "lesen");
                Assert.Equal(PdfPermissions.All, reopened.Security!.Permissions & PdfPermissions.All);
                using var owner = PdfDocument.Open(pdf, "besitzer");
                Assert.True(owner.Security!.IsOwner);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>Opened with the owner password: no confirmation is asked; "Also remove the open password" saves without protection.</summary>
    [Fact]
    public void AsOwner_NoQuestion_RemovingOpenPasswordSavesPlain()
    {
        var owned = Restricted("besitzer.pdf", "lesen");
        var plain = Restricted("ohne-passwort.pdf", "lesen");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                vm.PickRestrictions = (_, _, _, _) => throw new InvalidOperationException("mit dem Rechte-Passwort keine Rückfrage");
                vm.AskPassword = (_, _) => Task.FromResult<string?>("besitzer");
                await vm.OpenAsync([owned]);
                Assert.True(vm.LiftRestrictionsCommand.CanExecute(null));   // the file itself is still restricted
                await vm.LiftRestrictionsCommand.ExecuteAsync(null);
                await vm.SaveCommand.ExecuteAsync(null);
                using (var user = PdfDocument.Open(owned, "lesen"))
                    Assert.Equal(PdfPermissions.All, user.Security!.Permissions & PdfPermissions.All);

                vm.AskPassword = (_, _) => Task.FromResult<string?>("lesen");
                await vm.OpenAsync([plain]);
                vm.PickRestrictions = (_, _, _, _) => Task.FromResult<RestrictionsReply?>(new RestrictionsReply(KeepOpenPassword: false));
                await vm.LiftRestrictionsCommand.ExecuteAsync(null);
                await vm.SaveCommand.ExecuteAsync(null);
                using var open = PdfDocument.Open(plain);
                Assert.Null(open.Security);
                Assert.Equal(2, open.PageCount);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>Permissions only, no open password: after confirmation the file is saved without encryption.</summary>
    [Fact]
    public void RightsOnly_SavesWithoutEncryption()
    {
        var pdf = Restricted("nur-rechte.pdf", "");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                vm.PickRestrictions = (_, _, hasOpen, _) =>
                {
                    Assert.False(hasOpen);
                    return Task.FromResult<RestrictionsReply?>(new RestrictionsReply(KeepOpenPassword: true));
                };
                await vm.LiftRestrictionsCommand.ExecuteAsync(null);
                await vm.SaveCommand.ExecuteAsync(null);
                using var open = PdfDocument.Open(pdf);
                Assert.Null(open.Security);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>The dialog: "Remove" is enabled only with confirmation or the correct owner password; the open-password check box appears only if there is one.</summary>
    [Fact]
    public void Window_NeedsConsentOrOwnerPassword()
    {
        UiSession.Run(() =>
        {
            var window = new RestrictionsWindow("datei.pdf", "Kopieren, Ändern", true, pw => pw == "besitzer");
            window.Show();
            try
            {
                Shoot(window, "88b-einschraenkungen-fenster");
                CheckBox Check(string name) => window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == name);
                var password = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "OwnerPassword");
                var lift = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "LiftButton");
                Assert.True(Check("RemoveOpenBox").IsVisible);
                Assert.False(window.CanLift);
                Assert.False(lift.IsEnabled);

                password.Text = "falsch";
                Assert.False(window.CanLift);
                password.Text = "besitzer";
                Assert.True(window.CanLift);
                Assert.False(Check("ConsentBox").IsEnabled);
                Shoot(window, "88c-einschraenkungen-rechte-passwort");

                password.Text = "";
                Check("ConsentBox").IsChecked = true;
                Assert.True(window.CanLift);
                Assert.True(lift.IsEnabled);
                Shoot(window, "88d-einschraenkungen-bestaetigt");
            }
            finally
            {
                window.Close();
            }
        });

        UiSession.Run(() =>
        {
            var window = new RestrictionsWindow("datei.pdf", "Kopieren", false, _ => false);
            window.Show();
            try
            {
                Assert.False(window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "RemoveOpenBox").IsVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
