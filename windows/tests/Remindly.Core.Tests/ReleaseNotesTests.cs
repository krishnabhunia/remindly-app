using Remindly.Core.Updates;
using Xunit;

namespace Remindly.Core.Tests;

public sealed class ReleaseNotesTests
{
    [Fact]
    public void Structured_release_contains_actual_features()
    {
        var body = """
            ## What's New in Remindly 2.17.0-beta001
            - **Windows UI (PR #21):** **Account status:** Shows the signed-in account and sync errors in Settings.
            - **Fixed (PR #21):** **Version selection:** Avoids installing a release without confirmation.
            """;
        var notes = ReleaseNotes.Parse("2.17.0-beta001", body);
        Assert.Equal(2, notes.Count);
        Assert.Contains(notes, n => n.Title == "Account status" && n.Detail.Contains("signed-in"));
        Assert.Contains(notes, n => n.Title == "Version selection");
    }

    [Fact]
    public void Existing_sync_release_has_real_details_despite_sparse_github_body()
    {
        var notes = ReleaseNotes.Parse("2.16.0", "Download Remindly_2.16.0.zip. Stable release.");
        Assert.Contains(notes, n => n.Title.Contains("Google sign-in"));
        Assert.Contains(notes, n => n.Title.Contains("cloud sync"));
        Assert.Contains(notes, n => n.Category == "Limitation");
    }

    [Fact]
    public void Missing_notes_are_explicit_not_invented()
    {
        Assert.Empty(ReleaseNotes.Parse("2.17.0", null));
    }
}
