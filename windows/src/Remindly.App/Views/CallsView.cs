using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Remindly.App.Services;
using Remindly.Core;

namespace Remindly.App.Views;

/// <summary>
/// Calls: call-back reminders. On a PC there is no call log, so every call-back is added by hand
/// (Android's auto-detected missed calls arrive with an imported backup). Call opens the tel: handler
/// (Phone Link), WhatsApp opens wa.me with the saved message.
/// </summary>
public sealed class CallsView : DockPanel, IPage
{
    private readonly StackPanel _list = new();
    private readonly TextBlock _count = Ui.Sub("");
    private bool _doneView;

    public CallsView()
    {
        var bar = Ui.Columns("*,Auto",
            Ui.Stack(Ui.H1("Calls"), _count),
            Ui.Row(
                Ui.Chip("Active", "calls", true, () => { _doneView = false; Refresh(); }),
                Ui.Chip("Done", "calls", false, () => { _doneView = true; Refresh(); }),
                new Border { Width = 12 },
                Ui.Primary("+ Call-back", () => CallEditor.OpenFor(null))));
        bar.Margin = new Thickness(0, 0, 0, 10);
        SetDock(bar, Dock.Top);
        Children.Add(bar);
        Children.Add(Ui.Scroll(_list));
    }

    public void Refresh()
    {
        var state = AppState.Current;
        long now = state.Now;
        var all = state.LiveCalls().ToList();
        _count.Text = $"{all.Count(c => !c.Done)} to call back · {all.Count(c => c.Done)} done";
        var shown = all.Where(c => c.Done == _doneView)
            .OrderBy(c => _doneView ? -(c.DoneAt ?? 0) : (c.SnoozedUntil ?? c.RecurAt ?? long.MaxValue)).ToList();
        _list.Children.Clear();
        if (shown.Count == 0)
        {
            _list.Children.Add(Ui.EmptyState(_doneView ? "No finished call-backs" : "Nobody to call back",
                "Add a call-back with a time and Remindly reminds you — with Call and WhatsApp buttons."));
            return;
        }
        foreach (var c in shown) _list.Children.Add(CallCard(c, now));
    }

    private static Border CallCard(CallReminder c, long now)
    {
        var state = AppState.Current;
        // Same Move-to-Done flow as tasks, with the phone's Calls delay (approved M1–M4).
        var key = DoneFlow.Key(c);
        var check = Ui.Check(c.Done || DoneFlow.IsBusy(key), on =>
        {
            if (!on && DoneFlow.IsPending(key)) { DoneFlow.Cancel(key); return; }
            var before = state.LiveCalls().FirstOrDefault(x => x.Id == c.Id);
            if (before == null || before.Done == on) return;
            if (on)
            {
                int delay = state.Phone.CallsDelaySec;
                DoneFlow.Complete(key, delay, () =>
                {
                    var current = state.LiveCalls().FirstOrDefault(x => x.Id == c.Id);
                    if (current == null || current.Done) return;
                    var message = state.CompleteCall(current);
                    if (delay == 0 || current.RepeatMode != "OFF")
                        App.Current.Main.Snack(message, () => { state.UpsertCall(current); DoneFlow.MarkGlow(key); });
                });
            }
            else DoneFlow.Revive(key, () =>
            {
                var current = state.LiveCalls().FirstOrDefault(x => x.Id == c.Id);
                if (current == null || !current.Done) return;
                state.UpsertCall(current with { Done = false, DoneAt = null });
                App.Current.Main.Snack("Back to Active", () => state.UpsertCall(current));
            });
        }, "Called back");
        WorkspaceUi.Identify(check, $"complete-call-{c.Id}", "Called back: " + c.Display);

        var titleRow = new WrapPanel();
        // One click on the name opens the editor (approved F1); double-clicking the card still works.
        var name = WorkspaceUi.Link($"call-name-{c.Id}", "Edit call-back " + c.Display, c.Display, () => CallEditor.OpenFor(c), 15);
        name.Margin = new Thickness(0, 0, 8, 0);
        name.VerticalAlignment = VerticalAlignment.Center;
        titleRow.Children.Add(name);
        // The phone's Call card details (approved S10) and Card details for Calls (S5).
        var phone = state.Phone;
        var parts = phone.Card(null);
        if (!parts.Checkbox) check.Visibility = Visibility.Collapsed;
        if (!c.Done && phone.CallCardShowMissedAt)
            titleRow.Children.Add(Ui.Tag(c.Source == CallSource.AUTO ? "Auto" : "Manual", Ui.Res("BlueBrush"), Ui.Res("BlueSoftBrush")));
        if (c.MissedCount > 1 && (c.Done || phone.CallCardShowMissedCount))
            titleRow.Children.Add(Ui.Tag($"×{c.MissedCount} missed", Ui.Res("DangerBrush"), Ui.Res("DangerSoftBrush")));
        if (phone.CallCardShowLabel && !string.IsNullOrWhiteSpace(c.Label))
            titleRow.Children.Add(Ui.Tag(c.Label!, Ui.Res("InkSubtleBrush"), Ui.Res("SurfaceBrush")));

        var meta = new List<string>();
        if (c.Display != c.Number && phone.CallCardShowNumber) meta.Add(c.Number);   // a nameless call already shows its number
        if (!string.IsNullOrWhiteSpace(c.Company)) meta.Add(c.Company!);
        if (c.Done)
        {
            if (parts.DateTime && c.DoneAt is long doneAt) meta.Add("Done " + Clock.FormatDayTime(doneAt));
        }
        else if (c.SnoozedUntil is long sn && sn > now) meta.Add("💤 " + Clock.FormatDayTime(sn));
        else if (phone.CallCardShowMissedAt && parts.DateTime && (c.LastMissedAt ?? c.RecurAt ?? c.CreatedAt) is long stamp)
            meta.Add(Clock.FormatDayTime(stamp));
        if (c.RepeatMode != "OFF" && c.RecurAt is long next && (c.Done || phone.CallCardShowNext))
            meta.Add((c.Done ? "↻ returns " : "↻ next ") + Clock.FormatDay(next));
        if (phone.CallCardShowNote && !string.IsNullOrWhiteSpace(c.Note)) meta.Add("✎ " + c.Note!.Trim());
        if (!string.IsNullOrWhiteSpace(c.Message)) meta.Add("💬 " + c.Message);
        var metaText = Ui.Sub(string.Join("  ·  ", meta));
        if (meta.Count == 0) metaText.Visibility = Visibility.Collapsed;
        if (!c.Done && c.RecurAt is long due && due < now) metaText.Foreground = Ui.Res("DangerBrush");

        var actions = Ui.Row(
            Ui.IconBtn("📞", () => InstallInfo.OpenUrl("tel:" + CallText.NormalizePhone(c.Number)), "Call (opens Phone Link or your calling app)"),
            Ui.IconBtn("💬", () => InstallInfo.OpenUrl(CallText.WhatsAppUrl(c.Number, c.Message)), "WhatsApp"),
            Ui.IconBtn("✎", () => CallEditor.OpenFor(c), "Edit"),
            Ui.IconBtn("🗑", () =>
            {
                // The phone's Delete style for Calls (approved S3).
                if (state.Phone.ConfirmDelete(null) && !Ui.Confirm($"Move the call-back \"{c.Display}\" to the Bin?")) return;
                state.UpsertCall(c with { DeletedAt = state.Now });
                App.Current.Main.Snack("Moved to the Bin", () => state.UpsertCall(c));
            }, "Delete"));
        var body = Ui.Stack(titleRow, metaText);
        if (DoneFlow.Strip(key) is { } strip) body.Children.Add(strip);
        return DoneFlow.Track(Ui.Card(Ui.Columns("Auto,*,Auto", check, body, actions), () => CallEditor.OpenFor(c)), key);
    }
}

public sealed class CallEditor : EditorWindow
{
    private readonly CallReminder _orig;
    private readonly bool _isNew;
    private readonly TextBox _first = new(), _last = new(), _number = new(), _company = new(), _note = new(), _message = new(), _label = new();
    private readonly DatePicker _date = new() { Width = 150 };
    private readonly TextBox _time = new() { Width = 70 };
    private readonly ComboBox _repeat;
    private readonly ComboBox _alert;
    private static readonly (string Code, string Label)[] Modes = { ("OFF", "Once"), ("DAILY", "Daily"), ("WEEKLY", "Weekly (same weekday)"), ("MONTHLY_DAY", "Monthly (same day)"), ("YEARLY", "Yearly") };

    /// <summary>A new call-back started from typed text: digits become the number, anything else the name.</summary>
    public static void New(string text)
    {
        long now = AppState.Current.Now;
        text = text.Trim();
        bool phone = text.Length > 0 && text.All(ch => char.IsDigit(ch) || ch is '+' or ' ' or '-' or '(' or ')');
        var c = new CallReminder { Id = Ids.Next(), Source = CallSource.MANUAL, CreatedAt = now, RecurAt = now + 3_600_000L, Number = phone ? text : "", Name = phone ? null : text };
        new CallEditor(c, true).Open();
    }

    public static void OpenFor(CallReminder? c)
    {
        var now = Clock.NowMs();
        new CallEditor(c ?? new CallReminder { Id = Ids.Next(), Source = CallSource.MANUAL, CreatedAt = now, RecurAt = now + 3_600_000L }, c == null).Open();
    }

    private CallEditor(CallReminder c, bool isNew) : base(isNew ? "New call-back" : "Edit call-back", 500)
    {
        _orig = c;
        _isNew = isNew;
        _first.Text = c.FirstName ?? (c.FirstName == null && c.LastName == null ? c.Name ?? "" : "");
        _last.Text = c.LastName ?? "";
        Form.Children.Add(TwoColumns(Labeled("First name", _first), Labeled("Last name", _last)));
        _number.Text = c.Number;
        _company.Text = c.Company ?? "";
        Form.Children.Add(TwoColumns(Labeled("Phone number (with country code for WhatsApp)", _number), Labeled("Company", _company)));
        var at = Clock.ToLocal(c.RecurAt ?? Clock.NowMs());
        _date.SelectedDate = at.Date;
        _time.Text = at.ToString("HH:mm", CultureInfo.InvariantCulture);
        Field("Remind me", Ui.Row(_date, new Border { Width = 8 }, Ui.Sub("at"), new Border { Width = 8 }, _time));
        _repeat = new ComboBox();
        foreach (var (_, l) in Modes) _repeat.Items.Add(l);
        _repeat.SelectedIndex = Math.Max(0, Array.FindIndex(Modes, m => m.Code == c.RepeatMode));
        _alert = Combo(new[] { "Notify", "Ring", "Muted" }, c.AlertType == "OFF" ? "Muted" : c.AlertType.Contains('R') || c.AlertType.Contains('A') ? "Ring" : "Notify");
        Form.Children.Add(TwoColumns(Labeled("Repeat", _repeat), Labeled("Reminder", _alert)));
        _note.Text = c.Note ?? "";
        Field("Note", _note);
        _message.Text = c.Message ?? "";
        Field("WhatsApp message (optional)", _message);
        _label.Text = c.Label ?? "";
        Field("Label", _label);
        AddButtons(isNew ? "Add" : "Save");
        Loaded += (_, _) => _first.Focus();
    }

    protected override bool Save()
    {
        var number = _number.Text.Trim();
        if (number.Length == 0 && _first.Text.Trim().Length == 0) { ShowWarning("Enter a name or a phone number."); return false; }
        if (!TimeSpan.TryParseExact(_time.Text.Trim(), new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out var tod) || tod.TotalHours >= 24)
        {
            ShowWarning("Time must look like 18:30 (24-hour).");
            return false;
        }
        var at = Clock.FromLocal((_date.SelectedDate ?? DateTime.Today).Date + tod);
        var local = Clock.ToLocal(at);
        var mode = Modes[Math.Max(0, _repeat.SelectedIndex)].Code;
        string? N(TextBox t) => string.IsNullOrWhiteSpace(t.Text) ? null : t.Text.Trim();
        var rec = _orig with
        {
            FirstName = N(_first), LastName = N(_last), Name = CallText.FullName(N(_first), N(_last)) ?? _orig.Name,
            Number = number, Company = N(_company), Note = N(_note), Message = N(_message), Label = N(_label),
            RecurAt = at, SnoozedUntil = at != _orig.RecurAt ? null : _orig.SnoozedUntil,
            RepeatMode = mode,
            RepeatDays = mode switch { "WEEKLY" => new List<int> { Recurrence.IsoDow(local) }, "MONTHLY_DAY" => new List<int> { local.Day }, _ => new List<int>() },
            AlertType = (_alert.SelectedItem as string) switch { "Ring" => "R", "Muted" => "OFF", _ => "N" },
        };
        AppState.Current.UpsertCall(rec);
        App.Current.Main.Snack(_isNew ? $"Call-back added for {Clock.FormatDayTime(at)}" : "Saved");
        return true;
    }
}
