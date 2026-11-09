using System.Text.Json.Nodes;
using Remindly.Core;
using Remindly.Core.Sync;
using Xunit;

namespace Remindly.Core.Tests;

/// <summary>The phone's settings on the PC: same defaults, same per-tab inherit rules as Android (Model.kt).</summary>
public class PhonePrefsTests
{
    private static PhonePrefs P(string json) => PhonePrefs.FromJson(JsonNode.Parse(json) as JsonObject, "sync", 1);

    [Fact]
    public void Defaults_match_a_fresh_phone()
    {
        var p = new PhonePrefs();
        Assert.Equal(3, p.DelaySec(Tab.TASKS));
        Assert.Equal(3, p.CallsDelaySec);
        Assert.Equal(0, p.ClearDays(Tab.TASKS));
        Assert.False(p.ConfirmDelete(Tab.TASKS));
        Assert.Equal(90, p.SnoozeMinutes);
        Assert.Equal(18 * 60, p.DueMinuteOfDay);
        Assert.Equal("DATE", p.Sort(Tab.TASKS));
        Assert.True(p.AlertsOn(Tab.LEARN));
        Assert.True(p.AlertsOn(null));
        Assert.Equal(new CardParts(true, true, true, true, false), p.Card(Tab.TASKS));
        Assert.False(p.ShowPriority(Priority.MEDIUM, Tab.TASKS));
        Assert.True(p.ShowPriority(Priority.LOW, Tab.TASKS));
        Assert.Equal("ONCE", p.NewSchedule(Tab.TASKS));
    }

    [Fact]
    public void Missing_or_malformed_keys_keep_the_phone_defaults()
    {
        var p = P("""{"moveDelaySec":"x","tasksMoveDelaySec":null,"deleteStyle":"MAYBE","timeFormat":7,"snoozeM1":true,"tasksSort":"WEIRD"}""");
        Assert.Equal(5, p.MoveDelaySec);
        Assert.Equal(3, p.DelaySec(Tab.TASKS));
        Assert.Equal("UNDO", p.DeleteStyle);
        Assert.Equal("PHONE", p.TimeFormat);
        Assert.Equal(90, p.SnoozeMinutes);
        Assert.Equal("DATE", p.Sort(Tab.TASKS));
        Assert.Equal("sync", p.Source);
        Assert.Equal("UNDO", PhonePrefs.FromJson("not json", "sync", 1).DeleteStyle);
    }

    [Fact]
    public void Delay_inherits_the_global_when_the_tab_is_negative_and_is_clamped()
    {
        var p = P("""{"moveDelaySec":7,"tasksMoveDelaySec":-1,"shopMoveDelaySec":0,"learnMoveDelaySec":-5,"callsMoveDelaySec":900}""");
        Assert.Equal(7, p.DelaySec(Tab.TASKS));
        Assert.Equal(0, p.DelaySec(Tab.SHOP));
        Assert.Equal(7, p.DelaySec(Tab.LEARN));
        Assert.Equal(300, p.CallsDelaySec);
    }

    [Fact]
    public void Auto_clear_days_inherit_like_the_delay()
    {
        var p = P("""{"globalDoneClearDays":30,"tasksDoneClearDays":-1,"learnDoneClearDays":7,"callsDoneClearDays":0}""");
        Assert.Equal(30, p.ClearDays(Tab.TASKS));
        Assert.Equal(7, p.ClearDays(Tab.LEARN));
        Assert.Equal(0, p.ClearDays(null));
    }

    [Fact]
    public void Delete_style_uses_the_tab_override_map_then_the_global()
    {
        var p = P("""{"deleteStyle":"CONFIRM","tabGestureOv":{"tDeleteStyle":"UNDO","cDeleteStyle":"CONFIRM"},"tDeleteStyle":"CONFIRM"}""");
        Assert.False(p.ConfirmDelete(Tab.TASKS));      // the legacy tDeleteStyle field is not read, as on the phone
        Assert.True(p.ConfirmDelete(Tab.LEARN));       // no override: the global
        Assert.True(p.ConfirmDelete(null));
    }

    [Fact]
    public void Card_details_take_on_off_overrides_and_calls_never_show_the_reminder_chip()
    {
        var p = P("""{"cardShowDateTime":false,"cardShowAlertType":true,"tabCardOv":{"lCardDateTime":"ON","tCardCheckbox":"OFF","cCardAlertType":"ON","tCardAlertStyle":"TEXT","tCardAlertColorA":"#123456","sCardAlertColorR":"bad"}}""");
        Assert.Equal(new CardParts(true, false, false, true, true), p.Card(Tab.TASKS));
        Assert.True(p.Card(Tab.LEARN).DateTime);
        Assert.False(p.Card(null).AlertType);
        Assert.Equal("TEXT", p.AlertStyle(Tab.TASKS));
        Assert.Equal("ICON", p.AlertStyle(Tab.LEARN));
        Assert.Equal("#123456", p.AlertColor(Tab.TASKS, 'A'));
        Assert.Equal("#EF6C00", p.AlertColor(Tab.SHOP, 'R'));
    }

    [Fact]
    public void Medium_tag_follows_the_tab_then_the_global()
    {
        var p = P("""{"showMediumTag":true,"tasksShowMediumTag":0,"learnShowMediumTag":-1,"shopShowMediumTag":1}""");
        Assert.False(p.ShowPriority(Priority.MEDIUM, Tab.TASKS));
        Assert.True(p.ShowPriority(Priority.MEDIUM, Tab.LEARN));
        Assert.True(p.ShowPriority(Priority.MEDIUM, Tab.SHOP));
        Assert.True(p.ShowPriority(Priority.URGENT, Tab.TASKS));
        Assert.False(p.ShowPriority(null, Tab.TASKS));
    }

    [Fact]
    public void Alerts_switch_per_tab()
    {
        var p = P("""{"alertsEnabled":false,"tasksAlertsOn":"ON","callsAlertsOn":"INHERIT"}""");
        Assert.True(p.AlertsOn(Tab.TASKS));
        Assert.False(p.AlertsOn(Tab.LEARN));
        Assert.False(p.AlertsOn(null));
    }

    [Fact]
    public void Time_pattern_follows_the_phone_or_windows()
    {
        Assert.Equal("h:mm tt", P("""{"timeFormat":"H12"}""").TimePattern(true));
        Assert.Equal("HH:mm", P("""{"timeFormat":"H24"}""").TimePattern(false));
        Assert.Equal("HH:mm", new PhonePrefs().TimePattern(true));
        Assert.Equal("h:mm tt", new PhonePrefs().TimePattern(false));
    }

    [Fact]
    public void New_due_follows_mode_days_and_time_like_defaultNewDue()
    {
        long now = T.At(2026, 11, 9, 19, 30);
        var tomorrow = new PhonePrefs().NewDue(Tab.TASKS, now, T.Zone);
        Assert.Equal(T.At(2026, 11, 10, 10, 0), tomorrow.DueAt);
        Assert.True(tomorrow.HasTime);

        var p = P("""{"tasksNewDueMode":"INHERIT","globalNewDueMode":"IN_N","globalNewDueDays":5,"globalNewDueMinutes":540,"learnNewDueMode":"TODAY","learnNewDueMinutes":600,"learnNewDueTimed":"OFF","defaultDueMinutes":1200,"shopNewDueMode":"OFF"}""");
        Assert.Equal(T.At(2026, 11, 14, 9, 0), p.NewDue(Tab.TASKS, now, T.Zone).DueAt);
        var learn = p.NewDue(Tab.LEARN, now, T.Zone);
        Assert.Equal(T.At(2026, 11, 9, 20, 0), learn.DueAt);   // date-only: the phone's ring time
        Assert.False(learn.HasTime);
        Assert.Null(p.NewDue(Tab.SHOP, now, T.Zone).DueAt);
    }

    [Fact]
    public void New_schedule_never_starts_a_buy_item_with_no_date()
    {
        var p = P("""{"schedDefault":"NONE","learnSchedDefault":"REPEAT"}""");
        Assert.Equal("NONE", p.NewSchedule(Tab.TASKS));
        Assert.Equal("REPEAT", p.NewSchedule(Tab.LEARN));
        Assert.Equal("ONCE", p.NewSchedule(Tab.SHOP));
    }

    [Fact]
    public void Sort_shows_calendar_as_date_and_reads_group_priority_none()
    {
        Assert.Equal("DATE", P("""{"tasksSort":"CALENDAR"}""").Sort(Tab.TASKS));
        Assert.Equal("GROUP", P("""{"learnSort":"GROUP"}""").Sort(Tab.LEARN));
        Assert.Equal("NONE", P("""{"tasksSort":"NONE"}""").Sort(Tab.TASKS));
    }

    [Fact]
    public void Sync_reads_the_phone_settings_but_never_uploads_them()
    {
        var doc = new CloudDocument("app", """{"taskLists":[],"shopLists":[],"moveDelaySec":1,"tasksMoveDelaySec":-1,"snoozeM1":30,"settingsUpdatedAt":500}""", 400, CloudWire.Schema);
        var plan = CloudListSettings.Reconcile(doc, new AppSettings(), 900);
        Assert.Null(plan.Upload);
        Assert.Equal("sync", plan.Incoming.Phone.Source);
        Assert.Equal(500, plan.Incoming.Phone.SyncedAt);
        Assert.Equal(1, plan.Incoming.Phone.DelaySec(Tab.TASKS));
        Assert.Equal(30, plan.Incoming.Phone.SnoozeMinutes);
    }

    [Fact]
    public void An_android_backup_brings_its_settings_and_a_newer_copy_wins()
    {
        var android = DataStore.Parse("""{"version":2,"app":"Remindly","exportedAt":700,"items":[],"settings":{"ver":44,"snoozeM1":15,"badges":false}}""")!;
        Assert.Equal("backup", android.Settings.Phone.Source);
        Assert.Equal(15, android.Settings.Phone.SnoozeMinutes);

        var current = new RemindlyData { Settings = new AppSettings { Phone = new PhonePrefs { Source = "sync", SyncedAt = 900, SnoozeM1 = 60 } } };
        DataStore.MergeInto(current, android, 1000);
        Assert.Equal(60, current.Settings.Phone.SnoozeMinutes);          // the older backup does not replace a newer sync

        var fresh = new RemindlyData();
        DataStore.MergeInto(fresh, android, 1000);
        Assert.Equal(15, fresh.Settings.Phone.SnoozeMinutes);
        Assert.False(fresh.Settings.Phone.Badges);
    }

    [Fact]
    public void Alerts_off_on_the_phone_are_dropped_not_postponed()
    {
        long now = T.At(2026, 11, 9, 12);
        var data = new RemindlyData
        {
            Items = { new Item { Id = 1, Tab = Tab.TASKS, Title = "a", DueAt = now - 60_000 }, new Item { Id = 2, Tab = Tab.LEARN, Title = "b", DueAt = now - 60_000 } },
            Settings = new AppSettings { Phone = new PhonePrefs { LearnAlertsOn = "OFF" } },
        };
        var muted = new List<string>();
        var due = Reminders.Due(data, now, new HashSet<string>(), T.Zone, muted);
        Assert.Single(due);
        Assert.Equal(1, due[0].RecordId);
        Assert.Equal(new[] { Reminders.ItemKey(2, now - 60_000) }, muted);
    }

    [Fact]
    public void Auto_clear_picks_done_records_older_than_the_tab_days_and_keeps_returning_buy_items()
    {
        long now = T.At(2026, 11, 9, 12);
        long old = now - 10 * 86_400_000L;
        var phone = new PhonePrefs { GlobalDoneClearDays = 7, TasksDoneClearDays = -1, ShopDoneClearDays = -1, LearnDoneClearDays = 0, CallsDoneClearDays = -1 };
        var items = new[]
        {
            new Item { Id = 1, Tab = Tab.TASKS, Done = true, DoneAt = old },
            new Item { Id = 2, Tab = Tab.TASKS, Done = true, DoneAt = now - 86_400_000L },
            new Item { Id = 3, Tab = Tab.LEARN, Done = true, DoneAt = old },
            new Item { Id = 4, Tab = Tab.SHOP, Done = true, DoneAt = old, ReturnAt = now + 86_400_000L },
            new Item { Id = 5, Tab = Tab.TASKS, Done = true, DoneAt = old, DeletedAt = old },
            new Item { Id = 6, Tab = Tab.TASKS, Done = false },
        };
        var calls = new[] { new CallReminder { Id = 9, Number = "1", Done = true, DoneAt = old }, new CallReminder { Id = 10, Number = "2", Done = true } };
        var (clearItems, clearCalls) = ItemRules.AutoClearDue(items, calls, phone, now, T.Zone);
        Assert.Equal(new long[] { 1 }, clearItems.Select(i => i.Id));
        Assert.Equal(new long[] { 9 }, clearCalls.Select(c => c.Id));
        Assert.Empty(ItemRules.AutoClearDue(items, calls, new PhonePrefs(), now, T.Zone).Items);
    }

    [Fact]
    public void Sort_groups_like_the_phone()
    {
        long now = T.At(2026, 11, 9, 12);
        var items = new[]
        {
            new Item { Id = 1, Tab = Tab.TASKS, Title = "a", Priority = Priority.LOW, Group = "Home", CreatedAt = 3 },
            new Item { Id = 2, Tab = Tab.TASKS, Title = "b", Priority = Priority.URGENT, Group = "work", CreatedAt = 2 },
            new Item { Id = 3, Tab = Tab.TASKS, Title = "c", Group = " ", CreatedAt = 1 },
        };
        var byPriority = ItemRules.GroupForSort(items, false, now, "PRIORITY", Tab.TASKS, false, T.Zone);
        Assert.Equal(new[] { "Urgent", "Medium", "Low" }, byPriority.Select(g => g.Label));
        var byGroup = ItemRules.GroupForSort(items, false, now, "GROUP", Tab.TASKS, false, T.Zone);
        Assert.Equal(new[] { "Home", "work", "No Group" }, byGroup.Select(g => g.Label));
        var none = ItemRules.GroupForSort(items, false, now, "NONE", Tab.TASKS, false, T.Zone);
        Assert.Equal(new long[] { 3, 2, 1 }, none.Single().Items.Select(i => i.Id));
        var inside = ItemRules.GroupForSort(items, false, now, "GROUP", Tab.TASKS, true, T.Zone);
        Assert.Equal(ItemRules.GroupByDay(items, false, now, T.Zone).Select(g => g.Label), inside.Select(g => g.Label));
        var learn = ItemRules.GroupForSort(new[] { new Item { Id = 7, Tab = Tab.LEARN, Topic = null } }, false, now, "GROUP", Tab.LEARN, false, T.Zone);
        Assert.Equal("No Topic", learn.Single().Label);
    }

    [Fact]
    public void Move_to_today_uses_the_phone_ring_time_for_items_without_a_date()
    {
        long now = T.At(2026, 11, 9, 8);
        var moved = DesktopViews.MoveToTodayAt(new Item { Id = 1, Tab = Tab.TASKS }, now, 18 * 60 + 30, T.Zone);
        Assert.Equal(T.At(2026, 11, 9, 18, 30), moved.DueAt);
        Assert.False(moved.DueHasTime);
    }
}
