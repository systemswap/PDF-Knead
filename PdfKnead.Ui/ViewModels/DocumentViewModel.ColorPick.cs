namespace PdfKnead.ViewModels;

/// <summary>
/// Colour picker (eyedropper): the next click on a page picks up the colour visible there; Esc, a right-click or a second click
/// on the button cancels. The caller that started it receives the colour (null = cancelled).
/// </summary>
public sealed partial class DocumentViewModel
{
    private Action<int?>? _colorPick;

    /// <summary>Whether a colour pick is in progress.</summary>
    public bool IsPickingColor => _colorPick is not null;

    /// <summary>Starts a colour pick; cancels one that is still running.</summary>
    /// <param name="done">Receives the picked colour as 0xRRGGBB, or null if cancelled.</param>
    public void BeginColorPick(Action<int?> done)
    {
        EndColorPick(null);
        _colorPick = done;
        OnPropertyChanged(nameof(IsPickingColor));
    }

    /// <summary>Ends the running colour pick, if any, and passes <paramref name="rgb"/> to its caller.</summary>
    /// <param name="rgb">The picked colour as 0xRRGGBB, or null to cancel.</param>
    public void EndColorPick(int? rgb)
    {
        if (_colorPick is not { } done) return;
        _colorPick = null;
        OnPropertyChanged(nameof(IsPickingColor));
        done(rgb);
    }
}
