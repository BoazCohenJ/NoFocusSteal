using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NoFocusSteal;

internal sealed class LogEntry
{
    public DateTime Time;
    public Verdict Verdict;
    public string Reason = "";
    public WindowInfo Window;
    public WindowInfo Previous;
    public string Note = "";

    public bool IsSuspicious => Verdict != Verdict.Allowed;

    public string VerdictText => Verdict switch
    {
        Verdict.Allowed => "Allowed",
        Verdict.Unsolicited => "Unsolicited",
        Verdict.Blocked => "Blocked",
        Verdict.WouldBlock => "Would block",
        _ => Verdict.ToString(),
    };
}

/// <summary>Keeps recent focus changes in memory and appends every one to a CSV file for later digging.</summary>
internal sealed class FocusLog
{
    private const int MaxInMemory = 2000;
    private const long MaxFileBytes = 5 * 1024 * 1024;

    private readonly List<LogEntry> _entries = new();

    public static string CsvPath => Path.Combine(Settings.DataDir, "focus-log.csv");

    public event Action<LogEntry> Added;

    public IReadOnlyList<LogEntry> Entries => _entries;

    public void Add(LogEntry e)
    {
        _entries.Add(e);
        if (_entries.Count > MaxInMemory) _entries.RemoveRange(0, _entries.Count - MaxInMemory);
        AppendCsv(e);
        Added?.Invoke(e);
    }

    public void Clear() => _entries.Clear();

    private static void AppendCsv(LogEntry e)
    {
        try
        {
            Directory.CreateDirectory(Settings.DataDir);
            var file = new FileInfo(CsvPath);
            if (file.Exists && file.Length > MaxFileBytes)
            {
                string old = CsvPath + ".old";
                if (File.Exists(old)) File.Delete(old);
                file.MoveTo(old);
            }
            bool header = !File.Exists(CsvPath);
            using var w = new StreamWriter(CsvPath, true, new UTF8Encoding(true));
            if (header)
                w.WriteLine("Time,Result,Process,PID,Path,WindowTitle,WindowClass,Visible,PreviousProcess,PreviousTitle,Reason");
            w.WriteLine(string.Join(",",
                e.Time.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                e.VerdictText,
                Csv(e.Window.ExeName),
                e.Window.ProcessId,
                Csv(e.Window.ExePath),
                Csv(e.Window.Title),
                Csv(e.Window.ClassName),
                e.Window.Visible ? "yes" : "no",
                Csv(e.Previous?.ExeName ?? ""),
                Csv(e.Previous?.Title ?? ""),
                Csv(e.Reason + (e.Note.Length > 0 ? " " + e.Note : ""))));
        }
        catch
        {
            // Logging to disk is best effort; the in-memory log still works.
        }
    }

    private static string Csv(string s)
    {
        // Window titles are arbitrary text; keep spreadsheet apps from treating one as a formula.
        if (s.Length > 0 && "=+-@".IndexOf(s[0]) >= 0) s = "'" + s;
        return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
