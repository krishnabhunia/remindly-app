using System.Text.Json;
using System.Text.Json.Nodes;

namespace Remindly.Core.Sync;

public sealed record CloudListSettingsPlan(AppSettings Incoming, CloudDocument? Upload);

/// <summary>Only shared list data is patched; Android preferences and unknown keys stay intact.</summary>
public static class CloudListSettings
{
    public static CloudListSettingsPlan Reconcile(CloudDocument? document, AppSettings local, long now)
    {
        if (document == null)
        {
            // A partial settings document labelled as current-schema would reset phone defaults.
            // Never claim success while list metadata is waiting for Android initialization.
            if (local.TaskLists.Count > 0 || local.ShopLists.Count > 0)
                throw new InvalidOperationException("Cloud list settings are not initialized. Open Android Remindly with Cloud sync enabled, then select Sync now.");
            return new(new AppSettings(), null);
        }
        var json = JsonNode.Parse(document.Json) as JsonObject ??
            throw new JsonException("Cloud settings must be a JSON object.");
        var tasks = json["taskLists"]?.Deserialize<List<TaskList>>(Json.Options) ?? new();
        var shops = json["shopLists"]?.Deserialize<List<ShopList>>(Json.Options) ?? new();
        var groups = json["tasksGroups"]?.Deserialize<List<string>>(Json.Options) ?? new();
        // The phone's own preferences ride the same document; Windows follows them read-only.
        var phone = PhonePrefs.FromJson(json, "sync", Math.Max(document.UpdatedAt, json["settingsUpdatedAt"] is JsonValue v && v.TryGetValue<long>(out var at) ? at : 0));
        var incoming = new AppSettings { TaskLists = tasks, TasksGroups = groups, ShopLists = shops, Phone = phone };
        bool newerTasks = TaskLists.HasNewer(local.TaskLists, tasks);
        bool newerShops = local.ShopLists.Any(l => !shops.Any(c => c.Id == l.Id && c.UpdatedAt >= l.UpdatedAt));
        if (!newerTasks && !newerShops) return new(incoming, null);

        if (newerTasks)
        {
            var merged = TaskLists.Merge(tasks, local.TaskLists);
            json["taskLists"] = JsonSerializer.SerializeToNode(merged, Json.Options);
            json["tasksGroups"] = JsonSerializer.SerializeToNode(
                TaskLists.Mirror(new AppSettings { TaskLists = merged }).TasksGroups, Json.Options);
        }
        if (newerShops)
            json["shopLists"] = JsonSerializer.SerializeToNode(ShopLists.Merge(shops, local.ShopLists), Json.Options);

        long stamp = Math.Max(document.UpdatedAt, json["settingsUpdatedAt"]?.GetValue<long>() ?? 0);
        if (stamp == long.MaxValue) throw new InvalidOperationException("Cloud settings timestamp is invalid; no settings were overwritten.");
        stamp = Math.Max(stamp + 1, now);
        json["settingsUpdatedAt"] = stamp;
        return new(incoming, new CloudDocument("app", json.ToJsonString(), stamp, CloudWire.Schema));
    }
}
