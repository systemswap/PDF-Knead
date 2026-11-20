using PdfKnead.Core.Pdf.Accessibility;

namespace PdfKnead.Tests.Pdf;

/// <summary>The containment rules of the standard structure types (ISO/TS 32005, Table 5; docs/FEHLERGEDAECHTNIS.md, No. 507).</summary>
public class ContainmentTests
{
    /// <summary>Pairs whose answer is known: what is forbidden, what is allowed, how often, which types hold no content.</summary>
    [Fact]
    public void KnownPairs()
    {
        Assert.False(Containment.Allowed("P", "P"));
        Assert.False(Containment.Allowed("Table", "P"));
        Assert.False(Containment.Allowed("Table", "Table"));
        Assert.False(Containment.Allowed("TR", "Table"));
        Assert.False(Containment.Allowed("L", "P"));
        Assert.False(Containment.Allowed("Document", "Span"));
        Assert.True(Containment.Allowed("TR", "TD"));
        Assert.True(Containment.Allowed("LI", "LBody"));
        Assert.True(Containment.Allowed("P", "Table"));
        Assert.True(Containment.Allowed("P", "L"));
        Assert.True(Containment.Allowed("Div", "P"));
        Assert.Equal(1, Containment.Most("Document", "H"));
        Assert.Null(Containment.Most("Div", "P"));
        Assert.False(Containment.MayHoldContent("Table"));
        Assert.False(Containment.MayHoldContent("TR"));
        Assert.True(Containment.MayHoldContent("TD"));
        Assert.True(Containment.MayHoldContent("P"));
        Assert.True(Containment.Known("H3"));
        Assert.False(Containment.Known("TableCell"));
    }
}
