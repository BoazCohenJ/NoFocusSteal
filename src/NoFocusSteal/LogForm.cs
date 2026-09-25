using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace NoFocusSteal;

internal sealed class LogForm : Form
{
    private static readonly Color BlockedColor = Color.FromArgb(255, 222, 222);
    private static readonly Color WouldBlockColor = Color.FromArgb(255, 233, 204);
    private static readonly Color UnsolicitedColor = Color.FromArgb(255, 247, 204);

    private readonly Settings _settings;
    private readonly FocusLog _log;
    private readonly TrayApp _app;
    private readonly ListView _list;
    private readonly ComboBox _mode;
    private readonly CheckBox _suspiciousOnly;
    private readonly ToolStripStatusLabel _status;
    private readonly ContextMenuStrip _rowMenu;
    private bool _reallyClose;
    private bool _syncing;

    private static readonly (ProtectionMode Mode, string Text)[] Modes =
    {
        (ProtectionMode.Guard, "Guard: block focus stealing while I type"),
        (ProtectionMode.Strict, "Strict: block everything I didn't ask for"),
        (ProtectionMode.LogOnly, "Log only: just show me who steals focus"),
        (ProtectionMode.Off, "Off"),
    };

    public LogForm(Settings settings, FocusLog log, TrayApp app)
    {
        _settings = settings;
        _log = log;
        _app = app;

        Text = "NoFocusSteal - focus log";
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        Font = SystemFonts.MessageBoxFont;
        ClientSize = new Size(980, 520);
        MinimumSize = new Size(640, 320);
        StartPosition = FormStartPosition.CenterScreen;

        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(8, 8, 8, 4),
            WrapContents = true,
        };
        top.Controls.Add(new Label { Text = "Protection:", AutoSize = true, Margin = new Padding(0, 7, 4, 0) });
        _mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300, Margin = new Padding(0, 3, 12, 3) };
        foreach (var m in Modes) _mode.Items.Add(m.Text);
        _mode.SelectedIndexChanged += (_, _) =>
        {
            if (!_syncing && _mode.SelectedIndex >= 0) _app.SetMode(Modes[_mode.SelectedIndex].Mode);
        };
        top.Controls.Add(_mode);

        _suspiciousOnly = new CheckBox { Text = "Only suspicious", AutoSize = true, Margin = new Padding(0, 6, 12, 0) };
        _suspiciousOnly.CheckedChanged += (_, _) => Reload();
        top.Controls.Add(_suspiciousOnly);

        top.Controls.Add(MakeButton("Test it", (_, _) => RunTest()));
        top.Controls.Add(MakeButton("Rules...", (_, _) => ShowRules()));
        top.Controls.Add(MakeButton("Open CSV log", (_, _) => OpenCsv()));
        top.Controls.Add(MakeButton("Clear", (_, _) =>
        {
            _log.Clear();
            Reload();
        }));
        top.Controls.Add(MakeButton("Help", (_, _) => TrayApp.OpenUrl(TrayApp.HomePage)));

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false,
        };
        _list.Columns.Add("Time", 90);
        _list.Columns.Add("Result", 90);
        _list.Columns.Add("App", 150);
        _list.Columns.Add("Window", 260);
        _list.Columns.Add("Reason", 380);

        _rowMenu = new ContextMenuStrip();
        _rowMenu.Opening += OnRowMenuOpening;
        _list.ContextMenuStrip = _rowMenu;

        var statusStrip = new StatusStrip();
        _status = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        statusStrip.Items.Add(_status);

        Controls.Add(_list);
        Controls.Add(top);
        Controls.Add(statusStrip);

        _log.Added += OnAdded;
        SyncMode();
        Reload();
    }

    private static Button MakeButton(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Margin = new Padding(0, 2, 6, 2) };
        b.Click += onClick;
        return b;
    }

    public void SyncMode()
    {
        _syncing = true;
        _mode.SelectedIndex = Array.FindIndex(Modes, m => m.Mode == _settings.Policy.Mode);
        _syncing = false;
    }

    public void SetStatus(string text) => _status.Text = text;

    private void OnAdded(LogEntry e)
    {
        if (IsDisposed) return;
        if (!_suspiciousOnly.Checked || e.IsSuspicious)
        {
            _list.BeginUpdate();
            _list.Items.Insert(0, MakeItem(e));
            while (_list.Items.Count > 1000) _list.Items.RemoveAt(_list.Items.Count - 1);
            _list.EndUpdate();
        }
        UpdateSummary();
    }

    private void Reload()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        var rows = _log.Entries.Reverse().Where(e => !_suspiciousOnly.Checked || e.IsSuspicious).Take(1000);
        _list.Items.AddRange(rows.Select(MakeItem).ToArray());
        _list.EndUpdate();
        UpdateSummary();
    }

    private static ListViewItem MakeItem(LogEntry e)
    {
        string reason = e.Note.Length > 0 ? e.Reason + " " + e.Note : e.Reason;
        var item = new ListViewItem(new[]
        {
            e.Time.ToString("HH:mm:ss"),
            e.VerdictText,
            e.Window.ExeName,
            e.Window.Describe(),
            reason,
        })
        { Tag = e, ToolTipText = e.Window.ExePath };
        item.BackColor = e.Verdict switch
        {
            Verdict.Blocked => BlockedColor,
            Verdict.WouldBlock => WouldBlockColor,
            Verdict.Unsolicited => UnsolicitedColor,
            _ => item.BackColor,
        };
        return item;
    }

    private void UpdateSummary()
    {
        var entries = _log.Entries;
        int blocked = entries.Count(e => e.Verdict == Verdict.Blocked);
        var suspicious = entries.Where(e => e.IsSuspicious).ToList();
        string top = suspicious.GroupBy(e => e.Window.ExeName, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).Select(g => $"{g.Key} ({g.Count()})").FirstOrDefault();
        SetStatus($"{entries.Count} focus changes this session, {blocked} blocked, {suspicious.Count} suspicious."
                  + (top != null ? " Top suspect: " + top : ""));
    }

    private LogEntry SelectedEntry => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as LogEntry : null;

    private void OnRowMenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _rowMenu.Items.Clear();
        LogEntry entry = SelectedEntry;
        if (entry == null)
        {
            e.Cancel = true;
            return;
        }
        string exe = entry.Window.ExeName;
        AppRule rule = _settings.RuleFor(exe);
        _rowMenu.Items.Add(new ToolStripMenuItem($"Always allow {exe} to take focus", null,
            (_, _) => SetRule(exe, AppRule.Allow)) { Checked = rule == AppRule.Allow });
        _rowMenu.Items.Add(new ToolStripMenuItem($"Block {exe} even when I'm not typing", null,
            (_, _) => SetRule(exe, AppRule.Block)) { Checked = rule == AppRule.Block });
        if (rule != AppRule.Default)
            _rowMenu.Items.Add($"Remove rule for {exe}", null, (_, _) => SetRule(exe, AppRule.Default));
        _rowMenu.Items.Add(new ToolStripSeparator());
        if (File.Exists(entry.Window.ExePath))
            _rowMenu.Items.Add("Open file location", null, (_, _) =>
                Process.Start("explorer.exe", "/select,\"" + entry.Window.ExePath + "\""));
        _rowMenu.Items.Add("Copy details", null, (_, _) => Clipboard.SetText(Details(entry)));
    }

    private void SetRule(string exe, AppRule rule)
    {
        _settings.SetRule(exe, rule);
        _settings.Save();
    }

    private static string Details(LogEntry e) =>
        $"Time: {e.Time:yyyy-MM-dd HH:mm:ss.fff}\r\n" +
        $"Result: {e.VerdictText} ({e.Reason}{(e.Note.Length > 0 ? " " + e.Note : "")})\r\n" +
        $"App: {e.Window.ExeName} (PID {e.Window.ProcessId})\r\n" +
        $"Path: {e.Window.ExePath}\r\n" +
        $"Window: {e.Window.Title} [{e.Window.ClassName}]{(e.Window.Visible ? "" : " (invisible)")}\r\n" +
        $"Previous: {e.Previous?.ExeName} - {e.Previous?.Title}";

    public void ShowRules()
    {
        using var dialog = new RulesForm(_settings);
        dialog.ShowDialog(this);
    }

    private void RunTest()
    {
        var result = MessageBox.Show(this,
            "In 5 seconds a test window will try to steal focus, the same way misbehaving apps do.\r\n\r\n" +
            "After pressing OK, click into another app (Notepad, a browser text box...) and keep typing.\r\n\r\n" +
            "With protection on, your typing stays where it is and the test window's taskbar button flashes instead.",
            "Test NoFocusSteal", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
        if (result != DialogResult.OK) return;
        Process.Start(Application.ExecutablePath, "--steal-test 5");
        WindowState = FormWindowState.Minimized;
    }

    private static void OpenCsv()
    {
        if (File.Exists(FocusLog.CsvPath))
            Process.Start("explorer.exe", "/select,\"" + FocusLog.CsvPath + "\"");
        else
            MessageBox.Show("Nothing has been logged yet.", "NoFocusSteal");
    }

    public void CloseForReal()
    {
        _reallyClose = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing the window just hides it; the app keeps protecting from the tray.
        if (!_reallyClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _log.Added -= OnAdded;
        base.OnFormClosing(e);
    }
}
