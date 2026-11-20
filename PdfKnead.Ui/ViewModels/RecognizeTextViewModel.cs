using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfKnead.Core.Ocr;
using PdfKnead.Core.Pdf.Editing;

namespace PdfKnead.ViewModels;

/// <summary>Which pages the text recognition reads.</summary>
public enum RecognitionPages
{
    /// <summary>All pages.</summary>
    All,

    /// <summary>The page shown.</summary>
    Current,

    /// <summary>The pages typed in („1-3, 7“).</summary>
    Range,
}

/// <summary>
/// The window „Text erkennen“ (KONZEPT.md, section 5.9): which pages, which size of the recognition — small (the default) or medium, both come
/// with the program (decided on 13.11.2026). A size whose files are missing cannot be chosen; that means a damaged installation.
/// </summary>
public sealed partial class RecognizeTextViewModel : ObservableObject
{
    private readonly int _pageCount;
    private readonly int _currentPage;

    /// <summary>Creates the model.</summary>
    /// <param name="pageCount">Pages of the document.</param>
    /// <param name="currentPage">The page shown (0-based).</param>
    /// <param name="size">The size chosen last time.</param>
    public RecognizeTextViewModel(int pageCount, int currentPage, OcrModelSize size)
    {
        _pageCount = pageCount;
        _currentPage = Math.Clamp(currentPage, 0, Math.Max(0, pageCount - 1));
        RangeText = pageCount > 1 ? $"1-{pageCount}" : "1";
        MediumInstalled = OcrModels.IsInstalled(OcrModelSize.Medium);
        SmallInstalled = OcrModels.IsInstalled(OcrModelSize.Small);
        Size = Installed(size) ? size : SmallInstalled ? OcrModelSize.Small : MediumInstalled ? OcrModelSize.Medium : size;
    }

    /// <summary>Which pages.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error), nameof(CanStart))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial RecognitionPages Scope { get; set; }

    /// <summary>The pages for <see cref="RecognitionPages.Range"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error), nameof(CanStart))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial string RangeText { get; set; }

    /// <summary>The size.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMedium), nameof(IsSmall), nameof(CanStart))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial OcrModelSize Size { get; set; }

    /// <summary>Radio button „medium“.</summary>
    public bool IsMedium { get => Size == OcrModelSize.Medium; set { if (value) Size = OcrModelSize.Medium; } }

    /// <summary>Radio button „small“.</summary>
    public bool IsSmall { get => Size == OcrModelSize.Small; set { if (value) Size = OcrModelSize.Small; } }

    /// <summary>Radio button „Alle Seiten“.</summary>
    public bool IsAll { get => Scope == RecognitionPages.All; set { if (value) Scope = RecognitionPages.All; } }

    /// <summary>Radio button „Diese Seite“.</summary>
    public bool IsCurrent { get => Scope == RecognitionPages.Current; set { if (value) Scope = RecognitionPages.Current; } }

    /// <summary>Radio button „Seiten:“.</summary>
    public bool IsRange { get => Scope == RecognitionPages.Range; set { if (value) Scope = RecognitionPages.Range; } }

    /// <summary>„Diese Seite (3)“.</summary>
    public string CurrentText => Tr.T("Diese Seite ({0})", _currentPage + 1);

    /// <summary>Whether the size „medium“ is there.</summary>
    public bool MediumInstalled { get; }

    /// <summary>Whether the size „small“ is there.</summary>
    public bool SmallInstalled { get; }

    /// <summary>Whether any size is there.</summary>
    public bool HasModels => MediumInstalled || SmallInstalled;

    /// <summary>What is wrong with the input; empty if nothing.</summary>
    public string Error => !HasModels ? Tr.T("Die Modelle der Texterkennung fehlen im Ordner „ocr“ neben dem Programm — bitte PDF-Knead neu entpacken bzw. installieren.")
        : Scope == RecognitionPages.Range && PageRange.Parse(RangeText, _pageCount) is not { Count: > 0 } ? Tr.T("Seiten so angeben: 1-3, 7")
        : "";

    /// <summary>Whether it can start.</summary>
    public bool CanStart => Error.Length == 0 && Installed(Size);

    /// <summary>The pages chosen (0-based).</summary>
    public IReadOnlyList<int> Pages => Scope switch
    {
        RecognitionPages.Current => [_currentPage],
        RecognitionPages.Range => PageRange.Parse(RangeText, _pageCount) ?? [],
        _ => [.. Enumerable.Range(0, _pageCount)],
    };

    /// <summary>Set when the window is to close with „Erkennen“.</summary>
    public event Action? Started;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start() => Started?.Invoke();

    private bool Installed(OcrModelSize size) => size == OcrModelSize.Medium ? MediumInstalled : SmallInstalled;
}
