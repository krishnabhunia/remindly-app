using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Remindly.Core.Updates;

namespace Remindly.App.Views;

/// <summary>Exact release implementation details; does not select or install an update.</summary>
internal static class ReleaseNotesWindow
{
    public static void Show(Window? owner, ReleaseInfo release)
    {
        var dialog = new Window
        {
            Title = $"Remindly · What's New in v{release.DisplayVersion}",
            Width = 800, Height = 670, MinWidth = 540, MinHeight = 430,
            WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner, ShowInTaskbar = false,
        };
        var content = new StackPanel { Margin = new Thickness(22) };
        content.Children.Add(new TextBlock { Text = $"What's New in Remindly v{release.DisplayVersion}", FontSize = 23,
            FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = release.IsBeta ? "Beta preview" : "Stable release", Margin = new Thickness(0, 4, 0, 8) });
        content.Children.Add(new TextBlock { Text = "The following implementations belong to the selected release. Opening this page never installs or selects an update.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 13) });
        var entries = ReleaseNotes.Parse(release.DisplayVersion, release.Body);
        if (entries.Count == 0)
            content.Children.Add(new TextBlock { Text = "This release did not publish feature-level implementation details. Check its GitHub release and linked pull requests before updating.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 16) });
        foreach (var entry in entries)
        {
            var group = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            group.Children.Add(new TextBlock { Text = entry.Category, FontSize = 12, Foreground = Brushes.SlateGray });
            group.Children.Add(new TextBlock { Text = entry.Title, FontWeight = FontWeights.SemiBold, FontSize = 15,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 4) });
            group.Children.Add(new TextBlock { Text = entry.Detail, TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new Border { BorderBrush = SystemColors.ActiveBorderBrush,
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 2), Child = group });
        }
        content.Children.Add(new TextBlock { Text = $"Source: {release.TagName} · GitHub Release: {release.HtmlUrl}",
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 8, 0, 12) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var close = new Button { Content = "Close", Padding = new Thickness(20, 8, 20, 8) };
        close.Click += (_, _) => dialog.Close();
        actions.Children.Add(close);
        content.Children.Add(actions);
        dialog.Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        dialog.ShowDialog();
    }
}
