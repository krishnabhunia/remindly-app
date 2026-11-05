using Remindly.Core;
using Remindly.Core.Updates;
using Xunit;

namespace Remindly.Core.Tests;

public class UpdateLogicTests
{
    [Theory]
    [InlineData("win-v2.11.0", true)]
    [InlineData("WIN-V3.0.1", true)]
    [InlineData("v2.17.0-beta001", true)]
    [InlineData("win-v2.17.0-beta999", true)]
    [InlineData("v2.17.0-beta0001", false)]
    [InlineData("v2.11", false)]     // Android release in the same repository
    [InlineData("mac-v1.0.0", false)]
    [InlineData("win-vX", false)]
    [InlineData(null, false)]
    public void Only_windows_tags_count(string? tag, bool expected) => Assert.Equal(expected, UpdateLogic.IsWindowsTag(tag));

    [Fact]
    public void Versions_parse_to_three_parts_and_compare()
    {
        Assert.Equal(new Version(2, 11, 0), UpdateLogic.ParseVersion("win-v2.11"));
        Assert.Equal(new Version(2, 11, 1), UpdateLogic.ParseVersion("win-v2.11.1-beta"));
        Assert.True(UpdateLogic.IsNewer(new Version(2, 11, 0, 0), new Version(2, 11, 1)));
        Assert.False(UpdateLogic.IsNewer(new Version(2, 11, 0, 0), new Version(2, 11, 0)));
    }

    private const string ListJson = """
    [
      {"tag_name":"v2.12","name":"Remindly 2.12","draft":false,"prerelease":false,"assets":[{"name":"Remindly-2.12.apk","browser_download_url":"https://x/apk","size":1}]},
      {"tag_name":"win-v2.12.0","name":"Remindly 2.12.0 for Windows","draft":false,"prerelease":false,"html_url":"https://github.com/krishnabhunia/remindly-app/releases/tag/win-v2.12.0",
       "assets":[{"name":"Remindly-Windows-2.12.0.zip","browser_download_url":"https://x/zip","size":123},{"name":"Remindly-Windows-2.12.0.zip.sha256","browser_download_url":"https://x/sha","size":64}]},
      {"tag_name":"win-v2.13.0","draft":true,"prerelease":false,"assets":[]},
      {"tag_name":"win-v2.14.0","draft":false,"prerelease":true,"assets":[]},
      {"tag_name":"win-v2.11.0","draft":false,"prerelease":false,"assets":[]}
    ]
    """;

    [Fact]
    public void Latest_skips_android_drafts_and_prereleases()
    {
        var latest = UpdateLogic.PickLatest(UpdateLogic.ParseReleaseList(ListJson))!;
        Assert.Equal("win-v2.12.0", latest.TagName);
        var zip = UpdateLogic.PickZip(latest)!;
        Assert.Equal("Remindly-Windows-2.12.0.zip", zip.Name);
        Assert.Equal("https://x/sha", UpdateLogic.PickChecksum(latest, zip)!.DownloadUrl);
    }

    [Fact]
    public void Atom_fallback_finds_windows_tags()
    {
        const string atom = """
            <feed><entry><link rel="alternate" type="text/html" href="https://github.com/krishnabhunia/remindly-app/releases/tag/v2.12"/></entry>
            <entry><link rel="alternate" type="text/html" href="https://github.com/krishnabhunia/remindly-app/releases/tag/win-v2.12.0"/></entry></feed>
            """;
        var tags = UpdateLogic.ParseAtomTags(atom);
        Assert.Equal(new[] { "win-v2.12.0" }, tags.ToArray());
        var r = UpdateLogic.ReleaseFromTag(tags[0])!;
        Assert.Equal("https://github.com/krishnabhunia/remindly-app/releases/download/win-v2.12.0/Remindly_2.12.0.zip", UpdateLogic.PickZip(r)!.DownloadUrl);
        Assert.Contains(r.Assets, asset => asset.Name == "Remindly-Windows-2.12.0.zip");
        Assert.Contains(r.Assets, asset => asset.Name == "Remindly-Windows-2.12.0.zip.sha256");
    }

    [Fact]
    public void Checksum_parsing()
    {
        var h = new string('a', 64);
        Assert.Equal(h, UpdateLogic.ParseSha256(h.ToUpperInvariant() + "\r\n"));
        Assert.Equal(h, UpdateLogic.ParseSha256($"{h}  Remindly-Windows-2.12.0.zip"));
        Assert.Null(UpdateLogic.ParseSha256("nope"));
    }

    [Fact]
    public void Zip_layout_allows_only_installer_and_portable()
    {
        var good = new[] { "installer/", "installer/Remindly-Setup-2.12.0.exe", "portable/", "portable/Remindly.exe" };
        Assert.Empty(UpdateLogic.ValidateZipLayout(good));
        Assert.Equal("installer/Remindly-Setup-2.12.0.exe", UpdateLogic.PickZipEntry(good, installedMode: true));
        Assert.Equal("portable/Remindly.exe", UpdateLogic.PickZipEntry(good, installedMode: false));

        var bad = good.Append("docs/readme.txt").Append("README.md").Append("portable/x/y.dll");
        var problems = UpdateLogic.ValidateZipLayout(bad);
        Assert.Contains(problems, p => p.Contains("unexpected folder: docs"));
        Assert.Contains(problems, p => p.Contains("file at the zip root"));
        Assert.Contains(problems, p => p.Contains("nested folder"));
        Assert.Contains("portable/Remindly.exe is missing", UpdateLogic.ValidateZipLayout(new[] { "installer/Remindly-Setup-1.0.0.exe" }));
    }

    [Fact]
    public void Version_picker_filters_exact_newer_and_older_verified_releases()
    {
        ReleaseInfo Release(string tag, bool beta = false, bool checksum = true)
        {
            var version = UpdateLogic.ParseVersion(tag)!;
            var display = tag.StartsWith("win-v") ? tag[5..] : tag[1..];
            var zip = $"Remindly_{display}.zip";
            var assets = new List<ReleaseAsset> { new(zip, "https://example.test/" + zip, 100) };
            if (checksum) assets.Add(new(zip + ".sha256", "https://example.test/" + zip + ".sha256", 64));
            return new ReleaseInfo(version, tag, tag, "", "", beta, assets);
        }

        var releases = new[]
        {
            Release("win-v2.15.0"),
            Release("win-v2.16.0-beta.2", beta: true),
            Release("win-v2.14.0"),
            Release("win-v2.13.2"),
            Release("win-v2.12.0", checksum: false),
        };
        var updates = UpdateLogic.SelectVersions(releases, "2.14.0", older: false);
        Assert.Equal(new[] { "2.16.0-beta.2", "2.15.0" }, updates.Select(r => r.DisplayVersion));
        var stableUpdates = UpdateLogic.SelectVersions(releases, "2.14.0", older: false, includeBeta: false);
        Assert.Equal(new[] { "2.15.0" }, stableUpdates.Select(r => r.DisplayVersion));
        var older = UpdateLogic.SelectVersions(releases, "2.14.0", older: true);
        Assert.Equal(new[] { "2.13.2" }, older.Select(r => r.DisplayVersion));
    }

    [Fact]
    public void Incomplete_newest_release_never_hides_a_verified_older_release()
    {
        var goodTag = "win-v2.14.0";
        var good = new ReleaseInfo(new Version(2, 14, 0), goodTag, goodTag, "", null, false,
            [new("Remindly_2.14.0.zip", "https://example.test/zip", 100),
             new("Remindly_2.14.0.zip.sha256", "https://example.test/sha", 64)]);
        var incomplete = new ReleaseInfo(new Version(2, 15, 0), "win-v2.15.0", "Incomplete", "", null, false, []);
        Assert.Equal(goodTag, UpdateLogic.PickLatest([incomplete, good])!.TagName);
    }

    [Fact]
    public void Multi_part_beta_identifiers_sort_by_pr_then_run_not_only_last_number()
    {
        Assert.True(UpdateLogic.CompareVersions("2.15.0-beta.105.20.1", "2.15.0-beta.104.999.1") > 0);
        Assert.True(UpdateLogic.CompareVersions("2.15.0-beta.105.21.1", "2.15.0-beta.105.20.1") > 0);
        Assert.True(UpdateLogic.CompareVersions("2.15.0", "2.15.0-beta.105.21.1") > 0);
        Assert.True(UpdateLogic.CompareVersions("2.15.0-beta.105.21.1", "2.15.0") < 0);

        ReleaseInfo Release(string version)
        {
            var tag = "win-v" + version;
            var zip = "Remindly_" + version + ".zip";
            return new ReleaseInfo(UpdateLogic.ParseVersion(tag)!, tag, tag, "", null, true,
                [new(zip, "https://example.test/zip", 100), new(zip + ".sha256", "https://example.test/sha", 64)]);
        }

        var releases = new[] { Release("2.15.0-beta.104.999.1"), Release("2.15.0-beta.105.21.1"), Release("2.15.0-beta.105.20.1") };
        Assert.Equal("2.15.0-beta.105.21.1", UpdateLogic.PickLatest(releases, includeBeta: true)!.DisplayVersion);
        Assert.Equal(new[] { "2.15.0-beta.105.21.1", "2.15.0-beta.105.20.1" },
            UpdateLogic.SelectVersions(releases, "2.15.0-beta.104.999.1", older: false).Select(r => r.DisplayVersion));
    }

    [Fact]
    public void Three_digit_betas_sort_in_sequence_and_after_legacy_betas()
    {
        Assert.True(UpdateLogic.CompareVersions("2.17.0-beta002", "2.17.0-beta001") > 0);
        Assert.True(UpdateLogic.CompareVersions("2.17.0-beta001", "2.17.0-beta.18.29") > 0);
        Assert.True(UpdateLogic.CompareVersions("2.17.0-beta.18.29", "2.17.0-beta001") < 0);
        Assert.True(UpdateLogic.CompareVersions("2.17.0", "2.17.0-beta999") > 0);
        Assert.True(UpdateLogic.ReleaseFromTag("v2.17.0-beta003")!.IsBeta);
    }

    [Theory]
    [InlineData("2.15.0", "2.14.0", true, true)]
    [InlineData("2.15.0-beta.105.3.1", "2.15.0-beta.104.4.1", true, true)]
    [InlineData("2.15.0", "2.16.0", true, false)]
    [InlineData("2.15.0", "2.14.0", false, false)]
    public void Only_portable_rollbacks_require_disabling_legacy_auto_checks(
        string current, string target, bool portable, bool expected) =>
        Assert.Equal(expected, UpdateLogic.RequiresLegacyAutoCheckOff(current, target, portable));

    [Fact]
    public void Rollback_never_qualifies_for_newer_update_banner()
    {
        var older = UpdateLogic.ReleaseFromTag("win-v2.14.0")!;
        var newer = UpdateLogic.ReleaseFromTag("win-v2.16.0")!;
        Assert.False(UpdateLogic.IsNewer("2.15.0", older));
        Assert.True(UpdateLogic.IsNewer("2.15.0", newer));
    }

    [Fact]
    public void Atom_fallback_builds_checksum_backed_version_choices()
    {
        const string feed = "<feed><entry><link href=\"https://github.com/krishnabhunia/remindly-app/releases/tag/win-v2.14.0\"/></entry></feed>";
        var versions = UpdateLogic.ParseAtomTags(feed)
            .Select(UpdateLogic.ReleaseFromTag)
            .OfType<ReleaseInfo>()
            .ToArray();
        Assert.Single(versions);
        Assert.Single(UpdateLogic.SelectVersions(versions, "2.15.0", older: true));
        Assert.NotNull(UpdateLogic.PickChecksum(versions[0], UpdateLogic.PickZip(versions[0])!));
    }

    [Fact]
    public void Auto_update_checks_once_on_the_next_day()
    {
        long last = T.At(2026, 9, 29, 8);
        Assert.False(UpdateLogic.IsCheckDue(last, T.At(2026, 9, 29, 23, 59), true, T.Zone));   // same day: no
        Assert.True(UpdateLogic.IsCheckDue(last, T.At(2026, 9, 30, 0, 1), true, T.Zone));      // next day: yes
        Assert.False(UpdateLogic.IsCheckDue(last, T.At(2026, 10, 5), false, T.Zone));          // box unticked: never
        Assert.False(UpdateLogic.IsCheckDue(0, T.At(2026, 10, 5), true, T.Zone));              // first run: tomorrow
        Assert.Equal(T.At(2026, 9, 30), UpdateLogic.NextCheckAt(last, last, T.Zone));
    }

    [Fact]
    public void The_assembly_carries_the_directory_build_props_version()
    {
        var v = UpdateService.CurrentVersion;
        Assert.True(v.Major >= 2, $"version {v}");
        Assert.Equal(-1, v.Revision);
    }
}
