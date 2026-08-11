// ParsecWatcher.cs -- tails the Parsec host log and tracks connected clients.
// C# 5 only.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ParsecHooks
{
    internal class ParsecWatcher
    {
        // Deliberately case-SENSITIVE and anchored to the [I] info level, and the user
        // token must look like "name#digits". Verified against 5151 lines of real host
        // log: 5 connects / 5 disconnects, zero false positives.
        //
        // What this rejects that a naive EndsWith(" connected.") would risk:
        //   "[D ...] IPC AS Client Connected."                    (84 occurrences)
        //   "[D ...] UPNP: ... reported as not connected"
        //   "[I ...] someone#1234567 disconnected."             (no space before "connected.")
        private static readonly Regex ReConnect = new Regex(
            @"^\[I \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\] (?<user>.+?#\d+) connected\.\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex ReDisconnect = new Regex(
            @"^\[I \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\] (?<user>.+?#\d+) disconnected\.\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex ReParsecStarted = new Regex(
            @"^\[F \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\] ===== Parsec: Started =====",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private const long ReconcileTailBytes = 512 * 1024;

        private readonly HashSet<string> _connected = new HashSet<string>(StringComparer.Ordinal);
        private readonly StringBuilder _partial = new StringBuilder();
        private Decoder _decoder = Encoding.UTF8.GetDecoder();
        private string _configuredPath;
        private string _path;
        private long _pos;
        private int _pollsSinceProcCheck;
        private int _pollsSinceResolve;
        private bool _warnedMissing;

        public event Action<string, int> ClientConnected;
        public event Action<string, int> ClientDisconnected;
        public event Action ParsecRestarted;

        public string LogPath { get { return _path; } }
        public int ConnectedCount { get { return _connected.Count; } }

        public string[] ConnectedUsers
        {
            get
            {
                string[] a = new string[_connected.Count];
                _connected.CopyTo(a);
                Array.Sort(a, StringComparer.Ordinal);
                return a;
            }
        }

        public ParsecWatcher(string configuredPath)
        {
            _configuredPath = configuredPath;
        }

        public void SetConfiguredPath(string p)
        {
            if (string.Equals(p, _configuredPath, StringComparison.OrdinalIgnoreCase)) return;
            _configuredPath = p;
            _path = null; // force re-resolve on next poll
        }

        /// <summary>Candidate log locations. Note that a per-MACHINE Parsec install still
        /// writes its host log into the logged-in user's %APPDATA% (verified on this box:
        /// install lives in Program Files, log in %APPDATA%, and %ProgramData%\Parsec does
        /// not exist at all), so %APPDATA% is tried first.</summary>
        public static IEnumerable<string> CandidatePaths(string configured)
        {
            if (!string.IsNullOrEmpty(configured)) yield return Environment.ExpandEnvironmentVariables(configured.Trim());
            string appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appdata)) yield return Path.Combine(appdata, "Parsec", "log.txt");
            string progdata = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!string.IsNullOrEmpty(progdata)) yield return Path.Combine(progdata, "Parsec", "log.txt");
        }

        private string Resolve()
        {
            foreach (string p in CandidatePaths(_configuredPath))
            {
                try { if (File.Exists(p)) return p; }
                catch { /* ignore unreadable candidates */ }
            }
            return null;
        }

        public static bool IsParsecRunning()
        {
            try
            {
                Process[] ps = Process.GetProcessesByName("parsecd");
                try { return ps.Length > 0; }
                finally { foreach (Process p in ps) p.Dispose(); }
            }
            catch { return true; } // if we cannot tell, do not invent a disconnect
        }

        /// <summary>Determines who is connected right now by replaying only the tail of the
        /// log since the last "Parsec: Started" marker, then parks the read cursor at EOF.
        /// Without this, starting the app mid-session would leave us blind, and starting it
        /// at boot would replay months of history.</summary>
        public void Reconcile()
        {
            _connected.Clear();
            _partial.Length = 0;
            _decoder = Encoding.UTF8.GetDecoder();

            _path = Resolve();
            if (_path == null)
            {
                Log.Info("Parsec log not found yet; will keep looking. Tried:");
                foreach (string p in CandidatePaths(_configuredPath)) Log.Info("    " + p);
                _pos = 0;
                return;
            }

            try
            {
                using (FileStream fs = Open(_path))
                {
                    long len = fs.Length;
                    long start = len > ReconcileTailBytes ? len - ReconcileTailBytes : 0;
                    fs.Seek(start, SeekOrigin.Begin);
                    byte[] buf = new byte[len - start];
                    int read = ReadFully(fs, buf);
                    string text = Encoding.UTF8.GetString(buf, 0, read);
                    _pos = len;

                    string[] lines = text.Split('\n');
                    // Anything before the newest restart marker describes a dead process.
                    int from = 0;
                    for (int i = 0; i < lines.Length; i++)
                        if (ReParsecStarted.IsMatch(lines[i].TrimEnd('\r'))) from = i + 1;

                    for (int i = from; i < lines.Length; i++)
                    {
                        string line = lines[i].TrimEnd('\r');
                        Match m = ReConnect.Match(line);
                        if (m.Success) { _connected.Add(m.Groups["user"].Value); continue; }
                        m = ReDisconnect.Match(line);
                        if (m.Success) { _connected.Remove(m.Groups["user"].Value); }
                    }
                }

                if (_connected.Count > 0 && !IsParsecRunning())
                {
                    Log.Info("log implies " + _connected.Count + " connected client(s) but parsecd.exe is not running; treating as idle");
                    _connected.Clear();
                }

                Log.Info("watching " + _path + " (starting at offset " + _pos + "); " +
                         _connected.Count + " client(s) currently connected" +
                         (_connected.Count > 0 ? ": " + string.Join(", ", ConnectedUsers) : ""));
            }
            catch (Exception ex)
            {
                Log.Error("reconcile failed for " + _path, ex);
                _pos = 0;
            }
        }

        private static FileStream Open(string path)
        {
            // parsecd keeps the log open for writing, so we must not ask for exclusivity.
            // FileShare.Delete also lets us survive Parsec rotating the file underneath us.
            return new FileStream(path, FileMode.Open, FileAccess.Read,
                                  FileShare.ReadWrite | FileShare.Delete);
        }

        private static int ReadFully(FileStream fs, byte[] buf)
        {
            int total = 0;
            while (total < buf.Length)
            {
                int n = fs.Read(buf, total, buf.Length - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }

        public void Poll()
        {
            if (_path == null)
            {
                string found = Resolve();
                if (found == null) return;
                _path = found;
                _warnedMissing = false;
                try { using (FileStream fs = Open(_path)) { _pos = fs.Length; } }
                catch { _pos = 0; }
                Log.Info("Parsec log appeared: " + _path + " (offset " + _pos + ")");
                return;
            }

            // Periodically re-check the candidate list. If the log we are tailing is not the
            // highest-priority one that exists, switch. This matters after a fallback: if an
            // explicitly configured logPath did not exist at startup we will have dropped to
            // %APPDATA%, and without this we would keep tailing the wrong file forever once
            // the configured one appeared.
            if (++_pollsSinceResolve >= 8)
            {
                _pollsSinceResolve = 0;
                string preferred = Resolve();
                if (preferred != null && !string.Equals(preferred, _path, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Info("Parsec log appeared with higher priority: " + preferred + " (was tailing " + _path + ")");
                    _path = preferred;
                    _partial.Length = 0;
                    _decoder = Encoding.UTF8.GetDecoder();
                    try { using (FileStream fs = Open(_path)) { _pos = fs.Length; } }
                    catch { _pos = 0; }
                    return;
                }
            }

            try
            {
                if (!File.Exists(_path))
                {
                    if (!_warnedMissing) { Log.Warn("Parsec log vanished: " + _path); _warnedMissing = true; }
                    _path = null;
                    return;
                }

                using (FileStream fs = Open(_path))
                {
                    long len = fs.Length;
                    if (len < _pos)
                    {
                        // Parsec rotates log.txt -> log.1.txt at roughly 1 MB, which shows up
                        // here as the file getting shorter. Restart from the beginning.
                        Log.Info("log truncated/rotated (" + _pos + " -> " + len + "); rereading from start");
                        _pos = 0;
                        _partial.Length = 0;
                        _decoder = Encoding.UTF8.GetDecoder();
                    }
                    if (len == _pos) return;

                    fs.Seek(_pos, SeekOrigin.Begin);
                    byte[] buf = new byte[len - _pos];
                    int read = ReadFully(fs, buf);
                    _pos += read;

                    // Stateful decoder: a chunk boundary can land mid-UTF-8-sequence.
                    char[] chars = new char[_decoder.GetCharCount(buf, 0, read)];
                    int nc = _decoder.GetChars(buf, 0, read, chars, 0);
                    _partial.Append(chars, 0, nc);
                }

                DrainLines();
            }
            catch (IOException ex) { Log.Debug("log read retry: " + ex.Message); }
            catch (Exception ex) { Log.Error("poll failed", ex); }

            if (_connected.Count > 0 && ++_pollsSinceProcCheck >= 12)
            {
                _pollsSinceProcCheck = 0;
                if (!IsParsecRunning())
                {
                    Log.Warn("parsecd.exe disappeared while " + _connected.Count + " client(s) were connected; treating as disconnect");
                    _connected.Clear();
                    Action<string, int> h = ClientDisconnected;
                    if (h != null) h("(parsec exited)", 0);
                }
            }
        }

        private void DrainLines()
        {
            while (true)
            {
                string all = _partial.ToString();
                int nl = all.IndexOf('\n');
                if (nl < 0) break;
                string line = all.Substring(0, nl).TrimEnd('\r');
                _partial.Remove(0, nl + 1);
                Handle(line);
            }

            // Guard against an unterminated final line growing without bound.
            if (_partial.Length > 64 * 1024) _partial.Length = 0;
        }

        private void Handle(string line)
        {
            if (line.Length == 0) return;

            Match m = ReConnect.Match(line);
            if (m.Success)
            {
                string user = m.Groups["user"].Value;
                bool added = _connected.Add(user);
                Log.Info("CONNECT   " + user + (added ? "" : " (already tracked)") + " -> " + _connected.Count + " client(s)");
                Action<string, int> h = ClientConnected;
                if (h != null) h(user, _connected.Count);
                return;
            }

            m = ReDisconnect.Match(line);
            if (m.Success)
            {
                string user = m.Groups["user"].Value;
                bool removed = _connected.Remove(user);
                Log.Info("DISCONNECT " + user + (removed ? "" : " (was not tracked)") + " -> " + _connected.Count + " client(s)");
                Action<string, int> h = ClientDisconnected;
                if (h != null) h(user, _connected.Count);
                return;
            }

            if (ReParsecStarted.IsMatch(line))
            {
                Log.Info("Parsec restarted; clearing " + _connected.Count + " tracked client(s)");
                _connected.Clear();
                Action h = ParsecRestarted;
                if (h != null) h();
            }
        }
    }
}
