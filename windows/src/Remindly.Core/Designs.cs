namespace Remindly.Core;

/// <summary>One of the desktop looks offered in Settings → Appearance.</summary>
public sealed record DesignInfo(string Code, string Letter, string Name, string Summary);

/// <summary>
/// The desktop app's designs (Windows; macOS when its app exists). The choice is stored in the Windows-only part of
/// <see cref="AppSettings"/>, is never adopted from an imported backup, and Android ignores it.
/// </summary>
public static class Designs
{
    public const string Fluent = "FLUENT";
    public const string Board = "BOARD";
    public const string Command = "COMMAND";
    public const string Hub = "HUB";

    /// <summary>Design A is the default.</summary>
    public const string Default = Fluent;

    public static readonly IReadOnlyList<DesignInfo> All = new[]
    {
        new DesignInfo(Fluent, "A", "Fluent", "Windows 11 look: a left menu, your list and a details pane beside it."),
        new DesignInfo(Board, "B", "Day Board", "Columns for Overdue, Today, Tomorrow and Later. In Shop mode your lists sit side by side."),
        new DesignInfo(Command, "C", "Command Dark", "Dark and dense: table rows, quick views and a Ctrl+K command bar."),
        new DesignInfo(Hub, "D", "Today Hub", "A home screen of tiles: due today, call-backs, the week, learning and shopping."),
    };

    public static string Normalize(string? code)
    {
        var c = (code ?? "").Trim().ToUpperInvariant();
        return All.Any(d => d.Code == c) ? c : Default;
    }

    public static DesignInfo Info(string? code)
    {
        var c = Normalize(code);
        return All.First(d => d.Code == c);
    }
}
