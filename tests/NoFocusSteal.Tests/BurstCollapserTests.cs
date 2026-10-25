using Xunit;

namespace NoFocusSteal.Tests;

public class BurstCollapserTests
{
    private static LogEntry Entry(Verdict verdict) =>
        new LogEntry { Verdict = verdict, Window = new WindowInfo(), Reason = "took focus while you were typing" };

    [Fact]
    public void RapidRepeatsBecomeOneRowWithACount()
    {
        var b = new BurstCollapser();
        var first = Entry(Verdict.Blocked);
        Assert.Empty(b.Add(first, "thief|Blocked", 1000));
        Assert.Empty(b.Add(Entry(Verdict.Blocked), "thief|Blocked", 1044));
        Assert.Empty(b.Add(Entry(Verdict.Blocked), "thief|Blocked", 1083));
        Assert.Empty(b.Tick(1500));

        var rows = b.Tick(2100);
        Assert.Single(rows);
        Assert.Same(first, rows[0]);
        Assert.Equal("(3 attempts in 83 ms)", rows[0].Note);
    }

    [Fact]
    public void SingleAttemptGetsNoNote()
    {
        var b = new BurstCollapser();
        b.Add(Entry(Verdict.Blocked), "thief|Blocked", 1000);
        var rows = b.Tick(2500);
        Assert.Single(rows);
        Assert.Equal("", rows[0].Note);
    }

    [Fact]
    public void DifferentWindowStartsANewRowAndKeepsOrder()
    {
        var b = new BurstCollapser();
        var a = Entry(Verdict.Blocked);
        b.Add(a, "thief|Blocked", 1000);
        var c = Entry(Verdict.Blocked);
        var rows = b.Add(c, "other|Blocked", 1100);
        Assert.Single(rows);
        Assert.Same(a, rows[0]);
        Assert.Same(c, b.Flush()[0]);
    }

    [Fact]
    public void AllowedEntriesAreLoggedImmediatelyAfterAnyHeldBurst()
    {
        var b = new BurstCollapser();
        var blocked = Entry(Verdict.Blocked);
        b.Add(blocked, "thief|Blocked", 1000);
        var allowed = Entry(Verdict.Allowed);
        var rows = b.Add(allowed, "you|Allowed", 1200);
        Assert.Equal(2, rows.Count);
        Assert.Same(blocked, rows[0]);
        Assert.Same(allowed, rows[1]);
        Assert.False(b.Holding);
    }

    [Fact]
    public void RepeatsFurtherApartThanTheGapAreSeparateRows()
    {
        var b = new BurstCollapser();
        b.Add(Entry(Verdict.Blocked), "thief|Blocked", 1000);
        var rows = b.Add(Entry(Verdict.Blocked), "thief|Blocked", 2500);
        Assert.Single(rows);
        Assert.True(b.Holding);
    }

    [Fact]
    public void ExistingNoteIsKept()
    {
        var b = new BurstCollapser();
        var e = Entry(Verdict.Blocked);
        e.Note = "(invisible window)";
        b.Add(e, "k", 0);
        b.Add(Entry(Verdict.Blocked), "k", 10);
        Assert.Equal("(invisible window) (2 attempts in 10 ms)", b.Flush()[0].Note);
    }
}
