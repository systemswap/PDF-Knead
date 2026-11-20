using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// Dimensions (Ablaufplan 7.6 f; docs/FEHLERGEDAECHTNIS.md, No. 587): a measurement drawn as on the screen — a length with arrows at both
/// ends, leader ticks and its value along it; a polyline with an arrow at its end and its total to the right of it; an area filled, its value
/// in its middle; an arc with its centre; an angle with a small arc — and, where switched on, the length of each piece and the angle at each
/// bend. Labels on a white field framed in the colour, in Liberation Sans, upright as the page is shown.
/// </summary>
public sealed partial class ElementAppearance
{
    /// <summary>Size of the labels of dimensions in points (on the paper).</summary>
    public const double DimensionFontSize = 8;

    /// <summary>The height of a label of a dimension with its field (page space).</summary>
    public static double DimensionLabelHeight
    {
        get
        {
            var face = ElementFace.For(ElementFont.Sans);
            return (face.Ascent - face.Descent) * DimensionFontSize + 3;
        }
    }

    /// <summary>
    /// Where the caption of a line dimension sits by itself (ISO 32000-2, 12.5.6.7: /CP /Top — above the line): its middle, as far from the
    /// line as this program lays it (half the label, half the line, a little room), on the left of the direction from the first point to the
    /// second. /CO is the offset from it: along the line, then across it (No. 703).
    /// </summary>
    public static (double X, double Y) CaptionHome((double X, double Y) a, (double X, double Y) b, double width, out (double X, double Y) along,
        out (double X, double Y) across)
    {
        var length = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        along = length > 0 ? ((b.X - a.X) / length, (b.Y - a.Y) / length) : (1, 0);
        across = (-along.Y, along.X);
        var offset = DimensionLabelHeight / 2 + width / 2 + 1.5;
        return ((a.X + b.X) / 2 + across.X * offset, (a.Y + b.Y) / 2 + across.Y * offset);
    }

    /// <summary>A label: its text laid out, its middle and its direction in page space, its size with the field around it.</summary>
    private sealed record DimensionLabel(TextLine Line, double X, double Y, double Angle, double Width, double Height);

    /// <summary>What a dimension draws, in page space.</summary>
    private sealed record DimensionDrawing(List<List<(double X, double Y)>> Strokes, List<(double X, double Y)[]> Heads, (double X, double Y)[]? Fill,
        List<DimensionLabel> Labels);

    /// <summary>
    /// The dimension of a measurement: laid out on the page, its rectangle around everything it draws; null without a value.
    /// </summary>
    /// <param name="measurement">The measurement (page space).</param>
    /// <param name="color">Its colour (0xRRGGBB).</param>
    /// <param name="width">Its line width.</param>
    /// <param name="fill">How strongly an area is filled.</param>
    /// <param name="segments">Whether the length of each piece is shown.</param>
    /// <param name="angles">Whether the angle at each bend is shown.</param>
    /// <param name="pageRotation">The rotation of the page (labels upright as shown).</param>
    /// <param name="detail">More about it, for the contents (of an arc its length and angle).</param>
    /// <param name="labelAt">Where the user moved the label of its value to (its middle, page space); null: its own place (No. 703).</param>
    public static DimensionElement? Dimension(Measurement measurement, int color, double width, double fill, bool segments, bool angles, int pageRotation,
        string detail = "", (double X, double Y)? labelAt = null)
    {
        if (measurement.Value is null || measurement.Points.Count < 2 || measurement.Text.Length == 0) return null;
        var rotation = ((pageRotation % 360) + 360) % 360;
        var drawing = LayDimension(measurement.Kind, measurement.Points, measurement.Arc?.Path ?? [], measurement.Arc?.Centre, measurement.Outline, measurement.Text,
            segments ? measurement.Segments : [], angles ? measurement.Corners : [], width, rotation, labelAt);
        var (left, bottom, right, top) = Bounds(drawing);
        var margin = width + 2;   // the glow beneath the lines reaches beyond them (No. 685)
        var rect = new PdfRectangle(left - margin, bottom - margin, right + margin, top + margin);
        (double X, double Y) Relative((double X, double Y) p) => (p.X - rect.Left, p.Y - rect.Bottom);
        return new DimensionElement
        {
            Rect = rect,
            Kind = measurement.Kind,
            Points = [.. measurement.Points.Select(Relative)],
            Path = [.. (measurement.Arc?.Path ?? []).Select(Relative)],
            Centre = measurement.Arc is { } arc ? Relative(arc.Centre) : null,
            Text = measurement.Text,
            Detail = detail,
            Segments = measurement.Segments,
            Corners = measurement.Corners,
            ArcPoints = measurement.ArcPoints,
            Color = color & 0xFFFFFF,
            StrokeWidth = Math.Clamp(width, 0.25, 12),
            FillOpacity = Math.Clamp(fill, 0, 1),
            ShowSegments = segments,
            ShowAngles = angles,
            Measure = measurement.Kind == MeasureKind.Angle ? null : measurement.Measure,
            PageRotation = rotation,
            LabelAt = labelAt is not null && drawing.Labels.Count > 0 ? Relative((drawing.Labels[0].X, drawing.Labels[0].Y)) : null,
        };
    }

    private static ElementAppearance BuildDimension(DimensionElement d)
    {
        var drawing = LayDimension(d.Kind, d.PagePoints, d.PagePath, d.Centre is { } c ? (d.Rect.Left + c.X, d.Rect.Bottom + c.Y) : null, Outline(d), d.Text,
            d.ShowSegments ? d.Segments : [], d.ShowAngles ? d.Corners : [], d.StrokeWidth, d.PageRotation, d.PageLabelAt);
        var (ox, oy) = (d.Rect.Left, d.Rect.Bottom);
        string Pt((double X, double Y) p) => P(p.X - ox, p.Y - oy);
        var color = Rgb(d.Color);
        var sb = new StringBuilder("q\n");
        var resources = new PdfDictionary();
        if (drawing.Fill is { Length: >= 3 } area && d.FillOpacity > 0)
        {
            var state = new PdfDictionary();
            state.Set(PdfName.Get("ca"), new PdfReal(Math.Round(d.FillOpacity, 3)));
            var fillStates = new PdfDictionary();
            fillStates.Set(PdfName.Get("GS0"), state);
            resources.Set(PdfName.Get("ExtGState"), fillStates);
            sb.Append("q /GS0 gs ").Append(color).Append(" rg\n").Append(Pt(area[0])).Append(" m\n");
            foreach (var p in area.Skip(1)) sb.Append(Pt(p)).Append(" l\n");
            sb.Append("h f Q\n");
        }
        // the lines with a faint glow of their colour beneath (design decision, No. 685): a wider stroke at a fifth of the opacity
        var states = resources.Get(PdfName.Get("ExtGState")) as PdfDictionary ?? new PdfDictionary();
        var glow = new PdfDictionary();
        glow.Set(PdfName.Get("CA"), new PdfReal(DimensionGlowOpacity));
        states.Set(PdfName.Get("GS1"), glow);
        resources.Set(PdfName.Get("ExtGState"), states);
        void Strokes()
        {
            foreach (var stroke in drawing.Strokes)
            {
                if (stroke.Count < 2) continue;
                sb.Append(Pt(stroke[0])).Append(" m\n");
                foreach (var p in stroke.Skip(1)) sb.Append(Pt(p)).Append(" l\n");
                sb.Append("S\n");
            }
        }
        sb.Append("q /GS1 gs ").Append(color).Append(" RG ").Append(ElementFace.Number(d.StrokeWidth + DimensionGlowWidth)).Append(" w 1 J 1 j\n");
        Strokes();
        sb.Append("Q\n");
        sb.Append(color).Append(" RG ").Append(ElementFace.Number(d.StrokeWidth)).Append(" w 1 J 1 j\n");
        Strokes();
        // the heads filled with a gradient along them: the colour at the tip, lighter towards the base (an axial shading clipped to the head)
        if (drawing.Heads.Count > 0)
        {
            var shadings = new PdfDictionary();
            var (dark, light) = HeadColors(d.Color);
            for (var i = 0; i < drawing.Heads.Count; i++)
            {
                var head = drawing.Heads[i];
                var tip = head[0];
                var baseMiddle = ((head[1].X + head[2].X) / 2, (head[1].Y + head[2].Y) / 2);
                var function = new PdfDictionary();
                function.Set(PdfName.Get("FunctionType"), PdfInteger.Of(2));
                function.Set(PdfName.Get("Domain"), new PdfArray([PdfInteger.Of(0), PdfInteger.Of(1)]));
                function.Set(PdfName.Get("C0"), new PdfArray([.. dark.Select(c => (PdfObject)new PdfReal(c))]));
                function.Set(PdfName.Get("C1"), new PdfArray([.. light.Select(c => (PdfObject)new PdfReal(c))]));
                function.Set(PdfName.Get("N"), PdfInteger.Of(1));
                var shading = new PdfDictionary();
                shading.Set(PdfName.Get("ShadingType"), PdfInteger.Of(2));
                shading.Set(PdfName.Get("ColorSpace"), PdfName.Get("DeviceRGB"));
                shading.Set(PdfName.Get("Coords"), new PdfArray([new PdfReal(Math.Round(tip.X - ox, 3)), new PdfReal(Math.Round(tip.Y - oy, 3)),
                    new PdfReal(Math.Round(baseMiddle.Item1 - ox, 3)), new PdfReal(Math.Round(baseMiddle.Item2 - oy, 3))]));
                shading.Set(PdfName.Get("Function"), function);
                shading.Set(PdfName.Get("Extend"), new PdfArray([PdfBoolean.True, PdfBoolean.True]));
                shadings.Set(PdfName.Get("Sh" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)), shading);
                sb.Append("q ").Append(Pt(head[0])).Append(" m ").Append(Pt(head[1])).Append(" l ").Append(Pt(head[2])).Append(" l h W n /Sh")
                  .Append(i.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" sh Q\n");
            }
            resources.Set(PdfName.Get("Shading"), shadings);
        }

        var face = ElementFace.For(ElementFont.Sans);
        var glyphs = new Dictionary<int, string>();
        foreach (var label in drawing.Labels)
        {
            double cos = Math.Cos(label.Angle), sin = Math.Sin(label.Angle);
            sb.Append("q ").Append(ElementFace.Number(cos)).Append(' ').Append(ElementFace.Number(sin)).Append(' ').Append(ElementFace.Number(-sin)).Append(' ')
              .Append(ElementFace.Number(cos)).Append(' ').Append(Pt((label.X, label.Y))).Append(" cm\n");
            // a white field with rounded corners and a fine frame, as on the screen (No. 685 — before square, the frame heavier)
            var box = RoundedBox(-label.Width / 2, -label.Height / 2, label.Width, label.Height, 1.6);
            sb.Append("1 1 1 rg ").Append(box).Append("f\n");
            sb.Append(color).Append(" RG 0.4 w ").Append(box).Append("S\n");
            sb.Append("BT /F1 ").Append(ElementFace.Number(DimensionFontSize)).Append(" Tf ").Append(color).Append(" rg 1 0 0 1 ")
              .Append(P(-label.Line.Width / 2, -(face.Ascent + face.Descent) / 2 * DimensionFontSize)).Append(" Tm ");
            AppendShowText(sb, face, label.Line, DimensionFontSize);
            sb.Append("ET Q\n");
            for (var i = 0; i < label.Line.Glyphs.Count; i++) glyphs.TryAdd(label.Line.Glyphs[i], label.Line.Text[i]);
        }
        sb.Append("Q\n");
        var fonts = new PdfDictionary();
        fonts.Set(PdfName.Get("F1"), face.DisplayFont);
        resources.Set(PdfName.Get("Font"), fonts);
        return new ElementAppearance(d, FormStream(d, resources, Encoding.ASCII.GetBytes(sb.ToString())), face, glyphs);
    }

    /// <summary>The glow beneath the lines of a dimension: how much wider than the line, and how opaque (No. 685).</summary>
    public const double DimensionGlowWidth = 2.4, DimensionGlowOpacity = 0.2;

    /// <summary>
    /// The colours of the gradient in an arrow head (0…1 each, RGB): at the tip the colour a little deeper, at the base mixed a third with
    /// white (No. 685).
    /// </summary>
    public static (double[] Dark, double[] Light) HeadColors(int rgb)
    {
        double[] c = [((rgb >> 16) & 0xFF) / 255.0, ((rgb >> 8) & 0xFF) / 255.0, (rgb & 0xFF) / 255.0];
        return ([.. c.Select(v => Math.Round(v * 0.85, 3))], [.. c.Select(v => Math.Round(v + (1 - v) * 0.35, 3))]);
    }

    /// <summary>
    /// The length of an arrow head of a dimension for a line width (points): in proportion to the line as in technical drawings (there about
    /// ten times the width of a thin dimension line, ISO 129-1 with ISO 128-2) — 13 times at 0.5 pt, 9 at 1 pt, 7 at 2 pt; a thick line not
    /// with a huge head (No. 685: before 5 + 1.6 × width, small beside a thick line).
    /// </summary>
    public static double DimensionHeadLength(double width) => 4 + 5 * width;

    /// <summary>Half the width of an arrow head per its length: tan 15° (a head of 30°).</summary>
    public const double DimensionHeadSlope = 0.268;

    /// <summary>The room left between the heads of a length before they go outside (points).</summary>
    internal const double DimensionHeadGap = 2;

    /// <summary>A rectangle with rounded corners as a path (the corners Bézier quarter circles), ready for f or S.</summary>
    private static string RoundedBox(double x, double y, double w, double h, double r)
    {
        r = Math.Min(r, Math.Min(w, h) / 2);
        var k = r * 0.5523;   // the control distance of a quarter circle
        var sb = new StringBuilder();
        sb.Append(P(x + r, y)).Append(" m ").Append(P(x + w - r, y)).Append(" l ")
          .Append(P(x + w - r + k, y)).Append(' ').Append(P(x + w, y + r - k)).Append(' ').Append(P(x + w, y + r)).Append(" c ")
          .Append(P(x + w, y + h - r)).Append(" l ")
          .Append(P(x + w, y + h - r + k)).Append(' ').Append(P(x + w - r + k, y + h)).Append(' ').Append(P(x + w - r, y + h)).Append(" c ")
          .Append(P(x + r, y + h)).Append(" l ")
          .Append(P(x + r - k, y + h)).Append(' ').Append(P(x, y + h - r + k)).Append(' ').Append(P(x, y + h - r)).Append(" c ")
          .Append(P(x, y + r)).Append(" l ")
          .Append(P(x, y + r - k)).Append(' ').Append(P(x + r - k, y)).Append(' ').Append(P(x + r, y)).Append(" c h ");
        return sb.ToString();
    }

    /// <summary>
    /// The pieces of a polyline or area dimension in page space (straight and arcs; Ablaufplan 7.6 j), with its own scale — on the paper without
    /// one; null for the other kinds.
    /// </summary>
    public static MeasureOutline? Outline(DimensionElement d) => d.Kind is MeasureKind.Polyline or MeasureKind.Area
        ? MeasureGeometry.Build(d.PagePoints, d.ArcPoints, d.Kind == MeasureKind.Area, d.Measure ?? Measuring.Paper)
        : null;

    /// <summary>
    /// The course a label of the value keeps to (<see cref="MeasureLabel.Clamp"/>): of an area and a polyline the course of their pieces (arcs
    /// finely divided), of a length its ends, of an arc the arc, of an angle its three points.
    /// </summary>
    public static IReadOnlyList<(double X, double Y)> LabelCourse(MeasureKind kind, IReadOnlyList<(double X, double Y)> points,
        IReadOnlyList<(double X, double Y)> path, MeasureOutline? outline) => kind switch
    {
        MeasureKind.Length when points.Count >= 2 => [points[0], points[^1]],
        MeasureKind.Radius when path.Count >= 2 => path,
        MeasureKind.Area or MeasureKind.Polyline when outline is { Path.Count: >= 2 } => kind == MeasureKind.Area && outline.Path.Count > 2 &&
                                                                                          outline.Path[0] == outline.Path[^1] ? outline.Path.Take(outline.Path.Count - 1).ToList() : outline.Path,
        _ => points,
    };

    /// <summary>The bounds of everything a dimension draws (labels with their field).</summary>
    private static (double Left, double Bottom, double Right, double Top) Bounds(DimensionDrawing drawing)
    {
        double left = double.PositiveInfinity, bottom = double.PositiveInfinity, right = double.NegativeInfinity, top = double.NegativeInfinity;
        void Add((double X, double Y) p)
        {
            left = Math.Min(left, p.X);
            bottom = Math.Min(bottom, p.Y);
            right = Math.Max(right, p.X);
            top = Math.Max(top, p.Y);
        }
        foreach (var stroke in drawing.Strokes) foreach (var p in stroke) Add(p);
        foreach (var head in drawing.Heads) foreach (var p in head) Add(p);
        foreach (var p in drawing.Fill ?? []) Add(p);
        foreach (var label in drawing.Labels)
        {
            double cos = Math.Cos(label.Angle), sin = Math.Sin(label.Angle);
            foreach (var (u, v) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
            {
                double x = u * label.Width / 2, y = v * label.Height / 2;
                Add((label.X + x * cos - y * sin, label.Y + x * sin + y * cos));
            }
        }
        return double.IsFinite(left) ? (left, bottom, right, top) : (0, 0, 0, 0);
    }

    /// <summary>
    /// Lays out a dimension in page space (y up). „Up“ and „right“ are those of the page as shown (rotated by <paramref name="rotation"/>
    /// clockwise): labels along a piece are never upside down, other labels stand upright.
    /// </summary>
    /// <param name="outline">Of a polyline or area its pieces (straight and arcs, Ablaufplan 7.6 j); its labels sit at the middle of each piece.</param>
    private static DimensionDrawing LayDimension(MeasureKind kind, IReadOnlyList<(double X, double Y)> points, IReadOnlyList<(double X, double Y)> path,
        (double X, double Y)? centre, MeasureOutline? outline, string text, IReadOnlyList<string> segments, IReadOnlyList<string> corners, double width, int rotation,
        (double X, double Y)? labelAt = null)
    {
        var strokes = new List<List<(double X, double Y)>>();
        var heads = new List<(double X, double Y)[]>();
        var labels = new List<DimensionLabel>();
        (double X, double Y)[]? fill = null;
        var r = rotation * Math.PI / 180;
        (double X, double Y) up = (-Math.Sin(r), Math.Cos(r)), right = (Math.Cos(r), Math.Sin(r));
        var face = ElementFace.For(ElementFont.Sans);
        var pad = 1.5;

        DimensionLabel Label(string t, (double X, double Y) at, double angle)
        {
            // Liberation Sans has no diameter sign (U+2300): the „Ø“ of technical drawings instead (the contents keep „⌀“)
            t = t.Replace('⌀', 'Ø');
            var line = Layout(new TextElement { Rect = new PdfRectangle(0, 0, 100_000, 100), Text = t, FontSize = DimensionFontSize, Multiline = false }, face)[0];
            return new DimensionLabel(line, at.X, at.Y, angle, line.Width + 2 * pad + 2, (face.Ascent - face.Descent) * DimensionFontSize + 2 * pad);
        }
        void Free(string t, (double X, double Y) at) => labels.Add(Label(t, at, r));
        // beside a point, to the right as shown
        void Beside(string t, (double X, double Y) at)
        {
            var label = Label(t, at, r);
            labels.Add(label with { X = at.X + right.X * (label.Width / 2 + 6), Y = at.Y + right.Y * (label.Width / 2 + 6) });
        }
        // along a piece, on the side of the normal given
        void Along(string t, (double X, double Y) a, (double X, double Y) b, (double X, double Y) side)
        {
            var angle = Math.Atan2(b.Y - a.Y, b.X - a.X) - r;
            while (angle > Math.PI / 2) angle -= Math.PI;
            while (angle <= -Math.PI / 2) angle += Math.PI;
            var label = Label(t, a, angle + r);
            var offset = label.Height / 2 + width / 2 + 1.5;
            labels.Add(label with { X = (a.X + b.X) / 2 + side.X * offset, Y = (a.Y + b.Y) / 2 + side.Y * offset });
        }
        static (double X, double Y) Unit((double X, double Y) v)
        {
            var l = Math.Sqrt(v.X * v.X + v.Y * v.Y);
            return l > 0 ? (v.X / l, v.Y / l) : (1, 0);
        }
        static double Span((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        (double X, double Y) Up((double X, double Y) a, (double X, double Y) b)
        {
            var n = Unit((-(b.Y - a.Y), b.X - a.X));
            var dot = n.X * up.X + n.Y * up.Y;
            return dot > 1e-9 || Math.Abs(dot) <= 1e-9 && n.X * right.X + n.Y * right.Y < 0 ? n : (-n.X, -n.Y);
        }
        // a closed, filled arrow head of 30° as in technical drawings (ISO 129-1 allows 15° to 90°; No. 685 — before 42°, stubby)
        var headLength = DimensionHeadLength(width);
        void Head((double X, double Y) tip, (double X, double Y) from)
        {
            var d = Unit((tip.X - from.X, tip.Y - from.Y));
            (double X, double Y) n = (-d.Y, d.X);
            var half = headLength * DimensionHeadSlope;
            heads.Add([tip, (tip.X - d.X * headLength + n.X * half, tip.Y - d.Y * headLength + n.Y * half),
                (tip.X - d.X * headLength - n.X * half, tip.Y - d.Y * headLength - n.Y * half)]);
        }
        void Bend((double X, double Y) here, (double X, double Y) u, (double X, double Y) w, (double X, double Y) bisector, double radius)
        {
            double a = Math.Atan2(u.Y, u.X), b = Math.Atan2(w.Y, w.X), c = Math.Atan2(bisector.Y, bisector.X);
            static double Positive(double x) => (x % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);
            var sweep = Positive(b - a);
            if (Positive(c - a) > sweep) sweep -= 2 * Math.PI;
            var arc = new List<(double X, double Y)>();
            for (var k = 0; k <= 12; k++)
            {
                var t = a + sweep * k / 12;
                arc.Add((here.X + Math.Cos(t) * radius, here.Y + Math.Sin(t) * radius));
            }
            strokes.Add(arc);
        }
        (double X, double Y) Bisector((double X, double Y) u, (double X, double Y) w)
        {
            var s = (u.X + w.X, u.Y + w.Y);
            return Math.Sqrt(s.Item1 * s.Item1 + s.Item2 * s.Item2) < 1e-9 ? (-u.Y, u.X) : Unit(s);
        }

        var area = kind == MeasureKind.Area && points.Count >= 3;
        // the course of a polyline or area along its pieces (arcs along their arc); the points themselves without
        IReadOnlyList<(double X, double Y)> course = outline?.Path ?? points;
        switch (kind)
        {
            case MeasureKind.Length:
            {
                var (a, b) = (points[0], points[^1]);
                if (Span(a, b) > 0)
                {
                    var d = Unit((b.X - a.X, b.Y - a.Y));
                    if (Span(a, b) >= 2 * headLength + DimensionHeadGap)
                    {
                        // the line ends inside the heads (its round end would blunt their tips)
                        var into = headLength * 0.7;
                        strokes.Add([(a.X + d.X * into, a.Y + d.Y * into), (b.X - d.X * into, b.Y - d.Y * into)]);
                        Head(a, b);
                        Head(b, a);
                    }
                    else
                    {
                        // too short for both heads between the ends: they come from outside, the line runs on beyond both (ISO 129-1, where
                        // there is no room; No. 685 — before they lay on top of each other)
                        var reach = headLength + 3;
                        strokes.Add([(a.X - d.X * reach, a.Y - d.Y * reach), (b.X + d.X * reach, b.Y + d.Y * reach)]);
                        Head(a, (a.X - d.X, a.Y - d.Y));
                        Head(b, (b.X + d.X, b.Y + d.Y));
                    }
                    var n = Unit((-(b.Y - a.Y), b.X - a.X));
                    var tick = 3 + width;
                    strokes.Add([(a.X - n.X * tick, a.Y - n.Y * tick), (a.X + n.X * tick, a.Y + n.Y * tick)]);
                    strokes.Add([(b.X - n.X * tick, b.Y - n.Y * tick), (b.X + n.X * tick, b.Y + n.Y * tick)]);
                }
                else strokes.Add([a, b]);
                Along(text, a, b, Up(a, b));
                break;
            }
            case MeasureKind.Polyline:
            {
                // the arrow in the direction the last piece arrives (of an arc its tangent); the line ends inside it
                (double X, double Y)? arrives = outline is { Pieces.Count: > 0 } ? outline.Pieces[^1].EndDirection
                    : Span(points[^2], points[^1]) > 0 ? Unit((points[^1].X - points[^2].X, points[^1].Y - points[^2].Y)) : null;
                var line = course.ToList();
                if (arrives is { } u && line.Count >= 2 && Span(line[^2], line[^1]) > headLength)
                    line[^1] = (line[^1].X - u.X * headLength * 0.7, line[^1].Y - u.Y * headLength * 0.7);
                strokes.Add(line);
                if (arrives is { } w) Head(points[^1], (points[^1].X - w.X, points[^1].Y - w.Y));
                Beside(text, points[^1]);
                break;
            }
            case MeasureKind.Area when area:
                fill = [.. course];
                strokes.Add([.. course, course[0]]);
                Free(text, Centroid(course));
                break;
            case MeasureKind.Angle when points.Count >= 3:
            {
                strokes.Add([points[0], points[1], points[2]]);
                var (u, w) = (Unit((points[0].X - points[1].X, points[0].Y - points[1].Y)), Unit((points[2].X - points[1].X, points[2].Y - points[1].Y)));
                var bisector = Bisector(u, w);
                Bend(points[1], u, w, bisector, 10);
                Free(text, (points[1].X + bisector.X * 22, points[1].Y + bisector.Y * 22));
                break;
            }
            case MeasureKind.Radius when path.Count >= 2 && centre is { } c:
            {
                strokes.Add([.. path]);
                strokes.Add([(c.X - 3, c.Y - 3), (c.X + 3, c.Y + 3)]);
                strokes.Add([(c.X - 3, c.Y + 3), (c.X + 3, c.Y - 3)]);
                var middle = path[path.Count / 2];
                strokes.Add([c, middle]);
                var outward = Span(middle, c) > 0 ? Unit((middle.X - c.X, middle.Y - c.Y)) : up;
                var label = Label(text, middle, r);
                var reach = Math.Abs(outward.X * right.X + outward.Y * right.Y) * label.Width / 2 + Math.Abs(outward.X * up.X + outward.Y * up.Y) * label.Height / 2 + 4;
                labels.Add(label with { X = middle.X + outward.X * reach, Y = middle.Y + outward.Y * reach });
                break;
            }
            default:
                strokes.Add([.. points]);
                Beside(text, points[^1]);
                break;
        }

        // the label of the value where the user moved it, within the rules (MeasureLabel; design decision, No. 703): the first label laid
        if (labelAt is { } wanted && labels.Count > 0)
        {
            var main = labels[0];
            var keep = LabelCourse(kind, points, path, outline);
            var (x, y) = MeasureLabel.Clamp(kind, keep, wanted, main.Width, main.Height, main.Angle);
            labels[0] = main with { X = x, Y = y };
        }

        // the length of each piece at its middle (of an arc with its radius) — of an area on the outside (no more labels than the pieces: a
        // crafted list must not reach past them, No. 596)
        for (var i = 0; outline is not null && i < Math.Min(segments.Count, outline.Pieces.Count) && kind is MeasureKind.Polyline or MeasureKind.Area; i++)
        {
            if (segments[i].Length == 0) continue;
            var piece = outline.Pieces[i];
            var length = 0.0;
            for (var k = 0; k + 1 < piece.Path.Count; k++) length += Span(piece.Path[k], piece.Path[k + 1]);
            if (length < 1) continue;
            // a stretch along the piece at its middle: the label lies on it as on a straight piece
            var half = Math.Min(length, 24) / 2;
            (double X, double Y) a = (piece.Middle.X - piece.Direction.X * half, piece.Middle.Y - piece.Direction.Y * half),
                b = (piece.Middle.X + piece.Direction.X * half, piece.Middle.Y + piece.Direction.Y * half);
            var side = Up(a, b);
            if (area)
            {
                var n = Unit((-(b.Y - a.Y), b.X - a.X));
                var mid = (piece.Middle.X + n.X * 2, piece.Middle.Y + n.Y * 2);
                side = Inside(course, mid) ? (-n.X, -n.Y) : n;
            }
            Along(segments[i], a, b, side);
        }
        // the angle at each bend, inside it — between the directions of the pieces meeting there (of an arc its tangent)
        for (var i = 0; outline is not null && i < corners.Count && i < points.Count && kind is MeasureKind.Polyline or MeasureKind.Area; i++)
        {
            if (corners[i].Length == 0) continue;
            if (outline.Pieces.FirstOrDefault(p => p.To == i) is not { } incoming || outline.Pieces.FirstOrDefault(p => p.From == i) is not { } outgoing) continue;
            var here = points[i];
            var (u, w) = ((-incoming.EndDirection.X, -incoming.EndDirection.Y), outgoing.StartDirection);
            var bisector = Bisector(u, w);
            if (area && !Inside(course, (here.X + bisector.X * 2, here.Y + bisector.Y * 2))) bisector = (-bisector.X, -bisector.Y);
            Bend(here, u, w, bisector, 6);
            Free(corners[i], (here.X + bisector.X * 16, here.Y + bisector.Y * 16));
        }
        return new DimensionDrawing(strokes, heads, fill, labels);
    }

    /// <summary>Whether a point lies inside a polygon (even–odd).</summary>
    private static bool Inside(IReadOnlyList<(double X, double Y)> polygon, (double X, double Y) p)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var (a, b) = (polygon[i], polygon[j]);
            if (a.Y > p.Y != b.Y > p.Y && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    /// <summary>The centroid of a polygon (the mean of its points if it has no area).</summary>
    private static (double X, double Y) Centroid(IReadOnlyList<(double X, double Y)> polygon)
    {
        double twice = 0, cx = 0, cy = 0;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var cross = polygon[j].X * polygon[i].Y - polygon[i].X * polygon[j].Y;
            twice += cross;
            cx += (polygon[j].X + polygon[i].X) * cross;
            cy += (polygon[j].Y + polygon[i].Y) * cross;
        }
        return Math.Abs(twice) < 1e-9 ? (polygon.Average(p => p.X), polygon.Average(p => p.Y)) : (cx / (3 * twice), cy / (3 * twice));
    }
}
