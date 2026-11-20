using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.ViewModels;

namespace PdfKnead.Controls;

/// <summary>
/// Filling in forms (existing and newly created fields): a click on a field (without dragging, since dragging moves it) fills it in:
/// text in the input box, check boxes and radio buttons toggle, choices as a list, date fields with a calendar. Tab moves to the
/// next field, Space toggles, Enter or F2 opens.
/// </summary>
public sealed partial class DocumentView
{
    /// <summary>Fills in a field according to its kind.</summary>
    private void ActivateField(ElementRef target)
    {
        if (_doc is not { } doc || !DocumentViewModel.IsFillable(target.Element) || doc.SpecOf(target.Element) is not { } spec) return;
        if (!doc.CanFillForms)
        {
            doc.Notify(Tr.T("Dieses Dokument erlaubt keine Änderungen (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen."));
            return;
        }
        switch (spec.Kind)
        {
            case FormFieldKind.CheckBox or FormFieldKind.Radio:
                doc.ToggleField(target);
                break;
            case FormFieldKind.ComboBox or FormFieldKind.ListBox:
                ShowOptions(doc, target, spec);
                break;
            case FormFieldKind.Text when spec.Code is { } look:
                ShowCodeInput(doc, target, look);
                break;
            case FormFieldKind.Text when spec.Format.Kind == FieldFormatKind.Date:
                ShowCalendar(doc, target, spec.Format);
                break;
            case FormFieldKind.Text:
                doc.BeginFill(target);
                break;
        }
    }

    /// <summary>
    /// Choice field: list of the options below the field (the chosen one with a check mark, at most 1,000 options); editable ones also with
    /// custom text. List boxes with multiple selection: a click selects or deselects an item (without Ctrl), and the list stays open until “Done”.
    /// </summary>
    private void ShowOptions(DocumentViewModel doc, ElementRef target, FieldSpec spec)
    {
        var multi = spec.Kind == FormFieldKind.ListBox && spec.MultiSelect;
        var chosenValues = DocumentViewModel.ValueOf(target.Element).Split('\n').ToHashSet(StringComparer.Ordinal);
        var current = target;
        var flyout = new Flyout { FlyoutPresenterClasses = { "choiceFlyout" } };
        var items = new StackPanel { Spacing = 2 };
        Button? first = null;
        foreach (var option in spec.Options.Take(1000))
        {
            var selected = chosenValues.Contains(option.Export);
            var check = new Avalonia.Controls.Shapes.Path { Data = CheckMark, Classes = { "choiceCheck" }, IsVisible = selected };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("22,*") };
            row.Children.Add(check);
            var text = new TextBlock { Text = option.Display.Length == 0 ? " " : option.Display, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var item = new Button { Content = row, Classes = { "choiceItem" } };
            if (selected)
            {
                item.Classes.Add("selected");
                first ??= item;
            }
            item.Click += (_, _) =>
            {
                if (!multi)
                {
                    flyout.Hide();
                    doc.SetFieldValue(current, option.Export);
                    return;
                }
                // Multiple selection: toggle, the list stays open; stored in the order of the items.
                if (!chosenValues.Remove(option.Export)) chosenValues.Add(option.Export);
                var on = chosenValues.Contains(option.Export);
                check.IsVisible = on;
                item.Classes.Set("selected", on);
                var value = string.Join('\n', spec.Options.Where(o => chosenValues.Contains(o.Export)).Select(o => o.Export));
                if (doc.SetFieldValue(current, value) && doc.SelectedElement is { } updated) current = updated;
            };
            items.Children.Add(item);
        }
        if (spec.Editable || spec.Options.Count == 0)
        {
            if (items.Children.Count > 0) items.Children.Add(new Separator { Margin = new Thickness(6, 4) });
            var own = new Button { Content = Tr.T("Eigener Text …"), Classes = { "calLink" }, HorizontalAlignment = HorizontalAlignment.Left };
            own.Click += (_, _) =>
            {
                flyout.Hide();
                doc.BeginFill(current);
            };
            items.Children.Add(own);
        }
        var width = Math.Max(200, ViewRect(target.Page, target.Element.Rect).Width);
        var content = new StackPanel
        {
            MinWidth = width,
            Children =
            {
                new TextBlock
                {
                    Text = multi ? doc.FieldNameOf(target.Element) + " · " + Tr.T("mehrere wählbar") : doc.FieldNameOf(target.Element),
                    Classes = { "choiceHeader" },
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
                new ScrollViewer { Content = items, MaxHeight = 340, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
            },
        };
        if (multi)
        {
            var done = new Button { Content = Tr.T("Fertig"), Classes = { "primary", "calToday" }, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 4, 2) };
            done.Click += (_, _) => flyout.Hide();
            content.Children.Add(done);
        }
        flyout.Content = content;
        flyout.Opened += (_, _) => (first ?? items.Children.OfType<Button>().FirstOrDefault())?.Focus();
        ShowUnder(flyout, target, centered: spec.Kind == FormFieldKind.ListBox, width);
    }

    /// <summary>Shows a flyout directly below the field: left-aligned (combo box, calendar) or centred (list box).</summary>
    private void ShowUnder(Avalonia.Controls.Primitives.PopupFlyoutBase flyout, ElementRef target, bool centered, double width)
    {
        var field = ViewRect(target.Page, target.Element.Rect);
        flyout.Placement = PlacementMode.AnchorAndGravity;
        flyout.PlacementAnchor = Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.TopLeft;
        flyout.PlacementGravity = Avalonia.Controls.Primitives.PopupPositioning.PopupGravity.BottomRight;
        flyout.HorizontalOffset = centered ? field.Center.X - width / 2 : field.Left;
        flyout.VerticalOffset = field.Bottom + 3;
        flyout.ShowAt(this);
    }

    /// <summary>
    /// Code field (KONZEPT.md, section 5.19, step 4): the content in a box below the field — several lines for QR and Data Matrix —, checked
    /// while typing. „Übernehmen“, Enter (Ctrl+Enter for several lines) or a click elsewhere takes it over in one step; Esc and „Abbrechen“
    /// leave the field as it was. Content that gives no code is never taken over.
    /// </summary>
    private void ShowCodeInput(DocumentViewModel doc, ElementRef target, CodeLook look)
    {
        const double Width = 340;
        var current = DocumentViewModel.ValueOf(target.Element);
        var box = new TextBox
        {
            Text = current, AcceptsReturn = look.IsMatrix, TextWrapping = TextWrapping.Wrap, MinHeight = look.IsMatrix ? 76 : 30, MaxHeight = 220,
            Margin = new Thickness(6, 0),
        };
        Avalonia.Automation.AutomationProperties.SetName(box, Tr.T("Inhalt des Codes"));
        var error = new TextBlock { Classes = { "codeInputError" }, IsVisible = false };
        var take = new Button { Content = Tr.T("Übernehmen"), Classes = { "primary" } };
        var cancel = new Button { Content = Tr.T("Abbrechen") };
        var flyout = new Flyout { FlyoutPresenterClasses = { "choiceFlyout" } };
        var closed = false;
        string? Problem(string text) => text.Length == 0 ? null : CodeHints.Error(look.Apply(new CodeElement { Rect = target.Element.Rect, Text = text }));
        bool Take()
        {
            var text = box.Text ?? "";
            if (Problem(text) is { } problem)
            {
                error.Text = problem;
                error.IsVisible = true;
                return false;
            }
            if (text != current) doc.SetFieldValue(target, text);
            return true;
        }
        box.TextChanged += (_, _) =>
        {
            var problem = Problem(box.Text ?? "");
            error.Text = problem ?? "";
            error.IsVisible = problem is not null;
            take.IsEnabled = problem is null;
        };
        box.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                closed = true;
                flyout.Hide();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && (!look.IsMatrix || e.KeyModifiers.HasFlag(KeyModifiers.Control)))
            {
                e.Handled = true;
                if (Take())
                {
                    closed = true;
                    flyout.Hide();
                }
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        take.Click += (_, _) =>
        {
            if (!Take()) return;
            closed = true;
            flyout.Hide();
        };
        cancel.Click += (_, _) =>
        {
            closed = true;
            flyout.Hide();
        };
        // a click elsewhere takes over what fits; what does not stays out, with the reason
        flyout.Closed += (_, _) =>
        {
            if (closed || (box.Text ?? "") == current) return;
            if (Problem(box.Text ?? "") is { } problem) doc.Notify(Tr.T("Nicht übernommen: {0}", problem));
            else doc.SetFieldValue(target, box.Text ?? "");
        };
        flyout.Content = new StackPanel
        {
            Width = Width,
            Children =
            {
                new TextBlock { Text = doc.FieldNameOf(target.Element) + " · " + CodeHints.FieldTitle(look), Classes = { "choiceHeader" }, TextTrimming = TextTrimming.CharacterEllipsis },
                box,
                new TextBlock { Text = CodeHints.ContentHint(look.Format, look.Gs1) + (look.IsMatrix ? " " + Tr.T("Strg+Enter übernimmt.") : ""), Classes = { "codeInputHint" } },
                error,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(6, 8, 6, 2), Children = { cancel, take } },
            },
        };
        flyout.Opened += (_, _) =>
        {
            box.Focus();
            box.CaretIndex = box.Text?.Length ?? 0;
        };
        ShowUnder(flyout, target, centered: false, Width);
    }

    private static readonly Avalonia.Media.Geometry CheckMark = Avalonia.Media.Geometry.Parse("M 3 8.5 L 6.5 12 L 13 4.5");

    /// <summary>Date field: calendar, plus typing and clearing. The date is entered in the field's format.</summary>
    private void ShowCalendar(DocumentViewModel doc, ElementRef target, FieldFormat format)
    {
        var calendar = new FieldCalendar(format.DateOf(DocumentViewModel.ValueOf(target.Element)));
        var flyout = new Flyout { Content = calendar, FlyoutPresenterClasses = { "calendarFlyout" } };
        calendar.DateChosen += chosen =>
        {
            flyout.Hide();
            doc.SetFieldValue(target, format.Format(chosen));
        };
        calendar.TypeRequested += () =>
        {
            flyout.Hide();
            doc.BeginFill(target);
        };
        calendar.Cleared += () =>
        {
            flyout.Hide();
            doc.SetFieldValue(target, "");
        };
        flyout.Opened += (_, _) => calendar.Focus();
        ShowUnder(flyout, target, centered: false, calendar.Width);
    }

    /// <summary>Keys for fields: Tab/Shift+Tab next/previous field, Space toggles, Enter or F2 opens. true = handled.</summary>
    private bool FieldKey(KeyEventArgs e)
    {
        if (_doc is not { } doc || doc.FieldsMovable) return false;   // there fields are moved, not filled in
        if (e.Key == Key.Tab)
            return FocusNextField(e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        if (doc.SelectedElement is not { } selected || !DocumentViewModel.IsFillable(selected.Element) || doc.SpecOf(selected.Element) is not { } spec) return false;
        switch (e.Key)
        {
            case Key.Space when spec.Kind is FormFieldKind.CheckBox or FormFieldKind.Radio:
            case Key.Enter or Key.F2:
                ActivateField(selected);
                return true;
        }
        return false;
    }

    /// <summary>Moves to the next (previous) field: selects it, scrolls it into view, and opens text fields in the input box right away.</summary>
    /// <param name="backwards">Move to the previous field instead of the next one.</param>
    /// <returns><see langword="false"/> if there is no field.</returns>
    internal bool FocusNextField(bool backwards)
    {
        if (_doc is not { } doc || doc.NextField(doc.SelectedElement, backwards) is not { } next) return false;
        doc.SelectedElement = next;
        var r = next.Element.Rect;
        Reveal(next.Page, new TextQuad(r.Left, r.Bottom, r.Right, r.Bottom, r.Right, r.Top, r.Left, r.Top));
        if (doc.SpecOf(next.Element) is { Kind: FormFieldKind.Text } spec && spec.Format.Kind != FieldFormatKind.Date)
        {
            if (spec.Code is { } look) ShowCodeInput(doc, next, look);
            else doc.BeginFill(next);
        }
        InvalidateVisual();
        return true;
    }
}
