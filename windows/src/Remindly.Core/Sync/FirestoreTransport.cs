using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Remindly.Core.Sync;

/// <summary>One Firestore document in the Android JSON-envelope format.</summary>
public sealed record CloudDocument(string Id, string Json, long UpdatedAt, int SchemaVer, string? UpdateTime = null);

/// <summary>
/// Authenticated Firestore REST transport. Uses Firebase user ID tokens, so Firestore security
/// rules are evaluated for precisely the same /users/{uid} root as on Android.
/// </summary>
public sealed class FirestoreTransport : IDisposable
{
    private readonly FirebaseConfig _config;
    private readonly FirebaseAuth _auth;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _apiBase;

    public FirestoreTransport(FirebaseConfig config, FirebaseAuth auth, HttpClient? http = null,
        string apiBase = "https://firestore.googleapis.com/v1/")
    {
        _config = config;
        _auth = auth;
        _ownsHttp = http == null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        _apiBase = apiBase.TrimEnd('/') + "/";
    }

    private string Root => _apiBase + _config.DocumentsRoot
        + "/users/" + Uri.EscapeDataString(_auth.Uid ?? throw new InvalidOperationException("Not signed in"));

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, JsonNode? body, CancellationToken ct)
    {
        var token = await _auth.GetIdTokenAsync(ct);
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            // Do not log the request, bearer token, record JSON or arbitrary server body.
            throw new HttpRequestException($"Firestore returned {(int)response.StatusCode} ({response.ReasonPhrase}); check network, authentication and security rules.");
        }
        return response;
    }

    internal static CloudDocument? Decode(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;
        var name = obj["name"]?.GetValue<string>();
        var json = obj["fields"]?["json"]?["stringValue"]?.GetValue<string>();
        if (name == null || json == null) return null;
        long.TryParse(obj["fields"]?["updatedAt"]?["integerValue"]?.ToString(), out var stamp);
        int.TryParse(obj["fields"]?["schemaVer"]?["integerValue"]?.ToString(), out var schema);
        return new CloudDocument(name[(name.LastIndexOf('/') + 1)..], json, stamp, schema,
            obj["updateTime"]?.GetValue<string>());
    }

    public async Task<Dictionary<string, CloudDocument>> ReadCollectionAsync(string collection, CancellationToken ct)
    {
        var documents = new Dictionary<string, CloudDocument>();
        string? next = null;
        do
        {
            var url = Root + "/" + Uri.EscapeDataString(collection) + "?pageSize=300";
            if (!string.IsNullOrEmpty(next)) url += "&pageToken=" + Uri.EscapeDataString(next);
            using var response = await SendAsync(HttpMethod.Get, url, null, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) break;
            var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            if (payload?["documents"] is JsonArray arr)
                foreach (var node in arr)
                    if (Decode(node) is { } doc) documents[doc.Id] = doc;
            next = payload?["nextPageToken"]?.GetValue<string>();
        } while (!string.IsNullOrEmpty(next));
        return documents;
    }

    public async Task<CloudDocument?> ReadSettingsAsync(CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, Root + "/settings/app", null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        return Decode(JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)));
    }

    /// <summary>Optimistic precondition: concurrent Android writes cannot be silently overwritten.</summary>
    public async Task WriteAsync(string collection, CloudDocument desired, CloudDocument? previouslyRead, CancellationToken ct)
    {
        var url = Root + "/" + Uri.EscapeDataString(collection) + "/" + Uri.EscapeDataString(desired.Id);
        url += previouslyRead?.UpdateTime is { Length: > 0 } time
            ? "?currentDocument.updateTime=" + Uri.EscapeDataString(time)
            : "?currentDocument.exists=false";
        var payload = new JsonObject
        {
            ["fields"] = new JsonObject
            {
                ["json"] = new JsonObject { ["stringValue"] = desired.Json },
                ["updatedAt"] = new JsonObject { ["integerValue"] = desired.UpdatedAt.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                ["schemaVer"] = new JsonObject { ["integerValue"] = desired.SchemaVer.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            },
        };
        using var response = await SendAsync(HttpMethod.Patch, url, payload, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new HttpRequestException("Firestore target document vanished during sync; retry will reconcile it.");
    }

    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}

/// <summary>Pure conflict and wire handling, independent of Firebase and WPF.</summary>
public static class CloudWire
{
    public const int Schema = 71;

    /// <summary>Overlay known desktop fields while retaining Android-only JSON keys.</summary>
    public static string PreserveUnknownFields(string windowsJson, string? androidJson)
    {
        var local = JsonNode.Parse(windowsJson) as JsonObject ?? throw new ArgumentException("Invalid local record");
        if (androidJson == null) return local.ToJsonString();
        var remote = JsonNode.Parse(androidJson) as JsonObject ?? throw new ArgumentException("Invalid remote record");
        foreach (var field in local) remote[field.Key] = field.Value?.DeepClone();
        return remote.ToJsonString();
    }

    /// <summary>An older Android writer must not clear a field absent from its JSON payload.</summary>
    public static string OverlayOlderWriter(string olderJson, string currentJson)
    {
        var old = JsonNode.Parse(olderJson) as JsonObject ?? throw new ArgumentException("Invalid older record");
        var current = JsonNode.Parse(currentJson) as JsonObject ?? throw new ArgumentException("Invalid current record");
        foreach (var field in old) current[field.Key] = field.Value?.DeepClone();
        return current.ToJsonString();
    }

    public static bool ShouldUpload(long localStamp, CloudDocument? remote) =>
        localStamp > 0 && (remote == null || localStamp > remote.UpdatedAt);

    public static bool ShouldApply(long localStamp, CloudDocument remote) =>
        remote.UpdatedAt > 0 && remote.UpdatedAt > localStamp;
}
