// HookApp.cs -- tray icon, timers and the connect/disconnect state machine.
// C# 5 only.
//
// Rules below were all measured on Windows 11 25H2 (build 26200), not assumed.
//
// 1. ORDERING. Changing HDR makes Windows re-apply its persisted display layout, so an
//    HDR change AFTER a topology change silently undoes it:
//        disable secondary -> active 1,1,1,1   then HDR off -> 2,2,2,2
//    Therefore HDR is always changed first and topology last.
//
// 2. THE PERSISTED LAYOUT CARRIES THE MODE. That same re-apply also restores the persisted
//    resolution:
//        set 1280x800 -> 1280x800 x4   then HDR off -> 3440x1440 x10
//
// 3. SO THE SESSION TOPOLOGY MUST BE PERSISTED (SDC_SAVE_TO_DATABASE). Applying it
//    transiently leaves the database disagreeing with reality, and Windows then re-applies
//    the database roughly every 10-12 seconds unprompted -- re-enabling the monitor and
//    resetting the resolution each time. That is what made a real Steam Deck session flap
//    between 1280x800 and 3440x1440 forever. Persisting removes the disagreement.
//    Cost: a hard power-off mid-session boots with the monitor still disabled. Covered by
//    the applied-state file (restored at logon) and by "--revert". Reverting always
//    persists the baseline back.
//
// 4. NEVER SET THE RESOLUTION. Parsec owns it: it switches the host to the client's
//    resolution and RE-ENFORCES that about every 10 seconds. Measured while we were holding
//    1280x800 ourselves -- the two of us traded the mode back and forth indefinitely:
//        19:48:52 guard: restoring     19:48:56 CHANGED[EXT] -> 3440x1440
//        19:49:07 CHANGED[EXT]         19:49:17 ...  19:49:27 ...
//    So the only thing we do is put the mode BACK if our own HDR/topology changes disturbed
//    it (they trigger the persisted-layout re-apply, see 2). That preserves Parsec's choice
//    including its refresh rate, and never competes with it.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using WinTimer = System.Windows.Forms.Timer;

namespace ParsecHooks
{
    internal enum Pending { None, Apply, Revert }

    internal class HookApp : ApplicationContext
    {
        private Config _cfg;
        private ParsecWatcher _watcher;

        private Topology _baseline;          // known-good idle topology, restored on disconnect
        private bool _topologyEnforced;      // we disabled displays, so the guard should re-assert

        // Diagnostics: counts nested display operations we are performing ourselves, so the
        // DisplaySettingsChanged trace can tell our own changes apart from external ones.
        private int _selfOp;
        private string _lastStateLine;
        private string _lastRatified;        // state we last wrote into the display database
        // Serialises corrections: the guard runs on the UI thread while DisplaySettingsChanged
        // arrives on a SystemEvents thread, and both drive the same display APIs.
        private readonly object _gate = new object();
        private DateTime _lastReassert;
        // WM_DISPLAYCHANGE lands 100-400ms after the call that caused it, by which time the
        // _selfOp counter has already dropped. Without a grace window our own changes look
        // external and we re-assert against ourselves, which is a feedback loop.
        private DateTime _selfOpUntil;
        private const int SelfOpGraceMs = 1500;

        private bool IsSelfChange()
        {
            return _selfOp > 0 || DateTime.UtcNow < _selfOpUntil;
        }
        private int _expectedActive;         // how many displays should be lit while applied
        private List<HdrRecord> _hdrChanges = new List<HdrRecord>();
        private List<PanelRecord> _panelsAsleep = new List<PanelRecord>();
        private List<IconRecord> _iconsSaved;   // desktop layout from before we packed it
        private bool _applied;
        private bool _paused;

        private NotifyIcon _tray;
        private ToolStripMenuItem _miStatus, _miPause, _miApply, _miRevert;
        private Icon _icoIdle, _icoActive, _icoPaused;

        private WinTimer _pollTimer, _baselineTimer, _pendingTimer, _guardTimer;
        private Pending _pending = Pending.None;
        private SettingsForm _settings;
        private DateTime _cfgStamp;
        private DateTime _ignoreCfgUntil;

        public HookApp()
        {
            _cfg = Config.Load();
            _cfgStamp = ConfigStamp();

            Log.Info("================ parsec-hooks starting ================");
            Log.Info("exe      : " + Path.Combine(Paths.ExeDir, AppDomain.CurrentDomain.FriendlyName));
            Log.Info("data dir : " + Paths.DataDir);
            Log.Info("config   : " + Paths.ConfigFile);
            Log.Info("OS       : " + DescribeOs() + " 64bit=" + Environment.Is64BitOperatingSystem);
            Log.Info("config   : keep='" + _cfg.Keep + "' disableMonitors=" + _cfg.DisableSecondaryMonitors +
                     " standbyMonitors=" + _cfg.StandbySecondaryMonitors +
                     " moveIcons=" + _cfg.MoveIconsToPrimary +
                     " disableHdr=" + _cfg.DisableHdr + " hdrScope=" + _cfg.HdrScope + " guardMs=" + _cfg.GuardMs);

            // Older installs registered a Startup-folder shortcut; fold that into the Run
            // value so the settings checkbox has a single mechanism to toggle.
            AutoStart.MigrateLegacyShortcut();
            Log.Info("autostart : " + AutoStart.Describe());

            RecoverFromCrash();

            _watcher = new ParsecWatcher(_cfg.LogPath);
            _watcher.ClientConnected += OnClientConnected;
            _watcher.ClientDisconnected += OnClientDisconnected;
            _watcher.ParsecRestarted += OnParsecRestarted;
            _watcher.Reconcile();

            if (_watcher.ConnectedCount == 0)
            {
                CaptureBaseline();
            }
            else
            {
                // A session is already live, so the current topology may already carry
                // Parsec's own resolution change. Snapshotting it now would bake that in
                // as "normal", so we stay hands-off until the session ends.
                Log.Warn("a Parsec session is already active at startup; not snapshotting a baseline " +
                         "and not applying tweaks until the next idle period");
            }

            BuildTray();

            _pollTimer = new WinTimer();
            _pollTimer.Interval = _cfg.PollMs;
            _pollTimer.Tick += delegate { SafePoll(); };
            _pollTimer.Start();

            _baselineTimer = new WinTimer();
            _baselineTimer.Interval = _cfg.BaselineRefreshMs;
            _baselineTimer.Tick += delegate { MaybeRefreshBaseline(); };
            _baselineTimer.Start();

            _pendingTimer = new WinTimer();
            _pendingTimer.Tick += delegate { RunPending(); };

            _guardTimer = new WinTimer();
            _guardTimer.Interval = _cfg.GuardMs > 0 ? _cfg.GuardMs : 5000;
            _guardTimer.Tick += delegate { GuardTick(); };

            SystemEvents.SessionEnding += OnSessionEnding;
            // Fires for ANY display change, whoever caused it. Together with _selfOp this
            // gives a trace that names the culprit when the session state drifts.
            SystemEvents.DisplaySettingsChanging += OnDisplaySettingsChanging;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            Log.Info("displays  : " + StateLine());
            UpdateTray();
        }

        /// <summary>Environment.OSVersion lies without a supportedOS manifest entry (it
        /// reports 6.2 on Windows 11), so read the real build from the registry.</summary>
        private static string DescribeOs()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (k == null) return Environment.OSVersion.Version.ToString();
                    object disp = k.GetValue("DisplayVersion");
                    object build = k.GetValue("CurrentBuild");
                    object ubr = k.GetValue("UBR");
                    string name = k.GetValue("ProductName") == null ? "Windows" : k.GetValue("ProductName").ToString();

                    // ProductName was never updated for Windows 11 and still reads
                    // "Windows 10 ..." there; the build number is the reliable signal.
                    int buildNum;
                    if (build != null && int.TryParse(build.ToString(), out buildNum) && buildNum >= 22000)
                        name = name.Replace("Windows 10", "Windows 11");

                    return string.Format("{0} {1} (build {2}.{3})", name,
                        disp == null ? "" : disp.ToString(),
                        build == null ? "?" : build.ToString(),
                        ubr == null ? "0" : ubr.ToString());
                }
            }
            catch { return Environment.OSVersion.Version.ToString(); }
        }

        // ---------------- startup crash recovery ----------------

        private void RecoverFromCrash()
        {
            Topology t; List<HdrRecord> h; ModeRecord mr;
            if (!DisplayManager.TryLoadState(Paths.StateFile, out t, out h, out mr)) return;

            Log.Warn("found applied-state from a previous run (it exited while displays were modified); restoring now");

            // HDR first, topology last -- see the ordering rule at the top of this file.
            if (h != null)
            {
                foreach (HdrRecord r in h)
                {
                    if (!r.WasOn) continue;
                    string how;
                    bool ok = DisplayManager.SetHdr(r.Adapter(), r.TargetId, true, out how);
                    Log.Info("  restore HDR ON tgt=" + r.TargetId + " -> " + how + (ok ? "" : " (FAILED)"));
                }
                if (h.Count > 0) Settle();
            }

            if (mr != null)
            {
                string how;
                if (DisplayManager.TrySetMode(mr.GdiName, mr.Mode(), out how))
                    Log.Info("  restore mode on " + mr.GdiName + " -> " + how);
                else
                {
                    string how2;
                    DisplayManager.ResetModeToDefault(mr.GdiName, out how2);
                    Log.Warn("  recorded mode restore failed (" + how + "); registry default -> " + how2);
                }
                Settle();
            }

            int err;
            if (!DisplayManager.Apply(t, true, out err))
            {
                Log.Error("could not restore saved topology (err " + err + "); trying extend fallback");
                DisplayManager.ApplyExtendFallback();
            }
            DisplayManager.ClearState(Paths.StateFile);
            Log.Info("crash recovery complete");
        }

        // ---------------- baseline ----------------

        private void CaptureBaseline()
        {
            try
            {
                Topology t = DisplayManager.Capture();
                DisplayInfo[] infos = DisplayManager.Describe(t);
                _baseline = t;
                Log.Debug("baseline captured: " + infos.Length + " active display(s)");
                foreach (DisplayInfo d in infos) Log.Debug("    " + d.Describe());
            }
            catch (Exception ex)
            {
                Log.Error("failed to capture baseline topology", ex);
            }
        }

        private void MaybeRefreshBaseline()
        {
            // Only ever snapshot while genuinely idle. Parsec rewrites the host's primary
            // display mode to match the client (verified: 3440x1440 -> 1280x800@60 for a
            // Steam Deck), so a snapshot taken mid-session would "restore" you to that.
            if (_applied || _pending != Pending.None) return;
            if (_watcher.ConnectedCount != 0) return;
            CaptureBaseline();
        }

        // ---------------- watcher events ----------------

        private void SafePoll()
        {
            try { _watcher.Poll(); }
            catch (Exception ex) { Log.Error("watcher poll threw", ex); }
            try { CheckConfigFile(); }
            catch (Exception ex) { Log.Error("config watch threw", ex); }
        }

        private static DateTime ConfigStamp()
        {
            try
            {
                FileInfo fi = new FileInfo(Paths.ConfigFile);
                return fi.Exists ? fi.LastWriteTimeUtc : DateTime.MinValue;
            }
            catch { return DateTime.MinValue; }
        }

        /// <summary>Picks up edits made outside this process -- the standalone "--settings"
        /// mode, or someone editing the ini by hand -- without needing Reload config.
        /// Polled from the existing timer rather than via FileSystemWatcher, whose events
        /// arrive on a worker thread and would need marshalling back with no form to
        /// marshal through.</summary>
        private void CheckConfigFile()
        {
            if (DateTime.UtcNow < _ignoreCfgUntil) return;
            DateTime s = ConfigStamp();
            if (s == _cfgStamp) return;
            _cfgStamp = s;
            Log.Info("config file changed on disk; reloading automatically");
            ReloadConfig();
        }

        private void OnClientConnected(string user, int count)
        {
            if (_pending == Pending.Revert)
            {
                CancelPending();
                Log.Info("cancelled pending revert (a client connected again)");
            }
            if (_paused) { Log.Info("automation paused; ignoring connect"); UpdateTray(); return; }
            if (!_applied) SchedulePending(Pending.Apply, _cfg.ApplyDelayMs);
            UpdateTray();
        }

        private void OnClientDisconnected(string user, int count)
        {
            if (count > 0) { UpdateTray(); return; }

            if (_pending == Pending.Apply)
            {
                CancelPending();
                Log.Info("cancelled pending apply (client left before it ran)");
            }
            if (_applied) SchedulePending(Pending.Revert, _cfg.RevertDelayMs);
            else OnBecameIdle();
            UpdateTray();
        }

        private void OnParsecRestarted()
        {
            if (_applied) SchedulePending(Pending.Revert, 500);
            else OnBecameIdle();
            UpdateTray();
        }

        /// <summary>Called the moment we know nothing is connected and we have not modified
        /// anything. This is the only safe time to snapshot, and taking it immediately rather
        /// than waiting for the refresh timer matters when the app was launched mid-session:
        /// otherwise the next client to connect finds no baseline and we decline to act.</summary>
        private void OnBecameIdle()
        {
            if (_applied) return;
            if (_baseline == null) Log.Info("now idle - capturing the baseline we declined to take at startup");
            CaptureBaseline();
        }

        private void SchedulePending(Pending what, int delayMs)
        {
            _pending = what;
            _pendingTimer.Stop();
            _pendingTimer.Interval = delayMs < 1 ? 1 : delayMs;
            _pendingTimer.Start();
            Log.Debug("scheduled " + what + " in " + delayMs + "ms");
        }

        private void CancelPending()
        {
            _pendingTimer.Stop();
            _pending = Pending.None;
        }

        private void RunPending()
        {
            Pending what = _pending;
            CancelPending();
            lock (_gate)
            {
                _selfOp++;
                try
                {
                    if (what != Pending.None) Log.Info(what + ": state before -> " + StateLine());
                    if (what == Pending.Apply) DoApply();
                    else if (what == Pending.Revert) DoRevert(false);
                    if (what != Pending.None)
                    {
                        _lastStateLine = StateLine();
                        Log.Info(what + ": state after  -> " + _lastStateLine);
                    }
                }
                catch (Exception ex) { Log.Error(what + " failed", ex); }
                finally
                {
                    _selfOp--;
                    _lastReassert = DateTime.UtcNow;
                    _selfOpUntil = DateTime.UtcNow.AddMilliseconds(SelfOpGraceMs);
                }
            }
            UpdateTray();
        }

        private void Settle()
        {
            if (_cfg.SettleMs > 0) Thread.Sleep(_cfg.SettleMs);
        }

        // ---------------- diagnostics ----------------

        /// <summary>One-line snapshot of every active display: mode, primary marker, HDR.</summary>
        private static string StateLine()
        {
            try
            {
                DisplayInfo[] ds = DisplayManager.Describe(DisplayManager.Capture());
                StringBuilder sb = new StringBuilder();
                sb.Append(ds.Length).Append(" active");
                foreach (DisplayInfo d in ds)
                {
                    DisplayMode m = DisplayManager.GetCurrentMode(d.Gdi);
                    sb.Append("  |  ").Append(string.IsNullOrEmpty(d.Name) ? "?" : d.Name);
                    if (d.IsPrimary) sb.Append("*");
                    sb.Append(' ').Append(m != null ? m.ToString() : d.Width + "x" + d.Height + "@?");
                    sb.Append(" HDR=").Append(!d.HdrSupported ? "n/a" : (d.HdrEnabled ? "ON" : "off"));
                }
                return sb.ToString();
            }
            catch (Exception ex) { return "<unavailable: " + ex.Message + ">"; }
        }

        /// <summary>Makes whatever is currently on screen authoritative in Windows' display
        /// database. Without this, our own sequence leaves live state and database disagreeing
        /// and Windows re-applies the database every ~10s, which knocked the resolution back to
        /// native mid-session. Skipped when nothing changed since the last ratification so we
        /// are not writing the database on every guard tick.</summary>
        private void RatifyState(string reason)
        {
            if (!_applied) return;
            string now = StateLine();
            if (now == _lastRatified) return;
            if (DisplayManager.PersistCurrent())
            {
                _lastRatified = now;
                Log.Info("[" + reason + "] ratified current layout so Windows stops reverting it: " + now);
            }
        }

        private void OnDisplaySettingsChanging(object sender, EventArgs e)
        {
            Log.Debug("display settings CHANGING " + (IsSelfChange() ? "(ours)" : "(EXTERNAL)"));
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            bool ours = IsSelfChange();
            string s = StateLine();
            _lastStateLine = s;
            Log.Info("display settings CHANGED " + (ours ? "(ours)" : "(EXTERNAL - not initiated by parsec-hooks)") +
                     " -> " + s);
            if (!_applied) return;
            Log.Info("            expected: " + ExpectationLine());
            if (ours) return;

            // React to the event instead of waiting up to guardMs. Windows re-applies its
            // persisted layout for driver and monitor events, and that restores the persisted
            // MODE as well as the topology, so an external change here is exactly the moment
            // the session silently loses its resolution.
            lock (_gate)
            {
                if ((DateTime.UtcNow - _lastReassert).TotalMilliseconds < 700)
                {
                    Log.Debug("re-assert debounced (a persisted-layout re-apply raises several events)");
                    return;
                }
                _selfOp++;
                try { RunHolds("external display change"); }
                catch (Exception ex) { Log.Error("re-assert after external change failed", ex); }
                finally { _selfOp--; _selfOpUntil = DateTime.UtcNow.AddMilliseconds(SelfOpGraceMs); }
            }
        }

        /// <summary>What the current session is supposed to look like, for comparison against
        /// the line above.</summary>
        private string ExpectationLine()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(_expectedActive).Append(" active");
            sb.Append(", resolution left to Parsec");
            if (_hdrChanges != null && _hdrChanges.Count > 0) sb.Append(", HDR off on ").Append(_hdrChanges.Count).Append(" display(s)");
            sb.Append(_topologyEnforced ? ", topology enforced" : ", topology not enforced");
            return sb.ToString();
        }

        // ---------------- apply / revert ----------------

        private void DoApply()
        {
            if (_applied) { Log.Debug("already applied; nothing to do"); return; }
            if (_baseline == null) { Log.Warn("no baseline topology available; refusing to change displays"); return; }

            DisplayInfo[] infos = DisplayManager.Describe(_baseline);
            List<DisplayInfo> kept = new List<DisplayInfo>();
            foreach (DisplayInfo d in infos) if (DisplayManager.MatchesKeep(d, _cfg.Keep)) kept.Add(d);

            if (kept.Count == 0)
            {
                Log.Error("selector keep='" + _cfg.Keep + "' matches none of the " + infos.Length +
                          " active displays; aborting so we do not black out every screen");
                return;
            }

            StringBuilder summary = new StringBuilder();

            // Snapshot the kept display's mode BEFORE anything, because the very first step
            // (HDR) already resets it. Whatever Parsec negotiated for the client lives here.
            string keptGdiEarly = KeptGdi(kept);
            DisplayMode modeBeforeBatch = string.IsNullOrEmpty(keptGdiEarly)
                ? null : DisplayManager.GetCurrentMode(keptGdiEarly);
            if (modeBeforeBatch != null) Log.Debug("mode before our changes: " + modeBeforeBatch + " on " + keptGdiEarly);

            // ---- 1. HDR FIRST (an HDR change re-applies the persisted topology) ----
            List<HdrRecord> changed = new List<HdrRecord>();
            if (_cfg.DisableHdr)
            {
                List<DisplayInfo> scope = string.Equals(_cfg.HdrScope, "all", StringComparison.OrdinalIgnoreCase)
                    ? new List<DisplayInfo>(infos) : kept;

                foreach (DisplayInfo d in scope)
                {
                    // Read live rather than trusting the baseline, which may be seconds old.
                    bool sup, en; uint mode, bpc, enc; string via;
                    if (!DisplayManager.TryGetHdr(d.Adapter, d.TargetId, out sup, out en, out mode, out bpc, out enc, out via))
                    {
                        Log.Debug("HDR unreadable on " + d.Short() + ": " + via);
                        continue;
                    }
                    if (!sup || !en)
                    {
                        Log.Debug("HDR already off/unsupported on " + d.Short() + " (" + via + ")");
                        continue;
                    }

                    string how;
                    if (DisplayManager.SetHdr(d.Adapter, d.TargetId, false, out how))
                    {
                        HdrRecord rec = new HdrRecord();
                        rec.AdapterLow = d.Adapter.LowPart;
                        rec.AdapterHigh = d.Adapter.HighPart;
                        rec.TargetId = d.TargetId;
                        rec.WasOn = true;
                        changed.Add(rec);
                        Log.Info("HDR off on " + d.Short() + " via " + how);
                        if (summary.Length > 0) summary.Append(" | ");
                        summary.Append("HDR off: " + d.Short());
                    }
                    else
                    {
                        Log.Error("could not turn HDR off on " + d.Short() + ": " + how);
                    }
                }
            }

            if (changed.Count > 0) Settle();

            // ---- 2. Topology, built from a LIVE query ----
            // Never from the baseline. Parsec rewrites the primary's mode to match the client
            // (3440x1440 -> 1280x800 for a Steam Deck) about a second after connecting, and
            // applying baseline path/mode arrays would overwrite that back to the idle
            // resolution -- which is exactly the bug that made Parsec's own resolution
            // matching stop working. Re-querying means we only clear active flags.
            bool topologyChanged = false;
            int expectedActive = kept.Count;
            if (_cfg.DisableSecondaryMonitors)
            {
                List<DisplayInfo> toDisable, keptNow;
                Topology target = DisplayManager.BuildKeepOnlyFromCurrent(_cfg.Keep, out toDisable, out keptNow);
                if (keptNow != null && keptNow.Count > 0) expectedActive = keptNow.Count;

                if (target == null)
                {
                    Log.Info("no secondary displays to disable");
                }
                else
                {
                    int err;
                    if (DisplayManager.Apply(target, true, out err))
                    {
                        // Trust the OS, not the return code: SetDisplayConfig can report
                        // success while changing nothing.
                        int after = DisplayManager.ActiveDisplayCount();
                        if (after == expectedActive)
                        {
                            topologyChanged = true;
                            List<string> names = new List<string>();
                            foreach (DisplayInfo d in toDisable) names.Add(d.Short());
                            Log.Info("disabled " + toDisable.Count + " display(s): " + string.Join(", ", names.ToArray()));
                            if (summary.Length > 0) summary.Insert(0, "Off: " + string.Join(", ", names.ToArray()) + " | ");
                            else summary.Append("Off: " + string.Join(", ", names.ToArray()));
                        }
                        else
                        {
                            Log.Error("SetDisplayConfig reported success but " + after + " display(s) are still active " +
                                      "(expected " + expectedActive + "); treating topology as unchanged");
                        }
                    }
                    else
                    {
                        Log.Error("topology change failed (err " + err + "); leaving displays alone");
                    }
                }
            }

            // ---- 3. Resolution, after topology ----
            // Safe in this order: a mode change does NOT make Windows re-apply its persisted
            // topology (measured), unlike an HDR change.
            // Put back whatever Parsec had chosen, which our own HDR and topology changes will
            // have knocked back to the persisted mode. We never pick a resolution ourselves.
            string gdiNow = keptGdiEarly;
            if (string.IsNullOrEmpty(gdiNow))
            {
                try
                {
                    foreach (DisplayInfo d in DisplayManager.Describe(DisplayManager.Capture()))
                        if (DisplayManager.MatchesKeep(d, _cfg.Keep)) { gdiNow = d.Gdi; break; }
                }
                catch { }
            }
            RestoreModeIfWeBrokeIt(gdiNow, modeBeforeBatch, "apply");

            // ---- 3. Panel standby ----
            // Deliberately AFTER the topology work and deliberately not part of it: this blanks
            // the other panels over DDC/CI without deactivating their display paths, so no
            // phantom monitor registrations appear and the capture stream stays intact.
            _panelsAsleep = new List<PanelRecord>();
            if (_cfg.StandbySecondaryMonitors)
            {
                List<string> keepGdi = new List<string>();
                try
                {
                    // Live query: GDI names can differ from the baseline after a topology change.
                    foreach (DisplayInfo d in DisplayManager.Describe(DisplayManager.Capture()))
                        if (DisplayManager.MatchesKeep(d, _cfg.Keep) && !string.IsNullOrEmpty(d.Gdi))
                            keepGdi.Add(d.Gdi);
                }
                catch (Exception ex) { Log.Debug("panel standby: could not resolve kept displays: " + ex.Message); }

                if (keepGdi.Count == 0)
                    Log.Warn("panel standby: could not identify the kept display; leaving panels on");
                else
                {
                    try { _panelsAsleep = PanelPower.StandbyAllExcept(keepGdi); }
                    catch (Exception ex) { Log.Error("panel standby failed", ex); }
                    if (_panelsAsleep.Count > 0)
                    {
                        if (summary.Length > 0) summary.Append(" | ");
                        summary.Append("Panels asleep: " + _panelsAsleep.Count);
                    }
                }
            }

            // ---- 4. Desktop icons ----
            // Last, because it depends on the primary's final size -- which is the client's
            // resolution, not the idle one.
            _iconsSaved = null;
            if (_cfg.MoveIconsToPrimary)
            {
                try
                {
                    _iconsSaved = DesktopIcons.PackOntoPrimary();
                    if (_iconsSaved != null)
                    {
                        DesktopIcons.Save(Paths.IconsFile, _iconsSaved);
                        if (summary.Length > 0) summary.Append(" | ");
                        summary.Append("Icons moved to primary");
                    }
                }
                catch (Exception ex) { Log.Error("desktop icons: pack failed", ex); }
            }

            _hdrChanges = changed;
            _topologyEnforced = topologyChanged;
            _expectedActive = expectedActive;
            _applied = topologyChanged || changed.Count > 0 ||
                       _panelsAsleep.Count > 0 || _iconsSaved != null;

            if (_applied)
            {
                // The topology was persisted while the mode was still clobbered, and the mode
                // repair above is transient -- so ratify now, or Windows re-applies the stale
                // database within ~10s and the client's resolution is lost.
                RatifyState("apply");

                // Persist before announcing, so a crash right now is still recoverable.
                DisplayManager.SaveState(Paths.StateFile, _baseline, changed, null);
                if (_cfg.GuardMs > 0) _guardTimer.Start();
                Notify("Parsec session active", summary.Length > 0 ? summary.ToString() : "Display tweaks applied");
            }
            else
            {
                Log.Info("nothing needed changing");
            }
        }

        // ---------------- default layout (the escape hatch) ----------------

        /// <summary>Records whatever is on screen right now as the layout to fall back to.
        /// Deliberately a manual action: the point is that the user confirms the screen looks
        /// right at the moment it is captured, which nothing automatic can know.</summary>
        private bool SaveDefaultLayout(out string describe)
        {
            return DefaultLayout.Save(out describe);
        }

        /// <summary>Puts the screens back to the saved default: panels awake, topology, mode and
        /// HDR all restored. This is the "something went wrong, fix it" button, so it runs even
        /// when nothing is applied and it never throws at the caller.</summary>
        private bool ResetToDefaultLayout(out string message)
        {
            bool ok;
            lock (_gate)
            {
                _selfOp++;
                try
                {
                    _iconsSaved = null;   // DefaultLayout restores from the file, not from us
                    ok = DefaultLayout.Reset(out message);

                    // Whatever the outcome, the session's tweaks are no longer being held, so
                    // stop the guard re-asserting a topology the user just overrode.
                    _panelsAsleep = new List<PanelRecord>();
                    _hdrChanges = new List<HdrRecord>();
                    _topologyEnforced = false;
                    _lastRatified = null;
                    _applied = false;
                    _guardTimer.Stop();
                    CaptureBaseline();
                }
                finally { _selfOp--; _selfOpUntil = DateTime.UtcNow.AddMilliseconds(SelfOpGraceMs); }
            }
            return ok;
        }

        /// <summary>Re-applies the idle baseline, retrying until the OS agrees.
        ///
        /// One attempt is not enough. The HDR restore that runs just before this makes Windows
        /// re-apply its persisted layout, and at that moment the persisted layout is still our
        /// reduced session config -- so that async re-apply can land after our baseline call and
        /// silently put the monitor back off. Observed exactly once as "baseline restore reported
        /// success but 1 display(s) are active (expected 2)".</summary>
        private bool RestoreBaselineTopology()
        {
            int want;
            try { want = DisplayManager.Describe(_baseline).Length; }
            catch { want = -1; }

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                int err;
                if (!DisplayManager.Apply(_baseline, true, out err))
                {
                    Log.Error("restoring baseline topology failed (err " + err + ") on attempt " + attempt);
                }
                else
                {
                    int got = DisplayManager.ActiveDisplayCount();
                    if (want < 0 || got == want)
                    {
                        Log.Info("restored baseline topology (" + got + " display(s) active)");
                        return true;
                    }
                    Log.Warn("baseline restore attempt " + attempt + ": " + got + " display(s) active, expected " +
                             want + " (an async persisted-layout re-apply can land mid-flight); retrying");
                }
                if (attempt < 3) Thread.Sleep(Math.Max(_cfg.SettleMs * 2, 600));
            }

            Log.Error("baseline topology did not take after 3 attempts; trying extend fallback");
            DisplayManager.ApplyExtendFallback();
            return false;
        }

        /// <summary>Undoes the resolution reset that our OWN changes cause as a side effect.
        /// Both the HDR change and the topology change make Windows re-apply its persisted
        /// layout, and that restores the persisted MODE too -- so disabling the second monitor
        /// throws away whatever resolution Parsec had just negotiated for the client.
        ///
        /// Called with the mode observed before the batch started, so it puts back exactly what
        /// was there, refresh rate included. That matters: Parsec asks for 1280x800@60, and
        /// imposing our own 1280x800@165 instead makes Parsec re-enforce its choice roughly
        /// every 10 seconds, which is visible as the resolution flapping back and forth.
        ///
        /// Only used when we are NOT imposing a configured resolution. Scoped to a single batch
        /// of our own operations rather than held continuously, so a genuine mid-session change
        /// by Parsec or the client is left alone.</summary>
        private void RestoreModeIfWeBrokeIt(string gdi, DisplayMode pre, string reason)
        {
            if (pre == null || string.IsNullOrEmpty(gdi)) return;
            DisplayMode now = DisplayManager.GetCurrentMode(gdi);
            if (now == null) return;
            if (now.Width == pre.Width && now.Height == pre.Height && now.Hz == pre.Hz) return;

            string how;
            if (DisplayManager.TrySetMode(gdi, pre, out how))
                Log.Info("[" + reason + "] our own changes had reset the mode to " + now +
                         "; put the client's " + pre + " back (" + how + ")");
            else
                Log.Warn("[" + reason + "] mode was reset to " + now + " and restoring " + pre + " failed: " + how);
        }

        private static string KeptGdi(List<DisplayInfo> kept)
        {
            if (kept == null) return null;
            foreach (DisplayInfo d in kept) if (!string.IsNullOrEmpty(d.Gdi)) return d.Gdi;
            return null;
        }

        private void DoRevert(bool manual)
        {
            if (!_applied && !manual) { Log.Debug("nothing applied; nothing to revert"); return; }

            _guardTimer.Stop();
            bool ok = true;

            // ---- 0. Wake panels FIRST. Waking a panel is a monitor-arrival event, and Windows
            //         responds by re-applying its persisted display database. Doing it before the
            //         HDR and topology work lets that re-apply land first, so our restore is what
            //         wins rather than being silently overwritten a second later.
            if (_panelsAsleep != null && _panelsAsleep.Count > 0)
            {
                try { PanelPower.Wake(_panelsAsleep); }
                catch (Exception ex) { Log.Error("panel wake failed", ex); }
                _panelsAsleep = new List<PanelRecord>();
                Settle();
            }

            // ---- 1. HDR FIRST. Besides the ordering rule, restoring HDR nudges Windows
            //         into re-applying its persisted topology -- which is the very config
            //         we are about to assert anyway, so the two pull in the same direction.
            foreach (HdrRecord r in _hdrChanges)
            {
                if (!r.WasOn) continue;
                string how;
                if (DisplayManager.SetHdr(r.Adapter(), r.TargetId, true, out how))
                    Log.Info("HDR restored ON for tgt=" + r.TargetId + " via " + how);
                else
                {
                    ok = false;
                    Log.Error("could not restore HDR for tgt=" + r.TargetId + ": " + how);
                }
            }
            if (_hdrChanges.Count > 0) Settle();

            // ---- 2. Topology LAST: assert the exact saved geometry ----
            // This also restores the idle resolution, because the baseline's mode arrays carry
            // it. No separate mode step is needed, and we must not add one: Parsec restores its
            // own resolution on disconnect too, and two parties setting it fight each other.
            if (_baseline != null && !RestoreBaselineTopology()) ok = false;

            // ---- 3. Desktop icons LAST: their coordinates only make sense once the desktop is
            //         back to its idle size, so this has to follow the topology restore.
            if (_iconsSaved != null)
            {
                try { DesktopIcons.Restore(_iconsSaved); }
                catch (Exception ex) { Log.Error("desktop icons: restore failed", ex); }
                _iconsSaved = null;
            }
            DesktopIcons.ClearSaved(Paths.IconsFile);

            _hdrChanges = new List<HdrRecord>();
            _topologyEnforced = false;
            _lastRatified = null;   // the baseline restore above re-persisted the idle layout
            _applied = false;
            DisplayManager.ClearState(Paths.StateFile);
            Notify("Parsec session ended", ok ? "Displays and HDR restored" : "Restored with errors - see log");

            // Re-snapshot now that things have settled so the next session starts from truth.
            CaptureBaseline();
        }

        /// <summary>While tweaks are applied, notice and undo external drift. Windows
        /// re-applies its persisted topology whenever HDR changes or display settings are
        /// touched, which would silently re-light the monitors we disabled. Since we
        /// intentionally never wrote our reduced topology to the persistence database, that
        /// drift is expected rather than exceptional, so we re-assert instead of complaining.</summary>
        private void GuardTick()
        {
            if (!_applied) { _guardTimer.Stop(); return; }
            lock (_gate)
            {
                _selfOp++;
                try { RunHolds("guard"); }
                catch (Exception ex) { Log.Error("guard tick failed", ex); }
                finally { _selfOp--; _selfOpUntil = DateTime.UtcNow.AddMilliseconds(SelfOpGraceMs); }
            }
        }

        /// <summary>Re-asserts everything the active session is supposed to be holding: HDR off,
        /// the reduced topology, and the forced resolution. Idempotent, and safe to call from
        /// either the guard timer or a display-change event. Caller holds <see cref="_gate"/>.</summary>
        private void RunHolds(string reason)
        {
            _lastReassert = DateTime.UtcNow;
            try
            {
                // Same reasoning as in DoApply: our corrections below reset the mode as a side
                // effect, so remember it first and put it back afterwards.
                string keptGdiNow = null;
                DisplayMode modeBeforeBatch = null;
                try
                {
                    foreach (DisplayInfo d in DisplayManager.Describe(DisplayManager.Capture()))
                        if (DisplayManager.MatchesKeep(d, _cfg.Keep)) { keptGdiNow = d.Gdi; break; }
                    if (!string.IsNullOrEmpty(keptGdiNow)) modeBeforeBatch = DisplayManager.GetCurrentMode(keptGdiNow);
                }
                catch (Exception ex) { Log.Debug("could not snapshot mode before holds: " + ex.Message); }

                // HDR drift first, because fixing it perturbs the topology.
                bool hdrFixed = false;
                foreach (HdrRecord r in _hdrChanges)
                {
                    if (!r.WasOn) continue;
                    bool sup, en; uint m, b, e; string via;
                    if (!DisplayManager.TryGetHdr(r.Adapter(), r.TargetId, out sup, out en, out m, out b, out e, out via)) continue;
                    if (!en) continue;

                    string how;
                    if (DisplayManager.SetHdr(r.Adapter(), r.TargetId, false, out how))
                    {
                        hdrFixed = true;
                        Log.Info("guard: HDR had come back on for tgt=" + r.TargetId + "; turned it off again");
                    }
                }
                // Windows applies the persisted layout asynchronously in response to the HDR
                // change above, so give it longer than the normal settle before asserting
                // topology -- otherwise its re-apply lands after ours and undoes it.
                if (hdrFixed) Thread.Sleep(Math.Max(_cfg.SettleMs * 2, 600));

                if (_topologyEnforced)
                {
                    int now = DisplayManager.ActiveDisplayCount();
                    if (now >= 0 && now != _expectedActive)
                    {
                        Log.Info("guard: " + now + " display(s) active but expected " + _expectedActive + "; re-asserting");

                        // Two attempts, because a late persisted-layout re-apply can land
                        // between our SetDisplayConfig and its taking effect. If both fail
                        // the next tick tries again anyway.
                        for (int attempt = 1; attempt <= 2; attempt++)
                        {
                            // Rebuilt from a live query each time so re-asserting the topology
                            // cannot revert the session's resolution.
                            List<DisplayInfo> dis, keptNow;
                            Topology target = DisplayManager.BuildKeepOnlyFromCurrent(_cfg.Keep, out dis, out keptNow);
                            if (target == null)
                            {
                                Log.Debug("guard: nothing to re-assert");
                                break;
                            }

                            int err;
                            if (!DisplayManager.Apply(target, true, out err))
                            {
                                Log.Warn("guard: re-assert attempt " + attempt + " failed (err " + err + ")");
                                break;
                            }
                            int after = DisplayManager.ActiveDisplayCount();
                            if (after == _expectedActive)
                            {
                                Log.Info("guard: re-asserted OK (" + after + " display(s) active)");
                                break;
                            }
                            Log.Debug("guard: still " + after + " active after attempt " + attempt);
                            if (attempt == 1) Thread.Sleep(Math.Max(_cfg.SettleMs, 400));
                        }
                    }
                }

                // Resolution last, matching the apply order. This only holds a resolution the
                // user explicitly configured. When sessionResolution is blank we never touch
                // it, so a client-driven mid-session resolution change is still free to happen.
                //
                // This has to exist because a persisted-layout re-apply (which an HDR change
                // triggers, and which Windows also does on its own for driver/monitor events)
                // restores the persisted MODE as well as the persisted topology -- so without
                // this the display silently returns to its native resolution mid-session and
                // nothing puts it back.
                // Only ever undoes the mode reset our own corrections above caused. We never
                // hold a resolution of our own: Parsec re-enforces its choice about every 10
                // seconds, so competing with it just makes the screen flap.
                RestoreModeIfWeBrokeIt(keptGdiNow, modeBeforeBatch, reason);

                // Ratify whatever is now on screen, including a resolution Parsec has just
                // changed. This is what stops Windows reverting Parsec's choice.
                RatifyState(reason);

                string now2 = StateLine();
                if (now2 != _lastStateLine)
                {
                    _lastStateLine = now2;
                    Log.Info("[" + reason + "] state now " + now2);
                }
                else if (hdrFixed)
                {
                    Log.Info("[" + reason + "] corrections applied, state " + now2);
                }
            }
            catch (Exception ex) { Log.Error("RunHolds(" + reason + ") failed", ex); }
        }

        // ---------------- tray ----------------

        private void BuildTray()
        {
            _icoIdle = MakeIcon(Color.FromArgb(48, 54, 64), Color.FromArgb(150, 158, 172));
            _icoActive = MakeIcon(Color.FromArgb(28, 122, 62), Color.FromArgb(88, 214, 141));
            _icoPaused = MakeIcon(Color.FromArgb(120, 84, 8), Color.FromArgb(232, 178, 58));

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.ShowImageMargin = false;

            _miStatus = new ToolStripMenuItem("Status");
            _miStatus.Enabled = false;
            menu.Items.Add(_miStatus);
            menu.Items.Add(new ToolStripSeparator());

            // Bold marks the default item, matching the shell convention that double-click
            // invokes it.
            ToolStripMenuItem miSettings = new ToolStripMenuItem("Settings...");
            miSettings.Font = new Font(miSettings.Font, FontStyle.Bold);
            miSettings.Click += delegate { ShowSettings(); };
            menu.Items.Add(miSettings);

            ToolStripMenuItem miShowTop = new ToolStripMenuItem("Show status...");
            miShowTop.Click += delegate { ShowStatus(); };
            menu.Items.Add(miShowTop);

            menu.Items.Add(new ToolStripSeparator());

            _miApply = new ToolStripMenuItem("Apply tweaks now (test)");
            _miApply.Click += delegate
            {
                try { DoApply(); } catch (Exception ex) { Log.Error("manual apply failed", ex); }
                UpdateTray();
            };
            menu.Items.Add(_miApply);

            _miRevert = new ToolStripMenuItem("Revert now");
            _miRevert.Click += delegate
            {
                try { CancelPending(); DoRevert(true); } catch (Exception ex) { Log.Error("manual revert failed", ex); }
                UpdateTray();
            };
            menu.Items.Add(_miRevert);

            menu.Items.Add(new ToolStripSeparator());

            // The recovery pair. Kept at the top level rather than behind Settings, because the
            // moment you need them is the moment a screen is black or the resolution is wrong,
            // and a dialog you cannot read is no use.
            ToolStripMenuItem miReset = new ToolStripMenuItem("Reset displays to default");
            miReset.Click += delegate
            {
                try
                {
                    string msg;
                    if (ResetToDefaultLayout(out msg))
                        Notify("Displays reset", msg);
                    else
                        MessageBox.Show(msg, "parsec-hooks", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex) { Log.Error("reset to default failed", ex); }
                UpdateTray();
            };
            menu.Items.Add(miReset);

            ToolStripMenuItem miSaveDefault = new ToolStripMenuItem("Save current layout as default");
            miSaveDefault.Click += delegate
            {
                try
                {
                    string now = StateLine();
                    if (MessageBox.Show(
                            "Remember this as the layout to return to?\n\n" + now +
                            "\n\nMake sure the screens look right before saving.",
                            "parsec-hooks", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                        return;

                    string desc;
                    if (SaveDefaultLayout(out desc)) Notify("Default layout saved", desc);
                    else MessageBox.Show("Could not save the default layout - see the log.",
                                         "parsec-hooks", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                catch (Exception ex) { Log.Error("save default failed", ex); }
            };
            menu.Items.Add(miSaveDefault);

            menu.Items.Add(new ToolStripSeparator());

            _miPause = new ToolStripMenuItem("Pause automation");
            _miPause.CheckOnClick = true;
            _miPause.Click += delegate
            {
                _paused = _miPause.Checked;
                Log.Info("automation " + (_paused ? "PAUSED" : "resumed"));
                if (_paused) CancelPending();
                UpdateTray();
            };
            menu.Items.Add(_miPause);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem miReload = new ToolStripMenuItem("Reload config from file");
            miReload.Click += delegate { ReloadConfig(); };
            menu.Items.Add(miReload);

            ToolStripMenuItem miLog = new ToolStripMenuItem("Open parsec-hooks log");
            miLog.Click += delegate { OpenPath(Paths.LogFile); };
            menu.Items.Add(miLog);

            ToolStripMenuItem miPLog = new ToolStripMenuItem("Open Parsec log");
            miPLog.Click += delegate
            {
                string p = _watcher.LogPath;
                if (string.IsNullOrEmpty(p)) MessageBox.Show("Parsec log not found yet.", "parsec-hooks");
                else OpenPath(p);
            };
            menu.Items.Add(miPLog);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem miExit = new ToolStripMenuItem("Exit");
            miExit.Click += delegate { ExitApp(); };
            menu.Items.Add(miExit);

            _tray = new NotifyIcon();
            _tray.Icon = _icoIdle;
            _tray.Visible = true;
            _tray.ContextMenuStrip = menu;
            _tray.Text = "parsec-hooks";
            _tray.DoubleClick += delegate { ShowSettings(); };
        }

        /// <summary>Opens the settings dialog, reloading config afterwards if anything was
        /// saved. Kept to a single instance so double-clicking the tray icon repeatedly just
        /// re-focuses the existing window.</summary>
        private void ShowSettings()
        {
            if (_settings != null && !_settings.IsDisposed)
            {
                try { _settings.Activate(); } catch { }
                return;
            }

            try
            {
                _settings = new SettingsForm(
                    _cfg,
                    delegate { return DisplayManager.Describe(DisplayManager.Capture()); },
                    delegate { return _watcher.LogPath; });

                _settings.ShowDialog();
                bool applied = _settings.Applied;
                _settings.Dispose();
                _settings = null;

                if (applied) ReloadConfig();
            }
            catch (Exception ex)
            {
                Log.Error("settings dialog failed", ex);
                if (_settings != null) { try { _settings.Dispose(); } catch { } _settings = null; }
                MessageBox.Show("Could not open settings:\n\n" + ex.Message, "parsec-hooks",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static Icon MakeIcon(Color fill, Color edge)
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (SolidBrush b = new SolidBrush(fill)) g.FillRectangle(b, 3, 5, 26, 17);
                    using (Pen p = new Pen(edge, 2.5f)) g.DrawRectangle(p, 3, 5, 26, 17);
                    using (SolidBrush b = new SolidBrush(edge))
                    {
                        g.FillRectangle(b, 13, 22, 6, 4);
                        g.FillRectangle(b, 8, 26, 16, 3);
                    }
                }
                IntPtr h = bmp.GetHicon();
                try { return (Icon)Icon.FromHandle(h).Clone(); }
                finally { Native.DestroyIcon(h); }
            }
        }

        private void UpdateTray()
        {
            if (_tray == null) return;

            int n = _watcher == null ? 0 : _watcher.ConnectedCount;
            string state;
            if (_paused) state = "Paused";
            else if (_applied) state = "Active - tweaks applied";
            else if (n > 0) state = "Client connected";
            else state = "Idle";

            _miStatus.Text = state + "  (" + n + " client" + (n == 1 ? "" : "s") + ")";
            _miApply.Enabled = !_applied && _baseline != null;
            _miRevert.Enabled = _applied;

            _tray.Icon = _paused ? _icoPaused : (_applied ? _icoActive : _icoIdle);

            StringBuilder tip = new StringBuilder();
            tip.Append("parsec-hooks - ").Append(state);
            if (n > 0) tip.Append("\n").Append(string.Join(", ", _watcher.ConnectedUsers));
            // NotifyIcon.Text is capped at 63 chars on older shells; keep it short.
            string s = tip.ToString();
            _tray.Text = s.Length > 62 ? s.Substring(0, 62) : s;
        }

        private void Notify(string title, string body)
        {
            Log.Info("notify: " + title + " - " + body);
            if (!_cfg.Notifications || _tray == null) return;
            try
            {
                _tray.BalloonTipTitle = title;
                _tray.BalloonTipText = body;
                _tray.BalloonTipIcon = ToolTipIcon.Info;
                _tray.ShowBalloonTip(4000);
            }
            catch (Exception ex) { Log.Debug("balloon failed: " + ex.Message); }
        }

        private void ReloadConfig()
        {
            _cfg = Config.Load();
            // Our own write must not immediately re-trigger the on-disk watcher.
            _cfgStamp = ConfigStamp();
            _ignoreCfgUntil = DateTime.UtcNow.AddSeconds(2);
            _pollTimer.Interval = _cfg.PollMs;
            _baselineTimer.Interval = _cfg.BaselineRefreshMs;
            _guardTimer.Interval = _cfg.GuardMs > 0 ? _cfg.GuardMs : 5000;
            if (_cfg.GuardMs <= 0) _guardTimer.Stop();
            else if (_applied) _guardTimer.Start();
            _watcher.SetConfiguredPath(_cfg.LogPath);
            Log.Info("config reloaded: keep='" + _cfg.Keep + "' disableMonitors=" + _cfg.DisableSecondaryMonitors +
                     " standbyMonitors=" + _cfg.StandbySecondaryMonitors +
                     " moveIcons=" + _cfg.MoveIconsToPrimary +
                     " disableHdr=" + _cfg.DisableHdr + " hdrScope=" + _cfg.HdrScope + " guardMs=" + _cfg.GuardMs);
            UpdateTray();
            Notify("parsec-hooks", "Config reloaded");
        }

        private void ShowStatus()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("State      : " + (_paused ? "PAUSED" : (_applied ? "tweaks APPLIED" : "idle")));
            sb.AppendLine("Clients    : " + _watcher.ConnectedCount +
                          (_watcher.ConnectedCount > 0 ? " (" + string.Join(", ", _watcher.ConnectedUsers) + ")" : ""));
            sb.AppendLine("Parsec log : " + (_watcher.LogPath == null ? "<not found>" : _watcher.LogPath));
            sb.AppendLine("parsecd    : " + (ParsecWatcher.IsParsecRunning() ? "running" : "not running"));
            sb.AppendLine("Pending    : " + _pending);
            sb.AppendLine();
            sb.AppendLine("keep = " + _cfg.Keep + "   disableMonitors = " + _cfg.DisableSecondaryMonitors +
                          "   disableHdr = " + _cfg.DisableHdr + " (" + _cfg.HdrScope + ")");
            sb.AppendLine();

            sb.AppendLine("--- displays right now ---");
            try
            {
                DisplayInfo[] now = DisplayManager.Describe(DisplayManager.Capture());
                foreach (DisplayInfo d in now)
                    sb.AppendLine((DisplayManager.MatchesKeep(d, _cfg.Keep) ? "[keep] " : "[  off] ") + d.Describe());
            }
            catch (Exception ex) { sb.AppendLine("(failed: " + ex.Message + ")"); }

            sb.AppendLine();
            sb.AppendLine("--- saved baseline (restored on disconnect) ---");
            if (_baseline == null) sb.AppendLine("(none captured yet)");
            else
            {
                try
                {
                    foreach (DisplayInfo d in DisplayManager.Describe(_baseline)) sb.AppendLine("        " + d.Describe());
                }
                catch (Exception ex) { sb.AppendLine("(failed: " + ex.Message + ")"); }
            }

            sb.AppendLine();
            sb.AppendLine("Log: " + Paths.LogFile);

            MessageBox.Show(sb.ToString(), "parsec-hooks status", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static void OpenPath(string p)
        {
            try
            {
                if (!File.Exists(p)) { MessageBox.Show("Not found:\n" + p, "parsec-hooks"); return; }
                ProcessStartInfo psi = new ProcessStartInfo(p);
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Exception ex) { MessageBox.Show("Could not open:\n" + p + "\n\n" + ex.Message, "parsec-hooks"); }
        }

        private void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            Log.Info("session ending (" + e.Reason + "); reverting before shutdown");
            try { if (_applied) DoRevert(false); } catch (Exception ex) { Log.Error("revert on session end failed", ex); }
        }

        private void ExitApp()
        {
            Log.Info("exit requested");
            try { CancelPending(); if (_applied) DoRevert(false); }
            catch (Exception ex) { Log.Error("revert on exit failed", ex); }
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { SystemEvents.SessionEnding -= OnSessionEnding; } catch { }
                try { SystemEvents.DisplaySettingsChanging -= OnDisplaySettingsChanging; } catch { }
                try { SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged; } catch { }
                if (_pollTimer != null) { _pollTimer.Stop(); _pollTimer.Dispose(); }
                if (_baselineTimer != null) { _baselineTimer.Stop(); _baselineTimer.Dispose(); }
                if (_pendingTimer != null) { _pendingTimer.Stop(); _pendingTimer.Dispose(); }
                if (_guardTimer != null) { _guardTimer.Stop(); _guardTimer.Dispose(); }
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
                if (_icoIdle != null) _icoIdle.Dispose();
                if (_icoActive != null) _icoActive.Dispose();
                if (_icoPaused != null) _icoPaused.Dispose();
                Log.Info("parsec-hooks stopped");
            }
            base.Dispose(disposing);
        }
    }
}
