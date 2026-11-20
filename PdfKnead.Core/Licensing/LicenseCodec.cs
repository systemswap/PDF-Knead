using System.Security.Cryptography;
using System.Text;

namespace PdfKnead.Core.Licensing;

/// <summary>
/// The features that need a license in commercial use after the introductory period (LICENSE, section 1 (5)). The key has room for 32;
/// <see cref="LicensedFeatureTabs"/> sorts them by the ribbon tab they are in.
/// </summary>
[Flags]
public enum LicensedFeature : uint
{
    None = 0,
    Redaction = 1,
    Measuring = 2,
    CertificateSigning = 4,
    PdfA = 8,
    Scanning = 16,
    /// <summary>Creating forms: „Formular bearbeiten“, inserting fields (except signature fields), „Felder erkennen“.</summary>
    Forms = 32,
    /// <summary>„Bearbeiten“: text, pictures, codes and fields of the page (KONZEPT §5.22). Filling in forms stays free.</summary>
    Editing = 64,
    /// <summary>„Einschränkungen entfernen“: lifting the permissions and passwords of a file (KONZEPT §5.11).</summary>
    Restrictions = 128,
    All = Redaction | Measuring | CertificateSigning | PdfA | Scanning | Forms | Editing | Restrictions,
}

/// <summary>
/// The licensed features by the ribbon tab they are in, in the order of the tabs: how the key generator offers them and how a key is
/// shown — so that whoever hands out keys finds a feature where it is in the program. The tab names are those of the program (German, as
/// translated there).
/// </summary>
public static class LicensedFeatureTabs
{
    public static readonly IReadOnlyList<(string Tab, LicensedFeature[] Features)> Groups =
    [
        ("Start", [LicensedFeature.Scanning, LicensedFeature.Editing, LicensedFeature.Measuring]),
        ("Formular", [LicensedFeature.Forms]),
        ("Unterschrift", [LicensedFeature.CertificateSigning]),
        ("Sicher", [LicensedFeature.Redaction, LicensedFeature.Restrictions]),
        ("Datei", [LicensedFeature.PdfA]),
    ];
}

/// <summary>The kind of a license; shown in the program and in the key generator, without effect on the check except the dates.</summary>
public enum LicenseKind : byte
{
    Full = 0,
    Trial = 1,
    Internal = 2,
    Education = 3,
    NotForResale = 4,
}

/// <summary>What a value of a license is bound to; only a checksum of it is in the key.</summary>
public enum BindingKind
{
    Email,
    Hostname,
    Domain,
}

/// <summary>
/// The contents of a license key. A key without a time limit and without an update limit carries <see cref="LicenseCodec.Unlimited"/> as
/// date. A commercial license works without a time limit in every version released up to <see cref="UpdatesUntil"/> (LICENSE, section 3 (2)).
/// </summary>
public sealed record LicenseCode
{
    /// <summary>Product short name; a key of another product is refused.</summary>
    public string Product { get; init; } = LicenseCodec.ProductCode;

    /// <summary>Format version of the key.</summary>
    public int FormatVersion { get; init; } = LicenseCodec.CurrentFormatVersion;

    /// <summary>Last day the key works, inclusive (a trial); <see cref="LicenseCodec.Unlimited"/> = no time limit.</summary>
    public DateOnly ExpiresOn { get; init; } = LicenseCodec.Unlimited;

    /// <summary>Versions released up to this day, inclusive, are covered; <see cref="LicenseCodec.Unlimited"/> = all versions.</summary>
    public DateOnly UpdatesUntil { get; init; } = LicenseCodec.Unlimited;

    public bool BindsEmail { get; init; }
    public bool BindsHostname { get; init; }
    public bool BindsDomain { get; init; }

    /// <summary>Checksums of the bound values; the values themselves are not in the key.</summary>
    public uint EmailHash { get; init; }
    public uint HostnameHash { get; init; }
    public uint DomainHash { get; init; }

    /// <summary>Running number of the issued key.</summary>
    public int Serial { get; init; }

    public LicenseKind Kind { get; init; } = LicenseKind.Full;

    /// <summary>The features the key unlocks.</summary>
    public LicensedFeature Features { get; init; } = LicensedFeature.All;

    /// <summary>The licensee as shown; at most <see cref="LicenseCodec.MaxCompanyBytes"/> bytes in UTF-8.</summary>
    public string Company { get; init; } = "";

    public bool IsPerpetual => ExpiresOn >= LicenseCodec.Unlimited;

    public bool HasUpdatesWithoutEnd => UpdatesUntil >= LicenseCodec.Unlimited;

    public bool HasAnyBinding => BindsEmail || BindsHostname || BindsDomain;
}

/// <summary>
/// Writes and reads license keys. Layout of the data before scrambling:
/// <code>
///   0-1    product short name „PK“
///   2      upper 4 bits format version, lower 4 bits binding flags
///   3-4    last day (days since 2020-01-01)
///   5-6    updates until (days since 2020-01-01)
///   7-18   checksums of e-mail, host name and domain (random when not bound, so that a key does not show how strict it is)
///   19-21  running number
///   22     kind
///   23-26  features (32 bits, <see cref="LicensedFeature"/>)
///   27     length of the licensee, then the licensee in UTF-8
///   then   two check bytes over everything before (typing errors)
/// </code>
/// The whole is scrambled with AES-256 and written in Base32 without I, L, O and U, in groups of five. The scrambling key is part of the
/// public source code: it makes a key look random, it does not protect it — anyone who reads the code can make keys. The license, not the
/// technique, is authoritative (LICENSE, section 3 (4)); a key that can be typed and sent by e-mail is worth that price.
/// </summary>
public static class LicenseCodec
{
    public const string ProductCode = "PK";
    // 2: features in four bytes instead of one (no key of format 1 was ever issued)
    public const int CurrentFormatVersion = 2;
    public const int MaxCompanyBytes = 40;

    /// <summary>The value for „no limit“, both for the last day and for the updates.</summary>
    public static readonly DateOnly Unlimited = new(2099, 12, 31);

    private static readonly DateOnly Epoch = new(2020, 1, 1);
    private const int FixedLength = 28;
    private const byte FlagEmail = 1, FlagHostname = 2, FlagDomain = 4;

    // Base32 without the easily confused letters I, L, O and U
    private const string Alphabet = "ABCDEFGHJKMNPQRSTVWXYZ0123456789";

    private static byte[] Key() => SHA256.HashData("PDF-Knead.License.v1.Schluessel"u8);

    private static byte[] Iv() => SHA256.HashData("PDF-Knead.License.v1.IV"u8)[..16];

    /// <summary>The checksum of a bound value (trimmed, lower case).</summary>
    public static uint HashBinding(string? value, BindingKind kind)
    {
        var normalized = Normalize(value);
        if (normalized.Length == 0) return 0;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("PDF-Knead:" + kind + ":" + normalized));
        return BitConverter.ToUInt32(hash, 0);
    }

    /// <summary>A bound value as compared: without surrounding white space, in lower case.</summary>
    public static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToLowerInvariant();

    /// <summary>
    /// The forms a value of the computer is compared in: the full name and, for host and domain, also the first part — a key issued for
    /// „server01“ matches „server01.firma.local“, but one issued for „firma.de“ does not match „firma.local“.
    /// </summary>
    public static IReadOnlyList<string> MatchCandidates(string? value, BindingKind kind)
    {
        var full = Normalize(value);
        if (full.Length == 0) return [];
        if (kind == BindingKind.Email) return [full];
        var first = full.Split('.')[0];
        return first == full ? [full] : [full, first];
    }

    /// <summary>Whether one of the values matches a checksum of the key.</summary>
    public static bool Matches(IEnumerable<string?> values, BindingKind kind, uint expected) =>
        values.SelectMany(v => MatchCandidates(v, kind)).Any(c => HashBinding(c, kind) == expected);

    /// <summary>Writes a key.</summary>
    /// <exception cref="ArgumentException">The licensee is too long, or a date lies outside 2020–2099.</exception>
    public static string Encode(LicenseCode code)
    {
        var company = Encoding.UTF8.GetBytes(code.Company.Trim());
        if (company.Length > MaxCompanyBytes) throw new ArgumentException($"Licensee longer than {MaxCompanyBytes} bytes.", nameof(code));
        var data = new byte[FixedLength + company.Length + 2];
        data[0] = (byte)'P';
        data[1] = (byte)'K';
        byte flags = 0;
        if (code.BindsEmail) flags |= FlagEmail;
        if (code.BindsHostname) flags |= FlagHostname;
        if (code.BindsDomain) flags |= FlagDomain;
        data[2] = (byte)((CurrentFormatVersion << 4) | flags);
        WriteDay(data, 3, code.ExpiresOn);
        WriteDay(data, 5, code.UpdatesUntil);
        WriteHash(data, 7, code.BindsEmail, code.EmailHash);
        WriteHash(data, 11, code.BindsHostname, code.HostnameHash);
        WriteHash(data, 15, code.BindsDomain, code.DomainHash);
        data[19] = (byte)code.Serial;
        data[20] = (byte)(code.Serial >> 8);
        data[21] = (byte)(code.Serial >> 16);
        data[22] = (byte)code.Kind;
        BitConverter.TryWriteBytes(data.AsSpan(23, 4), (uint)(code.Features & LicensedFeature.All));
        data[27] = (byte)company.Length;
        company.CopyTo(data, FixedLength);
        var check = Checksum(data.AsSpan(0, data.Length - 2));
        data[^2] = (byte)check;
        data[^1] = (byte)(check >> 8);
        return Groups(ToBase32(Scramble(data, encrypt: true)));
    }

    /// <summary>Reads a key; null when it is unreadable, of another product or format, or has a typing error.</summary>
    public static LicenseCode? Decode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 400) return null;
        try
        {
            var bytes = FromBase32(text);
            if (bytes.Length == 0 || bytes.Length % 16 != 0) return null;
            var raw = Scramble(bytes, encrypt: false);
            if (raw.Length < FixedLength + 2) return null;
            var expected = (ushort)(raw[^2] | (raw[^1] << 8));
            if (Checksum(raw.AsSpan(0, raw.Length - 2)) != expected) return null;
            if (raw[0] != 'P' || raw[1] != 'K' || raw[2] >> 4 != CurrentFormatVersion) return null;
            var length = raw[27];
            if (FixedLength + length + 2 != raw.Length) return null;
            var flags = raw[2] & 0x0F;
            return new LicenseCode
            {
                ExpiresOn = ReadDay(raw, 3),
                UpdatesUntil = ReadDay(raw, 5),
                BindsEmail = (flags & FlagEmail) != 0,
                BindsHostname = (flags & FlagHostname) != 0,
                BindsDomain = (flags & FlagDomain) != 0,
                EmailHash = BitConverter.ToUInt32(raw, 7),
                HostnameHash = BitConverter.ToUInt32(raw, 11),
                DomainHash = BitConverter.ToUInt32(raw, 15),
                Serial = raw[19] | (raw[20] << 8) | (raw[21] << 16),
                Kind = (LicenseKind)raw[22],
                Features = (LicensedFeature)BitConverter.ToUInt32(raw, 23) & LicensedFeature.All,
                Company = Encoding.UTF8.GetString(raw, FixedLength, length),
            };
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static void WriteDay(byte[] data, int at, DateOnly day)
    {
        var days = day.DayNumber - Epoch.DayNumber;
        if (days is < 0 or > ushort.MaxValue || day > Unlimited) throw new ArgumentException("Date outside 2020–2099.", nameof(day));
        data[at] = (byte)days;
        data[at + 1] = (byte)(days >> 8);
    }

    private static DateOnly ReadDay(byte[] data, int at)
    {
        var day = Epoch.AddDays(data[at] | (data[at + 1] << 8));
        return day > Unlimited ? Unlimited : day;
    }

    private static void WriteHash(byte[] data, int at, bool bound, uint hash)
    {
        if (bound) BitConverter.TryWriteBytes(data.AsSpan(at, 4), hash);
        else RandomNumberGenerator.Fill(data.AsSpan(at, 4));
    }

    // Fletcher-like: finds typing errors and swapped characters
    private static ushort Checksum(ReadOnlySpan<byte> data)
    {
        int a = 0xAB, b = 0xCD;
        foreach (var value in data)
        {
            a = (a + value) % 255;
            b = (b + a) % 255;
        }
        return (ushort)((b << 8) | a);
    }

    private static byte[] Scramble(byte[] data, bool encrypt)
    {
        using var aes = Aes.Create();
        aes.Key = Key();
        return encrypt ? aes.EncryptCbc(data, Iv(), PaddingMode.PKCS7) : aes.DecryptCbc(data, Iv(), PaddingMode.PKCS7);
    }

    private static string ToBase32(byte[] data)
    {
        var text = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var value in data)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                text.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
            buffer &= (1 << bits) - 1;
        }
        if (bits > 0) text.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return text.ToString();
    }

    private static byte[] FromBase32(string text)
    {
        var output = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var raw in text.ToUpperInvariant())
        {
            // separators and spaces are ignored; common confusions map to the allowed character
            var c = raw switch { 'I' or 'L' => '1', 'O' => '0', 'U' => 'V', _ => raw };
            var index = Alphabet.IndexOf(c);
            if (index < 0) continue;
            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
            buffer &= (1 << bits) - 1;
        }
        return [.. output];
    }

    private static string Groups(string text)
    {
        var grouped = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (i > 0 && i % 5 == 0) grouped.Append('-');
            grouped.Append(text[i]);
        }
        return grouped.ToString();
    }
}
