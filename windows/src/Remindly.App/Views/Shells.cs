using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Remindly.App.Services;
using Remindly.Core;
using Remindly.Core.Updates;

namespace Remindly.App.Views;

/// <summary>A design's window chrome: menu, Task/Shop switch, version, the top-right update button and the page area.</summary>
public interface IShell
{
    FrameworkElement Root { get; }

    /// <summary>Redraws selection and counts.</summary>
    void Sync();

    /// <summary>Ctrl+K: focuses the command bar when the design has one.</summary>
    bool FocusCommand();
}

public static class Shells
{
    public static IShell Create(string design, MainWindow main) => design switch
    {
        Designs.Board => new BoardShell(main),
        Designs.Command => new CommandShell(main),
        Designs.Hub => new HubShell(main),
        _ => new FluentShell(main),
    };

    internal static Image Logo(double size) => new()
    {
        Source = new BitmapImage(new Uri("pack://application:,,,/Assets/app-256.png")),
        Width = size,
        Height = size,
        VerticalAlignment = VerticalAlignment.Center,
    };

    internal static string TodayLong => DateTime.Now.ToString("dddd, d MMMM", CultureInfo.InvariantCulture);

    internal static Button Labelled(Button b, string name)
    {
        AutomationProperties.SetName(b, name);
        return b;
    }

    /// <summary>Square icon button (Settings in the top bars).</summary>
    internal static Button IconSquare(string glyph, string tip, bool selected, Action go, double size = 40)
    {
        var b = Ui.Plain(Ui.Glyph(glyph, 17, selected ? Ui.Res("AccentInkBrush") : Ui.Res("InkSubtleBrush")), go, tip, size);
        b.Width = size;
        b.Padding = new Thickness(0);
        b.HorizontalContentAlignment = HorizontalAlignment.Center;
        b.BorderThickness = new Thickness(1);
        b.BorderBrush = Ui.Res("BorderBrush");
        b.Background = selected ? Ui.Res("AccentSoftBrush") : Ui.Res("CardBrush");
        return Labelled(b, tip);
    }

    /// <summary>Adds quick-add text from a command bar to the screen that is open.</summary>
    internal static void CommandAdd(MainWindow m, string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        var tab = m.SelectedTab;
        var state = AppState.Current;
        if (m.IsShop)
        {
            long? listId = tab == m.BuyTab && m.Buy.OpenListId is long open && open >= 0 ? open : state.Settings.ShopDefaultListId;
            var list = listId is long l ? state.List(l) : null;
            var item = state.NewItem(Tab.SHOP, text) with { ListId = list?.Id, Group = list?.Name, Personal = list?.Personal == true };
            if (list?.UsualShopId is long sid && state.Shops.FirstOrDefault(s => s.Id == sid) is Shop us) item = item with { ShopId = us.Id, ShopName = us.Name };
            state.Upsert(item);
            m.Snack($"Added to {list?.Name ?? "Unsorted"}", () => state.DeleteForever(item));
            return;
        }
        if (tab == m.CallsTab) { CallEditor.OpenFor(null); return; }
        ItemsView.AddQuick(tab == m.LearnTab ? Tab.LEARN : Tab.TASKS, text);
    }
}

// ═════════════════════════ A · Fluent ═════════════════════════

/// <summary>Windows 11: a left menu (logo, version, Task/Shop segment, sections with counts) and a rounded content layer.</summary>
internal sealed class FluentShell : IShell
{
    private readonly MainWindow _m;
    private readonly StackPanel _nav = new();
    private readonly StackPanel _bottom = new();
    private readonly Grid _root = new();

    public FrameworkElement Root => _root;

    public FluentShell(MainWindow m)
    {
        _m = m;
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new DockPanel { Width = 236, Margin = new Thickness(12, 18, 12, 14) };
        var name = Ui.Stack(Ui.Text("Remindly", 15, FontWeights.SemiBold), Ui.Sub(m.VersionLabel));
        name.Margin = new Thickness(10, 0, 0, 0);
        var brand = Ui.Row(Shells.Logo(34), name);
        brand.Margin = new Thickness(6, 0, 0, 16);
        DockPanel.SetDock(brand, Dock.Top);
        left.Children.Add(brand);

        var seg = new UniformGrid { Columns = 2 };
        seg.Children.Add(Segment("Tasks", !m.IsShop, () => m.SetMode("TASK")));
        seg.Children.Add(Segment("Shopping", m.IsShop, () => m.SetMode("SHOP")));
        var segBox = new Border { Background = Ui.Res("BorderBrush"), CornerRadius = new CornerRadius(9), Padding = new Thickness(3), Margin = new Thickness(4, 0, 4, 14), Child = seg };
        AutomationProperties.SetName(segBox, "Mode");
        DockPanel.SetDock(segBox, Dock.Top);
        left.Children.Add(segBox);

        DockPanel.SetDock(_bottom, Dock.Bottom);
        left.Children.Add(_bottom);
        left.Children.Add(_nav);
        Grid.SetColumn(left, 0);
        _root.Children.Add(left);

        var content = new DockPanel();
        m.UpdateSlot.HorizontalAlignment = HorizontalAlignment.Right;
        m.UpdateSlot.Margin = new Thickness(0, 0, 0, 10);
        DockPanel.SetDock(m.UpdateSlot, Dock.Top);
        content.Children.Add(m.UpdateSlot);
        m.Page.Margin = new Thickness(0);
        content.Children.Add(m.Page);
        var layer = new Border
        {
            Background = Ui.Res("LayerBrush"),
            BorderBrush = Ui.Res("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 12, 12, 12),
            Padding = new Thickness(24, 18, 18, 12),
            Child = content,
        };
        Grid.SetColumn(layer, 1);
        _root.Children.Add(layer);
    }

    private static Button Segment(string label, bool selected, Action go)
    {
        var b = Ui.Plain(Ui.Text(label, 13.5, selected ? FontWeights.SemiBold : FontWeights.Normal, selected ? Ui.Res("InkBrush") : Ui.Res("InkSubtleBrush"), wrap: false), go, height: 34);
        b.HorizontalContentAlignment = HorizontalAlignment.Center;
        if (selected) b.Background = Ui.Res("NavSelectedBrush");
        return Shells.Labelled(b, label + " mode");
    }

    private Button Item(string glyph, string label, int? count, bool selected, Action go)
    {
        var pill = new Border { Width = 3, Height = 16, CornerRadius = new CornerRadius(2), Background = selected ? Ui.Res("AccentBrush") : Brushes.Transparent, Margin = new Thickness(-10, 0, 9, 0) };
        var text = Ui.Text(label, 14, selected ? FontWeights.SemiBold : FontWeights.Normal, wrap: false);
        text.Margin = new Thickness(12, 0, 0, 0);
        var g = Ui.Columns("Auto,Auto,*,Auto", pill, Ui.Glyph(glyph, 16, selected ? Ui.Res("AccentBrush") : Ui.Res("InkSubtleBrush")), text,
            count is int n ? Ui.Text(n.ToString(CultureInfo.InvariantCulture), 12, color: Ui.Res("InkSubtleBrush"), wrap: false) : new TextBlock());
        var b = Ui.Plain(g, go, height: 42);
        b.Margin = new Thickness(0, 0, 0, 2);
        if (selected) b.Background = Ui.Res("NavSelectedBrush");
        return Shells.Labelled(b, label);
    }

    public void Sync()
    {
        _nav.Children.Clear();
        foreach (var e in _m.NavEntries())
        {
            var tab = e.Tab;
            _nav.Children.Add(Item(e.Glyph, e.Label, e.Count, _m.SelectedTab == tab, () => _m.Go(tab)));
        }
        _bottom.Children.Clear();
        _bottom.Children.Add(Item(Glyphs.Settings, "Settings", null, _m.SelectedTab == _m.SettingsTab, () => _m.Go(_m.SettingsTab)));
    }

    public bool FocusCommand() => false;
}

// ═════════════════════════ B · Day Board ═════════════════════════

/// <summary>A white top bar (logo + version + date, a centred Task/Shop pill, update and Settings), then underline section tabs.</summary>
internal sealed class BoardShell : IShell
{
    private readonly MainWindow _m;
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
    private readonly ContentControl _settings = new() { Focusable = false };
    private readonly DockPanel _root = new();

    public FrameworkElement Root => _root;

    public BoardShell(MainWindow m)
    {
        _m = m;
        var bar = new Grid();
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var name = Ui.Stack(Ui.Text("Remindly", 17, FontWeights.Bold), Ui.Sub($"v{UpdateService.CurrentDisplayVersion} · {InstallInfo.ModeLabel} · {Shells.TodayLong}"));
        name.Margin = new Thickness(12, 0, 0, 0);
        var brand = Ui.Row(Shells.Logo(36), name);
        Grid.SetColumn(brand, 0);
        bar.Children.Add(brand);

        var pill = new Border
        {
            Background = Ui.Res("NavSelectedBrush"),
            CornerRadius = new CornerRadius(22),
            Padding = new Thickness(4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Ui.Row(Segment("Tasks", !m.IsShop, () => m.SetMode("TASK")), Segment("Shopping", m.IsShop, () => m.SetMode("SHOP"))),
        };
        AutomationProperties.SetName(pill, "Mode");
        Grid.SetColumn(pill, 1);
        bar.Children.Add(pill);

        m.UpdateSlot.HorizontalAlignment = HorizontalAlignment.Right;
        m.UpdateSlot.Margin = new Thickness(0, 0, 10, 0);
        var right = Ui.Row(m.UpdateSlot, _settings);
        right.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(right, 2);
        bar.Children.Add(right);

        var header = new Border { Background = Ui.Res("CardBrush"), BorderBrush = Ui.Res("BorderBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(24, 12, 24, 12), Child = bar };
        DockPanel.SetDock(header, Dock.Top);
        _root.Children.Add(header);

        _tabs.Margin = new Thickness(24, 12, 24, 0);
        DockPanel.SetDock(_tabs, Dock.Top);
        _root.Children.Add(_tabs);

        m.Page.Margin = new Thickness(24, 14, 20, 12);
        _root.Children.Add(m.Page);
    }

    private static Button Segment(string label, bool selected, Action go)
    {
        var b = Ui.Plain(Ui.Text(label, 14, FontWeights.Bold, selected ? Ui.Res("OnAccentBrush") : Ui.Res("InkSubtleBrush"), wrap: false), go, height: 36);
        b.Padding = new Thickness(20, 0, 20, 0);
        if (selected) b.Background = Ui.Res("AccentBrush");
        return Shells.Labelled(b, label + " mode");
    }

    public void Sync()
    {
        _tabs.Children.Clear();
        foreach (var e in _m.NavEntries())
        {
            var tab = e.Tab;
            bool sel = _m.SelectedTab == tab;
            var content = Ui.Row(Ui.Text(e.Label, 18, sel ? FontWeights.ExtraBold : FontWeights.Bold, sel ? Ui.Res("InkBrush") : Ui.Res("InkSubtleBrush"), wrap: false));
            if (e.Count is int n)
            {
                var badge = Ui.Pill(n.ToString(CultureInfo.InvariantCulture), sel ? Ui.Res("AccentInkBrush") : Ui.Res("InkSubtleBrush"), sel ? Ui.Res("AccentSoftBrush") : Ui.Res("NavSelectedBrush"), 12, FontWeights.Bold);
                badge.Margin = new Thickness(8, 0, 0, 0);
                content.Children.Add(badge);
            }
            var b = Ui.Plain(content, () => _m.Go(tab), height: 42);
            b.Padding = new Thickness(2, 0, 2, 0);
            Shells.Labelled(b, e.Label);
            _tabs.Children.Add(new Border { BorderThickness = new Thickness(0, 0, 0, 3), BorderBrush = sel ? Ui.Res("AccentBrush") : Brushes.Transparent, Margin = new Thickness(0, 0, 22, 0), Child = b });
        }
        _settings.Content = Shells.IconSquare(Glyphs.Settings, "Settings", _m.SelectedTab == _m.SettingsTab, () => _m.Go(_m.SettingsTab));
    }

    public bool FocusCommand() => false;
}

// ═════════════════════════ C · Command Dark ═════════════════════════

/// <summary>A dark sidebar (quick views, sections or lists, with counts) and a Ctrl+K command bar that adds to the open screen.</summary>
internal sealed class CommandShell : IShell
{
    private readonly MainWindow _m;
    private readonly StackPanel _side = new();
    private readonly ContentControl _settings = new() { Focusable = false };
    private readonly TextBox _cmd;
    private readonly Grid _root = new();

    public FrameworkElement Root => _root;

    public CommandShell(MainWindow m)
    {
        _m = m;
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(224) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ── sidebar ──
        var side = new DockPanel();
        var title = Ui.Text("Remindly", 15, FontWeights.SemiBold, wrap: false);
        title.Margin = new Thickness(10, 0, 0, 0);
        var brand = Ui.Columns("Auto,*,Auto", Shells.Logo(26), title, Ui.Mono(UpdateService.CurrentDisplayVersion, 11.5));
        brand.Margin = new Thickness(6, 0, 4, 14);
        brand.ToolTip = m.VersionLabel;
        DockPanel.SetDock(brand, Dock.Top);
        side.Children.Add(brand);

        var seg = new UniformGrid { Columns = 2, Margin = new Thickness(2, 0, 2, 16) };
        seg.Children.Add(Segment("Tasks", !m.IsShop, () => m.SetMode("TASK")));
        seg.Children.Add(Segment("Shopping", m.IsShop, () => m.SetMode("SHOP")));
        DockPanel.SetDock(seg, Dock.Top);
        side.Children.Add(seg);

        DockPanel.SetDock(_settings, Dock.Bottom);
        side.Children.Add(_settings);
        side.Children.Add(Ui.Scroll(_side));
        var sideBox = new Border { Background = Ui.Res("NavBrush"), BorderBrush = Ui.Res("BorderBrush"), BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(10, 16, 6, 12), Child = side };
        Grid.SetColumn(sideBox, 0);
        _root.Children.Add(sideBox);

        // ── command bar ──
        _cmd = new TextBox { BorderThickness = new Thickness(0), Padding = new Thickness(4, 0, 4, 0), FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(_cmd, "Command bar");
        Ui.SetPlaceholder(_cmd, m.IsShop ? "Add an item to the open list — type and press Enter" : "Add to the open section — type and press Enter");
        _cmd.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { var t = _cmd.Text; _cmd.Clear(); Shells.CommandAdd(_m, t); e.Handled = true; }
            if (e.Key == Key.Escape) { _cmd.Clear(); e.Handled = true; }
        };
        var bar = new Border
        {
            Background = Ui.Res("InputBrush"),
            BorderBrush = Ui.Res("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Height = 44,
            Padding = new Thickness(12, 0, 10, 0),
            Child = Ui.Columns("Auto,*,Auto", Ui.Glyph(Glyphs.Search, 15), _cmd, Kbd("Ctrl K")),
        };
        m.UpdateSlot.HorizontalAlignment = HorizontalAlignment.Right;
        m.UpdateSlot.Margin = new Thickness(12, 0, 0, 0);
        var top = Ui.Columns("*,Auto", bar, m.UpdateSlot);

        var keys = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var (k, what) in new[] { ("Ctrl K", "command bar"), ("Enter", "add"), ("Ctrl N", "new with details"), ("Ctrl 1–4", "sections"), ("Ctrl M", "Task ⇄ Shop"), ("Double-click", "edit") })
        {
            var hint = Ui.Row(Kbd(k), Ui.Sub(" " + what));
            hint.Margin = new Thickness(0, 0, 18, 0);
            keys.Children.Add(hint);
        }

        var main = new DockPanel { Margin = new Thickness(24, 16, 20, 12) };
        DockPanel.SetDock(top, Dock.Top);
        main.Children.Add(top);
        DockPanel.SetDock(keys, Dock.Bottom);
        main.Children.Add(keys);
        m.Page.Margin = new Thickness(0, 16, 0, 0);
        main.Children.Add(m.Page);
        Grid.SetColumn(main, 1);
        _root.Children.Add(main);
    }

    internal static Border Kbd(string text) => new()
    {
        BorderBrush = Ui.Res("InverseSubBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(5, 1, 5, 1),
        VerticalAlignment = VerticalAlignment.Center,
        Child = Ui.Mono(text, 11.5, Ui.Res("InkBrush")),
    };

    private static Button Segment(string label, bool selected, Action go)
    {
        var b = Ui.Plain(Ui.Text(label, 13, selected ? FontWeights.SemiBold : FontWeights.Normal, selected ? Ui.Res("InkBrush") : Ui.Res("InkSubtleBrush"), wrap: false), go, height: 32);
        b.HorizontalContentAlignment = HorizontalAlignment.Center;
        b.BorderThickness = new Thickness(1);
        b.BorderBrush = selected ? Ui.Res("AccentBrush") : Ui.Res("BorderBrush");
        b.Background = selected ? Ui.Res("AccentSoftBrush") : Brushes.Transparent;
        b.Margin = new Thickness(2, 0, 2, 0);
        return Shells.Labelled(b, label + " mode");
    }

    private static TextBlock Caption(string text)
    {
        var t = Ui.Text(text.ToUpperInvariant(), 11.5, FontWeights.SemiBold, Ui.Res("InkHintBrush"), wrap: false);
        t.Margin = new Thickness(10, 12, 0, 6);
        return t;
    }

    private static Button Entry(string label, string? count, bool selected, Action go, Brush? dot = null)
    {
        var parts = new List<UIElement>();
        if (dot != null) parts.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = dot, Margin = new Thickness(0, 0, 10, 0) });
        else parts.Add(new Border());
        parts.Add(Ui.Text(label, 14, selected ? FontWeights.SemiBold : FontWeights.Normal, selected ? Ui.Res("InkBrush") : Ui.Res("InkSubtleBrush"), wrap: false));
        parts.Add(count == null ? new TextBlock() : Ui.Mono(count, 12));
        var b = Ui.Plain(Ui.Columns("Auto,*,Auto", parts.ToArray()), go, height: 36);
        if (selected) b.Background = Ui.Res("NavSelectedBrush");
        b.Margin = new Thickness(0, 0, 0, 1);
        return Shells.Labelled(b, label);
    }

    public void Sync()
    {
        var state = AppState.Current;
        long now = state.Now;
        _side.Children.Clear();
        var sel = _m.SelectedTab;
        if (!_m.IsShop)
        {
            _side.Children.Add(Caption("Views"));
            var tasks = state.LiveItems(Tab.TASKS).ToList();
            foreach (var f in ItemsView.QuickViews)
            {
                var code = f.Code;
                int n = tasks.Count(i => ItemsView.Matches(code, i, now));
                _side.Children.Add(Entry(f.Label, n.ToString(CultureInfo.InvariantCulture), sel == _m.TasksTab && ItemsView.CommandView == code,
                    () => { ItemsView.CommandView = code; _m.Go(_m.TasksTab); }, Theme.Brush(f.Dot)));
            }
            _side.Children.Add(Caption("Sections"));
            foreach (var e in _m.NavEntries())
            {
                var tab = e.Tab;
                bool selected = sel == tab && (tab != _m.TasksTab || ItemsView.CommandView == ItemsView.AllView);
                _side.Children.Add(Entry(e.Label, e.Count?.ToString(CultureInfo.InvariantCulture), selected, () =>
                {
                    if (tab == _m.TasksTab) ItemsView.CommandView = ItemsView.AllView;
                    _m.Go(tab);
                }));
            }
        }
        else
        {
            _side.Children.Add(Caption("Sections"));
            foreach (var e in _m.NavEntries())
            {
                var tab = e.Tab;
                bool selected = sel == tab && (tab != _m.BuyTab || _m.Buy.OpenListId == null);
                _side.Children.Add(Entry(e.Label, e.Count?.ToString(CultureInfo.InvariantCulture), selected, () =>
                {
                    _m.Go(tab);
                    if (tab == _m.BuyTab) _m.Buy.BackToLists();
                }));
            }
            _side.Children.Add(Caption("Lists"));
            var open = _m.Buy.OpenListId;
            void ListEntry(string name, long id, int count) =>
                _side.Children.Add(Entry(name, count.ToString(CultureInfo.InvariantCulture), sel == _m.BuyTab && open == id, () => { _m.Go(_m.BuyTab); _m.Buy.OpenList(id); }));
            ListEntry("Buy now (all)", ShopLists.BuyNowListId, state.LiveItems(Tab.SHOP).Count(i => !i.Done));
            foreach (var l in ShopLists.Sorted(state.Settings.ShopLists, state.Settings.ListSort, x => x.UpdatedAt))
                ListEntry(l.Name, l.Id, state.ItemsIn(l.Id).Count(i => !i.Done));
            int unsorted = state.ItemsIn(ShopLists.UnsortedListId).Count(i => !i.Done);
            if (unsorted > 0) ListEntry("Unsorted", ShopLists.UnsortedListId, unsorted);
        }
        _settings.Content = Entry("Settings", null, sel == _m.SettingsTab, () => _m.Go(_m.SettingsTab));
    }

    public bool FocusCommand()
    {
        _cmd.Focus();
        _cmd.SelectAll();
        return true;
    }
}

// ═════════════════════════ D · Today Hub ═════════════════════════

/// <summary>A light top row: logo, a pill menu (Today / Overview first), the Task/Shop pill, Settings and the update button.</summary>
internal sealed class HubShell : IShell
{
    private readonly MainWindow _m;
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
    private readonly ContentControl _settings = new() { Focusable = false };
    private readonly DockPanel _root = new() { Margin = new Thickness(28, 18, 24, 0) };

    public FrameworkElement Root => _root;

    public HubShell(MainWindow m)
    {
        _m = m;
        var bar = Ui.Columns("Auto,*,Auto");
        var name = Ui.Stack(Ui.Text("Remindly", 15, FontWeights.ExtraBold, wrap: false), Ui.Sub($"v{UpdateService.CurrentDisplayVersion} · {InstallInfo.ModeLabel}"));
        name.Margin = new Thickness(10, 0, 18, 0);
        var nav = new Border { Background = Ui.Res("CardBrush"), BorderBrush = Ui.Res("BorderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(4), Child = _tabs, VerticalAlignment = VerticalAlignment.Center };
        var left = Ui.Row(Shells.Logo(40), name, nav);
        Grid.SetColumn(left, 0);
        bar.Children.Add(left);

        var mode = new Border
        {
            Background = Ui.Res("AccentSoftBrush"),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(4),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Ui.Row(Segment("Tasks", !m.IsShop, () => m.SetMode("TASK")), Segment("Shopping", m.IsShop, () => m.SetMode("SHOP"))),
        };
        AutomationProperties.SetName(mode, "Mode");
        m.UpdateSlot.HorizontalAlignment = HorizontalAlignment.Right;
        m.UpdateSlot.Margin = new Thickness(10, 0, 0, 0);
        var right = Ui.Row(mode, _settings, m.UpdateSlot);
        right.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(right, 2);
        bar.Children.Add(right);
        DockPanel.SetDock(bar, Dock.Top);
        _root.Children.Add(bar);

        m.Page.Margin = new Thickness(0, 18, 0, 16);
        _root.Children.Add(m.Page);
    }

    private static Button Segment(string label, bool selected, Action go)
    {
        var b = Ui.Plain(Ui.Text(label, 14, selected ? FontWeights.Bold : FontWeights.SemiBold, selected ? Ui.Res("InkBrush") : Ui.Res("InkSubtleBrush"), wrap: false), go, height: 36);
        b.Padding = new Thickness(16, 0, 16, 0);
        if (selected) b.Background = Ui.Res("CardBrush");
        return Shells.Labelled(b, label + " mode");
    }

    public void Sync()
    {
        _tabs.Children.Clear();
        foreach (var e in _m.NavEntries())
        {
            var tab = e.Tab;
            bool sel = _m.SelectedTab == tab;
            var b = Ui.Plain(Ui.Text(e.Label, 14, sel ? FontWeights.Bold : FontWeights.SemiBold, sel ? Ui.Res("InverseInkBrush") : Ui.Res("InkSubtleBrush"), wrap: false), () => _m.Go(tab), height: 36);
            b.Padding = new Thickness(16, 0, 16, 0);
            b.Margin = new Thickness(0, 0, 2, 0);
            if (sel) b.Background = Ui.Res("InverseBrush");
            _tabs.Children.Add(Shells.Labelled(b, e.Label));
        }
        _settings.Content = Shells.IconSquare(Glyphs.Settings, "Settings", _m.SelectedTab == _m.SettingsTab, () => _m.Go(_m.SettingsTab), 44);
    }

    public bool FocusCommand() => false;
}
