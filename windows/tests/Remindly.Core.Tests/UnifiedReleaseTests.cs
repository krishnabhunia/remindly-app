using Remindly.Core.Updates;
using Xunit;

namespace Remindly.Core.Tests;

public class UnifiedReleaseTests
{
    private static ReleaseInfo Release(string version, bool beta = false) => new(
        UpdateLogic.ParseVersion(version)!, "v" + version, "Remindly", "https://github.com", null, beta,
        new() { new($"Remindly_{version}.zip", "https://github.com/build.zip", 10),
                new($"Remindly_{version}.zip.sha256", "https://github.com/build.zip.sha256", 64) });

    [Fact]
    public void StableDefaultRejectsBeta_andOptInOrdersBuildsAndStablePromotion()
    {
        var stable = Release("2.12.0");
        var beta = Release("2.13.0-beta007", true);
        Assert.Equal(stable, UpdateLogic.PickLatest(new[] { stable, beta }));
        Assert.Equal(beta, UpdateLogic.PickLatest(new[] { stable, beta }, includeBeta: true));
        Assert.True(UpdateLogic.IsNewer("2.12.0-beta007", stable));
        Assert.False(UpdateLogic.IsNewer("2.13.0", beta));
        Assert.True(UpdateLogic.IsNewer("2.13.0-beta006", beta));
        Assert.True(UpdateLogic.IsNewer("2.13.0-beta.7.20", beta)); // an old long beta of the same version
        Assert.False(UpdateLogic.IsNewer("2.13.0-beta007", beta));
    }

    [Fact]
    public void UnifiedZipValidatesExactNamesAndSelectsTheCorrectWindowsMode()
    {
        string file = "Remindly_2.12.0-beta001";
        string[] entries = { $"portable/{file}.exe", $"windows-x64/{file}.exe", $"Android/{file}.apk", "macOS/" };
        Assert.Empty(UpdateLogic.ValidateZipLayout(entries));
        Assert.Equal(entries[0], UpdateLogic.PickZipEntry(entries, installedMode: false));
        Assert.Equal(entries[1], UpdateLogic.PickZipEntry(entries, installedMode: true));
        Assert.NotEmpty(UpdateLogic.ValidateZipLayout(entries.Append("macOS/fake.dmg")));
        Assert.NotEmpty(UpdateLogic.ValidateZipLayout(entries.Append(entries[0])));
        Assert.NotEmpty(UpdateLogic.ValidateZipLayout(entries.Where(x => !x.StartsWith("Android/"))));
        Assert.Equal("Remindly_2.12.0.zip", UpdateLogic.PickZip(Release("2.12.0"))!.Name);
    }

    [Fact]
    public void OldLongBetaZipsStillValidate()
    {
        string file = "Remindly_2.12.0-beta.7.20";
        Assert.Empty(UpdateLogic.ValidateZipLayout(new[] { $"portable/{file}.exe", $"windows-x64/{file}.exe", $"Android/{file}.apk", "macOS/" }));
        Assert.NotEmpty(UpdateLogic.ValidateZipLayout(new[] { "portable/Remindly_2.12.0-beta.7.exe", "windows-x64/Remindly_2.12.0-beta.7.exe", "Android/Remindly_2.12.0-beta.7.apk", "macOS/" }));
    }

    [Theory]
    [InlineData("v2.17.0-beta001")]
    [InlineData("v2.12.0-beta.7.20")]
    public void UnifiedAtomFallbackUsesZipAndPreservesBetaFlag(string tag)
    {
        Assert.True(UpdateLogic.IsWindowsTag("v2.12.0"));
        Assert.False(UpdateLogic.IsWindowsTag("v2.12"));
        var release = UpdateLogic.ReleaseFromTag(tag)!;
        Assert.True(release.IsBeta);
        Assert.Equal($"Remindly_{tag[1..]}.zip", UpdateLogic.PickZip(release)!.Name);
        Assert.Null(UpdateLogic.PickLatest(new[] { release }));
    }

    [Theory]
    [InlineData("v2.17.0-beta001", true)]
    [InlineData("v2.17.0-beta999", true)]
    [InlineData("v2.16.0-beta.18.29", true)]
    [InlineData("v2.17.0-beta.007", false)]   // leading zeros are not valid in a numeric SemVer identifier
    [InlineData("v2.17.0-beta01", false)]
    [InlineData("v2.17.0-beta0001", false)]
    [InlineData("beta", false)]               // the beta feed pointer release
    public void UnifiedTagsAcceptShortAndOldLongBetas(string tag, bool expected) =>
        Assert.Equal(expected, UpdateLogic.IsWindowsTag(tag));

    [Fact]
    public void ShortBetasRankAboveOldLongBetasAndBelowStable()
    {
        Assert.True(UpdateLogic.CompareVersions("2.16.0-beta001", "2.16.0-beta.18.29") > 0);
        Assert.True(UpdateLogic.CompareVersions("2.16.0-beta.18.29", "2.16.0-beta001") < 0);
        Assert.True(UpdateLogic.CompareVersions("2.16.0-beta010", "2.16.0-beta009") > 0);
        Assert.True(UpdateLogic.CompareVersions("2.16.0-beta100", "2.16.0-beta099") > 0);
        Assert.Equal(0, UpdateLogic.CompareVersions("2.16.0-beta007", "2.16.0-beta007"));
        Assert.True(UpdateLogic.CompareVersions("2.16.0", "2.16.0-beta999") > 0);
        Assert.True(UpdateLogic.CompareVersions("2.17.0-beta001", "2.16.0") > 0);
        Assert.True(UpdateLogic.CompareVersions("2.16.1-beta.1.1", "2.16.0-beta999") > 0);

        var releases = new[] { Release("2.16.0-beta.18.29", true), Release("2.16.0-beta002", true), Release("2.16.0-beta001", true), Release("2.15.1") };
        Assert.Equal("2.16.0-beta002", UpdateLogic.PickLatest(releases, includeBeta: true)!.DisplayVersion);
        Assert.Equal(new[] { "2.16.0-beta002", "2.16.0-beta001", "2.16.0-beta.18.29", "2.15.1" },
            UpdateLogic.SelectVersions(releases, "2.15.0", older: false).Select(r => r.DisplayVersion));
        Assert.Equal(new[] { "2.16.0-beta.18.29", "2.15.1" },
            UpdateLogic.SelectVersions(releases, "2.16.0-beta001", older: true).Select(r => r.DisplayVersion));
    }

    [Fact]
    public void BetaDetectionLooksForAnyBetaSuffix()
    {
        Assert.True(UpdateLogic.IsBetaVersion("2.17.0-beta001"));
        Assert.True(UpdateLogic.IsBetaVersion("2.16.0-beta.18.29"));
        Assert.False(UpdateLogic.IsBetaVersion("2.17.0"));
        Assert.Equal(7, UpdateLogic.ShortBetaNumber("2.17.0-beta007"));
        Assert.Equal(7, UpdateLogic.ShortBetaNumber("2.17.0-beta007+4e68c2a"));
        Assert.Null(UpdateLogic.ShortBetaNumber("2.16.0-beta.18.29"));
        Assert.Null(UpdateLogic.ShortBetaNumber("2.17.0"));
        Assert.True(Release("2.17.0-beta001").IsBeta); // even when GitHub's prerelease flag is missing
    }
}
