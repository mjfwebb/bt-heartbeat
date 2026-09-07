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

A tiny Win32 tray app that keeps a silent WASAPI shared-mode stream open
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
  stderr, for diagnosing idle detection. The published exe is a GUI binary
  with no console attached, so this is only visible under `dotnet run` or
  with stderr redirected: `BTHeartbeat.exe --debug-meter 2> log.txt`.

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
The menu has a "Start with Windows" toggle (registers the exe in the HKCU
Run key) and Exit.

## Publish a standalone build

```
dotnet publish -c Release -r win-x64 -o publish
```

Produces `publish/BTHeartbeat.exe`: no console window, tray icon only,
about 2MB. Run it once and tick "Start with Windows" in the tray menu to
have it launch on login.

The project sets `PublishAot`, so this is a native compilation: a single exe
with no .NET runtime to carry. It needs the Visual Studio C++ build tools
("Desktop development with C++") locally; GitHub's `windows-latest` runner
already has them. Native code cannot be cross-compiled, so the release build
needs a Windows machine, which this app requires anyway.

For reference, the same app was 68MB self-contained, 13MB trimmed, and 2.1MB
with AOT.

AOT and trimming (which `PublishAot` implies) are both safe only because of
NAudio 3's source-generated COM interop. If the NAudio reference is ever moved
back to 2.x, both must come off with it: built-in `[ComImport]` interop
survives neither, and the resulting build still starts and shows a tray icon
while every WASAPI call fails and no heartbeat runs. Check such a build by
hovering the tray icon: it must read "Heartbeat ON", not "starting..." or "No
default render device". The process staying alive proves nothing on its own.

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
  STA thread that owns the message loop, driven by `WM_TIMER`.
- **No WinForms.** The tray icon, menu, timers and message loop are plain
  Win32 (`TrayShell.cs`), because Windows Forms drags the whole
  WindowsDesktop runtime pack into a self-contained publish and supports
  neither trimming nor Native AOT. Nothing here ever
  created a `Form`, so the only things to replace were `Shell_NotifyIcon`,
  `TrackPopupMenuEx`, `SetTimer` and `GetMessage`. The interop is
  source-generated (`LibraryImport`) with a `[UnmanagedCallersOnly]` window
  procedure, so no P/Invoke stub is built at runtime.
- **Silence threshold is 1e-4, not 0.** Some audio engines / enhancement
  APOs add dither or a noise floor to the meter. On the test machine the
  meter reads an exact `0.000000` when nothing is playing, but a hard zero
  would never trigger idle release on a machine where it doesn't.
- If digital silence alone doesn't stop the crackle on some Bluetooth
  stacks (a few idle-detect on amplitude, not just stream presence), swap
  `SilenceProvider` in `HeartbeatService.cs` for a very low-level
  (e.g. -70dB) sine wave to force genuinely non-zero PCM through the link.
