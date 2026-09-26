using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NoFocusSteal;

/// <summary>
/// Watches keyboard and mouse activity through Raw Input (no hooks, nothing injected into other processes)
/// and keeps short histories of when you acted deliberately and when you were typing. Only timestamps and
/// key categories are kept; which keys you pressed is never stored.
/// </summary>
internal sealed class InputTracker : NativeWindow, IDisposable
{
    private const int HistorySize = 16;
    private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

    private readonly int[] _intents = new int[HistorySize];
    private readonly int[] _typing = new int[HistorySize];
    private readonly int[] _moves = new int[HistorySize];
    private readonly int[] _clicks = new int[HistorySize];
    private readonly IntPtr[] _clickWindows = new IntPtr[HistorySize];
    private int _clickCount, _clickNext;
    private int _intentCount, _intentNext, _typingCount, _typingNext, _moveCount, _moveNext;
    private int _lastMoveRecorded;
    private bool _ctrl, _alt, _win;
    private IntPtr _buffer = Marshal.AllocHGlobal(256);
    private uint _bufferSize = 256;

    public InputTracker()
    {
        CreateHandle(new CreateParams { Caption = "NoFocusSteal.Input", Parent = HWND_MESSAGE });
        var devices = new[]
        {
            new Native.RAWINPUTDEVICE { UsagePage = 1, Usage = 2, Flags = Native.RIDEV_INPUTSINK, Target = Handle },
            new Native.RAWINPUTDEVICE { UsagePage = 1, Usage = 6, Flags = Native.RIDEV_INPUTSINK, Target = Handle },
        };
        Registered = Native.RegisterRawInputDevices(devices, (uint)devices.Length,
            (uint)Marshal.SizeOf(typeof(Native.RAWINPUTDEVICE)));
    }

    public bool Registered { get; }

    /// <summary>Treat input synthesized by software (remote desktop tools, on-screen keyboards) like real input.
    /// Off by default, because the classic focus-stealing trick is to fake an Alt keypress.</summary>
    public bool TrustInjectedInput { get; set; }

    /// <summary>Tick of the last raw input event of any kind, including mouse moves and injected input.</summary>
    public int LastAnyInput { get; private set; }

    // Times of recent raw input of any kind (thinned to one per 10 ms), to tell whether the system's
    // last-input time is input we saw or input that never reached us.
    private readonly int[] _any = new int[64];
    private int _anyCount, _anyNext;
    public bool HasAnyInput { get; private set; }

    private DateTime _ignoreUntilUtc = DateTime.MinValue;

    /// <summary>Ignore input for a moment while NoFocusSteal injects its own no-op key, so it isn't mistaken for the user.</summary>
    public void IgnoreInputFor(int ms) => _ignoreUntilUtc = DateTime.UtcNow.AddMilliseconds(ms);

    public bool IsIgnoring => DateTime.UtcNow < _ignoreUntilUtc;

    /// <summary>True if Raw Input reported something close to <paramref name="time"/>.</summary>
    public bool SawInputNear(int time)
    {
        for (int i = 0; i < _anyCount; i++)
        {
            int d = FocusPolicy.Elapsed(_any[i], time);
            if (d >= -150 && d <= 60) return true;
        }
        return false;
    }

    /// <summary>
    /// Handle raw input that is already waiting in the queue. A focus change is judged the moment Windows
    /// reports it, and the click or keypress that caused it is usually still queued at that point.
    /// </summary>
    public void ProcessPending()
    {
        while (Native.PeekMessage(out Native.MSG msg, Handle, Native.WM_INPUT, Native.WM_INPUT, Native.PM_REMOVE))
            Native.DispatchMessage(ref msg);
    }

    public int? LastIntentAtOrBefore(int time) => LatestAtOrBefore(_intents, _intentCount, time);
    public int? LastTypingAtOrBefore(int time) => LatestAtOrBefore(_typing, _typingCount, time);
    public int? LastMouseMoveAtOrBefore(int time) => LatestAtOrBefore(_moves, _moveCount, time);

    /// <summary>Latest click at or before <paramref name="time"/> whose top-level window satisfies <paramref name="where"/>.</summary>
    public int? LastClickAtOrBefore(int time, Func<IntPtr, bool> where)
    {
        int? best = null;
        for (int i = 0; i < _clickCount; i++)
        {
            int t = _clicks[i];
            if (FocusPolicy.Elapsed(time, t) < 0 || !where(_clickWindows[i])) continue;
            if (best == null || FocusPolicy.Elapsed(t, best.Value) > 0) best = t;
        }
        return best;
    }

    /// <summary>Record input that happened but that Raw Input didn't report (touch, pen), as a deliberate action.</summary>
    public void AddIntent(int time) => Push(_intents, ref _intentNext, ref _intentCount, time);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_INPUT)
        {
            try
            {
                OnRawInput(m.LParam, Native.GetMessageTime());
            }
            catch
            {
                // Never let a malformed input packet take the app down.
            }
        }
        base.WndProc(ref m);
    }

    private void OnRawInput(IntPtr hRawInput, int time)
    {
        uint headerSize = (uint)(8 + 2 * IntPtr.Size);
        uint size = 0;
        Native.GetRawInputData(hRawInput, Native.RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0) return;
        if (size > _bufferSize)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = Marshal.AllocHGlobal((int)size);
            _bufferSize = size;
        }
        if (Native.GetRawInputData(hRawInput, Native.RID_INPUT, _buffer, ref size, headerSize) != size) return;

        uint type = (uint)Marshal.ReadInt32(_buffer, 0);
        IntPtr device = Marshal.ReadIntPtr(_buffer, 8);
        int data = (int)headerSize;

        if (!HasAnyInput || FocusPolicy.Elapsed(time, LastAnyInput) >= 10)
            Push(_any, ref _anyNext, ref _anyCount, time);
        LastAnyInput = time;
        HasAnyInput = true;

        if (IsIgnoring) return;
        if (device == IntPtr.Zero && !TrustInjectedInput) return;

        if (type == Native.RIM_TYPEKEYBOARD)
        {
            ushort flags = (ushort)Marshal.ReadInt16(_buffer, data + 2);
            ushort vkey = (ushort)Marshal.ReadInt16(_buffer, data + 6);
            OnKey(vkey, (flags & Native.RI_KEY_BREAK) == 0, time);
        }
        else if (type == Native.RIM_TYPEMOUSE)
        {
            ushort buttonFlags = (ushort)Marshal.ReadInt16(_buffer, data + 4);
            // Left, right, middle, X1, X2 button-down flags.
            const ushort anyButtonDown = 0x0001 | 0x0004 | 0x0010 | 0x0040 | 0x0100;
            if ((buttonFlags & anyButtonDown) != 0)
            {
                // Remember which top-level window was clicked: clicking the window you're already in is working
                // in it, clicking anything else is asking to switch. The monitor decides which once it knows.
                IntPtr clicked = IntPtr.Zero;
                if (Native.GetCursorPos(out Native.POINT cursor))
                {
                    IntPtr under = Native.WindowFromPoint(cursor);
                    if (under != IntPtr.Zero) clicked = Native.GetAncestor(under, Native.GA_ROOTOWNER);
                }
                _clickWindows[_clickNext] = clicked;
                Push(_clicks, ref _clickNext, ref _clickCount, time);
            }

            // Pointer movement matters when Windows is set to activate the window under the mouse
            // (X-Mouse / "focus follows mouse"). Mice report hundreds of moves a second, so thin them out.
            ushort moveFlags = (ushort)Marshal.ReadInt16(_buffer, data);
            int dx = Marshal.ReadInt32(_buffer, data + 12), dy = Marshal.ReadInt32(_buffer, data + 16);
            bool moved = dx != 0 || dy != 0 || (moveFlags & 0x01) != 0; // MOUSE_MOVE_ABSOLUTE: tablets, touchpads
            if (moved && (_moveCount == 0 || FocusPolicy.Elapsed(time, _lastMoveRecorded) >= 30))
            {
                Push(_moves, ref _moveNext, ref _moveCount, time);
                _lastMoveRecorded = time;
            }
        }
    }

    private void OnKey(ushort vk, bool down, int time)
    {
        switch (vk)
        {
            case 0x10: // Shift on its own is neither typing nor a request to switch.
                return;
            case 0x11:
                _ctrl = down;
                return;
            case 0x12:
                _alt = down;
                // Alt+Tab switches windows when Alt is released, so both edges count.
                Push(_intents, ref _intentNext, ref _intentCount, time);
                return;
            case 0x5B:
            case 0x5C:
                _win = down;
                Push(_intents, ref _intentNext, ref _intentCount, time);
                return;
            case 0xFF: // Fake key sent as part of escaped sequences.
                return;
        }

        if (!down) return;

        // A key-up can go missing (for example while a UAC prompt had the secure desktop), which would leave
        // a modifier stuck "down" and turn all typing into shortcuts. Trust the live key state in that case.
        if (_ctrl && !IsPhysicallyDown(0x11)) _ctrl = false;
        if (_alt && !IsPhysicallyDown(0x12)) _alt = false;
        if (_win && !IsPhysicallyDown(0x5B) && !IsPhysicallyDown(0x5C)) _win = false;

        if (_ctrl || _alt || _win || IsIntentKey(vk))
            Push(_intents, ref _intentNext, ref _intentCount, time);
        else
            Push(_typing, ref _typingNext, ref _typingCount, time);
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static bool IsPhysicallyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    internal static bool IsIntentKey(ushort vk) =>
        vk == 0x09 // Tab
        || vk == 0x0D // Enter
        || vk == 0x1B // Esc
        || vk == 0x5D // Menu / Apps
        || (vk >= 0x70 && vk <= 0x87) // F1-F24
        || (vk >= 0xA6 && vk <= 0xB7); // browser, media and app-launch keys

    private static void Push(int[] ring, ref int next, ref int count, int time)
    {
        ring[next] = time;
        next = (next + 1) % ring.Length;
        if (count < ring.Length) count++;
    }

    private static int? LatestAtOrBefore(int[] ring, int count, int time)
    {
        int? best = null;
        for (int i = 0; i < count; i++)
        {
            int t = ring[i];
            if (FocusPolicy.Elapsed(time, t) < 0) continue;
            if (best == null || FocusPolicy.Elapsed(t, best.Value) > 0) best = t;
        }
        return best;
    }

    public void Dispose()
    {
        DestroyHandle();
        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }
    }
}
