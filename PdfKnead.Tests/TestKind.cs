namespace PdfKnead.Tests;

/// <summary>
/// Kinds of tests (xUnit traits). Tests with a fixed time limit carry <c>[Trait(TestKind.Name, TestKind.Time)]</c>: the publish scripts leave
/// them out (<c>--filter Art!=Zeit</c>) — a slower computer or the load of the full run must not stop a build of a working program. They run
/// on their own (<c>dotnet test --filter Art=Zeit</c>).
/// </summary>
internal static class TestKind
{
    /// <summary>Name of the trait.</summary>
    public const string Name = "Art";

    /// <summary>A test with a fixed time limit.</summary>
    public const string Time = "Zeit";

    /// <summary>
    /// Factor on every time limit: by default ten, so that a limit still catches a hang or an endless loop but not a slow computer;
    /// <c>PDFKNEAD_ZEITFAKTOR=1</c> checks the limits as measured (for a measuring run on a quiet computer).
    /// </summary>
    public static double Factor { get; } =
        double.TryParse(Environment.GetEnvironmentVariable("PDFKNEAD_ZEITFAKTOR"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var f) && f > 0 ? f : 10;
}
