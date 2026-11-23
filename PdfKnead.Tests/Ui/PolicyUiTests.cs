using Avalonia.Controls;
using Avalonia.VisualTree;
using PdfKnead.Controls;
using PdfKnead.Core.Policy;
using PdfKnead.Services;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// The function file of an organization in the program (KONZEPT §5.27): it switches functions off — greyed out on the ribbon, refused by the
/// commands —; a damaged file switches everything off; without a file everything is allowed. Everybody can see it. Its kind of use is read and
/// passed over: PDF-Knead has been free for everyone since 21.11.2026 and asks for no kind of use.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class PolicyUiTests
{
    private static readonly PolicyLoad None = new(PolicyLoadState.NotThere, null, "");

    private static void Reset()
    {
        ActivePolicy.Use(None);
    }

    private static T Button<T>(Window window, string label) where T : Control =>
        window.GetVisualDescendants().OfType<T>().First(b => b switch
        {
            RibbonButton r => r.Label == Tr.T(label),
            RibbonToggleButton t => t.Label == Tr.T(label),
            _ => false,
        });

    [Fact]
    public void FunctionFile_SwitchesFunctionsOff()
    {
        UiSession.Run(async () =>
        {
            Reset();
            ActivePolicy.Use(new PolicyLoad(PolicyLoadState.Loaded, new FeaturePolicy
            {
                Usage = PolicyUsage.Authority,
                Organization = "Musteramt – IT",
                Denied = new HashSet<string> { "start.print", "form.all", "secure.remove-restrictions", "zukunft.unbekannt" },
            }, "berechtigungen.json"));
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                Assert.Contains("Musteramt – IT", vm.Settings.PolicySummary);

                // switched off: refused by the command, greyed out on the ribbon; the rest and unknown names stay allowed
                Assert.False(vm.EnsureAllowed("start.print"));
                Assert.True(vm.EnsureAllowed("start.save"));
                Assert.True(ActivePolicy.Allows("start.unbekannt"));
                Assert.False(Button<RibbonButton>(window, "Drucken").IsEnabled);
                vm.RibbonTab = RibbonTab.Secure;
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Assert.False(Button<RibbonButton>(window, "Einschränkungen entfernen").IsEnabled);
                Assert.Equal(MainViewModel.PolicyRefusal("secure.remove-restrictions"), ToolTip.GetTip(Button<RibbonButton>(window, "Einschränkungen entfernen")));
                vm.RibbonTab = RibbonTab.Form;
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Assert.False(Button<RibbonToggleButton>(window, "Formular bearbeiten").IsEffectivelyEnabled);
                vm.SetToolCommand.Execute(ViewTool.DesignForm);
                Assert.NotEqual(ViewTool.DesignForm, vm.Tool);

                // everybody can see what applies
                var permissions = new PolicyWindow();
                permissions.Show();
                var names = permissions.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
                Assert.Contains(Tr.T("Drucken"), names);
                Assert.Contains(MainViewModel.PolicySummary(), names);
                permissions.Close();
            }
            finally
            {
                window.Close();
                Reset();
            }
        });
    }

    [Fact]
    public void Import_LaysTheFileBesideTheProgram_AndStartsAnew()
    {
        UiSession.Run(async () =>
        {
            Reset();
            var folder = Directory.CreateTempSubdirectory("pdfknead-einspielen-ui-").FullName;
            try
            {
                var vm = new MainViewModel { PolicyFolder = () => folder };
                var restarts = new List<IReadOnlyList<string>>();
                var shutDowns = 0;
                vm.RestartProgram = files =>
                {
                    restarts.Add(files);
                    return true;
                };
                vm.ShutDownForUpdate = () => shutDowns++;

                // not a function file: nothing laid down, no restart
                var wrong = Path.Combine(folder, "falsch.json");
                File.WriteAllText(wrong, "{}");
                vm.PickPolicyFile = () => Task.FromResult<string?>(wrong);
                await vm.ImportPolicyAsync();
                Assert.False(File.Exists(Path.Combine(folder, FeaturePolicy.FileName)));
                Assert.Empty(restarts);

                // a function file: beside the program, then the program starts anew and this run ends
                var right = Path.Combine(folder, "von-der-it.json");
                File.WriteAllBytes(right, new FeaturePolicy { Denied = new HashSet<string> { "start.print" } }.ToJson());
                vm.PickPolicyFile = () => Task.FromResult<string?>(right);
                await vm.ImportPolicyAsync();
                Assert.True(File.Exists(Path.Combine(folder, FeaturePolicy.FileName)));
                Assert.Single(restarts);
                Assert.Equal(1, shutDowns);
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
                Reset();
            }
        });
    }

    [Fact]
    public void DamagedFile_SwitchesEveryFunctionOff_ButViewingWorks()
    {
        UiSession.Run(() =>
        {
            Reset();
            ActivePolicy.Use(new PolicyLoad(PolicyLoadState.Unreadable, null, "berechtigungen.json"));
            try
            {
                var vm = new MainViewModel();
                Assert.All(PolicyCatalog.All, f => Assert.False(ActivePolicy.Allows(f.Id)));
                Assert.False(vm.EnsureAllowed("start.save"));
                Assert.Contains(Tr.T("nicht lesbar"), MainViewModel.PolicySummary());
            }
            finally
            {
                Reset();
            }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void WithoutFile_EverythingAllowed()
    {
        UiSession.Run(() =>
        {
            Reset();
            try
            {
                var vm = new MainViewModel();
                Assert.All(PolicyCatalog.All, f => Assert.True(vm.EnsureAllowed(f.Id)));
                Assert.Equal(Tr.T("Keine Funktionsdatei: alle Funktionen sind erlaubt."), vm.Settings.PolicySummary);
            }
            finally
            {
                Reset();
            }
            return Task.CompletedTask;
        });
    }
}
