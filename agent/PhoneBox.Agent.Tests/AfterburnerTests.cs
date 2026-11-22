using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>MSI Afterburner's shared memory as the sensor source: parsing, keys, live pushed values.</summary>
public sealed class AfterburnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"phonebox-ab-{Guid.NewGuid():N}");
    public AfterburnerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // the layout Afterburner 4.6 writes: a 32-byte header (version 2.0) and 1324-byte entries
    private static byte[] Memory(params (string Name, string Units, float Value, uint Gpu)[] entries)
    {
        const int header = 32, entry = 1324;
        var mem = new byte[header + entries.Length * entry];
        BitConverter.GetBytes(0x4D41484Du).CopyTo(mem, 0);
        BitConverter.GetBytes(0x20000u).CopyTo(mem, 4);
        BitConverter.GetBytes((uint)header).CopyTo(mem, 8);
        BitConverter.GetBytes((uint)entries.Length).CopyTo(mem, 12);
        BitConverter.GetBytes((uint)entry).CopyTo(mem, 16);
        for (var i = 0; i < entries.Length; i++)
        {
            var at = header + i * entry;
            Encoding.Latin1.GetBytes(entries[i].Name).CopyTo(mem, at);
            Encoding.Latin1.GetBytes(entries[i].Units).CopyTo(mem, at + 260);
            BitConverter.GetBytes(entries[i].Value).CopyTo(mem, at + 1300);
            BitConverter.GetBytes(entries[i].Gpu).CopyTo(mem, at + 1316);
        }
        return mem;
    }

    [Fact]
    public void Reads_the_sensors_with_their_units_and_gpu()
    {
        var r = AfterburnerMonitor.Parse(Memory(
            ("Framerate", "FPS", float.MaxValue, AfterburnerMonitor.NoGpu),
            ("CPU temperature", "°C", 41f, AfterburnerMonitor.NoGpu),
            ("GPU temperature", "°C", 54f, 0)))!;
        Assert.Equal(3, r.Count);
        Assert.Equal("CPU temperature", r[1].Name);
        Assert.Equal("°C", r[1].Units);
        Assert.Equal(41f, r[1].Value);
        Assert.Equal(0u, r[2].Gpu);
        Assert.False(AfterburnerMonitor.Valid(r[0].Value)); // FPS without a game
    }

    [Fact]
    public void Memory_that_is_not_afterburners_reads_as_nothing()
    {
        var mem = Memory(("CPU temperature", "°C", 41f, AfterburnerMonitor.NoGpu));
        BitConverter.GetBytes(0xDEADu).CopyTo(mem, 0); // what Afterburner leaves when it exits
        Assert.Null(AfterburnerMonitor.Parse(mem));
        Assert.Null(AfterburnerMonitor.Parse(new byte[8]));
    }

    [Fact]
    public void Keys_are_slugs_and_a_second_gpu_is_marked()
    {
        AfterburnerMonitor.Reading R(string n, uint gpu = AfterburnerMonitor.NoGpu) => new(n, "", 1, gpu);
        Assert.Equal("ab.cpu-temperature", AfterburnerMonitor.Key(R("CPU temperature")));
        Assert.Equal("ab.gpu-temperature", AfterburnerMonitor.Key(R("GPU temperature", 0)));
        Assert.Equal("ab.gpu-temperature.gpu2", AfterburnerMonitor.Key(R("GPU temperature", 1)));
        Assert.Equal("ab.cpu1-usage", AfterburnerMonitor.Key(R(" CPU1 usage ")));
        Assert.Null(AfterburnerMonitor.Key(R("°")));
    }

    [Fact]
    public void Values_are_rounded_to_what_a_widget_shows()
    {
        Assert.Equal(144, AfterburnerMonitor.Round(143.7f));
        Assert.Equal(6.9, AfterburnerMonitor.Round(6.94f));
        Assert.Equal(0.75, AfterburnerMonitor.Round(0.7512f));
    }

    [Fact]
    public void Live_values_are_listed_but_never_saved()
    {
        var path = Path.Combine(_dir, "data.json");
        using (var push = new PushStore(NullLogger<PushStore>.Instance, path))
        {
            Assert.Null(push.Set("mine", JsonSerializer.SerializeToElement(1), null));
            var v0 = push.Version;
            push.SetLive("ab.framerate", JsonSerializer.SerializeToElement(144));
            Assert.True(push.Version > v0);
            Assert.Equal(144, push.Get("ab.framerate")!.Data.GetInt32());
            Assert.Contains("mine", push.Snapshot().Keys);
        }
        var saved = File.ReadAllText(path);
        Assert.Contains("mine", saved);
        Assert.DoesNotContain("ab.framerate", saved);

        using var again = new PushStore(NullLogger<PushStore>.Instance, path);
        again.SetLive("ab.framerate", JsonSerializer.SerializeToElement(60));
        again.RemoveLive("ab.framerate");
        Assert.Null(again.Get("ab.framerate"));
    }

    [Fact]
    public void The_source_setting_defaults_to_lhm_and_accepts_afterburner()
    {
        ConfigStore Store(string json)
        {
            var p = Path.Combine(_dir, $"c-{Guid.NewGuid():N}.json");
            File.WriteAllText(p, json);
            return new ConfigStore(p);
        }
        Assert.Equal(SensorsConfig.Lhm, Store("{}").Get().Sensors.Source);
        Assert.Equal(SensorsConfig.Lhm, Store("{\"sensors\":{\"source\":\"nonsense\"}}").Get().Sensors.Source);
        Assert.Equal(SensorsConfig.Lhm, Store("{\"sensors\":null}").Get().Sensors.Source);
        Assert.Equal(SensorsConfig.Afterburner, Store("{\"sensors\":{\"source\":\"Afterburner\"}}").Get().Sensors.Source);
    }

    [Fact]
    public void The_slowest_frame_of_the_second_is_its_own_value()
    {
        AfterburnerMonitor.Reading R(string n, float v) => new(n, "ms", v, AfterburnerMonitor.NoGpu);
        IReadOnlyList<AfterburnerMonitor.Reading> game = [R("Framerate", 144), R("Frametime", 6.9f)];
        Assert.Equal(6.9f, AfterburnerMonitor.Frametime(game));
        Assert.Equal(0f, AfterburnerMonitor.Frametime([R("Frametime", 0)]));          // no game
        Assert.Equal(0f, AfterburnerMonitor.Frametime([R("Frametime", float.MaxValue)]));

        var withMax = AfterburnerMonitor.WithFrametimeMax(game, 41.5f);
        Assert.Equal("ab.frametime-max", AfterburnerMonitor.Key(withMax[^1]));
        Assert.Equal(41.5f, withMax[^1].Value);
        Assert.Same(game, AfterburnerMonitor.WithFrametimeMax(game, 0)); // nothing measured: no value
    }

    [Fact]
    public void The_list_of_sensors_to_send_takes_keys_with_or_without_the_prefix()
    {
        Assert.Null(AfterburnerMonitor.AllowedKeys(""));
        Assert.Null(AfterburnerMonitor.AllowedKeys(" , "));
        var keys = AfterburnerMonitor.AllowedKeys("framerate, ab.Frametime-Max;gpu-temperature\nfan-tachometer")!;
        Assert.Equal(["ab.fan-tachometer", "ab.framerate", "ab.frametime-max", "ab.gpu-temperature"], keys.Order());
    }

    [Fact]
    public void The_read_interval_is_kept_within_bounds()
    {
        SensorsConfig Read(string json)
        {
            var p = Path.Combine(_dir, $"p-{Guid.NewGuid():N}.json");
            File.WriteAllText(p, json);
            return new ConfigStore(p).Get().Sensors;
        }
        Assert.Equal(1000, Read("{}").AfterburnerPollMs);
        Assert.Equal(250, Read("{\"sensors\":{\"afterburnerPollMs\":250}}").AfterburnerPollMs);
        Assert.Equal(100, Read("{\"sensors\":{\"afterburnerPollMs\":10}}").AfterburnerPollMs);
        Assert.Equal(2000, Read("{\"sensors\":{\"afterburnerPollMs\":60000}}").AfterburnerPollMs);
        Assert.Equal(1000, Read("{\"sensors\":{\"afterburnerPollMs\":0}}").AfterburnerPollMs);
    }

    [Fact]
    public async Task Not_selected_it_reads_nothing()
    {
        var p = Path.Combine(_dir, "lhm.json");
        File.WriteAllText(p, "{}");
        using var push = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_dir, "d.json"));
        var ab = new AfterburnerMonitor(push, NullLogger<AfterburnerMonitor>.Instance, new ConfigStore(p));
        Assert.False(ab.Selected);
        using var cts = new CancellationTokenSource();
        await ab.StartAsync(cts.Token);
        await Task.Delay(200);
        Assert.Null(ab.CpuTemp);
        Assert.DoesNotContain(push.Snapshot().Keys, k => k.StartsWith(AfterburnerMonitor.KeyPrefix));
        await cts.CancelAsync();
        await ab.StopAsync(CancellationToken.None);
    }
}
