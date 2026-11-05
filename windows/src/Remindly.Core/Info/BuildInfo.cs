using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Remindly.Core.Updates;

namespace Remindly.Core.Info;

/// <summary>
/// The facts CI stamps into the build (Directory.Build.props → AssemblyMetadata): the pull request, the commit,
/// the build time and the GitHub tag. All empty in a local build.
/// </summary>
public sealed record BuildMetadata(string? SourcePr, string? SourceCommit, string? BuildTimeUtc, string? ReleaseTag)
{
    public static BuildMetadata Current { get; } = FromAssembly(typeof(BuildMetadata).Assembly);

    public static BuildMetadata FromAssembly(Assembly assembly)
    {
        var values = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .GroupBy(a => a.Key).ToDictionary(g => g.Key, g => g.Last().Value);
        string? Get(string key) => values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        return new BuildMetadata(Get("SourcePr"), Get("SourceCommit"), Get("BuildTimeUtc"), Get("ReleaseTag"));
    }

    /// <summary>No CI facts at all: a developer's local build.</summary>
    public bool IsLocal => SourceCommit == null && BuildTimeUtc == null && ReleaseTag == null;

    public int? PrNumber => int.TryParse(SourcePr?.TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;

    public string? ShortCommit => SourceCommit is { Length: >= 7 } c && Regex.IsMatch(c, "^[0-9a-fA-F]+$") ? c[..7].ToLowerInvariant() : null;

    public DateTime? BuiltUtc => DateTime.TryParse(BuildTimeUtc, CultureInfo.InvariantCulture,
        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : null;
}

/// <summary>One row of a table in the Info window: the item, its value and an optional link.</summary>
public sealed record InfoRow(string Item, string Value, string? Link = null);

/// <summary>Version, channel and the text of Info → This build and "Copy build info" (kept out of the UI to be unit-tested).</summary>
public static class BuildInfo
{
    public const string AppName = "Remindly";
    public const string LocalBuild = "Local build";

    public static bool IsBeta(string version) => UpdateLogic.IsBetaVersion(version);

    public static string Channel(string version) => IsBeta(version) ? "Beta" : "Stable";

    /// <summary>"2.17.0-beta001+abc" → "2.17.0".</summary>
    public static string CoreVersion(string version) => version.Split('+')[0].Split('-')[0];

    /// <summary>"001" for 2.17.0-beta001, else null.</summary>
    public static string? BetaNumberText(string version) =>
        UpdateLogic.ShortBetaNumber(version) is int n ? n.ToString("000", CultureInfo.InvariantCulture) : null;

    /// <summary>"Version 2.17.0-beta001 · Beta · Windows".</summary>
    public static string Subtitle(string version) => $"Version {version} · {Channel(version)} · Windows";

    /// <summary>The banner of a test build, or null for a stable one.</summary>
    public static string? BetaBanner(string version)
    {
        if (!IsBeta(version)) return null;
        var core = CoreVersion(version);
        return BetaNumberText(version) is string n
            ? $"This is a test build. It is beta {n} of {AppName} {core}, made from a pull request."
            : $"This is a test build of {AppName} {core}, made from a pull request.";
    }

    /// <summary>"04-Nov-2026, 19:55" in the PC's time zone.</summary>
    public static string BuiltOn(BuildMetadata meta, TimeZoneInfo? zone = null) =>
        meta.BuiltUtc is DateTime utc
            ? TimeZoneInfo.ConvertTimeFromUtc(utc, zone ?? TimeZoneInfo.Local).ToString("dd-MMM-yyyy, HH:mm", CultureInfo.InvariantCulture)
            : LocalBuild;

    public static string PullRequestUrl(int pr) => $"{UpdateLogic.RepoUrl}/pull/{pr}";

    public static string CommitUrl(string sha) => $"{UpdateLogic.RepoUrl}/commit/{sha}";

    public static string TagUrl(string tag) => $"{UpdateLogic.RepoUrl}/releases/tag/{Uri.EscapeDataString(tag)}";

    /// <summary>The rows of Info → This build. "Made from" appears only when the pull request is known.</summary>
    public static List<InfoRow> ThisBuild(string version, BuildMetadata meta, string installedAs, TimeZoneInfo? zone = null)
    {
        var rows = new List<InfoRow> { new("Version", version), new("Channel", Channel(version)) };
        if (meta.PrNumber is int pr) rows.Add(new("Made from", $"Pull request #{pr}", PullRequestUrl(pr)));
        else if (meta.IsLocal) rows.Add(new("Made from", LocalBuild));
        rows.Add(meta.ShortCommit is string c ? new("Commit", c, CommitUrl(meta.SourceCommit!)) : new("Commit", LocalBuild));
        rows.Add(new("Built on", BuiltOn(meta, zone)));
        rows.Add(meta.ReleaseTag is string tag ? new("GitHub tag", tag, TagUrl(tag)) : new("GitHub tag", LocalBuild));
        rows.Add(new("Download", $"{AppName}_{version}.zip"));
        rows.Add(new("Installed as", installedAs));
        return rows;
    }

    /// <summary>
    /// Two lines for a bug report, e.g.
    /// "Remindly 2.17.0-beta001 (Beta) · PR #20 · 4e68c2a · built 2026-11-04 14:25 UTC" and
    /// "Windows 11 Pro 24H2 x64 · installer, per-user · .NET 8".
    /// </summary>
    public static string CopyText(string version, BuildMetadata meta, string windows, string installedAs, string dotnet)
    {
        var first = new List<string> { $"{AppName} {version} ({Channel(version)})" };
        if (meta.PrNumber is int pr) first.Add($"PR #{pr}");
        if (meta.ShortCommit is string c) first.Add(c);
        if (meta.BuiltUtc is DateTime t) first.Add($"built {t.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC");
        if (meta.IsLocal) first.Add(LocalBuild);
        return string.Join(" · ", first) + Environment.NewLine + $"{windows} · {installedAs} · {dotnet}";
    }

    /// <summary>
    /// "Windows 11 Pro 24H2 x64". Windows 11 still reports "Windows 10 …" as its product name in the registry;
    /// the build number (22000 and later) tells them apart.
    /// </summary>
    public static string WindowsName(string? productName, string? displayVersion, int build, string architecture)
    {
        var name = string.IsNullOrWhiteSpace(productName) ? "Windows" : productName.Trim();
        if (build >= 22000 && name.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase)) name = "Windows 11" + name[10..];
        var parts = new List<string> { name };
        if (!string.IsNullOrWhiteSpace(displayVersion)) parts.Add(displayVersion.Trim());
        if (!string.IsNullOrWhiteSpace(architecture)) parts.Add(architecture.Trim().ToLowerInvariant());
        return string.Join(" ", parts);
    }

    /// <summary>".NET 8" from Environment.Version.</summary>
    public static string DotNetName(Version runtime) => $".NET {runtime.Major}";
}
