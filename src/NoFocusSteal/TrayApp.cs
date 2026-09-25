using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;

namespace NoFocusSteal;

internal sealed class TrayApp : ApplicationContext
{
    public const string HomePage = "https://boazcohenj.github.io/NoFocusSteal/";
    private static readonly TimeSpan NotifyCooldown = TimeSpan.FromMinutes(5);

    private readonly Settings _settings;
    private readonly FocusLog _log = new();
    private readonly FocusMonitor _monitor;
    private readonly NotifyIcon _tray;
    private readonly Control _invoker = new();
    private readonly Icon _iconOn, _iconOff;
    private readonly Dictionary<ProtectionMode, ToolStripMenuItem> _modeItems = new();
    private readonly Dictionary<string, DateTime> _lastNotified = new(StringComparer.OrdinalIgnoreCase);
    private readonly ToolStripMenuItem _notifyItem, _startupItem;
    private LogForm _form;

    public TrayApp(bool startHidden)
    {
        _invoker.CreateControl();
        _settings = Settings.Load();
        _monitor = new FocusMonitor(_settings, _log);
        _monitor.Blocked += OnBlocked;

        _iconOn = LoadIcon(SystemInformation.SmallIconSize);
        _iconOff = MakeGray(_iconOn);

        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("Show focus log", null, (_, _) => ShowLog()) { Font = new Font(menu.Font, FontStyle.Bold) };
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());
        AddMode(menu, ProtectionMode.Guard, "Guard: block focus stealing while I type");
        AddMode(menu, ProtectionMode.Strict, "Strict: block everything I didn't ask for");
        AddMode(menu, ProtectionMode.LogOnly, "Log only: just show me who steals focus");
        AddMode(menu, ProtectionMode.Off, "Off");
        menu.Items.Add(new ToolStripSeparator());
        _notifyItem = new ToolStripMenuItem("Notify me when something is blocked", null, (_, _) =>
        {
            _settings.Notify = !_settings.Notify;
            _settings.Save();
            SyncMenu();
        });
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) =>
        {
            try
            {
                Settings.StartWithWindows = !Settings.StartWithWindows;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Couldn't change the startup setting: " + ex.Message, "NoFocusSteal");
            }
            SyncMenu();
        });
        menu.Items.Add(_notifyItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add("Rules...", null, (_, _) => ShowLog().ShowRules());
        menu.Items.Add("Website and help", null, (_, _) => OpenUrl(HomePage));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        menu.Opening += (_, _) => SyncMenu();

        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowLog();
        };
        _tray.BalloonTipClicked += (_, _) => ShowLog();

        _monitor.Start();
        SyncMenu();

        if (_settings.FirstRun)
        {
            _settings.Save();
            _tray.ShowBalloonTip(8000, "NoFocusSteal is running",
                "Apps can no longer grab focus while you type. It lives here in the tray; click for the focus log.",
                ToolTipIcon.Info);
        }
        if (!startHidden) ShowLog();
    }

    public void ShowFromOtherInstance() => _invoker.BeginInvoke(new Action(() => ShowLog()));

    private void AddMode(ContextMenuStrip menu, ProtectionMode mode, string text)
    {
        var item = new ToolStripMenuItem(text, null, (_, _) => SetMode(mode));
        _modeItems[mode] = item;
        menu.Items.Add(item);
    }

    public void SetMode(ProtectionMode mode)
    {
        _settings.Policy.Mode = mode;
        _settings.Save();
        SyncMenu();
        _form?.SyncMode();
    }

    private void SyncMenu()
    {
        foreach (var pair in _modeItems) pair.Value.Checked = pair.Key == _settings.Policy.Mode;
        _notifyItem.Checked = _settings.Notify;
        try
        {
            _startupItem.Checked = Settings.StartWithWindows;
        }
        catch
        {
            _startupItem.Checked = false;
        }

        bool on = _settings.Policy.Mode is ProtectionMode.Guard or ProtectionMode.Strict;
        _tray.Icon = on ? _iconOn : _iconOff;
        _tray.Text = "NoFocusSteal: " + _settings.Policy.Mode switch
        {
            ProtectionMode.Guard => "guarding while you type",
            ProtectionMode.Strict => "strict protection",
            ProtectionMode.LogOnly => "logging only",
            _ => "off",
        };
    }

    private LogForm ShowLog()
    {
        if (_form == null || _form.IsDisposed)
        {
            _form = new LogForm(_settings, _log, this) { Icon = LoadIcon(new Size(32, 32)) };
            if (!_monitor.InputRegistered)
                _form.SetStatus("Warning: couldn't watch keyboard and mouse input, so nothing will be blocked.");
        }
        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.Activate();
        return _form;
    }

    private void OnBlocked(LogEntry e)
    {
        if (!_settings.Notify) return;
        string exe = e.Window.ExeName;
        if (_lastNotified.TryGetValue(exe, out var last) && DateTime.Now - last < NotifyCooldown) return;
        _lastNotified[exe] = DateTime.Now;
        _tray.ShowBalloonTip(5000, "Blocked " + exe,
            $"It tried to grab focus ({e.Reason}). Your window kept it; the app's taskbar button flashes instead.",
            ToolTipIcon.None);
    }

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // No default browser; nothing sensible to do.
        }
    }

    public static Icon LoadIcon(Size size)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("NoFocusSteal.app.ico");
        return stream != null ? new Icon(stream, size) : SystemIcons.Application;
    }

    private static Icon MakeGray(Icon icon)
    {
        using var source = icon.ToBitmap();
        using var gray = new Bitmap(source.Width, source.Height);
        using (var g = Graphics.FromImage(gray))
        {
            var matrix = new ColorMatrix(new[]
            {
                new[] { 0.3f, 0.3f, 0.3f, 0, 0 },
                new[] { 0.59f, 0.59f, 0.59f, 0, 0 },
                new[] { 0.11f, 0.11f, 0.11f, 0, 0 },
                new[] { 0f, 0, 0, 0.75f, 0 },
                new[] { 0f, 0, 0, 0, 1 },
            });
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(matrix);
            g.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height), 0, 0, source.Width, source.Height,
                GraphicsUnit.Pixel, attributes);
        }
        IntPtr handle = gray.GetHicon();
        var result = (Icon)Icon.FromHandle(handle).Clone();
        Native.DestroyIcon(handle);
        return result;
    }

    protected override void ExitThreadCore()
    {
        _monitor.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _form?.CloseForReal();
        base.ExitThreadCore();
    }
}
