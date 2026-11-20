using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Accessibility;

public static partial class AccessibilityCheck
{
    private sealed partial class Run
    {
        private const int MaxRows = 10_000, MaxSpan = 1000;

        /// <summary>A cell of a table grid: its element, whether it is a header cell, its first row and column, how far it reaches.</summary>
        private sealed record GridCell(PdfDictionary Element, bool Header, int Row, int Column, int RowSpan, int ColSpan);

        /// <summary>
        /// Stage G (docs/ABLAUFPLAN.md, 6.7 step 3; docs/FEHLERGEDAECHTNIS.md, No. 666): containment and tables. PDF/UA-1: the places and kids of
        /// table, list and contents elements (ISO 14289-1, 7.2 tests 3–20, 26–28, 36–40), regular tables (7.2 tests 15, 41–43) and determinable
        /// headers (7.5). PDF/UA-2: the containment rules of ISO/TS 32005, Table 5 (<see cref="Containment.Violations"/>), Ruby and Warichu
        /// (8.2.5.23, 8.2.5.24), content of list items (8.2.5.25), regular tables with determinable headers (8.2.5.26), captions (8.2.5.27) and
        /// the references between notes and the places citing them (8.2.5.14). Types count after the role map; kids without a standard type are
        /// left out (they are findings of stage E).
        /// </summary>
        public void Nesting()
        {
            if (Catalog?.GetDictionary(PdfName.Get("StructTreeRoot")) is not { } root) return;
            var elements = Elements();
            var types = new Dictionary<PdfDictionary, string?>(ReferenceEqualityComparer.Instance);
            string? TypeOf(PdfDictionary element)
            {
                if (!types.TryGetValue(element, out var type)) types[element] = type = StandardStructure.Resolve(element, root)?.Name;
                return type;
            }
            var parents = new Dictionary<PdfDictionary, PdfDictionary>(ReferenceEqualityComparer.Instance);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var citedBy = new Dictionary<PdfDictionary, List<PdfDictionary>>(ReferenceEqualityComparer.Instance);
            foreach (var element in elements)
            {
                foreach (var kid in Kids(element)) parents.TryAdd(kid, element);
                if (element.Get(IdKey) is PdfString id) ids.Add(Convert.ToHexString(id.Bytes));
                if (part == 2)
                    foreach (var target in References(element))
                    {
                        if (!citedBy.TryGetValue(target, out var citing)) citedBy[target] = citing = [];
                        citing.Add(element);
                    }
            }
            foreach (var element in elements)
            {
                cancel.ThrowIfCancellationRequested();
                if (TypeOf(element) is not { } type) continue;
                var kids = Kids(element).Select(TypeOf).OfType<string>().ToList();
                var parent = parents.TryGetValue(element, out var p) ? TypeOf(p) : null;
                if (part == 1) Ua1Places(type, parent, kids);
                else Ua2Kinds(element, type, kids, citedBy);
                if (type == "Table") Grid(element, TypeOf, ids);
            }
            if (part == 2 && Containment.Violations(root, 1) is [var first, ..])
                Add(AccessibilityRules.ContainmentRules, Tr.T("Ein Strukturelement steht, wo die Einschlussregeln (ISO/TS 32005) es nicht erlauben: {0}.", first));
        }

        /// <summary>PDF/UA-1, 7.2: where table, list and contents elements stand and what they hold.</summary>
        private void Ua1Places(string type, string? parent, List<string> kids)
        {
            bool Only(params string[] allowed) => kids.All(allowed.Contains);
            int Count(string kind) => kids.Count(k => k == kind);
            var captionLater = kids.Skip(1).Contains("Caption");
            switch (type)
            {
                case "Table":
                    if (!Only("TR", "THead", "TBody", "TFoot", "Caption"))
                        Add(AccessibilityRules.TableKids, Tr.T("Eine Tabelle (Table) enthält anderes als TR, THead, TBody, TFoot und Caption."));
                    if (Count("THead") > 1) Add(AccessibilityRules.OneTableHead, Tr.T("Eine Tabelle hat mehr als einen Tabellenkopf (THead)."));
                    if (Count("TFoot") > 1) Add(AccessibilityRules.OneTableFoot, Tr.T("Eine Tabelle hat mehr als einen Tabellenfuß (TFoot)."));
                    if (Count("TFoot") > 0 && Count("TBody") == 0)
                        Add(AccessibilityRules.FootNeedsBody, Tr.T("Eine Tabelle hat einen Tabellenfuß (TFoot), aber keinen Tabellenrumpf (TBody)."));
                    if (Count("THead") > 0 && Count("TBody") == 0)
                        Add(AccessibilityRules.HeadNeedsBody, Tr.T("Eine Tabelle hat einen Tabellenkopf (THead), aber keinen Tabellenrumpf (TBody)."));
                    if (InTheMiddle(kids))
                        Add(AccessibilityRules.TableCaptionPlace, Tr.T("Die Beschriftung (Caption) einer Tabelle steht weder am Anfang noch am Ende."));
                    if (Count("Caption") > 1) Add(AccessibilityRules.OneTableCaption, Tr.T("Eine Tabelle hat mehr als eine Beschriftung (Caption)."));
                    break;
                case "TR":
                    if (parent is not ("Table" or "THead" or "TBody" or "TFoot"))
                        Add(AccessibilityRules.RowInTable, Tr.T("Eine Tabellenzeile (TR) steht nicht in Table, THead, TBody oder TFoot."));
                    if (!Only("TH", "TD")) Add(AccessibilityRules.RowKids, Tr.T("Eine Tabellenzeile (TR) enthält anderes als TH und TD."));
                    break;
                case "THead":
                    if (parent != "Table") Add(AccessibilityRules.HeadInTable, Tr.T("Ein Tabellenkopf (THead) steht nicht in einer Tabelle (Table)."));
                    if (!Only("TR")) Add(AccessibilityRules.HeadKids, Tr.T("Ein Tabellenkopf (THead) enthält anderes als Tabellenzeilen (TR)."));
                    break;
                case "TBody":
                    if (parent != "Table") Add(AccessibilityRules.BodyInTable, Tr.T("Ein Tabellenrumpf (TBody) steht nicht in einer Tabelle (Table)."));
                    if (!Only("TR")) Add(AccessibilityRules.BodyKids, Tr.T("Ein Tabellenrumpf (TBody) enthält anderes als Tabellenzeilen (TR)."));
                    break;
                case "TFoot":
                    if (parent != "Table") Add(AccessibilityRules.FootInTable, Tr.T("Ein Tabellenfuß (TFoot) steht nicht in einer Tabelle (Table)."));
                    if (!Only("TR")) Add(AccessibilityRules.FootKids, Tr.T("Ein Tabellenfuß (TFoot) enthält anderes als Tabellenzeilen (TR)."));
                    break;
                case "TH" when parent != "TR":
                    Add(AccessibilityRules.HeaderCellInRow, Tr.T("Eine Kopfzelle (TH) steht nicht in einer Tabellenzeile (TR)."));
                    break;
                case "TD" when parent != "TR":
                    Add(AccessibilityRules.DataCellInRow, Tr.T("Eine Datenzelle (TD) steht nicht in einer Tabellenzeile (TR)."));
                    break;
                case "L":
                    if (!Only("L", "LI", "Caption")) Add(AccessibilityRules.ListKids, Tr.T("Eine Liste (L) enthält anderes als L, LI und Caption."));
                    if (captionLater) Add(AccessibilityRules.ListCaptionFirst, Tr.T("Die Beschriftung (Caption) einer Liste steht nicht am Anfang."));
                    break;
                case "LI":
                    if (parent != "L") Add(AccessibilityRules.ItemInList, Tr.T("Ein Listenpunkt (LI) steht nicht in einer Liste (L)."));
                    if (!Only("Lbl", "LBody")) Add(AccessibilityRules.ItemKids, Tr.T("Ein Listenpunkt (LI) enthält anderes als Lbl und LBody."));
                    break;
                case "LBody" when parent != "LI":
                    Add(AccessibilityRules.BodyInItem, Tr.T("Ein Listenrumpf (LBody) steht nicht in einem Listenpunkt (LI)."));
                    break;
                case "TOC":
                    if (!Only("TOC", "TOCI", "Caption"))
                        Add(AccessibilityRules.TocKids, Tr.T("Ein Inhaltsverzeichnis (TOC) enthält anderes als TOC, TOCI und Caption."));
                    if (captionLater) Add(AccessibilityRules.TocCaptionFirst, Tr.T("Die Beschriftung (Caption) eines Inhaltsverzeichnisses steht nicht am Anfang."));
                    break;
                case "TOCI" when parent != "TOC":
                    Add(AccessibilityRules.TociInToc, Tr.T("Ein Eintrag des Inhaltsverzeichnisses (TOCI) steht nicht in einem Inhaltsverzeichnis (TOC)."));
                    break;
            }
        }

        /// <summary>A Caption among the kids neither first nor last.</summary>
        private static bool InTheMiddle(List<string> kids)
        {
            for (var i = 1; i < kids.Count - 1; i++)
                if (kids[i] == "Caption") return true;
            return false;
        }

        /// <summary>PDF/UA-2, 8.2.5: Ruby, Warichu, content of list items, captions, the references of notes.</summary>
        private void Ua2Kinds(PdfDictionary element, string type, List<string> kids, Dictionary<PdfDictionary, List<PdfDictionary>> citedBy)
        {
            var sequence = string.Join('&', kids);
            switch (type)
            {
                case "Ruby" when sequence is not ("RB&RT" or "RB&RP&RT&RP"):
                    Add(AccessibilityRules.RubyKids, Tr.T("Ein Ruby-Element besteht nicht aus RB und RT (oder RB, RP, RT, RP)."));
                    break;
                case "Warichu" when sequence != "WP&WT&WP":
                    Add(AccessibilityRules.WarichuKids, Tr.T("Ein Warichu-Element besteht nicht aus WP, WT und WP."));
                    break;
                case "LI" when HoldsContent(element):
                    Add(AccessibilityRules.ItemContentInBody, Tr.T("Ein Listenpunkt (LI) enthält Inhalt direkt statt in Lbl oder LBody."));
                    break;
                case "FENote":
                    var listed = References(element).ToHashSet(ReferenceEqualityComparer.Instance);
                    var citing = citedBy.GetValueOrDefault(element) ?? [];
                    if (citing.Any(c => !listed.Contains(c)))
                        Add(AccessibilityRules.NoteListsCitations, Tr.T("Eine Fuß- oder Endnote (FENote) nennt in /Ref nicht alle Stellen, die auf sie verweisen."));
                    if (listed.Any(l => !citing.Contains(l, ReferenceEqualityComparer.Instance)))
                        Add(AccessibilityRules.CitationsReferToNote, Tr.T("Eine Fuß- oder Endnote (FENote) nennt in /Ref Stellen, die nicht auf sie verweisen."));
                    break;
            }
            if (InTheMiddle(kids))
                Add(AccessibilityRules.CaptionFirstOrLast, Tr.T("Eine Beschriftung (Caption) steht weder am Anfang noch am Ende ihres Elements."));
        }

        /// <summary>An element holds content items directly: marked content (a number or MCR) or an object (OBJR).</summary>
        private static bool HoldsContent(PdfDictionary element)
        {
            static bool Item(PdfObject? kid) => kid is PdfInteger || kid is PdfDictionary d && d.GetName(PdfName.Type)?.Value is "MCR" or "OBJR";
            return element.Get(KKey) switch
            {
                PdfArray kids => Enumerable.Range(0, Math.Min(kids.Count, 100_000)).Any(i => Item(kids.Get(i))),
                var one => Item(one),
            };
        }

        /// <summary>The structure elements an element names in /Ref (ISO 32000-2, Table 355).</summary>
        private static IEnumerable<PdfDictionary> References(PdfDictionary element) => element.Get(RefKey) switch
        {
            PdfArray list => Enumerable.Range(0, Math.Min(list.Count, 1000)).Select(list.Get).OfType<PdfDictionary>().Where(d => d.ContainsKey(SKey)),
            PdfDictionary one when one.ContainsKey(SKey) => [one],
            _ => [],
        };

        /// <summary>
        /// The grid of a table (ISO 14289-1, 7.2 tests 15, 41–43, 7.5; -2, 8.2.5.26): rows of the table and of its THead, TBody and TFoot in their
        /// order, cells placed left to right in the next free column, reaching over RowSpan rows and ColSpan columns. Cells must not overlap; every
        /// row reaches over as many columns as the first, every column over as many rows as the first (PDF/UA-2: no cell beyond its row group);
        /// in a table with header cells every data cell with content has a header — through /Headers naming known IDs, or a TH with /Scope left of
        /// it or above it (<see cref="HasScopedHeader"/>).
        /// </summary>
        private void Grid(PdfDictionary table, Func<PdfDictionary, string?> typeOf, HashSet<string> ids)
        {
            var rows = new List<(PdfDictionary Row, int Group)>();
            var group = 0;
            foreach (var kid in Kids(table))
                switch (typeOf(kid))
                {
                    case "TR":
                        rows.Add((kid, 0));
                        break;
                    case "THead" or "TBody" or "TFoot":
                        group++;
                        rows.AddRange(Kids(kid).Where(k => typeOf(k) == "TR").Select(k => (k, group)));
                        break;
                }
            if (rows.Count is 0 or > MaxRows) return;
            var grid = new Dictionary<(int Row, int Column), GridCell>();
            var cells = new List<GridCell>();
            var overlap = false;
            for (var r = 0; r < rows.Count; r++)
            {
                var column = 0;
                foreach (var element in Kids(rows[r].Row))
                {
                    if (typeOf(element) is not ("TH" or "TD") || cells.Count > MaxRows * 10) continue;
                    while (grid.ContainsKey((r, column))) column++;
                    var cell = new GridCell(element, typeOf(element) == "TH", r, column, Span(element, "RowSpan"), Span(element, "ColSpan"));
                    cells.Add(cell);
                    for (var i = 0; i < cell.RowSpan; i++)
                        for (var j = 0; j < cell.ColSpan; j++)
                            overlap |= !grid.TryAdd((r + i, column + j), cell);
                    column += cell.ColSpan;
                }
            }
            if (overlap) Add(AccessibilityRules.CellsOverlap, Tr.T("Zellen einer Tabelle überdecken einander (RowSpan, ColSpan)."));
            var widths = new int[rows.Count];
            var heights = new Dictionary<int, int>();
            foreach (var (row, column) in grid.Keys)
            {
                if (row < rows.Count) widths[row]++;
                heights[column] = heights.GetValueOrDefault(column) + 1;
            }
            for (var r = 1; r < rows.Count; r++)
            {
                if (widths[r] == widths[0]) continue;
                if (widths[r] > widths[0]) Add(AccessibilityRules.RowsReachBeyond, Tr.T("Zeile {0} einer Tabelle reicht über mehr Spalten als Zeile 1.", r + 1));
                else Add(AccessibilityRules.RowsRegular, Tr.T("Die Zeilen 1 und {0} einer Tabelle reichen über verschieden viele Spalten ({1} und {2}).", r + 1, widths[0], widths[r]));
                break;
            }
            var first = heights.GetValueOrDefault(0);
            if (heights.FirstOrDefault(h => h.Value != first) is { Value: > 0 } wrong)
                Add(AccessibilityRules.ColumnsRegular, Tr.T("Die Spalten 1 und {0} einer Tabelle reichen über verschieden viele Zeilen.", wrong.Key + 1));
            else if (part == 2 && cells.Any(c => c.Row + c.RowSpan - 1 < rows.Count && rows[c.Row + c.RowSpan - 1].Group != rows[c.Row].Group))
                Add(AccessibilityRules.ColumnsRegular, Tr.T("Eine Zelle reicht über das Ende ihres Tabellenkopfs, -rumpfs oder -fußes hinaus (RowSpan)."));
            if (!cells.Any(c => c.Header)) return;
            // an empty data cell (no /K, such as the corner above the row headers) needs no header
            foreach (var cell in cells.Where(c => !c.Header && c.Element.Get(KKey) is not (null or PdfNull or PdfArray { Count: 0 })))
            {
                var unknown = new List<string>();
                var named = false;
                if (Attribute(cell.Element, "Table", "Headers") is PdfArray { Count: > 0 } headers)
                {
                    for (var i = 0; i < headers.Count && i < 1000; i++)
                        if (headers.Get(i) is PdfString id && !ids.Contains(Convert.ToHexString(id.Bytes))) unknown.Add(id.ToText());
                    named = unknown.Count == 0;
                }
                if (named || HasScopedHeader(cell, cells)) continue;
                if (unknown.Count > 0)
                    Add(AccessibilityRules.HeaderReferences, Tr.T("Eine Datenzelle (TD) nennt in /Headers unbekannte Kopfzellen ({0}); über /Scope lässt sich auch keine zuordnen.", string.Join(", ", unknown.Take(5))));
                else Add(AccessibilityRules.HeaderDeterminable, Tr.T("Einer Datenzelle (TD) lässt sich keine Kopfzelle zuordnen (weder /Headers noch eine Kopfzelle TH mit /Scope)."));
            }
        }

        /// <summary>A RowSpan or ColSpan of a cell: 1 if absent or not a positive number, at most <see cref="MaxSpan"/>.</summary>
        private static int Span(PdfDictionary cell, string key) =>
            Attribute(cell, "Table", key) is PdfInteger { Value: > 0 } span ? (int)Math.Min(span.Value, MaxSpan) : 1;

        /// <summary>
        /// A header cell with a /Scope (Row, Column or Both) left of the cell in its rows or above it in its columns. Whether the scope says the
        /// right direction is for a person to judge (6.7 step 5, M4): the rule asks that the structure be determinable, as the reference checker
        /// reads it.
        /// </summary>
        private static bool HasScopedHeader(GridCell cell, List<GridCell> cells) =>
            cells.Any(h => h.Header && Attribute(h.Element, "Table", "Scope") is PdfName { Value: "Row" or "Column" or "Both" } &&
                           (h.Column < cell.Column && h.Row < cell.Row + cell.RowSpan && cell.Row < h.Row + h.RowSpan ||
                            h.Row < cell.Row && h.Column < cell.Column + cell.ColSpan && cell.Column < h.Column + h.ColSpan));
    }
}
