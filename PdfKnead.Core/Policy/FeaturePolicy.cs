using System.Text.Json;
using PdfKnead.Core.Updates;

namespace PdfKnead.Core.Policy;

/// <summary>
/// A function of the program that an administrator can switch off: a stable id (in the file) and its name in the program; a part of another
/// function names that one as <paramref name="Parent"/> — it is allowed only while its parent is.
/// </summary>
public sealed record PolicyFunction(string Id, string Name, string? Parent = null);

/// <summary>
/// The functions an administrator can switch off, by the ribbon tab they are in, in the order of the tabs: the function file says what the
/// users of the organization may do. Viewing functions (checking an e-invoice, attachments, the permissions of a file) are not listed —
/// looking is always allowed.
/// </summary>
public static class PolicyCatalog
{
    public static readonly IReadOnlyList<(string Tab, PolicyFunction[] Functions)> Tabs =
    [
        ("Start",
        [
            new("start.new", "Neu"), new("start.save", "Speichern"), new("start.print", "Drucken"), new("start.compress", "Komprimieren"),
            new("start.scan", "Scannen"), new("start.edit", "Bearbeiten"), new("start.measure", "Messen"), new("start.ocr", "OCR"),
            new("start.notes", "Notizen"),
        ]),
        ("Seiten",
        [
            new("pages.extract", "Extrahieren"), new("pages.split", "Teilen"), new("pages.merge", "Zusammenführen"),
            new("pages.sort", "Sortieren"), new("pages.reverse", "Umkehren"),
        ]),
        ("Einfügen",
        [
            new("insert.content", "Inhalt"), new("insert.element", "Element"), new("insert.images", "Bilder"), new("insert.notes", "Notizen"),
            new("insert.codes", "Codes"), new("insert.shapes", "Formen"),
        ]),
        ("Formular", [new("form.all", "Alle Funktionen")]),
        ("Unterschrift", [new("signature.sign", "Unterschreiben"), new("signature.digital", "Digital signieren"), new("signature.certificate", "Zertifikat")]),
        ("Sicher",
        [
            new("secure.redact-area", "Bereich schwärzen"), new("secure.redact-hidden", "Verborgenes schwärzen"),
            new("secure.redact-text", "Text schwärzen"), new("secure.search-redact", "Suchen und schwärzen"),
            new("secure.apply-redactions", "Schwärzungen anwenden"), new("secure.flatten", "Flachlegen"),
            new("secure.flatten-image", "Flachlegen als Bild"), new("secure.password", "Passwort und Rechte"),
            new("secure.remove-restrictions", "Einschränkungen entfernen"),
            // removing them without the permissions password, only after confirming the right to (decided by the user on 22.11.2026)
            new("secure.remove-restrictions-without-password", "Ohne Rechte-Passwort entfernen", "secure.remove-restrictions"),
            new("secure.clean", "Dokument bereinigen"),
            new("secure.accessibility", "Barrierefreiheit"),
        ]),
        ("E-Rechnung", [new("einvoice.save-xml", "XML speichern")]),   // Andockstelle E-Rechnung
        // the window that invites support after saving (decided on 21.11.2026): only an organization switches it off
        ("Programm", [new("program.donation-window", "Spenden-Fenster")]),
    ];

    /// <summary>All functions, in the order of the tabs.</summary>
    public static IEnumerable<PolicyFunction> All => Tabs.SelectMany(t => t.Functions);

    public static bool IsKnown(string id) => All.Any(f => f.Id == id);

    /// <summary>The function of an id, or null when this version does not know it.</summary>
    public static PolicyFunction? Find(string id) => All.FirstOrDefault(f => f.Id == id);
}

/// <summary>The kind of use a function file sets: a file is only laid out by the administrator of a company or an authority.</summary>
public enum PolicyUsage
{
    Commercial,
    Authority,
}

/// <summary>
/// The function file of an organization (<see cref="FileName"/> beside the program): which functions are switched off, the kind of use,
/// the name of the organization. While the file is there it applies — the kind of use cannot be changed in the program; only whoever may
/// delete the file can undo it, and an administrator lays it where users cannot write. A list of switched-off functions rather than of
/// allowed ones: a function added in a later version is allowed until the administrator switches it off.
/// </summary>
public sealed record FeaturePolicy
{
    public const string FileName = "berechtigungen.json";
    public const int CurrentFormat = 1;
    private const int MaxFileBytes = 64 * 1024;
    private const int MaxEntries = 1000;

    public PolicyUsage Usage { get; init; } = PolicyUsage.Commercial;

    /// <summary>Shown in the program („managed by …“); may be empty.</summary>
    public string Organization { get; init; } = "";

    /// <summary>The ids of the switched-off functions; unknown ids (of a later version) are kept.</summary>
    public IReadOnlySet<string> Denied { get; init; } = new HashSet<string>();

    public bool Allows(string id) => !Denied.Contains(id) && (PolicyCatalog.Find(id)?.Parent is not { } parent || Allows(parent));

    /// <summary>Writes the file.</summary>
    public byte[] ToJson()
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("format", CurrentFormat);
            w.WriteString("usage", Usage == PolicyUsage.Authority ? "authority" : "commercial");
            w.WriteString("organization", Organization.Trim());
            w.WriteStartArray("denied");
            // in the order of the catalog, so that the file reads like the program; unknown ids at the end
            var known = PolicyCatalog.All.Select(f => f.Id).Where(Denied.Contains).ToList();
            foreach (var id in known.Concat(Denied.Except(known).Order(StringComparer.Ordinal))) w.WriteStringValue(id);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <summary>Reads a function file; null when it is not one as written by <see cref="ToJson"/> (or of a later format).</summary>
    public static FeaturePolicy? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length is 0 or > MaxFileBytes) return null;
        try
        {
            using var doc = JsonDocument.Parse(data.ToArray(), new JsonDocumentOptions { MaxDepth = 4, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("format", out var format) || !format.TryGetInt32(out var version) || version != CurrentFormat) return null;
            var usage = PolicyUsage.Commercial;
            if (root.TryGetProperty("usage", out var u))
            {
                if (u.ValueKind != JsonValueKind.String) return null;
                usage = u.GetString() switch
                {
                    "commercial" => PolicyUsage.Commercial,
                    "authority" => PolicyUsage.Authority,
                    _ => (PolicyUsage)(-1),
                };
                if (!Enum.IsDefined(usage)) return null;
            }
            var organization = "";
            if (root.TryGetProperty("organization", out var o))
            {
                if (o.ValueKind != JsonValueKind.String || o.GetString() is not { Length: <= 200 } name) return null;
                organization = name.Trim();
            }
            var denied = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("denied", out var list))
            {
                if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > MaxEntries) return null;
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 and <= 100 } id) return null;
                    denied.Add(id);
                }
            }
            return new FeaturePolicy { Usage = usage, Organization = organization, Denied = denied };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Lays a function file into a folder (the folder of the program) under <see cref="FileName"/>: only one that reads as a function file;
    /// written beside the target first and then moved into place, so that a broken copy never replaces a good file.
    /// </summary>
    /// <returns>What happened; the detail is the target, the folder without permission, or the error.</returns>
    public static (PolicyInstallResult Result, string Detail) Install(string source, string folder)
    {
        byte[] data;
        try
        {
            if (new FileInfo(source).Length > MaxFileBytes) return (PolicyInstallResult.NotAFunctionFile, "");
            data = File.ReadAllBytes(source);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (PolicyInstallResult.Failed, e.Message);
        }
        if (Parse(data) is null) return (PolicyInstallResult.NotAFunctionFile, "");
        var target = Path.Combine(folder, FileName);
        var temporary = target + ".neu";
        try
        {
            File.WriteAllBytes(temporary, data);
            File.Move(temporary, target, overwrite: true);
            return (PolicyInstallResult.Installed, target);
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(temporary);
            return (PolicyInstallResult.NoPermission, folder);
        }
        catch (IOException e)
        {
            TryDelete(temporary);
            return (PolicyInstallResult.Failed, e.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // nothing written, nothing to remove
        }
    }

    /// <summary>
    /// Looks for the function file in a folder (the folder of the program). A file that is there but cannot be read counts as
    /// <see cref="PolicyLoadState.Unreadable"/>: the program then switches off every function in the list rather than allowing all — in an
    /// organization a damaged file must not open everything.
    /// </summary>
    public static PolicyLoad Load(string folder)
    {
        var path = Path.Combine(folder, FileName);
        if (!File.Exists(path)) return new PolicyLoad(PolicyLoadState.NotThere, null, path);
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxFileBytes) return new PolicyLoad(PolicyLoadState.Unreadable, null, path);
            return Parse(File.ReadAllBytes(path)) is { } policy
                ? new PolicyLoad(PolicyLoadState.Loaded, policy, path)
                : new PolicyLoad(PolicyLoadState.Unreadable, null, path);
        }
        catch (IOException)
        {
            return new PolicyLoad(PolicyLoadState.Unreadable, null, path);
        }
        catch (UnauthorizedAccessException)
        {
            return new PolicyLoad(PolicyLoadState.Unreadable, null, path);
        }
    }
}

public enum PolicyInstallResult
{
    Installed,
    NotAFunctionFile,
    NoPermission,
    Failed,
}

public enum PolicyLoadState
{
    NotThere,
    Loaded,
    Unreadable,
}

/// <summary>The result of looking for the function file.</summary>
public sealed record PolicyLoad(PolicyLoadState State, FeaturePolicy? Policy, string Path);

/// <summary>
/// The function file that applies to this program run: looked for once at the start beside the program (beside the AppImage under Linux,
/// like „updates-aus“). Without a file every function is allowed; with one, its list applies; with a damaged one every function of the
/// catalog is switched off (a damaged file must not open everything). Its kind of use is read and passed over: PDF-Knead is free for
/// everyone since 21.11.2026 and asks for no kind of use; the field stays for a paid version later (docs/LIZENZMODELL.md).
/// </summary>
public static class ActivePolicy
{
    public static PolicyLoad Current { get; private set; } = new(PolicyLoadState.NotThere, null, "");

    /// <summary>Sets the file that applies (at the start; tests set their own).</summary>
    public static void Use(PolicyLoad load) => Current = load;

    /// <summary>Looks for the function file beside the program.</summary>
    public static PolicyLoad LoadBesideProgram() => FeaturePolicy.Load(UpdateStart.ProgramLocation().Folder);

    /// <summary>Whether a function file applies (also a damaged one).</summary>
    public static bool IsActive => Current.State != PolicyLoadState.NotThere;

    /// <summary>Whether a function may be used; ids that are not in the catalog of this version are always allowed.</summary>
    public static bool Allows(string id) => Current.State switch
    {
        PolicyLoadState.Loaded => Current.Policy!.Allows(id),
        PolicyLoadState.Unreadable => !PolicyCatalog.IsKnown(id),
        _ => true,
    };

    /// <summary>The organization named in the file; empty when none.</summary>
    public static string Organization => Current.Policy?.Organization ?? "";
}
