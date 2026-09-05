# BTHeartbeat

Fixes Bluetooth audio crackle/pop on Windows that happens whenever audio stops
and starts again: pausing/resuming Spotify, switching Twitch streams, any
gap between one app's audio and the next.

## Why this happens

When every audio session on the default output device goes idle, the
Bluetooth A2DP link typically renegotiates or drops into a power-saving
state. When real audio resumes, that renegotiation causes an audible
pop/crackle.

## What it does

A tiny WinForms tray app that keeps a silent WASAPI shared-mode stream open
on the default output device. That keeps the Bluetooth link "busy" instead
of idle, so it never renegotiates, so there's nothing to crackle when your
real audio starts after a gap. Mixing zeros alongside real audio costs
nothing, so the stream just runs continuously.

The one exception: if the output has been silent for a while (default 15
minutes), the heartbeat is released so your headset can go to sleep instead
of draining its battery all night. It restarts automatically the moment any
audio plays again. That first resume may crackle once; everything after it
is clean.

It follows the OS default output device automatically, so reconnecting or
switching Bluetooth devices is handled without restarting the app.

## Options

```
BTHeartbeat.exe [--idle-timeout <seconds>] [--debug-meter]
```

- `--idle-timeout <seconds>`: how long the output must be silent before the
  heartbeat is released. `0` disables the release entirely (heartbeat runs
  as long as the app does). Default: `900` (15 minutes).
- `--debug-meter`: log the raw endpoint meter reading on every tick to
  stderr. Only useful under `dotnet run` when diagnosing idle detection.

## Download

Grab the latest self-contained `BTHeartbeat.exe` from the
[Releases](https://github.com/mjfwebb/bt-heartbeat/releases) page. No
.NET install required, just run it.

## Build & run

Requires .NET 9 SDK.

```
cd BTHeartbeat
dotnet build
dotnet run
```

Tray icon shows current status (hover for tooltip, right-click for menu).
Right-click → Exit to quit.

## Publish a standalone build

```
dotnet publish -c Release -r win-x64 --self-contained false -o publish
```

Produces `publish/BTHeartbeat.exe`: no console window, tray icon only.
Add a shortcut to it in `shell:startup` to run it on login.

## Design notes

- **The steady-state tick allocates nothing.** Two earlier iterations
  enumerated WASAPI audio sessions on a timer to decide when to run the
  heartbeat, and both leaked memory (one hit ~4.5GB). NAudio's session
  wrappers (`SessionCollection`, `AudioSessionControl`) don't release their
  COM interfaces deterministically; they die on the finalizer thread, and
  releasing STA-affined COM objects from there is slow enough that the
  queue grows faster than it drains. The current design reads a single
  float from a cached `IAudioMeterInformation` and checks a `PlaybackState`
  enum per tick. COM objects are only created on rare transitions (start,
  device change, idle release/resume), each with one owner that disposes it.
- **Polling, not COM callbacks.** WASAPI session/device notifications fire
  on a background COM (MTA) thread, and touching `MMDevice`/`WasapiOut`
  objects created on the main STA thread from there fails with
  `QueryInterface` `E_NOINTERFACE` on `IMMDevice`. Everything runs on the
  STA UI thread via WinForms Timers instead.
- **Silence threshold is 1e-4, not 0.** Some audio engines / enhancement
  APOs add dither or a noise floor to the meter. On the test machine the
  meter reads an exact `0.000000` when nothing is playing, but a hard zero
  would never trigger idle release on a machine where it doesn't.
- If digital silence alone doesn't stop the crackle on some Bluetooth
  stacks (a few idle-detect on amplitude, not just stream presence), swap
  `SilenceProvider` in `HeartbeatService.cs` for a very low-level
  (e.g. -70dB) sine wave to force genuinely non-zero PCM through the link.
