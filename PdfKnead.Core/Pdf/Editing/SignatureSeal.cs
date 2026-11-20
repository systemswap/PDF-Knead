using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PdfKnead.Core.Platform;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// Origin of a signature: a readable line (name, date, identifier made of a computer and a user part) and a signature by the
/// device identity over this line and the content of the signature. Not a legally valid digital signature (PAdES) but evidence
/// of the device on which the signature was placed. On saving it is stored in the file's metadata (<c>pdf-knead herkunft</c>
/// checks it).
/// </summary>
/// <param name="Origin">The readable origin line.</param>
/// <param name="Code">The device identifier as text.</param>
/// <param name="PublicKey">Public key of the device identity.</param>
/// <param name="ContentHash">SHA-256 of the signature content (<see cref="ContentHashOf"/>).</param>
/// <param name="Signature">Signature over the origin line and the content hash.</param>
public sealed record SignatureSeal(string Origin, string Code, byte[] PublicKey, byte[] ContentHash, byte[] Signature)
{
    private const string Prefix = "PDF-Knead Unterschrift 1\n";

    /// <summary>Seals a signature: hashes the content and signs it together with the origin line.</summary>
    /// <param name="element">The signature element.</param>
    /// <param name="origin">The origin line.</param>
    /// <param name="identity">The device identity that signs.</param>
    /// <returns>The seal.</returns>
    public static SignatureSeal Create(PageElement element, string origin, DeviceIdentity identity)
    {
        var hash = ContentHashOf(element);
        return new SignatureSeal(origin, identity.Code.ToString(), identity.PublicKey, hash, identity.Sign(Message(origin, hash)));
    }

    /// <summary>Checks whether the signature matches origin line, content hash and public key.</summary>
    /// <returns><see langword="true"/> if the signature is valid.</returns>
    public bool Verify() => DeviceIdentity.Verify(PublicKey, Message(Origin, ContentHash), Signature);

    /// <summary>Checks whether the seal belongs to this content (name and font, strokes or image).</summary>
    /// <param name="element">The element.</param>
    /// <returns><see langword="true"/> if the content hash matches.</returns>
    /// <remarks>Seals up to 1.1.181 wrote a stroke width with the decimal separator of the language set at the time („3,5“ under
    /// German); they still match.</remarks>
    public bool Matches(PageElement element) =>
        ContentHashOf(element).AsSpan().SequenceEqual(ContentHash) ||
        element is InkElement { StrokeWidth: var width } && width != Math.Floor(width) &&
        ContentHashOf(element, GermanBefore1_1_182).AsSpan().SequenceEqual(ContentHash);

    private static readonly CultureInfo GermanBefore1_1_182 = CultureInfo.GetCultureInfo("de-DE");

    private static byte[] Message(string origin, byte[] hash) => [.. Encoding.UTF8.GetBytes(Prefix + origin + "\n"), .. hash];

    /// <summary>Computes SHA-256 over the content — not over position and size, because moving is allowed.</summary>
    /// <param name="element">The element.</param>
    /// <returns>The 32-byte hash.</returns>
    /// <remarks>Numbers independent of the language: a seal made under German must also match under English.</remarks>
    public static byte[] ContentHashOf(PageElement element) => ContentHashOf(element, CultureInfo.InvariantCulture);

    internal static byte[] ContentHashOf(PageElement element, CultureInfo culture)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        switch (element)
        {
            case SignatureElement s:
                sha.AppendData(Encoding.UTF8.GetBytes(string.Create(culture, $"Schrift\n{s.Name}\n{s.FontFile}\n{s.Color:X6}")));
                break;
            case InkElement i:
                sha.AppendData(Encoding.UTF8.GetBytes(string.Create(culture, $"Striche\n{i.StrokeWidth:R}\n{i.Color:X6}")));
                foreach (var stroke in i.Strokes)
                {
                    sha.AppendData(BitConverter.GetBytes(stroke.Length));
                    sha.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(stroke.AsSpan()));   // like BitConverter per value, without an array per number
                }
                break;
            case ImageElement b:
                sha.AppendData(Encoding.UTF8.GetBytes(string.Create(culture, $"Bild\n{b.Image.Width}x{b.Image.Height}")));
                sha.AppendData(b.Image.Pixels);
                break;
            default:
                sha.AppendData(Encoding.UTF8.GetBytes(element.GetType().Name));
                break;
        }
        return sha.GetHashAndReset();
    }
}

/// <summary>Origin record from a file: page (at the time of saving) and seal.</summary>
/// <param name="Page">Page number as stored.</param>
/// <param name="Seal">The seal.</param>
public sealed record SealRecord(int Page, SignatureSeal Seal)
{
    /// <summary>Reads all origin records from the metadata of a file.</summary>
    /// <param name="doc">The document.</param>
    /// <returns>The records; empty if there are none or they are unreadable. Broken entries are skipped.</returns>
    public static List<SealRecord> Read(Document.PdfDocument doc)
    {
        var result = new List<SealRecord>();
        // in the information dictionary; in the catalogue of a PDF/A-4 copy, which has none
        if ((doc.Trailer.GetDictionary(Objects.PdfName.Info)?.GetString(Writing.PdfWriter.SignaturesKey) ??
             doc.Catalog?.GetString(Writing.PdfWriter.SignaturesKey))?.ToText() is not { } text) return result;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(text);
            if (json.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return result;
            foreach (var item in json.RootElement.EnumerateArray())
            {
                // Per entry: a broken one does not spoil the others
                try
                {
                    result.Add(new SealRecord(
                        item.GetProperty("seite").GetInt32(),
                        new SignatureSeal(
                            item.GetProperty("herkunft").GetString() ?? "",
                            item.GetProperty("kennung").GetString() ?? "",
                            item.GetProperty("schluessel").GetBytesFromBase64(),
                            item.GetProperty("inhalt").GetBytesFromBase64(),
                            item.GetProperty("signatur").GetBytesFromBase64())));
                }
                catch (Exception e) when (e is InvalidOperationException or FormatException or KeyNotFoundException)
                {
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // broken or foreign: no records
        }
        return result;
    }
}
