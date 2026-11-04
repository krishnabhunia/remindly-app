using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Remindly.App.Services;
using Remindly.Core;

namespace Remindly.App.Views;

/// <summary>Approved design C: an explicit, non-destructive three-step first connection.</summary>
public sealed class CloudSetupWindow : Window
{
    private readonly CloudAccount _cloud;
    private readonly StackPanel _body = new() { Margin = new Thickness(22, 18, 22, 18) };
    private int _step = 1;
    private string? _backupPath;
    private string? _message;

    public CloudSetupWindow(CloudAccount cloud)
    {
        _cloud = cloud;
        Title = "Remindly · Connect your devices";
        Width = 625;
        Height = 530;
        MinWidth = 500;
        MinHeight = 430;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _body };
        Theme.Dress(this);
        SetResourceReference(BackgroundProperty, "SurfaceBrush");
        Render();
    }

    private static Button Action(string name, Action action, bool primary = false)
    {
        var button = primary ? Ui.Primary(name, action) : Ui.Btn(name, action);
        button.Margin = new Thickness(0, 6, 9, 6);
        return button;
    }

    private void Render()
    {
        _body.Children.Clear();
        _body.Children.Add(Ui.Sub($"GOOGLE ACCOUNT  /  SAFE MERGE  /  READY      Step {_step} of 3"));
        _body.Children.Add(Ui.H1(_step switch
        {
            1 => "Your reminders, everywhere",
            2 => "Bring existing data together",
            _ => "Account & Sync ready",
        }));
        if (_step == 1)
        {
            _body.Children.Add(Ui.Sub("Sign in with the SAME Google account used by Remindly on your Android phone. " +
                "Google opens in your system browser; Remindly never asks for your Google password."));
            _body.Children.Add(Ui.Sub("A one-time Google OAuth Desktop client must be created in the same Firebase project as your phone. " +
                "Select its downloaded client_secret_*.json here; it is stored encrypted on this Windows account."));
            _body.Children.Add(Ui.Row(Action("Select Google Desktop client JSON…", SelectClient),
                Ui.Sub(_cloud.Auth?.HasClient == true ? "✓ Desktop client configured" : "Not configured yet")));
            var signIn = Action("Continue with Google", async () => await SignIn(), primary: true);
            signIn.IsEnabled = _cloud.Ready && _cloud.Auth?.HasClient == true;
            _body.Children.Add(signIn);
            if (_cloud.SignedIn)
            {
                _body.Children.Add(Ui.Sub("Signed in as " + _cloud.Email));
                _body.Children.Add(Action("Next: Merge safely →", () => { _step = 2; Render(); }, primary: true));
            }
            if (_cloud.ConfigurationError != null) _body.Children.Add(Ui.Sub(_cloud.ConfigurationError));
        }
        else if (_step == 2)
        {
            _body.Children.Add(Ui.Sub("Recommended: preserve both devices' records. Remindly first saves an independent " +
                "local backup, then downloads Android records, merges by stable ID and last-updated time, and uploads " +
                "only missing or newer Windows records. Deleted-item tombstones remain intact."));
            var safe = Ui.Stack(
                Ui.Text("✓ Merge Android + Windows data without replacing either dataset", 14),
                Ui.Sub("✓ Create a timestamped pre-sync backup on this PC"),
                Ui.Sub("✓ Keep Windows appearance, notification and updater preferences local"),
                Ui.Sub("✓ Stop and show an error if Firebase rules or authentication reject the sync"));
            safe.Margin = new Thickness(0, 15, 0, 15);
            _body.Children.Add(safe);
            _body.Children.Add(Ui.Row(Action("← Back", () => { _step = 1; Render(); }),
                Action("Back up & enable two-way sync", Enable, primary: true)));
        }
        else
        {
            _body.Children.Add(Ui.Sub("Your Windows device is configured to synchronize automatically. " +
                "Android must also have Google Account → Cloud sync (beta) enabled with the same account."));
            if (_backupPath != null) _body.Children.Add(Ui.Sub("Pre-sync recovery backup: " + _backupPath));
            _body.Children.Add(Ui.Sub("This step confirms local setup, not that remote sync succeeded. " +
                "Check the Account & Sync status on the Settings page for a successful cloud round-trip."));
            _body.Children.Add(Action("Open Account & Sync", Close, primary: true));
        }
        if (!string.IsNullOrEmpty(_message))
        {
            var hint = Ui.Sub(_message);
            hint.Margin = new Thickness(0, 16, 0, 0);
            _body.Children.Add(hint);
        }
    }

    private void SelectClient()
    {
        var d = new OpenFileDialog
        {
            Title = "Google OAuth Desktop client (from the same Firebase project)",
            Filter = "Google OAuth client (*.json)|*.json",
        };
        if (d.ShowDialog(this) != true) return;
        try { _cloud.SaveDesktopClient(File.ReadAllText(d.FileName)); _message = "Desktop OAuth client saved securely."; }
        catch (Exception ex) { _message = "Client configuration failed: " + ex.Message; }
        Render();
    }

    private async Task SignIn()
    {
        _message = "Waiting for Google sign-in in your browser…";
        Render();
        try
        {
            if (await _cloud.SignInAsync())
            {
                _step = 2;
                _message = "Signed in as " + _cloud.Email;
            }
            else _message = _cloud.Auth?.LastError ?? "Sign-in was not completed.";
        }
        catch (Exception ex) { _message = ex.Message; }
        Render();
    }

    private void Enable()
    {
        try
        {
            _backupPath = _cloud.EnableAfterSafeMergeApproval();
            _message = "Backup created; cloud synchronization has started.";
            _step = 3;
        }
        catch (Exception ex) { _message = "Sync was not enabled: " + ex.Message; }
        Render();
    }
}
