using System.Globalization;
using Avalonia;
using Avalonia.Media;
using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.ViewModels;

namespace PdfKnead.Controls;

/// <summary>
/// The helps of the human check of „barrierefrei?“ on the pages (docs/ABLAUFPLAN.md, 6.7 step 5; docs/FEHLERGEDAECHTNIS.md, No. 670): M1 the
/// reading order as numbered boxes with the type, M2 the figures and formulas with their alternative text (missing ones in red), M6 the
/// artifacts as grey areas. Drawn over the page whatever tool is on, while the help is switched on in the area.
/// </summary>
public sealed partial class DocumentView
{
    private static readonly IBrush OrderArea = new SolidColorBrush(Color.FromArgb(0x1F, 0x3A, 0x5B, 0xD9));
    private static readonly IPen OrderPen = new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x5B, 0xD9)), 1.2);
    private static readonly IBrush OrderBadge = new SolidColorBrush(Color.FromRgb(0x3A, 0x5B, 0xD9));
    private static readonly IBrush ArtifactArea = new SolidColorBrush(Color.FromArgb(0x40, 0x70, 0x70, 0x70));
    private static readonly IPen ArtifactPen = new Pen(new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)), 1, new DashStyle([4, 3], 0));
    private static readonly IBrush MissingAltBrush = new SolidColorBrush(Color.FromRgb(0xB4, 0x23, 0x18));
    private static readonly IPen MissingAltPen = new Pen(new SolidColorBrush(Color.FromRgb(0xD9, 0x2D, 0x20)), 1.5);

    private void DrawAccessibilityHelp(DrawingContext context, Rect view)
    {
        if (_doc is not { ShownAccessibilityHelp: not AccessibilityHelp.None } doc) return;
        var screen = new Rect(Bounds.Size);
        for (var page = 0; page < _pageRects.Count && page < doc.Pages.Count; page++)
        {
            if (!_shown[page] || !_pageRects[page].Intersects(view) || doc.AccessibilityHelpFor(page) is not { } help) continue;
            var toView = PageToView(page);
            // at least 6 points wide and high on the screen: a line (a rule, a hairline frame) has bounds without height
            Rect ToScreen(Core.Pdf.Graphics.RectD r)
            {
                var b = toView.TransformBounds(r);
                var grow = new Point(Math.Max(0, 6 - b.Width) / 2, Math.Max(0, 6 - b.Height) / 2);
                return new Rect(b.Left - grow.X, b.Top - grow.Y, b.Width + 2 * grow.X, b.Height + 2 * grow.Y);
            }
            switch (doc.ShownAccessibilityHelp)
            {
                case AccessibilityHelp.Artifacts:
                    foreach (var area in help.Artifacts)
                    {
                        var frame = ToScreen(area);
                        if (!frame.Intersects(screen)) continue;
                        context.FillRectangle(ArtifactArea, frame);
                        context.DrawRectangle(null, ArtifactPen, frame);
                    }
                    if (help.Artifacts.Count > 0) Label(context, ToScreen(help.Artifacts[0]), Tr.T("Artefakt"), CleanupKeptLabelBrush);
                    break;
                case AccessibilityHelp.ReadingOrder:
                    foreach (var box in help.Order)
                    {
                        var frame = ToScreen(box.Bounds);
                        if (!frame.Intersects(screen)) continue;
                        context.FillRectangle(OrderArea, frame);
                        context.DrawRectangle(null, OrderPen, frame);
                        // the number in a disc at the top left corner, the type beside it
                        var number = new FormattedText(box.Number.ToString(CultureInfo.InvariantCulture), Tr.Culture, FlowDirection.LeftToRight, HiddenLabelFace, 11, Brushes.White);
                        var radius = Math.Max(9, number.Width / 2 + 4);
                        var centre = new Point(frame.Left, frame.Top);
                        context.DrawEllipse(OrderBadge, null, centre, radius, 9);
                        context.DrawText(number, centre - new Point(number.Width / 2, number.Height / 2));
                        var type = new FormattedText(box.Type, Tr.Culture, FlowDirection.LeftToRight, HiddenLabelFace, 10, OrderBadge);
                        var typeRect = new Rect(centre.X + radius + 2, centre.Y - type.Height / 2, type.Width + 4, type.Height);
                        context.FillRectangle(HiddenLabelBack, typeRect);
                        context.DrawText(type, typeRect.TopLeft + new Point(2, 0));
                    }
                    break;
                case AccessibilityHelp.Figures:
                    foreach (var box in help.Order.Where(b => b.Type is "Figure" or "Formula"))
                    {
                        var frame = ToScreen(box.Bounds);
                        if (!frame.Intersects(screen)) continue;
                        var missing = string.IsNullOrWhiteSpace(box.Alt);
                        context.DrawRectangle(null, missing ? MissingAltPen : OrderPen, frame);
                        var text = missing ? Tr.T("Kein Ersatztext") : Tr.T("Ersatztext: {0}", box.Alt!.Length > 120 ? box.Alt[..120] + " …" : box.Alt);
                        Label(context, frame, text, missing ? MissingAltBrush : OrderBadge);
                    }
                    break;
            }
        }
    }

    /// <summary>A label above a frame on a light background (wrapped to the frame's width, at least 160).</summary>
    private static void Label(DrawingContext context, Rect frame, string text, IBrush brush)
    {
        var label = new FormattedText(text, Tr.Culture, FlowDirection.LeftToRight, HiddenLabelFace, 11, brush) { MaxTextWidth = Math.Max(160, frame.Width) };
        var rect = new Rect(frame.Left, frame.Top - label.Height - 1, label.Width + 6, label.Height);
        context.FillRectangle(HiddenLabelBack, rect);
        context.DrawText(label, rect.TopLeft + new Point(3, 0));
    }
}
