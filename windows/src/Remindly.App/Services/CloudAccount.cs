using System.Windows;
using Remindly.Core;
using Remindly.Core.Sync;

namespace Remindly.App.Services;

/// <summary>
/// Bridges the Firebase transport to the WPF-owned AppState. Authentication is via the user's
/// browser; refresh tokens and the Google Desktop client are encrypted using Windows DPAPI.
/// </summary>
public sealed class CloudAccount : IDisposable
{
    public FirebaseAuth? Auth { get; }
    public DesktopCloudSync? Sync { get; }
    public string? ConfigurationError { get; }
    public event Action? Changed;

    public bool Ready => Auth != null && Sync != null;
    public bool SignedIn => Auth?.IsSignedIn == true;
    public string? Email => Auth?.Session?.Email;

    public CloudAccount()
    {
        try
        {
            var config = FirebaseConfig.Load();
            Auth = new FirebaseAuth(config, Path.Combine(AppPaths.DataRoot, "secure"));
            Auth.Log = (message, error) =>
            {
                if (error == null) Log.Info("AUTH " + message);
                else Log.Error("AUTH " + message, error);
            };
            var gateway = new FirestoreTransport(config, Auth);
            Sync = new DesktopCloudSync(gateway, Auth,
                snapshot: () => Application.Current.Dispatcher.Invoke(() =>
                    Json.Deserialize(Json.Serialize(AppState.Current.Data))!),
                apply: incoming => Application.Current.Dispatcher.Invoke(() => AppState.Current.ApplyCloud(incoming)));
            Auth.Changed += Notify;
            Sync.Changed += Notify;
        }
        catch (Exception ex)
        {
            ConfigurationError = "Firebase configuration is missing or invalid in this Windows build.";
            Log.Error("Cloud configuration could not start", ex);
        }
    }

    private void Notify()
    {
        if (Application.Current.Dispatcher.CheckAccess()) Changed?.Invoke();
        else Application.Current.Dispatcher.BeginInvoke(() => Changed?.Invoke());
    }

    /// <summary>Start persisted sync only after the main window has initialized its UI listeners.</summary>
    public void StartExistingAccount() => Sync?.SetEnabled(
        AppState.Current.Settings.CloudSyncEnabled && AppState.Current.Settings.CloudSyncSetupComplete);

    public void SaveDesktopClient(string json)
    {
        if (Auth == null) throw new InvalidOperationException(ConfigurationError);
        var client = OAuthClient.ParseJson(json) ??
            throw new ArgumentException("Select the JSON credentials for a Google OAuth Desktop app, not a Web app.");
        Auth.SaveClient(client);
        Notify();
    }

    public async Task<bool> SignInAsync()
    {
        if (Auth == null) throw new InvalidOperationException(ConfigurationError);
        var success = await Auth.SignInAsync(CancellationToken.None);
        if (success) Sync?.SetEnabled(AppState.Current.Settings.CloudSyncEnabled &&
                                       AppState.Current.Settings.CloudSyncSetupComplete);
        Notify();
        return success;
    }

    /// <summary>Mandatory recoverable backup before the FIRST Firestore write from this PC.</summary>
    public string EnableAfterSafeMergeApproval()
    {
        if (!SignedIn) throw new InvalidOperationException("Sign in with Google first.");
        AppState.Current.Save();
        var backup = Path.Combine(AppPaths.BackupDir,
            $"pre-cloud-sync-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(AppPaths.BackupDir);
        File.Copy(AppPaths.DataFile, backup);
        AppState.Current.UpdateSettings(s => s with
        {
            CloudSyncSetupComplete = true,
            CloudSyncEnabled = true,
        });
        Sync?.SetEnabled(true);
        Notify();
        return backup;
    }

    public void Pause()
    {
        AppState.Current.UpdateSettingsQuiet(s => s with { CloudSyncEnabled = false });
        Sync?.SetEnabled(false);
        Notify();
    }

    public void Resume()
    {
        if (!SignedIn || !AppState.Current.Settings.CloudSyncSetupComplete)
            throw new InvalidOperationException("Complete first-time sign-in and safe merge setup.");
        AppState.Current.UpdateSettingsQuiet(s => s with { CloudSyncEnabled = true });
        Sync?.SetEnabled(true);
        Notify();
    }

    public async Task SyncNowAsync()
    {
        if (Sync == null) throw new InvalidOperationException(ConfigurationError);
        await Sync.SyncNowAsync();
    }

    public void SignOut()
    {
        Pause();
        Auth?.SignOut();
        Notify();
    }

    public void Dispose()
    {
        if (Auth != null) Auth.Changed -= Notify;
        if (Sync != null) { Sync.Changed -= Notify; Sync.Dispose(); }
    }
}
