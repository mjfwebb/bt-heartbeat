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
/// Implemented as a poll loop on a WinForms Timer (STA UI thread) rather than
/// WASAPI session/device change events: those callbacks fire on a background COM
/// (MTA) thread, and touching MMDevice/WasapiOut objects created on the main STA
/// thread from that callback thread breaks the COM proxy (QueryInterface
/// E_NOINTERFACE on IMMDevice). Polling avoids the cross-apartment issue entirely.
/// </summary>
public sealed class HeartbeatService : IDisposable
{
    private const int PollIntervalMs = 150;

    public event Action<string>? StatusChanged;

    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly int _ownPid = Environment.ProcessId;
    private readonly System.Windows.Forms.Timer _timer;

    private string? _boundDeviceId;
    private WasapiOut? _heartbeatOut;
    private bool _disposed;

    public HeartbeatService()
    {
        _timer = new System.Windows.Forms.Timer { Interval = PollIntervalMs };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start()
    {
        Poll();
        _timer.Start();
    }

    private void Poll()
    {
        MMDevice? device;
        try
        {
            device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch (Exception ex)
        {
            StopHeartbeat();
            _boundDeviceId = null;
            Report($"No default render device: {ex.Message}");
            return;
        }

        using (device)
        {
            if (device.ID != _boundDeviceId)
            {
                // Default device changed (e.g. Bluetooth headset reconnected) — drop
                // any heartbeat bound to the old device before switching over.
                StopHeartbeat();
                _boundDeviceId = device.ID;
                Report($"Watching device: {device.FriendlyName}");
            }

            bool anyRealSessionActive;
            try
            {
                anyRealSessionActive = HasActiveRealSession(device);
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
                EnsureHeartbeat(device);
            }
        }
    }

    private bool HasActiveRealSession(MMDevice device)
    {
        var sessions = device.AudioSessionManager.Sessions;
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
            var freshDevice = _enumerator.GetDevice(device.ID); // fresh COM instance, owned by the output
            var output = new WasapiOut(freshDevice, AudioClientShareMode.Shared, true, 100);
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

    private void StopHeartbeat()
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
            Report("Heartbeat OFF (real audio active)");
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
        _timer.Stop();
        _timer.Dispose();
        StopHeartbeat();
        _enumerator.Dispose();
    }
}
