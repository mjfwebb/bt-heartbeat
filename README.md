# BTHeartbeat

Fixes Bluetooth audio crackle/pop on Windows that happens whenever audio stops
and starts again — pausing/resuming Spotify, switching Twitch streams, any
gap between one app's audio and the next.

## Why this happens

When every audio session on the default output device goes idle, the
Bluetooth A2DP link typically renegotiates or drops into a power-saving
state. When real audio resumes, that renegotiation causes an audible
pop/crackle.

## What it does

A tiny WinForms tray app that polls the default render device's active
WASAPI sessions every 150ms. When no real application session is active
(i.e. there's a gap — paused, stopped, switching streams), it plays a
silent WASAPI stream of its own. That keeps the Bluetooth link "busy"
instead of idle, so it never renegotiates, so there's nothing to crackle
when your real audio resumes. As soon as a real session goes active again,
the heartbeat stream stops.

It follows the OS default output device automatically, so reconnecting or
switching Bluetooth devices is handled without restarting the app.

## Download

Grab the latest self-contained `BTHeartbeat.exe` from the
[Releases](https://github.com/mjfwebb/bt-heartbeat/releases) page — no
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

Produces `publish/BTHeartbeat.exe` — no console window, tray icon only.
Add a shortcut to it in `shell:startup` to run it on login.

## Notes / possible follow-ups

- If digital silence alone doesn't stop the crackle on some Bluetooth
  stacks (a few idle-detect on amplitude, not just stream presence), swap
  `SilenceProvider` in `HeartbeatService.cs` for a very low-level
  (e.g. -70dB) sine wave to force genuinely non-zero PCM through the link.
- Implemented as a poll loop rather than WASAPI session/device change
  events on purpose: those callbacks fire on a background COM (MTA)
  thread, and touching `MMDevice`/`WasapiOut` objects created on the main
  STA thread from that callback thread breaks the COM proxy
  (`QueryInterface` `E_NOINTERFACE` on `IMMDevice`). Polling avoids the
  cross-apartment issue entirely.
