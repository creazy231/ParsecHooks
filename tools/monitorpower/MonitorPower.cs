// MonitorPower -- turns a monitor's panel off over DDC/CI without touching the Windows
// display topology.
//
// WHY: deactivating a display path (what disableSecondaryMonitors does) is what triggers
// the phantom-monitor churn that kills Desktop Duplication every ~10s on this host --
// see tools/lagwatch/README.md. DDC/CI talks to the monitor's own firmware over the
// display cable instead, so the panel powers down without a SetDisplayConfig call.
// Measured: 35 s / 2101 frames / 0 invalidations with the panel off this way.
//
// !! READ THIS BEFORE USING !!
// Waking a panel only works while Windows still ENUMERATES the monitor, because the
// handle comes from GetPhysicalMonitorsFromHMONITOR. Turning a panel off with state 4
// made this host drop the monitor from the topology entirely, and in that window
// nothing software-side brought it back (SDC_TOPOLOGY_EXTEND|SDC_FORCE_MODE_ENUMERATION
// returned 87, an SC_MONITORPOWER broadcast did nothing) -- it took a power cycle.
// After that reboot the monitor was enumerated again while still reporting state 4, and
// an ordinary "on" woke it. So this is recoverable, but keep the monitor's power button
// within reach and do not test it over a remote session.
//
// Also note waking a panel is a monitor-arrival event, so Windows re-applies its
// persisted display database and reverts any dynamically-set mode. Set modes with
// CDS_UPDATEREGISTRY if they need to survive.
//
// VCP code 0xD6 (Power Mode): 1 = on, 2 = standby, 3 = suspend, 4 = off (low power),
// 5 = off (hard).
//
// C# 5 only -- built by the in-box csc.exe, same constraint as the rest of the repo.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

internal static class MonitorPower
{
    private const uint VCP_POWER_MODE = 0xD6;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szDescription;
    }

    private delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMon, ref MONITORINFOEX mi);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMon, ref uint count);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMon, uint count, [Out] PHYSICAL_MONITOR[] arr);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitors(uint count, [In] PHYSICAL_MONITOR[] arr);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetVCPFeature(IntPtr h, byte code, uint value);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte code, IntPtr type,
                                                               ref uint current, ref uint max);

    private class Mon
    {
        public string Gdi;
        public bool Primary;
        public PHYSICAL_MONITOR Phys;
    }

    private static List<Mon> Collect()
    {
        List<Mon> list = new List<Mon>();
        MonitorEnumProc cb = delegate(IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr data)
        {
            MONITORINFOEX mi = new MONITORINFOEX();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
            GetMonitorInfoW(hMon, ref mi);

            uint n = 0;
            if (GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, ref n) && n > 0)
            {
                PHYSICAL_MONITOR[] arr = new PHYSICAL_MONITOR[n];
                if (GetPhysicalMonitorsFromHMONITOR(hMon, n, arr))
                {
                    for (int i = 0; i < n; i++)
                    {
                        Mon m = new Mon();
                        m.Gdi = mi.szDevice;
                        m.Primary = (mi.dwFlags & 1) != 0;   // MONITORINFOF_PRIMARY
                        m.Phys = arr[i];
                        list.Add(m);
                    }
                }
            }
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
        return list;
    }

    private static void Usage()
    {
        Console.WriteLine("MonitorPower -- DDC/CI panel power, without changing display topology");
        Console.WriteLine("  MonitorPower list              show monitors and whether DDC/CI answers");
        Console.WriteLine("  MonitorPower off  [match]      power the panel down  (see the one-way warning!)");
        Console.WriteLine("  MonitorPower on   [match]      power the panel up");
        Console.WriteLine("  MonitorPower set  <n> [match]  raw VCP 0xD6 value (2 = standby, 4 = off)");
        Console.WriteLine();
        Console.WriteLine("  [match] is a substring of the GDI name (e.g. DISPLAY33) or the monitor");
        Console.WriteLine("  description. Note the description is often a generic string such as");
        Console.WriteLine("  'Generic PnP Monitor', so matching on the GDI name is usually what works.");
        Console.WriteLine("  With no match, every NON-PRIMARY monitor is targeted.");
    }

    private static int Main(string[] args)
    {
        string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        if (cmd == "--help" || cmd == "-h" || cmd == "/?") { Usage(); return 0; }

        uint explicitValue = 0;
        string match;
        if (cmd == "set")
        {
            if (args.Length < 2 || !uint.TryParse(args[1], out explicitValue)) { Usage(); return 1; }
            match = args.Length > 2 ? args[2] : null;
        }
        else match = args.Length > 1 ? args[1] : null;

        List<Mon> mons = Collect();
        if (mons.Count == 0) { Console.WriteLine("no physical monitors found"); return 1; }

        int hits = 0;
        foreach (Mon m in mons)
        {
            string label = m.Gdi.Replace(@"\\.\", "") + (m.Primary ? "*" : "") + "  " + m.Phys.szDescription;

            uint cur = 0, max = 0;
            bool canRead = GetVCPFeatureAndVCPFeatureReply(m.Phys.hPhysicalMonitor, (byte)VCP_POWER_MODE,
                                                           IntPtr.Zero, ref cur, ref max);

            if (cmd == "list")
            {
                Console.WriteLine(label);
                Console.WriteLine("      DDC/CI power (0xD6): " +
                    (canRead ? "supported, current=" + cur + " max=" + max : "NOT supported / no reply"));
                continue;
            }

            bool target = (match == null) ? !m.Primary
                                          : (label.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0);
            if (!target) continue;

            if (!canRead) { Console.WriteLine(label + " -> DDC/CI not available, skipping"); continue; }

            uint want = (cmd == "set") ? explicitValue : (cmd == "off" ? 4u : 1u);
            bool ok = SetVCPFeature(m.Phys.hPhysicalMonitor, (byte)VCP_POWER_MODE, want);
            Console.WriteLine(label + " -> VCP 0xD6 = " + want + (ok ? "  OK" : "  FAILED err " + Marshal.GetLastWin32Error()));
            if (ok) hits++;
        }

        PHYSICAL_MONITOR[] all = new PHYSICAL_MONITOR[mons.Count];
        for (int i = 0; i < mons.Count; i++) all[i] = mons[i].Phys;
        DestroyPhysicalMonitors((uint)all.Length, all);

        if (cmd != "list")
        {
            Console.WriteLine(hits + " monitor(s) switched");
            if (hits == 0) Console.WriteLine("(nothing matched -- try the GDI name, e.g. DISPLAY33, and see 'list')");
        }
        return 0;
    }
}
