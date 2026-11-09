namespace Remindly.Core;

/// <summary>Desktop-only navigation; never serialized or sent to Firestore.</summary>
public readonly record struct TaskLocation(long? ListId, string QuickView = "ALL")
{
    public static TaskLocation Lists => new(null);
    public static TaskLocation AllTasks => new(-1);
    public bool IsOverview => ListId == null && QuickView == "ALL";
    public bool IsInsideList => ListId is >= 0 && QuickView == "ALL";
    public bool IsAllTasks => ListId == -1 && QuickView == "ALL";
    public bool ShowRootTabs => !IsInsideList;
    public bool ShowNewList => IsOverview;
    public bool ShowTaskControls => !IsOverview;
}

public sealed record TaskPageMemory(string Search = "", bool Done = false,
    double Vertical = 0, double Horizontal = 0, string Draft = "", long? Destination = null);

/// <summary>Back restores the originating view, including a filtered All tasks view.</summary>
public sealed class TaskNavigationState
{
    private readonly Dictionary<TaskLocation, TaskPageMemory> _pages = new();
    private readonly Stack<TaskLocation> _history = new();
    public TaskLocation Current { get; private set; } = TaskLocation.Lists;
    public TaskLocation BackTarget => _history.TryPeek(out var location) ? location : TaskLocation.Lists;
    public TaskPageMemory Memory => _pages.GetValueOrDefault(Current) ?? new();
    public void Remember(TaskPageMemory memory) => _pages[Current] = memory;

    public TaskPageMemory Go(TaskLocation target, TaskPageMemory memory)
    {
        Remember(memory);
        if (target != Current) { _history.Push(Current); Current = target; }
        return Memory;
    }

    public TaskPageMemory Back(TaskPageMemory memory)
    {
        Remember(memory);
        Current = _history.TryPop(out var target) ? target : TaskLocation.Lists;
        return Memory;
    }
}

public static class TaskWorkspaceRules
{
    public static bool CanQuickAdd(string title, TaskLocation location, long? destination,
        IEnumerable<long> liveListIds)
    {
        if (string.IsNullOrWhiteSpace(title) || location.IsOverview) return false;
        var id = location.IsInsideList ? location.ListId : destination;
        return id == 0 || (id is > 0 && liveListIds.Contains(id.Value));
    }

    public static string SearchHint(TaskLocation location, string listName) => location.IsOverview
        ? "Search lists" : location.IsInsideList ? "Search in " + listName : "Search all tasks";

    public static string EmptyTitle(bool searching, bool done) => searching ? "No matching tasks"
        : done ? "No completed tasks here" : "No active tasks here";
}
