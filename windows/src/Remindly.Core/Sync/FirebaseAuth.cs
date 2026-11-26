using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Remindly.Core.Sync;

/// <summary>A Google Desktop app OAuth registration for the Firebase project.</summary>
public sealed record OAuthClient(string ClientId, string ClientSecret)
{
    /// <summary>Accepts the client_secret_*.json Google offers for download, or a bare {clientId, clientSecret}.</summary>
    public static OAuthClient? ParseJson(string json, string? expectedProjectId = null)
    {
        try
        {
            var root = JsonNode.Parse(json) as JsonObject;
            if (root == null) return null;
            // A Web OAuth client does not permit the desktop loopback redirect.
            if (root.ContainsKey("web")) return null;
            var installed = root["installed"] as JsonObject;
            if (expectedProjectId != null)
            {
                // Official builds use Google's downloaded Desktop format for the same Firebase project.
                if (string.IsNullOrWhiteSpace(expectedProjectId) ||
                    installed?["project_id"]?.GetValue<string>() != expectedProjectId ||
                    string.IsNullOrWhiteSpace(installed?["client_id"]?.GetValue<string>()) ||
                    string.IsNullOrWhiteSpace(installed?["client_secret"]?.GetValue<string>()))
                    return null;
            }
            var inner = installed ?? root;
            var id = inner["client_id"]?.GetValue<string>() ?? inner["clientId"]?.GetValue<string>();
            var secret = inner["client_secret"]?.GetValue<string>() ?? inner["clientSecret"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id)) return null;
            return new OAuthClient(id.Trim(), (secret ?? "").Trim());
        }
        catch { return null; }
    }

    internal static OAuthClient? LoadBundled(string projectId)
    {
        using var stream = typeof(OAuthClient).Assembly.GetManifestResourceStream("google-desktop-client.json");
        if (stream == null) return null;
        using var reader = new StreamReader(stream);
        return ParseJson(reader.ReadToEnd(), projectId);
    }

    public string ToJson() => new JsonObject { ["installed"] = new JsonObject { ["client_id"] = ClientId, ["client_secret"] = ClientSecret } }.ToJsonString();
}

public sealed class AuthSession
{
    public string Uid { get; set; } = "";
    public string Email { get; set; } = "";
    public string? DisplayName { get; set; }
    public string FirebaseRefreshToken { get; set; } = "";
    public string? GoogleRefreshToken { get; set; }
    public OAuthClient? GoogleRefreshClient { get; set; }
    public string IdToken { get; set; } = "";
    public long IdTokenExpiresAt { get; set; }
}

public enum AuthState { SignedOut, SigningIn, SignedIn, Error }

/// <summary>
/// Google Sign-In for a desktop app (OAuth 2.0 loopback + PKCE, system browser — Google blocks
/// embedded web views), exchanged for a Firebase Auth session through the Identity Toolkit REST
/// API. Because the Desktop client lives in the SAME Google Cloud project as the Android app,
/// the resulting Firebase uid is the SAME uid the phone uses, so both devices read and write
/// /users/{uid}/… exactly like SyncAuth.linkFromGoogle on Android.
/// </summary>
public sealed class FirebaseAuth
{
    private readonly FirebaseConfig _cfg;
    private readonly string _dir;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(40) };
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public const string Scopes = "openid email profile";

    public AuthSession? Session { get; private set; }
    private readonly OAuthClient? _bundledClient;
    private OAuthClient? _savedClient;
    public OAuthClient? Client => _savedClient ?? _bundledClient;
    public bool HasBundledClient => _bundledClient != null;
    public bool HasSavedClient => _savedClient != null;
    public AuthState State { get; private set; } = AuthState.SignedOut;
    public string? LastError { get; private set; }
    public event Action? Changed;
    public Action<string, Exception?>? Log { get; set; }

    private string ClientPath => Path.Combine(_dir, "oauth-client.bin");
    private string SessionPath => Path.Combine(_dir, "session.bin");

    public FirebaseAuth(FirebaseConfig cfg, string secureDir)
        : this(cfg, secureDir, OAuthClient.LoadBundled(cfg.ProjectId)) { }

    internal FirebaseAuth(FirebaseConfig cfg, string secureDir, OAuthClient? bundledClient)
    {
        _bundledClient = bundledClient;
        _cfg = cfg; _dir = secureDir;
        Directory.CreateDirectory(_dir);
        var c = SecureStore.Read(ClientPath);
        if (c != null) _savedClient = OAuthClient.ParseJson(c);
        var s = SecureStore.Read(SessionPath);
        if (s != null)
        {
            try { Session = JsonSerializer.Deserialize<AuthSession>(s); } catch { Session = null; }
            if (Session != null && !string.IsNullOrEmpty(Session.FirebaseRefreshToken)) State = AuthState.SignedIn;
        }
    }

    /// <summary>Tests only: pretend a user is signed in (emulator accepts any bearer token).</summary>
    public void UseTestSession(string uid, string email)
    {
        Session = new AuthSession { Uid = uid, Email = email, FirebaseRefreshToken = "test", IdToken = "owner", IdTokenExpiresAt = long.MaxValue };
        State = AuthState.SignedIn;
        Changed?.Invoke();
    }

    public bool HasClient => Client != null && !string.IsNullOrWhiteSpace(Client.ClientId);
    public bool IsSignedIn => State == AuthState.SignedIn && Session != null;
    public string? Uid => Session?.Uid;

    public void SaveClient(OAuthClient client)
    {
        SecureStore.Write(ClientPath, client.ToJson());
        _savedClient = client;
        Changed?.Invoke();
    }

    public void ClearClient() { SecureStore.Delete(ClientPath); _savedClient = null; Changed?.Invoke(); }

    private void Persist()
    {
        if (Session == null) SecureStore.Delete(SessionPath);
        else SecureStore.Write(SessionPath, JsonSerializer.Serialize(Session));
    }

    public void SignOut()
    {
        Session = null; State = AuthState.SignedOut; LastError = null;
        Persist();
        Changed?.Invoke();
    }

    /// <summary>Interactive sign-in. Opens the system browser and waits for the loopback redirect.</summary>
    public async Task<bool> SignInAsync(CancellationToken ct, Action<string>? status = null)
    {
        var client = Client;
        if (client == null) { LastError = "No Google Desktop client configured."; State = AuthState.Error; Changed?.Invoke(); return false; }
        State = AuthState.SigningIn; LastError = null; Changed?.Invoke();
        HttpListener? listener = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var port = FreePort();
            var redirect = $"http://127.0.0.1:{port}/";
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var state = Base64Url(RandomNumberGenerator.GetBytes(16));

            listener = new HttpListener();
            listener.Prefixes.Add(redirect);
            listener.Start();

            var url = "https://accounts.google.com/o/oauth2/v2/auth?" + Query(new()
            {
                ["client_id"] = client.ClientId,
                ["redirect_uri"] = redirect,
                ["response_type"] = "code",
                ["scope"] = Scopes,
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256",
                ["state"] = state,
                ["access_type"] = "offline",
                ["prompt"] = "select_account consent",
            });
            status?.Invoke("Waiting for you to choose a Google account in the browser…");
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            var ctx = await listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(5), ct);
            var q = ctx.Request.QueryString;
            var err = q["error"]; var code = q["code"]; var st = q["state"];
            var ok = err == null && code != null && st == state;
            await Respond(ctx, ok ? "Signed in to Remindly. You can close this tab and return to the app." : "Sign-in failed: " + (err ?? "state mismatch") + ". You can close this tab.");
            if (!ok) throw new InvalidOperationException("Google returned: " + (err ?? "invalid state"));

            status?.Invoke("Exchanging the code with Google…");
            var tok = await PostForm("https://oauth2.googleapis.com/token", new()
            {
                ["client_id"] = client.ClientId,
                ["client_secret"] = client.ClientSecret,
                ["code"] = code!,
                ["code_verifier"] = verifier,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = redirect,
            }, ct);
            var idToken = tok["id_token"]?.GetValue<string>() ?? throw new InvalidOperationException("Google did not return an id_token");
            var googleRefresh = tok["refresh_token"]?.GetValue<string>();

            status?.Invoke("Linking to Firebase…");
            var fb = await PostJson($"https://identitytoolkit.googleapis.com/v1/accounts:signInWithIdp?key={_cfg.ApiKey}", new JsonObject
            {
                ["postBody"] = $"id_token={idToken}&providerId=google.com",
                ["requestUri"] = "http://localhost",
                ["returnIdpCredential"] = true,
                ["returnSecureToken"] = true,
            }, ct);
            var s = new AuthSession
            {
                Uid = fb["localId"]?.GetValue<string>() ?? throw new InvalidOperationException("Firebase did not return a uid"),
                Email = fb["email"]?.GetValue<string>() ?? "",
                DisplayName = fb["displayName"]?.GetValue<string>(),
                FirebaseRefreshToken = fb["refreshToken"]?.GetValue<string>() ?? "",
                IdToken = fb["idToken"]?.GetValue<string>() ?? "",
                GoogleRefreshToken = googleRefresh,
                GoogleRefreshClient = googleRefresh == null ? null : client,
            };
            var expiresIn = int.TryParse(fb["expiresIn"]?.GetValue<string>(), out var e) ? e : 3600;
            s.IdTokenExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (expiresIn - 120) * 1000L;
            ct.ThrowIfCancellationRequested();
            Session = s; State = AuthState.SignedIn; Persist(); Changed?.Invoke();
            Log?.Invoke($"Signed in as {s.Email} (uid {s.Uid[..Math.Min(6, s.Uid.Length)]}…)", null);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            LastError = "Sign-in cancelled.";
            State = Session == null ? AuthState.SignedOut : AuthState.SignedIn;
            Changed?.Invoke();
            return false;
        }
        catch (Exception ex)
        {
            LastError = ex.Message; State = Session != null ? AuthState.SignedIn : AuthState.Error;
            Log?.Invoke("Sign-in failed", ex);
            Changed?.Invoke();
            return false;
        }
        finally
        {
            try { listener?.Stop(); listener?.Close(); } catch { /* ignore */ }
        }
    }

    /// <summary>A valid Firebase ID token, refreshed through securetoken.googleapis.com when within 2 min of expiry.</summary>
    public async Task<string> GetIdTokenAsync(CancellationToken ct = default)
    {
        var s = Session ?? throw new InvalidOperationException("Signed out");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!string.IsNullOrEmpty(s.IdToken) && s.IdTokenExpiresAt > now) return s.IdToken;
        await _refreshGate.WaitAsync(ct);
        try
        {
            s = Session ?? throw new InvalidOperationException("Signed out");
            now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (!string.IsNullOrEmpty(s.IdToken) && s.IdTokenExpiresAt > now) return s.IdToken;
            var r = await PostForm($"https://securetoken.googleapis.com/v1/token?key={_cfg.ApiKey}", new()
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = s.FirebaseRefreshToken,
            }, ct);
            s.IdToken = r["id_token"]?.GetValue<string>() ?? throw new InvalidOperationException("no id_token in refresh");
            s.FirebaseRefreshToken = r["refresh_token"]?.GetValue<string>() ?? s.FirebaseRefreshToken;
            var expiresIn = int.TryParse(r["expires_in"]?.GetValue<string>(), out var e) ? e : 3600;
            s.IdTokenExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (expiresIn - 120) * 1000L;
            Persist();
            return s.IdToken;
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("TOKEN_EXPIRED") || ex.Message.Contains("USER_DISABLED") || ex.Message.Contains("USER_NOT_FOUND") || ex.Message.Contains("INVALID_REFRESH_TOKEN"))
        {
            Log?.Invoke("Refresh token rejected — signed out", ex);
            SignOut();
            throw;
        }
        finally { _refreshGate.Release(); }
    }

    /// <summary>Google access token (calendar.readonly) — for the read-only calendar view (later build).</summary>
    public async Task<string?> GetGoogleAccessTokenAsync(CancellationToken ct = default)
    {
        var session = Session;
        var client = session?.GoogleRefreshClient ?? Client;
        if (client == null || session?.GoogleRefreshToken == null) return null;
        var r = await PostForm("https://oauth2.googleapis.com/token", new()
        {
            ["client_id"] = client.ClientId, ["client_secret"] = client.ClientSecret,
            ["grant_type"] = "refresh_token", ["refresh_token"] = session.GoogleRefreshToken,
        }, ct);
        return r["access_token"]?.GetValue<string>();
    }

    // ── plumbing ──
    private async Task<JsonObject> PostForm(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var resp = await _http.PostAsync(url, new FormUrlEncodedContent(form), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{(int)resp.StatusCode} {url.Split('?')[0]}: {Redact(body)}");
        return JsonNode.Parse(body) as JsonObject ?? new JsonObject();
    }

    private async Task<JsonObject> PostJson(string url, JsonObject payload, CancellationToken ct)
    {
        using var resp = await _http.PostAsync(url, new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{(int)resp.StatusCode} {url.Split('?')[0]}: {Redact(body)}");
        return JsonNode.Parse(body) as JsonObject ?? new JsonObject();
    }

    private static string Redact(string body)
    {
        try { var o = JsonNode.Parse(body) as JsonObject; var m = o?["error"]?["message"]?.GetValue<string>(); if (m != null) return m; } catch { }
        return body.Length > 300 ? body[..300] : body;
    }

    private static async Task Respond(HttpListenerContext ctx, string text)
    {
        var html = "<!doctype html><html><head><meta charset='utf-8'><title>Remindly</title></head><body style='font-family:Segoe UI,sans-serif;padding:40px;text-align:center'><h2>Remindly for Windows</h2><p>" + WebUtility.HtmlEncode(text) + "</p></body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.OutputStream.Close();
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }

    private static string Query(Dictionary<string, string> kv) => string.Join("&", kv.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
