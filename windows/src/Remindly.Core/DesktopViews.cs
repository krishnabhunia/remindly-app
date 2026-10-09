namespace Remindly.Core;

/// <summary>A column of the Day Board: Overdue · Today · Tomorrow · Later.</summary>
public sealed record BoardColumn(string Key, string Label, List<Item> Items);

/// <summary>One day of the Today Hub's 7-day strip.</summary>
public sealed record WeekDay(DateTime Day, int Count);

/// <summary>The same product seen at two shops: the cheapest and the dearest latest unit price.</summary>
public sealed record PriceCompare(string Product, string Unit, string BestShop, double BestUnitPrice, string DearShop, double DearUnitPrice)
{
    public double Saving => DearUnitPrice - BestUnitPrice;
}

/// <summary>Pure data shaping for the desktop designs (Day Board, Command views, Today Hub), kept here so it is unit-tested.</summary>
public static class DesktopViews
{
    public const string Overdue = "overdue";
    public const string Today = "today";
    public const string Tomorrow = "tomorrow";
    public const string Later = "later";

    /// <summary>Active items as four columns, always in this order (an empty column stays, so the board keeps its shape).</summary>
    public static List<BoardColumn> BoardColumns(IEnumerable<Item> active, long now, TimeZoneInfo? zone = null)
    {
        var today = Clock.LocalDate(now, zone);
        string todayKey = today.ToString("yyyy-MM-dd"), tomorrowKey = today.AddDays(1).ToString("yyyy-MM-dd");
        var groups = ItemRules.GroupByDay(active, false, now, zone);
        List<Item> Of(Func<DayGroup<Item>, bool> pick) => groups.Where(pick).SelectMany(g => g.Items).ToList();
        return new List<BoardColumn>
        {
            new(Overdue, "Overdue", Of(g => g.Key == "overdue")),
            new(Today, "Today", Of(g => g.Key == todayKey)),
            new(Tomorrow, "Tomorrow", Of(g => g.Key == tomorrowKey)),
            new(Later, "Later", Of(g => g.Key != "overdue" && g.Key != todayKey && g.Key != tomorrowKey)),
        };
    }

    /// <summary>
    /// "Move to today": keeps the item's time of day (a date-only item keeps being date-only) and clears a snooze.
    /// An undated item gets today at <paramref name="defaultHour"/>.
    /// </summary>
    public static Item MoveToToday(Item item, long now, int defaultHour, TimeZoneInfo? zone = null)
    {
        var today = Clock.LocalDate(now, zone);
        long due = item.DueAt is long d
            ? Clock.FromLocal(today + Clock.ToLocal(d, zone).TimeOfDay, zone)
            : Clock.FromLocal(today.AddHours(Math.Clamp(defaultHour, 0, 23)), zone);
        return item with { DueAt = due, DueHasTime = item.DueAt == null ? false : item.DueHasTime, SnoozedUntil = null };
    }

    /// <summary>Open items due on each of the next seven days, starting today (overdue ones are not counted).</summary>
    public static List<WeekDay> WeekStrip(IEnumerable<Item> active, long now, TimeZoneInfo? zone = null)
    {
        var today = Clock.LocalDate(now, zone);
        var counts = active.Where(i => !i.Done && i.DeletedAt == null && i.DueAt != null)
            .GroupBy(i => Clock.LocalDate(i.DueAt!.Value, zone)).ToDictionary(g => g.Key, g => g.Count());
        return Enumerable.Range(0, 7).Select(n => today.AddDays(n)).Select(day => new WeekDay(day, counts.TryGetValue(day, out var c) ? c : 0)).ToList();
    }

    /// <summary>Items due today or earlier that are still open, earliest first.</summary>
    public static List<Item> DueTodayOrEarlier(IEnumerable<Item> active, long now, TimeZoneInfo? zone = null)
    {
        long tomorrow = Clock.StartOfNextDay(now, zone);
        return active.Where(i => !i.Done && i.DeletedAt == null && i.DueAt is long d && d < tomorrow)
            .OrderBy(i => i.DueAt).ThenBy(i => PriorityNames.Rank(i.Priority)).ToList();
    }

    /// <summary>
    /// Products whose latest unit price is known at two or more shops (same unit), biggest saving first. Built only from
    /// the price history Remindly already records when you buy something.
    /// </summary>
    public static List<PriceCompare> LowestPrices(IEnumerable<Item> buyItems, int take = 3)
    {
        var result = new List<PriceCompare>();
        var byProduct = buyItems.Where(i => i.Tab == Tab.SHOP && i.DeletedAt == null && !string.IsNullOrWhiteSpace(i.Title))
            .GroupBy(i => i.Title.Trim().ToLowerInvariant());
        foreach (var product in byProduct)
        {
            var name = product.OrderByDescending(i => i.UpdatedAt).First().Title.Trim();
            var points = product.SelectMany(i => i.PriceHistory ?? new())
                .Where(p => p.UnitPrice > 0 && !string.IsNullOrWhiteSpace(p.Shop))
                .GroupBy(p => (p.Unit ?? "").Trim().ToLowerInvariant());
            foreach (var unit in points)
            {
                var latestPerShop = unit.GroupBy(p => p.Shop.Trim(), StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(p => p.At).First()).ToList();
                if (latestPerShop.Count < 2) continue;
                var best = latestPerShop.OrderBy(p => p.UnitPrice).First();
                var dear = latestPerShop.OrderByDescending(p => p.UnitPrice).First();
                if (dear.UnitPrice <= best.UnitPrice) continue;
                result.Add(new PriceCompare(name, (best.Unit ?? "").Trim(), best.Shop.Trim(), best.UnitPrice, dear.Shop.Trim(), dear.UnitPrice));
            }
        }
        return result.OrderByDescending(p => p.Saving / p.DearUnitPrice).ThenBy(p => p.Product).Take(take).ToList();
    }

    /// <summary>"Good morning" before noon, "Good afternoon" before 5 pm, else "Good evening".</summary>
    public static string Greeting(long now, TimeZoneInfo? zone = null)
    {
        int h = Clock.ToLocal(now, zone).Hour;
        return h < 12 ? "Good morning" : h < 17 ? "Good afternoon" : "Good evening";
    }
}
