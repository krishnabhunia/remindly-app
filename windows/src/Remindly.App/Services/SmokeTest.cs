using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Remindly.App.Views;
using Remindly.Core;

namespace Remindly.App.Services;

/// <summary>Existing isolated Windows UI smoke test, including the approved R1 interaction checks.</summary>
public static class SmokeTest
{
    private static string _dir = "";
    private static readonly List<string> Report = new();
    private static int _failures;
    public static void Fail(Exception ex) { _failures++; Report.Add("FAIL " + ex); }
    public static void Run(MainWindow main, string dir)
    {
        _dir = Path.GetFullPath(dir); Directory.CreateDirectory(_dir);
        main.Dispatcher.BeginInvoke(async () =>
        {
            try { await RunAsync(main); }
            catch (Exception ex) { Fail(ex); }
            Finish();
        });
    }
    private static async Task RunAsync(MainWindow main)
    {
        var state = AppState.Current;
        Seed(state);
        main.WindowStartupLocation = WindowStartupLocation.Manual;
        main.Left = 0; main.Top = 0; main.Width = 1200; main.Height = 820;
        main.Show(); await Idle();
        var groceries = state.Lists.First(l => l.Name == "Groceries");
        Check(state.Settings.Design == Designs.Fluent, "design A is the default");
        foreach (var design in Designs.All)
        {
            main.ChooseDesign(design.Code); await Idle();
            Check(main.Design == design.Code && state.Settings.Design == design.Code, $"design {design.Letter} applied and remembered");
            var prefix = $"design-{design.Letter.ToLowerInvariant()}-";
            foreach (var tab in main.AllTabs)
            {
                main.Go(tab); await Idle();
                Check(ReferenceEquals(main.SelectedTab, tab), $"{design.Name}: {tab.Header} opens");
                Capture(main, prefix + "tab-" + ((string)tab.Header).ToLowerInvariant());
            }
            Check(Named(main, "Info") is System.Windows.Controls.Button, $"{design.Name}: Info button next to version");
            Check(main.IsBetaBuild == (Named(main, "Beta build") != null), $"{design.Name}: Beta chip only on a test build");
            foreach (var (code, _) in InfoWindow.Tabs)
                await ShowAndCapture(() => Check(main.OpenInfo(code).CurrentTab == code, $"{design.Name}: Info → {code} opens"), prefix + "info-" + code);
            main.Go(main.BuyTab); main.Buy.OpenList(groceries.Id); await Idle();
            Capture(main, prefix + "buy-list-groceries"); main.Buy.BackToLists();
            if (design.Code == Designs.Command)
            {
                foreach (var view in ItemsView.QuickViews)
                {
                    ItemsView.CommandView = view.Code; main.Go(main.TasksTab); await Idle();
                    Capture(main, prefix + "view-" + view.Code.ToLowerInvariant());
                }
                ItemsView.CommandView = ItemsView.AllView; main.Go(main.TasksTab);
                // Quick add must use an explicit open task list, not silently create an Unsorted task.
                var projects = TaskLists.Live(state.Settings.TaskLists).First(l => l.Name == "Projects");
                ((ItemsView)main.Page.Content).OpenTaskList(projects.Id);
                Shells.CommandAdd(main, "Added from the command bar");
                Check(state.LiveItems(Tab.TASKS).Any(i => i.Title == "Added from the command bar" && i.ListId == projects.Id), "command bar adds to the open task list");
            }
        }
        main.ChooseDesign(Designs.Fluent); await Idle();
        Check(main.Design == Designs.Fluent, "back to design A");

        main.Activate();
        var infoBefore = Application.Current.Windows.OfType<InfoWindow>().Count();
        main.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(main), 0, System.Windows.Input.Key.F1)
            { RoutedEvent = UIElement.PreviewKeyDownEvent });
        await Idle();
        var opened = Application.Current.Windows.OfType<InfoWindow>().ToList();
        Check(opened.Count == infoBefore + 1, "F1 opens the Info window");
        foreach (var window in opened) window.Close();
        var buildInfo = InfoWindow.BuildInfoText();
        Check(buildInfo.StartsWith("Remindly ") && buildInfo.Split('\n').Length == 2, "copy build info: " + buildInfo.Replace(Environment.NewLine, " / "));

        main.Go(main.TabFor(Tab.SHOP)); main.Buy.OpenList(groceries.Id); await Idle();
        Capture(main, "buy-list-groceries"); state.UpdateSettings(s => s with { ListInnerGroup = "SHOP" }); await Idle();
        Capture(main, "buy-list-by-shop"); main.Buy.OpenList(ShopLists.BuyNowListId); await Idle();
        Capture(main, "buy-now"); main.Buy.BackToLists();
        var milk = state.ItemsIn(groceries.Id).First(i => i.Title == "Milk");
        Check(state.Complete(milk).StartsWith("Bought"), "complete a Buy item");
        var text = ShareWindow.TextFor(groceries.Id).Text;
        Check(text.StartsWith("Groceries:-\n\n1. "), "share text format: " + text.Replace("\n", "\\n"));
        var snapshot = state.TakeSnapshot();
        state.DeleteList(state.Lists.First(l => l.Name == "Party"), ListDeleteMode.KEEP_UNSORTED, null);
        Check(state.ItemsIn(ShopLists.UnsortedListId).Any(i => i.Title == "Balloons"), "delete list keeps items in Unsorted");
        state.Restore(snapshot); Check(state.Lists.Any(l => l.Name == "Party"), "undo delete list");
        Check(state.RestartList(groceries.Id) >= 1, "restart list");
        var gym = state.LiveItems(Tab.TASKS).First(i => i.RepeatMode == "DAILY");
        Check(state.Complete(gym).StartsWith("Done · returns"), "complete a recurring task");

        await ShowAndCapture(() => ItemEditor.New(Tab.TASKS), "editor-task-new");
        await ShowAndCapture(() => ItemEditor.Edit(state.LiveItems(Tab.SHOP).First()), "editor-buy-item");
        await ShowAndCapture(() => ItemEditor.Edit(state.LiveItems(Tab.LEARN).First()), "editor-learn-item");
        await ShowAndCapture(() => CallEditor.OpenFor(state.LiveCalls().First()), "editor-call");
        await ShowAndCapture(() => ListEditor.OpenFor(groceries), "editor-list");
        await ShowAndCapture(() => ShareWindow.OpenFor(groceries.Id), "share-preview");
        await ShowAndCapture(() => DeleteListWindow.OpenFor(groceries, () => { }), "delete-list");
        await ShowAndCapture(() => ShopEditor.OpenFor(state.Shops.First()), "editor-shop");
        await ShowAndCapture(() => ProductEditor.OpenFor(state.Products.First()), "editor-product");
        var alert = new AlertWindow(new DueAlert(AlertKind.ITEM, gym.Id, state.Now, "Gym", "Tasks · leg day", "smoke", Ring: true), 0);
        alert.Show(); await Idle(); Capture(alert, "alert-card"); alert.Close();
        Check(Reminders.Due(state.Data, state.Now, new HashSet<string>()).Count >= 1, "due alerts computed");
        Check(File.Exists(AppPaths.DataFile), "data saved");

        await TaskWorkspaceSmoke.Run(main, Check, Capture);
    }

    private static void Seed(AppState state)
    {
        long now = state.Now;
        var groceries = state.CreateList(new ShopList { Name = "Groceries", Icon = "🥦", Pinned = true, ShoppingDay = now });
        var party = state.CreateList(new ShopList { Name = "Party", Icon = "🎉" });
        state.CreateList(new ShopList { Name = "Monthly stock", Icon = "📦", Personal = true });
        var projects = new TaskList { Id = Ids.Next(), Name = "Projects", CreatedAt = now, UpdatedAt = now };
        state.UpdateSettings(s => TaskLists.Mirror(s with { TaskLists = s.TaskLists.Append(projects).ToList() }));
        var dmart = new Shop { Id = Ids.Next(), Name = "D-Mart", CityId = state.CityNamed("Kolkata").Id, ChainId = state.ChainNamed("D-Mart").Id, IsDefault = true };
        state.UpsertShop(dmart); state.UpsertShop(new Shop { Id = Ids.Next(), Name = "Local kirana", CityId = state.CityNamed("Kolkata").Id });
        var milkProduct = new Product { Id = Ids.Next(), Name = "Milk", Category = "Dairy", DefaultUnit = "L" };
        state.UpsertProduct(milkProduct); state.UpsertProduct(new Product { Id = Ids.Next(), Name = "Basmati rice", Category = "Grains", DefaultUnit = "kg" });
        Item Buy(string title, ShopList list, string? qty = null, string? unit = null, string? price = null, Priority? priority = null, string? shop = null) =>
            state.NewItem(Tab.SHOP, title) with { ListId = list.Id, Group = list.Name, Quantity = qty, Unit = unit, Price = price, Priority = priority ?? Priority.MEDIUM, ShopName = shop, Personal = list.Personal };
        state.UpsertMany(new[]
        {
            Buy("Milk", groceries, "2", "L", "60", Priority.URGENT, "D-Mart") with { ProductId = milkProduct.Id },
            Buy("Basmati rice", groceries, "5", "kg", "450", shop: "D-Mart") with
            {
                PriceHistory = new()
                {
                    new PricePoint { At = now - 20 * 86_400_000L, Shop = "Local kirana", Qty = 5, Unit = "kg", UnitPrice = 96, Price = 480, Paid = 480 },
                    new PricePoint { At = now - 5 * 86_400_000L, Shop = "D-Mart", Qty = 5, Unit = "kg", UnitPrice = 90, Price = 450, Paid = 450 },
                },
            },
            Buy("Eggs", groceries, "12", shop: "Local kirana"), Buy("Balloons", party, "30"),
            Buy("Cake", party, price: "900", priority: Priority.HIGH), state.NewItem(Tab.SHOP, "Batteries"),
        });
        Item TaskItem(string title) => state.NewItem(Tab.TASKS, title) with { ListId = projects.Id, Group = projects.Name };
        state.UpsertMany(new[]
        {
            TaskItem("Pay electricity bill") with { DueAt = now - 3_600_000L, Priority = Priority.URGENT, Notes = "CESC portal" },
            TaskItem("Gym") with { DueAt = now + 3_600_000L, RepeatMode = "DAILY", AlertType = "R" },
            TaskItem("Call the plumber") with { DueAt = now + 26 * 3_600_000L }, TaskItem("Plan the weekend trip"),
            state.NewItem(Tab.LEARN, "LangGraph agents course") with { Platform = "Udemy", Progress = 40, HoursSpent = 6.5, Url = "https://www.udemy.com", RepeatMode = "SPACED", DueAt = now + 86_400_000L },
            state.NewItem(Tab.LEARN, "Neo4j Cypher tuning") with { Topic = "Graphs" },
        });
        state.UpsertCall(new CallReminder { Id = Ids.Next(), FirstName = "Asha", LastName = "Rao", Name = "Asha Rao", Number = "+91 98300 12345", Source = CallSource.MANUAL, CreatedAt = now, RecurAt = now - 60_000L, Message = "Calling you back shortly" });
        state.UpsertCall(new CallReminder { Id = Ids.Next(), Name = "Bank", Number = "1800 123 456", Source = CallSource.MANUAL, CreatedAt = now, RecurAt = now + 86_400_000L, RepeatMode = "WEEKLY", RepeatDays = new() { 1 } });
    }

    private static async Task ShowAndCapture(Action open, string name)
    {
        var before = Application.Current.Windows.Cast<Window>().ToHashSet();
        open(); await Idle();
        var window = Application.Current.Windows.Cast<Window>().FirstOrDefault(w => !before.Contains(w));
        if (window == null) { Report.Add($"FAIL {name}: no window opened"); _failures++; return; }
        Capture(window, name); window.Close(); await Idle();
    }
    private static DependencyObject? Named(DependencyObject root, string name)
    {
        if (System.Windows.Automation.AutomationProperties.GetName(root) == name) return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (Named(VisualTreeHelper.GetChild(root, i), name) is DependencyObject found) return found;
        return null;
    }
    private static async Task Idle() { await Task.Delay(250); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    private static void Check(bool ok, string what) { Report.Add((ok ? "ok   " : "FAIL ") + what); if (!ok) _failures++; }
    private static void Capture(Window window, string name)
    {
        try
        {
            if (window.Content is not FrameworkElement root) { Report.Add($"FAIL {name}: no content"); _failures++; return; }
            root.UpdateLayout();
            int width = (int)Math.Ceiling(root.ActualWidth), height = (int)Math.Ceiling(root.ActualHeight);
            if (width <= 0 || height <= 0) { Report.Add($"FAIL {name}: empty layout"); _failures++; return; }
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
            {
                context.DrawRectangle(window.Background is SolidColorBrush brush && brush.Color.A > 0 ? brush : Brushes.White, null, new Rect(0, 0, width, height));
                context.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, width, height));
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(_dir, name + ".png")); encoder.Save(stream);
            Report.Add($"ok   screenshot {name} ({width}x{height})");
        }
        catch (Exception ex) { Fail(ex); }
    }
    private static void Finish()
    {
        Report.Insert(0, $"Remindly {Core.Updates.UpdateService.CurrentDisplayVersion} smoke test — {(_failures == 0 ? "PASSED" : _failures + " FAILURE(S)")}");
        File.WriteAllLines(Path.Combine(_dir, "smoke-result.txt"), Report);
        Application.Current.Shutdown(_failures == 0 ? 0 : 1);
    }
}
