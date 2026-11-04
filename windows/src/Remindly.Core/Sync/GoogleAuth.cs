using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Remindly.Core.Sync;

/// <summary>PKCE (RFC 7636) for the browser sign-in: the code can only be exchanged by the app that asked for it.</summary>
public static class Pkce
{
    public static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(48));
    public static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(16));
    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    public static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>The browser's request to the loopback redirect ("GET /?code=…&amp;state=… HTTP/1.1").</summary>
public sealed record LoopbackRequest(string Path, IReadOnlyDictionary<string, string> Query)
{
    public static LoopbackRequest? Parse(string request)
    {
        var line = request.Split('\n', 2)[0].Trim();
        var parts = line.Split(' ');
        if (parts.Length < 2 || parts[0] != "GET") return null;
        var target = parts[1];
        var q = target.IndexOf('?');
        var path = q < 0 ? target : target[..q];
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        if (q >= 0)
            foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var k = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
                var v = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
                query[k] = v;
            }
        return new LoopbackRequest(path, query);
    }
}

/// <summary>The page the browser tab shows after Google sends it back to Remindly (approved design, screen 3).</summary>
public static class SignInPages
{
    public static string Success(string email) => Page("You're signed in to Remindly",
        $"Signed in as {WebUtility.HtmlEncode(email)}. You can close this tab and go back to Remindly on your PC — it is bringing your data together now.", true);

    public static string Failure(string reason) => Page("Sign-in didn't finish",
        $"{WebUtility.HtmlEncode(reason)} Close this tab, go back to Remindly and press Sign in with Google again.", false);

    private static string Page(string title, string body, bool ok)
    {
        var mark = ok
            ? "<svg width='32' height='32' viewBox='0 0 24 24' fill='none' stroke='#0B7A55' stroke-width='2.4' stroke-linecap='round' stroke-linejoin='round' aria-hidden='true'><polyline points='20 6 9 17 4 12'/></svg>"
            : "<svg width='32' height='32' viewBox='0 0 24 24' fill='none' stroke='#B42318' stroke-width='2.4' stroke-linecap='round' aria-hidden='true'><line x1='6' y1='6' x2='18' y2='18'/><line x1='18' y1='6' x2='6' y2='18'/></svg>";
        return "<!doctype html><html lang='en'><head><meta charset='utf-8'><title>Remindly — " + WebUtility.HtmlEncode(title) + "</title>" +
               "<meta name='viewport' content='width=device-width,initial-scale=1'></head>" +
               "<body style=\"margin:0;font-family:'Segoe UI Variable Text','Segoe UI',system-ui,sans-serif;color:#1B1B1F;background:#FFFFFF\">" +
               "<main style='min-height:100vh;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:16px;padding:40px;box-sizing:border-box;text-align:center'>" +
               $"<div style='width:64px;height:64px;border-radius:50%;background:{(ok ? "#E3F4EC" : "#FDECEA")};display:flex;align-items:center;justify-content:center'>{mark}</div>" +
               $"<h1 style='margin:0;font-size:28px;font-weight:600'>{WebUtility.HtmlEncode(title)}</h1>" +
               $"<p style='margin:0;font-size:16px;color:#5A5C66;max-width:520px;line-height:1.5'>{body}</p>" +
               "<p style='margin:8px 0 0;font-size:13px;color:#5A5C66'>This page comes from Remindly on your own PC, not from the internet.</p>" +
               "</main></body></html>";
    }
}

/// <summary>What is kept (encrypted) between runs. Never part of remindly-data.json.</summary>
public sealed class AuthSession
{
    public string Uid { get; set; } = "";
    public string Email { get; set; } = "";
    public string? DisplayName { get; set; }
    public string RefreshToken { get; set; } = "";
    public string IdToken { get; set; } = "";
    public long IdTokenExpiresAt { get; set; }
}

public enum AuthState { SignedOut, SigningIn, SignedIn, Expired }

/// <summary>
/// Google sign-in for a desktop app: OAuth 2.0 with the system browser, a loopback redirect to
/// 127.0.0.1 and PKCE (Google blocks embedded web views), exchanged for a Firebase session through
/// the Identity Toolkit REST API. Because the Desktop client is in the same Google Cloud project as the
/// phone, the Firebase user id is the phone's, so both read and write the same /users/{uid}.
/// Remindly only receives the name, email and sign-in tokens — never the password.
/// </summary>
public sealed class GoogleAuth
{
    public const string Scopes = "openid email profile";

    private readonly FirebaseConfig? _cfg;
    private readonly string _dir;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public AuthSession? Session { get; private set; }
    public AuthState State { get; private set; } = AuthState.SignedOut;
    public string? LastError { get; private set; }
    /// <summary>The Google page of the sign-in in progress ("Open the browser page again").</summary>
    public string? PendingUrl { get; private set; }
    /// <summary>The account last signed in — kept after an expiry so "Sign in again" can name it.</summary>
    public string? LastEmail { get; private set; }
    public OAuthClient? Client { get; private set; }
    public bool ClientIsEmbedded { get; private set; }
    public event Action? Changed;
    public Action<string, Exception?>? Log { get; set; }

    private string SessionPath => Path.Combine(_dir, "session.bin");
    private string ClientPath => Path.Combine(_dir, "oauth-client.bin");

    public GoogleAuth(FirebaseConfig? cfg, string secureDir, HttpClient? http = null)
    {
        _cfg = cfg;
        _dir = secureDir;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        Client = OAuthClient.Embedded();
        ClientIsEmbedded = Client != null;
        if (Client == null && SecureStore.Read(ClientPath) is string c) Client = OAuthClient.Parse(c);
        if (SecureStore.Read(SessionPath) is string s)
        {
            try { Session = JsonSerializer.Deserialize<AuthSession>(s); } catch { Session = null; }
            if (Session is { RefreshToken.Length: > 0 }) { State = AuthState.SignedIn; LastEmail = Session.Email; }
            else Session = null;
        }
    }

    /// <summary>Both halves present: the Firebase project (built in) and a Google Desktop client (built in or chosen).</summary>
    public bool IsConfigured => _cfg != null && Client != null;
    public bool HasFirebase => _cfg != null;
    public bool IsSignedIn => State == AuthState.SignedIn && Session != null;
    public string? Uid => Session?.Uid;

    /// <summary>Settings → "Use a Google client file…": the client_secret_*.json downloaded from Google Cloud.</summary>
    public bool ImportClient(string json)
    {
        var c = OAuthClient.Parse(json);
        if (c == null) return false;
        Client = c;
        ClientIsEmbedded = false;
        SecureStore.Write(ClientPath, c.ToJson());
        Changed?.Invoke();
        return true;
    }

    /// <summary>Tests: a signed-in session without the browser (the emulator accepts any bearer token).</summary>
    public void UseTestSession(string uid, string email)
    {
        Session = new AuthSession { Uid = uid, Email = email, RefreshToken = "test", IdToken = "owner", IdTokenExpiresAt = long.MaxValue };
        State = AuthState.SignedIn;
        LastEmail = email;
        Changed?.Invoke();
    }

    public void SignOut()
    {
        Session = null;
        State = AuthState.SignedOut;
        LastError = null;
        PendingUrl = null;
        SecureStore.Delete(SessionPath);
        Changed?.Invoke();
    }

    private void Persist()
    {
        if (Session == null) SecureStore.Delete(SessionPath);
        else SecureStore.Write(SessionPath, JsonSerializer.Serialize(Session));
    }

    /// <summary>
    /// Interactive sign-in: opens Google in the browser, waits (up to 5 minutes, or until [ct] is
    /// cancelled) for the loopback redirect, then links the Google account to Firebase.
    /// </summary>
    public async Task<bool> SignInAsync(Action<string> openBrowser, CancellationToken ct)
    {
        if (_cfg == null || Client == null)
        {
            LastError = "Sign-in is not set up in this build of Remindly.";
            Changed?.Invoke();
            return false;
        }
        var before = State;
        State = AuthState.SigningIn;
        LastError = null;
        Changed?.Invoke();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var redirect = $"http://127.0.0.1:{port}/";
            var verifier = Pkce.NewVerifier();
            var state = Pkce.NewState();
            PendingUrl = "https://accounts.google.com/o/oauth2/v2/auth?" + Query(new()
            {
                ["client_id"] = Client.ClientId,
                ["redirect_uri"] = redirect,
                ["response_type"] = "code",
                ["scope"] = Scopes,
                ["code_challenge"] = Pkce.Challenge(verifier),
                ["code_challenge_method"] = "S256",
                ["state"] = state,
                ["prompt"] = "select_account",
            });
            openBrowser(PendingUrl);

            while (true)
            {
                using var tcp = await listener.AcceptTcpClientAsync(timeout.Token);
                var stream = tcp.GetStream();
                var req = LoopbackRequest.Parse(await ReadRequestAsync(stream, timeout.Token));
                if (req == null || req.Path != "/" || (!req.Query.ContainsKey("code") && !req.Query.ContainsKey("error")))
                {
                    await RespondAsync(stream, "404 Not Found", "<!doctype html><title>Remindly</title>", timeout.Token);
                    continue;   // favicon and the like; keep waiting for Google
                }
                if (req.Query.TryGetValue("error", out var err) || !req.Query.TryGetValue("code", out var code) || req.Query.GetValueOrDefault("state") != state)
                {
                    var why = err == "access_denied" ? "You cancelled the Google sign-in." : "Google did not complete the sign-in (" + (err ?? "state mismatch") + ").";
                    await RespondAsync(stream, "200 OK", SignInPages.Failure(why), timeout.Token);
                    throw new InvalidOperationException(why);
                }
                try
                {
                    var session = await ExchangeAsync(code, verifier, redirect, timeout.Token);
                    Session = session;
                    State = AuthState.SignedIn;
                    LastEmail = session.Email;
                    Persist();
                    await RespondAsync(stream, "200 OK", SignInPages.Success(session.Email), CancellationToken.None);
                    Log?.Invoke($"Signed in as {session.Email} (uid {session.Uid[..Math.Min(6, session.Uid.Length)]}…)", null);
                    return true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await RespondAsync(stream, "200 OK", SignInPages.Failure(ex.Message), CancellationToken.None);
                    throw;
                }
            }
        }
        catch (OperationCanceledException)
        {
            LastError = ct.IsCancellationRequested ? null : "Sign-in timed out after 5 minutes.";
            State = Session != null ? AuthState.SignedIn : before == AuthState.Expired ? AuthState.Expired : AuthState.SignedOut;
            return false;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            State = Session != null ? AuthState.SignedIn : before == AuthState.Expired ? AuthState.Expired : AuthState.SignedOut;
            Log?.Invoke("Sign-in failed", ex);
            return false;
        }
        finally
        {
            PendingUrl = null;
            try { listener.Stop(); } catch { /* ignore */ }
            Changed?.Invoke();
        }
    }

    private async Task<AuthSession> ExchangeAsync(string code, string verifier, string redirect, CancellationToken ct)
    {
        var tok = await PostForm("https://oauth2.googleapis.com/token", new()
        {
            ["client_id"] = Client!.ClientId,
            ["client_secret"] = Client.ClientSecret,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirect,
        }, ct);
        var idToken = tok["id_token"]?.GetValue<string>() ?? throw new InvalidOperationException("Google did not return an ID token.");
        var fb = await PostJson($"https://identitytoolkit.googleapis.com/v1/accounts:signInWithIdp?key={_cfg!.ApiKey}", new JsonObject
        {
            ["postBody"] = $"id_token={idToken}&providerId=google.com",
            ["requestUri"] = "http://localhost",
            ["returnIdpCredential"] = true,
            ["returnSecureToken"] = true,
        }, ct);
        var s = new AuthSession
        {
            Uid = fb["localId"]?.GetValue<string>() ?? throw new InvalidOperationException("Firebase did not return a user id."),
            Email = fb["email"]?.GetValue<string>() ?? "",
            DisplayName = fb["displayName"]?.GetValue<string>() ?? fb["fullName"]?.GetValue<string>(),
            RefreshToken = fb["refreshToken"]?.GetValue<string>() ?? "",
            IdToken = fb["idToken"]?.GetValue<string>() ?? "",
        };
        s.IdTokenExpiresAt = Clock.NowMs() + (ParseSeconds(fb["expiresIn"]) - 120) * 1000L;
        return s;
    }

    /// <summary>A valid Firebase ID token, refreshed when within two minutes of expiry. A rejected refresh marks the sign-in expired.</summary>
    public async Task<string> GetIdTokenAsync(CancellationToken ct = default)
    {
        var s = Session ?? throw new InvalidOperationException("Not signed in");
        if (s.IdToken.Length > 0 && s.IdTokenExpiresAt > Clock.NowMs()) return s.IdToken;
        await _refreshGate.WaitAsync(ct);
        try
        {
            s = Session ?? throw new InvalidOperationException("Not signed in");
            if (s.IdToken.Length > 0 && s.IdTokenExpiresAt > Clock.NowMs()) return s.IdToken;
            JsonObject r;
            try
            {
                r = await PostForm($"https://securetoken.googleapis.com/v1/token?key={_cfg?.ApiKey}", new()
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = s.RefreshToken,
                }, ct);
            }
            catch (AuthRejectedException ex)
            {
                Log?.Invoke("Google ended this PC's sign-in — sign in again", ex);
                Session = null;
                State = AuthState.Expired;
                SecureStore.Delete(SessionPath);
                Changed?.Invoke();
                throw;
            }
            s.IdToken = r["id_token"]?.GetValue<string>() ?? throw new InvalidOperationException("No ID token in the refresh reply.");
            s.RefreshToken = r["refresh_token"]?.GetValue<string>() ?? s.RefreshToken;
            s.IdTokenExpiresAt = Clock.NowMs() + (ParseSeconds(r["expires_in"]) - 120) * 1000L;
            Persist();
            return s.IdToken;
        }
        finally { _refreshGate.Release(); }
    }

    /// <summary>Firestore refused the token (clock skew, revoked early): fetch a fresh one on the next call.</summary>
    public void ForceTokenRefresh() { if (Session != null) Session.IdTokenExpiresAt = 0; }

    private static long ParseSeconds(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<string>(out var str) && long.TryParse(str, out var l)) return Math.Max(300, l);
            if (v.TryGetValue<long>(out var num)) return Math.Max(300, num);
        }
        return 3600;
    }

    // ── plumbing ──

    /// <summary>The refresh token or account was refused (revoked, password changed, user disabled).</summary>
    public sealed class AuthRejectedException(string message) : Exception(message);

    private async Task<JsonObject> PostForm(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var resp = await _http.PostAsync(url, new FormUrlEncodedContent(form), ct);
        return await ReadReply(resp, url, ct);
    }

    private async Task<JsonObject> PostJson(string url, JsonObject payload, CancellationToken ct)
    {
        using var resp = await _http.PostAsync(url, new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        return await ReadReply(resp, url, ct);
    }

    private static async Task<JsonObject> ReadReply(HttpResponseMessage resp, string url, CancellationToken ct)
    {
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (resp.IsSuccessStatusCode) return JsonNode.Parse(body) as JsonObject ?? new JsonObject();
        var msg = ErrorMessage(body);
        var where = new Uri(url).Host;
        if (msg is "TOKEN_EXPIRED" or "USER_DISABLED" or "USER_NOT_FOUND" or "INVALID_REFRESH_TOKEN" or "invalid_grant")
            throw new AuthRejectedException($"{where}: {msg}");
        throw new HttpRequestException($"{where} said {(int)resp.StatusCode}: {msg}");
    }

    internal static string ErrorMessage(string body)
    {
        try
        {
            if (JsonNode.Parse(body) is JsonObject o)
            {
                if (o["error"] is JsonObject e && e["message"]?.GetValue<string>() is string m) return m;
                if (o["error"] is JsonValue ev && ev.TryGetValue<string>(out var code)) return o["error_description"]?.GetValue<string>() is string d ? $"{code} ({d})" : code;
            }
        }
        catch { /* not JSON */ }
        return body.Length > 300 ? body[..300] : body;
    }

    private static async Task<string> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[8192];
        var sb = new StringBuilder();
        while (sb.Length < 16_384)
        {
            var n = await stream.ReadAsync(buf, ct);
            if (n <= 0) break;
            sb.Append(Encoding.ASCII.GetString(buf, 0, n));
            if (sb.ToString().Contains("\r\n\r\n", StringComparison.Ordinal)) break;
        }
        return sb.ToString();
    }

    private static async Task RespondAsync(NetworkStream stream, string status, string html, CancellationToken ct)
    {
        try
        {
            var body = Encoding.UTF8.GetBytes(html);
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head, ct);
            await stream.WriteAsync(body, ct);
            await stream.FlushAsync(ct);
        }
        catch { /* the browser tab may already be gone */ }
    }

    private static string Query(Dictionary<string, string> kv) =>
        string.Join("&", kv.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
}
