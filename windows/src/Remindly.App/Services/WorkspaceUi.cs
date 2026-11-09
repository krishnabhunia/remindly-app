using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Remindly.App.Services;

/// <summary>Opt-in workspace controls. Existing shopping and settings card gestures stay unchanged.</summary>
internal static class WorkspaceUi
{
    public static T Identify<T>(T element, string id, string name) where T : FrameworkElement
    {
        AutomationProperties.SetAutomationId(element, id);
        AutomationProperties.SetName(element, name);
        if (element is Control control) control.FocusVisualStyle = FocusStyle();
        return element;
    }

    private static Style FocusStyle()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        border.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(FrameworkElement.MarginProperty, new Thickness(2));
        var style = new Style(typeof(Control));
        style.Setters.Add(new Setter(Control.TemplateProperty,
            new ControlTemplate(typeof(Control)) { VisualTree = border }));
        return style;
    }

    public static Button Action(string id, string name, object content, Action action)
    {
        var button = Identify(Ui.Plain(content, action, name), id, name);
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.Cursor = Cursors.Hand;
        // A name/pencil/menu click must not also activate the containing row.
        button.Click += (_, e) => e.Handled = true;
        return button;
    }

    public static Button Link(string id, string name, string text, Action action, double size = 14, bool done = false)
    {
        var label = Ui.Text(text, size, FontWeights.SemiBold, wrap: true);
        if (done) { label.TextDecorations = TextDecorations.Strikethrough; label.Foreground = Ui.Res("InkSubtleBrush"); }
        var button = Action(id, name, label, action);
        button.Padding = new Thickness(2);
        button.MouseEnter += (_, _) => { if (!done) label.TextDecorations = TextDecorations.Underline; };
        button.MouseLeave += (_, _) => { if (!done) label.TextDecorations = null; };
        return button;
    }

    public static RadioButton Segment(string id, string text, string group, Action select)
    {
        var button = Identify(Ui.Chip(text, group, false, select), id, text);
        var style = new Style(typeof(RadioButton), (Style)Application.Current.Resources["Chip"]);
        var selected = new Trigger { Property = RadioButton.IsCheckedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        selected.Setters.Add(new Setter(Control.BorderBrushProperty, Ui.Res("AccentBrush")));
        selected.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 0, 2)));
        style.Triggers.Add(selected);
        button.Style = style;
        return button;
    }

    public static Button More(string id, string name, Func<ContextMenu> menu)
    {
        Button? button = null;
        button = Action(id, name, "⋯", () =>
        {
            var popup = menu();
            popup.PlacementTarget = button;
            popup.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            popup.IsOpen = true;
        });
        button.MinWidth = 34;
        button.FontSize = 20;
        return button;
    }
}

/// <summary>A replaceable placeholder without accumulating TextChanged handlers on every refresh.</summary>
internal sealed class WorkspaceSearchBox : TextBox
{
    private string _hint = "Search";
    public string Hint
    {
        get => _hint;
        set { if (_hint == value) return; _hint = value; ToolTip = value; AutomationProperties.SetName(this, value); PaintHint(); }
    }

    public WorkspaceSearchBox()
    {
        MinWidth = 180;
        Width = 240;
        TextChanged += (_, _) => PaintHint();
        PaintHint();
    }

    private void PaintHint()
    {
        if (Text.Length > 0) { Background = Ui.Res("InputBrush"); return; }
        Background = new VisualBrush(new Border
        {
            Background = Ui.Res("InputBrush"),
            Child = new TextBlock
            {
                Text = _hint, Foreground = Ui.Res("InkHintBrush"), Margin = new Thickness(8, 0, 0, 0),
                FontSize = 13, FontFamily = (FontFamily)Application.Current.Resources["AppFont"],
            },
        }) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Center };
    }
}
