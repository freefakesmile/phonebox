using PhoneBox.Agent;
using Xunit;

/// <summary>Settings › Features switches: the adb check interval and the defaults that keep everything on.</summary>
public sealed class FeatureSwitchTests
{
    [Theory]
    [InlineData(false, 30, 4)]   // something to set up: never the long wait
    [InlineData(true, 30, 30)]
    [InlineData(true, 1, 4)]     // never faster than before
    [InlineData(true, 600, 60)]  // and never slower than a minute
    public void AdbWaitsLongOnlyWhenAllIsInPlace(bool steady, int setting, int expected) =>
        Assert.Equal(expected, AdbReverse.NextRoundSec(steady, setting));

    [Fact]
    public void EverythingIsOnByDefault()
    {
        var f = new FeaturesConfig();
        Assert.True(f.Ping && f.Privacy && f.GitHub && f.UpdateCheck);
        Assert.Equal(FeaturesConfig.DefaultAdbSec, f.AdbSteadySec);
        Assert.True(new SensorsConfig().NvidiaSmi);
    }

    [Theory]
    [InlineData("""{"features":{"adbSteadySec":0}}""", 4)]
    [InlineData("""{"features":{"adbSteadySec":15}}""", 15)]
    [InlineData("""{"features":{"adbSteadySec":999}}""", 60)]
    public void AdbIntervalIsKeptInRange(string json, int expected)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pb-features-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        try { Assert.Equal(expected, new ConfigStore(path).Get().Features.AdbSteadySec); }
        finally { File.Delete(path); File.Delete(path + ".bak"); }
    }

    [Fact]
    public void PingSwitchedOffSendsNothing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pb-ping-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"features":{"ping":false},"ping":{"host":"1.1.1.1"}}""");
        try
        {
            var monitor = new PingMonitor(new ConfigStore(path));
            monitor.Sample(12, "1.1.1.1");
            Assert.Null(monitor.Snapshot());
        }
        finally { File.Delete(path); File.Delete(path + ".bak"); }
    }
}
