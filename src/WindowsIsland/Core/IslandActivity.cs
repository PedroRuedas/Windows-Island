using System.Windows.Media;

namespace WindowsIsland.Core;

public enum ActivityStyle
{
    /// <summary>Icon + title (+ subtitle and progress when expanded).</summary>
    Standard,

    /// <summary>Icon + level bar in compact mode (volume, brightness, ...).</summary>
    Level,
}

/// <summary>
/// Anything that wants to live in the island: a notification, a timer, a download, the volume OSD...
/// </summary>
public sealed class IslandActivity
{
    public required string Id { get; init; }
    public string Title { get; init; } = "";
    public string? Subtitle { get; init; }

    /// <summary>Small label above the title when expanded, e.g. the app that sent a notification.</summary>
    public string? Caption { get; init; }

    /// <summary>Optional picture (app logo, avatar) shown instead of <see cref="Icon"/>.</summary>
    public ImageSource? Image { get; init; }

    /// <summary>Icon name (see <see cref="Icons"/>) or a raw Segoe Fluent Icons glyph.</summary>
    public string? Icon { get; init; }

    public Color Accent { get; init; } = Colors.White;

    /// <summary>0..1, or null when the activity has no progress.</summary>
    public double? Progress { get; init; }

    /// <summary>Auto-dismiss after this time. Null keeps it until removed.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>Higher wins. System volume uses 100, API activities are clamped to 0..99.</summary>
    public int Priority { get; init; } = 50;

    /// <summary>http(s) URL opened when the user clicks the activity.</summary>
    public string? ActionUrl { get; init; }

    public ActivityStyle Style { get; init; } = ActivityStyle.Standard;

    /// <summary>Briefly expands the island when the activity first appears (like an iOS alert).</summary>
    public bool ExpandOnArrive { get; init; }

    public string Source { get; init; } = "api";

    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
