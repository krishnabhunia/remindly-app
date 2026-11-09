using Remindly.Core;
using Remindly.Core.Sync;
using Xunit;

namespace Remindly.Core.Tests;

public class DesktopLabelsTests
{
    [Theory]
    [InlineData(CloudStatus.UpToDate, "Up to date")]
    [InlineData(CloudStatus.Syncing, "Syncing…")]
    [InlineData(CloudStatus.Connecting, "Connecting…")]
    [InlineData(CloudStatus.Paused, "Paused")]
    [InlineData(CloudStatus.Error, "Sync problem")]
    [InlineData(CloudStatus.SignedOut, "Signed out")]
    public void Sync_status_uses_plain_words(CloudStatus status, string expected) =>
        Assert.Equal(expected, SyncStatusText.Label(status));

    [Fact]
    public void Every_sync_status_has_a_label_without_code_words()
    {
        foreach (var status in Enum.GetValues<CloudStatus>())
        {
            var label = SyncStatusText.Label(status);
            Assert.False(string.IsNullOrWhiteSpace(label));
            // No joined-up code words such as "UpToDate" or "SignedOut".
            Assert.DoesNotMatch("[a-z][A-Z]", label);
        }
        Assert.Equal("Signed out", SyncStatusText.Label(null));
    }

    [Theory]
    [InlineData(CloudStatus.UpToDate, SyncTone.Good)]
    [InlineData(CloudStatus.Syncing, SyncTone.Busy)]
    [InlineData(CloudStatus.Connecting, SyncTone.Busy)]
    [InlineData(CloudStatus.Error, SyncTone.Problem)]
    [InlineData(CloudStatus.Paused, SyncTone.Idle)]
    [InlineData(CloudStatus.SignedOut, SyncTone.Idle)]
    public void Sync_status_maps_to_a_dot_tone(CloudStatus status, SyncTone tone) =>
        Assert.Equal(tone, SyncStatusText.Tone(status));

    [Fact]
    public void Icon_picker_offers_presets_and_selects_default_for_a_list_without_icon()
    {
        Assert.Same(TaskListIcons.Presets, TaskListIcons.Choices(null));
        Assert.Same(TaskListIcons.Presets, TaskListIcons.Choices("  "));
        Assert.Equal(TaskListIcons.Default, TaskListIcons.Selected(null));
        Assert.Equal("💼", TaskListIcons.Selected(" 💼 "));
        Assert.Equal(TaskListIcons.Presets.Count, TaskListIcons.Presets.Distinct().Count());
    }

    [Fact]
    public void Custom_icon_from_the_phone_is_offered_first_and_kept()
    {
        var choices = TaskListIcons.Choices("🦄");
        Assert.Equal("🦄", choices[0]);
        Assert.Equal(TaskListIcons.Presets.Count + 1, choices.Count);
        Assert.Equal("🦄", TaskListIcons.ToStored("🦄", "🦄"));
    }

    [Fact]
    public void Saving_an_unchanged_icon_never_rewrites_the_stored_value()
    {
        Assert.Null(TaskListIcons.ToStored(TaskListIcons.Default, null));
        Assert.Equal("", TaskListIcons.ToStored(TaskListIcons.Default, ""));
        Assert.Equal("📋", TaskListIcons.ToStored("📋", "📋"));
        Assert.Equal(" 💼", TaskListIcons.ToStored("💼", " 💼"));
    }

    [Fact]
    public void Picking_a_different_icon_stores_it()
    {
        Assert.Equal("🛒", TaskListIcons.ToStored("🛒", null));
        Assert.Equal("📋", TaskListIcons.ToStored("📋", "💼"));
        Assert.Equal("🎓", TaskListIcons.ToStored("🎓", "🦄"));
    }
}
