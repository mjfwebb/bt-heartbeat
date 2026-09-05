using System;
using System.Threading;
using System.Windows.Forms;

namespace BTHeartbeat;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // Build our own sync context rather than relying on SynchronizationContext.Current
        // — NotifyIcon's window is a raw NativeWindow, not a Control, so it isn't
        // guaranteed to auto-install one, and capturing a null context here means
        // every later Post() below silently no-ops, leaving the tray text stuck
        // on "starting...".
        var uiContext = new WindowsFormsSynchronizationContext();

        using var service = new HeartbeatService();
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

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
