// MarwanOS - LibraryApi console harness
//
// Exercises LibraryApi.Handle() from the command line so the installed-software scan can be
// tested without going anywhere near the live shell.
//
//   libraryapi-cli.exe lib.sources
//   libraryapi-cli.exe --scan                    scan (no icons) and print a readable table
//   libraryapi-cli.exe --scan --icons            same, but also extract icons
//   libraryapi-cli.exe lib.scan --wait           raw JSON of the finished job
//   libraryapi-cli.exe lib.list
//   libraryapi-cli.exe lib.icon id=steam:730 size=128
//   libraryapi-cli.exe --json "{\"cmd\":\"lib.scan\",\"icons\":false,\"sync\":true}"
//
// Flags:
//   --scan        run lib.scan synchronously and print the entry table (implies --noicons
//                 unless --icons is given, because base64 in a terminal is unreadable)
//   --icons       with --scan, extract icons too (only the byte count is printed)
//   --wait[=ms]   if the response carries a jobId, poll job.status until it finishes
//   --raw         do not pretty-print
//   --quiet       print only data
//   --trim        replace every data: URI in the output with its length, so a scan with icons
//                 can still be read in an SSH transcript
//   --list        command catalogue as a table
//   --file s.txt  one command per line in ONE process (job.status and lib.launch both need
//                 state cached by an earlier call in the same process)
//
// LAUNCHING IS DESTRUCTIVE ON A TEST MACHINE. lib.launch really starts the game. The harness
// does not run it unless you type it.
//
// Language level: C# 5 (inbox .NET Framework csc.exe). Built by build-lib-cli.cmd.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using MarwanOs.Library;

namespace MarwanOs.Library.Cli
{
    public static class LibCliProgram
    {
        // STA for the same reason LibraryApi's own helpers care: IShellLinkW, ShellExecuteEx and
        // IApplicationActivationManager all want an STA caller, and LSta.Run then runs them inline
        // on this thread instead of spinning one up.
        [STAThread]
        public static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; }
            catch { }

            bool wait = false, raw = false, quiet = false, list = false, trim = false;
            bool scan = false, icons = false;
            int waitMs = 180000;
            string explicitJson = null;
            string scriptFile = null;
            List<string> rest = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--raw") raw = true;
                else if (a == "--quiet") quiet = true;
                else if (a == "--list") list = true;
                else if (a == "--trim") trim = true;
                else if (a == "--scan") scan = true;
                else if (a == "--icons") icons = true;
                else if (a == "--noicons") icons = false;
                else if (a == "--wait") wait = true;
                else if (a.StartsWith("--wait=", StringComparison.Ordinal))
                {
                    wait = true;
                    int ms;
                    if (int.TryParse(a.Substring(7), NumberStyles.Integer, CultureInfo.InvariantCulture, out ms)) waitMs = ms;
                }
                else if (a == "--json") { if (i + 1 < args.Length) explicitJson = args[++i]; }
                else if (a == "--file") { if (i + 1 < args.Length) scriptFile = args[++i]; }
                else if (a == "-h" || a == "--help" || a == "/?") { Usage(); return 0; }
                else rest.Add(a);
            }

            if (list) return PrintCatalog();
            if (scan) return RunScanTable(icons);
            if (scriptFile != null) return RunScript(scriptFile, raw, quiet, trim, wait, waitMs);

            string request;
            if (explicitJson != null) request = explicitJson;
            else if (rest.Count == 0) { Usage(); return 2; }
            else request = BuildJson(rest);

            string response = LibraryApi.Handle(request);

            if (wait)
            {
                string jobId = FindJobId(response);
                if (jobId != null) response = PollJob(jobId, waitMs);
            }

            Print(response, raw, quiet, trim);
            return IsOk(response) ? 0 : 1;
        }

        static void Usage()
        {
            Console.WriteLine("MarwanOS LibraryApi harness");
            Console.WriteLine();
            Console.WriteLine("  libraryapi-cli --scan [--icons]      scan and print the entry table");
            Console.WriteLine("  libraryapi-cli <cmd> [key=value ...] [--wait[=ms]] [--raw] [--quiet] [--trim]");
            Console.WriteLine("  libraryapi-cli --json '{\"cmd\":\"...\"}'");
            Console.WriteLine("  libraryapi-cli --list                command catalogue");
            Console.WriteLine("  libraryapi-cli --file s.txt          one command per line, ONE process");
            Console.WriteLine();
            Console.WriteLine("Values are typed automatically: true/false -> bool, 12 / 0.5 -> number,");
            Console.WriteLine("[...] and {...} -> raw JSON, everything else -> string.");
            Console.WriteLine();
            Console.WriteLine("lib.launch really launches. It is never run for you.");
        }

        // ---------------------------------------------------------------------------------
        // The readable one. This is what you actually want over SSH.
        // ---------------------------------------------------------------------------------
        static int RunScanTable(bool icons)
        {
            string req = "{\"cmd\":\"lib.scan\",\"sync\":true,\"icons\":" + (icons ? "true" : "false") + "}";
            DateTime t0 = DateTime.UtcNow;
            string resp = LibraryApi.Handle(req);
            double ms = (DateTime.UtcNow - t0).TotalMilliseconds;

            LJ env;
            try { env = LJ.Parse(resp); }
            catch (Exception ex) { Console.Error.WriteLine("unparseable response: " + ex.Message); return 1; }

            if (!env.B("ok", false))
            {
                Console.WriteLine("SCAN FAILED: " + env.S("error", "?") + " - " + env.S("detail", ""));
                return 1;
            }
            LJ d = env.Get("data");

            Console.WriteLine("MarwanOS library scan");
            Console.WriteLine("  machine   " + d.S("machine", "?") + "   user " + d.S("user", "?") +
                              "   elevated " + (d.B("elevated", false) ? "yes" : "no"));
            Console.WriteLine("  scanned   " + d.S("scannedUtc", "?") + "   in " +
                              ms.ToString("F0", CultureInfo.InvariantCulture) + " ms");
            Console.WriteLine();

            Console.WriteLine("SOURCES");
            Console.WriteLine("  " + Pad("source", 12) + Pad("present", 9) + Pad("found", 7) + Pad("ms", 7) + "where / note");
            LJ srcs = d.Get("sources");
            if (srcs != null)
                for (int i = 0; i < srcs.Count; i++)
                {
                    LJ s = srcs.At(i);
                    string tail = s.S("where", "");
                    string note = s.S("note", null);
                    string err = s.S("error", null);
                    if (!string.IsNullOrEmpty(note)) tail = tail.Length > 0 ? (tail + "  |  " + note) : note;
                    if (!string.IsNullOrEmpty(err)) tail = tail + "  |  ERROR: " + err;
                    Console.WriteLine("  " + Pad(s.S("name", "?"), 12) +
                                      Pad(s.B("present", false) ? "yes" : "no", 9) +
                                      Pad(s.I("found", 0).ToString(CultureInfo.InvariantCulture), 7) +
                                      Pad(s.S("ms", "0"), 7) + Clip(tail, 150));
                }
            Console.WriteLine();

            LJ counts = d.Get("counts");
            int games = counts == null ? 0 : counts.I("games", 0);
            int apps = counts == null ? 0 : counts.I("apps", 0);
            int lnch = counts == null ? 0 : counts.I("launchers", 0);
            Console.WriteLine("ENTRIES  " + (counts == null ? 0 : counts.I("total", 0)) +
                              " total   " + games + " game" + (games == 1 ? "" : "s") +
                              "   " + lnch + " launcher" + (lnch == 1 ? "" : "s") +
                              "   " + apps + " app" + (apps == 1 ? "" : "s"));

            LJ arr = d.Get("entries");
            if (arr == null || arr.Count == 0)
            {
                Console.WriteLine();
                Console.WriteLine("  (nothing found - this is the honest empty state the Play tab has to render)");
            }
            else
            {
                Console.WriteLine("  " + Pad("kind", 6) + Pad("source", 10) + Pad("title", 34) +
                                  Pad("size", 10) + Pad("last played", 12) + Pad("ico", 6) + "launch");
                for (int i = 0; i < arr.Count; i++)
                {
                    LJ e = arr.At(i);
                    string sz = "-";
                    LJ szv = e.Get("sizeBytes");
                    if (szv != null && szv.Kind == LJ.TNum) sz = Human((long)szv.AsNum(0));
                    string lp = "-";
                    string lps = e.S("lastPlayedUtc", null);
                    if (!string.IsNullOrEmpty(lps) && lps.Length >= 10) lp = lps.Substring(0, 10);
                    string ico = "-";
                    string icv = e.S("icon", null);
                    if (!string.IsNullOrEmpty(icv)) ico = (icv.Length / 1024) + "k";
                    else if (e.B("hasIcon", false)) ico = "y";
                    Console.WriteLine("  " + Pad(e.S("kind", "?"), 6) + Pad(e.S("source", "?"), 10) +
                                      Pad(Clip(e.S("title", "?"), 32), 34) + Pad(sz, 10) + Pad(lp, 12) +
                                      Pad(ico, 6) +
                                      Clip(e.S("launchKind", "?") + "  " + e.S("launchTarget", ""), 90));
                }
                Console.WriteLine();
                Console.WriteLine("  install directories");
                for (int i = 0; i < arr.Count; i++)
                {
                    LJ e = arr.At(i);
                    Console.WriteLine("    " + Pad(Clip(e.S("title", "?"), 32), 34) + e.S("installDir", "-"));
                }
            }
            Console.WriteLine();
            Console.WriteLine("cache: " + CachePath());
            return 0;
        }

        static string CachePath()
        {
            try
            {
                LJ env = LJ.Parse(LibraryApi.Handle("api.version"));
                LJ d = env.Get("data");
                return d == null ? "?" : d.S("cache", "?");
            }
            catch { return "?"; }
        }

        static string Human(long b)
        {
            if (b < 0) return "-";
            string[] u = new string[] { "B", "KB", "MB", "GB", "TB" };
            double v = b;
            int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return v.ToString(v >= 100 || i == 0 ? "F0" : "F1", CultureInfo.InvariantCulture) + " " + u[i];
        }

        static string Pad(string s, int n)
        {
            if (s == null) s = "";
            if (s.Length >= n) return s.Substring(0, n - 1) + " ";
            return s + new string(' ', n - s.Length);
        }

        static string Clip(string s, int n)
        {
            if (s == null) return "";
            return s.Length <= n ? s : s.Substring(0, n - 1) + "…";
        }

        // ---------------------------------------------------------------------------------
        static int PrintCatalog()
        {
            string resp = LibraryApi.Handle("api.commands");
            LJ env;
            try { env = LJ.Parse(resp); }
            catch { Console.WriteLine(resp); return 1; }
            if (!env.B("ok", false)) { Console.WriteLine(resp); return 1; }
            LJ d = env.Get("data");
            Console.WriteLine("LibraryApi " + d.S("apiVersion", "?") + "   jobs: " + d.S("jobPrefix", "?"));
            Console.WriteLine("sources: " + d.S("sources", ""));
            Console.WriteLine();
            LJ cmds = d.Get("commands");
            for (int i = 0; i < cmds.Count; i++)
            {
                LJ c = cmds.At(i);
                Console.WriteLine("  " + Pad(c.S("cmd", "?"), 16) + c.S("what", ""));
            }
            Console.WriteLine();
            Console.WriteLine(d.S("note", ""));
            return 0;
        }

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
                sb.Append(',');
                Quote(sb, p.Substring(0, eq));
                sb.Append(':');
                AppendTyped(sb, p.Substring(eq + 1));
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
            if (v[0] == '[' || v[0] == '{')
            {
                bool valid;
                try { LJ.Parse(v); valid = true; }
                catch { valid = false; }
                if (valid) { sb.Append(v); return; }
            }
            // Entry ids are "steam:1623730" - never let that become a number, and never let a
            // bare appid argument lose its leading zeros.
            double d;
            if (v.IndexOf(':') < 0 &&
                double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) { sb.Append(v); return; }
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
            try { return LJ.Parse(response).B("ok", false); }
            catch { return false; }
        }

        static string FindJobId(string response)
        {
            try
            {
                LJ j = LJ.Parse(response);
                if (!j.B("ok", false)) return null;
                LJ d = j.Get("data");
                return d == null ? null : d.S("jobId", null);
            }
            catch { return null; }
        }

        static string PollJob(string jobId, int timeoutMs)
        {
            int waited = 0;
            string last = null;
            while (waited < timeoutMs)
            {
                last = LibraryApi.Handle("{\"cmd\":\"job.status\",\"jobId\":\"" + jobId + "\"}");
                try
                {
                    LJ j = LJ.Parse(last);
                    LJ d = j.Get("data");
                    string state = d == null ? null : d.S("state", null);
                    if (state != null && state != "running") return last;
                    Console.Error.WriteLine("  ... " + state + "  " + (d == null ? "" : d.S("phase", "")) +
                        "  (" + waited + " ms)");
                }
                catch { }
                Thread.Sleep(300);
                waited += 300;
            }
            return last == null ? "{\"ok\":false,\"error\":\"timeout\",\"detail\":\"job did not finish\"}" : last;
        }

        static void Print(string response, bool raw, bool quiet, bool trim)
        {
            string text = response;
            if (quiet)
            {
                try
                {
                    LJ j = LJ.Parse(response);
                    LJ d = j.Get("data");
                    if (d != null) text = d.ToJson();
                }
                catch { }
            }
            if (trim) text = TrimDataUris(text);
            Console.WriteLine(raw ? text : Pretty(text));
        }

        // A scan with icons is a megabyte of base64. Keep the shape, drop the payload.
        static string TrimDataUris(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            int i = 0;
            while (i < s.Length)
            {
                int at = s.IndexOf("\"data:", i, StringComparison.Ordinal);
                if (at < 0) { sb.Append(s, i, s.Length - i); break; }
                sb.Append(s, i, at - i);
                int end = s.IndexOf('"', at + 1);
                if (end < 0) { sb.Append(s, at, s.Length - at); break; }
                int len = end - at - 1;
                int comma = s.IndexOf(',', at);
                string mime = (comma > at && comma < end) ? s.Substring(at + 1, comma - at - 1) : "data:";
                sb.Append('"').Append(mime).Append(",<").Append(len).Append(" chars>\"");
                i = end + 1;
            }
            return sb.ToString();
        }

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
                    case ',': sb.Append(c).Append('\n').Append(' ', depth * 2); break;
                    case ':': sb.Append(": "); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        // One command per line, ONE process. job.status only finds jobs this process started, and
        // lib.launch / lib.icon resolve ids out of the scan this process performed (or the cache).
        // Blank lines and lines starting with # are ignored.
        static int RunScript(string path, bool raw, bool quiet, bool trim, bool wait, int waitMs)
        {
            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("cannot read " + path + ": " + ex.Message);
                return 2;
            }
            int bad = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                Console.WriteLine("=== " + line);
                string req = line[0] == '{' ? line : BuildJson(new List<string>(line.Split(' ')));
                string resp = LibraryApi.Handle(req);
                if (wait)
                {
                    string jid = FindJobId(resp);
                    if (jid != null) resp = PollJob(jid, waitMs);
                }
                Print(resp, raw, quiet, trim);
                if (!IsOk(resp)) bad++;
                Console.WriteLine();
            }
            return bad == 0 ? 0 : 1;
        }
    }
}
