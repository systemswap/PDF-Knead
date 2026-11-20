namespace PdfKnead.EInvoice.Tests;

/// <summary>
/// The tests that switch the language of the program (it is one setting for the whole process): they run alone, after the others, never
/// beside a test that expects the German texts. Every class with <c>Tr.Use</c> belongs here (as in PdfKnead.Tests the UI collection).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LanguageCollection
{
    public const string Name = "Sprache";
}
