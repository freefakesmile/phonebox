using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PhoneBox.Agent;

/// <summary>
/// Talks to the running adb server over its own socket (127.0.0.1:5037), so the frequent calls of
/// <see cref="AdbReverse"/> (devices every 4 s, "reverse --list" per phone, the key and version reads) do not start an
/// adb.exe process each time. Starting a process costs about 20 ms of CPU on Windows; twice every 4 s that was ~1% of a
/// core for nothing. Only the calls whose answer is the same as the command line's are handled here; anything else, or
/// any failure (no server running yet, an odd answer), returns null and the caller runs adb.exe as before, which also
/// starts the server when it is not running.
/// </summary>
internal static class AdbServer
{
    /// <summary>The server's port; null turns the socket path off (the unit tests do that, so they never reach a real adb).</summary>
    internal static int? Port { get; set; } = DefaultPort();

    private static int? DefaultPort()
    {
        // ADB_SERVER_SOCKET points adb somewhere else entirely: leave that to adb.exe.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ADB_SERVER_SOCKET"))) return null;
        var env = Environment.GetEnvironmentVariable("ANDROID_ADB_SERVER_PORT");
        return int.TryParse(env, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is > 0 and < 65536 ? p : 5037;
    }

    /// <summary>The socket request for an adb command line, or null when the command line is not one handled here.</summary>
    internal static Request? Parse(string args, bool anyExit)
    {
        var a = args.Trim();
        if (a == "devices") return new(null, "host:devices", "List of devices attached\n");
        if (a == "devices -l") return new(null, "host:devices-l", "List of devices attached\n");
        if (!a.StartsWith("-s ", StringComparison.Ordinal)) return null;
        var rest = a[3..].TrimStart();
        var space = rest.IndexOf(' ');
        if (space <= 0) return null;
        var serial = rest[..space];
        var command = rest[(space + 1)..].TrimStart();
        if (command == "reverse --list") return new(serial, "reverse:list-forward", "");
        // "shell" over the socket has no exit code (and mixes stderr into stdout), so only the calls that ignore it.
        if (anyExit && command.StartsWith("shell ", StringComparison.Ordinal) && command.Length > 6)
            return new(serial, "shell:" + command[6..].TrimStart(), "", Raw: true);
        return null;
    }

    internal sealed record Request(string? Serial, string Service, string Prefix, bool Raw = false);

    /// <summary>The command's output as adb.exe would print it, or null to run adb.exe instead.</summary>
    internal static async Task<string?> TryRunAsync(string args, bool anyExit, TimeSpan timeout, CancellationToken ct)
    {
        if (Port is not { } port || Parse(args, anyExit) is not { } req) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
            client.NoDelay = true;
            var s = client.GetStream();
            if (req.Serial is { } serial)
            {
                await SendAsync(s, "host:transport:" + serial, deadline.Token);
                if (!await OkayAsync(s, deadline.Token)) return null; // e.g. "device not found": let adb.exe report it
            }
            await SendAsync(s, req.Service, deadline.Token);
            if (!await OkayAsync(s, deadline.Token)) return null;
            // host:devices and reverse:list-forward answer OKAY + a hex-length frame; shell streams until it closes.
            var body = req.Raw ? await ReadToEndAsync(s, deadline.Token) : await ReadFramedAsync(s, deadline.Token);
            return req.Prefix + body;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; } // timed out
        catch (Exception ex) when (ex is SocketException or IOException or FormatException) { return null; }
    }

    private static async Task SendAsync(NetworkStream s, string request, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(request);
        var frame = Encoding.ASCII.GetBytes(payload.Length.ToString("x4", CultureInfo.InvariantCulture));
        await s.WriteAsync(frame, ct);
        await s.WriteAsync(payload, ct);
    }

    private static async Task<bool> OkayAsync(NetworkStream s, CancellationToken ct)
    {
        var status = await ReadExactAsync(s, 4, ct);
        return status is not null && Encoding.ASCII.GetString(status) == "OKAY";
    }

    private static async Task<string> ReadFramedAsync(NetworkStream s, CancellationToken ct)
    {
        var head = await ReadExactAsync(s, 4, ct);
        if (head is null) return ""; // closed after OKAY: nothing to list
        var len = int.Parse(Encoding.ASCII.GetString(head), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var body = await ReadExactAsync(s, len, ct) ?? throw new IOException("adb server closed the connection");
        return Encoding.UTF8.GetString(body);
    }

    private static async Task<string> ReadToEndAsync(NetworkStream s, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Exactly <paramref name="count"/> bytes, or null when the stream ends before the first one.</summary>
    private static async Task<byte[]?> ReadExactAsync(NetworkStream s, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await s.ReadAsync(buf.AsMemory(read), ct);
            if (n == 0) return read == 0 ? null : throw new IOException("adb server closed the connection");
            read += n;
        }
        return buf;
    }
}
