// LagWatch.cs -- a Desktop Duplication canary for diagnosing periodic Parsec stalls.
//
// WHY THIS EXISTS
// ---------------
// A Parsec host session showed a hard stall roughly every 10 seconds: two stalls
// about a second apart, then several clean seconds, forever. Parsec's own log
// blamed it on the capture layer:
//
//     [I 20:15:14] FRAME: DXGI_ERROR_ACCESS_LOST
//     [I 20:15:15] FRAME: DXGI_ERROR_ACCESS_LOST
//     [D 20:15:16] [0] FPS:39.2/66, L:5.2/39.5, ...      <- 66 frames dropped
//
// DXGI_ERROR_ACCESS_LOST means the Desktop Duplication handle was invalidated, so
// Parsec has to tear down and rebuild capture + the NVENC encoder. That rebuild is
// the freeze the player feels. It is not a network problem, which is why changing
// the bitrate never helped.
//
// The hard part is attribution: Parsec logs the symptom at one-second resolution
// and cannot say who invalidated the handle. This tool holds its own duplication of
// the same output and reports, to the millisecond, exactly when it is invalidated
// and how long recovery takes. Because it is independent of Parsec it can be run
// with the game closed, which turns "reproduce it by playing for a while" into a
// 30-second A/B test: toggle one suspect, watch the cadence.
//
// C# 5 only -- built by the in-box csc.exe, same constraint as the rest of the repo.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LagWatch
{
    // ------------------------------------------------------------------
    // Minimal DXGI / D3D11 interop. Only the vtable slots we actually call
    // are declared, but every preceding slot must still be present and in
    // order or the calls land on the wrong function.
    // ------------------------------------------------------------------
    internal static class Dxgi
    {
        public const int DXGI_ERROR_ACCESS_LOST  = unchecked((int)0x887A0026);
        public const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
        public const int DXGI_ERROR_INVALID_CALL = unchecked((int)0x887A0001);
        public const int DXGI_ERROR_UNSUPPORTED  = unchecked((int)0x887A0004);
        public const int DXGI_ERROR_NOT_FOUND    = unchecked((int)0x887A0002);
        public const int E_ACCESSDENIED          = unchecked((int)0x80070005);
        public const int D3D11_SDK_VERSION       = 7;

        public static string Hr(int hr)
        {
            switch (hr)
            {
                case 0:                        return "S_OK";
                case DXGI_ERROR_ACCESS_LOST:   return "DXGI_ERROR_ACCESS_LOST";
                case DXGI_ERROR_WAIT_TIMEOUT:  return "DXGI_ERROR_WAIT_TIMEOUT";
                case DXGI_ERROR_INVALID_CALL:  return "DXGI_ERROR_INVALID_CALL";
                case DXGI_ERROR_UNSUPPORTED:   return "DXGI_ERROR_UNSUPPORTED";
                case DXGI_ERROR_NOT_FOUND:     return "DXGI_ERROR_NOT_FOUND";
                case E_ACCESSDENIED:           return "E_ACCESSDENIED";
                default:                       return "0x" + hr.ToString("X8", CultureInfo.InvariantCulture);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DXGI_OUTPUT_DESC
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public RECT DesktopCoordinates;
            [MarshalAs(UnmanagedType.Bool)] public bool AttachedToDesktop;
            public uint Rotation;
            public IntPtr Monitor;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct DXGI_OUTDUPL_POINTER_POSITION
        {
            public POINT Position;
            [MarshalAs(UnmanagedType.Bool)] public bool Visible;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DXGI_OUTDUPL_FRAME_INFO
        {
            public long LastPresentTime;
            public long LastMouseUpdateTime;
            public uint AccumulatedFrames;
            [MarshalAs(UnmanagedType.Bool)] public bool RectsCoalesced;
            [MarshalAs(UnmanagedType.Bool)] public bool ProtectedContentMaskedOut;
            public DXGI_OUTDUPL_POINTER_POSITION PointerPosition;
            public uint TotalMetadataBufferSize;
            public uint PointerShapeBufferSize;
        }

        [DllImport("d3d11.dll")]
        public static extern int D3D11CreateDevice(
            IntPtr pAdapter, uint DriverType, IntPtr Software, uint Flags,
            IntPtr pFeatureLevels, uint FeatureLevels, uint SDKVersion,
            out IntPtr ppDevice, out uint pFeatureLevel, out IntPtr ppImmediateContext);

        [ComImport, Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IDXGIDevice
        {
            // IDXGIObject
            void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData();
            [PreserveSig] int GetParent(ref Guid riid, out IntPtr ppParent);
            // IDXGIDevice
            [PreserveSig] int GetAdapter(out IntPtr pAdapter);
        }

        [ComImport, Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IDXGIAdapter
        {
            // IDXGIObject
            void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData();
            [PreserveSig] int GetParent(ref Guid riid, out IntPtr ppParent);
            // IDXGIAdapter
            [PreserveSig] int EnumOutputs(uint Output, out IntPtr ppOutput);
        }

        [ComImport, Guid("00cddea8-939b-4b83-a340-a685226666cc"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IDXGIOutput1
        {
            // IDXGIObject
            void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData();
            [PreserveSig] int GetParent(ref Guid riid, out IntPtr ppParent);
            // IDXGIOutput
            [PreserveSig] int GetDesc(out DXGI_OUTPUT_DESC pDesc);
            void GetDisplayModeList(); void FindClosestMatchingMode(); void WaitForVBlank();
            void TakeOwnership(); void ReleaseOwnership();
            void GetGammaControlCapabilities(); void SetGammaControl(); void GetGammaControl();
            void SetDisplaySurface(); void GetDisplaySurfaceData(); void GetFrameStatistics();
            // IDXGIOutput1
            void GetDisplayModeList1(); void FindClosestMatchingMode1(); void GetDisplaySurfaceData1();
            [PreserveSig] int DuplicateOutput(IntPtr pDevice, out IntPtr ppOutputDuplication);
        }

        [ComImport, Guid("191cfac3-a341-470d-b26e-a864f428319c"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IDXGIOutputDuplication
        {
            // IDXGIObject
            void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData();
            [PreserveSig] int GetParent(ref Guid riid, out IntPtr ppParent);
            // IDXGIOutputDuplication
            void GetDesc(IntPtr pDesc);
            [PreserveSig] int AcquireNextFrame(uint TimeoutInMilliseconds,
                                               out DXGI_OUTDUPL_FRAME_INFO pFrameInfo,
                                               out IntPtr ppDesktopResource);
            void GetFrameDirtyRects(); void GetFrameMoveRects(); void GetFramePointerShape();
            void MapDesktopSurface(); void UnMapDesktopSurface();
            [PreserveSig] int ReleaseFrame();
        }
    }

    internal static class User32
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
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
            public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency,
                        dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2,
                        dmPanningWidth, dmPanningHeight;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]  public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        public const uint ATTACHED_TO_DESKTOP = 0x1;
        public const uint PRIMARY_DEVICE = 0x4;
        public const int ENUM_CURRENT_SETTINGS = -1;

        [DllImport("user32.dll", CharSet = CharSet.Ansi)]
        public static extern bool EnumDisplayDevices(string dev, uint n, ref DISPLAY_DEVICE d, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Ansi)]
        public static extern bool EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        /// <summary>One line describing every attached display, so a mode change shows up as a
        /// changed string. Proving the mode is STABLE is what rules out the obvious explanation
        /// for ACCESS_LOST and forces the search onto other causes.</summary>
        public static string DisplaySnapshot()
        {
            StringBuilder sb = new StringBuilder();
            int n = 0;
            for (uint i = 0; ; i++)
            {
                DISPLAY_DEVICE dd = new DISPLAY_DEVICE();
                dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
                if ((dd.StateFlags & ATTACHED_TO_DESKTOP) == 0) continue;
                DEVMODE dm = new DEVMODE();
                dm.dmSize = (ushort)Marshal.SizeOf(typeof(DEVMODE));
                if (!EnumDisplaySettings(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref dm)) continue;
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(dd.DeviceName.Replace(@"\\.\", ""));
                if ((dd.StateFlags & PRIMARY_DEVICE) != 0) sb.Append("*");
                sb.Append(" " + dm.dmPelsWidth + "x" + dm.dmPelsHeight + "@" + dm.dmDisplayFrequency);
                n++;
            }
            return n + " active :: " + sb;
        }

        /// <summary>Every display adapter and its monitor, INCLUDING ones not attached to the
        /// desktop. DisplaySnapshot deliberately filters to attached displays, which makes it
        /// blind to a virtual monitor being plugged in and pulled out again -- and that is
        /// exactly the kind of event that invalidates a duplication without changing the
        /// desktop. This sees it.</summary>
        public static string DeviceSnapshot()
        {
            StringBuilder sb = new StringBuilder();
            for (uint i = 0; ; i++)
            {
                DISPLAY_DEVICE ad = new DISPLAY_DEVICE();
                ad.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                if (!EnumDisplayDevices(null, i, ref ad, 0)) break;

                DISPLAY_DEVICE mon = new DISPLAY_DEVICE();
                mon.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                bool hasMon = EnumDisplayDevices(ad.DeviceName, 0, ref mon, 0);

                // Windows exposes ~35 adapter slots, nearly all empty. Listing them buries the
                // one line that matters, so only report slots that actually have something.
                if (!hasMon && (ad.StateFlags & ATTACHED_TO_DESKTOP) == 0) continue;

                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(ad.DeviceName.Replace(@"\\.\", ""));
                sb.Append((ad.StateFlags & ATTACHED_TO_DESKTOP) != 0 ? "+" : "-");
                sb.Append(hasMon ? "[" + mon.DeviceString + "]" : "[no monitor]");
            }
            return sb.ToString();
        }

        public static string Foreground()
        {
            IntPtr h = GetForegroundWindow();
            StringBuilder sb = new StringBuilder(300);
            GetWindowTextW(h, sb, 300);
            uint pid; GetWindowThreadProcessId(h, out pid);
            string name = "?";
            try { name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
            catch { }
            string t = sb.ToString();
            return name + (t.Length > 0 ? " \"" + t + "\"" : "");
        }
    }

    /// <summary>Follows a growing log file from a byte offset, surviving Parsec's rotation and
    /// its habit of holding the file open for write.</summary>
    internal sealed class LogTail
    {
        private readonly string _path;
        private long _offset;
        public LogTail(string path)
        {
            _path = path;
            try { _offset = new FileInfo(path).Length; } catch { _offset = 0; }
        }
        public bool Available { get { return File.Exists(_path); } }
        public List<string> ReadNew()
        {
            List<string> lines = new List<string>();
            try
            {
                FileInfo fi = new FileInfo(_path);
                if (!fi.Exists) return lines;
                if (fi.Length < _offset) _offset = 0;          // rotated
                if (fi.Length == _offset) return lines;
                using (FileStream fs = new FileStream(_path, FileMode.Open, FileAccess.Read,
                                                      FileShare.ReadWrite | FileShare.Delete))
                {
                    fs.Seek(_offset, SeekOrigin.Begin);
                    using (StreamReader sr = new StreamReader(fs))
                    {
                        string l;
                        while ((l = sr.ReadLine()) != null) lines.Add(l);
                        _offset = fs.Position;
                    }
                }
            }
            catch { }
            return lines;
        }
    }

    internal static class Program
    {
        private static StreamWriter _csv;
        private static readonly DateTime Start = DateTime.Now;

        private static void Emit(string kind, string detail)
        {
            DateTime t = DateTime.Now;
            string line = t.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " +
                          kind.PadRight(16) + detail;
            Console.WriteLine(line);
            if (_csv != null)
            {
                _csv.WriteLine(t.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "," +
                               ((t - Start).TotalSeconds).ToString("F3", CultureInfo.InvariantCulture) + "," +
                               kind + ",\"" + detail.Replace("\"", "'") + "\"");
                _csv.Flush();
            }
        }

        private static int Main(string[] args)
        {
            int seconds = 120;
            string csvPath = null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if ((a == "--seconds" || a == "-s") && i + 1 < args.Length)
                    int.TryParse(args[++i], out seconds);
                else if ((a == "--csv" || a == "-c") && i + 1 < args.Length)
                    csvPath = args[++i];
                else if (a == "--help" || a == "-h" || a == "/?")
                {
                    Console.WriteLine("LagWatch -- Desktop Duplication canary");
                    Console.WriteLine("  --seconds N   how long to run (default 120)");
                    Console.WriteLine("  --csv PATH    also write a CSV of every event");
                    return 0;
                }
            }

            if (csvPath != null)
            {
                _csv = new StreamWriter(csvPath, false);
                _csv.WriteLine("timestamp,elapsed_s,kind,detail");
            }

            Console.WriteLine("=================================================================");
            Console.WriteLine(" LagWatch -- Desktop Duplication canary");
            Console.WriteLine(" Holds its own duplication of the primary output and reports every");
            Console.WriteLine(" invalidation (DXGI_ERROR_ACCESS_LOST) to the millisecond.");
            Console.WriteLine("=================================================================");
            Emit("START", "display: " + User32.DisplaySnapshot());
            Emit("START", "foreground: " + User32.Foreground());

            // Parsec's own log is tailed alongside, so its ACCESS_LOST lines land on the same
            // timeline as ours. Agreement means the fault is system-wide rather than a Parsec bug.
            string parsecLog = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Parsec\log.txt");
            LogTail parsec = new LogTail(parsecLog);
            Emit("START", "parsec log: " + (parsec.Available ? parsecLog : "(not found)"));

            Duplicator dup = new Duplicator();
            string err;
            if (!dup.Create(out err))
            {
                Emit("FATAL", "could not create duplication: " + err);
                Console.WriteLine();
                Console.WriteLine("If this says E_ACCESSDENIED, run LagWatch as the same user that");
                Console.WriteLine("owns the interactive session (and not elevated from a service).");
                if (_csv != null) _csv.Close();
                return 1;
            }
            Emit("READY", "duplicating " + dup.OutputName + "  " + dup.OutputSize);

            List<DateTime> losses = new List<DateTime>();
            DateTime end = DateTime.Now.AddSeconds(seconds);
            string lastDisplay = User32.DisplaySnapshot();
            string lastDevices = User32.DeviceSnapshot();
            string lastFg = User32.Foreground();
            Emit("START", "devices: " + lastDevices);
            DateTime nextPoll = DateTime.Now;
            long frames = 0;

            while (DateTime.Now < end)
            {
                Dxgi.DXGI_OUTDUPL_FRAME_INFO info;
                IntPtr res;
                int hr = dup.Acquire(50, out info, out res);

                if (hr == 0)
                {
                    frames++;
                    if (res != IntPtr.Zero) Marshal.Release(res);
                    dup.Release();
                }
                else if (hr == Dxgi.DXGI_ERROR_WAIT_TIMEOUT)
                {
                    // Normal: nothing on screen changed within the timeout.
                }
                else if (hr == Dxgi.DXGI_ERROR_ACCESS_LOST)
                {
                    DateTime t0 = DateTime.Now;
                    losses.Add(t0);
                    string gap = "";
                    if (losses.Count > 1)
                        gap = "  (+" + (t0 - losses[losses.Count - 2]).TotalSeconds
                                        .ToString("F3", CultureInfo.InvariantCulture) + "s since last)";
                    Emit("ACCESS_LOST", "#" + losses.Count + gap +
                                        "   display now: " + User32.DisplaySnapshot());

                    // Rebuilding is exactly what Parsec must do, so the time it takes here is a
                    // fair estimate of the stall the player feels.
                    dup.Dispose();
                    string e2;
                    int tries = 0;
                    while (!dup.Create(out e2) && DateTime.Now < end)
                    {
                        tries++;
                        System.Threading.Thread.Sleep(50);
                        if (tries > 100) break;
                    }
                    double ms = (DateTime.Now - t0).TotalMilliseconds;
                    Emit("RECOVERED", "rebuild took " + ms.ToString("F0", CultureInfo.InvariantCulture) +
                                      " ms" + (tries > 0 ? " after " + tries + " retr" + (tries == 1 ? "y" : "ies") : ""));
                }
                else
                {
                    Emit("ACQUIRE_ERR", Dxgi.Hr(hr));
                    System.Threading.Thread.Sleep(200);
                }

                if (DateTime.Now >= nextPoll)
                {
                    // 100 ms, not 250: a virtual monitor can come and go inside a second, and
                    // missing it is what sent the first pass of this investigation astray.
                    nextPoll = DateTime.Now.AddMilliseconds(100);

                    string dev = User32.DeviceSnapshot();
                    if (dev != lastDevices) { Emit("DEVICE_CHANGE", lastDevices + "   ->   " + dev); lastDevices = dev; }

                    string d = User32.DisplaySnapshot();
                    if (d != lastDisplay) { Emit("DISPLAY_CHANGE", lastDisplay + "   ->   " + d); lastDisplay = d; }
                    string f = User32.Foreground();
                    if (f != lastFg) { Emit("FOREGROUND", f); lastFg = f; }

                    foreach (string l in parsec.ReadNew())
                    {
                        if (l.IndexOf("ACCESS_LOST", StringComparison.OrdinalIgnoreCase) >= 0)
                            Emit("PARSEC", l.Trim());
                        else if (l.IndexOf("FPS:", StringComparison.Ordinal) >= 0 &&
                                 l.IndexOf("/0,", StringComparison.Ordinal) < 0)
                            Emit("PARSEC_DROP", l.Trim());   // a stats line reporting dropped frames
                    }
                }
            }

            dup.Dispose();
            Summarise(losses, frames, seconds);
            if (_csv != null) _csv.Close();
            return 0;
        }

        private static void Summarise(List<DateTime> losses, long frames, int seconds)
        {
            Console.WriteLine();
            Console.WriteLine("=================================================================");
            Console.WriteLine(" SUMMARY  (" + seconds + "s, " + frames + " frames acquired)");
            Console.WriteLine("=================================================================");
            Console.WriteLine(" ACCESS_LOST events : " + losses.Count);
            if (losses.Count < 2)
            {
                Console.WriteLine();
                Console.WriteLine(losses.Count == 0
                    ? " CLEAN. The capture handle was never invalidated during this run."
                    : " Only one event -- run longer to see whether it is periodic.");
                return;
            }

            List<double> gaps = new List<double>();
            for (int i = 1; i < losses.Count; i++) gaps.Add((losses[i] - losses[i - 1]).TotalSeconds);
            gaps.Sort();
            double sum = 0; foreach (double g in gaps) sum += g;

            Console.WriteLine(" gaps: min " + gaps[0].ToString("F2", CultureInfo.InvariantCulture) +
                              "s  median " + gaps[gaps.Count / 2].ToString("F2", CultureInfo.InvariantCulture) +
                              "s  max " + gaps[gaps.Count - 1].ToString("F2", CultureInfo.InvariantCulture) +
                              "s  mean " + (sum / gaps.Count).ToString("F2", CultureInfo.InvariantCulture) + "s");
            Console.WriteLine(" rate: " + (losses.Count * 60.0 / seconds).ToString("F1", CultureInfo.InvariantCulture) +
                              " events/min");
            Console.WriteLine();
            Console.WriteLine(" Interval histogram (rounded to 0.5s):");
            Dictionary<double, int> hist = new Dictionary<double, int>();
            foreach (double g in gaps)
            {
                double b = Math.Round(g * 2, MidpointRounding.AwayFromZero) / 2;
                if (!hist.ContainsKey(b)) hist[b] = 0;
                hist[b]++;
            }
            List<double> keys = new List<double>(hist.Keys);
            keys.Sort();
            foreach (double k in keys)
                Console.WriteLine("   " + k.ToString("F1", CultureInfo.InvariantCulture).PadLeft(6) + "s : " +
                                  hist[k].ToString().PadLeft(4) + "  " + new string('#', Math.Min(50, hist[k])));
        }
    }

    /// <summary>Owns one duplication of the primary output, and can be torn down and rebuilt --
    /// which is the whole point, since recovery is what costs the visible stall.</summary>
    internal sealed class Duplicator
    {
        private IntPtr _device, _context, _output, _dupl;
        private Dxgi.IDXGIOutputDuplication _d;
        public string OutputName = "?";
        public string OutputSize = "?";

        public bool Create(out string err)
        {
            err = null;
            try
            {
                uint fl;
                // D3D_DRIVER_TYPE_UNKNOWN(0) requires an adapter; HARDWARE(1) with a null adapter
                // picks the default, which is the adapter driving the desktop -- what we want.
                int hr = Dxgi.D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0, IntPtr.Zero, 0,
                                                Dxgi.D3D11_SDK_VERSION, out _device, out fl, out _context);
                if (hr != 0) { err = "D3D11CreateDevice -> " + Dxgi.Hr(hr); return false; }

                Dxgi.IDXGIDevice dev = (Dxgi.IDXGIDevice)Marshal.GetObjectForIUnknown(_device);
                IntPtr adapterPtr;
                hr = dev.GetAdapter(out adapterPtr);
                if (hr != 0) { err = "GetAdapter -> " + Dxgi.Hr(hr); return false; }

                Dxgi.IDXGIAdapter adapter = (Dxgi.IDXGIAdapter)Marshal.GetObjectForIUnknown(adapterPtr);
                hr = adapter.EnumOutputs(0, out _output);
                if (hr != 0) { err = "EnumOutputs(0) -> " + Dxgi.Hr(hr); return false; }

                Dxgi.IDXGIOutput1 o1 = (Dxgi.IDXGIOutput1)Marshal.GetObjectForIUnknown(_output);
                Dxgi.DXGI_OUTPUT_DESC desc;
                if (o1.GetDesc(out desc) == 0)
                {
                    OutputName = desc.DeviceName;
                    OutputSize = (desc.DesktopCoordinates.right - desc.DesktopCoordinates.left) + "x" +
                                 (desc.DesktopCoordinates.bottom - desc.DesktopCoordinates.top);
                }

                hr = o1.DuplicateOutput(_device, out _dupl);
                if (hr != 0) { err = "DuplicateOutput -> " + Dxgi.Hr(hr); return false; }

                _d = (Dxgi.IDXGIOutputDuplication)Marshal.GetObjectForIUnknown(_dupl);
                Marshal.Release(adapterPtr);
                return true;
            }
            catch (Exception ex) { err = ex.Message; return false; }
        }

        public int Acquire(uint timeoutMs, out Dxgi.DXGI_OUTDUPL_FRAME_INFO info, out IntPtr res)
        {
            info = new Dxgi.DXGI_OUTDUPL_FRAME_INFO();
            res = IntPtr.Zero;
            if (_d == null) return Dxgi.DXGI_ERROR_ACCESS_LOST;
            try { return _d.AcquireNextFrame(timeoutMs, out info, out res); }
            catch (COMException ce) { return ce.ErrorCode; }
        }

        public void Release()
        {
            if (_d == null) return;
            try { _d.ReleaseFrame(); } catch { }
        }

        public void Dispose()
        {
            _d = null;
            SafeRelease(ref _dupl);
            SafeRelease(ref _output);
            SafeRelease(ref _context);
            SafeRelease(ref _device);
        }

        private static void SafeRelease(ref IntPtr p)
        {
            if (p == IntPtr.Zero) return;
            try { Marshal.Release(p); } catch { }
            p = IntPtr.Zero;
        }
    }
}
