namespace BTHeartbeat;

/// <summary>
/// Schedules repeating callbacks on the single STA thread that owns the message loop.
///
/// This exists because the WASAPI objects in <see cref="HeartbeatService"/> are STA-affined:
/// they must only be touched from the thread that created them, or QueryInterface on
/// IMMDevice fails with E_NOINTERFACE. A message-pump timer (WM_TIMER, previously
/// System.Windows.Forms.Timer) guarantees that; a threadpool timer does not.
/// </summary>
public interface ITimerScheduler
{
    /// <summary>Starts a repeating timer. Dispose the handle to stop it.</summary>
    IDisposable Schedule(int intervalMs, Action onTick);
}
