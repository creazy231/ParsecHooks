# 🧪 Tests

```powershell
.\test\run-tests.ps1
```

Integration tests. They drive the **real** `ParsecHooks.exe` against a synthetic Parsec log and
assert **real** display state — never the app's own reporting.

| File | Purpose |
|---|---|
| `run-tests.ps1` | the suite |
| `ProbeDisplay.cs` | a standalone CCD/GDI probe, built automatically into `%TEMP%\parsechooks-tests` |

## How it works

Two deliberate design choices make the results trustworthy:

**A synthetic log, not a real client.** The app's only input is Parsec's `log.txt`, so the suite
points `logPath` at a file it writes itself. That makes connect/disconnect sequences exact and
repeatable — two clients, a reconnect inside the revert delay, a rotation mid-session — none of
which you can stage reliably with real hardware.

**An independent probe for assertions.** `ProbeDisplay.exe` reads the CCD and GDI APIs directly.
If the app claimed success while changing nothing, the suite would still catch it — which is not
hypothetical: `SDC_ALLOW_CHANGES` genuinely makes `SetDisplayConfig` report success without
applying anything, and that bug was found exactly this way.

**Nothing is hardcoded to one machine.** Display count, the primary's mode, every position and
refresh rate, the OS build, and a stand-in "client" resolution are all detected at startup, so
the exact-restoration checks work on any setup.

## What you need

- **2 or more active displays** — topology phases skip otherwise
- **HDR on the primary** — HDR phases skip otherwise
- Run as your normal user, not elevated

Phases that can't run report `SKIP` with a reason rather than failing, and the exit code is the
number of real failures.

> [!WARNING]
> This blanks and re-enables your secondary display several times and toggles HDR. It restores
> everything in a `finally` block and re-persists the layout afterwards, but expect roughly four
> minutes of flickering. Don't run it mid-game.

## Coverage

| Phase | Asserts |
|:--:|---|
| 1 | launch, startup reconciliation, baseline capture, no premature change |
| 2 | noise rejection — `IPC AS Client Connected.`, UPNP, STUN, dormant, wrong log level |
| 3 · 3b | apply, then hold for 8 s without drifting |
| 3c | external HDR drift re-asserted (topology *and* HDR) |
| 4 · 5 | second client causes no extra action; first leaving does **not** revert |
| 6 · 7 | full revert, then every position, size and refresh rate restored exactly |
| 8 | reconnect inside the revert delay cancels the pending revert |
| 9 | killed while applied → state survives, next launch recovers |
| 10 | log rotation, and the immediate idle baseline that follows |
| 11 | log deleted mid-run is tolerated |
| 12 | config edited on disk reloads without a restart |
| 13 | **a client-negotiated resolution survives 30 s** — the ratification regression |
| 14 | auto-start registered once, no duplicate Startup shortcut |

Phase 13 is the important one. It reproduces the failure that took several attempts to pin down:
our own HDR and topology changes reset the display mode as a side effect, and unless the display
database is ratified afterwards, Windows re-applies it every ~10 seconds and the client's
resolution is silently lost. See findings 1–4 in the [main README](../README.md).

## If a run leaves your displays wrong

Teardown restores state even when assertions fail, but if something goes badly wrong:

```powershell
..\bin\ParsecHooks.exe --revert                       # restore from the state file
%TEMP%\parsechooks-tests\ProbeDisplay.exe restore     # restore the last snapshot
```

Then `Win`+`P` → **Extend**, or Windows Display settings.
