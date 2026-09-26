using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NoFocusSteal;

/// <summary>
/// "--steal-test N [method] [repeatMs]": waits N seconds, then forces a window into the foreground with the
/// same tricks misbehaving apps use, so you can see NoFocusSteal block it. With repeatMs it keeps grabbing
/// focus every repeatMs milliseconds, like the worst offenders do. Runs as its own process on purpose.
/// </summary>
internal static class StealTest
{
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    public static void Run(int delaySeconds, string method, int repeatMs = 0)
    {
        var form = new Form
        {
            Text = "NoFocusSteal test window",
            AutoScaleMode = AutoScaleMode.Dpi,
            AutoScaleDimensions = new SizeF(96f, 96f),
            Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 11f),
            ClientSize = new Size(460, 170),
            StartPosition = FormStartPosition.CenterScreen,
            Icon = TrayApp.LoadIcon(new Size(32, 32)),
        };
        form.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            Text = "This window just tried to steal focus.\r\n\r\n" +
                   "If you were typing somewhere else and your keystrokes stayed there, NoFocusSteal blocked it. " +
                   "This window closes itself in 20 seconds.",
        });

        var delay = new Timer { Interval = Math.Max(1, delaySeconds) * 1000 };
        delay.Tick += (_, _) =>
        {
            delay.Stop();
            form.Show();
            Steal(form.Handle, method);
            if (repeatMs > 0)
            {
                var again = new Timer { Interval = Math.Max(100, repeatMs) };
                again.Tick += (_, _) => Steal(form.Handle, method);
                again.Start();
            }
            var close = new Timer { Interval = 20000 };
            close.Tick += (_, _) => Application.Exit();
            close.Start();
        };
        delay.Start();
        Application.Run();
    }

    private static void Steal(IntPtr hwnd, string method)
    {
        if (method == "alt" || method == "both")
        {
            // Fake an Alt press so Windows thinks this process got the last input.
            keybd_event(0x12, 0, 0, UIntPtr.Zero);
            Native.SetForegroundWindow(hwnd);
            keybd_event(0x12, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
        if (method == "attach" || method == "both")
        {
            // Borrow the foreground thread's input state, then take over.
            IntPtr fg = Native.GetForegroundWindow();
            uint fgThread = Native.GetWindowThreadProcessId(fg, out _);
            uint me = Native.GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != me && Native.AttachThreadInput(me, fgThread, true);
            Native.BringWindowToTop(hwnd);
            Native.SetForegroundWindow(hwnd);
            if (attached) Native.AttachThreadInput(me, fgThread, false);
        }
    }
}
