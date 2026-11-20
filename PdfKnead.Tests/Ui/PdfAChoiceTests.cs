using PdfKnead.Core.Pdf.Writing;
using PdfKnead.ViewModels;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// „Als PDF/A speichern“: every part written and checked once on opening, sorted into what this file can meet (left, the fitting one chosen) and
/// what it cannot (right, with the reason); a large file only until the first fitting part; a copy that does not conform saved only on request
/// (decided on 5.11.2026).
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class PdfAChoiceTests
{
    private static readonly PdfAViolation NoUnicode = new("6.2.11.7.2", "Ein Zeichen hat keine Unicode-Zuordnung.");

    /// <summary>A writer that meets every part except those named, and counts what it writes.</summary>
    private static (Func<PdfAConformance, PdfAAccessibility?, Task<PdfAResult>> Write, List<PdfAConformance> Written) Writer(params PdfAConformance[] failing)
    {
        var written = new List<PdfAConformance>();
        return ((part, _) =>
        {
            written.Add(part);
            return Task.FromResult(new PdfAResult(new byte[1234], failing.Contains(part) ? [NoUnicode] : []));
        }, written);
    }

    [Fact]
    public async Task Probe_SortsTheParts_AndChoosesTheFittingOne()
    {
        // text without Unicode: the u levels (and a, which includes u) do not fit; no attachment: 4f does not either — known at once
        var (write, written) = Writer(PdfAConformance.A2u, PdfAConformance.A3u, PdfAConformance.A2a, PdfAConformance.A3a, PdfAConformance.A1a);
        var vm = new PdfAViewModel(write, []);
        Assert.Contains(vm.NotPossible, o => o.Part == PdfAConformance.A4f && o.Note == Tr.T("PDF/A-4f verlangt mindestens einen Anhang (/EmbeddedFiles)."));
        Assert.All(vm.Possible, o => Assert.True(o.Waiting));

        await vm.ProbeAsync();

        Assert.DoesNotContain(PdfAConformance.A4f, written);   // (not written at all)
        Assert.Equal(10, written.Count);
        Assert.Equal(PdfAConformance.A2u, written[0]);          // the fitting parts first
        // both columns sorted by part and level (decided on 7.11.2026), whatever order they were probed in
        Assert.Equal([PdfAConformance.A1b, PdfAConformance.A2b, PdfAConformance.A3b, PdfAConformance.A4, PdfAConformance.A4e],
            vm.Possible.Select(o => o.Part).ToArray());
        Assert.Equal([PdfAConformance.A1a, PdfAConformance.A2a, PdfAConformance.A2u, PdfAConformance.A3a, PdfAConformance.A3u, PdfAConformance.A4f],
            vm.NotPossible.Select(o => o.Part).ToArray());
        Assert.All(vm.NotPossible.Where(o => o.Part != PdfAConformance.A4f), o => Assert.Equal(NoUnicode.Text, o.Note));
        Assert.Equal(PdfAConformance.A2b, vm.Recommended);
        Assert.Equal(PdfAConformance.A2b, vm.Part);
        Assert.True(vm.Options.Single(o => o.Part == PdfAConformance.A2b).IsRecommended);
        Assert.Equal(DocumentViewModel.FormatSize(1234), vm.Options.Single(o => o.Part == PdfAConformance.A4).Size);
        Assert.Equal(Tr.T("{0} von {1} Fassungen geprüft", 11, 11), vm.Progress);
    }

    /// <summary>
    /// The parts in the window by part and level — 1a, 1b, 2a, 2b, 2u, 3a, 3b, 3u, 4, 4e, 4f (decided on 7.11.2026; before: by use, 2b
    /// first, 1a last); PDF/A-2b stays chosen before anything is checked.
    /// </summary>
    [Fact]
    public void Options_SortedByPartAndLevel_2bChosen()
    {
        var (write, _) = Writer();
        var vm = new PdfAViewModel(write, []);
        Assert.Equal(["1a", "1b", "2a", "2b", "2u", "3a", "3b", "3u", "4", "4e", "4f"],
            vm.Options.Select(o => o.Part.Name()["PDF/A-".Length..]).ToArray());
        Assert.Equal(PdfAConformance.A2b, vm.Part);
        Assert.Single(vm.Options, o => o.IsSelected);
    }

    [Fact]
    public async Task Probe_WithAttachments_ProposesTheParts_ThatKeepThem()
    {
        var (write, _) = Writer();
        var vm = new PdfAViewModel(write, [], hasAttachments: true);
        await vm.ProbeAsync();
        Assert.Equal(PdfAConformance.A3u, vm.Recommended);
        Assert.Empty(vm.NotPossible);
    }

    /// <summary>
    /// A module's advice (e.g. for an e-invoice, decided on 15.11.2026): its sentence at the top, its part chosen at once and, the
    /// file meeting it, marked as fitting before the parts the program would propose; the others stay open to choose.
    /// </summary>
    [Fact]
    public async Task Probe_WithAModulesAdvice_ItsPartFirst_TheOthersOpen()
    {
        var (write, written) = Writer();
        var vm = new PdfAViewModel(write, [], hasAttachments: true, advice: new Modules.PdfAAdvice("Rat des Bausteins.", PdfAConformance.A3b));
        Assert.True(vm.HasAdvice);
        Assert.Equal("Rat des Bausteins.", vm.Advice);
        Assert.Equal(PdfAConformance.A3b, vm.Part);   // chosen before anything is checked
        await vm.ProbeAsync();
        Assert.Equal(PdfAConformance.A3b, written[0]);
        Assert.Equal(PdfAConformance.A3b, vm.Recommended);
        Assert.Equal(PdfAConformance.A3b, vm.Part);
        Assert.Contains(vm.Possible, o => o.Part == PdfAConformance.A2b);   // still to choose
        // without advice nothing at the top
        Assert.False(new PdfAViewModel(write, []).HasAdvice);
    }

    [Fact]
    public async Task Probe_TheUsersChoiceStays()
    {
        var (write, _) = Writer();
        var vm = new PdfAViewModel(write, []);
        vm.Options.Single(o => o.Part == PdfAConformance.A4).IsSelected = true;
        await vm.ProbeAsync();
        Assert.Equal(PdfAConformance.A2u, vm.Recommended);
        Assert.Equal(PdfAConformance.A4, vm.Part);
        Assert.Single(vm.Options, o => o.IsSelected);
    }

    [Fact]
    public async Task LargeFile_ProbedUntilTheFirstFittingPart_TheRestOnRequest()
    {
        var (write, written) = Writer(PdfAConformance.A2u);
        var vm = new PdfAViewModel(write, [], fileSize: PdfAViewModel.LargeFile);
        await vm.ProbeAsync();
        Assert.Equal([PdfAConformance.A2u, PdfAConformance.A2b], written);
        Assert.Equal(PdfAConformance.A2b, vm.Part);
        Assert.True(vm.PartlyProbed);
        Assert.Equal(Tr.T("nicht vorab geprüft (große Datei) — wird beim Speichern geprüft"), vm.Options.Single(o => o.Part == PdfAConformance.A4).Note);

        await vm.ProbeAllCommand.ExecuteAsync(null);
        Assert.Equal(10, written.Count);
        Assert.False(vm.PartlyProbed);
        // the copy of the fitting part is kept: saving it writes nothing again
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.Equal(10, written.Count);
        Assert.NotNull(vm.Result);
    }

    [Fact]
    public async Task NotConforming_SavedOnlyOnRequest()
    {
        var (write, _) = Writer(PdfAConformance.A2u);
        var vm = new PdfAViewModel(write, []);
        PdfAResult? ready = null;
        vm.Ready += r => ready = r;
        vm.Options.Single(o => o.Part == PdfAConformance.A2u).IsSelected = true;
        Assert.False(vm.SaveAnywayCommand.CanExecute(null));
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.True(vm.Failed);
        Assert.Null(ready);
        Assert.True(vm.SaveAnywayCommand.CanExecute(null));
        vm.SaveAnywayCommand.Execute(null);
        Assert.NotNull(ready);
        Assert.False(ready!.Conforms);
        // another part chosen: the copy of the old one is not saved under it
        vm.Options.Single(o => o.Part == PdfAConformance.A4).IsSelected = true;
        Assert.False(vm.SaveAnywayCommand.CanExecute(null));
    }
}
