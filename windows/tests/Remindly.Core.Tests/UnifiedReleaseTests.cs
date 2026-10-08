using Remindly.Core.Updates;
using Xunit;

namespace Remindly.Core.Tests;

public class UnifiedReleaseTests
{
    private static ReleaseInfo Release(string version, bool beta = false) => new(
        UpdateLogic.ParseVersion(version)!, "v" + version, "Remindly", "https://github.com", null, beta,
        new() { new($"Remindly_{version}.zip", "https://github.com/build.zip", 10) });

    [Fact]
    public void StableDefaultRejectsBeta_andOptInOrdersBuildsAndStablePromotion()
    {
        var stable = Release("2.12.0");
        var beta = Release("2.13.0-beta.7.20", true);
        Assert.Equal(stable, UpdateLogic.PickLatest(new[] { stable, beta }));
        Assert.Equal(beta, UpdateLogic.PickLatest(new[] { stable, beta }, includeBeta: true));
        Assert.True(UpdateLogic.IsNewer("2.12.0-beta.7.20", stable));
        Assert.False(UpdateLogic.IsNewer("2.13.0", beta));
        Assert.True(UpdateLogic.IsNewer("2.13.0-beta.7.19", beta));
        Assert.False(UpdateLogic.IsNewer("2.13.0-beta.7.20", beta));
    }

    [Fact]
    public void UnifiedZipValidatesExactNamesAndSelectsTheCorrectWindowsMode()
    {
        string file = "Remindly_2.12.0-beta.7.20";
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
    public void UnifiedAtomFallbackUsesZipAndPreservesBetaFlag()
    {
        Assert.True(UpdateLogic.IsWindowsTag("v2.12.0"));
        Assert.False(UpdateLogic.IsWindowsTag("v2.12"));
        var release = UpdateLogic.ReleaseFromTag("v2.12.0-beta.7.20")!;
        Assert.True(release.IsBeta);
        Assert.Equal("Remindly_2.12.0-beta.7.20.zip", UpdateLogic.PickZip(release)!.Name);
        Assert.Null(UpdateLogic.PickLatest(new[] { release }));
    }
}
