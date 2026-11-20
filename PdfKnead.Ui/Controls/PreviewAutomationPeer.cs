using Avalonia.Automation.Peers;
using Avalonia.Controls;

namespace PdfKnead.Controls;

/// <summary>
/// Exposes preview surfaces as images in the automation tree. A custom control without a peer is missing from the tree entirely,
/// so screen readers and UI Automation cannot find it despite AutomationProperties.Name (found in a probe of the AOT build).
/// </summary>
internal sealed class PreviewAutomationPeer(Control owner) : ControlAutomationPeer(owner)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
}
