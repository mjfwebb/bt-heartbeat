using System;
using System.Globalization;
using Microsoft.Win32;

namespace BTHeartbeat;

internal static class Program
{
    private static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// STA is not about UI here: the WASAPI COM objects in HeartbeatService are
    /// STA-affined, and every one of them is created and touched on this thread.
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        using var shell = new TrayShell("BT Heartbeat")
        {
            IsStartupChecked = IsStartupEnabled,
            OnStartupToggled = SetStartup,
        };

        using var service = new HeartbeatService(shell, ParseIdleTimeout(args))
        {
            DebugMeter = Array.Exists(args, a => a.Equals("--debug-meter", StringComparison.OrdinalIgnoreCase)),
        };

        service.StatusChanged += shell.SetStatus;
        service.Start();

        shell.Run();
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

    // ---- startup registration (HKCU Run key, same approach as the other tray apps)

    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "BTHeartbeat";

    private static bool IsStartupEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return k?.GetValue(RunValue) != null;
        }
        catch
        {
            return false;
        }
    }

    private static void SetStartup(bool enable)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (k is null) return;
            // Environment.ProcessPath is the apphost path, same value Application.ExecutablePath
            // returned, and it stays correct under single-file publish.
            if (enable) k.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        catch
        {
            // Registry access denied or similar: the menu re-reads the real state
            // every time it opens, so a failed write simply shows up unchecked.
        }
    }
}
