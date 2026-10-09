namespace Remindly.Core;

/// <summary>Task list identities are scoped to TASKS, independent of shopping lists.</summary>
public static class TaskLists
{
    public const long UnsortedId = -1;
    public static List<TaskList> Normalize(IEnumerable<TaskList> lists) => lists.Where(l => l != null && l.Id > 0)
        .GroupBy(l => l.Id).Select(g => g.OrderByDescending(l => l.UpdatedAt).First())
        .Select(l => l with { Name = (l.Name ?? "").Trim() }).ToList();
    public static List<TaskList> Merge(IEnumerable<TaskList> a, IEnumerable<TaskList> b) => Normalize(a.Concat(b));
    public static long IdOf(Item item, IEnumerable<TaskList> lists) => item.Tab == Tab.TASKS && item.ListId is long id && lists.Any(l => l.Id == id && l.DeletedAt == null) ? id : UnsortedId;
    public static bool ListsFirst(AppSettings s) => s.TasksListsFirst == 1 || (s.TasksListsFirst != 0 && s.GlobalTaskListsFirst);
    public static void MigrateGroups(List<Item> items, ref AppSettings settings, long now)
    {
        var lists = settings.TaskLists.ToList();
        for (int n = 0; n < items.Count; n++)
        {
            var i = items[n];
            if (i.Tab != Tab.TASKS || i.ListId != null || string.IsNullOrWhiteSpace(i.Group)) continue;
            var name = i.Group.Trim();
            // A deleted list must never be resurrected by its legacy name mirror.
            var l = lists.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
            if (l == null) { l = new TaskList { Id = Ids.Next(), Name = name, CreatedAt = now, UpdatedAt = now }; lists.Add(l); }
            if (l.DeletedAt == null) items[n] = i with { ListId = l.Id, Group = l.Name };
        }
        settings = settings with { TaskLists = lists };
    }
}
