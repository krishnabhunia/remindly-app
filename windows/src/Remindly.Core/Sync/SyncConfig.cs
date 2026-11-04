using System.Reflection;
using System.Text.Json.Nodes;

namespace Remindly.Core.Sync;

/// <summary>
/// Which Firebase project to talk to: the project id and Web API key from the same google-services.json
/// the Android build uses (a Firebase API key identifies the project — access is decided by the Firestore
/// rules and the signed-in user's token). CI writes the file next to this source before building and
/// deletes it afterwards; it is embedded into the exe and never committed.
/// </summary>
public sealed class FirebaseConfig
{
    public string ProjectId { get; init; } = "";
    public string ApiKey { get; init; } = "";
    public string Database => $"projects/{ProjectId}/databases/(default)";
    public string DocumentsRoot => Database + "/documents";

    public static FirebaseConfig? Parse(string json)
    {
        try
        {
            var root = JsonNode.Parse(json)!.AsObject();
            var id = root["project_info"]?["project_id"]?.GetValue<string>();
            var key = root["client"]?.AsArray().Select(c => c?["api_key"]?.AsArray().FirstOrDefault()?["current_key"]?.GetValue<string>())
                .FirstOrDefault(k => !string.IsNullOrWhiteSpace(k));
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(key)) return null;
            return new FirebaseConfig { ProjectId = id.Trim(), ApiKey = key.Trim() };
        }
        catch { return null; }
    }

    /// <summary>The configuration built into this exe, or null in a build made without it (local developer builds).</summary>
    public static FirebaseConfig? Embedded() => Resource("google-services.json") is string j ? Parse(j) : null;

    /// <summary>The Firestore security rules (docs/FIRESTORE-RULES.rules) — "Copy the rules" on the problem card.</summary>
    public static string Rules() => Resource("firestore.rules") ?? "";

    /// <summary>The Firebase console page where the rules are published.</summary>
    public string RulesConsoleUrl => $"https://console.firebase.google.com/project/{ProjectId}/firestore/rules";

    internal static string? Resource(string suffix)
    {
        var asm = typeof(FirebaseConfig).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (name == null) return null;
        using var s = asm.GetManifestResourceStream(name);
        if (s == null) return null;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}

/// <summary>
/// The Google "Desktop app" OAuth client (created once in Google Cloud, same project as Firebase, so the
/// PC gets the SAME Firebase user id as the phone). Google treats an installed app's client secret as
/// not confidential; it is still kept out of the repository and embedded by CI.
/// </summary>
public sealed record OAuthClient(string ClientId, string ClientSecret)
{
    /// <summary>Accepts the client_secret_*.json Google offers for download, or a bare {client_id, client_secret}.</summary>
    public static OAuthClient? Parse(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root) return null;
            var inner = root["installed"] as JsonObject ?? root["web"] as JsonObject ?? root;
            var id = inner["client_id"]?.GetValue<string>() ?? inner["clientId"]?.GetValue<string>();
            var secret = inner["client_secret"]?.GetValue<string>() ?? inner["clientSecret"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id) || !id.Trim().EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal)) return null;
            return new OAuthClient(id.Trim(), (secret ?? "").Trim());
        }
        catch { return null; }
    }

    public string ToJson() => new JsonObject { ["installed"] = new JsonObject { ["client_id"] = ClientId, ["client_secret"] = ClientSecret } }.ToJsonString();

    /// <summary>The client built into this exe, if CI had it.</summary>
    public static OAuthClient? Embedded() => FirebaseConfig.Resource("oauth-desktop-client.json") is string j ? Parse(j) : null;
}
