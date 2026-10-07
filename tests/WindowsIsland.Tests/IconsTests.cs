using WindowsIsland.Core;

namespace WindowsIsland.Tests;

public sealed class IconsTests
{
    [Theory]
    [InlineData("battery")]
    [InlineData("BATTERY")]
    [InlineData(" battery ")]
    public void NamesAreCaseAndSpaceInsensitive(string name) => Assert.Equal(Icons.Resolve("battery"), Icons.Resolve(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-such-icon")]
    public void UnknownNamesFallBackToTheBell(string? name) => Assert.Equal(Icons.Resolve("bell"), Icons.Resolve(name));

    [Fact]
    public void RawGlyphsPassThrough() => Assert.Equal("★", Icons.Resolve("★"));

    [Fact]
    public void HeadphonesHaveTheirOwnGlyph() => Assert.NotEqual(Icons.Resolve("bell"), Icons.Resolve("headphones"));
}
