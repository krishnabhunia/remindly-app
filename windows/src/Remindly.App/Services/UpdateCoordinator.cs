using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Remindly.Core;
using Remindly.Core.Updates;

namespace Remindly.App.Services;

/// <summary>
/// Version-aware update behavior:
///  • checks GitHub at startup and then daily when enabled;
///  • automatic checks notify but never download or install a release on their own;
///  • only the exact version the user selects may be downloaded, checksum-verified and installed;
///  • a chosen version becomes pinned only after that executable successfully starts.
/// With the box unticked nothing is checked automatically; Settings → Updates → Check now still works.
/// </summary>
public sealed class UpdateCoordinator
{
    private readonly UpdateService _service = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(30) };
    private readonly DispatcherTimer _pendingTimer = new() { Interval = TimeSpan.FromMinutes(2) };
    private string? _pendingFile;
    private bool _busy;
    private string? _pendingVersionPin;
    private bool _restoreAutoCheckOnFailure;

    public UpdateCheckResult? Last { get; private set; }
    public string Status { get; private set; } = "";
    public int Progress { get; private set; } = -1;
    public bool Busy => _busy;
    public event Action? Changed;

    public UpdateCoordinator()
    {
        _timer.Tick += async (_, _) => await TickAsync();
        _pendingTimer.Tick += (_, _) => TryApplyPending();
    }

    public void Start()
    {
        // Complete a pin only when the selected executable is actually running.
        // If an installer failed and the previous version reopened, discard the pending pin.
        var s = AppState.Current.Settings;
        if (s.UpdatePendingPinnedVersion is { Length: > 0 } pending)
        {
            var installed = UpdateService.CurrentDisplayVersion;
            AppState.Current.UpdateSettingsQuiet(x => x with
            {
                UpdatePinnedVersion = string.Equals(pending, installed, StringComparison.OrdinalIgnoreCase)
                    ? pending : x.UpdatePinnedVersion,
                UpdatePendingPinnedVersion = null
            });
        }
        else if (s.UpdatePinnedVersion is { Length: > 0 } pinned &&
                 !string.Equals(pinned, UpdateService.CurrentDisplayVersion, StringComparison.OrdinalIgnoreCase))
        {
            AppState.Current.UpdateSettingsQuiet(x => x with { UpdatePinnedVersion = null });
        }
        // Keep Remindly.old.exe as a recoverable previous portable build.
        SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == PowerModes.Resume) Application.Current.Dispatcher.BeginInvoke(async () => await TickAsync());
        };
        _timer.Start();
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, async () =>
        {
            if (AppState.Current.Settings.UpdateAutoCheck) await CheckNowAsync(interactive: false);
        });
    }

    public void Stop()
    {
        _timer.Stop();
        _pendingTimer.Stop();
    }

    public DateTime NextAutoCheck => Clock.ToLocal(UpdateLogic.NextCheckAt(AppState.Current.Settings.UpdateLastCheck, Clock.NowMs()));

    private async Task TickAsync()
    {
        var s = AppState.Current.Settings;
        if (_busy || !UpdateLogic.IsCheckDue(s.UpdateLastCheck, Clock.NowMs(), s.UpdateAutoCheck)) return;
        await CheckNowAsync(interactive: false);
    }

    /// <summary>List exact targets; a version cannot be installed until the user selects it.</summary>
    public Task<IReadOnlyList<ReleaseInfo>> ListVersionsAsync(CancellationToken ct) => _service.ListVersionsAsync(ct);

    /// <summary>Explicit, verified version change, including a requested older beta/stable release.</summary>
    public async Task InstallSelectedAsync(ReleaseInfo release)
    {
        if (_busy || UpdateLogic.PickZip(release) is not ReleaseAsset asset ||
            UpdateLogic.PickChecksum(release, asset) == null)
            throw new InvalidOperationException("This release has no verified Windows installer.");
        if (string.Equals(release.DisplayVersion, UpdateService.CurrentDisplayVersion, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This version is already installed.");

        // Stop before changing binaries if the data file cannot be read or backed up.
        CreateVersionBackup();
        Last = new UpdateCheckResult(UpdateStatus.UpdateAvailable, UpdateService.CurrentVersion, release,
            $"Selected Remindly {release.DisplayVersion} for installation.");
        _pendingVersionPin = release.DisplayVersion;
        await DownloadAndInstallAsync(background: false);
    }

    /// <summary>Save validated user data before any version change, including manual installed rollback.</summary>
    public static string? CreateVersionBackup()
    {
        if (!File.Exists(AppPaths.DataFile)) return null;
        if ((File.GetAttributes(AppPaths.DataFile) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The data file is a link. The version change was stopped.");
        if (DataStore.Parse(File.ReadAllText(AppPaths.DataFile)) is null)
            throw new InvalidDataException("The current data file could not be validated. The version change was stopped.");
        Directory.CreateDirectory(AppPaths.BackupDir);
        var backup = Path.Combine(AppPaths.BackupDir,
            $"before-version-change-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.json");
        File.Copy(AppPaths.DataFile, backup, overwrite: false);
        Log.Info("Saved pre-version-change data backup: " + backup);
        return backup;
    }

    public void ClearVersionPin()
    {
        _pendingVersionPin = null;
        AppState.Current.UpdateSettingsQuiet(x => x with { UpdatePinnedVersion = null, UpdatePendingPinnedVersion = null });
        SetStatus("Version pin removed. You can choose another release.");
    }

    private bool StageSelectedVersionPin()
    {
        if (_pendingVersionPin is not { } version) return false;
        // An older portable executable may not understand the new version pin and
        // may silently reinstall the newest build. Disable the legacy auto-update
        // setting before the old executable starts.
        bool disableLegacyAutoUpdate = UpdateLogic.RequiresLegacyAutoCheckOff(
            UpdateService.CurrentDisplayVersion, version, portableMode: !InstallInfo.IsInstalledMode());
        _restoreAutoCheckOnFailure = disableLegacyAutoUpdate && AppState.Current.Settings.UpdateAutoCheck;
        AppState.Current.UpdateSettingsQuiet(x => x with
        {
            UpdatePendingPinnedVersion = version,
            UpdateAutoCheck = disableLegacyAutoUpdate ? false : x.UpdateAutoCheck
        });
        var saved = DataStore.Parse(File.ReadAllText(AppPaths.DataFile));
        if (!string.Equals(saved?.Settings.UpdatePendingPinnedVersion, version, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Could not persist the selected version safely; the update was stopped.");
        _pendingVersionPin = null;
        return true;
    }

    /// <summary>Checks now. Automatic checks only notify; they never select/install the latest version.</summary>
    public async Task<UpdateCheckResult?> CheckNowAsync(bool interactive)
    {
        if (_busy) return Last;
        _busy = true;
        SetStatus("Checking GitHub for a new version…");
        try
        {
            var beta = AppState.Current.Settings.UpdateBeta;
            var r = await _service.CheckAsync(CancellationToken.None, beta);
            if (beta != AppState.Current.Settings.UpdateBeta) { ResetChannel(); return null; }
            Last = r;
            AppState.Current.UpdateSettingsQuiet(x => x with { UpdateLastCheck = Clock.NowMs() });
            SetStatus(r.Message);
            if (r.Status == UpdateStatus.UpdateAvailable)
            {
                if (AppState.Current.Settings.UpdatePinnedVersion is { Length: > 0 } pin)
                    SetStatus($"Pinned to Remindly {pin}. Open version history to choose another release.");
                else App.Tray?.Balloon("Remindly update", r.Message + " Open Settings → Updates to choose a version.");
            }
            return r;
        }
        catch (Exception ex)
        {
            Log.Error("Update check failed", ex);
            SetStatus("Update check failed: " + ex.Message);
            return null;
        }
        finally { _busy = false; Changed?.Invoke(); }
    }

    /// <summary>Downloads + verifies the newer release, then installs it (now, or at the next quiet moment when background).</summary>
    public async Task DownloadAndInstallAsync(bool background)
    {
        if (_busy || Last?.Release is not ReleaseInfo release || Last.Status != UpdateStatus.UpdateAvailable) return;
        if (_pendingVersionPin is null)
        {
            SetStatus("Choose the exact version from Version History before downloading.");
            return;
        }
        _busy = true;
        try
        {
            bool installed = InstallInfo.IsInstalledMode();
            SetStatus($"Downloading Remindly {release.DisplayVersion}…");
            var zip = await _service.DownloadAsync(release, new Progress<int>(p => { Progress = p; Changed?.Invoke(); }), CancellationToken.None);
            var file = await Task.Run(() => UpdateService.ExtractForMode(zip, installed));
            Progress = -1;
            _pendingFile = file;
            if (background && UserIsBusy())
            {
                SetStatus($"Remindly {release.DisplayVersion} is downloaded and verified — it installs as soon as you are not using Remindly.");
                _pendingTimer.Start();
                return;
            }
            Apply();
        }
        catch (Exception ex)
        {
            Progress = -1;
            Log.Error("Update download/install failed", ex);
            _pendingVersionPin = null;
            SetStatus("Update failed: " + ex.Message);
            if (background) App.Tray?.Balloon("Remindly update failed", ex.Message, warning: true);
        }
        finally { _busy = false; Changed?.Invoke(); }
    }

    private void TryApplyPending()
    {
        if (_pendingFile == null) { _pendingTimer.Stop(); return; }
        if (UserIsBusy()) return;
        _pendingTimer.Stop();
        Apply();
    }

    public void ResetChannel()
    {
        Last = null;
        _pendingFile = null;
        _pendingTimer.Stop();
        SetStatus("Update channel changed. Check now to refresh.");
    }

    /// <summary>An editor or reminder is open, or Remindly is the window the user is working in.</summary>
    private static bool UserIsBusy()
    {
        var app = Application.Current;
        if (App.Reminders?.AnyAlertOpen == true) return true;
        foreach (Window w in app.Windows)
            if (w != app.MainWindow && w.IsVisible) return true;
        return app.MainWindow is { IsVisible: true, IsActive: true };
    }

    private void Apply()
    {
        var file = _pendingFile;
        if (file == null || !File.Exists(file)) return;
        _pendingFile = null;
        bool hidden = Application.Current.MainWindow is not { IsVisible: true };
        bool attemptedStagePin = false;
        bool replacedPortable = false;
        string? originalPortable = null;
        string? previousPortable = null;
        try
        {
            if (InstallInfo.IsInstalledMode())
            {
                // Setup finds this copy running, asks it to exit (--exit → the Exit event), installs and starts it again.
                var args = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /REMINDLYUPDATE=1" + (hidden ? " /RELAUNCH=tray" : "");
                attemptedStagePin = true;
                StageSelectedVersionPin();
                using var installer = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(file)! });
                if (installer is null) throw new InvalidOperationException("The selected installer did not start.");
                Log.Info("Started Setup for the selected update: " + file);
                App.Current.ExitApp("installing update");
                return;
            }

            // Portable: the running exe can be renamed (not deleted) — move it aside, put the new one in its place, restart.
            var exe = InstallInfo.ExePath;
            var old = Path.Combine(InstallInfo.ExeDirectory, "Remindly.old.exe");
            try { if (File.Exists(old)) File.Delete(old); } catch { }
            File.Move(exe, old);
            try { File.Copy(file, exe, overwrite: true); }
            catch
            {
                // File.Copy can leave a partially written destination. Remove it
                // before restoring the original executable.
                try { if (File.Exists(exe)) File.Delete(exe); } catch { /* report original copy failure */ }
                if (!File.Exists(exe)) File.Move(old, exe);
                throw;
            }
            originalPortable = exe;
            previousPortable = old;
            replacedPortable = true;
            attemptedStagePin = true;
            StageSelectedVersionPin();
            using var restarted = Process.Start(new ProcessStartInfo(exe, "--updated" + (hidden ? " --tray" : "")) { UseShellExecute = false, WorkingDirectory = InstallInfo.ExeDirectory });
            if (restarted is null) throw new InvalidOperationException("The selected portable executable did not start.");
            Log.Info("Portable copy replaced; restarting " + exe);
            App.Current.ExitApp("portable update applied");
        }
        catch (Exception ex)
        {
            _pendingVersionPin = null;
            if (attemptedStagePin)
                AppState.Current.UpdateSettingsQuiet(x => x with
                {
                    UpdatePendingPinnedVersion = null,
                    UpdateAutoCheck = _restoreAutoCheckOnFailure || x.UpdateAutoCheck
                });
            _restoreAutoCheckOnFailure = false;
            string recovery = "";
            if (replacedPortable && originalPortable != null && previousPortable != null && File.Exists(previousPortable))
            {
                string? failed = null;
                try
                {
                    if (File.Exists(originalPortable))
                    {
                        failed = Path.Combine(AppPaths.UpdatesDir, "Remindly-failed-" + Guid.NewGuid().ToString("N") + ".exe");
                        File.Move(originalPortable, failed);
                    }
                    File.Move(previousPortable, originalPortable);
                    recovery = " The previous executable was restored.";
                }
                catch (Exception restoreError)
                {
                    if (failed != null && !File.Exists(originalPortable) && File.Exists(failed))
                    {
                        try { File.Move(failed, originalPortable); } catch { /* preserve the recovery error */ }
                    }
                    Log.Error("Could not restore the old portable executable", restoreError);
                    recovery = " Restore the previous executable manually from " + previousPortable + ".";
                }
            }
            Log.Error("Applying the update failed", ex);
            SetStatus("Could not install the selected version: " + ex.Message + "." + recovery);
            App.Tray?.Balloon("Remindly update", "The selected version could not be started. Open Settings → Updates.", warning: true);
        }
    }

    private void SetStatus(string s)
    {
        Status = s;
        Changed?.Invoke();
    }
}
