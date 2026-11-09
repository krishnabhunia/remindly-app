using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Remindly.App.Services;
using Remindly.Core;

namespace Remindly.App.Views;

public sealed partial class ItemsView
{
    private readonly TaskNavigationState _navigation = new();
    private readonly StackPanel _breadcrumbs = Ui.Row();
    private readonly StackPanel _rootTabs = Ui.Row();
    private readonly StackPanel _statusTabs = Ui.Row();
    private readonly WorkspaceSearchBox _search = new();
    private readonly TextBox _quick = new();
    private readonly ComboBox _destination = new() { DisplayMemberPath = "Label", MinWidth = 150, MaxWidth = 220 };
    private readonly TextBlock _addTo = Ui.Sub("");
    private readonly ComboBox _sort = new() { Width = 110 };
    private readonly Border _composer = new();
    private readonly Dictionary<long, Button> _listButtons = new();
    private Button _primaryButton = null!, _editListButton = null!, _listMoreButton = null!, _addButton = null!;
    private RadioButton _listsTab = null!, _allTab = null!, _activeTab = null!, _doneTab = null!;
    private bool _ready, _restoring;
    private long _renderGeneration;
    private long? _focusListOnReturn;
    private TaskPageMemory? _restoreView;
    private string _destinationSignature = "";
    private sealed record DestinationChoice(long? Id, string Label);

    public TaskLocation CurrentLocation => _tab == Tab.TASKS ? _navigation.Current : TaskLocation.AllTasks;
    private bool HasCommandFilter => _tab == Tab.TASKS && CurrentLocation.QuickView != AllView;
    private long? TaskListForNew => _tab == Tab.TASKS && CurrentLocation.IsInsideList ? CurrentLocation.ListId : null;
    private TaskList? CurrentList => TaskLists.Live(AppState.Current.Settings.TaskLists)
        .FirstOrDefault(l => l.Id == CurrentLocation.ListId);

    private void BuildWorkspaceChrome()
    {
        _breadcrumbs.Margin = new Thickness(0, 0, 0, 12);
        SetDock(_breadcrumbs, Dock.Top);
        Children.Add(_breadcrumbs);
        _primaryButton = WorkspaceUi.Identify(Ui.Primary("+ New task", CreateInScope), "workspace-new", "New task");
        _editListButton = WorkspaceUi.Identify(Ui.Btn("Edit list…", () => { if (CurrentList is { } list) TaskListEditor.Edit(list); }), "edit-current-list", "Edit current list");
        // Inside a list: Edit list… plus a ⋯ menu with Edit, Pin / Unpin and Delete (approved design A6).
        _listMoreButton = WorkspaceUi.More("current-list-menu", "More options for this list",
            () => CurrentList is { } list ? ListMenu(list) : new ContextMenu());
        _listMoreButton.Margin = new Thickness(4, 0, 8, 0);
        var actions = Ui.Row(_editListButton, _listMoreButton, _primaryButton);
        SetDock(actions, Dock.Right);
        var heading = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 14) };
        heading.Children.Add(actions);
        _title.TextWrapping = TextWrapping.NoWrap;
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        heading.Children.Add(Ui.Stack(_title, _count));
        SetDock(heading, Dock.Top);
        Children.Add(heading);

        string group = Guid.NewGuid().ToString("N");
        _listsTab = WorkspaceUi.Segment("view-lists", "Lists", group + "scope", () => { if (!_restoring) ShowTaskLists(); });
        _allTab = WorkspaceUi.Segment("view-all-tasks", "All tasks", group + "scope", () => { if (!_restoring) ShowAllTasks(); });
        _rootTabs.Children.Add(_listsTab); _rootTabs.Children.Add(_allTab);
        _activeTab = WorkspaceUi.Segment("filter-active", "Active", group + "status", () => SetDoneView(false));
        _doneTab = WorkspaceUi.Segment("filter-done", "Done", group + "status", () => SetDoneView(true));
        _statusTabs.Children.Add(_activeTab); _statusTabs.Children.Add(_doneTab);
        WorkspaceUi.Identify(_search, "workspace-search", "Search lists");
        _search.TextChanged += (_, _) => { if (_ready && !_restoring) { _restoreView = ReadMemory() with { Vertical = 0, Horizontal = 0 }; Refresh(); } };
        var clear = WorkspaceUi.Action("clear-search", "Clear search", "Clear", () => { _search.Clear(); _search.Focus(); });
        _sort.Items.Add("A–Z"); _sort.Items.Add("Recent"); _sort.SelectedIndex = 0;
        WorkspaceUi.Identify(_sort, "list-sort", "Sort lists");
        _sort.SelectionChanged += (_, _) => { if (_ready) Refresh(); };
        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var child in new FrameworkElement[] { _rootTabs, _statusTabs, _search, clear, _sort })
        { child.Margin = new Thickness(0, 0, 10, 5); toolbar.Children.Add(child); }
        SetDock(toolbar, Dock.Top); Children.Add(toolbar);

        WorkspaceUi.Identify(_quick, "quick-task-title", "New task title");
        _quick.MinWidth = 80;
        _quick.TextChanged += (_, _) => ValidateQuickAdd();
        _quick.KeyDown += (_, e) => { if (e.Key == Key.Enter) { QuickAdd(); e.Handled = true; } };
        WorkspaceUi.Identify(_destination, "quick-task-list", "Choose destination list");
        _destination.SelectionChanged += (_, _) => ValidateQuickAdd();
        _addButton = WorkspaceUi.Identify(Ui.Btn("Add task", QuickAdd), "quick-task-add", "Add task");
        var destinationArea = Ui.Stack(_destination, _addTo);
        destinationArea.Margin = new Thickness(10, 0, 10, 0);
        _composer.Child = Ui.Columns("*,Auto,Auto", _quick, destinationArea, _addButton);
        _composer.Padding = new Thickness(10);
        _composer.BorderThickness = new Thickness(1);
        _composer.BorderBrush = Ui.Res("BorderBrush");
        _composer.Background = Ui.Res("CardBrush");
        _composer.CornerRadius = new CornerRadius(8);
        _composer.Margin = new Thickness(0, 0, 0, 12);
        SetDock(_composer, Dock.Top); Children.Add(_composer);
    }

    private void SetDoneView(bool done)
    {
        if (_restoring || !_ready || _doneView == done) return;
        _doneView = done;
        _restoreView = ReadMemory() with { Vertical = 0, Horizontal = 0 };
        Refresh();
    }

    private TaskPageMemory ReadMemory()
    {
        var scroll = FindScroll(_body.Content as DependencyObject);
        return new(_search.Text, _doneView, scroll?.VerticalOffset ?? 0, scroll?.HorizontalOffset ?? 0,
            _quick.Text, (_destination.SelectedItem as DestinationChoice)?.Id);
    }

    private void RestoreMemory(TaskPageMemory memory)
    {
        _restoring = true;
        try { _search.Text = memory.Search; _doneView = memory.Done; _quick.Text = memory.Draft; }
        finally { _restoring = false; }
        _restoreView = memory;
        _selected = null;
    }

    private void Navigate(TaskLocation target)
    {
        if (_tab != Tab.TASKS) return;
        RestoreMemory(_navigation.Go(target, ReadMemory()));
        CommandView = target.QuickView;
        Refresh();
    }

    public void ShowTaskLists() => Navigate(TaskLocation.Lists);
    public void ShowAllTasks() => Navigate(TaskLocation.AllTasks);
    public void OpenTaskList(long id) => Navigate(new TaskLocation(id));
    public void BackInTasks()
    {
        if (_tab != Tab.TASKS) return;
        var from = CurrentLocation;
        RestoreMemory(_navigation.Back(ReadMemory()));
        CommandView = CurrentLocation.QuickView;
        if (CurrentLocation.IsOverview && from.IsInsideList) _focusListOnReturn = from.ListId;
        Refresh();
    }

    // Called before an ordinary refresh, including when Command Dark changes its sidebar filter.
    private void ReconcileNavigation()
    {
        if (_tab != Tab.TASKS) return;
        if (_design == Designs.Command && CurrentLocation.QuickView != CommandView)
            RestoreMemory(_navigation.Go(CommandView == AllView ? TaskLocation.Lists : new TaskLocation(-1, CommandView), ReadMemory()));
        if (CurrentLocation.IsInsideList && CurrentLocation.ListId is > 0 && CurrentList == null)
        {
            RestoreMemory(_navigation.Go(TaskLocation.Lists, ReadMemory()));
            CommandView = AllView;
            App.Current.Main.Snack("This list is no longer available. Your tasks have not been deleted.");
        }
    }

    private void UpdateWorkspaceChrome(List<Item> scoped)
    {
        var location = CurrentLocation;
        bool tasks = _tab == Tab.TASKS;
        _restoring = true;
        try
        {
            _rootTabs.Visibility = tasks && location.ShowRootTabs ? Visibility.Visible : Visibility.Collapsed;
            _listsTab.IsChecked = location.IsOverview;
            _allTab.IsChecked = location.IsAllTasks;
            _statusTabs.Visibility = location.ShowTaskControls ? Visibility.Visible : Visibility.Collapsed;
            _activeTab.Content = $"Active  {scoped.Count(i => !i.Done)}";
            _doneTab.Content = $"Done  {scoped.Count(i => i.Done)}";
            _activeTab.IsChecked = !_doneView; _doneTab.IsChecked = _doneView;
            _primaryButton.Content = tasks && location.IsOverview ? "+ New list" : _tab == Tab.LEARN ? "+ New learning item" : "+ New task";
            System.Windows.Automation.AutomationProperties.SetName(_primaryButton, _primaryButton.Content.ToString());
            _editListButton.Visibility = tasks && location.IsInsideList && CurrentList != null ? Visibility.Visible : Visibility.Collapsed;
            _listMoreButton.Visibility = _editListButton.Visibility;
            _sort.Visibility = tasks && location.IsOverview ? Visibility.Visible : Visibility.Collapsed;
            _composer.Visibility = location.ShowTaskControls ? Visibility.Visible : Visibility.Collapsed;
            _search.Hint = tasks ? TaskWorkspaceRules.SearchHint(location, TaskScopeTitle("Tasks")) : "Search learning items";
            _quick.ToolTip = tasks && location.IsInsideList ? "Add a task to " + TaskScopeTitle("this list") : "Add a task — choose its destination";
            _destination.Visibility = tasks && !location.IsInsideList && !location.IsOverview ? Visibility.Visible : Visibility.Collapsed;
            _addTo.Visibility = _destination.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            _addTo.Text = tasks ? "Adds to " + TaskScopeTitle("Unsorted") : "Adds to Learn";
            PopulateDestinations();
            if (_restoreView is { } restore)
                _destination.SelectedItem = _destination.Items.Cast<DestinationChoice>().FirstOrDefault(c => c.Id == restore.Destination) ?? _destination.Items[0];
        }
        finally { _restoring = false; }
        _breadcrumbs.Children.Clear();
        _breadcrumbs.Visibility = tasks ? Visibility.Visible : Visibility.Collapsed;
        if (tasks)
        {
            if (location.IsInsideList || HasCommandFilter)
                _breadcrumbs.Children.Add(WorkspaceUi.Action("task-back", "Back to previous view (Alt+Left)", "← Back", BackInTasks));
            _breadcrumbs.Children.Add(WorkspaceUi.Link("crumb-tasks", "Tasks home", "Tasks", ShowTaskLists, 12));
            if (location.IsInsideList)
            {
                _breadcrumbs.Children.Add(Ui.Sub("  ›  "));
                _breadcrumbs.Children.Add(WorkspaceUi.Link("crumb-lists", "Back to Lists", "Lists", ShowTaskLists, 12));
            }
            if (!location.IsOverview)
            {
                _breadcrumbs.Children.Add(Ui.Sub("  ›  "));
                var current = Ui.Sub(TaskScopeTitle(HasCommandFilter ? QuickViews.FirstOrDefault(v => v.Code == location.QuickView)?.Label ?? "Tasks" : "All tasks"));
                current.MaxWidth = 380; current.TextWrapping = TextWrapping.NoWrap; current.TextTrimming = TextTrimming.CharacterEllipsis;
                current.ToolTip = current.Text;
                _breadcrumbs.Children.Add(current);
            }
        }
        ValidateQuickAdd();
    }

    private void PopulateDestinations()
    {
        var lists = TaskLists.Live(AppState.Current.Settings.TaskLists).OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var signature = System.Text.Json.JsonSerializer.Serialize(lists.Select(l => new { l.Id, l.Name }));
        if (_destinationSignature == signature && _destination.Items.Count > 0) return;
        var selected = (_destination.SelectedItem as DestinationChoice)?.Id;
        _destinationSignature = signature;
        _destination.Items.Clear();
        _destination.Items.Add(new DestinationChoice(null, "Choose list…"));
        _destination.Items.Add(new DestinationChoice(0, "Unsorted"));
        foreach (var list in lists) _destination.Items.Add(new DestinationChoice(list.Id, list.Name));
        _destination.SelectedItem = _destination.Items.Cast<DestinationChoice>().FirstOrDefault(c => c.Id == selected) ?? _destination.Items[0];
    }

    private void ValidateQuickAdd()
    {
        if (!_ready || _addButton == null) return;
        _addButton.IsEnabled = _tab == Tab.TASKS
            ? TaskWorkspaceRules.CanQuickAdd(_quick.Text, CurrentLocation, (_destination.SelectedItem as DestinationChoice)?.Id,
                TaskLists.Live(AppState.Current.Settings.TaskLists).Select(l => l.Id))
            : !string.IsNullOrWhiteSpace(_quick.Text);
    }

    private void CreateInScope()
    {
        if (_tab == Tab.TASKS && CurrentLocation.IsOverview) TaskListEditor.Create();
        else CreateTask();
    }

    private void CreateTask(long? dueAt = null) => ItemEditor.New(_tab, listId: TaskListForNew, dueAt: dueAt);

    public bool HandleWorkspaceShortcut(Key key, ModifierKeys modifiers, IInputElement? focused = null)
    {
        focused ??= Keyboard.FocusedElement;
        if (modifiers == ModifierKeys.Control && key == Key.F) { _search.Focus(); _search.SelectAll(); return true; }
        if (_tab != Tab.TASKS) return false;
        if (modifiers == ModifierKeys.Control && key == Key.N) { CreateInScope(); return true; }
        if (modifiers == ModifierKeys.Alt && key == Key.Left && !CurrentLocation.IsOverview) { BackInTasks(); return true; }
        // Esc also goes back, except while typing or choosing in a box (those keep their own Esc).
        if (modifiers == ModifierKeys.None && key == Key.Escape && !CurrentLocation.IsOverview &&
            focused is not (TextBoxBase or ComboBox or ComboBoxItem or PasswordBox))
        { BackInTasks(); return true; }
        return false;
    }

    private IEnumerable<Item> ScopeToTaskList(IEnumerable<Item> items) => _tab == Tab.TASKS && CurrentLocation.IsInsideList
        ? TaskLists.Members(items, CurrentLocation.ListId!.Value, AppState.Current.Settings.TaskLists) : items;

    private string TaskScopeTitle(string fallback)
    {
        if (_tab != Tab.TASKS || HasCommandFilter || CurrentLocation.IsOverview) return fallback;
        return CurrentLocation.IsAllTasks ? "All tasks" : CurrentLocation.ListId == 0 ? "Unsorted" : CurrentList?.Name ?? fallback;
    }

    private void ReplaceBody(UIElement content)
    {
        var memory = _restoreView ?? ReadMemory();
        _restoreView = null;
        var focus = _focusListOnReturn; _focusListOnReturn = null;
        long generation = ++_renderGeneration;
        _body.Content = content;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (generation != _renderGeneration) return;
            if (focus is long id && _listButtons.TryGetValue(id, out var button) && CurrentLocation.IsOverview) button.Focus();
            if (FindScroll(_body.Content as DependencyObject) is ScrollViewer scroll)
            { scroll.ScrollToVerticalOffset(memory.Vertical); scroll.ScrollToHorizontalOffset(memory.Horizontal); }
        });
    }

    private bool TryShowTaskLists(long now, string query)
    {
        if (_tab != Tab.TASKS || !CurrentLocation.IsOverview) return false;
        var state = AppState.Current;
        var lists = TaskLists.Live(state.Settings.TaskLists);
        _title.Text = "Tasks";
        _count.Text = $"{lists.Count} lists · {state.LiveItems(Tab.TASKS).Count(i => !i.Done)} active tasks";
        var cards = new StackPanel(); _listButtons.Clear();
        void AddCard(long id, string name, string? icon, TaskList? record)
        {
            if (query.Length > 0 && !name.Contains(query, StringComparison.OrdinalIgnoreCase)) return;
            var members = TaskLists.Members(state.Data.Items, id, state.Settings.TaskLists);
            int active = members.Count(i => !i.Done), done = members.Count(i => i.Done);
            var nameRow = new WrapPanel();
            nameRow.Children.Add(Ui.Text(TaskListIcons.Selected(icon) + "  " + name, 16, FontWeights.SemiBold));
            if (record?.Pinned == true)
            {
                var pin = Ui.Glyph(Glyphs.Pin, 13, Ui.Res("AccentBrush"));
                pin.Margin = new Thickness(8, 0, 0, 0);
                pin.ToolTip = "Pinned";
                WorkspaceUi.Identify(pin, $"task-list-pin-{id}", "Pinned");
                nameRow.Children.Add(pin);
            }
            var summary = Ui.Stack(nameRow, Ui.Sub(members.Count == 0 ? "No tasks yet" : $"{active} active · {done} done"));
            var progress = Ui.Stack(Ui.Sub(members.Count == 0 ? "Empty list" : $"{done} of {members.Count} done"));
            progress.Width = 150;
            if (members.Count > 0) progress.Children.Add(new ProgressBar
            {
                Minimum = 0, Maximum = members.Count, Value = done, Height = 4,
                Margin = new Thickness(0, 6, 0, 0), Foreground = Ui.Res("AccentBrush"), Background = Ui.Res("NavSelectedBrush"),
            });
            progress.Margin = new Thickness(12, 0, 16, 0);
            var open = WorkspaceUi.Action($"open-task-list-{id}", "Open list " + name,
                Ui.Columns("*,Auto,Auto", summary, progress, Ui.Sub($"{active} active   ›")), () => OpenTaskList(id));
            open.Padding = new Thickness(12, 9, 12, 9);
            open.MinHeight = 64;
            _listButtons[id] = open;
            var more = record == null ? (UIElement)new Border { Width = 34 } : WorkspaceUi.More($"task-list-menu-{id}", "More options for " + name, () => ListMenu(record));
            var card = Ui.Card(Ui.Columns("*,Auto", open, more));
            card.Padding = new Thickness(0); card.Margin = new Thickness(0, 0, 0, 7);
            card.SizeChanged += (_, _) => progress.Visibility = card.ActualWidth < 620 ? Visibility.Collapsed : Visibility.Visible;
            cards.Children.Add(card);
        }
        IEnumerable<TaskList> sorted = _sort.SelectedIndex == 1
            ? lists.OrderByDescending(l => l.Pinned).ThenByDescending(l => Math.Max(l.UpdatedAt,
                TaskLists.Members(state.Data.Items, l.Id, lists).Select(i => Math.Max(i.UpdatedAt, i.CreatedAt)).DefaultIfEmpty().Max()))
            : lists.OrderByDescending(l => l.Pinned).ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase);
        // Pinned lists get their own heading above "All lists" (approved F7); both sorts keep pinned first.
        bool Shown(string name) => query.Length == 0 || name.Contains(query, StringComparison.OrdinalIgnoreCase);
        var visible = sorted.Where(l => Shown(l.Name)).ToList();
        bool unsorted = TaskLists.Members(state.Data.Items, 0, lists).Count > 0 && Shown("Unsorted");
        bool sections = visible.Any(l => l.Pinned), othersHeading = false;
        void Heading(string id, string text, bool pinned)
        {
            var label = Ui.Header(text);
            var row = pinned ? Ui.Row(Ui.Glyph(Glyphs.Pin, 12, Ui.Res("InkSubtleBrush")), new Border { Width = 6 }, label) : Ui.Row(label);
            row.Margin = new Thickness(2, cards.Children.Count == 0 ? 0 : 10, 0, 6);
            cards.Children.Add(WorkspaceUi.Identify(row, id, text));
        }
        if (sections) Heading("lists-pinned-heading", "Pinned", true);
        foreach (var list in visible)
        {
            if (sections && !list.Pinned && !othersHeading) { Heading("lists-all-heading", "All lists", false); othersHeading = true; }
            AddCard(list.Id, list.Name, list.Icon, list);
        }
        if (unsorted)
        {
            if (sections && !othersHeading) Heading("lists-all-heading", "All lists", false);
            AddCard(0, "Unsorted", null, null);
        }
        if (cards.Children.Count == 0)
            cards.Children.Add(Ui.EmptyState(query.Length > 0 ? "No matching lists" : "No task lists yet",
                query.Length > 0 ? "Clear the search to see all lists." : "Choose + New list to get started."));
        ReplaceBody(Ui.Scroll(cards));
        return true;
    }

    private static ContextMenu ListMenu(TaskList record)
    {
        var menu = new ContextMenu();
        void Add(string title, Action action) { var item = new MenuItem { Header = title }; item.Click += (_, e) => { e.Handled = true; action(); }; menu.Items.Add(item); }
        Add("Edit list…", () => TaskListEditor.Edit(record));
        Add(record.Pinned ? "Unpin list" : "Pin list", () => TaskListEditor.TogglePin(record));
        menu.Items.Add(new Separator());
        Add("Delete list…", () => TaskListEditor.Remove(record));
        return menu;
    }

    private Button? TaskBadge(Item item)
    {
        if (item.Tab != Tab.TASKS || CurrentLocation.IsInsideList) return null;
        long id = TaskLists.ListIdOf(item, AppState.Current.Settings.TaskLists) ?? 0;
        var name = TaskLists.Live(AppState.Current.Settings.TaskLists).FirstOrDefault(l => l.Id == id)?.Name ?? "Unsorted";
        var button = WorkspaceUi.Link($"task-list-badge-{item.Id}", "Open list " + name, "📋 " + name, () => OpenTaskList(id), 11);
        button.Foreground = Ui.Res("AccentBrush");
        return button;
    }
}

public sealed class TaskListEditor : EditorWindow
{
    private readonly TaskList? _original;
    private readonly TextBox _name = new();
    private readonly WrapPanel _icons = new();
    private string _icon;
    private readonly CheckBox _pinned = new() { Content = "Pinned" };

    private TaskListEditor(TaskList? list) : base(list == null ? "New task list" : "Edit task list", 460)
    {
        _original = list;
        _name.Text = list?.Name ?? ""; _pinned.IsChecked = list?.Pinned == true;
        WorkspaceUi.Identify(_name, "task-list-name", "List name");
        // An icon picker replaces the free-text box (approved F14). Same emoji text as the phone stores.
        _icon = TaskListIcons.Selected(list?.Icon);
        string group = "taskListIcon" + Guid.NewGuid().ToString("N");
        int n = 0;
        foreach (var choice in TaskListIcons.Choices(list?.Icon))
        {
            var chip = WorkspaceUi.Identify(Ui.Chip(choice, group, choice == _icon, () => _icon = choice), $"task-list-icon-{n++}", "Icon " + choice);
            chip.FontSize = 18; chip.MinWidth = 44; chip.Margin = new Thickness(0, 0, 6, 6);
            _icons.Children.Add(chip);
        }
        WorkspaceUi.Identify(_icons, "task-list-icons", "List icon");
        Field("List name", _name); Field("Icon", _icons); Form.Children.Add(_pinned);
        EnableUnsavedGuard();
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
        if (_original != null && current!.UpdatedAt != _original.UpdatedAt)
        { ShowWarning("This list changed while you were editing. Reopen it before saving."); return false; }
        var list = (current ?? new TaskList { Id = Ids.Next(), CreatedAt = state.Now }) with
        {
            Name = name, Icon = TaskListIcons.ToStored(_icon, current?.Icon),
            Pinned = _pinned.IsChecked == true, UpdatedAt = NextStamp(current?.UpdatedAt ?? 0, state.Now),
        };
        if (current != null && current.Name != name)
            state.UpsertMany(TaskLists.Members(state.Data.Items, current.Id, state.Settings.TaskLists)
                .Select(i => i with { ListId = list.Id, Group = name }).ToList());
        state.UpdateSettings(s => TaskLists.Mirror(s with { TaskLists = TaskLists.Merge(s.TaskLists, new[] { list }) }));
        return true;
    }

    public static void TogglePin(TaskList list)
    {
        var state = AppState.Current;
        var current = TaskLists.Live(state.Settings.TaskLists).FirstOrDefault(l => l.Id == list.Id);
        if (current == null) return;
        var changed = current with { Pinned = !current.Pinned, UpdatedAt = NextStamp(current.UpdatedAt, state.Now) };
        state.UpdateSettings(s => s with { TaskLists = TaskLists.Merge(s.TaskLists, new[] { changed }) });
    }

    public static void Remove(TaskList list)
    {
        var state = AppState.Current;
        var current = TaskLists.Live(state.Settings.TaskLists).FirstOrDefault(l => l.Id == list.Id);
        if (current == null || !Ui.Confirm($"Delete list \"{current.Name}\"? Its tasks will be kept in Unsorted. This also syncs to Android.")) return;
        var tasks = TaskLists.Members(state.Data.Items, current.Id, state.Settings.TaskLists);
        state.UpsertMany(tasks.Select(i => i with { ListId = null, Group = null }).ToList());
        var moved = state.Data.Items.Where(i => tasks.Any(t => t.Id == i.Id)).ToDictionary(i => i.Id, i => i.UpdatedAt);
        long stamp = NextStamp(current.UpdatedAt, state.Now);
        var deleted = current with { DeletedAt = stamp, UpdatedAt = stamp };
        state.UpdateSettings(s => TaskLists.Mirror(s with { TaskLists = TaskLists.Merge(s.TaskLists, new[] { deleted }) }));
        App.Current.Main.Snack("List removed; tasks kept in Unsorted", () =>
        {
            var latest = state.Settings.TaskLists.FirstOrDefault(l => l.Id == current.Id);
            if (latest?.DeletedAt == null || latest.UpdatedAt != stamp)
            { App.Current.Main.Snack("The list changed again; Undo was not applied."); return; }
            var restored = current with { UpdatedAt = NextStamp(latest.UpdatedAt, state.Now), DeletedAt = null };
            state.UpdateSettings(s => TaskLists.Mirror(s with { TaskLists = TaskLists.Merge(s.TaskLists, new[] { restored }) }));
            state.UpsertMany(state.Data.Items.Where(i => moved.TryGetValue(i.Id, out var editedAt) && i.UpdatedAt == editedAt &&
                    i.ListId == null && i.Group == null && i.DeletedAt == null)
                .Select(i => i with { ListId = current.Id, Group = current.Name }).ToList());
        });
    }
}
