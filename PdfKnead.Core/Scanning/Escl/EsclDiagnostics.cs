using System.Globalization;
using System.Text;

namespace PdfKnead.Core.Scanning.Escl;

/// <summary>
/// What the eSCL access did lately, kept in memory only (KONZEPT.md §5.25, S9, „Diagnose“): search hits, requests, answer codes and times,
/// shortened XML. Never image data, never credentials. It names devices and addresses, so it is written to a file only when the user asks
/// („Diagnose speichern …“). The oldest entries make way once <see cref="MaxEntries"/> is reached.
/// </summary>
public sealed class EsclDiagnostics
{
    /// <summary>The most entries kept.</summary>
    public const int MaxEntries = 2000;

    /// <summary>The longest text of one entry; longer texts are cut with a note.</summary>
    public const int MaxText = 4000;

    private readonly Queue<string> _entries = new();
    private readonly Lock _lock = new();

    /// <summary>Adds an entry.</summary>
    /// <param name="area">What it is about, e.g. „Suche“, „Anfrage“, „Antwort“.</param>
    /// <param name="text">The text; cut at <see cref="MaxText"/>.</param>
    public void Add(string area, string text)
    {
        if (text.Length > MaxText) text = text[..MaxText] + $" … ({text.Length - MaxText} Zeichen gekürzt)";
        var line = $"{DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} {area}: {text}";
        lock (_lock)
        {
            _entries.Enqueue(line);
            while (_entries.Count > MaxEntries) _entries.Dequeue();
        }
    }

    /// <summary>The entries, oldest first.</summary>
    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_lock) return [.. _entries];
        }
    }

    /// <summary>The whole protocol as text, for „Diagnose speichern …“.</summary>
    public string Text()
    {
        var builder = new StringBuilder();
        builder.AppendLine("PDF-Knead — Diagnose des eSCL-Zugangs (enthält Gerätenamen und Adressen im eigenen Netzwerk)");
        foreach (var entry in Entries) builder.AppendLine(entry);
        return builder.ToString();
    }

    /// <summary>Removes all entries.</summary>
    public void Clear()
    {
        lock (_lock) _entries.Clear();
    }
}
