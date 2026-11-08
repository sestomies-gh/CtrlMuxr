// CtrlMuxr - GUI for the dinput_proxy settings.
// C# 5 / .NET Framework 4.x (built with the csc.exe that ships with Windows), no external libraries.
// Edits the dinput_proxy.ini that sits next to the proxy DLL in a game folder; the proxy reloads it while the game runs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.Reflection;

[assembly: AssemblyTitle("CtrlMuxr - game controller multiplexer")]
[assembly: AssemblyProduct("CtrlMuxr")]
[assembly: AssemblyCopyright("Copyright (c) 2026 sestomies-gh")]
[assembly: AssemblyVersion("1.15.0.0")]
[assembly: AssemblyFileVersion("1.15.0.0")]

namespace CtrlMuxr
{
    class GameDef
    {
        public string Name, Folder, Exe, Wheel;     // Exe: file names separated by '|'
        public bool Running;
        public int Pid;
        public DateTime Started;                    // process start time while running (the newest started game gets selected automatically)
        public override string ToString() { return (Running ? "\u25CF " : "   ") + Name; }
    }

    // Controls that ignore the mouse wheel: scrolling over a list or number field must never change a setting by accident.
    class NoWheelCombo : ComboBox
    {
        // item index -> true when the item belongs to a device that is not connected: such items are drawn in red
        Func<int, bool> offline;
        public Func<int, bool> Offline
        {
            get { return offline; }
            set { offline = value; DrawMode = value != null ? DrawMode.OwnerDrawFixed : DrawMode.Normal; }
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (offline == null || e.Index < 0) { base.OnDrawItem(e); return; }
            bool hot = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            e.DrawBackground();
            Color c = !Enabled ? SystemColors.GrayText : offline(e.Index) ? (hot ? Color.FromArgb(255, 170, 170) : Color.FromArgb(192, 0, 0)) : (hot ? SystemColors.HighlightText : SystemColors.WindowText);
            TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), e.Font, e.Bounds, c, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            e.DrawFocusRectangle();
        }

        // the closed box can be too narrow for a long device name: the open list is as wide as its longest item
        protected override void OnDropDown(EventArgs e)
        {
            int w = Width;
            foreach (object it in Items) w = Math.Max(w, TextRenderer.MeasureText(it.ToString(), Font).Width + SystemInformation.VerticalScrollBarWidth + 10);
            DropDownWidth = Math.Min(w, 700);
            base.OnDropDown(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (DroppedDown) { base.OnMouseWheel(e); return; }
            HandledMouseEventArgs h = e as HandledMouseEventArgs;
            if (h != null) h.Handled = true;
        }
    }

    class NoWheelNumeric : NumericUpDown
    {
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            HandledMouseEventArgs h = e as HandledMouseEventArgs;
            if (h != null) h.Handled = true;
        }
    }

    class NoWheelTrack : TrackBar
    {
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            HandledMouseEventArgs h = e as HandledMouseEventArgs;
            if (h != null) h.Handled = true;
        }
    }

    // INI file that keeps comments, order and unknown lines intact (only the touched lines change).
    class IniFile
    {
        public string Path;
        public List<string> Lines = new List<string>();
        public static readonly Encoding Enc = Encoding.GetEncoding(1252);

        public static IniFile Load(string path)
        {
            IniFile f = new IniFile();
            f.Path = path;
            if (File.Exists(path)) f.Lines.AddRange(File.ReadAllLines(path, Enc));
            return f;
        }

        public void Save()
        {
            string tmp = Path + ".tmp";
            File.WriteAllLines(tmp, Lines.ToArray(), Enc);
            if (File.Exists(Path)) File.Replace(tmp, Path, null);      // atomic: the proxy polls the file
            else File.Move(tmp, Path);
        }

        int SectionStart(string sec)
        {
            for (int i = 0; i < Lines.Count; i++)
                if (string.Equals(Lines[i].Trim(), "[" + sec + "]", StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        int SectionEnd(int start)       // index after the last line of the section
        {
            int i = start + 1;
            while (i < Lines.Count && !Lines[i].TrimStart().StartsWith("[")) i++;
            return i;
        }

        int FindKey(string sec, string key)
        {
            int s = SectionStart(sec);
            if (s < 0) return -1;
            int e = SectionEnd(s);
            for (int i = s + 1; i < e; i++)
            {
                string l = Lines[i].Trim();
                if (l.StartsWith(";") || l.Length == 0) continue;
                int eq = l.IndexOf('=');
                if (eq > 0 && string.Equals(l.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        public string Get(string sec, string key)
        {
            int i = FindKey(sec, key);
            return i < 0 ? null : Lines[i].Substring(Lines[i].IndexOf('=') + 1).Trim();
        }

        public void Set(string sec, string key, string value)
        {
            int i = FindKey(sec, key);
            if (i >= 0) { Lines[i] = key + "=" + value; return; }
            int s = SectionStart(sec);
            if (s < 0) { Lines.Add("[" + sec + "]"); Lines.Add(key + "=" + value); return; }
            int e = SectionEnd(s);
            while (e > s + 1 && Lines[e - 1].Trim().Length == 0) e--;      // keep trailing blank lines after the new key
            Lines.Insert(e, key + "=" + value);
        }

        public List<string> Keys(string sec)
        {
            List<string> r = new List<string>();
            int s = SectionStart(sec);
            if (s < 0) return r;
            int e = SectionEnd(s);
            for (int i = s + 1; i < e; i++)
            {
                string l = Lines[i].Trim();
                if (l.StartsWith(";") || l.Length == 0) continue;
                int eq = l.IndexOf('=');
                if (eq > 0) r.Add(l.Substring(0, eq).Trim());
            }
            return r;
        }

        public void RemoveSection(string sec)
        {
            int st = SectionStart(sec);
            if (st < 0) return;
            Lines.RemoveRange(st, SectionEnd(st) - st);
            while (Lines.Count > 0 && Lines[Lines.Count - 1].Trim().Length == 0) Lines.RemoveAt(Lines.Count - 1);
        }

        public void Remove(string sec, string key)
        {
            int i = FindKey(sec, key);
            if (i >= 0) Lines.RemoveAt(i);
        }
    }

    // [Shape] value: "invert dz=% expo=% gain=% adz=% lead=sec center=raw"
    class ShapeVals
    {
        public bool Invert;
        public double Dz, Expo, Gain = 100, Adz, Lead, Center = 32767.5;

        public static ShapeVals Parse(string v)
        {
            ShapeVals s = new ShapeVals();
            if (v == null) return s;
            foreach (string t in v.Split(new char[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(t, "invert", StringComparison.OrdinalIgnoreCase)) { s.Invert = true; continue; }
                int eq = t.IndexOf('=');
                if (eq < 0) continue;
                double x;
                if (!double.TryParse(t.Substring(eq + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out x)) continue;
                string k = t.Substring(0, eq).ToLowerInvariant();
                if (k == "dz") s.Dz = x; else if (k == "expo") s.Expo = x; else if (k == "gain") s.Gain = x;
                else if (k == "adz") s.Adz = x; else if (k == "lead") s.Lead = x; else if (k == "center") s.Center = x;
            }
            return s;
        }

        static string F(double x) { return x.ToString("0.###", CultureInfo.InvariantCulture); }

        public override string ToString()
        {
            return (Invert ? "invert " : "") + "dz=" + F(Dz) + " expo=" + F(Expo) + " gain=" + F(Gain) + " adz=" + F(Adz) + " lead=" + F(Lead) + (Math.Abs(Center - 32767.5) > 0.6 ? " center=" + F(Center) : "");
        }
    }

    static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        // true when a multiline TextBox is scrolled to its last line (EM_GETFIRSTVISIBLELINE / EM_GETLINECOUNT)
        public static bool AtBottom(TextBox tb)
        {
            int first = (int)SendMessage(tb.Handle, 0xCE, IntPtr.Zero, IntPtr.Zero);
            int total = (int)SendMessage(tb.Handle, 0xBA, IntPtr.Zero, IntPtr.Zero);
            int visible = Math.Max(1, tb.ClientSize.Height / Math.Max(1, tb.Font.Height));
            return first + visible >= total - 1;
        }

        // grey hint text inside an empty TextBox (EM_SETCUEBANNER)
        public static void Cue(TextBox tb, string text)
        {
            if (tb.IsHandleCreated) SendMessage(tb.Handle, 0x1501, (IntPtr)1, text);
            else tb.HandleCreated += delegate { SendMessage(tb.Handle, 0x1501, (IntPtr)1, text); };
        }

        public static string ProcessPath(int pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid);          // PROCESS_QUERY_LIMITED_INFORMATION
            if (h == IntPtr.Zero) return null;
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                int n = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }
    }

    // Reads the proxy's live block ("Local\CONTROLLAPP_live_<pid>", layout: see LiveBlock in core.cpp)
    class LiveReader
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr OpenFileMapping(uint acc, bool inh, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr MapViewOfFile(IntPtr h, uint acc, uint hi, uint lo, UIntPtr n);
        [DllImport("kernel32.dll")]
        static extern bool UnmapViewOfFile(IntPtr p);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        IntPtr map = IntPtr.Zero, view = IntPtr.Zero;
        int pid;
        public int[] Axes = new int[8], Min = new int[8], Max = new int[8];
        public byte[] Btn = new byte[128];
        public uint Vid, Pid, DevType, CapsFlags, GameDz, FmtSize;
        public string Name = "";

        public bool Open(int p)
        {
            if (view != IntPtr.Zero && pid == p) return true;
            Close();
            map = OpenFileMapping(4, false, "Local\\CONTROLLAPP_live_" + p);
            if (map == IntPtr.Zero) return false;
            view = MapViewOfFile(map, 4, 0, 0, UIntPtr.Zero);
            if (view == IntPtr.Zero) { Close(); return false; }
            pid = p;
            return true;
        }

        public void Close()
        {
            if (view != IntPtr.Zero) UnmapViewOfFile(view);
            if (map != IntPtr.Zero) CloseHandle(map);
            view = IntPtr.Zero; map = IntPtr.Zero; pid = 0;
        }

        public bool Read()
        {
            if (view == IntPtr.Zero || Marshal.ReadInt32(view, 0) != 0x564C4943) return false;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                int s1 = Marshal.ReadInt32(view, 4);
                if ((s1 & 1) != 0) continue;
                Vid = (uint)Marshal.ReadInt32(view, 12); Pid = (uint)Marshal.ReadInt32(view, 16);
                for (int i = 0; i < 8; i++)
                {
                    Axes[i] = Marshal.ReadInt32(view, 20 + i * 4);
                    Min[i] = Marshal.ReadInt32(view, 52 + i * 4);
                    Max[i] = Marshal.ReadInt32(view, 84 + i * 4);
                }
                Marshal.Copy(IntPtr.Add(view, 116), Btn, 0, 128);
                DevType = (uint)Marshal.ReadInt32(view, 244); CapsFlags = (uint)Marshal.ReadInt32(view, 248);
                GameDz = (uint)Marshal.ReadInt32(view, 252); FmtSize = (uint)Marshal.ReadInt32(view, 256);
                Name = Marshal.PtrToStringAnsi(IntPtr.Add(view, 260), 64);
                int z = Name.IndexOf('\0'); if (z >= 0) Name = Name.Substring(0, z);
                if (Marshal.ReadInt32(view, 4) == s1) return true;
            }
            return false;
        }
    }

    // Application settings (settings.ini next to the exe): language and the game that was selected last
    static class Settings
    {
        static string PathOf { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini"); } }

        public static string Get(string key, string def)
        {
            try { string v = IniFile.Load(PathOf).Get("App", key); return string.IsNullOrEmpty(v) ? def : v; }
            catch { return def; }
        }

        public static bool ReadOnly;        // the --selftest / --uitest runs must not change the user's settings

        public static void Set(string key, string value)
        {
            if (ReadOnly) return;
            try { IniFile f = IniFile.Load(PathOf); f.Set("App", key, value); f.Save(); } catch { }
        }
    }

    // Texts live in lang\<code>.ini (UTF-8, key=text, "\n" = line break). en.ini is the fallback; a missing key shows the key.
    // ---------------------------------------------------------------- log analysis (what the proxy handed to the game in the last session)
    class Finding
    {
        public bool Glow = true;        // false: only shown in the view, does not make the button glow
        public string Text;
        public Finding(string text) { Text = text; }
    }

    class SessionLog
    {
        public bool Has;
        public string Name, Sig;
        public DateTime Time;
        public List<string> BadCfg = new List<string>(), SrcFail = new List<string>(), AcqFail = new List<string>(), AcqRecovered = new List<string>();
        public bool WheelListed, WheelHidden, WheelOpened, OutSeen;
        public string WheelName;
        public Dictionary<string, int[]> SrcMin = new Dictionary<string, int[]>(), SrcMax = new Dictionary<string, int[]>();
        public int[] OutMin = new int[8], OutMax = new int[8];

        public int SrcRange(string dev, int axis)
        {
            int[] lo, hi;
            if (!SrcMin.TryGetValue(dev, out lo) || !SrcMax.TryGetValue(dev, out hi)) return 0;
            return hi[axis] - lo[axis];
        }

        public static string Signature(string path)
        {
            FileInfo fi = new FileInfo(path);
            return path + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
        }

        // the proxy's own session logs are dinput_<number>.log (dinput_errors.log is the separate error log)
        public static bool IsSessionLog(string path)
        {
            return System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), @"^dinput_\d+\.log$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        // newest dinput_<pid>.log in the game folder (null when there is none)
        public static string Newest(string folder)
        {
            string newest = null; DateTime best = DateTime.MinValue;
            try
            {
                foreach (string f in Directory.GetFiles(folder, "dinput_*.log"))
                {
                    if (!IsSessionLog(f)) continue;
                    DateTime t = File.GetLastWriteTime(f);
                    if (t > best) { best = t; newest = f; }
                }
            }
            catch { }
            return newest;
        }

        public static SessionLog Parse(string path, string wheelVid, string wheelPid)
        {
            SessionLog s = new SessionLog();
            if (path == null) return s;
            string[] lines;
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader sr = new StreamReader(fs, Encoding.GetEncoding(1252)))
                    lines = sr.ReadToEnd().Split('\n');
            }
            catch { return s; }
            s.Has = true; s.Name = Path.GetFileName(path); s.Sig = Signature(path); s.Time = File.GetLastWriteTime(path);
            bool outInit = false;
            string lastEnum = null; bool lastEnumWheel = false;
            HashSet<string> ready = new HashSet<string>();
            Dictionary<string, string> acqLast = new Dictionary<string, string>(), acqBad = new Dictionary<string, string>();   // per device: last Acquire hr, first failing line
            for (int n = 0; n < lines.Length; n++)
            {
                string l = lines[n].TrimEnd('\r');
                int b = l.IndexOf("] ");
                if (b < 0) continue;
                l = l.Substring(b + 2);
                string t = l.TrimStart();
                if (t.StartsWith("Config: bad ") || t.StartsWith("Config: unknown ")) { s.BadCfg.Add(t.Substring(8)); continue; }
                if (t.StartsWith("Source ") && t.IndexOf(" opened:") < 0 && !t.EndsWith(": back") && t.IndexOf(": lost (") < 0) { s.SrcFail.Add(t); continue; }
                if (t.StartsWith("Acquire "))
                {
                    if (t.IndexOf("(repeated") >= 0) continue;                                  // repeat count of the previous result; the next line has the new one
                    int h = t.LastIndexOf("hr=");
                    string hr = h >= 0 ? t.Substring(h + 3).Split(' ')[0].Trim() : "0";
                    if (t.IndexOf("fallback") >= 0 || t.IndexOf("upgrade failed") >= 0)        // a lasting problem: the proxy had to change the level
                    {
                        if (s.AcqFail.Count < 5) s.AcqFail.Add(t);
                        continue;
                    }
                    int e = t.IndexOf(" hr=");
                    string dn = e > 8 ? t.Substring(8, e - 8) : "?";
                    acqLast[dn] = hr;
                    // a failure is only a finding if the device never got the access back (hr=1: already acquired). Otherwise the game window was not in the foreground.
                    if (hr.Length > 0 && hr != "0" && hr != "1" && !acqBad.ContainsKey(dn)) acqBad[dn] = t;
                    continue;
                }
                if (t.StartsWith("Enum: \"") || t.StartsWith("EnumW: \""))      // DirectInput 8 / DirectInput 1-7 (wide) device lists
                {
                    int q1 = t.IndexOf('"'), q2 = t.IndexOf('"', q1 + 1);
                    int v = t.IndexOf("VID="), p = t.IndexOf("PID=");
                    lastEnum = q2 > q1 ? t.Substring(q1 + 1, q2 - q1 - 1) : null;
                    lastEnumWheel = v >= 0 && p >= 0 && t.Length >= p + 8 && string.Equals(t.Substring(v + 4, 4), wheelVid, StringComparison.OrdinalIgnoreCase) && string.Equals(t.Substring(p + 4, 4), wheelPid, StringComparison.OrdinalIgnoreCase);
                    if (lastEnumWheel) { s.WheelListed = true; s.WheelName = lastEnum; }
                    continue;
                }
                if (t.StartsWith("-> hidden from game")) { if (lastEnumWheel) s.WheelHidden = true; continue; }
                if (t.StartsWith("Device ready: "))
                {
                    int p = t.IndexOf(" ptr=");
                    if (p > 14) ready.Add(t.Substring(14, p - 14));
                    continue;
                }
                if (t.StartsWith("Src ") || t.StartsWith("Out "))
                {
                    int dw = t.IndexOf(" dw:");
                    int bt = t.IndexOf(" | btn:");
                    if (dw < 0) continue;
                    string[] tk = (bt > dw ? t.Substring(dw + 4, bt - dw - 4) : t.Substring(dw + 4)).Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tk.Length < 8) continue;
                    int[] v8 = new int[8];
                    bool ok = true;
                    for (int i = 0; i < 8; i++) if (!int.TryParse(tk[i], out v8[i])) ok = false;
                    if (!ok) continue;
                    if (t.StartsWith("Out "))
                    {
                        s.OutSeen = true;
                        for (int i = 0; i < 8; i++)
                        {
                            if (!outInit) { s.OutMin[i] = s.OutMax[i] = v8[i]; }
                            else { if (v8[i] < s.OutMin[i]) s.OutMin[i] = v8[i]; if (v8[i] > s.OutMax[i]) s.OutMax[i] = v8[i]; }
                        }
                        outInit = true;
                    }
                    else
                    {
                        string dev = t.Substring(4, 9).ToUpperInvariant();
                        int[] lo, hi;
                        if (!s.SrcMin.TryGetValue(dev, out lo)) { lo = (int[])v8.Clone(); hi = (int[])v8.Clone(); s.SrcMin[dev] = lo; s.SrcMax[dev] = hi; }
                        else
                        {
                            hi = s.SrcMax[dev];
                            for (int i = 0; i < 8; i++) { if (v8[i] < lo[i]) lo[i] = v8[i]; if (v8[i] > hi[i]) hi[i] = v8[i]; }
                        }
                    }
                }
            }
            foreach (KeyValuePair<string, string> kv in acqBad)
            {
                string last = acqLast.ContainsKey(kv.Key) ? acqLast[kv.Key] : "";
                if (last == "0" || last == "1") s.AcqRecovered.Add(kv.Key);
                else if (s.AcqFail.Count < 5) s.AcqFail.Add(kv.Value);
            }
            s.WheelOpened = s.WheelName != null && ready.Contains(s.WheelName);
            return s;
        }
    }

    // the "Analyse" view: findings from the log, then questions about what the game accepted and suggestions for what to try next
    class AnalysisForm : Form
    {
        ComboBox[] answers;
        TextBox result = new TextBox();

        public AnalysisForm(string title, string intro, string findings, string[] controls, string[] answerNames, string suggestLabel, string closeLabel, string qHeader, Func<int[], string> suggest)
        {
            Text = title; Width = 820; Height = 720; MinimumSize = new Size(640, 520); StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9f);
            Label il = new Label(); il.Text = intro; il.Dock = DockStyle.Top; il.Height = 40; il.Padding = new Padding(8, 8, 8, 0); il.ForeColor = SystemColors.GrayText;
            TextBox fb = new TextBox(); fb.Multiline = true; fb.ReadOnly = true; fb.ScrollBars = ScrollBars.Vertical; fb.Text = findings; fb.Dock = DockStyle.Top; fb.Height = 190; fb.BackColor = SystemColors.Window;
            Label qh = new Label(); qh.Text = qHeader; qh.Dock = DockStyle.Top; qh.Height = 44; qh.Padding = new Padding(8, 10, 8, 0); qh.Font = new Font(Font, FontStyle.Bold);
            TableLayoutPanel t = new TableLayoutPanel(); t.Dock = DockStyle.Top; t.AutoSize = true; t.ColumnCount = 2; t.Padding = new Padding(8, 0, 8, 0);
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200)); t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
            answers = new ComboBox[controls.Length];
            for (int i = 0; i < controls.Length; i++)
            {
                Label l = new Label(); l.Text = controls[i]; l.AutoSize = false; l.Width = 190; l.Height = 24; l.TextAlign = ContentAlignment.MiddleLeft;
                answers[i] = new ComboBox(); answers[i].DropDownStyle = ComboBoxStyle.DropDownList; answers[i].Width = 210;
                answers[i].Items.AddRange(answerNames); answers[i].SelectedIndex = 0;
                t.Controls.Add(l, 0, i); t.Controls.Add(answers[i], 1, i);
            }
            Button sg = new Button(); sg.Text = suggestLabel; sg.Dock = DockStyle.Top; sg.Height = 30;
            Button close = new Button(); close.Text = closeLabel; close.Dock = DockStyle.Bottom; close.Height = 30;
            close.Click += delegate { Close(); };
            result.Multiline = true; result.ReadOnly = true; result.ScrollBars = ScrollBars.Vertical; result.Dock = DockStyle.Fill; result.BackColor = SystemColors.Window;
            sg.Click += delegate
            {
                int[] a = new int[answers.Length];
                for (int i = 0; i < a.Length; i++) a[i] = answers[i].SelectedIndex;
                result.Text = suggest(a).Replace("\r\n", "\n").Replace("\n", "\r\n");
            };
            Controls.Add(result); Controls.Add(close); Controls.Add(sg); Controls.Add(t); Controls.Add(qh); Controls.Add(fb); Controls.Add(il);
        }
    }

    // Where things are kept: app\data\games\<game>\ holds the master copy of the game's dinput_proxy.ini, its backups and profiles;
    // the game folder holds the working copy (what the proxy reads). The copies are synchronised; nothing is overwritten without a backup.
    static class Store
    {
        public static string RootOverride;      // tests use a temporary folder
        public static string Root(string appDir) { return RootOverride ?? Path.Combine(appDir, "data"); }

        // the store folder of a game, not created
        public static string GameDirPath(string appDir, string name)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in name) sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            return Path.Combine(Path.Combine(Root(appDir), "games"), sb.ToString());
        }

        public static string GameDir(string appDir, string name)
        {
            string d = GameDirPath(appDir, name);
            Directory.CreateDirectory(d);
            return d;
        }

        // the settings the game would start with when added: the INI in the game folder, else the stored copy of this game; null = none
        public static string[] ExistingIni(string appDir, string name, string folder)
        {
            try
            {
                string gp = Path.Combine(folder, "dinput_proxy.ini"), ap = Path.Combine(GameDirPath(appDir, name), "dinput_proxy.ini");
                if (File.Exists(gp)) return File.ReadAllLines(gp, IniFile.Enc);
                if (File.Exists(ap)) return File.ReadAllLines(ap, IniFile.Enc);
            }
            catch { }
            return null;
        }

        public static string Hash(string path)
        {
            using (System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create())
                return BitConverter.ToString(md5.ComputeHash(File.ReadAllBytes(path)));
        }

        static string ReadMeta(string meta)
        {
            try { return File.Exists(meta) ? File.ReadAllText(meta).Trim() : null; } catch { return null; }
        }

        static void WriteMeta(string meta, string hash) { try { File.WriteAllText(meta, hash); } catch { } }

        // true when the file is one of our proxy DLLs (they contain the live-block name)
        public static bool IsOurProxy(string path)
        {
            try
            {
                FileInfo fi = new FileInfo(path);
                if (!fi.Exists || fi.Length > 4 * 1024 * 1024) return false;
                return Encoding.GetEncoding(1252).GetString(File.ReadAllBytes(path)).Contains("CONTROLLAPP_live");
            }
            catch { return false; }
        }

        public static readonly string[] ProxyNames = new string[] { "dinput.dll", "dinput8.dll", "msacm32.dll" };

        public static bool HasProxy(string folder)
        {
            foreach (string n in ProxyNames) if (IsOurProxy(Path.Combine(folder, n))) return true;
            return false;
        }

        // earlier versions kept backups and profiles in the game folder: move them to the store
        static void Migrate(string dir, string folder)
        {
            try
            {
                List<string> files = new List<string>();
                files.AddRange(Directory.GetFiles(folder, "dinput_proxy.ini.bak_*"));
                files.AddRange(Directory.GetFiles(folder, "dinput_proxy.profile_*.ini"));
                foreach (string f in files)
                {
                    string t = Path.Combine(dir, Path.GetFileName(f));
                    if (File.Exists(t)) File.Delete(f); else File.Move(f, t);
                }
            }
            catch { }
        }

        // Brings the two copies of dinput_proxy.ini together. Only one side changed since the last sync: that side wins.
        // Both changed (or never synced): the newer one wins and the other is kept as a backup.
        public static void Sync(string appDir, string name, string folder)
        {
            try
            {
                if (name == null || folder == null || !Directory.Exists(folder)) return;
                string dir = GameDir(appDir, name);
                Migrate(dir, folder);
                string gp = Path.Combine(folder, "dinput_proxy.ini"), ap = Path.Combine(dir, "dinput_proxy.ini"), meta = Path.Combine(dir, "sync.txt");
                bool g = File.Exists(gp), a = File.Exists(ap);
                if (!g && !a) return;
                if (g && !a) { File.Copy(gp, ap, true); WriteMeta(meta, Hash(gp)); return; }
                if (!g) { if (HasProxy(folder)) { File.Copy(ap, gp, false); WriteMeta(meta, Hash(ap)); } return; }   // no proxy in the folder: it was uninstalled on purpose
                string gh = Hash(gp), ah = Hash(ap);
                if (gh == ah) { WriteMeta(meta, gh); return; }
                string last = ReadMeta(meta);
                bool onlyOne = last != null && (gh == last || ah == last);
                bool gameWins = onlyOne ? ah == last : File.GetLastWriteTimeUtc(gp) >= File.GetLastWriteTimeUtc(ap);
                string win = gameWins ? gp : ap, lose = gameWins ? ap : gp;
                if (!onlyOne) File.Copy(lose, Path.Combine(dir, "dinput_proxy.ini.bak_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")), true);
                File.Copy(win, lose, true);
                WriteMeta(meta, Hash(win));
            }
            catch { }
        }

        // the game's INI was just written: the store gets the same content
        public static void AfterSave(string appDir, string name, string folder)
        {
            try
            {
                string gp = Path.Combine(folder, "dinput_proxy.ini");
                if (!File.Exists(gp)) return;
                string dir = GameDir(appDir, name);
                File.Copy(gp, Path.Combine(dir, "dinput_proxy.ini"), true);
                WriteMeta(Path.Combine(dir, "sync.txt"), Hash(gp));
            }
            catch { }
        }

        public static void Delete(string appDir, string name)
        {
            try { Directory.Delete(GameDir(appDir, name), true); } catch { }
        }
    }

    // Message box with buttons in the app's language (the system MessageBox shows the buttons in the language of Windows).
    static class Dlg
    {
        public static DialogResult Show(string text, string caption) { return Show(null, text, caption, MessageBoxButtons.OK, MessageBoxIcon.None); }
        public static DialogResult Show(IWin32Window owner, string text, string caption) { return Show(owner, text, caption, MessageBoxButtons.OK, MessageBoxIcon.None); }

        public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            return Show(owner, text, caption, buttons, icon, null);
        }

        // customLabels: texts for the buttons in order (the results stay Yes/No/Cancel), or null for the usual ones
        public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, string[] customLabels)
        {
            using (Form f = new Form())
            {
                f.Text = caption; f.FormBorderStyle = FormBorderStyle.FixedDialog; f.MaximizeBox = false; f.MinimizeBox = false; f.ShowInTaskbar = false;
                f.StartPosition = owner != null ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen;
                f.Font = new Font("Segoe UI", 9f);
                Icon ic = null;
                if (icon == MessageBoxIcon.Question) ic = SystemIcons.Question;
                else if (icon == MessageBoxIcon.Warning) ic = SystemIcons.Warning;
                else if (icon == MessageBoxIcon.Error) ic = SystemIcons.Error;
                else if (icon == MessageBoxIcon.Information) ic = SystemIcons.Information;
                int left = 16;
                if (ic != null)
                {
                    PictureBox pb = new PictureBox(); pb.Image = ic.ToBitmap(); pb.SizeMode = PictureBoxSizeMode.CenterImage; pb.Left = 14; pb.Top = 16; pb.Width = 40; pb.Height = 40;
                    f.Controls.Add(pb); left = 62;
                }
                Label l = new Label(); l.Text = text; l.AutoSize = true; l.MaximumSize = new Size(440, 0); l.Left = left; l.Top = 18;
                f.Controls.Add(l);
                Size ps = l.GetPreferredSize(new Size(440, 0));
                string[] labels; DialogResult[] results;
                if (buttons == MessageBoxButtons.YesNoCancel) { labels = new string[] { Loc.T("dlg.yes"), Loc.T("dlg.no"), Loc.T("dlg.cancel") }; results = new DialogResult[] { DialogResult.Yes, DialogResult.No, DialogResult.Cancel }; }
                else if (buttons == MessageBoxButtons.YesNo) { labels = new string[] { Loc.T("dlg.yes"), Loc.T("dlg.no") }; results = new DialogResult[] { DialogResult.Yes, DialogResult.No }; }
                else if (buttons == MessageBoxButtons.OKCancel) { labels = new string[] { Loc.T("dlg.ok"), Loc.T("dlg.cancel") }; results = new DialogResult[] { DialogResult.OK, DialogResult.Cancel }; }
                else { labels = new string[] { Loc.T("dlg.ok") }; results = new DialogResult[] { DialogResult.OK }; }
                if (customLabels != null && customLabels.Length == labels.Length) labels = customLabels;
                int width = Math.Max(left + ps.Width + 20, labels.Length * 90 + 20);
                int top = Math.Max(ps.Height + 18, ic != null ? 56 : 0) + 22;
                int x = width - 10;
                Button first = null, cancel = null;
                for (int i = labels.Length - 1; i >= 0; i--)
                {
                    Button b = new Button(); b.Text = labels[i]; b.DialogResult = results[i]; b.Width = 84; b.Height = 26; b.Left = x - 84; b.Top = top; x -= 90;
                    f.Controls.Add(b);
                    if (i == 0) first = b;
                    if (results[i] == DialogResult.Cancel || results[i] == DialogResult.No && cancel == null && labels.Length == 2) cancel = b;
                }
                f.AcceptButton = first; f.CancelButton = cancel ?? first;
                f.ClientSize = new Size(width, top + 26 + 14);
                return f.ShowDialog(owner);
            }
        }
    }

    static class Loc
    {
        static Dictionary<string, string> cur = new Dictionary<string, string>(), fb = new Dictionary<string, string>();
        public static string Dir, Code = "en";

        static Dictionary<string, string> Read(string path)
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            if (!File.Exists(path)) return d;
            foreach (string raw in File.ReadAllLines(path, new UTF8Encoding(false)))
            {
                string l = raw.TrimStart('\uFEFF');
                if (l.Length == 0 || l[0] == ';' || l[0] == '#') continue;
                int eq = l.IndexOf('=');
                if (eq <= 0) continue;
                d[l.Substring(0, eq).Trim()] = l.Substring(eq + 1).Replace("\\n", "\n");
            }
            return d;
        }

        public static void Init(string appDir)
        {
            Dir = Path.Combine(appDir, "lang");
            string pref = Settings.Get("Language", "auto");
            if (pref == "auto") pref = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            InitFor(appDir, pref);
        }

        public static void InitFor(string appDir, string pref)
        {
            Dir = Path.Combine(appDir, "lang");
            fb = Read(Path.Combine(Dir, "en.ini"));
            if (!File.Exists(Path.Combine(Dir, pref + ".ini"))) pref = "en";
            Code = pref;
            cur = pref == "en" ? fb : Read(Path.Combine(Dir, pref + ".ini"));
        }

        public static string T(string key)
        {
            string v;
            if (cur.TryGetValue(key, out v) || fb.TryGetValue(key, out v)) return key.StartsWith("tt.") ? Wrap(v, 64) : v;
            return key;
        }

        // tooltips: a line holds at most about 10-12 words
        static string Wrap(string s, int width)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string para in s.Split('\n'))
            {
                int col = 0;
                foreach (string w in para.Split(' '))
                {
                    if (col + w.Length > width && col > 0) { sb.Append('\n'); col = 0; }
                    else if (col > 0) { sb.Append(' '); col++; }
                    sb.Append(w); col += w.Length;
                }
                sb.Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }

        public static string T(string key, params object[] args) { return string.Format(T(key), args); }

        // language files found in lang\: {code, display name}
        public static List<string[]> Languages()
        {
            List<string[]> r = new List<string[]>();
            if (Dir == null || !Directory.Exists(Dir)) return r;
            string[] files = Directory.GetFiles(Dir, "*.ini");
            Array.Sort(files);
            foreach (string f in files)
            {
                string code = Path.GetFileNameWithoutExtension(f);
                Dictionary<string, string> d = Read(f);
                string n;
                r.Add(new string[] { code, d.TryGetValue("lang.name", out n) ? n : code });
            }
            return r;
        }
    }

    // Reads what a Windows executable imports: machine type and imported DLLs / functions.
    class PeInfo
    {
        public bool Valid, Is64;
        public Dictionary<string, List<string>> Imports = new Dictionary<string, List<string>>();   // dll (lower case) -> function names

        public bool Has(string dll) { return Imports.ContainsKey(dll.ToLowerInvariant()); }

        public bool HasFunc(string dll, params string[] funcs)
        {
            List<string> l;
            if (!Imports.TryGetValue(dll.ToLowerInvariant(), out l)) return false;
            foreach (string f in funcs) if (l.Contains(f)) return true;
            return false;
        }

        public static PeInfo Read(string path)
        {
            PeInfo r = new PeInfo();
            try
            {
                byte[] d = File.ReadAllBytes(path);
                int pe = BitConverter.ToInt32(d, 0x3c);
                if (d.Length < pe + 24 || d[pe] != (byte)'P' || d[pe + 1] != (byte)'E') return r;
                int nsec = BitConverter.ToUInt16(d, pe + 6), optsz = BitConverter.ToUInt16(d, pe + 20), opt = pe + 24;
                r.Is64 = BitConverter.ToUInt16(d, opt) == 0x20b;
                int dd = opt + (r.Is64 ? 112 : 96);
                uint impRva = BitConverter.ToUInt32(d, dd + 8);
                int so = opt + optsz;
                uint[][] secs = new uint[nsec][];
                for (int i = 0; i < nsec; i++)
                {
                    uint vs = BitConverter.ToUInt32(d, so + i * 40 + 8), va = BitConverter.ToUInt32(d, so + i * 40 + 12);
                    uint rs = BitConverter.ToUInt32(d, so + i * 40 + 16), ro = BitConverter.ToUInt32(d, so + i * 40 + 20);
                    secs[i] = new uint[] { va, Math.Max(vs, rs), ro };
                }
                Func<uint, int> off = delegate(uint rva)
                {
                    foreach (uint[] sc in secs) if (rva >= sc[0] && rva < sc[0] + sc[1]) return (int)(rva - sc[0] + sc[2]);
                    return -1;
                };
                Func<int, string> cstr = delegate(int o)
                {
                    int e = o;
                    while (e < d.Length && d[e] != 0) e++;
                    return Encoding.ASCII.GetString(d, o, e - o);
                };
                int desc = off(impRva);
                while (desc >= 0 && desc + 20 <= d.Length)
                {
                    uint ot = BitConverter.ToUInt32(d, desc), nm = BitConverter.ToUInt32(d, desc + 12), ft = BitConverter.ToUInt32(d, desc + 16);
                    if (nm == 0) break;
                    string dll = cstr(off(nm)).ToLowerInvariant();
                    List<string> funcs = new List<string>();
                    int t = off(ot != 0 ? ot : ft);
                    int step = r.Is64 ? 8 : 4;
                    while (t >= 0 && t + step <= d.Length)
                    {
                        ulong v = r.Is64 ? BitConverter.ToUInt64(d, t) : BitConverter.ToUInt32(d, t);
                        if (v == 0) break;
                        bool ordinal = (v >> (r.Is64 ? 63 : 31) & 1) != 0;
                        if (!ordinal) { int o2 = off((uint)(v & 0x7fffffff)); if (o2 >= 0) funcs.Add(cstr(o2 + 2)); }
                        t += step;
                    }
                    if (!r.Imports.ContainsKey(dll)) r.Imports[dll] = funcs; else r.Imports[dll].AddRange(funcs);
                    desc += 20;
                }
                r.Valid = true;
            }
            catch { }
            return r;
        }
    }

    // "Add game" dialog: detects how the game reads controllers, installs the matching proxy DLL and a starting INI.
    class AddGameForm : Form
    {
        public string GameName, Folder, ExeName, Wheel;
        readonly string appDir;
        readonly DRDevInfo[] devs;
        TextBox txtExe = new TextBox(), txtName = new TextBox();
        Panel wheelPanel = new Panel();                     // one radio button per connected controller (+ "other"): the one the game will see
        List<RadioButton> wheelRadios = new List<RadioButton>();
        List<string> wheelKeys = new List<string>();
        RadioButton wheelOther = new RadioButton();
        TextBox txtOther = new TextBox();
        CheckBox chkHide = new CheckBox();
        Label lblInfo = new Label();
        Button ok = new Button(), cancel = new Button(), browse = new Button();
        ToolTip tip = new ToolTip();
        string dllName;             // proxy DLL to install, or null
        bool is64, usesWinmm;

        readonly Func<string, string> listedGameOf;        // name of the listed game whose folder this is, or null
        string reinstallName, reinstallWheel;               // set when the proxy of a listed game is installed again

        // "Install proxy": the window for a game that is already on the list (its exe, name and wheel are fixed)
        public void PrepareReinstall(string name, string folder, string exeNames, string wheelKey)
        {
            reinstallName = name; reinstallWheel = wheelKey;
            string exe = null;
            foreach (string e in (exeNames ?? "").Split('|'))
            {
                string p = Path.Combine(folder, e.Trim());
                if (e.Trim().Length > 0 && File.Exists(p)) { exe = p; break; }
            }
            txtExe.Text = exe ?? ""; txtExe.ReadOnly = true; browse.Enabled = false;
            txtName.Text = name; txtName.ReadOnly = true;
            wheelPanel.Enabled = false;
            Text = Loc.T("add.title.reinstall", name);
            Analyze();
        }

        public AddGameForm(string appDirectory, DRDevInfo[] connected, Func<string, string> listedGameOf)
        {
            appDir = appDirectory; devs = connected; this.listedGameOf = listedGameOf;
            Text = Loc.T("add.title");
            Width = 760; Height = 600; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9f);
            int y = 14;
            Label l1 = new Label(); l1.Text = Loc.T("add.exe"); l1.Left = 12; l1.Top = y + 4; l1.AutoSize = false; l1.Width = 250; l1.Height = 30;
            txtExe.Left = 270; txtExe.Top = y; txtExe.Width = 360;
            browse.Text = Loc.T("add.browse"); browse.Left = 640; browse.Top = y - 1; browse.Width = 90;
            browse.Click += delegate
            {
                using (OpenFileDialog dlg = new OpenFileDialog())
                {
                    dlg.Filter = "*.exe|*.exe"; dlg.Title = Loc.T("add.exe");
                    if (dlg.ShowDialog(this) == DialogResult.OK) { txtExe.Text = dlg.FileName; Analyze(); }
                }
            };
            txtExe.Leave += delegate { Analyze(); };
            y += 36;
            Label l2 = new Label(); l2.Text = Loc.T("add.name"); l2.Left = 12; l2.Top = y + 4; l2.AutoSize = false; l2.Width = 250; l2.Height = 30;
            txtName.Left = 270; txtName.Top = y; txtName.Width = 300;
            y += 36;
            Label l3 = new Label(); l3.Text = Loc.T("add.wheel"); l3.Font = new Font(Font, FontStyle.Bold); l3.Left = 12; l3.Top = y + 4; l3.AutoSize = false; l3.Width = 250; l3.Height = 30;
            // default: the device with force feedback (the wheel); without one, the device with the most buttons
            int best = -1, bestBtn = -1;
            for (int i = 0; i < devs.Length; i++) if (devs[i].ffb != 0) { best = i; break; }
            if (best < 0) for (int i = 0; i < devs.Length; i++) if (devs[i].numButtons > bestBtn) { bestBtn = devs[i].numButtons; best = i; }
            wheelPanel.Left = 270; wheelPanel.Top = y; wheelPanel.Width = 460;
            int ry = 0;
            for (int i = 0; i < devs.Length; i++)
            {
                string key = string.Format("{0:X4}:{1:X4}", devs[i].vid, devs[i].pid);
                RadioButton rb = new RadioButton(); rb.AutoSize = false; rb.Left = 0; rb.Top = ry; rb.Width = 460; rb.Height = 24;
                rb.Text = devs[i].name.Trim() + "   (" + key + ")" + (devs[i].ffb != 0 ? "   – " + Loc.T("add.ffbtag") + (i == best ? " – " + Loc.T("add.suggested") : "") : "");
                if (devs[i].ffb != 0) rb.Font = new Font(Font, FontStyle.Bold);
                rb.Checked = i == best;
                wheelPanel.Controls.Add(rb); wheelRadios.Add(rb); wheelKeys.Add(key);
                tip.SetToolTip(rb, Loc.T("tt.add.wheel"));
                ry += 24;
            }
            wheelOther.Text = Loc.T("add.wheel.other"); wheelOther.AutoSize = false; wheelOther.Left = 0; wheelOther.Top = ry; wheelOther.Width = 250; wheelOther.Height = 24;
            txtOther.Left = 255; txtOther.Top = ry + 1; txtOther.Width = 120;
            txtOther.Enter += delegate { wheelOther.Checked = true; };
            if (best < 0) wheelOther.Checked = true;
            wheelPanel.Controls.Add(wheelOther); wheelPanel.Controls.Add(txtOther);
            ry += 28; wheelPanel.Height = ry;
            y += ry + 4;
            Label wheelHint = new Label(); wheelHint.Text = Loc.T("add.wheel.hint"); wheelHint.Left = 270; wheelHint.Top = y; wheelHint.Width = 460; wheelHint.Height = 96; wheelHint.AutoSize = false;
            wheelHint.ForeColor = Color.FromArgb(150, 80, 0);
            y += 100;
            chkHide.Checked = true;                     // the other controllers are always hidden from the game and merged into the chosen one (no separate choice)
            lblInfo.Left = 12; lblInfo.Top = y; lblInfo.Width = 720; lblInfo.Height = 190; lblInfo.BorderStyle = BorderStyle.FixedSingle; lblInfo.Padding = new Padding(8);
            lblInfo.Text = Loc.T("add.pick");
            ok.Text = Loc.T("add.install"); ok.Left = 540; ok.Top = y + 200; ok.Width = 110; ok.Enabled = false; ok.Click += delegate { Install(); };
            cancel.Text = Loc.T("add.cancel"); cancel.Left = 656; cancel.Top = y + 200; cancel.Width = 76; cancel.DialogResult = DialogResult.Cancel;
            Controls.AddRange(new Control[] { l1, txtExe, browse, l2, txtName, l3, wheelPanel, wheelHint, lblInfo, ok, cancel });
            Height = y + 200 + 78;
            CancelButton = cancel;
            tip.SetToolTip(txtExe, Loc.T("tt.add.exe")); tip.SetToolTip(browse, Loc.T("tt.add.exe")); tip.SetToolTip(txtName, Loc.T("tt.add.name"));
            tip.SetToolTip(ok, Loc.T("tt.add.install"));
        }

        string ProxySource(string dll)
        {
            string arch = is64 ? "x64" : "x86";
            string[] cands = new string[] { Path.Combine(Path.Combine(Path.Combine(appDir, "data"), "proxy"), Path.Combine(arch, dll)), Path.Combine(appDir, "proxy", arch, dll), Path.Combine(appDir, "..", "src", "proxy", "out", arch, dll) };
            foreach (string c in cands) if (File.Exists(c)) return Path.GetFullPath(c);
            return null;
        }

        void Analyze()
        {
            dllName = null; ok.Enabled = false;
            string path = txtExe.Text.Trim();
            if (!File.Exists(path)) { lblInfo.Text = Loc.T("add.pick"); return; }
            PeInfo pe = PeInfo.Read(path);
            if (!pe.Valid) { lblInfo.Text = Loc.T("add.notpe"); return; }
            is64 = pe.Is64;
            string via = null;
            bool exeWinmm = pe.HasFunc("winmm.dll", "joyGetPosEx", "joyGetPos", "joyGetDevCapsA", "joyGetDevCapsW");   // the hook only reaches the exe's own imports
            string exeFolder = Path.GetDirectoryName(path);
            if (!pe.Has("dinput8.dll") && !pe.Has("dinput.dll") && !pe.HasFunc("winmm.dll", "joyGetPosEx", "joyGetPos", "joyGetDevCapsA", "joyGetDevCapsW"))
            {
                // the game's own libraries may do the input (e.g. an engine DLL): look at the DLLs in the game folder
                foreach (string f in Directory.GetFiles(exeFolder, "*.dll"))
                {
                    try
                    {
                        if (new FileInfo(f).Length > 60 * 1024 * 1024) continue;
                        PeInfo o = PeInfo.Read(f);
                        if (!o.Valid || o.Is64 != pe.Is64) continue;
                        if (o.Has("dinput8.dll") || o.Has("dinput.dll") || o.HasFunc("winmm.dll", "joyGetPosEx", "joyGetPos", "joyGetDevCapsA", "joyGetDevCapsW"))
                        {
                            foreach (KeyValuePair<string, List<string>> kv in o.Imports)
                            {
                                if (!pe.Imports.ContainsKey(kv.Key)) pe.Imports[kv.Key] = new List<string>();
                                pe.Imports[kv.Key].AddRange(kv.Value);
                            }
                            via = via == null ? Path.GetFileName(f) : via + ", " + Path.GetFileName(f);
                        }
                    }
                    catch { }
                }
            }
            usesWinmm = exeWinmm;
            string api;
            if (pe.Has("dinput8.dll")) { dllName = "dinput8.dll"; api = "DirectInput 8"; }
            else if (pe.Has("dinput.dll")) { dllName = "dinput.dll"; api = "DirectInput 1\u20137"; }
            else if (usesWinmm && pe.Has("msacm32.dll")) { dllName = "msacm32.dll"; api = "winmm (joyGetPos)"; }
            else if (usesWinmm) api = "winmm (joyGetPos)";
            else api = Loc.T("add.noapi");
            if (string.IsNullOrEmpty(txtName.Text)) txtName.Text = Path.GetFileNameWithoutExtension(path);
            string folder = Path.GetDirectoryName(path);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(Loc.T("add.machine", is64 ? "x64" : "x86"));
            sb.AppendLine(Loc.T("add.api", api + (usesWinmm && pe.Has("dinput.dll") ? " + winmm" : "")));
            if (via != null) sb.AppendLine(Loc.T("add.via", via));
            if (dllName == null) sb.AppendLine(Loc.T("add.unsupported"));
            else
            {
                string src = ProxySource(dllName);
                sb.AppendLine(Loc.T("add.dll", dllName, folder));
                if (src == null) sb.AppendLine(Loc.T("add.nosource", dllName, is64 ? "x64" : "x86"));
                else
                {
                    string target = Path.Combine(folder, dllName);
                    if (File.Exists(target)) sb.AppendLine(Loc.T("add.existing", dllName));
                    if (usesWinmm) sb.AppendLine(Loc.T("add.winmmnote"));
                    if (File.Exists(Path.Combine(folder, "dinput_proxy.ini"))) sb.AppendLine(Loc.T("add.iniexists"));
                    ok.Enabled = true;
                }
            }
            lblInfo.Text = sb.ToString();
        }

        // `CtrlMuxr.exe --addtest <out.txt>`: detection on the known games and an install into a temporary folder
        public string TestRun(string exe, string tmpFolder, bool install)
        {
            txtExe.Text = exe;
            txtName.Text = "";
            Analyze();
            string r = string.Format("{0} -> {1} dll={2} winmm={3} installable={4}", Path.GetFileName(exe), is64 ? "x64" : "x86", dllName ?? "-", usesWinmm, ok.Enabled);
            if (install && ok.Enabled)
            {
                Directory.CreateDirectory(tmpFolder);
                string copy = Path.Combine(tmpFolder, Path.GetFileName(exe));
                File.Copy(exe, copy, true);
                txtExe.Text = copy;
                Analyze();
                wheelOther.Checked = true; txtOther.Text = "346E:0002";
                Install();
                bool dllOk = File.Exists(Path.Combine(tmpFolder, dllName)), iniOk = File.Exists(Path.Combine(tmpFolder, "dinput_proxy.ini"));
                string ini = iniOk ? File.ReadAllText(Path.Combine(tmpFolder, "dinput_proxy.ini")) : "";
                r += string.Format("\n    install: dll={0} ini={1} result={2} wheel={3} hideSection={4} winmmSection={5}", dllOk, iniOk, DialogResult, Wheel,
                    ini.Contains("[Hide]"), ini.Contains("[Winmm]"));
            }
            return r;
        }

        string SelectedWheel()
        {
            for (int i = 0; i < wheelRadios.Count; i++) if (wheelRadios[i].Checked) return wheelKeys[i];
            return wheelOther.Checked ? WheelKeyOf(txtOther.Text) : null;
        }

        static string WheelKeyOf(string text)
        {
            System.Text.RegularExpressions.MatchCollection m = System.Text.RegularExpressions.Regex.Matches(text, @"[0-9A-Fa-f]{4}:[0-9A-Fa-f]{4}");
            return m.Count > 0 ? m[m.Count - 1].Value.ToUpperInvariant() : null;
        }

        void Install()
        {
            string exe = txtExe.Text.Trim(), name = txtName.Text.Trim();
            string wheel = reinstallWheel ?? SelectedWheel();
            if (name.Length == 0 || name.IndexOfAny(new char[] { '[', ']', '=' }) >= 0) { Dlg.Show(this, Loc.T("add.badname"), Text); return; }
            if (wheel == null) { Dlg.Show(this, Loc.T("add.nowheel"), Text); return; }
            string folder = Path.GetDirectoryName(exe), src = ProxySource(dllName), target = Path.Combine(folder, dllName);
            // the folder already belongs to a listed game: nothing is added (two entries would share one settings file)
            string listed = listedGameOf(folder);
            if (listed != null && listed != reinstallName) { Dlg.Show(this, Loc.T("add.listed", listed), Text); return; }
            // settings that exist already (in the folder or saved for this game): they are used as they are, with the main device they were made for
            // (the chosen wheel is ignored); when that device cannot be told, or the file is clearly for another wheel, nothing is added
            string mainNote = null;
            string[] oldIni = Store.ExistingIni(appDir, name, folder);
            if (oldIni != null)
            {
                string other = RestoreForm.MainOf(oldIni);
                if (other == null && reinstallWheel != null) other = reinstallWheel.ToUpperInvariant();      // the game is listed: its own wheel stands in for the old file's missing note
                if (other == null)
                {
                    Dlg.Show(this, Loc.T("add.cleanup", Path.Combine(folder, "dinput_proxy.ini"), Store.GameDirPath(appDir, name)), Text);
                    return;
                }
                mainNote = other == wheel.ToUpperInvariant() ? null : Loc.T("add.mainfromini", WheelTitle(other), WheelTitle(wheel));
                wheel = other;
            }
            try
            {
                if (File.Exists(target) && !FilesEqual(src, target) && !Store.IsOurProxy(target))      // only the game's own file is saved; an older CtrlMuxr proxy is simply replaced
                {
                    string bak = target + ".orig_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    File.Copy(target, bak, false);
                }
                File.Copy(src, target, true);
            }
            catch (Exception ex) { Dlg.Show(this, Loc.T("add.copyfail", ex.Message), Text); return; }
            string iniPath = Path.Combine(folder, "dinput_proxy.ini"), notice = null;
            try
            {
                bool hadIni = File.Exists(iniPath);
                string before = hadIni ? File.ReadAllText(iniPath) : null;
                Store.Sync(appDir, name, folder);                                    // an earlier installation of this game: its settings come back
                if (File.Exists(iniPath)) notice = !hadIni || File.ReadAllText(iniPath) != before ? Loc.T("add.restored", name) : Loc.T("add.iniused");
                if (!File.Exists(iniPath)) File.WriteAllText(iniPath, BuildIni(name, wheel), IniFile.Enc);
                Store.AfterSave(appDir, name, folder);
            }
            catch (Exception ex) { Dlg.Show(this, Loc.T("add.copyfail", ex.Message), Text); return; }
            if (mainNote != null) notice = notice == null ? mainNote : notice + "\n\n" + mainNote;
            if (oldIni != null && mainNote == null) notice = (notice == null ? "" : notice + "\n\n") + Loc.T("add.mainkept", WheelTitle(wheel));
            if (notice != null) Dlg.Show(this, notice, Text);
            GameName = name; Folder = folder; ExeName = Path.GetFileName(exe); Wheel = wheel;
            DialogResult = DialogResult.OK;
        }

        string WheelTitle(string key)
        {
            for (int i = 0; i < devs.Length; i++)
                if (string.Format("{0:X4}:{1:X4}", devs[i].vid, devs[i].pid) == key) return devs[i].name.Trim() + " (" + key + ")";
            return key + " " + Loc.T("add.notconnected");
        }

        static bool FilesEqual(string a, string b)
        {
            byte[] x = File.ReadAllBytes(a), y = File.ReadAllBytes(b);
            if (x.Length != y.Length) return false;
            for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) return false;
            return true;
        }

        string BuildIni(string name, string wheel)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("; dinput_proxy.ini - created by CtrlMuxr for " + name + " (" + DateTime.Now.ToString("yyyy-MM-dd") + ")");
            sb.AppendLine("; Edit with the app (Virtual device / Tuning tabs) or by hand. Reloaded while the game runs, except [Hide], [DevType], [Identity] and [Caps].");
            sb.AppendLine("[Main]");
            sb.AppendLine("; the wheel (force feedback device) these settings were made for; the proxy ignores this section");
            sb.AppendLine("Wheel=" + wheel.ToUpperInvariant());
            if (usesWinmm)
            {
                sb.AppendLine("[Winmm]");
                sb.AppendLine("; the game reads the joystick through winmm: this wheel is what it sees as joystick id 0 (its data is found by VID:PID)");
                sb.AppendLine("Enabled=1");
                sb.AppendLine("Wheel=" + wheel);
                sb.AppendLine("Id=0");
                sb.AppendLine("Source=auto");                                // the data comes from the joystick with the wheel's VID:PID, not from whatever is joystick 0
            }
            sb.AppendLine("[Hide]");
            sb.AppendLine("; devices hidden from the game (they are merged into the wheel on the Virtual device tab)");
            if (chkHide.Checked)
                for (int i = 0; i < devs.Length; i++)
                {
                    string k = string.Format("{0:X4}:{1:X4}", devs[i].vid, devs[i].pid);
                    if (k != wheel) sb.AppendLine(k + "=");
                }
            sb.AppendLine("[AxisMap]");
            sb.AppendLine("[AxisCombine]");
            sb.AppendLine("[ButtonMap]");
            sb.AppendLine("[AxisButton]");
            sb.AppendLine("[Shape]");
            sb.AppendLine("[Caps]");
            sb.AppendLine("; if buttons do not map in the game: MaxButtons=12 and ClearFlags=A (hexadecimal);  if the game has its own deadzone: IgnoreDeadzone=1");
            sb.AppendLine("[FFB]");
            sb.AppendLine("; Boost=100");
            return sb.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    struct DRDevInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string name;
        public uint vid, pid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 40)] public string guid;
        public int axisMask, numButtons;
        public int ffb;                 // 1 = reports force feedback
    }

    // TabControl with clearly outlined tabs (grey tones only): the Windows default is a pale, flat strip that is hard to read.
    class OutlinedTabControl : TabControl
    {
        static readonly Color Line = Color.FromArgb(96, 96, 96);
        static readonly Color Idle = Color.FromArgb(218, 218, 218);
        public OutlinedTabControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Padding = new Point(14, 6);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(SystemColors.Control);
            if (TabCount == 0) return;
            Rectangle first = GetTabRect(0);
            int top = first.Bottom;                                    // top edge of the page frame
            Rectangle frame = new Rectangle(0, top, Width - 1, Height - top - 1);
            using (Pen pen = new Pen(Line))
            {
                g.DrawRectangle(pen, frame);
                for (int i = 0; i < TabCount; i++)
                {
                    if (i == SelectedIndex) continue;
                    DrawTab(g, pen, i, false);
                }
                if (SelectedIndex >= 0) DrawTab(g, pen, SelectedIndex, true);   // the selected tab is drawn last, over the frame line
            }
        }
        void DrawTab(Graphics g, Pen pen, int i, bool sel)
        {
            Rectangle r = GetTabRect(i);
            if (sel) r.Inflate(2, 0);
            if (sel) { r.Y -= 2; r.Height += 3; }                       // raised, and covers the frame line beneath it
            r.Width -= 1;
            using (SolidBrush fill = new SolidBrush(sel ? SystemColors.Control : Idle)) g.FillRectangle(fill, r);
            g.DrawLine(pen, r.Left, r.Bottom - (sel ? 1 : 0), r.Left, r.Top);
            g.DrawLine(pen, r.Left, r.Top, r.Right, r.Top);
            g.DrawLine(pen, r.Right, r.Top, r.Right, r.Bottom - (sel ? 1 : 0));
            using (Font f = new Font(Font, sel ? FontStyle.Bold : FontStyle.Regular))
                TextRenderer.DrawText(g, TabPages[i].Text, f, new Rectangle(r.Left, r.Top + (sel ? 0 : 1), r.Width, r.Height), SystemColors.ControlText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
    }

    static class DevRead      // devread.dll (src\devread): DirectInput 8 access for the GUI
    {
        [DllImport("devread.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int DR_Enum([Out] DRDevInfo[] arr, int max);
        [DllImport("devread.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int DR_Open(int idx, IntPtr hwnd);
        [DllImport("devread.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int DR_AxisMask(int idx);
        [DllImport("devread.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int DR_NumButtons(int idx);
        [DllImport("devread.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int DR_Read(int idx, [Out] int[] axes, [Out] int[] pov, [Out] byte[] buttons);
        [DllImport("devread.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void DR_CloseAll();
    }

    // What the game would see, calculated from the device readings with the rules of the proxy (ApplyMaps / ShapeValue in src\proxy\core.cpp).
    // Not included: the "lead" option of [Shape] (needs the history of the movement) and the game's own axis ranges.
    static class Preview
    {
        static readonly string[] Ax = new string[] { "X", "Y", "Z", "Rx", "Ry", "Rz", "S0", "S1" };
        static int AxIdx(string n) { for (int i = 0; i < Ax.Length; i++) if (string.Equals(Ax[i], n, StringComparison.OrdinalIgnoreCase)) return i; return -1; }

        static bool AxRef(string r, out string dev, out int ax)
        {
            dev = null; ax = -1;
            int d = r.IndexOf('.');
            if (d <= 0) return false;
            dev = r.Substring(0, d).ToUpperInvariant(); ax = AxIdx(r.Substring(d + 1));
            return ax >= 0;
        }

        static int Clamp(double v) { return (int)(v < 0 ? 0 : v > 65535 ? 65535 : v); }

        // axesOf / btnOf: the current readings of a device (axes 0..65535, buttons 0x80 = pressed), null when it cannot be read
        public static void Compute(IniFile ini, string dest, Func<string, int[]> axesOf, Func<string, byte[]> btnOf, int[] ax, byte[] btn, bool[] has)
        {
            dest = dest.ToUpperInvariant();
            int[] d = axesOf(dest); byte[] db = btnOf(dest);
            for (int a = 0; a < 8; a++) { ax[a] = d != null ? d[a] : 32767; has[a] = d != null; }
            for (int b = 0; b < 128; b++) btn[b] = db != null ? db[b] : (byte)0;
            // [Mask]: the axis is held at its neutral value
            foreach (string t in (ini.Get("Mask", dest) ?? "").Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int a = AxIdx(t); if (a < 0) continue;
                int n; ax[a] = int.TryParse(ini.Get("Neutral", dest + "." + Ax[a]) ?? "", out n) ? n : 32767; has[a] = true;
            }
            foreach (string k in ini.Keys("AxisMap"))
            {
                if (!k.StartsWith(dest + ".", StringComparison.OrdinalIgnoreCase)) continue;
                int da = AxIdx(k.Substring(dest.Length + 1));
                string[] tk = (ini.Get("AxisMap", k) ?? "").Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                string sd; int sa;
                if (da < 0 || tk.Length == 0 || !AxRef(tk[0], out sd, out sa)) continue;
                int[] s = axesOf(sd);
                if (s == null) { has[da] = false; continue; }
                double v = s[sa]; bool inv = false; int half = 0;
                for (int i = 1; i < tk.Length; i++)
                {
                    string w = tk[i].ToLowerInvariant();
                    if (w == "invert") inv = true; else if (w == "half") half = 1; else if (w == "halfneg") half = 2; else if (w == "splithi") half = 3; else if (w == "splitlo") half = 4;
                }
                if (inv) v = 65535 - v;
                if (half == 1) v = (int)(32767.5 + v / 2.0);
                else if (half == 2) v = (int)(32767.5 - v / 2.0);
                else if (half == 3) v = Clamp((v - 32767.5) * 2.0);
                else if (half == 4) v = Clamp((32767.5 - v) * 2.0);
                ax[da] = (int)v; has[da] = true;
            }
            // a game button can have several sources ([ButtonMap] and [AxisButton]): it is pressed while any of them is
            foreach (string sec in new string[] { "ButtonMap", "AxisButton" })
                foreach (string k in ini.Keys(sec))
                {
                    int n;
                    if (k.StartsWith(dest + ".B", StringComparison.OrdinalIgnoreCase) && int.TryParse(k.Substring(dest.Length + 2), out n) && n >= 1 && n <= 128) btn[n - 1] = 0;
                }
            foreach (string k in ini.Keys("ButtonMap"))
            {
                int n;
                if (!k.StartsWith(dest + ".B", StringComparison.OrdinalIgnoreCase) || !int.TryParse(k.Substring(dest.Length + 2), out n) || n < 1 || n > 128) continue;
                foreach (string v in (ini.Get("ButtonMap", k) ?? "").Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int m, dot = v.IndexOf('.');
                    if (dot <= 1 || v.Length < dot + 3 || !int.TryParse(v.Substring(dot + 2), out m) || m < 1 || m > 128) continue;
                    byte[] sb = btnOf(v.Substring(0, dot).ToUpperInvariant());
                    if (sb != null && sb[m - 1] != 0) btn[n - 1] = 0x80;
                }
            }
            foreach (string k in ini.Keys("AxisButton"))
            {
                if (!k.StartsWith(dest + ".B", StringComparison.OrdinalIgnoreCase)) continue;
                int n; string[] tk = (ini.Get("AxisButton", k) ?? "").Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                string sd; int sa;
                if (!int.TryParse(k.Substring(dest.Length + 2), out n) || n < 1 || n > 128 || tk.Length == 0 || !AxRef(tk[0], out sd, out sa)) continue;
                int[] s = axesOf(sd); if (s == null) continue;
                double thr = 32768; bool inv = false;
                for (int i = 1; i < tk.Length; i++) { double x; if (string.Equals(tk[i], "invert", StringComparison.OrdinalIgnoreCase)) inv = true; else if (double.TryParse(tk[i], NumberStyles.Float, CultureInfo.InvariantCulture, out x)) thr = x; }
                if (inv ? s[sa] <= thr : s[sa] >= thr) btn[n - 1] = 0x80;
            }
            foreach (string k in ini.Keys("AxisCombine"))
            {
                if (!k.StartsWith(dest + ".", StringComparison.OrdinalIgnoreCase)) continue;
                int da = AxIdx(k.Substring(dest.Length + 1));
                string[] tk = (ini.Get("AxisCombine", k) ?? "").Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                string d1, d2; int a1, a2;
                if (da < 0 || tk.Length < 2 || !AxRef(tk[0], out d1, out a1) || !AxRef(tk[1], out d2, out a2)) continue;
                int[] s1 = axesOf(d1), s2 = axesOf(d2);
                if (s1 == null || s2 == null) { has[da] = false; continue; }
                bool inv = tk.Length > 2 && string.Equals(tk[2], "invert", StringComparison.OrdinalIgnoreCase);
                double g = s1[a1], b = s2[a2];
                ax[da] = Clamp(32767.5 + (inv ? (g - b) : (b - g)) / 2 + 0.5); has[da] = true;
            }
            for (int a = 0; a < 8; a++)          // [Shape] runs last, on the final value
            {
                string sh = ini.Get("Shape", dest + "." + Ax[a]);
                if (string.IsNullOrEmpty(sh)) continue;
                double dz = 0, expo = 0, gain = 1, adz = 0, center = 32767.5; bool inv = false;
                foreach (string t in sh.Split(new char[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = t.IndexOf('='); double x = 0;
                    if (eq > 0) double.TryParse(t.Substring(eq + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out x);
                    string key = eq > 0 ? t.Substring(0, eq).ToLowerInvariant() : t.ToLowerInvariant();
                    if (key == "invert") inv = true; else if (key == "dz") dz = x / 100; else if (key == "expo") expo = x / 100; else if (key == "gain") gain = x / 100; else if (key == "adz") adz = x / 100; else if (key == "center") center = x;
                }
                double nn = (ax[a] - center) / 32767.5;
                if (nn > 1) nn = 1; else if (nn < -1) nn = -1;
                if (inv) nn = -nn;
                double sg = nn < 0 ? -1 : 1, am = nn < 0 ? -nn : nn;
                if (dz > 0) am = am < dz ? 0 : (am - dz) / (1 - dz);
                if (expo > 0) am = (1 - expo) * am + expo * am * am * am;
                am *= gain; if (am > 1) am = 1;
                if (adz > 0 && am > 0) am = adz + am * (1 - adz);
                ax[a] = Clamp(center + sg * am * 32767.5 + 0.5);
            }
        }
    }

    class Bar : Control      // horizontal value bar with a centre mark
    {
        double v;
        bool dim;
        public double Value { get { return v; } set { if (value != v) { v = value; Invalidate(); } } }
        // Dim = the axis does not exist / has no value: grey background, no fill (the same look as a greyed-out button)
        public bool Dim { get { return dim; } set { if (value != dim) { dim = value; Invalidate(); } } }
        public Bar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            Height = 18;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (dim)
            {
                e.Graphics.Clear(Color.FromArgb(236, 236, 236));
                e.Graphics.DrawLine(Pens.Gainsboro, Width / 2, 0, Width / 2, Height);
                e.Graphics.DrawRectangle(Pens.Gainsboro, 0, 0, Width - 1, Height - 1);
                return;
            }
            e.Graphics.Clear(Color.White);
            int w = (int)((Width - 1) * Math.Max(0, Math.Min(1, v)));
            using (Brush b = new SolidBrush(Color.FromArgb(70, 130, 220))) e.Graphics.FillRectangle(b, 0, 0, w, Height - 1);
            e.Graphics.DrawLine(Pens.Silver, Width / 2, 0, Width / 2, Height);
            e.Graphics.DrawRectangle(Pens.Gray, 0, 0, Width - 1, Height - 1);
        }
    }

    // An earlier state of a game's dinput_proxy.ini: an automatic backup (dinput_proxy.ini.bak_<time>) or a named profile (dinput_proxy.profile_<name>.ini).
    class RsEntry
    {
        public string Path, Kind, Label;     // Kind: "backup" | "profile"
        public DateTime Content;             // when the file's content was last changed (a backup keeps the time of the state it copied)
        public string[] Lines;
    }

    // Lists the backups and profiles of one game, shows what restoring one would change and saves/deletes profiles.
    class RestoreForm : Form
    {
        readonly string folder;
        readonly string[] now;               // the settings as they are on screen
        readonly string wheel;               // the game's main device (VID:PID)
        readonly Func<string, string> title; // device name for a VID:PID
        public string[] ChosenLines;         // set when the user pressed Restore
        ListView list = new ListView();
        TextBox diffBox = new TextBox();
        Button restoreBtn = new Button(), saveBtn = new Button(), delBtn = new Button(), closeBtn = new Button();
        List<RsEntry> entries = new List<RsEntry>();

        public static string ProfilePath(string folder, string name) { return System.IO.Path.Combine(folder, "dinput_proxy.profile_" + name + ".ini"); }

        public static bool ValidName(string n)
        {
            if (string.IsNullOrWhiteSpace(n) || n.Trim() != n || n.Length > 40) return false;
            foreach (char c in n) if (!(char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_')) return false;
            return true;
        }

        public static List<RsEntry> Scan(string folder)
        {
            List<RsEntry> r = new List<RsEntry>();
            try
            {
                foreach (string f in Directory.GetFiles(folder, "dinput_proxy.ini.bak_*"))
                {
                    RsEntry e = new RsEntry(); e.Path = f; e.Kind = "backup"; e.Content = File.GetLastWriteTime(f);
                    string stamp = System.IO.Path.GetFileName(f).Substring("dinput_proxy.ini.bak_".Length);
                    DateTime t;
                    e.Label = DateTime.TryParseExact(stamp, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t) ? t.ToString("d.M.yyyy HH:mm:ss") : stamp;
                    e.Lines = File.ReadAllLines(f, IniFile.Enc);
                    r.Add(e);
                }
                r.Sort(delegate(RsEntry a, RsEntry b) { return string.Compare(b.Path, a.Path, StringComparison.OrdinalIgnoreCase); });     // newest backup first
                List<RsEntry> p = new List<RsEntry>();
                foreach (string f in Directory.GetFiles(folder, "dinput_proxy.profile_*.ini"))
                {
                    RsEntry e = new RsEntry(); e.Path = f; e.Kind = "profile"; e.Content = File.GetLastWriteTime(f);
                    string n = System.IO.Path.GetFileName(f);
                    e.Label = n.Substring("dinput_proxy.profile_".Length, n.Length - "dinput_proxy.profile_".Length - ".ini".Length);
                    e.Lines = File.ReadAllLines(f, IniFile.Enc);
                    p.Add(e);
                }
                p.Sort(delegate(RsEntry a, RsEntry b) { return string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase); });
                r.InsertRange(0, p);                       // profiles first, then backups
            }
            catch { }
            return r;
        }

        // the lines that carry settings: "[Section] line"; comments, blank lines and [Names] (written by the app) are ignored
        static List<string> Significant(string[] lines)
        {
            List<string> r = new List<string>();
            string sec = "";
            foreach (string raw in lines)
            {
                string l = raw.Trim();
                if (l.Length == 0 || l.StartsWith(";")) continue;
                if (l.StartsWith("[") && l.EndsWith("]")) { sec = l.Substring(1, l.Length - 2); continue; }
                if (string.Equals(sec, "Names", StringComparison.OrdinalIgnoreCase) || string.Equals(sec, "Main", StringComparison.OrdinalIgnoreCase)) continue;       // [Main] is a note about the origin (see MainOf), not a setting
                r.Add("[" + sec + "] " + l);
            }
            return r;
        }

        // the main device (the wheel the force feedback comes from) a state was made for: [Main] Wheel, or [Winmm] Wheel; null = not recorded
        public static string MainOf(string[] lines)
        {
            IniFile f = new IniFile(); f.Lines.AddRange(lines);
            string w = f.Get("Main", "Wheel");
            if (string.IsNullOrEmpty(w)) w = f.Get("Winmm", "Wheel");
            return string.IsNullOrEmpty(w) ? null : w.ToUpperInvariant();
        }

        // true when the state is known to be made for another main device than `wheel`: a recorded one differs, or (nothing recorded) the wheel is hidden in it
        public static bool OtherMain(string[] lines, string wheel)
        {
            if (string.IsNullOrEmpty(wheel)) return false;
            string m = MainOf(lines);
            if (m != null) return m != wheel.ToUpperInvariant();
            IniFile f = new IniFile(); f.Lines.AddRange(lines);
            foreach (string k in f.Keys("Hide")) if (string.Equals(k, wheel, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // true when two states give the game the same settings (comments, blank lines, order and [Names] do not count)
        public static bool Same(string[] a, string[] b) { int n; Diff(a, b, out n); return n == 0; }

        // what restoring `other` over `now` would change; count = number of differing lines
        public static string Diff(string[] now, string[] other, out int count)
        {
            List<string> a = Significant(now), b = Significant(other), add = new List<string>(), rem = new List<string>(a);
            foreach (string l in b) { if (!rem.Remove(l)) add.Add(l); }
            count = add.Count + rem.Count;
            if (count == 0) return Loc.T("rs.diff.none");
            StringBuilder sb = new StringBuilder();
            if (add.Count > 0) { sb.AppendLine(Loc.T("rs.diff.add")); foreach (string l in add) sb.AppendLine("  + " + l); sb.AppendLine(); }
            if (rem.Count > 0) { sb.AppendLine(Loc.T("rs.diff.remove")); foreach (string l in rem) sb.AppendLine("  − " + l); }
            return sb.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n");
        }

        public RestoreForm(string folder, string[] now, string gameName, string wheel, Func<string, string> title)
        {
            this.folder = folder; this.now = now; this.wheel = wheel; this.title = title;
            Text = Loc.T("rs.title", gameName);
            Width = 1100; Height = 560; MinimumSize = new Size(700, 400); StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9f);
            Label intro = new Label(); intro.Text = Loc.T("rs.intro"); intro.Dock = DockStyle.Top; intro.Height = 70; intro.Padding = new Padding(8, 8, 8, 0); intro.ForeColor = SystemColors.GrayText;
            list.Dock = DockStyle.Left; list.Width = 570; list.ShowGroups = true; list.View = View.Details; list.FullRowSelect = true; list.MultiSelect = false; list.HideSelection = false;
            list.Columns.Add(Loc.T("rs.col.name"), 200); list.Columns.Add(Loc.T("rs.col.date"), 135); list.Columns.Add(Loc.T("rs.col.main"), 215);
            diffBox.Dock = DockStyle.Fill; diffBox.Multiline = true; diffBox.ReadOnly = true; diffBox.ScrollBars = ScrollBars.Both; diffBox.WordWrap = false; diffBox.Font = new Font("Consolas", 9f);
            FlowLayoutPanel bp = new FlowLayoutPanel(); bp.Dock = DockStyle.Bottom; bp.Height = 42; bp.Padding = new Padding(6, 6, 6, 0); bp.FlowDirection = FlowDirection.LeftToRight;
            restoreBtn.Text = Loc.T("rs.restore"); saveBtn.Text = Loc.T("rs.saveprofile"); delBtn.Text = Loc.T("rs.delete"); closeBtn.Text = Loc.T("rs.close");
            foreach (Button b in new Button[] { restoreBtn, saveBtn, delBtn, closeBtn }) { b.AutoSize = true; b.Height = 28; bp.Controls.Add(b); }
            Controls.Add(diffBox); Controls.Add(list); Controls.Add(bp); Controls.Add(intro);
            list.SelectedIndexChanged += delegate { ShowSelected(); };
            restoreBtn.Click += delegate
            {
                RsEntry e = Selected(); if (e == null || e.Kind == "current") return;
                if (OtherMain(e.Lines, wheel) && Dlg.Show(this, Loc.T("rs.othermain", MainText(e.Lines), title(wheel)), "CtrlMuxr", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                ChosenLines = e.Lines; DialogResult = DialogResult.OK; Close();
            };
            saveBtn.Click += delegate { SaveProfile(); };
            delBtn.Click += delegate { DeleteProfile(); };
            closeBtn.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            CancelButton = closeBtn;
            Fill(null);
        }

        RsEntry Selected() { return list.SelectedItems.Count == 0 ? null : list.SelectedItems[0].Tag as RsEntry; }

        // the main device column: the device's name, or "not recorded"
        string MainText(string[] lines)
        {
            string m = MainOf(lines);
            return m == null ? Loc.T("rs.main.unknown") : title(m) + " (" + m + ")";
        }

        void Fill(string selectLabel)
        {
            entries = Scan(folder);
            list.Items.Clear(); list.Groups.Clear();
            ListViewGroup gc = new ListViewGroup(Loc.T("rs.group.current")), gp = new ListViewGroup(Loc.T("rs.group.profile")), gb = new ListViewGroup(Loc.T("rs.group.backup"));
            list.Groups.Add(gc); list.Groups.Add(gp); list.Groups.Add(gb);
            RsEntry curE = new RsEntry(); curE.Kind = "current"; curE.Lines = now;      // the state on screen: not a file, cannot be restored
            ListViewItem ci = new ListViewItem(Loc.T("rs.current.name"));
            ci.SubItems.Add(""); ci.SubItems.Add(MainText(now));
            ci.UseItemStyleForSubItems = false; ci.Tag = curE; ci.Group = gc; ci.Font = new Font(list.Font, FontStyle.Bold);
            list.Items.Add(ci);
            bool anyProfile = false;
            foreach (RsEntry e in entries)
            {
                bool profile = e.Kind == "profile";
                int diff; Diff(now, e.Lines, out diff);
                string name = profile ? e.Label : Loc.T("rs.backup.name", e.Label);
                ListViewItem it = new ListViewItem(diff == 0 ? name + "  " + Loc.T("rs.inuse") : name);
                it.SubItems.Add(e.Content.ToString("d.M.yyyy HH:mm:ss"));
                it.SubItems.Add(MainText(e.Lines));
                if (OtherMain(e.Lines, wheel)) it.SubItems[2].ForeColor = Color.Firebrick;
                it.UseItemStyleForSubItems = false;
                it.Tag = e; it.Group = profile ? gp : gb;
                if (diff == 0) it.Font = new Font(list.Font, FontStyle.Bold);
                list.Items.Add(it);
                if (profile) anyProfile = true;
                if (selectLabel != null && profile && e.Label == selectLabel) it.Selected = true;
            }
            if (!anyProfile) { ListViewItem ph = new ListViewItem(Loc.T("rs.noprofiles")); ph.ForeColor = SystemColors.GrayText; ph.Group = gp; list.Items.Add(ph); }
            if (list.SelectedItems.Count == 0) foreach (ListViewItem it in list.Items) if (it.Tag is RsEntry) { it.Selected = true; break; }
            ShowSelected();
        }

        void ShowSelected()
        {
            RsEntry e = Selected();
            restoreBtn.Enabled = e != null && e.Kind != "current";
            delBtn.Enabled = e != null && e.Kind == "profile";
            if (e == null) { diffBox.Text = entries.Count == 0 ? Loc.T("rs.empty") : ""; return; }
            diffBox.WordWrap = e.Kind == "current";            // the note is a long sentence; the diff lines are not wrapped
            if (e.Kind == "current")
            {
                StringBuilder cs = new StringBuilder(Loc.T("rs.current.info")).AppendLine().AppendLine();
                foreach (string l in Significant(now)) cs.AppendLine(l);
                diffBox.Text = cs.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n");
                return;
            }
            int n;
            diffBox.Text = Diff(now, e.Lines, out n);
        }

        void SaveProfile()
        {
            string name = Prompt(Loc.T("rs.prompt.title"), Loc.T("rs.prompt.text"));
            if (name == null) return;
            name = name.Trim();
            if (!ValidName(name)) { Dlg.Show(this, Loc.T("rs.badname"), "CtrlMuxr"); return; }
            string path = ProfilePath(folder, name);
            foreach (RsEntry pe in entries)
                if (pe.Kind == "profile" && !string.Equals(pe.Label, name, StringComparison.OrdinalIgnoreCase) && Same(now, pe.Lines))
                    if (Dlg.Show(this, Loc.T("rs.same", pe.Label, name), "CtrlMuxr", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return; else break;
            if (File.Exists(path) && Dlg.Show(this, Loc.T("rs.exists", name), "CtrlMuxr", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try { File.WriteAllLines(path, now, IniFile.Enc); }
            catch (Exception ex) { Dlg.Show(this, Loc.T("msg.savefail") + ex.Message, "CtrlMuxr"); return; }
            Fill(name);
        }

        void DeleteProfile()
        {
            RsEntry e = Selected();
            if (e == null || e.Kind != "profile") return;
            if (Dlg.Show(this, Loc.T("rs.delete.confirm", e.Label), "CtrlMuxr", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try { File.Delete(e.Path); } catch { }
            Fill(null);
        }

        string Prompt(string title, string text)
        {
            using (Form f = new Form())
            {
                f.Text = title; f.Width = 420; f.Height = 150; f.StartPosition = FormStartPosition.CenterParent; f.FormBorderStyle = FormBorderStyle.FixedDialog; f.MaximizeBox = false; f.MinimizeBox = false;
                Label l = new Label(); l.Text = text; l.Left = 10; l.Top = 10; l.Width = 390; l.Height = 32;
                TextBox t = new TextBox(); t.Left = 10; t.Top = 44; t.Width = 385;
                Button ok = new Button(); ok.Text = Loc.T("rs.ok"); ok.Left = 230; ok.Top = 76; ok.DialogResult = DialogResult.OK;
                Button cancel = new Button(); cancel.Text = Loc.T("rs.cancel"); cancel.Left = 315; cancel.Top = 76; cancel.DialogResult = DialogResult.Cancel;
                f.Controls.Add(l); f.Controls.Add(t); f.Controls.Add(ok); f.Controls.Add(cancel);
                f.AcceptButton = ok; f.CancelButton = cancel;
                return f.ShowDialog(this) == DialogResult.OK ? t.Text : null;
            }
        }
    }

    class MainForm : Form
    {
        readonly string appDir = AppDomain.CurrentDomain.BaseDirectory;
        List<GameDef> games = new List<GameDef>();
        ListBox gameList = new ListBox();
        Label status = new Label(); Button folderBtn = new Button();
        OutlinedTabControl tabs = new OutlinedTabControl();
        // steering tab
        ComboBox axisBox = new NoWheelCombo();
        CheckBox invertBox = new CheckBox();
        TrackBar[] bars = new TrackBar[6];
        NumericUpDown[] nums = new NumericUpDown[6];
        // raw ini tab / log tab
        TextBox rawBox = new TextBox();
        TextBox logBox = new TextBox();
        System.Windows.Forms.Timer pollTimer = new System.Windows.Forms.Timer();
        System.Windows.Forms.Timer saveTimer = new System.Windows.Forms.Timer();
        bool loading;
        IniFile ini;                                      // working copy of the selected game's INI (the file itself when saved)
        GameDef cur;
        // Safety: a running game is edited live (saved ~0.3 s after each change); a game that is not running is only edited in `ini`
        // (a draft) and written when the user presses Save. Every write keeps a backup and an undo step.
        bool dirty;
        Label banner = new Label();
        Panel actionBar = new Panel();
        Label barText = new Label();
        Button saveBtn = new Button(), discardBtn = new Button(), undoBtn = new Button(), backupBtn = new Button(), exBtn = new Button();
        Button updateProxyBtn = new Button();                // next to the outdated-proxy warning
        Label warnLbl = new Label();                      // notes that can explain a missing force feedback (e.g. Steam is running)
        bool steamRunning;
        List<string[]> undoStack = new List<string[]>();  // earlier file contents of the selected game (newest last)
        List<DateTime> undoWhen = new List<DateTime>();   // time of the save that pushed each undoStack entry (same index)
        DateTime undoneTo = DateTime.MinValue;            // set by UndoLast: the file is back to its state before the save made at this time
        string undoGame, backupGame;                      // game the undo stack / the session backup belong to
        DateTime lastSaved = DateTime.MinValue;
        bool pollSeen;                                    // the first process scan has been applied
        ComboBox shapeDevBox = new NoWheelCombo();
        public static bool Restart;                       // set when the language changes: Main re-creates the window
        ComboBox langBox = new NoWheelCombo();
        List<string> langCodes = new List<string>();
        Label vdSaveInfo = new Label();
        Button addBtn = new Button(), removeBtn = new Button(), uninstallBtn = new Button(), helpBtn = new Button();
        Button refreshBtn, iniSaveBtn, iniReloadBtn;
        // virtual device tab
        TabPage vdTab = new TabPage(Loc.T("tab.virtual"));
        // a property, not a static field: a static initializer can run before the language file is loaded and would store the raw keys
        static string[] ModeNames { get { return new string[] { Loc.T("mode.direct"), Loc.T("mode.half"), Loc.T("mode.halfneg"), Loc.T("mode.splithi"), Loc.T("mode.splitlo"), Loc.T("mode.combine"), Loc.T("mode.mask"), Loc.T("mode.digital") }; } }
        static readonly string[] ModeToks = new string[] { "", "half", "halfneg", "splithi", "splitlo", "combine", "mask", "digital" };
        const int ModeCombine = 5, ModeMask = 6, ModeDigital = 7;          // mask is written to [Mask], digital to [AxisButton] (+ a [Gui] link), the others to [AxisMap] / [AxisCombine]
        Label vdModeDesc = new Label(), presetDesc = new Label();
        Button presetApplyBtn = new Button();
        ComboBox[] vdSrc = new ComboBox[8], vdMode = new ComboBox[8], vdSrc2 = new ComboBox[8];
        CheckBox[] vdInv = new CheckBox[8];
        Bar[] vdBar = new Bar[8];
        Label[] vdVal = new Label[8], vdRange = new Label[8];
        Label[] vdBtn = new Label[128];
        Label vdRangeHead;                                          // "Range": shown only while the game runs (live min/max)
        Label[] vdEcho = new Label[8];                              // "-> X" next to the live bar; grey while the row has no source (held at the centre)
        ComboBox[] vdBtnNo = new ComboBox[8];                       // digital mode: destination button B1..B32 (index 0 = not chosen)
        NumericUpDown[] vdThr = new NumericUpDown[8];               // digital mode: threshold, % of the source axis
        Panel[] vdDig = new Panel[8];
        // buttons tab ([ButtonMap]): 48 rows exist (the proxy takes 48 [ButtonMap] sources; axis -> digital has its own limit), the selector shows 8, 16, 32 or 48 of them (a view setting only)
        const int BtnRows = 48;
        TabPage btnTab = new TabPage(Loc.T("tab.buttons"));
        ComboBox[] bmOut = new ComboBox[BtnRows], bmSrc = new ComboBox[BtnRows];
        Label[] bmLed = new Label[BtnRows];                 // green while the row's button is pressed
        Label[] bmNum = new Label[BtnRows];
        Label[] bmWarn = new Label[BtnRows], vdWarn = new Label[8];      // "!" marks: the same game button on a Digital row and on a Buttons row
        ComboBox btnRowsBox = new NoWheelCombo();
        int btnRowsWant = 8, btnRowsShown = 8;              // rows the user chose / rows shown (rows in use are always shown)
        bool btnRowsLoading;
        string btnTitlesSig;                               // the source list the rows hold now: refilled only when it changes (it rarely does between games)
        TableLayoutPanel btnTable;
        int btnRowsApplied = -1;
        Button btnClear = new Button();
        HashSet<int> btnOpened = new HashSet<int>();
        DRDevInfo[] btnDevRef;
        List<string> btnSrcKeys = new List<string>();
        CheckBox chkOnlyNamed = new CheckBox();
        Label bmInfo = new Label();
        Label vdInfo = new Label();
        ComboBox presetBox = new NoWheelCombo();
        List<string> srcKeys = new List<string>();
        bool vdLoading;
        LiveReader live = new LiveReader();
        System.Windows.Forms.Timer vdTimer = new System.Windows.Forms.Timer();
        System.Windows.Forms.Timer vdSaveTimer = new System.Windows.Forms.Timer();
        List<string> shapeDevKeys = new List<string>();
        TabPage logTab;
        static readonly string[] AxisNames = new string[] { "X", "Y", "Z", "Rx", "Ry", "Rz", "S0", "S1" };
        // devices tab
        TabPage devTab = new TabPage(Loc.T("tab.devices"));
        ComboBox devBox = new NoWheelCombo();
        Label devInfo = new Label(), movedLabel = new Label(), btnLast = new Label();
        DRDevInfo[] devInfos = new DRDevInfo[0];
        bool devReadFailed;                                  // devread.dll could not be used: then no device is marked as missing
        int devIdx = -1, devReads, lastBtn = -1, devButtons;
        Label[] axLabel = new Label[8];
        Bar[] axBar = new Bar[8];
        Label[] axVal = new Label[8];
        TextBox[] axText = new TextBox[8];
        Label[] btnCell = new Label[128];
        TextBox btnName = new TextBox();
        ToolTip tip = new ToolTip();
        int[] axVals = new int[8], axPrev = new int[8], povs = new int[4];
        byte[] btns = new byte[128], btnPrev = new byte[128];
        DateTime[] axMoved = new DateTime[8];
        bool loadingNames;
        IniFile names;
        System.Windows.Forms.Timer devTimer = new System.Windows.Forms.Timer();
        System.Windows.Forms.Timer nameSaveTimer = new System.Windows.Forms.Timer();

        // name, unit, min, max, decimals, scale (trackbar ticks per unit)
        // a property, not a static field: a static initializer runs before the language file is loaded and would store the raw keys
        static readonly double[] ParamDefaults = new double[] { 0, 0, 100, 0, 0, 32768 };
        static object[][] Params
        {
            get
            {
                return new object[][]
                {
                    new object[] { Loc.T("p.dz"), "%", 0.0, 50.0, 1, 10 },
                    new object[] { Loc.T("p.expo"), "%", 0.0, 100.0, 0, 10 },
                    new object[] { Loc.T("p.gain"), "%", 50.0, 200.0, 0, 10 },
                    new object[] { Loc.T("p.adz"), "%", 0.0, 50.0, 1, 10 },
                    new object[] { Loc.T("p.lead"), "s", 0.0, 0.6, 2, 1000 },
                    new object[] { Loc.T("p.center"), "", 0.0, 65535.0, 0, 1 },
                };
            }
        }

        public MainForm()
        {
            Text = Loc.T("app.title");
            try { Version v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version; int i = Text.IndexOf("CtrlMuxr"); if (i == 0) Text = "CtrlMuxr " + v.Major + "." + v.Minor + Text.Substring(8); } catch { }      // the version (from AssemblyVersion) in the title
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }      // the exe carries the CtrlMuxr icon
            Width = 1240; Height = 700; MinimumSize = new Size(900, 560);
            Font = new Font("Segoe UI", 9f);

            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill; split.SplitterDistance = 230; split.FixedPanel = FixedPanel.Panel1;
            Controls.Add(split);

            Label gl = new Label(); gl.Text = Loc.T("list.games"); gl.Dock = DockStyle.Top; gl.Height = 24; gl.Padding = new Padding(4, 6, 0, 0);
            gameList.Dock = DockStyle.Fill; gameList.IntegralHeight = false;
            gameList.SelectedIndexChanged += delegate { SelectGame(); };
            split.Panel1.Controls.Add(gameList);
            split.Panel1.Controls.Add(gl);
            removeBtn.Text = Loc.T("game.remove"); removeBtn.Dock = DockStyle.Bottom; removeBtn.Height = 28;
            removeBtn.Click += delegate { RemoveFromList(); };
            uninstallBtn.Text = Loc.T("game.uninstall"); uninstallBtn.Dock = DockStyle.Bottom; uninstallBtn.Height = 28;
            uninstallBtn.Click += delegate { if (cur != null && !cur.Running && !Store.HasProxy(cur.Folder)) AddGame(cur); else UninstallProxy(); };
            addBtn.Text = Loc.T("game.add"); addBtn.Dock = DockStyle.Bottom; addBtn.Height = 30;
            addBtn.Click += delegate { AddGame(); };
            helpBtn.Text = Loc.T("game.help"); helpBtn.Dock = DockStyle.Bottom; helpBtn.Height = 28;
            helpBtn.Click += delegate { OpenHelp(); };
            KeyPreview = true;
            KeyDown += delegate(object ks, KeyEventArgs ke) { if (ke.KeyCode == Keys.F1) { OpenHelp(); ke.Handled = true; } };
            Panel lp = new Panel(); lp.Dock = DockStyle.Bottom; lp.Height = 58;
            Label ll = new Label(); ll.Text = Loc.T("lang.label"); ll.Left = 6; ll.Top = 6; ll.AutoSize = true;
            langBox.DropDownStyle = ComboBoxStyle.DropDownList; langBox.Left = 6; langBox.Top = 26; langBox.Width = 170;
            lp.Controls.Add(ll); lp.Controls.Add(langBox);
            Panel gap = new Panel(); gap.Dock = DockStyle.Bottom; gap.Height = 16;       // separates the harmless buttons from the destructive ones
            // docking: the control added last sits lowest, so top -> bottom: Add game, Help, gap, Uninstall, Remove, language
            split.Panel1.Controls.Add(addBtn);
            split.Panel1.Controls.Add(helpBtn);
            split.Panel1.Controls.Add(gap);
            split.Panel1.Controls.Add(uninstallBtn);
            split.Panel1.Controls.Add(removeBtn);
            split.Panel1.Controls.Add(lp);
            FillLanguages();
            Load += delegate { split.SplitterDistance = 190; };      // set before the form has its size it is clamped to the default width (~130 px); the tabs are laid out for ~1050 px, so the window is 60 px wider

            status.Dock = DockStyle.Top; status.Height = 24; status.Padding = new Padding(6, 4, 6, 0);
            banner.Dock = DockStyle.Top; banner.Height = 34; banner.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
            banner.TextAlign = ContentAlignment.MiddleLeft; banner.Padding = new Padding(6, 0, 0, 0);
            BuildActionBar();
            warnLbl.Dock = DockStyle.Top; warnLbl.Height = 22; warnLbl.Padding = new Padding(8, 3, 8, 0); warnLbl.ForeColor = Color.FromArgb(150, 80, 0); warnLbl.Visible = false;
            updateProxyBtn.Text = Loc.T("warn.update"); updateProxyBtn.AutoSize = true; updateProxyBtn.Height = 22; updateProxyBtn.Top = 1; updateProxyBtn.Visible = false;
            updateProxyBtn.Click += delegate { UpdateOutdatedProxy(); };
            tip.SetToolTip(updateProxyBtn, Loc.T("warn.update.tt"));
            warnLbl.Controls.Add(updateProxyBtn);
            warnLbl.Resize += delegate { updateProxyBtn.Left = Math.Max(0, warnLbl.ClientSize.Width - updateProxyBtn.Width - 12); };
            tabs.Dock = DockStyle.Fill;
            split.Panel2.Controls.Add(tabs);
            BuildStartPanel();
            split.Panel2.Controls.Add(startPanel);
            split.Panel2.Controls.Add(actionBar);
            split.Panel2.Controls.Add(warnLbl);
            split.Panel2.Controls.Add(status);
            status.Visible = false;                                  // the folder / DLL / INI line is shown in the action bar (UpdateBar)
            split.Panel2.Controls.Add(banner);

            names = IniFile.Load(Path.Combine(appDir, "devices.ini"));
            BuildDevicesTab();
            BuildVirtualTab();
            BuildButtonsTab();
            BuildSteeringTab();
            BuildAdvancedTab();
            BuildRawTab();
            BuildLogTab();

            pollTimer.Interval = 1000; pollTimer.Tick += delegate { PollRunning(); RefreshLog(); }; pollTimer.Start();
            saveTimer.Interval = 300; saveTimer.Tick += delegate { saveTimer.Stop(); SaveShape(); };

            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (!ConfirmLeave()) { Restart = false; e.Cancel = true; return; }
                try { DevRead.DR_CloseAll(); live.Close(); } catch { }
            };
            LoadGames();
            if (!Settings.ReadOnly) foreach (GameDef g in games) Store.Sync(appDir, g.Name, g.Folder);
            foreach (GameDef g in games) gameList.Items.Add(g);
            gameList.SelectedIndex = -1;          // no game is selected at start: a game is only edited when it was chosen on purpose (or it is the one running)
            ApplyTooltips();
            UpdateStatus();
            PollRunning();
        }

        // ---------------------------------------------------------------- games list
        void LoadGames()
        {
            string path = Path.Combine(appDir, "games.ini");
            if (!File.Exists(path)) WriteDefaultGames(path);
            IniFile f = IniFile.Load(path);
            string sec = null;
            GameDef g = null;
            foreach (string raw in f.Lines)
            {
                string l = raw.Trim();
                if (l.StartsWith("[") && l.EndsWith("]")) { sec = l.Substring(1, l.Length - 2); g = new GameDef(); g.Name = sec; games.Add(g); continue; }
                int eq = l.IndexOf('=');
                if (g == null || eq < 0 || l.StartsWith(";")) continue;
                string k = l.Substring(0, eq).Trim().ToLowerInvariant(), v = l.Substring(eq + 1).Trim();
                if (k == "folder") g.Folder = v; else if (k == "exe") g.Exe = v; else if (k == "wheel") g.Wheel = v;
            }
        }

        // a new installation starts with an empty list (the first-run guide points to "Add game")
        static void WriteDefaultGames(string path)
        {
            File.WriteAllLines(path, new string[] { "; games.ini - games the app knows. Folder = game folder (where dinput_proxy.ini lives), Exe = exe names (| separated), Wheel = VID:PID of the wheel the settings target." }, IniFile.Enc);
        }

        // Which games are running: the process scan runs on a worker thread; the UI is touched only when something changed
        // (replacing list items fires SelectedIndexChanged, so that is guarded).
        bool polling, suppressSelect;

        void PollRunning()
        {
            if (polling) return;
            polling = true;
            string[][] defs = new string[games.Count][];
            for (int i = 0; i < games.Count; i++) defs[i] = new string[] { games[i].Folder, games[i].Exe };
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                int[] pids = new int[defs.Length];
                DateTime[] starts = new DateTime[defs.Length];
                bool steam = false;
                try
                {
                    foreach (Process p in Process.GetProcesses())
                    {
                        try
                        {
                            string name = p.ProcessName;
                            if (string.Equals(name, "steam", StringComparison.OrdinalIgnoreCase)) steam = true;
                            for (int i = 0; i < defs.Length; i++)
                            {
                                if (pids[i] != 0 || defs[i][0] == null || defs[i][1] == null) continue;
                                foreach (string e in defs[i][1].Split('|'))
                                {
                                    if (!string.Equals(name, Path.GetFileNameWithoutExtension(e), StringComparison.OrdinalIgnoreCase)) continue;
                                    string pp = Native.ProcessPath(p.Id);
                                    if (pp != null && pp.StartsWith(defs[i][0], StringComparison.OrdinalIgnoreCase))
                                    {
                                        pids[i] = p.Id;
                                        try { starts[i] = p.StartTime; } catch { }
                                    }
                                }
                            }
                        }
                        catch { }
                        finally { p.Dispose(); }
                    }
                }
                catch { }
                try { BeginInvoke(new MethodInvoker(delegate { ApplyRunning(pids, starts, steam); })); } catch { polling = false; }
            });
        }

        void ApplyRunning(int[] pids, DateTime[] starts, bool steam)
        {
            polling = false;
            if (steam != steamRunning) { steamRunning = steam; UpdateWarn(); }
            bool changed = false;
            GameDef newest = null;                 // the game that started most recently (when several start at once)
            for (int i = 0; i < games.Count && i < pids.Length; i++)
            {
                bool run = pids[i] != 0;
                bool startedNow = run && (!games[i].Running || games[i].Pid != pids[i] || !pollSeen);
                if (games[i].Running != run || games[i].Pid != pids[i]) changed = true;
                games[i].Running = run; games[i].Pid = pids[i]; games[i].Started = starts[i];
                if (startedNow && (newest == null || games[i].Started > newest.Started)) newest = games[i];
            }
            pollSeen = true;
            if (changed)
            {
                suppressSelect = true;
                int sel = gameList.SelectedIndex;
                gameList.BeginUpdate();
                for (int i = 0; i < games.Count && i < gameList.Items.Count; i++) gameList.Items[i] = games[i];     // refresh the markers
                gameList.SelectedIndex = sel;
                gameList.EndUpdate();
                suppressSelect = false;
            }
            // follow the game that just started, unless the user has unsaved changes to another game
            if (newest != null && newest != cur && !dirty)
            {
                int idx = games.IndexOf(newest);
                if (idx >= 0 && idx < gameList.Items.Count) gameList.SelectedIndex = idx;
            }
            if (changed || newest != null) UpdateStatus();
        }

        string IniPath { get { return cur == null ? null : Path.Combine(cur.Folder, "dinput_proxy.ini"); } }

        void UpdateStatus()
        {
            removeBtn.Enabled = uninstallBtn.Enabled = cur != null;
            bool hasProxy = cur != null && Store.HasProxy(cur.Folder);          // the same button installs the proxy again when it is not there
            uninstallBtn.Text = Loc.T(cur == null || hasProxy ? "game.uninstall" : "game.install");
            tip.SetToolTip(uninstallBtn, Loc.T(cur == null || hasProxy ? "tt.uninstall" : "tt.install"));
            if (cur == null) status.Text = "";
            else
            {
                string proxy = "";
                foreach (string n in new string[] { "dinput.dll", "dinput8.dll", "msacm32.dll" })
                    if (File.Exists(Path.Combine(cur.Folder, n))) proxy += (proxy.Length > 0 ? ", " : "") + n;
                status.Text = Loc.T("status.line2", cur.Folder, proxy.Length > 0 ? proxy : Loc.T("status.nodll"), File.Exists(IniPath) ? Loc.T("status.ok") : Loc.T("status.missing"));
                vdSaveInfo.Text = Loc.T("vd.saveinfo", IniPath);
            }
            UpdateBanner();
            UpdateBar();
            UpdateWarn();
        }

        // ---------------------------------------------------------------- notes and the exclusive access test
        // PE machine type of an exe/dll: 0x14C = x86, 0x8664 = x64, 0 = unknown
        public static int PeMachine(string path)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] b = new byte[0x40];
                    if (fs.Read(b, 0, b.Length) < 0x40 || b[0] != 'M' || b[1] != 'Z') return 0;
                    int pe = BitConverter.ToInt32(b, 0x3C);
                    fs.Seek(pe, SeekOrigin.Begin);
                    byte[] h = new byte[6];
                    if (fs.Read(h, 0, 6) < 6 || h[0] != 'P' || h[1] != 'E') return 0;
                    return BitConverter.ToUInt16(h, 4);
                }
            }
            catch { return 0; }
        }

        // "x86" / "x64" of the game: the proxy DLL in its folder (it is built for the game's architecture), else the game's exe; null = unknown
        static string GameArch(GameDef g)
        {
            if (g == null || string.IsNullOrEmpty(g.Folder)) return null;
            List<string> files = new List<string>();
            foreach (string n in new string[] { "dinput.dll", "dinput8.dll", "msacm32.dll" }) files.Add(Path.Combine(g.Folder, n));
            if (!string.IsNullOrEmpty(g.Exe)) foreach (string e in g.Exe.Split('|')) files.Add(Path.Combine(g.Folder, e));
            foreach (string f in files)
            {
                if (!File.Exists(f)) continue;
                int m = PeMachine(f);
                if (m == 0x14C) return "x86";
                if (m == 0x8664) return "x64";
            }
            return null;
        }

        // Steam (Steam Input) was found to stop 32-bit games from getting exclusive access to the wheel, which means no force feedback
        void UpdateWarn()
        {
            List<string> w = new List<string>();
            string old = cur != null ? OutdatedProxy(cur) : null;
            if (old != null) w.Add(Loc.T("warn.oldproxy", old));
            if (cur != null && ini != null && HasZeroBasedButtons()) w.Add(Loc.T("warn.oldini"));
            warnLbl.Visible = w.Count > 0;
            updateProxyBtn.Visible = old != null;
            if (old != null) updateProxyBtn.Left = Math.Max(0, warnLbl.ClientSize.Width - updateProxyBtn.Width - 12);
            if (w.Count > 0) { warnLbl.Height = 22 * w.Count; warnLbl.Text = string.Join("\n", w.ToArray()); }
        }

        // the proxy DLLs in the game folder that are ours but not the same file as the app's current build: { target, source, name }
        List<string[]> OutdatedProxies(GameDef g)
        {
            List<string[]> r = new List<string[]>();
            try
            {
                if (string.IsNullOrEmpty(g.Folder)) return r;
                foreach (string n in Store.ProxyNames)
                {
                    string f = Path.Combine(g.Folder, n);
                    if (!Store.IsOurProxy(f)) continue;
                    int m = PeMachine(f);
                    string arch = m == 0x14C ? "x86" : m == 0x8664 ? "x64" : null;
                    string src = arch == null ? null : Path.Combine(Path.Combine(Path.Combine(appDir, "data"), "proxy"), Path.Combine(arch, n));
                    if (src != null && File.Exists(src) && Store.Hash(src) != Store.Hash(f)) r.Add(new string[] { f, src, n });
                }
            }
            catch { }
            return r;
        }

        string OutdatedProxy(GameDef g) { List<string[]> l = OutdatedProxies(g); return l.Count > 0 ? l[0][2] : null; }

        // "Update proxy": the app's current DLL goes over the old one in the game folder; the settings (INI) are not touched
        void UpdateOutdatedProxy()
        {
            if (cur == null) return;
            if (cur.Running) { Dlg.Show(this, Loc.T("msg.proxyrunning", cur.Name), "CtrlMuxr"); return; }
            try { foreach (string[] o in OutdatedProxies(cur)) File.Copy(o[1], o[0], true); }
            catch (Exception ex) { Dlg.Show(this, Loc.T("add.copyfail", ex.Message), "CtrlMuxr"); }
            UpdateStatus();
        }

        // an INI written before the buttons were numbered from 1 has B0 keys in [ButtonMap] / [AxisButton]
        bool HasZeroBasedButtons()
        {
            foreach (string sec in new string[] { "ButtonMap", "AxisButton" })
                foreach (string k in ini.Keys(sec))
                    if (k.EndsWith(".B0", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Summarises the output of di_acq_test: "ok" (exclusive works), "blocked" (only non-exclusive works), "fail", "nodev" or "unknown"
        public static string ExSummary(string output)
        {
            if (string.IsNullOrEmpty(output) || output.IndexOf("process:", StringComparison.Ordinal) < 0) return "unknown";
            if (output.IndexOf("no device matching", StringComparison.Ordinal) >= 0) return "nodev";
            int exOk = 0, exFail = 0, nonOk = 0;
            foreach (string raw in output.Replace("\r", "").Split('\n'))
            {
                if (raw.IndexOf("Acquire=", StringComparison.Ordinal) < 0) continue;
                bool ok = raw.TrimEnd().EndsWith("OK", StringComparison.Ordinal);
                if (raw.IndexOf("NONEXCLUSIVE|", StringComparison.Ordinal) >= 0) { if (ok) nonOk++; }
                else if (raw.IndexOf("EXCLUSIVE|", StringComparison.Ordinal) >= 0) { if (ok) exOk++; else exFail++; }
            }
            if (exOk > 0 && exFail == 0) return "ok";
            if (nonOk > 0) return "blocked";
            return "fail";
        }

        static string FindTestExe(string arch)
        {
            string n = "di_acq_test_" + arch + ".exe";
            string a = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", n);
            if (File.Exists(a)) return a;
            string b = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "tools", "out", n));
            return File.Exists(b) ? b : null;
        }

        static string RunTestExe(string exe, string wheel)
        {
            ProcessStartInfo si = new ProcessStartInfo(exe, wheel);
            si.UseShellExecute = false; si.RedirectStandardOutput = true; si.CreateNoWindow = true;
            using (Process p = Process.Start(si))
            {
                string o = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } }
                return o;
            }
        }

        // runs the test for the game's architecture (both when unknown) with the game's wheel and tells the result in plain words
        void RunExclusiveTest()
        {
            if (cur == null || string.IsNullOrEmpty(cur.Wheel)) return;
            if (cur.Running) { Dlg.Show(this, Loc.T("ex.running", cur.Name), "CtrlMuxr"); return; }
            string arch = GameArch(cur);
            string[] archs = arch != null ? new string[] { arch } : new string[] { "x86", "x64" };
            StringBuilder text = new StringBuilder(), raw = new StringBuilder();
            Cursor = Cursors.WaitCursor;
            try
            {
                foreach (string a in archs)
                {
                    string exe = FindTestExe(a);
                    string o = exe == null ? "" : RunTestExe(exe, cur.Wheel);
                    string res = exe == null ? "missing" : ExSummary(o);
                    text.AppendLine(Loc.T("ex.arch." + a) + ": " + Loc.T("ex.res." + res));
                    if (o.Length > 0) { raw.AppendLine("--- " + a); raw.AppendLine(o.Replace("\r", "").Replace("\n", "\r\n")); }
                    if (res == "blocked" && a == "x86" && steamRunning) text.AppendLine("  " + Loc.T("ex.steam"));
                }
            }
            catch (Exception ex) { text.AppendLine(Loc.T("msg.error") + " " + ex.Message); }
            finally { Cursor = Cursors.Default; }
            using (Form f = new Form())
            {
                f.Text = Loc.T("ex.title", cur.Name); f.Width = 760; f.Height = 460; f.StartPosition = FormStartPosition.CenterParent; f.Font = Font;
                Label l = new Label(); l.Text = text.ToString().TrimEnd(); l.Dock = DockStyle.Top; l.Height = 110; l.Padding = new Padding(10, 10, 10, 0);
                Label h = new Label(); h.Text = Loc.T("ex.details"); h.Dock = DockStyle.Top; h.Height = 24; h.Padding = new Padding(10, 6, 10, 0); h.ForeColor = SystemColors.GrayText;
                TextBox t = new TextBox(); t.Multiline = true; t.ReadOnly = true; t.ScrollBars = ScrollBars.Both; t.WordWrap = false; t.Font = new Font("Consolas", 9f); t.Dock = DockStyle.Fill; t.Text = raw.ToString();
                Button ok = new Button(); ok.Text = Loc.T("rs.close"); ok.Dock = DockStyle.Bottom; ok.Height = 32; ok.DialogResult = DialogResult.OK;
                f.Controls.Add(t); f.Controls.Add(h); f.Controls.Add(l); f.Controls.Add(ok);
                f.AcceptButton = ok;
                f.ShowDialog(this);
            }
        }

        // big name of the game the settings go to, coloured by state; the other tabs are only usable once a game is selected
        // first run: while the list is empty the banner and the Add game button point to the first step, and a short guide is shown
        FlowLayoutPanel startPanel = new FlowLayoutPanel();
        Label[] startTitle = new Label[3];
        string[] startTitleText = new string[3];
        bool startHidden;                          // "Hide this guide": until the app is restarted
        void BuildStartPanel()
        {
            startPanel.Dock = DockStyle.Top; startPanel.FlowDirection = FlowDirection.TopDown; startPanel.WrapContents = false; startPanel.AutoSize = true;
            startPanel.Padding = new Padding(12, 8, 12, 8); startPanel.BackColor = Color.FromArgb(225, 238, 250); startPanel.Visible = false;
            Label t = new Label(); t.Text = Loc.T("start.title"); t.AutoSize = true; t.Font = new Font("Segoe UI", 11f, FontStyle.Bold); t.Margin = new Padding(0, 0, 0, 4);
            startPanel.Controls.Add(t);
            string[] desc = new string[] { Loc.T("start.1", Loc.T("game.add")), Loc.T("start.2", Loc.T("tab.devices")), Loc.T("start.3", Loc.T("tab.virtual"), Loc.T("bar.save")) };
            for (int k = 0; k < 3; k++)
            {
                startTitleText[k] = Loc.T("start." + (k + 1) + ".t");
                startTitle[k] = new Label(); startTitle[k].AutoSize = true; startTitle[k].Margin = new Padding(0, 4, 0, 0);
                Label l = new Label(); l.Text = desc[k]; l.AutoSize = true; l.MaximumSize = new Size(1000, 0); l.Margin = new Padding(14, 0, 0, 2);
                startPanel.Controls.Add(startTitle[k]); startPanel.Controls.Add(l);
            }
            FlowLayoutPanel row = new FlowLayoutPanel(); row.AutoSize = true; row.WrapContents = false; row.Margin = new Padding(0, 6, 0, 0);
            LinkLabel h = new LinkLabel(); h.Text = Loc.T("start.help"); h.AutoSize = true; h.Margin = new Padding(0, 0, 18, 0);
            h.LinkClicked += delegate { OpenHelp(); };
            LinkLabel hide = new LinkLabel(); hide.Text = Loc.T("start.hide"); hide.AutoSize = true;
            hide.LinkClicked += delegate { startHidden = true; UpdateFirstRun(); };
            row.Controls.Add(h); row.Controls.Add(hide);
            startPanel.Controls.Add(row);
        }

        static readonly System.Text.RegularExpressions.Regex NameKey = new System.Text.RegularExpressions.Regex(@"^(X|Y|Z|Rx|Ry|Rz|S0|S1|B\d+)\s*=\s*\S", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // has any axis or button been given a name yet (devices.ini)?
        bool AnyNames()
        {
            foreach (string l in names.Lines) if (NameKey.IsMatch(l.Trim())) return true;
            return false;
        }

        // the guide shows the three steps and stays until the first save has been made (settings.ini: GuideDone=1); the current step is highlighted, finished ones get a tick.
        // (It must not vanish while the first name is being typed: the names count as step 2 done, step 3 then becomes the current one.)
        bool guideDone = Settings.Get("GuideDone", "") == "1";
        void UpdateFirstRun()
        {
            bool noGames = gameList.Items.Count == 0;
            bool noNames = !AnyNames();
            startPanel.Visible = !guideDone && !startHidden;
            int cur1 = noGames ? 0 : noNames ? 1 : 2;
            for (int k = 0; k < 3; k++)
            {
                startTitle[k].Text = (k < cur1 ? "\u2713 " : "") + startTitleText[k];
                startTitle[k].Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
                startTitle[k].ForeColor = k == cur1 ? Color.FromArgb(0, 70, 160) : k < cur1 ? Color.FromArgb(0, 120, 50) : SystemColors.ControlText;      // done = green, current = blue
            }
            if (noGames)
            {
                addBtn.FlatStyle = FlatStyle.Flat; addBtn.FlatAppearance.BorderColor = Color.FromArgb(70, 130, 220); addBtn.BackColor = Color.FromArgb(160, 205, 245);
                addBtn.Font = new Font(Font, FontStyle.Bold);
            }
            else
            {
                addBtn.FlatStyle = FlatStyle.Standard; addBtn.UseVisualStyleBackColor = true; addBtn.Font = Font;
            }
        }

        void UpdateBanner()
        {
            UpdateFirstRun();
            if (cur == null && gameList.Items.Count == 0) { banner.Text = Loc.T("banner.empty", Loc.T("game.add")); banner.BackColor = Color.FromArgb(205, 225, 245); }
            else if (cur == null) { banner.Text = Loc.T("banner.none"); banner.BackColor = Color.FromArgb(250, 215, 215); }
            else if (cur.Running) { banner.Text = Loc.T("banner.running", cur.Name); banner.BackColor = Color.FromArgb(205, 238, 205); }
            else if (dirty) { banner.Text = Loc.T("banner.draft", cur.Name); banner.BackColor = Color.FromArgb(255, 225, 130); }      // unsaved changes: the draft warning only now
            else { banner.Text = Loc.T("banner.stopped", cur.Name); banner.BackColor = Color.FromArgb(250, 238, 195); }
            foreach (TabPage tp in tabs.TabPages)
                if (tp != devTab && tp != logTab) tp.Enabled = cur != null;
        }

        void BuildActionBar()
        {
            actionBar.Dock = DockStyle.Top; actionBar.Height = 34;
            barText.Left = 8; barText.Top = 9; barText.AutoSize = true;
            saveBtn.Text = Loc.T("bar.save"); discardBtn.Text = Loc.T("bar.discard"); undoBtn.Text = Loc.T("bar.undo"); backupBtn.Text = Loc.T("rs.btn"); exBtn.Text = Loc.T("ex.btn"); folderBtn.Text = Loc.T("status.folder");
            foreach (Button b in new Button[] { saveBtn, discardBtn, undoBtn, backupBtn, exBtn, folderBtn }) { b.AutoSize = true; b.Top = 3; b.Height = 28; b.Visible = false; actionBar.Controls.Add(b); }
            actionBar.Controls.Add(barText);
            saveBtn.Click += delegate { if (dirty && ini != null && cur != null) { try { WriteIni(); } catch (Exception ex) { Dlg.Show(this, Loc.T("msg.savefail") + ex.Message, "CtrlMuxr"); } } };
            discardBtn.Click += delegate { dirty = false; ReloadUi(); };
            undoBtn.Click += delegate { UndoLast(); };
            backupBtn.Click += delegate { OpenRestore(); };
            exBtn.Click += delegate { RunExclusiveTest(); };
            folderBtn.Click += delegate { if (cur != null && Directory.Exists(cur.Folder)) System.Diagnostics.Process.Start("explorer.exe", "\"" + cur.Folder + "\""); };
            actionBar.Resize += delegate { PlaceBarButtons(); };
            tip.SetToolTip(saveBtn, Loc.T("tt.bar.save")); tip.SetToolTip(discardBtn, Loc.T("tt.bar.discard")); tip.SetToolTip(undoBtn, Loc.T("tt.bar.undo")); tip.SetToolTip(backupBtn, Loc.T("tt.rs.btn")); tip.SetToolTip(exBtn, Loc.T("tt.ex.btn")); tip.SetToolTip(folderBtn, Loc.T("status.openfolder"));
        }

        void PlaceBarButtons()
        {
            int x = actionBar.ClientSize.Width - 8;
            foreach (Button b in new Button[] { exBtn, backupBtn, undoBtn, discardBtn, saveBtn })
                if (b.Visible) { x -= b.Width; b.Left = x; x -= 6; }
            folderBtn.Top = 5; folderBtn.Height = 24; folderBtn.Left = 8;
            barText.Left = folderBtn.Visible ? folderBtn.Right + 6 : 8;
            barText.AutoSize = false; barText.AutoEllipsis = true; barText.Height = 18;
            barText.Width = Math.Max(60, x - barText.Left);          // the text gives way to the buttons (full text in the tooltip)
            tip.SetToolTip(barText, barText.Text);
        }

        // backups and named profiles of the selected game: pick one, see what it would change, restore it (as an ordinary change: live or draft, can be undone)
        void OpenRestore()
        {
            if (cur == null || ini == null) return;
            SetMain(ini);                                       // the state shown (and saved as a profile) carries the main device
            using (RestoreForm f = new RestoreForm(StoreDir, ini.Lines.ToArray(), cur.Name, DestKey, DeviceTitle))
            {
                if (f.ShowDialog(this) != DialogResult.OK || f.ChosenLines == null) return;
                ini.Lines.Clear(); ini.Lines.AddRange(f.ChosenLines);
                SetMain(ini);                                   // restored over a different main device (the user confirmed): the note tells the truth again
                SyncNames(ini);
                CommitIni();
                RefreshUiFromIni();
            }
        }

        void UpdateBar()
        {
            UpdateFirstRun();
            if (cur == null) { actionBar.Visible = false; return; }
            actionBar.Visible = true;
            saveBtn.Visible = discardBtn.Visible = dirty;
            backupBtn.Visible = exBtn.Visible = true;
            folderBtn.Visible = !dirty && !cur.Running;      // the button is the "Folder:" label of the path line, which is shown only while it is the bar text
            undoBtn.Visible = !dirty && undoGame == cur.Name && undoStack.Count > 0;
            undoBtn.Text = Loc.T("bar.undo") + (undoBtn.Visible ? " (" + undoStack.Count + ")" : "");
            if (dirty) { barText.Text = Loc.T("bar.dirty", cur.Name); actionBar.BackColor = Color.FromArgb(255, 225, 130); }
            else
            {
                string when = lastSaved == DateTime.MinValue || undoGame != cur.Name ? "" : "  " + Loc.T("bar.lastsaved", lastSaved.ToString("HH:mm:ss"));
                if (undoneTo != DateTime.MinValue && undoGame == cur.Name) when = "  " + Loc.T("bar.undone", undoneTo.ToString("HH:mm:ss"));
                barText.Text = (cur.Running ? Loc.T("bar.live") : status.Text) + when;
                actionBar.BackColor = SystemColors.Control;
            }
            PlaceBarButtons();
        }

        // ---------------------------------------------------------------- writing the INI: backup + undo + draft handling
        // Called after the UI values were put into `ini`: a running game gets the file written, others keep a draft until Save.
        void CommitIni()
        {
            if (cur == null || ini == null) return;
            rawBox.Text = string.Join(Environment.NewLine, ini.Lines.ToArray());
            if (cur.Running) WriteIni(); else dirty = true;
            UpdateStatus();
        }

        void WriteIni()
        {
            BackupOnce();
            PushUndo();
            SetMain(ini);
            ini.Save();
            Store.AfterSave(appDir, cur.Name, cur.Folder);
            dirty = false;
            lastSaved = DateTime.Now;
            undoneTo = DateTime.MinValue;
            if (!guideDone) { guideDone = true; Settings.Set("GuideDone", "1"); }      // the first save ends the start guide
            UpdateStatus();
        }

        // records the game's main device in the INI ([Main] Wheel=VID:PID): backups and profiles tell which wheel they were made for
        void SetMain(IniFile f)
        {
            if (DestKey != null && f != null) f.Set("Main", "Wheel", DestKey);
        }

        void PushUndo()
        {
            if (undoGame != cur.Name) { undoStack.Clear(); undoWhen.Clear(); undoGame = cur.Name; undoneTo = DateTime.MinValue; }
            undoStack.Add(File.Exists(IniPath) ? File.ReadAllLines(IniPath, IniFile.Enc) : new string[0]);
            undoWhen.Add(DateTime.Now);
            if (undoStack.Count > 30) { undoStack.RemoveAt(0); undoWhen.RemoveAt(0); }
        }

        // first write to a game's INI in this session: keep a copy (the 5 newest copies stay)
        string StoreDir { get { return Store.GameDir(appDir, cur.Name); } }

        void BackupOnce()
        {
            if (backupGame == cur.Name || !File.Exists(IniPath)) return;
            try
            {
                string sd = StoreDir;
                string stamp = Path.Combine(sd, "dinput_proxy.ini.bak_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                string[] cur2 = File.ReadAllLines(IniPath, IniFile.Enc); string dup = null;
                foreach (string b in Directory.GetFiles(sd, "dinput_proxy.ini.bak_*"))
                    if (RestoreForm.Same(cur2, File.ReadAllLines(b, IniFile.Enc))) { dup = b; break; }
                if (dup != null) { if (!File.Exists(stamp)) File.Move(dup, stamp); }              // the same settings are already kept: that copy just becomes the newest
                else File.Copy(IniPath, stamp, true);
                string[] old = Directory.GetFiles(sd, "dinput_proxy.ini.bak_*");
                Array.Sort(old, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < old.Length - 5; i++) try { File.Delete(old[i]); } catch { }
                backupGame = cur.Name;
            }
            catch { }
        }

        void UndoLast()
        {
            if (cur == null || undoGame != cur.Name || undoStack.Count == 0) return;
            try
            {
                string[] prev = undoStack[undoStack.Count - 1];
                DateTime when = undoWhen[undoWhen.Count - 1];
                undoStack.RemoveAt(undoStack.Count - 1);
                undoWhen.RemoveAt(undoWhen.Count - 1);
                IniFile f = IniFile.Load(IniPath);
                f.Lines.Clear(); f.Lines.AddRange(prev);
                f.Save();
                Store.AfterSave(appDir, cur.Name, cur.Folder);      // the app's stored copy follows the game's file
                undoneTo = when;
                ReloadUi();
            }
            catch (Exception ex) { Dlg.Show(this, Loc.T("msg.savefail") + ex.Message, "CtrlMuxr"); }
        }

        // true when it is fine to leave the selected game: pending live changes are written, a draft is saved/discarded on request
        bool ConfirmLeave()
        {
            if (cur == null || ini == null) return true;
            if (saveTimer.Enabled) { saveTimer.Stop(); SaveShape(); }
            if (vdSaveTimer.Enabled) { vdSaveTimer.Stop(); SaveVirtual(); }
            if (!dirty) return true;
            DialogResult r = Dlg.Show(this, Loc.T("msg.leave", cur.Name), "CtrlMuxr", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return false;
            if (r == DialogResult.Yes) { try { WriteIni(); } catch (Exception ex) { Dlg.Show(this, Loc.T("msg.savefail") + ex.Message, "CtrlMuxr"); return false; } }
            dirty = false;
            return true;
        }

        void SelectGame()
        {
            if (suppressSelect) return;
            GameDef next = gameList.SelectedItem as GameDef;
            if (next == cur) return;
            if (!ConfirmLeave())
            {
                suppressSelect = true; gameList.SelectedItem = cur; suppressSelect = false;      // stay with the game that has unsaved changes
                return;
            }
            saveTimer.Stop(); vdSaveTimer.Stop();
            btnRowsWant = 8;                                              // the rows shown are a view of this game: the next one starts at 8 (more if its rows need them)
            cur = next;
            if (cur != null) Settings.Set("LastGame", cur.Name);
            ReloadUi();
            SelectWheelDefault();
            if (cur != null) StartPreCreate();
        }

        // WinForms creates the controls of a tab page only when the page is first shown (Virtual device ~0.8 s, Buttons ~1.7 s, measured):
        // after a game is selected (its lists are filled by then) the pages are built in the background, one per timer tick, so the first click is quick
        System.Windows.Forms.Timer preTimer;
        void StartPreCreate()
        {
            if (preTimer != null) return;
            preTimer = new System.Windows.Forms.Timer();
            preTimer.Interval = 400;                                      // let the selected tab paint first
            preTimer.Tick += delegate
            {
                preTimer.Stop();
                foreach (TabPage tp in tabs.TabPages)
                    if (tp != tabs.SelectedTab && !tp.IsHandleCreated)
                    {
                        try { CreateHandles(tp, HiddenRowControls()); } catch { }
                        preTimer.Interval = 150; preTimer.Start();        // the next page
                        return;
                    }
            };
            preTimer.Start();
        }

        // creates the native windows of a page now; the Buttons rows that are not shown get theirs when they are first shown
        // (48 rows x 2 lists took ~5.5 s, measured), and a table lays itself out once after its controls, not after each of them
        static void CreateHandles(Control c, HashSet<Control> skip)
        {
            TableLayoutPanel tl = c as TableLayoutPanel;
            if (tl != null) tl.SuspendLayout();
            IntPtr h = c.Handle;
            foreach (Control k in c.Controls) if (skip == null || !skip.Contains(k)) CreateHandles(k, skip);
            if (tl != null) tl.ResumeLayout(false);
        }

        HashSet<Control> HiddenRowControls()
        {
            HashSet<Control> h = new HashSet<Control>();
            for (int r = Math.Max(8, btnRowsApplied); r < BtnRows; r++) { h.Add(bmNum[r]); h.Add(bmSrc[r]); h.Add(bmOut[r]); h.Add(bmLed[r]); h.Add(bmWarn[r]); }
            return h;
        }

        // the Devices and Tuning tabs start on the game's wheel (not on whatever device the system lists first)
        void SelectWheelDefault()
        {
            if (cur == null || string.IsNullOrEmpty(cur.Wheel)) return;
            if (devInfos.Length == 0) EnumDevices();
            string w = cur.Wheel.ToUpperInvariant();
            for (int i = 0; i < devInfos.Length; i++)
                if (string.Format("{0:X4}:{1:X4}", devInfos[i].vid, devInfos[i].pid) == w) { if (devBox.SelectedIndex != i) devBox.SelectedIndex = i; break; }
            int si = shapeDevKeys.IndexOf(w);
            if (si >= 0 && shapeDevBox.SelectedIndex != si) shapeDevBox.SelectedIndex = si;
        }

        // loads the selected game's INI into every tab (also used to throw a draft away)
        void ReloadUi()
        {
            saveTimer.Stop(); vdSaveTimer.Stop();
            dirty = false;
            if (cur != null) Store.Sync(appDir, cur.Name, cur.Folder);
            ini = cur == null ? null : IniFile.Load(IniPath);
            RefreshUiFromIni();
        }

        // puts the working copy `ini` into every tab (without reading the file again, so a draft stays a draft)
        void RefreshUiFromIni()
        {
            FillShapeDevices();
            RefreshAxisLabels();
            ReadShapeToUi();
            RefreshVirtual();
            RefreshAdvanced();
            rawBox.Text = ini == null ? "" : string.Join(Environment.NewLine, ini.Lines.ToArray());
            UpdateStatus();
            RefreshLog();
        }

        // the listed game that lives in this folder (comparison without case and trailing backslash), or null
        string ListedGameOf(string folder)
        {
            string f = (folder ?? "").TrimEnd('\\', '/');
            foreach (GameDef g in games)
                if (string.Equals((g.Folder ?? "").TrimEnd('\\', '/'), f, StringComparison.OrdinalIgnoreCase)) return g.Name;
            return null;
        }

        // "Add game": detect, install the proxy, create a starting INI and list the game
        void AddGame() { AddGame(null); }

        // reinstall != null: "Install proxy" for that listed game (its proxy was uninstalled; the saved settings come back)
        void AddGame(GameDef reinstall)
        {
            if (!ConfirmLeave()) return;
            if (devInfos.Length == 0) EnumDevices();
            using (AddGameForm f = new AddGameForm(appDir, devInfos, ListedGameOf))
            {
                if (reinstall != null) f.PrepareReinstall(reinstall.Name, reinstall.Folder, reinstall.Exe, reinstall.Wheel);
                if (f.ShowDialog(this) != DialogResult.OK) return;
                string path = Path.Combine(appDir, "games.ini");
                IniFile gi = IniFile.Load(path);
                if (gi.Lines.Count > 0 && gi.Lines[gi.Lines.Count - 1].Trim().Length > 0) gi.Lines.Add("");
                gi.Set(f.GameName, "Folder", f.Folder);
                gi.Set(f.GameName, "Exe", f.ExeName);
                gi.Set(f.GameName, "Wheel", f.Wheel);
                gi.Save();
                games.Clear();
                LoadGames();
                suppressSelect = true;
                gameList.Items.Clear();
                foreach (GameDef g in games) gameList.Items.Add(g);
                suppressSelect = false;
                for (int i = 0; i < games.Count; i++) if (games[i].Name == f.GameName) gameList.SelectedIndex = i;
                PollRunning();
            }
        }

        // the help file is an ordinary HTML page next to the program (app\help\help.<language>.html); it opens in the default browser
        void OpenHelp()
        {
            try
            {
                string dir = Path.Combine(appDir, "help");
                string path = Path.Combine(dir, "help." + Loc.Code + ".html");
                if (!File.Exists(path)) path = Path.Combine(dir, "help.en.html");
                if (!File.Exists(path)) { Dlg.Show(this, Loc.T("help.missing", dir), "CtrlMuxr"); return; }
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) { Dlg.Show(this, Loc.T("help.missing", ex.Message), "CtrlMuxr"); }
        }

        // "Remove from list": the game's entry goes; the settings kept in the store go only when asked
        void RemoveFromList()
        {
            if (cur == null) return;
            if (cur.Running) { Dlg.Show(this, Loc.T("ex.running", cur.Name), "CtrlMuxr"); return; }
            if (!ConfirmLeave()) return;
            DialogResult r = Dlg.Show(this, Loc.T("rm.confirm", cur.Name) + (Store.HasProxy(cur.Folder) ? "\n\n" + Loc.T("rm.proxyleft") : ""), "CtrlMuxr", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, new string[] { Loc.T("rm.delete"), Loc.T("rm.keep"), Loc.T("dlg.cancel") });      // Yes = Delete, No = Keep; the proxy and its ini stay in the game folder: say so
            if (r == DialogResult.Cancel) return;
            string name = cur.Name;
            IniFile gi = IniFile.Load(Path.Combine(appDir, "games.ini"));
            gi.RemoveSection(name);
            gi.Save();
            if (r == DialogResult.Yes) Store.Delete(appDir, name);
            ReloadGameList();
        }

        void ReloadGameList()
        {
            suppressSelect = true;
            cur = null; ini = null; dirty = false;
            games.Clear(); LoadGames();
            gameList.Items.Clear();
            foreach (GameDef g in games) gameList.Items.Add(g);
            gameList.SelectedIndex = -1;
            suppressSelect = false;
            ReloadUi();
            PollRunning();
        }

        // "Uninstall proxy": removes our DLL (and puts the game's own back if the installer saved it) and the working INI; the store keeps the settings
        void UninstallProxy()
        {
            if (cur == null) return;
            if (cur.Running) { Dlg.Show(this, Loc.T("ex.running", cur.Name), "CtrlMuxr"); return; }
            if (!ConfirmLeave()) return;
            List<string> dlls = new List<string>();
            foreach (string n in Store.ProxyNames) if (Store.IsOurProxy(Path.Combine(cur.Folder, n))) dlls.Add(n);
            if (dlls.Count == 0) { Dlg.Show(this, Loc.T("un.none", cur.Folder), "CtrlMuxr"); return; }
            if (Dlg.Show(this, Loc.T("un.confirm", cur.Name, string.Join(", ", dlls.ToArray())), "CtrlMuxr", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try
            {
                Store.Sync(appDir, cur.Name, cur.Folder);                          // the latest settings are in the store before anything is deleted
                foreach (string n in dlls)
                {
                    string target = Path.Combine(cur.Folder, n);
                    File.Delete(target);
                    List<string> orig = new List<string>(Directory.GetFiles(cur.Folder, n + ".orig_*"));
                    orig.RemoveAll(delegate(string o) { return Store.IsOurProxy(o); });         // an older CtrlMuxr proxy is not the game's own file: it is not put back
                    orig.Sort(StringComparer.OrdinalIgnoreCase);
                    if (orig.Count > 0) File.Move(orig[orig.Count - 1], target);      // the game's own DLL (saved by the installer)
                }
                if (File.Exists(IniPath)) File.Delete(IniPath);
                foreach (string lf in Directory.GetFiles(cur.Folder, "dinput_*.log"))                  // the proxy's own logs: dinput_<number>.log and dinput_errors.log
                    if (SessionLog.IsSessionLog(lf) || string.Equals(Path.GetFileName(lf), "dinput_errors.log", StringComparison.OrdinalIgnoreCase)) try { File.Delete(lf); } catch { }
            }
            catch (Exception ex) { Dlg.Show(this, Loc.T("msg.savefail") + ex.Message, "CtrlMuxr"); return; }
            ReloadUi();
            if (Dlg.Show(this, Loc.T("un.done", cur.Name), "CtrlMuxr", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) RemoveFromList();
        }

        void FillLanguages()
        {
            langCodes.Clear();
            langBox.Items.Clear();
            langCodes.Add("auto");
            langBox.Items.Add(Loc.T("lang.auto"));
            foreach (string[] l in Loc.Languages()) { langCodes.Add(l[0]); langBox.Items.Add(l[1]); }
            string pref = Settings.Get("Language", "auto");
            int sel = langCodes.IndexOf(pref);
            langBox.SelectedIndex = sel >= 0 ? sel : 0;
            langBox.SelectedIndexChanged += delegate
            {
                int i = langBox.SelectedIndex;
                if (i < 0 || i >= langCodes.Count || langCodes[i] == Settings.Get("Language", "auto")) return;
                Settings.Set("Language", langCodes[i]);
                Restart = true;
                Close();
            };
        }

        // hover help on every control where something is chosen or typed
        void ApplyTooltips()
        {
            tip.SetToolTip(gameList, Loc.T("tt.games")); tip.SetToolTip(addBtn, Loc.T("tt.add")); tip.SetToolTip(removeBtn, Loc.T("tt.remove")); tip.SetToolTip(helpBtn, Loc.T("tt.help")); tip.SetToolTip(uninstallBtn, Loc.T("tt.uninstall")); tip.SetToolTip(langBox, Loc.T("tt.lang"));
            tip.SetToolTip(devBox, Loc.T("tt.devbox")); tip.SetToolTip(refreshBtn, Loc.T("tt.refresh"));
            tip.SetToolTip(btnName, Loc.T("tt.btnname")); tip.SetToolTip(presetBox, Loc.T("tt.vd.preset"));
            for (int i = 0; i < 8; i++)
            {
                tip.SetToolTip(axText[i], Loc.T("tt.axname"));
                tip.SetToolTip(vdSrc[i], Loc.T("tt.vd.src")); tip.SetToolTip(vdMode[i], Loc.T("tt.vd.mode"));
                tip.SetToolTip(vdInv[i], Loc.T("tt.vd.inv")); tip.SetToolTip(vdSrc2[i], Loc.T("tt.vd.src2"));
                tip.SetToolTip(vdBar[i], Loc.T("tt.vd.bar"));
            }
            tip.SetToolTip(presetApplyBtn, Loc.T("tt.vd.presetapply"));
            tip.SetToolTip(shapeDevBox, Loc.T("tt.shape.dev")); tip.SetToolTip(axisBox, Loc.T("tt.shape.axis")); tip.SetToolTip(invertBox, Loc.T("tt.shape.invert"));
            string[] sk = new string[] { "dz", "expo", "gain", "adz", "lead" };
            for (int i = 0; i < sk.Length; i++) { tip.SetToolTip(bars[i], Loc.T("tt.shape." + sk[i])); tip.SetToolTip(nums[i], Loc.T("tt.shape." + sk[i])); }
            tip.SetToolTip(rawBox, Loc.T("tt.ini.edit")); tip.SetToolTip(iniSaveBtn, Loc.T("tt.ini.save")); tip.SetToolTip(iniReloadBtn, Loc.T("tt.ini.reload"));
            tip.SetToolTip(logBox, Loc.T("tt.log"));
        }

        // One look for axes and buttons: exists = black text on white (a bar or a cell), does not exist = pale text on grey.
        // Buttons: pressed = green, named = blue.
        static string BKey(int b0) { return "B" + (b0 + 1); }      // buttons are numbered from 1 everywhere the user sees them and in the INI; b0 is the 0-based index inside the program
        static readonly Color NamedColor = Color.FromArgb(160, 205, 245);
        static readonly Color DimText = Color.FromArgb(170, 170, 170);
        static readonly Color DimBack = Color.FromArgb(236, 236, 236);

        void PaintBtn(int b, bool down)
        {
            Label c = btnCell[b];
            bool named = DevKey != null && !string.IsNullOrEmpty(names.Get(DevKey, BKey(b)));
            if (down) { c.BackColor = Color.LimeGreen; c.ForeColor = SystemColors.ControlText; }
            else if (named) { c.BackColor = NamedColor; c.ForeColor = SystemColors.ControlText; }
            else if (b < devButtons) { c.BackColor = Color.White; c.ForeColor = SystemColors.ControlText; }
            else { c.BackColor = DimBack; c.ForeColor = DimText; }
        }

        // ---------------------------------------------------------------- devices tab (live test + naming)
        void BuildDevicesTab()
        {
            Panel top = new Panel(); top.Dock = DockStyle.Top; top.Height = 70;
            devBox.DropDownStyle = ComboBoxStyle.DropDownList; devBox.Left = 10; devBox.Top = 8; devBox.Width = 420;
            devBox.SelectedIndexChanged += delegate { OnDeviceSelected(); };
            Button refresh = new Button(); refresh.Text = Loc.T("dev.refresh"); refresh.Left = 440; refresh.Top = 7; refresh.Width = 80;
            refreshBtn = refresh;
            refresh.Click += delegate { RefreshDevicesAndLists(); };
            devInfo.Left = 10; devInfo.Top = 34; devInfo.Width = 700; devInfo.Height = 18; devInfo.ForeColor = SystemColors.GrayText;
            top.Controls.Add(devBox); top.Controls.Add(refresh); top.Controls.Add(devInfo);
            AddAnalyseButton(top, 610, 6);

            TableLayoutPanel t = new TableLayoutPanel();
            t.Dock = DockStyle.Top; t.AutoSize = true; t.ColumnCount = 4; t.Padding = new Padding(10, 6, 10, 0);
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            for (int i = 0; i < 8; i++)
            {
                int idx = i;
                axLabel[i] = new Label(); axLabel[i].Text = AxisNames[i]; axLabel[i].AutoSize = false; axLabel[i].Width = 36; axLabel[i].Height = 22;
                axLabel[i].TextAlign = ContentAlignment.MiddleLeft; axLabel[i].Font = new Font(Font, FontStyle.Bold);
                axBar[i] = new Bar(); axBar[i].Dock = DockStyle.Fill; axBar[i].Margin = new Padding(3, 4, 3, 4);
                axVal[i] = new Label(); axVal[i].Text = "-"; axVal[i].AutoSize = false; axVal[i].Width = 56; axVal[i].Height = 22; axVal[i].TextAlign = ContentAlignment.MiddleRight;
                axText[i] = new TextBox(); axText[i].Width = 180; Native.Cue(axText[i], Loc.T("common.nameq"));
                axText[i].TextChanged += delegate { if (loadingNames || devIdx < 0) return; SaveName(AxisNames[idx], axText[idx].Text); };
                t.Controls.Add(axLabel[i], 0, i); t.Controls.Add(axBar[i], 1, i); t.Controls.Add(axVal[i], 2, i); t.Controls.Add(axText[i], 3, i);
            }
            Label ah = new Label(); ah.Text = Loc.T("dev.axishint"); ah.Dock = DockStyle.Top; ah.Height = 22; ah.Padding = new Padding(10, 4, 10, 0); ah.ForeColor = SystemColors.GrayText; ah.UseCompatibleTextRendering = true;
            ah.Resize += delegate { int hh = FitHeight(ah, ah.Width - 20) + 4; if (hh != ah.Height) ah.Height = hh; };
            movedLabel.Dock = DockStyle.Top; movedLabel.Height = 22; movedLabel.Padding = new Padding(10, 4, 0, 0); movedLabel.Font = new Font(Font, FontStyle.Bold);

            Panel bp = new Panel(); bp.Dock = DockStyle.Top; bp.Height = 30;
            btnLast.Left = 10; btnLast.Top = 8; btnLast.Width = 330; btnLast.Text = Loc.T("dev.pressbtn");
            btnName.Left = 350; btnName.Top = 4; btnName.Width = 190; Native.Cue(btnName, Loc.T("common.nameq"));
            btnName.TextChanged += delegate { if (loadingNames || lastBtn < 0 || devIdx < 0) return; SaveName(BKey(lastBtn), btnName.Text); btnCell[lastBtn].Tag = btnName.Text; tip.SetToolTip(btnCell[lastBtn], BtnTip(lastBtn)); if ((btns[lastBtn] & 0x80) == 0) PaintBtn(lastBtn, false); };
            Label legend = new Label(); legend.Text = Loc.T("dev.legend"); legend.Left = 556; legend.Top = 8; legend.AutoSize = false; legend.Height = 18; legend.UseCompatibleTextRendering = true; legend.ForeColor = SystemColors.GrayText;
            bp.Resize += delegate { legend.Width = Math.Max(120, bp.Width - legend.Left - 10); legend.Height = FitHeight(legend, legend.Width); bp.Height = Math.Max(30, legend.Top + legend.Height + 4); };
            bp.Controls.Add(btnLast); bp.Controls.Add(btnName); bp.Controls.Add(legend);

            FlowLayoutPanel cells = new FlowLayoutPanel(); cells.Dock = DockStyle.Fill; cells.Padding = new Padding(10, 2, 10, 2); cells.AutoScroll = true;
            for (int b = 0; b < 128; b++)
            {
                Label c = new Label(); c.Text = (b + 1).ToString(); c.Width = 30; c.Height = 20; c.TextAlign = ContentAlignment.MiddleCenter;
                c.BorderStyle = BorderStyle.FixedSingle; c.Margin = new Padding(2); c.BackColor = SystemColors.Control; c.Font = new Font(Font.FontFamily, 8f);
                btnCell[b] = c;
                tip.SetToolTip(c, BtnTip(b));
                cells.Controls.Add(c);
            }

            devTab.Controls.Add(cells);
            devTab.Controls.Add(bp);
            devTab.Controls.Add(movedLabel);
            devTab.Controls.Add(ah);
            devTab.Controls.Add(t);
            devTab.Controls.Add(top);
            tabs.TabPages.Add(devTab);

            devTimer.Interval = 40; devTimer.Tick += delegate { DevTick(); };
            nameSaveTimer.Interval = 500; nameSaveTimer.Tick += delegate { nameSaveTimer.Stop(); try { names.Save(); UpdateFirstRun(); RefreshAxisLabels(); FillShapeDevices(); SyncNamesToAllGames(); RefreshVirtual(); } catch { } };
            tabs.SelectedIndexChanged += delegate { if (tabs.SelectedTab == devTab) { if (devInfos.Length == 0) EnumDevices(); devTimer.Start(); } else devTimer.Stop(); };
            Shown += delegate { if (tabs.SelectedTab == devTab) { EnumDevices(); devTimer.Start(); } };
        }

        // height a wrapped label needs at the given width (compatible text rendering, so words are not broken)
        static int FitHeight(Label l, int width)
        {
            if (width < 40) return l.Height;
            using (Graphics g = l.CreateGraphics()) return (int)Math.Ceiling(g.MeasureString(l.Text, l.Font, width).Height) + 2;
        }

        string DevKey { get { return devIdx >= 0 && devIdx < devInfos.Length ? string.Format("{0:X4}:{1:X4}", devInfos[devIdx].vid, devInfos[devIdx].pid) : null; } }

        string BtnTip(int b)
        {
            string n = DevKey == null ? null : names.Get(DevKey, BKey(b));
            return Loc.T("tip.btn", b + 1, string.IsNullOrEmpty(n) ? "" : " \u2013 " + n);
        }

        void SaveName(string key, string value)
        {
            if (DevKey == null) return;
            if (string.IsNullOrEmpty(value)) names.Remove(DevKey, key); else names.Set(DevKey, key, value);
            nameSaveTimer.Stop(); nameSaveTimer.Start();
        }

        void EnumDevices()
        {
            string keep = DevKey;
            try
            {
                DRDevInfo[] arr = new DRDevInfo[16];
                int n = DevRead.DR_Enum(arr, 16);
                Array.Resize(ref arr, Math.Max(0, n));
                devInfos = arr;
                devReadFailed = false;
            }
            catch (Exception ex)
            {
                devReadFailed = true;
                devInfo.Text = Loc.T("dev.dllfail") + ex.Message;
                devInfos = new DRDevInfo[0];
            }
            devIdx = -1;
            devBox.Items.Clear();
            int select = -1;
            for (int i = 0; i < devInfos.Length; i++)
            {
                string k = string.Format("{0:X4}:{1:X4}", devInfos[i].vid, devInfos[i].pid);
                devBox.Items.Add(devInfos[i].name + "   (" + k + ")");
                if (k == keep) select = i;
            }
            if (devBox.Items.Count > 0) devBox.SelectedIndex = select >= 0 ? select : 0;
            else if (devInfos.Length == 0 && string.IsNullOrEmpty(devInfo.Text)) devInfo.Text = Loc.T("dev.none");
            RememberButtonCounts();
            FillShapeDevices();
        }

        // Refresh button: the "not connected" marks and button lists follow what is connected now (not done in EnumDevices itself,
        // because timers call that too and rebuilding the lists would close a drop-down list the user has open)
        void RefreshDevicesAndLists()
        {
            EnumDevices();
            if (ini != null && DestKey != null) { FillSources(); FillButtonSources(); }
        }

        // devices.ini keeps the button count of every device the user has named or opened, so the Buttons tab can size an unplugged device's list
        void RememberButtonCounts()
        {
            bool changed = false;
            foreach (DRDevInfo d in devInfos)
            {
                string k = string.Format("{0:X4}:{1:X4}", d.vid, d.pid);
                if (d.numButtons <= 0 || names.Keys(k).Count == 0) continue;
                if (names.Get(k, "Buttons") != d.numButtons.ToString()) { names.Set(k, "Buttons", d.numButtons.ToString()); changed = true; }
            }
            if (changed) { try { names.Save(); } catch { } }
        }

        void OnDeviceSelected()
        {
            devIdx = devBox.SelectedIndex;
            if (devIdx < 0 || devIdx >= devInfos.Length) return;
            devReads = 0; lastBtn = -1;
            if (string.IsNullOrEmpty(names.Get(string.Format("{0:X4}:{1:X4}", devInfos[devIdx].vid, devInfos[devIdx].pid), "Name")))
                names.Set(string.Format("{0:X4}:{1:X4}", devInfos[devIdx].vid, devInfos[devIdx].pid), "Name", devInfos[devIdx].name.Trim());
            int rc = DevRead.DR_Open(devIdx, Handle);
            int mask = DevRead.DR_AxisMask(devIdx);
            devButtons = DevRead.DR_NumButtons(devIdx);
            devInfo.Text = (rc == 0 ? "" : Loc.T("dev.openfail", rc)) + Loc.T("dev.info", DevKey, CountBits(mask), devButtons);
            loadingNames = true;
            for (int i = 0; i < 8; i++)
            {
                bool present = (mask & (1 << i)) != 0;
                axLabel[i].ForeColor = present ? SystemColors.ControlText : DimText;
                axText[i].Text = names.Get(DevKey, AxisNames[i]) ?? "";
                axText[i].Enabled = present;
                axBar[i].Value = 0;
                axBar[i].Dim = !present;
                axVal[i].ForeColor = present ? SystemColors.ControlText : DimText;
                axVal[i].Text = present ? "-" : Loc.T("dev.absent");
            }
            btnName.Text = ""; btnLast.Text = Loc.T("dev.pressbtn");
            for (int b = 0; b < 128; b++) { PaintBtn(b, false); tip.SetToolTip(btnCell[b], BtnTip(b)); }
            loadingNames = false;
            Array.Clear(axPrev, 0, 8); Array.Clear(btnPrev, 0, 128);
        }

        static int CountBits(int m) { int c = 0; while (m != 0) { c += m & 1; m >>= 1; } return c; }

        void DevTick()
        {
            if (devIdx < 0 || tabs.SelectedTab != devTab) return;
            int rc;
            try { rc = DevRead.DR_Read(devIdx, axVals, povs, btns); }
            catch { devTimer.Stop(); return; }
            if (rc != 0) return;
            devReads++;
            int mask = DevRead.DR_AxisMask(devIdx);
            DateTime now = DateTime.Now;
            int best = -1;
            DateTime bestT = DateTime.MinValue;
            for (int i = 0; i < 8; i++)
            {
                if ((mask & (1 << i)) == 0) continue;
                axBar[i].Value = axVals[i] / 65535.0;
                axVal[i].Text = axVals[i].ToString();
                if (devReads > 3 && Math.Abs(axVals[i] - axPrev[i]) > 1500) axMoved[i] = now;
                axPrev[i] = axVals[i];
                if ((now - axMoved[i]).TotalSeconds < 1.5 && axMoved[i] > bestT) { bestT = axMoved[i]; best = i; }
            }
            for (int i = 0; i < 8; i++) axLabel[i].BackColor = i == best ? Color.Gold : Color.Transparent;
            movedLabel.Text = best >= 0 ? Loc.T("dev.moving", AxisNames[best]) : "";
            for (int b = 0; b < 128; b++)
            {
                bool down = (btns[b] & 0x80) != 0, was = (btnPrev[b] & 0x80) != 0;
                if (down != was)
                    PaintBtn(b, down);
                if (down && !was && devReads > 3)
                {
                    lastBtn = b;
                    btnLast.Text = Loc.T("dev.lastbtn", b + 1);
                    loadingNames = true; btnName.Text = names.Get(DevKey, BKey(b)) ?? ""; loadingNames = false;
                }
                btnPrev[b] = btns[b];
            }
        }


        // `CtrlMuxr.exe --uitest <folder> <out.txt>`: drives the virtual-device table against a temporary INI (no window shown)
        // ---------------------------------------------------------------- performance test (`CtrlMuxr.exe --perftest out.txt [tag]`)
        // Shows the window off screen with the games of games.ini and times: game selection, tab switches (first and later visits),
        // the longest time the UI thread was blocked, the timers' work and the memory / handle counts.
        [DllImport("user32.dll")] static extern uint GetGuiResources(IntPtr hProcess, uint flags);
        double blockMax;                                                  // longest single DoEvents round (a UI freeze) seen by Pump

        void Pump(int ms)
        {
            Stopwatch t = Stopwatch.StartNew();
            while (t.ElapsedMilliseconds < ms)
            {
                Stopwatch r = Stopwatch.StartNew();
                Application.DoEvents();
                double e = r.Elapsed.TotalMilliseconds;
                if (e > blockMax) blockMax = e;
                System.Threading.Thread.Sleep(5);
            }
        }

        double TimeMs(Action a)
        {
            Stopwatch sw = Stopwatch.StartNew();
            a();
            Refresh();                                                    // paint now, so layout and drawing are inside the time
            return sw.Elapsed.TotalMilliseconds;
        }

        static void PerfMem(StringBuilder sb, string label)
        {
            Process p = Process.GetCurrentProcess();
            sb.AppendLine(string.Format("mem {0}: working set {1} MB, private {2} MB, GC heap {3} MB, handles {4}, GDI {5}, USER {6}",
                label, p.WorkingSet64 / 1048576, p.PrivateMemorySize64 / 1048576, GC.GetTotalMemory(false) / 1048576, p.HandleCount, GetGuiResources(p.Handle, 0), GetGuiResources(p.Handle, 1)));
        }

        // `CtrlMuxr.exe --soak out.txt minutes`: games, tabs, row counts and backup scans in a loop; one memory line per minute
        public string SoakTest(string outFile, int minutes)
        {
            StartPosition = FormStartPosition.Manual; Location = new Point(-3000, -3000); ShowInTaskbar = false;
            Show(); Pump(300);
            int n = games.Count, nt = tabs.TabPages.Count;
            Random rnd = new Random(1);
            Stopwatch total = Stopwatch.StartNew(), minute = Stopwatch.StartNew();
            int cycles = 0, cyclesMin = 0; double selSum = 0, scanSum = 0; int scans = 0;
            File.WriteAllText(outFile, "soak " + minutes + " min, games=" + n + "  " + DateTime.Now.ToString("HH:mm:ss") + Environment.NewLine);
            Action<string> sample = delegate(string label)
            {
                Process p = Process.GetCurrentProcess();
                long gc = GC.GetTotalMemory(true) / 1048576;
                File.AppendAllText(outFile, string.Format("{0} min {1,3}: cycles {2,4} (+{3}), working set {4} MB, private {5} MB, GC heap {6} MB, handles {7}, GDI {8}, USER {9}, select avg {10} ms, backup scan avg {11} ms{12}",
                    label, total.Elapsed.TotalMinutes.ToString("F1"), cycles, cyclesMin, p.WorkingSet64 / 1048576, p.PrivateMemorySize64 / 1048576, gc, p.HandleCount, GetGuiResources(p.Handle, 0), GetGuiResources(p.Handle, 1),
                    cyclesMin > 0 ? (selSum / cyclesMin).ToString("F0") : "-", scans > 0 ? (scanSum / scans).ToString("F1") : "-", Environment.NewLine));
                cyclesMin = 0; selSum = 0; scanSum = 0; scans = 0;
            };
            sample("start");
            while (total.Elapsed.TotalMinutes < minutes)
            {
                int g = rnd.Next(n);
                selSum += TimeMs(delegate { gameList.SelectedIndex = g; });
                for (int i = 0; i < nt; i++) { int t = rnd.Next(nt); tabs.SelectedIndex = t; Refresh(); Pump(20); }
                btnRowsWant = rnd.Next(2) == 0 ? 8 : 48; ApplyBtnRows();
                Stopwatch sc = Stopwatch.StartNew(); RestoreForm.Scan(Store.GameDir(appDir, games[g].Name)); scanSum += sc.Elapsed.TotalMilliseconds; scans++;
                cycles++; cyclesMin++;
                Pump(200);
                if (minute.Elapsed.TotalSeconds >= 60) { sample("run"); minute.Restart(); }
            }
            sample("end");
            return File.ReadAllText(outFile);
        }

        public string PerfTest(string tag, int maxGames, int waitMs)
        {
            StringBuilder sb = new StringBuilder();
            StartPosition = FormStartPosition.Manual; Location = new Point(-3000, -3000); ShowInTaskbar = false;
            Show(); Pump(300);
            int n = games.Count, nt = tabs.TabPages.Count;
            sb.AppendLine("perf " + tag + "  games=" + n + "  tabs=" + nt + "  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("startup (process start -> window shown): " + (long)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds + " ms");
            PerfMem(sb, "start");
            if (n == 0) return sb.ToString();
            string[] tn = new string[nt];
            for (int i = 0; i < nt; i++) tn[i] = tabs.TabPages[i].Text;

            // A: first game, every tab visited at once (the background page creation has not run yet)
            blockMax = 0;
            sb.AppendLine("A. first game, tabs visited at once: select " + TimeMs(delegate { gameList.SelectedIndex = 0; }).ToString("F0") + " ms");
            if (waitMs > 0) { Pump(waitMs); sb.AppendLine("   (waited " + waitMs + " ms after the game was selected; longest UI block " + blockMax.ToString("F0") + " ms)"); }
            for (int i = 0; i < nt; i++) sb.AppendLine("   first visit " + tn[i] + ": " + TimeMs(delegate { tabs.SelectedIndex = i; }).ToString("F0") + " ms");
            sb.AppendLine("   longest UI block meanwhile: " + blockMax.ToString("F0") + " ms");
            tabs.SelectedIndex = 0;

            // B: settle (the pages are created in the background), then visit again
            blockMax = 0;
            Pump(8000);
            sb.AppendLine("B. after 8 s: longest UI block during the settle (background page creation): " + blockMax.ToString("F0") + " ms");
            for (int i = 0; i < nt; i++) sb.AppendLine("   later visit " + tn[i] + ": " + TimeMs(delegate { tabs.SelectedIndex = i; }).ToString("F0") + " ms");
            tabs.SelectedIndex = 0;
            PerfMem(sb, "after B");

            // C: through the games, every tab of each
            int cnt = Math.Min(n, maxGames);
            double selSum = 0, selMax = 0; double[] tSum = new double[nt], tMax = new double[nt]; int[] tN = new int[nt];
            blockMax = 0;
            Stopwatch all = Stopwatch.StartNew();
            for (int g = 0; g < cnt; g++)
            {
                double sel = TimeMs(delegate { gameList.SelectedIndex = g; });
                selSum += sel; if (sel > selMax) selMax = sel;
                for (int i = 0; i < nt; i++)
                {
                    double v = TimeMs(delegate { tabs.SelectedIndex = i; });
                    tSum[i] += v; tN[i]++; if (v > tMax[i]) tMax[i] = v;
                }
                tabs.SelectedIndex = 0;
                Pump(30);
                if (g == cnt / 2 || g == cnt - 1) PerfMem(sb, "after " + (g + 1) + " games");
            }
            sb.AppendLine("C. " + cnt + " games, every tab of each (" + all.ElapsedMilliseconds + " ms in total): game select avg " + (selSum / cnt).ToString("F0") + " ms, max " + selMax.ToString("F0") + " ms; longest UI block " + blockMax.ToString("F0") + " ms");
            for (int i = 0; i < nt; i++) sb.AppendLine("   tab " + tn[i] + ": avg " + (tSum[i] / Math.Max(1, tN[i])).ToString("F0") + " ms, max " + tMax[i].ToString("F0") + " ms");

            // D: what the timers do on every tick
            tabs.SelectedIndex = Math.Max(0, tabs.TabPages.IndexOf(vdTab));
            Pump(300);
            Stopwatch d = Stopwatch.StartNew();
            for (int i = 0; i < 20; i++) VdTick();
            sb.AppendLine("D. VdTick (every 100 ms): avg " + (d.Elapsed.TotalMilliseconds / 20).ToString("F1") + " ms");
            d.Restart();
            for (int i = 0; i < 20; i++) DevTick();
            sb.AppendLine("   DevTick (every 40 ms): avg " + (d.Elapsed.TotalMilliseconds / 20).ToString("F1") + " ms");

            // E: the Buttons tab with 8 and 48 rows shown (rows are not what they were in 1.10: 16 before)
            btnRowsWant = 8; ApplyBtnRows();
            int bt = tabs.TabPages.IndexOf(btnTab);
            tabs.SelectedIndex = 0; Pump(200);
            sb.AppendLine("E. Buttons tab, 8 rows shown: visit " + TimeMs(delegate { tabs.SelectedIndex = bt; }).ToString("F0") + " ms");
            btnRowsWant = 48;
            sb.AppendLine("   switching to 48 rows shown: " + TimeMs(delegate { ApplyBtnRows(); }).ToString("F0") + " ms");
            Stopwatch f = Stopwatch.StartNew();
            for (int i = 0; i < 5; i++) { vdLoading = true; FillButtonSources(); vdLoading = false; }
            sb.AppendLine("   FillButtonSources: avg " + (f.Elapsed.TotalMilliseconds / 5).ToString("F0") + " ms");
            f.Restart();
            for (int i = 0; i < 5; i++) { vdLoading = true; RefreshButtons(); vdLoading = false; }
            sb.AppendLine("   RefreshButtons: avg " + (f.Elapsed.TotalMilliseconds / 5).ToString("F0") + " ms");
            f.Restart();
            for (int i = 0; i < 100; i++) UpdateClashMarks();
            sb.AppendLine("   UpdateClashMarks: avg " + (f.Elapsed.TotalMilliseconds / 100).ToString("F2") + " ms");
            sb.AppendLine("   changing one row (the handler path: BtnWarn + VdChanged): " + TimeMs(delegate { bmOut[0].SelectedIndex = bmOut[0].SelectedIndex == 0 ? 1 : 0; }).ToString("F0") + " ms");
            btnRowsWant = 8; ApplyBtnRows();
            PerfMem(sb, "end");
            return sb.ToString();
        }


        public string UiTest(string folder)
        {
            StringBuilder sb = new StringBuilder();
            Directory.CreateDirectory(folder);
            Store.RootOverride = Path.Combine(folder, "_store");
            string iniPath = Path.Combine(folder, "dinput_proxy.ini");
            File.WriteAllLines(iniPath, new string[] { "[Hide]", "0079:0006=", "[AxisMap]", "346E:0002.Rz=DDFD:5008.X half invert", "[Shape]", "346E:0002.X=gain=110" }, IniFile.Enc);
            games.Clear();
            GameDef g = new GameDef(); g.Name = "T"; g.Folder = folder; g.Exe = "x.exe"; g.Wheel = "346E:0002";
            games.Add(g);
            gameList.Items.Clear(); gameList.Items.Add(g); gameList.SelectedIndex = 0;
            int gas = SrcIndex("DDFD:5008.X"), brake = SrcIndex("DDFD:5008.Y");
            sb.AppendLine((gas > 0 && brake > 0 ? "ok    " : "FAIL  ") + "sources listed (gas idx " + gas + ", brake idx " + brake + ")");
            sb.AppendLine((vdSrc[5].SelectedIndex == gas && vdMode[5].SelectedIndex == 1 && vdInv[5].Checked ? "ok    " : "FAIL  ") + "Rz row loaded from INI (half + invert)");
            vdSrc[1].SelectedIndex = gas; vdMode[1].SelectedIndex = 5; vdSrc2[1].SelectedIndex = brake;           // Y = combine(gas, brake)
            vdSrc[2].SelectedIndex = brake; vdMode[2].SelectedIndex = 3; vdInv[2].Checked = true;                 // Z = brake, split upper half, inverted
            vdMode[5].SelectedIndex = 0; vdInv[5].Checked = false; vdSrc[5].SelectedIndex = SrcIndex("346E:0002.Rz");   // Rz back to its own axis
            SaveVirtual();
            Chk(sb, "draft: game not running, nothing written yet", dirty && IniFile.Load(iniPath).Get("AxisCombine", "346E:0002.Y") == null);
            WriteIni();                                                                                           // the Save button
            IniFile r = IniFile.Load(iniPath);
            Chk(sb, "combine written", r.Get("AxisCombine", "346E:0002.Y") == "DDFD:5008.X DDFD:5008.Y");
            Chk(sb, "split written", r.Get("AxisMap", "346E:0002.Z") == "DDFD:5008.Y invert splithi");
            Chk(sb, "cleared row removed", r.Get("AxisMap", "346E:0002.Rz") == null);
            Chk(sb, "other sections kept", r.Get("Shape", "346E:0002.X") == "gain=110" && r.Get("Hide", "0079:0006") == "");
            RefreshVirtual();                                                                                    // read it back into the table
            Chk(sb, "table reloads combine", vdMode[1].SelectedIndex == 5 && vdSrc[1].SelectedIndex == gas && vdSrc2[1].SelectedIndex == brake);
            Chk(sb, "table reloads split", vdMode[2].SelectedIndex == 3 && vdInv[2].Checked && vdSrc[2].SelectedIndex == brake);
            Chk(sb, "unmapped row shows the device's own axis", vdSrc[0].SelectedIndex == SrcIndex("346E:0002.X") && vdSrc[0].SelectedIndex > 0 && r.Get("AxisMap", "346E:0002.X") == null);
            vdMode[7].SelectedIndex = ModeMask;                                                                   // S1 masked
            vdSrc[4].SelectedIndex = gas;                                                                         // Ry = gas, direct
            Chk(sb, "draft again: file unchanged before Save", IniFile.Load(iniPath).Get("AxisMap", "346E:0002.Ry") == null && dirty);
            WriteIni();
            r = IniFile.Load(iniPath);
            Chk(sb, "save writes the draft", r.Get("AxisMap", "346E:0002.Ry") == "DDFD:5008.X" && r.Get("Mask", "346E:0002") == "S1");
            RefreshVirtual();
            Chk(sb, "mask row reloads", vdMode[7].SelectedIndex == ModeMask);
            UndoLast();
            r = IniFile.Load(iniPath);
            Chk(sb, "undo restores the earlier file", r.Get("AxisMap", "346E:0002.Ry") == null && r.Get("Mask", "346E:0002") == null && r.Get("AxisCombine", "346E:0002.Y") == "DDFD:5008.X DDFD:5008.Y");
            Chk(sb, "session backup was made (in the store, not in the game folder)", Directory.GetFiles(Store.GameDir(appDir, "T"), "dinput_proxy.ini.bak_*").Length >= 1 && Directory.GetFiles(folder, "dinput_proxy.ini.bak_*").Length == 0);
            // presets need names
            names.Set("DDFD:5008", "X", "Kaasu"); names.Set("DDFD:5008", "Y", "Jarru");
            FillShapeDevices(); FillSources();
            int p1 = FindNamed("kaasu"), p2 = FindNamed("jarru");
            Chk(sb, "named lookup", p1 == SrcIndex("DDFD:5008.X") && p2 == SrcIndex("DDFD:5008.Y"));
            names.Remove("DDFD:5008", "X"); names.Remove("DDFD:5008", "Y");

            // game selection: follow the game that starts, never over an unsaved draft; a running game is written at once
            string folder2 = folder + "_b";
            Directory.CreateDirectory(folder2);
            string iniPath2 = Path.Combine(folder2, "dinput_proxy.ini");
            File.WriteAllLines(iniPath2, new string[] { "[Hide]", "0079:0006=" }, IniFile.Enc);
            GameDef g2 = new GameDef(); g2.Name = "T2"; g2.Folder = folder2; g2.Exe = "y.exe"; g2.Wheel = "346E:0002";
            games.Add(g2); gameList.Items.Add(g2);
            gameList.SelectedIndex = 0;
            dirty = false; ReloadUi();
            vdSrc[3].SelectedIndex = gas;                                               // a draft on game T (not running)
            Chk(sb, "draft pending on the first game", dirty && cur == games[0]);
            ApplyRunning(new int[] { 0, 0 }, new DateTime[2], false);
            ApplyRunning(new int[] { 0, 4242 }, new DateTime[] { DateTime.MinValue, DateTime.Now }, false);
            Chk(sb, "unsaved draft: no automatic switch", cur == games[0] && dirty);
            dirty = false; ReloadUi();                                                  // throw the draft away
            ApplyRunning(new int[] { 0, 0 }, new DateTime[2], false);
            ApplyRunning(new int[] { 0, 4242 }, new DateTime[] { DateTime.MinValue, DateTime.Now }, false);
            Chk(sb, "game that started is selected automatically", cur == games[1] && gameList.SelectedIndex == 1);
            vdSrc[3].SelectedIndex = gas;                                               // running game: the change is saved by the timer ...
            vdSaveTimer.Stop(); SaveVirtual();                                          // ... which is what the timer does
            Chk(sb, "running game is written at once", !dirty && IniFile.Load(iniPath2).Get("AxisMap", "346E:0002.Rx") == "DDFD:5008.X");
            Chk(sb, "other game's file untouched", IniFile.Load(iniPath).Get("AxisMap", "346E:0002.Rx") == null);

            // digital rows (axis -> button) and the buttons table
            int hb = SrcIndex("DDFD:5008.Rx");
            vdMode[4].SelectedIndex = ModeDigital; vdSrc[4].SelectedIndex = hb; vdBtnNo[4].SelectedIndex = 6; vdThr[4].Value = 30;
            vdSaveTimer.Stop(); SaveVirtual();
            IniFile dr = IniFile.Load(iniPath2);
            Chk(sb, "digital row writes [AxisButton], link and mask", (dr.Get("AxisButton", "346E:0002.B6") ?? "").StartsWith("DDFD:5008.Rx 19660") && dr.Get("Gui", "346E:0002.Ry") == "B6" && (dr.Get("Mask", "346E:0002") ?? "").Contains("Ry"));
            RefreshVirtual();
            Chk(sb, "digital row reloads", vdMode[4].SelectedIndex == ModeDigital && vdBtnNo[4].SelectedIndex == 6 && vdThr[4].Value == 30 && vdSrc[4].SelectedIndex == hb);
            int up = btnSrcKeys.IndexOf("0079:0006.B10") + 1;
            bmOut[0].SelectedIndex = 3; bmSrc[0].SelectedIndex = up;
            vdSaveTimer.Stop(); SaveVirtual();
            Chk(sb, "button row writes [ButtonMap]", IniFile.Load(iniPath2).Get("ButtonMap", "346E:0002.B3") == "0079:0006.B10");
            string oldB9 = names.Get("0079:0006", "B9");
            names.Set("0079:0006", "B9", "DOWN");
            vdLoading = true; FillButtonSources(); vdLoading = false;      // the list follows the names (also with "only named" on in the user's settings)
            MapNamedButtons(); vdSaveTimer.Stop(); SaveVirtual();
            Chk(sb, "named buttons map to the same number", IniFile.Load(iniPath2).Get("ButtonMap", "346E:0002.B9") == "0079:0006.B9");
            names.Set("FFFF:0001", "B20", "GHOST");                                                              // remembered name of a device that is not connected
            FillShapeDevices(); vdLoading = true; FillButtonSources(); vdLoading = false;
            MapNamedButtons(); vdSaveTimer.Stop(); SaveVirtual();
            Chk(sb, "named buttons of a disconnected device are not mapped", !string.Join("\n", IniFile.Load(iniPath2).Lines.ToArray()).Contains("FFFF:0001.") && bmInfo.Text.Contains(Loc.T("btn.mapped.offline").Substring(0, 8)));
            names.Remove("FFFF:0001", "B20");
            RefreshVirtual();
            up = btnSrcKeys.IndexOf("0079:0006.B10") + 1;                                                   // the list follows the names, so the index can move
            Chk(sb, "button rows reload", bmOut[0].SelectedIndex == 3 && bmSrc[0].SelectedIndex == up);
            int dn = btnSrcKeys.IndexOf("0079:0006.B9") + 1;                                                    // a second source on the same out button
            bmOut[1].SelectedIndex = 3; bmSrc[1].SelectedIndex = dn;
            vdSaveTimer.Stop(); SaveVirtual();
            Chk(sb, "two sources on one out button: one key with both", IniFile.Load(iniPath2).Get("ButtonMap", "346E:0002.B3") == "0079:0006.B10 0079:0006.B9");
            RefreshVirtual();
            Chk(sb, "two sources reload as two rows", bmOut[0].SelectedIndex == 3 && bmOut[1].SelectedIndex == 3 && bmSrc[0].SelectedIndex == up && bmSrc[1].SelectedIndex == dn);
            bmOut[1].SelectedIndex = 0; bmSrc[1].SelectedIndex = 0; vdSaveTimer.Stop(); SaveVirtual();
            vdMode[4].SelectedIndex = 0; vdSrc[4].SelectedIndex = SrcIndex("346E:0002.Ry");
            vdSaveTimer.Stop(); SaveVirtual();
            dr = IniFile.Load(iniPath2);
            Chk(sb, "leaving digital removes the button and the link", dr.Get("AxisButton", "346E:0002.B6") == null && dr.Get("Gui", "346E:0002.Ry") == null);
            if (oldB9 != null) names.Set("0079:0006", "B9", oldB9); else names.Remove("0079:0006", "B9");
            for (int q = 0; q < BtnRows; q++) { bmOut[q].SelectedIndex = 0; bmSrc[q].SelectedIndex = 0; }
            vdSaveTimer.Stop(); SaveVirtual();
            btnRowsWant = 8; ApplyBtnRows();
            Chk(sb, "button rows: 8 shown by default", btnRowsShown == 8 && bmSrc[7].Visible == bmNum[7].Visible);
            bmOut[9].SelectedIndex = 1; bmSrc[9].SelectedIndex = 1; ApplyBtnRows();
            Chk(sb, "button rows: a row in use below the chosen count widens the view", btnRowsShown == 16 && btnRowsBox.Text == "16");
            bmOut[9].SelectedIndex = 0; bmSrc[9].SelectedIndex = 0; ApplyBtnRows();
            Chk(sb, "button rows: back to the chosen count", btnRowsShown == 8);
            vdMode[4].SelectedIndex = ModeDigital; vdSrc[4].SelectedIndex = SrcIndex("346E:0002.Ry"); vdBtnNo[4].SelectedIndex = 6;
            bmOut[0].SelectedIndex = 6; bmSrc[0].SelectedIndex = 1;
            Chk(sb, "mark: a Digital button also mapped on the Buttons tab (both sides)", DigitalClash(4) && ClashAxes(0) == "Ry");
            bmOut[0].SelectedIndex = 7;
            Chk(sb, "mark: gone when the numbers differ", !DigitalClash(4) && ClashAxes(0) == "");
            bmOut[0].SelectedIndex = 0; bmSrc[0].SelectedIndex = 0; vdMode[4].SelectedIndex = 0;
            bmOut[12].SelectedIndex = 2; bmSrc[12].SelectedIndex = 1; bmOut[0].SelectedIndex = 3; bmSrc[0].SelectedIndex = 1; ApplyBtnRows();
            ClearButtonRows(); vdSaveTimer.Stop();
            bool allEmpty = true;
            for (int q2 = 0; q2 < BtnRows; q2++) if (bmOut[q2].SelectedIndex != 0 || bmSrc[q2].SelectedIndex != 0) allEmpty = false;
            Chk(sb, "buttons: clear all rows", allEmpty && btnRowsShown == btnRowsWant);
            vdSaveTimer.Stop(); SaveVirtual();

            // advanced tab: [Caps], [FFB] Boost, [Identity], [DevType]
            advMaxBtn.Value = 12; advFlagDev.Checked = true; advFlagFmt.Checked = true; advBoost.Value = 120; advIdentity.SelectedIndex = 1; advDevType.SelectedIndex = 1; advNonExcl.Checked = true; advAutoCoop.Checked = false; advGainMag.Checked = true; advBlockSine.Checked = true; logChks[0].Checked = true;
            IniFile ai = IniFile.Load(iniPath2);
            Chk(sb, "advanced: values written", ai.Get("Caps", "MaxButtons") == "12" && ai.Get("Caps", "ClearFlags") == "A" && ai.Get("FFB", "Boost") == "120" && ai.Get("Caps", "NonExclusive") == "1" && ai.Get("Caps", "AutoCoop") == "0" && ai.Get("FFB", "GainAsMagnitude") == "1" && ai.Get("FFB", "BlockSine") == "1" && ai.Get("Log", "Always") == "1"
                && (ai.Get("Identity", "346E:0002") ?? "").StartsWith("046D:C298") && ai.Get("DevType", "346E:0002") == "10114");
            RefreshAdvanced();
            Chk(sb, "advanced: values reload", advMaxBtn.Value == 12 && advFlagDev.Checked && advFlagFmt.Checked && advBoost.Value == 120 && advIdentity.SelectedIndex == 1 && advDevType.SelectedIndex == 1 && advNonExcl.Checked && !advAutoCoop.Checked && advGainMag.Checked && advBlockSine.Checked && logChks.TrueForAll(delegate(CheckBox c) { return c.Checked; }));
            advMaxBtn.Value = 0; advFlagDev.Checked = false; advFlagFmt.Checked = false; advBoost.Value = 100; advIdentity.SelectedIndex = 0; advDevType.SelectedIndex = 0; advNonExcl.Checked = false; advAutoCoop.Checked = true; advGainMag.Checked = false; advBlockSine.Checked = false; logChks[0].Checked = false;
            ai = IniFile.Load(iniPath2);
            Chk(sb, "advanced: defaults remove the keys", ai.Get("Caps", "MaxButtons") == null && ai.Get("Caps", "ClearFlags") == null && ai.Get("FFB", "Boost") == null && ai.Get("FFB", "GainAsMagnitude") == null && ai.Get("FFB", "BlockSine") == null && ai.Get("Log", "Always") == null && ai.Get("Identity", "346E:0002") == null && ai.Get("DevType", "346E:0002") == null && ai.Get("Caps", "AutoCoop") == null && ai.Get("Caps", "NonExclusive") == null);

            // store: the working INI in the game folder and the master copy in the app's data folder
            string sf = Path.Combine(folder, "_sync"); Directory.CreateDirectory(sf);
            string sdll = Path.Combine(sf, "dinput.dll"), sgame = Path.Combine(sf, "dinput_proxy.ini"), sapp = Path.Combine(Store.GameDir(appDir, "S"), "dinput_proxy.ini");
            File.WriteAllText(sdll, "x CONTROLLAPP_live x");
            File.WriteAllText(sgame, "k=1");
            Store.Sync(appDir, "S", sf);
            Chk(sb, "store: first sync copies the game's INI", File.Exists(sapp) && File.ReadAllText(sapp) == "k=1");
            File.WriteAllText(sapp, "k=2"); Store.Sync(appDir, "S", sf);
            Chk(sb, "store: a changed master copy goes to the game folder", File.ReadAllText(sgame) == "k=2");
            File.WriteAllText(sgame, "k=3"); Store.Sync(appDir, "S", sf);
            Chk(sb, "store: a changed working copy goes to the store", File.ReadAllText(sapp) == "k=3");
            File.Delete(sgame); Store.Sync(appDir, "S", sf);
            Chk(sb, "store: a missing working INI is restored while the proxy is installed", File.Exists(sgame) && File.ReadAllText(sgame) == "k=3");
            File.WriteAllText(sgame, "k=4"); File.WriteAllText(sapp, "k=5");
            File.SetLastWriteTimeUtc(sgame, DateTime.UtcNow.AddMinutes(-5));          // both changed: the newer one (the store's) wins
            Store.Sync(appDir, "S", sf);
            Chk(sb, "store: both changed, the newer wins and the other is kept as a backup", File.ReadAllText(sgame) == "k=5" && Directory.GetFiles(Store.GameDir(appDir, "S"), "dinput_proxy.ini.bak_*").Length == 1);
            File.Delete(sdll); File.Delete(sgame); Store.Sync(appDir, "S", sf);
            Chk(sb, "store: after an uninstall (no proxy) the INI is not brought back", !File.Exists(sgame) && File.Exists(sapp));

            // log analysis: a synthetic session log next to the game's INI
            File.WriteAllLines(Path.Combine(folder2, "dinput_9999.log"), new string[] {
                "[ 1] Config: bad AxisMap line \"x\"",
                "[ 2]   Enum: \"MOZA R9 Base\" type=10316 VID=346E PID=0002 guidProduct=0",
                "[ 3] Device ready: MOZA R9 Base ptr=1 guidInstance=0",
                "[ 4] Acquire MOZA R9 Base hr=80070005",
                "[ 5] Src DDFD:5008 dw: 0 0 0 0 0 0 0 0 | btn:",
                "[ 6] Src DDFD:5008 dw: 65535 0 0 0 0 0 0 0 | btn:",
                "[ 7] Out MOZA R9 Base dw: 100 0 0 0 0 0 0 0 | btn:",
                "[ 8] Out MOZA R9 Base dw: 100 0 0 0 0 0 0 0 | btn:" }, IniFile.Enc);
            File.WriteAllLines(Path.Combine(folder2, "dinput_9998.log"), new string[] {
                "[ 1] Acquire MOZA R9 Base hr=0", "[ 2] Acquire MOZA R9 Base hr=1 (repeated 500 more times)", "[ 3] Acquire MOZA R9 Base hr=80070005",
                "[ 4] Acquire Keyboard hr=80070005", "[ 5] Acquire MOZA R9 Base hr=80070005 (repeated 30 more times)", "[ 6] Acquire MOZA R9 Base hr=0",
                "[ 7] Acquire Keyboard hr=0" }, IniFile.Enc);
            SessionLog tr = SessionLog.Parse(Path.Combine(folder2, "dinput_9998.log"), "346E", "0002");
            Chk(sb, "log: Acquire failure followed by success is not a finding", tr.AcqFail.Count == 0 && tr.AcqRecovered.Count == 2);
            SessionLog tl = SessionLog.Parse(Path.Combine(folder2, "dinput_9999.log"), "346E", "0002");
            Chk(sb, "log parsed", tl.Has && tl.WheelOpened && tl.OutSeen && tl.BadCfg.Count == 1 && tl.AcqFail.Count == 1 && tl.SrcRange("DDFD:5008", 0) == 65535);
            List<Finding> tf = Analyse(tl);
            string alltxt = "";
            foreach (Finding fi in tf) alltxt += fi.Text + "\n";
            Chk(sb, "analysis: bad line, exclusive access and hidden unused device", alltxt.Contains("x") && alltxt.Contains(Loc.T("an.f.acq").Substring(0, 20)) && alltxt.Contains(Loc.T("an.f.hiddenunused").Substring(0, 12)));
            Chk(sb, "analysis: mapped source moved but the game axis did not", alltxt.Contains(string.Format(Loc.T("an.f.nopass"), "Rx")));
            Chk(sb, "suggestions are produced", Suggest(new int[] { 0, 2, 2, 0, 0, 0, 0 }).Length > 20);
            File.WriteAllLines(Path.Combine(folder2, "dinput_9998.log"), new string[] {
                "[ 2] Device ready: MOZA R9 Base ptr=1 guidInstance=0",
                "[ 3]   EnumW: \"MOZA R9 Base\" type=10604 VID=346E PID=0002" }, IniFile.Enc);
            SessionLog tw = SessionLog.Parse(Path.Combine(folder2, "dinput_9998.log"), "346E", "0002");
            Chk(sb, "log parsed: DirectInput 1-7 list (EnumW)", tw.WheelListed && !tw.WheelHidden && tw.WheelOpened);
            File.Delete(Path.Combine(folder2, "dinput_9998.log"));
            File.Delete(Path.Combine(folder2, "dinput_9999.log"));

            // profiles / backups: save a named profile, change the settings, see the difference, restore it
            string[] good = ini.Lines.ToArray();
            File.WriteAllLines(RestoreForm.ProfilePath(Store.GameDir(appDir, "T2"), "toimiva"), good, IniFile.Enc);
            ini.Set("AxisMap", "346E:0002.Ry", "DDFD:5008.Y half");       // a "spoiled" working copy
            int nd;
            string dtext = RestoreForm.Diff(ini.Lines.ToArray(), good, out nd);
            Chk(sb, "diff shows what a restore changes", nd == 1 && dtext.Contains("[AxisMap] 346E:0002.Ry=DDFD:5008.Y half") && dtext.Contains(Loc.T("rs.diff.remove")));
            int nd0;
            RestoreForm.Diff(good, good, out nd0);
            Chk(sb, "diff of equal settings is empty", nd0 == 0);
            List<RsEntry> found = RestoreForm.Scan(Store.GameDir(appDir, "T2"));
            bool hasProfile = false, hasBackup = false;
            foreach (RsEntry re in found) { if (re.Kind == "profile" && re.Label == "toimiva") hasProfile = true; if (re.Kind == "backup") hasBackup = true; }
            Chk(sb, "scan finds the profile and the backup", hasProfile && hasBackup);
            Chk(sb, "profile names are checked", RestoreForm.ValidName("Toimiva 1_a-b") && !RestoreForm.ValidName("a/b") && !RestoreForm.ValidName(" x") && !RestoreForm.ValidName(""));
            Chk(sb, "main device: [Main]/[Winmm] Wheel read; [Main] is not a setting; other main or hidden wheel is detected",
                RestoreForm.MainOf(new string[] { "[Main]", "Wheel=346e:0002" }) == "346E:0002" && RestoreForm.MainOf(new string[] { "[Winmm]", "Wheel=0079:0006" }) == "0079:0006" && RestoreForm.MainOf(new string[] { "[Hide]" }) == null
                && RestoreForm.Same(new string[] { "[Main]", "Wheel=1111:2222", "[Mask]", "X=1" }, new string[] { "[Mask]", "X=1" })
                && RestoreForm.OtherMain(new string[] { "[Main]", "Wheel=046D:CA03" }, "346E:0002") && !RestoreForm.OtherMain(new string[] { "[Main]", "Wheel=346e:0002" }, "346E:0002")
                && RestoreForm.OtherMain(new string[] { "[Hide]", "346E:0002=" }, "346E:0002") && !RestoreForm.OtherMain(new string[] { "[Hide]", "DDFD:5008=" }, "346E:0002"));
            Chk(sb, "same settings: comments, [Names] and order do not count, a changed setting does", RestoreForm.Same(new string[] { "; a", "[Mask]", "X=1", "[Names]", "A=1", "[Shape]", "Y=2" }, new string[] { "[Shape]", "Y=2", "[Mask]", "X=1", "[Names]", "A=9" }) && !RestoreForm.Same(new string[] { "[Mask]", "X=1" }, new string[] { "[Mask]", "X=2" }));
            ini.Lines.Clear(); ini.Lines.AddRange(good); CommitIni();                      // what the Restore button does
            Chk(sb, "restore writes the chosen state to a running game", !dirty && IniFile.Load(iniPath2).Get("AxisMap", "346E:0002.Ry") == null && IniFile.Load(iniPath2).Get("AxisMap", "346E:0002.Rx") == "DDFD:5008.X");
            // presets use the names given on the Devices tab: set here in memory only
            names.Set("DDFD:5008", "X", "KAASU"); names.Set("DDFD:5008", "Y", "JARRU"); names.Set("DDFD:5008", "Z", "KYTKIN"); names.Set("DDFD:5008", "Rx", "KÄSIJARRU");
            names.Set("0079:0006", "B9", "VAIHDE ALAS"); names.Set("0079:0006", "B10", "VAIHDE YLÖS"); names.Set("0079:0006", "B6", "NAPPI");
            RefreshUiFromIni();
            Chk(sb, "preset test: names found", FindNamed("kaasu", "gas", "throttle") > 0 && FindNamed("jarru", "brake") > 0);
            {
                ApplyPresetCore(2);
                vdSaveTimer.Stop(); SaveVirtual();
                IniFile cp = IniFile.Load(iniPath2);
                ApplyPresetCore(1);
                vdSaveTimer.Stop(); SaveVirtual();
                IniFile dp = IniFile.Load(iniPath2);
                Chk(sb, "preset direct: the named axes pass straight through, the others are masked", dp.Get("AxisMap", "346E:0002.Y") != null && dp.Get("AxisMap", "346E:0002.Y").StartsWith("DDFD:5008.X") && dp.Get("AxisCombine", "346E:0002.Y") == null && dp.Get("Mask", "346E:0002") != null);
                ApplyPresetCore(2);
                vdSaveTimer.Stop(); SaveVirtual();
                Chk(sb, "preset combined pedals: Y combined, identity and button limit untouched", cp.Get("AxisCombine", "346E:0002.Y") != null && cp.Get("Identity", "346E:0002") == null && cp.Get("Caps", "MaxButtons") == null);
                ApplyPresetCore(4);
                vdSaveTimer.Stop(); SaveVirtual();
                IniFile gp = IniFile.Load(iniPath2);
                Chk(sb, "preset G27: gas Y, brake Rz, clutch S1 inverted, others masked, identity G27", (gp.Get("AxisMap", "346E:0002.Y") ?? "").EndsWith("invert") && (gp.Get("AxisMap", "346E:0002.Rz") ?? "").EndsWith("invert") && (gp.Get("AxisMap", "346E:0002.S1") ?? "").EndsWith("invert") && gp.Get("AxisMap", "346E:0002.Z") == null && (gp.Get("Mask", "346E:0002") ?? "").Contains("Z") && (gp.Get("Identity", "346E:0002") ?? "").StartsWith("046D:C29B"));
            }
            // button source list: sized by the device, named and mapped buttons always shown, "only named" keeps just those
            Chk(sb, "buttons: beyond the device's count hidden, named/mapped shown", !ButtonShown(5, 4, false, false, false) && ButtonShown(3, 4, false, false, false) && ButtonShown(9, 4, true, false, false) && ButtonShown(9, 4, false, true, false));
            Chk(sb, "buttons: only named hides the unnamed ones", !ButtonShown(1, 32, false, false, true) && ButtonShown(1, 32, true, false, true) && ButtonShown(1, 32, false, true, true));
            names.Set("FFFF:0001", "Buttons", "4"); names.Set("FFFF:0002", "Buttons", "128");
            Chk(sb, "buttons: remembered count (max 32; unknown = 32)", BtnCount("FFFF:0001") == 4 && BtnCount("FFFF:0002") == 32 && BtnCount("FFFF:0003") == 32);
            names.RemoveSection("FFFF:0001"); names.RemoveSection("FFFF:0002");
            Chk(sb, "a device that is not connected is marked", !Connected("FFFF:0001") || devReadFailed);
            sb.AppendLine(sb.ToString().Contains("FAIL") ? "FAILED" : "ALL OK");
            return sb.ToString();
        }

        static void Chk(StringBuilder sb, string name, bool ok) { sb.AppendLine((ok ? "ok    " : "FAIL  ") + name); }

        // ---------------------------------------------------------------- virtual device tab (mapping table + live view)
        string DestKey { get { return cur != null && cur.Wheel != null ? cur.Wheel.ToUpperInvariant() : null; } }

        void BuildVirtualTab()
        {
            Panel top = new Panel(); top.Dock = DockStyle.Top; top.Height = 146;
            Label pl = new Label(); pl.Text = Loc.T("vd.preset"); pl.Left = 10; pl.Top = 12; pl.AutoSize = true;
            presetBox.DropDownStyle = ComboBoxStyle.DropDownList; presetBox.Left = pl.Left + pl.PreferredWidth + 16; presetBox.Top = 8; presetBox.Width = 330; presetBox.DropDownWidth = 560;
            presetBox.Items.AddRange(new object[] { Loc.T("vd.preset0"), Loc.T("vd.preset1"), Loc.T("vd.preset2"), Loc.T("vd.preset3"), Loc.T("vd.preset4") });
            presetBox.SelectedIndex = 0;
            // choosing a layout only describes it; the table changes when the button is pressed and confirmed
            presetBox.SelectedIndexChanged += delegate { UpdatePresetInfo(); };
            presetApplyBtn.Text = Loc.T("vd.preset.apply"); presetApplyBtn.Left = presetBox.Right + 10; presetApplyBtn.Top = 6; presetApplyBtn.Width = 150; presetApplyBtn.Height = 26; presetApplyBtn.Enabled = false;
            presetApplyBtn.Click += delegate { ApplyPreset(); };
            presetDesc.Left = 10; presetDesc.Top = 36; presetDesc.Width = 940; presetDesc.Height = 52; presetDesc.AutoSize = false; presetDesc.ForeColor = SystemColors.GrayText;
            presetDesc.Text = Loc.T("vd.preset0.desc");
            presetDesc.Height = 62;
            EventHandler fitDesc = delegate { presetDesc.Width = Math.Max(300, Math.Min(940, top.ClientSize.Width - 10 - 224)); };      // the right-hand column is the Analyse button and its checkbox
            top.Resize += fitDesc; fitDesc(null, EventArgs.Empty);
            vdSaveInfo.Left = 10; vdSaveInfo.Top = 100; vdSaveInfo.Width = 940; vdSaveInfo.Height = 20; vdSaveInfo.AutoSize = false; vdSaveInfo.ForeColor = SystemColors.GrayText;
            vdInfo.Left = 10; vdInfo.Top = 122; vdInfo.Width = 940; vdInfo.Height = 20; vdInfo.ForeColor = SystemColors.GrayText;
            top.Controls.Add(pl); top.Controls.Add(presetBox); top.Controls.Add(presetApplyBtn); top.Controls.Add(presetDesc); top.Controls.Add(vdSaveInfo); top.Controls.Add(vdInfo);
            BuildAnalyseButton(top);

            TableLayoutPanel t = new TableLayoutPanel();
            t.Dock = DockStyle.Top; t.AutoSize = true; t.ColumnCount = 9; t.Padding = new Padding(8, 4, 8, 0);
            int[] widths = new int[] { 44, 205, 185, 62, 205, 44, 120, 48, 80 };      // sum 993: fits the tab at the default window width (column 5 repeats the game axis next to what the game sees)
            for (int c = 0; c < widths.Length; c++) t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, widths[c]));
            string[] heads = new string[] { "", Loc.T("vd.h.src"), Loc.T("vd.h.mode"), Loc.T("vd.h.inv"), Loc.T("vd.h.src2"), Loc.T("vd.h.live"), "", "", Loc.T("vd.h.range") };      // no heading over the axis names on the left; "Game sees" spans the "-> X" cell and the bar
            for (int c = 0; c < heads.Length; c++)
            {
                if (heads[c].Length == 0) continue;
                Label h = new Label(); h.Text = heads[c]; h.AutoSize = true; h.Font = new Font(Font, FontStyle.Bold); t.Controls.Add(h, c, 0);
                if (c == 5) t.SetColumnSpan(h, 2);
                if (c == 8) vdRangeHead = h;
            }
            for (int i = 0; i < 8; i++)
            {
                int idx = i;
                Label n = new Label(); n.Text = AxisNames[i]; n.AutoSize = false; n.Width = 40; n.Height = 24; n.TextAlign = ContentAlignment.MiddleLeft; n.Font = new Font(Font, FontStyle.Bold);
                vdSrc[i] = new NoWheelCombo(); vdSrc[i].DropDownStyle = ComboBoxStyle.DropDownList; vdSrc[i].Width = 195;
                ((NoWheelCombo)vdSrc[i]).Offline = delegate(int x) { return x > 0 && x <= srcKeys.Count && !Connected(srcKeys[x - 1]); };
                vdMode[i] = new NoWheelCombo(); vdMode[i].DropDownStyle = ComboBoxStyle.DropDownList; vdMode[i].Width = 175; vdMode[i].Items.AddRange(ModeNames); vdMode[i].SelectedIndex = 0;
                vdInv[i] = new CheckBox(); vdInv[i].AutoSize = true;
                vdSrc2[i] = new NoWheelCombo(); vdSrc2[i].DropDownStyle = ComboBoxStyle.DropDownList; vdSrc2[i].Width = 195; vdSrc2[i].Enabled = false; vdSrc2[i].Visible = false;
                ((NoWheelCombo)vdSrc2[i]).Offline = delegate(int x) { return x > 0 && x <= srcKeys.Count && !Connected(srcKeys[x - 1]); };
                vdDig[i] = new Panel(); vdDig[i].Width = 195; vdDig[i].Height = 24; vdDig[i].Visible = false;
                vdBtnNo[i] = new NoWheelCombo(); vdBtnNo[i].DropDownStyle = ComboBoxStyle.DropDownList; vdBtnNo[i].Width = 76; vdBtnNo[i].Left = 22; vdBtnNo[i].Top = 0;
                vdBtnNo[i].Items.Add(Loc.T("vd.btn.choose"));
                for (int b = 0; b < 32; b++) vdBtnNo[i].Items.Add(BKey(b));
                vdBtnNo[i].SelectedIndex = 0;
                vdThr[i] = new NumericUpDown(); vdThr[i].Minimum = 1; vdThr[i].Maximum = 100; vdThr[i].Value = 50; vdThr[i].Width = 52; vdThr[i].Left = 104; vdThr[i].Top = 1;
                Label pct = new Label(); pct.Text = "%"; pct.AutoSize = true; pct.Left = 162; pct.Top = 5;
                vdWarn[i] = ClashMark(); vdWarn[i].Left = 0; vdWarn[i].Top = 1;
                vdDig[i].Controls.Add(vdBtnNo[i]); vdDig[i].Controls.Add(vdThr[i]); vdDig[i].Controls.Add(pct); vdDig[i].Controls.Add(vdWarn[i]);
                Panel cell2 = new Panel(); cell2.Width = 200; cell2.Height = 26; cell2.Margin = new Padding(0);
                cell2.Controls.Add(vdSrc2[i]); cell2.Controls.Add(vdDig[i]);
                Label outEcho = new Label(); outEcho.Text = "\u2192 " + AxisNames[i]; outEcho.AutoSize = false; outEcho.Width = 44; outEcho.Height = 24; outEcho.TextAlign = ContentAlignment.MiddleLeft; outEcho.Font = new Font(Font, FontStyle.Bold); outEcho.Margin = new Padding(0, 3, 0, 3); vdEcho[i] = outEcho;
                vdBar[i] = new Bar(); vdBar[i].Width = 110; vdBar[i].Margin = new Padding(3, 6, 3, 4); vdBar[i].Dim = true;
                vdVal[i] = new Label(); vdVal[i].ForeColor = DimText; vdVal[i].Text = "-"; vdVal[i].AutoSize = false; vdVal[i].Width = 46; vdVal[i].Height = 24; vdVal[i].TextAlign = ContentAlignment.MiddleRight;
                vdRange[i] = new Label(); vdRange[i].Text = ""; vdRange[i].AutoSize = false; vdRange[i].Width = 74; vdRange[i].Height = 24; vdRange[i].TextAlign = ContentAlignment.MiddleLeft; vdRange[i].ForeColor = SystemColors.GrayText;
                EventHandler changed = delegate(object sender, EventArgs ev)
                {
                    // steering (X) taken from another device than the main device: ask first, Cancel puts the earlier source back
                    if (idx == 0 && sender == vdSrc[0])
                    {
                        if (!vdLoading && ConfirmSteerElsewhere())
                        {
                            vdLoading = true; vdSrc[0].SelectedIndex = vdSrc0Last; vdLoading = false;
                            RowState(0); ShowModeDesc(0);
                            return;
                        }
                        vdSrc0Last = vdSrc[0].SelectedIndex;
                    }
                    // choosing "(no mapping)" on a plain row switches the row to Mask (held at the centre); the source cell stays selectable
                    if (!vdLoading && vdSrc[idx].SelectedIndex == 0 && vdMode[idx].SelectedIndex != ModeMask)
                    {
                        vdLoading = true; vdMode[idx].SelectedIndex = ModeMask; vdLoading = false;
                    }
                    // picking a real source on a masked row switches it back to plain
                    else if (!vdLoading && sender == vdSrc[idx] && vdSrc[idx].SelectedIndex > 0 && vdMode[idx].SelectedIndex == ModeMask)
                    {
                        vdLoading = true; vdMode[idx].SelectedIndex = 0; vdLoading = false;
                    }
                    RowState(idx); ShowModeDesc(idx); VdChanged();
                };
                vdSrc[i].SelectedIndexChanged += changed; vdMode[i].SelectedIndexChanged += changed;
                vdInv[i].CheckedChanged += changed; vdSrc2[i].SelectedIndexChanged += changed;
                vdBtnNo[i].SelectedIndexChanged += changed; vdThr[i].ValueChanged += changed;
                EventHandler focus = delegate { ShowModeDesc(idx); };
                vdSrc[i].Enter += focus; vdMode[i].Enter += focus; vdSrc2[i].Enter += focus; vdInv[i].Enter += focus; vdBtnNo[i].Enter += focus; vdThr[i].Enter += focus;
                t.Controls.Add(n, 0, i + 1); t.Controls.Add(vdSrc[i], 1, i + 1); t.Controls.Add(vdMode[i], 2, i + 1); t.Controls.Add(vdInv[i], 3, i + 1);
                t.Controls.Add(cell2, 4, i + 1); t.Controls.Add(outEcho, 5, i + 1); t.Controls.Add(vdBar[i], 6, i + 1); t.Controls.Add(vdVal[i], 7, i + 1); t.Controls.Add(vdRange[i], 8, i + 1);
            }
            vdModeDesc.Dock = DockStyle.Top; vdModeDesc.Height = 72; vdModeDesc.Padding = new Padding(10, 6, 10, 0); vdModeDesc.Text = Loc.T("vd.modedesc.none");
            Label bl = new Label(); bl.Text = Loc.T("vd.btnhint"); bl.Dock = DockStyle.Top; bl.Height = 24; bl.Padding = new Padding(10, 6, 0, 0); bl.ForeColor = SystemColors.GrayText;
            FlowLayoutPanel cells = new FlowLayoutPanel(); cells.Dock = DockStyle.Fill; cells.Padding = new Padding(10, 2, 10, 2); cells.AutoScroll = true;
            for (int b = 0; b < 128; b++)
            {
                Label c = new Label(); c.Text = (b + 1).ToString(); c.Width = 30; c.Height = 20; c.TextAlign = ContentAlignment.MiddleCenter;
                c.BorderStyle = BorderStyle.FixedSingle; c.Margin = new Padding(2); c.BackColor = DimBack; c.ForeColor = DimText; c.Font = new Font(Font.FontFamily, 8f);
                vdBtn[b] = c; cells.Controls.Add(c);
            }
            vdTab.Controls.Add(cells); vdTab.Controls.Add(bl); vdTab.Controls.Add(vdModeDesc); vdTab.Controls.Add(t); vdTab.Controls.Add(top);
            vdTab.AutoScroll = false;
            tabs.TabPages.Add(vdTab);

            vdTimer.Interval = 100; vdTimer.Tick += delegate { VdTick(); };
            vdSaveTimer.Interval = 400; vdSaveTimer.Tick += delegate { vdSaveTimer.Stop(); SaveVirtual(); };
            tabs.SelectedIndexChanged += delegate { if (tabs.SelectedTab == vdTab || tabs.SelectedTab == btnTab) vdTimer.Start(); else vdTimer.Stop(); };
        }

        static string ShortNum(int v)     // compact range text: 65535 stays, 1073741824 -> 1073M (the live range column is narrow)
        {
            long a = Math.Abs((long)v);
            return a >= 1000000 ? (v / 1000000) + "M" : v.ToString();
        }

        string SrcTitle(string key)       // "VID:PID.AXIS" -> "Device – X (Name)"
        {
            int dot = key.IndexOf('.');
            string dev = key.Substring(0, dot), ax = key.Substring(dot + 1);
            string n = names.Get(dev, ax);
            return DeviceTitle(dev) + " – " + ax + (string.IsNullOrEmpty(n) ? "" : "  (" + n + ")") + OfflineTag(dev);
        }

        // lists every axis of every known device as a source choice
        void FillSources()
        {
            string[] keep = new string[16];
            for (int i = 0; i < 8; i++)
            {
                int a = vdSrc[i].SelectedIndex, b = vdSrc2[i].SelectedIndex;
                keep[i] = a > 0 && a <= srcKeys.Count ? srcKeys[a - 1] : null;
                keep[8 + i] = b > 0 && b <= srcKeys.Count ? srcKeys[b - 1] : null;
            }
            srcKeys.Clear();
            foreach (string dev in shapeDevKeys)
            {
                int mask = 0xFF;
                for (int d = 0; d < devInfos.Length; d++)
                    if (string.Format("{0:X4}:{1:X4}", devInfos[d].vid, devInfos[d].pid) == dev) mask = devInfos[d].axisMask != 0 ? devInfos[d].axisMask : 0xFF;
                for (int a = 0; a < 8; a++) if ((mask & (1 << a)) != 0) srcKeys.Add(dev + "." + AxisNames[a]);
            }
            vdLoading = true;
            for (int i = 0; i < 8; i++)
            {
                foreach (ComboBox cb in new ComboBox[] { vdSrc[i], vdSrc2[i] })
                {
                    cb.Items.Clear();
                    cb.Items.Add(Loc.T("vd.none"));
                    foreach (string k in srcKeys) cb.Items.Add(SrcTitle(k));
                    cb.SelectedIndex = 0;
                }
                if (keep[i] != null) vdSrc[i].SelectedIndex = srcKeys.IndexOf(keep[i]) + 1;
                if (keep[8 + i] != null) vdSrc2[i].SelectedIndex = srcKeys.IndexOf(keep[8 + i]) + 1;
            }
            vdLoading = false;
        }

        int SrcIndex(string key)
        {
            for (int i = 0; i < srcKeys.Count; i++) if (string.Equals(srcKeys[i], key, StringComparison.OrdinalIgnoreCase)) return i + 1;
            return 0;
        }

        // reads the destination device's axis rows from the INI into the table
        void RefreshVirtual()
        {
            if (ini == null || DestKey == null) return;
            FillSources();
            vdLoading = true;
            for (int a = 0; a < 8; a++)
            {
                string key = DestKey + "." + AxisNames[a];
                string comb = ini.Get("AxisCombine", key), map = ini.Get("AxisMap", key);
                vdSrc[a].SelectedIndex = 0; vdSrc2[a].SelectedIndex = 0; vdMode[a].SelectedIndex = 0; vdInv[a].Checked = false;
                vdBtnNo[a].SelectedIndex = 0; vdThr[a].Value = 50;
                string dig = ini.Get("Gui", key);                                  // "B5": this row is a digital (axis -> button) row
                string ab = dig != null ? ini.Get("AxisButton", DestKey + "." + dig) : null;
                int digNo = -1;
                if (ab != null && dig.Length > 1 && int.TryParse(dig.Substring(1), out digNo) && digNo >= 1 && digNo <= 32)
                {
                    string[] tk = ab.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tk.Length >= 1) vdSrc[a].SelectedIndex = SrcIndex(tk[0]);
                    vdMode[a].SelectedIndex = ModeDigital;
                    vdBtnNo[a].SelectedIndex = digNo;
                    for (int k = 1; k < tk.Length; k++)
                    {
                        int thr;
                        if (string.Equals(tk[k], "invert", StringComparison.OrdinalIgnoreCase)) vdInv[a].Checked = true;
                        else if (int.TryParse(tk[k], out thr)) vdThr[a].Value = Math.Max(1, Math.Min(100, (int)Math.Round(thr * 100.0 / 65535)));
                    }
                }
                else if (comb != null)
                {
                    string[] tk = comb.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tk.Length >= 2) { vdSrc[a].SelectedIndex = SrcIndex(tk[0]); vdSrc2[a].SelectedIndex = SrcIndex(tk[1]); }
                    vdMode[a].SelectedIndex = ModeCombine;
                    for (int k = 2; k < tk.Length; k++) if (string.Equals(tk[k], "invert", StringComparison.OrdinalIgnoreCase)) vdInv[a].Checked = true;
                }
                else if (map != null)
                {
                    string[] tk = map.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tk.Length >= 1) vdSrc[a].SelectedIndex = SrcIndex(tk[0]);
                    for (int k = 1; k < tk.Length; k++)
                    {
                        if (string.Equals(tk[k], "invert", StringComparison.OrdinalIgnoreCase)) vdInv[a].Checked = true;
                        int m = Array.IndexOf(ModeToks, tk[k].ToLowerInvariant());
                        if (m > 0 && m < ModeCombine) vdMode[a].SelectedIndex = m;
                    }
                }
                else if (MaskedAxes().Contains(AxisNames[a])) vdMode[a].SelectedIndex = ModeMask;
                if (vdSrc[a].SelectedIndex == 0 && vdMode[a].SelectedIndex != ModeCombine && vdMode[a].SelectedIndex != ModeMask) SetOwnAxis(a);       // nothing mapped: the device's own axis passes through
                if (vdSrc[a].SelectedIndex == 0) vdMode[a].SelectedIndex = ModeMask;                                                                  // still no source: held at the centre, mode locked
                RowState(a);
            }
            RefreshButtons();
            vdLoading = false;
        }

        // axes of the destination device listed in [Mask] (kept at the centre value instead of the real, often static, value)
        List<string> MaskedAxes()
        {
            List<string> r = new List<string>();
            string v = ini == null || DestKey == null ? null : ini.Get("Mask", DestKey);
            if (v != null)
                foreach (string t in v.Split(new char[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
                    foreach (string n in AxisNames) if (string.Equals(t, n, StringComparison.OrdinalIgnoreCase)) r.Add(n);
            return r;
        }

        string OwnKey(int a) { return DestKey + "." + AxisNames[a]; }

        // selects the destination device's own axis a as the source (informational: it is what the game gets when nothing is mapped)
        void SetOwnAxis(int a)
        {
            if (DestKey == null) return;
            bool was = vdLoading;
            vdLoading = true;
            int s = SrcIndex(OwnKey(a));
            if (s > 0) vdSrc[a].SelectedIndex = s;
            vdLoading = was;
        }

        int vdSrc0Last;             // source index of the X row before the current change

        // true when the user chose not to continue: X was set to an axis of another device than the main device (the earlier source was the
        // main device's own X or none). The main device then only runs the force feedback and its own steering does not reach the game.
        bool ConfirmSteerElsewhere()
        {
            int sx = vdSrc[0].SelectedIndex;
            if (DestKey == null || sx <= 0 || sx > srcKeys.Count || vdMode[0].SelectedIndex == ModeMask) return false;
            string key = srcKeys[sx - 1];
            if (string.Equals(key, OwnKey(0), StringComparison.OrdinalIgnoreCase)) return false;
            if (vdSrc0Last > 0 && vdSrc0Last <= srcKeys.Count && !string.Equals(srcKeys[vdSrc0Last - 1], OwnKey(0), StringComparison.OrdinalIgnoreCase)) return false;      // already moved away earlier
            int dot = key.IndexOf('.');
            string dev = dot > 0 ? key.Substring(0, dot) : key;
            return Dlg.Show(this, Loc.T("steer.confirm", DeviceTitle(DestKey), DeviceTitle(dev)), "CtrlMuxr", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes;
        }

        bool IsOwnPlain(int a)      // own axis, direct, not inverted: the row writes nothing to the INI
        {
            int si = vdSrc[a].SelectedIndex;
            return DestKey != null && si > 0 && si <= srcKeys.Count && vdMode[a].SelectedIndex == 0 && !vdInv[a].Checked
                && string.Equals(srcKeys[si - 1], OwnKey(a), StringComparison.OrdinalIgnoreCase);
        }

        // enables/greys the cells of a row for its mode
        void RowState(int a)
        {
            int mode = vdMode[a].SelectedIndex;
            vdMode[a].Enabled = vdSrc[a].SelectedIndex > 0;                    // no source: the row is always Mask (held at the centre)
            vdSrc2[a].Visible = vdSrc2[a].Enabled = mode == ModeCombine;
            vdDig[a].Visible = mode == ModeDigital;
            vdSrc[a].ForeColor = IsOwnPlain(a) || mode == ModeMask ? SystemColors.GrayText : SystemColors.WindowText;
            vdEcho[a].ForeColor = RowUnmapped(a) ? DimText : SystemColors.ControlText;
        }

        // no source (or masked): the game gets the axis held at the centre, so its live bar is greyed
        bool RowUnmapped(int a) { return vdSrc[a].SelectedIndex == 0 || vdMode[a].SelectedIndex == ModeMask || vdMode[a].SelectedIndex == ModeDigital; }

        static readonly string[] PedalWords = new string[] { "kaasu", "jarru", "kytkin", "käsijarru", "gas", "throttle", "brake", "clutch", "handbrake", "pedal" };

        bool SourceLooksLikePedal(int src)
        {
            if (src <= 0 || src > srcKeys.Count) return false;
            string k = srcKeys[src - 1];
            int dot = k.IndexOf('.');
            string n = (names.Get(k.Substring(0, dot), k.Substring(dot + 1)) ?? "").ToLowerInvariant();
            foreach (string w in PedalWords) if (n.Contains(w)) return true;
            return false;
        }

        // a Digital row (Virtual device tab) and a row of the Buttons tab with the same game button: allowed (pressed when either is), but worth a notice
        bool DigitalClash(int a)
        {
            if (vdMode[a].SelectedIndex != ModeDigital || vdSrc[a].SelectedIndex <= 0 || vdBtnNo[a].SelectedIndex <= 0) return false;
            for (int r = 0; r < BtnRows; r++) if (bmSrc[r].SelectedIndex > 0 && bmOut[r].SelectedIndex == vdBtnNo[a].SelectedIndex) return true;
            return false;
        }

        // the "!" label (orange square); the text is set when it is shown
        Label ClashMark()
        {
            Label m = new Label(); m.Text = "!"; m.AutoSize = false; m.Width = 16; m.Height = 22; m.TextAlign = ContentAlignment.MiddleCenter;
            m.Font = new Font(Font, FontStyle.Bold); m.ForeColor = Color.White; m.BackColor = Color.DarkOrange; m.Visible = false;
            return m;
        }

        // the Digital axes whose game button is also the out button of Buttons row r
        string ClashAxes(int r)
        {
            if (bmSrc[r].SelectedIndex <= 0 || bmOut[r].SelectedIndex <= 0) return "";
            List<string> l = new List<string>();
            for (int a = 0; a < 8; a++) if (DigitalClash(a) && vdBtnNo[a].SelectedIndex == bmOut[r].SelectedIndex) l.Add(AxisNames[a]);
            return string.Join(", ", l.ToArray());
        }

        void UpdateClashMarks()
        {
            for (int a = 0; a < 8; a++)
            {
                bool c = DigitalClash(a);
                vdWarn[a].Visible = c;
                if (c) tip.SetToolTip(vdWarn[a], string.Format(Loc.T("tt.clash.digital"), BKey(vdBtnNo[a].SelectedIndex - 1)));
            }
            for (int r = 0; r < BtnRows; r++)
            {
                string axes = ClashAxes(r);
                bmWarn[r].Visible = axes.Length > 0;
                if (axes.Length > 0) tip.SetToolTip(bmWarn[r], string.Format(Loc.T("tt.clash.btn"), axes));
            }
        }
        // what the row's mode means, in plain words, plus a warning when the choice looks wrong
        void ShowModeDesc(int a)
        {
            int mode = vdMode[a].SelectedIndex;
            string s = AxisNames[a] + ": " + Loc.T("mode.desc." + mode);
            if ((mode == 3 || mode == 4) && SourceLooksLikePedal(vdSrc[a].SelectedIndex)) s += "\n" + Loc.T("mode.warn.split");
            if (mode == ModeCombine && vdSrc2[a].SelectedIndex <= 0) s += "\n" + Loc.T("mode.warn.combine");
            int sx = vdSrc[a].SelectedIndex;
            if (a == 0 && mode != ModeMask && DestKey != null && sx > 0 && sx <= srcKeys.Count && !string.Equals(srcKeys[sx - 1], OwnKey(0), StringComparison.OrdinalIgnoreCase))
                s = Loc.T("mode.warn.steer", DeviceTitle(DestKey)) + "\n" + s;      // first: the end of a long text is clipped
            if (mode == ModeDigital)
            {
                if (vdSrc[a].SelectedIndex <= 0 || vdBtnNo[a].SelectedIndex <= 0) s += "\n" + Loc.T("mode.warn.digital");
            }
            vdModeDesc.Text = s;
        }

        void UpdatePresetInfo()
        {
            int p = presetBox.SelectedIndex;
            presetApplyBtn.Enabled = p > 0;
            presetDesc.Text = Loc.T("vd.preset" + Math.Max(0, p) + ".desc");
        }

        // running game: save live after a short pause; otherwise only the draft changes (Save writes it)
        void VdChanged()
        {
            if (vdLoading || ini == null || DestKey == null || cur == null) return;
            BtnWarn();
            if (cur.Running) { vdSaveTimer.Stop(); vdSaveTimer.Start(); }
            else SaveVirtual();
        }

        void SaveVirtual()
        {
            if (cur == null || DestKey == null) return;
            try
            {
                if (!dirty) ini = IniFile.Load(IniPath);            // keeps edits made elsewhere; a draft is kept as it is
                List<string> mask = new List<string>();
                for (int a = 0; a < 8; a++)
                {
                    string key = OwnKey(a);
                    ini.Remove("AxisMap", key); ini.Remove("AxisCombine", key);
                    string oldDig = ini.Get("Gui", key);                          // forget the earlier digital link of this row
                    if (oldDig != null) { ini.Remove("AxisButton", DestKey + "." + oldDig); ini.Remove("Gui", key); }
                    int mode = vdMode[a].SelectedIndex;
                    if (mode == ModeMask) { mask.Add(AxisNames[a]); continue; }
                    int si = vdSrc[a].SelectedIndex;
                    if (mode == ModeDigital)
                    {
                        mask.Add(AxisNames[a]);                                   // the axis itself is not passed on: the game gets the button
                        int bn = vdBtnNo[a].SelectedIndex - 1;
                        if (si <= 0 || si > srcKeys.Count || bn < 0) continue;
                        int thr = (int)Math.Round((double)vdThr[a].Value * 65535 / 100);
                        ini.Set("AxisButton", DestKey + "." + BKey(bn), srcKeys[si - 1] + " " + thr + (vdInv[a].Checked ? " invert" : ""));
                        ini.Set("Gui", key, BKey(bn));
                        continue;
                    }
                    if (si <= 0 || si > srcKeys.Count) continue;
                    if (IsOwnPlain(a)) continue;                    // the device's own axis, unchanged: nothing to write
                    string src = srcKeys[si - 1];
                    string inv = vdInv[a].Checked ? " invert" : "";
                    if (mode == ModeCombine)
                    {
                        int s2 = vdSrc2[a].SelectedIndex;
                        if (s2 <= 0 || s2 > srcKeys.Count) continue;
                        ini.Set("AxisCombine", key, src + " " + srcKeys[s2 - 1] + inv);
                    }
                    else ini.Set("AxisMap", key, src + inv + (ModeToks[mode].Length > 0 ? " " + ModeToks[mode] : ""));
                }
                if (mask.Count > 0) ini.Set("Mask", DestKey, string.Join(" ", mask.ToArray())); else ini.Remove("Mask", DestKey);
                WriteButtons();
                SyncNames(ini);
                CommitIni();
            }
            catch (Exception ex) { Dlg.Show(this, Loc.T("msg.savefail") + ex.Message, "CtrlMuxr"); }
        }

        // first source whose user-given name equals one of the words
        int FindNamed(params string[] words)
        {
            foreach (string dev in shapeDevKeys)
                for (int a = 0; a < 8; a++)
                {
                    string n = names.Get(dev, AxisNames[a]);
                    if (string.IsNullOrEmpty(n)) continue;
                    foreach (string w in words) if (string.Equals(n.Trim(), w, StringComparison.OrdinalIgnoreCase)) return SrcIndex(dev + "." + AxisNames[a]);
                }
            return 0;
        }

        // the first axis of any device whose name is of the given kind (see ControlWords: 1 throttle, 2 brake, 3 clutch, 4 handbrake); 0 = none
        int FindNamedClass(int cls)
        {
            foreach (string dev in shapeDevKeys)
                for (int a = 0; a < 8; a++)
                    if (Classify(names.Get(dev, AxisNames[a])) == cls) return SrcIndex(dev + "." + AxisNames[a]);
            return 0;
        }

        // the layout needs to know which axis is the throttle, brake, clutch and handbrake. A name that contains a usual word is taken as that
        // (a guess, see ControlWords); otherwise the user is asked: the names themselves can be anything. Returns the source indexes, or null on Cancel.
        int[] AskPresetAxes(int[] guess)
        {
            string[] labels = new string[] { Loc.T("pa.gas"), Loc.T("pa.brake"), Loc.T("pa.clutch"), Loc.T("pa.hand") };
            ComboBox[] cb = new ComboBox[4];
            using (Form f = new Form())
            {
                f.Text = Loc.T("pa.title"); f.Font = Font; f.FormBorderStyle = FormBorderStyle.FixedDialog; f.MaximizeBox = false; f.MinimizeBox = false; f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(560, 250);
                Label intro = new Label(); intro.Text = Loc.T("pa.intro"); intro.Left = 12; intro.Top = 10; intro.Width = 536; intro.Height = 62;
                f.Controls.Add(intro);
                for (int k = 0; k < 4; k++)
                {
                    Label l = new Label(); l.Text = labels[k]; l.Left = 12; l.Top = 82 + k * 32 + 4; l.Width = 130;
                    cb[k] = new NoWheelCombo(); cb[k].DropDownStyle = ComboBoxStyle.DropDownList; cb[k].Left = 150; cb[k].Top = 82 + k * 32; cb[k].Width = 398;
                    for (int n = 0; n < vdSrc[0].Items.Count; n++) cb[k].Items.Add(vdSrc[0].Items[n]);
                    cb[k].SelectedIndex = guess != null && guess[k] > 0 && guess[k] < cb[k].Items.Count ? guess[k] : 0;
                    f.Controls.Add(l); f.Controls.Add(cb[k]);
                }
                Button ok = new Button(), cancel = new Button();
                ok.Text = Loc.T("dlg.ok"); ok.Left = 380; ok.Top = 214; ok.Width = 80; cancel.Text = Loc.T("dlg.cancel"); cancel.Left = 468; cancel.Top = 214; cancel.Width = 80; cancel.DialogResult = DialogResult.Cancel;
                ok.Click += delegate
                {
                    if (cb[0].SelectedIndex <= 0 || cb[1].SelectedIndex <= 0) { Dlg.Show(f, Loc.T("msg.namefirst"), "CtrlMuxr"); return; }
                    f.DialogResult = DialogResult.OK;
                };
                f.Controls.Add(ok); f.Controls.Add(cancel); f.AcceptButton = ok; f.CancelButton = cancel;
                if (f.ShowDialog(this) != DialogResult.OK) return null;
                int[] r = new int[4];
                for (int k = 0; k < 4; k++) r[k] = cb[k].SelectedIndex;
                return r;
            }
        }

        void ApplyPreset()
        {
            int p = presetBox.SelectedIndex;
            if (p <= 0 || cur == null || DestKey == null) return;
            int[] pick = new int[] { FindNamedClass(1), FindNamedClass(2), FindNamedClass(3), FindNamedClass(4) };
            if (pick[0] == 0 || pick[1] == 0) { pick = AskPresetAxes(pick); if (pick == null) return; }
            if (Dlg.Show(this, Loc.T("msg.preset", cur.Name), "CtrlMuxr", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            ApplyPresetCore(p, pick);
        }

        // 1 direct (named axes straight through), 2 combined pedal axis Y, 3 separate half axes
        void ApplyPresetCore(int p) { ApplyPresetCore(p, null); }

        // pick = the axes the user chose (throttle, brake, clutch, handbrake as source indexes); null = found from the names
        void ApplyPresetCore(int p, int[] pick)
        {
            int gas = pick != null ? pick[0] : FindNamedClass(1), brake = pick != null ? pick[1] : FindNamedClass(2), clutch = pick != null ? pick[2] : FindNamedClass(3), hand = pick != null ? pick[3] : FindNamedClass(4);
            if (gas == 0 || brake == 0) return;
            presetBox.SelectedIndex = 0;
            vdLoading = true;
            for (int a = 0; a < 8; a++) { vdSrc[a].SelectedIndex = 0; vdSrc2[a].SelectedIndex = 0; vdMode[a].SelectedIndex = 0; vdInv[a].Checked = false; vdBtnNo[a].SelectedIndex = 0; vdThr[a].Value = 50; }
            if (p == 1 || p == 3)          // the named axes on Y Z Rx Ry: straight through (1) or as half axes (3)
            {
                int[] sources = new int[] { gas, brake, clutch, hand };
                int[] outs = new int[] { 1, 2, 3, 4 };      // Y Z Rx Ry
                for (int k = 0; k < 4; k++) if (sources[k] > 0) { vdSrc[outs[k]].SelectedIndex = sources[k]; vdMode[outs[k]].SelectedIndex = p == 3 ? 1 : 0; }
            }
            else if (p == 4)     // Logitech G27 as the game's own table has it: gas Y, brake Rz, clutch S1, all inverted (rest at the maximum)
            {
                int[] sources = new int[] { gas, brake, clutch };
                int[] outs = new int[] { 1, 5, 7 };         // Y Rz S1
                for (int k = 0; k < 3; k++) if (sources[k] > 0) { vdSrc[outs[k]].SelectedIndex = sources[k]; vdMode[outs[k]].SelectedIndex = 0; vdInv[outs[k]].Checked = true; }
            }
            else                 // combined pedals on Y
            {
                vdSrc[1].SelectedIndex = gas; vdSrc2[1].SelectedIndex = brake; vdMode[1].SelectedIndex = ModeCombine;
            }
            for (int a = 0; a < 8; a++)
            {
                if (vdSrc[a].SelectedIndex == 0 && vdMode[a].SelectedIndex != ModeCombine)
                {
                    if (a == 0) SetOwnAxis(a);                                  // the steering axis passes through, the unused axes are masked
                    else vdMode[a].SelectedIndex = ModeMask;
                }
                RowState(a);
            }
            vdLoading = false;
            VdChanged();
            if (p == 4) SetIdentityG27();
        }

        // the G27 layout also makes the game see a G27 (its table is chosen by the controller's product id)
        void SetIdentityG27()
        {
            if (cur == null || ini == null || DestKey == null) return;
            try
            {
                if (!dirty) ini = IniFile.Load(IniPath);
                ini.Set("Identity", DestKey, IdentityValues[IdentityValues.Length - 1]);
                CommitIni();
                RefreshAdvanced();
            }
            catch (Exception ex) { Dlg.Show(this, Loc.T("msg.savefail") + ex.Message, "CtrlMuxr"); }
        }

        // live view: what the proxy hands to the game (only while the selected game is running)
        // Buttons tab: a row lights up while its button is pressed. Game running: what the game sees (proxy live block);
        // otherwise the source device is read here (a running game holds the devices, so they are not opened then).
        void BtnTick()
        {
            if (cur == null || ini == null || DestKey == null) return;
            bool running = cur.Running && cur.Pid > 0 && live.Open(cur.Pid) && live.Read();
            if (!running && cur.Running) { for (int r = 0; r < BtnRows; r++) bmLed[r].BackColor = DimBack; return; }
            byte[] pv = null;                                    // game not running: the result calculated from the devices
            if (!running) { pv = new byte[128]; ComputePreviewNow(new int[8], pv, new bool[8]); }
            for (int r = 0; r < BtnRows; r++)
            {
                int o = bmOut[r].SelectedIndex - 1, sidx = bmSrc[r].SelectedIndex - 1;
                Color c = DimBack;
                if (o >= 0 && sidx >= 0 && sidx < btnSrcKeys.Count)
                {
                    bool pressed = running ? live.Btn[o] != 0 : pv[o] != 0;
                    c = pressed ? Color.LimeGreen : Color.White;
                }
                if (bmLed[r].BackColor != c) bmLed[r].BackColor = c;
            }
        }

        // "VID:PID.B7" -> 0-based index 6 (the keys number the buttons from 1); -1 if it is not a button key
        public static int BtnIndexOfKey(string key)
        {
            int dot = key == null ? -1 : key.IndexOf('.'), bn;
            if (dot > 0 && key.Length > dot + 2 && (key[dot + 1] == 'B' || key[dot + 1] == 'b') && int.TryParse(key.Substring(dot + 2), out bn) && bn >= 1 && bn <= 128) return bn - 1;
            return -1;
        }

        byte[] ReadDeviceButtons(string dev)
        {
            int[] ax; byte[] b;
            return ReadDevice(dev, out ax, out b) ? b : null;
        }

        // reads one device straight from the hardware (no game needed): axes 0..65535 and the 128 buttons
        bool ReadDevice(string dev, out int[] ax, out byte[] b)
        {
            ax = new int[8]; b = new byte[128];
            try
            {
                if (!object.ReferenceEquals(btnDevRef, devInfos)) { btnOpened.Clear(); btnDevRef = devInfos; }
                int d = -1;
                for (int i = 0; i < devInfos.Length; i++) if (string.Format("{0:X4}:{1:X4}", devInfos[i].vid, devInfos[i].pid) == dev) d = i;
                if (d < 0) return false;
                if (!btnOpened.Contains(d)) { if (DevRead.DR_Open(d, Handle) != 0) return false; btnOpened.Add(d); }
                int[] pov = new int[4];
                return DevRead.DR_Read(d, ax, pov, b) == 0;
            }
            catch { return false; }
        }

        // tooltips of the button grid on the Virtual device tab: which source feeds the game button (from [ButtonMap] and [AxisButton])
        string[] gridTip = new string[128];
        void UpdateGridTips()
        {
            if (ini == null || DestKey == null) return;
            string[] t = new string[128];
            for (int b = 0; b < 128; b++) t[b] = BKey(b);
            foreach (string k in ini.Keys("ButtonMap"))
            {
                int n;
                if (!k.StartsWith(DestKey + ".B", StringComparison.OrdinalIgnoreCase) || !int.TryParse(k.Substring(DestKey.Length + 2), out n) || n < 1 || n > 128) continue;
                foreach (string v in (ini.Get("ButtonMap", k) ?? "").Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int dot = v.IndexOf('.');
                    string nm = dot > 0 ? names.Get(v.Substring(0, dot), v.Substring(dot + 1)) : null;
                    t[n - 1] += (t[n - 1] == BKey(n - 1) ? " \u2013 " : " + ") + (string.IsNullOrEmpty(nm) ? v : nm);
                }
            }
            foreach (string k in ini.Keys("AxisButton"))
            {
                int n;
                if (!k.StartsWith(DestKey + ".B", StringComparison.OrdinalIgnoreCase) || !int.TryParse(k.Substring(DestKey.Length + 2), out n) || n < 1 || n > 128) continue;
                string[] tk = (ini.Get("AxisButton", k) ?? "").Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                string nm = tk.Length > 0 ? SrcName(tk[0]) : null;
                t[n - 1] += (t[n - 1] == BKey(n - 1) ? " \u2013 " : " + ") + (string.IsNullOrEmpty(nm) ? (tk.Length > 0 ? tk[0] : "?") : nm) + " (" + Loc.T("tip.axisbtn") + ")";
            }
            for (int b = 0; b < 128; b++) if (t[b] != gridTip[b]) { gridTip[b] = t[b]; tip.SetToolTip(vdBtn[b], t[b]); }
        }

        // game not running: what the game would see, calculated from the devices with the proxy's rules (Preview.Compute)
        // what the game would see right now, from the devices and the INI (the game is not running)
        void ComputePreviewNow(int[] ax, byte[] bt, bool[] has)
        {
            if (devInfos.Length == 0) EnumDevices();
            Dictionary<string, int[]> axc = new Dictionary<string, int[]>();
            Dictionary<string, byte[]> btc = new Dictionary<string, byte[]>();
            Func<string, int[]> axesOf = delegate(string dev) { ReadCached(dev, axc, btc); return axc[dev]; };
            Func<string, byte[]> btnOf = delegate(string dev) { ReadCached(dev, axc, btc); return btc[dev]; };
            Preview.Compute(ini, DestKey, axesOf, btnOf, ax, bt, has);
        }

        void PreviewTick()
        {
            vdInfo.Text = Loc.T("vd.live.preview");
            vdRangeHead.Visible = false;
            if (cur == null || ini == null || DestKey == null) return;
            int[] ax = new int[8]; byte[] bt = new byte[128]; bool[] has = new bool[8];
            ComputePreviewNow(ax, bt, has);
            for (int a = 0; a < 8; a++)
            {
                bool unm = RowUnmapped(a);
                vdBar[a].Dim = !has[a] || unm;
                vdBar[a].Value = has[a] ? ax[a] / 65535.0 : 0;
                vdVal[a].ForeColor = has[a] && !unm ? SystemColors.ControlText : DimText;
                vdVal[a].Text = has[a] ? ax[a].ToString() : "-";
                vdRange[a].Text = "";
            }
            for (int b = 0; b < 128; b++)
            {
                vdBtn[b].BackColor = bt[b] != 0 ? Color.LimeGreen : Color.White;
                vdBtn[b].ForeColor = SystemColors.ControlText;
            }
        }

        void ReadCached(string dev, Dictionary<string, int[]> axc, Dictionary<string, byte[]> btc)
        {
            if (axc.ContainsKey(dev)) return;
            int[] a; byte[] b;
            if (ReadDevice(dev, out a, out b)) { axc[dev] = a; btc[dev] = b; } else { axc[dev] = null; btc[dev] = null; }
        }

        // an open drop-down list (the owner-drawn source lists) is closed by the device reads of the live view: no update while one is open
        bool DropDownOpen()
        {
            Control c = ActiveControl;
            while (c is ContainerControl && ((ContainerControl)c).ActiveControl != null) c = ((ContainerControl)c).ActiveControl;
            ComboBox cb = c as ComboBox;
            return cb != null && cb.DroppedDown;
        }

        void VdTick()
        {
            if (DropDownOpen()) return;
            if (tabs.SelectedTab == btnTab) { BtnTick(); return; }
            if (tabs.SelectedTab != vdTab) return;
            UpdateGridTips();
            bool ok = cur != null && cur.Running && cur.Pid > 0 && live.Open(cur.Pid) && live.Read();
            if (!ok && !(cur != null && cur.Running)) { PreviewTick(); return; }
            if (!ok)
            {
                vdInfo.Text = Loc.T("vd.live.nolive");
                vdRangeHead.Visible = false;
                for (int a = 0; a < 8; a++) { vdBar[a].Dim = true; vdBar[a].Value = 0; vdVal[a].Text = "-"; vdVal[a].ForeColor = DimText; vdRange[a].Text = ""; }
                for (int b = 0; b < 128; b++) { vdBtn[b].BackColor = DimBack; vdBtn[b].ForeColor = DimText; }
                return;
            }
            vdRangeHead.Visible = true;
            vdInfo.Text = string.Format(Loc.T("vd.live.info"),
                live.Name, live.Vid, live.Pid, live.DevType, live.GameDz, live.GameDz / 100.0, live.FmtSize);
            for (int a = 0; a < 8; a++)
            {
                bool has = live.Axes[a] >= 0;
                bool unm = RowUnmapped(a);
                vdBar[a].Dim = !has || unm;
                vdBar[a].Value = has ? live.Axes[a] / 65535.0 : 0;
                vdVal[a].ForeColor = has && !unm ? SystemColors.ControlText : DimText;
                vdVal[a].Text = has ? live.Axes[a].ToString() : "-";
                vdRange[a].Text = has ? "[" + ShortNum(live.Min[a]) + "," + ShortNum(live.Max[a]) + "]" : "";
            }
            for (int b = 0; b < 128; b++)
            {
                vdBtn[b].BackColor = live.Btn[b] != 0 ? Color.LimeGreen : Color.White;      // live: every button the game can see is "existing"
                vdBtn[b].ForeColor = SystemColors.ControlText;
            }
        }

        // ---------------------------------------------------------------- buttons tab ([ButtonMap]: button of one device -> button of the game's wheel)
        void BuildButtonsTab()
        {
            Label intro = new Label(); intro.Text = Loc.T("btn.intro"); intro.Dock = DockStyle.Top; intro.Height = 96; intro.Padding = new Padding(10, 8, 230, 0); intro.ForeColor = SystemColors.GrayText;
            TableLayoutPanel t = new TableLayoutPanel();
            t.Dock = DockStyle.Top; t.AutoSize = true; t.ColumnCount = 5; t.Padding = new Padding(8, 4, 8, 0);
            btnTable = t;
            int[] widths = new int[] { 40, 420, 150, 80, 30 };
            for (int c = 0; c < widths.Length; c++) t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, widths[c]));
            string[] heads = new string[] { "#", Loc.T("btn.h.src"), Loc.T("btn.h.out"), Loc.T("btn.h.led") };      // reads left to right: source button -> game button -> what the game sees
            for (int c = 0; c < heads.Length; c++) { Label h = new Label(); h.Text = heads[c]; h.AutoSize = true; h.Font = new Font(Font, FontStyle.Bold); t.Controls.Add(h, c, 0); }
            EventHandler changed = delegate { if (!vdLoading) { BtnWarn(); VdChanged(); } };
            for (int r = 0; r < BtnRows; r++)
            {
                Label n = new Label(); n.Text = (r + 1).ToString(); n.AutoSize = false; n.Width = 30; n.Height = 24; n.TextAlign = ContentAlignment.MiddleLeft;
                bmOut[r] = new NoWheelCombo(); bmOut[r].DropDownStyle = ComboBoxStyle.DropDownList; bmOut[r].Width = 140;
                bmOut[r].Items.Add(Loc.T("vd.none"));
                for (int b = 0; b < 32; b++) bmOut[r].Items.Add(BKey(b));
                bmOut[r].SelectedIndex = 0;
                bmSrc[r] = new NoWheelCombo(); bmSrc[r].DropDownStyle = ComboBoxStyle.DropDownList; bmSrc[r].Width = 400;
                ((NoWheelCombo)bmSrc[r]).Offline = delegate(int x) { return x > 0 && x <= btnSrcKeys.Count && !Connected(btnSrcKeys[x - 1]); };
                bmOut[r].SelectedIndexChanged += changed; bmSrc[r].SelectedIndexChanged += changed;
                bmLed[r] = new Label(); bmLed[r].AutoSize = false; bmLed[r].Width = 24; bmLed[r].Height = 20; bmLed[r].BorderStyle = BorderStyle.FixedSingle; bmLed[r].BackColor = DimBack; bmLed[r].Margin = new Padding(8, 4, 3, 4);
                tip.SetToolTip(bmLed[r], Loc.T("tt.btn.led"));
                bmNum[r] = n;
                bmWarn[r] = ClashMark(); bmWarn[r].Margin = new Padding(0, 3, 0, 3);
                t.Controls.Add(n, 0, r + 1); t.Controls.Add(bmSrc[r], 1, r + 1); t.Controls.Add(bmOut[r], 2, r + 1); t.Controls.Add(bmLed[r], 3, r + 1); t.Controls.Add(bmWarn[r], 4, r + 1);
            }
            Button mapNamed = new Button(); mapNamed.Text = Loc.T("btn.mapnamed"); mapNamed.Left = 10; mapNamed.Top = 8; mapNamed.Width = 330; mapNamed.Height = 26;
            mapNamed.Click += delegate { MapNamedButtons(); };
            Label rowsLbl = new Label(); rowsLbl.Text = Loc.T("btn.rows"); rowsLbl.AutoSize = true; rowsLbl.Left = 350; rowsLbl.Top = 13;
            btnRowsBox.DropDownStyle = ComboBoxStyle.DropDownList; btnRowsBox.Width = 56; btnRowsBox.Top = 9; btnRowsBox.Left = rowsLbl.Left + rowsLbl.PreferredWidth + 8;
            btnRowsBox.Items.AddRange(new object[] { "8", "16", "32", "48" });
            btnRowsWant = 8;
            btnRowsBox.SelectedIndex = RowsIndex(btnRowsWant);
            tip.SetToolTip(btnRowsBox, Loc.T("tt.btn.rows")); tip.SetToolTip(rowsLbl, Loc.T("tt.btn.rows"));
            btnRowsBox.SelectedIndexChanged += delegate { if (btnRowsLoading) return; btnRowsWant = int.Parse(btnRowsBox.Text); ApplyBtnRows(); };
            bmInfo.Left = btnRowsBox.Right + 20; bmInfo.Top = 6; bmInfo.Width = 600 - (btnRowsBox.Right + 20 - 350); bmInfo.Height = 34; bmInfo.AutoSize = false; bmInfo.ForeColor = SystemColors.GrayText;
            chkOnlyNamed.Text = Loc.T("btn.onlynamed"); chkOnlyNamed.AutoSize = true; chkOnlyNamed.Left = 10; chkOnlyNamed.Top = 42;
            chkOnlyNamed.Checked = Settings.Get("BtnOnlyNamed", "0") == "1";
            tip.SetToolTip(chkOnlyNamed, Loc.T("tt.btn.onlynamed"));
            chkOnlyNamed.CheckedChanged += delegate { Settings.Set("BtnOnlyNamed", chkOnlyNamed.Checked ? "1" : "0"); if (ini != null && DestKey != null) { vdLoading = true; FillButtonSources(); vdLoading = false; } };
            Panel actions = new Panel(); actions.Dock = DockStyle.Top; actions.Height = 68;
            btnClear.Text = Loc.T("btn.clear"); btnClear.Width = 120; btnClear.Height = 26; btnClear.Top = 38; btnClear.Left = 8 + widths[0] + widths[1] + widths[2] + 8 + 24 - btnClear.Width;      // its right edge lines up with the right edge of the "Game sees" boxes
            tip.SetToolTip(btnClear, Loc.T("tt.btn.clear"));
            btnClear.Click += delegate
            {
                bool any = false;
                for (int r = 0; r < BtnRows; r++) if (bmOut[r].SelectedIndex > 0 || bmSrc[r].SelectedIndex > 0) any = true;
                if (!any || cur == null || DestKey == null) return;
                if (Dlg.Show(this, Loc.T("msg.btnclear"), "CtrlMuxr", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                ClearButtonRows();
            };
            actions.Controls.Add(btnClear); actions.Controls.Add(mapNamed); actions.Controls.Add(rowsLbl); actions.Controls.Add(btnRowsBox); actions.Controls.Add(bmInfo); actions.Controls.Add(chkOnlyNamed);
            Panel scroll = new Panel(); scroll.Dock = DockStyle.Fill; scroll.AutoScroll = true;      // the rows can be more than a small window fits
            scroll.Controls.Add(t);
            ApplyBtnRows();
            btnTab.Controls.Add(scroll); btnTab.Controls.Add(actions); btnTab.Controls.Add(intro);
            AddAnalyseButton(btnTab, 0, 8);
            tabs.TabPages.Add(btnTab);
        }

        // the buttons of every known device as source choices (see ButtonShown for which ones)
        void FillButtonSources()
        {
            string[] keep = new string[BtnRows];
            for (int r = 0; r < BtnRows; r++) { int s = bmSrc[r].SelectedIndex; keep[r] = s > 0 && s <= btnSrcKeys.Count ? btnSrcKeys[s - 1] : null; }
            List<string> mapped = new List<string>();                      // buttons used by the saved settings or the rows must stay selectable
            foreach (string kp in keep) if (kp != null) mapped.Add(kp.ToUpperInvariant());
            if (ini != null)
                foreach (string k in ini.Keys("ButtonMap"))
                    foreach (string tok in (ini.Get("ButtonMap", k) ?? "").Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) mapped.Add(tok.ToUpperInvariant());
            btnSrcKeys.Clear();
            List<string> titles = new List<string>();
            foreach (string dev in shapeDevKeys)
            {
                int count = BtnCount(dev);
                for (int b = 0; b < 32; b++)
                {
                    string n = names.Get(dev, BKey(b));
                    string key = dev + "." + BKey(b);
                    if (!ButtonShown(b, count, !string.IsNullOrEmpty(n), mapped.Contains(key.ToUpperInvariant()), chkOnlyNamed.Checked)) continue;
                    btnSrcKeys.Add(key);
                    titles.Add(DeviceTitle(dev) + " – " + BKey(b) + (string.IsNullOrEmpty(n) ? "" : "  (" + n + ")") + OfflineTag(dev));
                }
            }
            string sig = string.Join("\n", titles.ToArray());
            bool same = sig == btnTitlesSig;                               // same list: only the selections are restored (refilling 96 lists took ~0.9 s)
            for (int r = 0; r < BtnRows; r++)
            {
                int want = keep[r] != null ? btnSrcKeys.IndexOf(keep[r]) + 1 : 0;
                if (same) { if (bmSrc[r].SelectedIndex != want) bmSrc[r].SelectedIndex = want; continue; }
                bmSrc[r].Items.Clear();
                bmSrc[r].Items.Add(Loc.T("vd.none"));
                bmSrc[r].Items.AddRange(titles.ToArray());
                bmSrc[r].SelectedIndex = want;
            }
            btnTitlesSig = sig;
        }

        // reads the destination's [ButtonMap] rows into the table (the caller holds vdLoading)
        void RefreshButtons()
        {
            if (ini == null || DestKey == null) return;
            FillButtonSources();
            for (int q = 0; q < BtnRows; q++) { bmOut[q].SelectedIndex = 0; bmSrc[q].SelectedIndex = 0; }
            int row = 0;
            foreach (string k in ini.Keys("ButtonMap"))
            {
                if (row >= BtnRows) break;
                if (!k.StartsWith(DestKey + ".B", StringComparison.OrdinalIgnoreCase)) continue;
                int o;
                if (!int.TryParse(k.Substring(DestKey.Length + 2), out o) || o < 1 || o > 32) continue;
                o--;                                                       // the INI numbers buttons from 1
                foreach (string tok in (ini.Get("ButtonMap", k) ?? "").Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))      // several sources: one row each
                {
                    if (row >= BtnRows) break;
                    int s = 0;
                    for (int i = 0; i < btnSrcKeys.Count && s == 0; i++) if (string.Equals(btnSrcKeys[i], tok, StringComparison.OrdinalIgnoreCase)) s = i + 1;
                    if (s == 0) continue;
                    bmOut[row].SelectedIndex = o + 1; bmSrc[row].SelectedIndex = s;
                    row++;
                }
            }
            BtnWarn();
            ApplyBtnRows();
        }

        static int RowsIndex(int rows) { return rows >= 48 ? 3 : rows >= 32 ? 2 : rows >= 16 ? 1 : 0; }

        // shows 8, 16, 32 or 48 rows: what the user chose, or more when rows further down are in use (the setting only changes the view)
        void ApplyBtnRows()
        {
            int need = 0;
            for (int r = 0; r < BtnRows; r++) if (bmOut[r].SelectedIndex > 0 || bmSrc[r].SelectedIndex > 0) need = r + 1;
            int shown = Math.Max(btnRowsWant, need <= 8 ? 8 : need <= 16 ? 16 : need <= 32 ? 32 : 48);
            btnRowsShown = shown;
            if (shown != btnRowsApplied)                                   // the table lays itself out once, not after every control
            {
                btnTable.SuspendLayout();
                for (int r = 0; r < BtnRows; r++) { bool v = r < shown; bmNum[r].Visible = v; bmSrc[r].Visible = v; bmOut[r].Visible = v; bmLed[r].Visible = v; }
                btnTable.ResumeLayout();
                btnRowsApplied = shown;
            }
            btnRowsLoading = true; btnRowsBox.SelectedIndex = RowsIndex(shown); btnRowsLoading = false;
        }

        // rows with the same out button are fine: the game button is pressed while any of their sources is
        void BtnWarn()
        {
            bmInfo.Text = HasNamedButtons() ? "" : Loc.T("btn.nonames");
            UpdateClashMarks();
        }

        // is any button of the other devices named (Devices tab)?
        bool HasNamedButtons()
        {
            foreach (string dev in shapeDevKeys)
            {
                if (string.Equals(dev, DestKey, StringComparison.OrdinalIgnoreCase)) continue;
                for (int b = 0; b < 128; b++) if (!string.IsNullOrEmpty(names.Get(dev, BKey(b)))) return true;
            }
            return false;
        }

        void WriteButtons()
        {
            foreach (string k in ini.Keys("ButtonMap"))
                if (k.StartsWith(DestKey + ".B", StringComparison.OrdinalIgnoreCase)) ini.Remove("ButtonMap", k);
            Dictionary<int, List<string>> byOut = new Dictionary<int, List<string>>();      // out button -> its sources (any of them presses it)
            List<int> order = new List<int>();
            for (int r = 0; r < BtnRows; r++)
            {
                int o = bmOut[r].SelectedIndex, s = bmSrc[r].SelectedIndex;
                if (o <= 0 || s <= 0 || s > btnSrcKeys.Count) continue;
                if (!byOut.ContainsKey(o)) { byOut[o] = new List<string>(); order.Add(o); }
                if (!byOut[o].Contains(btnSrcKeys[s - 1])) byOut[o].Add(btnSrcKeys[s - 1]);
            }
            foreach (int o in order) ini.Set("ButtonMap", DestKey + ".B" + o, string.Join(" ", byOut[o].ToArray()));
        }

        // empties every row of the Buttons tab (the Digital axes on the Virtual device tab stay)
        void ClearButtonRows()
        {
            vdLoading = true;
            for (int r = 0; r < BtnRows; r++) { bmOut[r].SelectedIndex = 0; bmSrc[r].SelectedIndex = 0; }
            vdLoading = false;
            ApplyBtnRows();
            bmInfo.Text = "";
            BtnWarn();
            VdChanged();
        }

        // every named button of the connected other devices goes to the same number on the wheel (B1..B32);
        // a device that is not connected (names remembered from earlier) is skipped unless this game already maps it
        void MapNamedButtons()
        {
            if (cur == null || DestKey == null) return;
            int added = 0, skipped = 0;
            List<string> offline = new List<string>();
            bool[] used = new bool[32];
            for (int r = 0; r < BtnRows; r++) if (bmOut[r].SelectedIndex > 0 && bmSrc[r].SelectedIndex > 0) used[bmOut[r].SelectedIndex - 1] = true;
            vdLoading = true;
            foreach (string dev in shapeDevKeys)
            {
                if (string.Equals(dev, DestKey, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Connected(dev) && !HasButtonRow(dev))
                {
                    for (int b = 0; b < 32; b++)
                        if (!string.IsNullOrEmpty(names.Get(dev, BKey(b)))) { offline.Add(DeviceTitle(dev)); break; }
                    continue;
                }
                for (int b = 0; b < 32; b++)
                {
                    if (string.IsNullOrEmpty(names.Get(dev, BKey(b)))) continue;
                    int src = btnSrcKeys.IndexOf(dev + "." + BKey(b)) + 1;
                    int free = -1;
                    for (int r = 0; r < BtnRows && free < 0; r++) if (bmOut[r].SelectedIndex == 0 && bmSrc[r].SelectedIndex == 0) free = r;
                    bool already = false;
                    for (int r = 0; r < BtnRows; r++) if (bmSrc[r].SelectedIndex == src) already = true;
                    if (already) continue;
                    if (used[b] || free < 0 || src <= 0) { skipped++; continue; }
                    bmOut[free].SelectedIndex = b + 1; bmSrc[free].SelectedIndex = src;
                    used[b] = true; added++;
                }
            }
            vdLoading = false;
            ApplyBtnRows();
            if (added > 0) VdChanged();
            bmInfo.Text = string.Format(Loc.T("btn.mapped"), added, skipped);
            if (offline.Count > 0) bmInfo.Text += " " + string.Format(Loc.T("btn.mapped.offline"), string.Join(", ", offline.ToArray()));
        }

        // true when a row of the Buttons tab already takes a button of this device as its source
        bool HasButtonRow(string dev)
        {
            for (int r = 0; r < BtnRows; r++)
            {
                int i = bmSrc[r].SelectedIndex - 1;
                if (i >= 0 && i < btnSrcKeys.Count && btnSrcKeys[i].StartsWith(dev + ".", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- log analysis button + view (Virtual device tab)
        Button analyseBtn = new Button();
        System.Windows.Forms.Timer analyseTimer = new System.Windows.Forms.Timer(), glowTimer = new System.Windows.Forms.Timer();
        SessionLog sessLog = new SessionLog();
        List<Finding> findings = new List<Finding>();
        string ackSig = "", logGame = "";
        bool glowOn;
        AnalysisForm anaForm;

        List<Button> analyseBtns = new List<Button>();      // the same button on the Devices, Virtual device and Buttons tabs
        List<CheckBox> logChks = new List<CheckBox>();      // "log also without CtrlMuxr" under each of them: one setting ([Log] Always), shown on all
        bool logOptLoading;

        // reads [Log] Always of the selected game into the checkboxes
        void RefreshLogOpt()
        {
            logOptLoading = true;
            foreach (CheckBox c in logChks)
            {
                c.Enabled = ini != null;
                c.Checked = ini != null && (ini.Get("Log", "Always") ?? "0").Trim() != "0";
            }
            logOptLoading = false;
        }

        void SaveLogOpt(CheckBox src)
        {
            if (logOptLoading || cur == null || ini == null) return;
            try
            {
                if (!dirty) ini = IniFile.Load(IniPath);
                if (src.Checked) ini.Set("Log", "Always", "1"); else ini.Remove("Log", "Always");
                SyncNames(ini);
                CommitIni();
            }
            catch (Exception ex) { Dlg.Show(this, Loc.T("msg.savefail") + ex.Message, "CtrlMuxr"); return; }
            RefreshLogOpt();
        }

        Button AddAnalyseButton(Control parent, int left, int top)
        {
            Button b = new Button();
            b.Text = Loc.T("an.btn"); b.Left = left; b.Top = top; b.Width = 200; b.Height = 26;
            b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderColor = SystemColors.ControlDark;
            b.Click += delegate { ShowAnalysis(); };
            tip.SetToolTip(b, Loc.T("an.btn.tip"));
            parent.Controls.Add(b);
            b.BringToFront();
            b.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            EventHandler place = delegate { b.Left = Math.Max(0, parent.ClientSize.Width - b.Width - 12); };
            parent.Resize += place; place(null, EventArgs.Empty);        // right-hand edge of the tab
            analyseBtns.Add(b);
            // under the button: short text that wraps to two lines, the long explanation is in the tooltip
            CheckBox lc = new CheckBox();
            lc.Text = Loc.T("an.log"); lc.AutoSize = false; lc.Width = b.Width; lc.Height = 32; lc.Top = b.Top + b.Height + 2;
            lc.CheckAlign = ContentAlignment.TopLeft; lc.TextAlign = ContentAlignment.TopLeft;
            lc.Enabled = ini != null; lc.Checked = ini != null && (ini.Get("Log", "Always") ?? "0").Trim() != "0";
            lc.CheckedChanged += delegate { SaveLogOpt(lc); };
            tip.SetToolTip(lc, WrapText(Loc.T("an.log.tip"), 70));
            parent.Controls.Add(lc);
            lc.BringToFront();
            lc.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            EventHandler placeLc = delegate { lc.Left = b.Left; };
            parent.Resize += placeLc; placeLc(null, EventArgs.Empty);
            logChks.Add(lc);
            return b;
        }

        void SetAnalyseColor(Color c) { foreach (Button b in analyseBtns) b.BackColor = c; }

        void BuildAnalyseButton(Panel top)
        {
            analyseBtn = AddAnalyseButton(top, 610, 6);
            analyseTimer.Interval = 2000; analyseTimer.Tick += delegate { CheckLog(); };
            glowTimer.Interval = 550; glowTimer.Tick += delegate
            {
                glowOn = !glowOn;
                SetAnalyseColor(glowOn ? Color.Orange : SystemColors.Control);
            };
            analyseTimer.Start();
        }

        // re-reads the newest log when it has changed (not while the game runs: the log is still growing) and lights the button
        void CheckLog()
        {
            if (cur == null || ini == null || DestKey == null || cur.Running) { return; }
            try
            {
                string path = SessionLog.Newest(cur.Folder);
                string sig = path == null ? "" : SessionLog.Signature(path);
                if (path != null && (sessLog.Sig != sig || logGame != cur.Name))
                {
                    int c = DestKey.IndexOf(':');
                    sessLog = SessionLog.Parse(path, DestKey.Substring(0, c), DestKey.Substring(c + 1));
                    logGame = cur.Name;
                }
                else if (path == null) { sessLog = new SessionLog(); logGame = cur.Name; }
                findings = Analyse(sessLog);
                bool glow = false;
                foreach (Finding f in findings) if (f.Glow) glow = true;
                glow = glow && sessLog.Has && sessLog.Sig != ackSig;      // no glow before a game session has been logged (right after Add game the hidden devices are simply not mapped yet)
                if (glow && !glowTimer.Enabled) glowTimer.Start();
                if (!glow && glowTimer.Enabled) { glowTimer.Stop(); glowOn = false; SetAnalyseColor(SystemColors.Control); }
            }
            catch { }
        }

        static bool RefStartsWith(List<string> refs, string dev)
        {
            foreach (string r in refs) if (r.StartsWith(dev + ".", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // every source key ("VID:PID.Axis" / ".B3") that the INI maps from
        List<string> UsedSources()
        {
            List<string> refs = new List<string>();
            foreach (string sec in new string[] { "AxisMap", "AxisCombine", "AxisButton", "ButtonMap" })
                foreach (string k in ini.Keys(sec))
                {
                    string[] tk = (ini.Get(sec, k) ?? "").Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tk.Length > 0) refs.Add(tk[0].ToUpperInvariant());
                    if (sec == "AxisCombine" && tk.Length > 1) refs.Add(tk[1].ToUpperInvariant());
                    if (sec == "ButtonMap") for (int ti = 1; ti < tk.Length; ti++) refs.Add(tk[ti].ToUpperInvariant());
                }
            return refs;
        }

        static int AxisIdx(string name)
        {
            for (int i = 0; i < AxisNames.Length; i++) if (string.Equals(AxisNames[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        List<Finding> Analyse(SessionLog sl)
        {
            List<Finding> f = new List<Finding>();
            if (ini == null || DestKey == null) return f;
            List<string> refs = UsedSources();
            foreach (string h in ini.Keys("Hide"))
            {
                string dev = h.ToUpperInvariant();
                if (dev == DestKey.ToUpperInvariant()) continue;
                if (!RefStartsWith(refs, dev)) { Finding hu = new Finding(string.Format(Loc.T("an.f.hiddenunused"), DeviceTitle(dev))); hu.Glow = false; f.Add(hu); }      // information only: it does not make the Analyse button glow after every game
            }
            if (!sl.Has) { foreach (Finding x in f) x.Glow = false; if (f.Count == 0) { Finding n = new Finding(Loc.T("an.nolog")); n.Glow = false; f.Add(n); } return f; }
            string ip = IniPath;
            if (ip != null && File.Exists(ip) && File.GetLastWriteTime(ip) > sl.Time.AddSeconds(2))
            {
                foreach (Finding x in f) x.Glow = false;                      // settings were changed after the log: nothing here is news from a game session
                Finding st = new Finding(string.Format(Loc.T("an.f.stale"), sl.Name)); st.Glow = false; f.Add(st);
                return f;                                                   // the log is from before the present settings: comparing them would mislead
            }
            foreach (string b in sl.BadCfg) f.Add(new Finding(string.Format(Loc.T("an.f.bad"), b)));
            foreach (string b in sl.SrcFail) f.Add(new Finding(string.Format(Loc.T("an.f.srcfail"), b)));
            foreach (string b in sl.AcqFail) f.Add(new Finding(string.Format(Loc.T("an.f.acq"), b)));
            if (sl.AcqRecovered.Count > 0) { Finding ar = new Finding(string.Format(Loc.T("an.f.acqlost"), string.Join(", ", sl.AcqRecovered.ToArray()))); ar.Glow = false; f.Add(ar); }
            if (!sl.WheelListed) f.Add(new Finding(string.Format(Loc.T("an.f.notlisted"), DeviceTitle(DestKey))));
            else if (sl.WheelHidden) f.Add(new Finding(string.Format(Loc.T("an.f.wheelhidden"), DeviceTitle(DestKey))));
            else if (!sl.WheelOpened) f.Add(new Finding(string.Format(Loc.T("an.f.notopened"), DeviceTitle(DestKey))));
            // sources that moved but nothing uses them
            foreach (KeyValuePair<string, int[]> kv in sl.SrcMin)
                for (int a = 0; a < 8; a++)
                    if (sl.SrcRange(kv.Key, a) > 2000 && !refs.Contains(kv.Key + "." + AxisNames[a].ToUpperInvariant()))
                        f.Add(new Finding(string.Format(Loc.T("an.f.unmapped"), SrcTitle(kv.Key + "." + AxisNames[a]))));
            // mapped source moved, the game's axis did not
            if (sl.OutSeen)
            {
                List<string> masked = MaskedAxes();
                foreach (string sec in new string[] { "AxisMap", "AxisCombine" })
                    foreach (string k in ini.Keys(sec))
                    {
                        if (!k.StartsWith(DestKey + ".", StringComparison.OrdinalIgnoreCase)) continue;
                        int d = AxisIdx(k.Substring(DestKey.Length + 1));
                        if (d < 0 || masked.Contains(AxisNames[d])) continue;
                        string[] tk = (ini.Get(sec, k) ?? "").Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        bool moved = false;
                        for (int i = 0; i < tk.Length && i < (sec == "AxisCombine" ? 2 : 1); i++)
                        {
                            int dot = tk[i].IndexOf('.');
                            int ax = dot > 0 ? AxisIdx(tk[i].Substring(dot + 1)) : -1;
                            if (ax >= 0 && sl.SrcRange(tk[i].Substring(0, dot).ToUpperInvariant(), ax) > 2000) moved = true;
                        }
                        if (moved && sl.OutMax[d] - sl.OutMin[d] <= 2000)
                            f.Add(new Finding(string.Format(Loc.T("an.f.nopass"), AxisNames[d])));
                    }
            }
            if (sl.SrcMin.Count == 0 && refs.Count > 0 && sl.WheelOpened) f.Add(new Finding(Loc.T("an.f.nosrc")));
            return f;
        }

        static readonly string[][] ControlWords = new string[][] {
            new string[] { "ohjaus", "steer", "wheel" },
            new string[] { "kaasu", "gas", "throttle", "accel" },
            new string[] { "jarru", "brake", "brk" },
            new string[] { "kytkin", "clutch", "clut", "kytk" },
            new string[] { "käsijarru", "kasijarru", "handbrake", "hand brake", "handbr", "hbrake", "ebrake", "e-brake" },
            new string[] { "vaihde yl", "gear up", "shift up", "upshift" },
            new string[] { "vaihde alas", "gear down", "shift down", "downshift" } };

        int Classify(string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            if (n.Length == 0) return -1;
            n = n.Trim();
            if (n == "up" || n == "ylös" || n == "ylos" || n == "+") return 5;            // too short to match as a part of a longer name
            if (n == "down" || n == "alas" || n == "-") return 6;
            for (int w = 0; w < ControlWords[4].Length; w++) if (n.Contains(ControlWords[4][w])) return 4;     // handbrake before brake
            for (int c = 0; c < ControlWords.Length; c++)
                for (int w = 0; w < ControlWords[c].Length; w++) if (n.Contains(ControlWords[c][w])) return c;
            return -1;
        }

        string SrcName(string key)
        {
            int dot = key.IndexOf('.');
            return dot > 0 ? names.Get(key.Substring(0, dot), key.Substring(dot + 1)) : null;
        }

        // what carries each control today: axis (destination axis index) or button (destination number), from the INI and the names
        void FindCarriers(int[] axisOf, int[] buttonOf, string[] modeOf)
        {
            for (int c = 0; c < 7; c++) { axisOf[c] = -1; buttonOf[c] = -1; modeOf[c] = ""; }
            axisOf[0] = 0;
            foreach (string sec in new string[] { "AxisMap", "AxisCombine" })
                foreach (string k in ini.Keys(sec))
                {
                    if (!k.StartsWith(DestKey + ".", StringComparison.OrdinalIgnoreCase)) continue;
                    int d = AxisIdx(k.Substring(DestKey.Length + 1));
                    string[] tk = (ini.Get(sec, k) ?? "").Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 0; i < tk.Length && i < (sec == "AxisCombine" ? 2 : 1); i++)
                    {
                        int c = Classify(SrcName(tk[i]));
                        if (c <= 0 || d < 0) continue;
                        axisOf[c] = d;
                        modeOf[c] = "direct";
                        if (sec == "AxisCombine") modeOf[c] = "combine";
                        else for (int m = 1; m < tk.Length; m++) { string w = tk[m].ToLowerInvariant(); if (w == "half" || w == "halfneg" || w == "splithi" || w == "splitlo") modeOf[c] = w; }
                    }
                }
            foreach (string sec in new string[] { "AxisButton", "ButtonMap" })
                foreach (string k in ini.Keys(sec))
                {
                    if (!k.StartsWith(DestKey + ".B", StringComparison.OrdinalIgnoreCase)) continue;
                    int bn;
                    if (!int.TryParse(k.Substring(DestKey.Length + 2), out bn)) continue;
                    string[] tk = (ini.Get(sec, k) ?? "").Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int ti = 0; ti < (sec == "ButtonMap" ? tk.Length : Math.Min(1, tk.Length)); ti++)
                    {
                        int c = Classify(SrcName(tk[ti]));
                        if (c >= 4) buttonOf[c] = bn - 1;
                    }           // 0-based inside the analysis
                }
        }

        string Suggest(int[] ans)
        {
            // ans: 0 not tried, 1 accepted, 2 not accepted, 3 the game has no such control (nothing to suggest)
            int[] axisOf = new int[7], buttonOf = new int[7]; string[] modeOf = new string[7];
            FindCarriers(axisOf, buttonOf, modeOf);
            StringBuilder sb = new StringBuilder();
            string[] cn = ControlNames();
            List<int> usedAxes = new List<int>(), usedBtns = new List<int>();
            for (int c = 0; c < 7; c++) { if (axisOf[c] >= 0) usedAxes.Add(axisOf[c]); if (buttonOf[c] >= 0) usedBtns.Add(buttonOf[c]); }
            bool any = false;
            for (int c = 0; c < 7; c++)
            {
                if (ans[c] != 2) continue;
                any = true;
                sb.AppendLine(cn[c] + ":");
                if (axisOf[c] < 0 && buttonOf[c] < 0)
                {
                    sb.AppendLine("  " + string.Format(Loc.T("an.s.nocarrier"), cn[c]));
                    sb.AppendLine();
                    continue;
                }
                if (buttonOf[c] >= 0)
                {
                    int n = buttonOf[c];
                    List<string> free = new List<string>();
                    for (int b = 0; b < 32 && free.Count < 3; b++) if (b != n && !usedBtns.Contains(b)) free.Add(BKey(b));
                    sb.AppendLine("  " + string.Format(Loc.T("an.s.btn"), BKey(n), string.Join(", ", free.ToArray())));
                    sb.AppendLine("  " + Loc.T("an.s.caps"));
                    sb.AppendLine();
                    continue;
                }
                int d = axisOf[c];
                if (c == 0) { sb.AppendLine("  " + Loc.T("an.s.steer")); sb.AppendLine(); continue; }
                bool moved = sessLog.Has && sessLog.OutSeen && sessLog.OutMax[d] - sessLog.OutMin[d] > 2000;
                sb.AppendLine("  " + (!sessLog.Has ? Loc.T("an.s.nolog") : moved ? string.Format(Loc.T("an.s.moved"), AxisNames[d]) : string.Format(Loc.T("an.s.notmoved"), AxisNames[d])));
                List<string> alt = new List<string>();
                for (int a = 1; a < 8 && alt.Count < 2; a++) if (a != d && !usedAxes.Contains(a)) alt.Add(AxisNames[a]);
                int step = 1;
                if (c == 4) sb.AppendLine("  " + step++ + ". " + Loc.T("an.s.handdigital"));      // old games often take the handbrake only as a button
                sb.AppendLine("  " + step++ + ". " + string.Format(Loc.T("an.s.otheraxis"), AxisNames[d], string.Join(" / ", alt.ToArray())));
                if (modeOf[c] == "direct") sb.AppendLine("  " + step++ + ". " + Loc.T("an.s.halfmode"));
                else if (modeOf[c] == "half" || modeOf[c] == "halfneg") sb.AppendLine("  " + step++ + ". " + Loc.T("an.s.directmode"));
                if ((c == 1 || c == 2) && modeOf[c] != "combine") sb.AppendLine("  " + step++ + ". " + Loc.T("an.s.combine"));
                sb.AppendLine("  " + step++ + ". " + Loc.T("an.s.identity"));
                sb.AppendLine();
            }
            if (!any) sb.AppendLine(Loc.T("an.s.none"));
            return sb.ToString();
        }

        string[] ControlNames()
        {
            return new string[] { Loc.T("an.c.steer"), Loc.T("an.c.throttle"), Loc.T("an.c.brake"), Loc.T("an.c.clutch"), Loc.T("an.c.handbrake"), Loc.T("an.c.gearup"), Loc.T("an.c.geardown") };
        }

        void ShowAnalysis()
        {
            if (cur == null || ini == null || DestKey == null) return;
            CheckLogNow();
            ackSig = sessLog.Sig ?? "";
            if (glowTimer.Enabled) { glowTimer.Stop(); glowOn = false; SetAnalyseColor(SystemColors.Control); }
            StringBuilder sb = new StringBuilder();
            if (findings.Count == 0) sb.AppendLine(Loc.T("an.f.none"));
            foreach (Finding f in findings) sb.AppendLine("• " + f.Text).AppendLine();
            string head = sessLog.Has ? string.Format(Loc.T("an.intro"), sessLog.Name, sessLog.Time.ToString("g")) : Loc.T("an.nolog");
            AnalysisForm form = new AnalysisForm(Loc.T("an.title", cur.Name), head, sb.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n"), ControlNames(),
                new string[] { Loc.T("an.a.0"), Loc.T("an.a.1"), Loc.T("an.a.2"), Loc.T("an.a.3") }, Loc.T("an.suggest"), Loc.T("rs.close"), Loc.T("an.q"), new Func<int[], string>(Suggest));
            // not modal: it stays open beside the main window while the changes it suggests are made
            if (anaForm != null && !anaForm.IsDisposed) { form.StartPosition = FormStartPosition.Manual; form.Location = anaForm.Location; form.Size = anaForm.Size; anaForm.Close(); }
            else
            {
                Rectangle wa = Screen.FromControl(this).WorkingArea;
                form.StartPosition = FormStartPosition.Manual;
                form.Top = Math.Max(wa.Top, Math.Min(Top, wa.Bottom - form.Height));
                form.Left = Right + form.Width <= wa.Right ? Right : Math.Max(wa.Left, wa.Right - form.Width);      // beside the window when it fits, else over its right edge
            }
            anaForm = form;
            form.ShowInTaskbar = false;
            form.FormClosed += delegate { if (anaForm == form) anaForm = null; };
            form.Show(this);
        }

        // like CheckLog, but also while the game runs (the user asked)
        void CheckLogNow()
        {
            bool was = cur.Running;
            cur.Running = false;
            try { CheckLog(); } finally { cur.Running = was; }
        }

        // ---------------------------------------------------------------- advanced tab ([Caps], [FFB] Boost, [Identity], [DevType])
        NumericUpDown advMaxBtn = new NoWheelNumeric(), advBoost = new NoWheelNumeric();
        CheckBox advFlagDev = new CheckBox(), advFlagFmt = new CheckBox(), advIgnoreDz = new CheckBox(), advAutoCoop = new CheckBox(), advNonExcl = new CheckBox(), advGainMag = new CheckBox(), advBlockSine = new CheckBox();
        ComboBox advIdentity = new NoWheelCombo(), advDevType = new NoWheelCombo();
        bool advLoading;
        string advIdCustom, advTypeCustom;                 // a value written by hand that is not one of the choices: kept as it is
        static readonly string[] IdentityValues = new string[] { "", "046D:C298 Logitech Driving Force Pro", "046D:CA03 Logitech MOMO Racing Force Feedback Wheel", "045E:0034 Microsoft SideWinder Force Feedback Wheel", "046D:C29B Logitech G27 Racing Wheel" };       // DFP ID from memory (hid-lg4ff / usb.ids), not checked; SideWinder FF Wheel 045E:0034 (3 axes, 8 buttons) from ffb-probe.com, one report; MOMO Racing CA03 read from a real wheel by the user
        const string DevTypeJoystick = "10114";

        int AdvHead(TableLayoutPanel t, int row, string title)
        {
            Label h = new Label(); h.Text = title; h.AutoSize = true; h.Font = new Font("Segoe UI", 11f, FontStyle.Bold); h.ForeColor = Color.FromArgb(0, 70, 160); h.Margin = new Padding(3, 10, 3, 4);
            t.Controls.Add(h, 0, row); t.SetColumnSpan(h, 2);
            return row + 1;
        }

        // the long explanation of a setting is a speech bubble (the "?" next to the title, the title and the control); one short line stays visible
        ToolTip helpTip = new ToolTip { IsBalloon = true, AutoPopDelay = 30000, InitialDelay = 300, ReshowDelay = 100, ShowAlways = true };

        static string WrapText(string s, int width)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string para in s.Split('\n'))
            {
                int col = 0;
                foreach (string w in para.Split(' '))
                {
                    if (col + w.Length > width && col > 0) { sb.Append('\n'); col = 0; }
                    else if (col > 0) { sb.Append(' '); col++; }
                    sb.Append(w); col += w.Length;
                }
                sb.Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }

        int AdvNote(TableLayoutPanel t, int row, string text)
        {
            Label n = new Label(); n.Text = text; n.AutoSize = true; n.MaximumSize = new Size(900, 0); n.UseCompatibleTextRendering = true; n.ForeColor = SystemColors.GrayText; n.Margin = new Padding(3, 0, 3, 6);
            t.Controls.Add(n, 0, row); t.SetColumnSpan(n, 2);
            return row + 1;
        }

        int AdvRow(TableLayoutPanel t, int row, string key, Control c, bool restart)
        {
            string longText = WrapText(Loc.T(key + ".d") + "\n\n" + (restart ? Loc.T("adv.restart") : Loc.T("adv.live")), 70);
            FlowLayoutPanel head = new FlowLayoutPanel(); head.AutoSize = true; head.WrapContents = false; head.Margin = new Padding(0);
            Label l = new Label(); l.Text = Loc.T(key); l.AutoSize = true; l.MaximumSize = new Size(290, 0); l.Font = new Font(Font, FontStyle.Bold); l.Padding = new Padding(0, 6, 0, 0); l.Margin = new Padding(0);
            Button q = new Button(); q.Text = "?"; q.Width = 22; q.Height = 22; q.FlatStyle = FlatStyle.Flat; q.FlatAppearance.BorderColor = SystemColors.ControlDark; q.Font = new Font(Font, FontStyle.Bold); q.Margin = new Padding(6, 3, 0, 0); q.Padding = new Padding(0);
            q.Click += delegate { helpTip.Show(longText, q, q.Width + 6, 0, 30000); };
            q.GotFocus += delegate { helpTip.Show(longText, q, q.Width + 6, 0, 30000); };
            q.LostFocus += delegate { helpTip.Hide(q); };
            head.Controls.Add(l); head.Controls.Add(q);
            helpTip.SetToolTip(l, longText); helpTip.SetToolTip(q, longText);
            t.Controls.Add(head, 0, row); t.Controls.Add(c, 1, row);
            Label d = new Label(); d.Text = Loc.T(key + ".s") + (restart ? "\n" + Loc.T("adv.restart") : "");
            d.AutoSize = true; d.MaximumSize = new Size(900, 0); d.UseCompatibleTextRendering = true; d.ForeColor = SystemColors.GrayText; d.Margin = new Padding(3, 2, 3, 0);
            t.Controls.Add(d, 0, row + 1); t.SetColumnSpan(d, 2);
            Label sep = new Label(); sep.AutoSize = false; sep.Height = 2; sep.BorderStyle = BorderStyle.Fixed3D; sep.Dock = DockStyle.Fill; sep.Margin = new Padding(0, 6, 0, 6);
            t.Controls.Add(sep, 0, row + 2); t.SetColumnSpan(sep, 2);
            return row + 3;
        }

        void BuildAdvancedTab()
        {
            TabPage tp = new TabPage(Loc.T("tab.advanced"));
            TableLayoutPanel t = new TableLayoutPanel();
            t.Dock = DockStyle.Top; t.AutoSize = true; t.ColumnCount = 2; t.Padding = new Padding(10);
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            EventHandler ch = delegate { if (!advLoading) SaveAdvanced(); };
            advMaxBtn.Minimum = 0; advMaxBtn.Maximum = 128; advMaxBtn.Width = 70; advMaxBtn.ValueChanged += ch;
            advBoost.Minimum = 10; advBoost.Maximum = 300; advBoost.Value = 100; advBoost.Width = 70; advBoost.ValueChanged += ch;
            advFlagDev.Text = Loc.T("adv.flag.dev"); advFlagDev.AutoSize = true; advFlagDev.CheckedChanged += ch;
            advFlagFmt.Text = Loc.T("adv.flag.fmt"); advFlagFmt.AutoSize = true; advFlagFmt.CheckedChanged += ch;
            FlowLayoutPanel fl = new FlowLayoutPanel(); fl.AutoSize = true; fl.WrapContents = false; fl.Margin = new Padding(0);
            fl.Controls.Add(advFlagDev); fl.Controls.Add(advFlagFmt);
            advIgnoreDz.Text = "1"; advIgnoreDz.AutoSize = true; advIgnoreDz.CheckedChanged += ch;
            advAutoCoop.Text = "1"; advAutoCoop.AutoSize = true; advAutoCoop.CheckedChanged += ch;
            advNonExcl.Text = "1"; advNonExcl.AutoSize = true; advNonExcl.CheckedChanged += ch;
            advNonExcl.CheckedChanged += delegate { advAutoCoop.Enabled = !advNonExcl.Checked; };       // AutoCoop only matters when exclusive access is requested
            advGainMag.Text = "1"; advGainMag.AutoSize = true; advGainMag.CheckedChanged += ch;
            advBlockSine.Text = "1"; advBlockSine.AutoSize = true; advBlockSine.CheckedChanged += ch;
            advIdentity.DropDownStyle = ComboBoxStyle.DropDownList; advIdentity.Width = 320; advIdentity.SelectedIndexChanged += ch;
            advDevType.DropDownStyle = ComboBoxStyle.DropDownList; advDevType.Width = 320; advDevType.SelectedIndexChanged += ch;
            int row = 0;
            row = AdvHead(t, row, Loc.T("adv.group.ffb"));
            row = AdvNote(t, row, Loc.T("adv.group.ffb.note"));
            row = AdvRow(t, row, "adv.gainmag", advGainMag, true);
            row = AdvRow(t, row, "adv.blocksine", advBlockSine, true);
            row = AdvRow(t, row, "adv.boost", advBoost, false);
            row = AdvRow(t, row, "adv.autocoop", advAutoCoop, true);
            row = AdvRow(t, row, "adv.nonexcl", advNonExcl, true);
            row = AdvHead(t, row, Loc.T("adv.group.compat"));
            row = AdvNote(t, row, Loc.T("adv.group.compat.note"));
            row = AdvRow(t, row, "adv.maxbtn", advMaxBtn, true);
            row = AdvRow(t, row, "adv.flags", fl, true);
            row = AdvRow(t, row, "adv.ignoredz", advIgnoreDz, true);
            row = AdvRow(t, row, "adv.identity", advIdentity, true);
            row = AdvRow(t, row, "adv.devtype", advDevType, true);
            Label intro = new Label(); intro.Text = Loc.T("adv.intro"); intro.Dock = DockStyle.Top; intro.Padding = new Padding(10, 8, 10, 6); intro.ForeColor = SystemColors.GrayText; intro.UseCompatibleTextRendering = true;
            EventHandler fitIntro = delegate { if (intro.Width > 40) intro.Height = (int)Math.Ceiling(intro.CreateGraphics().MeasureString(intro.Text, intro.Font, intro.Width - 20).Height) + 16; };
            intro.Resize += fitIntro; intro.TextChanged += fitIntro; fitIntro(null, null);
            tp.AutoScroll = true;
            tp.Controls.Add(t);
            tp.Controls.Add(intro);
            tabs.TabPages.Add(tp);
        }

        static uint ParseHex(string s)
        {
            uint v;
            return s != null && uint.TryParse(s.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        // reads the advanced settings of the selected game into the controls
        void RefreshAdvanced()
        {
            RefreshLogOpt();
            if (ini == null || DestKey == null) return;
            advLoading = true;
            int mb; int.TryParse(ini.Get("Caps", "MaxButtons") ?? "0", out mb);
            advMaxBtn.Value = Math.Max(0, Math.Min(128, mb));
            uint cf = ParseHex(ini.Get("Caps", "ClearFlags"));
            advFlagDev.Checked = (cf & 2) != 0; advFlagFmt.Checked = (cf & 8) != 0;
            advIgnoreDz.Checked = (ini.Get("Caps", "IgnoreDeadzone") ?? "0").Trim() != "0";
            advAutoCoop.Checked = (ini.Get("Caps", "AutoCoop") ?? "1").Trim() != "0";
            advNonExcl.Checked = (ini.Get("Caps", "NonExclusive") ?? "0").Trim() != "0";
            int bo; if (!int.TryParse(ini.Get("FFB", "Boost") ?? "100", out bo)) bo = 100;
            advBoost.Value = Math.Max(10, Math.Min(300, bo));
            advGainMag.Checked = (ini.Get("FFB", "GainAsMagnitude") ?? "0").Trim() != "0";
            advBlockSine.Checked = (ini.Get("FFB", "BlockSine") ?? "0").Trim() != "0";
            // wheel identity
            advIdentity.Items.Clear(); advIdentity.Items.Add(Loc.T("adv.identity.none"));
            for (int i = 1; i < IdentityValues.Length; i++) advIdentity.Items.Add(IdentityValues[i]);
            advIdCustom = null;
            string id = ini.Get("Identity", DestKey);
            int sel = 0;
            if (!string.IsNullOrEmpty(id))
            {
                sel = Array.IndexOf(IdentityValues, id);
                if (sel < 0) { advIdCustom = id; advIdentity.Items.Add(id); sel = advIdentity.Items.Count - 1; }
            }
            advIdentity.SelectedIndex = sel;
            // device type
            advDevType.Items.Clear(); advDevType.Items.Add(Loc.T("adv.devtype.none")); advDevType.Items.Add(Loc.T("adv.devtype.joy"));
            advTypeCustom = null;
            string dt = ini.Get("DevType", DestKey);
            sel = 0;
            if (!string.IsNullOrEmpty(dt))
            {
                if (string.Equals(dt.Trim(), DevTypeJoystick, StringComparison.OrdinalIgnoreCase)) sel = 1;
                else { advTypeCustom = dt; advDevType.Items.Add(dt); sel = advDevType.Items.Count - 1; }
            }
            advDevType.SelectedIndex = sel;
            advLoading = false;
        }

        void SaveAdvanced()
        {
            if (cur == null || ini == null || DestKey == null) return;
            try
            {
                if (!dirty) ini = IniFile.Load(IniPath);
                int mb = (int)advMaxBtn.Value;
                if (mb > 0) ini.Set("Caps", "MaxButtons", mb.ToString()); else ini.Remove("Caps", "MaxButtons");
                uint cf = ParseHex(ini.Get("Caps", "ClearFlags")) & ~(2u | 8u);          // other bits written by hand stay
                if (advFlagDev.Checked) cf |= 2; if (advFlagFmt.Checked) cf |= 8;
                if (cf != 0) ini.Set("Caps", "ClearFlags", cf.ToString("X")); else ini.Remove("Caps", "ClearFlags");
                if (advIgnoreDz.Checked) ini.Set("Caps", "IgnoreDeadzone", "1"); else ini.Remove("Caps", "IgnoreDeadzone");
                if (!advAutoCoop.Checked) ini.Set("Caps", "AutoCoop", "0"); else ini.Remove("Caps", "AutoCoop");
                if (advNonExcl.Checked) ini.Set("Caps", "NonExclusive", "1"); else ini.Remove("Caps", "NonExclusive");
                int bo = (int)advBoost.Value;
                if (bo != 100) ini.Set("FFB", "Boost", bo.ToString()); else ini.Remove("FFB", "Boost");
                if (advGainMag.Checked) ini.Set("FFB", "GainAsMagnitude", "1"); else ini.Remove("FFB", "GainAsMagnitude");
                if (advBlockSine.Checked) ini.Set("FFB", "BlockSine", "1"); else ini.Remove("FFB", "BlockSine");
                int si = advIdentity.SelectedIndex;
                string idv = si <= 0 ? null : si < IdentityValues.Length ? IdentityValues[si] : advIdCustom;
                if (idv != null) ini.Set("Identity", DestKey, idv); else ini.Remove("Identity", DestKey);
                int ti = advDevType.SelectedIndex;
                string dtv = ti <= 0 ? null : ti == 1 ? DevTypeJoystick : advTypeCustom;
                if (dtv != null) ini.Set("DevType", DestKey, dtv); else ini.Remove("DevType", DestKey);
                SyncNames(ini);
                CommitIni();
            }
            catch (Exception ex) { Dlg.Show(this, Loc.T("msg.savefail") + ex.Message, "CtrlMuxr"); }
        }

        // ---------------------------------------------------------------- steering tab ([Shape])
        void BuildSteeringTab()
        {
            TabPage tp = new TabPage(Loc.T("tab.tuning"));
            TableLayoutPanel t = new TableLayoutPanel();
            t.Dock = DockStyle.Top; t.AutoSize = true; t.ColumnCount = 5; t.Padding = new Padding(10);
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270));

            Label l0 = new Label(); l0.Text = Loc.T("shape.dev"); l0.AutoSize = true; l0.Padding = new Padding(0, 6, 0, 0);
            axisBox.DropDownStyle = ComboBoxStyle.DropDownList;
            axisBox.Width = 170;
            axisBox.Items.AddRange(new object[] { "X", "Y", "Z", "Rx", "Ry", "Rz", "S0", "S1" });
            axisBox.SelectedIndex = 0;
            axisBox.SelectedIndexChanged += delegate { if (!loading) ReadShapeToUi(); };
            l0.Text = Loc.T("shape.dev");
            shapeDevBox.DropDownStyle = ComboBoxStyle.DropDownList; shapeDevBox.Width = 290;
            ((NoWheelCombo)shapeDevBox).Offline = delegate(int x) { return x >= 0 && x < shapeDevKeys.Count && !Connected(shapeDevKeys[x]); };
            shapeDevBox.SelectedIndexChanged += delegate { if (loading) return; RefreshAxisLabels(); ReadShapeToUi(); };
            FlowLayoutPanel fp = new FlowLayoutPanel(); fp.AutoSize = true; fp.WrapContents = false; fp.Margin = new Padding(0);
            fp.Controls.Add(shapeDevBox); fp.Controls.Add(axisBox);
            t.Controls.Add(l0, 0, 0); t.Controls.Add(fp, 1, 0); t.SetColumnSpan(fp, 4);

            invertBox.Text = Loc.T("shape.invert"); invertBox.AutoSize = true;
            invertBox.CheckedChanged += delegate { Changed(); };
            t.Controls.Add(invertBox, 1, 1);

            for (int i = 0; i < Params.Length; i++)
            {
                object[] p = Params[i];
                double min = (double)p[2], max = (double)p[3];
                int dec = (int)p[4], scale = (int)p[5];
                Label l = new Label(); l.Text = (string)p[0]; l.AutoSize = true; l.Padding = new Padding(0, 6, 0, 0);
                TrackBar b = new NoWheelTrack(); b.AutoSize = false; b.Height = 28; b.Minimum = (int)Math.Round(min * scale); b.Maximum = (int)Math.Round(max * scale);
                b.TickStyle = TickStyle.None; b.Dock = DockStyle.Fill; b.SmallChange = Math.Max(1, scale / 10); b.LargeChange = scale;
                NumericUpDown n = new NoWheelNumeric(); n.DecimalPlaces = dec; n.Minimum = (decimal)min; n.Maximum = (decimal)max;
                n.Increment = dec == 0 ? 1 : (decimal)Math.Pow(10, -dec); n.Width = 70;
                Label u = new Label(); u.Text = (string)p[1]; u.AutoSize = true; u.Padding = new Padding(0, 6, 0, 0);
                int idx = i, sc = scale;
                if (i == 5) b.LargeChange = 2000;
                b.ValueChanged += delegate { if (loading) return; loading = true; nums[idx].Value = (decimal)b.Value / sc; loading = false; Changed(); };
                n.ValueChanged += delegate { if (loading) return; loading = true; bars[idx].Value = (int)Math.Round((double)nums[idx].Value * sc); loading = false; Changed(); };
                bars[i] = b; nums[i] = n;
                int row = 2 + 3 * i;
                t.Controls.Add(l, 0, row); t.Controls.Add(b, 1, row); t.Controls.Add(n, 2, row); t.Controls.Add(u, 3, row);
                FlowLayoutPanel bf = new FlowLayoutPanel(); bf.AutoSize = true; bf.WrapContents = false; bf.Margin = new Padding(0);
                Button rs = new Button(); rs.Text = Loc.T("shape.reset"); rs.AutoSize = true; rs.Margin = new Padding(3, 2, 3, 0);
                rs.Click += delegate { nums[idx].Value = (decimal)ParamDefaults[idx]; };
                tip.SetToolTip(rs, Loc.T("tt.shape.reset"));
                bf.Controls.Add(rs);
                if (i == 5)
                {
                    Button cu = new Button(); cu.Text = Loc.T("shape.setcurrent"); cu.AutoSize = true; cu.Margin = new Padding(3, 2, 3, 0);
                    cu.Click += delegate { SetCentreFromDevice(); };
                    tip.SetToolTip(cu, Loc.T("tt.shape.setcurrent"));
                    bf.Controls.Add(cu);
                }
                t.Controls.Add(bf, 4, row);
                // one plain sentence directly under its own control: what it does and when to use it; a line separates the settings
                Label d = new Label(); d.Text = Loc.T("p.desc." + new string[] { "dz", "expo", "gain", "adz", "lead", "center" }[i]);
                d.AutoSize = true; d.MaximumSize = new Size(640, 0); d.UseCompatibleTextRendering = true; d.ForeColor = SystemColors.GrayText; d.Margin = new Padding(3, 0, 3, 0);
                t.Controls.Add(d, 1, row + 1); t.SetColumnSpan(d, 3);
                Label sep = new Label(); sep.AutoSize = false; sep.Height = 2; sep.BorderStyle = BorderStyle.Fixed3D; sep.Dock = DockStyle.Fill; sep.Margin = new Padding(0, 4, 0, 4);
                t.Controls.Add(sep, 0, row + 2); t.SetColumnSpan(sep, 5);
            }

            Label hint = new Label();
            hint.Text = Loc.T("shape.intro");
            hint.Dock = DockStyle.Top; hint.Height = 66; hint.Padding = new Padding(10, 6, 10, 0); hint.ForeColor = SystemColors.GrayText;

            tp.AutoScroll = true;           // the explanations make the page taller than a small window
            tp.Controls.Add(t);
            tp.Controls.Add(hint);          // added last = docked first: the explanation sits at the top of the page
            tabs.TabPages.Add(tp);
        }

        // the selected axis of the selected device, as it is right now: becomes the centre point of the axis
        void SetCentreFromDevice()
        {
            if (cur == null || ini == null) return;
            try
            {
                if (devInfos.Length == 0) EnumDevices();
                string key = WheelKey;
                int d = -1;
                for (int i = 0; i < devInfos.Length; i++) if (string.Format("{0:X4}:{1:X4}", devInfos[i].vid, devInfos[i].pid) == key) d = i;
                int[] ax = new int[8]; int[] pov = new int[4]; byte[] b = new byte[128];
                int rc = d < 0 ? -1 : DevRead.DR_Open(d, Handle);
                if (rc == 0)
                {
                    System.Threading.Thread.Sleep(150);
                    DevRead.DR_Read(d, ax, pov, b);
                    System.Threading.Thread.Sleep(50);
                    rc = DevRead.DR_Read(d, ax, pov, b);
                }
                if (rc != 0) { Dlg.Show(this, Loc.T("shape.readfail"), "CtrlMuxr"); return; }
                nums[5].Value = Math.Max(nums[5].Minimum, Math.Min(nums[5].Maximum, ax[Math.Max(0, axisBox.SelectedIndex)]));
            }
            catch (Exception ex) { Dlg.Show(this, Loc.T("shape.readfail") + " " + ex.Message, "CtrlMuxr"); }
        }

        string WheelKey
        {
            get
            {
                int i = shapeDevBox.SelectedIndex;
                if (i >= 0 && i < shapeDevKeys.Count) return shapeDevKeys[i];
                return cur != null && cur.Wheel != null ? cur.Wheel : "346E:0002";
            }
        }

        // is the device (VID:PID, or a "VID:PID.AXIS" key) connected now? Unknown when devread.dll failed: then nothing is marked
        bool Connected(string key)
        {
            if (devReadFailed) return true;
            int dot = key.IndexOf('.');
            if (dot > 0) key = key.Substring(0, dot);
            for (int i = 0; i < devInfos.Length; i++)
                if (string.Format("{0:X4}:{1:X4}", devInfos[i].vid, devInfos[i].pid) == key) return true;
            return false;
        }

        string OfflineTag(string key) { return Connected(key) ? "" : "  (" + Loc.T("dev.offline") + ")"; }

        // how many buttons of the device are shown as sources (the proxy reads B1..B32 at most): the real count when it is connected,
        // the count remembered in devices.ini (Buttons=) otherwise, 32 when it is not known
        int BtnCount(string dev)
        {
            int n = 0;
            for (int i = 0; i < devInfos.Length && n == 0; i++)
                if (string.Format("{0:X4}:{1:X4}", devInfos[i].vid, devInfos[i].pid) == dev) n = devInfos[i].numButtons;
            if (n <= 0) int.TryParse(names.Get(dev, "Buttons") ?? "0", out n);
            return n <= 0 ? 32 : Math.Min(32, n);
        }

        // a button is offered as a source when it has a name or is already mapped; otherwise when it exists on the device
        // (or the count is unknown), unless "only named" is on
        static bool ButtonShown(int b, int count, bool named, bool mapped, bool onlyNamed)
        {
            if (named || mapped) return true;
            return !onlyNamed && b < count;
        }

        string DeviceTitle(string key)
        {
            for (int i = 0; i < devInfos.Length; i++)
                if (string.Format("{0:X4}:{1:X4}", devInfos[i].vid, devInfos[i].pid) == key) return devInfos[i].name.Trim();
            string n = names.Get(key, "Name");
            return string.IsNullOrEmpty(n) ? Loc.T("dev.generic") : n;
        }

        // devices the settings can address: the game's wheel first, then every VID:PID in its INI, named devices and connected ones
        void FillShapeDevices()
        {
            string keep = shapeDevBox.SelectedIndex >= 0 && shapeDevBox.SelectedIndex < shapeDevKeys.Count ? shapeDevKeys[shapeDevBox.SelectedIndex] : null;
            List<string> keys = new List<string>();
            if (cur != null && cur.Wheel != null) keys.Add(cur.Wheel.ToUpperInvariant());
            if (ini != null)
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(string.Join("\n", ini.Lines.ToArray()), @"\b[0-9A-Fa-f]{4}:[0-9A-Fa-f]{4}\b"))
                    if (!keys.Contains(m.Value.ToUpperInvariant())) keys.Add(m.Value.ToUpperInvariant());
            foreach (string l in names.Lines)
            {
                string t = l.Trim();
                if (t.StartsWith("[") && t.EndsWith("]") && t.Length == 11 && !keys.Contains(t.Substring(1, 9).ToUpperInvariant())) keys.Add(t.Substring(1, 9).ToUpperInvariant());
            }
            for (int i = 0; i < devInfos.Length; i++)
            {
                string k = string.Format("{0:X4}:{1:X4}", devInfos[i].vid, devInfos[i].pid);
                if (!keys.Contains(k)) keys.Add(k);
            }
            bool was = loading;
            loading = true;
            shapeDevKeys = keys;
            shapeDevBox.Items.Clear();
            foreach (string k in keys) shapeDevBox.Items.Add(DeviceTitle(k) + "   (" + k + ")" + OfflineTag(k));
            int sel = keep != null ? keys.IndexOf(keep) : 0;
            if (shapeDevBox.Items.Count > 0) shapeDevBox.SelectedIndex = sel >= 0 ? sel : 0;
            loading = was;
        }

        // [Names]: the user's axis/button names written into the game's INI for readability (the proxy ignores the section)
        bool SyncNames(IniFile f)
        {
            List<string> before = new List<string>(f.Lines);
            f.RemoveSection("Names");
            List<string> devs = new List<string>();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(string.Join("\n", f.Lines.ToArray()), @"\b[0-9A-Fa-f]{4}:[0-9A-Fa-f]{4}\b"))
                if (!devs.Contains(m.Value.ToUpperInvariant())) devs.Add(m.Value.ToUpperInvariant());
            List<string> add = new List<string>();
            foreach (string d in devs)
            {
                for (int i = 0; i < 8; i++) { string n = names.Get(d, AxisNames[i]); if (!string.IsNullOrEmpty(n)) add.Add(d + "." + AxisNames[i] + "=" + n); }
                for (int b = 0; b < 128; b++) { string n = names.Get(d, BKey(b)); if (!string.IsNullOrEmpty(n)) add.Add(d + "." + BKey(b) + "=" + n); }
            }
            if (add.Count > 0)
            {
                f.Lines.Add("");
                f.Lines.Add("[Names]");
                f.Lines.Add("; Written by CtrlMuxr from the names given on the Laitteet tab. Not read by the proxy.");
                f.Lines.AddRange(add);
            }
            if (f.Lines.Count != before.Count) return true;
            for (int i = 0; i < before.Count; i++) if (before[i] != f.Lines[i]) return true;
            return false;
        }

        void SyncNamesToAllGames()
        {
            foreach (GameDef g in games)
            {
                try
                {
                    string path = Path.Combine(g.Folder, "dinput_proxy.ini");
                    if (!File.Exists(path)) continue;
                    IniFile f = IniFile.Load(path);
                    if (SyncNames(f)) f.Save();
                }
                catch { }
            }
            if (cur != null && !dirty) { ini = IniFile.Load(IniPath); rawBox.Text = string.Join(Environment.NewLine, ini.Lines.ToArray()); }
        }

        string ShapeKey { get { return WheelKey + "." + AxisNames[Math.Max(0, axisBox.SelectedIndex)]; } }

        // the axis list shows the names the user gave on the Laitteet tab
        void RefreshAxisLabels()
        {
            int sel = Math.Max(0, axisBox.SelectedIndex);
            bool was = loading;
            loading = true;
            axisBox.Items.Clear();
            for (int i = 0; i < 8; i++)
            {
                string n = names.Get(WheelKey, AxisNames[i]);
                axisBox.Items.Add(AxisNames[i] + (string.IsNullOrEmpty(n) ? "" : "  \u2013  " + n));
            }
            axisBox.SelectedIndex = sel;
            loading = was;
        }

        void ReadShapeToUi()
        {
            if (ini == null) return;
            ShapeVals s = ShapeVals.Parse(ini.Get("Shape", ShapeKey));
            loading = true;
            invertBox.Checked = s.Invert;
            double[] v = new double[] { s.Dz, s.Expo, s.Gain, s.Adz, s.Lead, Math.Round(s.Center) };
            for (int i = 0; i < v.Length; i++)
            {
                double lo = (double)Params[i][2], hi = (double)Params[i][3];
                double x = Math.Max(lo, Math.Min(hi, v[i]));
                nums[i].Value = (decimal)x;
                bars[i].Value = (int)Math.Round(x * (int)Params[i][5]);
            }
            loading = false;
        }

        // running game: save live after a short pause; otherwise put the change into the draft right away (Save writes it)
        void Changed()
        {
            if (loading || ini == null || cur == null) return;
            if (cur.Running) { saveTimer.Stop(); saveTimer.Start(); }
            else SaveShape();
        }

        void SaveShape()
        {
            if (cur == null || ini == null) return;
            try
            {
                ShapeVals s = new ShapeVals();
                s.Invert = invertBox.Checked;
                s.Dz = (double)nums[0].Value; s.Expo = (double)nums[1].Value; s.Gain = (double)nums[2].Value;
                s.Adz = (double)nums[3].Value; s.Lead = (double)nums[4].Value; s.Center = (double)nums[5].Value;
                if (!dirty) ini = IniFile.Load(IniPath);            // re-read: keeps edits made elsewhere (e.g. by hand); a draft is kept as it is
                ini.Set("Shape", ShapeKey, s.ToString());
                SyncNames(ini);
                CommitIni();
            }
            catch (Exception ex) { Dlg.Show(this, Loc.T("msg.savefail") + ex.Message, "CtrlMuxr"); }
        }

        // ---------------------------------------------------------------- raw INI tab
        void BuildRawTab()
        {
            TabPage tp = new TabPage(Loc.T("tab.ini"));
            rawBox.Multiline = true; rawBox.ScrollBars = ScrollBars.Both; rawBox.WordWrap = false; rawBox.Font = new Font("Consolas", 9.5f);
            rawBox.Dock = DockStyle.Fill; rawBox.AcceptsReturn = true; rawBox.AcceptsTab = true;
            FlowLayoutPanel bp = new FlowLayoutPanel(); bp.Dock = DockStyle.Top; bp.Height = 36; bp.Padding = new Padding(4);
            Button save = new Button(); save.Text = Loc.T("ini.save"); save.AutoSize = true; iniSaveBtn = save;
            save.Click += delegate
            {
                if (cur == null) return;
                try
                {
                    BackupOnce(); PushUndo();
                    File.WriteAllText(IniPath + ".tmp", rawBox.Text.Replace("\r\n", "\n").Replace("\n", "\r\n"), IniFile.Enc);
                    if (File.Exists(IniPath)) File.Replace(IniPath + ".tmp", IniPath, null); else File.Move(IniPath + ".tmp", IniPath);
                    lastSaved = DateTime.Now; undoneTo = DateTime.MinValue;
                    Store.AfterSave(appDir, cur.Name, cur.Folder);
                    ReloadUi();
                }
                catch (Exception ex) { Dlg.Show(this, Loc.T("msg.savefail") + ex.Message, "CtrlMuxr"); }
            };
            Button reload = new Button(); reload.Text = Loc.T("ini.reload"); reload.AutoSize = true; iniReloadBtn = reload;
            reload.Click += delegate
            {
                if (cur == null) return;
                if (dirty && Dlg.Show(this, Loc.T("msg.discard", cur.Name), "CtrlMuxr", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                ReloadUi();
            };
            bp.Controls.Add(save); bp.Controls.Add(reload);
            Label note = new Label(); note.Text = Loc.T("ini.note"); note.AutoSize = true; note.Padding = new Padding(8, 8, 0, 0);
            bp.Controls.Add(note);
            tp.Controls.Add(rawBox); tp.Controls.Add(bp);
            tabs.TabPages.Add(tp);
        }

        // ---------------------------------------------------------------- log tab
        void BuildLogTab()
        {
            TabPage tp = logTab = new TabPage(Loc.T("tab.log"));
            logBox.Multiline = true; logBox.ReadOnly = true; logBox.ScrollBars = ScrollBars.Both; logBox.WordWrap = false;
            logBox.Font = new Font("Consolas", 9f); logBox.Dock = DockStyle.Fill;
            tp.Controls.Add(logBox);
            tabs.TabPages.Add(tp);
        }

        string lastLogText, lastLogName;

        void RefreshLog()
        {
            if (cur == null || tabs.SelectedTab != logTab) return;
            try
            {
                string newest = null; DateTime best = DateTime.MinValue;
                foreach (string f in Directory.GetFiles(cur.Folder, "dinput_*.log"))
                {
                    if (!SessionLog.IsSessionLog(f)) continue;
                    DateTime t = File.GetLastWriteTime(f);
                    if (t > best) { best = t; newest = f; }
                }
                string errLog = Path.Combine(cur.Folder, "dinput_errors.log");
                if (newest == null && File.Exists(errLog)) newest = errLog;                      // no session log: show the errors the proxy kept
                if (newest == null) { if (logBox.Text != Loc.T("log.none")) logBox.Text = Loc.T("log.none"); lastLogText = null; return; }
                using (FileStream fs = new FileStream(newest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long n = Math.Min(fs.Length, 200000);
                    fs.Seek(-n, SeekOrigin.End);
                    byte[] buf = new byte[n];
                    int r = fs.Read(buf, 0, buf.Length);
                    string s = Encoding.GetEncoding(1252).GetString(buf, 0, r);
                    int nl = s.IndexOf('\n');
                    string text = Path.GetFileName(newest) + "\r\n" + (nl >= 0 ? s.Substring(nl + 1) : s).Replace("\r", "").Replace("\n", "\r\n");
                    if (text == lastLogText) return;                                               // nothing new: the view stays as it is
                    bool newFile = lastLogName != newest;
                    if (!newFile && logBox.TextLength > 0 && !Native.AtBottom(logBox)) return;      // the user has scrolled up to read: do not jump to the end
                    lastLogText = text; lastLogName = newest;
                    logBox.Text = text;
                    logBox.SelectionStart = logBox.Text.Length; logBox.ScrollToCaret();
                }
            }
            catch { }
        }

        // Held while the app runs: the proxy in a game logs only while it can see this (or has [Log] Always=1). Lives until the process ends.
        static System.Threading.Mutex runMutex;

        static void HoldRunMutex()
        {
            try
            {
                System.Security.AccessControl.MutexSecurity sec = new System.Security.AccessControl.MutexSecurity();
                sec.AddAccessRule(new System.Security.AccessControl.MutexAccessRule(new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid, null),
                    System.Security.AccessControl.MutexRights.Synchronize, System.Security.AccessControl.AccessControlType.Allow));      // a game running with other rights can open it too
                bool created;
                runMutex = new System.Threading.Mutex(false, "Local\\CtrlMuxr_Running", out created, sec);
            }
            catch { }       // a second instance cannot open the first one's mutex: the first one keeps the proxy informed
        }

        [STAThread]
        static void Main(string[] args)
        {
            Loc.Init(AppDomain.CurrentDomain.BaseDirectory);
            if (args.Length > 0 && args[0].StartsWith("--")) Settings.ReadOnly = true;
            if (args.Length > 1 && args[0] == "--addtest")
            {
                StringBuilder sb = new StringBuilder();
                string[] exes = new string[] { @"E:\Volvo S40 Racing\s40.exe", @"C:\Steam\steamapps\common\S" + "\u00e9" + @"bastien Loeb Rally EVO\SLRX64.exe", @"E:\DaytonaUSA\DAYTONA.EXE", @"E:\CMRally\Rally.exe", @"E:\Need For Speed 2 SE\NFS2SEN.EXE", @"C:\Windows\notepad.exe" };
                bool first = true;
                foreach (string e in exes)
                {
                    if (!File.Exists(e)) { sb.AppendLine("missing: " + e); continue; }
                    string tmp = Path.Combine(Path.GetTempPath(), "controlapp_addtest_" + Path.GetFileNameWithoutExtension(e));
                    if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                    using (AddGameForm f = new AddGameForm(AppDomain.CurrentDomain.BaseDirectory, new DRDevInfo[0], delegate(string fo) { return null; }))
                        sb.AppendLine(f.TestRun(e, tmp, first || e.Contains("DAYTONA") || e.Contains("Rally.exe")));
                    first = false;
                }
                File.WriteAllText(args[1], sb.ToString());
                Environment.Exit(0);
                return;
            }
            if (args.Length > 2 && args[0] == "--uitest")
            {
                string res;
                try { using (MainForm f = new MainForm()) res = f.UiTest(args[1]); } catch (Exception ex) { res = "EXCEPTION " + ex; }
                File.WriteAllText(args[2], res);
                Environment.Exit(res.Contains("FAIL") || res.StartsWith("EXCEPTION") ? 1 : 0);
                return;
            }
            if (args.Length > 1 && args[0] == "--perftest")
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                string res;
                try { using (MainForm f = new MainForm()) res = f.PerfTest(args.Length > 2 ? args[2] : "", args.Length > 3 ? int.Parse(args[3]) : 100, args.Length > 4 ? int.Parse(args[4]) : 0); } catch (Exception ex) { res = "EXCEPTION " + ex; }
                File.WriteAllText(args[1], res);
                Environment.Exit(res.StartsWith("EXCEPTION") ? 1 : 0);
                return;
            }
            if (args.Length > 2 && args[0] == "--soak")
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                string res;
                try { using (MainForm f = new MainForm()) res = f.SoakTest(args[1], int.Parse(args[2])); } catch (Exception ex) { res = "EXCEPTION " + ex; File.AppendAllText(args[1], res); }
                Environment.Exit(res.StartsWith("EXCEPTION") ? 1 : 0);
                return;
            }
            if (args.Length > 0 && args[0] == "--selftest") { Environment.Exit(SelfTest.Run(args.Length > 1 ? args[1] : "selftest.txt")); return; }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            HoldRunMutex();
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.log"), DateTime.Now + "  " + e.Exception + Environment.NewLine); } catch { }
                Dlg.Show(Loc.T("msg.error") + e.Exception.Message + Environment.NewLine + Loc.T("msg.errorlog"), "CtrlMuxr");
            };
            do { Restart = false; Application.Run(new MainForm()); if (Restart) Loc.Init(AppDomain.CurrentDomain.BaseDirectory); } while (Restart);
        }
    }

    // `CtrlMuxr.exe --selftest out.txt`: checks the INI editing and shape parsing without opening a window
    static class SelfTest
    {
        public static int Run(string outFile)
        {
            StringBuilder sb = new StringBuilder();
            int fail = 0;
            string tmp = Path.Combine(Path.GetTempPath(), "controlapp_selftest.ini");
            File.WriteAllLines(tmp, new string[] { "; comment", "[Hide]", "0079:0006=", "[Shape]", "; note", "346E:0002.X=expo=0 dz=0 gain=110 adz=10 lead=0.08", "[Caps]", "MaxButtons=12", "" }, IniFile.Enc);
            IniFile f = IniFile.Load(tmp);
            ShapeVals s = ShapeVals.Parse(f.Get("Shape", "346E:0002.X"));
            fail += Check(sb, "parse gain", s.Gain == 110);
            fail += Check(sb, "parse lead", Math.Abs(s.Lead - 0.08) < 1e-9);
            s.Gain = 140; s.Lead = 0.25; s.Adz = 15; s.Invert = true;
            f.Set("Shape", "346E:0002.X", s.ToString());
            f.Set("Shape", "346E:0002.Y", "gain=100");
            f.Set("Mask", "346E:0002", "Y Rx");
            f.Save();
            IniFile g = IniFile.Load(tmp);
            fail += Check(sb, "roundtrip shape", g.Get("Shape", "346E:0002.X") == "invert dz=0 expo=0 gain=140 adz=15 lead=0.25");
            fail += Check(sb, "new key in section", g.Get("Shape", "346E:0002.Y") == "gain=100");
            fail += Check(sb, "new section", g.Get("Mask", "346E:0002") == "Y Rx");
            fail += Check(sb, "other sections untouched", g.Get("Caps", "MaxButtons") == "12" && g.Get("Hide", "0079:0006") == "");
            fail += Check(sb, "comments kept", g.Lines[0] == "; comment" && g.Lines.Contains("; note"));
            IniFile h = IniFile.Load(tmp);
            h.Lines.Add(""); h.Lines.Add("[Names]"); h.Lines.Add("346E:0002.X=Ohjaus");
            h.RemoveSection("Names");
            fail += Check(sb, "remove section", h.Get("Names", "346E:0002.X") == null && h.Lines[h.Lines.Count - 1].Trim().Length > 0);
            // PE machine types and the exclusive access test summary
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System), sysx86 = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
            fail += Check(sb, "PE machine x86", MainForm.PeMachine(Path.Combine(sysx86, "kernel32.dll")) == 0x14C);
            fail += Check(sb, "PE machine x64", !Environment.Is64BitOperatingSystem || MainForm.PeMachine(Path.Combine(sys, "kernel32.dll")) == 0x8664 || MainForm.PeMachine(Path.Combine(sys, "kernel32.dll")) == 0x14C);
            fail += Check(sb, "PE machine of a non-PE file is 0", MainForm.PeMachine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lang", "en.ini")) == 0);
            string okOut = "process: x86\ndevice: M\n[DI 500] EXCLUSIVE|BACKGROUND     SetCoop=0 Acquire=0 OK\n[DI 500] EXCLUSIVE|FOREGROUND     SetCoop=0 Acquire=0 OK\n[DI 500] NONEXCLUSIVE|BACKGROUND  SetCoop=0 Acquire=0 OK\n";
            string blockedOut = "process: x86\ndevice: M\n[DI 500] EXCLUSIVE|BACKGROUND     SetCoop=0 Acquire=80070005 FAILED\n[DI 500] EXCLUSIVE|FOREGROUND     SetCoop=0 Acquire=80070005 FAILED\n[DI 500] NONEXCLUSIVE|BACKGROUND  SetCoop=0 Acquire=0 OK\n";
            fail += Check(sb, "exclusive test: ok", MainForm.ExSummary(okOut) == "ok");
            fail += Check(sb, "exclusive test: blocked", MainForm.ExSummary(blockedOut) == "blocked");
            fail += Check(sb, "exclusive test: no device", MainForm.ExSummary("process: x86\n[DI 500] no device matching \"x\"\n") == "nodev");
            fail += Check(sb, "exclusive test: garbage", MainForm.ExSummary("") == "unknown" && MainForm.ExSummary("hello") == "unknown");
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            Loc.InitFor(appDir, "fi");
            fail += Check(sb, "loc fi", Loc.T("tab.devices") == "Laitteet" && Loc.T("app.title").StartsWith("CtrlMuxr"));
            fail += Check(sb, "loc newline escape", Loc.T("shape.hint1").EndsWith("\n"));
            fail += Check(sb, "loc missing key shows key", Loc.T("no.such.key") == "no.such.key");
            Loc.InitFor(appDir, "en");
            {
                string pt = Path.Combine(Path.GetTempPath(), "controlapp_preview.ini");
                File.WriteAllLines(pt, new string[] { "[Mask]", "346E:0002=Z", "[AxisMap]", "346E:0002.Rx=DDFD:5008.Y half", "346E:0002.Ry=DDFD:5008.Rx invert", "[AxisCombine]", "346E:0002.Y=DDFD:5008.X DDFD:5008.Y",
                    "[ButtonMap]", "346E:0002.B3=0079:0006.B11", "346E:0002.B4=0079:0006.B1 0079:0006.B11", "346E:0002.B5=0079:0006.B1 0079:0006.B2", "346E:0002.B7=0079:0006.B1", "[AxisButton]", "346E:0002.B6=DDFD:5008.Rz 20000", "346E:0002.B7=DDFD:5008.Rz 20000", "[Shape]", "346E:0002.X=gain=50" }, IniFile.Enc);
                IniFile pi = IniFile.Load(pt);
                int[] wheel = new int[] { 40000, 1, 1, 1, 1, 1, 1, 1 }, pedals = new int[] { 65535, 0, 0, 10000, 0, 30000, 0, 0 };
                byte[] shifter = new byte[128]; shifter[10] = 0x80;
                int[] ax = new int[8]; byte[] bt = new byte[128]; bool[] has = new bool[8];
                Preview.Compute(pi, "346E:0002",
                    delegate(string dv) { return dv == "346E:0002" ? wheel : dv == "DDFD:5008" ? pedals : null; },
                    delegate(string dv) { return dv == "0079:0006" ? shifter : new byte[128]; }, ax, bt, has);
                fail += Check(sb, "preview: shape, combine, half, invert, mask", Math.Abs(ax[0] - 36384) <= 1 && ax[1] == 0 && ax[2] == 32767 && ax[3] == 32767 && ax[4] == 55535);
                fail += Check(sb, "preview: buttons from ButtonMap and AxisButton", bt[2] == 0x80 && bt[5] == 0x80 && bt[0] == 0);
                fail += Check(sb, "preview: several sources on one button are OR-ed", bt[3] == 0x80 && bt[4] == 0 && bt[6] == 0x80);
                try { File.Delete(pt); } catch { }
            }
            fail += Check(sb, "button key -> 0-based index", MainForm.BtnIndexOfKey("0079:0006.B7") == 6 && MainForm.BtnIndexOfKey("0079:0006.B1") == 0 && MainForm.BtnIndexOfKey("0079:0006.B0") == -1 && MainForm.BtnIndexOfKey("0079:0006.B129") == -1 && MainForm.BtnIndexOfKey("0079:0006.X") == -1);
            fail += Check(sb, "loc en", Loc.T("tab.devices") == "Devices" && Loc.T("tip.btn", 1, "") == "Button 1");
            Loc.InitFor(appDir, "xx");     // unknown language -> English
            fail += Check(sb, "loc unknown lang falls back", Loc.T("tab.devices") == "Devices");
            bool hasFi = false, hasEn = false;
            foreach (string[] l in Loc.Languages()) { if (l[0] == "fi" && l[1] == "Suomi") hasFi = true; if (l[0] == "en" && l[1] == "English") hasEn = true; }
            fail += Check(sb, "loc languages listed", hasFi && hasEn);
            // every key of en.ini exists in fi.ini and vice versa
            Loc.InitFor(appDir, "fi");
            string[] enLines = File.ReadAllLines(Path.Combine(appDir, "lang", "en.ini"), new UTF8Encoding(false));
            int missing = 0;
            foreach (string raw in enLines)
            {
                string l = raw.Trim();
                if (l.Length == 0 || l[0] == ';' || l.IndexOf('=') <= 0) continue;
                string key = l.Substring(0, l.IndexOf('=')).Trim();
                if (Loc.T(key) == key) { missing++; sb.AppendLine("      missing in fi.ini: " + key); }
            }
            fail += Check(sb, "fi.ini has every en.ini key", missing == 0);
            Loc.Init(appDir);
            string ep = Native.ProcessPath(Process.GetCurrentProcess().Id);
            fail += Check(sb, "process path", ep != null && ep.EndsWith("CtrlMuxr.exe", StringComparison.OrdinalIgnoreCase));
            File.Delete(tmp);
            try   // devread.dll: list the attached controllers and read one sample from each
            {
                DRDevInfo[] arr = new DRDevInfo[16];
                int n = DevRead.DR_Enum(arr, 16);
                sb.AppendLine("devices: " + n);
                for (int i = 0; i < n; i++)
                {
                    int rc = DevRead.DR_Open(i, IntPtr.Zero);
                    int[] ax = new int[8]; int[] pov = new int[4]; byte[] b = new byte[128];
                    System.Threading.Thread.Sleep(100);
                    int rr = DevRead.DR_Read(i, ax, pov, b);
                    sb.AppendLine(string.Format("  {0} {1:X4}:{2:X4} open={3} read={4} axisMask={5:X2} buttons={6} axes={7}", arr[i].name, arr[i].vid, arr[i].pid, rc, rr,
                        DevRead.DR_AxisMask(i), DevRead.DR_NumButtons(i), string.Join(",", Array.ConvertAll(ax, delegate(int x) { return x.ToString(); }))));
                }
                DevRead.DR_CloseAll();
                fail += Check(sb, "devread enumerates", n >= 0);
            }
            catch (Exception ex) { sb.AppendLine("devread: " + ex.GetType().Name + " " + ex.Message); fail++; }
            sb.AppendLine(fail == 0 ? "ALL OK" : fail + " FAILED");
            File.WriteAllText(outFile, sb.ToString());
            return fail;
        }

        static int Check(StringBuilder sb, string name, bool ok) { sb.AppendLine((ok ? "ok    " : "FAIL  ") + name); return ok ? 0 : 1; }
    }
}
