// DisplayManager.cs -- topology snapshot/restore and HDR get/set via the CCD API.
// C# 5 only.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ParsecHooks
{
    internal class DisplayInfo
    {
        public Native.LUID Adapter;
        public uint SourceId;
        public uint TargetId;
        public string Name;
        public string Gdi;
        public string DevicePath;
        public uint Width;
        public uint Height;
        public int PosX;
        public int PosY;
        public double Hz;
        public bool IsPrimary;
        public uint OutputTech;
        public bool HdrSupported;
        public bool HdrEnabled;
        public uint ActiveColorMode; // 0=SDR 1=WCG 2=HDR

        public string Short()
        {
            return (string.IsNullOrEmpty(Name) ? "?" : Name) + " (tgt " + TargetId + ")";
        }

        public string Describe()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(IsPrimary ? "PRIMARY   " : "secondary ");
            sb.Append((string.IsNullOrEmpty(Name) ? "?" : Name).PadRight(22));
            sb.Append(Width.ToString(CultureInfo.InvariantCulture) + "x" + Height.ToString(CultureInfo.InvariantCulture));
            sb.Append(" @(" + PosX + "," + PosY + ")");
            sb.Append(" " + Hz.ToString("0.###", CultureInfo.InvariantCulture) + "Hz");
            sb.Append("  " + Native.OutputTechName(OutputTech));
            sb.Append("  " + (Gdi == null ? "" : Gdi));
            sb.Append("  HDR=");
            if (!HdrSupported) sb.Append("unsupported");
            else sb.Append(HdrEnabled ? "ON" : "off");
            return sb.ToString();
        }
    }

    /// <summary>An exact CCD configuration: the path and mode arrays as returned by
    /// QueryDisplayConfig. Re-applying these verbatim restores position, resolution and
    /// refresh rate bit-for-bit, which "just enable everything" cannot do.</summary>
    internal class Topology
    {
        public Native.PATH_INFO[] Paths;
        public Native.MODE_INFO[] Modes;

        public Topology Clone()
        {
            Topology t = new Topology();
            t.Paths = (Native.PATH_INFO[])Paths.Clone();
            t.Modes = (Native.MODE_INFO[])Modes.Clone();
            return t;
        }
    }

    internal class DisplayMode
    {
        public uint Width;
        public uint Height;
        public uint Hz;

        public override string ToString()
        {
            return Width + "x" + Height + (Hz > 0 ? "@" + Hz : "");
        }

        public string Pretty()
        {
            string ar = AspectName(Width, Height);
            return Width + " x " + Height + (Hz > 0 ? "  " + Hz + " Hz" : "") + (ar == null ? "" : "   (" + ar + ")");
        }

        public static string AspectName(uint w, uint h)
        {
            if (h == 0) return null;
            double a = (double)w / h;
            if (Math.Abs(a - 16.0 / 9.0) < 0.02) return "16:9";
            if (Math.Abs(a - 16.0 / 10.0) < 0.02) return "16:10";
            if (Math.Abs(a - 4.0 / 3.0) < 0.02) return "4:3";
            if (Math.Abs(a - 21.0 / 9.0) < 0.06) return "21:9";
            return null;
        }

        /// <summary>Parses "1280x800" or "1280x800@60". Empty/blank means "do not change".</summary>
        public static bool TryParse(string s, out DisplayMode m)
        {
            m = null;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim().ToLowerInvariant();
            if (s.Length == 0 || s == "off" || s == "none" || s == "auto") return false;

            uint hz = 0;
            int at = s.IndexOf('@');
            if (at >= 0)
            {
                string hzs = s.Substring(at + 1).Replace("hz", "").Trim();
                if (!uint.TryParse(hzs, NumberStyles.Integer, CultureInfo.InvariantCulture, out hz)) hz = 0;
                s = s.Substring(0, at).Trim();
            }

            int x = s.IndexOf('x');
            if (x <= 0) return false;
            uint w, h;
            if (!uint.TryParse(s.Substring(0, x).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out w)) return false;
            if (!uint.TryParse(s.Substring(x + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out h)) return false;
            if (w < 320 || h < 200 || w > 32000 || h > 32000) return false;

            m = new DisplayMode();
            m.Width = w; m.Height = h; m.Hz = hz;
            return true;
        }
    }

    /// <summary>The display mode we found before changing it, so a crashed run can put it
    /// back on the next launch.</summary>
    internal class ModeRecord
    {
        public string GdiName;
        public uint Width;
        public uint Height;
        public uint Hz;

        public DisplayMode Mode()
        {
            DisplayMode m = new DisplayMode();
            m.Width = Width; m.Height = Height; m.Hz = Hz;
            return m;
        }
    }

    internal class HdrRecord
    {
        public uint AdapterLow;
        public int AdapterHigh;
        public uint TargetId;
        public bool WasOn;

        public Native.LUID Adapter()
        {
            Native.LUID l = new Native.LUID();
            l.LowPart = AdapterLow;
            l.HighPart = AdapterHigh;
            return l;
        }
    }

    internal static class DisplayManager
    {
        private const uint ApplyFlags = Native.SDC_APPLY | Native.SDC_USE_SUPPLIED_DISPLAY_CONFIG | Native.SDC_ALLOW_CHANGES;
        private const uint ValidateFlags = Native.SDC_VALIDATE | Native.SDC_USE_SUPPLIED_DISPLAY_CONFIG | Native.SDC_ALLOW_CHANGES;

        // ---------------- capture / describe ----------------

        public static Topology Capture()
        {
            uint np, nm;
            int err = Native.GetDisplayConfigBufferSizes(Native.QDC_ONLY_ACTIVE_PATHS, out np, out nm);
            if (err != 0) throw new InvalidOperationException("GetDisplayConfigBufferSizes failed: " + err);

            Native.PATH_INFO[] paths = new Native.PATH_INFO[np];
            Native.MODE_INFO[] modes = new Native.MODE_INFO[nm];
            err = Native.QueryDisplayConfig(Native.QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero);
            if (err != 0) throw new InvalidOperationException("QueryDisplayConfig failed: " + err);

            Array.Resize(ref paths, (int)np);
            Array.Resize(ref modes, (int)nm);

            Topology t = new Topology();
            t.Paths = paths;
            t.Modes = modes;
            return t;
        }

        public static DisplayInfo[] Describe(Topology t)
        {
            List<DisplayInfo> list = new List<DisplayInfo>();
            for (int i = 0; i < t.Paths.Length; i++)
            {
                Native.PATH_INFO p = t.Paths[i];
                if ((p.flags & Native.DISPLAYCONFIG_PATH_ACTIVE) == 0) continue;

                DisplayInfo d = new DisplayInfo();
                d.Adapter = p.targetInfo.adapterId;
                d.SourceId = p.sourceInfo.id;
                d.TargetId = p.targetInfo.id;
                d.Hz = p.targetInfo.refreshRate.Hz;

                string devPath; uint tech;
                d.Name = QueryTargetName(p.targetInfo.adapterId, p.targetInfo.id, out devPath, out tech);
                d.DevicePath = devPath;
                d.OutputTech = tech;
                d.Gdi = QuerySourceGdi(p.sourceInfo.adapterId, p.sourceInfo.id);

                Native.SOURCE_MODE sm;
                if (TryGetSourceMode(t, p, out sm))
                {
                    d.Width = sm.width;
                    d.Height = sm.height;
                    d.PosX = sm.position.x;
                    d.PosY = sm.position.y;
                }
                d.IsPrimary = (d.PosX == 0 && d.PosY == 0);

                bool sup, en; uint mode, bpc, enc; string via;
                if (TryGetHdr(d.Adapter, d.TargetId, out sup, out en, out mode, out bpc, out enc, out via))
                {
                    d.HdrSupported = sup;
                    d.HdrEnabled = en;
                    d.ActiveColorMode = mode;
                }
                list.Add(d);
            }
            return list.ToArray();
        }

        private static bool TryGetSourceMode(Topology t, Native.PATH_INFO p, out Native.SOURCE_MODE sm)
        {
            sm = new Native.SOURCE_MODE();
            uint idx = p.sourceInfo.modeInfoIdx;
            if (idx != Native.DISPLAYCONFIG_PATH_MODE_IDX_INVALID && idx < (uint)t.Modes.Length &&
                t.Modes[idx].infoType == Native.MODE_INFO_TYPE_SOURCE)
            {
                sm = t.Modes[idx].mode.sourceMode;
                return true;
            }
            // Fall back to a scan (belt and braces if the index is not usable).
            for (int m = 0; m < t.Modes.Length; m++)
            {
                if (t.Modes[m].infoType == Native.MODE_INFO_TYPE_SOURCE &&
                    t.Modes[m].id == p.sourceInfo.id &&
                    t.Modes[m].adapterId.Equals(p.sourceInfo.adapterId))
                {
                    sm = t.Modes[m].mode.sourceMode;
                    return true;
                }
            }
            return false;
        }

        public static string QueryTargetName(Native.LUID adapter, uint id, out string devicePath, out uint outputTech)
        {
            Native.TARGET_DEVICE_NAME t = new Native.TARGET_DEVICE_NAME();
            t.header.type = Native.DEVICE_INFO_GET_TARGET_NAME;
            t.header.size = (uint)Marshal.SizeOf(typeof(Native.TARGET_DEVICE_NAME));
            t.header.adapterId = adapter;
            t.header.id = id;
            int r = Native.GetTargetName(ref t);
            devicePath = (r == 0) ? t.monitorDevicePath : null;
            outputTech = (r == 0) ? t.outputTechnology : 0;
            return (r == 0) ? t.monitorFriendlyDeviceName : null;
        }

        public static string QuerySourceGdi(Native.LUID adapter, uint id)
        {
            Native.SOURCE_DEVICE_NAME s = new Native.SOURCE_DEVICE_NAME();
            s.header.type = Native.DEVICE_INFO_GET_SOURCE_NAME;
            s.header.size = (uint)Marshal.SizeOf(typeof(Native.SOURCE_DEVICE_NAME));
            s.header.adapterId = adapter;
            s.header.id = id;
            return Native.GetSourceName(ref s) == 0 ? s.viewGdiDeviceName : null;
        }

        // ---------------- HDR ----------------

        /// <summary>Reads HDR state, preferring the 24H2+ packet and falling back to the
        /// legacy one on older builds.</summary>
        public static bool TryGetHdr(Native.LUID adapter, uint id, out bool supported, out bool enabled,
                                     out uint activeColorMode, out uint bitsPerChannel, out uint colorEncoding, out string via)
        {
            Native.ADV_COLOR_INFO_2 b = new Native.ADV_COLOR_INFO_2();
            b.header.type = Native.DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2;
            b.header.size = (uint)Marshal.SizeOf(typeof(Native.ADV_COLOR_INFO_2));
            b.header.adapterId = adapter;
            b.header.id = id;
            int r2 = Native.GetAdvancedColorInfo2(ref b);
            if (r2 == 0)
            {
                supported = (b.value & Native.ACI2_HDR_SUPPORTED) != 0;
                enabled = (b.value & Native.ACI2_HDR_USER_ENABLED) != 0;
                activeColorMode = b.activeColorMode;
                bitsPerChannel = b.bitsPerColorChannel;
                colorEncoding = b.colorEncoding;
                via = "ADVANCED_COLOR_INFO_2 raw=0x" + b.value.ToString("X8");
                return true;
            }

            Native.ADV_COLOR_INFO c = new Native.ADV_COLOR_INFO();
            c.header.type = Native.DEVICE_INFO_GET_ADVANCED_COLOR_INFO;
            c.header.size = (uint)Marshal.SizeOf(typeof(Native.ADV_COLOR_INFO));
            c.header.adapterId = adapter;
            c.header.id = id;
            int r1 = Native.GetAdvancedColorInfo(ref c);
            if (r1 == 0)
            {
                supported = (c.value & Native.ACI_SUPPORTED) != 0;
                enabled = (c.value & Native.ACI_ENABLED) != 0;
                activeColorMode = enabled ? 2u : 0u;
                bitsPerChannel = c.bitsPerColorChannel;
                colorEncoding = c.colorEncoding;
                via = "ADVANCED_COLOR_INFO raw=0x" + c.value.ToString("X8");
                return true;
            }

            supported = false; enabled = false; activeColorMode = 0; bitsPerChannel = 0; colorEncoding = 0;
            via = "unavailable (info2 err=" + r2 + ", info err=" + r1 + ")";
            return false;
        }

        /// <summary>Sets HDR, preferring SET_HDR_STATE and falling back to the legacy
        /// SET_ADVANCED_COLOR_STATE. Returns true on success.</summary>
        public static bool SetHdr(Native.LUID adapter, uint id, bool on, out string how)
        {
            Native.SET_HDR_STATE h = new Native.SET_HDR_STATE();
            h.header.type = Native.DEVICE_INFO_SET_HDR_STATE;
            h.header.size = (uint)Marshal.SizeOf(typeof(Native.SET_HDR_STATE));
            h.header.adapterId = adapter;
            h.header.id = id;
            h.value = on ? 1u : 0u;
            int rNew = Native.SetHdrStateRaw(ref h);
            if (rNew == 0) { how = "SET_HDR_STATE"; return true; }

            Native.SET_ADV_COLOR_STATE s = new Native.SET_ADV_COLOR_STATE();
            s.header.type = Native.DEVICE_INFO_SET_ADVANCED_COLOR_STATE;
            s.header.size = (uint)Marshal.SizeOf(typeof(Native.SET_ADV_COLOR_STATE));
            s.header.adapterId = adapter;
            s.header.id = id;
            s.value = on ? 1u : 0u;
            int rOld = Native.SetAdvancedColorState(ref s);
            if (rOld == 0) { how = "SET_ADVANCED_COLOR_STATE (new err=" + rNew + ")"; return true; }

            how = "FAILED (SET_HDR_STATE=" + rNew + ", SET_ADVANCED_COLOR_STATE=" + rOld + ")";
            return false;
        }

        // ---------------- topology mutation ----------------

        public static bool MatchesKeep(DisplayInfo d, string keep)
        {
            if (string.IsNullOrEmpty(keep) || string.Equals(keep, "primary", StringComparison.OrdinalIgnoreCase))
                return d.IsPrimary;

            uint tid;
            if (uint.TryParse(keep.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out tid))
                return d.TargetId == tid;

            string k = keep.Trim();
            if (d.Name != null && d.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (d.DevicePath != null && d.DevicePath.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>Queries the CURRENT topology and returns it with every path that is not
        /// being kept marked inactive. Returns null if there is nothing to disable, or if the
        /// selector would leave zero displays.</summary>
        /// <remarks>
        /// Building this from a live query rather than from the idle baseline is essential.
        /// Parsec changes the host's primary display mode to match the connecting client
        /// (3440x1440 becomes 1280x800 for a Steam Deck), and applying baseline path/mode
        /// arrays would overwrite that back to the idle resolution. Re-querying means we only
        /// ever clear the active flags and leave the current mode untouched, so Parsec's own
        /// resolution matching keeps working.
        /// </remarks>
        public static Topology BuildKeepOnlyFromCurrent(string keep, out List<DisplayInfo> disabled, out List<DisplayInfo> kept)
        {
            Topology current = Capture();
            DisplayInfo[] infos = Describe(current);
            return BuildKeepOnly(current, infos, keep, out disabled, out kept);
        }

        /// <summary>Builds a copy of <paramref name="source"/> with every path that is not
        /// being kept marked inactive. Returns null if that would leave zero displays.</summary>
        public static Topology BuildKeepOnly(Topology source, DisplayInfo[] infos, string keep,
                                             out List<DisplayInfo> disabled, out List<DisplayInfo> kept)
        {
            Topology baseline = source;
            disabled = new List<DisplayInfo>();
            kept = new List<DisplayInfo>();
            foreach (DisplayInfo d in infos)
            {
                if (MatchesKeep(d, keep)) kept.Add(d); else disabled.Add(d);
            }

            if (kept.Count == 0)
            {
                Log.Error("refusing to change topology: selector '" + keep + "' matched no display " +
                          "(that would black out every screen)");
                return null;
            }
            if (disabled.Count == 0) return null; // nothing to do

            Topology mod = baseline.Clone();
            for (int i = 0; i < mod.Paths.Length; i++)
            {
                if ((mod.Paths[i].flags & Native.DISPLAYCONFIG_PATH_ACTIVE) == 0) continue;
                uint tgt = mod.Paths[i].targetInfo.id;

                bool keepThis = false;
                foreach (DisplayInfo k in kept)
                {
                    if (k.TargetId == tgt && k.Adapter.Equals(mod.Paths[i].targetInfo.adapterId)) { keepThis = true; break; }
                }
                if (keepThis) continue;

                mod.Paths[i].flags &= ~Native.DISPLAYCONFIG_PATH_ACTIVE;
                mod.Paths[i].sourceInfo.modeInfoIdx = Native.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
                mod.Paths[i].targetInfo.modeInfoIdx = Native.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
            }
            return mod;
        }

        /// <summary>Validates then applies a topology. Validation first means a config the
        /// driver would reject never reaches the screen.</summary>
        /// <remarks>
        /// Both calls get throwaway COPIES of the arrays, which is essential rather than
        /// tidy. SDC_ALLOW_CHANGES explicitly permits SetDisplayConfig to rewrite the
        /// supplied path/mode arrays in place so it can massage them into something valid.
        /// Two consequences, both of which bit during testing:
        ///   1. Validating with the same arrays you then apply makes the validate pass
        ///      overwrite your intent -- it rewrites the config back to what is already
        ///      active, so SDC_APPLY reports success while nothing visibly changes.
        ///   2. Applying the caller's array directly would corrupt a long-lived snapshot
        ///      such as the baseline we need to restore later.
        /// </remarks>
        public static bool Apply(Topology t, out int err) { return Apply(t, false, out err); }

        /// <param name="persist">
        /// Write the config to Windows' display database as well as applying it.
        ///
        /// This is required for a session topology, not optional. A transient topology change
        /// leaves the database disagreeing with reality, and Windows then re-applies the
        /// database roughly every 10-12 seconds: the disabled monitor comes back AND the
        /// resolution snaps to the persisted mode, which is what made a Steam Deck session
        /// flap between 1280x800 and 3440x1440 indefinitely. Persisting removes the
        /// disagreement, so there is nothing left for Windows to restore.
        ///
        /// The cost is that a hard power-off mid-session boots with the monitor still
        /// disabled. That is covered by the applied-state file: the app restores it at logon,
        /// and "--revert" does it by hand. Reverting always persists the baseline back.
        /// </param>
        public static bool Apply(Topology t, bool persist, out int err)
        {
            Native.PATH_INFO[] vp = (Native.PATH_INFO[])t.Paths.Clone();
            Native.MODE_INFO[] vm = (Native.MODE_INFO[])t.Modes.Clone();
            err = Native.SetDisplayConfig((uint)vp.Length, vp, (uint)vm.Length, vm, ValidateFlags);
            if (err != 0)
            {
                Log.Warn("SDC_VALIDATE rejected the config (err " + err + "); not applying");
                return false;
            }

            Native.PATH_INFO[] ap = (Native.PATH_INFO[])t.Paths.Clone();
            Native.MODE_INFO[] am = (Native.MODE_INFO[])t.Modes.Clone();
            uint flags = ApplyFlags | (persist ? Native.SDC_SAVE_TO_DATABASE : 0u);
            err = Native.SetDisplayConfig((uint)ap.Length, ap, (uint)am.Length, am, flags);
            if (err != 0)
            {
                Log.Error("SDC_APPLY failed with err " + err + (persist ? " (with SAVE_TO_DATABASE)" : ""));
                return false;
            }
            return true;
        }

        /// <summary>Writes the CURRENT active configuration into Windows' display database
        /// without changing anything on screen.
        ///
        /// This exists because any disagreement between the live state and the database makes
        /// Windows re-apply the database roughly every 10 seconds, and that resets the
        /// resolution. Our own sequence creates exactly such a disagreement: the topology is
        /// persisted while the mode is still the clobbered one, and the mode is then repaired
        /// transiently. Ratifying afterwards makes live == database, so nothing is left to
        /// revert -- including the resolution Parsec chose for the client.
        ///
        /// Note this never *chooses* a mode. It only makes whatever is already on screen
        /// authoritative, so Windows stops second-guessing Parsec.</summary>
        public static bool PersistCurrent()
        {
            try
            {
                Topology t = Capture();
                Native.PATH_INFO[] p = (Native.PATH_INFO[])t.Paths.Clone();
                Native.MODE_INFO[] m = (Native.MODE_INFO[])t.Modes.Clone();
                int r = Native.SetDisplayConfig((uint)p.Length, p, (uint)m.Length, m,
                    Native.SDC_APPLY | Native.SDC_USE_SUPPLIED_DISPLAY_CONFIG |
                    Native.SDC_ALLOW_CHANGES | Native.SDC_SAVE_TO_DATABASE);
                if (r != 0) Log.Warn("could not ratify the current layout into the display database (err " + r + ")");
                return r == 0;
            }
            catch (Exception ex) { Log.Error("PersistCurrent failed", ex); return false; }
        }

        /// <summary>Counts currently-active displays straight from the OS. Used to confirm a
        /// topology change actually landed, rather than trusting SetDisplayConfig's return.</summary>
        public static int ActiveDisplayCount()
        {
            try
            {
                uint np, nm;
                if (Native.GetDisplayConfigBufferSizes(Native.QDC_ONLY_ACTIVE_PATHS, out np, out nm) != 0) return -1;
                Native.PATH_INFO[] paths = new Native.PATH_INFO[np];
                Native.MODE_INFO[] modes = new Native.MODE_INFO[nm];
                if (Native.QueryDisplayConfig(Native.QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return -1;
                int n = 0;
                for (int i = 0; i < (int)np; i++)
                    if ((paths[i].flags & Native.DISPLAYCONFIG_PATH_ACTIVE) != 0) n++;
                return n;
            }
            catch { return -1; }
        }

        // ---------------- display mode (resolution / refresh) ----------------

        private static Native.DEVMODE NewDevMode()
        {
            Native.DEVMODE dm = new Native.DEVMODE();
            dm.dmDeviceName = "";
            dm.dmFormName = "";
            dm.dmSize = (ushort)Marshal.SizeOf(typeof(Native.DEVMODE));
            return dm;
        }

        public static DisplayMode GetCurrentMode(string gdiName)
        {
            if (string.IsNullOrEmpty(gdiName)) return null;
            Native.DEVMODE dm = NewDevMode();
            if (!Native.EnumDisplaySettingsEx(gdiName, Native.ENUM_CURRENT_SETTINGS, ref dm, 0)) return null;
            DisplayMode m = new DisplayMode();
            m.Width = dm.dmPelsWidth;
            m.Height = dm.dmPelsHeight;
            m.Hz = dm.dmDisplayFrequency;
            return m;
        }

        /// <summary>Distinct 32bpp modes this output supports, largest first, each carrying its
        /// highest refresh rate. Used to populate the settings dropdown so the user picks a
        /// real mode instead of typing one the driver will reject.</summary>
        public static List<DisplayMode> EnumModes(string gdiName)
        {
            List<DisplayMode> list = new List<DisplayMode>();
            if (string.IsNullOrEmpty(gdiName)) return list;

            Dictionary<string, DisplayMode> seen = new Dictionary<string, DisplayMode>();
            for (uint i = 0; ; i++)
            {
                Native.DEVMODE dm = NewDevMode();
                if (!Native.EnumDisplaySettingsEx(gdiName, i, ref dm, 0)) break;
                if (dm.dmBitsPerPel != 32) continue;
                if (dm.dmPelsWidth == 0 || dm.dmPelsHeight == 0) continue;

                string key = dm.dmPelsWidth + "x" + dm.dmPelsHeight;
                DisplayMode existing;
                if (seen.TryGetValue(key, out existing))
                {
                    if (dm.dmDisplayFrequency > existing.Hz) existing.Hz = dm.dmDisplayFrequency;
                    continue;
                }
                DisplayMode m = new DisplayMode();
                m.Width = dm.dmPelsWidth;
                m.Height = dm.dmPelsHeight;
                m.Hz = dm.dmDisplayFrequency;
                seen[key] = m;
                list.Add(m);
            }

            list.Sort(delegate(DisplayMode a, DisplayMode b)
            {
                long aa = (long)a.Width * a.Height, bb = (long)b.Width * b.Height;
                if (aa != bb) return bb.CompareTo(aa);
                return b.Hz.CompareTo(a.Hz);
            });
            return list;
        }

        /// <summary>Applies a mode using a DEVMODE obtained from EnumDisplaySettings (as the
        /// API docs require), validating with CDS_TEST first. flags = 0 keeps the change dynamic
        /// and out of the registry, so a crash leaves nothing stuck.</summary>
        public static bool TrySetMode(string gdiName, DisplayMode want, out string how)
        {
            how = "no display";
            if (string.IsNullOrEmpty(gdiName) || want == null) return false;

            Native.DEVMODE best = NewDevMode();
            bool found = false;
            for (uint i = 0; ; i++)
            {
                Native.DEVMODE dm = NewDevMode();
                if (!Native.EnumDisplaySettingsEx(gdiName, i, ref dm, 0)) break;
                if (dm.dmBitsPerPel != 32) continue;
                if (dm.dmPelsWidth != want.Width || dm.dmPelsHeight != want.Height) continue;

                if (want.Hz > 0)
                {
                    if (dm.dmDisplayFrequency == want.Hz) { best = dm; found = true; break; }
                    // Keep the nearest rate as a fallback if the exact one is unavailable.
                    if (!found || AbsDiff(dm.dmDisplayFrequency, want.Hz) < AbsDiff(best.dmDisplayFrequency, want.Hz))
                    { best = dm; found = true; }
                }
                else if (!found || dm.dmDisplayFrequency > best.dmDisplayFrequency) { best = dm; found = true; }
            }

            if (!found)
            {
                how = want + " is not a supported mode on " + gdiName;
                return false;
            }

            best.dmFields = Native.DM_PELSWIDTH | Native.DM_PELSHEIGHT | Native.DM_BITSPERPEL | Native.DM_DISPLAYFREQUENCY;

            int test = Native.ChangeDisplaySettingsEx(gdiName, ref best, IntPtr.Zero, Native.CDS_TEST, IntPtr.Zero);
            if (test != Native.DISP_CHANGE_SUCCESSFUL)
            {
                how = "CDS_TEST rejected " + best.dmPelsWidth + "x" + best.dmPelsHeight + "@" +
                      best.dmDisplayFrequency + ": " + Native.DispChangeName(test);
                return false;
            }

            int r = Native.ChangeDisplaySettingsEx(gdiName, ref best, IntPtr.Zero, Native.CDS_DYNAMIC, IntPtr.Zero);
            how = best.dmPelsWidth + "x" + best.dmPelsHeight + "@" + best.dmDisplayFrequency + " -> " + Native.DispChangeName(r);
            return r == Native.DISP_CHANGE_SUCCESSFUL;
        }

        private static uint AbsDiff(uint a, uint b) { return a > b ? a - b : b - a; }

        /// <summary>Returns the output to the mode stored in the registry.</summary>
        public static bool ResetModeToDefault(string gdiName, out string how)
        {
            how = "no display";
            if (string.IsNullOrEmpty(gdiName)) return false;
            int r = Native.ChangeDisplaySettingsExDefault(gdiName, IntPtr.Zero, IntPtr.Zero, Native.CDS_DYNAMIC, IntPtr.Zero);
            how = Native.DispChangeName(r);
            return r == Native.DISP_CHANGE_SUCCESSFUL;
        }

        /// <summary>Last-resort recovery: ask Windows for its own extended-desktop topology.
        /// Loses exact positions but gets every panel lit again.</summary>
        public static bool ApplyExtendFallback()
        {
            int r = Native.SetDisplayConfig(0, null, 0, null, Native.SDC_APPLY | Native.SDC_TOPOLOGY_EXTEND);
            Log.Warn("extend-topology fallback -> " + (r == 0 ? "OK" : "err " + r));
            return r == 0;
        }

        // ---------------- persistence ----------------
        // Raw struct bytes, guarded by a magic value and the struct sizes they were
        // written with, so a future OS/SDK layout change is detected instead of
        // being fed back into SetDisplayConfig.

        private const string Magic = "PHK2";     // PHK1 had no mode section
        private const string MagicV1 = "PHK1";

        public static void SaveState(string file, Topology t, List<HdrRecord> hdr, ModeRecord mode)
        {
            try
            {
                using (FileStream fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None))
                using (BinaryWriter w = new BinaryWriter(fs))
                {
                    w.Write(Encoding.ASCII.GetBytes(Magic));
                    w.Write(Marshal.SizeOf(typeof(Native.PATH_INFO)));
                    w.Write(Marshal.SizeOf(typeof(Native.MODE_INFO)));
                    w.Write(t.Paths.Length);
                    w.Write(t.Modes.Length);
                    w.Write(StructsToBytes(t.Paths, typeof(Native.PATH_INFO)));
                    w.Write(StructsToBytes(t.Modes, typeof(Native.MODE_INFO)));
                    w.Write(hdr == null ? 0 : hdr.Count);
                    if (hdr != null)
                    {
                        foreach (HdrRecord h in hdr)
                        {
                            w.Write(h.AdapterLow);
                            w.Write(h.AdapterHigh);
                            w.Write(h.TargetId);
                            w.Write(h.WasOn);
                        }
                    }

                    w.Write(mode != null);
                    if (mode != null)
                    {
                        w.Write(mode.GdiName == null ? "" : mode.GdiName);
                        w.Write(mode.Width);
                        w.Write(mode.Height);
                        w.Write(mode.Hz);
                    }
                }
                Log.Debug("saved applied-state to " + file);
            }
            catch (Exception ex) { Log.Error("SaveState failed", ex); }
        }

        public static bool TryLoadState(string file, out Topology t, out List<HdrRecord> hdr, out ModeRecord mode)
        {
            t = null; hdr = null; mode = null;
            try
            {
                if (!File.Exists(file)) return false;
                using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (BinaryReader r = new BinaryReader(fs))
                {
                    string magic = Encoding.ASCII.GetString(r.ReadBytes(4));
                    bool v1 = (magic == MagicV1);
                    if (magic != Magic && !v1) { Log.Warn("state file magic mismatch; ignoring"); return false; }

                    int pathSize = r.ReadInt32();
                    int modeSize = r.ReadInt32();
                    if (pathSize != Marshal.SizeOf(typeof(Native.PATH_INFO)) ||
                        modeSize != Marshal.SizeOf(typeof(Native.MODE_INFO)))
                    {
                        Log.Warn("state file struct sizes differ from this build; ignoring");
                        return false;
                    }

                    int pathCount = r.ReadInt32();
                    int modeCount = r.ReadInt32();
                    if (pathCount < 0 || modeCount < 0 || pathCount > 4096 || modeCount > 8192) return false;

                    byte[] pb = r.ReadBytes(pathCount * pathSize);
                    byte[] mb = r.ReadBytes(modeCount * modeSize);
                    if (pb.Length != pathCount * pathSize || mb.Length != modeCount * modeSize) return false;

                    t = new Topology();
                    t.Paths = (Native.PATH_INFO[])BytesToStructs(pb, typeof(Native.PATH_INFO));
                    t.Modes = (Native.MODE_INFO[])BytesToStructs(mb, typeof(Native.MODE_INFO));

                    hdr = new List<HdrRecord>();
                    int hc = r.ReadInt32();
                    for (int i = 0; i < hc; i++)
                    {
                        HdrRecord h = new HdrRecord();
                        h.AdapterLow = r.ReadUInt32();
                        h.AdapterHigh = r.ReadInt32();
                        h.TargetId = r.ReadUInt32();
                        h.WasOn = r.ReadBoolean();
                        hdr.Add(h);
                    }

                    // v1 files stop here; they simply carry no recorded mode.
                    if (!v1 && r.ReadBoolean())
                    {
                        mode = new ModeRecord();
                        mode.GdiName = r.ReadString();
                        mode.Width = r.ReadUInt32();
                        mode.Height = r.ReadUInt32();
                        mode.Hz = r.ReadUInt32();
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Error("TryLoadState failed", ex);
                t = null; hdr = null; mode = null;
                return false;
            }
        }

        public static void ClearState(string file)
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch (Exception ex) { Log.Warn("could not delete state file: " + ex.Message); }
        }

        private static byte[] StructsToBytes(Array arr, Type t)
        {
            int sz = Marshal.SizeOf(t);
            byte[] buf = new byte[sz * arr.Length];
            IntPtr p = Marshal.AllocHGlobal(sz);
            try
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    Marshal.StructureToPtr(arr.GetValue(i), p, false);
                    Marshal.Copy(p, buf, i * sz, sz);
                }
            }
            finally { Marshal.FreeHGlobal(p); }
            return buf;
        }

        private static Array BytesToStructs(byte[] buf, Type t)
        {
            int sz = Marshal.SizeOf(t);
            int n = buf.Length / sz;
            Array arr = Array.CreateInstance(t, n);
            IntPtr p = Marshal.AllocHGlobal(sz);
            try
            {
                for (int i = 0; i < n; i++)
                {
                    Marshal.Copy(buf, i * sz, p, sz);
                    arr.SetValue(Marshal.PtrToStructure(p, t), i);
                }
            }
            finally { Marshal.FreeHGlobal(p); }
            return arr;
        }
    }
}
