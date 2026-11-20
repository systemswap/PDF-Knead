using PdfKnead.Core.Barcodes;
using PdfKnead.Core.Localization;
using PdfKnead.Core.Barcodes.DataMatrix;
using PdfKnead.Core.Barcodes.Qr;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Text;

namespace PdfKnead.Rendering;

/// <summary>What replacing a code on a page takes: the area to empty and the new code that takes its place.</summary>
/// <param name="Area">Area in page space whose content goes (as when redacting, but white): the code and its human-readable text.</param>
/// <param name="Element">The new code: same kind, content and settings as read, same place, size and turn.</param>
public sealed record CodeReplacementPlan(PdfRectangle Area, CodeElement Element);

/// <summary>
/// Plans the change of a code that is part of the page (KONZEPT.md, section 5.19, step 3): the old code goes through the mechanism of
/// redaction — with a white instead of a black bar — and a new one with the content read is placed exactly over it, to be changed in the
/// panel. Only for codes upright or turned by a multiple of 90° (elements turn in those steps), not for one of a chain of symbols.
/// </summary>
public static class CodeReplacement
{
    /// <summary>Deviation from a multiple of 90° still taken as upright.</summary>
    private const double AngleTolerance = 2;

    /// <summary>Plans the replacement.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="page">The page.</param>
    /// <param name="list">The display list of the page (bar codes are measured in a drawing of it).</param>
    /// <param name="found">The code read on the page.</param>
    /// <param name="refusal">Why it cannot be replaced (message for the user); null if it can.</param>
    /// <param name="images">Image cache; null for a new one.</param>
    /// <returns>The plan; null with <paramref name="refusal"/> set.</returns>
    public static CodeReplacementPlan? Plan(PdfDocument doc, PdfPage page, DisplayList list, PageBarcode found, out string? refusal, ImageCache? images = null)
    {
        refusal = null;
        var code = found.Code;
        if (code.Qr?.Chain is not null || code.DataMatrix?.Chain is not null || code.DataMatrix?.Macro is not null)
        {
            refusal = Tr.T("Dieser Code ist Teil einer Folge zusammengehöriger Codes und lässt sich nicht einzeln ändern.");
            return null;
        }
        var angle = Math.Atan2(found.Corners[1].Y - found.Corners[0].Y, found.Corners[1].X - found.Corners[0].X) * 180 / Math.PI;
        var snapped = Math.Round(angle / 90) * 90;
        if (Math.Abs(angle - snapped) > AngleTolerance)
        {
            refusal = Tr.T("Nur gerade oder um 90° gedrehte Codes lassen sich ändern (dieser steht um {0}° schräg).", Math.Round(angle - snapped));
            return null;
        }

        // the content as typed in the panel: GS1 data in brackets
        var gs1 = code.Qr?.Gs1 == true || code.DataMatrix?.Gs1 == true;
        var content = code.Format == BarcodeFormat.Gs1_128 || gs1 ? CodeSymbol.Gs1Brackets(code.Text) : code.Text;
        var shape = code.DataMatrix is { } dm && dm.Rows != dm.Columns ? DataMatrixShape.Rectangular : DataMatrixShape.Square;
        var level = code.Qr?.Level ?? QrErrorCorrection.M;
        CodeSymbol symbol;
        try
        {
            symbol = CodeSymbol.Create(code.Format, content, gs1, level, shape);
        }
        catch (ArgumentException)
        {
            refusal = Tr.T("Dieser Code lässt sich mit seinem Inhalt nicht neu erzeugen.");
            return null;
        }

        // the code's own frame: x along its top edge, y downwards from its top left corner (page space, exact at the snapped angle)
        var frame = new Frame(found.Corners[0], snapped);
        var (width, height) = found.Size;
        var m = found.ModulePoints;
        double removeLeft = -0.5 * m, removeRight = width + 0.5 * m;
        double top = 0, bottom = height, longTop = 0, longBottom = height, barsLeft = 0, barsRight = width;
        var caption = false;
        if (symbol.IsLinear)
        {
            // the area found is not the bars: it may hold only part of their height, or a digit beside them — measure them (the guard
            // bars reach further, towards the text)
            if (BarExtent(doc, page, list, images ?? new ImageCache(), frame, width, height, m) is { } extent)
                (top, bottom, longTop, longBottom, barsLeft, barsRight) = extent;
            // the text belongs below; above it means the code stands on its head (the area's corners do not tell the reading direction)
            var below = CaptionGlyphs(list.Text, frame, barsLeft, barsRight, top, bottom, longBottom, m, below: true);
            var above = CaptionGlyphs(list.Text, frame, barsLeft, barsRight, top, bottom, longTop, m, below: false);
            if (above.Count > 0 && below.Count == 0)
            {
                frame = frame.Turned(barsLeft + barsRight, top + bottom);
                (longTop, longBottom) = (top + bottom - longBottom, top + bottom - longTop);
                below = CaptionGlyphs(list.Text, frame, barsLeft, barsRight, top, bottom, longBottom, m, below: true);
            }
            caption = below.Count > 0;
            longBottom = Math.Max(longBottom, bottom);
            (removeLeft, removeRight) = (barsLeft - 0.5 * m, barsRight + 0.5 * m);
            foreach (var quad in below)
            {
                longBottom = Math.Max(longBottom, quad.Max(q => q.Y));
                removeLeft = Math.Min(removeLeft, quad.Min(q => q.X) - 0.5 * m);
                removeRight = Math.Max(removeRight, quad.Max(q => q.X) + 0.5 * m);
            }
        }

        // the new code: the same modules where the old ones were, its quiet zone around them; a bar code with text below as before
        double left, right, newTop, newBottom;
        if (symbol.Matrix is { } matrix)
        {
            // modules from the measured size (the new symbol may have been built with another version for the same content)
            var module = Math.Max(width / matrix.Width, height / matrix.Height);
            var q = symbol.QuietLeft * module;
            (left, right, newTop, newBottom) = (-q, matrix.Width * module + q, -q, matrix.Height * module + q);
        }
        else
        {
            var module = (barsRight - barsLeft) / symbol.Bars!.Length;
            (left, right, newTop, newBottom) = (barsLeft - symbol.QuietLeft * module, barsRight + symbol.QuietRight * module, top,
                bottom + (caption ? 0.95 * 9 * module : 0));
        }
        var element = new CodeElement
        {
            Rect = Bounds(frame.ToPage(left, newTop), frame.ToPage(right, newTop), frame.ToPage(right, newBottom), frame.ToPage(left, newBottom)),
            Rotation = frame.Turn,
            Format = code.Format,
            Text = content,
            Gs1 = gs1,
            Level = level,
            Shape = shape,
            Caption = caption,
            Replaces = content,
        };
        double removeTop = Math.Min(top, longTop) - 0.5 * m, removeBottom = longBottom + 0.5 * m;
        var area = Bounds(frame.ToPage(removeLeft, removeTop), frame.ToPage(removeRight, removeTop), frame.ToPage(removeRight, removeBottom),
            frame.ToPage(removeLeft, removeBottom));
        return new CodeReplacementPlan(area, element);
    }

    /// <summary>A code's own frame in page space: x along its top edge, y downwards, from its top left corner.</summary>
    private sealed class Frame
    {
        private readonly (double X, double Y) _origin, _u, _v;

        public Frame((double X, double Y) origin, double degrees)
        {
            _origin = origin;
            var rad = degrees * Math.PI / 180;
            _u = (Math.Round(Math.Cos(rad)), Math.Round(Math.Sin(rad)));
            _v = (_u.Y, -_u.X);
            Turn = (((int)Math.Round(degrees)) % 360 + 360) % 360;
        }

        /// <summary>Turn of the content as an element turns it (0, 90, 180, 270).</summary>
        public int Turn { get; }

        public (double X, double Y) ToPage(double x, double y) => (_origin.X + x * _u.X + y * _v.X, _origin.Y + x * _u.Y + y * _v.Y);

        public (double X, double Y) ToCode(double px, double py) =>
            ((px - _origin.X) * _u.X + (py - _origin.Y) * _u.Y, (px - _origin.X) * _v.X + (py - _origin.Y) * _v.Y);

        /// <summary>The same code on its head: x → sumX − x, y → sumY − y.</summary>
        public Frame Turned(double sumX, double sumY) => new(ToPage(sumX, sumY), Turn + 180);
    }

    /// <summary>
    /// The bars' extent in the frame (y downwards): top and bottom of the ordinary bars (the median of 30 bars), how far the longest reach
    /// up and down (guard bars), and the first and last bar halfway down. Drawn at about three pixels per module, walked along each bar
    /// until it is light for more than a module (bars may be drawn in pieces with hairline gaps), and along the middle
    /// until the quiet zone.
    /// </summary>
    private static (double Top, double Bottom, double LongTop, double LongBottom, double Left, double Right)? BarExtent(PdfDocument doc, PdfPage page, DisplayList list, ImageCache images,
        Frame frame, double width, double height, double module)
    {
        (double X, double Y)[] reach = [frame.ToPage(-2 * module, -height), frame.ToPage(width + 2 * module, -height),
            frame.ToPage(width + 2 * module, 2 * height), frame.ToPage(-2 * module, 2 * height)];
        var area = RectD.FromLtrb(reach.Min(p => p.X), reach.Max(p => p.Y), reach.Max(p => p.X), reach.Min(p => p.Y));
        var scale = Math.Clamp(3 / module, 1, 8);
        if (PageBarcodes.Render(doc, page, list, [], images, area, scale) is not var (image, toImage)) return null;
        bool Dark(double x, double y)
        {
            var (px, py) = frame.ToPage(x, y);
            var (ix, iy) = toImage.Transform(px, py);
            int cx = (int)ix, cy = (int)iy;
            return cx >= 0 && cy >= 0 && cx < image.Width && cy < image.Height && image.Pixels[cy * image.Width + cx] < 128;
        }
        var step = 0.3 / scale;
        var tops = new List<double>();
        var bottoms = new List<double>();
        for (var i = 0; i < 30; i++)
        {
            var x = width * (0.05 + 0.9 * i / 29);
            if (!Dark(x, height / 2)) continue;
            double up = height / 2, down = height / 2;
            // across hairline gaps up to a module (bars drawn in pieces one above the other leave two light pixels at this scale), not
            // across the wider gap to the text
            bool Continues(double y, int direction)
            {
                for (var d = step; d <= module; d += step)
                    if (Dark(x, y + direction * d)) return true;
                return false;
            }
            while (up > -height && Continues(up, -1)) up -= step;
            while (down < 2 * height && Continues(down, 1)) down += step;
            tops.Add(up);
            bottoms.Add(down);
        }
        if (tops.Count < 5) return null;
        tops.Sort();
        bottoms.Sort();
        var (top, bottom) = (tops[tops.Count / 2], bottoms[bottoms.Count / 2]);
        // first and last bar on the middle line: from the middle outwards until a light gap wider than the widest space (four modules)
        var middle = (top + bottom) / 2;
        double Edge(double from, int direction)
        {
            var last = from;
            for (var x = from; Math.Abs(x - from) < width + 12 * module; x += direction * step)
            {
                if (Dark(x, middle)) last = x;
                else if (Math.Abs(x - last) > 4.5 * module) break;
            }
            return last;
        }
        var first = Enumerable.Range(0, 60).Select(i => width * i / 59).FirstOrDefault(x => Dark(x, middle), width / 2);
        return (top, bottom, tops[0], bottoms[^1], Edge(first, -1), Edge(first, 1) + step);
    }

    /// <summary>Characters of the human-readable text: right below the bars (or above them), across the code's width and its quiet zones.</summary>
    private static List<(double X, double Y)[]> CaptionGlyphs(IReadOnlyList<TextGlyph> text, Frame frame, double left, double right, double top, double bottom,
        double longEdge, double module, bool below)
    {
        var found = new List<(double X, double Y)[]>();
        foreach (var glyph in text)
        {
            if (string.IsNullOrWhiteSpace(glyph.Text)) continue;
            (double X, double Y)[] quad = [frame.ToCode(glyph.X0, glyph.Y0), frame.ToCode(glyph.X1, glyph.Y1), frame.ToCode(glyph.X2, glyph.Y2), frame.ToCode(glyph.X3, glyph.Y3)];
            double cx = quad.Average(q => q.X), cy = quad.Average(q => q.Y);
            if (cx < left - 12 * module || cx > right + 12 * module) continue;
            // the digits of EAN/UPC sit between the guard bars, lower than the ordinary bars end
            var near = below ? cy > bottom - module && cy < Math.Max(bottom, longEdge) + 14 * module
                             : cy < top + module && cy > Math.Min(top, longEdge) - 14 * module;
            if (near) found.Add(quad);
        }
        return found;
    }

    /// <summary>
    /// Plans the change of a code set in a bar code font whose code could not be read (phase 8.2): the area is the block — its glyphs carry
    /// the bars and their quiet zone — and, if it belongs to it, the human-readable line right below (a block of the same direction whose
    /// text is the content); the new code takes the block's place and size with its own quiet zone inside, the module width following from
    /// the width. Without a content (it could be neither read nor told from the text) the new code shows the placeholder until the content
    /// is typed in the panel.
    /// </summary>
    /// <param name="block">The block in the bar code font.</param>
    /// <param name="blocks">All blocks of the page (for the human-readable line).</param>
    /// <param name="format">Kind of the code (from the font name, <see cref="CodeFonts.FormatOf"/>).</param>
    /// <param name="content">The content; empty if unknown.</param>
    /// <param name="refusal">Why it cannot be replaced; null if it can.</param>
    /// <returns>The plan; null with <paramref name="refusal"/> set.</returns>
    public static CodeReplacementPlan? ForFontBlock(PageTextBlock block, IReadOnlyList<PageTextBlock> blocks, BarcodeFormat format, string content,
        out string? refusal)
    {
        refusal = null;
        var turn = Math.Round(block.Angle / 90) * 90;
        if (Math.Abs(block.Angle - turn) > AngleTolerance)
        {
            refusal = Tr.T("Nur gerade oder um 90° gedrehte Codes lassen sich ändern (dieser steht um {0}° schräg).", Math.Round(block.Angle - turn));
            return null;
        }
        double left = block.Left, right = block.Right, top = block.Top, bottom = block.Bottom;
        var linear = !CodeSymbol.IsMatrix(format);
        // EAN and UPC fonts draw the digits themselves
        var caption = linear && (format is BarcodeFormat.Ean13 or BarcodeFormat.Ean8 or BarcodeFormat.UpcA or BarcodeFormat.UpcE ||
                                 block.Lines.SelectMany(l => l.Runs).Any(r => !CodeFonts.IsCodeFont(r.Format.FontName) && !string.IsNullOrWhiteSpace(r.Text)));
        var height = top - bottom;
        static string Bare(string s) => new(s.Where(c => !char.IsWhiteSpace(c) && c != '*').ToArray());
        if (linear && content.Length > 0)
            foreach (var other in blocks)
            {
                if (ReferenceEquals(other, block) || other.CodeFont || Math.Abs(other.Angle - block.Angle) > AngleTolerance) continue;
                var middle = (other.Left + other.Right) / 2;
                if (middle < left || middle > right || other.Top > bottom + 0.5 || other.Top < bottom - height) continue;
                if (Bare(other.Text) != Bare(content)) continue;
                caption = true;
                bottom = Math.Min(bottom, other.Bottom);
                left = Math.Min(left, other.Left);
                right = Math.Max(right, other.Right);
            }
        var frame = block.FrameToPage;
        PdfRectangle Box(double l, double b, double r, double t, double margin)
        {
            var corners = new[] { (l - margin, b - margin), (r + margin, b - margin), (r + margin, t + margin), (l - margin, t + margin) }
                .Select(c => frame.Transform(c.Item1, c.Item2)).ToArray();
            return Bounds(corners);
        }
        var element = new CodeElement
        {
            Rect = Box(block.Left, bottom, block.Right, top, 0),
            Rotation = ((int)turn % 360 + 360) % 360,
            Format = format,
            Text = content,
            QuietZone = true,
            Caption = caption,
            Replaces = content.Length > 0 ? content : null,
        };
        return new CodeReplacementPlan(Box(left, bottom, right, top, 0.5), element);
    }

    private static PdfRectangle Bounds(params (double X, double Y)[] points) =>
        new(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
}
