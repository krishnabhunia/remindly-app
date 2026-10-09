using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Remindly.App.Services;
using Remindly.Core;
using Remindly.Core.Updates;

namespace Remindly.App.Views;

/// <summary>Shared pieces of the Today Hub (design D) pages.</summary>
internal static class Hub
{
    public static TextBlock Title(string text)
    {
        var t = Ui.Text(text, 18, FontWeights.ExtraBold);
        t.Margin = new Thickness(0, 0, 0, 12);
        return t;
    }

    public static Grid TitleRow(string text, string? link, Action? go)
    {
        var t = Title(text);
        UIElement right = new Border();
        if (link != null && go != null)
        {
            var b = Ui.Plain(Ui.Text(link, 13, FontWeights.SemiBold, Ui.Res("AccentInkBrush"), wrap: false), go, height: 30);
            b.Padding = new Thickness(8, 0, 8, 0);
            b.VerticalAlignment = VerticalAlignment.Top;
            right = b;
        }
        return Ui.Columns("*,Auto", t, right);
    }

    public static Border Tile(UIElement child, bool inverse = false)
    {
        var tile = Ui.Tile(child, inverse ? Ui.Res("InverseBrush") : Ui.Res("CardBrush"), 20);
        tile.Margin = new Thickness(0, 0, 0, 16);
        return tile;
    }

    /// <summary>The page header: date and version, a large sentence, and a quick-add box.</summary>
    public static Grid Header(string sentence, string placeholder, Action<string> add)
    {
        var line = Ui.Text($"{Shells.TodayLong} · Remindly {UpdateService.CurrentDisplayVersion}", 13, FontWeights.SemiBold, Ui.Res("InkSubtleBrush"));
        var big = Ui.Text(sentence, 30, FontWeights.ExtraBold);
        var box = Ui.Input(placeholder: placeholder);
        box.Height = 46;
        box.FontSize = 15;
        box.BorderThickness = new Thickness(0);
        box.VerticalContentAlignment = VerticalAlignment.Center;
        void Go() { var t = box.Text; box.Clear(); add(t); }
        box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { Go(); e.Handled = true; } };
        var addBtn = Ui.Plain(Ui.Glyph(Glyphs.Add, 16, Ui.Res("InverseInkBrush")), Go, "Add", 38);
        addBtn.Width = 38;
        addBtn.HorizontalContentAlignment = HorizontalAlignment.Center;
        addBtn.Padding = new Thickness(0);
        addBtn.Background = Ui.Res("InverseBrush");
        var input = new Border
        {
            Background = Ui.Res("InputBrush"), BorderBrush = Ui.Res("BorderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16),
            Padding = new Thickness(10, 4, 6, 4), Width = 420, VerticalAlignment = VerticalAlignment.Bottom, Child = Ui.Columns("*,Auto", box, addBtn),
        };
        var g = Ui.Columns("*,Auto", Ui.Stack(line, big), input);
        g.Margin = new Thickness(0, 0, 0, 18);
        return g;
    }

    public static Brush AvatarBrush(string name)
    {
        string[] colors = { "#2563EB", "#0F766E", "#7C3AED", "#C2410C", "#BE185D" };
        int h = 0;
        foreach (var ch in name) h = (h * 31 + ch) & 0x7FFFFFFF;
        return Theme.Brush(colors[h % colors.Length]);
    }

    public static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(p => char.IsLetterOrDigit(p[0])).ToList();
        return parts.Count == 0 ? "?" : parts.Count == 1 ? parts[0][..1].ToUpperInvariant() : (parts[0][..1] + parts[^1][..1]).ToUpperInvariant();
    }
}

// ═════════════════════════ Task mode: Today ═════════════════════════

/// <summary>Design D's home in Task mode: due today (with overdue), call-backs, the next 7 days, learning and today's shopping.</summary>
public sealed class TodayHubView : ScrollViewer, IPage
{
    private readonly StackPanel _root = new();

    public TodayHubView()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Padding = new Thickness(0, 0, 8, 0);
        Content = _root;
    }

    public void Refresh()
    {
        var state = AppState.Current;
        long now = state.Now;
        var active = state.LiveItems(Tab.TASKS).Concat(state.LiveItems(Tab.LEARN)).Where(i => !i.Done).ToList();
        var due = DesktopViews.DueTodayOrEarlier(active, now);
        long startToday = Clock.StartOfDay(now);
        var overdue = due.Where(i => i.DueAt < startToday).ToList();
        var today = due.Where(i => i.DueAt >= startToday).ToList();
        _root.Children.Clear();
        var sentence = due.Count == 0 ? $"{DesktopViews.Greeting(now)}. Nothing is due today."
            : $"{DesktopViews.Greeting(now)}. {due.Count} thing{(due.Count == 1 ? "" : "s")} need{(due.Count == 1 ? "s" : "")} you today.";
        _root.Children.Add(Hub.Header(sentence, "What do you need to remember?", t => ItemsView.AddQuick(Tab.TASKS, t)));

        var row1 = Ui.Columns("2*,16,*", DueTile(state, overdue, today, now), new Border(), CallsTile(state, now));
        var row2 = Ui.Columns("*,16,*,16,*", WeekTile(active, now), new Border(), LearnTile(state), new Border(), ShoppingTile(state, now));
        _root.Children.Add(row1);
        _root.Children.Add(row2);
    }

    private static Border DueTile(AppState state, List<Item> overdue, List<Item> today, long now)
    {
        var p = new StackPanel();
        p.Children.Add(Hub.TitleRow("Due today", "All tasks", () => App.Current.Main.Go(App.Current.Main.TasksTab)));
        if (overdue.Count > 0)
        {
            var names = string.Join(" · ", overdue.Take(3).Select(i => i.Title)) + (overdue.Count > 3 ? $" · +{overdue.Count - 3}" : "");
            var move = Ui.Btn("Move to today", () =>
            {
                var snap = state.TakeSnapshot();
                state.UpsertMany(overdue.Select(i => DesktopViews.MoveToToday(i, state.Now, state.Settings.DefaultDueHour)));
                App.Current.Main.Snack($"{overdue.Count} moved to today", () => state.Restore(snap));
            });
            move.Foreground = Ui.Res("DangerBrush");
            move.Margin = new Thickness(8, 0, 0, 0);
            var text = Ui.Text(names, 13, color: Ui.Res("DangerBrush"));
            text.Margin = new Thickness(10, 0, 0, 0);
            p.Children.Add(new Border
            {
                Background = Ui.Res("DangerSoftBrush"), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 8, 8, 8), Margin = new Thickness(0, 0, 0, 6),
                Child = Ui.Columns("Auto,*,Auto", Ui.Text($"{overdue.Count} overdue", 14, FontWeights.Bold, Ui.Res("DangerBrush"), wrap: false), text, move),
            });
        }
        foreach (var i in today.Take(6)) p.Children.Add(Row(i));
        if (today.Count > 6) p.Children.Add(Ui.Sub($"+{today.Count - 6} more today"));
        if (today.Count == 0) { var e = Ui.Sub(overdue.Count > 0 ? "Nothing else is due today." : "Nothing is due today. Add something above."); e.Margin = new Thickness(0, 10, 0, 0); p.Children.Add(e); }
        return Hub.Tile(p);

        static UIElement Row(Item i)
        {
            var time = Ui.Text(i.DueHasTime && i.DueAt is long d ? Clock.ToLocal(d).ToString("HH:mm", CultureInfo.InvariantCulture) : "Today", 15, FontWeights.ExtraBold, wrap: false);
            time.Width = 64;
            var check = Ui.Check(false, on => { if (on) ItemsView.Toggle(i, true); }, "Done");
            var sub = new List<string> { TabNames.Title(i.Tab) };
            if (Recurrence.Label(i) is string rep) sub.Add("repeats " + rep.ToLowerInvariant());
            if (!string.IsNullOrWhiteSpace(i.Notes)) sub.Add(i.Notes.Trim().Replace('\n', ' '));
            var mid = Ui.Stack(Ui.Text(i.Title, 15, FontWeights.Bold), Ui.Sub(string.Join(" · ", sub)));
            UIElement pri = i.Priority is Priority p && p is Priority.HIGH or Priority.URGENT
                ? Ui.Pill(PriorityNames.Label(p), p == Priority.URGENT ? Ui.Res("DangerBrush") : Ui.Res("AmberBrush"), p == Priority.URGENT ? Ui.Res("DangerSoftBrush") : Ui.Res("AmberSoftBrush"), 12, FontWeights.Bold)
                : new Border();
            var b = new Border { BorderBrush = Ui.Res("BorderBrush"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(4, 10, 4, 10), Background = Brushes.Transparent, Child = Ui.Columns("Auto,Auto,*,Auto", time, check, mid, pri) };
            b.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { ItemEditor.Edit(i); e.Handled = true; } };
            return b;
        }
    }

    private static Border CallsTile(AppState state, long now)
    {
        var p = new StackPanel();
        p.Children.Add(Hub.TitleRow("Call back", "All calls", () => App.Current.Main.Go(App.Current.Main.CallsTab)));
        var calls = state.LiveCalls().Where(c => !c.Done).OrderBy(c => c.SnoozedUntil ?? c.RecurAt ?? long.MaxValue).Take(3).ToList();
        foreach (var c in calls)
        {
            var call = c;
            var avatar = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Background = Hub.AvatarBrush(c.Display), Child = Ui.Text(Hub.Initials(c.Display), 14, FontWeights.ExtraBold, Brushes.White, wrap: false) };
            ((TextBlock)avatar.Child).HorizontalAlignment = HorizontalAlignment.Center;
            var when = c.SnoozedUntil is long sn && sn > now ? "Snoozed to " + Clock.FormatTime(sn) : c.RecurAt is long at ? (at < now ? "Due " : "") + Clock.FormatDayTime(at) : c.Number;
            var who = Ui.Stack(Ui.Text(c.Display, 14.5, FontWeights.Bold, wrap: false), Ui.Sub(string.IsNullOrWhiteSpace(c.Note) ? when : when + " · " + c.Note));
            who.Margin = new Thickness(12, 0, 0, 0);
            var callBtn = Ui.Btn("Call", () => InstallInfo.OpenUrl("tel:" + CallText.NormalizePhone(call.Number)), "Opens Phone Link or your calling app");
            callBtn.Background = Ui.Res("InverseBrush");
            callBtn.BorderBrush = Ui.Res("InverseBrush");
            callBtn.Foreground = Ui.Res("InverseInkBrush");
            var wa = Ui.Btn("WhatsApp", () => InstallInfo.OpenUrl(CallText.WhatsAppUrl(call.Number, call.Message)));
            var buttons = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(0, 10, 0, 0) };
            buttons.Children.Add(callBtn);
            buttons.Children.Add(wa);
            wa.Margin = new Thickness(6, 0, 0, 0);
            callBtn.Margin = new Thickness(0);
            p.Children.Add(new Border
            {
                Background = Ui.Res("SurfaceBrush"),
                CornerRadius = new CornerRadius(14), Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 10),
                Child = Ui.Stack(Ui.Columns("Auto,*", avatar, who), buttons),
            });
        }
        if (calls.Count == 0)
        {
            p.Children.Add(Ui.Sub("Nobody to call back."));
            var add = Ui.Btn("+ Call-back", () => CallEditor.OpenFor(null));
            add.HorizontalAlignment = HorizontalAlignment.Left;
            add.Margin = new Thickness(0, 10, 0, 0);
            p.Children.Add(add);
        }
        return Hub.Tile(p);
    }

    private static Border WeekTile(List<Item> active, long now)
    {
        var p = new StackPanel();
        p.Children.Add(Hub.Title("Next 7 days"));
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 7 };
        foreach (var d in DesktopViews.WeekStrip(active, now))
        {
            bool today = d.Day == Clock.LocalDate(now);
            var fg = today ? Ui.Res("OnAccentBrush") : Ui.Res("InkBrush");
            var cell = Ui.Stack(Ui.Text(d.Day.ToString("ddd", CultureInfo.InvariantCulture), 11.5, FontWeights.Bold, fg, wrap: false),
                Ui.Text(d.Day.Day.ToString(CultureInfo.InvariantCulture), 17, FontWeights.ExtraBold, fg, wrap: false),
                Ui.Text(d.Count == 0 ? "·" : d.Count.ToString(CultureInfo.InvariantCulture), 11.5, FontWeights.Bold, fg, wrap: false));
            foreach (var t in cell.Children.OfType<TextBlock>()) t.HorizontalAlignment = HorizontalAlignment.Center;
            grid.Children.Add(new Border { Background = today ? Ui.Res("AccentBrush") : Ui.Res("SurfaceBrush"), CornerRadius = new CornerRadius(12), Padding = new Thickness(0, 8, 0, 8), Margin = new Thickness(2), Child = cell });
        }
        p.Children.Add(grid);
        var next = active.Where(i => i.DueAt is long d && d >= now).OrderBy(i => i.DueAt).FirstOrDefault();
        var line = Ui.Sub(next == null ? "Nothing scheduled ahead." : $"Next up: {next.Title}, {Ui.DueText(next.DueAt, next.DueHasTime, now)}" + (Recurrence.Label(next) is string r ? $" · repeats {r.ToLowerInvariant()}" : ""));
        line.Margin = new Thickness(0, 12, 0, 0);
        p.Children.Add(line);
        return Hub.Tile(p);
    }

    private static Border LearnTile(AppState state)
    {
        var p = new StackPanel();
        p.Children.Add(Hub.TitleRow("Learning", "Learn", () => App.Current.Main.Go(App.Current.Main.LearnTab)));
        var items = state.LiveItems(Tab.LEARN).Where(i => !i.Done).OrderByDescending(i => i.Progress).ThenByDescending(i => i.UpdatedAt).Take(3).ToList();
        foreach (var i in items)
        {
            var head = Ui.Columns("*,Auto", Ui.Text(i.Title, 14, FontWeights.Bold, wrap: false), Ui.Text($"{i.Progress}%", 14, FontWeights.ExtraBold, wrap: false));
            var bar = new ProgressBar { Value = i.Progress, Maximum = 100, Height = 8, Margin = new Thickness(0, 6, 0, 4), Foreground = Ui.Res("AccentBrush"), Background = Ui.Res("SurfaceBrush"), BorderThickness = new Thickness(0) };
            var sub = string.Join(" · ", new[] { i.Platform, i.Topic, i.HoursSpent > 0 ? $"{i.HoursSpent:0.#} h spent" : null }.Where(x => !string.IsNullOrWhiteSpace(x)));
            var s = Ui.Stack(head, bar);
            if (sub.Length > 0) s.Children.Add(Ui.Sub(sub));
            s.Margin = new Thickness(0, 0, 0, 12);
            p.Children.Add(s);
        }
        if (items.Count == 0) p.Children.Add(Ui.Sub("Nothing to learn yet."));
        return Hub.Tile(p);
    }

    private static Border ShoppingTile(AppState state, long now)
    {
        var p = new StackPanel();
        var ink = Ui.Res("InverseInkBrush");
        var sub = Ui.Res("InverseSubBrush");
        var lists = state.Lists;
        var withItems = lists.Select(l => (List: l, Open: state.ItemsIn(l.Id).Where(i => !i.Done).ToList())).ToList();
        var todayList = withItems.FirstOrDefault(x => x.List.ShoppingDay is long d && Clock.LocalDate(d) == Clock.LocalDate(now) && x.Open.Count > 0);
        var pick = todayList.List != null ? todayList : withItems.Where(x => x.Open.Count > 0).OrderByDescending(x => x.Open.Count).FirstOrDefault();
        p.Children.Add(Ui.Text(todayList.List != null ? "Shopping today" : "Shopping", 18, FontWeights.ExtraBold, ink));
        if (pick.List == null)
        {
            var none = Ui.Text("Nothing to buy.", 14, color: sub);
            none.Margin = new Thickness(0, 10, 0, 14);
            p.Children.Add(none);
            p.Children.Add(Open("Go to Buy", () => App.Current.Main.Go(App.Current.Main.BuyTab)));
            return Hub.Tile(p, inverse: true);
        }
        var st = ShopLists.Stats(pick.Open, state.ShopNamesById());
        var name = Ui.Text(pick.List.Name, 26, FontWeights.ExtraBold, ink);
        name.Margin = new Thickness(0, 8, 0, 4);
        p.Children.Add(name);
        var detail = $"{pick.Open.Count} item{(pick.Open.Count == 1 ? "" : "s")}" + (st.Shops.Count > 0 ? " at " + string.Join(", ", st.Shops) : "") + (ShopLists.EstLabel(st.EstTotal) is string e ? " · " + e : "");
        var d2 = Ui.Text(detail, 13.5, color: sub);
        d2.Margin = new Thickness(0, 0, 0, 16);
        p.Children.Add(d2);
        var id = pick.List.Id;
        p.Children.Add(Open("Open list", () => App.Current.Main.OpenList(id)));
        return Hub.Tile(p, inverse: true);

        static Button Open(string text, Action go)
        {
            var b = Ui.Btn(text, go);
            b.Background = Ui.Res("InverseInkBrush");
            b.BorderBrush = Ui.Res("InverseInkBrush");
            b.Foreground = Ui.Res("InverseBrush");
            b.FontWeight = FontWeights.Bold;
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.Margin = new Thickness(0);
            b.Padding = new Thickness(12, 9, 12, 9);
            return b;
        }
    }
}

// ═════════════════════════ Shop mode: Overview ═════════════════════════

/// <summary>Design D's home in Shop mode: list progress rings, Buy now by store, lowest prices seen and the next trip.</summary>
public sealed class ShopHubView : ScrollViewer, IPage
{
    private readonly StackPanel _root = new();

    public ShopHubView()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Padding = new Thickness(0, 0, 8, 0);
        Content = _root;
    }

    public void Refresh()
    {
        var state = AppState.Current;
        long now = state.Now;
        var open = state.LiveItems(Tab.SHOP).Where(i => !i.Done).ToList();
        double est = open.Sum(i => ShopLists.EstPriceOf(i) ?? 0);
        _root.Children.Clear();
        var sentence = open.Count == 0 ? "Nothing to buy right now."
            : $"{open.Count} thing{(open.Count == 1 ? "" : "s")} to buy" + (est > 0 ? $", about {Ui.Rupees(est)}." : ".");
        _root.Children.Add(Hub.Header(sentence, "Add an item — it goes to your default list", t => Shells.CommandAdd(App.Current.Main, t)));

        // list rings
        var shopNames = state.ShopNamesById();
        var lists = ShopLists.Sorted(state.Settings.ShopLists, state.Settings.ListSort, l => l.UpdatedAt).Take(4).ToList();
        if (lists.Count > 0)
        {
            var rings = new System.Windows.Controls.Primitives.UniformGrid { Columns = Math.Max(lists.Count, 2), Margin = new Thickness(0, 0, 0, 0) };
            foreach (var l in lists) rings.Children.Add(RingTile(state, l, shopNames, now));
            _root.Children.Add(rings);
        }

        var row = Ui.Columns("2*,16,*,16,*", StoreTile(open), new Border(), PriceTile(state), new Border(), TripTile(state, now));
        _root.Children.Add(row);
    }

    private static UIElement RingTile(AppState state, ShopList l, Dictionary<long, string> shopNames, long now)
    {
        var items = state.ItemsIn(l.Id);
        var st = ShopLists.Stats(items, shopNames, l.UpdatedAt);
        int total = st.ToBuy + st.Done;
        double frac = total == 0 ? 0 : (double)st.Done / total;
        var ring = Ui.Ring(frac, 64, Ui.Res("AccentBrush"), Ui.Res("SurfaceBrush"), Ui.Text($"{Math.Round(frac * 100)}%", 13.5, FontWeights.ExtraBold, wrap: false));
        var day = l.ShoppingDay is long d ? (Clock.LocalDate(d) == Clock.LocalDate(now) ? "Today" : Clock.ToLocal(d).ToString("ddd", CultureInfo.InvariantCulture)) : "No day";
        var text = Ui.Stack(Ui.Text(l.Name, 16, FontWeights.ExtraBold, wrap: false), Ui.Sub(st.ToBuy == 0 ? "All bought" : $"{st.ToBuy} left"),
            Ui.Text((ShopLists.EstLabel(st.EstTotal) is string e ? e + " · " : "") + day, 13, FontWeights.Bold, wrap: false));
        text.Margin = new Thickness(14, 0, 0, 0);
        var id = l.Id;
        var b = Ui.Plain(Ui.Columns("Auto,*", ring, text), () => App.Current.Main.OpenList(id), "Open " + l.Name);
        b.Padding = new Thickness(18);
        b.Background = Ui.Res("CardBrush");
        var wrap = new Border { CornerRadius = Ui.CardRadius, Margin = new Thickness(0, 0, 16, 16), Child = b, ClipToBounds = false };
        return wrap;
    }

    private static Border StoreTile(List<Item> open)
    {
        var p = new StackPanel();
        p.Children.Add(Hub.TitleRow("Buy now, by store", "All lists", () => App.Current.Main.Go(App.Current.Main.BuyTab)));
        var groups = ShopLists.ShopGroupsOf(open);
        foreach (var (name, items) in groups.Take(6))
        {
            var shop = name;
            double sub = items.Sum(i => ShopLists.EstPriceOf(i) ?? 0);
            var icon = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(12), Background = Ui.Res("AccentSoftBrush"), Child = Ui.Glyph(Glyphs.Shop, 18, Ui.Res("AccentInkBrush")) };
            ((TextBlock)icon.Child).HorizontalAlignment = HorizontalAlignment.Center;
            var mid = Ui.Stack(Ui.Text(name, 15, FontWeights.Bold, wrap: false), Ui.Text(string.Join(", ", items.Select(i => i.Title)), 12.5, color: Ui.Res("InkSubtleBrush"), wrap: false));
            mid.Margin = new Thickness(14, 0, 10, 0);
            var btn = Ui.Btn("Open", () => { App.Current.Main.Go(App.Current.Main.BuyTab); App.Current.Main.Buy.OpenBuyNow(shop); });
            btn.Margin = new Thickness(10, 0, 0, 0);
            var row = Ui.Columns("Auto,*,Auto,Auto", icon, mid, Ui.Text(sub > 0 ? Ui.Rupees(sub) : "", 14, FontWeights.ExtraBold, wrap: false), btn);
            p.Children.Add(new Border { BorderBrush = Ui.Res("BorderBrush"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 10, 0, 10), Child = row });
        }
        if (groups.Count == 0) p.Children.Add(Ui.Sub("Nothing to buy."));
        return Hub.Tile(p);
    }

    private static Border PriceTile(AppState state)
    {
        var p = new StackPanel();
        p.Children.Add(Hub.Title("Cheapest where"));
        var prices = DesktopViews.LowestPrices(state.LiveItems(Tab.SHOP));
        foreach (var c in prices)
        {
            string per = c.Unit.Length > 0 ? "/" + c.Unit : "";
            var line = Ui.Columns("*,Auto", Ui.Text($"{c.BestShop} {Ui.Rupees(c.BestUnitPrice)}{per}", 13, FontWeights.Bold, Ui.Res("SuccessBrush"), wrap: false),
                Ui.Text($"{c.DearShop} {Ui.Rupees(c.DearUnitPrice)}{per}", 13, color: Ui.Res("InkSubtleBrush"), wrap: false));
            p.Children.Add(new Border { Background = Ui.Res("SurfaceBrush"), CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 10), Child = Ui.Stack(Ui.Text(c.Product, 14, FontWeights.Bold), line) });
        }
        if (prices.Count == 0) p.Children.Add(Ui.Sub("Prices appear here once you have bought the same thing at two shops (Remindly keeps the price each time you tick an item with a price)."));
        return Hub.Tile(p);
    }

    private static Border TripTile(AppState state, long now)
    {
        var p = new StackPanel();
        var ink = Ui.Res("InverseInkBrush");
        var sub = Ui.Res("InverseSubBrush");
        long today = Clock.StartOfDay(now);
        var candidates = state.Lists.Select(l => (List: l, Open: state.ItemsIn(l.Id).Where(i => !i.Done).ToList())).Where(x => x.Open.Count > 0).ToList();
        var next = candidates.Where(x => x.List.ShoppingDay is long d && d >= today).OrderBy(x => x.List.ShoppingDay).FirstOrDefault();
        if (next.List == null) next = candidates.OrderByDescending(x => x.Open.Count).FirstOrDefault();
        p.Children.Add(Ui.Text("Next trip", 18, FontWeights.ExtraBold, ink));
        if (next.List == null)
        {
            var none = Ui.Text("No list has anything to buy.", 14, color: sub);
            none.Margin = new Thickness(0, 10, 0, 0);
            p.Children.Add(none);
            return Hub.Tile(p, inverse: true);
        }
        var st = ShopLists.Stats(next.Open, state.ShopNamesById());
        var when = next.List.ShoppingDay is long sd ? (Clock.LocalDate(sd) == Clock.LocalDate(now) ? "today" : Clock.ToLocal(sd).ToString("dddd", CultureInfo.InvariantCulture)) : "no day set";
        var title = Ui.Text($"{next.List.Name}, {when}", 24, FontWeights.ExtraBold, ink);
        title.Margin = new Thickness(0, 8, 0, 4);
        p.Children.Add(title);
        var d2 = Ui.Text($"{next.Open.Count} item{(next.Open.Count == 1 ? "" : "s")}" + (st.Shops.Count > 0 ? " at " + string.Join(", ", st.Shops) : "") + (ShopLists.EstLabel(st.EstTotal) is string e ? " · " + e : ""), 13.5, color: sub);
        d2.Margin = new Thickness(0, 0, 0, 16);
        p.Children.Add(d2);
        var id = next.List.Id;
        var wa = Ui.Btn("Send to WhatsApp", () => ShareWindow.WhatsApp(id));
        wa.Background = ink;
        wa.BorderBrush = ink;
        wa.Foreground = Ui.Res("InverseBrush");
        wa.FontWeight = FontWeights.Bold;
        wa.Margin = new Thickness(0);
        wa.Padding = new Thickness(12, 9, 12, 9);
        p.Children.Add(wa);
        return Hub.Tile(p, inverse: true);
    }
}
