using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Remindly.App.Services;
using Remindly.Core;
using Remindly.Core.Updates;

namespace Remindly.App.Views;

/// <summary>Settings: Updates (Auto update), General, Sharing a list, Backup &amp; Android import, Bin, About.</summary>
public sealed class SettingsView : ScrollViewer, IPage
{
    private readonly StackPanel _root = new() { MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _updateStatus = Ui.Sub("");
    private readonly TextBlock _updateTimes = Ui.Sub("");
    private readonly ProgressBar _updateProgress = new() { Height = 6, Maximum = 100, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _installBtn;
    private readonly Button _checkBtn;

    public SettingsView()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Content = _root;
        _checkBtn = Ui.Primary("Check now", async () =>
        {
            if (App.Updates == null) return;
            await App.Updates.CheckNowAsync(interactive: true);
            RefreshUpdates();
        });
        _installBtn = Ui.Btn("Version history / Choose version", async () =>
        {
            await OpenVersionPickerAsync();
            RefreshUpdates();
        });
        // Only while shown: a design switch replaces this page, and the old one must not keep listening.
        Loaded += (_, _) =>
        {
            if (App.Updates != null) App.Updates.Changed += OnUpdatesChanged;
            if (App.Cloud != null) App.Cloud.Changed += OnCloudChanged;
        };
        Unloaded += (_, _) =>
        {
            if (App.Updates != null) App.Updates.Changed -= OnUpdatesChanged;
            if (App.Cloud != null) App.Cloud.Changed -= OnCloudChanged;
        };
    }

    public void Refresh()
    {
        var state = AppState.Current;
        var s = state.Settings;
        _root.Children.Clear();
        _root.Children.Add(Ui.H1("Settings"));

        // ── Approved A+B+C: account entry, sync health and guided first-time setup ──
        var cloud = App.Cloud;
        var sync = Section("Account & Sync", "Keep Android and Windows on the same Google account, with safe two-way Firestore synchronization.");
        if (cloud?.ConfigurationError != null)
            sync.Children.Add(Ui.Sub(cloud.ConfigurationError));
        else if (cloud == null)
            sync.Children.Add(Ui.Sub("Cloud synchronization is not available in UI smoke-test mode."));
        else
        {
            sync.Children.Add(Ui.Text(cloud.SignedIn
                ? $"Google account: {cloud.Email}"
                : "Not connected to Google", 15, FontWeights.SemiBold));
            var current = cloud.Sync;
            sync.Children.Add(Ui.Sub($"Status: {current?.Status ?? Remindly.Core.Sync.CloudStatus.SignedOut} · " +
                $"Last successful sync: {(current?.LastSuccessAt > 0 ? Clock.ToLocal(current.LastSuccessAt).ToString("dd MMM yyyy, HH:mm") : "Never")}"));
            if (!string.IsNullOrEmpty(current?.LastError)) sync.Children.Add(Ui.Sub("Sync error: " + current.LastError));
            if (current?.Status == Remindly.Core.Sync.CloudStatus.Syncing)
                sync.Children.Add(new ProgressBar { Value = current.Progress, Maximum = 100, Height = 6, Margin = new Thickness(0, 8, 0, 8) });
            sync.Children.Add(Ui.Row(
                Ui.Primary(cloud.SignedIn ? "Account / Setup" : "Sign in with Google", OpenCloudWizard),
                Ui.Btn("Sync now", async () =>
                {
                    try { await cloud.SyncNowAsync(); }
                    catch (Exception ex) { Ui.Info("Sync could not complete: " + ex.Message); }
                }),
                Ui.Btn("Sign out", () =>
                {
                    if (Ui.Confirm("Sign out of Google on this PC? Your local Remindly data and backups will be kept."))
                        cloud.SignOut();
                })));
            sync.Children.Add(Ui.Setting("Automatic two-way sync on this PC", s.CloudSyncEnabled, enabled =>
            {
                try
                {
                    if (!enabled) cloud.Pause();
                    else if (!s.CloudSyncSetupComplete) OpenCloudWizard();
                    else cloud.Resume();
                }
                catch (Exception ex) { Ui.Info(ex.Message); }
                Refresh();
            }, "Android also needs Google Account → Cloud sync (beta) enabled."));
            sync.Children.Add(Ui.Sub("Syncs: Tasks, Learn, Calls, Buy items/lists, Shops and saved places. " +
                "Cities, chains and product catalogue are currently local on Android; they remain available through manual backup until Android cloud support is added."));
            sync.Children.Add(Ui.Sub("Conflict policy: newer updatedAt wins per record; deletedAt tombstones are retained. " +
                "The first connection creates a recoverable local backup before uploading."));
        }
        _root.Children.Add(Card(sync));

        // ── Appearance (Windows; macOS when its app exists — never on Android) ──
        var look = Section("Appearance", "Pick how Remindly looks on this PC. Only this PC changes: your phone keeps its own look and your data stays the same.");
        var designs = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        foreach (var d in Designs.All) designs.Children.Add(DesignCard(d, s.Design == d.Code));
        look.Children.Add(designs);
        _root.Children.Add(Card(look));

        // ── Updates ──
        var v = UpdateService.CurrentDisplayVersion;
        var upd = Section("Updates", $"Remindly for Windows {v} · {InstallInfo.ModeLabel}");
        upd.Children.Add(Ui.Setting("Auto update", s.UpdateAutoCheck, on =>
        {
            state.UpdateSettingsQuiet(x => x with { UpdateAutoCheck = on });
            RefreshUpdates();
        }, "Checks GitHub at startup and daily, then notifies you. An update is never installed until you choose its exact version."));
        upd.Children.Add(Ui.Setting("Include beta updates", s.UpdateBeta, on =>
        {
            state.UpdateSettingsQuiet(x => x with { UpdateBeta = on });
            App.Updates?.ResetChannel();
            RefreshUpdates();
        }, "Optional previews. Stable releases remain the default."));
        upd.Children.Add(Detach(_updateTimes));
        upd.Children.Add(Ui.Row(Detach(_checkBtn), Detach(_installBtn),
            Ui.Btn("ⓘ What's New / Implementations", async () => await ShowInstalledReleaseNotesAsync()),
            Ui.Btn("Releases page", () => InstallInfo.OpenUrl(UpdateLogic.ReleasesUrl))));
        upd.Children.Add(Detach(_updateProgress));
        upd.Children.Add(Detach(_updateStatus));
        _root.Children.Add(Card(upd));
        RefreshUpdates();

        // ── General ──
        var gen = Section("General", null);
        gen.Children.Add(Ui.Setting("Start with Windows", s.StartWithWindows, on =>
        {
            InstallInfo.SetStartWithWindows(on);
            state.UpdateSettingsQuiet(x => x with { StartWithWindows = on });
        }, "Starts hidden in the notification area so reminders fire even before you open Remindly."));
        gen.Children.Add(Ui.Setting("Keep running in the notification area when the window is closed", s.CloseToTray,
            on => state.UpdateSettingsQuiet(x => x with { CloseToTray = on }), "Off: closing the window exits Remindly and no reminders fire until you start it again."));
        var snooze = new ComboBox { Width = 90 };
        foreach (var m in new[] { 5, 10, 15, 30, 60 }) snooze.Items.Add(m);
        snooze.SelectedItem = new[] { 5, 10, 15, 30, 60 }.Contains(s.SnoozeMinutes) ? s.SnoozeMinutes : 10;
        snooze.SelectionChanged += (_, _) => state.UpdateSettingsQuiet(x => x with { SnoozeMinutes = (int)snooze.SelectedItem });
        var hour = new ComboBox { Width = 90 };
        for (int h = 0; h < 24; h++) hour.Items.Add($"{h:00}:00");
        hour.SelectedIndex = s.DefaultDueHour;
        hour.SelectionChanged += (_, _) => state.UpdateSettingsQuiet(x => x with { DefaultDueHour = hour.SelectedIndex });
        gen.Children.Add(Ui.Row(Ui.Sub("Snooze for "), snooze, Ui.Sub("  minutes      A date without a time rings at "), hour));
        _root.Children.Add(Card(gen));

        // ── Sharing a list ──
        var share = Section("Sharing a list", "The text is \"Groceries:-\", a blank line, then \"1. Milk - 2 / L - Urgent - Bought\".");
        share.Children.Add(Ui.Setting("Include bought items", s.ShareIncludeDone, on => state.UpdateSettingsQuiet(x => x with { ShareIncludeDone = on })));
        share.Children.Add(Ui.Setting("Quantity / type (\"2 / L\")", s.ShareIncludeQty, on => state.UpdateSettingsQuiet(x => x with { ShareIncludeQty = on })));
        share.Children.Add(Ui.Setting("\" - Urgent\" on urgent items", s.ShareUrgentTag, on => state.UpdateSettingsQuiet(x => x with { ShareUrgentTag = on })));
        share.Children.Add(Ui.Setting("\" - Bought\" on bought items", s.ShareBoughtTag, on => state.UpdateSettingsQuiet(x => x with { ShareBoughtTag = on })));
        var suffix = new TextBox { Text = s.ShareHeadingSuffix, Width = 70 };
        suffix.LostFocus += (_, _) => state.UpdateSettingsQuiet(x => x with { ShareHeadingSuffix = suffix.Text });
        share.Children.Add(Ui.Row(Ui.Sub("Text after the list name "), suffix));
        _root.Children.Add(Card(share));

        // ── Backup ──
        var bk = Section("Backup and your phone", "Import an Android backup (Settings → Backup on the phone, a remindly-data-*.json file) to bring your tasks, lists, calls and Buy items to this PC. Importing twice changes nothing; the newer copy of each record wins.");
        bk.Children.Add(Ui.Row(
            Ui.Primary("Import a backup…", Import),
            Ui.Btn("Export a backup…", Export),
            Ui.Btn("Open the data folder", () => InstallInfo.OpenUrl(AppPaths.DataRoot))));
        bk.Children.Add(Ui.Sub($"Saved automatically to {AppPaths.DataFile} · a daily copy is kept for 7 days."));
        _root.Children.Add(Card(bk));

        // ── Bin ──
        var binItems = state.Data.Items.Where(i => i.DeletedAt != null &&
            !s.CloudHiddenDeletedItemIds.Contains(i.Id)).OrderByDescending(i => i.DeletedAt).ToList();
        var binCalls = (state.Data.Calls ?? new()).Where(c => c.DeletedAt != null &&
            !s.CloudHiddenDeletedCallIds.Contains(c.Id)).OrderByDescending(c => c.DeletedAt).ToList();
        var bin = Section("Bin", $"{binItems.Count + binCalls.Count} deleted record(s) · kept for 30 days");
        foreach (var i in binItems.Take(40))
        {
            bin.Children.Add(Ui.Columns("*,Auto",
                Ui.Text($"{i.Title}  ·  {TabNames.Title(i.Tab)}  ·  deleted {Clock.FormatDate(i.DeletedAt!.Value)}", 13.5),
                Ui.Row(Ui.Btn("Restore", () => { state.RestoreFromBin(i); App.Current.Main.Snack($"Restored \"{i.Title}\""); }),
                       Ui.Btn("Delete forever", () => { if (Ui.Confirm($"Delete \"{i.Title}\" for good?")) state.DeleteForever(i); }, style: "Danger"))));
        }
        foreach (var c in binCalls.Take(20))
        {
            bin.Children.Add(Ui.Columns("*,Auto",
                Ui.Text($"{c.Display}  ·  Calls  ·  deleted {Clock.FormatDate(c.DeletedAt!.Value)}", 13.5),
                Ui.Row(Ui.Btn("Restore", () => state.UpsertCall(c with { DeletedAt = null })),
                       Ui.Btn("Delete forever", () => { if (Ui.Confirm($"Delete the call-back for {c.Display} for good?")) state.DeleteCallForever(c); }, style: "Danger"))));
        }
        if (binItems.Count + binCalls.Count > 0)
            bin.Children.Add(Ui.Btn("Empty the Bin", () =>
            {
                if (!Ui.Confirm("Delete everything in the Bin for good?")) return;
                state.EmptyBin();
            }, style: "Danger"));
        _root.Children.Add(Card(bin));

        // ── About ──
        var about = Section("About", null);
        about.Children.Add(Ui.Text($"Remindly {v} for Windows", 15, FontWeights.SemiBold));
        about.Children.Add(Ui.Sub($"{InstallInfo.ModeLabel} copy · {InstallInfo.ExePath}"));
        about.Children.Add(Ui.Sub("The Windows companion of the Remindly Android app: Tasks · Learn · Calls ⇄ Buy (lists) · Shops · Products. " +
            "Geofenced arrivals, call-log detection and maps stay on the phone; Account & Sync is now available on Windows."));
        about.Children.Add(Ui.Row(Ui.Btn("Source on GitHub", () => InstallInfo.OpenUrl(UpdateLogic.RepoUrl)), Ui.Btn("Log file", () => InstallInfo.OpenUrl(AppPaths.LogFile))));
        _root.Children.Add(Card(about));
    }

    private void OnCloudChanged() => Dispatcher.BeginInvoke(Refresh);

    public void OpenCloudWizard()
    {
        var cloud = App.Cloud;
        if (cloud == null) { Ui.Info("Google sign-in is not available in smoke-test mode."); return; }
        var window = new CloudSetupWindow(cloud) { Owner = App.Current.Main };
        window.ShowDialog();
        Refresh();
    }

    /// <summary>One design to choose: colour swatches, name, a line about it, and "In use" on the current one.</summary>
    private static Button DesignCard(DesignInfo d, bool selected)
    {
        var p = Theme.Palettes[d.Code];
        var swatches = Ui.Row();
        foreach (var hex in new[] { p.Surface, p.Card, p.Task.Accent, p.Shop.Accent, p.Ink })
            swatches.Children.Add(new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(6), Background = Theme.Brush(hex), BorderBrush = Ui.Res("BorderBrush"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 5, 0) });
        var name = Ui.Text($"{d.Letter} · {d.Name}" + (d.Code == Designs.Default ? " (default)" : ""), 15, FontWeights.SemiBold, wrap: false);
        name.FontFamily = Theme.FontOf(p.Font);
        var head = Ui.Columns("*,Auto", name, selected ? Ui.Pill("In use", Ui.Res("AccentInkBrush"), Ui.Res("AccentSoftBrush"), 12, FontWeights.Bold) : new Border());
        var summary = Ui.Sub(d.Summary);
        summary.Margin = new Thickness(0, 4, 0, 10);
        var body = Ui.Stack(head, summary, swatches);
        var b = Ui.Plain(body, () =>
        {
            if (selected) return;
            Application.Current.Dispatcher.BeginInvoke(() => App.Current.Main.ChooseDesign(d.Code));
        }, selected ? "The design in use" : $"Switch to {d.Name}");
        b.Padding = new Thickness(14, 12, 14, 12);
        b.Margin = new Thickness(0, 0, 10, 10);
        b.BorderThickness = new Thickness(selected ? 2 : 1);
        b.BorderBrush = selected ? Ui.Res("AccentBrush") : Ui.Res("BorderBrush");
        b.Background = Ui.Res("CardBrush");
        b.VerticalContentAlignment = VerticalAlignment.Top;
        System.Windows.Automation.AutomationProperties.SetName(b, $"Design {d.Letter}, {d.Name}" + (selected ? ", in use" : ""));
        return b;
    }

    private static T Detach<T>(T e) where T : FrameworkElement
    {
        (e.Parent as Panel)?.Children.Remove(e);
        return e;
    }

    private static StackPanel Section(string title, string? hint)
    {
        var p = new StackPanel();
        p.Children.Add(Ui.Text(title, 16, FontWeights.SemiBold));
        if (hint != null) { var h = Ui.Sub(hint); h.Margin = new Thickness(0, 2, 0, 8); p.Children.Add(h); }
        return p;
    }

    private static Border Card(UIElement child)
    {
        var c = Ui.Card(child);
        c.Padding = new Thickness(16, 12, 16, 14);
        c.Margin = new Thickness(0, 12, 0, 0);
        return c;
    }

    private void OnUpdatesChanged() => Dispatcher.BeginInvoke(RefreshUpdates);

    private void RefreshUpdates()
    {
        var u = App.Updates;
        var s = AppState.Current.Settings;
        var last = s.UpdateLastCheck > 0 ? Clock.ToLocal(s.UpdateLastCheck).ToString("ddd dd MMM, HH:mm", CultureInfo.InvariantCulture) : "never";
        var next = u != null && s.UpdateAutoCheck ? u.NextAutoCheck.ToString("ddd dd MMM", CultureInfo.InvariantCulture) : "—";
        _updateTimes.Text = s.UpdateAutoCheck ? $"Last check: {last} · next automatic check: {next}" : $"Last check: {last} · automatic checks are off";
        _updateStatus.Text = u?.Status ?? "";
        _checkBtn.IsEnabled = u is { Busy: false };
        _installBtn.Visibility = Visibility.Visible;
        _installBtn.Content = "Version history / Choose version";
        _installBtn.IsEnabled = u is { Busy: false };
        _updateProgress.Visibility = u is { Progress: >= 0 } ? Visibility.Visible : Visibility.Collapsed;
        _updateProgress.Value = Math.Max(0, u?.Progress ?? 0);
    }

    private sealed record VersionChoice(ReleaseInfo Release)
    {
        public override string ToString() =>
            $"v{Release.DisplayVersion}  ·  {(Release.IsBeta ? "Beta" : "Stable")}  ·  {Release.Name}";
    }

    /// <summary>Explicitly choose a published, checksum-backed Windows update or previous version.</summary>
    public async Task OpenVersionPickerAsync()
    {
        if (App.Updates is not { Busy: false } coordinator) return;
        IReadOnlyList<ReleaseInfo> catalog;
        try { catalog = await coordinator.ListVersionsAsync(CancellationToken.None); }
        catch (Exception ex) { Ui.Info("Could not load version history from GitHub: " + ex.Message); return; }

        var owner = Window.GetWindow(this);
        var window = new Window
        {
            Title = "Remindly · Version History",
            Width = 670, Height = 565, MinWidth = 520, MinHeight = 400,
            WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner, ShowInTaskbar = false,
        };
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock
        {
            Text = $"Installed: v{UpdateService.CurrentDisplayVersion} — choose the exact build; no release is preselected.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        });
        if (AppState.Current.Settings.UpdatePinnedVersion is { Length: > 0 } pinned)
        {
            var pinLabel = new TextBlock
            {
                Text = $"Pinned version: v{pinned}. Automatic checks cannot override your choice.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6)
            };
            var unpin = new Button
            {
                Content = "Clear version pin", HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 0, 10)
            };
            unpin.Click += (_, _) =>
            {
                coordinator.ClearVersionPin();
                pinLabel.Text = "Version pin removed.";
                unpin.IsEnabled = false;
            };
            root.Children.Add(pinLabel);
            root.Children.Add(unpin);
        }
        var mode = new ComboBox { Width = 210, Margin = new Thickness(0, 0, 12, 10), HorizontalAlignment = HorizontalAlignment.Left };
        mode.Items.Add("Newer updates");
        mode.Items.Add("Older versions / Rollback");
        mode.SelectedIndex = 0;
        var channel = new ComboBox { Width = 210, Margin = new Thickness(0, 0, 0, 10), HorizontalAlignment = HorizontalAlignment.Left };
        channel.Items.Add("All channels");
        channel.Items.Add("Stable only");
        channel.Items.Add("Beta only");
        channel.SelectedIndex = 0;
        root.Children.Add(Ui.Row(mode, channel));
        var options = new ListBox { Height = 235, Margin = new Thickness(0, 0, 0, 10) };
        root.Children.Add(options);
        var releaseSummary = new TextBlock { Text = "Select a release to see its actual implementations.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        root.Children.Add(releaseSummary);
        var seeChanges = new Button { Content = "ⓘ What's New in selected version", IsEnabled = false,
            Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 0, 9), HorizontalAlignment = HorizontalAlignment.Left };
        seeChanges.Click += (_, _) =>
        {
            if (options.SelectedItem is VersionChoice choice) ReleaseNotesWindow.Show(window, choice.Release);
        };
        root.Children.Add(seeChanges);
        var warning = new TextBlock
        {
            Text = "Before applying, Remindly backs up its data and verifies the download. Older installed-app downgrades need installer compatibility validation.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        };
        root.Children.Add(warning);
        var apply = new Button { Content = "Review selected version", IsEnabled = false, Padding = new Thickness(14, 8, 14, 8), HorizontalAlignment = HorizontalAlignment.Left };
        root.Children.Add(apply);
        window.Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        void Populate()
        {
            var rollback = mode.SelectedIndex == 1;
            var filtered = UpdateLogic.SelectVersions(catalog, UpdateService.CurrentDisplayVersion, rollback)
                .Where(r => channel.SelectedIndex switch { 1 => !r.IsBeta, 2 => r.IsBeta, _ => true })
                .Select(r => new VersionChoice(r)).ToList();
            options.ItemsSource = filtered;
            options.SelectedItem = null;
            apply.IsEnabled = false;
            seeChanges.IsEnabled = false;
            apply.Content = "Select a version to continue";
            if (filtered.Count == 0) warning.Text = "No Windows release with a published checksum matches this filter.";
            else warning.Text = "Your data will be backed up before installation. Download checksums are verified before replacement; an older version may not read newer data.";
        }
        mode.SelectionChanged += (_, _) => Populate();
        channel.SelectionChanged += (_, _) => Populate();
        options.SelectionChanged += (_, _) =>
        {
            apply.IsEnabled = options.SelectedItem is VersionChoice;
            seeChanges.IsEnabled = options.SelectedItem is VersionChoice;
            if (options.SelectedItem is VersionChoice choice)
            {
                apply.Content = mode.SelectedIndex == 1
                    ? $"Review rollback to v{choice.Release.DisplayVersion}"
                    : $"Install v{choice.Release.DisplayVersion}";
                var changes = ReleaseNotes.Parse(choice.Release.DisplayVersion, choice.Release.Body);
                releaseSummary.Text = changes.Count == 0 ? "Implementation details were not published for this release."
                    : $"Includes {changes.Count} documented implementation(s): " + string.Join("; ", changes.Take(3).Select(c => c.Title))
                      + (changes.Count > 3 ? "…" : "") + ". Select What's New to read every detail.";
            }
        };
        apply.Click += async (_, _) =>
        {
            if (options.SelectedItem is not VersionChoice chosen) return;
            var rollback = mode.SelectedIndex == 1;
            if (MessageBox.Show(window,
                    $"Installed: v{UpdateService.CurrentDisplayVersion}\nSelected: v{chosen.Release.DisplayVersion} ({(chosen.Release.IsBeta ? "Beta" : "Stable")})\n\n" +
                    "Back up data and install exactly this release? The application will restart." +
                    (rollback && !InstallInfo.IsInstalledMode()
                        ? "\n\nAutomatic update checks will be disabled in the older portable version to preserve your choice."
                        : ""),
                    rollback ? "Confirm rollback" : "Confirm selected update",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            if (rollback && InstallInfo.IsInstalledMode())
            {
                string? backup;
                try { backup = UpdateCoordinator.CreateVersionBackup(); }
                catch (Exception ex) { Ui.Info("Rollback stopped because your data could not be backed up: " + ex.Message); return; }
                MessageBox.Show(window, "Installed-edition downgrade has not passed compatibility testing. " +
                    "The selected release page will open for manual installer review. " +
                    (backup is null ? "There was no existing data file to back up." : "Your data backup is saved at: " + backup),
                    "Manual rollback required", MessageBoxButton.OK, MessageBoxImage.Information);
                InstallInfo.OpenUrl(chosen.Release.HtmlUrl);
                return;
            }
            window.Close();
            try { await coordinator.InstallSelectedAsync(chosen.Release); }
            catch (Exception ex) { Ui.Info("The selected version could not be installed: " + ex.Message); }
        };
        Populate();
        window.ShowDialog();
    }

    private async Task ShowInstalledReleaseNotesAsync()
    {
        var version = UpdateService.CurrentDisplayVersion;
        IReadOnlyList<ReleaseInfo> releases = [];
        try { if (App.Updates is { } updater) releases = await updater.ListVersionsAsync(CancellationToken.None); }
        catch { /* The 2.16.0 implementation catalog also works offline. */ }
        var installed = releases.FirstOrDefault(r =>
            string.Equals(r.DisplayVersion, version, StringComparison.OrdinalIgnoreCase));
        installed ??= new ReleaseInfo(UpdateLogic.ParseVersion(version) ?? new Version(0, 0, 0),
            "v" + version, "Remindly " + version, UpdateLogic.ReleasesUrl, null,
            version.Contains("-beta", StringComparison.OrdinalIgnoreCase), []);
        ReleaseNotesWindow.Show(Window.GetWindow(this), installed);
    }

    private void Import()
    {
        var dlg = new OpenFileDialog { Filter = "Remindly backup (*.json)|*.json|All files|*.*", Title = "Import a Remindly backup (Android or Windows)" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var blob = DataStore.Parse(File.ReadAllText(dlg.FileName));
            if (blob == null) { Ui.Info("That file is not a Remindly backup."); return; }
            var snap = AppState.Current.TakeSnapshot();
            var r = AppState.Current.Import(blob);
            Ui.Info($"Imported from {(r.FromAndroid ? "your phone" : "a Windows backup")}:\n\n• {r.Items} task / Learn / Buy item(s) new or newer\n• {r.Lists} new list(s)\n• {r.Calls} call-back(s)\n• {r.Shops} shop(s), {r.Products} product(s)");
            App.Current.Main.Snack("Backup imported", () => AppState.Current.Restore(snap));
        }
        catch (Exception ex)
        {
            Log.Error("Import failed", ex);
            Ui.Info("Import failed: " + ex.Message);
        }
    }

    private void Export()
    {
        var dlg = new SaveFileDialog { Filter = "Remindly backup (*.json)|*.json", FileName = $"remindly-windows-{DateTime.Now:yyyy-MM-dd}.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            AppState.Current.Export(dlg.FileName);
            App.Current.Main.Snack("Backup saved");
        }
        catch (Exception ex) { Ui.Info("Export failed: " + ex.Message); }
    }
}
