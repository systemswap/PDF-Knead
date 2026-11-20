using System.Text;
using PdfKnead.Core.Pdf.Editing;

namespace PdfKnead.Tests.Pdf;

/// <summary>The <c>/Tx</c> section of a field's appearance (ISO 32000-2, 12.7.4.3, sentences 46–47; docs/FEHLERGEDAECHTNIS.md, No. 815).</summary>
public sealed class FieldFrameTests
{
    /// <summary>The section is found with its operands and nested marked content; text outside it is noted; an unended one is refused.</summary>
    [Theory]
    [InlineData("1 g 0 0 9 9 re f /Tx BMC q BT (a) Tj ET Q EMC", true, false)]
    [InlineData("/Tx BMC /Span BMC (a) Tj EMC EMC 0 0 9 9 re S", true, false)]
    [InlineData("BT (a) Tj ET", false, true)]
    [InlineData("0 0 9 9 re f", false, false)]
    public void Section_FoundWithItsOperands(string content, bool found, bool textOutside)
    {
        var data = Encoding.ASCII.GetBytes(content);
        var section = FieldFrame.Section(data);
        Assert.NotNull(section);
        var (start, end, showsText) = section!.Value;
        Assert.Equal(found, start >= 0);
        Assert.Equal(textOutside, showsText);
        if (found) Assert.Matches(@"^/Tx BMC .*EMC$", content[start..end].Trim());   // from the end of the operator before it
        Assert.Null(FieldFrame.Section("/Tx BMC (a) Tj"u8.ToArray()));
    }
}
