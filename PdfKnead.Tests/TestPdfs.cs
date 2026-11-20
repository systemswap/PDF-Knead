using System.Runtime.CompilerServices;

namespace PdfKnead.Tests;

/// <summary>
/// The local test collection <c>TestPDFs/</c>. It is kept on the developer's machine only and is not part of the
/// repository, so a fresh clone does not have it; tests that read from it are reported as skipped instead of failing.
/// </summary>
internal static class TestPdfs
{
    /// <summary>Skip reason shown by the test runner.</summary>
    public const string MissingReason = "TestPDFs/ is missing: the test collection is kept locally only, not in the repository.";

    /// <summary>Whether a folder <c>TestPDFs</c> exists in the folder of the source file or one of its parents.</summary>
    /// <param name="source">Path of the calling source file (filled in by the compiler).</param>
    /// <returns><see langword="true"/> if the test collection was found.</returns>
    /// <summary>A file of a public test collection (TestPDFs/&lt;collection&gt;/&lt;name&gt;), found from the source file as above.</summary>
    public static string File(string collection, string name, [CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "TestPDFs"))) return Path.Combine(dir.FullName, "TestPDFs", collection, name);
        throw new FileNotFoundException("TestPDFs not found.", name);
    }

    public static bool Available([CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "TestPDFs"))) return true;
        return false;
    }

    /// <summary>
    /// The user's own PDFs directly in <c>TestPDFs/privat/</c> (a large plan for the timing tests): kept apart from the public collections,
    /// never shared, never in the repository. Empty if the folder is missing.
    /// </summary>
    /// <param name="source">Path of the calling source file (filled in by the compiler).</param>
    /// <returns>The files, by name.</returns>
    public static string[] Private([CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "TestPDFs")))
            {
                var folder = Path.Combine(dir.FullName, "TestPDFs", "privat");
                return Directory.Exists(folder) ? [.. Directory.GetFiles(folder, "*.pdf").Order(StringComparer.Ordinal)] : [];
            }
        return [];
    }
}

/// <summary>A <see cref="FactAttribute"/> that is skipped when the local test collection <c>TestPDFs/</c> is missing.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TestPdfsFactAttribute : FactAttribute
{
    /// <summary>Marks the test as skipped if <c>TestPDFs/</c> cannot be found.</summary>
    /// <param name="source">Path of the test's source file (filled in by the compiler).</param>
    public TestPdfsFactAttribute([CallerFilePath] string source = "")
    {
        if (!TestPdfs.Available(source)) Skip = TestPdfs.MissingReason;
    }
}

/// <summary>A <see cref="TheoryAttribute"/> that is skipped when the local test collection <c>TestPDFs/</c> is missing.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TestPdfsTheoryAttribute : TheoryAttribute
{
    /// <summary>Marks the test as skipped if <c>TestPDFs/</c> cannot be found.</summary>
    /// <param name="source">Path of the test's source file (filled in by the compiler).</param>
    public TestPdfsTheoryAttribute([CallerFilePath] string source = "")
    {
        if (!TestPdfs.Available(source)) Skip = TestPdfs.MissingReason;
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that is skipped when one collection of the local test collection (<c>TestPDFs/&lt;collection&gt;</c>) is
/// missing — a collection that came later (the e-invoices, 13.11.2026) may be missing on a machine that has the others.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TestPdfsCollectionFactAttribute : FactAttribute
{
    /// <summary>Marks the test as skipped if <c>TestPDFs/&lt;collection&gt;</c> cannot be found.</summary>
    /// <param name="collection">The folder below <c>TestPDFs/</c>, with „/“ between levels.</param>
    /// <param name="source">Path of the test's source file (filled in by the compiler).</param>
    public TestPdfsCollectionFactAttribute(string collection, [CallerFilePath] string source = "")
    {
        if (!TestPdfs.Available(source)) Skip = TestPdfs.MissingReason;
        else if (!Directory.Exists(TestPdfs.File(collection, "", source))) Skip = $"TestPDFs/{collection} is missing (kept locally only, see tools/pruefer/LIESMICH.md).";
    }
}
