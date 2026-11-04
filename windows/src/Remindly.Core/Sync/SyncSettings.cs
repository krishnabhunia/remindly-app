using System.Text.Json;
using System.Text.Json.Nodes;

namespace Remindly.Core.Sync;

/// <summary>
/// The settings document (/users/{uid}/settings/app) is the phone's whole preferences object. Windows
/// shares only a few keys with it (Buy lists and the sharing format); everything else in it — Task
/// lists, the phone's look, gestures, its own update switches — belongs to the phone and is carried
/// back untouched. Windows-only settings (design, tray, start-up, updates, Cloud sync on this PC)
/// never leave this PC.
/// </summary>
public static class SyncSettings
{
    /// <summary>The AppSettings keys both apps understand. Every other key of the document is the phone's.</summary>
    public static readonly string[] SharedKeys =
    {
        "shopLists", "shopDefaultListId",
        "shareIncludeDone", "shareIncludeQty", "shareUrgentTag", "shareBoughtTag", "shareHeadingSuffix",
        "shopGroups", "groupIcons", "shopDefaultGroup",
    };

    public const string StampKey = "settingsUpdatedAt";

    /// <summary>This PC's values of the shared keys.</summary>
    public static JsonObject Projection(AppSettings s)
    {
        var all = JsonSerializer.SerializeToNode(s, Json.Compact)!.AsObject();
        var o = new JsonObject();
        foreach (var k in SharedKeys) if (all.TryGetPropertyValue(k, out var v)) o[k] = v?.DeepClone();
        return o;
    }

    /// <summary>A fingerprint of the shared keys: when it changes, the PC has something to send.</summary>
    public static string Hash(AppSettings s) => Projection(s).ToJsonString();

    /// <summary>The phone's stamp on a settings document (its settingsUpdatedAt).</summary>
    public static long StampOf(JsonObject doc) =>
        doc.TryGetPropertyValue(StampKey, out var v) && v is JsonValue jv && jv.TryGetValue<long>(out var l) ? l : 0;

    /// <summary>
    /// The document to write: the phone's last document with this PC's shared keys laid over it and a
    /// new stamp. Null while the phone has never published its settings — writing a document of only
    /// the shared keys would reset every other preference on the phone to its default.
    /// </summary>
    public static JsonObject? BuildPush(AppSettings s, JsonObject? remoteBase, long stamp)
    {
        if (remoteBase == null) return null;
        var doc = remoteBase.DeepClone().AsObject();
        foreach (var (k, v) in Projection(s)) doc[k] = v?.DeepClone();
        doc[StampKey] = stamp;
        return doc;
    }

    /// <summary>
    /// Folds the phone's settings document into this PC's settings. Buy lists always merge per list
    /// (the newer copy of each list wins — exactly Android's mergeShopLists), whatever the document's
    /// stamp. The other shared values are taken from the phone only when [adoptValues] (its document
    /// is newer than this PC's last change). Windows-only settings are never touched.
    /// </summary>
    public static AppSettings ApplyRemote(AppSettings local, JsonObject remote, bool adoptValues)
    {
        var lists = Read<List<ShopList>>(remote, "shopLists")?.Where(l => l != null).Select(ShopLists.Heal).ToList();
        var s = local with { ShopLists = lists == null ? local.ShopLists : ShopLists.Merge(local.ShopLists, lists) };
        if (!adoptValues) return s;
        return s with
        {
            ShopDefaultListId = remote.ContainsKey("shopDefaultListId") ? Read<long?>(remote, "shopDefaultListId") : s.ShopDefaultListId,
            ShareIncludeDone = Read<bool?>(remote, "shareIncludeDone") ?? s.ShareIncludeDone,
            ShareIncludeQty = Read<bool?>(remote, "shareIncludeQty") ?? s.ShareIncludeQty,
            ShareUrgentTag = Read<bool?>(remote, "shareUrgentTag") ?? s.ShareUrgentTag,
            ShareBoughtTag = Read<bool?>(remote, "shareBoughtTag") ?? s.ShareBoughtTag,
            ShareHeadingSuffix = Read<string>(remote, "shareHeadingSuffix") ?? s.ShareHeadingSuffix,
            ShopGroups = Read<List<string>>(remote, "shopGroups") ?? s.ShopGroups,
            GroupIcons = Read<Dictionary<string, string>>(remote, "groupIcons") ?? s.GroupIcons,
            ShopDefaultGroup = Read<string>(remote, "shopDefaultGroup") ?? s.ShopDefaultGroup,
        };
    }

    /// <summary>True when this PC holds a list the document lacks or a newer copy of one it has.</summary>
    public static bool LocalHasNewerLists(AppSettings local, JsonObject remote)
    {
        var remoteLists = (Read<List<ShopList>>(remote, "shopLists") ?? new()).Where(l => l != null).ToDictionary(l => l.Id, l => l.UpdatedAt);
        return local.ShopLists.Any(l => !remoteLists.TryGetValue(l.Id, out var at) || l.UpdatedAt > at);
    }

    private static T? Read<T>(JsonObject o, string key)
    {
        try { return o.TryGetPropertyValue(key, out var v) && v != null ? v.Deserialize<T>(Json.Options) : default; }
        catch { return default; }
    }
}
