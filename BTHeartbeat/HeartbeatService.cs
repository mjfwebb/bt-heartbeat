using System;
using System.Diagnostics;
using System.Windows.Forms;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace BTHeartbeat;

/// <summary>
/// Keeps a silent WASAPI shared-mode stream open on the default render device so
/// the device (and any Bluetooth A2DP link behind it) never sees true silence and
/// never renegotiates / power-saves between one app's audio and the next.
///
/// The stream runs continuously. Mixing zeros alongside real audio costs nothing,
/// and while real audio is playing the link is alive regardless, so there is no
/// benefit in stopping the heartbeat when other apps are active. The only reason
/// to ever stop it is to let a headset idle when nobody is listening: if the
/// endpoint meter reads silence for <see cref="IdleTimeout"/>, the heartbeat is
/// released, and it restarts the moment the meter sees real audio again (that
/// first resume may crackle once — an accepted trade-off for battery).
///
/// Design notes, because two earlier iterations leaked memory:
///  - Do NOT enumerate audio sessions on a timer. Each pass allocates a
///    SessionCollection, an IAudioSessionEnumerator and several QI'd interfaces
///    per session, none of which NAudio releases deterministically. They die on
///    the finalizer thread, releasing STA-affined COM objects from there is slow,
///    and the queue grows faster than it drains (observed ~4.5GB, then ~100MB/h).
///  - The steady-state tick must allocate nothing. It reads one float from a
///    cached IAudioMeterInformation and checks a PlaybackState enum. That's it.
///  - COM objects are only created on rare transitions (start, device change,
///    idle release/resume), and each one has a single owner that disposes it.
///  - All WASAPI calls happen on the STA UI thread via WinForms Timers. COM
///    callbacks (session/device notifications) arrive on an MTA thread, and
///    touching STA-created MMDevice/WasapiOut objects from there fails with
///    E_NOINTERFACE, so we poll instead of subscribing.
/// </summary>
public sealed class HeartbeatService : IDisposable
{
    private const int MeterPollIntervalMs = 500;
    private const int DevicePollIntervalMs = 2000;

    public event Action<string>? StatusChanged;

    /// <summary>How long the endpoint must stay silent before the heartbeat is released.</summary>
    public TimeSpan IdleTimeout { get; }

    /// <summary>Log the raw meter peak on every tick (stderr only). For diagnosing idle detection.</summary>
    public bool DebugMeter { get; init; }

    // Peak at or below this counts as silence. Not exactly 0f: some audio engines /
    // enhancement APOs add dither or a noise floor, so a hard zero can never trigger.
    // 1e-4 is roughly -80 dBFS, far below anything an app deliberately renders.
    private const float SilenceThreshold = 1e-4f;

    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly System.Windows.Forms.Timer _meterTimer;
    private readonly System.Windows.Forms.Timer _deviceTimer;

    // Bound default device. Owns _meter; both replaced together on device change.
    private MMDevice? _device;
    private AudioMeterInformation? _meter;
    private string? _boundDeviceId;

    // Heartbeat stream. Has its own MMDevice instance so WasapiOut's lifetime is
    // independent of _device and of NAudio's internal AudioClient caching.
    private WasapiOut? _heartbeatOut;
    private MMDevice? _heartbeatDevice;

    private DateTime _lastRealAudioUtc = DateTime.UtcNow;
    private bool _idle;
    private bool _disposed;

    public HeartbeatService(TimeSpan idleTimeout)
    {
        IdleTimeout = idleTimeout;

        _meterTimer = new System.Windows.Forms.Timer { Interval = MeterPollIntervalMs };
        _meterTimer.Tick += (_, _) => Guard(PollMeter, "meter poll");

        _deviceTimer = new System.Windows.Forms.Timer { Interval = DevicePollIntervalMs };
        _deviceTimer.Tick += (_, _) => Guard(PollDevice, "device poll");
    }

    public void Start()
    {
        Guard(PollDevice, "device poll");
        _meterTimer.Start();
        _deviceTimer.Start();
    }

    /// <summary>
    /// An unhandled exception inside a WinForms Timer.Tick surfaces as the
    /// ThreadException dialog and leaves the app dead in the tray. Everything the
    /// timers run goes through here so a transient WASAPI failure is just a status line.
    /// </summary>
    private void Guard(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Report($"{what} failed: {ex.Message}");
        }
    }

    private void PollDevice()
    {
        MMDevice device;
        try
        {
            device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch (Exception ex)
        {
            if (_boundDeviceId != null)
            {
                StopHeartbeat("no output device");
                Rebind(null);
                Report($"No default render device: {ex.Message}");
            }
            return;
        }

        if (device.ID == _boundDeviceId)
        {
            device.Dispose(); // same device already bound, don't need this fresh instance
            return;
        }

        // Default device changed (e.g. Bluetooth headset reconnected). Drop the
        // heartbeat bound to the old device and start fresh on the new one.
        StopHeartbeat("device changed");
        Rebind(device);
        Report($"Watching device: {SafeFriendlyName(device)}");
        _lastRealAudioUtc = DateTime.UtcNow;
        _idle = false;
        StartHeartbeat();
    }

    private void Rebind(MMDevice? device)
    {
        _device?.Dispose();
        _device = device;
        _boundDeviceId = device?.ID;
        // Cache the meter ourselves so the steady-state tick never touches an
        // MMDevice property getter that might allocate a new COM wrapper.
        _meter = device?.AudioMeterInformation;
    }

    private void PollMeter()
    {
        if (_device is null || _meter is null) return;

        // If the stream died underneath us (device error, format renegotiation),
        // WasapiOut stops on its own thread and PlaybackState leaves Playing.
        // Tear it down here so the restart logic below can bring it back.
        if (_heartbeatOut != null && _heartbeatOut.PlaybackState != PlaybackState.Playing)
        {
            StopHeartbeat("stream stopped unexpectedly");
        }

        // Our own stream is all zeros, so it never moves this meter. Anything
        // above zero is another app actually rendering audio.
        float peak = _meter.MasterPeakValue;
        bool realAudio = peak > SilenceThreshold;
        var now = DateTime.UtcNow;

        if (DebugMeter)
        {
            Console.Error.WriteLine($"[BTHeartbeat] meter peak={peak:0.000000} realAudio={realAudio} idle={_idle} heartbeat={(_heartbeatOut != null ? "on" : "off")}");
        }

        if (realAudio)
        {
            _lastRealAudioUtc = now;
            if (_idle)
            {
                _idle = false;
                Report("Audio resumed");
            }
        }
        else if (!_idle && now - _lastRealAudioUtc >= IdleTimeout)
        {
            _idle = true;
            StopHeartbeat($"idle for {IdleTimeout.TotalMinutes:0.#} min, letting headset sleep");
        }

        if (!_idle && _heartbeatOut is null)
        {
            StartHeartbeat();
        }
    }

    private void StartHeartbeat()
    {
        if (_heartbeatOut != null || _boundDeviceId is null) return;

        MMDevice? device = null;
        WasapiOut? output = null;
        try
        {
            device = _enumerator.GetDevice(_boundDeviceId);
            output = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
            output.Init(new SilenceProvider(new WaveFormat(48000, 16, 2)));
            output.Play();
            _heartbeatOut = output;
            _heartbeatDevice = device;
            Report("Heartbeat ON (silent stream keeping link alive)");
        }
        catch (Exception ex)
        {
            output?.Dispose();
            device?.Dispose();
            Report($"Failed to start heartbeat: {ex.Message}");
        }
    }

    private void StopHeartbeat(string reason)
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
            _heartbeatDevice?.Dispose();
            _heartbeatDevice = null;
            Report($"Heartbeat OFF ({reason})");
        }
    }

    private static string SafeFriendlyName(MMDevice device)
    {
        // FriendlyName reads the property store and can throw for some endpoints.
        try { return device.FriendlyName; }
        catch { return device.ID; }
    }

    private void Report(string message)
    {
        Debug.WriteLine($"[BTHeartbeat] {message}");
        // No console is attached when run as a tray app, so this is a no-op there;
        // under `dotnet run` it gives a readable log for verifying behaviour.
        Console.Error.WriteLine($"[BTHeartbeat] {DateTime.Now:HH:mm:ss} {message}");
        StatusChanged?.Invoke(message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _meterTimer.Stop();
        _meterTimer.Dispose();
        _deviceTimer.Stop();
        _deviceTimer.Dispose();
        StopHeartbeat("shutting down");
        Rebind(null);
        _enumerator.Dispose();
    }
}
