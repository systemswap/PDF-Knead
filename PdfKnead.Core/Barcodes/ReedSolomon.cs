namespace PdfKnead.Core.Barcodes;

/// <summary>
/// Arithmetic in GF(2⁸) with a given primitive polynomial: QR codes use x⁸ + x⁴ + x³ + x² + 1 (0x11D, ISO/IEC 18004:2015, 7.5.2),
/// Data Matrix x⁸ + x⁵ + x³ + x² + 1 (0x12D, ISO/IEC 16022:2006, 5.7.1).
/// </summary>
internal sealed class GaloisField
{
    /// <summary>The field of QR codes.</summary>
    public static readonly GaloisField Qr = new(0x11D);

    /// <summary>The field of Data Matrix.</summary>
    public static readonly GaloisField DataMatrix = new(0x12D);

    private readonly byte[] _exp = new byte[512];
    private readonly int[] _log = new int[256];

    private GaloisField(int primitive)
    {
        var x = 1;
        for (var i = 0; i < 255; i++)
        {
            _exp[i] = (byte)x;
            _log[x] = i;
            x <<= 1;
            if (x >= 256) x ^= primitive;
        }
        for (var i = 255; i < 512; i++) _exp[i] = _exp[i - 255];
    }

    /// <summary>α^power (power ≥ 0).</summary>
    public byte Exp(int power) => _exp[power % 255];

    /// <summary>log_α(value); value must not be 0.</summary>
    public int Log(int value) => _log[value];

    public byte Multiply(int a, int b) => a == 0 || b == 0 ? (byte)0 : _exp[_log[a] + _log[b]];

    public byte Divide(int a, int b) => a == 0 ? (byte)0 : _exp[_log[a] + 255 - _log[b]];

    public byte Inverse(int a) => _exp[255 - _log[a]];
}

/// <summary>
/// Reed-Solomon codes over GF(2⁸) as used by QR codes (generator roots α⁰ … α^(n−1)) and Data Matrix (roots α¹ … αⁿ).
/// Decoding: syndromes, Berlekamp–Massey for the error locator, Chien search, Forney for the values — corrects up to n/2 errors.
/// </summary>
internal sealed class ReedSolomon(GaloisField field, int firstRoot)
{
    /// <summary>Codes of QR symbols.</summary>
    public static readonly ReedSolomon Qr = new(GaloisField.Qr, 0);

    /// <summary>Codes of Data Matrix symbols.</summary>
    public static readonly ReedSolomon DataMatrix = new(GaloisField.DataMatrix, 1);

    private readonly Dictionary<int, byte[]> _generators = [];

    /// <summary>The <paramref name="count"/> check codewords for the data (highest power first, as the symbols store them).</summary>
    public byte[] Encode(ReadOnlySpan<byte> data, int count)
    {
        var generator = Generator(count);
        var remainder = new byte[count];
        foreach (var d in data)
        {
            var factor = d ^ remainder[0];
            Array.Copy(remainder, 1, remainder, 0, count - 1);
            remainder[count - 1] = 0;
            if (factor == 0) continue;
            for (var i = 0; i < count; i++) remainder[i] ^= field.Multiply(generator[i + 1], factor);
        }
        return remainder;
    }

    private byte[] Generator(int count)
    {
        lock (_generators)
        {
            if (_generators.TryGetValue(count, out var known)) return known;
            // ∏ (x − α^(firstRoot + i)), coefficients from the highest power down; the leading 1 at index 0
            var g = new byte[count + 1];
            g[0] = 1;
            for (var i = 0; i < count; i++)
            {
                var root = field.Exp(firstRoot + i);
                for (var j = i + 1; j > 0; j--) g[j] = (byte)(g[j] ^ field.Multiply(g[j - 1], root));
            }
            return _generators[count] = g;
        }
    }

    /// <summary>Corrects a block (data followed by <paramref name="checkCount"/> check codewords) in place.</summary>
    /// <returns>Number of corrected codewords; −1 if there are too many errors.</returns>
    public int Correct(Span<byte> block, int checkCount)
    {
        var n = block.Length;
        // syndromes S_i = r(α^(firstRoot + i)), the block read as a polynomial with the first codeword as the highest power
        Span<byte> syndromes = stackalloc byte[checkCount];
        var clean = true;
        for (var i = 0; i < checkCount; i++)
        {
            var root = field.Exp(firstRoot + i);
            var s = 0;
            foreach (var c in block) s = field.Multiply(s, root) ^ c;
            syndromes[i] = (byte)s;
            clean &= s == 0;
        }
        if (clean) return 0;

        // Berlekamp–Massey: error locator Λ(x), lowest power first
        Span<byte> lambda = stackalloc byte[checkCount + 1];
        Span<byte> previous = stackalloc byte[checkCount + 1];
        Span<byte> temp = stackalloc byte[checkCount + 1];
        lambda.Clear();
        previous.Clear();
        lambda[0] = previous[0] = 1;
        int length = 0, shift = 1;
        byte lastDiscrepancy = 1;
        for (var r = 0; r < checkCount; r++)
        {
            var delta = (int)syndromes[r];
            for (var i = 1; i <= length; i++) delta ^= field.Multiply(lambda[i], syndromes[r - i]);
            if (delta == 0)
            {
                shift++;
                continue;
            }
            var scale = field.Divide(delta, lastDiscrepancy);
            lambda.CopyTo(temp);
            for (var i = 0; i + shift <= checkCount; i++) lambda[i + shift] ^= field.Multiply(scale, previous[i]);
            if (2 * length <= r)
            {
                length = r + 1 - length;
                temp.CopyTo(previous);
                lastDiscrepancy = (byte)delta;
                shift = 1;
            }
            else shift++;
        }
        if (length == 0 || 2 * length > checkCount) return -1;

        // Chien search: position p (counted from the end) is wrong where Λ(α^(−p)) = 0
        Span<int> positions = stackalloc int[length];
        var found = 0;
        for (var p = 0; p < n && found <= length; p++)
        {
            var x = field.Exp(255 - p % 255);   // α^(−p)
            var v = 0;
            for (var i = length; i >= 0; i--) v = field.Multiply(v, x) ^ lambda[i];
            if (v != 0) continue;
            if (found == length) return -1;
            positions[found++] = p;
        }
        if (found != length) return -1;

        // Forney: Ω(x) = S(x)·Λ(x) mod x^checkCount; value = X^(1 − firstRoot) · Ω(X⁻¹) / Λ'(X⁻¹)
        Span<byte> omega = stackalloc byte[checkCount];
        omega.Clear();
        for (var i = 0; i < checkCount; i++)
            for (var j = 0; j <= length && i + j < checkCount; j++)
                omega[i + j] ^= field.Multiply(syndromes[i], lambda[j]);
        for (var k = 0; k < found; k++)
        {
            var p = positions[k];
            var xInverse = field.Exp(255 - p % 255);
            var o = 0;
            for (var i = checkCount - 1; i >= 0; i--) o = field.Multiply(o, xInverse) ^ omega[i];
            // formal derivative: only odd powers remain
            var d = 0;
            for (var i = 1; i <= length; i += 2)
            {
                var term = (int)lambda[i];
                for (var t = 0; t < i - 1; t++) term = field.Multiply(term, xInverse);
                d ^= term;
            }
            if (d == 0) return -1;
            var value = field.Divide(o, d);
            // X^(1 − firstRoot): firstRoot 0 → × X, firstRoot 1 → × 1
            var xp = field.Exp(p % 255);
            for (var t = 0; t < 1 - firstRoot; t++) value = field.Multiply(value, xp);
            for (var t = 0; t < firstRoot - 1; t++) value = field.Divide(value, xp);
            block[n - 1 - p] ^= value;
        }
        return found;
    }
}
