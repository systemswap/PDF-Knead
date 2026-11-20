using System.Diagnostics;
using System.Runtime.CompilerServices;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Security;
using Xunit.Abstractions;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Broken and malicious files: the reader may only throw <see cref="PdfException"/>, never
/// crash or hang. Covered by randomly corrupted test PDFs (fixed seed, so that a failure
/// stays reproducible) and, if present, all PDFs in the TestPDFs folder.
/// </summary>
public sealed class RobustnessTests(ITestOutputHelper output)
{
    /// <summary>Opens the file and touches everything the user interface needs later.</summary>
    private static void Exercise(byte[] bytes, string? password = null)
    {
        try
        {
            using var doc = PdfDocument.Open(bytes, password);
            Touch(doc);
        }
        catch (PdfException)
        {
            // allowed
        }
    }

    private static void Touch(PdfDocument doc)
    {
        _ = doc.Info;
        _ = doc.Version;
        foreach (var page in doc.Pages)
        {
            _ = PageFormat.Describe(page.Width, page.Height);
            if (page.Dictionary.Get(PdfName.Contents) is PdfStream s) doc.GetStreamData(s);
        }
    }

    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void Fuzz_MutatedFiles_OnlyPdfExceptions()
    {
        var random = new Random(20261015);
        var bases = new[]
        {
            TestPdf.Simple().Build(),
            TestPdf.Simple().Build(XRefStyle.Stream),
            TestPdf.Simple().Build(cipher: PdfCipher.Aes128, user: ""),
            TestPdf.Simple().Build(XRefStyle.Stream, PdfCipher.Aes256, user: ""),
        };

        var watch = Stopwatch.StartNew();
        for (var round = 0; round < 1500; round++)
        {
            var bytes = (byte[])bases[round % bases.Length].Clone();
            switch (random.Next(4))
            {
                case 0:   // change single bytes
                    for (var k = random.Next(1, 20); k > 0; k--) bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
                    break;
                case 1:   // truncate
                    bytes = bytes[..random.Next(bytes.Length)];
                    break;
                case 2:   // delete a piece
                    var a = random.Next(bytes.Length);
                    var b = Math.Min(bytes.Length, a + random.Next(1, 200));
                    bytes = [.. bytes[..a], .. bytes[b..]];
                    break;
                default:  // duplicate a piece
                    var c = random.Next(bytes.Length);
                    var d = Math.Min(bytes.Length, c + random.Next(1, 300));
                    bytes = [.. bytes[..d], .. bytes[c..d], .. bytes[d..]];
                    break;
            }

            var started = watch.Elapsed;
            Exercise(bytes);
            Assert.True(watch.Elapsed - started < TimeSpan.FromSeconds(5 * TestKind.Factor), $"Runde {round} hängt");
        }
    }

    private static string TestPdfsFolder([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "TestPDFs"));

    /// <summary>
    /// All PDFs under TestPDFs/ (not in Git; own files, files from the public test collection …). Without the
    /// folder there is nothing to check. Expected: no errors other than PdfException.
    /// </summary>
    [Fact]
    public void Corpus_OpensWithoutCrashing()
    {
        var folder = TestPdfsFolder();
        if (!Directory.Exists(folder)) return;
        var files = Directory.EnumerateFiles(folder, "*.pdf", SearchOption.AllDirectories).ToList();
        var passwords = KnownPasswords(folder);
        int ok = 0, password = 0, rejected = 0, repaired = 0, withPassword = 0;
        foreach (var file in files)
        {
            try
            {
                var known = passwords.GetValueOrDefault(Path.GetFileName(file));
                using var doc = PdfDocument.Open(file, known);
                Touch(doc);
                ok++;
                if (doc.WasRepaired) repaired++;
                if (known is not null) withPassword++;
            }
            catch (PdfPasswordException)
            {
                password++;
            }
            catch (PdfException ex)
            {
                rejected++;
                output.WriteLine($"abgelehnt: {Path.GetRelativePath(folder, file)} — {ex.Message}");
            }
            catch (Exception ex)
            {
                Assert.Fail($"{Path.GetRelativePath(folder, file)}: {ex}");
            }
        }
        output.WriteLine($"{files.Count} Dateien: {ok} gelesen ({repaired} repariert, {withPassword} mit bekanntem Passwort), " +
                         $"{password} brauchen ein unbekanntes Passwort, {rejected} abgelehnt");
        // With a known password every file must open; this checks decryption on foreign files
        Assert.Equal(passwords.Count(p => files.Any(f => Path.GetFileName(f) == p.Key)), withPassword);
    }

    /// <summary>Reads "_passwoerter.txt" in every subfolder: one "file.pdf&lt;Tab&gt;password" per line (for the public test collection taken from its test list).</summary>
    private static Dictionary<string, string> KnownPasswords(string folder)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in Directory.EnumerateFiles(folder, "_passwoerter.txt", SearchOption.AllDirectories))
            foreach (var line in File.ReadAllLines(list))
                if (line.Split('\t', 2) is [var name, var pwd]) result[name] = pwd;
        return result;
    }
}
