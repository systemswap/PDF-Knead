using Avalonia.Controls;
using Avalonia.Threading;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>The area „Notizen“ on the right; a new own note gets the cursor in its text box.</summary>
public sealed partial class NotesPanel : UserControl
{
    private DocumentViewModel? _doc;

    public NotesPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_doc is not null) _doc.NoteTextRequested -= FocusText;
            _doc = DataContext as DocumentViewModel;
            if (_doc is not null) _doc.NoteTextRequested += FocusText;
        };
    }

    // after the layout, when the box of the new note is visible
    private void FocusText() => Dispatcher.UIThread.Post(() => NoteTextBox.Focus(), DispatcherPriority.Loaded);
}
