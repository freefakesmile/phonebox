using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using PhoneBox.Agent;
using Xunit;

internal static class AdbServerOff
{
    /// <summary>The tests use a fake adb.exe and must never reach a real adb server running on this machine.</summary>
    [ModuleInitializer]
    internal static void Init() => AdbServer.Port = null;
}

/// <summary>AdbServer against a fake adb server on a free local port (never a real adb).</summary>
[Collection("adb session")] // shares the static AdbServer.Port with the AdbReverse tests
public sealed class AdbServerTests : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly List<string> _requests = [];

    public AdbServerTests()
    {
        _listener.Start();
        AdbServer.Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        AdbServer.Port = null;
        _listener.Stop();
    }

    private static byte[] Frame(string s) => Encoding.ASCII.GetBytes(Encoding.UTF8.GetByteCount(s).ToString("x4") + s);

    /// <summary>Serves one connection: answers each request with <paramref name="answer"/>, then closes.</summary>
    private Task Serve(Func<string, byte[]?> answer) => Task.Run(async () =>
    {
        using var c = await _listener.AcceptTcpClientAsync();
        var s = c.GetStream();
        while (true)
        {
            var head = new byte[4];
            if (await s.ReadAtLeastAsync(head, 4, throwOnEndOfStream: false) < 4) return;
            var body = new byte[Convert.ToInt32(Encoding.ASCII.GetString(head), 16)];
            await s.ReadExactlyAsync(body);
            var req = Encoding.UTF8.GetString(body);
            lock (_requests) _requests.Add(req);
            var reply = answer(req);
            if (reply is null) return; // close
            await s.WriteAsync(reply);
            if (!req.StartsWith("host:transport:")) return; // one service per connection, as adb does
        }
    });

    [Theory]
    [InlineData("devices", false, null, "host:devices")]
    [InlineData("devices -l", false, null, "host:devices-l")]
    [InlineData("-s R5CT1 reverse --list", false, "R5CT1", "reverse:list-forward")]
    [InlineData("-s 10.0.0.5:5555 shell getprop ro.build.version.sdk", true, "10.0.0.5:5555", "shell:getprop ro.build.version.sdk")]
    public void Parses_the_handled_command_lines(string args, bool anyExit, string? serial, string service)
    {
        var r = AdbServer.Parse(args, anyExit);
        Assert.NotNull(r);
        Assert.Equal(serial, r!.Serial);
        Assert.Equal(service, r.Service);
    }

    [Theory]
    [InlineData("-s R5CT1 shell input keyevent KEYCODE_WAKEUP", false)] // needs the exit code
    [InlineData("-s R5CT1 reverse tcp:8765 tcp:40000", false)]            // changes state: adb.exe reports errors
    [InlineData("start-server", false)]
    [InlineData("-s R5CT1", true)]
    public void Leaves_other_command_lines_to_adb_exe(string args, bool anyExit) => Assert.Null(AdbServer.Parse(args, anyExit));

    [Fact]
    public async Task Devices_reads_like_the_command_line()
    {
        var served = Serve(_ => [.. "OKAY"u8, .. Frame("R5CT1\tdevice\nX2\tunauthorized\n")]);
        var output = await AdbServer.TryRunAsync("devices", false, TimeSpan.FromSeconds(5), CancellationToken.None);
        await served;
        Assert.Equal("List of devices attached\nR5CT1\tdevice\nX2\tunauthorized\n", output);
    }

    [Fact]
    public async Task Reverse_list_goes_through_the_phones_transport()
    {
        var served = Serve(req => req.StartsWith("host:transport:") ? "OKAY"u8.ToArray()
            : [.. "OKAY"u8, .. Frame("UsbFfs tcp:8765 tcp:40629\n")]);
        var output = await AdbServer.TryRunAsync("-s R5CT1 reverse --list", false, TimeSpan.FromSeconds(5), CancellationToken.None);
        await served;
        Assert.Equal("UsbFfs tcp:8765 tcp:40629\n", output);
        Assert.Equal(["host:transport:R5CT1", "reverse:list-forward"], _requests);
    }

    [Fact]
    public async Task Shell_streams_until_the_phone_closes()
    {
        var served = Serve(req => req.StartsWith("host:transport:") ? "OKAY"u8.ToArray() : [.. "OKAY"u8, .. "31\n"u8]);
        var output = await AdbServer.TryRunAsync("-s R5CT1 shell getprop ro.build.version.sdk", true, TimeSpan.FromSeconds(5), CancellationToken.None);
        await served;
        Assert.Equal("31\n", output);
    }

    [Fact]
    public async Task A_refusal_falls_back_to_adb_exe()
    {
        var served = Serve(_ => [.. "FAIL"u8, .. Frame("device 'NOPE' not found")]);
        Assert.Null(await AdbServer.TryRunAsync("-s NOPE reverse --list", false, TimeSpan.FromSeconds(5), CancellationToken.None));
        await served;
    }

    [Fact]
    public async Task No_server_falls_back_to_adb_exe()
    {
        _listener.Stop(); // nothing listens on the port any more
        Assert.Null(await AdbServer.TryRunAsync("devices", false, TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    [Fact]
    public async Task A_silent_server_times_out_into_adb_exe()
    {
        var served = Task.Run(async () => { using var c = await _listener.AcceptTcpClientAsync(); await Task.Delay(1500); });
        Assert.Null(await AdbServer.TryRunAsync("devices", false, TimeSpan.FromMilliseconds(300), CancellationToken.None));
        await served;
    }
}
