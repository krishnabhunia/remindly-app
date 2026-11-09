using System.Text.RegularExpressions;

namespace Remindly.Core.Updates;

/// <summary>A real per-version change, not the generic release download instructions.</summary>
public sealed record ImplementationChange(string Category, string Title, string Detail);

public static class ReleaseNotes
{
    // The first Windows/Android cloud-sync release was published with package-only
    // GitHub notes. Preserve the actual approved PR #17 implementation record here
    // so installed 2.16.0 copies can still explain what they contain.
    private static readonly ImplementationChange[] Sync216 =
    [
        new("New", "Google sign-in on Windows", "System-browser Google OAuth and a top-right Account entry; Settings → Account & Sync shows identity, status and errors. PR #17, Issue #16."),
        new("New", "Two-way Windows–Android cloud sync", "Tasks, Learn, Buy items/lists, Calls, shops and saved places synchronize through the same Firebase account. PR #17."),
        new("Improved", "Backups and conflict protection", "The first sync creates a local backup; timestamp merges, incremental queries and deletion markers protect changes. PR #17."),
        new("Limitation", "Some catalogues are local-only", "Android city, chain and product catalogues do not sync; live Google OAuth and two-device convergence still require device verification. Issue #16.")
    ];

    public static IReadOnlyList<ImplementationChange> Parse(string? version, string? body)
    {
        var entries = new List<ImplementationChange>();
        var category = "Implementation";
        foreach (var raw in (body ?? "").Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (Regex.Match(line, @"^#{2,4}\s+(.+)$") is { Success: true } heading)
            {
                category = heading.Groups[1].Value.Trim();
                continue;
            }
            if (!line.StartsWith("- ", StringComparison.Ordinal)) continue;
            var content = line[2..].Trim();
            // A structured release line uses - **New (PR #17):** **Feature:** detail.
            var label = Regex.Match(content, @"^\*\*(.+?)\*\*:?\s*(.*)$");
            var title = label.Success ? label.Groups[1].Value : content;
            var detail = label.Success ? label.Groups[2].Value.Trim() : content;
            if (detail.StartsWith("**", StringComparison.Ordinal))
            {
                var nested = Regex.Match(detail, @"^\*\*(.+?)\*\*:?\s*(.*)$");
                if (nested.Success) { title = nested.Groups[1].Value; detail = nested.Groups[2].Value; }
            }
            if (detail.Length < 15 || title.Length > 180) continue;
            entries.Add(new(category, title.TrimEnd(':').Trim(), detail));
        }
        if (entries.Count > 0) return entries;
        if (string.Equals((version ?? "").Split('-')[0], "2.16.0", StringComparison.OrdinalIgnoreCase)) return Sync216;
        return [];
    }
}
