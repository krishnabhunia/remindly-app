using Remindly.Core.Sync;

namespace Remindly.Core;

/// <summary>How the header chip colours its status dot.</summary>
public enum SyncTone { Good, Busy, Idle, Problem }

/// <summary>Plain words for the sync state; the enum names (e.g. UpToDate) are never shown to people.</summary>
public static class SyncStatusText
{
    public static string Label(CloudStatus? status) => status switch
    {
        CloudStatus.UpToDate => "Up to date",
        CloudStatus.Syncing => "Syncing…",
        CloudStatus.Connecting => "Connecting…",
        CloudStatus.Paused => "Paused",
        CloudStatus.Error => "Sync problem",
        _ => "Signed out",
    };

    public static SyncTone Tone(CloudStatus? status) => status switch
    {
        CloudStatus.UpToDate => SyncTone.Good,
        CloudStatus.Syncing or CloudStatus.Connecting => SyncTone.Busy,
        CloudStatus.Error => SyncTone.Problem,
        _ => SyncTone.Idle,
    };
}

/// <summary>
/// The task-list icon picker. Icons are the same emoji text the Android app stores in TaskList.Icon,
/// so the Firestore contract is unchanged. A custom icon set on the phone is offered first and kept.
/// </summary>
public static class TaskListIcons
{
    public const string Default = "📋";

    public static readonly IReadOnlyList<string> Presets = new[]
    {
        "📋", "💼", "🏠", "🛒", "💰", "🧾", "📅", "🎓", "💡", "❤️", "✈️", "🔧",
    };

    public static string? Normalize(string? icon) => string.IsNullOrWhiteSpace(icon) ? null : icon.Trim();

    /// <summary>What the picker shows as selected: the stored icon, or the default for a list without one.</summary>
    public static string Selected(string? stored) => Normalize(stored) ?? Default;

    /// <summary>The presets, with a custom stored icon placed first so it can stay selected.</summary>
    public static IReadOnlyList<string> Choices(string? stored)
    {
        var current = Normalize(stored);
        return current == null || Presets.Contains(current) ? Presets : new[] { current }.Concat(Presets).ToList();
    }

    /// <summary>
    /// The value to save. Keeping the selection leaves the stored value exactly as it was (no sync change);
    /// picking the default for a list that had no icon keeps it empty, as the phone writes it.
    /// </summary>
    public static string? ToStored(string chosen, string? original)
    {
        var pick = Normalize(chosen);
        if (pick == Selected(original)) return original;
        return pick;
    }
}
