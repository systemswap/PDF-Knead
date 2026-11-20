using System.Runtime.CompilerServices;
using PdfKnead.Core.Fonts;
using Xunit;

namespace PdfKnead.Tests.Fonts;

/// <summary>Tests for handwriting fonts embedded in the program and from the Schriften-Unterschrift folder, import as a copy, and the fallback for missing fonts.</summary>
public class SignatureFontTests
{
    /// <summary>
    /// Points the user font folder to a folder of the tests so that fonts imported on the developer's machine do not affect them, and
    /// puts the generated colour fonts (<see cref="TestColorFonts"/>) there.
    /// </summary>
    [ModuleInitializer]
    internal static void OwnFontFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pdf-knead-tests-schriften");
        SignatureFonts.UserFolderForTests = folder;
        TestColorFonts.WriteTo(folder);
    }

    [Fact]
    public void LicensedFonts_NotInProgram_ButFromFolderNextToIt()
    {
        var embedded = typeof(SignatureFonts).Assembly.GetManifestResourceNames().Where(n => n.StartsWith("Schriften/Handschriften/")).ToList();
        Assert.Equal(6, embedded.Count);
        Assert.DoesNotContain(embedded, n => n.Contains("Lucida"));
        Assert.Equal(SignatureFontOrigin.Folder, SignatureFonts.Origin("LucidaHandwriting.ttf"));
        Assert.Equal(SignatureFontOrigin.Embedded, SignatureFonts.Origin("PatrickHand-Regular.ttf"));
        Assert.Equal(SignatureFontOrigin.UserFolder, SignatureFonts.Origin(TestColorFonts.Sbix));
        // colour fonts from a folder bring their images with them
        Assert.All(TestColorFonts.All, file => Assert.NotNull(ColorGlyphs.For(EmbeddedFonts.HandwritingOpenType(file))));
    }

    [Fact]
    public void KuliTexture_WithAFreeFont()
    {
        // The ballpoint texture does not depend on any colour font, so it is available for every font with outlines
        var signature = new Core.Pdf.Editing.SignatureElement
        {
            Rect = new Core.Pdf.Document.PdfRectangle(0, 0, 200, 50), Name = "Max Mustermann", FontFile = "PatrickHand-Regular.ttf", Textured = true,
        };
        Assert.NotNull(Core.Pdf.Editing.ElementAppearance.For(signature).Texture);
    }

    [Fact]
    public void MissingFont_DrawsWithFallback()
    {
        // e.g. a signature whose font has been deleted from the folder
        Assert.Same(EmbeddedFonts.HandwritingOpenType(SignatureFonts.Fallback), EmbeddedFonts.HandwritingOpenType("GibtEsNicht.ttf"));
        Assert.Equal(EmbeddedFonts.LoadHandwriting(SignatureFonts.Fallback), EmbeddedFonts.LoadHandwriting("GibtEsNicht.ttf"));
    }

    [Fact]
    public void Import_CopiesFile_KeepsOriginal_RenamesAndRecognisesDuplicates()
    {
        var target = Directory.CreateTempSubdirectory("pdf-knead-schriften-").FullName;
        var sourceDir = Directory.CreateTempSubdirectory("pdf-knead-quelle-").FullName;
        try
        {
            // A built-in font, slightly modified (otherwise it would count as already present), under the name of an existing one
            var data = EmbeddedFonts.LoadHandwriting("GochiHand-Regular.ttf").Append((byte)0).ToArray();
            var source = Path.Combine(sourceDir, "PatrickHand-Regular.ttf");
            File.WriteAllBytes(source, data);

            var first = SignatureFonts.Import(source, [target]);
            Assert.Null(first.Error);
            Assert.False(first.AlreadyThere);
            Assert.Equal("PatrickHand-Regular (2).ttf", first.File);
            Assert.Equal("Gochi Hand", first.Family);
            Assert.True(File.Exists(source), "das Original bleibt");
            Assert.Equal(data, File.ReadAllBytes(Path.Combine(target, first.File!)));
            Assert.DoesNotContain(Directory.GetFiles(target), f => f.EndsWith(".neu"));

            // the same file again: nothing is copied
            var again = SignatureFonts.Import(source, [target]);
            Assert.True(again.AlreadyThere);
            Assert.Single(Directory.GetFiles(target));

            // a built-in font: already present
            var builtIn = Path.Combine(sourceDir, "Kopie.ttf");
            File.WriteAllBytes(builtIn, EmbeddedFonts.LoadHandwriting("Schoolbell-Regular.ttf"));
            Assert.Equal(("Schoolbell-Regular.ttf", true), (SignatureFonts.Import(builtIn, [target]).File, SignatureFonts.Import(builtIn, [target]).AlreadyThere));
        }
        finally
        {
            Directory.Delete(target, true);
            Directory.Delete(sourceDir, true);
        }
    }

    [Fact]
    public void Import_NextToProgramNotWritable_GoesToUserFolder()
    {
        var root = Directory.CreateTempSubdirectory("pdf-knead-schriften-").FullName;
        try
        {
            // The folder next to the program cannot be created because a file of the same name is in the way
            var blocked = Path.Combine(root, "Schriften-Unterschrift");
            File.WriteAllText(blocked, "");
            var user = Path.Combine(root, "benutzer");
            var source = Path.Combine(root, "Eigene.ttf");
            File.WriteAllBytes(source, EmbeddedFonts.LoadHandwriting("ComingSoon-Regular.ttf").Append((byte)0).ToArray());

            var result = SignatureFonts.Import(source, [blocked, user]);
            Assert.Null(result.Error);
            Assert.True(result.Fallback);
            Assert.True(File.Exists(Path.Combine(user, "Eigene.ttf")));

            // if the folder next to the program works, there is no fallback
            var program = Path.Combine(root, "programm");
            var second = Path.Combine(root, "Zweite.ttf");
            File.WriteAllBytes(second, EmbeddedFonts.LoadHandwriting("ComingSoon-Regular.ttf").Append((byte)1).ToArray());
            var normal = SignatureFonts.Import(second, [program, user]);
            Assert.False(normal.Fallback);
            Assert.True(File.Exists(Path.Combine(program, "Zweite.ttf")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Import_RejectsCutOrMisreportedFonts()
    {
        // Found by probing the built program: a colour font truncated to 3 000 bytes was accepted as a usable font
        var target = Directory.CreateTempSubdirectory("pdf-knead-schriften-").FullName;
        var sourceDir = Directory.CreateTempSubdirectory("pdf-knead-quelle-").FullName;
        try
        {
            var color = EmbeddedFonts.LoadHandwriting(TestColorFonts.Sbix);
            var gochi = EmbeddedFonts.LoadHandwriting("GochiHand-Regular.ttf");
            var cases = new Dictionary<string, byte[]>
            {
                ["Abgeschnitten.otf"] = color[..3000],
                ["Halb.ttf"] = gochi[..(gochi.Length / 2)],
                ["OhneUmrisse.ttf"] = Blank(gochi, "glyf"),
                ["OhneZeichen.ttf"] = Blank(gochi, "cmap"),
            };
            foreach (var (name, data) in cases)
            {
                var source = Path.Combine(sourceDir, name);
                File.WriteAllBytes(source, data);
                Assert.True(SignatureFonts.Import(source, [target]).Error == "Die Datei ist keine brauchbare Schrift.", name);
            }
            Assert.Empty(Directory.GetFiles(target));
        }
        finally
        {
            Directory.Delete(target, true);
            Directory.Delete(sourceDir, true);
        }

        // Overwrites a table with zeros (the file keeps its length and the table directory stays valid)
        static byte[] Blank(byte[] font, string tag)
        {
            var copy = font.ToArray();
            var table = OpenTypeFont.Parse(font).Table(tag)!.Value;
            Array.Clear(copy, table.Offset, table.Length);
            return copy;
        }
    }

    [Theory]
    [InlineData("bild.png", "Nur TrueType- oder OpenType-Schriften (.ttf, .otf) lassen sich importieren.")]
    [InlineData("kaputt.ttf", "Die Datei ist keine brauchbare Schrift.")]
    [InlineData("fehlt.otf", "Die Datei ließ sich nicht lesen.")]
    public void Import_RejectsWhatIsNoFont(string name, string error)
    {
        var target = Directory.CreateTempSubdirectory("pdf-knead-schriften-").FullName;
        var sourceDir = Directory.CreateTempSubdirectory("pdf-knead-quelle-").FullName;
        try
        {
            var source = Path.Combine(sourceDir, name);
            if (name != "fehlt.otf") File.WriteAllBytes(source, [0, 1, 0, 0, 0, 5, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13]);
            var result = SignatureFonts.Import(source, [target]);
            Assert.Equal(error, result.Error);
            Assert.Empty(Directory.GetFiles(target));
            Assert.Contains(error, SignatureFonts.Errors);
        }
        finally
        {
            Directory.Delete(target, true);
            Directory.Delete(sourceDir, true);
        }
    }
}

/// <summary>Changes the folder next to the tests, so it runs on its own; otherwise other tests would count the fonts in the middle of the change.</summary>
[Collection(nameof(SignatureFolderCollection))]
public class SignatureFolderTests
{
    [Fact]
    public void ImportNextToProgram_ThenRemove_KeepsLookOfOpenSignatures()
    {
        var sourceDir = Directory.CreateTempSubdirectory("pdf-knead-quelle-").FullName;
        string? copied = null;
        try
        {
            var source = Path.Combine(sourceDir, "Meine Handschrift.ttf");
            File.WriteAllBytes(source, EmbeddedFonts.LoadHandwriting("GochiHand-Regular.ttf").Append((byte)7).ToArray());
            var result = SignatureFonts.Import(source);
            Assert.Null(result.Error);
            Assert.False(result.Fallback);
            copied = Path.Combine(SignatureFonts.ProgramFolder, "Meine Handschrift.ttf");
            Assert.True(File.Exists(copied), "neben dem Programm, nicht im Benutzerordner");
            Assert.Contains("Meine Handschrift.ttf", EmbeddedFonts.Handwriting);
            Assert.Equal(SignatureFontOrigin.Folder, SignatureFonts.Origin("Meine Handschrift.ttf"));
            var font = EmbeddedFonts.HandwritingOpenType("Meine Handschrift.ttf");

            Assert.Null(SignatureFonts.Remove("Meine Handschrift.ttf"));
            Assert.False(File.Exists(copied));
            Assert.DoesNotContain("Meine Handschrift.ttf", EmbeddedFonts.Handwriting);
            // a signature already placed keeps its appearance until the program exits
            Assert.Same(font, EmbeddedFonts.HandwritingOpenType("Meine Handschrift.ttf"));

            Assert.Equal("Eingebaute Schriften lassen sich nicht entfernen.", SignatureFonts.Remove("PatrickHand-Regular.ttf"));
            Assert.Contains("PatrickHand-Regular.ttf", EmbeddedFonts.Handwriting);
        }
        finally
        {
            if (copied is not null && File.Exists(copied)) File.Delete(copied);
            SignatureFonts.Refresh();
            Directory.Delete(sourceDir, true);
        }
    }
}

[CollectionDefinition(nameof(SignatureFolderCollection), DisableParallelization = true)]
public sealed class SignatureFolderCollection;
