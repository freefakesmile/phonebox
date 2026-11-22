using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>Settings › General › Features: AI integrations and the process list can be switched off.</summary>
public sealed class FeatureToggleTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"phonebox-features-{Guid.NewGuid():N}.json");
    public void Dispose() { try { File.Delete(_path); } catch { } }

    private ConfigStore Store(string json)
    {
        File.WriteAllText(_path, json);
        return new ConfigStore(_path);
    }

    [Fact]
    public void Both_are_on_by_default_and_survive_a_null()
    {
        Assert.True(Store("{}").Get().Features.Ai);
        Assert.True(Store("{}").Get().Features.Processes);
        var c = Store("{\"features\":null}").Get();
        Assert.True(c.Features.Ai && c.Features.Processes);
    }

    [Fact]
    public void Switched_off_they_read_back_off()
    {
        var c = Store("{\"features\":{\"ai\":false,\"processes\":false}}").Get();
        Assert.False(c.Features.Ai);
        Assert.False(c.Features.Processes);
    }

    [Fact]
    public void Ai_off_hides_quotas_and_stops_their_polls()
    {
        var quotas = new QuotaMonitor(Store("{\"features\":{\"ai\":false}}"), NullLogger<QuotaMonitor>.Instance);
        Assert.False(quotas.AiEnabled());
        Assert.Empty(quotas.Snapshot());
        Assert.False(quotas.IsOverviewEnabled());
        Assert.False(quotas.IsCodexLiveEnabled());
    }

    [Fact]
    public async Task Ai_off_reads_no_sessions()
    {
        var sessions = new SessionMonitor(NullLogger<SessionMonitor>.Instance, Store("{\"features\":{\"ai\":false}}"));
        using var cts = new CancellationTokenSource();
        await sessions.StartAsync(cts.Token);
        await Task.Delay(300);
        Assert.Empty(sessions.Snapshot());
        await cts.CancelAsync();
        await sessions.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Processes_off_never_samples_even_when_asked()
    {
        var store = Store("{\"features\":{\"processes\":false}}");
        var procs = new ProcessMonitor(NullLogger<ProcessMonitor>.Instance, new SessionMonitor(NullLogger<SessionMonitor>.Instance), store);
        using var cts = new CancellationTokenSource();
        await procs.StartAsync(cts.Token);
        procs.Snapshot(); // the panel asks
        await Task.Delay(1500);
        Assert.Empty(procs.Snapshot());
        await cts.CancelAsync();
        await procs.StopAsync(CancellationToken.None);
    }
}
