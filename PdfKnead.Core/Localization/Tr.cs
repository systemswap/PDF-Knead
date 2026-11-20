using System.ComponentModel;
using System.Globalization;

namespace PdfKnead.Core.Localization;

/// <summary>
/// Translation of the user interface. The German text is also the key
/// (as with gettext): the code contains <c>Tr.T("PDF hierher ziehen")</c>, XAML
/// <c>{l:T 'PDF hierher ziehen'}</c>. German therefore needs no table,
/// English is defined in code (<see cref="English"/>), further languages come as
/// JSON files from the folder <c>languages</c> next to the program.
///
/// If a text is missing in a language, English applies, then the German text.
/// When the language is switched, <see cref="Version"/> reports a change; the
/// bindings of the user interface depend on it, so the texts change immediately.
/// </summary>
public sealed class Tr : INotifyPropertyChanged
{
    /// <summary>Gets the single instance, used as the binding source for <see cref="Version"/>.</summary>
    public static Tr Instance { get; } = new();

    private Language _current = Language.German;

    /// <summary>Raised for <see cref="Version"/> when the language is switched.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets a counter that increases with every language switch (trigger for bindings).</summary>
    public int Version { get; private set; }

    /// <summary>Gets the current language (German until <see cref="Use"/> is called).</summary>
    public static Language Current => Instance._current;

    /// <summary>Gets the culture of the language for dates and numbers; JSON languages via their code.</summary>
    /// <remarks>Falls back to the invariant culture if .NET does not know the language code.</remarks>
    public static CultureInfo Culture => Instance._culture;
    private CultureInfo _culture = CultureInfo.GetCultureInfo("de");

    /// <summary>Switches the user interface language.</summary>
    /// <param name="language">The language to use; nothing happens if it is already the current instance.</param>
    /// <remarks>Raises <see cref="PropertyChanged"/> synchronously on the calling thread.</remarks>
    public static void Use(Language language)
    {
        var tr = Instance;
        if (ReferenceEquals(tr._current, language)) return;
        tr._current = language;
        try { tr._culture = CultureInfo.GetCultureInfo(language.Code); }
        catch (CultureNotFoundException) { tr._culture = CultureInfo.InvariantCulture; }
        tr.Version++;
        tr.PropertyChanged?.Invoke(tr, new PropertyChangedEventArgs(nameof(Version)));
    }

    /// <summary>Returns a text in the selected language.</summary>
    /// <param name="german">The German text, which serves as the key.</param>
    /// <returns>The non-empty translation of the current language; otherwise the English text (unless the language is German); otherwise <paramref name="german"/>.</returns>
    public static string T(string german)
    {
        var lang = Instance._current;
        if (lang.Texts.TryGetValue(german, out var text) && !string.IsNullOrEmpty(text)) return text;
        if (!lang.IsGerman && TryEnglish(german, out var en)) return en;
        return german;
    }

    // the English tables of modules that bring their own (a feature that can be removed with its folders leaves nothing in English.cs)
    private static IReadOnlyDictionary<string, string>[] _moduleEnglish = [];
    private static readonly Lock ModuleGate = new();

    /// <summary>
    /// Adds the English texts of a module that is built as a removable part of its own (the e-invoice checker, KONZEPT.md, section 5.24): the
    /// module registers its table once, when it is loaded. A text that English.cs translates keeps that translation.
    /// </summary>
    /// <param name="texts">The module's table, keyed by the German text.</param>
    public static void AddEnglish(IReadOnlyDictionary<string, string> texts)
    {
        lock (ModuleGate)
            if (Array.IndexOf(_moduleEnglish, texts) < 0) _moduleEnglish = [.. _moduleEnglish, texts];
    }

    /// <summary>The English text of a key: from English.cs, else from the tables of the modules.</summary>
    /// <param name="german">The German text.</param>
    /// <param name="english">The English text, if there is one.</param>
    public static bool TryEnglish(string german, out string english)
    {
        if (English.Texts.TryGetValue(german, out english!)) return true;
        foreach (var table in Volatile.Read(ref _moduleEnglish))
            if (table.TryGetValue(german, out english!)) return true;
        english = "";
        return false;
    }

    /// <summary>Returns a text with placeholders {0}, {1} … filled in; numbers are formatted in the language's culture.</summary>
    /// <param name="german">The German text with placeholders, which serves as the key.</param>
    /// <param name="args">The values for the placeholders.</param>
    /// <returns>The translated and formatted text.</returns>
    public static string T(string german, params object?[] args) =>
        string.Format(Culture, T(german), args);
}
