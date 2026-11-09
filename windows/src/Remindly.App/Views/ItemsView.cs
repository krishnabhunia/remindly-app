using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Remindly.App.Services;
using Remindly.Core;

namespace Remindly.App.Views;

/// <summary>Tasks and Learn; navigation, membership and the desktop design are independent.</summary>
public sealed partial class ItemsView : DockPanel, IPage
{
    public const string AllView = "ALL";
    public sealed record QuickView(string Code, string Label, string Dot);
    public static readonly QuickView[] QuickViews =
    {
        new("TODAY", "Today", "#9B8CFF"), new("OVERDUE", "Overdue", "#F87171"),
        new("UPCOMING", "Next 7 days", "#60A5FA"), new("REPEATING", "Repeating", "#34D399"),
        new("PERSONAL", "Personal", "#A0A6B3"),
    };
    public static string CommandView { get; set; } = AllView;
    public static bool Matches(string view, Item i, long now)
    {
        if (view == AllView) return true;
        if (i.Done) return false;
        long today = Clock.StartOfDay(now), tomorrow = Clock.StartOfNextDay(now);
        return view switch
        {
            "TODAY" => i.DueAt is long d && d < tomorrow,
            "OVERDUE" => i.DueAt is long d && d < today,
            "UPCOMING" => i.DueAt is long d && d >= tomorrow && d < Clock.FromLocal(Clock.LocalDate(now).AddDays(8)),
            "REPEATING" => i.RepeatMode != "OFF", "PERSONAL" => i.Personal, _ => true,
        };
    }

    private readonly Tab _tab;
    private readonly string _design = Theme.Design;
    private readonly TextBlock _title;
    private readonly TextBlock _count = Ui.Sub("");
    private readonly ContentControl _body = new() { Focusable = false };
    private readonly Border _detail = new();
    private bool _doneView;
    private long? _selected;

    public ItemsView(Tab tab)
    {
        _tab = tab;
        LastChildFill = true;
        _title = Ui.H1(TabNames.Title(tab));
        if (_design == Designs.Hub) _title.FontSize = 28;
        BuildWorkspaceChrome();
        Children.Add(_body);
        _ready = true;
    }

    private void QuickAdd()
    {
        ValidateQuickAdd();
        if (!_addButton.IsEnabled) return;
        var destination = TaskListForNew ?? (_destination.SelectedItem as DestinationChoice)?.Id;
        if (AddQuick(_tab, _quick.Text, destination)) _quick.Clear();
    }

    /// <summary>Command-bar callers use the same current-list destination rules as the page.</summary>
    public static bool AddQuick(Tab tab, string text, long? taskListId = null)
    {
        text = text.Trim();
        if (text.Length == 0) return false;
        var state = AppState.Current;
        if (tab == Tab.TASKS && taskListId == null)
        {
            if (App.Current.Main.Page.Content is ItemsView view && view._tab == Tab.TASKS && view.CurrentLocation.IsInsideList)
                taskListId = view.TaskListForNew;
            else
            {
                // No invisible fallback to Unsorted. The editor requires a deliberate list choice.
                ItemEditor.New(tab, title: text);
                return false; // retain the command text if the editor is cancelled
            }
        }
        if (ItemRules.DupActiveMatch(state.Data.Items, tab, text) && !Ui.Confirm($"\"{text}\" is already on your {TabNames.Title(tab)} list. Add it again?")) return false;
        var item = state.NewItem(tab, text);
        if (tab == Tab.TASKS && taskListId is > 0)
        {
            var list = TaskLists.Live(state.Settings.TaskLists).FirstOrDefault(l => l.Id == taskListId);
            if (list == null) { App.Current.Main.Snack("The selected task list is no longer available."); return false; }
            item = item with { ListId = list.Id, Group = list.Name };
        }
        state.Upsert(item);
        App.Current.Main.Snack(tab == Tab.TASKS ? $"Added to {item.Group ?? "Unsorted"}" : $"Added to {TabNames.Title(tab)}");
        return true;
    }

    public void Refresh()
    {
        if (!_ready || _restoring) return;
        ReconcileNavigation();
        var state = AppState.Current;
        long now = state.Now;
        var query = _search.Text.Trim();
        var live = ScopeToTaskList(state.LiveItems(_tab)).ToList();
        var view = HasCommandFilter ? CurrentLocation.QuickView : AllView;
        var scoped = live.Where(i => i.Done || Matches(view, i, now)).ToList();
        UpdateWorkspaceChrome(scoped);
        if (TryShowTaskLists(now, query)) return;
        var shown = scoped.Where(i => i.Done == _doneView)
            .Where(i => query.Length == 0 || i.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                i.Notes.Contains(query, StringComparison.OrdinalIgnoreCase) || (i.Topic ?? "").Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (_tab == Tab.TASKS && !CurrentLocation.IsInsideList && (i.Group ?? "").Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
        _title.Text = TaskScopeTitle(view == AllView ? TabNames.Title(_tab) : QuickViews.First(v => v.Code == view).Label);
        _title.ToolTip = _title.Text;
        _count.Text = $"{scoped.Count(i => !i.Done)} active · {ItemRules.PendingTodayCount(scoped, _tab, now)} due today or overdue · {scoped.Count(i => i.Done)} done" +
            (query.Length > 0 ? $" · {shown.Count} matches" : "");
        if (shown.Count == 0)
        {
            var empty = Ui.Stack(Ui.EmptyState(TaskWorkspaceRules.EmptyTitle(query.Length > 0, _doneView), query.Length > 0
                ? "Try another search or clear it. Active and Done are searched separately."
                : _doneView ? "Completed tasks will appear here." : "Add a task to this view, or check Done."));
            if (query.Length > 0) empty.Children.Add(WorkspaceUi.Action("empty-clear-search", "Clear search", "Clear search", () => _search.Clear()));
            else if (!_doneView && scoped.Any(i => i.Done))
                empty.Children.Add(WorkspaceUi.Action("empty-show-done", "Show completed tasks", "Show Done", () => SetDoneView(true)));
            ReplaceBody(Ui.Scroll(empty));
            return;
        }
        ReplaceBody(_design switch
        {
            Designs.Board when !_doneView => BoardBody(shown, now),
            Designs.Command => TableBody(shown, now),
            Designs.Hub => TileBody(shown, now),
            Designs.Fluent when _tab != Tab.TASKS => FluentBody(shown, now),
            _ => ListBody(shown, now),
        });
    }

    private static ScrollViewer? FindScroll(DependencyObject? d)
    {
        if (d == null) return null;
        if (d is ScrollViewer scroll) return scroll;
        foreach (var child in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>())
            if (FindScroll(child) is { } found) return found;
        return null;
    }

    private ScrollViewer ListBody(List<Item> shown, long now)
    {
        var list = new StackPanel();
        foreach (var group in ItemRules.GroupByDay(shown, _doneView, now))
        {
            var header = Ui.Header($"{group.Label} · {group.Items.Count}");
            if (group.Key == "overdue") header.Foreground = Ui.Res("DangerBrush");
            list.Children.Add(header);
            foreach (var item in group.Items) list.Children.Add(ItemCard(item, now, taskBadge: TaskBadge(item)));
        }
        return Ui.Scroll(list);
    }

    // Learn retains its existing Fluent details pane. Tasks uses the approved full-width rows.
    private UIElement FluentBody(List<Item> shown, long now)
    {
        if (_selected is long sid && !shown.Any(i => i.Id == sid)) _selected = null;
        _selected ??= shown.FirstOrDefault(i => !i.Done)?.Id ?? shown.FirstOrDefault()?.Id;
        var list = new StackPanel();
        foreach (var group in ItemRules.GroupByDay(shown, _doneView, now))
        {
            var header = Ui.Header($"{group.Label} · {group.Items.Count}");
            header.Foreground = group.Key == "overdue" ? Ui.Res("DangerBrush") : Ui.Res("InkBrush");
            list.Children.Add(header);
            foreach (var item in group.Items)
            {
                var card = ItemCard(item, now);
                if (item.Id == _selected) { card.Background = Ui.Res("AccentSoftBrush"); card.BorderBrush = Ui.Res("AccentBrush"); }
                card.MouseLeftButtonUp += (_, e) =>
                {
                    if (e.OriginalSource is DependencyObject d && IsInside<ButtonBase>(d)) return;
                    _selected = item.Id; Refresh();
                };
                list.Children.Add(card);
            }
        }
        _detail.Child = DetailPane(_selected is long id ? shown.FirstOrDefault(i => i.Id == id) : null, now);
        _detail.Width = 330; _detail.Margin = new Thickness(16, 0, 0, 0);
        (_detail.Parent as Panel)?.Children.Remove(_detail);
        var grid = Ui.Columns("*,Auto", Ui.Scroll(list), _detail);
        grid.SizeChanged += (_, _) => _detail.Visibility = grid.ActualWidth > 0 && grid.ActualWidth < 760 ? Visibility.Collapsed : Visibility.Visible;
        return grid;
    }

    private static bool IsInside<T>(DependencyObject d) where T : DependencyObject
    {
        for (var x = d; x != null; x = x is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(x) ?? LogicalTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (x is T || x is CheckBox) return true;
        return false;
    }

    private UIElement DetailPane(Item? item, long now)
    {
        var body = new StackPanel();
        var box = new Border
        {
            Background = Ui.Res("CardBrush"), BorderBrush = Ui.Res("BorderBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(18, 16, 18, 16), VerticalAlignment = VerticalAlignment.Top, Child = body,
        };
        if (item == null) { body.Children.Add(Ui.Sub("Select an item to see its details.")); return box; }
        var state = AppState.Current;
        body.Children.Add(Ui.Columns("*,Auto", Ui.Sub("Details"), Ui.Sub("Edited " + Clock.FormatDayTime(Math.Max(item.UpdatedAt, item.CreatedAt)))));
        body.Children.Add(TitleButton(item, 18));
        body.Children.Add(Tags(item, now));
        void Field(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var caption = Ui.Text(label, 12, FontWeights.SemiBold, Ui.Res("InkSubtleBrush"));
            caption.Margin = new Thickness(0, 8, 0, 2); body.Children.Add(caption); body.Children.Add(Ui.Text(value!, 14));
        }
        var due = Ui.DueText(item.DueAt, item.DueHasTime, now);
        Field(item.Done ? (item.RepeatMode != "OFF" ? "Returns" : "Was due") : "Due", due.Length > 0 ? due : "No date");
        Field("Repeat", Recurrence.Label(item));
        Field("Priority", item.Priority is Priority priority ? PriorityNames.Label(priority) : null);
        Field("Alert", item.DueAt == null ? null : item.AlertType == "OFF" ? "Muted" : item.AlertType.Contains('R') || item.AlertType.Contains('A') ? "Ring" : "Notify");
        if (item.Tab == Tab.LEARN)
        {
            Field("Platform", item.Platform); Field("Topic", item.Topic);
            if (item.Progress > 0) Field("Progress", $"{item.Progress}% · {item.HoursSpent:0.#} h spent");
        }
        Field("Notes", item.Notes);
        if (item.Done && item.DoneAt is long doneAt) Field("Done", Clock.FormatDayTime(doneAt));
        var actions = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        actions.Children.Add(Ui.Primary("Edit…", () => EditLatest(item)));
        actions.Children.Add(Ui.Btn(item.Done ? "Back to Active" : "Done", () => Toggle(item, !item.Done)));
        if (!item.Done && item.DueAt != null) actions.Children.Add(Ui.Btn($"Snooze {state.Settings.SnoozeMinutes} min", () => Snooze(item)));
        if (!string.IsNullOrWhiteSpace(item.Url)) actions.Children.Add(Ui.Btn("Open link", () => InstallInfo.OpenUrl(item.Url!)));
        foreach (var button in actions.Children.OfType<Button>()) button.Margin = new Thickness(0, 0, 6, 6);
        body.Children.Add(actions);
        body.Children.Add(Ui.Btn("Delete", () => Delete(item), "Moves it to the Bin for 30 days", "Danger"));
        return box;
    }

    private UIElement BoardBody(List<Item> shown, long now)
    {
        var state = AppState.Current;
        var grid = new UniformGrid4();
        foreach (var column in DesktopViews.BoardColumns(shown, now))
        {
            bool overdue = column.Key == DesktopViews.Overdue;
            var stack = new StackPanel();
            var day = column.Key switch
            {
                DesktopViews.Today => Clock.ToLocal(now).ToString("ddd d", CultureInfo.InvariantCulture) + " · ",
                DesktopViews.Tomorrow => Clock.ToLocal(now).AddDays(1).ToString("ddd d", CultureInfo.InvariantCulture) + " · ", _ => "",
            };
            stack.Children.Add(Ui.Columns("*,Auto", Ui.Text(column.Label, 15, FontWeights.ExtraBold,
                overdue ? Ui.Res("DangerBrush") : Ui.Res("InkBrush"), wrap: false), Ui.Sub(day + column.Items.Count)));
            ((Grid)stack.Children[0]).Margin = new Thickness(4, 2, 4, 10);
            foreach (var item in column.Items) stack.Children.Add(BoardCard(item, now));
            if (column.Items.Count == 0) stack.Children.Add(Ui.Sub("Nothing here"));
            Button footer;
            if (overdue)
            {
                footer = Ui.Btn("Move all to today", () =>
                {
                    var snapshot = state.TakeSnapshot();
                    state.UpsertMany(column.Items.Select(i => DesktopViews.MoveToToday(i, state.Now, state.Settings.DefaultDueHour)));
                    App.Current.Main.Snack($"{column.Items.Count} moved to today", () => state.Restore(snapshot));
                });
                footer.IsEnabled = column.Items.Count > 0;
            }
            else
            {
                long? due = column.Key switch
                {
                    DesktopViews.Today => NextLocalHour(now),
                    DesktopViews.Tomorrow => Clock.FromLocal(Clock.LocalDate(now).AddDays(1).AddHours(state.Settings.DefaultDueHour)), _ => null,
                };
                footer = Ui.Btn("+ Add " + (column.Key == DesktopViews.Later ? "for later" : "to " + column.Label.ToLowerInvariant()), () => CreateTask(due));
            }
            footer.HorizontalAlignment = HorizontalAlignment.Stretch; footer.Background = Brushes.Transparent; footer.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(footer);
            grid.Children.Add(new Border
            {
                Background = overdue ? Ui.Res("DangerSoftBrush") : column.Key == DesktopViews.Today ? Ui.Res("AccentSoftBrush") : Ui.Res("NavSelectedBrush"),
                CornerRadius = new CornerRadius(14), Padding = new Thickness(10), Margin = new Thickness(0, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Top, Child = stack,
            });
        }
        var scroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = grid };
        scroll.SizeChanged += (_, _) => grid.Width = Math.Max(4 * 250, scroll.ViewportWidth > 0 ? scroll.ViewportWidth : scroll.ActualWidth);
        return scroll;
    }

    private static long NextLocalHour(long now)
    {
        var local = Clock.ToLocal(now);
        var next = local.Date.AddHours(local.Hour + 1);
        return Clock.FromLocal(next.Date == local.Date ? next : local.Date.AddHours(23).AddMinutes(59));
    }
    private sealed class UniformGrid4 : UniformGrid
    {
        public UniformGrid4() { Columns = 4; Rows = 1; VerticalAlignment = VerticalAlignment.Top; }
    }

    private Border BoardCard(Item item, long now)
    {
        var body = Ui.Stack(Ui.Columns("Auto,*", CompletionCheck(item), TitleButton(item, 14.5)));
        if (TaskBadge(item) is { } badge) body.Children.Add(badge);
        var chips = new WrapPanel { Margin = new Thickness(25, 6, 0, 0) };
        bool late = !item.Done && item.DueAt is long d && d < now;
        if (item.DueAt is long due) chips.Children.Add(Chip(item.DueHasTime ? Clock.FormatTime(due) : Clock.FormatDay(due),
            late ? Ui.Res("DangerBrush") : Ui.Res("InkSubtleBrush"), late ? Ui.Res("DangerSoftBrush") : Ui.Res("NavSelectedBrush")));
        foreach (var tag in TagList(item, now)) chips.Children.Add(tag);
        if (item.Tab == Tab.LEARN && item.Progress > 0) chips.Children.Add(Chip($"{item.Progress}%", Ui.Res("AccentInkBrush"), Ui.Res("AccentSoftBrush")));
        if (chips.Children.Count > 0) body.Children.Add(chips);
        if (!string.IsNullOrWhiteSpace(item.Notes))
        {
            var note = Ui.Sub(item.Notes.Trim().Replace('\n', ' ')); note.Margin = new Thickness(25, 6, 0, 0); note.MaxHeight = 36; body.Children.Add(note);
        }
        body.Children.Add(ItemActions(item));
        var card = Ui.Card(body);
        card.CornerRadius = new CornerRadius(12); card.Padding = new Thickness(12, 10, 12, 10); card.Margin = new Thickness(0, 0, 0, 8);
        card.ContextMenu = ItemMenu(item); card.ToolTip = "Click the name to edit · checkbox changes completion";
        return card;
    }

    private static Border Chip(string text, Brush foreground, Brush background)
    {
        var tag = Ui.Tag(text, foreground, background); tag.CornerRadius = new CornerRadius(8);
        tag.Padding = new Thickness(7, 2, 7, 2); tag.Margin = new Thickness(0, 0, 6, 4); return tag;
    }
    private static Brush PriorityInk(Priority priority) => priority switch
    {
        Priority.URGENT => Ui.Res("DangerBrush"), Priority.HIGH => Ui.Res("AmberBrush"), _ => Ui.Res("InkSubtleBrush"),
    };

    private const string TableCols = "36,*,160,130,100,Auto";
    private UIElement TableBody(List<Item> shown, long now)
    {
        var rows = new StackPanel();
        var head = Ui.Columns(TableCols, new Border(), Caption("Task"), Caption("Due"), Caption("Repeat"), Caption("Priority"), new Border { Width = 72 });
        head.Margin = new Thickness(12, 0, 12, 0); head.Height = 34;
        rows.Children.Add(new Border { BorderBrush = Ui.Res("BorderBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Child = head });
        foreach (var group in ItemRules.GroupByDay(shown, _doneView, now))
        {
            var label = Ui.Text($"{group.Label} · {group.Items.Count}", 12.5, FontWeights.SemiBold, group.Key == "overdue" ? Ui.Res("DangerBrush") : Ui.Res("InkSubtleBrush"));
            label.Margin = new Thickness(48, 12, 0, 6); rows.Children.Add(label);
            foreach (var item in group.Items) rows.Children.Add(TableRow(item, now));
        }
        var box = new Border
        {
            Background = Ui.Res("NavBrush"), BorderBrush = Ui.Res("BorderBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(0, 0, 0, 6), Child = rows, VerticalAlignment = VerticalAlignment.Top, MinWidth = 760,
        };
        return new ScrollViewer { Content = box, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 6, 0) };
    }
    private static TextBlock Caption(string text) => Ui.Text(text.ToUpperInvariant(), 11.5, FontWeights.SemiBold, Ui.Res("InkHintBrush"), wrap: false);

    private Border TableRow(Item item, long now)
    {
        var title = Ui.Stack(TitleButton(item));
        if (TaskBadge(item) is { } badge) title.Children.Add(badge);
        var tags = new WrapPanel();
        if (item.Tab == Tab.LEARN && !string.IsNullOrWhiteSpace(item.Topic)) tags.Children.Add(Outline(item.Topic!));
        if (item.Personal) tags.Children.Add(Outline("personal"));
        if (item.SnoozedUntil is long snooze && snooze > now) tags.Children.Add(Outline("snoozed " + Clock.FormatTime(snooze)));
        if (tags.Children.Count > 0) title.Children.Add(tags);
        bool late = !item.Done && item.DueAt is long d && d < now;
        var due = Ui.Mono(item.DueAt is long dd ? (item.DueHasTime ? Clock.ToLocal(dd).ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture)
            : Clock.ToLocal(dd).ToString("ddd d MMM", CultureInfo.InvariantCulture)) : "—", 12.5, late ? Ui.Res("DangerBrush") : Ui.Res("InkSubtleBrush"));
        var repeat = Ui.Text(Recurrence.Label(item) ?? "—", 13, color: Ui.Res("InkSubtleBrush"), wrap: false);
        var priority = Ui.Text(item.Priority is Priority p ? PriorityNames.Label(p) : "—", 13,
            color: item.Priority is Priority pr ? PriorityInk(pr) : Ui.Res("InkSubtleBrush"), wrap: false);
        var grid = Ui.Columns(TableCols, CompletionCheck(item), title, due, repeat, priority, ItemActions(item));
        grid.Margin = new Thickness(12, 0, 12, 0);
        var row = new Border { MinHeight = 48, Background = Brushes.Transparent, BorderBrush = Ui.Res("BorderBrush"), BorderThickness = new Thickness(0, 1, 0, 0), Child = grid, ToolTip = item.Notes.Length > 0 ? item.Notes : null };
        row.MouseEnter += (_, _) => row.Background = Ui.Res("HoverBrush");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.ContextMenu = ItemMenu(item);
        return row;
    }
    private static Border Outline(string text) => new()
    {
        BorderBrush = Ui.Res("InverseSubBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
        Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        Child = Ui.Text(text, 11.5, color: Ui.Res("InkSubtleBrush"), wrap: false),
    };

    private UIElement TileBody(List<Item> shown, long now)
    {
        var stack = new StackPanel();
        foreach (var group in ItemRules.GroupByDay(shown, _doneView, now))
        {
            var body = new StackPanel();
            body.Children.Add(Ui.Text(group.Label, 18, FontWeights.ExtraBold, group.Key == "overdue" ? Ui.Res("DangerBrush") : Ui.Res("InkBrush")));
            foreach (var item in group.Items) body.Children.Add(TimelineRow(item, now));
            var tile = Ui.Tile(body, padding: 20); tile.Margin = new Thickness(0, 0, 0, 14); stack.Children.Add(tile);
        }
        return Ui.Scroll(stack);
    }

    private UIElement TimelineRow(Item item, long now)
    {
        var time = Ui.Text(item.DueAt is long d && item.DueHasTime ? Clock.ToLocal(d).ToString("HH:mm", CultureInfo.InvariantCulture) : "—", 15, FontWeights.ExtraBold, wrap: false);
        time.Width = 64;
        var mid = Ui.Stack(TitleButton(item, 15));
        if (TaskBadge(item) is { } badge) mid.Children.Add(badge);
        var meta = new List<string>();
        if (Recurrence.Label(item) is string repeat) meta.Add("repeats " + repeat.ToLowerInvariant());
        if (item.Tab == Tab.LEARN && !string.IsNullOrWhiteSpace(item.Platform)) meta.Add(item.Platform!);
        if (item.Progress > 0) meta.Add($"{item.Progress}%");
        if (!string.IsNullOrWhiteSpace(item.Notes)) meta.Add(item.Notes.Trim().Replace('\n', ' '));
        if (meta.Count > 0) mid.Children.Add(Ui.Sub(string.Join(" · ", meta)));
        UIElement priority = item.Priority is Priority p && p != Priority.MEDIUM
            ? Ui.Pill(PriorityNames.Label(p), PriorityInk(p), p == Priority.URGENT ? Ui.Res("DangerSoftBrush") : p == Priority.HIGH ? Ui.Res("AmberSoftBrush") : Ui.Res("NavSelectedBrush"), 12, FontWeights.Bold)
            : new Border();
        var grid = Ui.Columns("Auto,Auto,*,Auto,Auto", time, CompletionCheck(item), mid, priority, ItemActions(item));
        return new Border
        {
            BorderBrush = Ui.Res("BorderBrush"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(4, 12, 4, 12),
            Margin = new Thickness(0, 10, 0, 0), Child = grid, Background = Brushes.Transparent, ContextMenu = ItemMenu(item),
        };
    }

    private static void EditLatest(Item item)
    {
        var current = AppState.Current.Item(item.Id);
        if (current == null || current.DeletedAt != null) { App.Current.Main.Snack("This task is no longer available."); return; }
        ItemEditor.Edit(current);
    }
    private static Button TitleButton(Item item, double size = 14) => WorkspaceUi.Link(
        $"edit-task-{item.Id}", "Edit task " + item.Title, item.Title, () => EditLatest(item), size, item.Done);
    private static CheckBox CompletionCheck(Item item)
    {
        var check = WorkspaceUi.Identify(Ui.Check(item.Done, done => Toggle(item, done)), $"complete-task-{item.Id}",
            (item.Done ? "Mark active: " : "Mark done: ") + item.Title);
        check.Click += (_, e) => e.Handled = true;
        return check;
    }
    private static StackPanel ItemActions(Item item)
    {
        var edit = WorkspaceUi.Action($"task-pencil-{item.Id}", "Edit task " + item.Title, "✎", () => EditLatest(item));
        edit.MinWidth = 32;
        var more = WorkspaceUi.More($"task-menu-{item.Id}", "More options for " + item.Title, () => ItemMenu(item));
        return Ui.Row(edit, more);
    }
    internal static void Toggle(Item item, bool isChecked)
    {
        var state = AppState.Current;
        var before = state.Item(item.Id);
        if (before == null || before.DeletedAt != null || before.Done == isChecked) return;
        if (isChecked) { var message = state.Complete(before); App.Current.Main.Snack(message, () => state.Upsert(before)); }
        else { state.Revive(before); App.Current.Main.Snack("Back to Active", () => state.Upsert(before)); }
    }
    private static void Snooze(Item item)
    {
        var state = AppState.Current;
        var current = state.Item(item.Id);
        if (current == null || current.DeletedAt != null) return;
        state.Snooze(current, state.Settings.SnoozeMinutes);
        App.Current.Main.Snack($"Snoozed {state.Settings.SnoozeMinutes} min", () => state.Upsert(current));
    }
    private static void Delete(Item item)
    {
        var current = AppState.Current.Item(item.Id);
        if (current == null || current.DeletedAt != null || !Ui.Confirm($"Move \"{current.Title}\" to the Bin?")) return;
        AppState.Current.SoftDelete(new[] { current });
        App.Current.Main.Snack("Moved to the Bin", () => AppState.Current.Upsert(current));
    }
    private static ContextMenu ItemMenu(Item item)
    {
        var state = AppState.Current;
        var menu = new ContextMenu();
        void Add(string header, Action action) { var entry = new MenuItem { Header = header }; entry.Click += (_, e) => { e.Handled = true; action(); }; menu.Items.Add(entry); }
        Add("Edit…", () => EditLatest(item));
        Add(item.Done ? "Back to Active" : "Done", () => Toggle(item, !item.Done));
        if (!item.Done && item.DueAt != null) Add($"Snooze {state.Settings.SnoozeMinutes} min", () => Snooze(item));
        if (!item.Done) Add("Move to today", () =>
        {
            var current = state.Item(item.Id);
            if (current == null || current.DeletedAt != null) return;
            state.Upsert(DesktopViews.MoveToToday(current, state.Now, state.Settings.DefaultDueHour));
            App.Current.Main.Snack("Moved to today", () => state.Upsert(current));
        });
        if (!string.IsNullOrWhiteSpace(item.Url)) Add("Open link", () => InstallInfo.OpenUrl(item.Url!));
        menu.Items.Add(new Separator()); Add("Delete (to the Bin)…", () => Delete(item));
        return menu;
    }
    private static List<UIElement> TagList(Item item, long now)
    {
        var tags = new List<UIElement>();
        if (Ui.PriorityTag(item.Priority) is Border priority) tags.Add(priority);
        if (Recurrence.Label(item) is string repeat) tags.Add(Ui.Tag("↻ " + repeat, Ui.Res("AccentInkBrush"), Ui.Res("AccentSoftBrush")));
        if (item.Personal) tags.Add(Ui.Tag("🔒 Personal", Ui.Res("InkSubtleBrush"), Ui.Res("NavSelectedBrush")));
        if (item.AlertType == "OFF") tags.Add(Ui.Tag("🔕 Muted", Ui.Res("InkSubtleBrush"), Ui.Res("NavSelectedBrush")));
        if (item.SnoozedUntil is long snooze && snooze > now) tags.Add(Ui.Tag("💤 " + Clock.FormatTime(snooze), Ui.Res("AmberBrush"), Ui.Res("AmberSoftBrush")));
        return tags;
    }
    private static WrapPanel Tags(Item item, long now)
    {
        var row = new WrapPanel(); foreach (var tag in TagList(item, now)) row.Children.Add(tag); return row;
    }

    internal static Border ItemCard(Item item, long now, bool showList = false, Button? taskBadge = null)
    {
        var state = AppState.Current;
        var titleRow = new WrapPanel();
        titleRow.Children.Add(TitleButton(item, 15));
        foreach (var tag in TagList(item, now)) titleRow.Children.Add(tag);
        var mid = Ui.Stack(titleRow);
        if (taskBadge != null) mid.Children.Add(taskBadge);
        var meta = new List<string>();
        var due = Ui.DueText(item.DueAt, item.DueHasTime, now);
        if (due.Length > 0) meta.Add((item.Done && item.RepeatMode != "OFF" ? "Returns " : "") + due);
        if (item.Tab == Tab.SHOP)
        {
            if (ShopLists.QtySegment(item) is string quantity) meta.Add(quantity);
            if (!string.IsNullOrWhiteSpace(item.ShopName)) meta.Add("🏪 " + item.ShopName);
            if (ShopLists.ParseNum(item.Price) is double price && price > 0) meta.Add("₹" + price.ToString("#,##0.##", CultureInfo.InvariantCulture));
            if (showList) meta.Add("📋 " + (ShopLists.ListIdOf(item, state.Settings.ShopLists) is long lid ? state.List(lid)?.Name : "Unsorted"));
            if (item.ReturnAt is long returnAt) meta.Add("back " + Clock.FormatDay(returnAt));
        }
        if (item.Tab == Tab.LEARN)
        {
            if (!string.IsNullOrWhiteSpace(item.Platform)) meta.Add(item.Platform!);
            if (!string.IsNullOrWhiteSpace(item.Topic)) meta.Add(item.Topic!);
            if (item.Progress > 0) meta.Add($"{item.Progress}%");
            if (item.HoursSpent > 0) meta.Add($"{item.HoursSpent:0.#} h");
        }
        if (!string.IsNullOrWhiteSpace(item.Notes)) meta.Add(item.Notes.Trim().Replace('\n', ' '));
        if (meta.Count > 0)
        {
            var text = Ui.Sub(string.Join("  ·  ", meta));
            if (!item.Done && item.DueAt is long d && d < now && item.Tab != Tab.SHOP) text.Foreground = Ui.Res("DangerBrush");
            mid.Children.Add(text);
        }
        if (item.Tab == Tab.LEARN && item.Progress > 0) mid.Children.Add(new ProgressBar
        {
            Value = item.Progress, Maximum = 100, Height = 4, Margin = new Thickness(0, 5, 0, 0), Foreground = Ui.Res("AccentBrush"),
            Background = Ui.Res("NavSelectedBrush"), BorderThickness = new Thickness(0),
        });
        var actions = ItemActions(item);
        var card = Ui.Card(Ui.Columns("Auto,*,Auto", CompletionCheck(item), mid, actions));
        card.ContextMenu = ItemMenu(item);
        if (item.Personal) card.BorderBrush = Ui.Res("AccentSoftBrush");
        return card;
    }
}
