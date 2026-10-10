using Remindly.Core;
using Xunit;

namespace Remindly.Core.Tests;

/// <summary>Settings → Appearance (A Fluent · B Day Board · C Command Dark · D Today Hub) and the data behind each design.</summary>
public class DesignTests
{
    private static Item Task(string title, long? due, bool hasTime = true, bool done = false, Priority? pri = null) =>
        new() { Id = Ids.Next(), Tab = Tab.TASKS, Title = title, DueAt = due, DueHasTime = hasTime, Done = done, Priority = pri, CreatedAt = 1, UpdatedAt = 1 };

    [Fact]
    public void Default_is_A_and_unknown_codes_fall_back()
    {
        Assert.Equal(Designs.Fluent, new AppSettings().Design);
        Assert.Equal("A", Designs.Info(null).Letter);
        Assert.Equal(Designs.Hub, Designs.Normalize(" hub "));
        Assert.Equal(Designs.Fluent, Designs.Normalize("NEON"));
        Assert.Equal(new[] { "A", "B", "C", "D" }, Designs.All.Select(d => d.Letter).ToArray());
    }

    [Fact]
    public void Heal_normalises_the_design()
    {
        var d = new RemindlyData { Settings = new AppSettings { Design = "bogus" } };
        Assert.Equal(Designs.Fluent, DataStore.Heal(d, 10).Settings.Design);
        var c = new RemindlyData { Settings = new AppSettings { Design = "command" } };
        Assert.Equal(Designs.Command, DataStore.Heal(c, 10).Settings.Design);
    }

    [Fact]
    public void Design_survives_save_round_trip_and_is_not_taken_from_an_import()
    {
        var current = DataStore.Heal(new RemindlyData { Settings = new AppSettings { Design = Designs.Board } }, 1727600000000);
        var json = Json.Serialize(current);
        Assert.Equal(Designs.Board, Json.Deserialize(json)!.Settings.Design);

        var incoming = DataStore.Heal(new RemindlyData { Settings = new AppSettings { Design = Designs.Hub } }, 1727600000000);
        DataStore.MergeInto(current, incoming, 1727600000000);
        Assert.Equal(Designs.Board, current.Settings.Design);
    }

    [Fact]
    public void Board_has_four_columns_in_order_and_later_holds_undated()
    {
        long now = T.At(2026, 10, 9, 13, 0);
        var items = new[]
        {
            Task("late", T.At(2026, 10, 8, 18, 0)),
            Task("today", T.At(2026, 10, 9, 19, 0)),
            Task("earlier today", T.At(2026, 10, 9, 9, 0)),
            Task("tomorrow", T.At(2026, 10, 10, 6, 30)),
            Task("next week", T.At(2026, 10, 15, 10, 0)),
            Task("someday", null),
        };
        var cols = DesktopViews.BoardColumns(items, now, T.Zone);
        Assert.Equal(new[] { "overdue", "today", "tomorrow", "later" }, cols.Select(c => c.Key).ToArray());
        Assert.Equal(new[] { "late" }, cols[0].Items.Select(i => i.Title).ToArray());
        Assert.Equal(new[] { "earlier today", "today" }, cols[1].Items.Select(i => i.Title).ToArray());
        Assert.Equal(new[] { "tomorrow" }, cols[2].Items.Select(i => i.Title).ToArray());
        Assert.Equal(new[] { "next week", "someday" }, cols[3].Items.Select(i => i.Title).ToArray());

        var empty = DesktopViews.BoardColumns(Array.Empty<Item>(), now, T.Zone);
        Assert.Equal(4, empty.Count);
        Assert.All(empty, c => Assert.Empty(c.Items));
    }

    [Fact]
    public void Move_to_today_keeps_the_time_of_day_and_clears_snooze()
    {
        long now = T.At(2026, 10, 9, 13, 0);
        var late = Task("bill", T.At(2026, 10, 7, 18, 30)) with { SnoozedUntil = T.At(2026, 10, 7, 19, 0) };
        var moved = DesktopViews.MoveToToday(late, now, 9, T.Zone);
        Assert.Equal(new DateTime(2026, 10, 9, 18, 30, 0), T.Local(moved.DueAt!.Value));
        Assert.True(moved.DueHasTime);
        Assert.Null(moved.SnoozedUntil);

        var dateOnly = DesktopViews.MoveToToday(Task("date only", T.At(2026, 10, 1, 9, 0), hasTime: false), now, 9, T.Zone);
        Assert.False(dateOnly.DueHasTime);
        Assert.Equal(new DateTime(2026, 10, 9), T.Local(dateOnly.DueAt!.Value).Date);

        var undated = DesktopViews.MoveToToday(Task("undated", null), now, 9, T.Zone);
        Assert.Equal(new DateTime(2026, 10, 9, 9, 0, 0), T.Local(undated.DueAt!.Value));
    }

    [Fact]
    public void Week_strip_counts_open_items_for_seven_days_from_today()
    {
        long now = T.At(2026, 10, 9, 13, 0);
        var items = new[]
        {
            Task("overdue", T.At(2026, 10, 8, 9, 0)),
            Task("a", T.At(2026, 10, 9, 9, 0)),
            Task("b", T.At(2026, 10, 9, 20, 0)),
            Task("done", T.At(2026, 10, 9, 21, 0), done: true),
            Task("c", T.At(2026, 10, 15, 8, 0)),
            Task("too far", T.At(2026, 10, 16, 8, 0)),
        };
        var week = DesktopViews.WeekStrip(items, now, T.Zone);
        Assert.Equal(7, week.Count);
        Assert.Equal(new DateTime(2026, 10, 9), week[0].Day);
        Assert.Equal(new[] { 2, 0, 0, 0, 0, 0, 1 }, week.Select(w => w.Count).ToArray());
    }

    [Fact]
    public void Due_today_or_earlier_is_open_items_before_tomorrow()
    {
        long now = T.At(2026, 10, 9, 13, 0);
        var items = new[] { Task("t2", T.At(2026, 10, 9, 19, 0)), Task("old", T.At(2026, 10, 1, 9, 0)), Task("tomorrow", T.At(2026, 10, 10, 0, 0)), Task("x", null) };
        Assert.Equal(new[] { "old", "t2" }, DesktopViews.DueTodayOrEarlier(items, now, T.Zone).Select(i => i.Title).ToArray());
    }

    [Fact]
    public void Lowest_prices_compare_latest_unit_price_per_shop_with_the_same_unit()
    {
        PricePoint P(string shop, double unitPrice, long at, string unit = "kg") => new() { Shop = shop, UnitPrice = unitPrice, At = at, Unit = unit, Price = unitPrice, Qty = 1 };
        var rice = T.Buy("Basmati rice", done: true) with { PriceHistory = new() { P("D-Mart", 95, 1), P("Kirana", 110, 2), P("D-Mart", 90, 3) } };
        var rice2 = T.Buy("basmati rice ") with { PriceHistory = new() { P("Reliance", 99, 4) } };
        var oil = T.Buy("Oil") with { PriceHistory = new() { P("D-Mart", 150, 1, "L"), P("Kirana", 152, 2, "L") } };
        var single = T.Buy("Eggs") with { PriceHistory = new() { P("Kirana", 7, 1, "pcs") } };
        var mixedUnits = T.Buy("Sugar") with { PriceHistory = new() { P("D-Mart", 45, 1, "kg"), P("Kirana", 1, 2, "g") } };

        var r = DesktopViews.LowestPrices(new[] { rice, rice2, oil, single, mixedUnits });
        Assert.Equal(2, r.Count);
        Assert.Equal("Basmati rice", r[0].Product, ignoreCase: true);
        Assert.Equal("D-Mart", r[0].BestShop);
        Assert.Equal(90, r[0].BestUnitPrice);
        Assert.Equal("Kirana", r[0].DearShop);
        Assert.Equal(110, r[0].DearUnitPrice);
        Assert.Equal("Oil", r[1].Product);
        Assert.Equal("L", r[1].Unit);
    }

    [Fact]
    public void Greeting_follows_the_clock()
    {
        Assert.Equal("Good morning", DesktopViews.Greeting(T.At(2026, 10, 9, 8, 0), T.Zone));
        Assert.Equal("Good afternoon", DesktopViews.Greeting(T.At(2026, 10, 9, 13, 0), T.Zone));
        Assert.Equal("Good evening", DesktopViews.Greeting(T.At(2026, 10, 9, 19, 0), T.Zone));
    }
}
