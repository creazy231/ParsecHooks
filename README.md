# parsec-hooks

A tray utility for **Windows Parsec hosts**. While a Parsec client is connected it:

- **disables every monitor except the one you keep** (default: the Windows primary), and
- **turns HDR off** — but only on displays where HDR was actually on.

When the last client disconnects it restores the **exact** prior display topology (positions,
resolutions, fractional refresh rates) and the prior HDR state.

**Resolution is deliberately left to Parsec.** Parsec already switches the host to the
connecting client's resolution and re-enforces that choice roughly every 10 seconds, so
anything this tool sets gets overridden and the screen visibly flaps between the two. All we do
is put the mode back if our *own* HDR/topology changes disturbed it. See trap 4.

Single ~90 KB executable. **No dependencies, no downloads, no admin rights, no console window.**

---

## Why there are no third-party tools here

Everything is done through the Win32 **CCD** (Connecting and Configuring Displays) API:

| Job | Mechanism |
|---|---|
| Enable/disable individual monitors | `QueryDisplayConfig` + `SetDisplayConfig` |
| Read HDR state | `DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2` (falls back to `..._GET_ADVANCED_COLOR_INFO`) |
| Set HDR state | `DISPLAYCONFIG_DEVICE_INFO_SET_HDR_STATE` (falls back to `..._SET_ADVANCED_COLOR_STATE`) |
| Put a mode back after our own changes | `EnumDisplaySettingsEx` + `ChangeDisplaySettingsEx` |
| Detect connect/disconnect | tail Parsec's own `log.txt` |
| Tray icon | WinForms `NotifyIcon` |

So there is no need for `HdrSwitcher`, `HDRCmd`, `HDRTray`, `HDRSwitch`, `Win+Alt+B` simulation,
the `DisplayConfig` PowerShell module, NirSoft `MultiMonitorTool`/`ControlMyMonitor`,
`DisplaySwitch.exe`, or AutoHotkey.

`DisplaySwitch /internal` would in any case have been the wrong tool: it resets your monitor
arrangement and cannot restore a custom layout.

---

## Requirements

- Windows 10 1709+ / Windows 11 (developed and verified on **Windows 11 25H2, build 26200**)
- .NET Framework 4.x — already present on every supported Windows install
- Parsec host installed and hosting under **your** user account

No admin rights are needed. Changing display topology, resolution and HDR all work unelevated,
which is why setup registers a plain `HKCU` Run value rather than a scheduled task, and never
triggers a UAC prompt.

---

## Build, install, remove

```bat
build.cmd        :: compiles bin\ParsecHooks.exe using the C# compiler inside Windows
install.cmd      :: registers it to start at logon and launches it
uninstall.cmd    :: reverts any active tweaks, stops it, removes the registration
```

`build.cmd` uses `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`. Because that is the
.NET **Framework** compiler, the source is deliberately restricted to **C# 5** — no string
interpolation, no `?.`, no expression-bodied members. Keep it that way or the build breaks.

`app.manifest` is embedded via `/win32manifest` for DPI awareness (so the settings dialog
renders sharp rather than bitmap-stretched) and a `supportedOS` block. If you edit it, note
that **XML comments may not contain a double hyphen** — an invalid manifest makes Windows
refuse to start the exe at all with "the side-by-side configuration is incorrect".

Auto-start uses `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` rather than a
Startup-folder shortcut, so the settings checkbox can toggle it without COM interop. An
existing shortcut from an older install is migrated automatically on first launch, so the app
is never registered twice.

---

## Tray menu

| Item | What it does |
|---|---|
| *(top line)* | current state and connected client count |
| **Settings…** | the settings dialog (also opened by double-clicking the icon) |
| **Show status…** | live display list, HDR per display, and the saved baseline |
| **Apply tweaks now** | applies immediately, for testing |
| **Revert now** | escape hatch — restore displays and HDR right now |
| **Pause automation** | stop reacting to connects until unchecked |
| **Reload config from file** | re-read the ini by hand (normally unnecessary, see below) |
| **Open parsec-hooks log** / **Open Parsec log** | jump to either log |
| **Exit** | reverts first, then quits |

Icon: grey = idle, green = tweaks applied, amber = paused.

## Settings dialog

Everything is configurable by clicking; you never have to open the ini. Three tabs:

- **Displays & HDR** — which display to keep (a dropdown listing your actual monitors with
  resolutions and target ids, so there is nothing to type), whether to switch the others off,
  whether to turn HDR off, and the HDR scope.
- **Timing** — the apply/revert/settle delays and the re-assert guard.
- **Advanced** — start-at-logon toggle, tray notifications, log verbosity, poll and
  re-snapshot intervals, an override for the Parsec log path with a file picker, and buttons
  that open either log or the config file.

A live status line at the bottom always shows the current display count, which displays have
HDR on, and whether Parsec and its log were found.

**OK** and **Apply** save and take effect immediately — no restart. Changes made *outside* the
app are picked up too: the config file's timestamp is polled, so editing the ini by hand or
running `ParsecHooks.exe --settings` from elsewhere reloads automatically within a second.
"Reload config from file" therefore exists only as a manual nudge.

The start-at-logon checkbox also reports when auto-start is registered but has been switched
off in **Task Manager → Startup apps**, which Windows records separately from the registry
value — without that check the box would claim auto-start is on while Windows ignores it.

### If something goes wrong

```bat
bin\ParsecHooks.exe --revert     :: restore displays and HDR, then exit
bin\ParsecHooks.exe --settings    :: open just the settings dialog
```

`--revert` restores from the state file that is written *before* any change is applied, and
works even if the tray app is dead. Failing that, `Win`+`P` → **Extend**, or Windows Display
settings.

---

## Configuration

Use **Settings…** in the tray menu. The reference below describes the underlying
`bin\parsec-hooks.ini`, which is created with documented defaults on first run and rewritten
(comments and all) whenever you save from the dialog.

| Key | Default | Meaning |
|---|---|---|
| `keep` | `primary` | Display to keep on: `primary`, a CCD target id, or a substring of the monitor name / device path |
| `disableSecondaryMonitors` | `true` | Turn the other displays off during a session |
| `disableHdr` | `true` | Turn HDR off during a session (only where it was on) |
| `hdrScope` | `kept` | `kept` = only displays left enabled; `all` = every active display |
| `applyDelayMs` | `1200` | Wait after `connected.` before touching displays |
| `revertDelayMs` | `2000` | Wait after the last `disconnected.` before reverting; also debounces reconnects |
| `settleMs` | `400` | Pause between the HDR change and the topology change |
| `pollMs` | `750` | Parsec log poll interval |
| `baselineRefreshMs` | `15000` | How often to re-snapshot the good topology (**only while idle**) |
| `guardMs` | `5000` | Re-assert interval while a session is active; `0` disables |
| `logPath` | *(blank)* | Override the Parsec log path; blank = auto-detect |
| `logLevel` | `info` | `debug` / `info` / `warn` / `error` |
| `notifications` | `true` | Tray balloon tips |

Its own log lives at `%LOCALAPPDATA%\parsec-hooks\parsec-hooks.log` (rotates at 1 MB).

### Reading the log when a session misbehaves

Every display change is traced, whoever caused it, which is what makes "it reverted after a
while" diagnosable rather than guesswork:

```
Apply: state before -> 2 active  |  AW3423DWF* 3440x1440@165 HDR=ON  |  GF270C 1920x1080@180 HDR=off
HDR off on AW3423DWF (tgt 4353) via SET_HDR_STATE
disabled 1 display(s): GF270C (tgt 4355)
resolution 3440x1440@165 -> 1280x800@165 -> OK on AW3423DWF (tgt 4353)
Apply: state after  -> 1 active  |  AW3423DWF* 1280x800@165 HDR=off

display settings CHANGED (EXTERNAL - not initiated by parsec-hooks) -> 2 active  | ...
            expected: 1 active, kept display at 1280x800, HDR off on 1 display(s), topology enforced
guard: resolution drifted to 3440x1440@165, wanted 1280x800; restoring
[external display change] state now 1 active  |  AW3423DWF* 1280x800@165 HDR=off
```

`(EXTERNAL - not initiated by parsec-hooks)` is the key marker: it means something else moved
your displays, and the lines after it show what was corrected. `(ours)` means the change was
one of ours and is expected. Set `logLevel = debug` to also see `CHANGING` events and debounce
decisions.

---

## How it works, and the traps found while building it

Everything below was measured on the target machine, not assumed.

### 1. HDR changes undo transient topology changes — order matters

This is the single most important finding. **Changing HDR makes Windows re-apply its
*persisted* display layout.** A topology change applied without `SDC_SAVE_TO_DATABASE` is
therefore silently reverted by any later HDR change. Measured active-display counts:

```
disable secondary   -> 1 1 1 1 1 1
then HDR off        -> 2 2 2 2 2 2 2 2      <-- monitor came back
```

So **HDR is always changed first and the topology change is always last** — on apply, on
revert, on crash recovery, and in `--revert`.

`SDC_SAVE_TO_DATABASE` would also hold the change, but it is deliberately **not** used:
persisting "secondary disabled" means a crash plus a reboot leaves you with a dead monitor and
Windows agreeing that this is correct. Leaving the database untouched means even a hard
power-off recovers by itself. The cost of that choice is that *other* events (opening Display
settings, a driver event, another app toggling HDR) can also re-light the monitors mid-session
— which is what `guardMs` exists to notice and correct.

### 2. `SDC_ALLOW_CHANGES` lets `SetDisplayConfig` rewrite your arrays

With `SDC_ALLOW_CHANGES`, `SetDisplayConfig` may modify the path/mode arrays you pass it. So
validating with the same arrays you then apply makes the validation pass overwrite your intent
— it rewrites the config back to what is already active, and the subsequent `SDC_APPLY`
reports **success while changing nothing**. Both calls therefore get throwaway copies, and the
long-lived baseline snapshot is never handed to the API directly.

Because of this class of bug, every topology change is verified against
`QueryDisplayConfig` afterwards rather than trusting the return code.

### 3. Never build a topology change from a stale snapshot

Disabling a monitor means calling `SetDisplayConfig` with path **and mode** arrays, so those
arrays carry a resolution whether you meant them to or not. The first version built them by
cloning the idle baseline, which quietly made this tool fight Parsec:

```
                        encode_x/encode_y     host_sys_display_1_width
without parsec-hooks    1280 x 800            1280 x 800
with parsec-hooks       3440 x 1440           3440 x 1440   <-- baseline re-applied
```

Parsec matched the client correctly, then ~1.2s later our apply overwrote it back to the idle
resolution, and the guard re-asserted that stale snapshot every 5s so Parsec could never win.
It also showed up as three `DXGI_ERROR_ACCESS_LOST` encoder restarts per session instead of one.

The fix is that both the apply and the guard now build the reduced topology from a **live**
`QueryDisplayConfig`, so they only ever clear active flags and never carry a resolution of their
own. Snapshots are used for one thing only: restoring on disconnect.

### 4. Do not touch resolution at all — Parsec polices it every ~10 seconds

Parsec sets the host to the client's resolution *before* we even see the connect line, and then
re-enforces that choice on a ~10 second cycle (its log shows `hosting: Initial /me interval set
to 10.000000 seconds`). Anything we set is overridden, and if we in turn re-assert ours, the two
of us trade the mode back and forth for the whole session:

```
19:48:37  resolution 3440x1440@165 -> 1280x800@60 -> OK      (our apply)
19:48:48  CHANGED -> 3440x1440@165
19:48:52  guard: resolution drifted; restoring
19:48:53  guard: resolution restored (1280x800@165 -> OK)
19:48:56  CHANGED[EXT] -> 3440x1440@165
19:49:07  ...  19:49:17  ...  19:49:27  ...                  every ~10s, indefinitely
```

So there is **no resolution setting at all**. The only mode call left is a repair: our HDR and
topology changes reset the mode as a side effect (traps 1, 5, 6), so we put back exactly what
was there beforehand — Parsec's own value, refresh rate included. That preserves Parsec's choice
rather than competing with it.

Two earlier attempts are worth recording as dead ends, because both look reasonable:
- *Forcing a size, e.g. `1280x800`.* Without a refresh rate that resolves to the panel maximum
  (@165) while Parsec wants @60, so Parsec re-enforces forever.
- *Forcing size and rate, and holding it.* Still fights, because Windows' persisted-layout
  re-apply and Parsec's own cycle both keep moving it.

**But not touching it is not sufficient either — the state has to be ratified.** Our own
sequence leaves the live state and the display database disagreeing:

1. HDR off → Windows re-applies the database → the mode reverts to native.
2. We persist the reduced topology — *while the mode is still that clobbered native one*.
3. We repair the mode back to Parsec's value, but `ChangeDisplaySettingsEx` with `flags = 0` is
   transient.

Net result: the database says native, the screen says 1280x800, and Windows re-asserts the
database every ~10 seconds. Measured, with Parsec only slowly winning it back:

```
20:00:57  put the client's 1280x800@60 back      (our repair, transient)
20:01:07  CHANGED -> 3440x1440@165               Windows re-applies the database
20:01:42  CHANGED[EXT] -> 1280x800@60            Parsec restores it, 35s later
20:01:47  CHANGED -> 3440x1440@165               and Windows reverts it again
```

So after our changes settle we **ratify**: re-apply the current configuration with
`SDC_SAVE_TO_DATABASE` and no modifications. That makes live == database, leaving Windows
nothing to revert. It never chooses a mode — it only makes whatever is on screen authoritative,
including a resolution Parsec has just set, which is why it also makes a mid-session change from
the client stick instead of being undone.

### 5. Resolution changes are safe after a topology change, unlike HDR

Measured, because the HDR result above made it obvious not to assume. Disabling the secondary
and then changing the mode holds fine:

```
disable secondary  -> 1 1 1 1 1 1
then mode change   -> 1 1 1 1 1 1 1 1 1 1     (topology unaffected)
```

So the apply order is **HDR, then topology, then resolution**, and revert unwinds it in the
mirror order. The mode change uses `dwflags = 0`, which is dynamic and not written to the
registry — same reasoning as not using `SDC_SAVE_TO_DATABASE`.

### 6. A transient topology change makes Windows fight you forever

This is the one that mattered most in practice. Applying the session topology *without*
`SDC_SAVE_TO_DATABASE` leaves the display database disagreeing with reality, and Windows then
re-applies the database **unprompted, roughly every 10–12 seconds**:

```
19:28:05  Apply: state after -> 1 active | AW3423DWF* 1280x800@60 HDR=off
19:28:18  display settings CHANGED (EXTERNAL) -> 2 active | 3440x1440@165
```

Each of those re-applies re-enables the monitor *and* resets the resolution (see trap 6). The
guard won the monitor back but the resolution stayed lost, so a real Steam Deck session flapped
between 1280x800 and 3440x1440 indefinitely.

The session topology is therefore applied **with** `SDC_SAVE_TO_DATABASE`, which removes the
disagreement so there is nothing left for Windows to restore. The cost is that a hard power-off
mid-session boots with the monitor still disabled; that is covered by the applied-state file
(restored at logon) and by `--revert`. Reverting always persists the baseline back.

One consequence worth knowing: because the persisted layout during a session is the *reduced*
one, the HDR restore at the start of a revert triggers a re-apply of that reduced layout, which
can land after the baseline restore and silently switch the monitor off again. The baseline
restore therefore verifies and retries up to three times.

### 7. The persisted-layout re-apply restores the persisted MODE too

This is what made a working session silently fall back to native resolution after a few
minutes. The re-apply described in trap 1 does not merely re-enable monitors — it also puts
the resolution back. Measured with an HDR toggle as the trigger:

```
set 1280x800 (transient)   -> 1280x800  1280x800  1280x800  1280x800
then toggle HDR            -> 3440x1440 3440x1440 3440x1440 ...
```

An HDR change is only the most reproducible trigger; Windows also does this for driver and
monitor events. So *anything* can knock a session's resolution back to native at any moment.

Consequently the guard **does** hold the resolution — but only when `sessionResolution` was
explicitly set, i.e. only when you asked for a specific value. When it is left blank we restore
whatever mode was in effect before our own changes (so our HDR/topology work does not throw away
Parsec's choice) but never fight a later change, leaving Parsec and the client free to
renegotiate mid-session.

And when a resolution *is* configured without a refresh rate, we reuse the rate that was already
in effect rather than the panel maximum. Parsec asks for 1280x800**@60**; imposing
1280x800**@165** instead made Parsec re-enforce its own choice on a ~10 second cycle, which is
its own flavour of flapping.

Waiting up to `guardMs` for that correction is visible, so corrections are also driven from
`SystemEvents.DisplaySettingsChanged`, which fires immediately on any display change from any
source. The handler debounces (one persisted-layout re-apply raises several events) and skips
changes we caused ourselves, tracked with a nesting counter.

### 8. Parsec rewrites your primary display's mode mid-session

`metrics_host.json` showed display 1 running at `1280x800@60` against a maximum of
`3440x1440` — Parsec had matched the host's primary display to the connecting Steam Deck.

So a snapshot taken *at connect time* can capture Parsec's already-modified mode and later
"restore" you to 1280x800. The baseline is therefore only ever captured **while no client is
connected**, refreshed on a timer and immediately on becoming idle. If the app starts while a
session is already live it declines to snapshot at all and stays hands-off until the session
ends.

### 9. Log location does not follow the install type

This machine has a **per-machine** install (`C:\Program Files\Parsec`) but a **per-user** log
(`%APPDATA%\Parsec\log.txt`); `%ProgramData%\Parsec` does not exist. `parsecd.exe` runs as the
logged-in user, launched by the Parsec service. Both locations are probed, `%APPDATA%` first.

**Run this as your normal user, not as SYSTEM**, or `%APPDATA%` resolves to the wrong profile.

### 10. Matching connect lines

```
[I 2026-04-21 19:07:40] someone#1234567 connected.
[I 2026-08-10 20:11:05] someone#1234567 disconnected.
[F 2026-08-11 17:23:28] ===== Parsec: Started =====
```

The matcher is anchored, case-sensitive, restricted to the `[I]` level, and requires a
`name#digits` user token. Against 5151 lines of real history: 5 connects, 5 disconnects,
perfectly balanced, zero false positives.

It deliberately rejects `[D] IPC AS Client Connected.` — which occurs **84 times** in that log
and would fire on any case-insensitive "ends with connected." check — plus
`UPNP: ... reported as not connected`, `Client '...' went dormant`, and `disconnected.` lines.

`===== Parsec: Started =====` is treated as "all previous sessions are dead", both when
tailing and when reconciling at startup.

### 11. Other robustness details

- **Log rotation**: Parsec rotates `log.txt` at roughly 1 MB. A shrinking file is detected and
  re-read from the start; a stateful UTF-8 decoder handles multi-byte characters split across
  read boundaries.
- **Startup reconciliation**: only the log tail since the last restart marker is replayed, so
  starting at boot does not replay months of history and starting mid-session is not blind.
- **Multiple clients**: tracked as a set. Tweaks apply once on 0→1 and revert only on →0.
- **Crash safety**: the state file is written *before* changes are announced and deleted on
  revert. Finding it at startup means the previous run died mid-session, and it is restored
  immediately.
- **Never blacks out everything**: if the `keep` selector matches no display, the change is
  refused and logged.
- **Parsec disappearing**: if `parsecd.exe` vanishes while clients are tracked, that is
  treated as a disconnect.
- Reverts also run on **Exit** and on **logoff/shutdown**.

---

## Known limitations

- **Windows on a disabled monitor move to the remaining display and do not move back.** This
  is how Windows behaves when a display is disabled; it is not something the app undoes. It is
  the accepted trade-off for the upside that your remote cursor cannot wander onto an
  off-screen desktop. If you would rather avoid it, set
  `disableSecondaryMonitors = false` and keep only the HDR behaviour.
- **`host_output` is not decoded.** Parsec's `config.json` pins which monitor it streams
  (`"host_output": "<opaque-id>"` here) and the format is undocumented. So "keep
  whichever monitor Parsec is streaming" cannot be done reliably — hence `keep = primary`.
  If you stream a non-primary display, set `keep` to that monitor's name or target id.
- **Aspect ratio is only fixed if you set a resolution.** Keeping a 21:9 primary at its native
  mode gives a 16:10 client a letterboxed image. Either let Parsec match the client (blank
  `sessionResolution`) or pick a 16:10 mode such as `1280x800`. A virtual display would handle
  it more cleanly still, but that needs a driver and is out of scope here.
- **A forced `sessionResolution` is held for the whole session.** If you want the client to be
  able to change the stream resolution mid-session instead, leave `sessionResolution` blank;
  then resolution is never touched and Parsec negotiates it freely.
- Parsec logs `FRAME: DXGI_ERROR_ACCESS_LOST` about a second after each connect and
  immediately re-acquires capture. That is normal, happens with or without this tool, and is
  why the display change is safe to make mid-session.

---

## Testing

Verified end-to-end on the target machine with a 69-check harness driving a synthetic Parsec
log (`logPath` override) and asserting real display state through an independent probe binary:
noise rejection, single and multi-client sequencing, exact geometry restoration, an 8-second
stability hold, deliberate external HDR drift and guard re-assertion, reconnect debouncing,
kill-while-applied crash recovery, log rotation, log deletion, log re-detection, automatic
reload of an externally edited config, auto-start registration, and a full resolution cycle:

```
before:                displays=2 res=3440x1440 hdr=True
during:                displays=1 res=1280x800  hdr=False
after injected drift:  displays=1 res=1280x800  hdr=False   (all three re-asserted)
after:                 displays=2 res=3440x1440 hdr=True    (secondary still -1920,165 / 179.998Hz)
```

The drift step deliberately reproduces the real-world failure: an external persisted-layout
re-apply is injected mid-session, and the harness asserts that resolution, topology and HDR all
come back.

The settings dialog was verified by launching `--settings`, capturing each tab, and inspecting
the result. That is worth doing rather than assuming, because it caught two layout faults that
compile and run perfectly happily: a `Form` with `AutoSize` around docked children collapsed to
132 px wide, and `FlowLayoutPanel.WrapContents` defaulting to `true` silently wrapped **Cancel**
and **Apply** out of view, leaving a dialog whose only button was OK.
