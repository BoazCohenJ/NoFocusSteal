namespace NoFocusSteal;

public enum ProtectionMode
{
    /// <summary>Do nothing at all.</summary>
    Off,
    /// <summary>Record every focus change and flag the ones that would be blocked, but never interfere.</summary>
    LogOnly,
    /// <summary>Block focus changes that happen while you are typing (default).</summary>
    Guard,
    /// <summary>Block every focus change that no click or keypress of yours asked for.</summary>
    Strict,
}

public enum AppRule
{
    Default,
    Allow,
    Block,
}

public enum Verdict
{
    /// <summary>The focus change followed something you did, or was structurally harmless.</summary>
    Allowed,
    /// <summary>Nothing you did asked for it, but it was let through because you weren't typing.</summary>
    Unsolicited,
    /// <summary>Focus was taken back and returned to your window.</summary>
    Blocked,
    /// <summary>Log-only mode: this would have been blocked.</summary>
    WouldBlock,
}

/// <summary>Everything the policy needs to know about one foreground change. Times are GetTickCount-style milliseconds.</summary>
public sealed class FocusSnapshot
{
    public int EventTime;

    public bool NextIsOwnProcess;
    public bool NextIsSystemUI;

    public bool HasPrevious;
    /// <summary>The previously focused window was closed, hidden, minimized or moved to another desktop.</summary>
    public bool PreviousGone;
    public bool SameProcess;
    public bool OwnerRelated;
    /// <summary>The new foreground window is one that should never have focus, like the invisible input-method
    /// window a Windows 11 24H2 bug hands focus to after every click.</summary>
    public bool NextIsBogus;
    /// <summary>Windows' "activate the window under the mouse" setting (X-Mouse) is on, the pointer just moved,
    /// and the new foreground window is the one under it.</summary>
    public bool FollowsMouse;

    /// <summary>Most recent request to switch at or before the event: a click on some other window, the taskbar
    /// or the desktop, a shortcut, or an Alt/Win/Tab/Enter/Esc press.</summary>
    public int? LastIntent;
    /// <summary>Most recent click inside the window that is losing focus. That is working in it, like typing,
    /// though it can also open something (a link opening the browser).</summary>
    public int? LastClickInPrevious;
    /// <summary>The window losing focus belongs to an administrator app and NoFocusSteal isn't one, so Windows
    /// hid the clicks and keypresses that went to it (and may have hidden the one that caused this switch).</summary>
    public bool InputHiddenFromUs;
    /// <summary>The app taking focus has grabbed focus uninvited within the last few minutes.</summary>
    public bool NextIsRepeatOffender;
    /// <summary>Most recent ordinary (typing) keypress at or before the event, if any.</summary>
    public int? LastTyping;
    /// <summary>When the process that now owns the foreground window was started, if known.</summary>
    public int? NextProcessStart;

    public AppRule Rule;
}

public sealed class PolicyOptions
{
    public ProtectionMode Mode = ProtectionMode.Guard;
    /// <summary>A keypress this recent counts as "you are typing".</summary>
    public int TypingWindowMs = 1500;
    /// <summary>In strict mode, a focus change this soon after a click or shortcut is treated as yours.</summary>
    public int IntentWindowMs = 1500;
    /// <summary>In strict mode, a window from a process started after your last click may take focus for this long.</summary>
    public int LaunchGraceMs = 10000;
}

public readonly struct Decision
{
    public Decision(Verdict verdict, string reason)
    {
        Verdict = verdict;
        Reason = reason;
    }

    public Verdict Verdict { get; }
    public string Reason { get; }

    public override string ToString() => Verdict + ": " + Reason;
}

public static class FocusPolicy
{
    public static Decision Decide(FocusSnapshot s, PolicyOptions o)
    {
        if (o.Mode == ProtectionMode.Off) return Allow("protection is off");
        if (s.NextIsOwnProcess) return Allow("NoFocusSteal window");
        if (s.NextIsSystemUI) return Allow("Windows system UI");
        if (!s.HasPrevious) return Allow("no previous window");
        if (s.PreviousGone) return Allow("previous window was closed or minimized");
        if (s.SameProcess) return Allow("same app");
        if (s.OwnerRelated) return Allow("dialog of the previous window");
        if (s.NextIsBogus)
        {
            const string bogus = "an invisible input-method window grabbed focus (known Windows 11 bug)";
            return o.Mode == ProtectionMode.LogOnly ? new Decision(Verdict.WouldBlock, bogus) : new Decision(Verdict.Blocked, bogus);
        }
        if (s.Rule == AppRule.Allow) return Allow("app is on your allow list");
        if (s.InputHiddenFromUs)
            return Allow("an administrator app had focus, so your input there wasn't visible (run NoFocusSteal as administrator to cover it)");
        if (s.FollowsMouse) return Allow("focus follows your mouse (X-Mouse)");

        // Typing only counts if it came after your last deliberate action: a click or Alt+Tab followed by
        // silence is a request to switch, while keys typed after it mean you are busy where you are.
        bool typing = s.LastTyping.HasValue
                      && Elapsed(s.EventTime, s.LastTyping.Value) <= o.TypingWindowMs
                      && (!s.LastIntent.HasValue || Elapsed(s.LastTyping.Value, s.LastIntent.Value) > 0);

        // Same for clicks inside the window you're in: newer than your last switch request means you're working there.
        bool clicking = s.LastClickInPrevious.HasValue
                        && Elapsed(s.EventTime, s.LastClickInPrevious.Value) <= o.TypingWindowMs
                        && (!s.LastIntent.HasValue || Elapsed(s.LastClickInPrevious.Value, s.LastIntent.Value) > 0);

        bool recentIntent = s.LastIntent.HasValue && Elapsed(s.EventTime, s.LastIntent.Value) <= o.IntentWindowMs;

        bool launchedByYou = LaunchedAfter(s, s.LastIntent, o) || LaunchedAfter(s, s.LastClickInPrevious, o);

        string blockReason;
        if (typing)
        {
            blockReason = "took focus while you were typing";
        }
        else if (clicking)
        {
            if (LaunchedAfter(s, s.LastClickInPrevious, o)) return Allow("an app you just launched");
            if (s.NextIsRepeatOffender) blockReason = "took focus right after you clicked in your window, and has grabbed focus before";
            else if (s.Rule == AppRule.Block) blockReason = "app is on your block list";
            else if (o.Mode == ProtectionMode.Strict) blockReason = "took focus right after you clicked in your window (strict mode)";
            // A well-behaved app coming forward right after a click is usually something the click opened.
            else return Allow("right after your click (it may have opened a link or file)");
        }
        else if (recentIntent)
        {
            return Allow("follows your click or keypress");
        }
        else if (launchedByYou)
        {
            return Allow("an app you just launched");
        }
        else if (s.Rule == AppRule.Block)
        {
            blockReason = "app is on your block list";
        }
        else if (s.NextIsRepeatOffender)
        {
            // One uninvited grab is let through while you're idle; an app that keeps doing it is not.
            blockReason = "keeps grabbing focus uninvited";
        }
        else if (o.Mode == ProtectionMode.Strict)
        {
            blockReason = "no click or keypress asked for it (strict mode)";
        }
        else
        {
            return new Decision(Verdict.Unsolicited, "no click or keypress asked for it; allowed because you weren't typing");
        }

        return o.Mode == ProtectionMode.LogOnly
            ? new Decision(Verdict.WouldBlock, blockReason)
            : new Decision(Verdict.Blocked, blockReason);
    }

    /// <summary>The new window's process started no earlier than a second before <paramref name="action"/>,
    /// and not too long ago: something that action launched.</summary>
    private static bool LaunchedAfter(FocusSnapshot s, int? action, PolicyOptions o) =>
        action.HasValue && s.NextProcessStart.HasValue
        && Elapsed(s.EventTime, action.Value) <= o.LaunchGraceMs
        && Elapsed(s.NextProcessStart.Value, action.Value) >= -1000;

    /// <summary>Milliseconds from <paramref name="earlier"/> to <paramref name="later"/>, safe across tick-count wraparound.</summary>
    public static int Elapsed(int later, int earlier) => unchecked(later - earlier);

    private static Decision Allow(string reason) => new Decision(Verdict.Allowed, reason);
}
