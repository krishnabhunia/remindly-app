using System.Windows;
using System.Windows.Controls;
using Remindly.App.Services;
using Remindly.Core;

namespace Remindly.App.Views;

public sealed partial class ItemsView
{
    // null = lists overview; -1 = all tasks; 0 = Unsorted; positive = stable task-list ID.
    private long? _taskListId;
    private bool HasCommandFilter => _design == Designs.Command && CommandView != AllView;
    private long? TaskListForNew => _tab == Tab.TASKS && _taskListId is > 0 ? _taskListId : null;

    private void AddTaskNavigation()
    {
        if (_tab != Tab.TASKS) return;
        var row = Ui.Row(
            Ui.Btn("Lists", () => { CommandView = AllView; _taskListId = null; _search.Clear(); Refresh(); }),
            Ui.Btn("All tasks", () => { CommandView = AllView; _taskListId = -1; _search.Clear(); Refresh(); }),
            Ui.Btn("+ New list", () => TaskListEditor.Create()));
        row.Margin = new Thickness(0, 0, 0, 10);
        SetDock(row, Dock.Top);
        Children.Add(row);
    }

    private IEnumerable<Item> ScopeToTaskList(IEnumerable<Item> items) =>
        _tab == Tab.TASKS && !HasCommandFilter && _taskListId is long id && id >= 0
            ? TaskLists.Members(items, id, AppState.Current.Settings.TaskLists) : items;

    private string TaskScopeTitle(string fallback)
    {
        if (_tab != Tab.TASKS || HasCommandFilter || _taskListId == null) return fallback;
        return _taskListId == -1 ? "All tasks" : _taskListId == 0 ? "Unsorted" :
            TaskLists.Live(AppState.Current.Settings.TaskLists).FirstOrDefault(l => l.Id == _taskListId)?.Name ?? fallback;
    }

    private bool TryShowTaskLists(long now, string query)
    {
        if (_tab != Tab.TASKS || HasCommandFilter) return false;
        var state = AppState.Current;
        var lists = TaskLists.Live(state.Settings.TaskLists);
        if (_taskListId is > 0 && lists.All(l => l.Id != _taskListId)) _taskListId = null;
        if (_taskListId != null) return false;
        _title.Text = "Tasks";
        _count.Text = $"{lists.Count} lists · {state.LiveItems(Tab.TASKS).Count(i => !i.Done)} active";
        var cards = new StackPanel();
        void AddCard(long id, string name, string? icon, TaskList? record)
        {
            if (query.Length > 0 && !name.Contains(query, StringComparison.OrdinalIgnoreCase)) return;
            var members = TaskLists.Members(state.Data.Items, id, state.Settings.TaskLists);
            int active = members.Count(i => !i.Done), done = members.Count(i => i.Done);
            void OpenList() { _taskListId = id; _selected = null; _search.Clear(); Refresh(); }
            var actions = Ui.Row(Ui.Btn("Open", OpenList));
            if (record != null)
            {
                actions.Children.Add(Ui.Btn("Edit list…", () => TaskListEditor.Edit(record)));
                actions.Children.Add(Ui.Btn("Delete list", () => TaskListEditor.Remove(record), style: "Danger"));
            }
            var summary = Ui.Stack(Ui.Text((icon ?? "📋") + " " + name + (record?.Pinned == true ? " · Pinned" : ""), 17, FontWeights.SemiBold),
                Ui.Sub($"{active} active · {done} done"));
            var body = Ui.Stack(Ui.Columns("*,Auto", summary, actions));
            body.Children.Add(new ProgressBar
            {
                Minimum = 0, Maximum = Math.Max(1, members.Count), Value = done, Height = 5,
                Margin = new Thickness(0, 10, 0, 0), Foreground = Ui.Res("AccentBrush"),
                Background = Ui.Res("NavSelectedBrush"), BorderThickness = new Thickness(0),
            });
            cards.Children.Add(Ui.Card(body, OpenList));
        }
        foreach (var list in lists.OrderByDescending(l => l.Pinned).ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
            AddCard(list.Id, list.Name, list.Icon, list);
        if (TaskLists.Members(state.Data.Items, TaskLists.UnsortedId, state.Settings.TaskLists).Count > 0)
            AddCard(TaskLists.UnsortedId, "Unsorted", null, null);
        if (cards.Children.Count == 0)
            cards.Children.Add(Ui.EmptyState(query.Length > 0 ? "No matching lists" : "No task lists yet",
                query.Length > 0 ? "Clear the search to see all lists." : "Use + New list, or Sync now to download the lists from Android."));
        _body.Content = Ui.Scroll(cards);
        return true;
    }
}

/// <summary>List metadata edits preserve IDs, tombstones and task contents.</summary>
public sealed class TaskListEditor : EditorWindow
{
    private readonly TaskList? _original;
    private readonly TextBox _name = new();
    private readonly TextBox _icon = new();
    private readonly CheckBox _pinned = new() { Content = "Pinned" };

    private TaskListEditor(TaskList? list) : base(list == null ? "New task list" : "Edit task list", 460)
    {
        _original = list;
        _name.Text = list?.Name ?? "";
        _icon.Text = list?.Icon ?? "";
        _pinned.IsChecked = list?.Pinned == true;
        Field("List name", _name);
        Field("Icon (optional)", _icon);
        Form.Children.Add(_pinned);
        AddButtons(list == null ? "Create" : "Save");
        Loaded += (_, _) => { _name.Focus(); _name.SelectAll(); };
    }

    public static void Create() => new TaskListEditor(null).Open();
    public static void Edit(TaskList list) => new TaskListEditor(list).Open();
    private static long NextStamp(long old, long now) => Math.Max(now, old == long.MaxValue ? old : old + 1);

    protected override bool Save()
    {
        var state = AppState.Current;
        var name = _name.Text.Trim();
        if (!TaskLists.ValidName(name)) { ShowWarning("Enter a name other than Unsorted."); return false; }
        if (TaskLists.Live(state.Settings.TaskLists).Any(l => l.Id != _original?.Id && string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)))
        { ShowWarning("A task list already has that name."); return false; }
        var current = _original == null ? null : state.Settings.TaskLists.FirstOrDefault(l => l.Id == _original.Id);
        if (_original != null && (current == null || current.DeletedAt != null))
        { ShowWarning("This list was removed on another device. Close this window and choose another list."); return false; }
        var list = (current ?? new TaskList { Id = Ids.Next(), CreatedAt = state.Now }) with
        {
            Name = name, Icon = string.IsNullOrWhiteSpace(_icon.Text) ? null : _icon.Text.Trim(),
            Pinned = _pinned.IsChecked == true, UpdatedAt = NextStamp(current?.UpdatedAt ?? 0, state.Now),
        };
        if (current != null && current.Name != name)
            state.UpsertMany(TaskLists.Members(state.Data.Items, current.Id, state.Settings.TaskLists)
                .Select(i => i with { ListId = list.Id, Group = name }).ToList());
        state.UpdateSettings(s => TaskLists.Mirror(s with { TaskLists = TaskLists.Merge(s.TaskLists, new[] { list }) }));
        return true;
    }

    public static void Remove(TaskList list)
    {
        var state = AppState.Current;
        var current = TaskLists.Live(state.Settings.TaskLists).FirstOrDefault(l => l.Id == list.Id);
        if (current == null || !Ui.Confirm($"Delete task list \"{current.Name}\"? Its tasks will be kept in Unsorted. This also syncs to Android.")) return;
        var tasks = TaskLists.Members(state.Data.Items, current.Id, state.Settings.TaskLists);
        state.UpsertMany(tasks.Select(i => i with { ListId = null, Group = null }).ToList());
        var stamp = NextStamp(current.UpdatedAt, state.Now);
        var deleted = current with { DeletedAt = stamp, UpdatedAt = stamp };
        state.UpdateSettings(s => TaskLists.Mirror(s with { TaskLists = TaskLists.Merge(s.TaskLists, new[] { deleted }) }));
    }
}
