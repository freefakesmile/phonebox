using Windows.Media.Control;

namespace PhoneBox.Agent;

/// <summary>Watches and controls the current Windows media session (Spotify, browsers, ...).</summary>
public sealed class MediaMonitor(ILogger<MediaMonitor> log, ConfigStore? config = null) : BackgroundService
{
    // Windows reports a new track, a pause or a seek as an event, and the phone counts the position on by itself (AgeMs),
    // so the session is read when something happened, not every second. FallbackPoll is a safety net for a player whose
    // events go missing; for SettleMs after a new track it reads each second while its thumbnail settles.
    internal static readonly TimeSpan FallbackPoll = TimeSpan.FromSeconds(10);
    private const long SettleMs = 8000;
    // a timeline that moves the position by more than this against the count is a seek
    private const long SeekMs = 1500;
    // Readings that events ask for are at least this far apart (no more often than the old once-a-second poll), and
    // wait this long for the rest of a burst (a new track raises several events).
    private const long MinGapMs = 1000;
    private const int CoalesceMs = 250;

    private GlobalSystemMediaTransportControlsSessionManager? _mgr;
    private volatile MediaSnapshot? _snap;
    private readonly MediaArtworkCache _art = new();
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private GlobalSystemMediaTransportControlsSession? _observed;
    private long _revision;
    private long _settleUntil;
    // set by the event handlers, which never call back into the session: reading it from inside one of its events can
    // raise that event again on the same stack
    private int _full, _timeline;

    public AlbumArt? Art => _art.Current;
    public MediaSnapshot? Snapshot() => _snap;

    /// <summary>Settings › General › Features › Media: off, the agent never talks to the media session.</summary>
    internal bool Enabled => config?.Get().Features.Media != false;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            var due = true;
            long lastPoll = long.MinValue / 2;
            while (!ct.IsCancellationRequested)
            {
                if (!Enabled)
                {
                    Detach();
                    due = true;
                    await Task.Delay(2000, ct);
                    continue;
                }
                try
                {
                    if (_mgr is null)
                    {
                        _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                        _mgr.CurrentSessionChanged += SessionChanged;
                        Settle();
                        due = true;
                    }
                    if (due || Interlocked.Exchange(ref _full, 0) == 1 || (Volatile.Read(ref _timeline) == 1 && Seeked()))
                    {
                        lastPoll = Environment.TickCount64;
                        await Poll(ct);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    log.LogDebug("media: {Msg}", ex.Message);
                    _snap = null;
                }
                // what the reads above raised themselves is not news
                Interlocked.Exchange(ref _full, 0);
                Interlocked.Exchange(ref _timeline, 0);
                _wake.Wait(0);

                var settling = Environment.TickCount64 < Interlocked.Read(ref _settleUntil);
                due = !await _wake.WaitAsync(settling ? TimeSpan.FromSeconds(1) : FallbackPoll, ct);
                if (!due)
                    await Task.Delay((int)Math.Max(CoalesceMs, MinGapMs - (Environment.TickCount64 - lastPoll)), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { Detach(); }
    }

    private void Detach()
    {
        if (_mgr is not null) { _mgr.CurrentSessionChanged -= SessionChanged; _mgr = null; }
        Observe(null);
        _snap = null;
        _art.Clear();
    }

    private void Wake()
    {
        try { if (_wake.CurrentCount == 0) _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    private void Settle() => Interlocked.Exchange(ref _settleUntil, Environment.TickCount64 + SettleMs);

    private void SessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    { Interlocked.Increment(ref _revision); Settle(); Volatile.Write(ref _full, 1); Wake(); }
    private void PropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    { Interlocked.Increment(ref _revision); Settle(); Volatile.Write(ref _full, 1); Wake(); }
    private void PlaybackChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    { Volatile.Write(ref _full, 1); Wake(); }
    // Some players publish their timeline every second or so; only a seek or a new length is worth a full reading.
    private void TimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    { Volatile.Write(ref _timeline, 1); Wake(); }

    private bool Seeked()
    {
        if (_snap is not { } snap || _observed is not { } session) return true;
        var (pos, dur) = Position(session.GetTimelineProperties(), snap.Playing);
        var expected = snap.PosMs + (snap.Playing ? snap.AgeMs : 0);
        return (long)dur.TotalMilliseconds != snap.DurMs || Math.Abs((long)pos.TotalMilliseconds - expected) > SeekMs;
    }

    private void Observe(GlobalSystemMediaTransportControlsSession? session)
    {
        if (ReferenceEquals(session, _observed)) return;
        if (_observed is not null)
        {
            _observed.MediaPropertiesChanged -= PropertiesChanged;
            _observed.PlaybackInfoChanged -= PlaybackChanged;
            _observed.TimelinePropertiesChanged -= TimelineChanged;
        }
        _observed = session;
        if (_observed is not null)
        {
            _observed.MediaPropertiesChanged += PropertiesChanged;
            _observed.PlaybackInfoChanged += PlaybackChanged;
            _observed.TimelinePropertiesChanged += TimelineChanged;
        }
    }

    private static (TimeSpan Pos, TimeSpan Dur) Position(GlobalSystemMediaTransportControlsSessionTimelineProperties tl, bool playing)
    {
        var (pos, dur) = Position(tl, playing);
        return (pos, dur);
    }

    private async Task Poll(CancellationToken ct = default)
    {
        await _pollGate.WaitAsync(ct);
        var before = _snap;
        try { await PollCore(ct); }
        finally
        {
            _pollGate.Release();
            // a new track, a pause or a seek reaches the phone now, not at its next whole-second state
            if (StateSignal.MediaChanged(before, _snap)) StateSignal.Poke();
        }
    }

    private async Task PollCore(CancellationToken ct)
    {
        if (_mgr is not { } mgr) { _snap = null; return; }
        var s = mgr.GetCurrentSession();
        Observe(s);
        if (s is null) { _snap = null; _art.Clear(); return; }
        var revision = Interlocked.Read(ref _revision);

        var props = await s.TryGetMediaPropertiesAsync();
        var pb = s.GetPlaybackInfo();
        var tl = s.GetTimelineProperties();
        var playing = pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

        var key = $"{s.SourceAppUserModelId}|{props.Title}|{props.Artist}";
        // Refresh after metadata events and while a new track's thumbnail is still settling.
        try
        {
            await _art.UpdateAsync(key, revision, props.Thumbnail is { } thumb ? async token =>
            {
                using var stream = await thumb.OpenReadAsync();
                if (stream.Size > 8 * 1024 * 1024) return null;
                using var ms = new MemoryStream();
                await stream.AsStreamForRead().CopyToAsync(ms, token);
                return ms.Length > 0 ? new AlbumArt(ms.ToArray(), string.IsNullOrEmpty(stream.ContentType) ? "image/png" : stream.ContentType) : null;
            } : null, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { log.LogDebug("cover art: {Msg}", ex.Message); }

        var sampledAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var (pos, dur) = Position(tl, playing);

        _snap = new MediaSnapshot(
            App: FriendlyApp(s.SourceAppUserModelId),
            Title: props.Title ?? "",
            Artist: props.Artist ?? "",
            Album: props.AlbumTitle ?? "",
            Playing: playing,
            PosMs: (long)pos.TotalMilliseconds,
            DurMs: (long)Math.Max(0, dur.TotalMilliseconds),
            ArtId: Art is null ? 0 : _art.Id,
            CanSeek: pb.Controls.IsPlaybackPositionEnabled) { SampledAt = sampledAt };
    }

    public async Task<bool> Control(string action, long? ms)
    {
        var s = Enabled ? _mgr?.GetCurrentSession() : null;
        if (s is null)
        {
            // No session: send the media key instead (wakes up Spotify, for example).
            if (action == "playpause") { Keys.Tap(Keys.VK_MEDIA_PLAY_PAUSE); return true; }
            return false;
        }
        var ok = action switch
        {
            "playpause" => await s.TryTogglePlayPauseAsync(),
            "next" => await s.TrySkipNextAsync(),
            "prev" => await s.TrySkipPreviousAsync(),
            "seek" when ms is { } m => await s.TryChangePlaybackPositionAsync(TimeSpan.FromMilliseconds(m).Ticks),
            _ => false,
        };
        try { await Poll(); } catch { }
        return ok;
    }

    public async Task<bool> PauseAsync()
    {
        var s = _mgr?.GetCurrentSession();
        if (s is null || s.GetPlaybackInfo().PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            return false;
        var ok = await s.TryPauseAsync();
        try { await Poll(); } catch { }
        return ok;
    }

    private static string FriendlyApp(string id)
    {
        var l = id.ToLowerInvariant();
        if (l.Contains("spotify")) return "Spotify";
        if (l.Contains("chrome")) return "Chrome";
        if (l.Contains("firefox") || l.Contains("308046b0af4a39cb")) return "Firefox";
        if (l.Contains("msedge")) return "Edge";
        if (l.Contains("zune") || l.Contains("media")) return "Media Player";
        if (l.Contains("vlc")) return "VLC";
        return Path.GetFileNameWithoutExtension(id.Split('!')[0]);
    }
}
