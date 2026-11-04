using System.Text.Json;
using System.Text.Json.Nodes;

namespace Remindly.Core.Sync;

public enum CloudStatus { SignedOut, Paused, Connecting, Syncing, UpToDate, Error }

/// <summary>
/// Bidirectional Firestore reconciliation, with a read-before-write first connection.
/// Android's existing collections use a JSON envelope, LWW timestamps and deletion tombstones.
/// The desktop preserves unknown Android fields before writing a modified record back.
/// </summary>
public sealed class DesktopCloudSync : IDisposable
{
    private readonly FirestoreTransport _transport;
    private readonly FirebaseAuth _auth;
    private readonly Func<RemindlyData> _snapshot;
    private readonly Action<RemindlyData> _apply;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Dictionary<string, CloudDocument>> _cache = new();
    private readonly Dictionary<string, long> _watermarks = new();
    private DateTimeOffset _lastFullRead = DateTimeOffset.MinValue;
    private CancellationTokenSource? _loopCancel;
    private Task? _loop;
    private bool _disposed;

    public CloudStatus Status { get; private set; } = CloudStatus.SignedOut;
    public string? LastError { get; private set; }
    public long LastSuccessAt { get; private set; }
    public int Progress { get; private set; }
    public bool Enabled { get; private set; }
    public event Action? Changed;

    public DesktopCloudSync(FirestoreTransport transport, FirebaseAuth auth,
        Func<RemindlyData> snapshot, Action<RemindlyData> apply)
    {
        _transport = transport;
        _auth = auth;
        _snapshot = snapshot;
        _apply = apply;
        _auth.Changed += OnAuthChanged;
    }

    private void OnAuthChanged() => SetEnabled(Enabled);

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
                try { await SyncNowAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Log.Error("Cloud sync cycle failed", ex);
                    Report(CloudStatus.Error, Progress, ex.Message);
                }
                try { await Task.Delay(TimeSpan.FromSeconds(15), ct); }
                catch (OperationCanceledException) { break; }
            }
        }, ct);
    }

    public async Task SyncNowAsync(CancellationToken ct = default)
    {
        if (!Enabled || !_auth.IsSignedIn) throw new InvalidOperationException("Sign in and enable cloud sync first.");
        await _gate.WaitAsync(ct);
        try
        {
            Report(CloudStatus.Syncing, 5);
            // A deep snapshot and an apply callback are marshalled to the WPF UI thread by
            // the caller. DataStore.MergeInto is LWW, so concurrent edits are not overwritten.
            var before = _snapshot();
            var incoming = new RemindlyData();
            var uploads = new List<(string collection, CloudDocument desired, CloudDocument? old)>();

            async Task Reconcile<T>(string collection, IEnumerable<T> local, Action<List<T>> accept,
                Func<T, long> id, Func<T, long> stamp)
            {
                bool fullRead = !_cache.ContainsKey(collection) ||
                                DateTimeOffset.UtcNow - _lastFullRead > TimeSpan.FromHours(24);
                var changed = fullRead
                    ? await _transport.ReadCollectionAsync(collection, ct)
                    : await _transport.ReadChangedCollectionAsync(collection, _watermarks[collection], ct);
                if (fullRead) _cache[collection] = changed;
                else foreach (var d in changed) _cache[collection][d.Key] = d.Value;
                var remote = _cache[collection];
                _watermarks[collection] = remote.Count == 0 ? 0 :
                    Math.Max(_watermarks.GetValueOrDefault(collection), remote.Values.Max(x => x.UpdatedAt));
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
                        if (obj != null) added.Add(obj);
                    }
                    catch (JsonException) { Log.Warn($"Unreadable cloud record in {collection}; preserved on server."); }
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
            _lastFullRead = DateTimeOffset.UtcNow;
            Report(CloudStatus.Syncing, 55);

            // Android v2.11 currently keeps cities/chains/products local. Do NOT publish
            // apparently synced values until Android also has readers and writers for them.
            // Windows keeps these records via its independent local JSON and backups.

            var settingsDoc = await _transport.ReadSettingsAsync(ct);
            if (settingsDoc != null)
            {
                var settingsJson = JsonNode.Parse(settingsDoc.Json) as JsonObject;
                if (settingsJson != null)
                {
                    var cloudLists = settingsJson["shopLists"]?.Deserialize<List<ShopList>>(Json.Options) ?? new();
                    incoming.Settings = new AppSettings { ShopLists = cloudLists };
                    var mergedLists = ShopLists.Merge(cloudLists, before.Settings.ShopLists);
                    // An empty/fresh desktop may not replace the phone's existing settings.
                    // Only upload the list patch when there is an actual NEWER local list.
                    bool desktopNewer = before.Settings.ShopLists.Any(l =>
                        !cloudLists.Any(c => c.Id == l.Id && c.UpdatedAt >= l.UpdatedAt));
                    if (desktopNewer)
                    {
                        settingsJson["shopLists"] = JsonSerializer.SerializeToNode(mergedLists, Json.Options);
                        var nextStamp = Math.Max(settingsDoc.UpdatedAt + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                        settingsJson["settingsUpdatedAt"] = nextStamp;
                        uploads.Add(("settings", new CloudDocument("app", settingsJson.ToJsonString(), nextStamp, CloudWire.Schema), settingsDoc));
                    }
                }
            }

            // Pull is applied FIRST, always through the local merge and never as a replace.
            // Even an empty fresh profile cannot erase the phone's Firestore records.
            _apply(incoming);
            Report(CloudStatus.Syncing, 70);

            foreach (var (collection, desired, old) in uploads)
            {
                ct.ThrowIfCancellationRequested();
                CloudDocument saved;
                try { saved = await _transport.WriteAsync(collection, desired, old, ct); }
                catch (HttpRequestException)
                {
                    // A simultaneous phone write can invalidate our updateTime precondition.
                    // Force a new full read rather than repeatedly retrying the stale cache.
                    if (collection != "settings")
                    {
                        _cache.Remove(collection);
                        _watermarks.Remove(collection);
                    }
                    throw;
                }
                if (collection != "settings" && _cache.TryGetValue(collection, out var docs))
                {
                    docs[desired.Id] = saved;
                    _watermarks[collection] = Math.Max(_watermarks[collection], desired.UpdatedAt);
                }
            }

            LastSuccessAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Report(CloudStatus.UpToDate, 100);
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
