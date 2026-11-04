using System.Text.Json;
using System.Text.Json.Nodes;

namespace Remindly.Core.Sync;

/// <summary>
/// The Firestore contract shared with the Android app (Sync.kt). It must never drift:
///   /users/{uid}/{items|calls|places|shops|cities|chains|products}/{id}  and  /users/{uid}/settings/app
///   each document = { json: "&lt;the record as Gson writes it&gt;", updatedAt: &lt;Long&gt;, schemaVer: 71 }.
/// Windows does not use the phone's product–shop price links (productlinks), so it never reads or writes them.
/// </summary>
public static class SyncContract
{
    /// <summary>Android SyncRepo.SYNC_SCHEMA. A lower value marks a writer that predates fields (field-preserving merge).</summary>
    public const int SyncSchema = 71;

    public const string Items = "items";
    public const string Calls = "calls";
    public const string Places = "places";
    public const string Shops = "shops";
    public const string Cities = "cities";
    public const string Chains = "chains";
    public const string Products = "products";
    public const string Settings = "settings";
    public const string SettingsDoc = "app";

    /// <summary>Record collections, in the order a batch is applied (settings first — lists before the items in them).</summary>
    public static readonly string[] Records = { Items, Calls, Places, Shops, Cities, Chains, Products };

    public static string Key(string col, string id) => col + "/" + id;

    public static string Encode<T>(T record) => JsonSerializer.Serialize(record, Json.Compact);

    /// <summary>"projects/p/databases/(default)/documents/users/{uid}/{col}/{id}" → (col, id); (null, "") when it is not a user document.</summary>
    public static (string? Col, string Id) SplitName(string name)
    {
        const string marker = "/documents/users/";
        var idx = name.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return (null, "");
        var rest = name[(idx + marker.Length)..].Split('/');
        return rest.Length >= 3 ? (rest[1], rest[2]) : (null, "");
    }

    /// <summary>User-facing name of a collection (First sync dialog, sync log).</summary>
    public static string Label(string col) => col switch
    {
        Items => "Tasks, Learn and Buy items",
        Calls => "Calls",
        Places => "Places",
        Shops => "Shops",
        Cities => "Cities",
        Chains => "Chains",
        Products => "Products",
        Settings => "Preferences and lists",
        _ => col,
    };
}

/// <summary>A document read from Firestore.</summary>
public sealed record RemoteDoc(string Id, string Json, int SchemaVer, long UpdatedAt);

/// <summary>A local record as it would be written to Firestore.</summary>
public sealed record LocalRecord(string Col, string Id, long Stamp, Func<string> Json)
{
    public string Key => SyncContract.Key(Col, Id);
}

public sealed class ApplyResult
{
    /// <summary>Records added or replaced on this PC.</summary>
    public int Changed { get; set; }
    /// <summary>The cloud holds an OLDER copy than this PC (the phone re-sent a stale copy): push ours again.</summary>
    public List<LocalRecord> LocalNewer { get; } = new();
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Pure merge rules, identical to Android's SyncRepo: last writer wins on the merge stamp
/// (items and calls: updatedAt, else createdAt; everything else: updatedAt), deletes travel as
/// deletedAt tombstones, and a record from an older writer is laid over the local copy so fields
/// it does not know are kept. Unknown fields ride in each record's Extra both ways.
/// </summary>
public static class SyncMerge
{
    private interface ICol
    {
        string Name { get; }
        IEnumerable<LocalRecord> Local(RemindlyData d);
        void Apply(RemindlyData d, IEnumerable<RemoteDoc> docs, ISet<string> forgotten, long now, ApplyResult r);
    }

    private sealed class Col<T> : ICol where T : class
    {
        public string Name { get; init; } = "";
        public Func<RemindlyData, List<T>> Get { get; init; } = null!;
        public Action<RemindlyData, List<T>> Set { get; init; } = null!;
        public Func<T, long> Id { get; init; } = null!;
        public Func<T, long> Stamp { get; init; } = null!;
        public Func<T, long?> Deleted { get; init; } = null!;
        /// <summary>The Bin forgets these after 30 days; an older tombstone from the cloud is not brought back.</summary>
        public bool Purges { get; init; }

        public IEnumerable<LocalRecord> Local(RemindlyData d) =>
            Get(d).Select(r => new LocalRecord(Name, Id(r).ToString(), Stamp(r), () => SyncContract.Encode(r)));

        public void Apply(RemindlyData d, IEnumerable<RemoteDoc> docs, ISet<string> forgotten, long now, ApplyResult r)
        {
            var list = Get(d);
            var index = new Dictionary<long, int>();
            for (int k = 0; k < list.Count; k++) index[Id(list[k])] = k;
            long cutoff = ItemRules.PurgeCutoff(now);
            bool any = false;
            foreach (var doc in docs)
            {
                T? inc;
                try { inc = JsonSerializer.Deserialize<T>(doc.Json, Json.Options); }
                catch (Exception ex) { r.Warnings.Add($"{Name}/{doc.Id} unreadable — skipped ({ex.Message})"); continue; }
                if (inc == null) { r.Warnings.Add($"{Name}/{doc.Id} empty — skipped"); continue; }
                var id = Id(inc);
                var key = SyncContract.Key(Name, id.ToString());
                index.TryGetValue(id, out var at);
                T? local = index.ContainsKey(id) ? list[at] : null;
                if (local != null && doc.SchemaVer < SyncContract.SyncSchema)
                {
                    var overlaid = OverlayOlder(doc.Json, local, out var kept);
                    if (overlaid != null) inc = overlaid;
                    if (kept.Count > 0) r.Warnings.Add($"{Name}/{id} from older sync schema v{doc.SchemaVer} — kept {kept.Count} local field(s): {string.Join(",", kept)}");
                }
                if (local == null)
                {
                    if (Deleted(inc) is long del && (forgotten.Contains(key) || (Purges && del < cutoff))) continue;
                    forgotten.Remove(key);
                    list.Add(inc);
                    index[id] = list.Count - 1;
                    r.Changed++; any = true;
                    continue;
                }
                long ls = Stamp(local), rs = Stamp(inc);
                if (rs > ls || (rs == ls && SyncContract.Encode(inc) != SyncContract.Encode(local)))
                {
                    list[at] = inc;
                    r.Changed++; any = true;
                }
                else if (rs < ls)
                {
                    var keep = local;
                    r.LocalNewer.Add(new LocalRecord(Name, id.ToString(), ls, () => SyncContract.Encode(keep)));
                }
            }
            if (any) Set(d, list);
        }
    }

    private static long StampOf(long updatedAt, long createdAt) => updatedAt > 0 ? updatedAt : createdAt;

    private static readonly ICol[] All =
    {
        new Col<Item> { Name = SyncContract.Items, Get = d => d.Items, Set = (d, l) => d.Items = l, Id = i => i.Id, Stamp = i => StampOf(i.UpdatedAt, i.CreatedAt), Deleted = i => i.DeletedAt, Purges = true },
        new Col<CallReminder> { Name = SyncContract.Calls, Get = d => d.Calls ??= new(), Set = (d, l) => d.Calls = l, Id = c => c.Id, Stamp = c => StampOf(c.UpdatedAt, c.CreatedAt), Deleted = c => c.DeletedAt, Purges = true },
        new Col<GeoPlace> { Name = SyncContract.Places, Get = d => d.Places, Set = (d, l) => d.Places = l, Id = p => p.Id, Stamp = p => p.UpdatedAt, Deleted = p => p.DeletedAt },
        new Col<Shop> { Name = SyncContract.Shops, Get = d => d.Shops, Set = (d, l) => d.Shops = l, Id = s => s.Id, Stamp = s => s.UpdatedAt, Deleted = s => s.DeletedAt },
        new Col<City> { Name = SyncContract.Cities, Get = d => d.Cities, Set = (d, l) => d.Cities = l, Id = c => c.Id, Stamp = c => c.UpdatedAt, Deleted = c => c.DeletedAt },
        new Col<Chain> { Name = SyncContract.Chains, Get = d => d.Chains, Set = (d, l) => d.Chains = l, Id = c => c.Id, Stamp = c => c.UpdatedAt, Deleted = c => c.DeletedAt },
        new Col<Product> { Name = SyncContract.Products, Get = d => d.Products, Set = (d, l) => d.Products = l, Id = p => p.Id, Stamp = p => p.UpdatedAt, Deleted = p => p.DeletedAt },
    };

    /// <summary>Every local record of every synced collection, with its merge stamp.</summary>
    public static IEnumerable<LocalRecord> LocalRecords(RemindlyData d) => All.SelectMany(c => c.Local(d));

    /// <summary>Folds one collection's documents into [d] by last-writer-wins. Call DataStore.Heal afterwards.</summary>
    public static ApplyResult Apply(RemindlyData d, string col, IEnumerable<RemoteDoc> docs, ISet<string> forgotten, long now)
    {
        var r = new ApplyResult();
        var c = All.FirstOrDefault(x => x.Name == col);
        if (c == null) { r.Warnings.Add($"collection '{col}' is not synced on Windows — ignored"); return r; }
        c.Apply(d, docs, forgotten, now, r);
        return r;
    }

    /// <summary>
    /// A record from an older writer (schemaVer &lt; 71): only the keys it carries replace the local
    /// copy's, so fields it predates keep their local values. [kept] lists the keys left alone.
    /// </summary>
    public static T? OverlayOlder<T>(string remoteJson, T local, out List<string> kept) where T : class
    {
        kept = new();
        try
        {
            var baseObj = JsonNode.Parse(SyncContract.Encode(local))!.AsObject();
            var remote = JsonNode.Parse(remoteJson)!.AsObject();
            foreach (var (k, _) in baseObj) if (!remote.ContainsKey(k)) kept.Add(k);
            foreach (var (k, v) in remote) baseObj[k] = v?.DeepClone();
            return baseObj.Deserialize<T>(Json.Options);
        }
        catch { return null; }
    }
}
