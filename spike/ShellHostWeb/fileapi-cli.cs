// MarwanOS - FileApi console harness
//
// Exercises FileApi.Handle() from the command line so the whole file-operations surface can be
// tested without going anywhere near the live shell. Same shape as systemapi-cli.cs.
//
//   fileapi-cli.exe fs.drives
//   fileapi-cli.exe fs.list path=C:\Users sort=size desc=true
//   fileapi-cli.exe fs.copy path=C:\big.bin to=D:\ --wait
//   fileapi-cli.exe --json "{\"cmd\":\"fs.delete\",\"paths\":[\"C:\\\\a\"],\"recycle\":false}" --wait
//   fileapi-cli.exe --file script.txt        one command per line, ONE process
//   fileapi-cli.exe --list                   the command catalogue as a table
//   fileapi-cli.exe --selftest C:\scratch    the full scratch-directory sweep (see below)
//
// Flags:
//   --wait[=ms]     if the response carries a jobId, poll job.status until it finishes
//   --cancel=ms     start the command, wait ms, then job.cancel it and keep polling.
//                   This is how the copy-cancellation path is proven: the numbers in the
//                   transcript are real bytes that really stopped moving.
//   --raw           do not pretty-print
//   --quiet         print only data
//   --selftest DIR  create DIR, run every command against it, clean up, print a PASS/FAIL table.
//                   Nothing outside DIR is ever written to or deleted.
//
// Language level: C# 5 (inbox .NET Framework csc.exe). Built by build-file-cli.cmd.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using MarwanOs.Files;

namespace MarwanOs.Files.Cli
{
    public static class FileCliProgram
    {
        static int _pass, _fail;

        [STAThread]
        public static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; }
            catch { }

            bool wait = false, raw = false, quiet = false, list = false;
            int waitMs = 300000;
            int cancelAfter = -1;
            string explicitJson = null, scriptFile = null, selftestDir = null;
            List<string> rest = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--raw") raw = true;
                else if (a == "--quiet") quiet = true;
                else if (a == "--list") list = true;
                else if (a == "--wait") wait = true;
                else if (a.StartsWith("--wait=", StringComparison.Ordinal))
                {
                    wait = true;
                    int ms;
                    if (int.TryParse(a.Substring(7), NumberStyles.Integer, CultureInfo.InvariantCulture, out ms)) waitMs = ms;
                }
                else if (a.StartsWith("--cancel=", StringComparison.Ordinal))
                {
                    wait = true;
                    int ms;
                    if (int.TryParse(a.Substring(9), NumberStyles.Integer, CultureInfo.InvariantCulture, out ms)) cancelAfter = ms;
                }
                else if (a == "--json") { if (i + 1 < args.Length) explicitJson = args[++i]; }
                else if (a == "--file") { if (i + 1 < args.Length) scriptFile = args[++i]; }
                else if (a == "--selftest") { if (i + 1 < args.Length) selftestDir = args[++i]; }
                else if (a == "-h" || a == "--help" || a == "/?") { Usage(); return 0; }
                else rest.Add(a);
            }

            if (list) return PrintCatalog();
            if (selftestDir != null) return SelfTest(selftestDir);
            if (scriptFile != null) return RunScript(scriptFile, raw, wait, waitMs);

            string request;
            if (explicitJson != null) request = explicitJson;
            else if (rest.Count == 0) { Usage(); return 2; }
            else request = BuildJson(rest);

            string response = FileApi.Handle(request);

            if (wait)
            {
                string jobId = FindJobId(response);
                if (jobId != null) response = PollJob(jobId, waitMs, cancelAfter);
            }

            Print(response, raw, quiet);
            return IsOk(response) ? 0 : 1;
        }

        static void Usage()
        {
            Console.WriteLine("MarwanOS FileApi harness");
            Console.WriteLine();
            Console.WriteLine("  fileapi-cli <cmd> [key=value ...] [--wait[=ms]] [--cancel=ms] [--raw] [--quiet]");
            Console.WriteLine("  fileapi-cli --json '{\"cmd\":\"...\"}'");
            Console.WriteLine("  fileapi-cli --list             command catalogue");
            Console.WriteLine("  fileapi-cli --file s.txt       one command per line in ONE process");
            Console.WriteLine("                                 (job.status only finds jobs from the same process)");
            Console.WriteLine("  fileapi-cli --selftest DIR     full scratch sweep inside DIR, then clean up");
            Console.WriteLine();
            Console.WriteLine("Values are typed automatically: true/false -> bool, 12 / 0.5 -> number,");
            Console.WriteLine("[...] and {...} -> raw JSON, everything else -> string.");
        }

        // ------------------------------------------------------------------------------------
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
            if (v[0] == '[' || v[0] == '{')
            {
                bool valid;
                try { FJ.Parse(v); valid = true; }
                catch { valid = false; }
                if (valid) { sb.Append(v); return; }
            }
            // A bare path is a string even though "12" is a number - only parse a number when the
            // whole token is one.
            double d;
            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d) &&
                v.IndexOf('\\') < 0 && v.IndexOf(':') < 0)
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
            try { return FJ.Parse(response).B("ok", false); }
            catch { return false; }
        }

        static string FindJobId(string response)
        {
            try
            {
                FJ j = FJ.Parse(response);
                if (!j.B("ok", false)) return null;
                FJ d = j.Get("data");
                if (d == null) return null;
                string direct = d.S("jobId", null);
                if (direct != null) return direct;
                FJ sub = d.Get("sizeJob");
                return sub == null ? null : sub.S("jobId", null);
            }
            catch { return null; }
        }

        // Poll, printing a live progress line. cancelAfterMs >= 0 fires job.cancel once the
        // elapsed time passes it - the proof that cancelling a real transfer works.
        static string PollJob(string jobId, int timeoutMs, int cancelAfterMs)
        {
            int waited = 0;
            bool cancelled = false;
            string last = null;
            while (waited < timeoutMs)
            {
                last = FileApi.Handle("{\"cmd\":\"job.status\",\"jobId\":\"" + jobId + "\"}");
                try
                {
                    FJ d = FJ.Parse(last).Get("data");
                    string state = d == null ? null : d.S("state", null);
                    if (state != null && state != "running") return last;

                    long done = (long)d.N("bytesDone", 0), total = (long)d.N("bytesTotal", 0);
                    long rate = (long)d.N("bytesPerSec", 0);
                    Console.Error.WriteLine("  ... " + state + " " + d.I("percent", -1) + "%  " +
                        Bytes(done) + " / " + Bytes(total) +
                        (rate > 0 ? ("  " + Bytes(rate) + "/s") : "") +
                        "  eta " + (d.Get("etaSec") == null || d.Get("etaSec").Kind == FJ.TNull ? "?" : d.N("etaSec", 0).ToString("0.0", CultureInfo.InvariantCulture) + "s") +
                        "  " + Trim(d.S("current", ""), 52) + "   (" + waited + " ms)");

                    if (!cancelled && cancelAfterMs >= 0 && waited >= cancelAfterMs)
                    {
                        cancelled = true;
                        Console.Error.WriteLine("  >>> CANCELLING at " + waited + " ms, " + Bytes(done) + " transferred");
                        Console.Error.WriteLine("  >>> " + FileApi.Handle("{\"cmd\":\"job.cancel\",\"jobId\":\"" + jobId + "\"}"));
                    }
                }
                catch { }
                Thread.Sleep(cancelAfterMs >= 0 ? 150 : 400);
                waited += cancelAfterMs >= 0 ? 150 : 400;
            }
            return last == null ? "{\"ok\":false,\"error\":\"timeout\",\"detail\":\"job did not finish\"}" : last;
        }

        public static string Bytes(long n)
        {
            if (n < 1024) return n + " B";
            double d = n;
            string[] u = new string[] { "KB", "MB", "GB", "TB" };
            int i = -1;
            while (d >= 1024 && i < 3) { d /= 1024; i++; }
            return d.ToString(d < 10 ? "0.00" : "0.0", CultureInfo.InvariantCulture) + " " + u[i];
        }

        static string Trim(string s, int n)
        {
            if (s == null) return "";
            if (s.Length <= n) return s;
            return "..." + s.Substring(s.Length - n + 3);
        }

        static void Print(string response, bool raw, bool quiet)
        {
            string text = response;
            if (quiet)
            {
                try
                {
                    FJ d = FJ.Parse(response).Get("data");
                    if (d != null) text = d.ToJson();
                }
                catch { }
            }
            Console.WriteLine(raw ? text : Pretty(text));
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

        static int RunScript(string path, bool raw, bool wait, int waitMs)
        {
            string[] lines;
            try { lines = File.ReadAllLines(path); }
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
                string resp = FileApi.Handle(request);
                if (lineWait)
                {
                    string jobId = FindJobId(resp);
                    if (jobId != null) resp = PollJob(jobId, waitMs, -1);
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
            string r = FileApi.Handle("api.commands");
            FJ j = FJ.Parse(r);
            FJ cmds = j.Get("data").Get("commands");
            Console.WriteLine("{0,-16} {1,-8} {2,-7} {3,-9} {4,-6} {5}",
                "COMMAND", "GROUP", "KIND", "ELEVATION", "ASYNC", "ARGS");
            Console.WriteLine(new string('-', 118));
            for (int i = 0; i < cmds.Count; i++)
            {
                FJ c = cmds.At(i);
                Console.WriteLine("{0,-16} {1,-8} {2,-7} {3,-9} {4,-6} {5}",
                    c.S("cmd", ""), c.S("group", ""), c.S("kind", ""), c.S("elevation", ""),
                    c.B("async", false) ? "yes" : "", c.S("args", ""));
            }
            Console.WriteLine();
            Console.WriteLine("elevated = " + j.Get("data").B("elevated", false) +
                              "   user = " + j.Get("data").S("user", "?"));
            return 0;
        }

        // ====================================================================================
        // SELF TEST
        //
        // Everything destructive happens inside the directory handed in, which this creates and
        // removes. Nothing outside it is written to, moved or deleted - the only paths that ever
        // reach a mutating command are built by combining that root with a name.
        // ====================================================================================
        static int SelfTest(string root)
        {
            Console.WriteLine("MarwanOS FileApi self-test");
            Console.WriteLine("user      : " + Environment.UserName + "  elevated=" + IsElev());
            Console.WriteLine("machine   : " + Environment.MachineName);
            Console.WriteLine("scratch   : " + root);
            Console.WriteLine();

            string scratch = null;
            try
            {
                scratch = Path.GetFullPath(root);
                if (!Directory.Exists(scratch)) Directory.CreateDirectory(scratch);
            }
            catch (Exception ex)
            {
                Console.WriteLine("cannot create the scratch directory: " + ex.Message);
                return 2;
            }

            try
            {
                // ---------------- reads ----------------
                Section("reads");
                Check("api.version", "api.version", true);
                Check("api.commands", "api.commands", true);
                Check("fs.drives", "fs.drives", true);
                Check("fs.home", "fs.home", true);
                Check("fs.diskSpace", J("fs.diskSpace", "path", scratch), true);
                Check("fs.list (scratch)", J("fs.list", "path", scratch), true);
                Check("fs.list (Windows, read is never blocked)", J("fs.list", "path", @"C:\Windows"), true);
                Check("fs.exists", J("fs.exists", "path", scratch), true);
                Check("fs.properties (dir)", J("fs.properties", "path", scratch), true);

                // ---------------- guards ----------------
                Section("guards - each of these MUST be refused");
                CheckFail("delete C:\\Windows", "{\"cmd\":\"fs.delete\",\"path\":\"C:\\\\Windows\"}", "refused_system");
                CheckFail("delete C:\\Program Files", "{\"cmd\":\"fs.delete\",\"path\":\"C:\\\\Program Files\"}", "refused_system");
                CheckFail("delete a drive root", "{\"cmd\":\"fs.delete\",\"path\":\"C:\\\\\"}", "refused_root");
                CheckFail("mkdir traversal via name", Json2("fs.mkdir", "path", scratch, "name", @"..\..\escaped"), "bad_name");
                CheckFail("mkdir absolute name", Json2("fs.mkdir", "path", scratch, "name", @"C:\escaped"), "bad_name");
                CheckFail("mkdir reserved device name", Json2("fs.mkdir", "path", scratch, "name", "CON"), "bad_name");
                CheckFail("rename with a path", Json2("fs.rename", "path", scratch, "name", @"a\b"), "bad_name");
                CheckFail("list a missing folder", J("fs.list", "path", scratch + @"\nope-nothing-here"), "not_found");
                CheckFail("bad sort key", Json2("fs.list", "path", scratch, "sort", "colour"), "bad_sort");
                CheckFail("relative path", "{\"cmd\":\"fs.list\",\"path\":\"..\"}", "bad_path");
                CheckFail("drive-relative path", "{\"cmd\":\"fs.list\",\"path\":\"C:docs\"}", "bad_path");

                // ---------------- writes ----------------
                Section("writes, inside the scratch directory only");
                string a = Path.Combine(scratch, "alpha");
                Check("fs.mkdir alpha", Json2("fs.mkdir", "path", scratch, "name", "alpha"), true);
                Check("fs.mkdir alpha again fails", Json2("fs.mkdir", "path", scratch, "name", "alpha"), false);
                Check("fs.mkdir beta", Json2("fs.mkdir", "path", scratch, "name", "beta"), true);
                Check("fs.mkdir alpha/nested", Json2("fs.mkdir", "path", a, "name", "nested"), true);

                // a handful of real files, one of them large enough for the progress path
                WriteFile(Path.Combine(a, "one.txt"), 1200);
                WriteFile(Path.Combine(a, "two.log"), 40000);
                WriteFile(Path.Combine(a, "nested", "three.bin"), 250000);
                Console.WriteLine("   (wrote 3 files, 291 KB, under " + a + ")");

                Check("fs.list alpha", J("fs.list", "path", a), true);
                Check("fs.list alpha by size desc", Json3("fs.list", "path", a, "sort", "size", "desc", "true"), true);
                Check("fs.properties one.txt", J("fs.properties", "path", Path.Combine(a, "one.txt")), true);
                Check("fs.properties folderSize job", Json2("fs.properties", "path", a, "folderSize", "true"), true, true);
                Check("fs.rename one.txt -> renamed.txt",
                      Json2("fs.rename", "path", Path.Combine(a, "one.txt"), "name", "renamed.txt"), true);
                CheckFail("rename onto an existing name",
                      Json2("fs.rename", "path", Path.Combine(a, "renamed.txt"), "name", "two.log"), "exists");

                Section("copy and move (jobs)");
                string b = Path.Combine(scratch, "beta");
                Check("fs.copy alpha -> beta", Json2("fs.copy", "path", a, "to", b), true, true);
                Check("fs.list beta after copy", J("fs.list", "path", b), true);
                Check("fs.move beta\\alpha -> scratch\\moved",
                      Json2("fs.mkdir", "path", scratch, "name", "moved"), true);
                Check("fs.move (same volume = rename)",
                      Json2("fs.move", "path", Path.Combine(b, "alpha"), "to", Path.Combine(scratch, "moved")), true, true);
                CheckFail("copy a folder into itself", Json2("fs.copy", "path", a, "to", Path.Combine(a, "nested")), "recursive");

                Section("delete");
                Check("fs.delete permanent (moved tree)",
                      Json2("fs.delete", "path", Path.Combine(scratch, "moved"), "recycle", "false"), true, true);
                Check("fs.delete recycle (beta)",
                      Json2("fs.delete", "path", b, "recycle", "true"), true, true);

                Section("watch");
                string wid = null;
                string wr = FileApi.Handle(J("fs.watch", "path", scratch));
                Report("fs.watch scratch", IsOk(wr), wr);
                try { wid = FJ.Parse(wr).Get("data").S("watchId", null); }
                catch { }
                WriteFile(Path.Combine(scratch, "watched.txt"), 10);
                Thread.Sleep(700);
                string ev = FileApi.Handle("{\"cmd\":\"fs.events\",\"since\":0}");
                int n = 0;
                try { n = FJ.Parse(ev).Get("data").I("count", 0); }
                catch { }
                Report("fs.events saw the new file (" + n + " event(s))", n > 0, ev);
                Check("fs.watch drives:true", "{\"cmd\":\"fs.watch\",\"drives\":true}", true);
                Check("fs.unwatch all", "{\"cmd\":\"fs.unwatch\",\"all\":true}", true);
                if (wid == null) Console.WriteLine("   (no watchId came back)");

                Section("eject - expected to refuse on a machine with no removable drive");
                string ej = FileApi.Handle("{\"cmd\":\"fs.eject\",\"drive\":\"C\"}");
                Report("fs.eject C: refused as not removable", !IsOk(ej) && ej.IndexOf("not_removable", StringComparison.Ordinal) >= 0, ej);
                string ejz = FileApi.Handle("{\"cmd\":\"fs.eject\",\"drive\":\"Z\"}");
                Report("fs.eject on an absent letter says so", !IsOk(ejz) && ejz.IndexOf("no_such_drive", StringComparison.Ordinal) >= 0, ejz);
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("SELF-TEST THREW (this is a bug in the harness, not the API): " + ex);
                _fail++;
            }
            finally
            {
                // Clean up with plain .NET, not with the API under test: a cleanup that depends on
                // the thing being tested is not a cleanup.
                Console.WriteLine();
                Console.WriteLine("cleaning up " + scratch);
                try
                {
                    if (scratch != null && Directory.Exists(scratch)) ForceDelete(scratch);
                    Console.WriteLine("removed: " + (!Directory.Exists(scratch)));
                }
                catch (Exception ex) { Console.WriteLine("cleanup problem: " + ex.Message); }
            }

            Console.WriteLine();
            Console.WriteLine("=============================================================");
            Console.WriteLine("self-test complete: " + _pass + " passed, " + _fail + " failed");
            Console.WriteLine("=============================================================");
            return _fail == 0 ? 0 : 1;
        }

        static void ForceDelete(string dir)
        {
            foreach (string f in Directory.GetFiles(dir))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); }
                catch { }
                try { File.Delete(f); }
                catch { }
            }
            foreach (string d in Directory.GetDirectories(dir))
            {
                try { ForceDelete(d); }
                catch { }
            }
            try { Directory.Delete(dir, false); }
            catch { }
        }

        static void WriteFile(string path, int bytes)
        {
            string dir = Path.GetDirectoryName(path);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            byte[] buf = new byte[bytes];
            for (int i = 0; i < bytes; i++) buf[i] = (byte)(i & 0xFF);
            File.WriteAllBytes(path, buf);
        }

        static bool IsElev()
        {
            try
            {
                using (System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent())
                    return new System.Security.Principal.WindowsPrincipal(id)
                        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine("-- " + title + " " + new string('-', Math.Max(2, 58 - title.Length)));
        }

        static string J(string cmd, string k, string v)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"cmd\":"); Quote(sb, cmd); sb.Append(',');
            Quote(sb, k); sb.Append(':'); Quote(sb, v); sb.Append('}');
            return sb.ToString();
        }

        static string Json2(string cmd, string k1, string v1, string k2, string v2)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"cmd\":"); Quote(sb, cmd); sb.Append(',');
            Quote(sb, k1); sb.Append(':'); AppendTyped(sb, v1); sb.Append(',');
            Quote(sb, k2); sb.Append(':'); AppendTyped(sb, v2); sb.Append('}');
            return sb.ToString();
        }

        static string Json3(string cmd, string k1, string v1, string k2, string v2, string k3, string v3)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"cmd\":"); Quote(sb, cmd); sb.Append(',');
            Quote(sb, k1); sb.Append(':'); AppendTyped(sb, v1); sb.Append(',');
            Quote(sb, k2); sb.Append(':'); AppendTyped(sb, v2); sb.Append(',');
            Quote(sb, k3); sb.Append(':'); AppendTyped(sb, v3); sb.Append('}');
            return sb.ToString();
        }

        static void Check(string label, string request, bool wantOk) { Check(label, request, wantOk, false); }

        static void Check(string label, string request, bool wantOk, bool job)
        {
            string r = FileApi.Handle(request);
            if (job)
            {
                string id = FindJobId(r);
                if (id != null) r = PollJob(id, 120000, -1);
            }
            bool ok = IsOk(r);
            if (job && ok)
            {
                // A finished job envelope is ok:true even when the job errored - check the state.
                try
                {
                    FJ d = FJ.Parse(r).Get("data");
                    string st = d.S("state", "");
                    if (st == "error" || st == "cancelled") ok = false;
                }
                catch { }
            }
            Report(label, ok == wantOk, r);
        }

        static void CheckFail(string label, string request, string wantCode)
        {
            string r = FileApi.Handle(request);
            bool failed = !IsOk(r);
            bool codeOk = true;
            string got = "";
            try { got = FJ.Parse(r).S("error", ""); }
            catch { }
            if (wantCode != null) codeOk = string.Equals(got, wantCode, StringComparison.Ordinal);
            Report(label + (wantCode != null ? ("  [" + wantCode + "]") : ""), failed && codeOk, r);
        }

        static void Report(string label, bool ok, string response)
        {
            if (ok) { _pass++; Console.WriteLine("   ok    " + label); }
            else
            {
                _fail++;
                Console.WriteLine("   FAIL  " + label);
                Console.WriteLine("         " + OneLine(response));
            }
            // The detail line is the human-readable text the UI would show; print it always, it
            // is half the point of the exercise.
            try
            {
                FJ j = FJ.Parse(response);
                if (!j.B("ok", false))
                    Console.WriteLine("         -> " + j.S("error", "") + ": " + j.S("detail", ""));
            }
            catch { }
        }

        static string OneLine(string s)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length > 300 ? s.Substring(0, 300) + " ..." : s;
        }
    }
}
