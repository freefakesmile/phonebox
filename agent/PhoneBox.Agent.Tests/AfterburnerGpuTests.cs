using System.Text;
using PhoneBox.Agent;
using Xunit;

/// <summary>The GPU from Afterburner's shared memory: its name from the GPU entries, its sensors, and how it slows the OS reading.</summary>
public sealed class AfterburnerGpuTests
{
    private const uint NoGpu = AfterburnerMonitor.NoGpu;

    // what Afterburner 4.6 wrote on an RTX 3080 Ti: a 32-byte header, 1324-byte entries, then 1304-byte GPU entries
    private static byte[] Memory((string Name, string Units, float Value, uint Gpu)[] entries, params string[] gpus)
    {
        const int header = 32, entry = 1324, gpuEntry = 1304;
        var mem = new byte[header + entries.Length * entry + gpus.Length * gpuEntry];
        BitConverter.GetBytes(0x4D41484Du).CopyTo(mem, 0);
        BitConverter.GetBytes(0x20000u).CopyTo(mem, 4);
        BitConverter.GetBytes((uint)header).CopyTo(mem, 8);
        BitConverter.GetBytes((uint)entries.Length).CopyTo(mem, 12);
        BitConverter.GetBytes((uint)entry).CopyTo(mem, 16);
        BitConverter.GetBytes((uint)gpus.Length).CopyTo(mem, 24);
        BitConverter.GetBytes((uint)gpuEntry).CopyTo(mem, 28);
        for (var i = 0; i < entries.Length; i++)
        {
            var at = header + i * entry;
            Encoding.Latin1.GetBytes(entries[i].Name).CopyTo(mem, at);
            Encoding.Latin1.GetBytes(entries[i].Units).CopyTo(mem, at + 260);
            BitConverter.GetBytes(entries[i].Value).CopyTo(mem, at + 1300);
            BitConverter.GetBytes(entries[i].Gpu).CopyTo(mem, at + 1316);
        }
        for (var g = 0; g < gpus.Length; g++)
        {
            var at = header + entries.Length * entry + g * gpuEntry;
            Encoding.Latin1.GetBytes("VEN_10DE&DEV_2208").CopyTo(mem, at);
            Encoding.Latin1.GetBytes(gpus[g]).CopyTo(mem, at + 520);
            Encoding.Latin1.GetBytes("617.42").CopyTo(mem, at + 780);
        }
        return mem;
    }

    private static readonly (string, string, float, uint)[] Rtx =
    [
        ("Framerate", "FPS", float.MaxValue, NoGpu),
        ("CPU temperature", "°C", 43f, NoGpu),
        ("GPU temperature", "°C", 36f, 0),
        ("GPU usage", "%", 4f, 0),
        ("Power", "W", 44.1f, 0),
        ("Fan tachometer", "RPM", 855f, 0),
        ("Memory usage", "MB", 2336.8f, 0),
        ("Core clock", "MHz", 210f, 0),
    ];

    [Fact]
    public void TheGpuGetsItsNameDriverAndSensors()
    {
        var mem = Memory(Rtx, "NVIDIA GeForce RTX 3080 Ti");
        var gpus = AfterburnerMonitor.GpusFrom(AfterburnerMonitor.Parse(mem)!, AfterburnerMonitor.ParseGpuNames(mem))!;
        var g = Assert.Single(gpus);
        Assert.Equal("NVIDIA GeForce RTX 3080 Ti", g.Name);
        Assert.Equal("617.42", g.Driver);
        Assert.Equal("nvidia", g.Vendor);
        Assert.Equal("afterburner", g.Source);
        Assert.Equal(4, g.Util);
        Assert.Equal(36, g.Temp);
        Assert.Equal(44.1, g.Power);
        Assert.Equal(2.28, g.VramUsed);
        Assert.Equal(210, g.Clock);
        Assert.Equal(855, g.FanRpm);
        Assert.Null(g.VramTotal); // left to Windows
    }

    [Fact]
    public void WithoutGpuEntriesTheGpuIsNumbered()
    {
        var mem = Memory(Rtx);
        Assert.Empty(AfterburnerMonitor.ParseGpuNames(mem));
        Assert.Equal("GPU 1", Assert.Single(AfterburnerMonitor.GpusFrom(AfterburnerMonitor.Parse(mem)!, [])!).Name);
    }

    [Fact]
    public void OnlyCpuSensorsMeanNoGpu() =>
        Assert.Null(AfterburnerMonitor.GpusFrom([new("CPU temperature", "°C", 43f, NoGpu)], []));

    [Fact]
    public void AfterburnerWinsAndWindowsFillsTheMemorySize()
    {
        var ab = new GpuInfo("NVIDIA GeForce RTX 3080 Ti", 4, 36, 44, null, 2.3, null, null, 210, Vendor: "nvidia", Source: "afterburner");
        var os = new GpuInfo("NVIDIA GeForce RTX 3080 Ti", 9, 40, null, null, 2.5, 12, null, null, Vendor: "nvidia", Source: "windows");
        var g = Assert.Single(Gpus.Compose([ab], [os]));
        Assert.Equal(4, g.Util);
        Assert.Equal(36, g.Temp);
        Assert.Equal(12, g.VramTotal);
        Assert.Equal("afterburner+windows", g.Source);
    }

    [Fact]
    public void WindowsIsReadSlowlyOnlyWhileAfterburnerCoversEveryDiscreteGpu()
    {
        var rtx = new GpuInfo("NVIDIA GeForce RTX 3080 Ti", 1, 30, null, null, 1, 12, null, null);
        var igpu = new GpuInfo("AMD Radeon(TM) Graphics", 1, 30, null, null, 1, 1, null, null, Integrated: true);
        var other = new GpuInfo("AMD Radeon RX 7900 XTX", 1, 30, null, null, 1, 24, null, null);
        Assert.Equal(2, SystemMonitor.OsGpuSeconds([rtx], null));
        Assert.Equal(30, SystemMonitor.OsGpuSeconds([rtx, igpu], [rtx]));  // the integrated GPU doesn't keep it fast
        Assert.Equal(2, SystemMonitor.OsGpuSeconds([rtx, other], [rtx]));  // a second card Afterburner doesn't report does
        Assert.Equal(60, SystemMonitor.OsGpuSeconds([], [rtx]));
    }
}
