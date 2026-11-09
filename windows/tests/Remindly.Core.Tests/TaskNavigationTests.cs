using Remindly.Core;
using Xunit;

namespace Remindly.Core.Tests;

public class TaskNavigationTests
{
    [Fact]
    public void Overview_only_offers_list_creation()
    {
        var location = TaskLocation.Lists;
        Assert.True(location.IsOverview);
        Assert.True(location.ShowRootTabs);
        Assert.True(location.ShowNewList);
        Assert.False(location.ShowTaskControls);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(42L)]
    public void Inside_list_hides_root_tabs_and_new_list(long id)
    {
        var location = new TaskLocation(id);
        Assert.True(location.IsInsideList);
        Assert.False(location.ShowRootTabs);
        Assert.False(location.ShowNewList);
        Assert.True(location.ShowTaskControls);
    }

    [Fact]
    public void All_tasks_is_selected_without_offering_new_list()
    {
        var location = TaskLocation.AllTasks;
        Assert.True(location.IsAllTasks);
        Assert.True(location.ShowRootTabs);
        Assert.True(location.ShowTaskControls);
        Assert.False(location.ShowNewList);
    }

    [Fact]
    public void Quick_view_is_not_falsely_highlighted_as_lists_or_all_tasks()
    {
        var location = new TaskLocation(-1, "TODAY");
        Assert.False(location.IsOverview);
        Assert.False(location.IsAllTasks);
        Assert.True(location.ShowTaskControls);
        Assert.False(location.ShowNewList);
    }

    [Fact]
    public void Back_restores_query_filter_scroll_and_unsubmitted_draft()
    {
        var navigation = new TaskNavigationState();
        var original = new TaskPageMemory("project", false, 400, 0, "", null);
        navigation.Go(new(42), original);
        var restored = navigation.Back(new("urgent", true, 120, 30, "Unsubmitted task", 42));
        Assert.Equal(TaskLocation.Lists, navigation.Current);
        Assert.Equal(original, restored);
        Assert.Equal("Unsubmitted task", navigation.Go(new(42), restored).Draft);
    }

    [Fact]
    public void Badge_navigation_returns_to_filtered_all_tasks_not_always_overview()
    {
        var navigation = new TaskNavigationState();
        navigation.Go(TaskLocation.AllTasks, new());
        var allTasks = new TaskPageMemory("report", true, 220, 25, "draft", 42);
        navigation.Go(new(42), allTasks);
        Assert.Equal(TaskLocation.AllTasks, navigation.BackTarget);
        Assert.Equal(allTasks, navigation.Back(new()));
        Assert.Equal(TaskLocation.AllTasks, navigation.Current);
    }

    [Fact]
    public void Remembering_or_reselecting_current_view_does_not_add_history()
    {
        var navigation = new TaskNavigationState();
        navigation.Go(new(42), new("lists"));
        navigation.Go(new(42), new("task"));
        navigation.Back(new("task"));
        Assert.Equal(TaskLocation.Lists, navigation.Current);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(0L, true)]
    [InlineData(42L, true)]
    [InlineData(99L, false)]
    public void All_tasks_quick_add_requires_explicit_valid_destination(long? destination, bool expected)
    {
        Assert.Equal(expected, TaskWorkspaceRules.CanQuickAdd("New task", TaskLocation.AllTasks, destination, new[] { 42L }));
    }

    [Fact]
    public void Quick_add_inside_list_uses_that_list_not_a_stale_destination()
    {
        Assert.True(TaskWorkspaceRules.CanQuickAdd("New", new(42), 99, new[] { 42L }));
        Assert.False(TaskWorkspaceRules.CanQuickAdd("New", new(99), 42, new[] { 42L }));
    }

    [Fact]
    public void Overview_and_blank_titles_cannot_quick_add()
    {
        Assert.False(TaskWorkspaceRules.CanQuickAdd("New", TaskLocation.Lists, 42, new[] { 42L }));
        Assert.False(TaskWorkspaceRules.CanQuickAdd("  ", new(42), null, new[] { 42L }));
    }

    [Fact]
    public void Search_labels_follow_the_scope()
    {
        Assert.Equal("Search lists", TaskWorkspaceRules.SearchHint(TaskLocation.Lists, ""));
        Assert.Equal("Search all tasks", TaskWorkspaceRules.SearchHint(TaskLocation.AllTasks, ""));
        Assert.Equal("Search in Projects", TaskWorkspaceRules.SearchHint(new(42), "Projects"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Search_miss_is_not_reported_as_an_empty_collection(bool done)
    {
        Assert.Equal("No matching tasks", TaskWorkspaceRules.EmptyTitle(true, done));
        Assert.NotEqual("No matching tasks", TaskWorkspaceRules.EmptyTitle(false, done));
    }
}
