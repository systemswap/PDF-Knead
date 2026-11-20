namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// XMP namespaces whose properties describe attached files — registered by a module at start (the core names none itself), for instance the
/// data of a hybrid document that name its embedded file. Where a save drops the attachments of the document („Anhänge“ cleaned up, a part
/// without the attachments of the whole), these properties and the extension schemas declaring them go too, so that the metadata name no
/// file that is gone (decided on 15.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 865). PDF/A copies that drop attachments carry no
/// declared properties at all (<see cref="PdfAXmp.CarryableDeclared"/>, No. 863).
/// </summary>
public static class AttachedFileMetadata
{
    private static readonly object Gate = new();
    private static HashSet<string> _namespaces = new(StringComparer.Ordinal);

    /// <summary>Registers namespaces (a second registration of the same does nothing).</summary>
    public static void Register(IEnumerable<string> namespaces)
    {
        lock (Gate)
        {
            var next = new HashSet<string>(_namespaces, StringComparer.Ordinal);
            next.UnionWith(namespaces.Where(n => n.Length > 0));
            _namespaces = next;
        }
    }

    /// <summary>The namespaces registered (a snapshot: registering later does not change it).</summary>
    public static IReadOnlySet<string> Namespaces => _namespaces;
}
