using System.Text.Json;
using System.Text.Json.Nodes;
using Remindly.Core;
using Remindly.Core.Sync;
using Xunit;

namespace Remindly.Core.Tests;

/// <summary>2.16 Cloud sync: the Firestore contract shared with Android Sync.kt, and the sign-in helpers.</summary>
public class SyncTests
{
    private const long Now = 1_793_800_000_000L;   // 4 Nov 2026

    /// <summary>An item as a newer Android build writes it: fields Windows models plus two it does not.</summary>
    private const string PhoneItem = """
        {"id":5001,"tab":"TASKS","title":"Pay rent","notes":"","createdAt":1793000000000,"dueAt":null,"priority":"HIGH",
         "alertType":"N","repeatMode":"OFF","repeatDays":[],"updatedAt":1793700000000,"deletedAt":null,
         "listId":777,"futureField":{"a":1},"anotherOne":[1,2,3]}
        """;

    private static RemoteDoc Doc(string json, long stamp, int ver = SyncContract.SyncSchema) =>
        new(JsonNode.Parse(json)!["id"]!.ToString(), json, ver, stamp);

    private static RemindlyData Data(params Item[] items) => new() { Items = items.ToList() };

    // ── wire format ──

    [Fact]
    public void Unknown_phone_fields_survive_read_edit_and_write()
    {
        var item = JsonSerializer.Deserialize<Item>(PhoneItem, Json.Options)!;
        Assert.Equal(777, item.ListId);
        var edited = item with { Title = "Pay rent today", UpdatedAt = Now };
        var wire = JsonNode.Parse(SyncContract.Encode(edited))!.AsObject();
        Assert.Equal("Pay rent today", wire["title"]!.GetValue<string>());
        Assert.Equal(1, wire["futureField"]!["a"]!.GetValue<int>());
        Assert.Equal(3, wire["anotherOne"]!.AsArray().Count);
        Assert.Equal("TASKS", wire["tab"]!.GetValue<string>());
        Assert.True(wire.ContainsKey("dueAt"), "nulls are written, so a cleared field travels as an explicit null");
    }

    [Fact]
    public void Call_display_is_not_invented_but_the_phones_display_passes_through()
    {
        var mine = new CallReminder { Id = 1, Number = "+91 98300 12345", FirstName = "Asha" };
        Assert.False(JsonNode.Parse(SyncContract.Encode(mine))!.AsObject().ContainsKey("display"));
        var phone = JsonSerializer.Deserialize<CallReminder>("""{"id":2,"number":"1","display":"Bank","nagAt":123}""", Json.Options)!;
        var back = JsonNode.Parse(SyncContract.Encode(phone))!.AsObject();
        Assert.Equal("Bank", back["display"]!.GetValue<string>());
        Assert.Equal(123, back["nagAt"]!.GetValue<long>());
    }

    [Fact]
    public void Document_names_split_into_collection_and_id()
    {
        Assert.Equal(("items", "123"), SyncContract.SplitName("projects/p/databases/(default)/documents/users/U1/items/123"));
        Assert.Equal(("settings", "app"), SyncContract.SplitName("projects/p/databases/(default)/documents/users/U1/settings/app"));
        Assert.Null(SyncContract.SplitName("projects/p/databases/(default)/documents/shares/9").Col);
    }

    [Fact]
    public void Local_records_use_createdAt_when_an_item_was_never_updated()
    {
        var d = Data(new Item { Id = 9, CreatedAt = 50, UpdatedAt = 0 });
        d.Shops.Add(new Shop { Id = 10, Name = "D-Mart", UpdatedAt = 70 });
        var recs = SyncMerge.LocalRecords(d).ToDictionary(r => r.Key);
        Assert.Equal(50, recs["items/9"].Stamp);
        Assert.Equal(70, recs["shops/10"].Stamp);
        Assert.DoesNotContain(recs.Keys, k => k.StartsWith("productlinks"));
    }

    // ── last writer wins ──

    [Fact]
    public void A_record_only_the_phone_has_is_added()
    {
        var d = Data();
        var r = SyncMerge.Apply(d, SyncContract.Items, new[] { Doc(PhoneItem, 1793700000000) }, new HashSet<string>(), Now);
        Assert.Equal(1, r.Changed);
        Assert.Equal("Pay rent", d.Items.Single().Title);
    }

    [Fact]
    public void Newer_remote_wins_older_remote_asks_for_ours_again()
    {
        var local = new Item { Id = 5001, Tab = Tab.TASKS, Title = "Local", CreatedAt = 1, UpdatedAt = 1793600000000 };
        var d = Data(local);
        var newer = SyncMerge.Apply(d, SyncContract.Items, new[] { Doc(PhoneItem, 1793700000000) }, new HashSet<string>(), Now);
        Assert.Equal(1, newer.Changed);
        Assert.Equal("Pay rent", d.Items.Single().Title);

        var mine = d.Items[0] with { Title = "Mine, newer", UpdatedAt = 1793750000000 };
        d.Items[0] = mine;
        var older = SyncMerge.Apply(d, SyncContract.Items, new[] { Doc(PhoneItem, 1793700000000) }, new HashSet<string>(), Now);
        Assert.Equal(0, older.Changed);
        Assert.Equal("Mine, newer", d.Items.Single().Title);
        var again = Assert.Single(older.LocalNewer);
        Assert.Equal("items/5001", again.Key);
        Assert.Contains("Mine, newer", again.Json());
    }

    [Fact]
    public void The_same_copy_coming_back_changes_nothing()
    {
        var d = Data();
        SyncMerge.Apply(d, SyncContract.Items, new[] { Doc(PhoneItem, 1793700000000) }, new HashSet<string>(), Now);
        var echo = SyncContract.Encode(d.Items[0]);
        var r = SyncMerge.Apply(d, SyncContract.Items, new[] { Doc(echo, 1793700000000) }, new HashSet<string>(), Now);
        Assert.Equal(0, r.Changed);
        Assert.Empty(r.LocalNewer);
    }

    [Fact]
    public void Deleted_for_good_on_this_PC_stays_gone_but_a_live_copy_returns()
    {
        var tomb = """{"id":42,"tab":"TASKS","title":"Old","createdAt":1,"updatedAt":1793700000000,"deletedAt":1793700000000}""";
        var forgotten = new HashSet<string> { "items/42" };
        var d = Data();
        Assert.Equal(0, SyncMerge.Apply(d, SyncContract.Items, new[] { Doc(tomb, 1) }, forgotten, Now).Changed);
        Assert.Empty(d.Items);

        var live = """{"id":42,"tab":"TASKS","title":"Back","createdAt":1,"updatedAt":1793710000000}""";
        Assert.Equal(1, SyncMerge.Apply(d, SyncContract.Items, new[] { Doc(live, 1) }, forgotten, Now).Changed);
        Assert.DoesNotContain("items/42", forgotten);
    }

    [Fact]
    public void A_tombstone_older_than_the_Bin_keeps_is_not_brought_back()
    {
        long old = Now - 40L * 86_400_000L;
        var tomb = $$"""{"id":43,"tab":"SHOP","title":"Old milk","createdAt":1,"updatedAt":{{old}},"deletedAt":{{old}}}""";
        var d = Data();
        Assert.Equal(0, SyncMerge.Apply(d, SyncContract.Items, new[] { Doc(tomb, old) }, new HashSet<string>(), Now).Changed);
        Assert.Empty(d.Items);
    }

    [Fact]
    public void An_older_writer_only_replaces_the_fields_it_knows()
    {
        var local = new Item { Id = 7, Tab = Tab.TASKS, Title = "Old title", AlertType = "R", MissedAt = new() { 5 }, CreatedAt = 1, UpdatedAt = 10 };
        var d = Data(local);
        var oldWriter = """{"id":7,"tab":"TASKS","title":"New title","createdAt":1,"updatedAt":20}""";
        var r = SyncMerge.Apply(d, SyncContract.Items, new[] { Doc(oldWriter, 20, ver: 0) }, new HashSet<string>(), Now);
        Assert.Equal(1, r.Changed);
        var got = d.Items.Single();
        Assert.Equal("New title", got.Title);
        Assert.Equal("R", got.AlertType);
        Assert.Equal(new List<long> { 5 }, got.MissedAt);
        Assert.Contains(r.Warnings, w => w.Contains("older sync schema"));
    }

    [Fact]
    public void An_unreadable_document_is_skipped_not_fatal()
    {
        var d = Data();
        var r = SyncMerge.Apply(d, SyncContract.Items, new[] { new RemoteDoc("1", "{not json", 71, 1) }, new HashSet<string>(), Now);
        Assert.Equal(0, r.Changed);
        Assert.Single(r.Warnings);
    }

    [Fact]
    public void Every_record_collection_merges()
    {
        var d = new RemindlyData();
        foreach (var (col, json) in new[]
        {
            (SyncContract.Calls, """{"id":1,"number":"123","createdAt":5,"updatedAt":5}"""),
            (SyncContract.Places, """{"id":2,"name":"Home","lat":1.5,"lng":2.5,"radius":100,"updatedAt":5}"""),
            (SyncContract.Shops, """{"id":3,"name":"D-Mart","updatedAt":5}"""),
            (SyncContract.Cities, """{"id":4,"name":"Pune","updatedAt":5}"""),
            (SyncContract.Chains, """{"id":5,"name":"D-Mart","updatedAt":5}"""),
            (SyncContract.Products, """{"id":6,"name":"Milk","category":"Dairy","updatedAt":5}"""),
        })
            Assert.Equal(1, SyncMerge.Apply(d, col, new[] { Doc(json, 5) }, new HashSet<string>(), Now).Changed);
        Assert.Single(d.Calls!);
        Assert.Single(d.Places);
        Assert.Single(d.Shops);
        Assert.Single(d.Cities);
        Assert.Single(d.Chains);
        Assert.Single(d.Products);
        Assert.Contains(SyncMerge.Apply(d, "productlinks", Array.Empty<RemoteDoc>(), new HashSet<string>(), Now).Warnings, w => w.Contains("not synced"));
    }

    // ── the settings document ──

    private const string PhoneSettings = """
        {"ver":43,"taskLists":[{"id":1,"name":"Home","updatedAt":5}],"updateAutoCheck":false,"updateBeta":true,"fontScale":1.2,
         "shopLists":[{"id":100,"name":"Groceries","order":0,"createdAt":1,"updatedAt":50},{"id":200,"name":"Party","order":1,"createdAt":1,"updatedAt":50}],
         "shopDefaultListId":100,"shareIncludeDone":true,"shareIncludeQty":false,"shareUrgentTag":true,"shareBoughtTag":true,"shareHeadingSuffix":" -",
         "shopGroups":["Groceries","Party"],"groupIcons":{},"shopDefaultGroup":"Groceries","settingsUpdatedAt":1000}
        """;

    private static JsonObject Phone() => JsonNode.Parse(PhoneSettings)!.AsObject();

    [Fact]
    public void Settings_are_never_written_before_the_phone_published_them()
    {
        Assert.Null(SyncSettings.BuildPush(new AppSettings(), null, 5));
    }

    [Fact]
    public void A_settings_write_keeps_every_phone_key_and_never_leaks_this_PCs_own()
    {
        var mine = new AppSettings { ShareHeadingSuffix = ":-", Design = Designs.Command, CloseToTray = false, UpdateAutoCheck = true, CloudSync = true };
        var doc = SyncSettings.BuildPush(mine, Phone(), 2000)!;
        Assert.Equal(":-", doc["shareHeadingSuffix"]!.GetValue<string>());
        Assert.Equal(2000, doc["settingsUpdatedAt"]!.GetValue<long>());
        Assert.Single(doc["taskLists"]!.AsArray());
        Assert.False(doc["updateAutoCheck"]!.GetValue<bool>());           // the phone's own switch, unchanged
        Assert.Equal(1.2, doc["fontScale"]!.GetValue<double>());
        foreach (var key in new[] { "design", "closeToTray", "cloudSync", "startWithWindows", "deviceTag", "lastMode", "updatePinnedVersion" })
            Assert.False(doc.ContainsKey(key), key + " must stay on this PC");
    }

    [Fact]
    public void Lists_merge_per_list_even_when_the_document_is_older()
    {
        var mine = new AppSettings
        {
            ShareHeadingSuffix = ":-",
            ShopLists = new() { T.List(100, "Groceries (mine)") with { UpdatedAt = 90 }, T.List(200, "Party") with { UpdatedAt = 10 } },
        };
        var s = SyncSettings.ApplyRemote(mine, Phone(), adoptValues: false);
        Assert.Equal("Groceries (mine)", s.ShopLists.Single(l => l.Id == 100).Name);   // ours is newer
        Assert.Equal(50, s.ShopLists.Single(l => l.Id == 200).UpdatedAt);              // the phone's is newer
        Assert.Equal(":-", s.ShareHeadingSuffix);                                      // values not adopted
        Assert.True(SyncSettings.LocalHasNewerLists(s, Phone()));
    }

    [Fact]
    public void A_newer_document_brings_the_shared_values_but_not_the_phones_own_switches()
    {
        var mine = new AppSettings { UpdateAutoCheck = true, UpdateBeta = false, Design = Designs.Hub, ShareIncludeDone = false };
        var s = SyncSettings.ApplyRemote(mine, Phone(), adoptValues: true);
        Assert.True(s.ShareIncludeDone);
        Assert.False(s.ShareIncludeQty);
        Assert.Equal(" -", s.ShareHeadingSuffix);
        Assert.Equal(100, s.ShopDefaultListId);
        Assert.Equal(2, s.ShopLists.Count);
        Assert.True(s.UpdateAutoCheck);
        Assert.False(s.UpdateBeta);
        Assert.Equal(Designs.Hub, s.Design);
        Assert.False(SyncSettings.LocalHasNewerLists(s, Phone()));
    }

    [Fact]
    public void The_settings_fingerprint_follows_only_shared_keys()
    {
        var a = new AppSettings();
        Assert.Equal(SyncSettings.Hash(a), SyncSettings.Hash(a with { Design = Designs.Board, CloseToTray = false, CloudSync = false }));
        Assert.NotEqual(SyncSettings.Hash(a), SyncSettings.Hash(a with { ShareHeadingSuffix = " -" }));
        Assert.Equal(1000, SyncSettings.StampOf(Phone()));
    }

    // ── sign-in helpers ──

    [Fact]
    public void Pkce_matches_the_RFC_7636_example()
    {
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
        var v = Pkce.NewVerifier();
        Assert.InRange(v.Length, 43, 128);
        Assert.DoesNotContain('=', v);
    }

    [Fact]
    public void The_loopback_request_is_parsed()
    {
        var r = LoopbackRequest.Parse("GET /?state=ab%2Bc&code=4%2F0AQ-x&scope=email+openid HTTP/1.1\r\nHost: 127.0.0.1:5000\r\n\r\n")!;
        Assert.Equal("/", r.Path);
        Assert.Equal("4/0AQ-x", r.Query["code"]);
        Assert.Equal("ab+c", r.Query["state"]);
        Assert.Equal("email openid", r.Query["scope"]);
        Assert.Equal("/favicon.ico", LoopbackRequest.Parse("GET /favicon.ico HTTP/1.1\r\n\r\n")!.Path);
        Assert.Null(LoopbackRequest.Parse("POST / HTTP/1.1\r\n\r\n"));
        Assert.Null(LoopbackRequest.Parse(""));
    }

    [Fact]
    public void The_browser_page_escapes_what_it_shows()
    {
        var page = SignInPages.Success("a<b>@x.com");
        Assert.Contains("signed in to Remindly", page);
        Assert.Contains("a&lt;b&gt;@x.com", page);
        Assert.DoesNotContain("<b>", page);
    }

    [Fact]
    public void Google_and_Firebase_errors_are_read()
    {
        Assert.Equal("INVALID_REFRESH_TOKEN", GoogleAuth.ErrorMessage("""{"error":{"code":400,"message":"INVALID_REFRESH_TOKEN"}}"""));
        Assert.Equal("invalid_grant (Bad Request)", GoogleAuth.ErrorMessage("""{"error":"invalid_grant","error_description":"Bad Request"}"""));
        Assert.Equal("plain text", GoogleAuth.ErrorMessage("plain text"));
    }

    [Fact]
    public void Configuration_files_are_read()
    {
        var cfg = FirebaseConfig.Parse("""{"project_info":{"project_id":"remindly-5c1e6","project_number":"1"},"client":[{"api_key":[{"current_key":"AIzaTEST"}]}]}""")!;
        Assert.Equal("remindly-5c1e6", cfg.ProjectId);
        Assert.Equal("AIzaTEST", cfg.ApiKey);
        Assert.Equal("projects/remindly-5c1e6/databases/(default)", cfg.Database);
        Assert.Null(FirebaseConfig.Parse("{}"));

        var c = OAuthClient.Parse("""{"installed":{"client_id":"123-abc.apps.googleusercontent.com","client_secret":"s"}}""")!;
        Assert.Equal("123-abc.apps.googleusercontent.com", c.ClientId);
        Assert.Equal(c, OAuthClient.Parse(c.ToJson()));
        Assert.Null(OAuthClient.Parse("""{"installed":{"client_id":"not-a-google-client"}}"""));
        Assert.Contains("match /users/{uid}", FirebaseConfig.Rules());
    }

    [Fact]
    public void Tokens_are_kept_outside_the_data_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "remindly-sync-" + Guid.NewGuid().ToString("N"));
        try
        {
            var auth = new GoogleAuth(null, dir);
            Assert.False(auth.IsConfigured);
            auth.UseTestSession("uid-1", "me@example.com");
            Assert.True(auth.IsSignedIn);
            auth.SignOut();
            Assert.False(auth.IsSignedIn);
            Assert.False(File.Exists(Path.Combine(dir, "session.bin")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
