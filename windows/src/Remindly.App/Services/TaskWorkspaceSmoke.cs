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
        // The phone's Move-to-Done countdown, shortened for the test; animations shortened too.
        state.UpdateSettings(s => s with { Phone = s.Phone with { TasksMoveDelaySec = 1, CallsMoveDelaySec = 1 } });
        (DoneFlow.CelebrateMs, DoneFlow.CollapseMs, DoneFlow.SlideMs, DoneFlow.GlowMs) = (60, 40, 60, 120);
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
                Check(Require<StackPanel>(view, "lists-pinned-heading").IsVisible, "pinned lists have a Pinned heading");
                Check(Require<StackPanel>(view, "lists-all-heading").IsVisible, "other lists sit under All lists");
                Check(Require<TextBlock>(view, $"task-list-pin-{list.Id}").Text == Glyphs.Pin, "pinned list shows a pin icon");
                Check(!Descendants(view).OfType<TextBlock>().Any(t => t.Text.Contains("· Pinned")), "no 'Pinned' word on list rows");
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
                Check(Require<Button>(view, "current-list-menu").IsVisible, "list ⋯ menu is visible inside the list");
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
                Check(!state.Item(item.Id)!.Done && DoneFlow.IsPending(DoneFlow.Key(item)), "tick starts the phone's countdown; nothing saved yet");
                Check(Require<Border>(view, $"done-countdown-item-{item.Id}").IsVisible, "Moving to Done chip is shown on the card");
                Check(Require<CheckBox>(view, $"complete-task-{item.Id}").IsChecked == true, "checkbox stays ticked during the countdown");
                Click(Require<Button>(view, $"done-undo-item-{item.Id}"));
                await Idle();
                Check(!DoneFlow.IsBusy(DoneFlow.Key(item)) && !state.Item(item.Id)!.Done, "UNDO keeps the task active");
                Check(!Descendants(view).OfType<Border>().Any(b => AutomationProperties.GetAutomationId(b) == $"done-countdown-item-{item.Id}"), "UNDO removes the chip");
                checkbox = Require<CheckBox>(view, $"complete-task-{item.Id}");
                checkbox.IsChecked = true;
                checkbox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(await Until(() => state.Item(item.Id)!.Done, 4000), "after the countdown and the green tick the task is Done");
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

                // Esc goes back from a list, but not while typing in the search box.
                Check(!view.HandleWorkspaceShortcut(Key.Escape, ModifierKeys.None, Require<TextBox>(view, "workspace-search"))
                    && view.CurrentLocation.IsInsideList,
                    "Esc in the search box stays in the list");
                Check(view.HandleWorkspaceShortcut(Key.Escape, ModifierKeys.None, Require<Button>(view, "current-list-menu")),
                    "workspace handles Esc");
                await Idle();
                Check(!view.CurrentLocation.IsInsideList, "Esc goes back from a list");
                Check(!Require<Button>(view, "current-list-menu").IsVisible, "list ⋯ menu hidden outside a list");

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
            await IconPickerAndNames(main, check);
            await DoneFlowAndPhone(main, check, list, item);

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
            DoneFlow.CancelAll();
            state.Restore(snapshot);
            ItemsView.CommandView = ItemsView.AllView;
            main.ChooseDesign(Designs.Fluent);
        }
    }

    /// <summary>F14 icon picker and F1 single-click names on Calls, Shops and Products.</summary>
    private static async Task IconPickerAndNames(MainWindow main, Action<bool, string> check)
    {
        var state = AppState.Current;
        var list = TaskLists.Live(state.Settings.TaskLists).First(l => l.Name == "R1 Projects");
        TaskListEditor.Edit(list);
        await Idle();
        var editor = Application.Current.Windows.OfType<TaskListEditor>().Single();
        var icons = Descendants(editor).OfType<RadioButton>()
            .Where(r => AutomationProperties.GetAutomationId(r).StartsWith("task-list-icon-")).ToList();
        check(icons.Count == TaskListIcons.Presets.Count, "Icons: picker shows every preset");
        check(icons.Count(r => r.IsChecked == true) == 1 && (string)icons.Single(r => r.IsChecked == true).Content == TaskListIcons.Default,
            "Icons: a list without an icon shows the default selected");
        check(!Descendants(editor).OfType<TextBox>().Any(t => AutomationProperties.GetAutomationId(t) != "task-list-name"),
            "Icons: no free-text icon box");
        var briefcase = icons.First(r => (string)r.Content == "💼");
        briefcase.IsChecked = true;
        Click(Require<Button>(editor, "editor-save"));
        await Idle();
        check(TaskLists.Live(state.Settings.TaskLists).First(l => l.Id == list.Id).Icon == "💼", "Icons: chosen icon is saved");

        void Names<TEditor>(string id, string what) where TEditor : Window
        {
            var name = Descendants(main).OfType<Button>().FirstOrDefault(b => AutomationProperties.GetAutomationId(b).StartsWith(id));
            check(name != null, $"{what}: name is a button");
            if (name == null) return;
            Click(name);
            check(Application.Current.Windows.OfType<TEditor>().Count() == 1, $"{what}: one click on the name opens one editor");
            foreach (var w in Application.Current.Windows.OfType<TEditor>().ToArray()) w.Close();
        }
        main.SetMode("TASK"); main.Go(main.CallsTab); await Idle();
        Names<CallEditor>("call-name-", "Calls");
        await Idle();
        main.SetMode("SHOP"); main.Go(main.ShopsTab); await Idle();
        Names<ShopEditor>("shop-name-", "Shops");
        await Idle();
        main.Go(main.ProductsTab); await Idle();
        Names<ProductEditor>("product-name-", "Products");
        await Idle();
        main.SetMode("TASK"); main.Go(main.TasksTab); await Idle();
    }

    /// <summary>Approved M1–M5 and S5/S8/S14: revive, delay Off, Calls countdown, phone settings in Settings and on cards.</summary>
    private static async Task DoneFlowAndPhone(MainWindow main, Action<bool, string> check, TaskList list, Item item)
    {
        var state = AppState.Current;
        main.ChooseDesign(Designs.Fluent);
        main.Go(main.TasksTab);
        await Idle();
        var view = (ItemsView)main.Page.Content;
        view.OpenTaskList(list.Id);
        await Idle();
        state.Complete(state.Item(item.Id)!);
        Require<RadioButton>(view, "filter-done").IsChecked = true;
        await Idle();
        var done = Require<CheckBox>(view, $"complete-task-{item.Id}");
        done.IsChecked = false;
        done.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        check(await Until(() => !state.Item(item.Id)!.Done, 3000), "Done: untick slides the task back to Active");
        Require<RadioButton>(view, "filter-active").IsChecked = true;
        await Idle();

        state.UpdateSettings(s => s with { Phone = s.Phone with { TasksMoveDelaySec = 0 } });
        await Idle();
        var tick = Require<CheckBox>(view, $"complete-task-{item.Id}");
        tick.IsChecked = true;
        tick.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        check(!DoneFlow.IsPending(DoneFlow.Key(item)), "Done: delay Off has no countdown");
        check(await Until(() => state.Item(item.Id)!.Done, 3000), "Done: delay Off goes straight to the green tick");
        state.Revive(state.Item(item.Id)!);

        // Card details from the phone: checkbox hidden on Tasks cards.
        state.UpdateSettings(s => s with { Phone = s.Phone with { TabCardOv = new() { ["tCardCheckbox"] = "OFF" } } });
        await Idle();
        check(Require<CheckBox>(view, $"complete-task-{item.Id}").Visibility == Visibility.Collapsed, "Phone: Card details hides the checkbox");
        state.UpdateSettings(s => s with { Phone = s.Phone with { TabCardOv = new() } });

        // New item's due date from the phone (Tomorrow 10:00 by default).
        check(ItemsView.AddQuick(Tab.TASKS, "R1 Phone default due", list.Id), "Phone: quick add works");
        var added = state.LiveItems(Tab.TASKS).First(i => i.Title == "R1 Phone default due");
        check(added.DueAt == state.Phone.NewDue(Tab.TASKS, state.Now).DueAt, "Phone: quick add uses the phone's new-item due date");

        // Calls: same countdown with UNDO.
        main.Go(main.CallsTab);
        await Idle();
        var call = state.LiveCalls().First(c => !c.Done);
        var callCheck = Require<CheckBox>(main, $"complete-call-{call.Id}");
        callCheck.IsChecked = true;
        callCheck.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Idle();
        check(Require<Border>(main, $"done-countdown-call-{call.Id}").IsVisible, "Calls: Moving to Done chip is shown");
        Click(Require<Button>(main, $"done-undo-call-{call.Id}"));
        await Idle();
        check(!state.LiveCalls().First(c => c.Id == call.Id).Done, "Calls: UNDO keeps the call-back active");

        // Visible tabs and Settings → Follows your phone.
        state.UpdateSettings(s => s with { Phone = s.Phone with { ShowLearn = false } });
        await Idle();
        check(main.NavEntries().All(e => e.Tab != main.LearnTab), "Phone: a tab hidden on the phone is hidden in the menu");
        state.UpdateSettings(s => s with { Phone = s.Phone with { ShowLearn = true } });
        main.Go(main.SettingsTab);
        await Idle();
        check(Require<StackPanel>(main, "settings-phone").IsVisible, "Settings: Follows your phone section is shown");
        check(Require<TextBlock>(main, "settings-phone-source").Text.Contains("phone's defaults"), "Settings: says where the values come from");
        main.Go(main.TasksTab);
        await Idle();
    }

    private static async Task<bool> Until(Func<bool> condition, int timeoutMs)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            if (condition()) return true;
            await Idle();
        }
        return condition();
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
