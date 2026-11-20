using System.Reflection;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// Every UI test class runs in <see cref="UiCollection"/> (docs/FEHLERGEDAECHTNIS.md, No. 627). All of them share the one UI thread of the
/// headless session: a class outside the collection runs in parallel with the others, and whenever one test waits (await) or lets the
/// dispatcher run (<see cref="UiSession.Capture"/>), the steps of another class's test run in the middle of it — a click then lands while
/// another window holds the mouse, and the test fails now and then, only in the full run. Before: 33 of the UI test classes had no collection.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class UiCollectionTests
{
    [Fact]
    public void EveryUiTestClass_RunsInTheUiCollection()
    {
        var outside = typeof(UiCollectionTests).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(UiCollectionTests).Namespace && t.IsClass && !t.IsAbstract && HasTests(t) && Collection(t) != nameof(UiCollection))
            .Select(t => t.Name)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.True(outside.Count == 0, $"Oberflächen-Testklassen außerhalb von {nameof(UiCollection)}: {string.Join(", ", outside)}");
    }

    /// <summary>Whether the class has a test method (Fact or Theory, also derived ones).</summary>
    private static bool HasTests(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Any(m => m.GetCustomAttributes<FactAttribute>(inherit: true).Any());

    /// <summary>The name of the collection the class is in, or null.</summary>
    private static string? Collection(Type type) =>
        type.GetCustomAttributesData().FirstOrDefault(a => a.AttributeType == typeof(CollectionAttribute))?.ConstructorArguments
            .Select(c => c.Value as string).FirstOrDefault(v => v is not null);
}
