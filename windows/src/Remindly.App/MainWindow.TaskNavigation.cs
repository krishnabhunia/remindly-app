using System.Windows.Input;
using Remindly.App.Views;

namespace Remindly.App;

public partial class MainWindow
{
    // Class handling runs before the shell's general PreviewKeyDown shortcut handler.
    // Ctrl+N must use the visible workspace, not the old unscoped ItemEditor.New call.
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Page.Content is ItemsView view && view.HandleWorkspaceShortcut(key, Keyboard.Modifiers))
        {
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }
}
