// Program.cs -- entry point, single-instance guard, emergency --revert. C# 5 only.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;

namespace ParsecHooks
{
    internal static class Program
    {
        private const string MutexName = @"Local\parsec-hooks-singleton";

        [STAThread]
        private static int Main(string[] args)
        {
            foreach (string a in args)
            {
                string s = a.TrimStart('-', '/').ToLowerInvariant();
                if (s == "revert" || s == "restore") return EmergencyRevert();
                if (s == "settings" || s == "config") return ShowSettingsStandalone();
                if (s == "help" || s == "?" )
                {
                    MessageBox.Show(
                        "parsec-hooks\n\n" +
                        "  (no arguments)   run in the system tray\n" +
                        "  --settings       open the settings dialog on its own\n" +
                        "  --revert         restore displays/HDR from the saved state file and exit\n\n" +
                        "Config: " + Paths.ConfigFile + "\nLog:    " + Paths.LogFile,
                        "parsec-hooks", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }
            }

            bool createdNew;
            using (Mutex mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("parsec-hooks is already running (look for the monitor icon in the tray).",
                                    "parsec-hooks", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // Never let a stray exception kill the process while displays are modified;
                // HookApp reverts on exit and leaves a recoverable state file behind.
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    Log.Error("unhandled UI exception", e.Exception);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    Log.Error("unhandled domain exception", e.ExceptionObject as Exception);
                };

                HookApp app = null;
                try
                {
                    app = new HookApp();
                    Application.Run(app);
                }
                catch (Exception ex)
                {
                    Log.Error("fatal startup error", ex);
                    MessageBox.Show("parsec-hooks failed to start:\n\n" + ex.Message +
                                    "\n\nSee " + Paths.LogFile, "parsec-hooks",
                                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
                finally
                {
                    if (app != null) app.Dispose();
                }
                return 0;
            }
        }

        /// <summary>Opens just the settings dialog, without the tray. Useful before the app is
        /// running, or from a shortcut. An already-running tray instance notices the rewritten
        /// config file by itself and reloads, so no IPC is needed.</summary>
        private static int ShowSettingsStandalone()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Config cfg = Config.Load();
            AutoStart.MigrateLegacyShortcut();

            string found = null;
            foreach (string p in ParsecWatcher.CandidatePaths(cfg.LogPath))
            {
                try { if (System.IO.File.Exists(p)) { found = p; break; } }
                catch { }
            }
            string logPath = found;

            using (SettingsForm f = new SettingsForm(cfg,
                       delegate { return DisplayManager.Describe(DisplayManager.Capture()); },
                       delegate { return logPath; }))
            {
                f.ShowDialog();
            }
            return 0;
        }

        /// <summary>Restores whatever the last apply recorded, without starting the tray.
        /// This is the escape hatch if the app was killed while monitors were disabled.</summary>
        private static int EmergencyRevert()
        {
            Topology t; List<HdrRecord> h; ModeRecord mr;
            if (!DisplayManager.TryLoadState(Paths.StateFile, out t, out h, out mr))
            {
                // Nothing recorded: still offer to light every panel back up.
                bool ok = DisplayManager.ApplyExtendFallback();
                MessageBox.Show(
                    "No saved display state was found at:\n" + Paths.StateFile +
                    "\n\nApplied Windows' 'Extend' topology instead: " + (ok ? "OK" : "FAILED") +
                    "\n\n(Exact monitor positions may need a manual fix in Display settings.)",
                    "parsec-hooks --revert", MessageBoxButtons.OK,
                    ok ? MessageBoxIcon.Warning : MessageBoxIcon.Error);
                return ok ? 0 : 1;
            }

            // HDR first, topology last: an HDR change makes Windows re-apply its persisted
            // topology, so doing it afterwards would undo the geometry we just restored.
            int restored = 0;
            if (h != null)
            {
                foreach (HdrRecord r in h)
                {
                    if (!r.WasOn) continue;
                    string how;
                    if (DisplayManager.SetHdr(r.Adapter(), r.TargetId, true, out how)) restored++;
                }
                if (h.Count > 0) System.Threading.Thread.Sleep(400);
            }

            string modeMsg = "not changed";
            if (mr != null)
            {
                string how;
                if (DisplayManager.TrySetMode(mr.GdiName, mr.Mode(), out how)) modeMsg = "restored (" + how + ")";
                else
                {
                    string how2;
                    DisplayManager.ResetModeToDefault(mr.GdiName, out how2);
                    modeMsg = "recorded mode failed (" + how + "), registry default -> " + how2;
                }
                System.Threading.Thread.Sleep(400);
            }

            int err;
            bool applied = DisplayManager.Apply(t, true, out err);
            if (!applied) DisplayManager.ApplyExtendFallback();

            DisplayManager.ClearState(Paths.StateFile);
            MessageBox.Show(
                "Topology restore: " + (applied ? "OK" : "FAILED (err " + err + "), used Extend fallback") +
                "\nHDR restored on " + restored + " display(s)." +
                "\nResolution: " + modeMsg,
                "parsec-hooks --revert", MessageBoxButtons.OK,
                applied ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            return applied ? 0 : 1;
        }
    }
}
