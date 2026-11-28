using System.Globalization;
using System.Text;
using PdfKnead.Core.Fonts.Layout;

namespace PdfKnead.Core.Security;

/// <summary>
/// Names from an opened file that the user interface or the command line shows (The Unicode Standard, 9: bidirectional text in identifiers, with
/// UTR #36). A directional formatting character in a name can make it read as something else — „Rechnung“ U+202E „fdp.exe“ shows as
/// „Rechnungexe.pdf“ — or reorder the text around it. Two levels (docs/FEHLERGEDAECHTNIS.md, No. 1217):
/// <list type="bullet">
/// <item><see cref="Safe"/> for the names where a deception touches security — the file names of attachments, the signer, subject and issuer of
/// a signature, the target of a link: every directional formatting character is shown as „⟨U+XXXX⟩“ (as the meaning of a barcode shows it);
/// <see cref="FileName"/> drops them from a file name proposed for saving, so that the saved name does not deceive either.</item>
/// <item><see cref="Isolated"/> for all other names (bookmarks, metadata, form fields, authors of comments): between FSI and PDI, so that an
/// open embedding or override cannot reach over the text beside the name; right-to-left names keep their characters and read as before.</item>
/// </list>
/// The names stored in the file stay as they are; only what is shown changes.
/// </summary>
public static class DisplayNames
{
    private const char FirstStrongIsolate = (char)0x2068, PopDirectionalIsolate = (char)0x2069;

    /// <summary>A name with every directional formatting character (ALM, LRM, RLM, LRE … RLO, LRI … PDI) shown as „⟨U+XXXX⟩“; empty for null.</summary>
    public static string Safe(string? name)
    {
        if (string.IsNullOrEmpty(name) || !HasControl(name)) return name ?? "";
        var sb = new StringBuilder(name.Length + 16);
        foreach (var c in name)
            if (Bidi.IsControl(c)) sb.Append(CultureInfo.InvariantCulture, $"⟨U+{(int)c:X4}⟩");
            else sb.Append(c);
        return sb.ToString();
    }

    /// <summary>A file name without its directional formatting characters (for a name proposed when saving); empty for null.</summary>
    public static string FileName(string? name)
    {
        if (string.IsNullOrEmpty(name) || !HasControl(name)) return name ?? "";
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            if (!Bidi.IsControl(c)) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>
    /// A name between FSI and PDI where it holds a right-to-left character or a directional formatting character (otherwise as it is): a PDI
    /// without its initiator, which would close the FSI early, is left out, and every isolate the name leaves open is closed before the final
    /// PDI — the embeddings and overrides inside end with it (UAX #9, X6a). Empty for null.
    /// </summary>
    public static string Isolated(string? name)
    {
        if (string.IsNullOrEmpty(name) || !HasControl(name) && !Bidi.HasRightToLeft(name)) return name ?? "";
        var sb = new StringBuilder(name.Length + 4);
        sb.Append(FirstStrongIsolate);
        var open = 0;
        foreach (var c in name)
        {
            if (c is (char)0x2066 or (char)0x2067 or FirstStrongIsolate) open++;
            else if (c == PopDirectionalIsolate)
            {
                if (open == 0) continue;
                open--;
            }
            sb.Append(c);
        }
        sb.Append(PopDirectionalIsolate, open + 1);
        return sb.ToString();
    }

    private static bool HasControl(string name)
    {
        foreach (var c in name)
            if (Bidi.IsControl(c)) return true;
        return false;
    }
}
