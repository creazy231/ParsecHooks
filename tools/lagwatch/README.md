# LagWatch

A Desktop Duplication canary for diagnosing periodic Parsec host stalls.

Build with `build.cmd` (uses the in-box `csc.exe`, like the main project — no SDK needed),
then run it from a terminal:

```
tools\lagwatch\build.cmd
tools\lagwatch\LagWatch.exe --seconds 25
tools\lagwatch\LagWatch.exe --seconds 120 --csv run.csv
```

## What it does

It holds its own Desktop Duplication of the primary output and reports, to the
millisecond, every time that handle is invalidated (`DXGI_ERROR_ACCESS_LOST`) and how
long re-creating it takes. Alongside it samples the display mode, **every display device
including ones not attached to the desktop**, the foreground window, and tails Parsec's
log so its `FRAME: DXGI_ERROR_ACCESS_LOST` lines land on the same timeline.

That "not attached to the desktop" part is the whole trick. The first version of this
tool only counted displays that were part of the desktop, and was therefore blind to the
event that actually causes the bug.

Because it is independent of Parsec, it answers the question Parsec's log cannot: is the
capture handle being invalidated system-wide, or is Parsec doing something to itself? A
run takes 25 seconds, so it turns "play for a while and see if it feels laggy" into an
A/B test. It prints an interval histogram at the end.

## The bug this was built for

**Symptom.** Streaming to a Steam Deck, the picture froze for ~500 ms, recovered for
about a second, froze again, then ran clean for several seconds — forever. Bitrate made
no difference.

**Cause.** `disableSecondaryMonitors = true` deactivates the second monitor's display
path while the monitor is still powered on and connected. Windows then leaves **three
phantom registrations of that monitor** behind and re-enumerates them on a ~10 s cycle.
Every appearance and disappearance invalidates every Desktop Duplication handle on the
machine. LagWatch catches it directly:

```
21:13:01.046  DEVICE_CHANGE  DISPLAY32+[Alienware] | DISPLAY33-[PnP] | DISPLAY34-[PnP] | DISPLAY35-[PnP]
                         ->  DISPLAY32+[Alienware]                        <- three devices vanish
21:13:01.063  ACCESS_LOST #1                                              <- 17 ms later
21:13:01.985  ACCESS_LOST #2  (+0.923s)
21:13:02.014  DEVICE_CHANGE  ...  -> DISPLAY33/34/35 all return
```

All three resolve to the same monitor instance — `MONITOR\HDY2725\...\0003`, the GF270C.

**Fix in this repo:** `disableSecondaryMonitors = false`.

## Why it costs half a second

Losing the handle is cheap — LagWatch rebuilds one in 20–30 ms. Parsec rebuilds its
whole NVENC pipeline each time:

```
[I 20:21:29] FRAME: DXGI_ERROR_ACCESS_LOST
[D 20:21:30] Using modern NVENC preset.
[D 20:21:30] encoder       = nvidia
[D 20:21:31] [0] FPS:39.5/88, L:2.0/48.5, ...      <- 88 frames dropped
```

Two invalidations ~1 s apart read as: freeze, brief recovery, freeze, then several clean
seconds.

## The measurements

Each row is a 22–40 s LagWatch run on the same machine during a live session.

| Second monitor | Other conditions | ACCESS_LOST |
| --- | --- | --- |
| deactivated | 1280x800@60 | 12 |
| deactivated | `discord_clips` killed | 6 |
| deactivated | ParsecHooks killed | 8 |
| deactivated | NVIDIA container + GameBar killed | 5 |
| deactivated | 1280x800@165 | 10 |
| deactivated | `server_resolution_x/y` removed | 8 |
| deactivated | native 3440x1440@165 | 6 |
| deactivated | **plus a live Parsec virtual display** | 11 |
| attached | 3440x1440@165, HDR on | **0** |
| attached | 1280x800@60, HDR off | **0** |
| attached | 40 s / 6596 frames | **0** |
| **powered off via DDC/CI** | 35 s / 2101 frames | **0** |

The last two rows are the interesting pair. "Deactivated but powered" stalls; "actually
powered off" does not. It is not about how many displays are active — it is about
leaving a live monitor deactivated in the display config.

## What was ruled out, and how

- **The network.** The stalls are `DXGI_ERROR_ACCESS_LOST` in the capture layer. Bitrate
  is irrelevant, which is why 5 and 25 Mbps behaved identically.
- **Parsec being at fault.** LagWatch's independent duplication is invalidated at the
  same instants Parsec reports, to the millisecond. Parsec is a victim.
- **`discord_clips`, ParsecHooks' timers, the NVIDIA App container, `GameBarPresenceWriter`.**
  Killed individually; the cadence never changed.
- **Resolution and refresh rate.** Stalls occur at 1280x800@60 and at native
  3440x1440@165. Removing `server_resolution_x`/`server_resolution_y` changed nothing.
- **HDR.** Clean runs happened with HDR both on and off.
- **The game being fullscreen.** Stalls occurred with the game focused and with an editor
  focused.
- **Display count.** Adding a live Parsec virtual display so two displays were active did
  *not* help — the phantom GF270C entries kept flapping underneath.

Two traps worth recording, because both nearly ended the investigation early:

1. Force-killing ParsecHooks did not stop the stalls, which looked exonerating. It only
   proved ParsecHooks was not firing a timer — killing it left the topology deactivated,
   so the trigger stayed in place.
2. A PowerShell `EnumDisplayDevices` probe passed `$null` for the device name, which
   marshals as an empty string and makes the call fail. It silently reported no devices
   at all. The C# version in this tool passes a real NULL.

## Keeping the second monitor off anyway

See `tools/monitorpower`. Powering the panel down over DDC/CI leaves the display config
untouched, so no phantom entries appear — measured clean. Read the one-way warning in
that README first.

## Unrelated but worth knowing

The Application log recorded `LiveKernelEvent 141` (GPU engine timeout) bursts at Parsec
start and at client connect. They did not track the ~10 s cadence, so they are not this
bug, but repeated GPU engine timeouts are their own problem.
