using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Remindly.App.Services;
using Remindly.Core;

namespace Remindly.App.Views;

public sealed class ItemEditor : EditorWindow
{
    private static readonly string[] Priorities = { "None", "Low", "Medium", "High", "Urgent" };
    private static readonly string[] AlertTypes = { "Notify", "Ring", "Muted" };
    private static readonly (string Code, string Label)[] RepeatModes =
    {
        ("OFF", "Does not repeat"), ("DAILY", "Daily"), ("WEEKLY", "Weekly on…"), ("MONTHLY_DAY", "Monthly on day(s)…"),
        ("MONTHLY_ORD", "Monthly on the…"), ("QUARTERLY", "Quarterly"), ("HALFYEARLY", "Half-yearly"), ("YEARLY", "Yearly"),
        ("EVERY_N", "Every N days / weeks / months"), ("SPACED", "Spaced revision (3 · 7 · 14 · 30 days)"),
    };
    private static readonly string[] Units = { "", "kg", "g", "L", "ml", "pcs", "dozen", "pack", "strip", "box", "bottle" };
    private static readonly string[] Platforms = { "Online", "Offline", "Internet", "LinkedIn Learning", "Udemy" };
    private static readonly string[] WeekDays = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
    private static readonly string[] Ords = { "First", "Second", "Third", "Fourth", "Last" };
    private readonly Item _orig;
    private readonly bool _isNew, _chooseDestination;
    private readonly TextBox _title = new();
    private readonly TextBox _notes = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60, MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top };
    private readonly ComboBox _priority;
    private readonly CheckBox _hasDue = new() { Content = "Due date" };
    private readonly DatePicker _date = new() { Width = 150 };
    private readonly CheckBox _hasTime = new() { Content = "at", Margin = new Thickness(12, 0, 6, 0) };
    private readonly TextBox _time = new() { Width = 70 };
    private readonly ComboBox _alert, _repeat;
    private readonly StackPanel _repeatExtra = new();
    private readonly CheckBox[] _weekDays = WeekDays.Select(d => new CheckBox { Content = d, Margin = new Thickness(0, 0, 10, 0) }).ToArray();
    private readonly TextBox _monthDays = new();
    private readonly ComboBox _ord = Combo(Ords, "First"), _ordDow = Combo(WeekDays, "Mon");
    private readonly TextBox _everyN = new() { Width = 60 };
    private readonly ComboBox _everyUnit = Combo(new[] { "days", "weeks", "months" }, "days");
    private readonly TextBox _repeatCount = new() { Width = 60 };
    private readonly TextBlock _preview = Ui.Sub("");
    private readonly CheckBox _personal = new() { Content = "Personal (hidden in reminders)" };
    private ComboBox? _taskList, _taskStatus, _list, _unit, _shop, _product, _lapseUnit, _platform;
    private TextBox? _qty, _price, _lapse, _url, _topic, _hours;
    private CheckBox? _staple;
    private Slider? _progress;

    public static void New(Tab tab, long? listId = null, string title = "", long? dueAt = null)
    {
        var state = AppState.Current;
        var item = state.NewItem(tab, title);
        if (dueAt is long due) item = item with { DueAt = due, DueHasTime = true };
        else item = state.WithNewDue(item, editor: true);
        if (tab == Tab.TASKS && listId is > 0)
        {
            var list = TaskLists.Live(state.Settings.TaskLists).FirstOrDefault(l => l.Id == listId);
            if (list == null) { Ui.Info("This task list is no longer available. Choose another list."); return; }
            item = item with { ListId = list.Id, Group = list.Name };
        }
        if (tab == Tab.SHOP)
        {
            var list = listId is long l && l >= 0 ? state.List(l) : (listId == null && state.Settings.ShopDefaultListId is long d ? state.List(d) : null);
            item = item with { ListId = list?.Id, Group = list?.Name, Personal = list?.Personal == true };
            if (list?.UsualShopId is long sid && state.Shops.FirstOrDefault(s => s.Id == sid) is Shop shop)
                item = item with { ShopId = shop.Id, ShopName = shop.Name };
        }
        new ItemEditor(item, isNew: true, chooseDestination: tab == Tab.TASKS && listId == null).Open();
    }
    public static void Edit(Item item) => new ItemEditor(item, isNew: false).Open();

    private ItemEditor(Item item, bool isNew, bool chooseDestination = false)
        : base((isNew ? "New " : "Edit ") + (item.Tab switch { Tab.SHOP => "Buy item", Tab.LEARN => "Learn item", _ => "task" }), 560)
    {
        _orig = item; _isNew = isNew; _chooseDestination = chooseDestination;
        var state = AppState.Current;
        EnableUnsavedGuard();
        if (item.Tab == Tab.TASKS) Form.Children.Add(Ui.Sub("Tasks / " + (_chooseDestination ? "Choose a list" : item.Group ?? "Unsorted")));
        WorkspaceUi.Identify(_title, "task-editor-title", item.Tab == Tab.TASKS ? "Task name" : "Item name");
        _title.Text = item.Title; Field("Title", _title); _title.TextChanged += (_, _) => CheckDuplicate();
        if (item.Tab == Tab.TASKS) BuildTaskList(item, state);
        if (item.Tab == Tab.SHOP) BuildBuy(item, state);
        if (item.Tab == Tab.LEARN) BuildLearn(item);
        Field("Notes", _notes); _notes.Text = item.Notes;
        _priority = Combo(Priorities, PriorityNames.Label(item.Priority));
        _alert = Combo(AlertTypes, item.AlertType == "OFF" ? "Muted" : item.AlertType.Contains('R') || item.AlertType.Contains('A') ? "Ring" : "Notify");
        Field("Priority", _priority);
        Section("When");
        var local = item.DueAt is long due ? Clock.ToLocal(due) : DateTime.Today.AddMinutes(state.Phone.DueMinuteOfDay);
        _hasDue.IsChecked = item.DueAt != null; _date.SelectedDate = local.Date;
        _hasTime.IsChecked = item.DueAt == null || item.DueHasTime;
        _time.Text = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        Form.Children.Add(Ui.Row(_hasDue, new Border { Width = 10 }, _date, _hasTime, _time));
        foreach (var control in new Control[] { _date, _time }) control.IsEnabled = _hasDue.IsChecked == true;
        _hasDue.Click += (_, _) => { _date.IsEnabled = _time.IsEnabled = _hasDue.IsChecked == true; UpdatePreview(); };
        _hasTime.Click += (_, _) => UpdatePreview();
        _date.SelectedDateChanged += (_, _) => UpdatePreview(); _time.TextChanged += (_, _) => UpdatePreview();
        _repeat = new ComboBox();
        foreach (var (_, label) in RepeatModes) _repeat.Items.Add(label);
        _repeat.SelectedIndex = Math.Max(0, Array.FindIndex(RepeatModes, mode => mode.Code == item.RepeatMode));
        _repeat.SelectionChanged += (_, _) => { BuildRepeatExtra(); UpdatePreview(); };
        foreach (var day in item.RepeatDays.Where(d => d is >= 1 and <= 7)) _weekDays[day - 1].IsChecked = true;
        _monthDays.Text = string.Join(",", item.RepeatDays.Where(d => d is >= 1 and <= 31));
        var pattern = item.RepeatOrdList.Count > 0 ? item.RepeatOrdList[0] : item.RepeatOrd * 10 + item.RepeatDow;
        _ord.SelectedIndex = Math.Clamp(pattern / 10 - 1, 0, 4); _ordDow.SelectedIndex = Math.Clamp(pattern % 10 - 1, 0, 6);
        _everyN.Text = item.RepeatN.ToString(CultureInfo.InvariantCulture);
        _everyUnit.SelectedIndex = item.RepeatUnit switch { "W" => 1, "M" => 2, _ => 0 };
        _repeatCount.Text = item.RepeatCount?.ToString(CultureInfo.InvariantCulture) ?? "";
        foreach (var checkbox in _weekDays) checkbox.Click += (_, _) => UpdatePreview();
        foreach (var text in new[] { _monthDays, _everyN }) text.TextChanged += (_, _) => UpdatePreview();
        _ord.SelectionChanged += (_, _) => UpdatePreview(); _ordDow.SelectionChanged += (_, _) => UpdatePreview();
        _everyUnit.SelectionChanged += (_, _) => UpdatePreview();
        BuildRepeatExtra();
        _personal.IsChecked = item.Personal; _personal.Margin = new Thickness(0, 12, 0, 0);
        var advanced = Ui.Stack(Labeled("Reminder", _alert), Labeled("Repeat", _repeat), _repeatExtra, _preview, _personal);
        Form.Children.Add(new Expander { Header = "Reminders and repeat", Content = advanced, IsExpanded = item.RepeatMode != "OFF", Margin = new Thickness(0, 14, 0, 0) });
        var extra = new List<Button>();
        if (!isNew)
        {
            extra.Add(Ui.Btn("Delete…", () =>
            {
                var current = state.Item(_orig.Id);
                if (current == null || current.DeletedAt != null) { ShowWarning("This task was already removed."); return; }
                if (!Ui.Confirm($"Move \"{current.Title}\" to the Bin? Unsaved edits will be discarded.")) return;
                state.SoftDelete(new[] { current });
                App.Current.Main.Snack("Moved to the Bin", () => state.Upsert(current));
                AcceptAndClose();
            }, style: "Danger"));
        }
        AddButtons(isNew ? (item.Tab == Tab.TASKS ? "Add task" : "Add") : "Save changes", extra.ToArray());
        Loaded += (_, _) => { _title.Focus(); _title.SelectAll(); UpdatePreview(); CheckDuplicate(); };
    }

    private sealed record TaskChoice(long? Id, string Label, bool Placeholder = false);
    private void BuildTaskList(Item item, AppState state)
    {
        _taskList = WorkspaceUi.Identify(new ComboBox { DisplayMemberPath = "Label" }, "task-editor-list", "Task list");
        if (_chooseDestination) _taskList.Items.Add(new TaskChoice(null, "Choose list…", true));
        _taskList.Items.Add(new TaskChoice(null, "Unsorted"));
        foreach (var list in TaskLists.Live(state.Settings.TaskLists).OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
            _taskList.Items.Add(new TaskChoice(list.Id, (list.Icon == null ? "" : list.Icon + " ") + list.Name));
        long? selected = item.ListId ?? TaskLists.ListIdOf(item, state.Settings.TaskLists);
        if (selected != null && !_taskList.Items.Cast<TaskChoice>().Any(c => c.Id == selected))
            _taskList.Items.Add(new TaskChoice(selected, "List metadata not available — keep existing membership"));
        _taskList.SelectedItem = _chooseDestination ? _taskList.Items[0] : _taskList.Items.Cast<TaskChoice>().First(c => c.Id == selected);
        _taskStatus = WorkspaceUi.Identify(Combo(new[] { "Active", "Done" }, item.Done ? "Done" : "Active"), "task-editor-status", "Task status");
        Form.Children.Add(TwoColumns(Labeled("Task list", _taskList), Labeled("Status", _taskStatus)));
    }

    private void BuildBuy(Item item, AppState state)
    {
        _list = new ComboBox { DisplayMemberPath = "Label" }; _list.Items.Add(new ListChoice(null, "Unsorted"));
        foreach (var list in state.Lists.OrderBy(l => l.Order).ThenBy(l => l.Name))
            _list.Items.Add(new ListChoice(list, (list.Icon != null ? list.Icon + " " : "") + list.Name));
        var current = ShopLists.ListIdOf(item, state.Settings.ShopLists) ?? item.ListId;
        _list.SelectedItem = _list.Items.Cast<ListChoice>().FirstOrDefault(c => c.List?.Id == current) ?? _list.Items[0];
        _list.SelectionChanged += (_, _) => CheckDuplicate(); Field("List", _list);
        _product = Combo(state.Products.Select(p => p.Name), state.Products.FirstOrDefault(p => p.Id == item.ProductId)?.Name ?? "", editable: true);
        _product.SelectionChanged += (_, _) =>
        {
            if (_product.SelectedItem is string name && state.Products.FirstOrDefault(p => p.Name == name) is Product product)
            {
                if (string.IsNullOrWhiteSpace(_title.Text)) _title.Text = product.Name;
                if (_unit != null && string.IsNullOrWhiteSpace(_unit.Text) && product.DefaultUnit != null) _unit.Text = product.DefaultUnit;
            }
        };
        _qty = new TextBox { Text = item.Quantity ?? "" }; _unit = Combo(Units, item.Unit ?? "", editable: true);
        Form.Children.Add(TwoColumns(Labeled("Quantity", _qty), Labeled("Unit", _unit)));
        _price = new TextBox { Text = item.Price ?? "" }; _shop = Combo(state.Shops.Select(s => s.Name), item.ShopName ?? "", editable: true);
        Form.Children.Add(TwoColumns(Labeled("Price (₹)", _price), Labeled("Shop", _shop))); Field("Product (from your catalogue)", _product);
        _lapse = new TextBox { Text = item.LapseValue?.ToString(CultureInfo.InvariantCulture) ?? "", Width = 60 };
        _lapseUnit = Combo(new[] { "days", "months" }, item.LapseUnit == LapseUnit.MONTHS ? "months" : "days");
        Field("After it is bought, put it back on the list after", Ui.Row(_lapse, new Border { Width = 8 }, _lapseUnit));
        _staple = new CheckBox { Content = "Staple (bought regularly)", IsChecked = item.Staple, Margin = new Thickness(0, 8, 0, 0) };
        Form.Children.Add(_staple);
    }
    private void BuildLearn(Item item)
    {
        _platform = Combo(Platforms, item.Platform ?? "", editable: true); _topic = new TextBox { Text = item.Topic ?? "" };
        Form.Children.Add(TwoColumns(Labeled("Platform", _platform), Labeled("Topic", _topic)));
        _url = new TextBox { Text = item.Url ?? "" }; Field("Link", _url);
        _progress = new Slider { Minimum = 0, Maximum = 100, Value = item.Progress, TickFrequency = 5, IsSnapToTickEnabled = true, Width = 220 };
        var percent = Ui.Sub($"{item.Progress}%"); _progress.ValueChanged += (_, _) => percent.Text = $"{(int)_progress.Value}%";
        _hours = new TextBox { Text = item.HoursSpent > 0 ? item.HoursSpent.ToString("0.#", CultureInfo.InvariantCulture) : "", Width = 70 };
        Form.Children.Add(TwoColumns(Labeled("Progress", Ui.Row(_progress, new Border { Width = 8 }, percent)), Labeled("Hours spent", _hours)));
    }
    private sealed record ListChoice(ShopList? List, string Label);
    private string RepeatCode => RepeatModes[Math.Max(0, _repeat.SelectedIndex)].Code;
    private void BuildRepeatExtra()
    {
        _repeatExtra.Children.Clear();
        switch (RepeatCode)
        {
            case "WEEKLY":
                var weekly = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                foreach (var checkbox in _weekDays) { (checkbox.Parent as Panel)?.Children.Remove(checkbox); weekly.Children.Add(checkbox); }
                _repeatExtra.Children.Add(weekly); break;
            case "MONTHLY_DAY":
                (_monthDays.Parent as Panel)?.Children.Remove(_monthDays);
                _repeatExtra.Children.Add(Labeled("Day(s) of the month, e.g. 1,15 (empty = the due date's day)", _monthDays)); break;
            case "MONTHLY_ORD":
                foreach (var control in new UIElement[] { _ord, _ordDow }) ((control as FrameworkElement)?.Parent as Panel)?.Children.Remove(control);
                _repeatExtra.Children.Add(Labeled("On the", Ui.Row(_ord, new Border { Width = 8 }, _ordDow))); break;
            case "EVERY_N":
                foreach (var control in new UIElement[] { _everyN, _everyUnit }) ((control as FrameworkElement)?.Parent as Panel)?.Children.Remove(control);
                _repeatExtra.Children.Add(Labeled("Every", Ui.Row(_everyN, new Border { Width = 8 }, _everyUnit))); break;
        }
        if (RepeatCode != "OFF")
        {
            (_repeatCount.Parent as Panel)?.Children.Remove(_repeatCount);
            _repeatExtra.Children.Add(Labeled("End after this many times (empty = never ends)", _repeatCount));
        }
    }
    private Item? ScheduleDraft(Item baseItem, out string? error)
    {
        error = null;
        long? dueAt = null; bool hasTime = _hasTime.IsChecked == true; string mode = RepeatCode;
        if (_hasDue.IsChecked == true || mode != "OFF")
        {
            var date = _date.SelectedDate ?? DateTime.Today;
            var time = TimeSpan.FromMinutes(AppState.Current.Phone.DueMinuteOfDay);
            if (hasTime && (!TimeSpan.TryParseExact(_time.Text.Trim(), new[] { @"h\:mm", @"hh\:mm", @"h\.mm", "hhmm" }, CultureInfo.InvariantCulture, out time) || time.TotalHours >= 24))
            { error = "Time must look like 09:30 (24-hour)."; return null; }
            dueAt = Clock.FromLocal(date.Date + time);
        }
        var days = mode switch
        {
            "WEEKLY" => _weekDays.Select((c, i) => (c, i)).Where(x => x.c.IsChecked == true).Select(x => x.i + 1).ToList(),
            "MONTHLY_DAY" => _monthDays.Text.Split(',', ' ', ';').Select(s => int.TryParse(s.Trim(), out var d) ? d : 0).Where(d => d is >= 1 and <= 31).Distinct().OrderBy(d => d).ToList(),
            _ => new List<int>(),
        };
        if (mode == "WEEKLY" && days.Count == 0 && dueAt is long da) days.Add(Recurrence.IsoDow(Clock.ToLocal(da)));
        int n = int.TryParse(_everyN.Text.Trim(), out var number) && number > 0 ? Math.Min(number, 999) : 1;
        var pattern = (Math.Max(0, _ord.SelectedIndex) + 1) * 10 + Math.Max(0, _ordDow.SelectedIndex) + 1;
        return baseItem with
        {
            DueAt = dueAt, DueHasTime = dueAt == null || hasTime, RepeatMode = mode, RepeatDays = days, RepeatN = n,
            RepeatUnit = _everyUnit.SelectedIndex switch { 1 => "W", 2 => "M", _ => "D" },
            RepeatOrdList = mode == "MONTHLY_ORD" ? new List<int> { pattern } : new List<int>(),
            RepeatOrd = pattern / 10, RepeatDow = pattern % 10,
            RepeatCount = Recurrence.SanitizeCount(int.TryParse(_repeatCount.Text.Trim(), out var count) ? count : null),
        };
    }
    private void UpdatePreview()
    {
        if (!IsLoaded) return;
        var draft = ScheduleDraft(_orig, out var error);
        if (error != null) { _preview.Text = error; return; }
        if (draft == null || draft.RepeatMode == "OFF") { _preview.Text = draft?.DueAt is long due ? "Due " + Clock.FormatDayTime(due) : "No due date — it stays on the list until you tick it."; return; }
        var next = Recurrence.Preview(draft, AppState.Current.Now);
        _preview.Text = next.Count == 0 ? "" : "Next: " + string.Join("  ·  ", next.Select(Clock.FormatDayTime));
    }
    private void CheckDuplicate()
    {
        var state = AppState.Current; var title = _title.Text.Trim();
        if (title.Length == 0) { ShowWarning(null); return; }
        if (_orig.Tab == Tab.SHOP && _list != null)
        {
            var current = (_list.SelectedItem as ListChoice)?.List?.Id;
            var other = ShopLists.OtherListHolding(state.Data.Items.Where(i => i.Id != _orig.Id), title, current, state.Settings.ShopLists);
            ShowWarning(other != null ? $"\"{title}\" is already on your {other.Name} list." : null); return;
        }
        ShowWarning(ItemRules.DupActiveMatch(state.Data.Items, _orig.Tab, title, _orig.Id) ? $"\"{title}\" is already on your {TabNames.Title(_orig.Tab)} list." : null);
    }

    protected override bool Save()
    {
        var state = AppState.Current; var title = _title.Text.Trim();
        if (title.Length == 0) { ShowWarning("Give it a title."); _title.Focus(); return false; }
        if (!_isNew)
        {
            var latest = state.Item(_orig.Id);
            if (latest == null || latest.DeletedAt != null || latest.UpdatedAt != _orig.UpdatedAt)
            { ShowWarning("This task changed or was removed while you were editing. Reopen it before saving; your draft is still here."); return false; }
        }
        var draft = ScheduleDraft(_orig, out var error);
        if (draft == null) { ShowWarning(error); return false; }
        var priority = Array.IndexOf(Priorities, _priority.SelectedItem as string ?? "None");
        draft = draft with
        {
            Title = title, Notes = _notes.Text.Trim(),
            Priority = priority switch { 1 => Priority.LOW, 2 => Priority.MEDIUM, 3 => Priority.HIGH, 4 => Priority.URGENT, _ => null },
            AlertType = (_alert.SelectedItem as string) switch { "Ring" => "R", "Muted" => "OFF", _ => "N" },
            Personal = _personal.IsChecked == true, SnoozedUntil = draft.DueAt != _orig.DueAt ? null : _orig.SnoozedUntil,
        };
        if (_orig.Tab == Tab.TASKS)
        {
            if (_taskList?.SelectedItem is not TaskChoice choice || choice.Placeholder)
            { ShowWarning("Choose a destination list, or explicitly select Unsorted."); _taskList?.Focus(); return false; }
            var list = TaskLists.Live(state.Settings.TaskLists).FirstOrDefault(l => l.Id == choice.Id);
            if (choice.Id != null && list == null)
            {
                bool deleted = state.Settings.TaskLists.Any(l => l.Id == choice.Id && l.DeletedAt != null);
                if (deleted || _isNew || choice.Id != _orig.ListId)
                { ShowWarning("That list is no longer available. Select another task list."); return false; }
                draft = draft with { ListId = _orig.ListId, Group = _orig.Group };
            }
            else draft = draft with { ListId = list?.Id, Group = list?.Name };
            bool done = _taskStatus?.SelectedIndex == 1;
            if (done != _orig.Done) draft = done ? ItemRules.Complete(draft, state.Now).Item : ItemRules.Revive(draft, state.Now);
        }
        if (_orig.Tab == Tab.SHOP)
        {
            var list = (_list?.SelectedItem as ListChoice)?.List;
            var shopName = _shop?.Text.Trim() ?? "";
            var shop = state.Shops.FirstOrDefault(s => string.Equals(s.Name, shopName, StringComparison.OrdinalIgnoreCase));
            var productName = _product?.Text.Trim() ?? "";
            var product = productName.Length == 0 ? null : state.Products.FirstOrDefault(p => string.Equals(p.Name, productName, StringComparison.OrdinalIgnoreCase));
            if (product == null && productName.Length > 0)
            {
                product = new Product { Id = Ids.Next(), Name = productName, DefaultUnit = string.IsNullOrWhiteSpace(_unit?.Text) ? null : _unit!.Text.Trim() };
                state.UpsertProduct(product);
            }
            int? lapse = int.TryParse(_lapse?.Text.Trim(), out var value) && value > 0 ? Math.Min(value, 3650) : null;
            draft = draft with
            {
                ListId = list?.Id, Group = list?.Name, Personal = draft.Personal || list?.Personal == true,
                Quantity = string.IsNullOrWhiteSpace(_qty?.Text) ? null : _qty!.Text.Trim(), Unit = string.IsNullOrWhiteSpace(_unit?.Text) ? null : _unit!.Text.Trim(),
                Price = string.IsNullOrWhiteSpace(_price?.Text) ? null : _price!.Text.Trim(), ShopName = shopName.Length == 0 ? null : shop?.Name ?? shopName,
                ShopId = shop?.Id, ProductId = product?.Id, LapseValue = lapse,
                LapseUnit = lapse == null ? null : _lapseUnit?.SelectedIndex == 1 ? LapseUnit.MONTHS : LapseUnit.DAYS, Staple = _staple?.IsChecked == true,
            };
        }
        if (_orig.Tab == Tab.LEARN) draft = draft with
        {
            Platform = string.IsNullOrWhiteSpace(_platform?.Text) ? null : _platform!.Text.Trim(), Url = string.IsNullOrWhiteSpace(_url?.Text) ? null : _url!.Text.Trim(),
            Topic = string.IsNullOrWhiteSpace(_topic?.Text) ? null : _topic!.Text.Trim(), Progress = (int)(_progress?.Value ?? 0),
            HoursSpent = double.TryParse(_hours?.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) && hours >= 0 ? hours : 0,
        };
        state.Upsert(draft);
        if (!_isNew && draft.ListId != _orig.ListId)
        {
            var name = _orig.Tab == Tab.TASKS ? draft.Group ?? "Unsorted" : draft.ListId is long id ? state.List(id)?.Name ?? "Unsorted" : "Unsorted";
            App.Current.Main.Snack($"Moved to {name}", () => state.Upsert(_orig));
        }
        else App.Current.Main.Snack(_isNew ? $"Added \"{title}\"" : "Saved");
        return true;
    }
}
