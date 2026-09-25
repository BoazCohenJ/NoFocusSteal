using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NoFocusSteal;

/// <summary>A snapshot of a window and the process that owns it, taken the moment it grabbed the foreground
/// (short-lived focus stealers are often gone a few milliseconds later).</summary>
internal sealed class WindowInfo
{
    // Parts of the Windows shell that legitimately take focus when you open them.
    private static readonly HashSet<string> SystemProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "StartMenuExperienceHost.exe", "SearchHost.exe", "SearchApp.exe", "SearchUI.exe",
        "ShellExperienceHost.exe", "ShellHost.exe", "LockApp.exe", "LogonUI.exe", "consent.exe",
        "TextInputHost.exe", "ScreenClippingHost.exe", "CredentialUIBroker.exe",
    };

    private static readonly HashSet<string> SystemExplorerClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "TaskSwitcherWnd", "MultitaskingViewFrame",
        "XamlExplorerHostIslandWindow", "ForegroundStaging", "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland", "Windows.UI.Core.CoreWindow", "#32768",
    };

    public IntPtr Hwnd;
    public IntPtr RootOwner;
    public uint ProcessId;
    public string ExePath = "";
    public string ExeName = "";
    public string Title = "";
    public string ClassName = "";
    public bool Visible;
    public int? ProcessStartTick;

    public bool IsSystemUI =>
        SystemProcesses.Contains(ExeName)
        || (ExeName.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) && SystemExplorerClasses.Contains(ClassName));

    public string Describe() => string.IsNullOrEmpty(Title) ? $"[{ClassName}]" : Title;

    public static WindowInfo Capture(IntPtr hwnd)
    {
        var info = new WindowInfo
        {
            Hwnd = hwnd,
            RootOwner = Native.GetAncestor(hwnd, Native.GA_ROOTOWNER),
            Visible = Native.IsWindowVisible(hwnd),
        };

        var sb = new StringBuilder(512);
        if (Native.GetWindowText(hwnd, sb, sb.Capacity) > 0) info.Title = sb.ToString();
        sb.Clear();
        if (Native.GetClassName(hwnd, sb, sb.Capacity) > 0) info.ClassName = sb.ToString();

        Native.GetWindowThreadProcessId(hwnd, out info.ProcessId);
        IntPtr process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, info.ProcessId);
        if (process != IntPtr.Zero)
        {
            try
            {
                sb.Clear();
                uint len = (uint)sb.Capacity;
                if (Native.QueryFullProcessImageName(process, 0, sb, ref len))
                {
                    info.ExePath = sb.ToString();
                    info.ExeName = Path.GetFileName(info.ExePath);
                }
                if (Native.GetProcessTimes(process, out long creation, out _, out _, out _) && creation > 0)
                {
                    double ageMs = (DateTime.UtcNow - DateTime.FromFileTimeUtc(creation)).TotalMilliseconds;
                    if (ageMs >= 0 && ageMs < int.MaxValue / 2)
                        info.ProcessStartTick = unchecked(Environment.TickCount - (int)ageMs);
                }
            }
            finally
            {
                Native.CloseHandle(process);
            }
        }
        if (info.ExeName.Length == 0) info.ExeName = info.ProcessId == 4 || info.ProcessId == 0 ? "System" : $"PID {info.ProcessId}";
        return info;
    }

    /// <summary>True if the window no longer exists, is hidden, minimized or cloaked (e.g. on another virtual desktop).</summary>
    public static bool IsGone(IntPtr hwnd)
    {
        if (!Native.IsWindow(hwnd) || !Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd)) return true;
        return Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
    }
}
