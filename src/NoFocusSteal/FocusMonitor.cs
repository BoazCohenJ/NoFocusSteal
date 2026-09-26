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
    // Input and focus events are stamped by a clock that ticks every ~16 ms, so allow that much slack
    // when asking whether an input came before a focus change.
    private const int ClockSlackMs = 20;
    // An app that grabs focus back this fast is fighting us in a loop; leave it alone for a while rather
    // than ping-pong. A window that grabs focus every second or two stays blocked.
    private const int FightLimit = 20;
    private const int FightWindowMs = 3000;
    private const int FightPauseMs = 60000;

    private readonly Settings _settings;
    private readonly FocusLog _log;
    private readonly InputTracker _input;
    private readonly Native.WinEventDelegate _callback;
    private readonly Queue<Pending> _pending = new();
    private bool _draining;

    // Windows doesn't always announce a foreground change. An app that steals by borrowing your window's input
    // connection (AttachThreadInput) can take over with no EVENT_SYSTEM_FOREGROUND at all, or finish taking
    // over after the announcement, when asking "who's in front?" still named your window. So also check the
    // foreground window on every keyboard-focus change and about 60 times a second, and judge any change
    // nobody announced like an announced one.
    private readonly Timer _pollTimer = new() { Interval = 15 };
    private readonly Native.WinEventDelegate _focusCallback;
    private IntPtr _focusHook;
    private IntPtr _lastSeenForeground;
    private readonly Dictionary<string, List<int>> _recentBlocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _pausedUntil = new(StringComparer.OrdinalIgnoreCase);
    // When each kind of window last grabbed focus uninvited; those get no benefit of the doubt after your clicks.
    private readonly Dictionary<string, int> _lastMisbehaved = new(StringComparer.OrdinalIgnoreCase);
    // The Windows 11 input-method bug fires on every click, so log it at most once a minute.
    private const int BogusLogIntervalMs = 60000;
    private int? _lastBogusLogged;
    private int _bogusSinceLogged;
    private const int OffenderMemoryMs = 10 * 60 * 1000;
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
        _focusCallback = (_, _, _, _, _, _, _) => CheckForeground();
        _pollTimer.Tick += (_, _) => CheckForeground();
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
        _focusHook = Native.SetWinEventHook(Native.EVENT_OBJECT_FOCUS, Native.EVENT_OBJECT_FOCUS,
            IntPtr.Zero, _focusCallback, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        _lastSeenForeground = fg;
        _pollTimer.Start();
    }

    public void ApplySettings() => _input.TrustInjectedInput = _settings.TrustInjectedInput;

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero || idObject != 0) return;
        Enqueue(hwnd, unchecked((int)time));
    }

    private void CheckForeground()
    {
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _lastSeenForeground) return;
        _lastSeenForeground = fg;
        if (_tracked != null && fg == _tracked.Hwnd) return;
        Enqueue(fg, Environment.TickCount);
    }

    private void Enqueue(IntPtr hwnd, int time)
    {
        try
        {
            var lii = new Native.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(Native.LASTINPUTINFO)) };
            Native.GetLastInputInfo(ref lii);
            _pending.Enqueue(new Pending
            {
                Window = WindowInfo.Capture(hwnd),
                EventTime = time,
                LastInputInfo = unchecked((int)lii.dwTime),
            });
        }
        catch (Exception ex)
        {
            Trace.WriteLine("NoFocusSteal: capture failed: " + ex);
        }
        // Judge right away: every millisecond the thief keeps focus is a keystroke that may land in it.
        // Pumping pending input can deliver further focus events re-entrantly; those just join the queue.
        if (!_draining) Drain();
    }

    private void Drain()
    {
        _draining = true;
        try
        {
            _input.ProcessPending();
        }
        finally
        {
            _draining = false;
        }
        _draining = true;
        try
        {
            DrainQueue();
        }
        finally
        {
            _draining = false;
        }
    }

    private void DrainQueue()
    {
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
        // Compare against input seen around that moment, not the latest input: mouse moves that arrive after
        // the switch (for example once an administrator app loses focus) mustn't hide a click we never saw.
        if (p.LastInputInfo != 0 && !_input.IsIgnoring && !_input.SawInputNear(p.LastInputInfo))
            _input.AddIntent(p.LastInputInfo);

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
            LastIntent = Latest(_input.LastIntentAtOrBefore(unchecked(p.EventTime + ClockSlackMs)),
                _input.LastClickAtOrBefore(unchecked(p.EventTime + ClockSlackMs), w => prev == null || !IsWindowOf(w, prev))),
            LastClickInPrevious = prev == null ? null
                : _input.LastClickAtOrBefore(unchecked(p.EventTime + ClockSlackMs), w => IsWindowOf(w, prev)),
            InputHiddenFromUs = prev != null && prev.Elevated && !WindowInfo.SelfElevated,
            NextIsRepeatOffender = _lastMisbehaved.TryGetValue(BehaviorKey(next), out int misbehaved)
                                   && FocusPolicy.Elapsed(p.EventTime, misbehaved) <= OffenderMemoryMs,
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
            if (!next.IsBogus && IsPaused(BehaviorKey(next), p.EventTime))
            {
                entry.Verdict = Verdict.Unsolicited;
                entry.Note = Join(entry.Note, "(not blocked: this app kept fighting back, paused for a minute)");
                _tracked = next;
            }
            else
            {
                bool fighting = !next.IsBogus && CountBlock(BehaviorKey(next), p.EventTime);
                if (!TakeFocusBack(prev, next, out bool alreadyBack))
                {
                    if (next.Elevated && !WindowInfo.SelfElevated)
                    {
                        // Windows doesn't let a normal app take focus from an administrator one; stop trying.
                        entry.Note = Join(entry.Note,
                            "(couldn't take focus back from an administrator app; run NoFocusSteal as administrator to cover it)");
                        _tracked = next;
                    }
                    else
                    {
                        // Often the switch just hasn't registered yet. Keep your window as the one you're in and
                        // look at the foreground again on the next check, which retries if the thief is still there.
                        _lastSeenForeground = IntPtr.Zero;
                    }
                }
                else if (alreadyBack && next.IsBogus)
                {
                    // Windows handed focus back before we could act: nothing was blocked, and the brief focus
                    // loss (flicker, FPS dip in games) already happened. Say so rather than claim a block.
                    entry.Verdict = Verdict.Unsolicited;
                    entry.Note = Join(entry.Note, "(Windows gave focus back by itself; this bug can only be logged, not prevented)");
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

        if (entry.Verdict != Verdict.Allowed && !next.IsBogus) _lastMisbehaved[BehaviorKey(next)] = p.EventTime;

        if (next.IsBogus)
        {
            if (_lastBogusLogged.HasValue && FocusPolicy.Elapsed(p.EventTime, _lastBogusLogged.Value) < BogusLogIntervalMs)
            {
                _bogusSinceLogged++;
                return;
            }
            if (_bogusSinceLogged > 0)
                entry.Note = Join(entry.Note, $"(and {_bogusSinceLogged} more times in the previous minute or so)");
            _lastBogusLogged = p.EventTime;
            _bogusSinceLogged = 0;
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

    /// <summary>
    /// What "the same offender" means: the program plus its window class. Host processes such as explorer.exe
    /// own unrelated windows (desktop, folders, copy dialogs), and one misbehaving shouldn't taint the rest.
    /// </summary>
    internal static string BehaviorKey(WindowInfo w) => w.ExeName + "|" + w.ClassName;

    private static bool IsWindowOf(IntPtr clickedRoot, WindowInfo window) =>
        clickedRoot != IntPtr.Zero && (clickedRoot == window.RootOwner || clickedRoot == window.Hwnd);

    private static int? Latest(int? a, int? b)
    {
        if (a == null) return b;
        if (b == null) return a;
        return FocusPolicy.Elapsed(a.Value, b.Value) >= 0 ? a : b;
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
        if (_focusHook != IntPtr.Zero) Native.UnhookWinEvent(_focusHook);
        _focusHook = IntPtr.Zero;
        _pollTimer.Dispose();
        _input.Dispose();
    }
}
