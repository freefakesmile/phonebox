using PhoneBox.Agent;
using Xunit;

/// <summary>What the USB loop writes to the log when adb fails: which call, adb's reason, and a lasting failure again.</summary>
[Collection("adb session")] // AdbServerTests points the static AdbServer.Port at a fake server
public sealed class AdbLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phonebox-adblog-" + Guid.NewGuid().ToString("N"));
    public AdbLogTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string FakeAdb(string windows, string unix)
    {
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(_dir, "adb.cmd");
            File.WriteAllText(cmd, "@echo off\r\n" + windows + "\r\n");
            return cmd;
        }
        var sh = Path.Combine(_dir, "adb");
        File.WriteAllText(sh, "#!/bin/sh\n" + unix + "\n");
        File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return sh;
    }

    [Fact]
    public async Task AFailedCallNamesTheCommandAndAdbsReason()
    {
        var adb = FakeAdb("echo error: device offline 1>&2\r\nexit /b 1", "echo 'error: device offline' >&2; exit 1");
        var ex = await Assert.ThrowsAsync<IOException>(() => AdbReverse.Adb(adb, "-s PHONE1 reverse tcp:8765 tcp:1234", default));
        Assert.Equal("adb -s PHONE1 reverse tcp:8765 tcp:1234 exited with code 1: error: device offline", ex.Message);
    }

    [Fact]
    public async Task ACallThatHangsNamesTheCommand()
    {
        var adb = FakeAdb("ping -n 30 127.0.0.1 >nul", "exec sleep 30");
        var ex = await Assert.ThrowsAsync<TimeoutException>(() => AdbReverse.Adb(adb, "-s PHONE1 shell input keyevent KEYCODE_WAKEUP", default));
        Assert.Equal("adb -s PHONE1 shell input keyevent KEYCODE_WAKEUP did not answer within 8 s", ex.Message);
    }

    [Fact]
    public void TheLoggedCommandStopsBeforeTheUsbPin()
    {
        var line = AdbReverse.Describe("-s PHONE1 shell content insert --uri content://net.oxog.phonebox.deskpanel.usb/pin --bind fp:s:123456");
        Assert.DoesNotContain("123456", line);
        Assert.Equal("-s PHONE1 shell content insert --uri …", line);
    }

    [Fact]
    public void TheAdbServerIsRestartedAfterAMinuteOfSilenceAndNotAgainRightAway()
    {
        var dog = new AdbWatchdog(silentMs: 60_000, cooldownMs: 300_000);
        Assert.False(dog.Silent(0));
        Assert.False(dog.Silent(40_000));
        Assert.True(dog.Silent(60_000));        // a minute without an answer
        Assert.False(dog.Silent(80_000));
        Assert.False(dog.Silent(200_000));      // still hung, but restarted less than 5 minutes ago
        Assert.True(dog.Silent(360_000));

        var answered = new AdbWatchdog(silentMs: 60_000, cooldownMs: 300_000);
        Assert.False(answered.Silent(0));
        answered.Answered();                    // one answer in between: the minute starts over
        Assert.False(answered.Silent(50_000));
        Assert.False(answered.Silent(100_000));
        Assert.True(answered.Silent(110_000));
    }

    [Fact]
    public void TheAdbServerProcessIsFoundInNetstatInAnyLanguage()
    {
        const string english = """

            Active Connections

              Proto  Local Address          Foreign Address        State           PID
              TCP    127.0.0.1:5037         127.0.0.1:50211        ESTABLISHED     4242
              TCP    0.0.0.0:135            0.0.0.0:0              LISTENING       1180
              TCP    127.0.0.1:5037         0.0.0.0:0              LISTENING       25760
            """;
        Assert.Equal(25760, AdbReverse.ListenerFromNetstat(english, 5037));
        Assert.Equal(1180, AdbReverse.ListenerFromNetstat(english, 135));
        Assert.Equal(3344, AdbReverse.ListenerFromNetstat("  TCP    127.0.0.1:5037         0.0.0.0:0              ABHÖREN         3344\r\n", 5037));
        Assert.Equal(77, AdbReverse.ListenerFromNetstat("  TCP    [::1]:5037             [::]:0                 LISTENING       77", 5037));
        Assert.Null(AdbReverse.ListenerFromNetstat("  TCP    127.0.0.1:15037        0.0.0.0:0              LISTENING       9", 5037));
        Assert.Null(AdbReverse.ListenerFromNetstat(english.Replace("25760", "x"), 5037));
    }

    [Fact]
    public void ALastingErrorIsLoggedAgainEveryMinuteOtherStatesOnce()
    {
        var log = new StateLog(repeatMs: 60_000);
        Assert.Equal("adb error: x", log.Next("adb error: x", repeat: true, now: 0));
        Assert.Null(log.Next("adb error: x", repeat: true, now: 20_000));
        Assert.Null(log.Next("adb error: x", repeat: true, now: 40_000));
        Assert.Equal("adb error: x (still, 4 times in 60 s)", log.Next("adb error: x", repeat: true, now: 60_000));
        Assert.Null(log.Next("adb error: x", repeat: true, now: 80_000));

        Assert.Equal("phone connected: P", log.Next("phone connected: P", repeat: false, now: 90_000));
        Assert.Null(log.Next("phone connected: P", repeat: false, now: 500_000)); // a good state is never repeated
        Assert.Equal("adb error: x", log.Next("adb error: x", repeat: true, now: 510_000)); // a new outage starts over
    }
}
