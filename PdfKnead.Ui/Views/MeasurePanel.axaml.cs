using Avalonia.Controls;
using Avalonia.Threading;
using PdfKnead.ViewModels;

namespace PdfKnead.Views;

/// <summary>The area „Messen“: kind of measurement, value, scale and the measurements of the session.</summary>
public sealed partial class MeasurePanel : UserControl
{
    private DocumentViewModel? _doc;

    public MeasurePanel()
    {
        InitializeComponent();
        // the calibration line set: the cursor goes into the length box (Ablaufplan 7.6 g)
        DataContextChanged += (_, _) =>
        {
            if (_doc is not null) _doc.CalibrationLengthWanted -= FocusLength;
            _doc = DataContext as DocumentViewModel;
            if (_doc is not null) _doc.CalibrationLengthWanted += FocusLength;
        };
    }

    private void FocusLength() => Dispatcher.UIThread.Post(() =>
    {
        CalibrationLengthBox.Focus();
        CalibrationLengthBox.SelectAll();
    }, DispatcherPriority.Background);
}
