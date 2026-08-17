/*  ptrtarget.cs — a foreign window for the host's pointer mode to be tested against.
 *
 *  Pointer mode (ShellHostWeb.cs, the "pointer mode" region) drives the REAL Windows cursor
 *  with SendInput so the pad can operate a window this shell did not draw. That claim can
 *  only be checked from the other side of the boundary: a separate process, with its own
 *  window and its own message loop, writing down what actually arrived. Reading the host's
 *  own log proves what it SENT, which is a different sentence.
 *
 *  So this is deliberately a different program. It is not part of the shell, nothing links
 *  to it, and it never runs on a console anybody is using — it is the bench's target.
 *
 *      ptrtarget.exe --log=C:\path\target.log [--rect=x,y,w,h] [--grab=ms] [--exit=ms]
 *
 *  --grab keeps the window in the foreground for that long after it starts. The bench runs a
 *  live full-screen shell that would otherwise sit on top of it, and the host itself takes
 *  the foreground when it starts; without this, a test would be clicking on whatever happened
 *  to win that race. It stops after --grab so that Triangle (which brings the SHELL forward
 *  for the keyboard) is not fought over.
 *
 *  --exit is not optional in spirit: nothing this file starts should outlive the test.
 *
 *  Build:  build-ptrtarget.cmd     (inbox csc.exe, same as everything else here)
 */

using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace MarwanOs.PtrTarget
{
    public class TargetForm : Form
    {
        const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
        const int WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_MOUSEWHEEL = 0x020A;
        const int WM_MOUSEHWHEEL = 0x020E, WM_CONTEXTMENU = 0x007B;
        const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, WM_CHAR = 0x0102;

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")]
        static extern bool GetCursorPos(out Point p);
        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")]
        static extern IntPtr SetActiveWindow(IntPtr h);

        /// <summary>
        /// Ask for the foreground, politely, and keep asking.
        ///
        /// Deliberately NOT the AttachThreadInput / ALT-tap escalation that
        /// Foreground.ForceForeground uses. That was tried here first and it wedged the
        /// thing under test: attaching this window's input queue to the host's UI thread
        /// while the host was still bringing WebView2 up left the host with a black window
        /// and a log that stopped mid-load. A test rig must not be able to break the program
        /// it is measuring, so this only ever asks — the harness arranges for the ask to
        /// succeed by minimising the host's own window, which releases the foreground
        /// without either process fighting for it.
        /// </summary>
        bool ForceForeground()
        {
            if (GetForegroundWindow() == Handle) return true;
            BringWindowToTop(Handle);
            SetForegroundWindow(Handle);
            SetActiveWindow(Handle);
            return GetForegroundWindow() == Handle;
        }

        readonly string _log;
        readonly int _grabMs, _exitMs;
        readonly DateTime _t0 = DateTime.Now;
        readonly Label _big = new Label();
        readonly Label _tail = new Label();
        readonly Timer _t = new Timer();
        int _moves;
        int _wheel;
        bool _hasFg, _grabOver;
        readonly StringBuilder _typed = new StringBuilder();

        public TargetForm(string log, Rectangle rect, int grabMs, int exitMs)
        {
            _log = log; _grabMs = grabMs; _exitMs = exitMs;

            Text = "MarwanOS pointer target";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            StartPosition = FormStartPosition.Manual;
            Bounds = rect;
            BackColor = Color.FromArgb(14, 16, 24);
            ForeColor = Color.White;
            TopMost = true;
            KeyPreview = true;

            _big.Dock = DockStyle.Top;
            _big.Height = rect.Height - 90;
            _big.Font = new Font("Segoe UI", 22f, FontStyle.Bold);
            _big.TextAlign = ContentAlignment.MiddleCenter;
            _big.Text = "waiting for the pointer";
            _tail.Dock = DockStyle.Bottom;
            _tail.Height = 80;
            _tail.Font = new Font("Consolas", 11f);
            _tail.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(_big);
            Controls.Add(_tail);

            // The button messages have to be taken from the CHILD controls too. A Label fills
            // the client area and swallows WM_LBUTTONDOWN whole - it never reaches the form's
            // WndProc - so the first run of this rig recorded the right click (WM_CONTEXTMENU
            // does bubble) and silently missed the left one. Watching the events instead of
            // the messages is what a real application does anyway.
            MouseDown += OnDown; MouseUp += OnUp;
            _big.MouseDown += OnDown; _big.MouseUp += OnUp;
            _tail.MouseDown += OnDown; _tail.MouseUp += OnUp;

            Write("target up, rect=" + rect.X + "," + rect.Y + " " + rect.Width + "x" + rect.Height
                + " pid=" + System.Diagnostics.Process.GetCurrentProcess().Id
                + " session=" + System.Diagnostics.Process.GetCurrentProcess().SessionId);

            _t.Interval = 500;
            _t.Tick += OnTick;
            _t.Start();
        }

        void OnTick(object sender, EventArgs e)
        {
            double up = (DateTime.Now - _t0).TotalMilliseconds;
            if (up < _grabMs)
            {
                TopMost = true;
                bool had = _hasFg;
                _hasFg = ForceForeground();
                if (_hasFg != had) Write(_hasFg ? "took the foreground" : "LOST the foreground");
            }
            else if (!_grabOver)
            {
                _grabOver = true;
                Write("grab window over (" + _grabMs + " ms); staying topmost but no longer taking the foreground");
            }
            if (_exitMs > 0 && up >= _exitMs)
            {
                Write("exit timer (" + _exitMs + " ms). moves=" + _moves + " wheel=" + _wheel
                    + " typed='" + _typed + "'");
                _t.Stop();
                Close();
            }
        }

        void OnDown(object sender, MouseEventArgs e) { Say("mouse " + e.Button.ToString().ToUpperInvariant() + " DOWN"); }
        void OnUp(object sender, MouseEventArgs e) { Say("mouse " + e.Button.ToString().ToUpperInvariant() + " UP"); }

        void Say(string what)
        {
            _big.Text = what;
            _tail.Text = "moves=" + _moves + "  wheel=" + _wheel + "  typed='" + _typed + "'";
            Write(what);
        }

        void Write(string line)
        {
            Point c;
            if (!GetCursorPos(out c)) { c.X = -1; c.Y = -1; }
            string s = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                     + "  cursor=(" + c.X + "," + c.Y + ")  " + line;
            try { File.AppendAllText(_log, s + Environment.NewLine); }
            catch { }
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_MOUSEMOVE:
                    _moves++;
                    // Every move would be a thousand lines a minute; the count is the evidence
                    // and a sample every 25th keeps the timing visible.
                    if (_moves % 25 == 1) Write("WM_MOUSEMOVE #" + _moves);
                    break;
                case WM_LBUTTONDOWN: Say("WM_LBUTTONDOWN"); break;
                case WM_LBUTTONUP:   Say("WM_LBUTTONUP"); break;
                case WM_RBUTTONDOWN: Say("WM_RBUTTONDOWN"); break;
                case WM_RBUTTONUP:   Say("WM_RBUTTONUP"); break;
                case WM_CONTEXTMENU: Say("WM_CONTEXTMENU"); break;
                case WM_MOUSEWHEEL:
                    _wheel += (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                    Say("WM_MOUSEWHEEL delta=" + (short)((m.WParam.ToInt64() >> 16) & 0xFFFF));
                    break;
                case WM_MOUSEHWHEEL:
                    Say("WM_MOUSEHWHEEL delta=" + (short)((m.WParam.ToInt64() >> 16) & 0xFFFF));
                    break;
                case WM_KEYDOWN:
                case WM_SYSKEYDOWN:
                    Say("WM_KEYDOWN vk=0x" + m.WParam.ToInt64().ToString("X2") + " (" + VkName((int)m.WParam) + ")");
                    break;
                case WM_CHAR:
                {
                    char ch = (char)m.WParam.ToInt64();
                    if (ch >= ' ') _typed.Append(ch);
                    Say("WM_CHAR '" + (ch >= ' ' ? ch.ToString() : "#" + (int)ch) + "'");
                    break;
                }
            }
            base.WndProc(ref m);
        }

        static string VkName(int vk)
        {
            switch (vk)
            {
                case 0x0D: return "Enter";
                case 0x1B: return "Escape";
                case 0x21: return "PageUp";
                case 0x22: return "PageDown";
                default: return ((Keys)vk).ToString();
            }
        }

        [STAThread]
        public static void Main(string[] args)
        {
            string log = "ptrtarget.log";
            Rectangle rect = new Rectangle(300, 300, 700, 420);
            int grab = 15000, exit = 120000;

            foreach (string a in args)
            {
                if (!a.StartsWith("--")) continue;
                int eq = a.IndexOf('=');
                string key = eq > 0 ? a.Substring(0, eq) : a;
                string val = eq > 0 ? a.Substring(eq + 1) : "";
                if (key == "--log") log = val;
                else if (key == "--grab") int.TryParse(val, out grab);
                else if (key == "--exit") int.TryParse(val, out exit);
                else if (key == "--rect")
                {
                    string[] p = val.Split(',');
                    if (p.Length == 4)
                        rect = new Rectangle(int.Parse(p[0], CultureInfo.InvariantCulture),
                                             int.Parse(p[1], CultureInfo.InvariantCulture),
                                             int.Parse(p[2], CultureInfo.InvariantCulture),
                                             int.Parse(p[3], CultureInfo.InvariantCulture));
                }
            }

            Application.EnableVisualStyles();
            Application.Run(new TargetForm(log, rect, grab, exit));
        }
    }
}
