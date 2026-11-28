using PdfKnead.Core.Scanning;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package uni-15 (Unicode chapter 18, the Han ideographs): the row of the draft that still asked for a test — PDF-Knead has no ordering
/// of its own for ideographs; lists of names are sorted by the platform. The field names of the formula panel are tested in
/// <c>Uni16FieldNameOrderUiTests</c> (M7Tests.uni-16.cs), the list of printers comes from the system and is sorted the same way as the
/// scan profiles here.
/// </summary>
public sealed partial class M7NormTests
{
    /// <summary>
    /// UNI 18.1.7 sentences 20–21: scan profiles whose names are ideographs of the basic block and of extension B come back from the store in
    /// exactly the order of <see cref="StringComparer.CurrentCultureIgnoreCase"/> — the sorting of the platform, no radical-stroke order of
    /// PDF-Knead's own.
    /// </summary>
    [Fact]
    public void Uni15_ScanProfileNamesWithIdeographs_InTheOrderOfThePlatform()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pdfknead-uni15-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ScanProfileStore(folder);
            string[] names = ["\u4E2D", "\u4E00", "\U0002000B", "\u4E01", "\U00020000", "B\u00FCro"];
            foreach (var name in names) store.Save(new ScanProfile(name));
            Assert.Equal(names.Order(StringComparer.CurrentCultureIgnoreCase), store.Load().Select(p => p.Name));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
