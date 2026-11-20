using System.Globalization;
using System.Text;
using PdfKnead.Core.Barcodes;
using PdfKnead.Rendering;

namespace PdfKnead.Cli;

/// <summary>A code found on a page as text, for <c>pdf-knead code</c> and the checks (docs/PRUEFUNGEN.md).</summary>
internal static class CodeReport
{
    public static string Describe(PageBarcode code)
    {
        var c = code.Code;
        var text = new StringBuilder();
        string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        text.AppendLine($"format\t{c.Format}");
        text.AppendLine($"inhalt\t{BarcodeInterpreter.Safe(c.Text).ReplaceLineEndings("⏎")}");
        text.AppendLine($"ecken\t{string.Join(" ", code.Corners.Select(p => F(p.X) + "," + F(p.Y)))}");
        text.AppendLine($"modul\t{F(code.ModuleMillimetres)} mm\truhezone\t{F(c.QuietZone)} Module\tdrehung\t{code.Rotation}");
        if (c.Qr is { } q)
            text.AppendLine($"qr\tversion {q.Version}\tstufe {q.Level}\tmaske {q.Mask}\tkorrigiert {q.Corrected}/{q.Capacity.Repairable}\tarten {string.Join(",", q.Modes)}\t{q.Charset}{(q.Gs1 ? "\tGS1" : "")}{(q.Mirrored ? "\tgespiegelt" : "")}");
        if (c.DataMatrix is { } d)
            text.AppendLine($"datamatrix\t{d.Rows}x{d.Columns}\tkorrigiert {d.Corrected}/{d.Capacity.Repairable}\t{string.Join(",", d.Schemes)}\t{d.Charset}{(d.Gs1 ? "\tGS1" : "")}");
        if (c.Linear is { } l)
            text.AppendLine($"strichcode\t{l.Symbology}\tmodule {l.Modules}\tpruefzeichen {(l.CheckValid is true ? "ja" : "-")}\t{string.Join(",", l.Sets)}");
        var meaning = BarcodeInterpreter.Interpret(c.Format, c.Text, c.Qr?.Gs1 == true || c.DataMatrix?.Gs1 == true);
        text.AppendLine($"bedeutung\t{meaning.Kind}");
        foreach (var (label, value) in meaning.Rows) text.AppendLine($"  {label}\t{value.ReplaceLineEndings("⏎")}");
        foreach (var w in meaning.Warnings) text.AppendLine($"warnung\t{w}");
        if (meaning.Action is { } a) text.AppendLine($"ziel\t{a.Target}");
        return text.ToString().TrimEnd();
    }
}
