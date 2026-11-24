using PdfKnead.Core.Policy;
using PdfKnead.ViewModels;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// „FunktionManager öffnen“ in „Nutzungsberechtigung“: starts the FunktionManager beside the program, in its folder; where it is missing,
/// that is said and nothing is started. A function file beside the program is read at every start of PDF-Knead (no import needed on each
/// computer) — the same folder the FunktionManager reads it from at its own start.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class FunctionManagerUiTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OpenFunctionManager_StartsItBesideTheProgram_OrSaysItIsMissing(bool present)
    {
        var folder = Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"funktionmanager-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        if (present) File.WriteAllBytes(Path.Combine(folder, MainViewModel.FunctionManagerFile), [0x4D, 0x5A]);
        UiSession.Run(() =>
        {
            var vm = new MainViewModel { PolicyFolder = () => folder };
            System.Diagnostics.ProcessStartInfo? started = null;
            vm.StartProgram = info =>
            {
                started = info;
                return true;
            };
            vm.Settings.OpenFunctionManagerNowCommand.Execute(null);
            if (present)
            {
                Assert.NotNull(started);
                Assert.Equal(Path.Combine(folder, MainViewModel.FunctionManagerFile), started.FileName);
                Assert.Equal(folder, started.WorkingDirectory);
            }
            else
            {
                Assert.Null(started);
                Assert.Contains(MainViewModel.FunctionManagerFile, vm.Notice);
            }
            return Task.CompletedTask;
        });
    }

    /// <summary>A function file beside the program is found at the start without any import (what PDF-Knead does at every start).</summary>
    [Fact]
    public void FunctionFile_BesideTheProgram_ReadWithoutImport()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"funktionsdatei-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var policy = new FeaturePolicy { Organization = "Musterfirma", Denied = new HashSet<string>(["signature.digital"], StringComparer.Ordinal) };
        File.WriteAllBytes(Path.Combine(folder, FeaturePolicy.FileName), policy.ToJson());
        var load = FeaturePolicy.Load(folder);
        Assert.Equal(PolicyLoadState.Loaded, load.State);
        Assert.Equal("Musterfirma", load.Policy!.Organization);
        Assert.Contains("signature.digital", load.Policy.Denied);
    }
}
