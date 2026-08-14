// ARC OS - SystemApi console harness
//
// Exercises SystemApi.Handle() from the command line so the whole system-control surface can be
// tested without going anywhere near the live shell.
//
//   systemapi-cli.exe sys.info
//   systemapi-cli.exe audio.setVolume level=0.35
//   systemapi-cli.exe wifi.connect ssid=MyNet password=secret --wait
//   systemapi-cli.exe --json "{\"cmd\":\"display.list\",\"modes\":true}"
//   systemapi-cli.exe --reads              run every fast read-only command
//   systemapi-cli.exe --list               print the command catalogue as a table
//
// Flags:
//   --wait[=ms]   if the response carries a jobId, poll job.status until it finishes
//   --raw         do not pretty-print
//   --quiet       print only the value of data (still valid JSON)
//   --reads       run every read-only, non-async command in the catalogue
//   --list        catalogue as a readable table
//
// Language level: C# 5 (inbox .NET Framework csc.exe). Built by build-cli.cmd.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ArcOs.Sys;

namespace ArcOs.Sys.Cli
{
    public static class CliProgram
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [STAThread]
        public static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; }
            catch { }

            // Only the harness does this - the shell host owns its own DPI awareness. Without it
            // display.list would report the system DPI instead of each monitor's real DPI.
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch { }

            bool wait = false, raw = false, quiet = false, reads = false, list = false;
            int waitMs = 120000;
            string explicitJson = null;
            string scriptFile = null;
            List<string> rest = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--raw") raw = true;
                else if (a == "--quiet") quiet = true;
                else if (a == "--reads") reads = true;
                else if (a == "--list") list = true;
                else if (a == "--wait") wait = true;
                else if (a.StartsWith("--wait=", StringComparison.Ordinal))
                {
                    wait = true;
                    int ms;
                    if (int.TryParse(a.Substring(7), NumberStyles.Integer, CultureInfo.InvariantCulture, out ms)) waitMs = ms;
                }
                else if (a == "--json")
                {
                    if (i + 1 < args.Length) explicitJson = args[++i];
                }
                else if (a == "--file")
                {
                    if (i + 1 < args.Length) scriptFile = args[++i];
                }
                else if (a == "-h" || a == "--help" || a == "/?")
                {
                    Usage();
                    return 0;
                }
                else rest.Add(a);
            }

            if (list) return PrintCatalog();
            if (reads) return RunAllReads(raw);
            if (scriptFile != null) return RunScript(scriptFile, raw, wait, waitMs);

            string request;
            if (explicitJson != null) request = explicitJson;
            else if (rest.Count == 0) { Usage(); return 2; }
            else request = BuildJson(rest);

            string response = SystemApi.Handle(request);

            if (wait)
            {
                string jobId = FindJobId(response);
                if (jobId != null) response = PollJob(jobId, waitMs);
            }

            Print(response, raw, quiet);
            return IsOk(response) ? 0 : 1;
        }

        static void Usage()
        {
            Console.WriteLine("ARC OS SystemApi harness");
            Console.WriteLine();
            Console.WriteLine("  systemapi-cli <cmd> [key=value ...] [--wait[=ms]] [--raw] [--quiet]");
            Console.WriteLine("  systemapi-cli --json '{\"cmd\":\"...\"}'");
            Console.WriteLine("  systemapi-cli --list          command catalogue");
            Console.WriteLine("  systemapi-cli --reads         run every fast read-only command");
            Console.WriteLine("  systemapi-cli --file s.txt    run one command per line in ONE process");
            Console.WriteLine("                                (needed for updates.list / job.status,");
            Console.WriteLine("                                 which read state cached by an earlier call)");
            Console.WriteLine();
            Console.WriteLine("Values are typed automatically: true/false -> bool, 12 / 0.5 -> number,");
            Console.WriteLine("[...] and {...} -> raw JSON, everything else -> string.");
        }

        // key=value pairs become a JSON command object.
        static string BuildJson(List<string> parts)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"cmd\":");
            Quote(sb, parts[0]);
            for (int i = 1; i < parts.Count; i++)
            {
                string p = parts[i];
                int eq = p.IndexOf('=');
                if (eq <= 0) continue;
                string k = p.Substring(0, eq);
                string v = p.Substring(eq + 1);
                sb.Append(',');
                Quote(sb, k);
                sb.Append(':');
                AppendTyped(sb, v);
            }
            sb.Append('}');
            return sb.ToString();
        }

        static void AppendTyped(StringBuilder sb, string v)
        {
            if (v.Length == 0) { sb.Append("\"\""); return; }
            if (string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)) { sb.Append("true"); return; }
            if (string.Equals(v, "false", StringComparison.OrdinalIgnoreCase)) { sb.Append("false"); return; }
            if (string.Equals(v, "null", StringComparison.OrdinalIgnoreCase)) { sb.Append("null"); return; }
            // Only pass a [..] / {..} value through as raw JSON if it actually parses. Audio
            // endpoint ids look like "{0.0.0.00000000}.{cc99...}", which starts with '{' but is
            // very much a string.
            if (v[0] == '[' || v[0] == '{')
            {
                bool valid;
                try { J.Parse(v); valid = true; }
                catch { valid = false; }
                if (valid) { sb.Append(v); return; }
            }
            double d;
            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
            {
                sb.Append(v);
                return;
            }
            Quote(sb, v);
        }

        static void Quote(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            sb.Append('"');
        }

        static bool IsOk(string response)
        {
            try
            {
                J j = J.Parse(response);
                return j.B("ok", false);
            }
            catch { return false; }
        }

        static string FindJobId(string response)
        {
            try
            {
                J j = J.Parse(response);
                if (!j.B("ok", false)) return null;
                J d = j.Get("data");
                if (d == null) return null;
                return d.S("jobId", null);
            }
            catch { return null; }
        }

        static string PollJob(string jobId, int timeoutMs)
        {
            int waited = 0;
            string last = null;
            while (waited < timeoutMs)
            {
                last = SystemApi.Handle("{\"cmd\":\"job.status\",\"jobId\":\"" + jobId + "\"}");
                try
                {
                    J j = J.Parse(last);
                    J d = j.Get("data");
                    string state = d == null ? null : d.S("state", null);
                    if (state != null && state != "running") return last;
                    Console.Error.WriteLine("  ... " + state + "  " + (d == null ? "" : d.S("phase", "")) +
                        "  (" + waited + " ms)");
                }
                catch { }
                Thread.Sleep(500);
                waited += 500;
            }
            return last == null ? "{\"ok\":false,\"error\":\"timeout\",\"detail\":\"job did not finish\"}" : last;
        }

        static void Print(string response, bool raw, bool quiet)
        {
            string text = response;
            if (quiet)
            {
                try
                {
                    J j = J.Parse(response);
                    J d = j.Get("data");
                    if (d != null) text = d.ToJson();
                }
                catch { }
            }
            Console.WriteLine(raw ? text : Pretty(text));
        }

        // Small standalone re-indenter so output is readable in a terminal / SSH transcript.
        public static string Pretty(string json)
        {
            StringBuilder sb = new StringBuilder(json.Length * 2);
            int depth = 0;
            bool inStr = false, esc = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (inStr)
                {
                    sb.Append(c);
                    if (esc) esc = false;
                    else if (c == '\\') esc = true;
                    else if (c == '"') inStr = false;
                    continue;
                }
                switch (c)
                {
                    case '"': inStr = true; sb.Append(c); break;
                    case '{':
                    case '[':
                        // Keep empty containers on one line.
                        if (i + 1 < json.Length && (json[i + 1] == '}' || json[i + 1] == ']'))
                        {
                            sb.Append(c).Append(json[i + 1]);
                            i++;
                            break;
                        }
                        depth++;
                        sb.Append(c).Append('\n').Append(' ', depth * 2);
                        break;
                    case '}':
                    case ']':
                        depth--;
                        sb.Append('\n').Append(' ', depth * 2).Append(c);
                        break;
                    case ',':
                        sb.Append(c).Append('\n').Append(' ', depth * 2);
                        break;
                    case ':':
                        sb.Append(": ");
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        // One command per line, run in a SINGLE process. This matters for anything that keeps
        // state between calls - updates.list only sees what updates.check cached, and job.status
        // only finds jobs started by the same process.
        // Blank lines and lines starting with # are ignored.
        static int RunScript(string path, bool raw, bool wait, int waitMs)
        {
            string[] lines;
            try { lines = System.IO.File.ReadAllLines(path); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("cannot read " + path + ": " + ex.Message);
                return 2;
            }
            int failures = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                bool lineWait = wait;
                if (line.EndsWith(" --wait", StringComparison.Ordinal))
                {
                    lineWait = true;
                    line = line.Substring(0, line.Length - 7).Trim();
                }

                Console.WriteLine();
                Console.WriteLine("=============================================================");
                Console.WriteLine("== " + line);
                Console.WriteLine("=============================================================");

                string request = line[0] == '{' ? line : BuildJson(Split(line));
                DateTime t0 = DateTime.UtcNow;
                string resp = SystemApi.Handle(request);
                if (lineWait)
                {
                    string jobId = FindJobId(resp);
                    if (jobId != null) resp = PollJob(jobId, waitMs);
                }
                int ms = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
                if (!IsOk(resp)) failures++;
                Console.WriteLine(raw ? resp : Pretty(resp));
                Console.WriteLine("-- " + ms + " ms");
            }
            Console.WriteLine();
            Console.WriteLine("script complete, failures=" + failures);
            return failures == 0 ? 0 : 1;
        }

        // Split on spaces but keep "quoted values" together, so
        //   sys.setTimezone "id=Romance Standard Time"
        // survives coming from a file rather than argv.
        static List<string> Split(string line)
        {
            List<string> parts = new List<string>();
            StringBuilder cur = new StringBuilder();
            bool q = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"') { q = !q; continue; }
                if (c == ' ' && !q)
                {
                    if (cur.Length > 0) { parts.Add(cur.ToString()); cur.Length = 0; }
                    continue;
                }
                cur.Append(c);
            }
            if (cur.Length > 0) parts.Add(cur.ToString());
            return parts;
        }

        static int PrintCatalog()
        {
            string r = SystemApi.Handle("api.commands");
            J j = J.Parse(r);
            J cmds = j.Get("data").Get("commands");
            Console.WriteLine("{0,-22} {1,-10} {2,-7} {3,-9} {4,-6} {5}",
                "COMMAND", "GROUP", "KIND", "ELEVATION", "ASYNC", "ARGS");
            Console.WriteLine(new string('-', 110));
            for (int i = 0; i < cmds.Count; i++)
            {
                J c = cmds.At(i);
                Console.WriteLine("{0,-22} {1,-10} {2,-7} {3,-9} {4,-6} {5}",
                    c.S("cmd", ""), c.S("group", ""), c.S("kind", ""), c.S("elevation", ""),
                    c.B("async", false) ? "yes" : "", c.S("args", ""));
            }
            Console.WriteLine();
            Console.WriteLine("elevated = " + j.Get("data").B("elevated", false));
            return 0;
        }

        // Runs every fast read-only command and prints each response. This is the "does the whole
        // surface work in this security context" sweep.
        static int RunAllReads(bool raw)
        {
            string r = SystemApi.Handle("api.commands");
            J cmds = J.Parse(r).Get("data").Get("commands");
            int failures = 0;
            for (int i = 0; i < cmds.Count; i++)
            {
                J c = cmds.At(i);
                if (c.S("kind", "") != "read") continue;
                if (c.B("async", false)) continue;
                string name = c.S("cmd", "");
                if (name == "api.commands" || name == "job.status" || name == "job.cancel") continue;
                if (name == "updates.list") continue;     // needs a prior search
                if (name == "display.modes") continue;    // covered by display.list
                if (name == "sys.timezones") continue;    // long and static

                Console.WriteLine();
                Console.WriteLine("=============================================================");
                Console.WriteLine("== " + name);
                Console.WriteLine("=============================================================");
                DateTime t0 = DateTime.UtcNow;
                string resp = SystemApi.Handle(name);
                int ms = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
                if (!IsOk(resp)) failures++;
                Console.WriteLine(raw ? resp : Pretty(resp));
                Console.WriteLine("-- " + ms + " ms");
            }
            Console.WriteLine();
            Console.WriteLine("read sweep complete, failures=" + failures);
            return failures == 0 ? 0 : 1;
        }
    }
}
