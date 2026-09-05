using System;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

namespace BTHeartbeat;

internal static class Program
{
    private static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(15);

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // Install our own sync context rather than relying on SynchronizationContext.Current.
        // NotifyIcon's window is a raw NativeWindow, not a Control, so it isn't
        // guaranteed to auto-install one, and a null context here means every later
        // Post() silently no-ops, leaving the tray text stuck on "starting...".
        var uiContext = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(uiContext);

        using var service = new HeartbeatService(ParseIdleTimeout(args))
        {
            DebugMeter = Array.Exists(args, a => a.Equals("--debug-meter", StringComparison.OrdinalIgnoreCase)),
        };
        using var trayIcon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Information,
            Text = "BT Heartbeat: starting...",
            Visible = true,
        };

        var menu = new ContextMenuStrip();
        var statusItem = new ToolStripMenuItem("Status: starting...") { Enabled = false };
        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => Application.Exit();
        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);
        trayIcon.ContextMenuStrip = menu;

        service.StatusChanged += message =>
        {
            // NotifyIcon/menu updates must happen on the UI thread.
            uiContext.Post(_ =>
            {
                trayIcon.Text = Truncate($"BT Heartbeat: {message}", 63); // NotifyIcon.Text max length
                statusItem.Text = $"Status: {message}";
            }, null);
        };

        service.Start();

        Application.Run();
    }

    /// <summary>
    /// `--idle-timeout &lt;seconds&gt;` overrides how long the output must be silent before
    /// the heartbeat is released to let a headset sleep. 0 disables the timeout
    /// (heartbeat never releases). Defaults to 15 minutes.
    /// </summary>
    private static TimeSpan ParseIdleTimeout(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--idle-timeout", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                && seconds >= 0)
            {
                return seconds == 0 ? TimeSpan.MaxValue : TimeSpan.FromSeconds(seconds);
            }
        }
        return DefaultIdleTimeout;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
