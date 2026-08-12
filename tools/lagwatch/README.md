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
long re-creating it takes. Alongside that it samples the display mode, the foreground
window, and tails Parsec's own log so its `FRAME: DXGI_ERROR_ACCESS_LOST` lines land on
the same timeline.

Because it is independent of Parsec, it answers the question Parsec's log cannot: is the
capture handle being invalidated system-wide, or is Parsec doing something to itself?
And because a run takes 25 seconds, it turns "play for a while and see if it feels
laggy" into a measurement you can A/B against a single change.

At the end it prints an interval histogram, which is what makes a periodic cause obvious.

## The bug this was built for, and its cause

**Symptom.** Streaming to a Steam Deck, the picture froze for roughly half a second,
recovered for about a second, froze again, then ran clean for several seconds — forever.
Changing the bitrate between 5 and 25 Mbps made no difference.

**Cause: `disableSecondaryMonitors = true`.** When parsec-hooks leaves exactly one
active display, this host's Desktop Duplication handle is invalidated in pairs roughly
every 9.2 seconds. With two displays active it is never invalidated. That is the whole
bug — and it was self-inflicted by this project.

**Fix.** `disableSecondaryMonitors = false` in `parsec-hooks.ini`.

## Why it costs half a second

Losing the duplication handle is cheap in itself — LagWatch rebuilds one in 20–30 ms.
The expensive part is that Parsec rebuilds its whole NVENC pipeline each time:

```
[I 20:21:29] FRAME: DXGI_ERROR_ACCESS_LOST
[D 20:21:30] dxgi          = 1.5
[D 20:21:30] Using modern NVENC preset.
[D 20:21:30] encoder       = nvidia
[D 20:21:30] encode_x      = 1280
[D 20:21:31] [0] FPS:39.5/88, L:2.0/48.5, ...      <- 88 frames dropped
```

Two invalidations about a second apart therefore read as: freeze, brief recovery,
freeze, then several clean seconds. Exactly the reported symptom.

## The measurements

Every row is a 22–40 s LagWatch run on the same machine during a live session.

| Active displays | Other conditions | ACCESS_LOST |
| --- | --- | --- |
| 1 | 1280x800@60, HDR off | 12 |
| 1 | discord_clips killed | 6 |
| 1 | ParsecHooks killed | 8 |
| 1 | NVIDIA container + GameBar killed | 5 |
| 1 | 1280x800@165 | 10 |
| 1 | `server_resolution_x/y` removed | 8 |
| 1 | 3440x1440@165 (native) | 6 |
| **2** | 3440x1440@165, HDR on | **0** |
| **2** | 1280x800@60, HDR off | **0** |
| **2** | 3440x1440@165, HDR on, 40 s / 6596 frames | **0** |

## What was ruled out, and how

- **The network.** The stalls are `DXGI_ERROR_ACCESS_LOST` in the capture layer. Bitrate
  is irrelevant, which is why 5 Mbps and 25 Mbps behaved identically.
- **Parsec being at fault.** LagWatch's independent duplication was invalidated at the
  same instants Parsec reported, to the millisecond — so Parsec is a victim, not the
  cause.
- **`discord_clips`, ParsecHooks' own timers, the NVIDIA App container (ShadowPlay /
  overlay), `GameBarPresenceWriter`.** Killed individually; the 9.2 s cadence never
  changed.
- **Resolution and refresh rate.** The stalls occur at both 1280x800@60 and the panel's
  native 3440x1440@165. An early hypothesis blamed the forced non-native mode; it was
  wrong, and removing `server_resolution_x`/`server_resolution_y` changed nothing.
- **HDR.** Clean runs happened with HDR both on and off.
- **The game being fullscreen.** Stalls occurred with the game focused and with VS Code
  focused.

A note on the ParsecHooks test, because it nearly sent the investigation the wrong way:
force-killing ParsecHooks did *not* stop the stalls, which looked exonerating. It only
proved ParsecHooks was not doing something on a timer — killing it left the topology at
one display, so the actual trigger stayed in place.

## Caveat

The mechanism behind "one active display invalidates duplication every ~9.2 s" is not
identified. Suspects not yet tested: `host_output` in Parsec's config pins capture to a
specific output (`535410-1998695304`), which may misbehave once that output's siblings
are gone; and this host has two virtual display drivers installed (Parsec VDD 0.45 and
SudoMaker). If you want the single-display setup back, removing `host_output` so Parsec
selects the output itself is the next thing to try — and a 25-second LagWatch run will
tell you immediately whether it worked.

## Unrelated but worth knowing

The Application log recorded `LiveKernelEvent 141` (GPU engine timeout) bursts at Parsec
start and at client connect. Those did not track the 9.2 s cadence, so they are not the
cause of this stutter, but repeated GPU engine timeouts are their own problem.
