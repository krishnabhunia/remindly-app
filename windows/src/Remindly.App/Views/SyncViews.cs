using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Remindly.App.Services;
using Remindly.Core;
using Remindly.Core.Sync;

namespace Remindly.App.Views;

/// <summary>Glyphs (Segoe Fluent Icons) used by sign-in and sync.</summary>
internal static class SyncGlyphs
{
    public const string Cloud = "";
    public const string Sync = "";
    public const string Warning = "";
    public const string Error = "";
    public const string Lock = "";
    public const string Phone = "";
    public const string Check = "";
}

/// <summary>
/// The sync chip beside the version at the top right, in every design (approved design, screen 6):
/// Synced · Syncing · Offline (n waiting) · Sync paused — fix it · Not syncing. A click opens Settings.
/// </summary>
public sealed class SyncChip : Button
{
    public SyncChip()
    {
        Padding = new Thickness(12, 5, 14, 5);
        Margin = new Thickness(0, 0, 8, 0);
        FontWeight = FontWeights.SemiBold;
        VerticalAlignment = VerticalAlignment.Center;
        Click += (_, _) => App.Current.Main.OpenSettings();
        Refresh();
    }

    public void Refresh()
    {
        var st = App.Sync?.Chip() ?? new ChipStatus(ChipKind.NotSyncing, "Not syncing", "Open Account and sync");
        var (fg, bg, border, glyph) = st.Kind switch
        {
            ChipKind.Synced => ("SuccessBrush", "SuccessSoftBrush", "SuccessSoftBrush", SyncGlyphs.Cloud),
            ChipKind.Syncing => ("AccentInkBrush", "AccentSoftBrush", "AccentSoftBrush", SyncGlyphs.Sync),
            ChipKind.Offline => ("AmberBrush", "AmberSoftBrush", "AmberSoftBrush", SyncGlyphs.Warning),
            ChipKind.Paused => ("DangerBrush", "DangerSoftBrush", "DangerSoftBrush", SyncGlyphs.Error),
            _ => ("InkSubtleBrush", "CardBrush", "BorderBrush", SyncGlyphs.Cloud),
        };
        Foreground = Ui.Res(fg);
        Background = Ui.Res(bg);
        BorderBrush = Ui.Res(border);
        BorderThickness = new Thickness(1);
        FontWeight = st.Kind == ChipKind.NotSyncing ? FontWeights.Normal : FontWeights.SemiBold;
        Content = Ui.Row(Ui.Glyph(glyph, 14, Foreground), Ui.Text("  " + st.Text, 13, FontWeight, Foreground, wrap: false));
        ToolTip = st.Tip;
        System.Windows.Automation.AutomationProperties.SetName(this, $"Cloud sync: {st.Text}. Open Account and sync");
    }
}

/// <summary>
/// Settings → Account and sync (approved design, screens 1, 5 and 7). Redraws itself while it is on screen,
/// so a sync in progress never rebuilds the rest of the Settings page.
/// </summary>
public sealed class AccountCard : Border
{
    private readonly StackPanel _body = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(30) };

    public AccountCard()
    {
        Style = (Style)Application.Current.Resources["Card"];
        Padding = new Thickness(16, 12, 16, 14);
        Margin = new Thickness(0, 12, 0, 0);
        Child = _body;
        _tick.Tick += (_, _) => Build();
        Loaded += (_, _) => { if (App.Sync != null) App.Sync.Changed += Build; _tick.Start(); Build(); };
        Unloaded += (_, _) => { if (App.Sync != null) App.Sync.Changed -= Build; _tick.Stop(); };
        Build();
    }

    private void Build()
    {
        _body.Children.Clear();
        var sync = App.Sync;
        var title = Ui.Text("Account and sync", 16, FontWeights.SemiBold, wrap: false);
        if (sync == null || !sync.IsConfigured) { NotConfigured(title, sync); return; }
        var auth = sync.Auth;
        if (auth.State == AuthState.Expired && !auth.IsSignedIn)
        {
            _body.Children.Add(title);
            _body.Children.Add(ProblemCards.SignInAgain(auth.LastEmail));
            return;
        }
        if (!auth.IsSignedIn) { SignedOut(title, auth.State == AuthState.SigningIn); return; }
        SignedIn(title, sync);
    }

    // ── screen 1: signed out ──

    private void SignedOut(TextBlock title, bool busy)
    {
        _body.Children.Add(Ui.Row(title, Space(10), Ui.Pill("New", Ui.Res("AccentInkBrush"), Ui.Res("AccentSoftBrush"), 12, FontWeights.SemiBold)));
        var intro = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new Thickness(0, 8, 0, 12) };
        intro.SetResourceReference(TextBlock.ForegroundProperty, "InkSubtleBrush");
        intro.Inlines.Add("Sign in with the ");
        var em = new System.Windows.Documents.Run("same Google account you use in Remindly on your phone") { FontWeight = FontWeights.SemiBold };
        em.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "InkBrush");
        intro.Inlines.Add(em);
        intro.Inlines.Add(". Your tasks, learning, calls, Buy lists, shops and products then stay the same on both, a few seconds after each change.");
        _body.Children.Add(intro);

        var signIn = Ui.Btn("", () => SignInFlow.Start(), busy ? "Finish signing in in your browser" : "Opens your browser");
        signIn.Content = Ui.Row(GoogleMark(), Ui.Text("  Sign in with Google", 14, FontWeights.SemiBold, wrap: false));
        signIn.Padding = new Thickness(16, 9, 18, 9);
        signIn.MinHeight = 44;
        signIn.IsEnabled = !busy;
        var note = Ui.Sub(busy ? "Waiting for the browser…" : "Opens your browser. Remindly never sees your password.");
        note.Margin = new Thickness(14, 0, 0, 0);
        note.VerticalAlignment = VerticalAlignment.Center;
        _body.Children.Add(Ui.Row(signIn, note));

        var grid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 14, 0, 0) };
        grid.Children.Add(Panel("Syncs with your phone", "Tasks · Learn · Calls · Buy items and lists · Shops · Products and prices · cities and chains · shared preferences", "SuccessSoftBrush", "SuccessBrush"));
        grid.Children.Add(Panel("Stays on this PC only", "Design choice · Start with Windows · notification area · Auto update · Cloud sync switch", "SurfaceBrush", "InkBrush"));
        _body.Children.Add(grid);
    }

    // ── screen 5: signed in ──

    private void SignedIn(TextBlock title, SyncService sync)
    {
        var auth = sync.Auth;
        var e = sync.Engine!;
        var s = auth.Session!;
        _body.Children.Add(title);

        var initials = Initials(s.DisplayName, s.Email);
        var avatar = new Border
        {
            Width = 44, Height = 44, CornerRadius = new CornerRadius(22), Background = Ui.Res("AccentBrush"),
            Child = new TextBlock { Text = initials, Foreground = Ui.Res("OnAccentBrush"), FontSize = 16, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var who = Ui.Stack(Ui.Text(string.IsNullOrWhiteSpace(s.DisplayName) ? s.Email : s.DisplayName!, 15, FontWeights.SemiBold), Ui.Sub(s.Email + " · Google"));
        who.Margin = new Thickness(12, 0, 0, 0);
        who.VerticalAlignment = VerticalAlignment.Center;
        var signOut = Ui.Btn("Sign out", () => SignOutWindow.OpenFor(), "Stop syncing this PC");
        signOut.SetResourceReference(Control.ForegroundProperty, "DangerBrush");
        signOut.MinHeight = 40;
        var head = Ui.Columns("Auto,*,Auto", avatar, who, signOut);
        head.Margin = new Thickness(0, 10, 0, 12);
        _body.Children.Add(head);

        var problem = ProblemCards.For(sync);
        if (problem != null) _body.Children.Add(problem);

        _body.Children.Add(Divider());
        var on = AppState.Current.Settings.CloudSync;
        var toggle = Ui.Setting("Cloud sync", on, v => sync.SetCloudSync(v),
            "Sends every change to your phone and takes its changes here. Switch off to keep this PC on its own for a while.");
        _body.Children.Add(toggle);

        var tiles = new UniformGrid { Columns = 4, Margin = new Thickness(0, 10, 0, 12) };
        tiles.Children.Add(Tile("Status", StatusText(sync), StatusBrush(sync)));
        tiles.Children.Add(Tile("Last change from phone", When(e.State.LastFromCloudAt), null));
        tiles.Children.Add(Tile("Last sent from this PC", When(e.State.LastSentAt), null));
        tiles.Children.Add(Tile("Waiting to send", $"{e.PendingCount} change{(e.PendingCount == 1 ? "" : "s")}", null));
        _body.Children.Add(tiles);

        var now = Ui.Primary("Sync now", () => sync.SyncNow(), "Send what is waiting and check the cloud again");
        now.IsEnabled = on;
        _body.Children.Add(Ui.Row(now, Ui.Btn("Sync log", () => InstallInfo.OpenUrl(AppPaths.LogFile), "Sync lines start with SYNC in the log file")));
    }

    // ── no sign-in in this build ──

    private void NotConfigured(TextBlock title, SyncService? sync)
    {
        _body.Children.Add(title);
        var hasFirebase = sync?.Config != null;
        var text = Ui.Sub(hasFirebase
            ? "To sign in, this copy of Remindly needs a Google sign-in client. Choose the client file (client_secret_….json) downloaded from Google Cloud, or update to a build that has it built in."
            : "Sign-in is not set up in this copy of Remindly (a developer build). Download Remindly from the Releases page to sign in and sync with your phone.");
        text.FontSize = 14;
        text.Margin = new Thickness(0, 6, 0, 10);
        _body.Children.Add(text);
        var row = Ui.Row();
        if (hasFirebase)
            row.Children.Add(Ui.Primary("Use a Google client file…", () =>
            {
                var dlg = new OpenFileDialog { Filter = "Google client file (*.json)|*.json", Title = "Choose the Google Desktop client file" };
                if (dlg.ShowDialog() != true) return;
                if (!sync!.ImportClientFile(dlg.FileName)) Ui.Info("That file is not a Google Desktop app client (client_secret_….json).");
                Build();
            }));
        row.Children.Add(Ui.Btn("How to set it up", () => InstallInfo.OpenUrl(UpdateLinks.SignInSetup)));
        _body.Children.Add(row);
    }

    // ── bits ──

    private static string StatusText(SyncService sync)
    {
        var e = sync.Engine!;
        if (!AppState.Current.Settings.CloudSync) return "Off on this PC";
        if (e.Problem is SyncProblem.PermissionDenied or SyncProblem.Other or SyncProblem.SignInExpired) return "Paused";
        return e.Phase switch
        {
            SyncPhase.Live => "Up to date",
            SyncPhase.Syncing => "Sending…",
            SyncPhase.Connecting => "Connecting…",
            SyncPhase.Offline => "Offline",
            SyncPhase.Paused => "Paused",
            _ => "Not syncing",
        };
    }

    private static Brush? StatusBrush(SyncService sync) => StatusText(sync) switch
    {
        "Up to date" => Ui.Res("SuccessBrush"),
        "Paused" => Ui.Res("DangerBrush"),
        "Offline" => Ui.Res("AmberBrush"),
        _ => null,
    };

    internal static string When(long ms)
    {
        if (ms <= 0) return "—";
        var local = Clock.ToLocal(ms);
        var day = local.Date == DateTime.Today ? "Today" : local.Date == DateTime.Today.AddDays(-1) ? "Yesterday" : local.ToString("dd MMM", CultureInfo.InvariantCulture);
        return $"{day}, {local:HH:mm}";
    }

    private static string Initials(string? name, string email)
    {
        var src = string.IsNullOrWhiteSpace(name) ? email.Split('@')[0] : name!;
        var parts = src.Split(new[] { ' ', '.', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
        var s = parts.Length >= 2 ? $"{parts[0][0]}{parts[^1][0]}" : src.Length > 0 ? src[..Math.Min(2, src.Length)] : "?";
        return s.ToUpperInvariant();
    }

    private static Border Tile(string label, string value, Brush? valueBrush)
    {
        var v = Ui.Text(value, 15, FontWeights.SemiBold, valueBrush);
        v.Margin = new Thickness(0, 2, 0, 0);
        return new Border
        {
            Background = Ui.Res("SurfaceBrush"), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 8, 0),
            Child = Ui.Stack(Ui.Sub(label), v),
        };
    }

    private static Border Panel(string head, string text, string bg, string headInk)
    {
        var h = Ui.Text(head, 13, FontWeights.SemiBold, Ui.Res(headInk));
        var t = Ui.Text(text, 13);
        t.Margin = new Thickness(0, 4, 0, 0);
        return new Border { Background = Ui.Res(bg), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 10, 0), Child = Ui.Stack(h, t) };
    }

    private static Border GoogleMark() => new()
    {
        Width = 22, Height = 22, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(2), BorderBrush = Ui.Res("AccentBrush"),
        Child = new TextBlock { Text = "G", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Ui.Res("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
    };

    private static Border Divider() => new() { Height = 1, Background = Ui.Res("BorderBrush"), Margin = new Thickness(0, 4, 0, 8) };

    private static FrameworkElement Space(double w) => new Border { Width = w };
}

/// <summary>Where the setup guide lives.</summary>
internal static class UpdateLinks
{
    public const string SignInSetup = "https://github.com/krishnabhunia/remindly-app/blob/main/docs/WINDOWS-SIGN-IN-SETUP.md";
}

/// <summary>"When something blocks sync" (approved design, screen 7). Each card has Copy details.</summary>
internal static class ProblemCards
{
    public static FrameworkElement? For(SyncService sync)
    {
        var e = sync.Engine;
        if (e == null || !AppState.Current.Settings.CloudSync) return null;
        return e.Problem switch
        {
            SyncProblem.PermissionDenied => Card("The cloud refused your data", SyncGlyphs.Error, "DangerBrush", "DangerSoftBrush",
                "Firestore said \"permission denied\". This happens when the Remindly security rules are not published in your Firebase project.",
                new[] { "Open the Firebase console → Firestore → Rules", "Paste the Remindly rules → Publish", "Come back and press Try again" },
                e.ProblemDetail,
                ("Try again", () => sync.SyncNow(), true),
                ("Copy the rules", () => CopyText(FirebaseConfig.Rules(), "Rules copied"), false),
                ("Open Firebase", () => { if (sync.Config != null) InstallInfo.OpenUrl(sync.Config.RulesConsoleUrl); }, false)),
            SyncProblem.NoPhoneData => Card("Nothing from your phone yet", SyncGlyphs.Phone, "AmberBrush", "AmberSoftBrush",
                $"Signed in as {sync.Auth.Session?.Email}, but this account has no data from a phone. On the phone, check:",
                new[] { "Settings → Google Account shows the same email", "Cloud sync (beta) is switched on", "The phone is online, with Remindly opened once" },
                e.ProblemDetail,
                ("Check again", () => sync.SyncNow(), true),
                ("Use another account", () => { sync.SignOut(removeData: false); SignInFlow.Start(); }, false)),
            SyncProblem.SignInExpired => SignInAgain(sync.Auth.LastEmail),
            SyncProblem.Other => Card("Sync stopped", SyncGlyphs.Warning, "AmberBrush", "AmberSoftBrush",
                "Remindly could not talk to the cloud. Your changes are kept on this PC and sent once this clears.",
                Array.Empty<string>(), e.ProblemDetail,
                ("Try again", () => sync.SyncNow(), true)),
            _ => null,
        };
    }

    public static FrameworkElement SignInAgain(string? email) => Card("Sign in again", SyncGlyphs.Lock, "AccentInkBrush", "AccentSoftBrush",
        "Google ended this PC's sign-in" + (email != null ? $" for {email}" : "") + " (password changed, or access removed in your Google account). Your changes on this PC are kept and will be sent after you sign in.",
        Array.Empty<string>(), App.Sync?.Engine?.ProblemDetail ?? App.Sync?.Auth.LastError,
        ("Sign in with Google", () => SignInFlow.Start(), true));

    private static FrameworkElement Card(string title, string glyph, string ink, string soft, string text, string[] steps, string? detail, params (string Label, Action Click, bool Primary)[] actions)
    {
        var p = new StackPanel();
        p.Children.Add(Ui.Row(Ui.Glyph(glyph, 18, Ui.Res(ink)), Ui.Text("  " + title, 15, FontWeights.SemiBold, wrap: false)));
        var t = Ui.Text(text, 13.5, color: Ui.Res("InkSubtleBrush"));
        t.Margin = new Thickness(0, 6, 0, 6);
        p.Children.Add(t);
        for (int i = 0; i < steps.Length; i++)
        {
            var st = Ui.Text($"{i + 1}. {steps[i]}", 13.5);
            st.Margin = new Thickness(4, 0, 0, 2);
            p.Children.Add(st);
        }
        var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var (label, click, primary) in actions)
        {
            var b = primary ? Ui.Primary(label, click) : Ui.Btn(label, click);
            b.Margin = new Thickness(0, 0, 8, 6);
            row.Children.Add(b);
        }
        var copy = Ui.Btn("Copy details", () => CopyText($"Remindly {Core.Updates.UpdateService.CurrentDisplayVersion} — {title}\n{detail ?? "(no error text)"}\nLog: {AppPaths.LogFile}", "Details copied"),
            "The exact error, for a bug report");
        copy.Margin = new Thickness(0, 0, 8, 6);
        row.Children.Add(copy);
        p.Children.Add(row);
        return new Border
        {
            Background = Ui.Res("CardBrush"), BorderBrush = Ui.Res(soft), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 12, 16, 8), Margin = new Thickness(0, 4, 0, 12), Child = p,
        };
    }

    private static void CopyText(string text, string snack)
    {
        try { Clipboard.SetText(text); App.Current.Main.Snack(snack); }
        catch (Exception ex) { Log.Warn("Clipboard: " + ex.Message); }
    }
}

/// <summary>Sign in → wait for the browser (screen 2) → first sync (screen 4).</summary>
internal static class SignInFlow
{
    private static bool _running;

    public static async void Start()
    {
        var sync = App.Sync;
        if (sync == null || _running) return;
        _running = true;
        try
        {
            var dlg = new SignInWindow(sync);
            var task = sync.SignInAsync();
            _ = task.ContinueWith(_ => dlg.Dispatcher.BeginInvoke(new Action(dlg.CloseQuietly)), TaskScheduler.Default);
            dlg.Open();
            var ok = await task;
            if (ok) FirstSyncWindow.OpenFor(sync);
            else if (!string.IsNullOrEmpty(sync.Auth.LastError)) Ui.Info("Sign-in didn't finish: " + sync.Auth.LastError);
        }
        catch (Exception ex)
        {
            Log.Error("SYNC sign-in flow", ex);
            Ui.Info("Sign-in didn't finish: " + ex.Message);
        }
        finally { _running = false; }
    }
}

/// <summary>Shared chrome for the sign-in dialogs: title, body, buttons on the right; Esc closes.</summary>
public abstract class SyncDialog : Window
{
    protected readonly StackPanel Body = new() { Margin = new Thickness(24, 20, 24, 8) };
    protected readonly StackPanel Buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 8, 20, 18) };

    protected SyncDialog(string title, double width)
    {
        Title = "Remindly · " + title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Theme.Dress(this);
        FontSize = 14;
        if (Application.Current.MainWindow is { IsVisible: true } owner && owner != this) Owner = owner;
        var root = new DockPanel();
        DockPanel.SetDock(Buttons, Dock.Bottom);
        root.Children.Add(Buttons);
        root.Children.Add(Body);
        Content = root;
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { Close(); e.Handled = true; } };
    }

    protected Button AddButton(string text, Action click, bool primary = false, string? style = null)
    {
        var b = primary ? Ui.Primary(text, click) : Ui.Btn(text, click, style: style ?? "");
        b.Margin = new Thickness(8, 0, 0, 0);
        b.MinHeight = 40;
        Buttons.Children.Add(b);
        return b;
    }

    public void Open()
    {
        if (App.IsSmokeTest) { Show(); return; }
        ShowDialog();
    }
}

/// <summary>Screen 2 — "Finish signing in in your browser".</summary>
public sealed class SignInWindow : SyncDialog
{
    private readonly SyncService? _sync;
    private bool _quiet;

    public SignInWindow(SyncService? sync) : base("Sign in", 540)
    {
        _sync = sync;
        Body.Children.Add(Ui.Row(Ui.Glyph(SyncGlyphs.Sync, 24, Ui.Res("AccentBrush")), Ui.Text("  Finish signing in in your browser", 19, FontWeights.SemiBold, wrap: false)));
        var steps = new StackPanel { Margin = new Thickness(0, 14, 0, 12) };
        foreach (var (n, line) in new[] { (1, "A Google page has opened in your browser."), (2, "Choose the Google account that is on your phone."), (3, "When the tab says \"You're signed in\", come back here. This window closes by itself.") })
        {
            var t = Ui.Text($"{n}. {line}", 14);
            t.Margin = new Thickness(0, 0, 0, 6);
            steps.Children.Add(t);
        }
        Body.Children.Add(steps);
        var note = new Border
        {
            Background = Ui.Res("SurfaceBrush"), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 9, 12, 9),
            Child = Ui.Text("Remindly only gets your name, email and a sign-in token. Your Google password stays with Google.", 13, color: Ui.Res("InkSubtleBrush")),
        };
        Body.Children.Add(note);
        var link = new TextBlock { Text = "Open the browser page again", TextDecorations = TextDecorations.Underline, FontSize = 14 };
        link.SetResourceReference(TextBlock.ForegroundProperty, "AccentInkBrush");
        var again = Ui.Plain(link, () => _sync?.ReopenBrowser(), "Opens the Google page again if you closed it");
        again.HorizontalAlignment = HorizontalAlignment.Left;
        again.MinHeight = 40;
        Buttons.HorizontalAlignment = HorizontalAlignment.Stretch;
        var spacerRow = Ui.Columns("*,Auto", again, Ui.Btn("Cancel", Close));
        spacerRow.Width = 492;
        Buttons.Children.Add(spacerRow);
        Closed += (_, _) => { if (!_quiet) _sync?.CancelSignIn(); };
        Loaded += (_, _) => { if (_quiet) Close(); };
    }

    /// <summary>Sign-in finished (either way): close without cancelling it — at once, or as soon as the window is up.</summary>
    public void CloseQuietly()
    {
        _quiet = true;
        if (IsLoaded) Close();
    }
}

/// <summary>Screen 4 — "Bringing this PC and your phone together".</summary>
public sealed class FirstSyncWindow : SyncDialog
{
    private readonly SyncService? _sync;
    private readonly Grid _table = new();
    private readonly ProgressBar _bar = new() { Height = 8, Maximum = 100, Margin = new Thickness(0, 12, 0, 12) };
    private readonly Button _done;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(600) };

    private static readonly (string Label, string[] Cols)[] Rows =
    {
        ("Tasks, Learn and Buy items", new[] { SyncContract.Items }),
        ("Calls", new[] { SyncContract.Calls }),
        ("Buy lists and preferences", new[] { SyncContract.Settings }),
        ("Shops, cities, chains", new[] { SyncContract.Shops, SyncContract.Cities, SyncContract.Chains }),
        ("Products", new[] { SyncContract.Products }),
    };

    public static void OpenFor(SyncService sync) => new FirstSyncWindow(sync).Open();

    public FirstSyncWindow(SyncService? sync) : base("First sync", 600)
    {
        _sync = sync;
        Body.Children.Add(Ui.Text("Bringing this PC and your phone together", 19, FontWeights.SemiBold));
        Body.Children.Add(Ui.Sub("Signed in as " + (sync?.Auth.Session?.Email ?? "your Google account")));
        Body.Children.Add(_bar);
        Body.Children.Add(_table);
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 12, 0, 0) };
        var strong = new System.Windows.Documents.Run("Nothing is deleted. ") { FontWeight = FontWeights.SemiBold };
        strong.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "SuccessBrush");
        note.Inlines.Add(strong);
        note.Inlines.Add("When the phone and this PC both have a record, the one changed last wins. A backup of this PC's data was saved first.");
        Body.Children.Add(new Border { Background = Ui.Res("SuccessSoftBrush"), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 12, 0, 0), Child = note });
        AddButton("Run in the background", Close);
        _done = AddButton("Done", Close, primary: true);
        _tick.Tick += (_, _) => Draw();
        Loaded += (_, _) => { if (_sync != null) _sync.Changed += Draw; _tick.Start(); };
        Closed += (_, _) => { if (_sync != null) _sync.Changed -= Draw; _tick.Stop(); };
        Draw();
    }

    private void Draw()
    {
        var e = _sync?.Engine;
        _table.Children.Clear();
        _table.RowDefinitions.Clear();
        _table.ColumnDefinitions.Clear();
        foreach (var w in new[] { "*", "110", "110", "100" }) _table.ColumnDefinitions.Add(new ColumnDefinition { Width = w == "*" ? new GridLength(1, GridUnitType.Star) : new GridLength(double.Parse(w, CultureInfo.InvariantCulture)) });
        void Cell(int r, int c, UIElement el) { Grid.SetRow(el, r); Grid.SetColumn(el, c); _table.Children.Add(el); }
        _table.RowDefinitions.Add(new RowDefinition());
        string[] heads = { "What", "From phone", "From this PC", "State" };
        for (int c = 0; c < heads.Length; c++)
        {
            var h = Ui.Text(heads[c], 12, FontWeights.SemiBold, Ui.Res("InkSubtleBrush"));
            if (c > 0) h.TextAlignment = TextAlignment.Right;
            h.Margin = new Thickness(0, 0, 0, 4);
            Cell(0, c, h);
        }
        int doneRows = 0;
        for (int i = 0; i < Rows.Length; i++)
        {
            var (label, cols) = Rows[i];
            _table.RowDefinitions.Add(new RowDefinition());
            int row = i + 1;
            int recv = 0, sent = 0;
            bool current = e != null;
            foreach (var c in cols)
            {
                if (e?.Progress.TryGetValue(c, out var p) == true) { recv += p.Received; sent += p.Sent; current &= p.Current; }
                else current = false;
            }
            bool done = e != null && (e.InitialSnapshotDone || e.State.FirstSyncDone) && current;
            if (done) doneRows++;
            var (stateText, ink) = done ? ("Done", "SuccessBrush") : recv > 0 || e?.Phase == SyncPhase.Connecting ? ("Working…", "AccentInkBrush") : ("Waiting", "InkSubtleBrush");
            var border = new Border { BorderBrush = Ui.Res("BorderBrush"), BorderThickness = new Thickness(0, 1, 0, 0) };
            Grid.SetColumnSpan(border, 4);
            Cell(row, 0, border);
            Cell(row, 0, Pad(Ui.Text(label, 14)));
            Cell(row, 1, Pad(Right(Ui.Text(recv.ToString(CultureInfo.InvariantCulture), 14))));
            Cell(row, 2, Pad(Right(Ui.Text(sent > 0 ? $"{sent} sent" : "—", 14))));
            Cell(row, 3, Pad(Right(Ui.Text(stateText, 14, FontWeights.SemiBold, Ui.Res(ink)))));
        }
        _bar.Value = Rows.Length == 0 ? 0 : 100.0 * doneRows / Rows.Length;
        var finished = e != null && e.InitialSnapshotDone && e.PendingCount == 0;
        _done.IsEnabled = finished || App.IsSmokeTest;
        if (e?.Problem is SyncProblem.PermissionDenied or SyncProblem.Other) _done.IsEnabled = true;
    }

    private static FrameworkElement Pad(FrameworkElement e) { e.Margin = new Thickness(0, 7, 0, 7); return e; }
    private static TextBlock Right(TextBlock t) { t.TextAlignment = TextAlignment.Right; return t; }
}

/// <summary>Screen 8 — "Sign out of Remindly on this PC?" Keep the data (default) or remove it.</summary>
public sealed class SignOutWindow : SyncDialog
{
    private readonly RadioButton _keep;
    private readonly RadioButton _remove;

    public static void OpenFor() => new SignOutWindow(App.Sync).Open();

    public SignOutWindow(SyncService? sync) : base("Sign out", 520)
    {
        Body.Children.Add(Ui.Text("Sign out of Remindly on this PC?", 19, FontWeights.SemiBold));
        var t = Ui.Sub("This PC stops syncing. Your phone and the cloud copy are not changed.");
        t.FontSize = 14;
        t.Margin = new Thickness(0, 6, 0, 12);
        Body.Children.Add(t);
        Body.Children.Add(Ui.Text("Your data on this PC", 13, FontWeights.SemiBold));
        _keep = Choice("Keep it (recommended)", "Reminders keep ringing here. Sign in again later and only the changes cross.", true);
        _remove = Choice("Remove it from this PC", "For a shared or borrowed PC. A backup file is saved first.", false);
        Body.Children.Add(_keep);
        Body.Children.Add(_remove);
        AddButton("Cancel", Close);
        AddButton("Sign out", () =>
        {
            var remove = _remove.IsChecked == true;
            Close();
            var backup = sync?.SignOut(remove);
            App.Current.Main.Snack(remove ? $"Signed out · this PC's data removed (backup in {Path.GetFileName(backup)})" : "Signed out · your data stays on this PC");
        }, style: "Danger");
    }

    private static RadioButton Choice(string head, string sub, bool on)
    {
        var r = new RadioButton { GroupName = "keep", IsChecked = on, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(8, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Top };
        r.Content = Ui.Stack(Ui.Text(head, 14, FontWeights.SemiBold), Ui.Sub(sub));
        return r;
    }
}
