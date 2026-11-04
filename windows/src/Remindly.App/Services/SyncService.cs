using System.Windows;
using Remindly.Core;
using Remindly.Core.Sync;

namespace Remindly.App.Services;

/// <summary>The header chip's five states (approved design, screen 6).</summary>
public enum ChipKind { Synced, Syncing, Offline, Paused, NotSyncing }

public sealed record ChipStatus(ChipKind Kind, string Text, string Tip);

/// <summary>
/// Google sign-in + Cloud sync for the app: owns the sign-in and the Firestore engine, feeds the engine
/// every save and every "Delete forever", and raises <see cref="Changed"/> on the UI thread so the header
/// chip and Settings → Account and sync redraw.
/// </summary>
public sealed class SyncService : ISyncHost, IDisposable
{
    public static string Dir => Path.Combine(AppPaths.DataRoot, "sync");

    public FirebaseConfig? Config { get; }
    public GoogleAuth Auth { get; }
    public FirestoreSync? Engine { get; }
    public event Action? Changed;

    private CancellationTokenSource? _signIn;
    private int _raiseQueued;

    public SyncService()
    {
        Config = FirebaseConfig.Embedded();
        Auth = new GoogleAuth(Config, Dir) { Log = Write };
        if (Config != null)
        {
            Engine = new FirestoreSync(this, Auth, Config, Dir) { Log = Write };
            Engine.Changed += Raise;
        }
        Auth.Changed += Raise;
        AppState.Current.Saved += OnSaved;
        AppState.Current.Forgotten += OnForgotten;
    }

    public void Start()
    {
        Log.Info($"SYNC configured: Firebase {(Config != null ? "yes" : "no")}, Google client {(Auth.Client != null ? (Auth.ClientIsEmbedded ? "built in" : "from file") : "no")}, signed in: {Auth.IsSignedIn}");
        Engine?.SetEnabled(AppState.Current.Settings.CloudSync);
    }

    public void Dispose()
    {
        AppState.Current.Saved -= OnSaved;
        AppState.Current.Forgotten -= OnForgotten;
        try { _signIn?.Cancel(); } catch { /* ignore */ }
        Engine?.Dispose();
    }

    private void OnSaved() => Engine?.LocalChanged();

    private void OnForgotten(string col, long id) => Engine?.Forget(col, id);

    private static void Write(string msg, Exception? ex)
    {
        var line = msg.StartsWith("SYNC", StringComparison.Ordinal) ? msg : "SYNC " + msg;
        if (ex == null) Log.Info(line);
        else Log.Warn($"{line}: {ex.GetType().Name}: {ex.Message}");
    }

    /// <summary>Coalesces bursts (a first sync raises hundreds) into one redraw.</summary>
    private void Raise()
    {
        if (Interlocked.Exchange(ref _raiseQueued, 1) == 1) return;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _raiseQueued, 0);
            Changed?.Invoke();
        });
    }

    // ── ISyncHost ──

    public void Invoke(Action action) => Application.Current.Dispatcher.BeginInvoke(action);

    public RemindlyData Data => AppState.Current.Data;

    public void AppliedRemote() => AppState.Current.ApplyRemote();

    public void BeforeFirstSync()
    {
        try
        {
            AppPaths.Ensure();
            var path = Path.Combine(AppPaths.BackupDir, $"remindly-data-before-first-sync-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(path, Json.Serialize(AppState.Current.Data));
            Log.Info("SYNC backup before the first sync: " + path);
        }
        catch (Exception ex) { Log.Error("SYNC backup before the first sync failed", ex); }
    }

    // ── actions (Settings → Account and sync) ──

    /// <summary>Both halves are here: the Firebase project (built in) and a Google Desktop client.</summary>
    public bool IsConfigured => Config != null && Auth.Client != null;

    /// <summary>Opens Google in the browser and waits. True when signed in.</summary>
    public async Task<bool> SignInAsync()
    {
        try { _signIn?.Cancel(); } catch { /* ignore */ }
        _signIn = new CancellationTokenSource();
        var ok = await Auth.SignInAsync(InstallInfo.OpenUrl, _signIn.Token);
        if (ok)
        {
            if (!AppState.Current.Settings.CloudSync) AppState.Current.UpdateSettingsQuiet(s => s with { CloudSync = true });
            Engine?.SetEnabled(true);
        }
        return ok;
    }

    public void CancelSignIn()
    {
        try { _signIn?.Cancel(); } catch { /* ignore */ }
    }

    public void ReopenBrowser()
    {
        if (Auth.PendingUrl is string u) InstallInfo.OpenUrl(u);
    }

    public void SetCloudSync(bool on)
    {
        AppState.Current.UpdateSettingsQuiet(s => s with { CloudSync = on });
        Engine?.SetEnabled(on);
        Raise();
    }

    public void SyncNow() => Engine?.SyncNow();

    /// <summary>Signs out; with [removeData] every record leaves this PC (a backup is written first — its path is returned).</summary>
    public string? SignOut(bool removeData)
    {
        Engine?.SignedOut();
        Auth.SignOut();
        var backup = removeData ? AppState.Current.RemoveAllData() : null;
        Log.Info("SYNC signed out" + (removeData ? " and removed this PC's data (backup " + backup + ")" : ""));
        Raise();
        return backup;
    }

    public bool ImportClientFile(string path)
    {
        try { return Auth.ImportClient(File.ReadAllText(path)); }
        catch (Exception ex) { Log.Warn("SYNC client file unreadable: " + ex.Message); return false; }
    }

    /// <summary>What the chip at the top right says.</summary>
    public ChipStatus Chip()
    {
        var e = Engine;
        if (e == null || !IsConfigured) return new(ChipKind.NotSyncing, "Not syncing", "Sign-in is not set up in this copy of Remindly");
        if (Auth.State == AuthState.Expired || e.Problem is SyncProblem.SignInExpired or SyncProblem.PermissionDenied or SyncProblem.Other)
            return new(ChipKind.Paused, "Sync paused — fix it", "Something needs you. Your changes are kept on this PC.");
        if (!Auth.IsSignedIn) return new(ChipKind.NotSyncing, "Not syncing", Auth.State == AuthState.SigningIn ? "Signing in…" : "Not signed in — open Account and sync");
        if (!AppState.Current.Settings.CloudSync) return new(ChipKind.NotSyncing, "Not syncing", "Cloud sync is switched off on this PC");
        int waiting = e.PendingCount;
        switch (e.Phase)
        {
            case SyncPhase.Offline:
                return new(ChipKind.Offline, waiting > 0 ? $"Offline · {waiting} waiting" : "Offline",
                    "No connection. Your changes are kept and sent when the PC is back online.");
            case SyncPhase.Connecting:
                return new(ChipKind.Syncing, "Syncing…", "Connecting to your phone's data");
            case SyncPhase.Syncing:
                return new(ChipKind.Syncing, waiting > 0 ? $"Syncing {waiting} change{(waiting == 1 ? "" : "s")}…" : "Syncing…", "Sending or receiving right now");
            case SyncPhase.Live:
                var last = Math.Max(e.State.LastFromCloudAt, e.State.LastSentAt);
                var ago = last > 0 ? ShopLists.RelativeAgo(last, Clock.NowMs()) : "";
                return new(ChipKind.Synced, ago.Length > 0 ? $"Synced · {ago}" : "Synced", "Everything on this PC is also on your phone, and the reverse.");
            case SyncPhase.Paused:
                return new(ChipKind.Paused, "Sync paused — fix it", "Something needs you. Your changes are kept on this PC.");
            default:
                return new(ChipKind.NotSyncing, "Not syncing", "Open Account and sync");
        }
    }
}
