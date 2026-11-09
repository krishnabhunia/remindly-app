using System.Text.Json;
using System.Text.Json.Nodes;
using Remindly.Core;
using Remindly.Core.Sync;
using Xunit;

namespace Remindly.Core.Tests;

public class TaskListSyncTests
{
    private static TaskList List(long id, string name, long stamp = 100) =>
        new() { Id = id, Name = name, CreatedAt = 10, UpdatedAt = stamp };
    private static CloudDocument Settings(string json, long stamp = 100) => new("app", json, stamp, 71);

    [Fact]
    public void Nine_lists_survive_cloud_pull_even_when_only_three_contain_active_tasks()
    {
        var lists = Enumerable.Range(1, 9).Select(i => List(100 + i, "List " + i)).ToList();
        var doc = Settings(new JsonObject
        {
            ["settingsUpdatedAt"] = 100,
            ["taskLists"] = JsonSerializer.SerializeToNode(lists, Json.Options),
            ["shopLists"] = new JsonArray(),
        }.ToJsonString());
        var plan = CloudListSettings.Reconcile(doc, new AppSettings(), 200);
        Assert.Null(plan.Upload);
        var incoming = new RemindlyData
        {
            Settings = plan.Incoming,
            Items = Enumerable.Range(1, 3).Select(i => new Item
            { Id = i, Tab = Tab.TASKS, Title = "Task " + i, ListId = 100 + i, UpdatedAt = 100, CreatedAt = 100 }).ToList(),
        };
        var desktop = new RemindlyData();
        DataStore.MergeInto(desktop, incoming, 200);
        Assert.Equal(9, TaskLists.Live(desktop.Settings.TaskLists).Count);
        Assert.Equal(3, desktop.Items.Count(i => !i.Done));
        Assert.Empty(TaskLists.Members(desktop.Items, 109, desktop.Settings.TaskLists));
        Assert.Equal(101L, desktop.Items[0].ListId);
        var restored = DataStore.Heal(Json.Deserialize(Json.Serialize(desktop))!, 300);
        Assert.Equal(9, restored.Settings.TaskLists.Count);
        Assert.Equal(3, restored.Items.Count);
    }

    [Fact]
    public void Data_only_android_backup_imports_root_task_lists_and_keeps_empty_lists()
    {
        var data = DataStore.Parse("""
            {"items":[],"taskLists":[{"id":42,"name":"Empty","updatedAt":10}],"tasksGroups":["Empty"]}
            """)!;
        DataStore.Heal(data, 200);
        Assert.Equal("Empty", Assert.Single(data.Settings.TaskLists).Name);
        Assert.Null(data.TaskLists);
        Assert.Null(data.TasksGroups);
        Assert.Equal("Empty", Assert.Single(Json.Deserialize(Json.Serialize(data))!.Settings.TaskLists).Name);
    }

    [Fact]
    public void Renaming_a_list_does_not_recreate_the_old_legacy_group()
    {
        var desktop = new RemindlyData
        {
            Settings = new AppSettings { TaskLists = new() { List(42, "Old name") }, TasksGroups = new() { "Old name" } },
            Items = new() { new Item { Id = 1, Tab = Tab.TASKS, ListId = 42, Group = "Old name", UpdatedAt = 100 } },
        };
        DataStore.MergeInto(desktop, new RemindlyData
        { Settings = new AppSettings { TaskLists = new() { List(42, "New name", 200) } } }, 300);
        Assert.Equal("New name", Assert.Single(desktop.Settings.TaskLists).Name);
        Assert.Equal(new[] { "New name" }, desktop.Settings.TasksGroups);
        Assert.Equal(42L, Assert.Single(desktop.Items).ListId);
    }

    [Fact]
    public void Deletion_wins_equal_timestamp_in_both_merge_directions()
    {
        var live = List(42, "Work", 200);
        var deleted = live with { DeletedAt = 200 };
        Assert.NotNull(Assert.Single(TaskLists.Merge(new[] { live }, new[] { deleted })).DeletedAt);
        Assert.NotNull(Assert.Single(TaskLists.Merge(new[] { deleted }, new[] { live })).DeletedAt);
        Assert.True(TaskLists.HasNewer(new[] { deleted }, new[] { live }));
        Assert.False(TaskLists.HasNewer(new[] { live }, new[] { deleted }));
    }

    [Fact]
    public void Missing_settings_do_not_strip_item_membership()
    {
        var desktop = new RemindlyData { Items = new()
        { new Item { Id = 1, Tab = Tab.TASKS, ListId = 42, Group = "Work", UpdatedAt = 100 } } };
        DataStore.Heal(desktop, 200);
        Assert.Equal(42L, Assert.Single(desktop.Items).ListId);
        Assert.Empty(desktop.Settings.TaskLists);
        DataStore.MergeInto(desktop, new RemindlyData
        { Settings = new AppSettings { TaskLists = new() { List(42, "Work") } } }, 300);
        Assert.Single(TaskLists.Members(desktop.Items, 42, desktop.Settings.TaskLists));
    }

    [Fact]
    public void Task_and_shopping_lists_with_the_same_id_or_name_remain_separate()
    {
        var lists = new[] { List(42, "Shared name") };
        var items = new[]
        {
            new Item { Id = 1, Tab = Tab.TASKS, ListId = 42 },
            new Item { Id = 2, Tab = Tab.SHOP, ListId = 42 },
            new Item { Id = 3, Tab = Tab.LEARN, ListId = 42 },
        };
        Assert.Equal(1L, Assert.Single(TaskLists.Members(items, 42, lists)).Id);
        Assert.Null(TaskLists.ListIdOf(items[1], lists));
    }

    [Fact]
    public void Removed_explicit_id_never_falls_back_to_another_list_with_the_same_name()
    {
        var deleted = List(1, "Work") with { DeletedAt = 100 };
        var current = List(2, "Work", 200);
        Assert.Null(TaskLists.ListIdOf(new Item { Tab = Tab.TASKS, ListId = 1, Group = "Work" }, new[] { deleted, current }));
    }

    [Fact]
    public void Legacy_empty_group_migration_is_stable_and_idempotent()
    {
        Assert.Equal(9032230694422062528L, TaskLists.SeedId(" Work "));
        var data = new RemindlyData { Settings = new AppSettings { TasksGroups = new() { "Work", "Study" } } };
        DataStore.Heal(data, 100);
        var before = Json.Serialize(data);
        DataStore.Heal(data, 200);
        Assert.Equal(before, Json.Serialize(data));
        Assert.Equal(2, data.Settings.TaskLists.Count);
    }

    [Fact]
    public void Windows_list_edit_patches_phone_settings_without_replacing_other_fields()
    {
        var remote = Settings("""
            {"settingsUpdatedAt":100,"cloudSync":true,"androidOnly":{"keep":"yes"},
             "taskLists":[{"id":42,"name":"Work","createdAt":10,"updatedAt":100,"futureField":true}],
             "shopLists":[{"id":9,"name":"Groceries","updatedAt":100}]}
            """);
        var pulled = CloudListSettings.Reconcile(remote, new AppSettings(), 200).Incoming;
        var local = pulled with { TaskLists = new() { pulled.TaskLists[0] with { Name = "Renamed", UpdatedAt = 200 } } };
        var plan = CloudListSettings.Reconcile(remote, local, 300);
        var written = JsonNode.Parse(plan.Upload!.Json)!;
        Assert.Equal("Renamed", written["taskLists"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(42L, written["taskLists"]![0]!["id"]!.GetValue<long>());
        Assert.True(written["taskLists"]![0]!["futureField"]!.GetValue<bool>());
        Assert.Equal("yes", written["androidOnly"]!["keep"]!.GetValue<string>());
        Assert.True(written["cloudSync"]!.GetValue<bool>());
        Assert.Equal("Groceries", written["shopLists"]![0]!["name"]!.GetValue<string>());
        Assert.Null(written["design"]);
        Assert.Equal("Renamed", written["tasksGroups"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Stale_desktop_does_not_upload_or_replace_a_newer_phone_list()
    {
        var remote = Settings("""{"taskLists":[{"id":42,"name":"New","updatedAt":200}]}""", 200);
        var plan = CloudListSettings.Reconcile(remote, new AppSettings { TaskLists = new() { List(42, "Old", 100) } }, 300);
        Assert.Null(plan.Upload);
        Assert.Equal("New", Assert.Single(plan.Incoming.TaskLists).Name);
    }

    [Fact]
    public void Missing_or_invalid_settings_cannot_be_reported_as_synced_list_metadata()
    {
        Assert.Throws<InvalidOperationException>(() => CloudListSettings.Reconcile(null,
            new AppSettings { TaskLists = new() { List(42, "Work") } }, 200));
        Assert.Throws<JsonException>(() => CloudListSettings.Reconcile(Settings("[]"), new AppSettings(), 200));
        Assert.Null(CloudListSettings.Reconcile(null, new AppSettings(), 200).Upload);
    }

    [Fact]
    public void Full_refresh_deadline_is_per_collection_and_not_slid_by_incremental_polls()
    {
        var clock = new FullReadSchedule();
        var now = DateTimeOffset.Parse("2026-11-09T00:00:00Z");
        Assert.True(clock.IsDue("items", now));
        clock.Completed("items", now);
        for (int hour = 1; hour < 24; hour++) Assert.False(clock.IsDue("items", now.AddHours(hour)));
        Assert.True(clock.IsDue("items", now.AddHours(24)));
        Assert.True(clock.IsDue("calls", now.AddHours(1)));
        clock.Completed("calls", now.AddHours(1));
        Assert.False(clock.IsDue("calls", now.AddHours(24)));
        clock.Clear();
        Assert.True(clock.IsDue("calls", now.AddHours(24)));
    }
}
