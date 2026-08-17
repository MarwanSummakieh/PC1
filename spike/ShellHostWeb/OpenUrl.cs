// MarwanOS - OpenUrl
//
// The program Windows runs when anything on this machine opens a web link.
//
// WHY THIS EXISTS AS A SEPARATE 40 KB EXECUTABLE, AND NOT AS THE SHELL ITSELF
// ---------------------------------------------------------------------------------------
// Registering the shell binary as the http/https handler would be the obvious move and it
// is the wrong one. ShellExecute STARTS A PROCESS. If the registered command were
// MarwanShellHostWeb.exe, every clicked link on the console would launch a SECOND full
// shell - a second WebView2 environment against a user-data folder the running one holds
// open, a second DualSense reader fighting for the same HID handle, a second full-screen
// window on top of the shell Shell Launcher is watching. The exit code of that second
// process is meaningless to Shell Launcher, but everything else about it is very real.
//
// So the registered handler is this: a tiny program that finds the shell already running
// in the caller's session, hands it the URL, and exits. It never renders anything, never
// touches the pad, and holds no state.
//
// THE THREE OUTCOMES, IN ORDER
// ---------------------------------------------------------------------------------------
//   1. A MarwanOS shell is running in this session  -> WM_COPYDATA to its window. exit 0.
//   2. No shell here, but a fallback browser command is registered (HKLM\SOFTWARE\MarwanOS\
//      Browser\FallbackCommand) -> run that. exit 0.
//   3. Neither -> queue the URL in %LOCALAPPDATA%\<Brand>\openurl\pending.url, which the
//      shell drains the next time its page comes up. exit 3.
//
// Outcome 2 is what keeps the primary account usable. The default-browser association is
// machine-wide policy - Windows has no supported per-user way to set it, the per-user
// UserChoice key is hash-protected on purpose - so registering MarwanOS as the default
// browser changes what happens when 'brain' clicks a link on their ordinary desktop too.
// Without a fallback that click would land in a queue file nobody reads. With it, brain's
// session keeps whatever browser was default at registration time, and only the session
// that actually runs the console shell gets the console's browser.
//
// WHAT IT REFUSES
// ---------------------------------------------------------------------------------------
// http, https and file only, 2048 characters maximum, no control characters. This program
// is reachable by anything that can call ShellExecute, so it stays a narrow pipe: it does
// not become a general "run this for me" surface just because it is registered machine-wide.
//
// Build: build-openurl.cmd   (inbox csc.exe, C# 5, no SDK, no references beyond System)

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace MarwanOs.Open
{
    internal static class N
    {
        public const int WM_COPYDATA = 0x004A;
        public const uint SMTO_ABORTIFHUNG = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        public struct COPYDATASTRUCT
        {
            public IntPtr dwData;
            public int cbData;
            public IntPtr lpData;
        }

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder s, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, int msg, IntPtr wParam,
            ref COPYDATASTRUCT lParam, uint flags, uint timeoutMs, out IntPtr result);
    }

    public static class OpenUrl
    {
        // The same 'MOSU' tag the shell checks in its WndProc. A WM_COPYDATA with any other
        // dwData is not ours and the shell ignores it - this is a sanity tag, not a secret.
        const int Magic = 0x4D4F5355;

        // Matched against Process.ProcessName, which has no extension. The deployed binaries
        // are versioned (MarwanShellHostWeb-v15.exe), so the match is a prefix, and the
        // unversioned build output has to match too.
        const string ShellProcessPrefix = "MarwanShellHostWeb";

        // The on-disk brand, not the product name: the same folder that holds the browser
        // profile and the pinned sites. See OnDisk.Brand in ShellHostWeb.cs - the machine
        // was never renamed when the product was.
        const string Brand = "ArcOS";

        const int MaxUrl = 2048;

        static string _logPath;

        [STAThread]
        public static int Main(string[] args)
        {
            string url = null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a.StartsWith("--log=", StringComparison.OrdinalIgnoreCase)) { _logPath = a.Substring(6); continue; }
                if (a.StartsWith("--", StringComparison.Ordinal)) continue;
                if (url == null) url = a;
            }

            if (string.IsNullOrEmpty(url))
            {
                Log("no argument given; nothing to open");
                return 1;
            }

            string clean = Normalise(url);
            if (clean == null)
            {
                // Deliberately terse in the log and silent on screen. A rejected scheme is
                // either a mis-registration or something probing what this will run.
                Log("REFUSED: '" + Clip(url) + "'");
                return 2;
            }

            Log("asked to open: " + clean);

            IntPtr shell = FindShellWindow();
            if (shell != IntPtr.Zero)
            {
                if (Send(shell, clean))
                {
                    Log("delivered to the shell (hwnd=0x" + shell.ToInt64().ToString("X") + ")");
                    return 0;
                }
                // A shell that is up but not answering messages is worth a queue entry
                // rather than a silent failure: it will pick the URL up when it recovers.
                Log("the shell window did not answer; falling through to the queue");
            }
            else
            {
                Log("no MarwanOS shell window in this session");

                string fallback = Fallback();
                if (!string.IsNullOrEmpty(fallback))
                {
                    if (RunFallback(fallback, clean)) return 0;
                }
            }

            return Queue(clean) ? 3 : 1;
        }

        // ── Input ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the URL to hand on, or null if this program will not carry it.
        ///
        /// A file association passes a PATH, not a URL (Windows hands "%1" to the registered
        /// command for .html), so a path is turned into a file:// URI here rather than being
        /// rejected as a scheme-less string.
        /// </summary>
        static string Normalise(string raw)
        {
            string s = raw.Trim();
            if (s.Length == 0 || s.Length > MaxUrl) return null;

            for (int i = 0; i < s.Length; i++)
                if (char.IsControl(s[i])) return null;

            // A local path, quoted or not.
            if (s.Length > 1 && s[0] == '"' && s[s.Length - 1] == '"') s = s.Substring(1, s.Length - 2);
            bool looksLikePath = s.Length > 2 && ((s[1] == ':' && (s[2] == '\\' || s[2] == '/')) || s.StartsWith("\\\\", StringComparison.Ordinal));
            if (looksLikePath)
            {
                try { return new Uri(Path.GetFullPath(s)).AbsoluteUri; }
                catch { return null; }
            }

            Uri u;
            if (!Uri.TryCreate(s, UriKind.Absolute, out u)) return null;
            string scheme = u.Scheme.ToLowerInvariant();
            if (scheme != "http" && scheme != "https" && scheme != "file") return null;
            return u.AbsoluteUri;
        }

        // ── Outcome 1: the running shell ────────────────────────────────────────────────

        /// <summary>
        /// The top-level window of a MarwanOS shell in THIS session, or IntPtr.Zero.
        ///
        /// Matched on the owning process's image name rather than on the window title,
        /// because the title is a display string that may reasonably change and the image
        /// name is what the deployment actually is. The session check matters: on a machine
        /// where brain is signed in at the same time as the console account, a link clicked
        /// on brain's desktop must not be thrown onto the console's screen.
        /// </summary>
        static IntPtr FindShellWindow()
        {
            int session = Process.GetCurrentProcess().SessionId;
            IntPtr found = IntPtr.Zero;

            N.EnumWindows(delegate(IntPtr hWnd, IntPtr lp)
            {
                if (!N.IsWindowVisible(hWnd)) return true;

                uint pid;
                N.GetWindowThreadProcessId(hWnd, out pid);
                if (pid == 0) return true;

                try
                {
                    Process p = Process.GetProcessById((int)pid);
                    if (p.SessionId != session) return true;
                    if (!p.ProcessName.StartsWith(ShellProcessPrefix, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { return true; }   // exited between the enumeration and the query

                found = hWnd;
                return false;            // stop at the first one
            }, IntPtr.Zero);

            return found;
        }

        static bool Send(IntPtr hWnd, string url)
        {
            byte[] bytes = Encoding.Unicode.GetBytes(url + "\0");
            IntPtr buf = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, buf, bytes.Length);
                N.COPYDATASTRUCT cds = new N.COPYDATASTRUCT();
                cds.dwData = new IntPtr(Magic);
                cds.cbData = bytes.Length;
                cds.lpData = buf;

                IntPtr result;
                // SendMessageTimeout, never SendMessage: this process must not hang because
                // the shell is busy building a WebView2 environment or waiting on a game.
                IntPtr ok = N.SendMessageTimeoutW(hWnd, N.WM_COPYDATA, IntPtr.Zero, ref cds,
                                                  N.SMTO_ABORTIFHUNG, 5000, out result);
                return ok != IntPtr.Zero && result != IntPtr.Zero;
            }
            catch (Exception ex) { Log("send failed: " + ex.Message); return false; }
            finally { Marshal.FreeHGlobal(buf); }
        }

        // ── Outcome 2: the session that is not the console ──────────────────────────────

        /// <summary>
        /// The command line 21-set-default-browser.ps1 recorded as the machine's previous
        /// default browser, with %1 where the URL goes. Empty if none was recorded, or if
        /// the program it names has since been removed.
        /// </summary>
        static string Fallback()
        {
            try
            {
                using (RegistryKey k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                                                  .OpenSubKey(@"SOFTWARE\MarwanOS\Browser"))
                {
                    if (k == null) return null;
                    return k.GetValue("FallbackCommand") as string;
                }
            }
            catch { return null; }
        }

        static bool RunFallback(string command, string url)
        {
            try
            {
                string exe, rest;
                SplitCommand(command, out exe, out rest);
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                {
                    Log("fallback command names '" + Clip(exe) + "', which is not on disk");
                    return false;
                }

                string a = rest.IndexOf("%1", StringComparison.Ordinal) >= 0
                    ? rest.Replace("%1", url)
                    : (rest.Length > 0 ? rest + " \"" + url + "\"" : "\"" + url + "\"");

                ProcessStartInfo psi = new ProcessStartInfo(exe, a);
                psi.UseShellExecute = false;
                Process.Start(psi);
                Log("handed to the fallback browser: " + Clip(exe));
                return true;
            }
            catch (Exception ex) { Log("fallback failed: " + ex.Message); return false; }
        }

        static void SplitCommand(string command, out string exe, out string rest)
        {
            command = command.Trim();
            if (command.Length > 0 && command[0] == '"')
            {
                int end = command.IndexOf('"', 1);
                if (end > 0)
                {
                    exe = command.Substring(1, end - 1);
                    rest = command.Substring(end + 1).Trim();
                    return;
                }
            }
            int sp = command.IndexOf(' ');
            if (sp < 0) { exe = command; rest = ""; return; }
            exe = command.Substring(0, sp);
            rest = command.Substring(sp + 1).Trim();
        }

        // ── Outcome 3: the queue ────────────────────────────────────────────────────────

        /// <summary>
        /// One file, overwritten, not a growing spool. A link clicked while the shell was
        /// down is worth opening once when it comes back; five of them are worth one tab,
        /// not five, and a spool nothing ever empties is a slow leak.
        /// </summary>
        static bool Queue(string url)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Brand + "\\openurl");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "pending.url"), url, Encoding.UTF8);
                Log("queued for the next shell start");
                return true;
            }
            catch (Exception ex) { Log("queue failed: " + ex.Message); return false; }
        }

        // ── Log ─────────────────────────────────────────────────────────────────────────

        static string Clip(string s)
        {
            if (s == null) return "(null)";
            return s.Length <= 160 ? s : s.Substring(0, 160) + "...";
        }

        static void Log(string line)
        {
            string path = _logPath;
            if (string.IsNullOrEmpty(path))
            {
                try
                {
                    path = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        Brand + "\\openurl\\openurl.log");
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                }
                catch { return; }
            }
            try
            {
                File.AppendAllText(path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                    + "  " + line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
    }
}
