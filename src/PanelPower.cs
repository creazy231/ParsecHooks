// PanelPower.cs -- turns a monitor's panel off over DDC/CI, without touching topology.
//
// WHY NOT JUST DISABLE THE DISPLAY: deactivating a display path leaves the monitor
// powered and connected, and Windows then keeps phantom registrations of it that it
// re-enumerates every ~10s. Every one of those invalidates Desktop Duplication, so
// Parsec rebuilds its NVENC pipeline and the client sees a ~500ms freeze. Measured in
// tools/lagwatch: 5-12 invalidations per 25s that way.
//
// DDC/CI talks to the monitor's own firmware over the display cable instead. No
// SetDisplayConfig call happens, the topology never changes, no phantoms appear.
// Measured: 30s / 4946 frames / 0 invalidations with the panel in standby.
//
// USE STANDBY (2), NOT OFF (4). Both blank the panel, but state 4 made this host drop
// the monitor from the topology entirely, and while it is gone there is no handle to
// wake it through -- that took a power cycle to undo. State 2 keeps the monitor
// enumerated and awake-able, which is what makes an automatic revert safe.
//
// C# 5 only.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ParsecHooks
{
    /// <summary>A panel we put to sleep, and the state it was in beforehand.</summary>
    internal class PanelRecord
    {
        public string Gdi;
        public uint PreviousValue;
    }

    internal static class PanelPower
    {
        private const byte VCP_POWER_MODE = 0xD6;
        public const uint PowerOn = 1;
        public const uint PowerStandby = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szDescription;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        private delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr data);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfoW(IntPtr hMon, ref MONITORINFOEX mi);
        [DllImport("dxva2.dll")]
        private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMon, ref uint count);
        [DllImport("dxva2.dll")]
        private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMon, uint count, [Out] PHYSICAL_MONITOR[] arr);
        [DllImport("dxva2.dll")]
        private static extern bool DestroyPhysicalMonitors(uint count, [In] PHYSICAL_MONITOR[] arr);
        [DllImport("dxva2.dll")]
        private static extern bool SetVCPFeature(IntPtr h, byte code, uint value);
        [DllImport("dxva2.dll")]
        private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte code, IntPtr type,
                                                                   ref uint current, ref uint max);

        private class Handle
        {
            public string Gdi;
            public IntPtr H;
        }

        /// <summary>Physical monitor handles keyed by GDI device name (\\.\DISPLAY1), which is
        /// what DisplayInfo carries, so the two can be matched up.</summary>
        private static List<Handle> Open()
        {
            List<Handle> list = new List<Handle>();
            MonitorEnumProc cb = delegate(IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr data)
            {
                MONITORINFOEX mi = new MONITORINFOEX();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
                if (!GetMonitorInfoW(hMon, ref mi)) return true;

                uint n = 0;
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, ref n) || n == 0) return true;
                PHYSICAL_MONITOR[] arr = new PHYSICAL_MONITOR[n];
                if (!GetPhysicalMonitorsFromHMONITOR(hMon, n, arr)) return true;

                for (int i = 0; i < n; i++)
                {
                    Handle h = new Handle();
                    h.Gdi = mi.szDevice;
                    h.H = arr[i].hPhysicalMonitor;
                    list.Add(h);
                }
                return true;
            };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
            return list;
        }

        private static void Close(List<Handle> handles)
        {
            if (handles.Count == 0) return;
            PHYSICAL_MONITOR[] arr = new PHYSICAL_MONITOR[handles.Count];
            for (int i = 0; i < handles.Count; i++) arr[i].hPhysicalMonitor = handles[i].H;
            try { DestroyPhysicalMonitors((uint)arr.Length, arr); } catch { }
        }

        private static bool TryRead(IntPtr h, out uint current)
        {
            current = 0; uint max = 0;
            try { return GetVCPFeatureAndVCPFeatureReply(h, VCP_POWER_MODE, IntPtr.Zero, ref current, ref max); }
            catch { return false; }
        }

        /// <summary>Puts every attached panel whose GDI name is NOT in <paramref name="keepGdi"/>
        /// into standby. Returns what was changed so it can be undone.</summary>
        public static List<PanelRecord> StandbyAllExcept(List<string> keepGdi)
        {
            List<PanelRecord> done = new List<PanelRecord>();
            List<Handle> handles = Open();
            try
            {
                foreach (Handle h in handles)
                {
                    bool keep = false;
                    foreach (string k in keepGdi)
                        if (string.Equals(k, h.Gdi, StringComparison.OrdinalIgnoreCase)) { keep = true; break; }
                    if (keep) continue;

                    uint cur;
                    if (!TryRead(h.H, out cur))
                    {
                        Log.Info("panel standby: " + h.Gdi + " does not answer DDC/CI; leaving it on");
                        continue;
                    }
                    if (cur == PowerStandby) { Log.Debug("panel standby: " + h.Gdi + " already in standby"); continue; }

                    if (SetVCPFeature(h.H, VCP_POWER_MODE, PowerStandby))
                    {
                        PanelRecord r = new PanelRecord();
                        r.Gdi = h.Gdi;
                        r.PreviousValue = cur;
                        done.Add(r);
                        Log.Info("panel standby: " + h.Gdi + " -> standby (was " + cur + ")");
                    }
                    else Log.Warn("panel standby: DDC/CI write failed on " + h.Gdi);
                }
            }
            finally { Close(handles); }
            return done;
        }

        /// <summary>Wakes every attached panel that reports itself as not-on, regardless of
        /// whether we were the one that blanked it. This is the recovery path: after a crash
        /// there is no record of what was put to sleep, so the only safe move is to wake
        /// everything DDC/CI will talk to.</summary>
        public static int WakeAll()
        {
            int woken = 0;
            List<Handle> handles = Open();
            try
            {
                foreach (Handle h in handles)
                {
                    uint cur;
                    if (!TryRead(h.H, out cur)) continue;
                    if (cur == PowerOn) continue;
                    if (SetVCPFeature(h.H, VCP_POWER_MODE, PowerOn))
                    {
                        woken++;
                        Log.Info("panel wake: " + h.Gdi + " was in state " + cur + " -> on");
                    }
                }
            }
            finally { Close(handles); }
            return woken;
        }

        /// <summary>Wakes panels recorded by <see cref="StandbyAllExcept"/>. Safe to call with a
        /// stale list -- monitors that are gone are simply skipped.</summary>
        public static void Wake(List<PanelRecord> records)
        {
            if (records == null || records.Count == 0) return;
            List<Handle> handles = Open();
            try
            {
                foreach (PanelRecord r in records)
                {
                    bool found = false;
                    foreach (Handle h in handles)
                    {
                        if (!string.Equals(h.Gdi, r.Gdi, StringComparison.OrdinalIgnoreCase)) continue;
                        found = true;
                        uint want = (r.PreviousValue == 0) ? PowerOn : r.PreviousValue;
                        if (want != PowerOn) want = PowerOn;   // never restore INTO a sleep state
                        if (SetVCPFeature(h.H, VCP_POWER_MODE, want))
                            Log.Info("panel wake: " + h.Gdi + " -> on");
                        else
                            Log.Warn("panel wake: DDC/CI write failed on " + h.Gdi);
                        break;
                    }
                    if (!found)
                        Log.Warn("panel wake: " + r.Gdi + " is no longer enumerated; it may need its power button");
                }
            }
            finally { Close(handles); }
        }
    }
}
