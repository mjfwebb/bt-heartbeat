using System;
using System.Diagnostics;
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
/// first resume may crackle once, an accepted trade-off for battery).
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
///  - All WASAPI calls happen on the STA UI thread via message-pump timers (see
///    <see cref="ITimerScheduler"/>). COM callbacks (session/device notifications)
///    arrive on an MTA thread, and touching STA-created MMDevice/WasapiPlayer objects
///    from there fails with E_NOINTERFACE, so we poll instead of subscribing.
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
    private readonly ITimerScheduler _timers;
    private IDisposable? _meterTimer;
    private IDisposable? _deviceTimer;

    // Bound default device. Owns _meter; both replaced together on device change.
    private MMDevice? _device;
    private AudioMeterInformation? _meter;
    private string? _boundDeviceId;

    // Heartbeat stream. Has its own MMDevice instance so the player's lifetime is
    // independent of _device and of NAudio's internal AudioClient caching.
    private WasapiPlayer? _heartbeatPlayer;
    private MMDevice? _heartbeatDevice;

    private DateTime _lastRealAudioUtc = DateTime.UtcNow;
    // Whether the current "cannot reach the default device" run has been reported.
    // Cleared when a device binds, so a later disconnect reports again.
    private bool _reportedNoDevice;
    private bool _idle;
    private bool _disposed;

    public HeartbeatService(ITimerScheduler timers, TimeSpan idleTimeout)
    {
        _timers = timers;
        IdleTimeout = idleTimeout;
    }

    public void Start()
    {
        Guard(PollDevice, "device poll");
        _meterTimer = _timers.Schedule(MeterPollIntervalMs, () => Guard(PollMeter, "meter poll"));
        _deviceTimer = _timers.Schedule(DevicePollIntervalMs, () => Guard(PollDevice, "device poll"));
    }

    /// <summary>
    /// An unhandled exception on a timer tick would unwind into the Win32 message
    /// dispatcher and leave the app dead in the tray. Everything the timers run goes
    /// through here so a transient WASAPI failure is just a status line.
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
                // Lost the device we were bound to.
                StopHeartbeat("no output device");
                Rebind(null);
                Report($"No default render device: {ex.Message}");
                _reportedNoDevice = true;
            }
            else if (!_reportedNoDevice)
            {
                // Nothing has ever bound, so there is no heartbeat to tear down. Do
                // still report it: returning silently here is how a broken WASAPI
                // stack (a mis-trimmed build being the likely cause, see the
                // PublishTrimmed note in the csproj) leaves the tray reading
                // "starting..." forever, indistinguishable from a working build.
                // Once only - the poll repeats every 2s and the message won't change.
                _reportedNoDevice = true;
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
        _reportedNoDevice = false;
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
        // WasapiPlayer stops on its own thread and PlaybackState leaves Playing.
        // Tear it down here so the restart logic below can bring it back.
        if (_heartbeatPlayer != null && _heartbeatPlayer.PlaybackState != PlaybackState.Playing)
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
            Console.Error.WriteLine($"[BTHeartbeat] meter peak={peak:0.000000} realAudio={realAudio} idle={_idle} heartbeat={(_heartbeatPlayer != null ? "on" : "off")}");
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

        if (!_idle && _heartbeatPlayer is null)
        {
            StartHeartbeat();
        }
    }

    private void StartHeartbeat()
    {
        if (_heartbeatPlayer != null || _boundDeviceId is null) return;

        MMDevice? device = null;
        WasapiPlayer? player = null;
        try
        {
            device = _enumerator.GetDevice(_boundDeviceId);
            // Shared mode, event sync, 100ms: the same stream WasapiOut opened before
            // this moved to WasapiPlayer. Deliberately not low latency - a heartbeat of
            // zeros has no deadline to meet, and IAudioClient3 would only pin the engine
            // to a smaller period and wake this process more often for nothing.
            player = new WasapiPlayerBuilder()
                .WithDevice(device)
                .WithSharedMode()
                .WithEventSync()
                .WithLatency(100)
                .Build();
            player.Init(new SilenceProvider(new WaveFormat(48000, 16, 2)));
            player.Play();
            _heartbeatPlayer = player;
            _heartbeatDevice = device;
            Report("Heartbeat ON (silent stream keeping link alive)");
        }
        catch (Exception ex)
        {
            player?.Dispose();
            device?.Dispose();
            Report($"Failed to start heartbeat: {ex.Message}");
        }
    }

    private void StopHeartbeat(string reason)
    {
        if (_heartbeatPlayer is null) return;
        try
        {
            _heartbeatPlayer.Stop();
            _heartbeatPlayer.Dispose();
        }
        catch { /* best effort */ }
        finally
        {
            _heartbeatPlayer = null;
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
        _meterTimer?.Dispose();
        _deviceTimer?.Dispose();
        StopHeartbeat("shutting down");
        Rebind(null);
        _enumerator.Dispose();
    }
}
