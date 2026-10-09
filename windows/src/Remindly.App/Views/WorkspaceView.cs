using System.Windows;
using System.Windows.Controls;
using Remindly.App.Services;
using Remindly.Core;
namespace Remindly.App.Views;
public sealed class WorkspaceView(bool remindersOnly) : ScrollViewer, IPage
{
    private string _filter = "Upcoming";
    public void Refresh()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var state = AppState.Current; var shop = state.Settings.LastMode == "SHOP" && !remindersOnly;
        var items = state.Data.Items.Where(i => i.DeletedAt == null && (shop ? i.Tab == Tab.SHOP : i.Tab != Tab.SHOP)).ToList();
        var pending = items.Where(i => !i.Done).ToList();
        var root = Ui.Stack(Ui.H1(remindersOnly ? "Reminders" : "Workspace overview"), Ui.Sub(DateTime.Now.ToString("dddd, dd MMMM yyyy") + " · Your tasks, reminders and progress"));
        var metrics = Ui.Columns("*,12,*,12,*", Metric("Active",pending.Count.ToString(),"Tasks waiting for you"),new Border(),Metric("Due / overdue",pending.Count(i => i.DueAt <= state.Now).ToString(),"Keep the next action visible"),new Border(),Metric("Completed",items.Count(i => i.Done).ToString(),"Finished items remain available"));
        metrics.Margin = new Thickness(0,20,0,16); root.Children.Add(metrics);
        if (!remindersOnly)
        {
            root.Children.Add(Ui.H2("Quick access")); var links = new WrapPanel();
            foreach (var t in App.Current.Main.AllTabs.Where(t => t.Visibility == Visibility.Visible && t.Header is string h && h != "Overview")) links.Children.Add(Ui.Btn((string)t.Header, () => App.Current.Main.Go(t)));
            root.Children.Add(links);
        }
        root.Children.Add(Ui.H2(remindersOnly ? "Reminder queue" : "Next on your schedule")); var filters = new WrapPanel();
        foreach (var f in new[] { "Upcoming", "Overdue", "Repeating", "Snoozed" }) filters.Children.Add(Ui.Chip(f,"queue",f == _filter, () => { _filter = f; Refresh(); }));
        root.Children.Add(filters);
        var queue = pending.Where(i => _filter switch { "Overdue" => i.DueAt < state.Now, "Repeating" => i.RepeatMode != "OFF", "Snoozed" => i.SnoozedUntil > state.Now, _ => i.DueAt != null }).OrderBy(i => i.SnoozedUntil ?? i.DueAt).Take(40).ToList();
        if (queue.Count == 0) root.Children.Add(Ui.EmptyState("No reminders in this view", "Add a due date or repeat from the item editor. Notification settings are available in Settings."));
        foreach (var i in queue) { root.Children.Add(Ui.Sub(TabNames.Title(i.Tab))); root.Children.Add(ItemsView.ItemCard(i,state.Now,true)); }
        if (!shop)
        {
            root.Children.Add(Ui.H2("Call backs")); var calls = state.LiveCalls().Where(c => !c.Done).OrderBy(c => c.RecurAt).Take(8).ToList();
            if (calls.Count == 0) root.Children.Add(Ui.Sub("No pending call backs. Use Calls to add one."));
            foreach (var c in calls) root.Children.Add(Ui.Card(Ui.Columns("*,Auto",Ui.Stack(Ui.Text(c.Display,15,FontWeights.SemiBold),Ui.Sub(c.Number + " · " + Ui.DueText(c.RecurAt,true,state.Now))),Ui.Btn("Edit",() => CallEditor.OpenFor(c)))));
        }
        Content = root;
    }
    private static Border Metric(string label,string value,string hint) => Ui.Card(Ui.Stack(Ui.Sub(label),Ui.Text(value,30,FontWeights.SemiBold),Ui.Sub(hint)));
}
