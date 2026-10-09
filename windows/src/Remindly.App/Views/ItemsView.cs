using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Remindly.App.Services;
using Remindly.Core;

namespace Remindly.App.Views;

/// <summary>
/// Tasks and Learn: quick add, Active / Done, grouped by day, tick to complete (with Undo). The layout follows the design
/// chosen in Settings → Appearance: A list + details pane · B day columns · C dense rows with quick views · D day tiles.
/// </summary>
public sealed class ItemsView : DockPanel, IPage
{
    // ───────────────────────── Command Dark quick views ─────────────────────────

    public const string AllView = "ALL";

    public sealed record QuickView(string Code, string Label, string Dot);

    public static readonly QuickView[] QuickViews =
    {
        new("TODAY", "Today", "#9B8CFF"),
        new("OVERDUE", "Overdue", "#F87171"),
        new("UPCOMING", "Next 7 days", "#60A5FA"),
        new("REPEATING", "Repeating", "#34D399"),
        new("PERSONAL", "Personal", "#A0A6B3"),
    };

    /// <summary>The quick view picked in the Command Dark sidebar (Tasks only).</summary>
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
            "REPEATING" => i.RepeatMode != "OFF",
            "PERSONAL" => i.Personal,
            _ => true,
        };
    }

    private readonly Tab _tab;
    private readonly string _design = Theme.Design;
    private readonly StackPanel _list = new();
    private readonly TextBlock _title;
    private readonly TextBlock _count = Ui.Sub("");
    private readonly TextBox? _quick;
    private readonly TextBox _search;
    private readonly ContentControl _body = new() { Focusable = false };
    private readonly Border _detail = new();
    private bool _doneView;
    private long? _selected;

    public ItemsView(Tab tab)
    {
        _tab = tab;
        LastChildFill = true;
        var noun = tab == Tab.LEARN ? "something to learn" : "a task";
        _search = Ui.Input(placeholder: "Search", width: 200);
        _search.TextChanged += (_, _) => Refresh();
        _title = Ui.H1(TabNames.Title(tab));
        if (_design == Designs.Hub) _title.FontSize = 28;

        var right = Ui.Row(
            Ui.Chip("Active", "view" + tab + _design, true, () => { _doneView = false; Refresh(); }),
            Ui.Chip("Done", "view" + tab + _design, false, () => { _doneView = true; Refresh(); }),
            new Border { Width = 12 },
            _search,
            new Border { Width = 8 },
            Ui.Primary("+ New", () => ItemEditor.New(_tab), "New with all details (Ctrl+N)"));
        var bar = Ui.Columns("*,Auto", Ui.Stack(_title, _count), right);
        bar.Margin = new Thickness(0, 0, 0, 10);
        SetDock(bar, Dock.Top);
        Children.Add(bar);

        // Command Dark adds from the window's command bar (Ctrl+K) instead of a box per page.
        if (_design != Designs.Command)
        {
            _quick = Ui.Input(placeholder: $"Add {noun} — type and press Enter", onEnter: QuickAdd);
            if (_design == Designs.Hub) { _quick.Height = 44; _quick.FontSize = 15; }
            var add = Ui.Columns("*,Auto", _quick, Ui.Btn("Add", QuickAdd));
            ((Button)add.Children[1]).Margin = new Thickness(8, 0, 0, 0);
            add.Margin = new Thickness(0, 0, 0, 8);
            SetDock(add, Dock.Top);
            Children.Add(add);
        }

        Children.Add(_body);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
            {
                ItemEditor.New(_tab);
                e.Handled = true;
            }
        };
    }

    private void QuickAdd()
    {
        if (_quick == null) return;
        var text = _quick.Text.Trim();
        if (AddQuick(_tab, text)) _quick.Clear();
    }

    /// <summary>Quick add with the duplicate check (used by the page box and the Command Dark command bar).</summary>
    public static bool AddQuick(Tab tab, string text)
    {
        text = text.Trim();
        if (text.Length == 0) return false;
        var state = AppState.Current;
        if (ItemRules.DupActiveMatch(state.Data.Items, tab, text) && !Ui.Confirm($"\"{text}\" is already on your {TabNames.Title(tab)} list. Add it again?")) return false;
        state.Upsert(state.NewItem(tab, text));
        App.Current.Main.Snack($"Added to {TabNames.Title(tab)}");
        return true;
    }

    public void Refresh()
    {
        var state = AppState.Current;
        long now = state.Now;
        var q = _search.Text.Trim();
        var live = state.LiveItems(_tab).ToList();
        var view = _design == Designs.Command && _tab == Tab.TASKS ? CommandView : AllView;
        var shown = live.Where(i => i.Done == _doneView)
            .Where(i => q.Length == 0 || i.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || i.Notes.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (i.Topic ?? "").Contains(q, StringComparison.OrdinalIgnoreCase))
            .Where(i => _doneView || Matches(view, i, now))
            .ToList();
        int open = live.Count(i => !i.Done);
        int today = ItemRules.PendingTodayCount(live, _tab, now);
        _title.Text = view == AllView ? TabNames.Title(_tab) : QuickViews.First(v => v.Code == view).Label;
        _count.Text = $"{open} active · {today} due today or overdue · {live.Count(i => i.Done)} done";

        if (shown.Count == 0 && !(_design == Designs.Board && !_doneView))
        {
            _body.Content = Ui.Scroll(Ui.EmptyState(_doneView ? "Nothing done yet" : "All clear",
                _doneView ? "Ticked items land here. Recurring ones come back on their next day." : "Type above and press Enter, or use + New for dates, repeats and priority."));
            return;
        }
        var old = FindScroll(_body.Content as DependencyObject);
        double v = old?.VerticalOffset ?? 0, h = old?.HorizontalOffset ?? 0;
        _body.Content = _design switch
        {
            Designs.Board when !_doneView => BoardBody(shown, now),
            Designs.Command => TableBody(shown, now),
            Designs.Hub => TileBody(shown, now),
            Designs.Fluent => FluentBody(shown, now),
            _ => ListBody(shown, now),
        };
        // Every refresh rebuilds the body; keep the reader where they were (runs before the next mouse input).
        if (v > 0 || h > 0)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                if (FindScroll(_body.Content as DependencyObject) is ScrollViewer sv) { sv.ScrollToVerticalOffset(v); sv.ScrollToHorizontalOffset(h); }
            });
    }

    private static ScrollViewer? FindScroll(DependencyObject? d)
    {
        if (d == null) return null;
        if (d is ScrollViewer sv) return sv;
        foreach (var child in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>())
            if (FindScroll(child) is ScrollViewer found) return found;
        return null;
    }

    private ScrollViewer ListBody(List<Item> shown, long now)
    {
        _list.Children.Clear();
        foreach (var g in ItemRules.GroupByDay(shown, _doneView, now))
        {
            var header = Ui.Header($"{g.Label} · {g.Items.Count}");
            if (g.Key == "overdue") header.Foreground = Ui.Res("DangerBrush");
            _list.Children.Add(header);
            foreach (var i in g.Items) _list.Children.Add(ItemCard(i, now));
        }
        (_list.Parent as ScrollViewer)?.SetValue(ContentControl.ContentProperty, null);
        return Ui.Scroll(_list);
    }

    // ───────────────────────── A · Fluent: list + details pane ─────────────────────────

    private UIElement FluentBody(List<Item> shown, long now)
    {
        if (_selected is long sid && !shown.Any(i => i.Id == sid)) _selected = null;
        _selected ??= shown.FirstOrDefault(i => !i.Done)?.Id ?? shown.FirstOrDefault()?.Id;
        var list = new StackPanel();
        foreach (var g in ItemRules.GroupByDay(shown, _doneView, now))
        {
            var header = Ui.Header($"{g.Label} · {g.Items.Count}");
            header.Foreground = g.Key == "overdue" ? Ui.Res("DangerBrush") : Ui.Res("InkBrush");
            list.Children.Add(header);
            foreach (var i in g.Items)
            {
                var card = ItemCard(i, now);
                var id = i.Id;
                if (i.Id == _selected)
                {
                    card.Background = Ui.Res("AccentSoftBrush");
                    card.BorderBrush = Ui.Res("AccentBrush");
                }
                card.Cursor = Cursors.Hand;
                card.MouseLeftButtonUp += (_, e) =>
                {
                    if (e.OriginalSource is DependencyObject d && IsInside<ButtonBase>(d)) return;
                    _selected = id;
                    Refresh();
                };
                list.Children.Add(card);
            }
        }
        var item = _selected is long s ? shown.FirstOrDefault(i => i.Id == s) : null;
        _detail.Child = DetailPane(item, now);
        _detail.Width = 330;
        _detail.Margin = new Thickness(16, 0, 0, 0);
        (_detail.Parent as Panel)?.Children.Remove(_detail);
        var grid = Ui.Columns("*,Auto", Ui.Scroll(list), _detail);
        void Fit() => _detail.Visibility = grid.ActualWidth > 0 && grid.ActualWidth < 760 ? Visibility.Collapsed : Visibility.Visible;
        grid.SizeChanged += (_, _) => Fit();
        return grid;
    }

    private static bool IsInside<T>(DependencyObject d) where T : DependencyObject
    {
        for (var x = d; x != null; x = x is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(x) ?? LogicalTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (x is T || x is CheckBox) return true;
        return false;
    }

    private UIElement DetailPane(Item? i, long now)
    {
        var p = new StackPanel();
        var box = new Border
        {
            Background = Ui.Res("CardBrush"),
            BorderBrush = Ui.Res("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18, 16, 18, 16),
            VerticalAlignment = VerticalAlignment.Top,
            Child = p,
        };
        if (i == null)
        {
            p.Children.Add(Ui.Text("Details", 16, FontWeights.SemiBold));
            p.Children.Add(Ui.Sub("Select a " + (_tab == Tab.LEARN ? "Learn item" : "task") + " to see it here."));
            return box;
        }
        var state = AppState.Current;
        p.Children.Add(Ui.Columns("*,Auto", Ui.Text("Details", 12.5, FontWeights.SemiBold, Ui.Res("InkSubtleBrush")),
            Ui.Sub("Edited " + Clock.FormatDayTime(Math.Max(i.UpdatedAt, i.CreatedAt)))));
        var title = Ui.Text(i.Title, 18, FontWeights.SemiBold);
        title.Margin = new Thickness(0, 6, 0, 8);
        p.Children.Add(title);
        var tags = Tags(i, now);
        if (tags.Children.Count > 0) { tags.Margin = new Thickness(0, 0, 0, 8); p.Children.Add(tags); }

        void Field(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var l = Ui.Text(label, 12, FontWeights.SemiBold, Ui.Res("InkSubtleBrush"));
            l.Margin = new Thickness(0, 8, 0, 2);
            p.Children.Add(l);
            p.Children.Add(Ui.Text(value!, 14));
        }
        var due = Ui.DueText(i.DueAt, i.DueHasTime, now);
        Field(i.Done ? (i.RepeatMode != "OFF" ? "Returns" : "Was due") : "Due", due.Length > 0 ? due : "No date");
        Field("Repeat", Recurrence.Label(i));
        Field("Priority", i.Priority is Priority pr ? PriorityNames.Label(pr) : null);
        Field("Alert", i.DueAt == null ? null : i.AlertType == "OFF" ? "Muted" : i.AlertType.Contains('R') || i.AlertType.Contains('A') ? "Ring" : "Notify");
        if (i.Tab == Tab.LEARN)
        {
            Field("Platform", i.Platform);
            Field("Topic", i.Topic);
            if (i.Progress > 0) Field("Progress", $"{i.Progress}% · {i.HoursSpent:0.#} h spent");
        }
        Field("Notes", i.Notes);
        if (i.Done && i.DoneAt is long da) Field("Done", Clock.FormatDayTime(da));

        var actions = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        actions.Children.Add(Ui.Primary("Edit…", () => ItemEditor.Edit(i)));
        actions.Children.Add(Ui.Btn(i.Done ? "Back to Active" : "Done", () => Toggle(i, !i.Done)));
        if (!i.Done && i.DueAt != null) actions.Children.Add(Ui.Btn($"Snooze {state.Settings.SnoozeMinutes} min", () => Snooze(i)));
        if (!string.IsNullOrWhiteSpace(i.Url)) actions.Children.Add(Ui.Btn("Open link", () => InstallInfo.OpenUrl(i.Url!)));
        foreach (var b in actions.Children.OfType<Button>()) b.Margin = new Thickness(0, 0, 6, 6);
        p.Children.Add(actions);
        var del = Ui.Btn("Delete", () => Delete(i), "Moves it to the Bin for 30 days", "Danger");
        del.HorizontalAlignment = HorizontalAlignment.Left;
        p.Children.Add(del);
        return box;
    }

    // ───────────────────────── B · Day Board: four columns ─────────────────────────

    private UIElement BoardBody(List<Item> shown, long now)
    {
        var state = AppState.Current;
        var cols = DesktopViews.BoardColumns(shown, now);
        var grid = new UniformGrid4();
        foreach (var c in cols)
        {
            bool overdue = c.Key == DesktopViews.Overdue;
            var stack = new StackPanel();
            var day = c.Key switch
            {
                DesktopViews.Today => Clock.ToLocal(now).ToString("ddd d", CultureInfo.InvariantCulture) + " · ",
                DesktopViews.Tomorrow => Clock.ToLocal(now).AddDays(1).ToString("ddd d", CultureInfo.InvariantCulture) + " · ",
                _ => "",
            };
            stack.Children.Add(Ui.Columns("*,Auto",
                Ui.Text(c.Label, 15, FontWeights.ExtraBold, overdue ? Ui.Res("DangerBrush") : Ui.Res("InkBrush"), wrap: false),
                Ui.Text(day + c.Items.Count, 12.5, FontWeights.SemiBold, Ui.Res("InkSubtleBrush"), wrap: false)));
            ((Grid)stack.Children[0]).Margin = new Thickness(4, 2, 4, 10);
            foreach (var i in c.Items) stack.Children.Add(BoardCard(i, now));
            if (c.Items.Count == 0) { var e = Ui.Sub("Nothing here"); e.Margin = new Thickness(4, 0, 0, 10); stack.Children.Add(e); }

            Button footer;
            if (overdue)
            {
                var late = c.Items;
                footer = Ui.Btn("Move all to today", () =>
                {
                    var snap = state.TakeSnapshot();
                    state.UpsertMany(late.Select(x => DesktopViews.MoveToToday(x, state.Now, state.Settings.DefaultDueHour)));
                    App.Current.Main.Snack($"{late.Count} moved to today", () => state.Restore(snap));
                });
                footer.IsEnabled = late.Count > 0;
            }
            else
            {
                long? due = c.Key switch
                {
                    DesktopViews.Today => Math.Min(Clock.StartOfNextDay(now) - 60_000L, (now / 3_600_000L + 1) * 3_600_000L),
                    DesktopViews.Tomorrow => Clock.StartOfNextDay(now) + state.Settings.DefaultDueHour * 3_600_000L,
                    _ => null,
                };
                footer = Ui.Btn("+ Add " + (c.Key == DesktopViews.Later ? "for later" : "to " + c.Label.ToLowerInvariant()), () => ItemEditor.New(_tab, dueAt: due));
            }
            footer.HorizontalAlignment = HorizontalAlignment.Stretch;
            footer.Background = Brushes.Transparent;
            footer.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(footer);

            grid.Children.Add(new Border
            {
                Background = overdue ? Ui.Res("DangerSoftBrush") : c.Key == DesktopViews.Today ? Ui.Res("AccentSoftBrush") : Ui.Res("NavSelectedBrush"),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Child = stack,
            });
        }
        var sv = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = grid };
        sv.SizeChanged += (_, _) => grid.Width = Math.Max(4 * 250, sv.ViewportWidth > 0 ? sv.ViewportWidth : sv.ActualWidth);
        return sv;
    }

    /// <summary>Four equal columns that fill the width they are given.</summary>
    private sealed class UniformGrid4 : UniformGrid
    {
        public UniformGrid4() { Columns = 4; Rows = 1; VerticalAlignment = VerticalAlignment.Top; }
    }

    private Border BoardCard(Item i, long now)
    {
        var state = AppState.Current;
        var check = Ui.Check(i.Done, on => Toggle(i, on), "Done");
        var title = Ui.Text(i.Title, 14.5, FontWeights.Bold);
        var chips = new WrapPanel { Margin = new Thickness(30, 6, 0, 0) };
        bool late = !i.Done && i.DueAt is long d && d < now;
        if (i.DueAt is long due)
            chips.Children.Add(Chip((i.DueHasTime ? Clock.FormatTime(due) : Clock.FormatDay(due)), late ? Ui.Res("DangerBrush") : Ui.Res("InkSubtleBrush"), late ? Ui.Res("DangerSoftBrush") : Ui.Res("NavSelectedBrush")));
        if (Recurrence.Label(i) is string rep) chips.Children.Add(Chip("↻ " + rep, Ui.Res("AccentInkBrush"), Ui.Res("AccentSoftBrush")));
        if (i.Priority is Priority pr && pr != Priority.MEDIUM) chips.Children.Add(Chip("● " + PriorityNames.Label(pr), PriorityInk(pr), Ui.Res("NavSelectedBrush")));
        if (i.Personal) chips.Children.Add(Chip("Personal", Ui.Res("InkSubtleBrush"), Ui.Res("NavSelectedBrush")));
        if (i.Tab == Tab.LEARN && i.Progress > 0) chips.Children.Add(Chip($"{i.Progress}%", Ui.Res("AccentInkBrush"), Ui.Res("AccentSoftBrush")));
        var body = Ui.Stack(Ui.Columns("Auto,*", check, title));
        if (chips.Children.Count > 0) body.Children.Add(chips);
        if (!string.IsNullOrWhiteSpace(i.Notes))
        {
            var n = Ui.Sub(i.Notes.Trim().Replace('\n', ' '));
            n.Margin = new Thickness(30, 6, 0, 0);
            n.MaxHeight = 36;
            body.Children.Add(n);
        }
        var card = Ui.Card(body, () => ItemEditor.Edit(i));
        card.CornerRadius = new CornerRadius(12);
        card.Padding = new Thickness(12, 10, 12, 10);
        card.Margin = new Thickness(0, 0, 0, 8);
        card.ContextMenu = ItemMenu(i);
        card.ToolTip = "Double-click to edit · right-click for more";
        return card;
    }

    private static Border Chip(string text, Brush fg, Brush bg)
    {
        var t = Ui.Tag(text, fg, bg);
        t.CornerRadius = new CornerRadius(8);
        t.Padding = new Thickness(7, 2, 7, 2);
        t.Margin = new Thickness(0, 0, 6, 4);
        return t;
    }

    private static Brush PriorityInk(Priority p) => p switch
    {
        Priority.URGENT => Ui.Res("DangerBrush"),
        Priority.HIGH => Ui.Res("AmberBrush"),
        _ => Ui.Res("InkSubtleBrush"),
    };

    // ───────────────────────── C · Command Dark: dense rows ─────────────────────────

    private const string TableCols = "36,*,180,150,100";

    private UIElement TableBody(List<Item> shown, long now)
    {
        var rows = new StackPanel();
        var head = Ui.Columns(TableCols, new Border(), Caption("Task"), Caption("Due"), Caption("Repeat"), Caption("Priority"));
        head.Margin = new Thickness(12, 0, 12, 0);
        head.Height = 34;
        rows.Children.Add(new Border { BorderBrush = Ui.Res("BorderBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Child = head });
        foreach (var g in ItemRules.GroupByDay(shown, _doneView, now))
        {
            var label = Ui.Text($"{g.Label} · {g.Items.Count}", 12.5, FontWeights.SemiBold, g.Key == "overdue" ? Ui.Res("DangerBrush") : Ui.Res("InkBrush"));
            label.Margin = new Thickness(48, 12, 0, 6);
            rows.Children.Add(label);
            foreach (var i in g.Items) rows.Children.Add(TableRow(i, now));
        }
        var box = new Border
        {
            Background = Ui.Res("NavBrush"),
            BorderBrush = Ui.Res("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(0, 0, 0, 6),
            Child = rows,
            VerticalAlignment = VerticalAlignment.Top,
        };
        return new ScrollViewer { Content = box, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 6, 0) };
    }

    private static TextBlock Caption(string text) => Ui.Text(text.ToUpperInvariant(), 11.5, FontWeights.SemiBold, Ui.Res("InkHintBrush"), wrap: false);

    private Border TableRow(Item i, long now)
    {
        var check = Ui.Check(i.Done, on => Toggle(i, on), "Done");
        check.LayoutTransform = new ScaleTransform(1.1, 1.1);
        var title = Ui.Row(Ui.Text(i.Title, 14, FontWeights.Medium, i.Done ? Ui.Res("InkSubtleBrush") : Ui.Res("InkBrush"), wrap: false));
        if (i.Done) ((TextBlock)title.Children[0]).TextDecorations = TextDecorations.Strikethrough;
        if (i.Tab == Tab.LEARN && !string.IsNullOrWhiteSpace(i.Topic)) title.Children.Add(Outline(i.Topic!));
        if (i.Personal) title.Children.Add(Outline("personal"));
        if (i.SnoozedUntil is long sn && sn > now) title.Children.Add(Outline("snoozed " + Clock.FormatTime(sn)));
        bool late = !i.Done && i.DueAt is long d && d < now;
        var due = Ui.Mono(i.DueAt is long dd ? (i.DueHasTime ? Clock.ToLocal(dd).ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture) : Clock.ToLocal(dd).ToString("ddd d MMM", CultureInfo.InvariantCulture)) : "—",
            12.5, late ? Ui.Res("DangerBrush") : Ui.Res("InkSubtleBrush"));
        var rep = Ui.Text(Recurrence.Label(i) ?? "—", 13, color: Ui.Res("InkSubtleBrush"), wrap: false);
        var pri = Ui.Row(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Background = i.Priority is Priority p && p != Priority.MEDIUM ? PriorityInk(p) : Ui.Res("BorderBrush"), Margin = new Thickness(0, 0, 6, 0) },
            Ui.Text(i.Priority is Priority p2 ? PriorityNames.Label(p2) : "—", 13, color: Ui.Res("InkSubtleBrush"), wrap: false));
        var grid = Ui.Columns(TableCols, check, title, due, rep, pri);
        grid.Margin = new Thickness(12, 0, 12, 0);
        var row = new Border { MinHeight = 40, Background = Brushes.Transparent, BorderBrush = Ui.Res("BorderBrush"), BorderThickness = new Thickness(0, 1, 0, 0), Child = grid, ToolTip = i.Notes.Length > 0 ? i.Notes : null };
        row.MouseEnter += (_, _) => row.Background = Ui.Res("HoverBrush");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { ItemEditor.Edit(i); e.Handled = true; } };
        row.ContextMenu = ItemMenu(i);
        return row;
    }

    private static Border Outline(string text) => new()
    {
        BorderBrush = Ui.Res("InverseSubBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(5, 0, 5, 1),
        Margin = new Thickness(8, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = Ui.Text(text, 11.5, color: Ui.Res("InkSubtleBrush"), wrap: false),
    };

    // ───────────────────────── D · Today Hub: a tile per day ─────────────────────────

    private UIElement TileBody(List<Item> shown, long now)
    {
        var stack = new StackPanel();
        foreach (var g in ItemRules.GroupByDay(shown, _doneView, now))
        {
            var p = new StackPanel();
            p.Children.Add(Ui.Text($"{g.Label}", 18, FontWeights.ExtraBold, g.Key == "overdue" ? Ui.Res("DangerBrush") : Ui.Res("InkBrush")));
            foreach (var i in g.Items) p.Children.Add(TimelineRow(i, now));
            var tile = Ui.Tile(p, padding: 20);
            tile.Margin = new Thickness(0, 0, 0, 14);
            stack.Children.Add(tile);
        }
        return Ui.Scroll(stack);
    }

    private UIElement TimelineRow(Item i, long now)
    {
        var time = Ui.Text(i.DueAt is long d && i.DueHasTime ? Clock.ToLocal(d).ToString("HH:mm", CultureInfo.InvariantCulture) : "—", 15, FontWeights.ExtraBold, wrap: false);
        time.Width = 64;
        var check = Ui.Check(i.Done, on => Toggle(i, on), "Done");
        var meta = new List<string>();
        if (Recurrence.Label(i) is string rep) meta.Add("repeats " + rep.ToLowerInvariant());
        if (i.Tab == Tab.LEARN && !string.IsNullOrWhiteSpace(i.Platform)) meta.Add(i.Platform!);
        if (i.Progress > 0) meta.Add($"{i.Progress}%");
        if (!string.IsNullOrWhiteSpace(i.Notes)) meta.Add(i.Notes.Trim().Replace('\n', ' '));
        var mid = Ui.Stack(Ui.Text(i.Title, 15, FontWeights.Bold));
        if (meta.Count > 0) mid.Children.Add(Ui.Sub(string.Join(" · ", meta)));
        UIElement pri = i.Priority is Priority p && p != Priority.MEDIUM
            ? Ui.Pill(PriorityNames.Label(p), PriorityInk(p), p == Priority.URGENT ? Ui.Res("DangerSoftBrush") : p == Priority.HIGH ? Ui.Res("AmberSoftBrush") : Ui.Res("NavSelectedBrush"), 12, FontWeights.Bold)
            : new Border();
        var grid = Ui.Columns("Auto,Auto,*,Auto", time, check, mid, pri);
        var row = new Border { BorderBrush = Ui.Res("BorderBrush"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(4, 12, 4, 12), Margin = new Thickness(0, 10, 0, 0), Child = grid, Background = Brushes.Transparent };
        row.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { ItemEditor.Edit(i); e.Handled = true; } };
        row.ContextMenu = ItemMenu(i);
        return row;
    }

    // ───────────────────────── shared actions ─────────────────────────

    internal static void Toggle(Item i, bool isChecked)
    {
        var state = AppState.Current;
        var before = i;
        if (isChecked)
        {
            var msg = state.Complete(i);
            App.Current.Main.Snack(msg, () => state.Upsert(before));
        }
        else
        {
            state.Revive(i);
            App.Current.Main.Snack("Back to Active", () => state.Upsert(before));
        }
    }

    private static void Snooze(Item i)
    {
        var state = AppState.Current;
        state.Snooze(i, state.Settings.SnoozeMinutes);
        App.Current.Main.Snack($"Snoozed {state.Settings.SnoozeMinutes} min", () => state.Upsert(i));
    }

    private static void Delete(Item i)
    {
        AppState.Current.SoftDelete(new[] { i });
        App.Current.Main.Snack("Moved to the Bin", () => AppState.Current.Upsert(i));
    }

    /// <summary>Right-click menu for a task (board cards, table rows, timeline rows).</summary>
    private static ContextMenu ItemMenu(Item i)
    {
        var state = AppState.Current;
        var m = new ContextMenu();
        void Add(string header, Action a) { var mi = new MenuItem { Header = header }; mi.Click += (_, _) => a(); m.Items.Add(mi); }
        Add("Edit…", () => ItemEditor.Edit(i));
        Add(i.Done ? "Back to Active" : "Done", () => Toggle(i, !i.Done));
        if (!i.Done && i.DueAt != null) Add($"Snooze {state.Settings.SnoozeMinutes} min", () => Snooze(i));
        if (!i.Done) Add("Move to today", () =>
        {
            state.Upsert(DesktopViews.MoveToToday(i, state.Now, state.Settings.DefaultDueHour));
            App.Current.Main.Snack("Moved to today", () => state.Upsert(i));
        });
        if (!string.IsNullOrWhiteSpace(i.Url)) Add("Open link", () => InstallInfo.OpenUrl(i.Url!));
        m.Items.Add(new Separator());
        Add("Delete (to the Bin)", () => Delete(i));
        return m;
    }

    private static List<UIElement> TagList(Item i, long now)
    {
        var tags = new List<UIElement>();
        if (Ui.PriorityTag(i.Priority) is Border pt) tags.Add(pt);
        if (Recurrence.Label(i) is string rep) tags.Add(Ui.Tag("↻ " + rep, Ui.Res("AccentInkBrush"), Ui.Res("AccentSoftBrush")));
        if (i.Personal) tags.Add(Ui.Tag("🔒 Personal", Ui.Res("InkSubtleBrush"), Ui.Res("NavSelectedBrush")));
        if (i.AlertType == "OFF") tags.Add(Ui.Tag("🔕 Muted", Ui.Res("InkSubtleBrush"), Ui.Res("NavSelectedBrush")));
        if (i.SnoozedUntil is long sn && sn > now) tags.Add(Ui.Tag("💤 " + Clock.FormatTime(sn), Ui.Res("AmberBrush"), Ui.Res("AmberSoftBrush")));
        return tags;
    }

    private static WrapPanel Tags(Item i, long now)
    {
        var row = new WrapPanel();
        foreach (var t in TagList(i, now)) row.Children.Add(t);
        return row;
    }

    /// <summary>The standard card (Buy lists and design A use it).</summary>
    internal static Border ItemCard(Item i, long now, bool showList = false)
    {
        var state = AppState.Current;
        var check = Ui.Check(i.Done, isChecked => Toggle(i, isChecked), i.Done ? "Back to Active" : (i.Tab == Tab.SHOP ? "Bought" : "Done"));

        var titleRow = new WrapPanel();
        var title = Ui.Text(i.Title, 15, FontWeights.SemiBold);
        if (i.Done) { title.TextDecorations = TextDecorations.Strikethrough; title.Foreground = Ui.Res("InkSubtleBrush"); }
        title.Margin = new Thickness(0, 0, 8, 0);
        titleRow.Children.Add(title);
        foreach (var t in TagList(i, now)) titleRow.Children.Add(t);

        var meta = new List<string>();
        var due = Ui.DueText(i.DueAt, i.DueHasTime, now);
        if (due.Length > 0) meta.Add((i.Done && i.RepeatMode != "OFF" ? "Returns " : "") + due);
        if (i.Tab == Tab.SHOP)
        {
            if (ShopLists.QtySegment(i) is string qs) meta.Add(qs);
            if (!string.IsNullOrWhiteSpace(i.ShopName)) meta.Add("🏪 " + i.ShopName);
            if (ShopLists.ParseNum(i.Price) is double p && p > 0) meta.Add("₹" + p.ToString("#,##0.##", CultureInfo.InvariantCulture));
            if (showList) meta.Add("📋 " + (ShopLists.ListIdOf(i, state.Settings.ShopLists) is long lid ? state.List(lid)?.Name : "Unsorted"));
            if (i.ReturnAt is long ra) meta.Add("back " + Clock.FormatDay(ra));
        }
        if (i.Tab == Tab.LEARN)
        {
            if (!string.IsNullOrWhiteSpace(i.Platform)) meta.Add(i.Platform!);
            if (!string.IsNullOrWhiteSpace(i.Topic)) meta.Add(i.Topic!);
            if (i.Progress > 0) meta.Add($"{i.Progress}%");
            if (i.HoursSpent > 0) meta.Add($"{i.HoursSpent:0.#} h");
        }
        if (!string.IsNullOrWhiteSpace(i.Notes)) meta.Add(i.Notes.Trim().Replace('\n', ' '));
        var metaText = Ui.Sub(string.Join("  ·  ", meta));
        if (!i.Done && i.DueAt is long d && d < now && i.Tab != Tab.SHOP) metaText.Foreground = Ui.Res("DangerBrush");

        var mid = Ui.Stack(titleRow);
        if (meta.Count > 0) mid.Children.Add(metaText);
        if (i.Tab == Tab.LEARN && i.Progress > 0)
            mid.Children.Add(new ProgressBar { Value = i.Progress, Maximum = 100, Height = 4, Margin = new Thickness(0, 5, 0, 0), Foreground = Ui.Res("AccentBrush"), Background = Ui.Res("NavSelectedBrush"), BorderThickness = new Thickness(0) });

        var actions = Ui.Row();
        if (!string.IsNullOrWhiteSpace(i.Url)) actions.Children.Add(Ui.IconBtn("🔗", () => InstallInfo.OpenUrl(i.Url!), "Open link"));
        if (!i.Done && i.DueAt != null) actions.Children.Add(Ui.IconBtn("💤", () => Snooze(i), $"Snooze {state.Settings.SnoozeMinutes} min"));
        actions.Children.Add(Ui.IconBtn("✎", () => ItemEditor.Edit(i), "Edit"));
        actions.Children.Add(Ui.IconBtn("🗑", () => Delete(i), "Delete (to the Bin for 30 days)"));

        var grid = Ui.Columns("Auto,*,Auto", check, mid, actions);
        var card = Ui.Card(grid, () => ItemEditor.Edit(i));
        if (i.Personal) card.BorderBrush = Ui.Res("AccentSoftBrush");
        return card;
    }
}
