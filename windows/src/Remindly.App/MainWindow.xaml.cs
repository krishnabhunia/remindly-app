using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
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

public partial class MainWindow : Window
{
    private readonly Dictionary<TabItem, FrameworkElement> _pages = new();
    private readonly DispatcherTimer _snackTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private Action? _undo;
    private bool _switchingMode;

    public MainWindow()
    {
        InitializeComponent();
        var v = UpdateService.CurrentDisplayVersion;
        Title = $"Remindly {v}";
        VersionText.Text = $"Version {v} · {InstallInfo.ModeLabel}";
        _snackTimer.Tick += (_, _) => HideSnack();
        AppState.Current.Changed += () => { (Page.Content as IPage)?.Refresh(); RefreshFooter(); };
        if (App.Updates != null) App.Updates.Changed += () => Dispatcher.BeginInvoke(RefreshUpdateBanner);
        Loaded += (_, _) => RefreshUpdateBanner();
        SetMode(AppState.Current.Settings.LastMode, initial: true);
        RefreshFooter();
    }

    private void RefreshFooter() => FooterText.Text = AppState.Current.Settings.CloseToTray ? "Reminders stay active while Remindly runs in the notification area" : "Closing the window exits Remindly and stops reminders";

    public BuyView Buy => (BuyView)PageFor(BuyTab);

    private FrameworkElement PageFor(TabItem tab)
    {
        if (_pages.TryGetValue(tab, out var p)) return p;
        FrameworkElement page = tab == OverviewTab ? new WorkspaceView(false)
            : tab == RemindersTab ? new WorkspaceView(true)
            : tab == TasksTab ? new TaskListsView()
            : tab == LearnTab ? new ItemsView(Tab.LEARN)
            : tab == CallsTab ? new CallsView()
            : tab == BuyTab ? new BuyView()
            : tab == ShopsTab ? new ShopsView()
            : tab == ProductsTab ? new ProductsView()
            : new SettingsView();
        _pages[tab] = page;
        return page;
    }

    // ───────────────────────── mode + navigation ─────────────────────────

    public void SetMode(string mode, bool initial = false)
    {
        bool shop = mode == "SHOP";
        _switchingMode = true;
        ModePicker.SelectedIndex = shop ? 1 : 0;
        _switchingMode = false;
        App.ApplyMode(shop ? "SHOP" : "TASK");
        foreach (var t in new[] { TasksTab, RemindersTab, LearnTab, CallsTab }) t.Visibility = shop ? Visibility.Collapsed : Visibility.Visible;
        foreach (var t in new[] { BuyTab, ShopsTab, ProductsTab }) t.Visibility = shop ? Visibility.Visible : Visibility.Collapsed;
        if (NavTabs.SelectedItem is not TabItem sel || sel.Visibility != Visibility.Visible || initial)
            NavTabs.SelectedItem = initial ? OverviewTab : shop ? BuyTab : TasksTab;
        if (!initial && AppState.Current.Settings.LastMode != (shop ? "SHOP" : "TASK"))
            AppState.Current.UpdateSettingsQuiet(s => s with { LastMode = shop ? "SHOP" : "TASK" });
        ShowSelected();
    }

    public void Go(TabItem tab)
    {
        bool shopTab = tab == BuyTab || tab == ShopsTab || tab == ProductsTab;
        bool taskTab = tab == RemindersTab || tab == TasksTab || tab == LearnTab || tab == CallsTab;
        if (shopTab && ModePicker.SelectedIndex == 0) SetMode("SHOP");
        if (taskTab && ModePicker.SelectedIndex == 1) SetMode("TASK");
        NavTabs.SelectedItem = tab;
        ShowSelected();
    }

    public TabItem TabFor(Tab t) => t switch { Tab.SHOP => BuyTab, Tab.LEARN => LearnTab, _ => TasksTab };

    public IEnumerable<TabItem> AllTabs => new[] { OverviewTab, TasksTab, RemindersTab, LearnTab, CallsTab, BuyTab, ShopsTab, ProductsTab, SettingsTab };

    private void ShowSelected()
    {
        if (Page == null || NavTabs?.SelectedItem is not TabItem t) return; // selection events arrive during InitializeComponent
        var page = PageFor(t);
        if (!ReferenceEquals(Page.Content, page)) Page.Content = page;
        (page as IPage)?.Refresh();
    }

    private void NavTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource == NavTabs) ShowSelected();
    }

    private void ModePicker_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_switchingMode && Page != null) SetMode(ModePicker.SelectedIndex == 1 ? "SHOP" : "TASK"); }

    // ───────────────────────── entry points (tray, alerts) ─────────────────────────

    public void OpenItem(Item item)
    {
        App.Current.ShowMain();
        if (item.Tab == Tab.SHOP && ShopLists.ListIdOf(item, AppState.Current.Settings.ShopLists) is long lid)
        {
            Go(BuyTab);
            Buy.OpenList(lid);
        }
        else
        {
            Go(TabFor(item.Tab));
            if (item.Tab == Tab.TASKS) ((TaskListsView)PageFor(TasksTab)).OpenList(TaskLists.IdOf(item, AppState.Current.Settings.TaskLists));
        }
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
        Snack("Choose a task list, then use + New to add your task.");
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

    // ───────────────────────── updates banner ─────────────────────────

    private void RefreshUpdateBanner()
    {
        var u = App.Updates;
        if (u?.Last is { Status: UpdateStatus.UpdateAvailable, Release: ReleaseInfo r })
        {
            UpdateBanner.Visibility = Visibility.Visible;
            UpdateBannerText.Text = u.Progress >= 0 ? $"Downloading Remindly {r.DisplayVersion}… {u.Progress}%" : $"Remindly {r.DisplayVersion} is available";
            UpdateBannerButton.Content = $"Update to v{r.DisplayVersion}";
            UpdateBannerText.Visibility = u.Progress >= 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateBannerButton.IsEnabled = !u.Busy;
        }
        else UpdateBanner.Visibility = Visibility.Collapsed;
    }

    private async void UpdateBannerButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.Updates != null) await App.Updates.DownloadAndInstallAsync(background: false);
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
