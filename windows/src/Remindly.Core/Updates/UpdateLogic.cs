using System.Text.Json;
using System.Text.RegularExpressions;

namespace Remindly.Core.Updates;

public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size);

public sealed record ReleaseInfo(Version Version, string TagName, string Name, string HtmlUrl, string? Body, bool Prerelease, List<ReleaseAsset> Assets)
{
    public string DisplayVersion => TagName.StartsWith("win-v", StringComparison.OrdinalIgnoreCase) ? TagName[5..] : TagName.TrimStart('v');
    public bool IsBeta => Prerelease || DisplayVersion.Contains("-beta", StringComparison.OrdinalIgnoreCase);
}

public enum UpdateStatus { UpToDate, UpdateAvailable, Unavailable }

public sealed record UpdateCheckResult(UpdateStatus Status, Version Current, ReleaseInfo? Release, string Message);

/// <summary>
/// Pure logic behind the Windows updates (unit-tested): versions, the win-v tag filter, GitHub
/// release / Atom parsing, the release-zip layout and the once-a-day ("next day") check rule.
///
/// The repository also publishes the Android app (tags vX.Y). Windows releases are tagged
/// win-vX.Y.Z and carry ONE zip, Remindly-Windows-X.Y.Z.zip, holding exactly two folders:
///   installer/Remindly-Setup-X.Y.Z.exe   — installs (Start menu, Apps &amp; features, uninstaller)
///   portable/Remindly.exe                — runs from anywhere, nothing installed
/// plus Remindly-Windows-X.Y.Z.zip.sha256 beside it for verification.
/// </summary>
public static class UpdateLogic
{
    public const string RepoOwner = "krishnabhunia";
    public const string RepoName = "remindly-app";
    public const string TagPrefix = "win-v";
    public const string InstallerFolder = "installer";
    public const string PortableFolder = "portable";
    public const string PortableExeName = "Remindly.exe";

    public static string RepoUrl => $"https://github.com/{RepoOwner}/{RepoName}";
    public static string ReleasesUrl => RepoUrl + "/releases";
    public static string ReleasesApiUrl => $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases?per_page=40";
    public static string ReleasesAtomUrl => ReleasesUrl + ".atom";

    public static bool IsWindowsTag(string? tag) => !string.IsNullOrWhiteSpace(tag) &&
        (Regex.IsMatch(tag.Trim(), @"^win-v\d+\.\d+(?:\.\d+)?(?:-beta(?:[0-9]{3}|(?:\.\d+)*))?$", RegexOptions.IgnoreCase) ||
         Regex.IsMatch(tag.Trim(), @"^v\d+\.\d+\.\d+(?:-beta(?:[0-9]{3}|\.\d+\.\d+))?$", RegexOptions.IgnoreCase));

    /// <summary>"win-v2.11.0" / "2.11" / "v2.11.1-beta" → 2.11.0 / 2.11.0 / 2.11.1 (always three parts).</summary>
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var m = Regex.Match(tag, @"(\d+)(?:\.(\d+))?(?:\.(\d+))?");
        if (!m.Success) return null;
        int P(int g) => m.Groups[g].Success ? int.Parse(m.Groups[g].Value) : 0;
        return new Version(P(1), P(2), P(3));
    }

    public static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    public static bool IsNewer(Version current, Version candidate) => Normalize(candidate) > Normalize(current);

    /// <summary>Compare the whole version, including every numeric beta identifier (PR and run numbers).</summary>
    public static int CompareVersions(string left, string right)
    {
        var coreLeft = Normalize(ParseVersion(left) ?? new Version(0, 0, 0));
        var coreRight = Normalize(ParseVersion(right) ?? new Version(0, 0, 0));
        var core = coreLeft.CompareTo(coreRight);
        if (core != 0) return core;

        var leftBeta = left.Contains("-beta", StringComparison.OrdinalIgnoreCase);
        var rightBeta = right.Contains("-beta", StringComparison.OrdinalIgnoreCase);
        if (leftBeta != rightBeta) return leftBeta ? -1 : 1;
        if (!leftBeta) return 0;

        // Three-digit beta names are a new, later release generation. Retain all
        // historical PR/run tags for exact rollback, but rank a short beta above
        // those old labels for the same core version.
        static bool ShortBeta(string version) => Regex.IsMatch(version,
            @"-beta[0-9]{3}(?:\+.*)?$", RegexOptions.IgnoreCase);
        var leftShort = ShortBeta(left);
        var rightShort = ShortBeta(right);
        if (leftShort != rightShort) return leftShort ? 1 : -1;

        static string[] Ids(string label)
        {
            var index = label.IndexOf("-beta", StringComparison.OrdinalIgnoreCase);
            var suffix = label[(index + 5)..].Split('+')[0].TrimStart('.');
            return suffix.Length == 0 ? [] : suffix.Split('.');
        }

        var l = Ids(left);
        var r = Ids(right);
        for (int i = 0; i < Math.Min(l.Length, r.Length); i++)
        {
            var leftNumeric = l[i].Length > 0 && l[i].All(char.IsDigit);
            var rightNumeric = r[i].Length > 0 && r[i].All(char.IsDigit);
            if (leftNumeric != rightNumeric) return leftNumeric ? -1 : 1;
            if (leftNumeric)
            {
                var x = l[i].TrimStart('0');
                var y = r[i].TrimStart('0');
                var numberOrder = x.Length.CompareTo(y.Length);
                if (numberOrder != 0) return numberOrder;
                numberOrder = string.CompareOrdinal(x, y);
                if (numberOrder != 0) return numberOrder;
            }
            else
            {
                var order = string.Compare(l[i], r[i], StringComparison.OrdinalIgnoreCase);
                if (order != 0) return order;
            }
        }
        return l.Length.CompareTo(r.Length);
    }

    public static bool IsNewer(string current, ReleaseInfo candidate) =>
        CompareVersions(candidate.DisplayVersion, current) > 0;

    /// <summary>Prevent legacy portable builds from silently undoing a chosen downgrade.</summary>
    public static bool RequiresLegacyAutoCheckOff(string current, string target, bool portableMode) =>
        portableMode && CompareVersions(target, current) < 0;

    public static string ZipName(Version v) => $"Remindly-Windows-{v.ToString(3)}.zip";

    public static string SetupName(Version v) => $"Remindly-Setup-{v.ToString(3)}.exe";

    /// <summary>Parses GitHub's "list releases" JSON; only published Windows releases are kept.</summary>
    public static List<ReleaseInfo> ParseReleaseList(string json)
    {
        var result = new List<ReleaseInfo>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
        foreach (var r in doc.RootElement.EnumerateArray())
            if (ParseRelease(r) is ReleaseInfo info) result.Add(info);
        return result;
    }

    public static ReleaseInfo? ParseRelease(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (tag == null || !IsWindowsTag(tag)) return null;
        if (root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                long size = a.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : 0;
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(url)) assets.Add(new ReleaseAsset(name, url, size));
            }
        }
        string? S(string p) => root.TryGetProperty(p, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        return new ReleaseInfo(ParseVersion(tag)!, tag, S("name") ?? tag, S("html_url") ?? ReleasesUrl, S("body"),
            root.TryGetProperty("prerelease", out var pr) && pr.ValueKind == JsonValueKind.True, assets);
    }

    /// <summary>The newest non-prerelease Windows release, or null.</summary>
    public static ReleaseInfo? PickLatest(IEnumerable<ReleaseInfo> releases, bool includeBeta = false) =>
        releases.Where(r => (includeBeta || !r.IsBeta) && IsWindowsTag(r.TagName))
            .Where(r => PickZip(r) is ReleaseAsset zip && PickChecksum(r, zip) != null)
            .OrderByDescending(r => r.DisplayVersion, Comparer<string>.Create(CompareVersions))
            .ThenByDescending(r => r.TagName.StartsWith("win-v", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();

    /// <summary>
    /// Exact-version picker. Includes only releases with an installable ZIP and checksum.
    /// An older release is offered for explicit rollback, never for automatic installation.
    /// </summary>
    public static IReadOnlyList<ReleaseInfo> SelectVersions(IEnumerable<ReleaseInfo> releases, string current, bool older, bool includeBeta = true) =>
        releases.Where(r => (includeBeta || !r.IsBeta) && IsWindowsTag(r.TagName))
            .Where(r => PickZip(r) is ReleaseAsset zip && PickChecksum(r, zip) != null)
            .Where(r => older
                ? CompareVersions(r.DisplayVersion, current) < 0
                : CompareVersions(r.DisplayVersion, current) > 0)
            .OrderByDescending(r => r.DisplayVersion, Comparer<string>.Create(CompareVersions))
            .ThenByDescending(r => r.TagName.StartsWith("win-v", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    /// <summary>Fallback without the API (rate limit): the releases Atom feed lists the recent tags.</summary>
    public static List<string> ParseAtomTags(string atom)
    {
        var tags = new List<string>();
        foreach (Match m in Regex.Matches(atom ?? "", @"/releases/tag/([^""<>\s?#]+)"))
        {
            var tag = Uri.UnescapeDataString(m.Groups[1].Value);
            if (IsWindowsTag(tag) && !tags.Contains(tag)) tags.Add(tag);
        }
        return tags;
    }

    /// <summary>A release built from its tag alone (Atom fallback): the asset URLs follow GitHub's download pattern.</summary>
    public static ReleaseInfo? ReleaseFromTag(string tag)
    {
        if (!IsWindowsTag(tag)) return null;
        var v = ParseVersion(tag)!;
        string Dl(string name) => $"{RepoUrl}/releases/download/{Uri.EscapeDataString(tag)}/{name}";
        // The Atom feed exposes tags, not asset names. Support both the current
        // unified archive and historical Windows-only archives; DownloadAsync
        // probes their checksums and installs only a verified existing asset.
        var display = tag.StartsWith("win-v", StringComparison.OrdinalIgnoreCase) ? tag[5..] : tag.TrimStart('v');
        var unified = $"Remindly_{display}.zip";
        var legacy = ZipName(v);
        return new ReleaseInfo(v, tag, tag, $"{RepoUrl}/releases/tag/{Uri.EscapeDataString(tag)}", null, tag.Contains("-beta", StringComparison.OrdinalIgnoreCase),
            new List<ReleaseAsset>
            {
                new(unified, Dl(unified), 0), new(unified + ".sha256", Dl(unified + ".sha256"), 0),
                new(legacy, Dl(legacy), 0), new(legacy + ".sha256", Dl(legacy + ".sha256"), 0)
            });
    }

    public static ReleaseAsset? PickZip(ReleaseInfo r) =>
        r.Assets.FirstOrDefault(a => a.Name.Equals($"Remindly_{r.DisplayVersion}.zip", StringComparison.OrdinalIgnoreCase))
        ?? r.Assets.FirstOrDefault(a => a.Name.Equals(ZipName(r.Version), StringComparison.OrdinalIgnoreCase))
        ?? r.Assets.FirstOrDefault(a => a.Name.StartsWith("Remindly-Windows", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

    public static ReleaseAsset? PickChecksum(ReleaseInfo r, ReleaseAsset asset) =>
        r.Assets.FirstOrDefault(a => a.Name.Equals(asset.Name + ".sha256", StringComparison.OrdinalIgnoreCase));

    /// <summary>The first 64-hex token of a checksum file ("ABC…", "abc…  file.zip", PowerShell output…).</summary>
    public static string? ParseSha256(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var token in text.Split(new[] { ' ', '\t', '\r', '\n', '*', '=' }, StringSplitOptions.RemoveEmptyEntries))
            if (token.Length == 64 && token.All(Uri.IsHexDigit)) return token.ToLowerInvariant();
        return null;
    }

    /// <summary>
    /// The deployment zip holds ONLY the folders "installer" and "portable" (no other folder, no loose
    /// files at the root). Returns the problems found (empty = valid).
    /// </summary>
    public static List<string> ValidateZipLayout(IEnumerable<string> entryNames)
    {
        var names = entryNames.Select(x => x.Replace('\\', '/')).ToList();
        if (names.Any(x => x.StartsWith("windows-x64/", StringComparison.Ordinal)))
        {
            var layoutProblems = new List<string>();
            var portableEntry = names.FirstOrDefault(x => Regex.IsMatch(x, @"^portable/Remindly_\d+\.\d+\.\d+(?:-beta\.\d+\.\d+)?\.exe$"));
            if (portableEntry == null) return new() { "portable/Remindly_<version>.exe is missing" };
            var file = portableEntry["portable/".Length..];
            var allowed = new HashSet<string> { portableEntry, $"windows-x64/{file}", $"Android/{file[..^4]}.apk", "macOS/" };
            if (names.Distinct().Count() != names.Count || !allowed.SetEquals(names))
                layoutProblems.Add("The unified ZIP must contain matching portable, windows-x64 and Android builds, and an empty macOS/ folder.");
            return layoutProblems;
        }
        var problems = new List<string>();
        bool setup = false, portable = false;
        foreach (var raw in entryNames)
        {
            var e = raw.Replace('\\', '/');
            if (e.EndsWith('/')) e = e.TrimEnd('/') + "/";
            var slash = e.IndexOf('/');
            if (slash < 0) { problems.Add($"file at the zip root: {e}"); continue; }
            var top = e[..slash];
            if (top != InstallerFolder && top != PortableFolder) { problems.Add($"unexpected folder: {top}"); continue; }
            var rest = e[(slash + 1)..];
            if (rest.Contains('/')) { problems.Add($"nested folder inside {top}: {e}"); continue; }
            if (top == InstallerFolder && rest.StartsWith("Remindly-Setup-", StringComparison.OrdinalIgnoreCase) && rest.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) setup = true;
            if (top == PortableFolder && rest.Equals(PortableExeName, StringComparison.OrdinalIgnoreCase)) portable = true;
        }
        if (!setup) problems.Add("installer/Remindly-Setup-<version>.exe is missing");
        if (!portable) problems.Add("portable/Remindly.exe is missing");
        return problems.Distinct().ToList();
    }

    /// <summary>The zip entry an update needs: the Setup for an installed copy, the exe for a portable one.</summary>
    public static string? PickZipEntry(IEnumerable<string> entryNames, bool installedMode)
    {
        var unified = entryNames.FirstOrDefault(x => Regex.IsMatch(x.Replace('\\', '/'),
            installedMode ? @"^windows-x64/Remindly_[^/]+\.exe$" : @"^portable/Remindly_[^/]+\.exe$"));
        if (unified != null) return unified;
        foreach (var raw in entryNames)
        {
            var e = raw.Replace('\\', '/');
            if (installedMode && e.StartsWith(InstallerFolder + "/Remindly-Setup-", StringComparison.OrdinalIgnoreCase) && e.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return raw;
            if (!installedMode && e.Equals(PortableFolder + "/" + PortableExeName, StringComparison.OrdinalIgnoreCase)) return raw;
        }
        return null;
    }

    /// <summary>
    /// The "Auto update" rule: check once a day, the first time on the day AFTER the last check
    /// (calendar day, local time). Never checked yet → not due (the first check happens tomorrow).
    /// </summary>
    public static bool IsCheckDue(long lastCheckMs, long nowMs, bool autoUpdate, TimeZoneInfo? zone = null)
    {
        if (!autoUpdate || lastCheckMs <= 0) return false;
        return Clock.LocalDate(nowMs, zone) > Clock.LocalDate(lastCheckMs, zone);
    }

    /// <summary>When the next automatic check will happen (local midnight after the last check).</summary>
    public static long NextCheckAt(long lastCheckMs, long nowMs, TimeZoneInfo? zone = null) =>
        Clock.StartOfNextDay(lastCheckMs > 0 ? lastCheckMs : nowMs, zone);

    public static string Describe(Version current, Version latest) => $"{current.ToString(3)} → {latest.ToString(3)}";
}
