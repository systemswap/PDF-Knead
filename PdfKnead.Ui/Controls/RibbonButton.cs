using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace PdfKnead.Controls;

/// <summary>
/// Ribbon button: icon and label. Class <c>large</c> = large icon above the text
/// (main action of a group), otherwise small with the text beside it (up to three stacked).
/// Appearance: <c>Styles/Ribbon.axaml</c>.
/// </summary>
public class RibbonButton : Button
{
    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<RibbonButton, Geometry?>(nameof(Icon));

    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<RibbonButton, string?>(nameof(Label));

    static RibbonButton()
    {
        LabelProperty.Changed.AddClassHandler<RibbonButton>((b, _) => SetAutomationName(b, b.Label));
        ToolTip.TipProperty.Changed.AddClassHandler<RibbonButton>((b, _) => SetAutomationName(b, b.Label));
        LoadedEvent.AddClassHandler<RibbonButton>((b, _) => SetAutomationName(b, b.Label));
    }

    /// <summary>
    /// Sets the name for screen readers and UI Automation: the label, or the tooltip for icon-only buttons;
    /// otherwise these buttons would have no name (accessibility, see KONZEPT.md, section 1). A line break the label sets for the ribbon is
    /// read as a space.
    /// </summary>
    internal static void SetAutomationName(Control control, string? label)
    {
        var name = label is { Length: > 0 } ? label : ToolTip.GetTip(control) switch
        {
            string s => s,
            TextBlock t => t.Text,
            _ => null,
        };
        if (name is { Length: > 0 }) AutomationProperties.SetName(control, name.Replace('\n', ' '));
    }

    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }
}

/// <summary>Toggle button in the ribbon (e.g. sidebar on/off), like <see cref="RibbonButton"/>.</summary>
public class RibbonToggleButton : ToggleButton
{
    public static readonly StyledProperty<Geometry?> IconProperty =
        RibbonButton.IconProperty.AddOwner<RibbonToggleButton>();

    public static readonly StyledProperty<string?> LabelProperty =
        RibbonButton.LabelProperty.AddOwner<RibbonToggleButton>();

    static RibbonToggleButton()
    {
        LabelProperty.Changed.AddClassHandler<RibbonToggleButton>((b, _) => RibbonButton.SetAutomationName(b, b.Label));
        ToolTip.TipProperty.Changed.AddClassHandler<RibbonToggleButton>((b, _) => RibbonButton.SetAutomationName(b, b.Label));
        LoadedEvent.AddClassHandler<RibbonToggleButton>((b, _) => RibbonButton.SetAutomationName(b, b.Label));
    }

    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>
    /// With a command, only the binding determines the state: otherwise the click itself would set a value, and a click on the tool
    /// that is already active would show the button as off although the tool is still on.
    /// </summary>
    protected override void Toggle()
    {
        if (Command is null) base.Toggle();
    }
}

/// <summary>
/// Ribbon group: content at the top, the name in small print below it, a thin separator on the right.
/// </summary>
public class RibbonGroup : HeaderedContentControl
{
}
