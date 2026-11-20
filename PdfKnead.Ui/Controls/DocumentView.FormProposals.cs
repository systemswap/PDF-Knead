using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.ViewModels;

namespace PdfKnead.Controls;

/// <summary>
/// The proposals of „Felder erkennen“ on the pages (KONZEPT.md, section 5.9a, step 7): dashed frames with the name, ticked ones in the accent
/// colour, the others grey; a click on a frame ticks or unticks it.
/// </summary>
public sealed partial class DocumentView
{
    private static readonly IPen ProposalPen = new Pen(new SolidColorBrush(Color.FromRgb(0x4F, 0x46, 0xE5)), 1.5, new DashStyle([4, 3], 0));
    private static readonly IPen ProposalOffPen = new Pen(new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90)), 1, new DashStyle([3, 3], 0));
    private static readonly IBrush ProposalArea = new SolidColorBrush(Color.FromArgb(0x26, 0x4F, 0x46, 0xE5));
    private static readonly IBrush ProposalLabel = new SolidColorBrush(Color.FromRgb(0x37, 0x30, 0xA3));
    private static readonly IBrush ProposalOffLabel = new SolidColorBrush(Color.FromRgb(0x70, 0x70, 0x70));

    private readonly List<(Rect Frame, FieldProposalRow Row)> _proposalFrames = [];

    /// <summary>The frames of the proposals drawn last (for tests).</summary>
    internal IReadOnlyList<(Rect Frame, FieldProposalRow Row)> ProposalFrames => _proposalFrames;

    private void DrawFieldProposals(DrawingContext context, Rect view)
    {
        _proposalFrames.Clear();
        if (_doc is not { IsFieldProposalsOpen: true } doc || doc.FieldProposals.Count == 0) return;
        var screen = new Rect(Bounds.Size);
        foreach (var row in doc.FieldProposals)
        {
            var page = row.Proposal.Page;
            if ((uint)page >= (uint)_pageRects.Count || !_shown[page] || !_pageRects[page].Intersects(view)) continue;
            var frame = ViewRect(page, row.Proposal.Rect);
            if (!frame.Intersects(screen)) continue;
            _proposalFrames.Add((frame, row));
            if (row.IsChecked) context.FillRectangle(ProposalArea, frame);
            context.DrawRectangle(null, row.IsChecked ? ProposalPen : ProposalOffPen, frame);
            if (frame.Height < 8 && frame.Width < 8) continue;
            var caption = new FormattedText(row.Name, Tr.Culture, FlowDirection.LeftToRight, HiddenLabelFace, 10, row.IsChecked ? ProposalLabel : ProposalOffLabel);
            var at = new Rect(frame.Left, frame.Top - caption.Height - 1, caption.Width + 6, caption.Height);
            using (context.PushClip(at))
            {
                context.FillRectangle(HiddenLabelBack, at);
                context.DrawText(caption, at.TopLeft + new Point(3, 0));
            }
        }
    }

    /// <summary>Click on a proposal's frame: ticks or unticks it. false elsewhere.</summary>
    private bool FieldProposalPressed(PointerPressedEventArgs e, Point position)
    {
        if (_doc is not { IsFieldProposalsOpen: true } || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return false;
        // the smallest frame under the pointer (a check box inside a box)
        var hit = _proposalFrames.Where(f => f.Frame.Inflate(2).Contains(position)).OrderBy(f => f.Frame.Width * f.Frame.Height).FirstOrDefault();
        if (hit.Row is null) return false;
        hit.Row.IsChecked = !hit.Row.IsChecked;
        InvalidateVisual();
        return true;
    }
}
