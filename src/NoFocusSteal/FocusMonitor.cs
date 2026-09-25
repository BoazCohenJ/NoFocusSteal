using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NoFocusSteal;

/// <summary>
/// Listens for foreground-window changes, decides whether each one was asked for, and hands focus back to
/// your window when it wasn't. Everything happens from this process through public Win32 APIs: no DLL
/// injection and no API hooking in other programs.
/// </summary>
internal sealed class FocusMonitor : IDisposable
{
    // Wait this long before judging a focus change so that the click or keypress that caused it
    // (delivered to us as a separate message) has been counted.
    private const int SettleMs = 40;
    // An app that is blocked this many times inside FightWindowMs is left alone for a while.
    private const int FightLimit = 12;
    private const int FightWindowMs = 10000;
    private const int FightPauseMs = 60000;

    private readonly Settings _settings;
    private readonly FocusLog _log;
    private readonly InputTracker _input;
    private readonly Native.WinEventDelegate _callback;
    private readonly Queue<Pending> _pending = new();
    private readonly Timer _timer = new() { Interval = SettleMs };
    private readonly Dictionary<string, List<int>> _recentBlocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _pausedUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly uint _ownPid = (uint)Process.GetCurrentProcess().Id;
    private IntPtr _hook;
    private WindowInfo _tracked;

    private sealed class Pending
    {
        public WindowInfo Window;
        public int EventTime;
        public int LastInputInfo;
    }

    public FocusMonitor(Settings settings, FocusLog log)
    {
        _settings = settings;
        _log = log;
        _input = new InputTracker { TrustInjectedInput = settings.TrustInjectedInput };
        _callback = OnWinEvent;
        _timer.Tick += (_, _) => Drain();
    }

    public event Action<LogEntry> Blocked;

    public bool InputRegistered => _input.Registered;

    public void Start()
    {
        IntPtr fg = Native.GetForegroundWindow();
        if (fg != IntPtr.Zero)
        {
            var info = WindowInfo.Capture(fg);
            if (!info.IsSystemUI) _tracked = info;
        }
        _hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _callback, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
    }

    public void ApplySettings() => _input.TrustInjectedInput = _settings.TrustInjectedInput;

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero || idObject != 0) return;
        try
        {
            var lii = new Native.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(Native.LASTINPUTINFO)) };
            Native.GetLastInputInfo(ref lii);
            _pending.Enqueue(new Pending
            {
                Window = WindowInfo.Capture(hwnd),
                EventTime = unchecked((int)time),
                LastInputInfo = unchecked((int)lii.dwTime),
            });
            if (!_timer.Enabled) _timer.Start();
        }
        catch (Exception ex)
        {
            Trace.WriteLine("NoFocusSteal: capture failed: " + ex);
        }
    }

    private void Drain()
    {
        _timer.Stop();
        while (_pending.Count > 0)
        {
            var p = _pending.Dequeue();
            try
            {
                Evaluate(p);
            }
            catch (Exception ex)
            {
                Trace.WriteLine("NoFocusSteal: evaluate failed: " + ex);
            }
        }
    }

    private void Evaluate(Pending p)
    {
        WindowInfo next = p.Window;
        if (_tracked != null && next.Hwnd == _tracked.Hwnd) return;

        // Input that Raw Input didn't report (touch, pen, some accessibility tools) still shows up in the
        // system-wide last-input time. Count it as a deliberate action rather than risk blocking the user.
        if (FocusPolicy.Elapsed(p.LastInputInfo, _input.LastAnyInput) > 150 || !_input.HasAnyInput)
        {
            if (!_input.IsIgnoring && p.LastInputInfo != 0)
                _input.AddIntent(p.LastInputInfo);
        }

        PolicyOptions options = _settings.Policy;
        if (options.Mode == ProtectionMode.Off)
        {
            if (!next.IsSystemUI) _tracked = next;
            return;
        }

        WindowInfo prev = _tracked;
        var snapshot = new FocusSnapshot
        {
            EventTime = p.EventTime,
            NextIsOwnProcess = next.ProcessId == _ownPid,
            NextIsSystemUI = next.IsSystemUI,
            NextIsBogus = next.IsBogus,
            HasPrevious = prev != null,
            PreviousGone = prev != null && WindowInfo.IsGone(prev.Hwnd),
            SameProcess = prev != null && prev.ProcessId == next.ProcessId,
            OwnerRelated = prev != null && (next.RootOwner == prev.RootOwner || next.RootOwner == prev.Hwnd
                                            || prev.RootOwner == next.Hwnd),
            LastIntent = _input.LastIntentAtOrBefore(unchecked(p.EventTime + SettleMs)),
            LastTyping = _input.LastTypingAtOrBefore(p.EventTime),
            FollowsMouse = FollowsMouse(next, p.EventTime),
            NextProcessStart = next.ProcessStartTick,
            Rule = _settings.RuleFor(next.ExeName),
        };

        Decision decision = FocusPolicy.Decide(snapshot, options);
        var entry = new LogEntry
        {
            Time = DateTime.Now,
            Verdict = decision.Verdict,
            Reason = decision.Reason,
            Window = next,
            Previous = prev,
        };
        if (!next.Visible) entry.Note = "(invisible window)";

        if (decision.Verdict == Verdict.Blocked)
        {
            // Bogus windows don't fight back; they get focus once per click, so never back off from them.
            if (!next.IsBogus && IsPaused(next.ExeName, p.EventTime))
            {
                entry.Verdict = Verdict.Unsolicited;
                entry.Note = Join(entry.Note, "(not blocked: this app kept fighting back, paused for a minute)");
                _tracked = next;
            }
            else
            {
                bool fighting = !next.IsBogus && CountBlock(next.ExeName, p.EventTime);
                if (!TakeFocusBack(prev, next, out bool alreadyBack))
                {
                    entry.Note = Join(entry.Note,
                        "(couldn't take focus back; if that app runs as administrator, run NoFocusSteal as administrator too)");
                    _tracked = next;
                }
                else if (alreadyBack)
                {
                    entry.Note = Join(entry.Note, "(it gave focus back by itself)");
                }
                if (fighting)
                    entry.Note = Join(entry.Note, "(it keeps fighting back; leaving it alone for a minute)");
                Flash(next);
            }
        }
        else if (!next.IsSystemUI)
        {
            _tracked = next;
        }

        _log.Add(entry);
        if (entry.Verdict == Verdict.Blocked) Blocked?.Invoke(entry);
    }

    /// <summary>
    /// With Windows' "activate a window by hovering over it" (X-Mouse) turned on, pointing at a window is how
    /// you switch to it. Treat that as yours when the pointer moved shortly before and is over the new window.
    /// </summary>
    private bool FollowsMouse(WindowInfo next, int eventTime)
    {
        if (!Native.SystemParametersInfoBool(Native.SPI_GETACTIVEWINDOWTRACKING, 0, out bool tracking, 0) || !tracking)
            return false;
        Native.SystemParametersInfoUInt(Native.SPI_GETACTIVEWNDTRKTIMEOUT, 0, out uint delay, 0);
        int? lastMove = _input.LastMouseMoveAtOrBefore(eventTime);
        if (lastMove == null || FocusPolicy.Elapsed(eventTime, lastMove.Value) > (int)Math.Min(delay, 10000) + 1000)
            return false;
        if (!Native.GetCursorPos(out Native.POINT cursor)) return false;
        IntPtr under = Native.WindowFromPoint(cursor);
        if (under == IntPtr.Zero) return false;
        IntPtr root = Native.GetAncestor(under, Native.GA_ROOTOWNER);
        return root == next.RootOwner || root == next.Hwnd || under == next.Hwnd;
    }

    private bool TakeFocusBack(WindowInfo prev, WindowInfo thief, out bool alreadyBack)
    {
        alreadyBack = false;
        IntPtr current = Native.GetForegroundWindow();
        if (current == prev.Hwnd)
        {
            alreadyBack = true;
            return true;
        }
        // Something else has come forward since; its own event will be judged on its merits.
        if (current != IntPtr.Zero && current != thief.Hwnd && Native.IsWindowVisible(current)
            && Native.GetAncestor(current, Native.GA_ROOTOWNER) != thief.RootOwner)
            return true;

        return ForceForeground(prev.Hwnd);
    }

    private bool ForceForeground(IntPtr target)
    {
        IntPtr fg = Native.GetForegroundWindow();
        uint fgThread = fg == IntPtr.Zero ? 0 : Native.GetWindowThreadProcessId(fg, out _);
        uint me = Native.GetCurrentThreadId();

        // Sharing input state with the thread that currently owns the foreground lets us move it.
        bool attached = fgThread != 0 && fgThread != me && Native.AttachThreadInput(me, fgThread, true);
        try
        {
            Native.BringWindowToTop(target);
            Native.SetForegroundWindow(target);
        }
        finally
        {
            if (attached) Native.AttachThreadInput(me, fgThread, false);
        }
        if (Native.GetForegroundWindow() == target) return true;

        // Fallback: Windows lets the process that produced the most recent input set the foreground.
        // Send a key that no program uses, then try again.
        _input.IgnoreInputFor(200);
        var inputs = new[]
        {
            new Native.INPUT { type = Native.INPUT_KEYBOARD, u = { ki = { wVk = Native.VK_NOOP } } },
            new Native.INPUT { type = Native.INPUT_KEYBOARD, u = { ki = { wVk = Native.VK_NOOP, dwFlags = Native.KEYEVENTF_KEYUP } } },
        };
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Native.INPUT)));
        Native.SetForegroundWindow(target);
        return Native.GetForegroundWindow() == target;
    }

    private static void Flash(WindowInfo thief)
    {
        IntPtr hwnd = thief.RootOwner != IntPtr.Zero && Native.IsWindowVisible(thief.RootOwner) ? thief.RootOwner : thief.Hwnd;
        if (!Native.IsWindowVisible(hwnd)) return;
        var info = new Native.FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf(typeof(Native.FLASHWINFO)),
            hwnd = hwnd,
            dwFlags = Native.FLASHW_TRAY | Native.FLASHW_TIMERNOFG,
        };
        Native.FlashWindowEx(ref info);
    }

    private bool IsPaused(string exe, int now) =>
        _pausedUntil.TryGetValue(exe, out int until) && FocusPolicy.Elapsed(until, now) > 0;

    /// <summary>Records a block and returns true if the app has now been blocked so often that we back off.</summary>
    private bool CountBlock(string exe, int now)
    {
        if (!_recentBlocks.TryGetValue(exe, out var times)) _recentBlocks[exe] = times = new List<int>();
        times.RemoveAll(t => FocusPolicy.Elapsed(now, t) > FightWindowMs);
        times.Add(now);
        if (times.Count < FightLimit) return false;
        _pausedUntil[exe] = unchecked(now + FightPauseMs);
        times.Clear();
        return true;
    }

    private static string Join(string a, string b) => a.Length == 0 ? b : a + " " + b;

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) Native.UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
        _timer.Dispose();
        _input.Dispose();
    }
}
