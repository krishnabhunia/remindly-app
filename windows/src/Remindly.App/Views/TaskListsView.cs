using System.Windows;
using System.Windows.Controls;
using Remindly.App.Services;
using Remindly.Core;
namespace Remindly.App.Views;
public sealed class TaskListsView : DockPanel, IPage
{
    private long? _selected;
    private bool _forceLists;
    private ItemsView? _items;
    private long? _itemsScope;
    private readonly TextBox _search = Ui.Input(placeholder: "Search task lists", width: 220);
    public TaskListsView() { _search.TextChanged += (_, _) => Refresh(); }
    public void OpenList(long id) { _selected = id; Refresh(); }
    public void BackToLists() { _selected = null; _forceLists = true; Refresh(); }
    public void Refresh()
    {
        Children.Clear();
        var state = AppState.Current;
        var lists = state.Settings.TaskLists.Where(l => l.DeletedAt == null).ToList();
        if (_selected != null || (!TaskLists.ListsFirst(state.Settings) && !_forceLists))
        {
            var back = Ui.Row(Ui.Btn("← Task lists", BackToLists), Ui.Sub(_selected is long id ? lists.FirstOrDefault(l => l.Id == id)?.Name ?? "Unsorted" : "All tasks"));
            SetDock(back, Dock.Top); Children.Add(back);
            if (_items == null || _itemsScope != _selected) { _items = new ItemsView(Tab.TASKS, _selected); _itemsScope = _selected; }
            Children.Add(_items); _items.Refresh(); return;
        }
        var heading = Ui.Columns("*,Auto", Ui.Stack(Ui.H1("Task lists"), Ui.Sub("Choose a list before adding tasks. Dates, priorities and repeats stay with each task.")), Ui.Primary("+ New list", () => new TaskListEditor(null).Open()));
        heading.Margin = new Thickness(0,0,0,18); SetDock(heading,Dock.Top); Children.Add(heading);
        var sort = new ComboBox { Width = 140, ItemsSource = new[] { "Recent", "A–Z" }, SelectedIndex = state.Settings.TaskListSort == "AZ" ? 1 : 0 };
        sort.SelectionChanged += (_,_) => { state.UpdateSettingsQuiet(s => s with { TaskListSort = sort.SelectedIndex == 1 ? "AZ" : "RECENT" }); Refresh(); };
        (_search.Parent as Panel)?.Children.Remove(_search);
        var toolbar = Ui.Row(_search, Ui.Sub("  Sort  "), sort); toolbar.Margin = new Thickness(0,0,0,14); SetDock(toolbar,Dock.Top); Children.Add(toolbar);
        var body = new StackPanel();
        void Add(long id, string name, TaskList? list)
        {
            if (_search.Text.Trim().Length > 0 && !name.Contains(_search.Text.Trim(),StringComparison.OrdinalIgnoreCase)) return;
            var items = state.LiveItems(Tab.TASKS).Where(i => TaskLists.IdOf(i,state.Settings.TaskLists) == id).ToList();
            var actions = Ui.Row(Ui.Primary("Open", () => OpenList(id)));
            if (list != null) { actions.Children.Add(Ui.Btn("Edit", () => new TaskListEditor(list).Open())); actions.Children.Add(Ui.Btn("Delete", () => {
                if (!Ui.Confirm($"Delete {name}? Tasks are kept in Unsorted with their reminders.")) return;
                var snap = state.TakeSnapshot();
                state.UpdateSettings(s => s with { TaskLists = s.TaskLists.Select(l => l.Id == id ? l with { DeletedAt = state.Now, UpdatedAt = state.Now } : l).ToList() });
                App.Current.Main.Snack("List deleted · tasks kept in Unsorted", () => state.Restore(snap));
            })); }
            body.Children.Add(Ui.Card(Ui.Columns("*,Auto", Ui.Stack(Ui.Text((list?.Pinned == true ? "★  " : "") + name,16,FontWeights.SemiBold), Ui.Sub($"{items.Count(i => !i.Done)} active · {items.Count(i => i.Done)} done · {items.Count(i => !i.Done && i.DueAt <= state.Now)} due")), actions), () => OpenList(id)));
        }
        Add(TaskLists.UnsortedId,"Unsorted",null);
        var ordered = lists.OrderByDescending(l => l.Pinned);
        foreach (var l in state.Settings.TaskListSort == "AZ" ? ordered.ThenBy(l => l.Name,StringComparer.OrdinalIgnoreCase) : ordered.ThenByDescending(l => l.UpdatedAt)) Add(l.Id,l.Name,l);
        if (lists.Count == 0) body.Children.Add(Ui.EmptyState("Create your first task list", "Keep work, personal tasks and projects in separate lists. Existing tasks remain in Unsorted."));
        Children.Add(Ui.Scroll(body));
    }
}
public sealed class TaskListEditor : EditorWindow
{
    private readonly TaskList? _original;
    private readonly TextBox _name;
    private readonly CheckBox _pinned;
    public TaskListEditor(TaskList? list) : base(list == null ? "New task list" : "Edit task list", 480)
    {
        _original = list; _name = Ui.Input(list?.Name ?? "", "List name"); Field("Name",_name);
        _pinned = new CheckBox { Content = "Pin this list", IsChecked = list?.Pinned == true, Margin = new Thickness(0,12,0,12) }; Form.Children.Add(_pinned);
        Form.Children.Add(Ui.Sub("Tasks keep their dates and reminders when a list is renamed or deleted.")); AddButtons();
    }
    protected override bool Save()
    {
        var state = AppState.Current; var name = _name.Text.Trim();
        if (name.Length == 0) { ShowWarning("Enter a list name."); return false; }
        if (state.Settings.TaskLists.Any(l => l.DeletedAt == null && l.Id != _original?.Id && l.Name.Equals(name,StringComparison.OrdinalIgnoreCase))) { ShowWarning("A task list already uses this name."); return false; }
        var list = (_original ?? new TaskList { Id = Ids.Next(), CreatedAt = state.Now }) with { Name = name, Pinned = _pinned.IsChecked == true, UpdatedAt = state.Now };
        state.UpdateSettings(s => s with { TaskLists = s.TaskLists.Where(l => l.Id != list.Id).Append(list).ToList() });
        if (_original != null) state.UpsertMany(state.LiveItems(Tab.TASKS).Where(i => i.ListId == list.Id).Select(i => i with { Group = name }).ToList());
        return true;
    }
}
