using Avalonia.Controls;
using Avalonia.Threading;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>Properties of the selected code (see KONZEPT.md, section 5.19); a new code without content gets the cursor in its content box.</summary>
public sealed partial class CodePropertiesPanel : UserControl
{
    private CodePropertiesViewModel? _model;

    public CodePropertiesPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_model is not null) _model.ContentFocusRequested -= FocusContent;
            _model = DataContext as CodePropertiesViewModel;
            if (_model is not null) _model.ContentFocusRequested += FocusContent;
        };
    }

    // after the panel has become visible (it is hidden until the code is selected)
    private void FocusContent(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => ContentBox.Focus(), DispatcherPriority.Loaded);
}
