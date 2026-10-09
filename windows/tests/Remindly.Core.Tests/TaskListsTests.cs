using Remindly.Core;
using Xunit;
namespace Remindly.Core.Tests;
public class TaskListsTests
{
    [Fact] public void DeletedTaskListKeepsTaskInUnsortedWithReminderAndShoppingIdentityUntouched()
    {
        var lists = new[] { new TaskList { Id = 10, Name = "Work", DeletedAt = 100 } };
        var task = new Item { Id = 1, Tab = Tab.TASKS, ListId = 10, DueAt = 500, RepeatMode = "DAILY" };
        Assert.Equal(TaskLists.UnsortedId,TaskLists.IdOf(task,lists)); Assert.Equal(500,task.DueAt); Assert.Equal("DAILY",task.RepeatMode);
        Assert.Equal(TaskLists.UnsortedId,TaskLists.IdOf(task with { Tab = Tab.SHOP },new[] { lists[0] with { DeletedAt = null } }));
    }
    [Fact] public void AndroidBackupPreservesListsAndNumericOverrideAcrossSaveAndImport()
    {
        var source = Json.Deserialize("""{"items":[{"id":1,"tab":"TASKS","title":"Report","listId":10}],"settings":{"taskLists":[{"id":10,"name":"Work","updatedAt":200}],"tasksListsFirst":1}}""")!;
        var local = DataStore.Heal(new RemindlyData(),1000);
        DataStore.MergeInto(local,source,1000); DataStore.MergeInto(local,source,1000);
        var loaded = DataStore.Heal(Json.Deserialize(Json.Serialize(local))!,1000);
        Assert.Single(loaded.Settings.TaskLists); Assert.Equal("Work",loaded.Settings.TaskLists[0].Name);
        Assert.Equal(10,TaskLists.IdOf(Assert.Single(loaded.Items),loaded.Settings.TaskLists));
        Assert.Equal(1,source.Settings.TasksListsFirst);
    }
    [Fact] public void DeletedListWinsOlderImportAndLegacyNameDoesNotResurrectIt()
    {
        var d = DataStore.Heal(new RemindlyData { Items = new() { new Item { Id=1, Tab=Tab.TASKS, Group="Work" } }, Settings = new() { TaskLists = TaskLists.Merge(new[] { new TaskList { Id=10,Name="Work",UpdatedAt=300,DeletedAt=300 } },new[] { new TaskList { Id=10,Name="Work",UpdatedAt=200 } }) } },1000);
        Assert.Single(d.Settings.TaskLists); Assert.NotNull(d.Settings.TaskLists[0].DeletedAt); Assert.Equal(TaskLists.UnsortedId,TaskLists.IdOf(d.Items[0],d.Settings.TaskLists));
    }
    [Fact] public void GlobalDefaultAndPerTaskOverrideChooseOpeningView()
    {
        Assert.True(TaskLists.ListsFirst(new())); Assert.False(TaskLists.ListsFirst(new() { GlobalTaskListsFirst=false }));
        Assert.True(TaskLists.ListsFirst(new() { GlobalTaskListsFirst=false,TasksListsFirst=1 })); Assert.False(TaskLists.ListsFirst(new() { TasksListsFirst=0 }));
    }
    [Fact] public void LegacyGroupsMigrateOnceAndCompactRowsPersist()
    {
        var d = DataStore.Heal(new RemindlyData { Items = new() { new Item { Id=1, Tab=Tab.TASKS, Group=" Work " }, new Item { Id=2,Tab=Tab.SHOP,Group="Work" } }, Settings = new() { DesktopCompactRows=true } },1000);
        var id = d.Items[0].ListId; Assert.NotNull(id);
        var loaded = DataStore.Heal(Json.Deserialize(Json.Serialize(d))!,2000);
        Assert.Single(loaded.Settings.TaskLists); Assert.Equal(id,loaded.Items[0].ListId); Assert.True(loaded.Settings.DesktopCompactRows); Assert.NotEqual(id,loaded.Items[1].ListId);
    }
}
