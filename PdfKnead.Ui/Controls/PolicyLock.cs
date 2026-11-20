using Avalonia;
using Avalonia.Controls;
using PdfKnead.Core.Policy;

namespace PdfKnead.Controls;

/// <summary>
/// Marks a control with the function it starts (<see cref="PolicyCatalog"/>): when the function file of the organization switches it off,
/// the control is greyed out and says why. The commands check again (keyboard shortcuts, menus); this is what the user sees.
/// </summary>
public static class PolicyLock
{
    public static readonly AttachedProperty<string?> IdProperty = AvaloniaProperty.RegisterAttached<Control, string?>("Id", typeof(PolicyLock));

    static PolicyLock() => IdProperty.Changed.AddClassHandler<Control>((control, _) => Apply(control));

    public static string? GetId(Control control) => control.GetValue(IdProperty);

    public static void SetId(Control control, string? value) => control.SetValue(IdProperty, value);

    private static void Apply(Control control)
    {
        if (GetId(control) is not { } id || ActivePolicy.Allows(id)) return;
        Lock(control, id);
        // once more when it is shown: an IsEnabled binding read after this attribute must not switch it on again
        control.AttachedToVisualTree += (_, _) => Lock(control, id);
    }

    private static void Lock(Control control, string id)
    {
        control.IsEnabled = false;
        ToolTip.SetTip(control, ViewModels.MainViewModel.PolicyRefusal(id));
        ToolTip.SetShowOnDisabled(control, true);
    }
}
