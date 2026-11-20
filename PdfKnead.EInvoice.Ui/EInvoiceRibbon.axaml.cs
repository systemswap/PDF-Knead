using Avalonia.Controls;

namespace PdfKnead.EInvoice.Ui;

/// <summary>The groups of the tab „E-Rechnung“.</summary>
public partial class EInvoiceRibbon : UserControl
{
    public EInvoiceRibbon() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is EInvoiceTabViewModel model) model.PickSavePath = (name, extension) => FilePickers.SaveAsync(this, name, extension);
    }
}
