using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PdfKnead.Core.Printing;
using SkiaSharp;

namespace PdfKnead.Controls;

/// <summary>
/// Overview of a poster (see KONZEPT.md, section 5.11): the enlarged page centred in the area of all sheets, above it the grid of
/// sheets with their names; the glue edges are tinted and the current sheet is highlighted. A click on a sheet reports it
/// (<see cref="TileClicked"/>), and the window then shows it large.
/// </summary>
public sealed class PosterPreview : Control
{
    public static readonly StyledProperty<SKImage?> ImageProperty = AvaloniaProperty.Register<PosterPreview, SKImage?>(nameof(Image));
    public static readonly StyledProperty<PosterGrid?> GridProperty = AvaloniaProperty.Register<PosterPreview, PosterGrid?>(nameof(Grid));
    public static readonly StyledProperty<int> CurrentProperty = AvaloniaProperty.Register<PosterPreview, int>(nameof(Current), -1);

    private static readonly BoxShadows Shadow = BoxShadows.Parse("0 2 10 0 #40000000");
    private static readonly IBrush OverlapBrush = new SolidColorBrush(Color.FromArgb(40, 79, 70, 229));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromArgb(170, 79, 70, 229)), 1);
    private static readonly IPen CurrentPen = new Pen(new SolidColorBrush(Color.FromRgb(79, 70, 229)), 2.5);
    private static readonly IPen PagePen = new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), 1, new DashStyle([4, 3], 0));
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.FromArgb(200, 79, 70, 229));

    /// <summary>Raised with the column and row of the clicked sheet.</summary>
    public event Action<int, int>? TileClicked;

    protected override Avalonia.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new PreviewAutomationPeer(this);

    static PosterPreview()
    {
        AffectsRender<PosterPreview>(ImageProperty, GridProperty, CurrentProperty);
        CursorProperty.OverrideDefaultValue<PosterPreview>(new Cursor(StandardCursorType.Hand));
    }

    public SKImage? Image
    {
        get => GetValue(ImageProperty);
        set => SetValue(ImageProperty, value);
    }

    public PosterGrid? Grid
    {
        get => GetValue(GridProperty);
        set => SetValue(GridProperty, value);
    }

    /// <summary>Current sheet in the grid (row × columns + column); −1 = none.</summary>
    public int Current
    {
        get => GetValue(CurrentProperty);
        set => SetValue(CurrentProperty, value);
    }

    /// <summary>Area of all sheets in user interface units, and the scale from points to units.</summary>
    private (Rect Area, double Scale) Layout(PosterGrid grid)
    {
        const double pad = 28;
        var scale = Math.Min(Math.Max(1, Bounds.Width - 2 * pad) / grid.CoverWidth, Math.Max(1, Bounds.Height - 2 * pad) / grid.CoverHeight);
        var size = new Size(grid.CoverWidth * scale, grid.CoverHeight * scale);
        return (new Rect(new Point((Bounds.Width - size.Width) / 2, (Bounds.Height - size.Height) / 2), size), scale);
    }

    /// <summary>Rectangle of a sheet in user interface units.</summary>
    private static Rect TileRect(PosterGrid grid, Rect area, double scale, int column, int row) =>
        new(area.X + column * grid.StepX * scale, area.Y + row * grid.StepY * scale, grid.TileWidth * scale, grid.TileHeight * scale);

    public override void Render(DrawingContext context)
    {
        if (Grid is not { } grid) return;
        var (area, scale) = Layout(grid);
        context.DrawRectangle(Brushes.White, null, new RoundedRect(area), Shadow);

        // The enlarged page
        var page = new Rect(area.X + grid.OffsetX * scale, area.Y + grid.OffsetY * scale, grid.Width * grid.Scale * scale, grid.Height * grid.Scale * scale);
        if (Image is { } image)
            context.Custom(new SkiaImageDrawing(area, [new SkiaImageDraw(image, new SKRect(0, 0, image.Width, image.Height),
                new SKRect((float)page.Left, (float)page.Top, (float)page.Right, (float)page.Bottom))]));
        // Where the page lies (otherwise white on white); the rest of the sheets stays empty.
        context.DrawRectangle(null, PagePen, page);

        // Glue edges (where two sheets overlap)
        var overlapX = (grid.TileWidth - grid.StepX) * scale;
        var overlapY = (grid.TileHeight - grid.StepY) * scale;
        for (var c = 1; c < grid.Columns; c++)
            context.DrawRectangle(OverlapBrush, null, new Rect(area.X + c * grid.StepX * scale, area.Y, overlapX, area.Height));
        for (var r = 1; r < grid.Rows; r++)
            context.DrawRectangle(OverlapBrush, null, new Rect(area.X, area.Y + r * grid.StepY * scale, area.Width, overlapY));

        // Grid and names
        var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
        for (var r = 0; r < grid.Rows; r++)
            for (var c = 0; c < grid.Columns; c++)
            {
                var tile = TileRect(grid, area, scale, c, r);
                context.DrawRectangle(null, GridPen, tile);
                var label = new FormattedText(PosterGrid.Label(c, r), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface,
                    Math.Clamp(Math.Min(tile.Width, tile.Height) / 6, 9, 22), LabelBrush);
                context.DrawText(label, new Point(tile.Center.X - label.Width / 2, tile.Center.Y - label.Height / 2));
            }
        if (Current >= 0 && Current < grid.SheetCount)
            context.DrawRectangle(null, CurrentPen, TileRect(grid, area, scale, Current % grid.Columns, Current / grid.Columns));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Grid is not { } grid || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var (area, scale) = Layout(grid);
        var point = e.GetPosition(this);
        if (!area.Contains(point)) return;
        // Each sheet covers the part up to the glue edge of the next one (the edge itself belongs to the neighbour).
        var column = Math.Clamp((int)((point.X - area.X) / (grid.StepX * scale)), 0, grid.Columns - 1);
        var row = Math.Clamp((int)((point.Y - area.Y) / (grid.StepY * scale)), 0, grid.Rows - 1);
        TileClicked?.Invoke(column, row);
        e.Handled = true;
    }
}
