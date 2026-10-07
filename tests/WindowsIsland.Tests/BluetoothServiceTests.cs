using WindowsIsland.Services;

namespace WindowsIsland.Tests;

public sealed class BluetoothServiceTests
{
    [Theory]
    [InlineData(25, 20, 20)]
    [InlineData(21, 19, 20)]
    [InlineData(15, 10, 10)]
    [InlineData(30, 8, 10)]   // Both at once: the more urgent one wins.
    public void CrossingAThresholdWarns(int previous, int current, int threshold) =>
        Assert.Equal(threshold, BluetoothService.LowBatteryCrossed(previous, current));

    [Theory]
    [InlineData(null, 5)]     // First reading after connecting: the "connected" pill already showed the level.
    [InlineData(20, 19)]      // Already below 20 → no repeat.
    [InlineData(10, 5)]
    [InlineData(50, 40)]
    [InlineData(15, 30)]      // Charging.
    public void OtherwiseStaysQuiet(int? previous, int current) =>
        Assert.Null(BluetoothService.LowBatteryCrossed(previous, current));

    [Fact]
    public void ColorFollowsTheLevel()
    {
        Assert.NotEqual(BluetoothService.LevelColor(80), BluetoothService.LevelColor(15));
        Assert.NotEqual(BluetoothService.LevelColor(15), BluetoothService.LevelColor(5));
        Assert.Equal(BluetoothService.LevelColor(10), BluetoothService.LevelColor(1));
    }
}
