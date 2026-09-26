using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace NoFocusSteal;

internal static class Program
{
    private const string MutexName = @"Local\NoFocusSteal-5b7e0d2c";
    private const string ShowEventName = @"Local\NoFocusSteal-5b7e0d2c-show";

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        int testIndex = Array.FindIndex(args, a => a.Equals("--steal-test", StringComparison.OrdinalIgnoreCase));
        if (testIndex >= 0)
        {
            int delay = testIndex + 1 < args.Length && int.TryParse(args[testIndex + 1], out int d) ? d : 5;
            string method = testIndex + 2 < args.Length ? args[testIndex + 2].ToLowerInvariant() : "both";
            int repeat = testIndex + 3 < args.Length && int.TryParse(args[testIndex + 3], out int r) ? r : 0;
            StealTest.Run(delay, method, repeat);
            return 0;
        }

        bool startHidden = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

        using var mutex = new Mutex(true, MutexName, out bool firstInstance);
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!firstInstance)
        {
            // Already running: bring up its window instead of starting a second copy.
            if (!startHidden) showEvent.Set();
            return 0;
        }

        var app = new TrayApp(startHidden);
        var listener = new Thread(() =>
        {
            try
            {
                while (showEvent.WaitOne()) app.ShowFromOtherInstance();
            }
            catch (ObjectDisposedException)
            {
                // Shutting down.
            }
        }) { IsBackground = true };
        listener.Start();

        Application.Run(app);
        return 0;
    }
}
