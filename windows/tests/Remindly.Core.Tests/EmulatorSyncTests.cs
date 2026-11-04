using System.Text;
using System.Text.Json.Nodes;
using Remindly.Core;
using Remindly.Core.Sync;
using Xunit;
using Xunit.Abstractions;

namespace Remindly.Core.Tests;

/// <summary>
/// End to end against a local Firestore emulator (set FIRESTORE_EMULATOR_HOST, e.g. 127.0.0.1:8085 —
/// "gcloud emulators firestore start"). "The phone" writes through the emulator's REST API EXACTLY the
/// envelope Android SyncRepo writes: { json: "&lt;gson&gt;", updatedAt, schemaVer: 71 }. Without an emulator
/// these tests return at once (CI has none; they were run locally before the PR).
/// </summary>
[Collection("storage")]
public class EmulatorSyncTests : IDisposable
{
    private static readonly string? Host = Environment.GetEnvironmentVariable("FIRESTORE_EMULATOR_HOST");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "remindly-emu-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http = new();
    private readonly FirebaseConfig _cfg = new() { ProjectId = "remindly-test", ApiKey = "x" };
    private readonly string _uid = "uid-" + Guid.NewGuid().ToString("N")[..8];
    private readonly ITestOutputHelper _out;
    private readonly string _prevRoot = AppPaths.DataRoot;

    public EmulatorSyncTests(ITestOutputHelper output)
    {
        _out = output;
        AppPaths.DataRoot = Path.Combine(_dir, "data");
    }

    public void Dispose()
    {
        AppPaths.DataRoot = _prevRoot;
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>The PC side: data owned by one lock (the app uses the UI thread).</summary>
    private sealed class Host2 : ISyncHost
    {
        private readonly object _gate = new();
        public RemindlyData Data { get; set; } = DataStore.Heal(new RemindlyData(), Clock.NowMs());
        public int Applied;
        public int Backups;
        public void Invoke(Action action) => Task.Run(() => { lock (_gate) action(); });
        public void AppliedRemote() => Interlocked.Increment(ref Applied);
        public void BeforeFirstSync() => Interlocked.Increment(ref Backups);
        public void Do(Action a) { lock (_gate) a(); }
    }

    private string DocUrl(string col, string id) => $"http://{Host}/v1/projects/{_cfg.ProjectId}/databases/(default)/documents/users/{_uid}/{col}/{id}";

    private async Task PhoneWrites(string col, string id, string json, long updatedAt, int schemaVer = 71)
    {
        var body = new JsonObject
        {
            ["fields"] = new JsonObject
            {
                ["json"] = new JsonObject { ["stringValue"] = json },
                ["updatedAt"] = new JsonObject { ["integerValue"] = updatedAt.ToString() },
                ["schemaVer"] = new JsonObject { ["integerValue"] = schemaVer.ToString() },
            },
        };
        var req = new HttpRequestMessage(HttpMethod.Patch, DocUrl(col, id)) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Add("Authorization", "Bearer owner");
        (await _http.SendAsync(req)).EnsureSuccessStatusCode();
    }

    private async Task<(string Json, long UpdatedAt, int SchemaVer)?> PhoneReads(string col, string id)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, DocUrl(col, id));
        req.Headers.Add("Authorization", "Bearer owner");
        var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return null;
        var f = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!["fields"]!.AsObject();
        return (f["json"]!["stringValue"]!.GetValue<string>(), long.Parse(f["updatedAt"]!["integerValue"]!.GetValue<string>()), int.Parse(f["schemaVer"]!["integerValue"]!.GetValue<string>()));
    }

    private (Host2 host, FirestoreSync sync) Boot(Action<RemindlyData>? seed = null)
    {
        var host = new Host2();
        seed?.Invoke(host.Data);
        var auth = new GoogleAuth(_cfg, Path.Combine(_dir, "secure"));
        auth.UseTestSession(_uid, "test@example.com");
        var sync = new FirestoreSync(host, auth, _cfg, Path.Combine(_dir, "state")) { EmulatorEndpoint = Host, EmulatorToken = "owner" };
        sync.Log = (m, e) => _out.WriteLine($"[sync] {m} {e?.Message}");
        return (host, sync);
    }

    private static async Task WaitUntil(Func<bool> cond, string what, int timeoutMs = 20000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("not met: " + what);
            await Task.Delay(100);
        }
    }

    private static async Task<T> WaitFor<T>(Func<Task<T?>> get, Func<T, bool> ok, string what, int timeoutMs = 20000) where T : struct
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (await get() is T v && ok(v)) return v;
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("not met: " + what);
            await Task.Delay(150);
        }
    }

    private const string PhoneSettings = """
        {"ver":43,"taskLists":[{"id":1,"name":"Home","updatedAt":5}],"updateAutoCheck":false,
         "shopLists":[{"id":100,"name":"Groceries","order":0,"createdAt":1,"updatedAt":50}],
         "shareHeadingSuffix":":-","shopGroups":["Groceries"],"groupIcons":{},"shopDefaultGroup":"","settingsUpdatedAt":1000}
        """;

    [Fact]
    public async Task Phone_record_arrives_live_and_a_PC_edit_lands_in_the_phone_format()
    {
        if (Host == null) return;
        long t0 = Clock.NowMs() - 60_000;
        await PhoneWrites("items", "5001", $$"""{"id":5001,"tab":"TASKS","title":"Pay rent","createdAt":{{t0}},"updatedAt":{{t0}},"nagFromPhone":42}""", t0);
        var (host, sync) = Boot();
        sync.SetEnabled(true);
        await WaitUntil(() => sync.InitialSnapshotDone, "first snapshot");
        await WaitUntil(() => { Item? i = null; host.Do(() => i = host.Data.Items.FirstOrDefault(x => x.Id == 5001)); return i?.Title == "Pay rent"; }, "phone item on the PC");
        Assert.Equal(1, host.Backups);

        // live: a second phone write arrives without reconnecting
        await PhoneWrites("items", "5002", $$"""{"id":5002,"tab":"SHOP","title":"Milk","createdAt":{{t0}},"updatedAt":{{t0}}}""", t0);
        await WaitUntil(() => { bool has = false; host.Do(() => has = host.Data.Items.Any(x => x.Id == 5002)); return has; }, "live phone write");

        // the PC edits: newer stamp, phone-only field kept, schemaVer 71
        long t1 = Clock.NowMs();
        host.Do(() =>
        {
            var idx = host.Data.Items.FindIndex(x => x.Id == 5001);
            host.Data.Items[idx] = host.Data.Items[idx] with { Title = "Pay rent today", UpdatedAt = t1 };
            sync.LocalChanged();
        });
        var doc = await WaitFor(() => PhoneReads("items", "5001"), d => d.UpdatedAt == t1, "PC write in the cloud");
        var j = JsonNode.Parse(doc.Json)!;
        Assert.Equal("Pay rent today", j["title"]!.GetValue<string>());
        Assert.Equal(42, j["nagFromPhone"]!.GetValue<int>());
        Assert.Equal(SyncContract.SyncSchema, doc.SchemaVer);
        sync.Dispose();
    }

    [Fact]
    public async Task A_stale_PC_never_overwrites_the_phone_and_PC_only_records_are_sent()
    {
        if (Host == null) return;
        long phone = Clock.NowMs() - 10_000, stale = phone - 100_000;
        await PhoneWrites("items", "6001", $$"""{"id":6001,"tab":"TASKS","title":"Phone's newer copy","createdAt":1,"updatedAt":{{phone}}}""", phone);
        var (host, sync) = Boot(d =>
        {
            d.Items.Add(new Item { Id = 6001, Tab = Tab.TASKS, Title = "PC's stale copy", CreatedAt = 1, UpdatedAt = stale });
            d.Items.Add(new Item { Id = 6002, Tab = Tab.TASKS, Title = "Only on the PC", CreatedAt = 1, UpdatedAt = stale });
        });
        sync.SetEnabled(true);
        await WaitUntil(() => sync.InitialSnapshotDone, "first snapshot");
        var only = await WaitFor(() => PhoneReads("items", "6002"), d => d.UpdatedAt == stale, "PC-only record sent");
        Assert.Contains("Only on the PC", only.Json);
        await WaitUntil(() => { string? t = null; host.Do(() => t = host.Data.Items.First(x => x.Id == 6001).Title); return t == "Phone's newer copy"; }, "phone wins");
        var still = await PhoneReads("items", "6001");
        Assert.Equal(phone, still!.Value.UpdatedAt);

        // the phone re-sends an OLDER copy (as Android does on start) → the PC answers with its newer one
        long pcNewer = Clock.NowMs();
        host.Do(() =>
        {
            var idx = host.Data.Items.FindIndex(x => x.Id == 6001);
            host.Data.Items[idx] = host.Data.Items[idx] with { Title = "PC edit", UpdatedAt = pcNewer };
            sync.LocalChanged();
        });
        await WaitFor(() => PhoneReads("items", "6001"), d => d.UpdatedAt == pcNewer, "PC edit sent");
        await PhoneWrites("items", "6001", $$"""{"id":6001,"tab":"TASKS","title":"Phone's newer copy","createdAt":1,"updatedAt":{{phone}}}""", phone);
        var healed = await WaitFor(() => PhoneReads("items", "6001"), d => d.UpdatedAt == pcNewer, "self-heal re-push");
        Assert.Contains("PC edit", healed.Json);
        sync.Dispose();
    }

    [Fact]
    public async Task Settings_round_trip_keeps_the_phones_keys_and_forget_deletes_the_document()
    {
        if (Host == null) return;
        await PhoneWrites("settings", "app", PhoneSettings, 1000);
        await PhoneWrites("items", "7001", """{"id":7001,"tab":"TASKS","title":"Bin me","createdAt":1,"updatedAt":5,"deletedAt":5}""", 5);
        var (host, sync) = Boot();
        sync.SetEnabled(true);
        await WaitUntil(() => sync.InitialSnapshotDone, "first snapshot");
        await WaitUntil(() => { bool ok = false; host.Do(() => ok = host.Data.Settings.ShopLists.Any(l => l.Id == 100)); return ok; }, "phone list on the PC");

        host.Do(() =>
        {
            host.Data.Settings = host.Data.Settings with { ShareHeadingSuffix = " —", Design = Designs.Command };
            sync.LocalChanged();
        });
        var s = await WaitFor(() => PhoneReads("settings", "app"), d => d.Json.Contains(" —"), "PC settings write");
        var o = JsonNode.Parse(s.Json)!.AsObject();
        Assert.Single(o["taskLists"]!.AsArray());
        Assert.False(o["updateAutoCheck"]!.GetValue<bool>());
        Assert.False(o.ContainsKey("design"));
        Assert.True(SyncSettings.StampOf(o) > 1000);
        Assert.Equal(SyncSettings.StampOf(o), s.UpdatedAt);

        // Delete forever (the tombstone was in the Bin) → the document goes too
        host.Do(() => host.Data.Items.RemoveAll(i => i.Id == 7001));
        sync.Forget(SyncContract.Items, 7001);
        await WaitUntil(() => PhoneReads("items", "7001").Result == null, "document deleted");
        Assert.Contains("items/7001", sync.State.Forgotten);
        sync.Dispose();
    }

    [Fact]
    public async Task An_older_schema_settings_document_is_not_adopted()
    {
        if (Host == null) return;
        await PhoneWrites("settings", "app", PhoneSettings.Replace(":-", "OLD"), 1000, schemaVer: 20);
        var (host, sync) = Boot();
        sync.SetEnabled(true);
        await WaitUntil(() => sync.InitialSnapshotDone, "first snapshot");
        await Task.Delay(500);
        host.Do(() => Assert.NotEqual("OLD", host.Data.Settings.ShareHeadingSuffix));
        host.Do(() => Assert.DoesNotContain(host.Data.Settings.ShopLists, l => l.Id == 100));
        Assert.Null(sync.State.RemoteSettings);
        sync.Dispose();
    }
}
