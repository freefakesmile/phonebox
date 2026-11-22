using System.Runtime.InteropServices;

namespace PhoneBox.Agent;

/// <summary>
/// Registers the page hotkeys on the tray's UI thread and follows the settings (checked every two seconds, so a change
/// on the settings page applies without a restart). Windows only; created and disposed by <see cref="Tray"/>.
/// </summary>
internal sealed class PageHotkeyWindow : System.Windows.Forms.NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312, IdPrev = 1, IdNext = 2;
    private readonly ConfigStore _config;
    private readonly AdbReverse _adb;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private string? _applied;

    public PageHotkeyWindow(ConfigStore config, AdbReverse adb)
    {
        _config = config;
        _adb = adb;
        CreateHandle(new System.Windows.Forms.CreateParams());
        _timer.Tick += (_, _) => Apply();
        _timer.Start();
        Apply();
    }

    private void Apply()
    {
        var c = _config.Get().PageKeys;
        var wanted = c.Enabled ? $"{c.Prev}|{c.Next}" : "";
        if (wanted == _applied) return;
        _applied = wanted;
        UnregisterHotKey(Handle, IdPrev);
        UnregisterHotKey(Handle, IdNext);
        if (!c.Enabled) return;
        // a combination another program already holds simply doesn't register
        if (PageHotkeys.Parse(c.Prev) is { } p) RegisterHotKey(Handle, IdPrev, p.Mods | PageHotkeys.ModNoRepeat, p.Vk);
        if (PageHotkeys.Parse(c.Next) is { } n) RegisterHotKey(Handle, IdNext, n.Mods | PageHotkeys.ModNoRepeat, n.Vk);
    }

    protected override void WndProc(ref System.Windows.Forms.Message m)
    {
        if (m.Msg == WmHotkey && (int)m.WParam is IdPrev or IdNext)
        {
            var next = (int)m.WParam == IdNext;
            _ = Task.Run(() => _adb.PageKeyAsync(next));
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        _timer.Dispose();
        if (Handle != IntPtr.Zero)
        {
            UnregisterHotKey(Handle, IdPrev);
            UnregisterHotKey(Handle, IdNext);
            DestroyHandle();
        }
    }

    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
