using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfWriter
{
    private sealed partial class Session
    {
        // the structure of a file PDF-Knead makes itself (OwnStructureIntoContent): its section, its table, the current row and its number
        private PdfObject? _ownSection, _ownTable, _ownRow;
        private string? _ownRowNumber;

        /// <summary>
        /// The structure of an element fixed into an untagged file PDF-Knead makes itself (the redaction protocol; docs/ABLAUFPLAN.md, 6.6 i,
        /// item 3; docs/FEHLERGEDAECHTNIS.md, No. 658), as <see cref="PdfSaveOptions.ElementStructure"/> gives it: everything in one Sect of a
        /// new tree — the heading H1, paragraphs P, the cells TH (with /Scope /Column) and TD in rows TR of one Table; a row's number in the
        /// role keeps its cells together. „Artifact“ (the head of the table repeated on a further page) is artifact content.
        /// </summary>
        private ContentMark? OwnStructureIntoContent(string role, int pageNumber)
        {
            if (role == "Artifact") return new ContentMark(PdfName.Get("Artifact"), null);
            if (NewStructureTree() is not { } plan || PageIndexOf(pageNumber) is var page && page < 0) return null;
            _newTreeRoot = _newTreeNumber;
            if ((_ownSection ??= plan.NewElement("Sect", null, page)) is not { } section) return null;
            var parts = role.Split(' ', 2);
            var type = parts[0];
            var parent = section;
            if (type is "TH" or "TD")
            {
                if ((_ownTable ??= plan.NewElement("Table", section, page)) is not { } table) return null;
                var row = parts.Length > 1 ? parts[1] : "";
                if (_ownRow is null || _ownRowNumber != row)
                {
                    _ownRow = plan.NewElement("TR", table, page);
                    _ownRowNumber = row;
                }
                if (_ownRow is not { } current) return null;
                parent = current;
            }
            if (plan.NewElement(type, parent, page) is not { } made) return null;
            if (type == "TH")
            {
                var scope = new PdfDictionary();
                scope.Set(PdfName.Get("O"), PdfName.Get("Table"));
                scope.Set(PdfName.Get("Scope"), PdfName.Get("Column"));
                plan.AddAttributes(made, scope);
            }
            var mcid = plan.NextMcid(page);
            plan.AddMarkedContent(made, page, mcid);
            return new ContentMark(PdfName.Get(type), mcid);
        }
    }
}
