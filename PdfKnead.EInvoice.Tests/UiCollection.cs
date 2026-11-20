namespace PdfKnead.EInvoice.Tests;

/// <summary>
/// The tests that run the interface headless (<c>UiSession.Run</c>): one session and one set of settings for the whole process, so they run
/// one after the other, never beside each other (15.11.2026: a second class of them made seven tests of the first fail now and then).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UiCollection
{
    public const string Name = "Oberfläche";
}
