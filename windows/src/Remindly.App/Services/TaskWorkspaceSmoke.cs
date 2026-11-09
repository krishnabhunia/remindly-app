using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Remindly.App.Views;
using Remindly.Core;

namespace Remindly.App.Services;

/// <summary>Runs in the existing Windows smoke-test process with its isolated --data-dir.</summary>
internal static class TaskWorkspaceSmoke
{
    public static async Task Run(MainWindow main, Action<bool, string> check, Action<Window, string> capture)
    {
        var state = AppState.Current;
        var snapshot = state.TakeSnapshot();
        var list = new TaskList { Id = Ids.Next(), Name = "R1 Projects", CreatedAt = state.Now, UpdatedAt = state.Now, Pinned = true };
        var filler = Enumerable.Range(1, 28).Select(n => new TaskList
        { Id = Ids.Next(), Name = $"R1 Empty {n:00}", CreatedAt = state.Now, UpdatedAt = state.Now }).ToList();
        state.UpdateSettings(s => TaskLists.Mirror(s with { TaskLists = s.TaskLists.Concat(filler).Append(list).ToList() }));
        var item = state.NewItem(Tab.TASKS, "R1 Verify single click") with { ListId = list.Id, Group = list.Name };
        state.Upsert(item);
        try
        {
            foreach (var design in Designs.All)
            {
                ItemsView.CommandView = ItemsView.AllView;
                main.ChooseDesign(design.Code);
                main.Go(main.TasksTab);
                await Idle();
                var view = (ItemsView)main.Page.Content;
                string prefix = "R1 " + design.Letter + ": ";
                void Check(bool result, string message) => check(result, prefix + message);
                var search = Require<TextBox>(view, "workspace-search");
                Check(Require<RadioButton>(view, "view-lists").IsChecked == true, "Lists selected on overview");
                Check(!Require<RadioButton>(view, "filter-active").IsVisible, "task filters hidden on overview");
                Check(!Require<TextBox>(view, "quick-task-title").IsVisible, "task composer hidden on overview");
                Check(Require<Button>(view, "workspace-new").Content.ToString() == "+ New list", "overview action is New list");
                search.Text = "R1";
                await Idle();
                var scroll = Descendants(view).OfType<ScrollViewer>().First(s => s.ScrollableHeight > 0);
                scroll.ScrollToVerticalOffset(150);
                await Idle();
                double position = scroll.VerticalOffset;
                var open = Require<Button>(view, $"open-task-list-{list.Id}");
                Check(open.FocusVisualStyle != null, "list name has visible keyboard focus style");
                Click(open);
                await Idle();
                Check(view.CurrentLocation.ListId == list.Id, "one button Click opens the list");
                Check(!Require<RadioButton>(view, "view-all-tasks").IsVisible, "All tasks switch hidden inside list");
                Check(Require<Button>(view, "workspace-new").Content.ToString() == "+ New task", "inside list cannot create a list");
                Check(Require<Button>(view, "crumb-lists").IsVisible, "Lists breadcrumb is visible");
                Check(Require<Button>(view, "task-back").IsVisible, "Back is visible");
                Check(Require<RadioButton>(view, "filter-active").IsChecked == true, "Active initially selected");
                Click(Require<Button>(view, $"edit-task-{item.Id}"));
                await Idle();
                Check(Application.Current.Windows.OfType<ItemEditor>().Count() == 1, "one task-name click opens exactly one editor");
                var editor = Application.Current.Windows.OfType<ItemEditor>().Single();
                editor.Close();
                await Idle();
                var checkbox = Require<CheckBox>(view, $"complete-task-{item.Id}");
                checkbox.IsChecked = true;
                checkbox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Idle();
                Check(state.Item(item.Id)!.Done, "checkbox completes task");
                Check(!Application.Current.Windows.OfType<ItemEditor>().Any(), "checkbox does not open editor");
                state.Revive(state.Item(item.Id)!);
                await Idle();
                Click(Require<Button>(view, "task-back"));
                await Idle();
                Check(view.CurrentLocation.IsOverview && search.Text == "R1", "Back restores overview search");
                var restoredScroll = Descendants(view).OfType<ScrollViewer>().First(s => s.ScrollableHeight > 0);
                Check(Math.Abs(restoredScroll.VerticalOffset - position) < 2, "Back restores overview scroll position");
                view.ShowAllTasks();
                await Idle();
                Check(Require<RadioButton>(view, "view-all-tasks").IsChecked == true, "All tasks selected");
                Check(Require<RadioButton>(view, "view-lists").IsChecked == false, "Lists not selected in All tasks");
                Check(Require<Button>(view, "workspace-new").Content.ToString() == "+ New task", "New list absent from All tasks");
                var quick = Require<TextBox>(view, "quick-task-title");
                quick.Text = "R1 Quick add";
                Check(!Require<Button>(view, "quick-task-add").IsEnabled, "All tasks requires a destination");
                Require<ComboBox>(view, "quick-task-list").SelectedIndex = 1; // explicit Unsorted
                Check(Require<Button>(view, "quick-task-add").IsEnabled, "explicit Unsorted is allowed");
                quick.Clear();
                search.Text = "R1 Verify";
                await Idle();
                Click(Require<Button>(view, $"task-list-badge-{item.Id}"));
                await Idle();
                Check(view.CurrentLocation.ListId == list.Id, "list badge opens its list");
                view.BackInTasks();
                await Idle();
                Check(view.CurrentLocation.IsAllTasks && search.Text == "R1 Verify", "badge Back restores filtered All tasks");
                search.Text = "no-match-6f28be";
                await Idle();
                Check(Descendants(view).OfType<TextBlock>().Any(t => t.Text == "No matching tasks"), "search miss has a distinct empty state");
                search.Clear();
                view.OpenTaskList(list.Id);
                await Idle();
                Check(view.HandleWorkspaceShortcut(Key.N, ModifierKeys.Control), "workspace handles Ctrl+N");
                await Idle();
                editor = Application.Current.Windows.OfType<ItemEditor>().Single();
                Check(Require<ComboBox>(editor, "task-editor-list").Text.Contains(list.Name), "Ctrl+N preselects open list");
                editor.Close();
                await Idle();
                capture(main, "r1-" + design.Letter.ToLowerInvariant() + "-inside-list");

                Click(Require<Button>(view, $"edit-task-{item.Id}"));
                await Idle();
                editor = Application.Current.Windows.OfType<ItemEditor>().Single();
                var original = state.Item(item.Id)!.Title;
                Require<TextBox>(editor, "task-editor-title").Text = original + " unsaved";
                editor.UnsavedDecisionForTest = () => MessageBoxResult.Cancel;
                editor.Close();
                Check(editor.IsVisible && editor.HasUnsavedChanges, "Cancel close retains dirty editor");
                Check(state.Item(item.Id)!.Title == original, "unsaved title never reaches the store");
                editor.UnsavedDecisionForTest = () => MessageBoxResult.No;
                editor.Close();
                await Idle();
                Check(!editor.IsVisible && state.Item(item.Id)!.Title == original, "discard leaves original data unchanged");

                Click(Require<Button>(view, $"edit-task-{item.Id}"));
                await Idle();
                editor = Application.Current.Windows.OfType<ItemEditor>().Single();
                Require<TextBox>(editor, "task-editor-title").Text = "";
                Click(Require<Button>(editor, "editor-save"));
                Check(editor.IsVisible, "invalid save keeps editor open");
                Require<TextBox>(editor, "task-editor-title").Text = original;
                Require<ComboBox>(editor, "task-editor-status").SelectedIndex = 1;
                Click(Require<Button>(editor, "editor-save"));
                await Idle();
                Check(!editor.IsVisible && state.Item(item.Id)!.Done, "status edit saves using task completion rules");
                state.Revive(state.Item(item.Id)!);
                await Idle();

                view.ShowAllTasks();
                await Idle();
                Click(Require<Button>(view, "workspace-new"));
                await Idle();
                editor = Application.Current.Windows.OfType<ItemEditor>().Single();
                Require<TextBox>(editor, "task-editor-title").Text = "R1 Explicit destination " + design.Letter;
                Click(Require<Button>(editor, "editor-save"));
                Check(editor.IsVisible, "new task cannot silently use Unsorted");
                Require<ComboBox>(editor, "task-editor-list").SelectedIndex = 1;
                Click(Require<Button>(editor, "editor-save"));
                await Idle();
                Check(!editor.IsVisible && state.LiveItems(Tab.TASKS).Any(i => i.Title == "R1 Explicit destination " + design.Letter && i.ListId == null),
                    "new task saves after explicit destination choice");
                view.ShowTaskLists();
                await Idle();
                capture(main, "r1-" + design.Letter.ToLowerInvariant() + "-lists");
            }
            // Simulate a list disappearing from synchronized settings; the page must recover without deleting tasks.
            main.Go(main.TasksTab);
            var currentView = (ItemsView)main.Page.Content;
            currentView.OpenTaskList(list.Id);
            long stamp = state.Now + 1;
            state.UpdateSettings(s => s with { TaskLists = s.TaskLists.Select(l => l.Id == list.Id ? l with { DeletedAt = stamp, UpdatedAt = stamp } : l).ToList() });
            await Idle();
            check(currentView.CurrentLocation.IsOverview, "R1: deleted open list returns to overview");
            check(state.Item(item.Id)!.DeletedAt == null, "R1: navigation recovery never deletes tasks");
        }
        finally
        {
            foreach (var editor in Application.Current.Windows.OfType<EditorWindow>().ToArray())
            { editor.UnsavedDecisionForTest = () => MessageBoxResult.No; editor.Close(); }
            state.Restore(snapshot);
            ItemsView.CommandView = ItemsView.AllView;
            main.ChooseDesign(Designs.Fluent);
        }
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static T Require<T>(DependencyObject root, string id) where T : FrameworkElement =>
        Descendants(root).OfType<T>().FirstOrDefault(e => AutomationProperties.GetAutomationId(e) == id)
        ?? throw new InvalidOperationException($"R1 control not found: {id}");
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static async Task Idle() { await Task.Delay(100); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
}
