using Avalonia.Controls;

namespace PdfKnead.Controls;

/// <summary>Information box for a compression method (see KONZEPT.md, section 5.12); the data context is a <see cref="ViewModels.CodecInfo"/>.</summary>
public partial class CodecInfoView : UserControl
{
    public CodecInfoView() => InitializeComponent();
}
