using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfKnead.Core.Localization;

namespace PdfKnead.ViewModels;

/// <summary>
/// The choices of the first start (<see cref="Views.WelcomeWindow"/>): the language — preselected as the program started, that is the
/// system's language where a translation exists, otherwise English (German only on a German system) and switched at once —, the name for
/// the own certificate and whether to check for updates (off at first, decided on 18.11.2026).
/// </summary>
public sealed partial class WelcomeViewModel : ObservableObject
{
    /// <param name="languages">The languages to choose from.</param>
    /// <param name="name">A name already known for signatures; null if none.</param>
    /// <param name="askName">The own certificate is still to be offered.</param>
    /// <param name="askUpdates">The question about updates is still open (and the IT did not switch them off).</param>
    public WelcomeViewModel(IReadOnlyList<Language> languages, string? name, bool askName, bool askUpdates)
    {
        foreach (var language in languages) Languages.Add(language);
        Name = name ?? "";
        AskName = askName;
        AskUpdates = askUpdates;
    }

    /// <summary>The languages to choose from.</summary>
    public ObservableCollection<Language> Languages { get; } = [];

    /// <summary>The language: the current one; choosing another switches the program at once.</summary>
    public Language? SelectedLanguage
    {
        get => Languages.FirstOrDefault(l => l.Code == Tr.Current.Code);
        set
        {
            if (value is null || value.Code == Tr.Current.Code) return;
            Tr.Use(value);
            OnPropertyChanged();
        }
    }

    /// <summary>The name for the own certificate; empty: none is created now.</summary>
    [ObservableProperty]
    public partial string Name { get; set; }

    /// <summary>Whether the name is asked (no own certificate yet, not offered before).</summary>
    public bool AskName { get; }

    /// <summary>Whether the question about updates is shown.</summary>
    public bool AskUpdates { get; }

    /// <summary>Check for updates.</summary>
    [ObservableProperty]
    public partial bool UpdatesOn { get; set; }
}
