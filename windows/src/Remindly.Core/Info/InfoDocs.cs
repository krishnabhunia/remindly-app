using System.Text.RegularExpressions;
using Remindly.Core.Updates;

namespace Remindly.Core.Info;

/// <summary>One row of docs/FEATURES.md.</summary>
public sealed record FeatureRow(int SrNo, string Area, string WhatItDoes, string Since);

/// <summary>
/// docs/FEATURES.md (embedded at build time): one table "| SrNo. | Area | What it does | Since |".
/// tools/features.py checks the same rules in CI.
/// </summary>
public static class FeaturesDoc
{
    public const string Header = "| SrNo. | Area | What it does | Since |";

    public static List<FeatureRow> Parse(string markdown)
    {
        var lines = (markdown ?? "").Replace("\r", "").Split('\n');
        var start = Array.FindIndex(lines, l => l.Trim() == Header);
        if (start < 0) throw new FormatException("FEATURES.md has no feature table.");
        var rows = new List<FeatureRow>();
        foreach (var raw in lines.Skip(start + 2))
        {
            var line = raw.Trim();
            if (!line.StartsWith('|')) break;
            var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length != 4 || !int.TryParse(cells[0], out var n) || n != rows.Count + 1 || !Regex.IsMatch(cells[3], @"^\d+\.\d+\.\d+$"))
                throw new FormatException($"FEATURES.md row {rows.Count + 1} is not valid: {line}");
            rows.Add(new FeatureRow(n, cells[1], cells[2], cells[3]));
        }
        return rows;
    }

    /// <summary>Search (area or text, any case) and optionally only the rows new in <paramref name="newIn"/>.</summary>
    public static List<FeatureRow> Filter(IEnumerable<FeatureRow> rows, string? query, string? newIn = null)
    {
        var q = (query ?? "").Trim();
        return rows.Where(r => newIn == null || r.Since == newIn)
            .Where(r => q.Length == 0 || r.Area.Contains(q, StringComparison.OrdinalIgnoreCase) || r.WhatItDoes.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}

/// <summary>A version's section of windows/CHANGELOG.md (embedded at build time).</summary>
public sealed record ChangelogSection(string Version, string Title, string Body);

public static class ChangelogDoc
{
    /// <summary>The "## x.y.z …" section (to the next "## " heading), or null.</summary>
    public static ChangelogSection? Section(string markdown, string version)
    {
        var lines = (markdown ?? "").Replace("\r", "").Split('\n');
        var head = new Regex(@"^##\s+" + Regex.Escape(version) + @"(?:\s|$)");
        var i = Array.FindIndex(lines, l => head.IsMatch(l));
        if (i < 0) return null;
        var body = lines.Skip(i + 1).TakeWhile(l => !l.StartsWith("## ", StringComparison.Ordinal));
        var title = lines[i][2..].Trim();
        return new ChangelogSection(version, title, string.Join("\n", body).Trim());
    }
}

public enum MdKind { Heading, Bullet, SubBullet, Paragraph }

/// <summary>One displayed block of the changelog and its inline runs.</summary>
public sealed record MdBlock(MdKind Kind, string Text);

/// <summary>Inline run: plain, bold (**x**) or code (`x`).</summary>
public sealed record MdRun(string Text, bool Bold, bool Code);

/// <summary>The little Markdown the changelog uses: ### headings, - bullets (two levels), paragraphs, **bold**, `code`. Tables are skipped.</summary>
public static class MarkdownLite
{
    public static List<MdBlock> Blocks(string markdown)
    {
        var blocks = new List<MdBlock>();
        bool joinable = false;
        foreach (var raw in (markdown ?? "").Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            var text = line.Trim();
            if (text.Length == 0 || text.StartsWith('|')) { joinable = false; continue; }
            if (text.StartsWith('#')) { blocks.Add(new MdBlock(MdKind.Heading, text.TrimStart('#').Trim())); joinable = false; continue; }
            var bullet = Regex.Match(line, @"^(\s*)[-*]\s+(.*)$");
            if (bullet.Success)
            {
                blocks.Add(new MdBlock(bullet.Groups[1].Length >= 2 ? MdKind.SubBullet : MdKind.Bullet, bullet.Groups[2].Value.Trim()));
                joinable = true;
                continue;
            }
            if (joinable && blocks.Count > 0) blocks[^1] = blocks[^1] with { Text = blocks[^1].Text + " " + text };
            else blocks.Add(new MdBlock(MdKind.Paragraph, text));
            joinable = true;
        }
        return blocks;
    }

    public static List<MdRun> Runs(string text)
    {
        var runs = new List<MdRun>();
        foreach (Match m in Regex.Matches(text ?? "", @"\*\*(.+?)\*\*|`([^`]+)`|([^*`]+|[*`])"))
        {
            if (m.Groups[1].Success) runs.Add(new MdRun(m.Groups[1].Value, true, false));
            else if (m.Groups[2].Success) runs.Add(new MdRun(m.Groups[2].Value, false, true));
            else if (runs.Count > 0 && !runs[^1].Bold && !runs[^1].Code) runs[^1] = runs[^1] with { Text = runs[^1].Text + m.Value };
            else runs.Add(new MdRun(m.Value, false, false));
        }
        return runs;
    }
}

/// <summary>The copies of docs/FEATURES.md and windows/CHANGELOG.md built into Remindly.Core.</summary>
public static class InfoResources
{
    public static string Read(string name)
    {
        using var stream = typeof(InfoResources).Assembly.GetManifestResourceStream(name);
        if (stream == null) return "";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static string FeaturesMarkdown => Read("FEATURES.md");

    public static string ChangelogMarkdown => Read("CHANGELOG.md");

    /// <summary>The feature table, or an empty list if the embedded file cannot be read.</summary>
    public static List<FeatureRow> Features()
    {
        try { return FeaturesDoc.Parse(FeaturesMarkdown); }
        catch (FormatException ex) { Log.Warn("FEATURES.md: " + ex.Message); return new(); }
    }

    /// <summary>This version's section (a beta shows the section of the x.y.z it is testing).</summary>
    public static ChangelogSection? WhatsNew(string version) => ChangelogDoc.Section(ChangelogMarkdown, BuildInfo.CoreVersion(version));

    public static string ReleasesUrl => UpdateLogic.ReleasesUrl;
}
