// AutoStart.cs -- per-user logon registration. C# 5 only.
//
// Uses HKCU\...\CurrentVersion\Run rather than a Startup-folder shortcut. A .lnk would
// need IShellLink COM interop (or shelling out to PowerShell) just to toggle a checkbox,
// whereas a Run value is a two-line registry write. Both appear in Task Manager's Startup
// tab and neither needs admin, so nothing is lost.
//
// Earlier versions installed a Startup shortcut, so MigrateLegacyShortcut() converts an
// existing install to the Run value exactly once, leaving one mechanism rather than two.
using System;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace ParsecHooks
{
    internal static class AutoStart
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const string ValueName = "parsec-hooks";
        private const string LegacyShortcut = "parsec-hooks.lnk";

        public static string ExePath
        {
            get
            {
                try
                {
                    Assembly a = Assembly.GetEntryAssembly();
                    if (a != null && !string.IsNullOrEmpty(a.Location)) return a.Location;
                }
                catch { }
                return Path.Combine(Paths.ExeDir, "ParsecHooks.exe");
            }
        }

        private static string Command { get { return "\"" + ExePath + "\""; } }

        public static string LegacyShortcutPath
        {
            get
            {
                try { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), LegacyShortcut); }
                catch { return null; }
            }
        }

        /// <summary>True when a Run value for us exists (regardless of whether the user has
        /// since switched it off in Task Manager -- see <see cref="IsBlockedByUser"/>).</summary>
        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                {
                    if (k == null) return false;
                    object v = k.GetValue(ValueName);
                    return v != null && !string.IsNullOrEmpty(v.ToString());
                }
            }
            catch (Exception ex) { Log.Warn("autostart read failed: " + ex.Message); return false; }
        }

        /// <summary>The Run value can point at a stale path if the folder was moved.</summary>
        public static bool PointsAtThisExe()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                {
                    if (k == null) return false;
                    object v = k.GetValue(ValueName);
                    if (v == null) return false;
                    string cur = v.ToString().Trim().Trim('"');
                    return string.Equals(cur, ExePath, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        /// <summary>Task Manager does not delete the Run value when you disable a startup
        /// item; it records the choice separately. Without checking this, the checkbox would
        /// claim auto-start is on while Windows ignores it.</summary>
        public static bool IsBlockedByUser()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath))
                {
                    if (k == null) return false;
                    byte[] data = k.GetValue(ValueName) as byte[];
                    if (data == null || data.Length == 0) return false;
                    // First byte: 0x02 = enabled, 0x03 = disabled.
                    return (data[0] & 0x01) != 0;
                }
            }
            catch { return false; }
        }

        public static bool Enable()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (k == null) return false;
                    k.SetValue(ValueName, Command, RegistryValueKind.String);
                }
                Log.Info("auto-start enabled -> " + Command);
                return true;
            }
            catch (Exception ex) { Log.Error("could not enable auto-start", ex); return false; }
        }

        public static bool Disable()
        {
            bool ok = true;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (k != null && k.GetValue(ValueName) != null) k.DeleteValue(ValueName, false);
                }
                Log.Info("auto-start disabled");
            }
            catch (Exception ex) { Log.Error("could not disable auto-start", ex); ok = false; }

            RemoveLegacyShortcut();
            return ok;
        }

        public static bool RemoveLegacyShortcut()
        {
            try
            {
                string p = LegacyShortcutPath;
                if (p != null && File.Exists(p)) { File.Delete(p); return true; }
            }
            catch (Exception ex) { Log.Warn("could not remove legacy Startup shortcut: " + ex.Message); }
            return false;
        }

        /// <summary>Converts a pre-existing Startup-folder shortcut into a Run value so both
        /// never fire at once. Safe to call on every launch.</summary>
        public static void MigrateLegacyShortcut()
        {
            try
            {
                string p = LegacyShortcutPath;
                if (p == null || !File.Exists(p)) return;

                Log.Info("migrating legacy Startup shortcut to the Run registry value");
                bool enabled = Enable();
                if (enabled) RemoveLegacyShortcut();
            }
            catch (Exception ex) { Log.Warn("autostart migration failed: " + ex.Message); }
        }

        /// <summary>One-line description for the settings dialog.</summary>
        public static string Describe()
        {
            if (!IsEnabled()) return "Not registered to start at logon.";
            if (IsBlockedByUser()) return "Registered, but switched off in Task Manager > Startup apps.";
            if (!PointsAtThisExe()) return "Registered, but pointing at a different copy of the exe.";
            return "Will start automatically when you log in.";
        }
    }
}
