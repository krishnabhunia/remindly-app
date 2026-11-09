using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;
using Remindly.App.Services;
using Remindly.Core;
using Remindly.Core.Info;
using Remindly.Core.Updates;

namespace Remindly.App.Views;

/// <summary>
/// ⓘ Info (IN1): This build · What's implemented · What's new · Under the hood, and "Copy build info".
/// Opened from the ⓘ next to the version in every design, F1 in the main window and Settings → About.
/// The text and tables come from Remindly.Core.Info (unit-tested); this class only lays them out.
/// </summary>
public sealed class InfoWindow : Window
{
    public const string BuildTab = "build";
    public const string FeaturesTab = "features";
    public const string NewTab = "new";
    public const string HoodTab = "hood";

    /// <summary>The four tabs in order (the smoke test opens each one in every design).</summary>
    public static readonly IReadOnlyList<(string Code, string Label)> Tabs = new[]
    {
        (BuildTab, "This build"), (FeaturesTab, "What's implemented"), (NewTab, "What's new"), (HoodTab, "Under the hood"),
    };

    private static InfoWindow? _open;

    private readonly string _version = UpdateService.CurrentDisplayVersion;
    private readonly ContentControl _page = new() { Focusable = false };
    private readonly Dictionary<string, RadioButton> _tabButtons = new();
    private readonly List<FeatureRow> _features = InfoResources.Features();
    private string _query = "";
    private bool _onlyNew;
    private ContentControl? _featureTable;

    /// <summary>Opens the window (or brings the open one forward) on <paramref name="tab"/>.</summary>
    public static InfoWindow Open(string tab = BuildTab)
    {
        if (_open is { IsVisible: true } existing)
        {
            existing.Select(tab);
            existing.Activate();
            return existing;
        }
        var w = new InfoWindow(tab);
        _open = w;
        w.Closed += (_, _) => { if (ReferenceEquals(_open, w)) _open = null; };
        w.Show();
        return w;
    }

    private InfoWindow(string tab)
    {
        Title = $"{BuildInfo.AppName} · Info";
        Width = 780;
        Height = 640;
        MinWidth = 560;
        MinHeight = 420;
        MaxHeight = SystemParameters.WorkArea.Height - 20;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Theme.Dress(this);
        FontSize = 14;
        if (Application.Current.MainWindow is { IsVisible: true } owner && owner != this) Owner = owner;

        var root = new DockPanel();

        var title = Ui.Text(Title, 18, FontWeights.SemiBold, Ui.Res("AccentInkBrush"), wrap: false);
        var subtitle = Ui.Sub(BuildInfo.Subtitle(_version));
        var head = new Border { Background = Ui.Res("AccentSoftBrush"), Padding = new Thickness(20, 12, 20, 12), Child = Ui.Stack(title, subtitle) };
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var strip = new WrapPanel { Margin = new Thickness(20, 12, 20, 4) };
        foreach (var (code, label) in Tabs)
        {
            var c = code;
            var chip = Ui.Chip(label, "info-tabs", false, () => Show(c));
            AutomationProperties.SetName(chip, label);
            chip.Margin = new Thickness(0, 0, 8, 6);
            _tabButtons[code] = chip;
            strip.Children.Add(chip);
        }
        DockPanel.SetDock(strip, Dock.Top);
        root.Children.Add(strip);

        var copy = Ui.Btn("Copy build info", CopyBuildInfo, "Copies two lines about this copy for a bug report");
        var close = Ui.Primary("Close", Close);
        close.Margin = new Thickness(0);
        var buttons = Ui.Row(copy, close);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Margin = new Thickness(20, 8, 14, 14);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var scroll = Ui.Scroll(_page);
        scroll.Margin = new Thickness(20, 6, 12, 0);
        root.Children.Add(scroll);
        Content = root;

        KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) { Close(); e.Handled = true; }
        };
        Select(tab);
    }

    /// <summary>The tab on show (smoke test and tests).</summary>
    public string CurrentTab { get; private set; } = BuildTab;

    public void Select(string tab)
    {
        if (!_tabButtons.ContainsKey(tab)) tab = BuildTab;
        if (_tabButtons[tab].IsChecked == true) Show(tab); // Checked does not fire again
        else _tabButtons[tab].IsChecked = true;
    }

    private void Show(string tab)
    {
        CurrentTab = tab;
        _page.Content = tab switch
        {
            FeaturesTab => FeaturesPage(),
            NewTab => WhatsNewPage(),
            HoodTab => HoodPage(),
            _ => BuildPage(),
        };
    }

    // ───────────────────────── 1 · This build ─────────────────────────

    private FrameworkElement BuildPage()
    {
        var page = new StackPanel();
        if (BuildInfo.BetaBanner(_version) is string banner)
        {
            page.Children.Add(new Border
            {
                Background = Ui.Res("AmberSoftBrush"),
                CornerRadius = Ui.ControlRadius,
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 0, 12),
                Child = Ui.Text(banner, 14, FontWeights.SemiBold, Ui.Res("AmberBrush")),
            });
        }
        var rows = BuildInfo.ThisBuild(_version, BuildMetadata.Current, InstallInfo.InstalledAs);
        page.Children.Add(Table(new[] { "SrNo.", "Item", "This copy" }, "Auto,Auto,*",
            rows.Select((r, i) => new[] { Cell((i + 1).ToString()), Cell(r.Item, bold: true), Cell(r.Value, r.Link) })));
        return page;
    }

    // ───────────────────────── 2 · What's implemented ─────────────────────────

    private FrameworkElement FeaturesPage()
    {
        var core = BuildInfo.CoreVersion(_version);
        var page = new StackPanel();
        var search = Ui.Input(_query, "Search what Remindly can do");
        AutomationProperties.SetName(search, "Search features");
        search.Margin = new Thickness(0, 0, 0, 8);
        search.TextChanged += (_, _) => { _query = search.Text; RedrawFeatures(); };
        page.Children.Add(search);
        var chips = Ui.Row(
            Ui.Chip("All areas", "info-feature-filter", !_onlyNew, () => { _onlyNew = false; RedrawFeatures(); }),
            Ui.Chip($"New in {core}", "info-feature-filter", _onlyNew, () => { _onlyNew = true; RedrawFeatures(); }));
        chips.Margin = new Thickness(0, 0, 0, 10);
        page.Children.Add(chips);
        _featureTable = new ContentControl { Focusable = false };
        page.Children.Add(_featureTable);
        RedrawFeatures();
        return page;
    }

    private void RedrawFeatures()
    {
        if (_featureTable == null) return;
        var rows = FeaturesDoc.Filter(_features, _query, _onlyNew ? BuildInfo.CoreVersion(_version) : null);
        if (_features.Count == 0) { _featureTable.Content = Ui.Sub("The feature list is not built into this copy."); return; }
        if (rows.Count == 0) { _featureTable.Content = Ui.EmptyState("Nothing matches", "Try another word, or choose All areas."); return; }
        _featureTable.Content = Table(new[] { "SrNo.", "Area", "What it does", "Since" }, "Auto,Auto,*,Auto",
            rows.Select(r => new[] { Cell(r.SrNo.ToString()), Cell(r.Area, bold: true), Cell(r.WhatItDoes), Cell(r.Since, mono: true) }));
    }

    // ───────────────────────── 3 · What's new ─────────────────────────

    private FrameworkElement WhatsNewPage()
    {
        var page = new StackPanel();
        var core = BuildInfo.CoreVersion(_version);
        var section = InfoResources.WhatsNew(_version);
        var heading = Ui.Row(Ui.Text(section?.Title ?? $"{BuildInfo.AppName} {core}", 17, FontWeights.SemiBold));
        if (BuildInfo.IsBeta(_version))
        {
            var label = BuildInfo.BetaNumberText(_version) is string n ? $"beta {n} · being tested" : "beta · being tested";
            var pill = Ui.Pill(label, Ui.Res("AmberBrush"), Ui.Res("AmberSoftBrush"), 12, FontWeights.Bold);
            pill.Margin = new Thickness(10, 0, 0, 0);
            heading.Children.Add(pill);
        }
        heading.Margin = new Thickness(0, 0, 0, 8);
        page.Children.Add(heading);
        if (section == null || section.Body.Length == 0)
            page.Children.Add(Ui.Text($"The notes for {core} are not built into this copy. Every version's notes are on GitHub Releases.", 14, color: Ui.Res("InkSubtleBrush")));
        else
            foreach (var block in MarkdownLite.Blocks(section.Body)) page.Children.Add(Block(block));
        var all = LinkText("All releases on GitHub", UpdateLogic.ReleasesUrl);
        all.Margin = new Thickness(0, 14, 0, 6);
        page.Children.Add(all);
        return page;
    }

    private static FrameworkElement Block(MdBlock block)
    {
        var t = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new Thickness(0, 2, 0, 4) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        foreach (var run in MarkdownLite.Runs(block.Text))
        {
            var r = new Run(run.Text);
            if (run.Bold) r.FontWeight = FontWeights.SemiBold;
            if (run.Code) r.SetResourceReference(TextElement.FontFamilyProperty, "MonoFont");
            t.Inlines.Add(r);
        }
        switch (block.Kind)
        {
            case MdKind.Heading:
                t.FontSize = 15;
                t.FontWeight = FontWeights.SemiBold;
                t.Margin = new Thickness(0, 10, 0, 4);
                return t;
            case MdKind.Bullet:
            case MdKind.SubBullet:
                var dot = Ui.Text(block.Kind == MdKind.Bullet ? "•" : "◦", 14, color: Ui.Res("AccentBrush"));
                dot.VerticalAlignment = VerticalAlignment.Top;
                dot.Margin = new Thickness(0, 2, 8, 0);
                var g = Ui.Columns("Auto,*", dot, t);
                g.Margin = new Thickness(block.Kind == MdKind.Bullet ? 4 : 24, 0, 0, 0);
                return g;
            default:
                return t;
        }
    }

    // ───────────────────────── 4 · Under the hood ─────────────────────────

    private FrameworkElement HoodPage()
    {
        var s = AppState.Current.Settings;
        var design = Designs.Info(Theme.Design);
        var rows = new List<(string Part, string Uses)>
        {
            ("App", $"C# on .NET {Environment.Version.ToString(3)} with WPF, screens built in code; Windows Forms only for the notification-area icon"),
            ("Runs on", WindowsName()),
            ("Your data", $"{AppPaths.DataFile} · saved automatically, a daily copy kept for 7 days"),
            ("Sync", SyncText()),
            ("Updates", $"GitHub Releases ({UpdateLogic.RepoOwner}/{UpdateLogic.RepoName}) · automatic checks {(s.UpdateAutoCheck ? "on" : "off")} · test versions (beta) {(s.UpdateBeta ? "on" : "off")}"),
            ("Design", $"{design.Letter} · {design.Name} ({(Theme.P.Dark ? "dark" : "light")}), chosen in Settings → Appearance"),
            ("Fonts", $"This design uses {FontName(Theme.P.Font)}. Bundled: Manrope, IBM Plex Sans / Mono, Plus Jakarta Sans (SIL Open Font License 1.1)"),
            ("Installed as", $"{InstallInfo.InstalledAs} · {InstallInfo.ExePath}"),
        };
        var page = new StackPanel();
        page.Children.Add(Table(new[] { "SrNo.", "Part", "This copy uses" }, "Auto,Auto,*",
            rows.Select((r, i) => new[] { Cell((i + 1).ToString()), Cell(r.Part, bold: true), Cell(r.Uses) })));
        var actions = Ui.Row(Ui.Btn("Open data folder", () => InstallInfo.OpenUrl(AppPaths.DataRoot)), Ui.Btn("Open log file", () => InstallInfo.OpenUrl(AppPaths.LogFile)));
        actions.Margin = new Thickness(0, 12, 0, 6);
        page.Children.Add(actions);
        return page;
    }

    private static string SyncText()
    {
        var cloud = App.Cloud;
        if (cloud == null) return "Google sign-in and Firestore sync with the Android app · not running in this session";
        if (cloud.ConfigurationError != null) return "Google sign-in is not set up in this build · data stays on this PC";
        return cloud.SignedIn
            ? $"Google account {cloud.Email ?? "(signed in)"} · Firestore live sync with the Android app · {cloud.Sync?.Status}"
            : "Google sign-in and Firestore live sync with the Android app · not signed in";
    }

    private static string FontName(string family)
    {
        var first = family.Split(',')[0].Trim();
        var hash = first.IndexOf('#');
        return hash >= 0 ? first[(hash + 1)..] : first;
    }

    // ───────────────────────── Copy build info ─────────────────────────

    public static string BuildInfoText() => BuildInfo.CopyText(UpdateService.CurrentDisplayVersion, BuildMetadata.Current,
        WindowsName(), InstallInfo.InstalledAs, BuildInfo.DotNetName(Environment.Version));

    private void CopyBuildInfo()
    {
        try
        {
            Clipboard.SetText(BuildInfoText());
            App.Current.Main.Snack("Build info copied");
        }
        catch (Exception ex) { Log.Warn("Copy build info failed: " + ex.Message); }
    }

    private static string WindowsName()
    {
        string? product = null, display = null;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            product = k?.GetValue("ProductName") as string;
            display = k?.GetValue("DisplayVersion") as string ?? k?.GetValue("ReleaseId") as string;
        }
        catch { /* ignore */ }
        return BuildInfo.WindowsName(product, display, Environment.OSVersion.Version.Build, RuntimeInformation.OSArchitecture.ToString());
    }

    // ───────────────────────── table helpers ─────────────────────────

    private static TextBlock Cell(string text, string? link = null, bool bold = false, bool mono = false)
    {
        if (link != null) return LinkText(text, link);
        var t = Ui.Text(text, 13.5, bold ? FontWeights.SemiBold : FontWeights.Normal);
        t.VerticalAlignment = VerticalAlignment.Top;
        if (mono) t.FontFamily = (FontFamily)Application.Current.Resources["MonoFont"];
        return t;
    }

    private static TextBlock LinkText(string text, string url)
    {
        var link = new Hyperlink(new Run(text)) { NavigateUri = new Uri(url), ToolTip = url };
        link.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
        link.RequestNavigate += (_, e) => { InstallInfo.OpenUrl(e.Uri.AbsoluteUri); e.Handled = true; };
        var t = new TextBlock { FontSize = 13.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top };
        t.Inlines.Add(link);
        return t;
    }

    /// <summary>A bordered table: a header row in the subtle ink, then one row per entry with a divider between rows.</summary>
    private static Border Table(string[] headers, string columns, IEnumerable<TextBlock[]> rows)
    {
        var grid = Ui.Columns(columns);
        void AddRow(int index, IReadOnlyList<TextBlock> cells, bool header)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var line = new Border
            {
                BorderBrush = Ui.Res("BorderBrush"),
                BorderThickness = new Thickness(0, index == 0 ? 0 : 1, 0, 0),
                Background = header ? Ui.Res("SurfaceBrush") : Brushes.Transparent,
            };
            Grid.SetRow(line, index);
            Grid.SetColumnSpan(line, grid.ColumnDefinitions.Count);
            grid.Children.Add(line);
            for (int c = 0; c < cells.Count; c++)
            {
                var cell = cells[c];
                cell.Margin = new Thickness(12, 8, 12, 8);
                Grid.SetRow(cell, index);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }
        AddRow(0, headers.Select(h => Ui.Text(h, 12.5, FontWeights.SemiBold, Ui.Res("InkSubtleBrush"), wrap: false)).ToArray(), header: true);
        int i = 1;
        foreach (var r in rows) AddRow(i++, r, header: false);
        return new Border
        {
            BorderBrush = Ui.Res("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = Ui.ControlRadius,
            Background = Ui.Res("CardBrush"),
            Child = grid,
            ClipToBounds = true,
        };
    }
}
