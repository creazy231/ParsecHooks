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

## The warning

**`off` is currently a one-way trip.**

Power state 4 made this host drop the monitor from the display topology entirely. Once
that happens the monitor is no longer enumerated, so `GetPhysicalMonitorsFromHMONITOR`
returns no handle for it and there is nothing left to send an "on" command to.
`MonitorPower on` reports `0 monitor(s) switched` and cannot recover it. Neither
`SetDisplayConfig` with `SDC_TOPOLOGY_EXTEND | SDC_FORCE_MODE_ENUMERATION` (returns 87)
nor an `SC_MONITORPOWER` wake broadcast brought it back. `pnputil /scan-devices` needs
admin and was not tried elevated.

Recovery was a power cycle of the PC.

Because of that, **do not wire `off` into an automatic apply/revert flow yet.** A
revert that cannot run is worse than not disabling the monitor at all.

## The next thing to try

Power state **2 (standby)** instead of 4. It is a lighter state and the monitor may stay
enumerated on the link, which would keep a handle available for waking it:

```
MonitorPower.exe set 2 DISPLAY33      # then check it is still listed
MonitorPower.exe list
tools\lagwatch\LagWatch.exe --seconds 25
MonitorPower.exe set 1 DISPLAY33      # can it be woken?
```

If state 2 both keeps the monitor enumerated and measures clean in LagWatch, that is the
combination worth building into parsec-hooks: on session start, put the secondary panel
into standby; on session end, wake it. That would give the power and burn-in saving of
`disableSecondaryMonitors` without the stutter.

Test it with the PC in front of you, not over a remote session.
