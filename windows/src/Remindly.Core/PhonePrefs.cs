using System.Text.Json;
using System.Text.Json.Nodes;

namespace Remindly.Core;

/// <summary>Which parts of a card the phone shows (Android cardFieldsFor).</summary>
public sealed record CardParts(bool Priority, bool DateTime, bool Checkbox, bool Repeat, bool AlertType);

/// <summary>
/// The Android app's own settings that Windows follows (approved S1–S14, S16). Read-only on the PC: they come from the
/// phone's synced settings document or an Android backup, and Windows never writes them back. Field names and
/// defaults are the Kotlin AppSettings ones; every resolver mirrors the Android function named in its comment.
/// Unknown or malformed values fall back to the phone's own defaults.
/// </summary>
public sealed record PhonePrefs
{
    /// <summary>"" = the phone's defaults (never synced), "sync" or "backup".</summary>
    public string Source { get; init; } = "";
    public long SyncedAt { get; init; }

    public int MoveDelaySec { get; init; } = 5;
    public int TasksMoveDelaySec { get; init; } = 3;
    public int ShopMoveDelaySec { get; init; } = 3;
    public int LearnMoveDelaySec { get; init; } = 3;
    public int CallsMoveDelaySec { get; init; } = 3;

    public int GlobalDoneClearDays { get; init; }
    public int TasksDoneClearDays { get; init; }
    public int ShopDoneClearDays { get; init; }
    public int LearnDoneClearDays { get; init; }
    public int CallsDoneClearDays { get; init; }

    public string DeleteStyle { get; init; } = "UNDO";
    public Dictionary<string, string> TabGestureOv { get; init; } = new();
    public bool ShowDeleteOnDone { get; init; } = true;

    public bool CardShowPriority { get; init; } = true;
    public bool CardShowDateTime { get; init; } = true;
    public bool CardShowCheckbox { get; init; } = true;
    public bool CardShowRepeat { get; init; } = true;
    public bool CardShowAlertType { get; init; }
    public string CardAlertTypeStyle { get; init; } = "ICON";
    public string AlertColorA { get; init; } = "#D32F2F";
    public string AlertColorR { get; init; } = "#EF6C00";
    public Dictionary<string, string> TabCardOv { get; init; } = new();

    public bool ShowMediumTag { get; init; }
    public int TasksShowMediumTag { get; init; } = -1;
    public int ShopShowMediumTag { get; init; } = -1;
    public int LearnShowMediumTag { get; init; } = -1;

    public string TasksSort { get; init; } = "DATE";
    public string ShopSort { get; init; } = "DATE";
    public string LearnSort { get; init; } = "DATE";

    public bool ShowTasks { get; init; } = true;
    public bool ShowLearn { get; init; } = true;
    public bool ShowCalls { get; init; } = true;
    public bool Badges { get; init; } = true;

    public bool CallCardShowNumber { get; init; }
    public bool CallCardShowMissedAt { get; init; } = true;
    public bool CallCardShowMissedCount { get; init; }
    public bool CallCardShowNote { get; init; }
    public bool CallCardShowLabel { get; init; }
    public bool CallCardShowNext { get; init; }

    public string TimeFormat { get; init; } = "PHONE";
    public int DefaultDueMinutes { get; init; } = 1080;
    public int SnoozeM1 { get; init; } = 90;

    public string GlobalNewDueMode { get; init; } = "TOMORROW";
    public int GlobalNewDueDays { get; init; } = 3;
    public int GlobalNewDueMinutes { get; init; } = 600;
    public bool GlobalNewDueTimed { get; init; } = true;
    public string TasksNewDueMode { get; init; } = "TOMORROW";
    public int TasksNewDueDays { get; init; } = 2;
    public int TasksNewDueMinutes { get; init; } = 600;
    public string TasksNewDueTimed { get; init; } = "INHERIT";
    public string ShopNewDueMode { get; init; } = "TOMORROW";
    public int ShopNewDueDays { get; init; } = 2;
    public int ShopNewDueMinutes { get; init; } = 600;
    public string ShopNewDueTimed { get; init; } = "INHERIT";
    public string LearnNewDueMode { get; init; } = "TOMORROW";
    public int LearnNewDueDays { get; init; } = 2;
    public int LearnNewDueMinutes { get; init; } = 600;
    public string LearnNewDueTimed { get; init; } = "INHERIT";
    public string SchedDefault { get; init; } = "ONCE";
    public string TasksSchedDefault { get; init; } = "INHERIT";
    public string ShopSchedDefault { get; init; } = "INHERIT";
    public string LearnSchedDefault { get; init; } = "INHERIT";

    public bool AlertsEnabled { get; init; } = true;
    public string TasksAlertsOn { get; init; } = "INHERIT";
    public string ShopAlertsOn { get; init; } = "INHERIT";
    public string LearnAlertsOn { get; init; } = "INHERIT";
    public string CallsAlertsOn { get; init; } = "INHERIT";

    // ───────────── resolvers (tab null = Calls, as on Android) ─────────────

    /// <summary>Android tabPrefix: t / s / l, c for Calls.</summary>
    public static string Prefix(Tab? tab) => tab switch { Tab.SHOP => "s", Tab.LEARN => "l", Tab.TASKS => "t", _ => "c" };

    /// <summary>delayFor: a negative per-tab value inherits the global; clamped 0..300 s.</summary>
    public int DelaySec(Tab? tab)
    {
        int v = tab switch { Tab.SHOP => ShopMoveDelaySec, Tab.LEARN => LearnMoveDelaySec, Tab.TASKS => TasksMoveDelaySec, _ => CallsMoveDelaySec };
        return Math.Clamp(v < 0 ? MoveDelaySec : v, 0, 300);
    }
    public int CallsDelaySec => DelaySec(null);

    /// <summary>clearFor: days after which a Done record is cleared; 0 = off.</summary>
    public int ClearDays(Tab? tab)
    {
        int v = tab switch { Tab.SHOP => ShopDoneClearDays, Tab.LEARN => LearnDoneClearDays, Tab.TASKS => TasksDoneClearDays, _ => CallsDoneClearDays };
        return Math.Max(0, v < 0 ? GlobalDoneClearDays : v);
    }

    /// <summary>gesturesFor(...).deleteStyle: the tab override, else the global.</summary>
    public bool ConfirmDelete(Tab? tab) =>
        (TabGestureOv.TryGetValue(Prefix(tab) + "DeleteStyle", out var v) ? v : DeleteStyle) == "CONFIRM";

    /// <summary>cardFieldsFor: "ON"/"OFF" overrides win; the reminder-type chip never shows on Calls.</summary>
    public CardParts Card(Tab? tab)
    {
        string p = Prefix(tab);
        bool B(string key, bool global) => TabCardOv.TryGetValue(p + key, out var v) ? v == "ON" || (v != "OFF" && global) : global;
        return new CardParts(B("CardPriority", CardShowPriority), B("CardDateTime", CardShowDateTime), B("CardCheckbox", CardShowCheckbox),
            B("CardRepeat", CardShowRepeat), tab != null && B("CardAlertType", CardShowAlertType));
    }

    /// <summary>cardAlertStyleFor: "ICON" or "TEXT".</summary>
    public string AlertStyle(Tab? tab) =>
        TabCardOv.TryGetValue(Prefix(tab) + "CardAlertStyle", out var v) && v is "ICON" or "TEXT" ? v : CardAlertTypeStyle == "TEXT" ? "TEXT" : "ICON";

    /// <summary>cardAlertColorFor: a valid "#RRGGBB" override, else the global colour for Alarm (A) or Ring (R).</summary>
    public string AlertColor(Tab? tab, char letter)
    {
        static bool Valid(string? c) => c is { Length: 7 } && c[0] == '#';
        string key = Prefix(tab) + (letter == 'A' ? "CardAlertColorA" : "CardAlertColorR");
        if (TabCardOv.TryGetValue(key, out var v) && Valid(v)) return v;
        var global = letter == 'A' ? AlertColorA : AlertColorR;
        return Valid(global) ? global : letter == 'A' ? "#D32F2F" : "#EF6C00";
    }

    /// <summary>showPriorityTag: Urgent, High and Low always; Medium only when the tab (or global) shows it.</summary>
    public bool ShowPriority(Priority? priority, Tab? tab)
    {
        if (priority == null) return false;
        if (priority != Priority.MEDIUM) return true;
        int raw = tab switch { Tab.SHOP => ShopShowMediumTag, Tab.LEARN => LearnShowMediumTag, Tab.TASKS => TasksShowMediumTag, _ => -1 };
        return raw == 1 || (raw != 0 && ShowMediumTag);
    }

    /// <summary>sortOf: DATE, GROUP, PRIORITY or NONE (Tasks' CALENDAR is shown as DATE on the PC).</summary>
    public string Sort(Tab tab)
    {
        var v = tab switch { Tab.SHOP => ShopSort, Tab.LEARN => LearnSort, _ => TasksSort };
        return v is "GROUP" or "PRIORITY" or "NONE" ? v : "DATE";
    }

    /// <summary>alertsEnabledFor: "ON"/"OFF" per tab, else the global switch.</summary>
    public bool AlertsOn(Tab? tab)
    {
        var v = tab switch { Tab.SHOP => ShopAlertsOn, Tab.LEARN => LearnAlertsOn, Tab.TASKS => TasksAlertsOn, _ => CallsAlertsOn };
        return v == "ON" || (v != "OFF" && AlertsEnabled);
    }

    /// <summary>snoozeMinutes: clamped 1..1440.</summary>
    public int SnoozeMinutes => Math.Clamp(SnoozeM1, 1, 24 * 60);

    /// <summary>Date-only items ring at this minute of the day (0..1439).</summary>
    public int DueMinuteOfDay => Math.Clamp(DefaultDueMinutes, 0, 24 * 60 - 1);

    /// <summary>timeFormatterFor: "h:mm tt" or "HH:mm"; PHONE follows the Windows clock setting.</summary>
    public string TimePattern(bool windowsIs24) => TimeFormat switch
    {
        "H12" => "h:mm tt",
        "H24" => "HH:mm",
        _ => windowsIs24 ? "HH:mm" : "h:mm tt",
    };

    /// <summary>newDueTimedFor: whether a new item's due date carries a clock time.</summary>
    public bool NewDueTimed(Tab tab)
    {
        var v = tab switch { Tab.SHOP => ShopNewDueTimed, Tab.LEARN => LearnNewDueTimed, _ => TasksNewDueTimed };
        return v == "ON" || (v != "OFF" && GlobalNewDueTimed);
    }

    /// <summary>defaultNewDue: the due date a new item starts with, or null for "Off".</summary>
    public (long? DueAt, bool HasTime) NewDue(Tab tab, long now, TimeZoneInfo? zone = null)
    {
        var (mode, days, mins) = tab switch
        {
            Tab.SHOP => (ShopNewDueMode, ShopNewDueDays, ShopNewDueMinutes),
            Tab.LEARN => (LearnNewDueMode, LearnNewDueDays, LearnNewDueMinutes),
            _ => (TasksNewDueMode, TasksNewDueDays, TasksNewDueMinutes),
        };
        if (mode == "INHERIT") (mode, days, mins) = (GlobalNewDueMode, GlobalNewDueDays, GlobalNewDueMinutes);
        if (mode == "OFF") return (null, false);
        bool timed = NewDueTimed(tab);
        if (!timed) mins = DefaultDueMinutes;
        mins = Math.Clamp(mins, 0, 24 * 60 - 1);
        int add = mode switch { "TODAY" => 0, "IN_N" => Math.Clamp(days, 0, 3650), _ => 1 };
        zone ??= TimeZoneInfo.Local;
        var today = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(now), zone).Date;
        var local = DateTime.SpecifyKind(today.AddDays(add).AddMinutes(mins), DateTimeKind.Unspecified);
        var at = new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUnixTimeMilliseconds();
        return (at, timed);
    }

    /// <summary>schedDefaultFor: NONE, ONCE or REPEAT for a new item's editor; Buy is never NONE.</summary>
    public string NewSchedule(Tab tab)
    {
        var ov = tab switch { Tab.SHOP => ShopSchedDefault, Tab.LEARN => LearnSchedDefault, _ => TasksSchedDefault };
        var raw = ov != "INHERIT" ? ov : SchedDefault;
        var kind = raw is "NONE" or "REPEAT" ? raw : "ONCE";
        return tab == Tab.SHOP && kind == "NONE" ? "ONCE" : kind;
    }

    /// <summary>Android isTabVisible for the PC menu.</summary>
    public bool TabVisible(Tab? tab) => tab switch { Tab.TASKS => ShowTasks, Tab.LEARN => ShowLearn, Tab.SHOP => true, _ => ShowCalls };

    // ───────────── reading the phone's settings JSON ─────────────

    /// <summary>
    /// Reads the phone's settings object (the Firestore settings document's json, or an Android backup's "settings").
    /// Keys are the Kotlin field names. Missing or wrong-typed keys keep the phone's defaults.
    /// </summary>
    public static PhonePrefs FromJson(JsonObject? json, string source, long syncedAt)
    {
        var d = new PhonePrefs();
        if (json == null) return d with { Source = source, SyncedAt = syncedAt };
        int I(string k, int def) => json[k] is JsonValue v && v.TryGetValue<double>(out var x) && double.IsFinite(x) ? (int)Math.Clamp(x, int.MinValue, int.MaxValue) : def;
        bool B(string k, bool def) => json[k] is JsonValue v && v.TryGetValue<bool>(out var x) ? x : def;
        string S(string k, string def, params string[] allowed)
        {
            if (json[k] is not JsonValue v || !v.TryGetValue<string>(out var x) || x == null) return def;
            return allowed.Length == 0 || allowed.Contains(x) ? x : def;
        }
        Dictionary<string, string> M(string k)
        {
            var map = new Dictionary<string, string>();
            if (json[k] is JsonObject o)
                foreach (var (key, value) in o)
                    if (value is JsonValue v && v.TryGetValue<string>(out var x) && x != null) map[key] = x;
            return map;
        }
        string[] newDue = { "INHERIT", "OFF", "TODAY", "TOMORROW", "IN_N" };
        string[] tri = { "INHERIT", "ON", "OFF" };
        string[] sched = { "INHERIT", "NONE", "ONCE", "REPEAT" };
        string[] sorts = { "DATE", "GROUP", "PRIORITY", "NONE", "CALENDAR", "SHOP" };
        return new PhonePrefs
        {
            Source = source, SyncedAt = syncedAt,
            MoveDelaySec = I("moveDelaySec", d.MoveDelaySec),
            TasksMoveDelaySec = I("tasksMoveDelaySec", d.TasksMoveDelaySec),
            ShopMoveDelaySec = I("shopMoveDelaySec", d.ShopMoveDelaySec),
            LearnMoveDelaySec = I("learnMoveDelaySec", d.LearnMoveDelaySec),
            CallsMoveDelaySec = I("callsMoveDelaySec", d.CallsMoveDelaySec),
            GlobalDoneClearDays = I("globalDoneClearDays", 0),
            TasksDoneClearDays = I("tasksDoneClearDays", 0),
            ShopDoneClearDays = I("shopDoneClearDays", 0),
            LearnDoneClearDays = I("learnDoneClearDays", 0),
            CallsDoneClearDays = I("callsDoneClearDays", 0),
            DeleteStyle = S("deleteStyle", "UNDO", "UNDO", "CONFIRM"),
            TabGestureOv = M("tabGestureOv"),
            ShowDeleteOnDone = B("showDeleteOnDone", true),
            CardShowPriority = B("cardShowPriority", true),
            CardShowDateTime = B("cardShowDateTime", true),
            CardShowCheckbox = B("cardShowCheckbox", true),
            CardShowRepeat = B("cardShowRepeat", true),
            CardShowAlertType = B("cardShowAlertType", false),
            CardAlertTypeStyle = S("cardAlertTypeStyle", "ICON", "ICON", "TEXT"),
            AlertColorA = S("alertColorA", d.AlertColorA),
            AlertColorR = S("alertColorR", d.AlertColorR),
            TabCardOv = M("tabCardOv"),
            ShowMediumTag = B("showMediumTag", false),
            TasksShowMediumTag = I("tasksShowMediumTag", -1),
            ShopShowMediumTag = I("shopShowMediumTag", -1),
            LearnShowMediumTag = I("learnShowMediumTag", -1),
            TasksSort = S("tasksSort", "DATE", sorts),
            ShopSort = S("shopSort", "DATE", sorts),
            LearnSort = S("learnSort", "DATE", sorts),
            ShowTasks = B("showTasks", true),
            ShowLearn = B("showLearn", true),
            ShowCalls = B("showCalls", true),
            Badges = B("badges", true),
            CallCardShowNumber = B("callCardShowNumber", false),
            CallCardShowMissedAt = B("callCardShowMissedAt", true),
            CallCardShowMissedCount = B("callCardShowMissedCount", false),
            CallCardShowNote = B("callCardShowNote", false),
            CallCardShowLabel = B("callCardShowLabel", false),
            CallCardShowNext = B("callCardShowNext", false),
            TimeFormat = S("timeFormat", "PHONE", "PHONE", "H12", "H24"),
            DefaultDueMinutes = I("defaultDueMinutes", 1080),
            SnoozeM1 = I("snoozeM1", 90),
            GlobalNewDueMode = S("globalNewDueMode", "TOMORROW", "OFF", "TODAY", "TOMORROW", "IN_N"),
            GlobalNewDueDays = I("globalNewDueDays", 3),
            GlobalNewDueMinutes = I("globalNewDueMinutes", 600),
            GlobalNewDueTimed = B("globalNewDueTimed", true),
            TasksNewDueMode = S("tasksNewDueMode", "TOMORROW", newDue),
            TasksNewDueDays = I("tasksNewDueDays", 2),
            TasksNewDueMinutes = I("tasksNewDueMinutes", 600),
            TasksNewDueTimed = S("tasksNewDueTimed", "INHERIT", tri),
            ShopNewDueMode = S("shopNewDueMode", "TOMORROW", newDue),
            ShopNewDueDays = I("shopNewDueDays", 2),
            ShopNewDueMinutes = I("shopNewDueMinutes", 600),
            ShopNewDueTimed = S("shopNewDueTimed", "INHERIT", tri),
            LearnNewDueMode = S("learnNewDueMode", "TOMORROW", newDue),
            LearnNewDueDays = I("learnNewDueDays", 2),
            LearnNewDueMinutes = I("learnNewDueMinutes", 600),
            LearnNewDueTimed = S("learnNewDueTimed", "INHERIT", tri),
            SchedDefault = S("schedDefault", "ONCE", "NONE", "ONCE", "REPEAT"),
            TasksSchedDefault = S("tasksSchedDefault", "INHERIT", sched),
            ShopSchedDefault = S("shopSchedDefault", "INHERIT", sched),
            LearnSchedDefault = S("learnSchedDefault", "INHERIT", sched),
            AlertsEnabled = B("alertsEnabled", true),
            TasksAlertsOn = S("tasksAlertsOn", "INHERIT", tri),
            ShopAlertsOn = S("shopAlertsOn", "INHERIT", tri),
            LearnAlertsOn = S("learnAlertsOn", "INHERIT", tri),
            CallsAlertsOn = S("callsAlertsOn", "INHERIT", tri),
        };
    }

    public static PhonePrefs FromJson(string? json, string source, long syncedAt)
    {
        if (string.IsNullOrWhiteSpace(json)) return new PhonePrefs { Source = source, SyncedAt = syncedAt };
        try { return FromJson(JsonNode.Parse(json) as JsonObject, source, syncedAt); }
        catch (JsonException) { return new PhonePrefs { Source = source, SyncedAt = syncedAt }; }
    }
}
