using System.Text;

namespace Remindly.Core;

/// <summary>Task-list wire and membership rules; never mix TASKS with SHOP lists.</summary>
public static class TaskLists
{
    public const long UnsortedId = 0;
    public static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) &&
        !string.Equals(name.Trim(), "Unsorted", StringComparison.OrdinalIgnoreCase);
    private static string Key(string name) => name.Trim().ToLowerInvariant();

    public static TaskList Heal(TaskList list) => list with
    {
        Name = (list.Name ?? "").Trim(),
        Icon = string.IsNullOrWhiteSpace(list.Icon) ? null : list.Icon.Trim(),
        CreatedAt = Math.Max(0, list.CreatedAt),
        UpdatedAt = Math.Max(0, Math.Max(list.UpdatedAt, list.DeletedAt ?? 0)),
    };

    public static List<TaskList> Live(IEnumerable<TaskList>? lists) => (lists ?? Array.Empty<TaskList>())
        .Where(l => l != null && l.Id > 0 && l.DeletedAt == null && ValidName(l.Name)).ToList();

    public static TaskList? Named(IEnumerable<TaskList>? lists, string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : Live(lists).FirstOrDefault(l => Key(l.Name) == Key(name));

    public static long? ListIdOf(Item item, IEnumerable<TaskList>? lists)
    {
        if (item.Tab != Tab.TASKS) return null;
        // An explicit ID must never fall back to a stale name after a deletion or rename.
        if (item.ListId is long id) return Live(lists).FirstOrDefault(l => l.Id == id)?.Id;
        return Named(lists, item.Group)?.Id;
    }

    public static List<Item> Members(IEnumerable<Item> items, long listId, IEnumerable<TaskList>? lists) =>
        items.Where(i => i.Tab == Tab.TASKS && i.DeletedAt == null &&
            (ListIdOf(i, lists) ?? UnsortedId) == listId).ToList();

    /// <summary>Same per-ID ordering as Android compareTaskListVersions; deletion wins a tie.</summary>
    public static int Compare(TaskList a, TaskList b)
    {
        int c = a.UpdatedAt.CompareTo(b.UpdatedAt);
        if (c != 0) return c;
        c = (a.DeletedAt != null).CompareTo(b.DeletedAt != null);
        if (c != 0) return c;
        c = (a.DeletedAt ?? 0).CompareTo(b.DeletedAt ?? 0);
        if (c != 0) return c;
        c = StringComparer.Ordinal.Compare(a.Name, b.Name);
        if (c != 0) return c;
        c = StringComparer.Ordinal.Compare(a.Icon ?? "", b.Icon ?? "");
        if (c != 0) return c;
        c = a.Pinned.CompareTo(b.Pinned);
        return c != 0 ? c : a.CreatedAt.CompareTo(b.CreatedAt);
    }

    public static List<TaskList> Merge(IEnumerable<TaskList>? local, IEnumerable<TaskList>? incoming)
    {
        var result = new Dictionary<long, TaskList>();
        foreach (var raw in (local ?? Array.Empty<TaskList>()).Concat(incoming ?? Array.Empty<TaskList>()))
        {
            if (raw == null || raw.Id <= 0) continue;
            var list = Heal(raw);
            if (!result.TryGetValue(list.Id, out var old) || Compare(list, old) > 0) result[list.Id] = list;
        }
        return result.Values.ToList();
    }

    public static bool HasNewer(IEnumerable<TaskList>? local, IEnumerable<TaskList>? remote)
    {
        var byId = Merge(Array.Empty<TaskList>(), remote).ToDictionary(l => l.Id);
        return Merge(Array.Empty<TaskList>(), local).Any(l =>
            !byId.TryGetValue(l.Id, out var old) || Compare(l, old) > 0);
    }

    public static AppSettings Mirror(AppSettings settings) => settings with
    {
        TasksGroups = Live(settings.TaskLists).OrderBy(l => Key(l.Name), StringComparer.Ordinal)
            .Select(l => l.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
    };

    /// <summary>Same UTF-8 FNV-1a ID and task prefix as Android seedTaskListId().</summary>
    public static long SeedId(string name)
    {
        ulong hash = 14695981039346656037UL;
        foreach (byte b in Encoding.UTF8.GetBytes(Key(name))) hash = unchecked((hash ^ b) * 1099511628211UL);
        return (long)((0x7DUL << 56) | (hash & 0x00FFFFFFFFFFFFFFUL));
    }

    /// <summary>
    /// Non-destructive legacy migration. Empty configured groups become lists. Retained
    /// tombstones block stale group names. Missing metadata NEVER clears an item's explicit
    /// listId: items and settings can arrive at different times during synchronization.
    /// </summary>
    public static void Normalize(RemindlyData data, long now)
    {
        var settings = data.Settings;
        var lists = Merge(settings.TaskLists, data.TaskLists);
        var groups = (settings.TasksGroups ?? new()).Concat(data.TasksGroups ?? new())
            .Concat(data.Items.Where(i => i.Tab == Tab.TASKS && i.DeletedAt == null && i.ListId == null)
                .Select(i => i.Group ?? ""));
        foreach (var name in groups.Where(ValidName).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Named(lists, name) != null || lists.Any(l => l.DeletedAt != null && Key(l.Name) == Key(name))) continue;
            long id = SeedId(name);
            if (lists.Any(l => l.Id == id)) continue;
            lists.Add(new TaskList { Id = id, Name = name, CreatedAt = now, UpdatedAt = now,
                Icon = settings.GroupIcons?.FirstOrDefault(p => Key(p.Key) == Key(name)).Value });
        }
        data.Items = data.Items.Select(i => i.Tab == Tab.TASKS && i.DeletedAt == null && i.ListId == null &&
            Named(lists, i.Group) is TaskList list ? i with { ListId = list.Id, Group = list.Name } : i).ToList();
        data.Settings = Mirror(settings with { TaskLists = lists });
        data.TaskLists = null;
        data.TasksGroups = null;
    }
}
