using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Remindly.App.Services;

namespace Remindly.App.Views;

/// <summary>Shared editor chrome. Draft protection is explicitly enabled by task/list editors.</summary>
public abstract class EditorWindow : Window
{
    protected readonly StackPanel Form = new() { Margin = new Thickness(20, 12, 20, 12) };
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 8, 14, 14) };
    protected readonly TextBlock Warning = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 0, 20, 0), Visibility = Visibility.Collapsed };
    private bool _guard, _accepted;
    private string? _initialForm;
    internal Func<MessageBoxResult>? UnsavedDecisionForTest { get; set; }
    internal bool HasUnsavedChanges => _guard && _initialForm != null && CaptureForm() != _initialForm;

    protected EditorWindow(string title, double width = 520)
    {
        Title = title; Width = width; SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false; Theme.Dress(this); FontSize = 14;
        Warning.SetResourceReference(TextBlock.ForegroundProperty, "AmberBrush");
        if (Application.Current.MainWindow is { IsVisible: true } owner && owner != this) Owner = owner;
        var root = new DockPanel();
        var head = new Border { Background = Ui.Res("AccentSoftBrush"), Padding = new Thickness(20, 12, 20, 12), Child = Ui.Text(title, 17, FontWeights.SemiBold, Ui.Res("AccentInkBrush")) };
        DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);
        DockPanel.SetDock(_buttons, Dock.Bottom); root.Children.Add(_buttons);
        DockPanel.SetDock(Warning, Dock.Bottom); root.Children.Add(Warning);
        root.Children.Add(Ui.Scroll(Form)); Content = root;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { TrySave(); e.Handled = true; }
        };
        ContentRendered += (_, _) => _initialForm ??= CaptureForm();
        Closing += CheckUnsavedDraft;
    }

    protected void EnableUnsavedGuard() => _guard = true;
    protected void AcceptAndClose() { _accepted = true; Close(); }

    private void CheckUnsavedDraft(object? sender, CancelEventArgs e)
    {
        if (_accepted || !HasUnsavedChanges) return;
        var answer = App.IsSmokeTest && UnsavedDecisionForTest != null ? UnsavedDecisionForTest()
            : MessageBox.Show(this, "Save your changes before closing?\n\nYes: Save changes\nNo: Discard changes\nCancel: Keep editing",
                "Unsaved changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.Cancel || answer == MessageBoxResult.None) { e.Cancel = true; return; }
        if (answer == MessageBoxResult.Yes && !Save()) { e.Cancel = true; return; }
        _accepted = true;
    }

    // Read logical input controls, not rendered templates. Collapsed repeat fields remain part of the draft.
    private string CaptureForm()
    {
        var values = new List<string>();
        void Visit(DependencyObject node)
        {
            switch (node)
            {
                case ComboBox combo: values.Add("combo:" + combo.SelectedIndex + ":" + combo.Text); return;
                case DatePicker date: values.Add("date:" + date.SelectedDate?.ToString("O", CultureInfo.InvariantCulture)); return;
                case TextBox text: values.Add("text:" + text.Text); return;
                case ToggleButton toggle: values.Add("toggle:" + toggle.IsChecked); return;
                case RangeBase range: values.Add("range:" + range.Value.ToString(CultureInfo.InvariantCulture)); return;
            }
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) Visit(child);
        }
        Visit(Form);
        return JsonSerializer.Serialize(values);
    }

    protected void AddButtons(string saveText = "Save", params Button[] extra)
    {
        foreach (var button in extra) _buttons.Children.Add(button);
        _buttons.Children.Add(WorkspaceUi.Identify(Ui.Btn("Cancel", Close), "editor-cancel", "Cancel editing"));
        var save = WorkspaceUi.Identify(Ui.Primary(saveText, TrySave, "Save (Ctrl+Enter)"), "editor-save", "Save changes");
        save.IsDefault = false; save.Margin = new Thickness(0); _buttons.Children.Add(save);
    }
    private void TrySave() { if (Save()) AcceptAndClose(); }
    protected abstract bool Save();
    protected void Field(string label, UIElement control)
    {
        Form.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.Resources["FieldLabel"] });
        Form.Children.Add(control);
    }
    protected void Section(string title) => Form.Children.Add(Ui.H2(title));
    protected static ComboBox Combo(IEnumerable<string> items, string? selected, bool editable = false)
    {
        var combo = new ComboBox { IsEditable = editable, IsTextSearchEnabled = true };
        foreach (var item in items) combo.Items.Add(item);
        if (editable) combo.Text = selected ?? "";
        else combo.SelectedItem = selected != null && combo.Items.Contains(selected) ? selected : (combo.Items.Count > 0 ? combo.Items[0] : null);
        return combo;
    }
    protected static Grid TwoColumns(UIElement a, UIElement b) => Ui.Columns("*,12,*", a, new Border(), b);
    protected static StackPanel Labeled(string label, UIElement control) =>
        Ui.Stack(new TextBlock { Text = label, Style = (Style)Application.Current.Resources["FieldLabel"] }, control);
    protected void ShowWarning(string? text)
    {
        Warning.Text = text ?? ""; Warning.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }
    public void Open() { if (App.IsSmokeTest) Show(); else ShowDialog(); }
}
