namespace WindowsIsland.Core;

/// <summary>Friendly icon names mapped to Segoe Fluent Icons / MDL2 glyphs.</summary>
public static class Icons
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bell"] = "",
        ["music"] = "",
        ["volume"] = "",
        ["mute"] = "",
        ["battery"] = "",
        ["charging"] = "",
        ["mail"] = "",
        ["chat"] = "",
        ["call"] = "",
        ["download"] = "",
        ["upload"] = "",
        ["timer"] = "",
        ["clock"] = "",
        ["calendar"] = "",
        ["check"] = "",
        ["error"] = "",
        ["warning"] = "",
        ["info"] = "",
        ["code"] = "",
        ["sync"] = "",
        ["heart"] = "",
        ["star"] = "",
        ["mic"] = "",
        ["camera"] = "",
        ["location"] = "",
        ["wifi"] = "",
        ["bluetooth"] = "",
        ["folder"] = "",
        ["play"] = "",
        ["pause"] = "",
        ["next"] = "",
        ["previous"] = "",
        // Not in Segoe Fluent Icons: rendered through the Segoe UI Symbol fallback of the icon font.
        ["claude"] = "✻",
    };

    public const string Play = "";
    public const string Pause = "";

    public static string Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Map["bell"];
        if (Map.TryGetValue(name.Trim(), out var glyph))
            return glyph;
        // Allow a raw glyph / single character; anything longer falls back to the bell.
        return name.Length <= 2 ? name : Map["bell"];
    }
}
