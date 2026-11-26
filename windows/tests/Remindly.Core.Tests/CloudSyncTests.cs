using System.Text.Json.Nodes;
using System.Net;
using System.Net.Http;
using Remindly.Core;
using Remindly.Core.Sync;
using Xunit;

namespace Remindly.Core.Tests;

public class CloudSyncTests
{
    [Fact]
    public void Only_newer_local_records_are_uploaded()
    {
        var remote = new CloudDocument("123", "{\"title\":\"Android\"}", 200, 71);
        Assert.False(CloudWire.ShouldUpload(199, remote));
        Assert.False(CloudWire.ShouldUpload(200, remote));
        Assert.True(CloudWire.ShouldUpload(201, remote));
        Assert.True(CloudWire.ShouldUpload(100, null));
        Assert.False(CloudWire.ShouldUpload(0, null));
        Assert.True(CloudWire.ShouldApply(199, remote));
        Assert.False(CloudWire.ShouldApply(201, remote));
    }

    [Fact]
    public void Windows_edit_does_not_erase_android_only_fields()
    {
        var android = "{\"title\":\"Old\",\"newAndroidAlertField\":{\"keep\":true},\"missedAt\":[17]}";
        var windows = "{\"title\":\"Updated\",\"missedAt\":[17]}";
        var outJson = JsonNode.Parse(CloudWire.PreserveUnknownFields(windows, android))!;
        Assert.Equal("Updated", outJson["title"]!.GetValue<string>());
        Assert.True(outJson["newAndroidAlertField"]!["keep"]!.GetValue<bool>());
        Assert.Equal(17, outJson["missedAt"]![0]!.GetValue<int>());
    }

    [Fact]
    public void Older_android_schema_cannot_drop_newer_desktop_fields()
    {
        var older = "{\"title\":\"Changed on old device\"}";
        var local = "{\"title\":\"Before\",\"alertType\":\"NRA\",\"missedAt\":[1,2]}";
        var merged = JsonNode.Parse(CloudWire.OverlayOlderWriter(older, local))!;
        Assert.Equal("Changed on old device", merged["title"]!.GetValue<string>());
        Assert.Equal("NRA", merged["alertType"]!.GetValue<string>());
        Assert.Equal(2, merged["missedAt"]!.AsArray().Count);
    }

    [Fact]
    public void Firestore_document_decodes_android_wire_envelope()
    {
        var doc = JsonNode.Parse("""
            {"name":"projects/test/databases/(default)/documents/users/uid/items/42",
             "updateTime":"2026-11-04T06:00:00Z",
             "fields":{"json":{"stringValue":"{\"id\":42}"},
                       "updatedAt":{"integerValue":"1670000123000"},
                       "schemaVer":{"integerValue":"71"}}}
            """);
        var decoded = FirestoreTransport.Decode(doc);
        Assert.NotNull(decoded);
        Assert.Equal("42", decoded!.Id);
        Assert.Equal(1670000123000L, decoded.UpdatedAt);
        Assert.Equal(71, decoded.SchemaVer);
        Assert.Equal("2026-11-04T06:00:00Z", decoded.UpdateTime);
    }

    [Fact]
    public void Only_desktop_oauth_clients_are_accepted()
    {
        Assert.Null(OAuthClient.ParseJson("{\"web\":{\"client_id\":\"web-id\"}}"));
        var client = OAuthClient.ParseJson("{\"installed\":{\"client_id\":\"desktop-id\",\"client_secret\":\"example\"}}");
        Assert.Equal("desktop-id", client?.ClientId);
    }

    [Fact]
    public void Bundled_google_client_requires_matching_desktop_registration()
    {
        const string desktop = """{"installed":{"project_id":"example","client_id":"pc-id","client_secret":"example-only"}}""";
        Assert.Equal("pc-id", OAuthClient.ParseJson(desktop, "example")?.ClientId);
        Assert.Null(OAuthClient.ParseJson(desktop, "other-project"));
        Assert.Null(OAuthClient.ParseJson(desktop, ""));
        Assert.Null(OAuthClient.ParseJson(desktop.Replace("installed", "web"), "example"));
        Assert.Null(OAuthClient.ParseJson(desktop.Replace("example-only", " "), "example"));
        Assert.Null(OAuthClient.ParseJson("""{"clientId":"old-format","clientSecret":"example"}""", "example"));
    }

    [Fact]
    public async Task New_profiles_use_the_bundle_and_existing_profiles_keep_their_client()
    {
        var dir = Directory.CreateTempSubdirectory("remindly-client-");
        try
        {
            var config = new FirebaseConfig { ProjectId = "example" };
            var bundled = new OAuthClient("bundled-id", "sample");
            var saved = new OAuthClient("previous-id", "sample");
            var auth = new FirebaseAuth(config, dir.FullName, bundled);
            Assert.True(auth.HasClient);
            Assert.False(auth.HasSavedClient);
            Assert.Equal(bundled, auth.Client);
            Assert.False(await auth.SignInAsync(new CancellationToken(canceled: true)));
            Assert.Equal(AuthState.SignedOut, auth.State);
            Assert.False(new FirebaseAuth(config, dir.FullName, null).HasClient);
            auth.SaveClient(saved);
            var upgraded = new FirebaseAuth(config, dir.FullName, bundled);
            Assert.Equal(saved, upgraded.Client);
            upgraded.ClearClient();
            Assert.Equal(bundled, upgraded.Client);
            Assert.Equal(bundled, new FirebaseAuth(config, dir.FullName, bundled).Client);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Cloud_opt_in_is_off_by_default()
    {
        var s = new AppSettings();
        Assert.False(s.CloudSyncEnabled);
        Assert.False(s.CloudSyncSetupComplete);
    }

    [Fact]
    public async Task Firestore_transport_uses_same_user_root_and_optimistic_write_preconditions()
    {
        var config = new FirebaseConfig { ProjectId = "test-project", ApiKey = "public-test-key" };
        var dir = Path.Combine(Path.GetTempPath(), "remindly-sync-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var auth = new FirebaseAuth(config, dir);
            auth.UseTestSession("android-uid", "test@example.com");
            var calls = new List<(string method, string uri, string? authorization, string? body)>();
            using var http = new HttpClient(new FakeHandler(async r =>
            {
                calls.Add((r.Method.Method, r.RequestUri!.ToString(), r.Headers.Authorization?.ToString(),
                    r.Content == null ? null : await r.Content.ReadAsStringAsync()));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(r.Method == HttpMethod.Get
                        ? """{"documents":[{"name":"projects/test-project/databases/(default)/documents/users/android-uid/items/42","updateTime":"2026-11-04T06:00:00Z","fields":{"json":{"stringValue":"{\"id\":42}"},"updatedAt":{"integerValue":"120"},"schemaVer":{"integerValue":"71"}}}]}"""
                        : r.Method == HttpMethod.Post
                        ? """[{"document":{"name":"projects/test-project/databases/(default)/documents/users/android-uid/items/42","updateTime":"2026-11-04T06:00:01Z","fields":{"json":{"stringValue":"{\"id\":42}"},"updatedAt":{"integerValue":"130"},"schemaVer":{"integerValue":"71"}}}}]"""
                        : "{}"),
                };
            }));
            using var transport = new FirestoreTransport(config, auth, http, "http://127.0.0.1:8080/v1/");
            var records = await transport.ReadCollectionAsync("items", CancellationToken.None);
            Assert.Equal(120L, records["42"].UpdatedAt);
            await transport.WriteAsync("items", new CloudDocument("42", "{\"id\":42}", 130L, 71),
                records["42"], CancellationToken.None);
            Assert.Equal(2, calls.Count);
            Assert.Contains("/users/android-uid/items", calls[0].uri);
            Assert.Equal("Bearer owner", calls[0].authorization);
            Assert.Contains("currentDocument.updateTime=", calls[1].uri);
            Assert.Contains("schemaVer", calls[1].body);
            var updated = await transport.ReadChangedCollectionAsync("items", 120, CancellationToken.None);
            Assert.Equal(130L, updated["42"].UpdatedAt);
            Assert.Equal("POST", calls[2].method);
            Assert.Contains("/users/android-uid:runQuery", calls[2].uri);
            Assert.Contains("GREATER_THAN", calls[2].body);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handle(request);
    }
}
