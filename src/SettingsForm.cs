// SettingsForm.cs -- native-feeling settings dialog. C# 5 only.
//
// Layout is built from TableLayoutPanels rather than hardcoded pixel coordinates so it
// stays correct at any display scaling, and every control uses SystemFonts.MessageBoxFont
// so it matches the shell instead of WinForms' legacy 8pt default.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace ParsecHooks
{
    internal class SettingsForm : Form
    {
        private readonly Config _cfg;               // working copy, applied on OK/Apply
        private readonly Func<DisplayInfo[]> _displays;
        private readonly Func<string> _parsecLogPath;

        private ComboBox _cbKeep, _cbHdrScope, _cbLogLevel;
        private CheckBox _chkDisableMonitors, _chkDisableHdr, _chkNotify, _chkAutoStart, _chkGuard;
        private NumericUpDown _numApply, _numRevert, _numSettle, _numPoll, _numBaseline, _numGuard;
        private TextBox _txtLogPath;
        private Label _lblAutoStart, _lblStatus, _lblDetectedLog;
        private bool _loading;

        /// <summary>Set when the user accepted changes, so the caller knows to reload.</summary>
        public bool Applied { get; private set; }

        public SettingsForm(Config current, Func<DisplayInfo[]> displays, Func<string> parsecLogPath)
        {
            _cfg = current.Clone();
            _displays = displays;
            _parsecLogPath = parsecLogPath;

            Text = "parsec-hooks settings";
            Font = SystemFonts.MessageBoxFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            // Kept true so the dialog is alt-tabbable and reachable from the taskbar. With
            // ShowInTaskbar = false WinForms reparents it onto a hidden owner window, which
            // also makes it invisible to Process.MainWindowHandle.
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Font;
            // Explicit client size rather than AutoSize: a Form that auto-sizes around
            // docked children has a circular width dependency and collapses to the width of
            // its narrowest child. AutoScaleMode.Font still scales this for the user's DPI.
            ClientSize = new Size(540, 520);
            Padding = new Padding(10);

            BuildUi();
            LoadFromConfig();
            RefreshStatus();

            // A tray app has no owner window, so without this a modal dialog can open behind
            // whatever currently has the foreground.
            Shown += delegate { try { Activate(); BringToFront(); } catch { } };
        }

        // ---------------- layout helpers ----------------

        private static TableLayoutPanel Grid()
        {
            TableLayoutPanel t = new TableLayoutPanel();
            t.Dock = DockStyle.Top;
            t.AutoSize = true;
            t.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            t.ColumnCount = 2;
            t.Padding = new Padding(12);
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            return t;
        }

        private static void Row(TableLayoutPanel t, string label, Control c)
        {
            int r = t.RowCount++;
            Label l = new Label();
            l.Text = label;
            l.AutoSize = true;
            l.Anchor = AnchorStyles.Left;
            l.Margin = new Padding(0, 6, 10, 3);
            t.Controls.Add(l, 0, r);
            c.Anchor = AnchorStyles.Left;
            c.Margin = new Padding(0, 3, 0, 3);
            t.Controls.Add(c, 1, r);
        }

        private static void Span(TableLayoutPanel t, Control c)
        {
            int r = t.RowCount++;
            t.Controls.Add(c, 0, r);
            t.SetColumnSpan(c, 2);
        }

        private static Label Hint(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.MaximumSize = new Size(430, 0);
            l.ForeColor = SystemColors.GrayText;
            l.Margin = new Padding(0, 0, 0, 10);
            return l;
        }

        private static CheckBox Chk(string text)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.AutoSize = true;
            c.Margin = new Padding(0, 4, 0, 2);
            return c;
        }

        private static NumericUpDown Num(int min, int max, int step)
        {
            NumericUpDown n = new NumericUpDown();
            n.Minimum = min;
            n.Maximum = max;
            n.Increment = step;
            n.ThousandsSeparator = true;
            n.Width = 90;
            n.TextAlign = HorizontalAlignment.Right;
            return n;
        }

        private static ComboBox Combo(int width)
        {
            ComboBox c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.Width = width;
            return c;
        }

        // Value/label pair so combo boxes can show friendly text but round-trip raw config values.
        private class Item
        {
            public readonly string Value;
            private readonly string _text;
            public Item(string value, string text) { Value = value; _text = text; }
            public override string ToString() { return _text; }
        }

        private static void SelectByValue(ComboBox cb, string value)
        {
            foreach (object o in cb.Items)
            {
                Item it = o as Item;
                if (it != null && string.Equals(it.Value, value, StringComparison.OrdinalIgnoreCase))
                {
                    cb.SelectedItem = o;
                    return;
                }
            }
            if (cb.Items.Count > 0) cb.SelectedIndex = 0;
        }

        private static string SelectedValue(ComboBox cb, string fallback)
        {
            Item it = cb.SelectedItem as Item;
            return it == null ? fallback : it.Value;
        }

        // ---------------- UI ----------------

        private void BuildUi()
        {
            TabControl tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;

            tabs.TabPages.Add(BuildDisplaysTab());
            tabs.TabPages.Add(BuildTimingTab());
            tabs.TabPages.Add(BuildAdvancedTab());

            // ---- status strip ----
            _lblStatus = new Label();
            _lblStatus.AutoSize = false;
            _lblStatus.Height = 38;
            _lblStatus.Dock = DockStyle.Fill;
            _lblStatus.ForeColor = SystemColors.GrayText;
            _lblStatus.Padding = new Padding(3, 6, 3, 0);

            // ---- buttons ----
            Button btnOk = new Button();
            btnOk.Text = "OK";
            btnOk.DialogResult = DialogResult.OK;
            btnOk.AutoSize = true;
            btnOk.Click += delegate { if (Commit()) { Applied = true; DialogResult = DialogResult.OK; Close(); } };

            Button btnCancel = new Button();
            btnCancel.Text = "Cancel";
            btnCancel.DialogResult = DialogResult.Cancel;
            btnCancel.AutoSize = true;

            Button btnApply = new Button();
            btnApply.Text = "Apply";
            btnApply.AutoSize = true;
            btnApply.Click += delegate { if (Commit()) { Applied = true; RefreshStatus(); } };

            Button btnDefaults = new Button();
            btnDefaults.Text = "Reset to defaults";
            btnDefaults.AutoSize = true;
            btnDefaults.Click += delegate
            {
                if (MessageBox.Show("Reset every setting to its default value?", "parsec-hooks",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                Config d = new Config();
                _cfg.Keep = d.Keep;
                _cfg.DisableSecondaryMonitors = d.DisableSecondaryMonitors;
                _cfg.DisableHdr = d.DisableHdr;
                _cfg.HdrScope = d.HdrScope;
                _cfg.ApplyDelayMs = d.ApplyDelayMs;
                _cfg.RevertDelayMs = d.RevertDelayMs;
                _cfg.SettleMs = d.SettleMs;
                _cfg.PollMs = d.PollMs;
                _cfg.BaselineRefreshMs = d.BaselineRefreshMs;
                _cfg.GuardMs = d.GuardMs;
                _cfg.LogPath = d.LogPath;
                _cfg.LogLevel = d.LogLevel;
                _cfg.Notifications = d.Notifications;
                LoadFromConfig();
            };

            FlowLayoutPanel right = new FlowLayoutPanel();
            right.FlowDirection = FlowDirection.LeftToRight;
            right.AutoSize = true;
            right.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            // WrapContents defaults to true, which silently wrapped Cancel and Apply onto
            // rows that were then clipped, leaving only OK visible.
            right.WrapContents = false;
            right.Anchor = AnchorStyles.Right;
            right.Margin = new Padding(0);
            right.Controls.Add(btnOk);
            right.Controls.Add(btnCancel);
            right.Controls.Add(btnApply);

            TableLayoutPanel bottom = new TableLayoutPanel();
            bottom.ColumnCount = 2;
            bottom.RowCount = 1;
            bottom.Dock = DockStyle.Fill;
            bottom.AutoSize = true;
            bottom.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            btnDefaults.Anchor = AnchorStyles.Left;
            bottom.Controls.Add(btnDefaults, 0, 0);
            bottom.Controls.Add(right, 1, 0);

            // One Fill root with explicit row sizing: tabs take the slack, the status line
            // and button row size to their content. No docking order to get wrong.
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(tabs, 0, 0);
            root.Controls.Add(_lblStatus, 0, 1);
            root.Controls.Add(bottom, 0, 2);
            Controls.Add(root);

            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }

        private TabPage BuildDisplaysTab()
        {
            TabPage p = new TabPage("Displays & HDR");
            p.UseVisualStyleBackColor = true;
            p.AutoScroll = true;   // safety net if a translation or larger font overflows
            TableLayoutPanel t = Grid();

            _cbKeep = Combo(300);
            Row(t, "Keep this display on:", _cbKeep);
            Span(t, Hint("Every other display is switched off while a client is connected. "
                       + "\"Primary\" follows whatever Windows currently treats as the main display."));

            _chkDisableMonitors = Chk("Switch off the other displays during a session");
            _chkDisableMonitors.CheckedChanged += delegate { UpdateEnabledState(); };
            Span(t, _chkDisableMonitors);
            Span(t, Hint("Note: Windows moves any windows on a disabled display onto the remaining one, "
                       + "and does not move them back afterwards. Untick this to keep only the HDR behaviour."));

            _chkDisableHdr = Chk("Turn HDR off during a session");
            _chkDisableHdr.CheckedChanged += delegate { UpdateEnabledState(); };
            Span(t, _chkDisableHdr);

            _cbHdrScope = Combo(300);
            _cbHdrScope.Items.Add(new Item("kept", "Only the display(s) left switched on"));
            _cbHdrScope.Items.Add(new Item("all", "Every active display"));
            Row(t, "     Apply HDR change to:", _cbHdrScope);
            Span(t, Hint("HDR is only ever switched off where it was actually on, and is restored to "
                       + "exactly its previous state on disconnect."));

            p.Controls.Add(t);
            return p;
        }

        private TabPage BuildTimingTab()
        {
            TabPage p = new TabPage("Timing");
            p.UseVisualStyleBackColor = true;
            p.AutoScroll = true;   // safety net if a translation or larger font overflows
            TableLayoutPanel t = Grid();

            _numApply = Num(0, 60000, 100);
            Row(t, "Delay before applying (ms):", _numApply);
            Span(t, Hint("Gives Parsec time to finish its own resolution match and capture setup first."));

            _numRevert = Num(0, 60000, 100);
            Row(t, "Delay before reverting (ms):", _numRevert);
            Span(t, Hint("Also debounces a client that drops and immediately reconnects."));

            _numSettle = Num(0, 5000, 50);
            Row(t, "Settle pause (ms):", _numSettle);
            Span(t, Hint("Pause between the HDR change and the display change."));

            _chkGuard = Chk("Keep re-asserting while a client is connected");
            _chkGuard.CheckedChanged += delegate { UpdateEnabledState(); };
            Span(t, _chkGuard);

            _numGuard = Num(1000, 600000, 1000);
            Row(t, "     Re-check every (ms):", _numGuard);
            Span(t, Hint("Windows re-applies its remembered display layout whenever HDR changes or you "
                       + "open Display settings, which would otherwise switch your monitors back on "
                       + "mid-session. Recommended: leave this on."));

            p.Controls.Add(t);
            return p;
        }

        private TabPage BuildAdvancedTab()
        {
            TabPage p = new TabPage("Advanced");
            p.UseVisualStyleBackColor = true;
            p.AutoScroll = true;   // safety net if a translation or larger font overflows
            TableLayoutPanel t = Grid();

            _chkAutoStart = Chk("Start automatically when I log in");
            _chkAutoStart.CheckedChanged += delegate
            {
                if (_loading) return;
                if (_chkAutoStart.Checked) AutoStart.Enable(); else AutoStart.Disable();
                UpdateAutoStartLabel();
            };
            Span(t, _chkAutoStart);
            _lblAutoStart = Hint("");
            Span(t, _lblAutoStart);

            _chkNotify = Chk("Show tray notifications when tweaks are applied or reverted");
            Span(t, _chkNotify);

            _cbLogLevel = Combo(200);
            _cbLogLevel.Items.Add(new Item("error", "Errors only"));
            _cbLogLevel.Items.Add(new Item("warn", "Warnings and errors"));
            _cbLogLevel.Items.Add(new Item("info", "Normal (recommended)"));
            _cbLogLevel.Items.Add(new Item("debug", "Verbose (debugging)"));
            Row(t, "Log detail:", _cbLogLevel);

            _numPoll = Num(100, 10000, 50);
            Row(t, "Parsec log poll (ms):", _numPoll);

            _numBaseline = Num(2000, 600000, 1000);
            Row(t, "Re-snapshot layout every (ms):", _numBaseline);
            Span(t, Hint("Only ever happens while no client is connected, so a session's temporary "
                       + "resolution can never be mistaken for your normal layout."));

            _txtLogPath = new TextBox();
            _txtLogPath.Width = 195;   // leaves room for Browse inside the tab width
            Button browse = new Button();
            browse.Text = "Browse...";
            browse.AutoSize = true;
            browse.Click += delegate
            {
                OpenFileDialog d = new OpenFileDialog();
                d.Title = "Select the Parsec host log";
                d.Filter = "Parsec log (log.txt)|log.txt|Text files (*.txt)|*.txt|All files (*.*)|*.*";
                try
                {
                    string appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    string guess = Path.Combine(appdata, "Parsec");
                    if (Directory.Exists(guess)) d.InitialDirectory = guess;
                }
                catch { }
                if (d.ShowDialog(this) == DialogResult.OK) _txtLogPath.Text = d.FileName;
            };
            FlowLayoutPanel logRow = new FlowLayoutPanel();
            logRow.AutoSize = true;
            logRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            logRow.WrapContents = false;   // keep Browse beside the textbox, not under it
            logRow.Margin = new Padding(0);
            logRow.Controls.Add(_txtLogPath);
            logRow.Controls.Add(browse);
            Row(t, "Parsec log path:", logRow);

            _lblDetectedLog = Hint("");
            Span(t, _lblDetectedLog);

            FlowLayoutPanel links = new FlowLayoutPanel();
            links.AutoSize = true;
            links.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            links.WrapContents = false;
            links.Margin = new Padding(0, 4, 0, 0);
            links.Controls.Add(LinkBtn("Open my log", delegate { Open(Paths.LogFile); }));
            links.Controls.Add(LinkBtn("Open Parsec log", delegate
            {
                string lp = _parsecLogPath();
                if (string.IsNullOrEmpty(lp)) MessageBox.Show(this, "Parsec log not found yet.", "parsec-hooks");
                else Open(lp);
            }));
            links.Controls.Add(LinkBtn("Open config file", delegate { Open(Paths.ConfigFile); }));
            Span(t, links);

            p.Controls.Add(t);
            return p;
        }

        private static Button LinkBtn(string text, EventHandler onClick)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.Click += onClick;
            return b;
        }

        private static void Open(string path)
        {
            try
            {
                if (!File.Exists(path)) { MessageBox.Show("Not found:\n" + path, "parsec-hooks"); return; }
                ProcessStartInfo psi = new ProcessStartInfo(path);
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Exception ex) { MessageBox.Show("Could not open:\n" + path + "\n\n" + ex.Message, "parsec-hooks"); }
        }

        // ---------------- load / commit ----------------

        private void LoadFromConfig()
        {
            _loading = true;
            try
            {
                // Rebuild the display list every time so hot-plugged monitors show up.
                _cbKeep.Items.Clear();
                _cbKeep.Items.Add(new Item("primary", "Primary display (follow Windows)"));
                bool matched = string.Equals(_cfg.Keep, "primary", StringComparison.OrdinalIgnoreCase);
                try
                {
                    foreach (DisplayInfo d in _displays())
                    {
                        string tid = d.TargetId.ToString(CultureInfo.InvariantCulture);
                        string text = (string.IsNullOrEmpty(d.Name) ? "Display" : d.Name)
                                    + "  -  " + d.Width + "x" + d.Height
                                    + (d.IsPrimary ? " (primary)" : "")
                                    + "  [target " + tid + "]";
                        _cbKeep.Items.Add(new Item(tid, text));
                        if (string.Equals(_cfg.Keep, tid, StringComparison.OrdinalIgnoreCase)) matched = true;
                    }
                }
                catch (Exception ex) { Log.Warn("could not list displays for settings: " + ex.Message); }

                // Preserve a hand-written name/substring selector instead of silently dropping it.
                if (!matched && !string.IsNullOrEmpty(_cfg.Keep))
                    _cbKeep.Items.Add(new Item(_cfg.Keep, "Custom: " + _cfg.Keep));

                SelectByValue(_cbKeep, _cfg.Keep);
                SelectByValue(_cbHdrScope, _cfg.HdrScope);
                SelectByValue(_cbLogLevel, _cfg.LogLevel);

                _chkDisableMonitors.Checked = _cfg.DisableSecondaryMonitors;
                _chkDisableHdr.Checked = _cfg.DisableHdr;
                _chkNotify.Checked = _cfg.Notifications;

                _numApply.Value = Clamp(_numApply, _cfg.ApplyDelayMs);
                _numRevert.Value = Clamp(_numRevert, _cfg.RevertDelayMs);
                _numSettle.Value = Clamp(_numSettle, _cfg.SettleMs);
                _numPoll.Value = Clamp(_numPoll, _cfg.PollMs);
                _numBaseline.Value = Clamp(_numBaseline, _cfg.BaselineRefreshMs);

                _chkGuard.Checked = _cfg.GuardMs > 0;
                _numGuard.Value = Clamp(_numGuard, _cfg.GuardMs > 0 ? _cfg.GuardMs : 5000);

                _txtLogPath.Text = _cfg.LogPath == null ? "" : _cfg.LogPath;

                _chkAutoStart.Checked = AutoStart.IsEnabled();
                UpdateAutoStartLabel();
                UpdateEnabledState();
            }
            finally { _loading = false; }
        }

        private static decimal Clamp(NumericUpDown n, int v)
        {
            if (v < n.Minimum) return n.Minimum;
            if (v > n.Maximum) return n.Maximum;
            return v;
        }

        private void UpdateEnabledState()
        {
            _cbHdrScope.Enabled = _chkDisableHdr.Checked;
            _numGuard.Enabled = _chkGuard.Checked;
            _cbKeep.Enabled = _chkDisableMonitors.Checked;
        }

        private void UpdateAutoStartLabel()
        {
            _lblAutoStart.Text = AutoStart.Describe();
        }

        private void RefreshStatus()
        {
            try
            {
                DisplayInfo[] ds = _displays();
                List<string> hdrOn = new List<string>();
                foreach (DisplayInfo d in ds) if (d.HdrEnabled) hdrOn.Add(d.Name);

                string log = _parsecLogPath();
                _lblStatus.Text =
                    "Now: " + ds.Length + " display" + (ds.Length == 1 ? "" : "s") + " active"
                    + "   -   HDR on: " + (hdrOn.Count == 0 ? "none" : string.Join(", ", hdrOn.ToArray()))
                    + "\r\nParsec: " + (ParsecWatcher.IsParsecRunning() ? "running" : "not running")
                    + "   -   log: " + (string.IsNullOrEmpty(log) ? "not found" : "found");

                _lblDetectedLog.Text = "Leave empty to auto-detect. Currently using: "
                                     + (string.IsNullOrEmpty(log) ? "(none found yet)" : log);
            }
            catch (Exception ex) { _lblStatus.Text = "Status unavailable: " + ex.Message; }
        }

        /// <summary>Pulls the controls back into the working config and writes the file.
        /// Returns false (and keeps the dialog open) if the file could not be written.</summary>
        private bool Commit()
        {
            _cfg.Keep = SelectedValue(_cbKeep, "primary");
            _cfg.HdrScope = SelectedValue(_cbHdrScope, "kept");
            _cfg.LogLevel = SelectedValue(_cbLogLevel, "info");
            _cfg.DisableSecondaryMonitors = _chkDisableMonitors.Checked;
            _cfg.DisableHdr = _chkDisableHdr.Checked;
            _cfg.Notifications = _chkNotify.Checked;
            _cfg.ApplyDelayMs = (int)_numApply.Value;
            _cfg.RevertDelayMs = (int)_numRevert.Value;
            _cfg.SettleMs = (int)_numSettle.Value;
            _cfg.PollMs = (int)_numPoll.Value;
            _cfg.BaselineRefreshMs = (int)_numBaseline.Value;
            _cfg.GuardMs = _chkGuard.Checked ? (int)_numGuard.Value : 0;
            _cfg.LogPath = _txtLogPath.Text.Trim();

            if (!_cfg.DisableSecondaryMonitors && !_cfg.DisableHdr)
            {
                if (MessageBox.Show(this,
                        "Both actions are switched off, so parsec-hooks will not change anything "
                        + "when a client connects.\r\n\r\nSave anyway?",
                        "parsec-hooks", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return false;
            }

            if (!_cfg.Save())
            {
                MessageBox.Show(this, "Could not write the config file:\n" + Paths.ConfigFile
                    + "\n\nSee " + Paths.LogFile, "parsec-hooks", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            Log.Info("settings saved from the settings dialog");
            return true;
        }
    }
}
