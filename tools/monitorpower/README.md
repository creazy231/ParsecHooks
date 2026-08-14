# MonitorPower

Turns a monitor's panel off over DDC/CI **without changing the Windows display topology**.

```
tools\monitorpower\build.cmd
tools\monitorpower\MonitorPower.exe list
tools\monitorpower\MonitorPower.exe off DISPLAY33
tools\monitorpower\MonitorPower.exe set 2 DISPLAY33     # 2 = standby instead of 4 = off
```

Match on the **GDI name** (`DISPLAY33`). The monitor description is usually a generic
string like `Generic PnP Monitor`, so matching the model name normally fails — that
mistake shows up as `0 monitor(s) switched`. Run `list` to see the names.

## Why this exists

`disableSecondaryMonitors = true` deactivates the second monitor's display *path*. On
this host that leaves three phantom registrations of the monitor behind, which Windows
re-enumerates every ~10 s, invalidating every Desktop Duplication handle and forcing
Parsec to rebuild its NVENC pipeline — a ~500 ms freeze each time. Full write-up in
`tools/lagwatch/README.md`.

DDC/CI sidesteps that. It talks to the monitor's own firmware over the display cable, so
no `SetDisplayConfig` call happens and no phantom entries are created. Measured with
LagWatch: **35 s, 2101 frames, 0 invalidations** with the panel powered off this way,
versus 5–12 invalidations per 25 s with the path deactivated.

Both panels on this host answer DDC/CI (`VCP 0xD6`, current=1 max=5).

## Waking a panel again

**Waking works, but only while Windows still enumerates the monitor.** The handle comes
from `GetPhysicalMonitorsFromHMONITOR`, so if the monitor is not enumerated there is
nothing to send the command to and `MonitorPower on` reports `0 monitor(s) switched`.

Both states were observed on this host:

- **Enumerated, panel off.** After a reboot the GF270C was listed again and still
  reported `current=4` — Windows saw it, the panel was dark. `MonitorPower on` woke it
  immediately. This is the normal case.
- **Not enumerated.** Right after powering it off, this host dropped the monitor from
  the topology completely. Nothing software-side brought it back: `SetDisplayConfig`
  with `SDC_TOPOLOGY_EXTEND | SDC_FORCE_MODE_ENUMERATION` returned 87, an
  `SC_MONITORPOWER` wake broadcast did nothing, and `pnputil /scan-devices` needs admin.
  A power cycle fixed it — and notably the panel came back *enumerated but still in
  state 4*, which is what made the ordinary wake work.

So `off` is recoverable, but there is a window where it is not. Have physical access to
the monitor's power button the first few times, and do not test this over a remote
session.

## Waking causes a hotplug, which reverts the resolution

Powering a panel back on is a monitor arrival event, and Windows responds by re-applying
its **persisted display database**. Anything set only dynamically is lost:

```
18:31:26  AW3423DWF 3440x1440@165     <- set dynamically
18:31:42  AW3423DWF 1280x800@60       <- reverted when the other panel woke
```

Set modes with `CDS_UPDATEREGISTRY` (or ratify with `SDC_SAVE_TO_DATABASE`) so the
database agrees with what is on screen. Anything wiring this into parsec-hooks has to
handle it: the revert path must restore the resolution too, not just the panel power.

## The next thing to try

Power state **2 (standby)** instead of 4 — lighter, and more likely to keep the monitor
enumerated the whole time, which closes the unrecoverable window above:

```
MonitorPower.exe set 2 DISPLAY2       # then check it is still listed
MonitorPower.exe list
tools\lagwatch\LagWatch.exe --seconds 25
MonitorPower.exe set 1 DISPLAY2       # wakes?
```

If state 2 keeps the monitor enumerated *and* measures clean in LagWatch, that is the
combination worth building into parsec-hooks: on session start put the secondary panel
into standby, on session end wake it and restore the mode. That gives the power and
burn-in saving of `disableSecondaryMonitors` without the stutter.

Note GDI names are not stable across reboots — the panels were `DISPLAY32`/`DISPLAY33`
before a restart and `DISPLAY1`/`DISPLAY2` after. Always check `list` first.
