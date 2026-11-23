using System.Text;
using System.Text.RegularExpressions;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.IO;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Parsing;

namespace PdfKnead.Tests.Pdf;

/// <summary>Annex F (linearized files) as a reader: the linearization parameters and hint tables are passed over, sentence by sentence.</summary>
public sealed partial class FileStructureNormTests
{
    /// <summary>Linearized files built for the tests of Annex F below.</summary>
    private static class LinearizedVariants
    {
        /// <summary>
        /// The linearized file of <see cref="LinearizedFile"/> with one value of its linearization parameter dictionary wrong, the same number of
        /// bytes long so that no offset moves: /H naming offset 0 and length 0, /E 0, /N 1 (two pages there) or /T 0.
        /// </summary>
        internal static byte[] WithWrong(string key)
        {
            var text = Encoding.Latin1.GetString(LinearizedFile());
            var (pattern, replacement) = key switch
            {
                "H" => (@"/H \[\d{10} \d{10}\]", $"/H [{0:D10} {0:D10}]"),
                "E" => (@"/E \d{10}", $"/E {0:D10}"),
                "N" => ("/N 2", "/N 1"),
                "T" => (@"/T \d{10}", $"/T {0:D10}"),
                _ => throw new ArgumentOutOfRangeException(nameof(key)),
            };
            var changed = new Regex(pattern).Replace(text, replacement, 1);
            Assert.NotEqual(text, changed);
            Assert.Equal(text.Length, changed.Length);
            return Encoding.Latin1.GetBytes(changed);
        }

        /// <summary>
        /// A linearized file of two pages with object streams and cross-reference streams in place of tables (F.3.1, sentences 10 to 16).
        /// Second group, numbered from 1: page tree (1), second page (2), its content (3), main cross-reference stream (4), an object stream (5)
        /// holding the resources of the second page (6). First group: linearization parameter dictionary (7), first-page cross-reference stream
        /// (8) with /Prev to the main one, catalogue (9), first page (10), its content (11), hint stream (12), an object stream (13) holding the
        /// resources of the first page (14) — the compressed objects with the highest numbers of their section. The hint stream holds a page
        /// offset hint table whose positions are wrong, and /H names the wrong place; the last startxref names the first-page stream.
        /// </summary>
        internal static byte[] WithObjectStreams()
        {
            var known = new Dictionary<int, long>();
            var (file, offsets) = Build(known);
            return Build(offsets).File is var second && second.Length == file.Length ? second : throw new InvalidOperationException("layout moved");

            static (byte[] File, Dictionary<int, long> Offsets) Build(Dictionary<int, long> known)
            {
                var output = new MemoryStream();
                void Text(string s) => output.Write(Encoding.Latin1.GetBytes(s));
                var offsets = new Dictionary<int, long>();
                long At(int n) => known.GetValueOrDefault(n);
                void Obj(int number, string body)
                {
                    offsets[number] = output.Position;
                    Text($"{number} 0 obj\n{body}\nendobj\n");
                }
                void Stream(int number, string dict, byte[] data)
                {
                    offsets[number] = output.Position;
                    Text($"{number} 0 obj\n<< {dict} /Length {data.Length} >>\nstream\n");
                    output.Write(data);
                    Text("\nendstream\nendobj\n");
                }
                static byte[] ObjStm(int number, string body) => Encoding.Latin1.GetBytes($"{number} 0 {body}");
                static byte[] Rows(params (byte Type, long F2, int F3)[] rows) =>
                    [.. rows.SelectMany(r => new[] { r.Type, (byte)(r.F2 >> 16), (byte)(r.F2 >> 8), (byte)r.F2, (byte)r.F3 })];
                const string Width = "/W [1 3 1]";
                var length = known.GetValueOrDefault(-1);

                Text("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n");
                // /H names a place where no hint stream stands (the first-page cross-reference stream), /T the main cross-reference stream
                Obj(7, $"<< /Linearized 1 /L {length:D10} /H [{At(8):D10} {16:D10}] /O 10 /E {At(1):D10} /N 2 /T {At(4):D10} >>");
                Stream(8, $"/Type /XRef /Size 15 /Index [7 8] {Width} /Root 9 0 R /ID [<{new string('B', 32)}> <{new string('B', 32)}>] /Prev {At(4):D10}",
                    Rows((1, At(7), 0), (1, At(8), 0), (1, At(9), 0), (1, At(10), 0), (1, At(11), 0), (1, At(12), 0), (1, At(13), 0), (2, 13, 0)));
                Text("startxref\n0\n%%EOF\n");
                Obj(9, "<< /Type /Catalog /Pages 1 0 R >>");
                Obj(10, "<< /Type /Page /Parent 1 0 R /MediaBox [0 0 200 200] /Resources 14 0 R /Contents 11 0 R >>");
                Obj(11, "<< /Length 18 >>\nstream\n/G1 gs 0 0 m 1 1 l\nendstream");
                // a page offset hint table (F.4.2) whose first page's object count and positions say nothing true
                Stream(12, "/S 0", [0, 0, 0, 99, 0, 0, 0, 7, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0]);
                var first = ObjStm(14, "<< /ExtGState << /G1 << /CA 0.5 >> >> >>");
                Stream(13, $"/Type /ObjStm /N 1 /First {first.Length - "<< /ExtGState << /G1 << /CA 0.5 >> >> >>".Length}", first);

                Obj(1, "<< /Type /Pages /Kids [10 0 R 2 0 R] /Count 2 >>");
                Obj(2, "<< /Type /Page /Parent 1 0 R /MediaBox [0 0 300 300] /Resources 6 0 R /Contents 3 0 R >>");
                Obj(3, "<< /Length 18 >>\nstream\n/G2 gs 0 0 m 2 2 l\nendstream");
                var second = ObjStm(6, "<< /ExtGState << /G2 << /CA 0.25 >> >> >>");
                Stream(5, $"/Type /ObjStm /N 1 /First {second.Length - "<< /ExtGState << /G2 << /CA 0.25 >> >> >>".Length}", second);
                offsets[4] = output.Position;
                Stream(4, $"/Type /XRef /Size 15 /Index [0 7] {Width}",
                    Rows((0, 0, 255), (1, At(1), 0), (1, At(2), 0), (1, At(3), 0), (1, At(4), 0), (1, At(5), 0), (2, 5, 0)));
                Text($"startxref\n{At(8):D10}\n%%EOF\n");   // as wide as the final offset, so that nothing moves
                offsets[-1] = output.Position;
                return (output.ToArray(), offsets);
            }
        }
    }

    /// <summary>
    /// F.3.3, Table F.1 (/H, /E, /N; /T): PDF-Knead writes no linearized files and reads them as ordinary ones (decided on 21.11.2026) — the
    /// offset and length of the hint stream, the end of the first page, the number of pages and the offset of the main cross-reference table
    /// are never relied on. With any of them wrong the file reads as with the right values: two pages in the order of the page tree, their
    /// content the same, nothing rebuilt.
    /// </summary>
    [Theory]
    [InlineData("H")]
    [InlineData("E")]
    [InlineData("N")]
    [InlineData("T")]
    public void Linearized_WrongParameter_ReadAsOrdinaryFile(string key)
    {
        using var right = PdfDocument.Open(LinearizedFile());
        using var doc = PdfDocument.Open(LinearizedVariants.WithWrong(key));
        Assert.False(doc.WasRepaired);
        Assert.Equal(2, doc.PageCount);
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(right.Pages[i].MediaBox, doc.Pages[i].MediaBox);
            var expected = right.DecodeStream((PdfStream)right.Pages[i].Dictionary.Get(PdfName.Contents)!).Data;
            Assert.Equal(expected, doc.DecodeStream((PdfStream)doc.Pages[i].Dictionary.Get(PdfName.Contents)!).Data);
        }
    }

    /// <summary>
    /// F.4.1, sentences 13 and 19 to 26, with F.3.1, sentences 10 to 16: in a linearized file with object streams and cross-reference streams a
    /// hint table gives at most a byte range for a compressed object; PDF-Knead reads no hint tables and finds every object — the compressed
    /// ones too — over the cross-reference streams, from the last startxref to the first-page stream and over its /Prev to the main one. The hint
    /// stream and /H name wrong places: both pages are there with their resources out of the object streams, nothing rebuilt.
    /// </summary>
    [Fact]
    public void Linearized_WithObjectStreams_CompressedObjectsFoundOverTheCrossReferenceStreams()
    {
        var file = LinearizedVariants.WithObjectStreams();
        var text = Encoding.Latin1.GetString(file);
        Assert.Contains($"/L {file.Length:D10}", text);
        Assert.True(text.IndexOf("endobj", StringComparison.Ordinal) < 1024);
        using (var source = PdfSource.FromBytes(file))
        {
            var table = XRefReader.Read(source, 0);
            Assert.Equal(new XRefEntry(XRefEntryType.Compressed, 13, 0), table.Entries[14]);
            Assert.Equal(new XRefEntry(XRefEntryType.Compressed, 5, 0), table.Entries[6]);
        }
        using var doc = PdfDocument.Open(file);
        Assert.False(doc.WasRepaired);
        Assert.Equal(2, doc.PageCount);
        Assert.Equal(200, doc.Pages[0].MediaBox.Width);
        Assert.Equal(300, doc.Pages[1].MediaBox.Width);
        double? Opacity(int page, string state) => doc.Pages[page].Resources!.GetDictionary(PdfName.Get("ExtGState"))!.GetDictionary(PdfName.Get(state))!.GetNumber(PdfName.Get("CA"));
        Assert.Equal(0.5, Opacity(0, "G1"));
        Assert.Equal(0.25, Opacity(1, "G2"));
    }
}
