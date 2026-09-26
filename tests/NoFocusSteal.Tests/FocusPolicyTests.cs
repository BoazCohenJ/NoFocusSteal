using Xunit;

namespace NoFocusSteal.Tests;

public class FocusPolicyTests
{
    private const int Now = 1_000_000;

    private static FocusSnapshot Steal(int? lastTypingAgo = null, int? lastIntentAgo = null, int? processStartAgo = null)
    {
        return new FocusSnapshot
        {
            EventTime = Now,
            HasPrevious = true,
            LastTyping = lastTypingAgo.HasValue ? Now - lastTypingAgo.Value : null,
            LastIntent = lastIntentAgo.HasValue ? Now - lastIntentAgo.Value : null,
            NextProcessStart = processStartAgo.HasValue ? Now - processStartAgo.Value : null,
        };
    }

    private static Decision Decide(FocusSnapshot s, ProtectionMode mode = ProtectionMode.Guard) =>
        FocusPolicy.Decide(s, new PolicyOptions { Mode = mode });

    [Fact]
    public void BlocksWhileTyping()
    {
        Assert.Equal(Verdict.Blocked, Decide(Steal(lastTypingAgo: 200, lastIntentAgo: 5000)).Verdict);
    }

    [Fact]
    public void BlocksWhileTypingWithNoEarlierClick()
    {
        Assert.Equal(Verdict.Blocked, Decide(Steal(lastTypingAgo: 200)).Verdict);
    }

    [Fact]
    public void TypingBlocksEvenAnAppYouJustLaunched()
    {
        // Pressed Enter to launch something, then kept typing elsewhere while it loaded.
        var s = Steal(lastTypingAgo: 300, lastIntentAgo: 3000, processStartAgo: 2900);
        Assert.Equal(Verdict.Blocked, Decide(s).Verdict);
    }

    [Fact]
    public void AllowsSwitchAfterClickWithoutTyping()
    {
        Assert.Equal(Verdict.Allowed, Decide(Steal(lastTypingAgo: 4000, lastIntentAgo: 100)).Verdict);
    }

    [Fact]
    public void ClickAfterTypingMeansYouWantedToSwitch()
    {
        // Typed, then clicked another window: the click is newer, so it's a deliberate switch.
        Assert.Equal(Verdict.Allowed, Decide(Steal(lastTypingAgo: 300, lastIntentAgo: 50)).Verdict);
    }

    [Fact]
    public void OldTypingDoesNotCount()
    {
        var d = Decide(Steal(lastTypingAgo: 5000, lastIntentAgo: 20000));
        Assert.Equal(Verdict.Unsolicited, d.Verdict);
    }

    [Fact]
    public void StrictBlocksUnsolicitedEvenWhenIdle()
    {
        Assert.Equal(Verdict.Blocked, Decide(Steal(lastIntentAgo: 20000), ProtectionMode.Strict).Verdict);
    }

    [Fact]
    public void StrictAllowsAppLaunchedAfterYourClick()
    {
        var s = Steal(lastIntentAgo: 4000, processStartAgo: 3800);
        Assert.Equal(Verdict.Allowed, Decide(s, ProtectionMode.Strict).Verdict);
    }

    [Fact]
    public void StrictBlocksOldProcessLongAfterClick()
    {
        var s = Steal(lastIntentAgo: 4000, processStartAgo: 3_600_000);
        Assert.Equal(Verdict.Blocked, Decide(s, ProtectionMode.Strict).Verdict);
    }

    [Fact]
    public void StrictLaunchGraceExpires()
    {
        var s = Steal(lastIntentAgo: 15000, processStartAgo: 14000);
        Assert.Equal(Verdict.Blocked, Decide(s, ProtectionMode.Strict).Verdict);
    }

    [Fact]
    public void LogOnlyNeverBlocks()
    {
        Assert.Equal(Verdict.WouldBlock, Decide(Steal(lastTypingAgo: 100), ProtectionMode.LogOnly).Verdict);
    }

    [Fact]
    public void OffAllowsEverything()
    {
        Assert.Equal(Verdict.Allowed, Decide(Steal(lastTypingAgo: 100), ProtectionMode.Off).Verdict);
    }

    [Theory]
    [InlineData("same")]
    [InlineData("gone")]
    [InlineData("owner")]
    [InlineData("system")]
    [InlineData("own")]
    [InlineData("allow")]
    public void StructuralCasesAreAlwaysAllowed(string kind)
    {
        var s = Steal(lastTypingAgo: 100);
        switch (kind)
        {
            case "same": s.SameProcess = true; break;
            case "gone": s.PreviousGone = true; break;
            case "owner": s.OwnerRelated = true; break;
            case "system": s.NextIsSystemUI = true; break;
            case "own": s.NextIsOwnProcess = true; break;
            case "allow": s.Rule = AppRule.Allow; break;
        }
        Assert.Equal(Verdict.Allowed, Decide(s, ProtectionMode.Strict).Verdict);
    }

    [Fact]
    public void BlockRuleActsLikeStrictForThatApp()
    {
        var s = Steal(lastIntentAgo: 20000);
        s.Rule = AppRule.Block;
        Assert.Equal(Verdict.Blocked, Decide(s).Verdict);
    }

    [Fact]
    public void BlockRuleStillRespectsYourClick()
    {
        var s = Steal(lastIntentAgo: 100);
        s.Rule = AppRule.Block;
        Assert.Equal(Verdict.Allowed, Decide(s).Verdict);
    }

    [Fact]
    public void BogusImeWindowIsBlockedEvenRightAfterAClick()
    {
        var s = Steal(lastIntentAgo: 20);
        s.NextIsBogus = true;
        Assert.Equal(Verdict.Blocked, Decide(s).Verdict);
        Assert.Equal(Verdict.WouldBlock, Decide(s, ProtectionMode.LogOnly).Verdict);
        Assert.Equal(Verdict.Allowed, Decide(s, ProtectionMode.Off).Verdict);
    }

    [Fact]
    public void BogusWindowOfTheSameAppIsLeftAlone()
    {
        var s = Steal(lastIntentAgo: 20);
        s.NextIsBogus = true;
        s.SameProcess = true;
        Assert.Equal(Verdict.Allowed, Decide(s).Verdict);
    }

    [Fact]
    public void FocusFollowsMouseIsAllowedEvenWhileTyping()
    {
        // X-Mouse users switch windows by pointing at them, often with their hands still on the keys.
        var s = Steal(lastTypingAgo: 100);
        s.FollowsMouse = true;
        Assert.Equal(Verdict.Allowed, Decide(s, ProtectionMode.Strict).Verdict);
    }

    [Fact]
    public void FocusFollowsMouseDoesNotExcuseTheImeBug()
    {
        var s = Steal(lastIntentAgo: 20);
        s.FollowsMouse = true;
        s.NextIsBogus = true;
        Assert.Equal(Verdict.Blocked, Decide(s).Verdict);
    }

    // Issue #1: an app that keeps grabbing focus must not get through just because you clicked
    // back into your own window a moment earlier.
    [Fact]
    public void RepeatOffenderIsBlockedRightAfterYouClickInYourWindow()
    {
        var s = Steal(lastIntentAgo: 5000);
        s.LastClickInPrevious = Now - 400;
        s.NextIsRepeatOffender = true;
        Assert.Equal(Verdict.Blocked, Decide(s).Verdict);
    }

    [Fact]
    public void WellBehavedAppMayFollowAClickInYourWindow()
    {
        // Clicking a link in a chat app brings the (already running) browser forward.
        var s = Steal(lastIntentAgo: 5000);
        s.LastClickInPrevious = Now - 400;
        Assert.Equal(Verdict.Allowed, Decide(s).Verdict);
    }

    [Fact]
    public void StrictBlocksAfterClickInYourWindow()
    {
        var s = Steal(lastIntentAgo: 5000);
        s.LastClickInPrevious = Now - 400;
        Assert.Equal(Verdict.Blocked, Decide(s, ProtectionMode.Strict).Verdict);
    }

    [Fact]
    public void AppLaunchedByYourClickIsAllowedEvenIfItMisbehavedBefore()
    {
        var s = Steal(lastIntentAgo: 5000, processStartAgo: 900);
        s.LastClickInPrevious = Now - 1000;
        s.NextIsRepeatOffender = true;
        Assert.Equal(Verdict.Allowed, Decide(s, ProtectionMode.Strict).Verdict);
    }

    [Fact]
    public void ClickInYourWindowAfterAltTabCountsAsWorkingThere()
    {
        // Alt+Tab to your window, click in it, then the offender grabs focus: the click is newer.
        var s = Steal(lastIntentAgo: 800);
        s.LastClickInPrevious = Now - 300;
        s.NextIsRepeatOffender = true;
        Assert.Equal(Verdict.Blocked, Decide(s).Verdict);
    }

    [Fact]
    public void SwitchRequestAfterClickInYourWindowIsHonoured()
    {
        // Clicked in your window, then Alt+Tabbed away: that's a request to switch.
        var s = Steal(lastIntentAgo: 100);
        s.LastClickInPrevious = Now - 600;
        s.NextIsRepeatOffender = true;
        Assert.Equal(Verdict.Allowed, Decide(s).Verdict);
    }

    [Fact]
    public void FirstUninvitedGrabWhileIdleIsOnlyFlagged()
    {
        var s = Steal(lastIntentAgo: 20000);
        s.LastClickInPrevious = Now - 5000;
        Assert.Equal(Verdict.Unsolicited, Decide(s).Verdict);
    }

    [Fact]
    public void RepeatOffenderIsBlockedEvenWhileIdle()
    {
        var s = Steal(lastIntentAgo: 20000);
        s.LastClickInPrevious = Now - 5000;
        s.NextIsRepeatOffender = true;
        Assert.Equal(Verdict.Blocked, Decide(s).Verdict);
    }

    [Fact]
    public void RepeatOffenderYouSwitchToYourselfIsAllowed()
    {
        var s = Steal(lastIntentAgo: 100);
        s.NextIsRepeatOffender = true;
        Assert.Equal(Verdict.Allowed, Decide(s).Verdict);
    }

    // Issue #1 follow-up (WizTree): input sent to an administrator app is invisible to a normal process, so a
    // switch away from one can't be judged. It must not be blocked or count against the other app.
    [Fact]
    public void SwitchAwayFromAdminAppIsAllowedWhenInputIsHidden()
    {
        var s = Steal(lastIntentAgo: 20000);
        s.InputHiddenFromUs = true;
        s.NextIsRepeatOffender = true;
        Assert.Equal(Verdict.Allowed, Decide(s, ProtectionMode.Strict).Verdict);
    }

    [Fact]
    public void ImeBugIsStillFlaggedWhenLeavingAnAdminApp()
    {
        var s = Steal(lastIntentAgo: 20);
        s.InputHiddenFromUs = true;
        s.NextIsBogus = true;
        Assert.Equal(Verdict.Blocked, Decide(s).Verdict);
    }

    [Fact]
    public void NoPreviousWindowIsAllowed()
    {
        var s = Steal(lastTypingAgo: 100);
        s.HasPrevious = false;
        Assert.Equal(Verdict.Allowed, Decide(s).Verdict);
    }

    [Fact]
    public void WorksAcrossTickCountWraparound()
    {
        int now = int.MinValue + 100; // 200 ms after the tick counter wrapped
        var s = new FocusSnapshot { EventTime = now, HasPrevious = true, LastTyping = int.MaxValue - 99 };
        Assert.Equal(200, FocusPolicy.Elapsed(now, s.LastTyping.Value));
        Assert.Equal(Verdict.Blocked, Decide(s).Verdict);
    }

    [Theory]
    [InlineData(0x09, true)]  // Tab
    [InlineData(0x0D, true)]  // Enter
    [InlineData(0x1B, true)]  // Esc
    [InlineData(0x74, true)]  // F5
    [InlineData(0x41, false)] // A
    [InlineData(0x20, false)] // Space
    [InlineData(0x08, false)] // Backspace
    public void ClassifiesKeys(int vk, bool intent)
    {
        Assert.Equal(intent, InputTracker.IsIntentKey((ushort)vk));
    }
}
