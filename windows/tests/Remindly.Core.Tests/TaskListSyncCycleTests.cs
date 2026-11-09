using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Remindly.Core;
using Remindly.Core.Sync;
using Xunit;

namespace Remindly.Core.Tests;

public class TaskListSyncCycleTests
{
    [Fact]
    public async Task Real_cycle_pulls_empty_lists_patches_windows_edits_and_recovers_late_records()
    {
        var directory = Path.Combine(Path.GetTempPath(), "remindly-list-cycle-" + Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = new FirebaseConfig { ProjectId = "test-project", ApiKey = "test-only" };
            var auth = new FirebaseAuth(configuration, directory);
            auth.UseTestSession("test-user", "test@example.com");
            using var server = new AndroidShapedServer();
            using var http = new HttpClient(server);
            var transport = new FirestoreTransport(configuration, auth, http, "http://127.0.0.1:8080/v1/");
            var data = new RemindlyData();
            var dataLock = new object();
            long now = 1800000000000L;
            using var sync = new DesktopCloudSync(transport, auth,
                () => { lock (dataLock) return Json.Deserialize(Json.Serialize(data))!; },
                incoming => { lock (dataLock) DataStore.MergeInto(data, incoming, Volatile.Read(ref now)); },
                () => DateTimeOffset.FromUnixTimeMilliseconds(Volatile.Read(ref now)));
            var firstCycle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            sync.Changed += () =>
            {
                if (sync.Status == CloudStatus.UpToDate) firstCycle.TrySetResult(true);
                if (sync.Status == CloudStatus.Error) firstCycle.TrySetException(new Exception(sync.LastError));
            };
            sync.SetEnabled(true);
            await firstCycle.Task.WaitAsync(TimeSpan.FromSeconds(10));
            lock (dataLock)
            {
                Assert.Equal(9, data.Settings.TaskLists.Count);
                Assert.Single(data.Items);
                Assert.Equal(101L, data.Items[0].ListId);
                Assert.Empty(TaskLists.Members(data.Items, 109, data.Settings.TaskLists));
                data.Settings = TaskLists.Mirror(data.Settings with
                {
                    TaskLists = data.Settings.TaskLists.Append(new TaskList
                    { Id = 500, Name = "Created on desktop", CreatedAt = now, UpdatedAt = now }).ToList(),
                });
            }
            await sync.SyncNowAsync();
            var cloudSettings = JsonNode.Parse(server.Settings.Json)!;
            Assert.Contains(cloudSettings["taskLists"]!.AsArray(), n => n?["id"]?.GetValue<long>() == 500);
            Assert.Equal("preserved", cloudSettings["androidOnly"]!.GetValue<string>());
            Assert.Null(cloudSettings["design"]);

            // Simulate an old offline write arriving after a newer document advanced the watermark.
            server.Items["2"] = AndroidShapedServer.Item("2", 50);
            await sync.SyncCycleAsync(false);
            lock (dataLock) Assert.DoesNotContain(data.Items, i => i.Id == 2);
            int reads = server.FullItemReads;
            await sync.SyncNowAsync();
            Assert.True(server.FullItemReads > reads);
            lock (dataLock) Assert.Contains(data.Items, i => i.Id == 2);

            // Incremental polling must not postpone the scheduled full read forever.
            Interlocked.Add(ref now, (long)TimeSpan.FromHours(1).TotalMilliseconds);
            await sync.SyncCycleAsync(false);
            reads = server.FullItemReads;
            Interlocked.Add(ref now, (long)TimeSpan.FromHours(23).TotalMilliseconds);
            await sync.SyncCycleAsync(false);
            Assert.True(server.FullItemReads > reads);
            Assert.Equal(CloudStatus.UpToDate, sync.Status);
            sync.SetEnabled(false);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private sealed class AndroidShapedServer : HttpMessageHandler
    {
        private const string Root = "projects/test-project/databases/(default)/documents/users/test-user";
        public readonly ConcurrentDictionary<string, CloudDocument> Items = new();
        public CloudDocument Settings { get; private set; }
        private int _fullItemReads;
        public int FullItemReads => Volatile.Read(ref _fullItemReads);

        public AndroidShapedServer()
        {
            Items["1"] = Item("1", 1000);
            Settings = new CloudDocument("app", new JsonObject
            {
                ["settingsUpdatedAt"] = 100,
                ["androidOnly"] = "preserved",
                ["shopLists"] = new JsonArray(),
                ["taskLists"] = JsonSerializer.SerializeToNode(Enumerable.Range(1, 9).Select(i =>
                    new TaskList { Id = 100 + i, Name = "List " + i, CreatedAt = 10, UpdatedAt = 100 }), Json.Options),
            }.ToJsonString(), 100, 71, "2026-11-09T00:00:00Z");
        }

        public static CloudDocument Item(string id, long stamp) => new(id,
            JsonSerializer.Serialize(new Remindly.Core.Item
            { Id = long.Parse(id), Tab = Tab.TASKS, Title = "Synthetic task " + id, ListId = 101, UpdatedAt = stamp, CreatedAt = stamp }, Json.Options),
            stamp, 71, "2026-11-09T00:00:00Z");

        private static JsonObject Encode(string collection, CloudDocument doc) => new()
        {
            ["name"] = Root + "/" + collection + "/" + doc.Id,
            ["updateTime"] = doc.UpdateTime ?? "2026-11-09T00:00:01Z",
            ["fields"] = new JsonObject
            {
                ["json"] = new JsonObject { ["stringValue"] = doc.Json },
                ["updatedAt"] = new JsonObject { ["integerValue"] = doc.UpdatedAt.ToString() },
                ["schemaVer"] = new JsonObject { ["integerValue"] = doc.SchemaVer.ToString() },
            },
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            JsonNode result;
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Patch)
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
                body["name"] = Root + "/settings/app";
                body["updateTime"] = "2026-11-09T00:00:01Z";
                Settings = FirestoreTransport.Decode(body)!;
                result = body;
            }
            else if (path.EndsWith("/settings/app")) result = Encode("settings", Settings);
            else if (request.Method == HttpMethod.Post)
            {
                var query = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!["structuredQuery"]!;
                var collection = query["from"]![0]!["collectionId"]!.GetValue<string>();
                long watermark = long.Parse(query["where"]!["fieldFilter"]!["value"]!["integerValue"]!.GetValue<string>());
                var rows = new JsonArray();
                if (collection == "items")
                    foreach (var item in Items.Values.Where(i => i.UpdatedAt > watermark))
                        rows.Add(new JsonObject { ["document"] = Encode("items", item) });
                result = rows;
            }
            else
            {
                var docs = new JsonArray();
                if (path.EndsWith("/items"))
                {
                    Interlocked.Increment(ref _fullItemReads);
                    foreach (var item in Items.Values) docs.Add(Encode("items", item));
                }
                result = new JsonObject { ["documents"] = docs };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(result.ToJsonString()) };
        }
    }
}
