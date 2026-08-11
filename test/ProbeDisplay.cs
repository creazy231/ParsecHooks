// ProbeDisplay.cs -- CCD API probe. C# 5 only (in-box .NET Framework csc.exe).
// Build: %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /optimize /target:exe /out:ProbeDisplay.exe ProbeDisplay.cs
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

internal static class Native
{
    public const uint QDC_ALL_PATHS = 0x1, QDC_ONLY_ACTIVE_PATHS = 0x2, QDC_VIRTUAL_MODE_AWARE = 0x10;
    public const uint PATH_ACTIVE = 0x1;
    public const uint MODE_IDX_INVALID = 0xFFFFFFFF;
    public const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20, SDC_VALIDATE = 0x40, SDC_APPLY = 0x80,
                      SDC_SAVE_TO_DATABASE = 0x200, SDC_ALLOW_CHANGES = 0x400, SDC_VIRTUAL_MODE_AWARE = 0x8000;

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID { public uint LowPart; public int HighPart;
        public override string ToString() { return HighPart + ":" + LowPart; } }

    [StructLayout(LayoutKind.Sequential)]
    public struct PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RATIONAL { public uint Numerator; public uint Denominator;
        public double Hz { get { return Denominator == 0 ? 0 : (double)Numerator / Denominator; } } }

    [StructLayout(LayoutKind.Sequential)]
    public struct PATH_TARGET_INFO
    {
        public LUID adapterId; public uint id; public uint modeInfoIdx;
        public uint outputTechnology; public uint rotation; public uint scaling;
        public RATIONAL refreshRate; public uint scanLineOrdering;
        public int targetAvailable; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PATH_INFO { public PATH_SOURCE_INFO sourceInfo; public PATH_TARGET_INFO targetInfo; public uint flags; }

    [StructLayout(LayoutKind.Sequential)] public struct POINTL { public int x; public int y; }
    [StructLayout(LayoutKind.Sequential)] public struct SOURCE_MODE { public uint width, height, pixelFormat; public POINTL position; }
    [StructLayout(LayoutKind.Sequential)] public struct RECTL { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct VIDEO_SIGNAL_INFO
    {
        public ulong pixelRate; public RATIONAL hSyncFreq; public RATIONAL vSyncFreq;
        public uint activeWidth, activeHeight, totalWidth, totalHeight, videoStandard, scanLineOrdering;
    }
    [StructLayout(LayoutKind.Sequential)] public struct TARGET_MODE { public VIDEO_SIGNAL_INFO targetVideoSignalInfo; }
    [StructLayout(LayoutKind.Sequential)] public struct DESKTOP_IMAGE_INFO { public POINTL PathSourceSize; public RECTL DesktopImageRegion; public RECTL DesktopImageClip; }

    [StructLayout(LayoutKind.Explicit)]
    public struct MODE_UNION
    {
        [FieldOffset(0)] public TARGET_MODE targetMode;
        [FieldOffset(0)] public SOURCE_MODE sourceMode;
        [FieldOffset(0)] public DESKTOP_IMAGE_INFO desktopImageInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct MODE_INFO { public uint infoType; public uint id; public LUID adapterId; public MODE_UNION mode; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVICE_INFO_HEADER { public uint type; public uint size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct TARGET_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER header; public uint flags; public uint outputTechnology;
        public ushort edidManufactureId; public ushort edidProductCodeId; public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SOURCE_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ADV_COLOR_INFO { public DEVICE_INFO_HEADER header; public uint value; public uint colorEncoding; public uint bitsPerColorChannel; }
    [StructLayout(LayoutKind.Sequential)]
    public struct ADV_COLOR_INFO_2 { public DEVICE_INFO_HEADER header; public uint value; public uint colorEncoding; public uint bitsPerColorChannel; public uint activeColorMode; }
    [StructLayout(LayoutKind.Sequential)]
    public struct SET_ADV_COLOR_STATE { public DEVICE_INFO_HEADER header; public uint value; }
    [StructLayout(LayoutKind.Sequential)]
    public struct SET_HDR_STATE { public DEVICE_INFO_HEADER header; public uint value; }

    [DllImport("user32.dll")] public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPath, out uint numMode);
    [DllImport("user32.dll")] public static extern int QueryDisplayConfig(uint flags, ref uint numPath, [Out] PATH_INFO[] paths, ref uint numMode, [Out] MODE_INFO[] modes, IntPtr topologyId);
    [DllImport("user32.dll")] public static extern int SetDisplayConfig(uint numPath, [In] PATH_INFO[] paths, uint numMode, [In] MODE_INFO[] modes, uint flags);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] public static extern int GetTargetName(ref TARGET_DEVICE_NAME p);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] public static extern int GetSourceName(ref SOURCE_DEVICE_NAME p);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] public static extern int GetAdvColorInfo(ref ADV_COLOR_INFO p);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] public static extern int GetAdvColorInfo2(ref ADV_COLOR_INFO_2 p);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo")] public static extern int SetAdvColorState(ref SET_ADV_COLOR_STATE p);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo")] public static extern int SetHdrState(ref SET_HDR_STATE p);

    // ---- GDI mode enumeration / change ----
    public const uint ENUM_CURRENT_SETTINGS = 0xFFFFFFFF;
    public const uint DM_BITSPERPEL = 0x00040000, DM_PELSWIDTH = 0x00080000,
                      DM_PELSHEIGHT = 0x00100000, DM_DISPLAYFREQUENCY = 0x00400000;
    public const uint CDS_TEST = 0x02, CDS_UPDATEREGISTRY = 0x01, CDS_RESET = 0x40000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
        public uint dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplaySettingsEx(string dev, uint modeNum, ref DEVMODE dm, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int ChangeDisplaySettingsEx(string dev, ref DEVMODE dm, IntPtr hwnd, uint flags, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", CharSet = CharSet.Unicode)]
    public static extern int ChangeDisplaySettingsExNull(string dev, IntPtr dm, IntPtr hwnd, uint flags, IntPtr lParam);
}

internal class Display
{
    public Native.LUID Adapter;
    public uint SourceId, TargetId;
    public string Name, GdiName, DevicePath;
    public uint Width, Height; public int PosX, PosY;
    public double Hz;
    public bool IsPrimary;
    public uint OutputTech;
}

internal static class Ccd
{
    public static void Query(uint flags, out Native.PATH_INFO[] paths, out Native.MODE_INFO[] modes)
    {
        uint np, nm;
        int e = Native.GetDisplayConfigBufferSizes(flags, out np, out nm);
        if (e != 0) throw new Exception("GetDisplayConfigBufferSizes=" + e);
        paths = new Native.PATH_INFO[np]; modes = new Native.MODE_INFO[nm];
        e = Native.QueryDisplayConfig(flags, ref np, paths, ref nm, modes, IntPtr.Zero);
        if (e != 0) throw new Exception("QueryDisplayConfig=" + e);
        Array.Resize(ref paths, (int)np); Array.Resize(ref modes, (int)nm);
    }

    public static string TargetName(Native.LUID a, uint id, out string devPath, out uint tech)
    {
        Native.TARGET_DEVICE_NAME t = new Native.TARGET_DEVICE_NAME();
        t.header.type = 2;
        t.header.size = (uint)Marshal.SizeOf(typeof(Native.TARGET_DEVICE_NAME));
        t.header.adapterId = a; t.header.id = id;
        int r = Native.GetTargetName(ref t);
        devPath = (r == 0) ? t.monitorDevicePath : null;
        tech = (r == 0) ? t.outputTechnology : 0;
        return (r == 0) ? t.monitorFriendlyDeviceName : ("<err " + r + ">");
    }

    public static string SourceGdi(Native.LUID a, uint id)
    {
        Native.SOURCE_DEVICE_NAME s = new Native.SOURCE_DEVICE_NAME();
        s.header.type = 1;
        s.header.size = (uint)Marshal.SizeOf(typeof(Native.SOURCE_DEVICE_NAME));
        s.header.adapterId = a; s.header.id = id;
        return Native.GetSourceName(ref s) == 0 ? s.viewGdiDeviceName : "?";
    }

    public static Display[] Enumerate(out Native.PATH_INFO[] paths, out Native.MODE_INFO[] modes)
    {
        Query(Native.QDC_ONLY_ACTIVE_PATHS, out paths, out modes);
        Display[] res = new Display[paths.Length];
        for (int i = 0; i < paths.Length; i++)
        {
            Native.PATH_INFO p = paths[i];
            Display d = new Display();
            d.Adapter = p.targetInfo.adapterId;
            d.SourceId = p.sourceInfo.id;
            d.TargetId = p.targetInfo.id;
            string dp; uint tech;
            d.Name = TargetName(p.targetInfo.adapterId, p.targetInfo.id, out dp, out tech);
            d.DevicePath = dp; d.OutputTech = tech;
            d.GdiName = SourceGdi(p.sourceInfo.adapterId, p.sourceInfo.id);
            d.Hz = p.targetInfo.refreshRate.Hz;
            // find the source mode for this path
            for (int m = 0; m < modes.Length; m++)
            {
                if (modes[m].infoType == 1 && modes[m].id == p.sourceInfo.id &&
                    modes[m].adapterId.LowPart == p.sourceInfo.adapterId.LowPart &&
                    modes[m].adapterId.HighPart == p.sourceInfo.adapterId.HighPart)
                {
                    d.Width = modes[m].mode.sourceMode.width;
                    d.Height = modes[m].mode.sourceMode.height;
                    d.PosX = modes[m].mode.sourceMode.position.x;
                    d.PosY = modes[m].mode.sourceMode.position.y;
                    break;
                }
            }
            d.IsPrimary = (d.PosX == 0 && d.PosY == 0);
            res[i] = d;
        }
        return res;
    }

    // ---- HDR ----
    public static bool TryGetHdr(Native.LUID a, uint id,
        out bool supported, out bool enabled, out uint activeMode, out uint bpc, out uint enc, out string via, out int err)
    {
        Native.ADV_COLOR_INFO_2 b = new Native.ADV_COLOR_INFO_2();
        b.header.type = 15;   // GET_ADVANCED_COLOR_INFO_2 (unnumbered in wingdi.h; auto-increments from 11)
        b.header.size = (uint)Marshal.SizeOf(typeof(Native.ADV_COLOR_INFO_2));
        b.header.adapterId = a; b.header.id = id;
        int r2 = Native.GetAdvColorInfo2(ref b);
        if (r2 == 0)
        {
            supported = (b.value & 0x10) != 0;          // highDynamicRangeSupported
            enabled = (b.value & 0x20) != 0;            // highDynamicRangeUserEnabled
            activeMode = b.activeColorMode; bpc = b.bitsPerColorChannel; enc = b.colorEncoding;
            via = "ADVANCED_COLOR_INFO_2(15) raw=0x" + b.value.ToString("X8"); err = 0;
            return true;
        }
        Native.ADV_COLOR_INFO c = new Native.ADV_COLOR_INFO();
        c.header.type = 9;
        c.header.size = (uint)Marshal.SizeOf(typeof(Native.ADV_COLOR_INFO));
        c.header.adapterId = a; c.header.id = id;
        int r1 = Native.GetAdvColorInfo(ref c);
        if (r1 == 0)
        {
            supported = (c.value & 0x1) != 0;
            enabled = (c.value & 0x2) != 0;
            activeMode = enabled ? 2u : 0u; bpc = c.bitsPerColorChannel; enc = c.colorEncoding;
            via = "ADVANCED_COLOR_INFO(9) raw=0x" + c.value.ToString("X8") + " [type15 err=" + r2 + "]"; err = 0;
            return true;
        }
        supported = false; enabled = false; activeMode = 0; bpc = 0; enc = 0;
        via = "both failed (15=" + r2 + ", 9=" + r1 + ")"; err = r1;
        return false;
    }

    public static string SetHdr(Native.LUID a, uint id, bool on)
    {
        Native.SET_HDR_STATE h = new Native.SET_HDR_STATE();
        h.header.type = 16;   // SET_HDR_STATE. NB: 14 is SET_RESERVED1 -- never call that.
        h.header.size = (uint)Marshal.SizeOf(typeof(Native.SET_HDR_STATE));
        h.header.adapterId = a; h.header.id = id;
        h.value = on ? 1u : 0u;
        int r14 = Native.SetHdrState(ref h);
        if (r14 == 0) return "SET_HDR_STATE(16) ok";

        Native.SET_ADV_COLOR_STATE s = new Native.SET_ADV_COLOR_STATE();
        s.header.type = 10;
        s.header.size = (uint)Marshal.SizeOf(typeof(Native.SET_ADV_COLOR_STATE));
        s.header.adapterId = a; s.header.id = id;
        s.value = on ? 1u : 0u;
        int r10 = Native.SetAdvColorState(ref s);
        if (r10 == 0) return "SET_ADVANCED_COLOR_STATE(10) ok [16 err=" + r14 + "]";
        return "FAILED (16=" + r14 + ", 10=" + r10 + ")";
    }

    // ---- raw struct-array (de)serialization for exact topology snapshots ----
    public static byte[] ToBytes(Array arr, Type t)
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

    public static Array FromBytes(byte[] buf, Type t)
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

internal static class Program
{
    static string Tech(uint v)
    {
        switch (v)
        {
            case 0xFFFFFFFF: return "Other";
            case 0: return "VGA";
            case 4: return "DVI";
            case 5: return "HDMI";
            case 6: return "LVDS";
            case 9: return "SDI";
            case 10: return "DisplayPort";
            case 11: return "DP-embedded";
            case 15: return "Miracast";
            case 16: return "IndirectWired";
            case 17: return "IndirectVirtual";
            case 18: return "DP-USB-tunnel";
            case 0x80000000: return "Internal";
            default: return "tech(" + v + ")";
        }
    }

    static void List()
    {
        Native.PATH_INFO[] paths; Native.MODE_INFO[] modes;
        Display[] ds = Ccd.Enumerate(out paths, out modes);
        Console.WriteLine("=== ACTIVE DISPLAYS (" + ds.Length + " paths, " + modes.Length + " modes) ===");
        for (int i = 0; i < ds.Length; i++)
        {
            Display d = ds[i];
            Console.WriteLine(string.Format("[{0}] {1,-9} {2,-22} {3,-13} src={4} tgt={5,-5} {6}x{7} @({8},{9}) {10:0.###}Hz {11}",
                i, d.IsPrimary ? "PRIMARY" : "secondary", d.Name, Tech(d.OutputTech),
                d.SourceId, d.TargetId, d.Width, d.Height, d.PosX, d.PosY, d.Hz, d.GdiName));
            Console.WriteLine("      devPath = " + d.DevicePath);

            bool sup, en; uint mode, bpc, enc; string via; int err;
            if (Ccd.TryGetHdr(d.Adapter, d.TargetId, out sup, out en, out mode, out bpc, out enc, out via, out err))
            {
                string mn = mode == 0 ? "SDR" : mode == 1 ? "WCG" : mode == 2 ? "HDR" : ("?" + mode);
                Console.WriteLine("      HDR: supported=" + sup + " enabled=" + en +
                                  " activeColorMode=" + mn + " bpc=" + bpc + " encoding=" + enc);
                Console.WriteLine("      via " + via);
            }
            else Console.WriteLine("      HDR: UNAVAILABLE -- " + via);
        }
    }

    static void HdrSet(string which, bool on)
    {
        Native.PATH_INFO[] paths; Native.MODE_INFO[] modes;
        Display[] ds = Ccd.Enumerate(out paths, out modes);
        foreach (Display d in ds)
        {
            bool match = which == "all" || which == d.TargetId.ToString() ||
                         (which == "primary" && d.IsPrimary);
            if (!match) continue;
            Console.WriteLine("SetHdr(" + d.Name + " tgt=" + d.TargetId + ", " + (on ? "ON" : "OFF") + ") -> " +
                              Ccd.SetHdr(d.Adapter, d.TargetId, on));
        }
    }

    // Disable every non-primary path, dwell, then restore the exact saved arrays.
    static void TopologyTest(int dwellSeconds)
    {
        Native.PATH_INFO[] paths; Native.MODE_INFO[] modes;
        Display[] ds = Ccd.Enumerate(out paths, out modes);

        string dir = AppDomain.CurrentDomain.BaseDirectory;
        File.WriteAllBytes(Path.Combine(dir, "snap.paths.bin"), Ccd.ToBytes(paths, typeof(Native.PATH_INFO)));
        File.WriteAllBytes(Path.Combine(dir, "snap.modes.bin"), Ccd.ToBytes(modes, typeof(Native.MODE_INFO)));
        Console.WriteLine("snapshot saved (" + paths.Length + " paths / " + modes.Length + " modes) to " + dir);

        Native.PATH_INFO[] mod = (Native.PATH_INFO[])paths.Clone();
        int n = 0;
        for (int i = 0; i < mod.Length; i++)
        {
            if (ds[i].IsPrimary) continue;
            mod[i].flags &= ~Native.PATH_ACTIVE;
            mod[i].sourceInfo.modeInfoIdx = Native.MODE_IDX_INVALID;
            mod[i].targetInfo.modeInfoIdx = Native.MODE_IDX_INVALID;
            n++;
            Console.WriteLine("  -> disabling " + ds[i].Name + " (tgt=" + ds[i].TargetId + ")");
        }
        if (n == 0) { Console.WriteLine("nothing to disable"); return; }

        uint applyFlags = Native.SDC_APPLY | Native.SDC_USE_SUPPLIED_DISPLAY_CONFIG | Native.SDC_ALLOW_CHANGES;
        int r = Native.SetDisplayConfig((uint)mod.Length, mod, (uint)modes.Length, modes, applyFlags);
        Console.WriteLine("APPLY disable -> " + (r == 0 ? "OK" : "err " + r));
        if (r != 0) return;

        Console.WriteLine("dwelling " + dwellSeconds + "s ...");
        Thread.Sleep(dwellSeconds * 1000);

        r = Native.SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, applyFlags);
        Console.WriteLine("RESTORE -> " + (r == 0 ? "OK" : "err " + r));

        Console.WriteLine();
        Console.WriteLine("--- post-restore state ---");
        List();
    }

    static void RestoreFromDisk()
    {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        Native.PATH_INFO[] paths = (Native.PATH_INFO[])Ccd.FromBytes(File.ReadAllBytes(Path.Combine(dir, "snap.paths.bin")), typeof(Native.PATH_INFO));
        Native.MODE_INFO[] modes = (Native.MODE_INFO[])Ccd.FromBytes(File.ReadAllBytes(Path.Combine(dir, "snap.modes.bin")), typeof(Native.MODE_INFO));
        uint f = Native.SDC_APPLY | Native.SDC_USE_SUPPLIED_DISPLAY_CONFIG | Native.SDC_ALLOW_CHANGES;
        int r = Native.SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, f);
        Console.WriteLine("restore-from-disk (" + paths.Length + " paths) -> " + (r == 0 ? "OK" : "err " + r));
    }

    static int ActiveCount()
    {
        uint np, nm;
        if (Native.GetDisplayConfigBufferSizes(Native.QDC_ONLY_ACTIVE_PATHS, out np, out nm) != 0) return -1;
        Native.PATH_INFO[] p = new Native.PATH_INFO[np];
        Native.MODE_INFO[] m = new Native.MODE_INFO[nm];
        if (Native.QueryDisplayConfig(Native.QDC_ONLY_ACTIVE_PATHS, ref np, p, ref nm, m, IntPtr.Zero) != 0) return -1;
        int n = 0;
        for (int i = 0; i < (int)np; i++) if ((p[i].flags & Native.PATH_ACTIVE) != 0) n++;
        return n;
    }

    static void Sample(string label, int times)
    {
        Console.Write("  " + label.PadRight(34));
        for (int i = 0; i < times; i++) { Console.Write(ActiveCount() + " "); Thread.Sleep(250); }
        Console.WriteLine();
    }

    // Does toggling HDR make Windows re-apply its persisted topology (undoing our
    // transient disable)? Disable -> sample -> HDR off -> sample.
    static void HdrInteraction(bool saveToDatabase)
    {
        Native.PATH_INFO[] paths; Native.MODE_INFO[] modes;
        Display[] ds = Ccd.Enumerate(out paths, out modes);
        Console.WriteLine("start: " + ActiveCount() + " active, saveToDatabase=" + saveToDatabase);

        Display prim = null;
        foreach (Display d in ds) if (d.IsPrimary) prim = d;
        if (prim == null) { Console.WriteLine("no primary"); return; }

        bool hdrWas; bool sup; uint mode, bpc, enc; string via; int e2;
        Ccd.TryGetHdr(prim.Adapter, prim.TargetId, out sup, out hdrWas, out mode, out bpc, out enc, out via, out e2);
        Console.WriteLine("primary HDR initially: " + hdrWas);

        Native.PATH_INFO[] mod = (Native.PATH_INFO[])paths.Clone();
        for (int i = 0; i < mod.Length; i++)
        {
            if (ds[i].IsPrimary) continue;
            mod[i].flags &= ~Native.PATH_ACTIVE;
            mod[i].sourceInfo.modeInfoIdx = Native.MODE_IDX_INVALID;
            mod[i].targetInfo.modeInfoIdx = Native.MODE_IDX_INVALID;
        }

        uint flags = Native.SDC_APPLY | Native.SDC_USE_SUPPLIED_DISPLAY_CONFIG | Native.SDC_ALLOW_CHANGES;
        if (saveToDatabase) flags |= Native.SDC_SAVE_TO_DATABASE;

        Native.PATH_INFO[] ap = (Native.PATH_INFO[])mod.Clone();
        Native.MODE_INFO[] am = (Native.MODE_INFO[])modes.Clone();
        int r = Native.SetDisplayConfig((uint)ap.Length, ap, (uint)am.Length, am, flags);
        Console.WriteLine("disable apply -> " + (r == 0 ? "OK" : "err " + r));
        Sample("after disable:", 6);

        if (hdrWas)
        {
            Console.WriteLine("now turning HDR OFF on primary...");
            Console.WriteLine("  -> " + Ccd.SetHdr(prim.Adapter, prim.TargetId, false));
            Sample("after HDR off:", 12);
        }
        else Console.WriteLine("(primary HDR already off; cannot test the interaction)");

        Console.WriteLine("restoring...");
        Native.PATH_INFO[] rp = (Native.PATH_INFO[])paths.Clone();
        Native.MODE_INFO[] rm = (Native.MODE_INFO[])modes.Clone();
        Native.SetDisplayConfig((uint)rp.Length, rp, (uint)rm.Length, rm,
            Native.SDC_APPLY | Native.SDC_USE_SUPPLIED_DISPLAY_CONFIG | Native.SDC_ALLOW_CHANGES);
        Thread.Sleep(600);
        if (hdrWas) Ccd.SetHdr(prim.Adapter, prim.TargetId, true);
        Thread.Sleep(600);
        Console.WriteLine("end: " + ActiveCount() + " active");
    }

    // Records the CURRENTLY active layout as Windows' remembered/persisted layout.
    // Needed to clean up after the "hdr-interaction save" experiment, which persisted a
    // reduced topology into the database.
    static void Persist()
    {
        Native.PATH_INFO[] paths; Native.MODE_INFO[] modes;
        Display[] ds = Ccd.Enumerate(out paths, out modes);
        Console.WriteLine("persisting the current layout (" + ds.Length + " display(s)) as the remembered default:");
        foreach (Display d in ds)
            Console.WriteLine("  " + (d.IsPrimary ? "PRIMARY   " : "secondary ") + d.Name +
                              "  " + d.Width + "x" + d.Height + " @(" + d.PosX + "," + d.PosY + ")");

        Native.PATH_INFO[] p = (Native.PATH_INFO[])paths.Clone();
        Native.MODE_INFO[] m = (Native.MODE_INFO[])modes.Clone();
        int r = Native.SetDisplayConfig((uint)p.Length, p, (uint)m.Length, m,
            Native.SDC_APPLY | Native.SDC_USE_SUPPLIED_DISPLAY_CONFIG | Native.SDC_ALLOW_CHANGES |
            Native.SDC_SAVE_TO_DATABASE);
        Console.WriteLine("SDC_SAVE_TO_DATABASE -> " + (r == 0 ? "OK" : "err " + r));
        Console.WriteLine("active now: " + ActiveCount());
    }

    static Native.DEVMODE NewDevMode()
    {
        Native.DEVMODE dm = new Native.DEVMODE();
        dm.dmDeviceName = "";
        dm.dmFormName = "";
        dm.dmSize = (ushort)Marshal.SizeOf(typeof(Native.DEVMODE));
        return dm;
    }

    static string CurMode(string gdi)
    {
        Native.DEVMODE dm = NewDevMode();
        if (!Native.EnumDisplaySettingsEx(gdi, Native.ENUM_CURRENT_SETTINGS, ref dm, 0)) return "?";
        return dm.dmPelsWidth + "x" + dm.dmPelsHeight + "@" + dm.dmDisplayFrequency + " " + dm.dmBitsPerPel + "bpp";
    }

    static void ListModes(string gdi)
    {
        Console.WriteLine("DEVMODE size = " + Marshal.SizeOf(typeof(Native.DEVMODE)) + " bytes (expect 220)");
        Console.WriteLine("current mode on " + gdi + " = " + CurMode(gdi));
        Console.WriteLine();

        // Group by resolution, collect refresh rates, and flag the aspect ratio so we can
        // see whether anything 16:10 (Steam Deck) is actually offered.
        var byRes = new System.Collections.Generic.SortedDictionary<string, System.Collections.Generic.List<uint>>();
        var order = new System.Collections.Generic.List<string>();
        for (uint i = 0; ; i++)
        {
            Native.DEVMODE dm = NewDevMode();
            if (!Native.EnumDisplaySettingsEx(gdi, i, ref dm, 0)) break;
            if (dm.dmBitsPerPel != 32) continue;
            string key = dm.dmPelsWidth + "x" + dm.dmPelsHeight;
            if (!byRes.ContainsKey(key)) { byRes[key] = new System.Collections.Generic.List<uint>(); order.Add(key); }
            if (!byRes[key].Contains(dm.dmDisplayFrequency)) byRes[key].Add(dm.dmDisplayFrequency);
        }

        Console.WriteLine("supported 32bpp modes (" + byRes.Count + " distinct resolutions):");
        foreach (string key in order)
        {
            string[] wh = key.Split('x');
            double w = double.Parse(wh[0]), h = double.Parse(wh[1]);
            double ar = w / h;
            string arName = Math.Abs(ar - 16.0 / 9) < 0.02 ? "16:9"
                          : Math.Abs(ar - 16.0 / 10) < 0.02 ? "16:10  <-- Steam Deck shape"
                          : Math.Abs(ar - 21.0 / 9) < 0.05 ? "21:9 (native)"
                          : Math.Abs(ar - 4.0 / 3) < 0.02 ? "4:3"
                          : ar.ToString("0.00");
            byRes[key].Sort();
            Console.WriteLine(string.Format("  {0,-12} {1,-24} Hz: {2}", key, arName, string.Join(",", byRes[key].ConvertAll(x => x.ToString()).ToArray())));
        }
    }

    // Does a resolution change also make Windows re-apply its persisted topology
    // (the way an HDR change does)? Disable secondary -> sample -> change mode -> sample.
    static void ResInteraction(uint w, uint h)
    {
        Native.PATH_INFO[] paths; Native.MODE_INFO[] modes;
        Display[] ds = Ccd.Enumerate(out paths, out modes);
        Display prim = null;
        foreach (Display d in ds) if (d.IsPrimary) prim = d;
        if (prim == null) { Console.WriteLine("no primary"); return; }
        string gdi = prim.GdiName;

        Console.WriteLine("start: " + ActiveCount() + " active, primary mode " + CurMode(gdi));

        Native.PATH_INFO[] mod = (Native.PATH_INFO[])paths.Clone();
        for (int i = 0; i < mod.Length; i++)
        {
            if (ds[i].IsPrimary) continue;
            mod[i].flags &= ~Native.PATH_ACTIVE;
            mod[i].sourceInfo.modeInfoIdx = Native.MODE_IDX_INVALID;
            mod[i].targetInfo.modeInfoIdx = Native.MODE_IDX_INVALID;
        }
        uint f = Native.SDC_APPLY | Native.SDC_USE_SUPPLIED_DISPLAY_CONFIG | Native.SDC_ALLOW_CHANGES;
        Native.PATH_INFO[] ap = (Native.PATH_INFO[])mod.Clone();
        Native.MODE_INFO[] am = (Native.MODE_INFO[])modes.Clone();
        Console.WriteLine("disable apply -> " + (Native.SetDisplayConfig((uint)ap.Length, ap, (uint)am.Length, am, f) == 0 ? "OK" : "err"));
        Sample("after disable:", 6);

        // Find an enumerated DEVMODE for the wanted size (MS: use an enumerated one, do not
        // hand-build). Prefer the highest refresh available at that size.
        Native.DEVMODE best = NewDevMode(); bool have = false;
        for (uint i = 0; ; i++)
        {
            Native.DEVMODE dm = NewDevMode();
            if (!Native.EnumDisplaySettingsEx(gdi, i, ref dm, 0)) break;
            if (dm.dmBitsPerPel != 32 || dm.dmPelsWidth != w || dm.dmPelsHeight != h) continue;
            if (!have || dm.dmDisplayFrequency > best.dmDisplayFrequency) { best = dm; have = true; }
        }
        if (!have) { Console.WriteLine("!! " + w + "x" + h + " is NOT an enumerated mode on " + gdi); }
        else
        {
            best.dmFields = Native.DM_PELSWIDTH | Native.DM_PELSHEIGHT | Native.DM_DISPLAYFREQUENCY | Native.DM_BITSPERPEL;
            int test = Native.ChangeDisplaySettingsEx(gdi, ref best, IntPtr.Zero, Native.CDS_TEST, IntPtr.Zero);
            Console.WriteLine("CDS_TEST for " + w + "x" + h + "@" + best.dmDisplayFrequency + " -> " + test);
            if (test == 0)
            {
                int r = Native.ChangeDisplaySettingsEx(gdi, ref best, IntPtr.Zero, 0, IntPtr.Zero);
                Console.WriteLine("apply mode (flags=0, not persisted) -> " + r);
                Thread.Sleep(700);
                Console.WriteLine("primary mode now = " + CurMode(gdi));
                Sample("after mode change:", 10);
            }
        }

        Console.WriteLine("restoring...");
        // NULL devmode + flags 0 is the documented way back to the registry default.
        Console.WriteLine("  reset mode -> " + Native.ChangeDisplaySettingsExNull(gdi, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero));
        Thread.Sleep(700);
        Native.PATH_INFO[] rp = (Native.PATH_INFO[])paths.Clone();
        Native.MODE_INFO[] rm = (Native.MODE_INFO[])modes.Clone();
        Native.SetDisplayConfig((uint)rp.Length, rp, (uint)rm.Length, rm, f);
        Thread.Sleep(900);
        Console.WriteLine("end: " + ActiveCount() + " active, primary mode " + CurMode(gdi));
    }

    static void SampleMode(string label, string gdi, int times)
    {
        Console.Write("  " + label.PadRight(30));
        for (int i = 0; i < times; i++) { Console.Write(CurMode(gdi).Replace(" 32bpp", "") + " "); Thread.Sleep(400); }
        Console.WriteLine();
    }

    // The decisive question: an HDR change makes Windows re-apply its PERSISTED layout --
    // does that also restore the persisted MODE, undoing a transient resolution change?
    static void ResHdrTest(uint w, uint h)
    {
        Native.PATH_INFO[] paths; Native.MODE_INFO[] modes;
        Display[] ds = Ccd.Enumerate(out paths, out modes);
        Display prim = null;
        foreach (Display d in ds) if (d.IsPrimary) prim = d;
        if (prim == null) { Console.WriteLine("no primary"); return; }
        string gdi = prim.GdiName;

        bool sup, hdrWas; uint mode, bpc, enc; string via; int e2;
        Ccd.TryGetHdr(prim.Adapter, prim.TargetId, out sup, out hdrWas, out mode, out bpc, out enc, out via, out e2);
        Console.WriteLine("start mode = " + CurMode(gdi) + ", primary HDR = " + hdrWas);
        if (!hdrWas) { Console.WriteLine("primary HDR is off; cannot exercise the trigger"); return; }

        Native.DEVMODE best = NewDevMode(); bool have = false;
        for (uint i = 0; ; i++)
        {
            Native.DEVMODE dm = NewDevMode();
            if (!Native.EnumDisplaySettingsEx(gdi, i, ref dm, 0)) break;
            if (dm.dmBitsPerPel != 32 || dm.dmPelsWidth != w || dm.dmPelsHeight != h) continue;
            if (!have || dm.dmDisplayFrequency > best.dmDisplayFrequency) { best = dm; have = true; }
        }
        if (!have) { Console.WriteLine("!! " + w + "x" + h + " not enumerated"); return; }

        best.dmFields = Native.DM_PELSWIDTH | Native.DM_PELSHEIGHT | Native.DM_DISPLAYFREQUENCY | Native.DM_BITSPERPEL;
        Console.WriteLine("set transient mode (flags=0) -> " +
            Native.ChangeDisplaySettingsEx(gdi, ref best, IntPtr.Zero, 0, IntPtr.Zero));
        Thread.Sleep(800);
        SampleMode("after mode change:", gdi, 4);

        Console.WriteLine("now toggling HDR OFF (the persisted-layout trigger)...");
        Console.WriteLine("  -> " + Ccd.SetHdr(prim.Adapter, prim.TargetId, false));
        SampleMode("after HDR off:", gdi, 10);

        Console.WriteLine("restoring...");
        Ccd.SetHdr(prim.Adapter, prim.TargetId, true);
        Thread.Sleep(800);
        Console.WriteLine("  reset mode -> " + Native.ChangeDisplaySettingsExNull(gdi, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero));
        Thread.Sleep(800);
        Console.WriteLine("end mode = " + CurMode(gdi) + ", active = " + ActiveCount());
    }

    // Stands in for Parsec setting the host mode to match a client. Pass persist=true to use
    // CDS_UPDATEREGISTRY: a transient change here gets undone by Windows re-applying its
    // persisted layout, so the simulation would never actually take effect.
    static void SetMode(string gdi, uint w, uint h, uint hz) { SetMode(gdi, w, h, hz, false); }
    static void SetMode(string gdi, uint w, uint h, uint hz, bool persist)
    {
        Native.DEVMODE best = NewDevMode(); bool have = false;
        for (uint i = 0; ; i++)
        {
            Native.DEVMODE dm = NewDevMode();
            if (!Native.EnumDisplaySettingsEx(gdi, i, ref dm, 0)) break;
            if (dm.dmBitsPerPel != 32 || dm.dmPelsWidth != w || dm.dmPelsHeight != h) continue;
            if (hz > 0) { if (dm.dmDisplayFrequency == hz) { best = dm; have = true; break; } }
            else if (!have || dm.dmDisplayFrequency > best.dmDisplayFrequency) { best = dm; have = true; }
        }
        if (!have) { Console.WriteLine("no enumerated mode " + w + "x" + h + (hz > 0 ? "@" + hz : "")); return; }
        best.dmFields = Native.DM_PELSWIDTH | Native.DM_PELSHEIGHT | Native.DM_DISPLAYFREQUENCY | Native.DM_BITSPERPEL;
        uint flags = persist ? Native.CDS_UPDATEREGISTRY : 0u;
        int r = Native.ChangeDisplaySettingsEx(gdi, ref best, IntPtr.Zero, flags, IntPtr.Zero);
        Thread.Sleep(700);
        Console.WriteLine("setmode" + (persist ? " (persisted)" : "") + " -> " + r +
                          "; current mode on " + gdi + " = " + CurMode(gdi));
    }

    /// <summary>The primary display's GDI name, resolved at runtime. Used as the default target
    /// so this tool works on any machine rather than assuming \\.\DISPLAY1 is the primary.</summary>
    static string PrimaryGdi()
    {
        Native.PATH_INFO[] p; Native.MODE_INFO[] m;
        Display[] ds = Ccd.Enumerate(out p, out m);
        foreach (Display d in ds)
        {
            if (d.IsPrimary && !string.IsNullOrEmpty(d.GdiName)) return d.GdiName;
        }
        foreach (Display d in ds)
        {
            if (!string.IsNullOrEmpty(d.GdiName)) return d.GdiName;
        }
        return null;
    }

    static int Main(string[] args)
    {
        try
        {
            string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
            if (cmd == "list") List();
            else if (cmd == "hdr" && args.Length >= 3) HdrSet(args[1].ToLowerInvariant(), args[2].ToLowerInvariant() == "on");
            else if (cmd == "topology-test") TopologyTest(args.Length > 1 ? int.Parse(args[1]) : 8);
            else if (cmd == "hdr-interaction") HdrInteraction(args.Length > 1 && args[1].ToLowerInvariant() == "save");
            else if (cmd == "persist") Persist();
            else if (cmd == "setmode")
                SetMode(PrimaryGdi(), uint.Parse(args[1]), uint.Parse(args[2]),
                        args.Length > 3 ? uint.Parse(args[3]) : 0,
                        args.Length > 4 && args[4].ToLowerInvariant() == "persist");
            else if (cmd == "modes") ListModes(args.Length > 1 ? args[1] : PrimaryGdi());
            else if (cmd == "res-hdr-test")
                ResHdrTest(args.Length > 2 ? uint.Parse(args[1]) : 1280, args.Length > 2 ? uint.Parse(args[2]) : 800);
            else if (cmd == "res-interaction")
                ResInteraction(args.Length > 2 ? uint.Parse(args[1]) : 1280, args.Length > 2 ? uint.Parse(args[2]) : 800);
            else if (cmd == "restore") RestoreFromDisk();
            else
            {
                Console.WriteLine("usage: ProbeDisplay [list | hdr <all|primary|targetId> <on|off> | topology-test [sec] | restore]");
                return 2;
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("ERROR: " + ex); return 1; }
    }
}
