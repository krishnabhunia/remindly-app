using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Api.Gax.Grpc;
using Google.Cloud.Firestore.V1;
using Grpc.Core;

namespace Remindly.Core.Sync;

/// <summary>What owns the data: the sync engine reads and changes it only through [Invoke] (the UI thread).</summary>
public interface ISyncHost
{
    void Invoke(Action action);
    RemindlyData Data { get; }
    /// <summary>A remote batch was folded in: save and redraw.</summary>
    void AppliedRemote();
    /// <summary>Just before the very first merge with the cloud: keep a backup of this PC's data.</summary>
    void BeforeFirstSync();
}

public enum SyncPhase { Off, SignedOut, Connecting, Syncing, Live, Offline, Paused }

public enum SyncProblem { None, PermissionDenied, SignInExpired, NoPhoneData, Other }

public sealed class CollectionProgress
{
    public string Name { get; init; } = "";
    public bool Current { get; set; }
    public int Received { get; set; }
    public int Sent { get; set; }
}

/// <summary>Kept in %LOCALAPPDATA%\Remindly\sync\state.json — no secrets (the tokens are in the DPAPI store).</summary>
public sealed class SyncLocalState
{
    public string Uid { get; set; } = "";
    public bool FirstSyncDone { get; set; }
    public long LastFromCloudAt { get; set; }
    public long LastSentAt { get; set; }
    /// <summary>The phone's last settings document — every key Windows does not own is sent back from it.</summary>
    public string? RemoteSettings { get; set; }
    public long RemoteSettingsStamp { get; set; }
    /// <summary>Stamp of this PC's last settings write.</summary>
    public long LocalSettingsStamp { get; set; }
    /// <summary>SyncSettings.Hash of the shared keys the cloud holds; a different local hash means "send".</summary>
    public string SyncedSettingsHash { get; set; } = "";
    /// <summary>"col/id" of records deleted for good on this PC: a tombstone of them from the cloud is not brought back.</summary>
    public HashSet<string> Forgotten { get; set; } = new();
    /// <summary>Documents to delete from Firestore (Delete forever / Empty the Bin while offline).</summary>
    public HashSet<string> PendingDeletes { get; set; } = new();
}

/// <summary>
/// Live Firestore mirror of the Android SyncRepo, over gRPC with the user's Firebase ID token:
///   • one Listen stream with a query target per collection and the settings document;
///   • remote → local at every consistent snapshot, through the same last-writer-wins rules as the phone;
///   • local → remote: Commit of the {json, updatedAt, schemaVer} envelope for every record whose stamp
///     is newer than the cloud's; changes made while offline wait and go when the PC is back online;
///   • nothing is sent before the first snapshot, so a stale PC can never overwrite the phone, and a
///     cloud copy older than this PC's (the phone re-sent a stale record) is answered with ours.
/// </summary>
public sealed class FirestoreSync : IDisposable
{
    private readonly ISyncHost _host;
    private readonly GoogleAuth _auth;
    private readonly FirebaseConfig _cfg;
    private readonly string _statePath;
    private FirestoreClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly ConcurrentDictionary<string, Pending> _pending = new();
    private readonly ConcurrentDictionary<string, long> _cloudStamp = new();
    private readonly object _stateGate = new();
    private int _backoffMs = 1000;
    private volatile bool _initialSnapshot;
    private bool _backedUp;
    private int _remoteDocs;
    private bool _sawSettings;

    public string? EmulatorEndpoint { get; init; }
    public string? EmulatorToken { get; init; }

    public SyncPhase Phase { get; private set; } = SyncPhase.Off;
    public SyncProblem Problem { get; private set; }
    /// <summary>The exact error text (Copy details, the log).</summary>
    public string? ProblemDetail { get; private set; }
    public bool Enabled { get; private set; }
    public bool InitialSnapshotDone => _initialSnapshot;
    public int PendingCount => _pending.Count + State.PendingDeletes.Count;
    public SyncLocalState State { get; private set; }
    public IReadOnlyDictionary<string, CollectionProgress> Progress => _progress;
    private readonly Dictionary<string, CollectionProgress> _progress = new();
    public event Action? Changed;
    public Action<string, Exception?>? Log { get; set; }

    private sealed record Pending(string Col, string Id, string Json, long Stamp);

    public FirestoreSync(ISyncHost host, GoogleAuth auth, FirebaseConfig cfg, string stateDir)
    {
        _host = host; _auth = auth; _cfg = cfg;
        _statePath = Path.Combine(stateDir, "state.json");
        State = LoadState();
        foreach (var c in SyncContract.Records.Append(SyncContract.Settings)) _progress[c] = new CollectionProgress { Name = c };
        _auth.Changed += OnAuthChanged;
    }

    private string Root => $"{_cfg.DocumentsRoot}/users/{_auth.Uid}";

    // ── lifecycle ─────────────────────────────────────────────────────────────────────────

    /// <summary>The device-local "Cloud sync" switch. Idempotent.</summary>
    public void SetEnabled(bool on)
    {
        Enabled = on;
        Reconsider();
    }

    private void OnAuthChanged()
    {
        if (_auth.State == AuthState.Expired) { Stop(SyncPhase.Paused); SetProblem(SyncProblem.SignInExpired, "Google ended this PC's sign-in."); return; }
        Reconsider();
    }

    private void Reconsider()
    {
        if (!_auth.IsSignedIn)
        {
            if (_auth.State == AuthState.SigningIn) return;
            Stop(_auth.State == AuthState.Expired ? SyncPhase.Paused : SyncPhase.SignedOut);
            return;
        }
        if (!Enabled) { Stop(SyncPhase.Off); return; }
        if (State.Uid != _auth.Uid) ResetForAccount(_auth.Uid!);
        if (Problem == SyncProblem.SignInExpired) SetProblem(SyncProblem.None, null);
        Start();
    }

    /// <summary>Another Google account (or the first one): nothing learnt about the previous cloud applies.</summary>
    private void ResetForAccount(string uid)
    {
        lock (_stateGate) State = new SyncLocalState { Uid = uid };
        _cloudStamp.Clear();
        _pending.Clear();
        SaveState();
    }

    private void Start()
    {
        if (_loop is { IsCompleted: false }) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _initialSnapshot = false;
        _backedUp = false;
        foreach (var p in _progress.Values) { p.Current = false; p.Received = 0; p.Sent = 0; }
        _loop = Task.Run(() => RunAsync(ct), ct);
    }

    private void Stop(SyncPhase to)
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _cts = null;
        _loop = null;
        _initialSnapshot = false;
        SetPhase(to);
    }

    /// <summary>"Sync now": reconnect at once (skipping any back-off) or, when live, send what is waiting.</summary>
    public void SyncNow()
    {
        if (!_auth.IsSignedIn || !Enabled) return;
        if (Phase == SyncPhase.Live) { _host.Invoke(LocalChanged); return; }
        Stop(Phase);
        _backoffMs = 1000;
        Start();
    }

    /// <summary>Sign out: stop, forget this account's sync bookkeeping.</summary>
    public void SignedOut()
    {
        Stop(SyncPhase.SignedOut);
        ResetForAccount("");
        SetProblem(SyncProblem.None, null);
    }

    public void Dispose()
    {
        _auth.Changed -= OnAuthChanged;
        Stop(SyncPhase.Off);
    }

    private void SetPhase(SyncPhase p)
    {
        Phase = p;
        Changed?.Invoke();
    }

    private void SetProblem(SyncProblem p, string? detail)
    {
        Problem = p;
        ProblemDetail = detail;
        Changed?.Invoke();
    }

    private FirestoreClient BuildClient()
    {
        var b = new FirestoreClientBuilder();
        if (EmulatorEndpoint != null) { b.Endpoint = EmulatorEndpoint; b.ChannelCredentials = ChannelCredentials.Insecure; }
        else b.ChannelCredentials = ChannelCredentials.SecureSsl;
        return b.Build();
    }

    private async Task<CallSettings> CallSettingsAsync(CancellationToken ct)
    {
        var token = EmulatorToken ?? await _auth.GetIdTokenAsync(ct);
        return CallSettings.FromHeader("Authorization", "Bearer " + token)
            .MergedWith(CallSettings.FromHeader("google-cloud-resource-prefix", _cfg.Database))
            .MergedWith(CallSettings.FromHeader("x-goog-request-params", "database=" + Uri.EscapeDataString(_cfg.Database)))
            .MergedWith(CallSettings.FromCancellationToken(ct));
    }

    // ── the listen loop ───────────────────────────────────────────────────────────────────

    private async Task RunAsync(CancellationToken ct)
    {
        _client ??= BuildClient();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                SetPhase(SyncPhase.Connecting);
                _remoteDocs = 0;
                _sawSettings = false;
                foreach (var p in _progress.Values) { p.Current = false; p.Received = 0; }
                var settings = await CallSettingsAsync(ct);
                using var stream = _client.Listen(settings);
                var targets = new Dictionary<int, string>();
                int tid = 1;
                foreach (var c in SyncContract.Records) targets[tid++] = c;
                targets[tid] = SyncContract.Settings;
                foreach (var (id, col) in targets)
                    await stream.WriteAsync(new ListenRequest { Database = _cfg.Database, AddTarget = TargetFor(id, col) });

                var buffer = new Dictionary<string, Dictionary<string, RemoteDoc>>();
                RemoteDoc? settingsDoc = null;
                await foreach (var resp in stream.GetResponseStream().WithCancellation(ct))
                {
                    switch (resp.ResponseTypeCase)
                    {
                        case ListenResponse.ResponseTypeOneofCase.TargetChange:
                        {
                            var tc = resp.TargetChange;
                            if (tc.TargetChangeType == TargetChange.Types.TargetChangeType.Remove)
                            {
                                var denied = tc.Cause?.Code == (int)StatusCode.PermissionDenied;
                                var msg = tc.Cause?.Message ?? "listen target removed";
                                Log?.Invoke($"SYNC listen target removed: {msg}", null);
                                if (denied) throw new RpcException(new Status(StatusCode.PermissionDenied, msg));
                                throw new RpcException(new Status(StatusCode.Aborted, msg));
                            }
                            if (tc.TargetChangeType == TargetChange.Types.TargetChangeType.Current)
                            {
                                if (tc.TargetIds.Count == 0) foreach (var col in targets.Values) _progress[col].Current = true;
                                foreach (var t in tc.TargetIds) if (targets.TryGetValue(t, out var col)) _progress[col].Current = true;
                            }
                            var boundary = tc.TargetIds.Count == 0 && tc.TargetChangeType is TargetChange.Types.TargetChangeType.NoChange or TargetChange.Types.TargetChangeType.Current;
                            if (boundary || tc.ResumeToken.Length > 0)
                            {
                                bool allCurrent = targets.Values.All(c => _progress[c].Current);
                                if (buffer.Count > 0 || settingsDoc != null || (allCurrent && !_initialSnapshot))
                                {
                                    var batch = buffer.ToDictionary(kv => kv.Key, kv => kv.Value.Values.ToList());
                                    buffer.Clear();
                                    var s = settingsDoc;
                                    settingsDoc = null;
                                    await ApplyBatchAsync(batch, s, firstSnapshot: allCurrent && !_initialSnapshot);
                                }
                                if (allCurrent && !_initialSnapshot)
                                {
                                    _initialSnapshot = true;
                                    _backoffMs = 1000;
                                    if (Problem is SyncProblem.PermissionDenied or SyncProblem.Other) SetProblem(SyncProblem.None, null);
                                    if (_remoteDocs == 0 && !_sawSettings) SetProblem(SyncProblem.NoPhoneData, "This account has no data from a phone yet.");
                                    await ReconcileAsync();
                                }
                                if (_initialSnapshot) SetPhase(PendingCount > 0 ? SyncPhase.Syncing : SyncPhase.Live);
                                _ = FlushAsync(ct);
                            }
                            break;
                        }
                        case ListenResponse.ResponseTypeOneofCase.DocumentChange:
                        {
                            var d = resp.DocumentChange.Document;
                            var (col, id) = SyncContract.SplitName(d.Name);
                            if (col == null || !d.Fields.TryGetValue("json", out var jv) || jv.StringValue is not { Length: > 0 } json) break;
                            var ver = d.Fields.TryGetValue("schemaVer", out var sv) ? (int)sv.IntegerValue : 0;
                            var upd = d.Fields.TryGetValue("updatedAt", out var uv) ? uv.IntegerValue : 0;
                            if (col == SyncContract.Settings)
                            {
                                if (id == SyncContract.SettingsDoc) { settingsDoc = new RemoteDoc(id, json, ver, upd); _sawSettings = true; }
                                break;
                            }
                            if (!_progress.ContainsKey(col)) break;
                            _cloudStamp[SyncContract.Key(col, id)] = upd;
                            if (!buffer.TryGetValue(col, out var docs)) buffer[col] = docs = new();
                            docs[id] = new RemoteDoc(id, json, ver, upd);
                            if (!_initialSnapshot) _progress[col].Received++;
                            Interlocked.Increment(ref _remoteDocs);
                            break;
                        }
                        case ListenResponse.ResponseTypeOneofCase.DocumentDelete:
                        case ListenResponse.ResponseTypeOneofCase.DocumentRemove:
                        {
                            // The phone deletes with tombstones; only "Delete forever" on a PC removes a document. Nothing to mirror.
                            var name = resp.ResponseTypeCase == ListenResponse.ResponseTypeOneofCase.DocumentDelete ? resp.DocumentDelete.Document : resp.DocumentRemove.Document;
                            var (col, id) = SyncContract.SplitName(name);
                            if (col != null) _cloudStamp.TryRemove(SyncContract.Key(col, id), out _);
                            break;
                        }
                        case ListenResponse.ResponseTypeOneofCase.Filter:
                            throw new RpcException(new Status(StatusCode.Aborted, "existence filter mismatch — resync"));
                    }
                }
                throw new RpcException(new Status(StatusCode.Unavailable, "listen stream ended"));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (GoogleAuth.AuthRejectedException ex)
            {
                Log?.Invoke("SYNC sign-in expired", ex);
                SetProblem(SyncProblem.SignInExpired, ex.Message);
                SetPhase(SyncPhase.Paused);
                return;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
            {
                Log?.Invoke("SYNC Firestore said PERMISSION_DENIED — the Remindly security rules are not published for this project", ex);
                SetProblem(SyncProblem.PermissionDenied, "Firestore: PERMISSION_DENIED — " + ex.Status.Detail);
                SetPhase(SyncPhase.Paused);
                if (!await Delay(60_000, ct)) return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                if (ex is RpcException { StatusCode: StatusCode.Unauthenticated }) _auth.ForceTokenRefresh();
                var offline = ex is HttpRequestException or System.Net.Sockets.SocketException
                              || ex is RpcException { StatusCode: StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.Aborted or StatusCode.Unauthenticated or StatusCode.Cancelled or StatusCode.Internal };
                Log?.Invoke(offline ? "SYNC offline — reconnecting" : "SYNC error — reconnecting", ex);
                if (offline) SetPhase(SyncPhase.Offline);
                else { SetProblem(SyncProblem.Other, ex.Message); SetPhase(SyncPhase.Paused); }
                if (!await Delay(_backoffMs, ct)) return;
                _backoffMs = Math.Min(_backoffMs * 2, 60_000);
            }
            finally
            {
                _initialSnapshot = false;
                foreach (var p in _progress.Values) p.Current = false;
            }
        }
    }

    private static async Task<bool> Delay(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct); return true; }
        catch (OperationCanceledException) { return false; }
    }

    private Target TargetFor(int id, string col) => col == SyncContract.Settings
        ? new Target { TargetId = id, Documents = new Target.Types.DocumentsTarget { Documents = { $"{Root}/{SyncContract.Settings}/{SyncContract.SettingsDoc}" } } }
        : new Target
        {
            TargetId = id,
            Query = new Target.Types.QueryTarget
            {
                Parent = Root,
                StructuredQuery = new StructuredQuery { From = { new StructuredQuery.Types.CollectionSelector { CollectionId = col } } },
            },
        };

    // ── remote → local ────────────────────────────────────────────────────────────────────

    private Task ApplyBatchAsync(Dictionary<string, List<RemoteDoc>> batch, RemoteDoc? settings, bool firstSnapshot)
    {
        var done = new TaskCompletionSource();
        _host.Invoke(() =>
        {
            try
            {
                if (!State.FirstSyncDone && !_backedUp) { _host.BeforeFirstSync(); _backedUp = true; }
                var d = _host.Data;
                var now = Clock.NowMs();
                int changed = 0;
                if (settings != null) changed += ApplySettings(d, settings) ? 1 : 0;
                foreach (var col in SyncContract.Records)
                {
                    if (!batch.TryGetValue(col, out var docs) || docs.Count == 0) continue;
                    var r = SyncMerge.Apply(d, col, docs, State.Forgotten, now);
                    foreach (var w in r.Warnings) Log?.Invoke("SYNC " + w, null);
                    changed += r.Changed;
                    foreach (var newer in r.LocalNewer) _pending[newer.Key] = new Pending(newer.Col, newer.Id, newer.Json(), newer.Stamp);
                }
                if (_remoteDocs > 0 && Problem == SyncProblem.NoPhoneData) SetProblem(SyncProblem.None, null);
                if (changed > 0)
                {
                    DataStore.Heal(d, now);
                    State.LastFromCloudAt = now;
                    SaveState();
                    _host.AppliedRemote();
                }
            }
            catch (Exception ex) { Log?.Invoke("SYNC applying the cloud's changes failed", ex); }
            finally { done.TrySetResult(); Changed?.Invoke(); }
        });
        return done.Task;
    }

    private bool ApplySettings(RemindlyData d, RemoteDoc doc)
    {
        if (doc.SchemaVer < SyncContract.SyncSchema)
        {
            Log?.Invoke($"SYNC settings document from older sync schema v{doc.SchemaVer} — not adopted (it would reset this PC's lists to the other device's defaults)", null);
            return false;
        }
        if (JsonNode.Parse(doc.Json) is not JsonObject remote) return false;
        var stamp = SyncSettings.StampOf(remote);
        if (stamp <= 0) stamp = doc.UpdatedAt;
        var adopt = stamp > State.LocalSettingsStamp;
        var before = SyncSettings.Hash(d.Settings);
        d.Settings = SyncSettings.ApplyRemote(d.Settings, remote, adopt);
        lock (_stateGate)
        {
            State.RemoteSettings = remote.ToJsonString(Json.Compact);
            State.RemoteSettingsStamp = Math.Max(State.RemoteSettingsStamp, stamp);
        }
        _progress[SyncContract.Settings].Received = 1;
        var healed = DataStore.Heal(d, Clock.NowMs());
        if (adopt && !SyncSettings.LocalHasNewerLists(healed.Settings, remote)) State.SyncedSettingsHash = SyncSettings.Hash(healed.Settings);
        SaveState();
        return SyncSettings.Hash(healed.Settings) != before;
    }

    /// <summary>After the first snapshot: send what this PC has that the cloud lacks or holds older.</summary>
    private Task ReconcileAsync()
    {
        var done = new TaskCompletionSource();
        _host.Invoke(() =>
        {
            try
            {
                Scan();
                lock (_stateGate) State.FirstSyncDone = true;
                SaveState();
            }
            catch (Exception ex) { Log?.Invoke("SYNC reconcile failed", ex); }
            finally { done.TrySetResult(); Changed?.Invoke(); }
        });
        return done.Task;
    }

    // ── local → remote ────────────────────────────────────────────────────────────────────

    /// <summary>Call on the owner thread after every save. Queues what is newer than the cloud and sends it.</summary>
    public void LocalChanged()
    {
        if (!_initialSnapshot || !Enabled || !_auth.IsSignedIn) return;
        Scan();
        _ = FlushAsync(_cts?.Token ?? CancellationToken.None);
    }

    private void Scan()
    {
        var d = _host.Data;
        foreach (var rec in SyncMerge.LocalRecords(d))
        {
            if (_cloudStamp.TryGetValue(rec.Key, out var cloud) && rec.Stamp <= cloud) continue;
            if (_pending.TryGetValue(rec.Key, out var queued) && queued.Stamp == rec.Stamp) continue;
            _pending[rec.Key] = new Pending(rec.Col, rec.Id, rec.Json(), rec.Stamp);
        }
        var hash = SyncSettings.Hash(d.Settings);
        if (State.RemoteSettings != null && hash != State.SyncedSettingsHash && JsonNode.Parse(State.RemoteSettings) is JsonObject baseDoc)
        {
            var stamp = Math.Max(Clock.NowMs(), Math.Max(State.RemoteSettingsStamp, State.LocalSettingsStamp) + 1);
            var doc = SyncSettings.BuildPush(d.Settings, baseDoc, stamp)!;
            _pending[SyncContract.Key(SyncContract.Settings, SyncContract.SettingsDoc)] = new Pending(SyncContract.Settings, SyncContract.SettingsDoc, doc.ToJsonString(Json.Compact), stamp);
            lock (_stateGate)
            {
                State.LocalSettingsStamp = stamp;
                State.SyncedSettingsHash = hash;
                State.RemoteSettings = doc.ToJsonString(Json.Compact);
                State.RemoteSettingsStamp = stamp;
            }
            SaveState();
        }
        if (PendingCount > 0 && Phase == SyncPhase.Live) SetPhase(SyncPhase.Syncing);
    }

    /// <summary>Delete forever / Empty the Bin: the document goes from the cloud too, and a tombstone of it is never brought back.</summary>
    public void Forget(string col, long id)
    {
        var key = SyncContract.Key(col, id.ToString());
        lock (_stateGate)
        {
            State.Forgotten.Add(key);
            if (State.Forgotten.Count > 5000) State.Forgotten = State.Forgotten.Skip(State.Forgotten.Count - 5000).ToHashSet();
            if (_auth.IsSignedIn && Enabled) State.PendingDeletes.Add(key);
        }
        _pending.TryRemove(key, out _);
        SaveState();
        _ = FlushAsync(_cts?.Token ?? CancellationToken.None);
    }

    /// <summary>Commit what is queued, 200 writes per call. Safe to call often; does nothing until the first snapshot.</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        if (!Enabled || !_auth.IsSignedIn || !_initialSnapshot || PendingCount == 0) return;
        if (!await _flushGate.WaitAsync(0, ct)) return;
        try
        {
            _client ??= BuildClient();
            while (PendingCount > 0 && !ct.IsCancellationRequested)
            {
                // The cloud may have moved on while this waited: last writer wins in the queue too.
                foreach (var w in _pending.Values.ToList())
                    if (w.Col != SyncContract.Settings && _cloudStamp.TryGetValue(SyncContract.Key(w.Col, w.Id), out var cs) && cs > w.Stamp)
                        _pending.TryRemove(SyncContract.Key(w.Col, w.Id), out _);
                List<string> deletes;
                lock (_stateGate) deletes = State.PendingDeletes.Take(100).ToList();
                var batch = _pending.Values.Where(w => w.Col == SyncContract.Settings).Concat(_pending.Values.Where(w => w.Col != SyncContract.Settings)).Take(200 - deletes.Count).ToList();
                if (batch.Count == 0 && deletes.Count == 0) break;
                var req = new CommitRequest { Database = _cfg.Database };
                foreach (var w in batch)
                    req.Writes.Add(new Write
                    {
                        Update = new Document
                        {
                            Name = $"{Root}/{w.Col}/{w.Id}",
                            Fields =
                            {
                                ["json"] = new Value { StringValue = w.Json },
                                ["updatedAt"] = new Value { IntegerValue = w.Stamp },
                                ["schemaVer"] = new Value { IntegerValue = SyncContract.SyncSchema },
                            },
                        },
                    });
                foreach (var key in deletes) req.Writes.Add(new Write { Delete = $"{Root}/{key}" });
                if (Phase == SyncPhase.Live) SetPhase(SyncPhase.Syncing);
                await _client.CommitAsync(req, await CallSettingsAsync(ct));
                foreach (var w in batch)
                {
                    var key = SyncContract.Key(w.Col, w.Id);
                    if (_pending.TryGetValue(key, out var cur) && cur.Stamp == w.Stamp) _pending.TryRemove(key, out _);
                    if (w.Col != SyncContract.Settings)
                    {
                        _cloudStamp[key] = w.Stamp;
                        if (_progress.TryGetValue(w.Col, out var p)) p.Sent++;
                    }
                }
                lock (_stateGate)
                {
                    foreach (var key in deletes) State.PendingDeletes.Remove(key);
                    State.LastSentAt = Clock.NowMs();
                }
                SaveState();
                Changed?.Invoke();
            }
            if (Phase == SyncPhase.Syncing && PendingCount == 0) SetPhase(SyncPhase.Live);
        }
        catch (OperationCanceledException) { /* stopped */ }
        catch (GoogleAuth.AuthRejectedException ex)
        {
            SetProblem(SyncProblem.SignInExpired, ex.Message);
            Stop(SyncPhase.Paused);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
        {
            Log?.Invoke("SYNC send refused (PERMISSION_DENIED)", ex);
            SetProblem(SyncProblem.PermissionDenied, "Firestore: PERMISSION_DENIED — " + ex.Status.Detail);
            SetPhase(SyncPhase.Paused);
        }
        catch (Exception ex)
        {
            if (ex is RpcException { StatusCode: StatusCode.Unauthenticated }) _auth.ForceTokenRefresh();
            Log?.Invoke("SYNC send failed — kept for later", ex);
            SetPhase(SyncPhase.Offline);
        }
        finally { _flushGate.Release(); }
    }

    // ── state file ──

    private SyncLocalState LoadState()
    {
        try
        {
            if (File.Exists(_statePath) && JsonSerializer.Deserialize<SyncLocalState>(File.ReadAllText(_statePath)) is SyncLocalState s)
            {
                s.Forgotten ??= new();
                s.PendingDeletes ??= new();
                return s;
            }
        }
        catch (Exception ex) { Log?.Invoke("SYNC state unreadable — starting fresh", ex); }
        return new SyncLocalState();
    }

    private void SaveState()
    {
        try
        {
            string json;
            lock (_stateGate) json = JsonSerializer.Serialize(State);
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var tmp = _statePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _statePath, overwrite: true);
        }
        catch { /* best effort: the next run reconciles from the cloud anyway */ }
    }
}
