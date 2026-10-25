using System.Collections.Generic;

namespace NoFocusSteal;

/// <summary>
/// Folds rapid repeats into one log row. A thief that tries several tricks in a row, or retries while it's
/// being sent back, would otherwise fill the log with identical rows a few milliseconds apart. Entries with
/// the same key (window type plus result), each within <see cref="GapMs"/> of the previous one, become the
/// first entry plus a note such as "(3 attempts in 83 ms)". Allowed entries are never held back.
/// </summary>
internal sealed class BurstCollapser
{
    public const int GapMs = 1000;

    private LogEntry _held;
    private string _key;
    private int _first, _last, _count;

    public bool Holding => _held != null;

    /// <summary>Adds an entry and returns the entries that are ready to be logged, in order.</summary>
    public List<LogEntry> Add(LogEntry entry, string key, int time)
    {
        var ready = new List<LogEntry>();
        if (_held != null && key == _key && FocusPolicy.Elapsed(time, _last) <= GapMs)
        {
            _count++;
            _last = time;
            return ready;
        }
        TakeHeld(ready);
        if (entry.Verdict == Verdict.Allowed)
        {
            ready.Add(entry);
            return ready;
        }
        _held = entry;
        _key = key;
        _first = _last = time;
        _count = 1;
        return ready;
    }

    /// <summary>Releases the held entry once its burst has been quiet for <see cref="GapMs"/>.</summary>
    public List<LogEntry> Tick(int now)
    {
        var ready = new List<LogEntry>();
        if (_held != null && FocusPolicy.Elapsed(now, _last) > GapMs) TakeHeld(ready);
        return ready;
    }

    /// <summary>Releases the held entry immediately (for example on exit).</summary>
    public List<LogEntry> Flush()
    {
        var ready = new List<LogEntry>();
        TakeHeld(ready);
        return ready;
    }

    private void TakeHeld(List<LogEntry> ready)
    {
        if (_held == null) return;
        if (_count > 1)
        {
            string note = $"({_count} attempts in {FocusPolicy.Elapsed(_last, _first)} ms)";
            _held.Note = _held.Note.Length == 0 ? note : _held.Note + " " + note;
        }
        ready.Add(_held);
        _held = null;
    }
}
