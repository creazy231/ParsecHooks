// Native.cs -- Win32 CCD (Connecting and Configuring Displays) interop.
//
// IMPORTANT: target C# 5 only. This project is built by the in-box .NET Framework
// compiler (%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe) so that it needs
// no SDK install. That means: no string interpolation, no ?. operator, no
// expression-bodied members, no nameof, no auto-property initializers, no out-var.
using System;
using System.Runtime.InteropServices;

namespace ParsecHooks
{
    internal static class Native
    {
        // ---- QueryDisplayConfig flags ----
        public const uint QDC_ALL_PATHS = 0x1;
        public const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
        public const uint QDC_DATABASE_CURRENT = 0x4;
        public const uint QDC_VIRTUAL_MODE_AWARE = 0x10;

        // ---- SetDisplayConfig flags ----
        public const uint SDC_TOPOLOGY_INTERNAL = 0x1;
        public const uint SDC_TOPOLOGY_CLONE = 0x2;
        public const uint SDC_TOPOLOGY_EXTEND = 0x4;
        public const uint SDC_TOPOLOGY_EXTERNAL = 0x8;
        public const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20;
        public const uint SDC_VALIDATE = 0x40;
        public const uint SDC_APPLY = 0x80;
        public const uint SDC_NO_OPTIMIZATION = 0x100;
        public const uint SDC_SAVE_TO_DATABASE = 0x200;
        public const uint SDC_ALLOW_CHANGES = 0x400;
        public const uint SDC_VIRTUAL_MODE_AWARE = 0x8000;

        public const uint DISPLAYCONFIG_PATH_ACTIVE = 0x1;
        public const uint DISPLAYCONFIG_PATH_MODE_IDX_INVALID = 0xFFFFFFFF;

        public const uint MODE_INFO_TYPE_SOURCE = 1;
        public const uint MODE_INFO_TYPE_TARGET = 2;

        // ---- DISPLAYCONFIG_DEVICE_INFO_TYPE ----
        // NB: in wingdi.h constants 12..17 carry NO explicit values; they auto-increment
        // from GET_SDR_WHITE_LEVEL = 11. Getting these wrong is dangerous: 14 is
        // SET_RESERVED1, an undocumented reserved *setter*. Verified against
        // learn.microsoft.com and the xbmc 24H2 HDR implementation.
        public const uint DEVICE_INFO_GET_SOURCE_NAME = 1;
        public const uint DEVICE_INFO_GET_TARGET_NAME = 2;
        public const uint DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9;    // legacy, pre-24H2
        public const uint DEVICE_INFO_SET_ADVANCED_COLOR_STATE = 10;  // legacy, pre-24H2
        public const uint DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2 = 15; // 24H2+
        public const uint DEVICE_INFO_SET_HDR_STATE = 16;             // 24H2+

        // ---- advancedColorInfo2 bitfield masks ----
        public const uint ACI2_ADVANCED_COLOR_SUPPORTED = 0x01;
        public const uint ACI2_ADVANCED_COLOR_ACTIVE = 0x02;
        public const uint ACI2_LIMITED_BY_POLICY = 0x08;
        public const uint ACI2_HDR_SUPPORTED = 0x10;
        public const uint ACI2_HDR_USER_ENABLED = 0x20;
        public const uint ACI2_WCG_SUPPORTED = 0x40;
        public const uint ACI2_WCG_USER_ENABLED = 0x80;

        // ---- legacy advancedColorInfo bitfield masks ----
        public const uint ACI_SUPPORTED = 0x01;
        public const uint ACI_ENABLED = 0x02;

        [StructLayout(LayoutKind.Sequential)]
        public struct LUID
        {
            public uint LowPart;
            public int HighPart;
            public bool Equals(LUID o) { return LowPart == o.LowPart && HighPart == o.HighPart; }
            public override string ToString() { return HighPart + ":" + LowPart; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RATIONAL
        {
            public uint Numerator;
            public uint Denominator;
            public double Hz { get { return Denominator == 0 ? 0.0 : (double)Numerator / Denominator; } }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint outputTechnology;
            public uint rotation;
            public uint scaling;
            public RATIONAL refreshRate;
            public uint scanLineOrdering;
            public int targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PATH_INFO
        {
            public PATH_SOURCE_INFO sourceInfo;
            public PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINTL { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SOURCE_MODE
        {
            public uint width;
            public uint height;
            public uint pixelFormat;
            public POINTL position;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECTL { public int left; public int top; public int right; public int bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct VIDEO_SIGNAL_INFO
        {
            public ulong pixelRate;
            public RATIONAL hSyncFreq;
            public RATIONAL vSyncFreq;
            public uint activeWidth;
            public uint activeHeight;
            public uint totalWidth;
            public uint totalHeight;
            public uint videoStandard;
            public uint scanLineOrdering;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TARGET_MODE { public VIDEO_SIGNAL_INFO targetVideoSignalInfo; }

        [StructLayout(LayoutKind.Sequential)]
        public struct DESKTOP_IMAGE_INFO
        {
            public POINTL PathSourceSize;
            public RECTL DesktopImageRegion;
            public RECTL DesktopImageClip;
        }

        // Union; sized by its largest member (TARGET_MODE, 48 bytes).
        [StructLayout(LayoutKind.Explicit)]
        public struct MODE_UNION
        {
            [FieldOffset(0)] public TARGET_MODE targetMode;
            [FieldOffset(0)] public SOURCE_MODE sourceMode;
            [FieldOffset(0)] public DESKTOP_IMAGE_INFO desktopImageInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MODE_INFO
        {
            public uint infoType;
            public uint id;
            public LUID adapterId;
            public MODE_UNION mode;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DEVICE_INFO_HEADER
        {
            public uint type;
            public uint size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct TARGET_DEVICE_NAME
        {
            public DEVICE_INFO_HEADER header;
            public uint flags;
            public uint outputTechnology;
            public ushort edidManufactureId;
            public ushort edidProductCodeId;
            public uint connectorInstance;
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
        public struct ADV_COLOR_INFO
        {
            public DEVICE_INFO_HEADER header;
            public uint value;
            public uint colorEncoding;
            public uint bitsPerColorChannel;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ADV_COLOR_INFO_2
        {
            public DEVICE_INFO_HEADER header;
            public uint value;
            public uint colorEncoding;
            public uint bitsPerColorChannel;
            public uint activeColorMode; // 0=SDR 1=WCG 2=HDR
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SET_ADV_COLOR_STATE
        {
            public DEVICE_INFO_HEADER header;
            public uint value; // bit0 = enableAdvancedColor
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SET_HDR_STATE
        {
            public DEVICE_INFO_HEADER header;
            public uint value; // bit0 = enableHdr
        }

        [DllImport("user32.dll")]
        public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        public static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] PATH_INFO[] pathArray,
                                                    ref uint numModeInfoArrayElements, [Out] MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        public static extern int SetDisplayConfig(uint numPathArrayElements, [In] PATH_INFO[] pathArray,
                                                  uint numModeInfoArrayElements, [In] MODE_INFO[] modeInfoArray, uint flags);

        // Distinct managed names per struct. Overloading on `ref <struct>` alone is a
        // trap for any dynamic caller (PowerShell binds the wrong overload); keeping
        // them separate also documents which packet each entry point expects.
        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        public static extern int GetTargetName(ref TARGET_DEVICE_NAME packet);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        public static extern int GetSourceName(ref SOURCE_DEVICE_NAME packet);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        public static extern int GetAdvancedColorInfo(ref ADV_COLOR_INFO packet);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        public static extern int GetAdvancedColorInfo2(ref ADV_COLOR_INFO_2 packet);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo")]
        public static extern int SetAdvancedColorState(ref SET_ADV_COLOR_STATE packet);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo")]
        public static extern int SetHdrStateRaw(ref SET_HDR_STATE packet);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        // ---- GDI display-mode enumeration and change ----
        // Resolution is done through ChangeDisplaySettingsEx rather than by hand-building a
        // CCD target mode: MS explicitly says to pass a DEVMODE obtained from
        // EnumDisplaySettings, and constructing a valid DISPLAYCONFIG_VIDEO_SIGNAL_INFO
        // (pixel rate, sync frequencies, totals) for an arbitrary mode is error-prone.
        public const uint ENUM_CURRENT_SETTINGS = 0xFFFFFFFF;
        public const uint ENUM_REGISTRY_SETTINGS = 0xFFFFFFFE;

        public const uint DM_BITSPERPEL = 0x00040000;
        public const uint DM_PELSWIDTH = 0x00080000;
        public const uint DM_PELSHEIGHT = 0x00100000;
        public const uint DM_DISPLAYFREQUENCY = 0x00400000;

        // flags = 0 changes the mode dynamically WITHOUT writing it to the registry, which
        // is what we want: a crash then leaves nothing behind to un-stick.
        public const uint CDS_DYNAMIC = 0x00;
        public const uint CDS_UPDATEREGISTRY = 0x01;
        public const uint CDS_TEST = 0x02;

        public const int DISP_CHANGE_SUCCESSFUL = 0;
        public const int DISP_CHANGE_RESTART = 1;
        public const int DISP_CHANGE_FAILED = -1;
        public const int DISP_CHANGE_BADMODE = -2;
        public const int DISP_CHANGE_NOTUPDATED = -3;
        public const int DISP_CHANGE_BADFLAGS = -4;
        public const int DISP_CHANGE_BADPARAM = -5;
        public const int DISP_CHANGE_BADDUALVIEW = -6;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public ushort dmSpecVersion;
            public ushort dmDriverVersion;
            public ushort dmSize;
            public ushort dmDriverExtra;
            public uint dmFields;
            public int dmPositionX;      // dmPosition, unioned with printer-only fields
            public int dmPositionY;
            public uint dmDisplayOrientation;
            public uint dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel;
            public uint dmPelsWidth;
            public uint dmPelsHeight;
            public uint dmDisplayFlags;
            public uint dmDisplayFrequency;
            public uint dmICMMethod;
            public uint dmICMIntent;
            public uint dmMediaType;
            public uint dmDitherType;
            public uint dmReserved1;
            public uint dmReserved2;
            public uint dmPanningWidth;
            public uint dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool EnumDisplaySettingsEx(string deviceName, uint modeNum, ref DEVMODE devMode, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE devMode, IntPtr hwnd, uint flags, IntPtr lParam);

        /// <summary>NULL devMode with flags 0 is the documented way back to the registry
        /// default after a dynamic mode change.</summary>
        [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", CharSet = CharSet.Unicode)]
        public static extern int ChangeDisplaySettingsExDefault(string deviceName, IntPtr devMode, IntPtr hwnd, uint flags, IntPtr lParam);

        public static string DispChangeName(int code)
        {
            switch (code)
            {
                case DISP_CHANGE_SUCCESSFUL: return "OK";
                case DISP_CHANGE_RESTART: return "needs restart";
                case DISP_CHANGE_FAILED: return "driver rejected it";
                case DISP_CHANGE_BADMODE: return "mode not supported";
                case DISP_CHANGE_NOTUPDATED: return "could not write registry";
                case DISP_CHANGE_BADFLAGS: return "bad flags";
                case DISP_CHANGE_BADPARAM: return "bad parameter";
                case DISP_CHANGE_BADDUALVIEW: return "bad dualview";
                default: return "code " + code;
            }
        }

        public static string OutputTechName(uint v)
        {
            switch (v)
            {
                case 0xFFFFFFFF: return "Other";
                case 0: return "VGA";
                case 1: return "S-Video";
                case 2: return "Composite";
                case 3: return "Component";
                case 4: return "DVI";
                case 5: return "HDMI";
                case 6: return "LVDS";
                case 8: return "D-Jpn";
                case 9: return "SDI";
                case 10: return "DisplayPort";
                case 11: return "DP-embedded";
                case 12: return "UDI";
                case 13: return "UDI-embedded";
                case 14: return "SDTV";
                case 15: return "Miracast";
                case 16: return "IndirectWired";
                case 17: return "IndirectVirtual";
                case 18: return "DP-USB-tunnel";
                case 0x80000000: return "Internal";
                default: return "tech(" + v + ")";
            }
        }
    }
}
