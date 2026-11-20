using System.Text;

namespace PdfKnead.Tests;

/// <summary>Test PDFs that the tests generate themselves instead of reading them from the repository.</summary>
internal static class TestFiles
{
    /// <summary>Writes the smallest valid PDF 1.7 file with one empty A4 page into its own temporary folder.</summary>
    /// <param name="name">File name of the PDF.</param>
    /// <returns>The full path of the written file.</returns>
    public static string MinimalPdf(string name = "leer.pdf")
    {
        var dir = Directory.CreateTempSubdirectory("pdf-knead-test-").FullName;
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, MinimalPdfBytes());
        return path;
    }

    /// <summary>Writes the bytes as a file into its own temporary folder.</summary>
    /// <param name="bytes">File content.</param>
    /// <param name="name">File name.</param>
    /// <returns>The full path of the written file.</returns>
    public static string Write(byte[] bytes, string name)
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("pdf-knead-test-").FullName, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public static byte[] MinimalPdfBytes()
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n%âãÏÓ\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Builds a PDF from objects given as text: string = object, (dictionary content, stream content) = stream with /Length; the classic
    /// cross-reference table and the trailer are written to fit. Shared with the tests of the e-invoice module (linked there).
    /// </summary>
    public static byte[] Build(Dictionary<int, object> objects, string trailerExtra = "")
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        W("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n");
        var max = objects.Keys.Max();
        var offsets = new long[max + 1];
        foreach (var (n, o) in objects.OrderBy(k => k.Key))
        {
            offsets[n] = ms.Position;
            if (o is string s)
            {
                W($"{n} 0 obj\n{s}\nendobj\n");
            }
            else if (o is (string dict, string content))
            {
                var bytes = Encoding.Latin1.GetBytes(content);
                W($"{n} 0 obj\n<<{dict}/Length {bytes.Length}>>\nstream\n");
                ms.Write(bytes);
                W("\nendstream\nendobj\n");
            }
        }
        var xref = ms.Position;
        W($"xref\n0 {max + 1}\n0000000000 65535 f \n");
        for (var i = 1; i <= max; i++) W(offsets[i] == 0 ? "0000000000 65535 f \n" : $"{offsets[i]:D10} 00000 n \n");
        W($"trailer\n<</Size {max + 1}/Root 1 0 R{trailerExtra}>>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }
}
