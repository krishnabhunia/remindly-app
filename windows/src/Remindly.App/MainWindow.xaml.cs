using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Remindly.App.Services;
using Remindly.App.Views;
using Remindly.Core;
using Remindly.Core.Updates;

namespace Remindly.App;

/// <summary>A screen that redraws itself from AppState.</summary>
public interface IPage
{
    void Refresh();
}

/// <summary>One entry of a design's menu.</summary>
public sealed record NavEntry(TabItem Tab, string Label, string Glyph, int? Count);

public partial class MainWindow : Window
{
    private readonly Dictionary<TabItem, FrameworkElement> _pages = new();
    private readonly DispatcherTimer _snackTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private Action? _undo;
    private bool _shop;
    private IShell? _shell;
    private Button? _updateButton;
    private Button? _accountButton;

    /// <summary>The page area; each design's shell places it.</summary>
    public ContentControl Page { get; } = new() { Focusable = false };

    /// <summary>Holds the top-right "Update to vx.y.z" button; collapsed unless GitHub offered a newer eligible version.</summary>
    public Border UpdateSlot { get; } = new() { VerticalAlignment = VerticalAlignment.Center };

    public MainWindow()
    {
        InitializeComponent();
        var v = UpdateService.CurrentDisplayVersion;
        Title = $"Remindly {v}";
        Theme.Dress(this, ground: false);
        SetResourceReference(BackgroundProperty, "SurfaceBrush");
        _snackTimer.Tick += (_, _) => HideSnack();
        AppState.Current.Changed += () =>
        {
            // A tab the phone just hid: move to the first one it shows, as the phone does.
            if (HiddenByPhone(SelectedTab)) { UpdateTabVisibility(); Go(HomeTab()); }
            (Page.Content as IPage)?.Refresh();
            _shell?.Sync();
        };
        // A countdown started or was cancelled: redraw so its card shows (or drops) the "Moving to Done" chip.
        DoneFlow.Changed += () => (Page.Content as IPage)?.Refresh();
        if (App.Updates != null) App.Updates.Changed += () => Dispatcher.BeginInvoke(RefreshUpdateBanner);
        if (App.Cloud != null) App.Cloud.Changed += () => Dispatcher.BeginInvoke(RefreshAccountButton);
        Loaded += (_, _) => RefreshUpdateBanner();
        PreviewKeyDown += OnShortcut;
        _shop = AppState.Current.Settings.LastMode == "SHOP";
        BuildShell();
        SetMode(AppState.Current.Settings.LastMode, initial: true);
    }

    public string VersionLabel => $"Version {UpdateService.CurrentDisplayVersion} · {InstallInfo.ModeLabel}";

    /// <summary>A test build (x.y.z-betaNNN): every design shows a small Beta chip next to the version.</summary>
    public bool IsBetaBuild => Remindly.Core.Info.BuildInfo.IsBeta(UpdateService.CurrentDisplayVersion);

    /// <summary>ⓘ next to the version, F1 and Settings → About open the Info window.</summary>
    public InfoWindow OpenInfo(string tab = InfoWindow.BuildTab) => InfoWindow.Open(tab);

    public BuyView Buy => (BuyView)PageFor(BuyTab);

    public bool IsShop => _shop;

    public string Design => Theme.Design;

    public TabItem? SelectedTab => NavTabs.SelectedItem as TabItem;

    private bool HubDesign => Theme.Design == Designs.Hub;

    private FrameworkElement PageFor(TabItem tab)
    {
        if (_pages.TryGetValue(tab, out var p)) return p;
        FrameworkElement page = tab == TodayTab ? new TodayHubView()
            : tab == TasksTab ? new ItemsView(Tab.TASKS)
            : tab == LearnTab ? new ItemsView(Tab.LEARN)
            : tab == CallsTab ? new CallsView()
            : tab == OverviewTab ? new ShopHubView()
            : tab == BuyTab ? new BuyView()
            : tab == ShopsTab ? new ShopsView()
            : tab == ProductsTab ? new ProductsView()
            : new SettingsView();
        _pages[tab] = page;
        return page;
    }

    // ───────────────────────── design (Settings → Appearance) ─────────────────────────

    /// <summary>Settings → Appearance: remembers the design on this PC and switches to it.</summary>
    public void ChooseDesign(string design)
    {
        var code = Designs.Normalize(design);
        if (AppState.Current.Settings.Design != code) AppState.Current.UpdateSettingsQuiet(x => x with { Design = code });
        ApplyDesign(code);
    }

    /// <summary>Switches the whole window to another design; the current mode and screen are kept.</summary>
    public void ApplyDesign(string design)
    {
        var keep = SelectedTab;
        Theme.Apply(design, _shop ? "SHOP" : "TASK");
        _pages.Clear();
        Page.Content = null;
        BuildShell();
        UpdateTabVisibility();
        var target = keep != null && keep.Visibility == Visibility.Visible ? keep : HomeTab();
        if (!ReferenceEquals(NavTabs.SelectedItem, target)) NavTabs.SelectedItem = target;
        ShowSelected();
        RefreshUpdateBanner();
    }

    private void BuildShell()
    {
        Detach(Page);
        Detach(UpdateSlot);
        _updateButton = BuildUpdateButton();
        _accountButton = Ui.Btn("Sign in with Google", () =>
        {
            Go(SettingsTab);
            if (PageFor(SettingsTab) is SettingsView settings) settings.OpenCloudWizard();
        });
        _accountButton.Margin = new Thickness(0, 0, 10, 0);
        UpdateSlot.Child = Ui.Row(_accountButton, _updateButton);
        _shell = Shells.Create(Theme.Design, this);
        ShellHost.Content = _shell.Root;
        _shell.Sync();
        RefreshUpdateBanner();
        RefreshAccountButton();
    }

    private static void Detach(FrameworkElement e)
    {
        switch (e.Parent)
        {
            case Panel p: p.Children.Remove(e); break;
            case Decorator d: d.Child = null; break;
            case ContentControl c: c.Content = null; break;
        }
    }

    private Button BuildUpdateButton()
    {
        var b = new Button { Margin = new Thickness(0), Padding = new Thickness(14, 7, 14, 7), FontWeight = FontWeights.SemiBold };
        switch (Theme.Design)
        {
            case Designs.Board:
                b.Background = Ui.Res("AmberSoftBrush");
                b.BorderBrush = Ui.Res("AmberSoftBrush");
                b.Foreground = Ui.Res("AmberBrush");
                break;
            case Designs.Hub:
                b.Style = (Style)FindResource("Primary");
                b.Margin = new Thickness(0);
                b.Padding = new Thickness(16, 9, 16, 9);
                break;
            default:
                b.Background = Brushes.Transparent;
                b.BorderBrush = Ui.Res("AccentBrush");
                b.Foreground = Ui.Res("AccentBrush");
                break;
        }
        b.Click += UpdateBannerButton_Click;
        return b;
    }

    // ───────────────────────── mode + navigation ─────────────────────────

    public void SetMode(string mode, bool initial = false)
    {
        bool shop = mode == "SHOP";
        bool changed = shop != _shop;
        _shop = shop;
        App.ApplyMode(shop ? "SHOP" : "TASK");
        if (changed) BuildShell();
        UpdateTabVisibility();
        if (NavTabs.SelectedItem is not TabItem sel || sel.Visibility != Visibility.Visible || initial || changed)
            NavTabs.SelectedItem = HomeTab();
        if (!initial && AppState.Current.Settings.LastMode != (shop ? "SHOP" : "TASK"))
            AppState.Current.UpdateSettingsQuiet(s => s with { LastMode = shop ? "SHOP" : "TASK" });
        ShowSelected();
    }

    public void ToggleMode() => SetMode(_shop ? "TASK" : "SHOP");

    private TabItem HomeTab() => _shop ? (HubDesign ? OverviewTab : BuyTab) : (HubDesign ? TodayTab : FirstTaskTab());

    /// <summary>The first task-mode tab the phone shows (Android firstVisibleTab), else Settings.</summary>
    private TabItem FirstTaskTab()
    {
        var phone = AppState.Current.Phone;
        return phone.ShowTasks ? TasksTab : phone.ShowLearn ? LearnTab : phone.ShowCalls ? CallsTab : SettingsTab;
    }

    private bool HiddenByPhone(TabItem? tab)
    {
        var phone = AppState.Current.Phone;
        return tab == TasksTab && !phone.ShowTasks || tab == LearnTab && !phone.ShowLearn || tab == CallsTab && !phone.ShowCalls;
    }

    private void UpdateTabVisibility()
    {
        TodayTab.Visibility = !_shop && HubDesign ? Visibility.Visible : Visibility.Collapsed;
        OverviewTab.Visibility = _shop && HubDesign ? Visibility.Visible : Visibility.Collapsed;
        foreach (var t in new[] { TasksTab, LearnTab, CallsTab }) t.Visibility = _shop || HiddenByPhone(t) ? Visibility.Collapsed : Visibility.Visible;
        foreach (var t in new[] { BuyTab, ShopsTab, ProductsTab }) t.Visibility = _shop ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Go(TabItem tab)
    {
        if (tab == TodayTab && !HubDesign) tab = TasksTab;
        if (HiddenByPhone(tab)) tab = FirstTaskTab();   // never open a tab the phone hides (quick views, shortcuts, alerts)
        if (tab == OverviewTab && !HubDesign) tab = BuyTab;
        bool shopTab = tab == BuyTab || tab == ShopsTab || tab == ProductsTab || tab == OverviewTab;
        bool taskTab = tab == TasksTab || tab == LearnTab || tab == CallsTab || tab == TodayTab;
        if (shopTab && !_shop) SetMode("SHOP");
        if (taskTab && _shop) SetMode("TASK");
        NavTabs.SelectedItem = tab;
        ShowSelected();
    }

    public TabItem TabFor(Tab t) => t switch { Tab.SHOP => BuyTab, Tab.LEARN => LearnTab, _ => TasksTab };

    /// <summary>Every screen of the current design (the smoke test walks these).</summary>
    public IEnumerable<TabItem> AllTabs => HubDesign
        ? new[] { TodayTab, TasksTab, LearnTab, CallsTab, OverviewTab, BuyTab, ShopsTab, ProductsTab, SettingsTab }
        : new[] { TasksTab, LearnTab, CallsTab, BuyTab, ShopsTab, ProductsTab, SettingsTab };

    /// <summary>The menu for the current mode, with live counts.</summary>
    public List<NavEntry> NavEntries()
    {
        var s = AppState.Current;
        var phone = s.Phone;
        // The phone's Count badges switch (approved S9): no counts when it is off.
        int? Open(Tab t) => phone.Badges ? s.LiveItems(t).Count(i => !i.Done) : null;
        var list = new List<NavEntry>();
        if (_shop)
        {
            if (HubDesign) list.Add(new NavEntry(OverviewTab, "Overview", Glyphs.Home, null));
            list.Add(new NavEntry(BuyTab, "Buy", Glyphs.Cart, Open(Tab.SHOP)));
            list.Add(new NavEntry(ShopsTab, "Shops", Glyphs.Shop, phone.Badges ? s.Shops.Count : null));
            list.Add(new NavEntry(ProductsTab, "Products", Glyphs.Package, phone.Badges ? s.Products.Count : null));
        }
        else
        {
            if (HubDesign) list.Add(new NavEntry(TodayTab, "Today", Glyphs.Home, null));
            // The phone's Visible tabs (approved S8): a tab hidden on the phone is hidden here too.
            if (phone.ShowTasks) list.Add(new NavEntry(TasksTab, "Tasks", Glyphs.Tasks, Open(Tab.TASKS)));
            if (phone.ShowLearn) list.Add(new NavEntry(LearnTab, "Learn", Glyphs.Learn, Open(Tab.LEARN)));
            if (phone.ShowCalls) list.Add(new NavEntry(CallsTab, "Calls", Glyphs.Phone, phone.Badges ? s.LiveCalls().Count(c => !c.Done) : null));
        }
        return list;
    }

    private void ShowSelected()
    {
        if (ShellHost == null || NavTabs?.SelectedItem is not TabItem t) return; // selection events arrive during InitializeComponent
        var page = PageFor(t);
        if (!ReferenceEquals(Page.Content, page)) Page.Content = page;
        (page as IPage)?.Refresh();
        _shell?.Sync();
    }

    private void NavTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource == NavTabs) ShowSelected();
    }

    /// <summary>F1 Info · Ctrl+K command bar (Command Dark) · Ctrl+1…4 menu entries (then Settings) · Ctrl+M Task / Shop · Ctrl+N new task / Learn item.</summary>
    private void OnShortcut(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F1 && Keyboard.Modifiers == ModifierKeys.None) { OpenInfo(); e.Handled = true; return; }
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.K && _shell?.FocusCommand() == true) { e.Handled = true; return; }
        if (e.Key == Key.M) { ToggleMode(); e.Handled = true; return; }
        if (e.Key == Key.N && (SelectedTab == TasksTab || SelectedTab == LearnTab || SelectedTab == TodayTab))
        {
            ItemEditor.New(SelectedTab == LearnTab ? Tab.LEARN : Tab.TASKS);
            e.Handled = true;
            return;
        }
        int n = e.Key switch { Key.D1 => 0, Key.D2 => 1, Key.D3 => 2, Key.D4 => 3, _ => -1 };
        var entries = NavEntries();
        if (n >= 0 && n < entries.Count) { Go(entries[n].Tab); e.Handled = true; }
        else if (n == entries.Count) { Go(SettingsTab); e.Handled = true; } // three sections: Ctrl+4 is Settings
    }

    // ───────────────────────── entry points (tray, alerts) ─────────────────────────

    public void OpenItem(Item item)
    {
        App.Current.ShowMain();
        if (item.Tab == Tab.SHOP && ShopLists.ListIdOf(item, AppState.Current.Settings.ShopLists) is long lid)
        {
            Go(BuyTab);
            Buy.OpenList(lid);
        }
        else Go(TabFor(item.Tab));
        ItemEditor.Edit(item);
    }

    public void OpenList(long listId)
    {
        App.Current.ShowMain();
        Go(BuyTab);
        Buy.OpenList(listId);
    }

    public void NewItemFromTray()
    {
        Go(TasksTab);
        ItemEditor.New(Tab.TASKS);
    }

    // ───────────────────────── snackbar ─────────────────────────

    public void Snack(string message, Action? undo = null)
    {
        SnackText.Text = message;
        _undo = undo;
        SnackUndo.Visibility = undo == null ? Visibility.Collapsed : Visibility.Visible;
        SnackBar.Visibility = Visibility.Visible;
        _snackTimer.Stop();
        _snackTimer.Start();
    }

    private void HideSnack()
    {
        _snackTimer.Stop();
        SnackBar.Visibility = Visibility.Collapsed;
        _undo = null;
    }

    private void SnackUndo_Click(object sender, RoutedEventArgs e)
    {
        var u = _undo;
        HideSnack();
        u?.Invoke();
    }

    // ───────────────────────── updates button (top right) ─────────────────────────

    private void RefreshUpdateBanner()
    {
        var u = App.Updates;
        if (_updateButton != null && u?.Last is { Status: UpdateStatus.UpdateAvailable, Release: ReleaseInfo r } &&
            UpdateLogic.IsNewer(UpdateService.CurrentDisplayVersion, r))
        {
            _updateButton.Visibility = Visibility.Visible;
            // The label stays "Update to vx.y.z" (AGENTS.md); download progress goes in the tooltip.
            _updateButton.Content = Ui.Row(Ui.Glyph(Glyphs.Download, 14, _updateButton.Foreground),
                Ui.Text($"  Update to v{r.DisplayVersion}", 13.5, FontWeights.SemiBold, _updateButton.Foreground, wrap: false));
            _updateButton.ToolTip = u.Progress >= 0 ? $"Downloading Remindly {r.DisplayVersion}… {u.Progress}%" : $"Remindly {r.DisplayVersion} is available";
            _updateButton.IsEnabled = !u.Busy;
        }
        else if (_updateButton != null) _updateButton.Visibility = Visibility.Collapsed;
    }

    private void RefreshAccountButton()
    {
        if (_accountButton == null) return;
        var cloud = App.Cloud;
        // A drawn cloud and a coloured dot replace the ☁ emoji, and the state is plain words, never "UpToDate".
        var cloudIcon = Ui.Glyph(Glyphs.Cloud, 15, Ui.Res("InkBrush"));
        cloudIcon.Margin = new Thickness(0, 0, 8, 0);
        string name;
        if (cloud?.SignedIn == true)
        {
            var status = cloud.Sync?.Status;
            var label = SyncStatusText.Label(status);
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 8, Height = 8, Margin = new Thickness(10, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                Fill = SyncStatusText.Tone(status) switch
                {
                    SyncTone.Good => Ui.Res("SuccessBrush"),
                    SyncTone.Busy => Ui.Res("BlueBrush"),
                    SyncTone.Problem => Ui.Res("DangerBrush"),
                    _ => Ui.Res("InkSubtleBrush"),
                },
            };
            _accountButton.Content = Ui.Row(cloudIcon, Ui.Text(cloud.Email ?? "Connected", 13.5, wrap: false), dot,
                Ui.Text(label, 13.5, wrap: false));
            name = $"Account {cloud.Email ?? "connected"}, sync {label}";
        }
        else
        {
            _accountButton.Content = Ui.Row(cloudIcon, Ui.Text("Sign in with Google", 13.5, wrap: false));
            name = "Sign in with Google";
        }
        System.Windows.Automation.AutomationProperties.SetAutomationId(_accountButton, "account-sync");
        System.Windows.Automation.AutomationProperties.SetName(_accountButton, name);
        _accountButton.ToolTip = cloud?.ConfigurationError ?? "Account & Sync";
    }

    private async void UpdateBannerButton_Click(object sender, RoutedEventArgs e)
    {
        // A notification opens the exact-version chooser. It must never install
        // whichever release happens to be latest without an explicit selection.
        Go(SettingsTab);
        if (PageFor(SettingsTab) is SettingsView settings)
            await settings.OpenVersionPickerAsync();
    }

    // ───────────────────────── close → notification area ─────────────────────────

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.Exiting && !App.IsSmokeTest && AppState.Current.Settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            App.Tray?.HintStillRunning();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!App.Exiting && !App.IsSmokeTest) App.Current.ExitApp("main window closed");
    }
}
