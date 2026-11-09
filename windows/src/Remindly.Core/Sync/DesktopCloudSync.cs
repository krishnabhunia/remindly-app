using System.Text.Json;

namespace Remindly.Core.Sync;

public enum CloudStatus { SignedOut, Paused, Connecting, Syncing, UpToDate, Error }

/// <summary>Read-before-write, non-destructive Firestore reconciliation for Android and Windows.</summary>
public sealed class DesktopCloudSync : IDisposable
{
    private readonly FirestoreTransport _transport;
    private readonly FirebaseAuth _auth;
    private readonly Func<RemindlyData> _snapshot;
    private readonly Action<RemindlyData> _apply;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Dictionary<string, CloudDocument>> _cache = new();
    private readonly Dictionary<string, long> _watermarks = new();
    private readonly FullReadSchedule _fullReads = new();
    private CancellationTokenSource? _loopCancel;
    private Task? _loop;
    private bool _disposed;
    private string? _cachedUserId;

    public CloudStatus Status { get; private set; } = CloudStatus.SignedOut;
    public string? LastError { get; private set; }
    public long LastSuccessAt { get; private set; }
    public int Progress { get; private set; }
    public bool Enabled { get; private set; }
    public event Action? Changed;

    public DesktopCloudSync(FirestoreTransport transport, FirebaseAuth auth,
        Func<RemindlyData> snapshot, Action<RemindlyData> apply, Func<DateTimeOffset>? now = null)
    {
        _transport = transport;
        _auth = auth;
        _snapshot = snapshot;
        _apply = apply;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _auth.Changed += OnAuthChanged;
    }

    private void OnAuthChanged()
    {
        // Cancel the old session immediately. Cache changes happen ONLY under _gate.
        _loopCancel?.Cancel();
        SetEnabled(Enabled);
    }

    private void Report(CloudStatus state, int progress, string? error = null)
    {
        Status = state;
        Progress = progress;
        LastError = error;
        Changed?.Invoke();
    }

    public void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        if (!enabled || !_auth.IsSignedIn)
        {
            _loopCancel?.Cancel();
            Report(!_auth.IsSignedIn ? CloudStatus.SignedOut : CloudStatus.Paused, 0);
            return;
        }
        if (_loop is { IsCompleted: false } && _loopCancel?.IsCancellationRequested == false) return;
        _loopCancel?.Dispose();
        _loopCancel = new CancellationTokenSource();
        var ct = _loopCancel.Token;
        Report(CloudStatus.Connecting, 0);
        _loop = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await SyncCycleAsync(false, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { Log.Error("Cloud sync cycle failed", ex); }
                try { await Task.Delay(TimeSpan.FromSeconds(15), ct); }
                catch (OperationCanceledException) { break; }
            }
        }, ct);
    }

    /// <summary>Manual retry always bypasses timestamp watermarks, including late offline writes.</summary>
    public Task SyncNowAsync(CancellationToken ct = default) => SyncCycleAsync(true, ct);

    internal async Task SyncCycleAsync(bool forceFullRead, CancellationToken ct = default)
    {
        if (!Enabled || !_auth.IsSignedIn) throw new InvalidOperationException("Sign in and enable cloud sync first.");
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct, _loopCancel?.Token ?? CancellationToken.None);
        ct = session.Token;
        await _gate.WaitAsync(ct);
        try
        {
            var uid = _auth.Uid;
            void CheckSession()
            {
                ct.ThrowIfCancellationRequested();
                if (!Enabled || !_auth.IsSignedIn || uid != _auth.Uid)
                    throw new OperationCanceledException("Cloud account changed during sync.");
            }
            CheckSession();
            if (_cachedUserId != uid)
            {
                _cache.Clear();
                _watermarks.Clear();
                _fullReads.Clear();
                _cachedUserId = uid;
            }
            Report(CloudStatus.Syncing, 5);
            var before = _snapshot();
            var incoming = new RemindlyData();
            var uploads = new List<(string collection, CloudDocument desired, CloudDocument? old)>();
            int unreadable = 0;

            async Task Reconcile<T>(string collection, IEnumerable<T> local, Action<List<T>> accept,
                Func<T, long> id, Func<T, long> stamp)
            {
                CheckSession();
                bool fullRead = forceFullRead || !_cache.ContainsKey(collection) || _fullReads.IsDue(collection, _now());
                var changed = fullRead
                    ? await _transport.ReadCollectionAsync(collection, ct)
                    : await _transport.ReadChangedCollectionAsync(collection, _watermarks[collection], ct);
                CheckSession();
                if (fullRead)
                {
                    _cache[collection] = changed;
                    // Incremental reads must not move the full-read deadline.
                    _fullReads.Completed(collection, _now());
                }
                else foreach (var d in changed) _cache[collection][d.Key] = d.Value;
                var remote = _cache[collection];
                long maxStamp = remote.Count == 0 ? 0 : remote.Values.Max(x => x.UpdatedAt);
                _watermarks[collection] = fullRead ? maxStamp : Math.Max(_watermarks.GetValueOrDefault(collection), maxStamp);
                var added = new List<T>();
                var localById = local.ToDictionary(x => id(x).ToString(System.Globalization.CultureInfo.InvariantCulture));
                foreach (var d in remote.Values)
                {
                    try
                    {
                        var wire = d.Json;
                        if (d.SchemaVer < CloudWire.Schema && localById.TryGetValue(d.Id, out var localExisting))
                            wire = CloudWire.OverlayOlderWriter(wire, JsonSerializer.Serialize(localExisting, Json.Options));
                        var obj = JsonSerializer.Deserialize<T>(wire, Json.Options);
                        if (obj != null) added.Add(obj); else unreadable++;
                    }
                    catch (JsonException)
                    {
                        unreadable++;
                        Log.Warn($"Unreadable cloud record in {collection}; preserved on server.");
                    }
                }
                accept(added);
                foreach (var rec in local)
                {
                    var key = id(rec).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    remote.TryGetValue(key, out var old);
                    if (!CloudWire.ShouldUpload(stamp(rec), old)) continue;
                    var desktopJson = JsonSerializer.Serialize(rec, Json.Options);
                    var wireJson = CloudWire.PreserveUnknownFields(desktopJson, old?.Json);
                    uploads.Add((collection, new CloudDocument(key, wireJson, stamp(rec), CloudWire.Schema), old));
                }
            }

            await Reconcile("items", before.Items, list => incoming.Items = list,
                x => x.Id, x => x.UpdatedAt > 0 ? x.UpdatedAt : x.CreatedAt);
            Report(CloudStatus.Syncing, 25);
            await Reconcile("calls", before.Calls ?? new(), list => incoming.Calls = list,
                x => x.Id, x => x.UpdatedAt > 0 ? x.UpdatedAt : x.CreatedAt);
            await Reconcile("places", before.Places, list => incoming.Places = list, x => x.Id, x => x.UpdatedAt);
            await Reconcile("shops", before.Shops, list => incoming.Shops = list, x => x.Id, x => x.UpdatedAt);
            Report(CloudStatus.Syncing, 55);

            // Catalogue entities remain local-only. Task lists, unlike catalogues, already
            // have an Android reader/writer in settings/app and MUST be reconciled here.
            var settingsDoc = await _transport.ReadSettingsAsync(ct);
            CheckSession();
            var settingsPlan = CloudListSettings.Reconcile(settingsDoc, before.Settings, _now().ToUnixTimeMilliseconds());
            incoming.Settings = settingsPlan.Incoming;
            if (settingsPlan.Upload is { } settingsUpload) uploads.Add(("settings", settingsUpload, settingsDoc));

            CheckSession();
            _apply(incoming);
            Report(CloudStatus.Syncing, 70);
            foreach (var (collection, desired, old) in uploads)
            {
                CheckSession();
                CloudDocument saved;
                try { saved = await _transport.WriteAsync(collection, desired, old, ct); }
                catch (HttpRequestException)
                {
                    if (collection != "settings")
                    {
                        _cache.Remove(collection);
                        _watermarks.Remove(collection);
                    }
                    throw;
                }
                CheckSession();
                if (collection != "settings" && _cache.TryGetValue(collection, out var docs))
                {
                    docs[desired.Id] = saved;
                    _watermarks[collection] = Math.Max(_watermarks[collection], desired.UpdatedAt);
                }
            }
            if (unreadable > 0)
                throw new InvalidOperationException($"{unreadable} cloud record(s) could not be read. They were preserved; synchronization is incomplete.");
            CheckSession();
            LastSuccessAt = _now().ToUnixTimeMilliseconds();
            Report(CloudStatus.UpToDate, 100);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Report(CloudStatus.Error, Progress, ex.Message);
            throw;
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _auth.Changed -= OnAuthChanged;
        _loopCancel?.Cancel();
        _transport.Dispose();
    }
}

internal sealed class FullReadSchedule
{
    private readonly Dictionary<string, DateTimeOffset> _completed = new();
    public bool IsDue(string collection, DateTimeOffset now) =>
        !_completed.TryGetValue(collection, out var last) || now - last >= TimeSpan.FromHours(24);
    public void Completed(string collection, DateTimeOffset now) => _completed[collection] = now;
    public void Clear() => _completed.Clear();
}
