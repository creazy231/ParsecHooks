// DefaultLayout.cs -- capture and restore a "this is how the screens should look" snapshot.
//
// This is the escape hatch. Unlike the applied-state file, which exists only between apply
// and revert, this one survives everything until the user replaces it -- so there is always
// something to go back to when a session leaves a display black or at the wrong resolution.
//
// The display work lives here rather than in HookApp so that both the tray menu and the
// command line can use it. That matters: when a screen is blank you cannot read a tray menu,
// but you can still run "ParsecHooks.exe --reset-default" from a shortcut or Run box.
//
// C# 5 only.
using System;
using System.Collections.Generic;

namespace ParsecHooks
{
    internal static class DefaultLayout
    {
        public static bool Exists()
        {
            try { return System.IO.File.Exists(Paths.DefaultsFile); }
            catch { return false; }
        }

        /// <summary>Records whatever is on screen right now: topology, the primary's mode, and
        /// every HDR-capable display's on/off state.</summary>
        public static bool Save(out string describe)
        {
            describe = null;
            try
            {
                Topology t = DisplayManager.Capture();
                DisplayInfo[] infos = DisplayManager.Describe(t);

                // Every HDR-capable display, not just ones we changed, so a reset reproduces the
                // whole picture rather than half of it.
                List<HdrRecord> hdr = new List<HdrRecord>();
                foreach (DisplayInfo d in infos)
                {
                    if (!d.HdrSupported) continue;
                    HdrRecord r = new HdrRecord();
                    r.AdapterLow = d.Adapter.LowPart;
                    r.AdapterHigh = d.Adapter.HighPart;
                    r.TargetId = d.TargetId;
                    r.WasOn = d.HdrEnabled;
                    hdr.Add(r);
                }

                ModeRecord mode = null;
                foreach (DisplayInfo d in infos)
                {
                    if (!d.IsPrimary || string.IsNullOrEmpty(d.Gdi)) continue;
                    DisplayMode m = DisplayManager.GetCurrentMode(d.Gdi);
                    if (m == null) break;
                    mode = new ModeRecord();
                    mode.GdiName = d.Gdi;
                    mode.Width = m.Width; mode.Height = m.Height; mode.Hz = m.Hz;
                    break;
                }

                DisplayManager.SaveState(Paths.DefaultsFile, t, hdr, mode);
                describe = Describe(infos);
                Log.Info("saved default layout: " + describe);
                return true;
            }
            catch (Exception ex) { Log.Error("could not save default layout", ex); return false; }
        }

        /// <summary>Puts the screens back to the saved default. Never throws at the caller --
        /// this is the button you press when things are already wrong.</summary>
        public static bool Reset(out string message)
        {
            Topology t; List<HdrRecord> hdr; ModeRecord mode;
            if (!DisplayManager.TryLoadState(Paths.DefaultsFile, out t, out hdr, out mode) || t == null)
            {
                message = "No default layout saved yet. Use \"Save current layout as default\" first.";
                return false;
            }

            try
            {
                // 1. Panels awake first. WakeAll, not just what we recorded: after a crash there
                //    is no record of what was blanked, and a dark monitor with nothing to wake it
                //    is precisely the situation this exists to fix. Waking is also a monitor
                //    arrival, which makes Windows re-apply its database -- better that happens
                //    now than after our restore, where it would silently undo it.
                try { PanelPower.WakeAll(); } catch (Exception ex) { Log.Error("reset: panel wake failed", ex); }
                System.Threading.Thread.Sleep(600);

                // 2. Topology, persisted so Windows stops second-guessing it.
                int err;
                if (!DisplayManager.Apply(t, true, out err))
                {
                    Log.Warn("reset: topology apply failed (err " + err + "); trying extend fallback");
                    DisplayManager.ApplyExtendFallback();
                }
                System.Threading.Thread.Sleep(600);

                // 3. Resolution.
                if (mode != null && !string.IsNullOrEmpty(mode.GdiName))
                {
                    string how;
                    if (DisplayManager.TrySetMode(mode.GdiName, mode.Mode(), out how))
                        Log.Info("reset: mode on " + mode.GdiName + " -> " + how);
                    else
                        Log.Warn("reset: could not set mode on " + mode.GdiName + ": " + how);
                }

                // 4. HDR to exactly the recorded state, on or off.
                if (hdr != null)
                {
                    foreach (HdrRecord r in hdr)
                    {
                        bool sup, en; uint m, b, e; string via;
                        if (!DisplayManager.TryGetHdr(r.Adapter(), r.TargetId, out sup, out en, out m, out b, out e, out via)) continue;
                        if (!sup || en == r.WasOn) continue;
                        string how;
                        if (DisplayManager.SetHdr(r.Adapter(), r.TargetId, r.WasOn, out how))
                            Log.Info("reset: HDR " + (r.WasOn ? "ON" : "off") + " for tgt=" + r.TargetId);
                    }
                }

                // 5. Icons, once the desktop is its proper size again.
                List<IconRecord> icons = DesktopIcons.Load(Paths.IconsFile);
                if (icons != null)
                {
                    try { DesktopIcons.Restore(icons); } catch (Exception ex) { Log.Error("reset: icon restore failed", ex); }
                    DesktopIcons.ClearSaved(Paths.IconsFile);
                }

                DisplayManager.ClearState(Paths.StateFile);
                DisplayManager.PersistCurrent();

                message = Describe(DisplayManager.Describe(DisplayManager.Capture()));
                Log.Info("reset to default layout -> " + message);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("reset to default layout failed", ex);
                message = "Reset failed - see the log.";
                return false;
            }
        }

        private static string Describe(DisplayInfo[] infos)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append(infos.Length + " active");
            foreach (DisplayInfo d in infos)
            {
                sb.Append("  |  " + (string.IsNullOrEmpty(d.Name) ? "?" : d.Name) + (d.IsPrimary ? "*" : ""));
                sb.Append(" " + d.Width + "x" + d.Height + "@" + Math.Round(d.Hz));
                sb.Append(" HDR=" + (!d.HdrSupported ? "n/a" : (d.HdrEnabled ? "ON" : "off")));
            }
            return sb.ToString();
        }
    }
}
