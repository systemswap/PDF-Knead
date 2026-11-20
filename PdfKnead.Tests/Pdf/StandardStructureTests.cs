using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The standard structure types of the two standard namespaces and the resolution of an element through /RoleMap and /RoleMapNS
/// (ISO 32000-2, 14.7.3, 14.7.4.2, 14.8.6, Annex M; docs/FEHLERGEDAECHTNIS.md, No. 628).
/// </summary>
public class StandardStructureTests
{
    private static PdfDictionary Namespace(string name, PdfDictionary? roles = null)
    {
        var ns = new PdfDictionary();
        ns.Set(PdfName.Type, PdfName.Get("Namespace"));
        ns.Set(PdfName.Get("NS"), new PdfString(System.Text.Encoding.ASCII.GetBytes(name)));
        if (roles is not null) ns.Set(PdfName.Get("RoleMapNS"), roles);
        return ns;
    }

    private static PdfDictionary Element(string type, PdfDictionary? ns = null)
    {
        var element = new PdfDictionary();
        element.Set(PdfName.Type, PdfName.Get("StructElem"));
        element.Set(PdfName.Get("S"), PdfName.Get(type));
        if (ns is not null) element.Set(PdfName.Get("NS"), ns);
        return element;
    }

    private static PdfDictionary Roles(params (string Key, PdfObject Value)[] entries)
    {
        var roles = new PdfDictionary();
        foreach (var (key, value) in entries) roles.Set(PdfName.Get(key), value);
        return roles;
    }

    private static PdfDictionary RootWith(PdfDictionary? roleMap)
    {
        var root = new PdfDictionary();
        root.Set(PdfName.Type, PdfName.Get("StructTreeRoot"));
        if (roleMap is not null) root.Set(PdfName.Get("RoleMap"), roleMap);
        return root;
    }

    /// <summary>The lists of Annex M: the types only one of the namespaces defines, and Hn above 6 only in 2.0.</summary>
    [Fact]
    public void Lists_FollowAnnexM()
    {
        foreach (var type in (string[])["Note", "Code", "Quote", "Reference", "BibEntry", "Art", "BlockQuote", "TOC", "TOCI", "Index", "Private"])
        {
            Assert.True(StandardStructure.IsStandard(type, StructureSpace.Pdf17), type);
            Assert.False(StandardStructure.IsStandard(type, StructureSpace.Pdf20), type);
        }
        foreach (var type in (string[])["DocumentFragment", "Aside", "Title", "FENote", "Sub", "Em", "Strong", "Artifact", "H7"])
        {
            Assert.False(StandardStructure.IsStandard(type, StructureSpace.Pdf17), type);
            Assert.True(StandardStructure.IsStandard(type, StructureSpace.Pdf20), type);
        }
        foreach (var type in (string[])["Document", "P", "H", "H1", "H6", "Table", "Lbl", "Figure", "NonStruct"])
            Assert.True(StandardStructure.IsStandard(type, StructureSpace.Pdf17) && StandardStructure.IsStandard(type, StructureSpace.Pdf20), type);
        foreach (var type in (string[])["H0", "H01", "Heading", "p", ""])
            Assert.False(StandardStructure.IsStandard(type, StructureSpace.Pdf20), type);
        Assert.False(StandardStructure.IsStandard("P", StructureSpace.Custom));
        Assert.True(StandardStructure.IsStandard("math", StructureSpace.MathML));
    }

    /// <summary>Without /NS an element is in the namespace for PDF 1.7; with one, in the namespace it names.</summary>
    [Fact]
    public void Namespace_DecidesTheList()
    {
        var root = RootWith(null);
        Assert.Equal(new StandardType(StructureSpace.Pdf17, "Note"), StandardStructure.Resolve(Element("Note"), root));
        Assert.Equal(new StandardType(StructureSpace.Pdf20, "FENote"), StandardStructure.Resolve(Element("FENote", Namespace(StandardStructure.Pdf20Namespace)), root));
        // Note is no type of the namespace for 2.0, and FENote none of the default namespace
        Assert.Null(StandardStructure.Resolve(Element("Note", Namespace(StandardStructure.Pdf20Namespace)), root));
        Assert.Null(StandardStructure.Resolve(Element("FENote"), root));
        Assert.Equal(new StandardType(StructureSpace.MathML, "math"), StandardStructure.Resolve(Element("math", Namespace(StandardStructure.MathMLNamespace)), root));
        // a namespace of its own without a role map: no standard type
        Assert.Null(StandardStructure.Resolve(Element("Para", Namespace("http://example.org/own")), root));
    }

    /// <summary>
    /// A single name in /RoleMapNS names a type of the default namespace (ISO 32000-2, Table 356): the chain ends there and does not go on
    /// through the role map of the element's own namespace. Before, it stayed in that namespace and went on: Para → P → Span gave Span.
    /// </summary>
    [Fact]
    public void RoleMapNs_SingleName_IsInTheDefaultNamespace()
    {
        var own = Namespace("http://example.org/own", Roles(("Para", PdfName.Get("P")), ("P", PdfName.Get("Span"))));
        Assert.Equal(new StandardType(StructureSpace.Pdf17, "P"), StandardStructure.Resolve(Element("Para", own), RootWith(null)));
        Assert.Equal("P", StructureRoles.Resolve(Element("Para", own), RootWith(null)));
    }

    /// <summary>An array in /RoleMapNS names a type and its namespace; the chain goes on through that namespace.</summary>
    [Fact]
    public void RoleMapNs_Array_ChangesTheNamespace()
    {
        var pdf20 = Namespace(StandardStructure.Pdf20Namespace);
        var middle = Namespace("http://example.org/middle", Roles(("Box", new PdfArray([PdfName.Get("Aside"), pdf20]))));
        var own = Namespace("http://example.org/own", Roles(("Sidebar", new PdfArray([PdfName.Get("Box"), middle]))));
        Assert.Equal(new StandardType(StructureSpace.Pdf20, "Aside"), StandardStructure.Resolve(Element("Sidebar", own), RootWith(null)));
    }

    /// <summary>
    /// A circle in the role map without a standard type on it stands for nothing (ISO 32000-2, 14.7.3, note 2). Before, the chain stopped after
    /// 20 steps at whichever type it had reached.
    /// </summary>
    [Fact]
    public void RoleMap_Circle_StandsForNothing()
    {
        var root = RootWith(Roles(("Alpha", PdfName.Get("Beta")), ("Beta", PdfName.Get("Alpha"))));
        Assert.Null(StandardStructure.Resolve(Element("Alpha"), root));
        Assert.Null(StructureRoles.Resolve(Element("Alpha"), root));
    }

    /// <summary>
    /// The role map is followed until it reaches a standard type (14.7.3, note 2), and a standard type with an entry of its own is mapped all
    /// the same (14.7.3): a two-way map ends at the first standard type.
    /// </summary>
    [Fact]
    public void RoleMap_StopsAtTheFirstStandardType()
    {
        var root = RootWith(Roles(("Heading", PdfName.Get("H1")), ("H1", PdfName.Get("Heading")), ("P", PdfName.Get("Span"))));
        Assert.Equal(new StandardType(StructureSpace.Pdf17, "H1"), StandardStructure.Resolve(Element("Heading"), root));
        // H1 has an entry: mapped to Heading, which is no standard type, and on to H1
        Assert.Equal(new StandardType(StructureSpace.Pdf17, "H1"), StandardStructure.Resolve(Element("H1"), root));
        Assert.Equal(new StandardType(StructureSpace.Pdf17, "Span"), StandardStructure.Resolve(Element("P"), root));
    }
}
