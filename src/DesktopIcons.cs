// DesktopIcons.cs -- moves desktop icons onto the visible primary monitor and back.
//
// WHY: Parsec shrinks the host's primary display to the client's resolution (3440x1440
// becomes 1280x800 for a Steam Deck). Icons laid out across the full desktop then sit
// outside the visible area, so from the client most of the desktop looks empty. Packing
// them into the current primary bounds makes them reachable, and the original layout is
// restored on disconnect.
//
// The desktop is an ordinary SysListView32 owned by Explorer, so positions are read and
// written with LVM_* messages. Because it lives in another process, the POINT and LVITEM
// buffers those messages read and write have to be allocated inside Explorer.
//
// C# 5 only.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ParsecHooks
{
    internal class IconRecord
    {
        public int Index;
        public string Name;
        public int X, Y;      // ListView coords; origin is the top-left of the virtual screen
    }

    internal static class DesktopIcons
    {
        private const int LVM_FIRST = 0x1000;
        private const int LVM_GETITEMCOUNT    = LVM_FIRST + 4;
        private const int LVM_SETITEMPOSITION = LVM_FIRST + 15;
        private const int LVM_GETITEMPOSITION = LVM_FIRST + 16;
        private const int LVM_GETITEMSPACING  = LVM_FIRST + 51;
        private const int LVM_GETITEMTEXTW    = LVM_FIRST + 115;
        private const int LVS_AUTOARRANGE = 0x0100;
        private const int GWL_STYLE = -16;

        private const uint PROCESS_VM_OPERATION = 0x0008, PROCESS_VM_READ = 0x0010,
                           PROCESS_VM_WRITE = 0x0020, PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_RELEASE = 0x8000;
        private const uint PAGE_READWRITE = 0x04;

        private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77;
        private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct LVITEM
        {
            public uint mask; public int iItem, iSubItem; public uint state, stateMask;
            public IntPtr pszText; public int cchTextMax, iImage; public IntPtr lParam;
            public int iIndent, iGroupId; public uint cColumns; public IntPtr puColumns;
            public IntPtr piColFmt; public int iGroup;
        }

        private delegate bool EnumWindowsProc(IntPtr h, IntPtr p);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowW(string cls, string win);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string win);
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessageW(IntPtr h, int msg, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll")] private static extern int GetWindowLongW(IntPtr h, int idx);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr p);

        [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint a, bool inherit, uint pid);
        [DllImport("kernel32.dll")] private static extern IntPtr VirtualAllocEx(IntPtr p, IntPtr addr, IntPtr size, uint type, uint prot);
        [DllImport("kernel32.dll")] private static extern bool VirtualFreeEx(IntPtr p, IntPtr addr, IntPtr size, uint type);
        [DllImport("kernel32.dll")] private static extern bool ReadProcessMemory(IntPtr p, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
        [DllImport("kernel32.dll")] private static extern bool WriteProcessMemory(IntPtr p, IntPtr addr, byte[] buf, IntPtr size, out IntPtr written);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

        /// <summary>The desktop ListView usually hangs off Progman, but Explorer re-parents it
        /// under a WorkerW window when a wallpaper slideshow is running. Looking only at
        /// Progman finds nothing in that state, so fall back to scanning top-level windows.</summary>
        public static IntPtr FindListView()
        {
            IntPtr lv = Descend(FindWindowW("Progman", null));
            if (lv != IntPtr.Zero) return lv;

            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr h, IntPtr p)
            {
                IntPtr cand = Descend(h);
                if (cand != IntPtr.Zero) { found = cand; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static IntPtr Descend(IntPtr parent)
        {
            if (parent == IntPtr.Zero) return IntPtr.Zero;
            IntPtr shell = FindWindowExW(parent, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (shell == IntPtr.Zero) return IntPtr.Zero;
            return FindWindowExW(shell, IntPtr.Zero, "SysListView32", null);
        }

        public static bool AutoArrangeOn(IntPtr lv)
        {
            return (GetWindowLongW(lv, GWL_STYLE) & LVS_AUTOARRANGE) != 0;
        }

        private static byte[] StructToBytes(LVITEM s)
        {
            int sz = Marshal.SizeOf(typeof(LVITEM));
            byte[] b = new byte[sz];
            IntPtr p = Marshal.AllocHGlobal(sz);
            try { Marshal.StructureToPtr(s, p, false); Marshal.Copy(p, b, 0, sz); }
            finally { Marshal.FreeHGlobal(p); }
            return b;
        }

        public static List<IconRecord> Read()
        {
            List<IconRecord> list = new List<IconRecord>();
            IntPtr lv = FindListView();
            if (lv == IntPtr.Zero) { Log.Warn("desktop icons: ListView not found"); return list; }

            uint pid; GetWindowThreadProcessId(lv, out pid);
            IntPtr proc = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_QUERY_INFORMATION,
                                      false, pid);
            if (proc == IntPtr.Zero) { Log.Warn("desktop icons: cannot open Explorer (err " + Marshal.GetLastWin32Error() + ")"); return list; }

            IntPtr rPoint = VirtualAllocEx(proc, IntPtr.Zero, (IntPtr)8, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            IntPtr rText  = VirtualAllocEx(proc, IntPtr.Zero, (IntPtr)520, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            IntPtr rItem  = VirtualAllocEx(proc, IntPtr.Zero, (IntPtr)Marshal.SizeOf(typeof(LVITEM)), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            try
            {
                int count = (int)SendMessageW(lv, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);
                for (int i = 0; i < count; i++)
                {
                    IconRecord ic = new IconRecord();
                    ic.Index = i;

                    SendMessageW(lv, LVM_GETITEMPOSITION, (IntPtr)i, rPoint);
                    byte[] pb = new byte[8]; IntPtr got;
                    ReadProcessMemory(proc, rPoint, pb, (IntPtr)8, out got);
                    ic.X = BitConverter.ToInt32(pb, 0);
                    ic.Y = BitConverter.ToInt32(pb, 4);

                    LVITEM li = new LVITEM();
                    li.iItem = i; li.pszText = rText; li.cchTextMax = 259;
                    byte[] ib = StructToBytes(li);
                    WriteProcessMemory(proc, rItem, ib, (IntPtr)ib.Length, out got);
                    int len = (int)SendMessageW(lv, LVM_GETITEMTEXTW, (IntPtr)i, rItem);
                    if (len > 0)
                    {
                        byte[] tb = new byte[520];
                        ReadProcessMemory(proc, rText, tb, (IntPtr)520, out got);
                        ic.Name = Encoding.Unicode.GetString(tb, 0, Math.Min(len * 2, 518));
                    }
                    list.Add(ic);
                }
            }
            catch (Exception ex) { Log.Error("desktop icons: read failed", ex); }
            finally
            {
                VirtualFreeEx(proc, rPoint, IntPtr.Zero, MEM_RELEASE);
                VirtualFreeEx(proc, rText, IntPtr.Zero, MEM_RELEASE);
                VirtualFreeEx(proc, rItem, IntPtr.Zero, MEM_RELEASE);
                CloseHandle(proc);
            }
            return list;
        }

        private static void Move(IntPtr lv, int index, int x, int y)
        {
            // LVM_SETITEMPOSITION packs both coordinates into a single LPARAM as 16-bit values,
            // so this cannot address negative coordinates -- fine here, since we only ever move
            // icons ONTO the primary monitor, which sits at non-negative ListView coordinates.
            IntPtr lp = (IntPtr)((y << 16) | (x & 0xFFFF));
            SendMessageW(lv, LVM_SETITEMPOSITION, (IntPtr)index, lp);
        }

        /// <summary>Moves every icon that is outside the primary monitor's current bounds into
        /// a free grid slot on it. Returns the positions from BEFORE the move, or null if
        /// nothing needed moving.</summary>
        public static List<IconRecord> PackOntoPrimary()
        {
            IntPtr lv = FindListView();
            if (lv == IntPtr.Zero) { Log.Warn("desktop icons: ListView not found; skipping"); return null; }
            if (AutoArrangeOn(lv))
            {
                Log.Info("desktop icons: auto-arrange is ON, so positions would snap back; skipping");
                return null;
            }

            List<IconRecord> before = Read();
            if (before.Count == 0) return null;

            // Primary is always at desktop (0,0); ListView coords are offset by the virtual origin.
            int primLeft = -GetSystemMetrics(SM_XVIRTUALSCREEN);
            int primTop = -GetSystemMetrics(SM_YVIRTUALSCREEN);
            int primW = GetSystemMetrics(SM_CXSCREEN);
            int primH = GetSystemMetrics(SM_CYSCREEN);

            int spacing = (int)SendMessageW(lv, LVM_GETITEMSPACING, IntPtr.Zero, IntPtr.Zero);
            int cx = spacing & 0xFFFF, cy = (spacing >> 16) & 0xFFFF;
            if (cx <= 0) cx = 75;
            if (cy <= 0) cy = 96;

            List<IconRecord> off = new List<IconRecord>();
            foreach (IconRecord ic in before)
            {
                bool on = ic.X >= primLeft && ic.X + cx <= primLeft + primW &&
                          ic.Y >= primTop && ic.Y + cy <= primTop + primH;
                if (!on) off.Add(ic);
            }
            if (off.Count == 0) { Log.Debug("desktop icons: all already on the primary monitor"); return null; }

            int cols = Math.Max(1, primW / cx), rows = Math.Max(1, primH / cy);
            bool[,] used = new bool[cols, rows];
            foreach (IconRecord ic in before)
            {
                if (off.Contains(ic)) continue;
                int c = (ic.X - primLeft) / cx, r = (ic.Y - primTop) / cy;
                if (c >= 0 && c < cols && r >= 0 && r < rows) used[c, r] = true;
            }

            int moved = 0, ci = 0, ri = 0;
            foreach (IconRecord ic in off)
            {
                while (ci < cols && used[ci, ri])
                    if (++ri >= rows) { ri = 0; ci++; }
                if (ci >= cols)
                {
                    Log.Warn("desktop icons: primary monitor is full; " + (off.Count - moved) + " icon(s) left where they were");
                    break;
                }
                used[ci, ri] = true;
                Move(lv, ic.Index, primLeft + ci * cx, primTop + ri * cy);
                moved++;
                if (++ri >= rows) { ri = 0; ci++; }
            }

            Log.Info("desktop icons: moved " + moved + " of " + before.Count + " onto the primary monitor");
            return before;
        }

        public static void Restore(List<IconRecord> saved)
        {
            if (saved == null || saved.Count == 0) return;
            IntPtr lv = FindListView();
            if (lv == IntPtr.Zero) { Log.Warn("desktop icons: ListView not found; cannot restore"); return; }
            if (AutoArrangeOn(lv)) { Log.Info("desktop icons: auto-arrange is ON; not restoring"); return; }

            foreach (IconRecord ic in saved) Move(lv, ic.Index, ic.X, ic.Y);
            Log.Info("desktop icons: restored " + saved.Count + " position(s)");
        }

        // ---- crash recovery ----
        // Icon layout is the one thing here a crash could lose for good, so it is written to
        // disk as well as kept in memory.

        public static void Save(string file, List<IconRecord> icons)
        {
            if (icons == null) return;
            try
            {
                using (StreamWriter w = new StreamWriter(file, false, Encoding.UTF8))
                    foreach (IconRecord ic in icons)
                        w.WriteLine(ic.Index + "\t" + ic.X + "\t" + ic.Y + "\t" + (ic.Name == null ? "" : ic.Name));
            }
            catch (Exception ex) { Log.Error("desktop icons: could not save layout", ex); }
        }

        public static List<IconRecord> Load(string file)
        {
            List<IconRecord> list = new List<IconRecord>();
            try
            {
                if (!File.Exists(file)) return null;
                foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
                {
                    string[] f = line.Split('\t');
                    if (f.Length < 3) continue;
                    IconRecord ic = new IconRecord();
                    ic.Index = int.Parse(f[0]);
                    ic.X = int.Parse(f[1]);
                    ic.Y = int.Parse(f[2]);
                    if (f.Length > 3) ic.Name = f[3];
                    list.Add(ic);
                }
                return list.Count > 0 ? list : null;
            }
            catch (Exception ex) { Log.Error("desktop icons: could not load layout", ex); return null; }
        }

        public static void ClearSaved(string file)
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { }
        }
    }
}
