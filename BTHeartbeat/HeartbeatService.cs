using System;
using System.Diagnostics;
using System.Windows.Forms;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace BTHeartbeat;

/// <summary>
/// Watches the default audio render device's active sessions. When every real
/// application session goes inactive (paused / stopped / between streams), starts
/// a silent WASAPI stream of our own so the device (and any Bluetooth A2DP link
/// behind it) never sees true silence and never renegotiates / power-saves.
/// As soon as a real session becomes active again, the heartbeat stops.
///
/// Implemented as a poll loop on WinForms Timers (STA UI thread) rather than
/// WASAPI session/device change events: those callbacks fire on a background COM
/// (MTA) thread, and touching MMDevice/WasapiOut objects created on the main STA
/// thread from that callback thread breaks the COM proxy (QueryInterface
/// E_NOINTERFACE on IMMDevice). Polling avoids the cross-apartment issue entirely.
///
/// Two timers, deliberately: the fast one reuses a single cached AudioSessionManager
/// and just re-reads its Sessions each tick. Re-creating the whole MMDevice ->
/// AudioSessionManager -> SessionCollection COM chain from scratch on every tick
/// (the original approach) leaked — the finalizer thread can't release COM RCWs as
/// fast as a 150ms loop can allocate new ones, and the process's working set grew
/// unbounded (observed ~4.5GB after a few hours). Device-change detection (rare) is
/// split onto its own slow timer so that expensive rebind only happens when needed.
/// </summary>
public sealed class HeartbeatService : IDisposable
{
    private const int SessionPollIntervalMs = 150;
    private const int DevicePollIntervalMs = 2000;

    public event Action<string>? StatusChanged;

    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly int _ownPid = Environment.ProcessId;
    private readonly System.Windows.Forms.Timer _sessionTimer;
    private readonly System.Windows.Forms.Timer _deviceTimer;

    private MMDevice? _device;
    private AudioSessionManager? _sessionManager;
    private string? _boundDeviceId;
    private WasapiOut? _heartbeatOut;
    private bool _disposed;

    public HeartbeatService()
    {
        _sessionTimer = new System.Windows.Forms.Timer { Interval = SessionPollIntervalMs };
        _sessionTimer.Tick += (_, _) => PollSessions();

        _deviceTimer = new System.Windows.Forms.Timer { Interval = DevicePollIntervalMs };
        _deviceTimer.Tick += (_, _) => PollDevice();
    }

    public void Start()
    {
        PollDevice();
        _sessionTimer.Start();
        _deviceTimer.Start();
    }

    private void PollDevice()
    {
        MMDevice? device;
        try
        {
            device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch (Exception ex)
        {
            StopHeartbeat("no output device");
            RebindTo(null, null);
            Report($"No default render device: {ex.Message}");
            return;
        }

        if (device.ID == _boundDeviceId)
        {
            device.Dispose(); // same device already bound, don't need this fresh instance
            return;
        }

        // Default device changed (e.g. Bluetooth headset reconnected) — drop any
        // heartbeat bound to the old device and rebind the cached chain to the new one.
        RebindTo(device, device.ID);
        Report($"Watching device: {device.FriendlyName}");
    }

    private void RebindTo(MMDevice? device, string? deviceId)
    {
        StopHeartbeat();
        _device?.Dispose();
        _device = device;
        _sessionManager = device?.AudioSessionManager;
        _boundDeviceId = deviceId;
    }

    private void PollSessions()
    {
        if (_device is null || _sessionManager is null) return;

        bool anyRealSessionActive;
        try
        {
            _sessionManager.RefreshSessions();
            anyRealSessionActive = HasActiveRealSession(_sessionManager);
        }
        catch (Exception ex)
        {
            Report($"Session enumeration error: {ex.Message}");
            return;
        }

        if (anyRealSessionActive)
        {
            StopHeartbeat();
        }
        else
        {
            EnsureHeartbeat(_device);
        }
    }

    private bool HasActiveRealSession(AudioSessionManager sessionManager)
    {
        var sessions = sessionManager.Sessions;
        for (int i = 0; i < sessions.Count; i++)
        {
            using var session = sessions[i];
            if (session.GetProcessID == (uint)_ownPid) continue;
            if (session.State == AudioSessionState.AudioSessionStateActive) return true;
        }
        return false;
    }

    private void EnsureHeartbeat(MMDevice device)
    {
        if (_heartbeatOut != null) return; // already running

        try
        {
            var silence = new SilenceProvider(new WaveFormat(48000, 16, 2));
            var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
            output.Init(silence);
            output.Play();
            _heartbeatOut = output;
            Report("Heartbeat ON (silence, keeping link alive)");
        }
        catch (Exception ex)
        {
            Report($"Failed to start heartbeat: {ex.Message}");
        }
    }

    private void StopHeartbeat(string reason = "real audio active")
    {
        if (_heartbeatOut is null) return;
        try
        {
            _heartbeatOut.Stop();
            _heartbeatOut.Dispose();
        }
        catch { /* best effort */ }
        finally
        {
            _heartbeatOut = null;
            Report($"Heartbeat OFF ({reason})");
        }
    }

    private void Report(string message)
    {
        Debug.WriteLine($"[BTHeartbeat] {message}");
        StatusChanged?.Invoke(message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sessionTimer.Stop();
        _sessionTimer.Dispose();
        _deviceTimer.Stop();
        _deviceTimer.Dispose();
        StopHeartbeat();
        _device?.Dispose();
        _enumerator.Dispose();
    }
}
