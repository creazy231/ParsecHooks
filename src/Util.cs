// Util.cs -- config file, logging, path helpers. C# 5 only.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ParsecHooks
{
    internal static class Paths
    {
        private static string _dataDir;

        public static string DataDir
        {
            get
            {
                if (_dataDir == null)
                {
                    _dataDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "parsec-hooks");
                    try { Directory.CreateDirectory(_dataDir); }
                    catch { _dataDir = AppDomain.CurrentDomain.BaseDirectory; }
                }
                return _dataDir;
            }
        }

        public static string ExeDir { get { return AppDomain.CurrentDomain.BaseDirectory; } }
        public static string ConfigFile { get { return Path.Combine(ExeDir, "parsec-hooks.ini"); } }
        public static string LogFile { get { return Path.Combine(DataDir, "parsec-hooks.log"); } }
        // Written when we apply tweaks, deleted when we revert. Its presence at startup
        // means a previous run died while displays were modified.
        public static string StateFile { get { return Path.Combine(DataDir, "applied-state.bin"); } }
        // Desktop icon layout is the one thing a crash could lose permanently, so it is
        // written out separately rather than folded into the binary state file.
        public static string IconsFile { get { return Path.Combine(DataDir, "desktop-icons.txt"); } }
        // A layout the user declared good, kept until they replace it. Unlike StateFile this
        // survives a clean revert, so it is always there as the "put everything back" escape
        // hatch when a session leaves the displays in a bad state.
        public static string DefaultsFile { get { return Path.Combine(DataDir, "default-layout.bin"); } }
    }

    internal enum LogLevel { Debug = 0, Info = 1, Warn = 2, Error = 3 }

    internal static class Log
    {
        private const long MaxBytes = 1024 * 1024;
        private static readonly object Gate = new object();
        public static LogLevel Level = LogLevel.Info;

        public static void Debug(string m) { Write(LogLevel.Debug, m); }
        public static void Info(string m) { Write(LogLevel.Info, m); }
        public static void Warn(string m) { Write(LogLevel.Warn, m); }
        public static void Error(string m) { Write(LogLevel.Error, m); }

        public static void Error(string m, Exception ex)
        {
            Write(LogLevel.Error, m + " :: " + (ex == null ? "(null)" : ex.ToString()));
        }

        private static void Write(LogLevel lvl, string msg)
        {
            if (lvl < Level) return;
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                        + " [" + lvl.ToString().ToUpperInvariant().PadRight(5) + "] " + msg;
            lock (Gate)
            {
                try
                {
                    string p = Paths.LogFile;
                    FileInfo fi = new FileInfo(p);
                    if (fi.Exists && fi.Length > MaxBytes)
                    {
                        string bak = p + ".1";
                        try { if (File.Exists(bak)) File.Delete(bak); File.Move(p, bak); }
                        catch { /* keep appending if rotation fails */ }
                    }
                    File.AppendAllText(p, line + Environment.NewLine, Encoding.UTF8);
                }
                catch { /* logging must never take the app down */ }
            }
        }
    }

    /// <summary>Dead-simple key=value config. Chosen over JSON so it stays hand-editable and
    /// needs no serializer reference. The settings dialog rewrites the whole file from
    /// <see cref="ToIni"/>, which regenerates the explanatory comments rather than trying to
    /// preserve them in place.</summary>
    internal class Config
    {
        public string Keep = "primary";              // primary | <targetId> | name/devicepath substring
        public bool DisableSecondaryMonitors = true;
        // Blank the other panels over DDC/CI instead of deactivating their display paths.
        // Deactivating leaves phantom monitor registrations that Windows re-enumerates every
        // ~10s, and each one costs Parsec a full encoder rebuild -- see tools/lagwatch.
        public bool StandbySecondaryMonitors = false;
        // Parsec shrinks the primary to the client's resolution, which leaves icons outside
        // the visible area. Pack them in for the session and put them back afterwards.
        public bool MoveIconsToPrimary = false;
        public bool DisableHdr = true;
        public string HdrScope = "kept";             // kept | all
        public int ApplyDelayMs = 1200;
        public int RevertDelayMs = 2000;
        public int PollMs = 750;
        public int BaselineRefreshMs = 15000;
        public int GuardMs = 5000;                   // 0 disables the re-assert guard
        public int SettleMs = 400;
        public string LogPath = "";                  // blank => auto-detect
        public string LogLevel = "info";
        public bool Notifications = true;

        public Config Clone()
        {
            Config c = new Config();
            c.Keep = Keep;
            c.DisableSecondaryMonitors = DisableSecondaryMonitors;
            c.StandbySecondaryMonitors = StandbySecondaryMonitors;
            c.MoveIconsToPrimary = MoveIconsToPrimary;
            c.DisableHdr = DisableHdr;
            c.HdrScope = HdrScope;
            c.ApplyDelayMs = ApplyDelayMs;
            c.RevertDelayMs = RevertDelayMs;
            c.PollMs = PollMs;
            c.BaselineRefreshMs = BaselineRefreshMs;
            c.GuardMs = GuardMs;
            c.SettleMs = SettleMs;
            c.LogPath = LogPath;
            c.LogLevel = LogLevel;
            c.Notifications = Notifications;
            return c;
        }

        public static LogLevel ParseLevel(string s)
        {
            if (string.IsNullOrEmpty(s)) return ParsecHooks.LogLevel.Info;
            switch (s.Trim().ToLowerInvariant())
            {
                case "debug": return ParsecHooks.LogLevel.Debug;
                case "warn": return ParsecHooks.LogLevel.Warn;
                case "error": return ParsecHooks.LogLevel.Error;
                default: return ParsecHooks.LogLevel.Info;
            }
        }

        public static Config Load()
        {
            Config c = new Config();
            string file = Paths.ConfigFile;
            if (!File.Exists(file))
            {
                c.Save();
                return c;
            }

            Dictionary<string, string> kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                // ReadAllLines strips a UTF-8 BOM, so a file saved by Notepad parses fine.
                foreach (string raw in File.ReadAllLines(file))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';' || line[0] == '[') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();
                    int semi = v.IndexOf(" ;", StringComparison.Ordinal);
                    if (semi >= 0) v = v.Substring(0, semi).Trim();
                    kv[k] = v;
                }
            }
            catch (Exception ex) { Log.Error("failed reading config, using defaults", ex); return c; }

            c.Keep = Str(kv, "keep", c.Keep);
            c.DisableSecondaryMonitors = Bool(kv, "disableSecondaryMonitors", c.DisableSecondaryMonitors);
            c.StandbySecondaryMonitors = Bool(kv, "standbySecondaryMonitors", c.StandbySecondaryMonitors);
            c.MoveIconsToPrimary = Bool(kv, "moveIconsToPrimary", c.MoveIconsToPrimary);
            c.DisableHdr = Bool(kv, "disableHdr", c.DisableHdr);
            c.HdrScope = Str(kv, "hdrScope", c.HdrScope);
            c.ApplyDelayMs = Int(kv, "applyDelayMs", c.ApplyDelayMs, 0, 60000);
            c.RevertDelayMs = Int(kv, "revertDelayMs", c.RevertDelayMs, 0, 60000);
            c.PollMs = Int(kv, "pollMs", c.PollMs, 100, 10000);
            c.BaselineRefreshMs = Int(kv, "baselineRefreshMs", c.BaselineRefreshMs, 2000, 600000);
            c.GuardMs = Int(kv, "guardMs", c.GuardMs, 0, 600000);
            c.SettleMs = Int(kv, "settleMs", c.SettleMs, 0, 5000);
            c.LogPath = Str(kv, "logPath", c.LogPath);
            c.LogLevel = Str(kv, "logLevel", c.LogLevel);
            c.Notifications = Bool(kv, "notifications", c.Notifications);

            Log.Level = ParseLevel(c.LogLevel);
            return c;
        }

        public bool Save()
        {
            try
            {
                // No BOM: keeps the file byte-identical to what a plain editor would write.
                File.WriteAllText(Paths.ConfigFile, ToIni(), new UTF8Encoding(false));
                Log.Level = ParseLevel(LogLevel);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("could not write config to " + Paths.ConfigFile, ex);
                return false;
            }
        }

        private static string Str(Dictionary<string, string> kv, string k, string def)
        {
            string v;
            return kv.TryGetValue(k, out v) ? v : def;
        }

        private static bool Bool(Dictionary<string, string> kv, string k, bool def)
        {
            string v;
            if (!kv.TryGetValue(k, out v)) return def;
            v = v.Trim().ToLowerInvariant();
            if (v == "true" || v == "1" || v == "yes" || v == "on") return true;
            if (v == "false" || v == "0" || v == "no" || v == "off") return false;
            return def;
        }

        private static int Int(Dictionary<string, string> kv, string k, int def, int min, int max)
        {
            string v; int n;
            if (!kv.TryGetValue(k, out v)) return def;
            if (!int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return def;
            if (n < min) n = min;
            if (n > max) n = max;
            return n;
        }

        private static string B(bool b) { return b ? "true" : "false"; }
        private static string I(int n) { return n.ToString(CultureInfo.InvariantCulture); }

        public string ToIni()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# parsec-hooks configuration");
            sb.AppendLine("# Easiest way to change these: tray icon -> Settings...");
            sb.AppendLine("# If you edit by hand, use the tray menu -> Reload config afterwards.");
            sb.AppendLine();
            sb.AppendLine("# Which display stays ON while a Parsec client is connected.");
            sb.AppendLine("#   primary  = whichever display Windows currently treats as primary");
            sb.AppendLine("#   <number> = a CCD target id (see Settings -> Displays & HDR)");
            sb.AppendLine("#   <text>   = substring match on monitor name or device path");
            sb.AppendLine("keep = " + Keep);
            sb.AppendLine();
            sb.AppendLine("# Turn every other display off for the duration of the session.");
            sb.AppendLine("#");
            sb.AppendLine("# WARNING: this deactivates the display PATH, which leaves phantom monitor");
            sb.AppendLine("# registrations that Windows re-enumerates every ~10s. Each one invalidates");
            sb.AppendLine("# Desktop Duplication and costs Parsec a full encoder rebuild -- a ~500ms");
            sb.AppendLine("# freeze on the client, twice, every ten seconds. Prefer");
            sb.AppendLine("# standbySecondaryMonitors below. Measured in tools/lagwatch.");
            sb.AppendLine("disableSecondaryMonitors = " + B(DisableSecondaryMonitors));
            sb.AppendLine();
            sb.AppendLine("# Blank the other panels over DDC/CI instead. The monitors stay attached, so");
            sb.AppendLine("# no phantom registrations appear and the stream stays smooth, but the panels");
            sb.AppendLine("# are powered down -- saving pixels and watts. Woken again on disconnect.");
            sb.AppendLine("# Measured: 30s / 4946 frames / 0 invalidations with a panel in standby.");
            sb.AppendLine("# Needs a monitor that answers DDC/CI; ones that do not are left alone.");
            sb.AppendLine("standbySecondaryMonitors = " + B(StandbySecondaryMonitors));
            sb.AppendLine();
            sb.AppendLine("# Parsec shrinks the host's primary display to the client's resolution, which");
            sb.AppendLine("# leaves desktop icons stranded outside the visible area. Pack them onto the");
            sb.AppendLine("# visible primary for the session and put them back on disconnect.");
            sb.AppendLine("# Ignored while the desktop has auto-arrange switched on.");
            sb.AppendLine("moveIconsToPrimary = " + B(MoveIconsToPrimary));
            sb.AppendLine();
            sb.AppendLine("# Turn HDR off for the duration of the session (only where it was ON).");
            sb.AppendLine("disableHdr = " + B(DisableHdr));
            sb.AppendLine("#   kept = only the display(s) left enabled;  all = every active display");
            sb.AppendLine("hdrScope = " + HdrScope);
            sb.AppendLine();
            sb.AppendLine("# NOTE ON RESOLUTION: parsec-hooks does not set it. Parsec switches the host");
            sb.AppendLine("# to the connecting client's resolution itself, and re-enforces that choice");
            sb.AppendLine("# about every 10 seconds, so anything we set gets overridden and the screen");
            sb.AppendLine("# visibly flaps. All we do is put the mode back if our own HDR/topology");
            sb.AppendLine("# changes disturbed it, which keeps Parsec's choice rather than replacing it.");
            sb.AppendLine();
            sb.AppendLine("# Wait after 'connected.' before touching displays, so Parsec can finish");
            sb.AppendLine("# its own resolution match and capture init first.");
            sb.AppendLine("applyDelayMs = " + I(ApplyDelayMs));
            sb.AppendLine("# Wait after the last 'disconnected.' before reverting, so Parsec can undo");
            sb.AppendLine("# its own resolution change first. Also debounces a reconnecting client.");
            sb.AppendLine("revertDelayMs = " + I(RevertDelayMs));
            sb.AppendLine("# Pause between the HDR change and the topology change.");
            sb.AppendLine("settleMs = " + I(SettleMs));
            sb.AppendLine();
            sb.AppendLine("# How often to poll the Parsec log for new lines.");
            sb.AppendLine("pollMs = " + I(PollMs));
            sb.AppendLine("# How often to re-snapshot the good topology. Only ever happens while idle.");
            sb.AppendLine("baselineRefreshMs = " + I(BaselineRefreshMs));
            sb.AppendLine();
            sb.AppendLine("# While a session is active, re-check every N ms that the monitors we turned");
            sb.AppendLine("# off are still off and HDR is still off, and re-assert if not. Windows");
            sb.AppendLine("# re-applies its remembered display layout whenever HDR changes or you open");
            sb.AppendLine("# Display settings, which would otherwise silently switch the monitors back");
            sb.AppendLine("# on mid-session. Set to 0 to disable.");
            sb.AppendLine("guardMs = " + I(GuardMs));
            sb.AppendLine();
            sb.AppendLine("# Blank = auto-detect (%APPDATA%\\Parsec\\log.txt, then %ProgramData%\\Parsec\\log.txt).");
            sb.AppendLine("logPath = " + LogPath);
            sb.AppendLine();
            sb.AppendLine("# debug | info | warn | error");
            sb.AppendLine("logLevel = " + LogLevel);
            sb.AppendLine("# Tray balloon tips on apply/revert.");
            sb.AppendLine("notifications = " + B(Notifications));
            return sb.ToString();
        }
    }
}
