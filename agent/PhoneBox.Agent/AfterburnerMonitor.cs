using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>
/// MSI Afterburner's monitoring data, read from the shared memory it publishes ("MAHMSharedMemory"; no rights needed).
/// With Settings › General › Features › Sensors set to Afterburner, every sensor it monitors (FPS and frametime through
/// RTSS, temperatures, loads, power, clocks …) becomes a live pushed value "ab.&lt;sensor&gt;" for the Pushed value
/// widget, and its CPU temperature replaces LibreHardwareMonitor's. Live values are never written to disk.
/// </summary>
public sealed class AfterburnerMonitor(PushStore push, ILogger<AfterburnerMonitor> log, ConfigStore? config = null) : BackgroundService
{
    public const string KeyPrefix = "ab.";
    private const string MapName = "MAHMSharedMemory";
    private const uint Signature = 0x4D41484D; // 'MAHM'
    private const int MaxEntries = 512;
    // MAHM_SHARED_MEMORY_ENTRY: five 260-byte strings (name, units, localized name, localized units, format), then
    // data, min and max as floats, flags, GPU index and source id
    private const int NameAt = 0, UnitsAt = 260, DataAt = 1300, GpuAt = 1316, MinEntrySize = 1320;
    internal const uint NoGpu = uint.MaxValue;

    internal sealed record Reading(string Name, string Units, float Value, uint Gpu);

    private volatile IReadOnlyList<Reading>? _last;
    private long _lastAt;
    private readonly Dictionary<string, double> _sent = [];

    internal bool Selected => config?.Get().Sensors.Source == SensorsConfig.Afterburner;
    private int PollMs => config?.Get().Sensors.AfterburnerPollMs ?? SensorsConfig.DefaultPollMs;
    internal const string FrametimeMaxName = "Frametime max"; // published as ab.frametime-max
    private string? _allowedText;
    private HashSet<string>? _allowed;

    /// <summary>"framerate, ab.frametime-max" → {"ab.framerate", "ab.frametime-max"}; null (everything) when empty.</summary>
    internal static HashSet<string>? AllowedKeys(string? text)
    {
        var keys = (text ?? "").Split([',', ';', ' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(k => k.Trim().ToLowerInvariant())
            .Select(k => k.StartsWith(KeyPrefix) ? k : KeyPrefix + k)
            .ToHashSet();
        return keys.Count == 0 ? null : keys;
    }

    private HashSet<string>? Allowed()
    {
        var text = config?.Get().Sensors.AfterburnerKeys ?? "";
        if (text != _allowedText) { _allowedText = text; _allowed = AllowedKeys(text); }
        return _allowed;
    }

    /// <summary>Afterburner's "CPU temperature", or null when it is not selected, not running or not monitoring it.</summary>
    public double? CpuTemp
    {
        get
        {
            if (_last is not { } r || Environment.TickCount64 - Interlocked.Read(ref _lastAt) > 5000) return null;
            foreach (var x in r)
                if (x.Name.Equals("CPU temperature", StringComparison.OrdinalIgnoreCase) && Valid(x.Value)) return Math.Round(x.Value, 1);
            return null;
        }
    }

    // How often the shared memory is looked at. Only a new Afterburner sample (its readings changed) is processed, so the
    // agent follows Afterburner's own polling period instead of beating against it with a timer of its own.
    private const int CheckMs = 100;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // A new sample is published at most every PollMs; with an interval under a second the stream is poked, so the
        // phone gets it right away and the panel's charts move at Afterburner's rate. ab.frametime-max is the slowest
        // frame seen in the last second, so its meaning doesn't depend on the interval.
        var recent = new Queue<(long At, float Ms)>();
        ulong? lastSample = null;
        long lastPublish = long.MinValue / 2;
        IReadOnlyList<Reading>? unsent = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var delay = CheckMs;
                if (!Selected) { Reset(); delay = 2000; }
                else
                {
                    try
                    {
                        var now = Environment.TickCount64;
                        var poll = PollMs;
                        if (Fingerprint() is not { } sample) { Reset(); delay = 10_000; } // not running: look again now and then
                        else
                        {
                            Interlocked.Exchange(ref _lastAt, now); // running: the last readings are still current
                            if (sample != lastSample && ReadShared() is { } readings)
                            {
                                lastSample = sample;
                                _last = readings;
                                if (Frametime(readings) is var ft and > 0) recent.Enqueue((now, ft));
                                unsent = readings;
                            }
                            while (recent.Count > 0 && now - recent.Peek().At > 1000) recent.Dequeue();
                            if (unsent is not null && now - lastPublish >= poll - CheckMs / 2)
                            {
                                var changed = Publish(WithFrametimeMax(unsent, recent.Count > 0 ? recent.Max(x => x.Ms) : 0));
                                if (changed && poll < 1000) StateSignal.Poke();
                                unsent = null;
                                lastPublish = now;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        log.LogDebug("afterburner: {Msg}", ex.Message);
                        Reset();
                        delay = 10_000;
                    }
                }
                await Task.Delay(delay, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { Reset(); }

        void Reset()
        {
            Clear();
            CloseMap();
            recent.Clear();
            lastSample = null;
            unsent = null;
        }
    }

    /// <summary>Afterburner's current frametime (ms), 0 without a game.</summary>
    internal static float Frametime(IReadOnlyList<Reading> readings)
    {
        foreach (var r in readings)
            if (r.Name.Equals("Frametime", StringComparison.OrdinalIgnoreCase) && Valid(r.Value) && r.Value > 0) return r.Value;
        return 0;
    }

    internal static IReadOnlyList<Reading> WithFrametimeMax(IReadOnlyList<Reading> readings, float max) =>
        max > 0 ? [.. readings, new Reading(FrametimeMaxName, "ms", max, NoGpu)] : readings;

    /// <returns>Whether a value changed (the phone has something new to fetch).</returns>
    private bool Publish(IReadOnlyList<Reading> readings)
    {
        var changed = false;
        var allowed = Allowed();
        var current = new HashSet<string>();
        foreach (var r in readings)
        {
            // no data (FPS without a game) counts as absent, and so does a sensor that isn't on the list: dropped below
            if (!Valid(r.Value) || Key(r) is not { } key || allowed?.Contains(key) == false || !current.Add(key)) continue;
            var v = Round(r.Value);
            if (_sent.TryGetValue(key, out var old) && old == v) continue; // unchanged: the phone needn't refetch
            _sent[key] = v;
            push.SetLive(key, JsonSerializer.SerializeToElement(v));
            changed = true;
        }
        foreach (var key in _sent.Keys.Where(k => !current.Contains(k)).ToList())
        {
            _sent.Remove(key);
            push.RemoveLive(key);
            changed = true;
        }
        return changed;
    }

    private void Clear()
    {
        _last = null;
        if (_sent.Count == 0) return;
        foreach (var key in _sent.Keys) push.RemoveLive(key);
        _sent.Clear();
    }

    /// <summary>"CPU temperature" → "ab.cpu-temperature"; a sensor of a second GPU gets ".gpu2".</summary>
    internal static string? Key(Reading r)
    {
        var sb = new StringBuilder(KeyPrefix);
        var dash = false;
        foreach (var ch in r.Name.ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') { sb.Append(ch); dash = false; }
            else if (!dash && sb.Length > KeyPrefix.Length) { sb.Append('-'); dash = true; }
        }
        while (sb.Length > KeyPrefix.Length && sb[^1] == '-') sb.Length--;
        if (sb.Length == KeyPrefix.Length) return null;
        if (r.Gpu != NoGpu && r.Gpu > 0) sb.Append(".gpu").Append(r.Gpu + 1);
        var key = sb.ToString();
        return key.Length <= 64 ? key : null;
    }

    // Afterburner reports a sensor without data (FPS with no game running) as FLT_MAX
    internal static bool Valid(float v) => float.IsFinite(v) && Math.Abs(v) < 1e30f;

    /// <summary>Whole numbers from 20 up (FPS, °C, W, MHz), one decimal below (frametime), two below 1 (volts).</summary>
    internal static double Round(float v) => Math.Abs(v) >= 20 ? Math.Round(v) : Math.Abs(v) >= 1 ? Math.Round(v, 1) : Math.Round(v, 2);

    private MemoryMappedFile? _map;
    private MemoryMappedViewAccessor? _view;
    private byte[] _buf = [];

    /// <summary>The shared memory, opened once and kept open (null while Afterburner isn't running).</summary>
    private MemoryMappedViewAccessor? View()
    {
        if (_view is not null) return _view;
        if (!OperatingSystem.IsWindows()) return null;
        try { _map = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read); }
        catch (FileNotFoundException) { return null; }
        _view = _map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        return _view;
    }

    private void CloseMap()
    {
        _view?.Dispose(); _view = null;
        _map?.Dispose(); _map = null;
    }

    /// <summary>
    /// A hash of every sensor's value (no copy, nothing allocated): it changes when Afterburner writes a new sample. Null
    /// when the memory isn't Afterburner's (it exited: the next look opens it again).
    /// </summary>
    private ulong? Fingerprint()
    {
        if (View() is not { } v || Layout(v) is not { } layout) { CloseMap(); return null; }
        var (headerSize, count, entrySize) = layout;
        var h = 14695981039346656037UL;
        for (var i = 0; i < count; i++)
        {
            var at = headerSize + (long)i * entrySize + DataAt;
            h = (h ^ v.ReadUInt64(at)) * 1099511628211UL;      // data and min
            h = (h ^ v.ReadUInt64(at + 8)) * 1099511628211UL;  // max and flags
        }
        return h;
    }

    /// <summary>The header's sizes, or default when the memory isn't (or no longer is) a whole Afterburner block.</summary>
    private static (int HeaderSize, int Count, int EntrySize)? Layout(MemoryMappedViewAccessor v)
    {
        if (v.Capacity < 20 || v.ReadUInt32(0) != Signature) return null; // 0xDEAD: Afterburner exited
        var headerSize = (int)v.ReadUInt32(8);
        var count = (int)v.ReadUInt32(12);
        var entrySize = (int)v.ReadUInt32(16);
        if (headerSize is < 20 or > 4096 || count is < 0 or > MaxEntries || entrySize is < MinEntrySize or > 8192) return null;
        return headerSize + (long)count * entrySize <= v.Capacity ? (headerSize, count, entrySize) : null;
    }

    private IReadOnlyList<Reading>? ReadShared()
    {
        if (View() is not { } v || Layout(v) is not { } layout) return null;
        var (headerSize, count, entrySize) = layout;
        var length = headerSize + count * entrySize;
        if (_buf.Length < length) _buf = new byte[length];
        v.ReadArray(0, _buf, 0, length);
        return Parse(_buf.AsSpan(0, length));
    }

    /// <summary>The sensors in a copy of the shared memory, or null when it is not (or no longer) Afterburner's.</summary>
    internal static IReadOnlyList<Reading>? Parse(ReadOnlySpan<byte> mem)
    {
        if (mem.Length < 20 || BitConverter.ToUInt32(mem[..4]) != Signature) return null; // 0xDEAD: Afterburner exited
        var headerSize = (int)BitConverter.ToUInt32(mem[8..12]);
        var count = BitConverter.ToUInt32(mem[12..16]);
        var entrySize = (int)BitConverter.ToUInt32(mem[16..20]);
        if (headerSize < 20 || count > MaxEntries || entrySize < MinEntrySize) return null;
        var list = new List<Reading>((int)count);
        for (var i = 0; i < count; i++)
        {
            var at = headerSize + i * entrySize;
            if (at + entrySize > mem.Length) break;
            var e = mem.Slice(at, entrySize);
            list.Add(new Reading(Text(e.Slice(NameAt, 260)), Text(e.Slice(UnitsAt, 260)),
                BitConverter.ToSingle(e.Slice(DataAt, 4)), BitConverter.ToUInt32(e.Slice(GpuAt, 4))));
        }
        return list;
    }

    // ANSI strings; Latin-1 keeps the degree sign of "°C"
    private static string Text(ReadOnlySpan<byte> s)
    {
        var end = s.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? s : s[..end]).Trim();
    }
}
