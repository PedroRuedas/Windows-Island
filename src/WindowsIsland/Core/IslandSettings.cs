using System.IO;
using System.Text.Json;
using System.Windows.Media;

namespace WindowsIsland.Core;

/// <summary>A named border gradient. The island itself always stays black; only its rim is colored.</summary>
public sealed record BorderPreset(string Id, string Name, string[] Colors);

/// <summary>User preferences, persisted as JSON in %LOCALAPPDATA%\WindowsIsland\settings.json.</summary>
public sealed class IslandSettings
{
    public const string NoBorder = "none";
    public const string Custom = "custom";

    public static readonly IReadOnlyList<BorderPreset> Presets =
    [
        new(NoBorder, "Clássica", []),
        new("intelligence", "Apple Intelligence", ["#0894FF", "#C959DD", "#FF2E54", "#FF9004"]),
        new("aurora", "Aurora", ["#7F5AF0", "#2CB1FF", "#2CFF9E"]),
        new("sunset", "Pôr do sol", ["#FF5F6D", "#FFC371"]),
        new("ocean", "Oceano", ["#00C6FF", "#0072FF"]),
        new("claude", "Claude", ["#D97757", "#F2B880"]),
        new("neon", "Neon", ["#FF00CC", "#3333FF"]),
        new("mono", "Prata", ["#FFFFFF", "#6E6E73"]),
    ];

    /// <summary>Colors offered for the custom gradient.</summary>
    public static readonly IReadOnlyList<string> Palette =
    [
        "#FF453A", "#FF9F0A", "#FFD60A", "#30D158", "#64D2FF",
        "#0A84FF", "#5E5CE6", "#BF5AF2", "#FF375F", "#FFFFFF",
    ];

    public string Border { get; set; } = NoBorder;
    public string[] CustomColors { get; set; } = ["#BF5AF2", "#64D2FF"];
    public double BorderThickness { get; set; } = 2;
    public bool AnimateBorder { get; set; }
    public bool Glow { get; set; } = true;
    public bool ShowIdleClock { get; set; } = true;

    /// <summary>The gradient stops for the current choice; empty = classic hairline.</summary>
    public Color[] BorderColors()
    {
        var hex = Border == Custom ? CustomColors : Presets.FirstOrDefault(p => p.Id == Border)?.Colors ?? [];
        return hex.Select(ParseColor).ToArray();
    }

    public static Color ParseColor(string hex)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch (FormatException)
        {
            return Colors.White;
        }
    }

    // ── Persistence ──

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static string FilePath => Path.Combine(App.DataDirectory, "settings.json");

    public static IslandSettings Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<IslandSettings>(File.ReadAllText(FilePath), Json) is { } settings)
            {
                settings.CustomColors = settings.CustomColors is { Length: >= 2 } ? settings.CustomColors[..2] : ["#BF5AF2", "#64D2FF"];
                settings.BorderThickness = Math.Clamp(settings.BorderThickness, 1, 3);
                return settings;
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        return new IslandSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(App.DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }
}
