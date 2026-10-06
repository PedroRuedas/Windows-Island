using System.Windows.Media;

namespace WindowsIsland.Core;

public sealed record MediaInfo(
    string Title,
    string Artist,
    string Album,
    ImageSource? Artwork,
    Color Accent,
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan Duration,
    DateTimeOffset PositionUpdatedAt,
    string SourceApp)
{
    /// <summary>Position extrapolated to "now" while playing.</summary>
    public TimeSpan CurrentPosition
    {
        get
        {
            var position = Position;
            if (IsPlaying)
                position += DateTimeOffset.Now - PositionUpdatedAt;
            if (position < TimeSpan.Zero)
                return TimeSpan.Zero;
            return Duration > TimeSpan.Zero && position > Duration ? Duration : position;
        }
    }
}
