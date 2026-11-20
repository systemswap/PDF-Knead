using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace PdfKnead.EInvoice.Ui;

/// <summary>The area of the checker on the right of the document; an XML file dropped on it is checked as an XRechnung of its own.</summary>
public partial class EInvoicePanel : UserControl
{
    public EInvoicePanel()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not EInvoicePanelViewModel model) return;
        model.PickSavePath = (name, extension) => FilePickers.SaveAsync(this, name, extension);
        model.PickXmlFile = () => FilePickers.OpenXmlAsync(this);
    }

    /// <summary>The XML file of a drop, if it is exactly one local file ending in .xml.</summary>
    private static string? XmlFile(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList() is [var path]
        && path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? path : null;

    // handled here: the window would open any dropped file as a PDF
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) && XmlFile(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (XmlFile(e) is { } path && DataContext is EInvoicePanelViewModel model) model.CheckLooseFile(path);
    }
}
