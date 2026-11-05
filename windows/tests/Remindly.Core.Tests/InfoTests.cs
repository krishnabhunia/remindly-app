using Remindly.Core.Info;
using Xunit;

namespace Remindly.Core.Tests;

public class InfoTests
{
    private static readonly BuildMetadata Ci = new("20", "4e68c2a1b2c3d4e5f60718293a4b5c6d7e8f9012", "2026-11-04T14:25:00Z", "v2.17.0-beta001");
    private static readonly BuildMetadata Local = new(null, null, null, null);

    [Fact]
    public void Channel_banner_and_subtitle_follow_the_version()
    {
        Assert.Equal("Beta", BuildInfo.Channel("2.17.0-beta001"));
        Assert.Equal("Stable", BuildInfo.Channel("2.17.0"));
        Assert.Equal("2.17.0", BuildInfo.CoreVersion("2.17.0-beta001+4e68c2a"));
        Assert.Equal("Version 2.17.0-beta001 · Beta · Windows", BuildInfo.Subtitle("2.17.0-beta001"));
        Assert.Equal("This is a test build. It is beta 001 of Remindly 2.17.0, made from a pull request.", BuildInfo.BetaBanner("2.17.0-beta001"));
        Assert.Equal("This is a test build of Remindly 2.16.0, made from a pull request.", BuildInfo.BetaBanner("2.16.0-beta.18.29"));
        Assert.Null(BuildInfo.BetaBanner("2.17.0"));
    }

    [Fact]
    public void This_build_rows_for_a_ci_beta()
    {
        var rows = BuildInfo.ThisBuild("2.17.0-beta001", Ci, "installer, per-user", T.Zone);
        Assert.Equal(new[] { "Version", "Channel", "Made from", "Commit", "Built on", "GitHub tag", "Download", "Installed as" }, rows.Select(r => r.Item));
        Assert.Equal("Pull request #20", rows[2].Value);
        Assert.Equal("https://github.com/krishnabhunia/remindly-app/pull/20", rows[2].Link);
        Assert.Equal("4e68c2a", rows[3].Value);
        Assert.Equal("04-Nov-2026, 19:55", rows[4].Value); // 14:25 UTC in IST
        Assert.Equal("v2.17.0-beta001", rows[5].Value);
        Assert.Equal("Remindly_2.17.0-beta001.zip", rows[6].Value);
    }

    [Fact]
    public void Local_builds_say_so_and_stable_ci_builds_omit_the_pull_request()
    {
        var local = BuildInfo.ThisBuild("2.16.0", Local, "portable", T.Zone);
        Assert.Equal(BuildInfo.LocalBuild, local.Single(r => r.Item == "Made from").Value);
        Assert.Equal(BuildInfo.LocalBuild, local.Single(r => r.Item == "Commit").Value);
        Assert.Equal(BuildInfo.LocalBuild, local.Single(r => r.Item == "Built on").Value);
        Assert.Equal(BuildInfo.LocalBuild, local.Single(r => r.Item == "GitHub tag").Value);
        var stable = BuildInfo.ThisBuild("2.17.0", Ci with { SourcePr = null, ReleaseTag = "v2.17.0" }, "portable", T.Zone);
        Assert.DoesNotContain(stable, r => r.Item == "Made from");
    }

    [Fact]
    public void Copy_build_info_has_two_lines()
    {
        var text = BuildInfo.CopyText("2.17.0-beta001", Ci, "Windows 11 Pro 24H2 x64", "installer, per-user", ".NET 8");
        Assert.Equal("Remindly 2.17.0-beta001 (Beta) · PR #20 · 4e68c2a · built 2026-11-04 14:25 UTC" + Environment.NewLine +
                     "Windows 11 Pro 24H2 x64 · installer, per-user · .NET 8", text);
        Assert.StartsWith("Remindly 2.16.0 (Stable) · Local build", BuildInfo.CopyText("2.16.0", Local, "Windows 10 Pro 22H2 x64", "portable", ".NET 8"));
    }

    [Fact]
    public void Windows_name_and_dotnet()
    {
        Assert.Equal("Windows 11 Pro 24H2 x64", BuildInfo.WindowsName("Windows 10 Pro", "24H2", 26100, "X64"));
        Assert.Equal("Windows 10 Pro 22H2 x64", BuildInfo.WindowsName("Windows 10 Pro", "22H2", 19045, "X64"));
        Assert.Equal("Windows arm64", BuildInfo.WindowsName(null, null, 0, "Arm64"));
        Assert.Equal(".NET 8", BuildInfo.DotNetName(new Version(8, 0, 11)));
    }

    [Fact]
    public void Metadata_parsing_tolerates_bad_values()
    {
        var bad = new BuildMetadata("abc", "xyz", "not a time", null);
        Assert.Null(bad.PrNumber);
        Assert.Null(bad.ShortCommit);
        Assert.Null(bad.BuiltUtc);
        Assert.False(bad.IsLocal);
        Assert.True(Local.IsLocal);
        // The test assembly is built without CI properties: every value is empty, so it reads as a local build.
        Assert.True(BuildMetadata.FromAssembly(typeof(InfoTests).Assembly).IsLocal);
    }

    [Fact]
    public void Embedded_features_parse_and_include_the_info_window()
    {
        var rows = InfoResources.Features();
        Assert.NotEmpty(rows);
        Assert.Equal(Enumerable.Range(1, rows.Count), rows.Select(r => r.SrNo));
        var info = Assert.Single(rows, r => r.Area == "Info window");
        Assert.Matches(@"^\d+\.\d+\.\d+$", info.Since);
        Assert.Contains(FeaturesDoc.Filter(rows, null, info.Since), r => r.Area == "Info window");
        Assert.All(FeaturesDoc.Filter(rows, "SYNC"), r => Assert.True(r.Area.Contains("sync", StringComparison.OrdinalIgnoreCase) || r.WhatItDoes.Contains("sync", StringComparison.OrdinalIgnoreCase)));
        Assert.Empty(FeaturesDoc.Filter(rows, "no such feature anywhere"));
    }

    [Fact]
    public void Features_table_rules()
    {
        const string good = "Intro\n\n| SrNo. | Area | What it does | Since |\n|---|---|---|---|\n| 1 | Tasks | Add. | 2.11.0 |\n| 2 | Info | Shows. | 2.17.0 |\n";
        Assert.Equal(2, FeaturesDoc.Parse(good).Count);
        Assert.Throws<FormatException>(() => FeaturesDoc.Parse(good.Replace("| 2 |", "| 3 |")));
        Assert.Throws<FormatException>(() => FeaturesDoc.Parse(good.Replace("2.17.0", "2.17")));
        Assert.Throws<FormatException>(() => FeaturesDoc.Parse("no table"));
    }

    [Fact]
    public void Whats_new_uses_the_section_of_the_version_a_beta_tests()
    {
        const string md = "# Log\n\n## 2.17.0 — Info (05-Nov-2026)\n\n### New\n- **Info** window\n  on F1.\n  - This build\n\n## 2.11.0 — first\n- old\n";
        var s = ChangelogDoc.Section(md, BuildInfo.CoreVersion("2.17.0-beta003"))!;
        Assert.Equal("2.17.0 — Info (05-Nov-2026)", s.Title);
        Assert.DoesNotContain("old", s.Body);
        Assert.Null(ChangelogDoc.Section(md, "2.1.0"));
        var blocks = MarkdownLite.Blocks(s.Body);
        Assert.Equal(new[] { MdKind.Heading, MdKind.Bullet, MdKind.SubBullet }, blocks.Select(b => b.Kind));
        Assert.Equal("**Info** window on F1.", blocks[1].Text);
        var runs = MarkdownLite.Runs("Use **Info** or `F1` now");
        Assert.Equal(new[] { "Use ", "Info", " or ", "F1", " now" }, runs.Select(r => r.Text));
        Assert.True(runs[1].Bold);
        Assert.True(runs[3].Code);
        Assert.NotNull(InfoResources.WhatsNew("2.17.0-beta001"));
    }
}
