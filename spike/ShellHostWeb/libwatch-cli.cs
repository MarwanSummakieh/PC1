// MarwanOS - LibraryWatch + MetaApi console harness
//
// Mirrors systemapi-cli.cs / libraryapi-cli.cs so the watcher, the installed rule, the scan
// config and the metadata provider can all be exercised without going near the live shell.
//
//   libwatch-cli.exe --installed              the "is it installed" rule over every Steam manifest
//   libwatch-cli.exe --art 730                local art discovery for one appid, with pixel sizes
//   libwatch-cli.exe --art 730 --net          same, but allowed to fetch the 2x hero from the CDN
//   libwatch-cli.exe --watch 90               start the watchers and print lib.changed for 90 s
//   libwatch-cli.exe lib.config.get
//   libwatch-cli.exe meta.cache
//   libwatch-cli.exe meta.lookup appId=367520 network=true
//   libwatch-cli.exe --json "{\"cmd\":\"lib.watch\"}"
//
// Flags:
//   --net       allow network in --art / --meta (default: local only)
//   --raw       do not pretty-print
//   --quiet     print only the data object
//   --steam-root <dir>   pretend Steam is installed here (for scratch-copy verification)
//
// NOTHING HERE LAUNCHES A GAME, WRITES TO STEAM, OR SIGNS INTO ANYTHING.
// --watch only reads; the rescan it triggers is the same read-only scan lib.scan already does.
//
// Language level: C# 5 (inbox .NET Framework csc.exe). Built by build-watch-cli.cmd.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using MarwanOs.Library;
using MarwanOs.LibWatch;
using MarwanOs.Meta;

namespace MarwanOs.LibWatch.Cli
{
    public static class WatchCliProgram
    {
        [STAThread]
        public static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; }
            catch { }

            bool raw = false, quiet = false, net = false;
            string explicitJson = null;
            int watchSecs = 0;
            string artApp = null;
            string upgradeApp = null;
            string guardDir = null;
            string steamRoot = null;
            bool installed = false;
            List<string> rest = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--raw") raw = true;
                else if (a == "--quiet") quiet = true;
                else if (a == "--net") net = true;
                else if (a == "--installed") installed = true;
                else if (a == "--art") { if (i + 1 < args.Length) artApp = args[++i]; }
                else if (a == "--upgrade") { if (i + 1 < args.Length) upgradeApp = args[++i]; }
                else if (a == "--guard") { if (i + 1 < args.Length) guardDir = args[++i]; }
                else if (a == "--steam-root") { if (i + 1 < args.Length) steamRoot = args[++i]; }
                else if (a == "--watch")
                {
                    watchSecs = 60;
                    if (i + 1 < args.Length)
                    {
                        int s;
                        if (int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out s))
                        { watchSecs = s; i++; }
                    }
                }
                else if (a == "--json") { if (i + 1 < args.Length) explicitJson = args[++i]; }
                else if (a == "-h" || a == "--help" || a == "/?") { Usage(); return 0; }
                else rest.Add(a);
            }

            if (!string.IsNullOrEmpty(steamRoot) && guardDir == null) WSteam.OverrideRoot(steamRoot);
            if (guardDir != null) return ShowGuard(guardDir, steamRoot);
            if (installed) return ShowInstalled();
            if (upgradeApp != null) return ShowUpgrade(upgradeApp);
            if (artApp != null) return ShowArt(artApp, net);
            if (watchSecs > 0) return RunWatch(watchSecs);

            string request;
            if (explicitJson != null) request = explicitJson;
            else if (rest.Count == 0) { Usage(); return 2; }
            else request = BuildJson(rest);

            string cmd = PeekCmd(request);
            string response = Route(cmd, request);
            Print(response, raw, quiet);
            return Ok(response) ? 0 : 1;
        }

        // One harness, three boundaries - routed the same way ShellHostWeb will route them.
        static string Route(string cmd, string json)
        {
            if (LibWatchApi.Owns(cmd)) return LibWatchApi.Handle(json);
            if (MetaApi.Owns(cmd)) return MetaApi.Handle(json);
            return LibraryApi.Handle(json);
        }

        // -----------------------------------------------------------------------------------
        // The installed rule, laid out so the verdict is readable at a glance.
        // -----------------------------------------------------------------------------------
        static int ShowInstalled()
        {
            string json = LibWatchApi.Handle("{\"cmd\":\"lib.installed\"}");
            LJ doc = LJ.Parse(json);
            if (!doc.B("ok", false)) { Console.WriteLine(json); return 1; }
            LJ data = doc.Get("data");

            LJ libs = data.Get("libraries");
            Console.WriteLine("Steam libraries:");
            for (int i = 0; i < libs.Count; i++) Console.WriteLine("  " + libs.At(i).AsStr(""));
            Console.WriteLine();

            Console.WriteLine("{0,-10} {1,-7} {2,-10} {3,-12} {4}", "appid", "flags", "installed", "state", "name");
            Console.WriteLine(new string('-', 78));
            LJ apps = data.Get("apps");
            for (int i = 0; i < apps.Count; i++)
            {
                LJ a = apps.At(i);
                long f = (long)a.N("stateFlags", 0);
                bool ins = a.B("installed", false);
                string state = ins
                    ? (a.B("updatePending", false) ? "update pend" : "installed")
                    : (a.B("downloading", false) ? "DOWNLOADING" : "NOT INSTALLED");
                Console.WriteLine("{0,-10} {1,-7} {2,-10} {3,-12} {4}",
                    a.S("appId", ""), f, ins ? "yes" : "NO", state, a.S("name", ""));
            }
            Console.WriteLine();
            Console.WriteLine("Rule: installed  <=>  (StateFlags & 4) != 0     [4 = StateFullyInstalled]");
            Console.WriteLine("      2 = update required only (payload absent), 1026 = 1024|2 downloading,");
            Console.WriteLine("      6 = 4|2 installed with an update pending -> still playable.");
            return 0;
        }

        // -----------------------------------------------------------------------------------
        // Art discovery, with real pixel dimensions so "is this actually 4K" is answerable.
        // -----------------------------------------------------------------------------------
        static int ShowArt(string appid, bool net)
        {
            string[] kinds = new string[] { "hero", "cover", "logo", "header" };
            Console.WriteLine("appid " + appid + (net ? "   (network allowed)" : "   (local only)"));
            Console.WriteLine();
            for (int i = 0; i < kinds.Length; i++)
            {
                string q = "{\"cmd\":\"meta.art\",\"appId\":\"" + appid + "\",\"kind\":\"" + kinds[i] +
                           "\",\"network\":" + (net ? "true" : "false") + "}";
                LJ doc = LJ.Parse(MetaApi.Handle(q));
                if (!doc.B("ok", false))
                {
                    Console.WriteLine("{0,-8} ERROR {1}", kinds[i], doc.S("detail", ""));
                    continue;
                }
                LJ d = doc.Get("data");
                string p = d.S("path", null);
                if (p == null)
                {
                    Console.WriteLine("{0,-8} (none)   origin={1}", kinds[i], d.S("origin", "?"));
                    continue;
                }
                Console.WriteLine("{0,-8} {1,-12} {2,11}  {3}", kinds[i], d.S("origin", "?"), Dims(p), p);
            }
            return 0;
        }

        // Reads the JPEG/PNG header directly. System.Drawing would work but pulls a reference
        // this harness does not otherwise need, and would decode the whole image to answer a
        // question the first twenty bytes already contain.
        static string Dims(string path)
        {
            try
            {
                using (FileStream fs = File.OpenRead(path))
                {
                    byte[] h = new byte[26];
                    int n = fs.Read(h, 0, h.Length);
                    if (n >= 24 && h[0] == 0x89 && h[1] == 0x50)
                    {
                        int w = (h[16] << 24) | (h[17] << 16) | (h[18] << 8) | h[19];
                        int ht = (h[20] << 24) | (h[21] << 16) | (h[22] << 8) | h[23];
                        return w + "x" + ht;
                    }
                    if (n >= 4 && h[0] == 0xFF && h[1] == 0xD8)
                    {
                        fs.Position = 2;
                        return JpegDims(fs);
                    }
                }
            }
            catch { }
            return "?";
        }

        static string JpegDims(FileStream fs)
        {
            byte[] b = new byte[9];
            while (true)
            {
                int m = fs.ReadByte();
                if (m < 0) return "?";
                if (m != 0xFF) continue;
                int t = fs.ReadByte();
                while (t == 0xFF) t = fs.ReadByte();
                if (t < 0) return "?";
                // SOF0..SOF15 except the non-frame markers DHT(C4), JPG(C8), DAC(CC)
                if (t >= 0xC0 && t <= 0xCF && t != 0xC4 && t != 0xC8 && t != 0xCC)
                {
                    if (fs.Read(b, 0, 7) < 7) return "?";
                    int h = (b[3] << 8) | b[4];
                    int w = (b[5] << 8) | b[6];
                    return w + "x" + h;
                }
                if (fs.Read(b, 0, 2) < 2) return "?";
                int len = (b[0] << 8) | b[1];
                if (len < 2) return "?";
                fs.Position += len - 2;
            }
        }

        // -----------------------------------------------------------------------------------
        // THE POINT OF THE WHOLE EXERCISE.
        //
        // Runs the ordinary games-folder source over a directory - typically a SCRATCH COPY of a
        // steamapps\common tree - and then applies the one installed rule to whatever it found.
        // Everything the folder source admits on "the directory exists" alone shows up in the
        // first list; everything the rule then rejects shows up in the second.
        //
        //   libwatch-cli.exe --guard <commonDir> --steam-root <scratchSteamRoot>
        // -----------------------------------------------------------------------------------
        static int ShowGuard(string commonDir, string steamRoot)
        {
            if (!string.IsNullOrEmpty(steamRoot))
            {
                WSteam.OverrideRoot(steamRoot);
                Console.WriteLine("steam root : " + steamRoot + "   (scratch copy)");
            }
            Console.WriteLine("scan root  : " + commonDir);
            Console.WriteLine();

            Console.WriteLine("manifests seen by the rule:");
            Dictionary<string, WSteamApp> idx = WSteam.Index();
            foreach (KeyValuePair<string, WSteamApp> kv in idx)
            {
                WSteamApp a = kv.Value;
                Console.WriteLine("  {0,-8} flags {1,-6} {2,-14} {3}",
                    a.AppId, a.Flags, a.Installed ? "installed" : "NOT INSTALLED", a.Name);
            }
            Console.WriteLine();

            List<LibEntry> found = new List<LibEntry>();
            LibSource src = new LibSource();
            src.Name = "folder";
            src.Label = "Installed folders";
            List<string> roots = new List<string>();
            roots.Add(commonDir);
            Sources.GameFolders(found, src, roots);

            Console.WriteLine("folder source admitted {0}:", found.Count);
            for (int i = 0; i < found.Count; i++)
                Console.WriteLine("  {0,-24} {1}", found[i].Title, found[i].LaunchTarget);
            Console.WriteLine();

            List<WGuard.Drop> drops = WGuard.Apply(found);

            Console.WriteLine("after the installed rule, {0} remain:", found.Count);
            for (int i = 0; i < found.Count; i++)
                Console.WriteLine("  KEEP {0,-24} {1}", found[i].Title, found[i].LaunchTarget);
            for (int i = 0; i < drops.Count; i++)
                Console.WriteLine("  DROP {0,-24} {1}", drops[i].Title, drops[i].Reason);
            return 0;
        }

        // -----------------------------------------------------------------------------------
        // Force the CDN upgrade for one appid, synchronously, and show before/after pixels.
        // This is what proves the 4K hero path independently of the background prefetch job.
        // -----------------------------------------------------------------------------------
        static int ShowUpgrade(string appid)
        {
            LJ before = LJ.Parse(MetaApi.Handle(
                "{\"cmd\":\"meta.art\",\"appId\":\"" + appid + "\",\"kind\":\"hero\"}")).Get("data");
            string bp = before == null ? null : before.S("path", null);
            Console.WriteLine("before : {0,-12} {1,11}  {2}",
                before == null ? "?" : before.S("origin", "?"), bp == null ? "-" : Dims(bp), bp == null ? "(none)" : bp);

            LJ got = MProvider.FetchArt(appid, "hero");
            if (got == null)
            {
                Console.WriteLine("after  : fetch failed (offline, 404 or throttled) - local art still stands");
                return 1;
            }
            string ap = got.S("path", null);
            Console.WriteLine("after  : {0,-12} {1,11}  {2}", got.S("origin", "?"), Dims(ap), ap);
            Console.WriteLine("         url  {0}", got.S("url", ""));
            Console.WriteLine("         {0} bytes", got.N("bytes", 0));

            LJ again = LJ.Parse(MetaApi.Handle(
                "{\"cmd\":\"meta.art\",\"appId\":\"" + appid + "\",\"kind\":\"hero\"}")).Get("data");
            Console.WriteLine("reread : {0,-12} {1,11}  (network={2})",
                again.S("origin", "?"), Dims(again.S("path", "")), again.B("network", false));
            return 0;
        }

        // -----------------------------------------------------------------------------------
        // Live watch. Prints lib.changed as it arrives.
        // -----------------------------------------------------------------------------------
        static int RunWatch(int secs)
        {
            Console.WriteLine(LibWatchApi.Handle("{\"cmd\":\"lib.watch\"}"));
            Console.WriteLine();
            Console.WriteLine("watching for " + secs + "s  (Ctrl-C to stop)");
            Console.WriteLine();

            long since = 0;
            DateTime end = DateTime.UtcNow.AddSeconds(secs);
            while (DateTime.UtcNow < end)
            {
                string j = LibWatchApi.Handle("{\"cmd\":\"lib.changed\",\"since\":" + since + "}");
                LJ doc = LJ.Parse(j);
                if (doc.B("ok", false))
                {
                    LJ d = doc.Get("data");
                    since = (long)d.N("seq", since);
                    LJ evs = d.Get("events");
                    for (int i = 0; i < evs.Count; i++)
                    {
                        LJ e = evs.At(i);
                        Console.WriteLine("[{0}] {1} {2}",
                            e.S("atUtc", ""), e.S("kind", ""), e.Get("data") == null ? e.S("detail", "") : e.Get("data").ToJson());
                    }
                }
                Thread.Sleep(500);
            }
            Console.WriteLine(LibWatchApi.Handle("{\"cmd\":\"lib.unwatch\",\"all\":true}"));
            return 0;
        }

        // -----------------------------------------------------------------------------------
        static string PeekCmd(string json)
        {
            try
            {
                string t = json.Trim();
                if (t.Length > 0 && t[0] != '{') return t;
                return LJ.Parse(t).S("cmd", "");
            }
            catch { return ""; }
        }

        static bool Ok(string response)
        {
            try { return LJ.Parse(response).B("ok", false); }
            catch { return false; }
        }

        // cmd key=value key=value  ->  JSON. true/false/numbers are kept as their JSON types so
        // {"network":true} does not become {"network":"true"}.
        static string BuildJson(List<string> parts)
        {
            LJ o = LJ.Obj();
            o.Set("cmd", parts[0]);
            for (int i = 1; i < parts.Count; i++)
            {
                int eq = parts[i].IndexOf('=');
                if (eq <= 0) continue;
                string k = parts[i].Substring(0, eq);
                string v = parts[i].Substring(eq + 1);
                if (v == "true") o.Set(k, true);
                else if (v == "false") o.Set(k, false);
                else
                {
                    long n;
                    if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) o.Set(k, n);
                    else o.Set(k, v);
                }
            }
            return o.ToJson();
        }

        static void Print(string response, bool raw, bool quiet)
        {
            string text = response;
            if (quiet)
            {
                try
                {
                    LJ doc = LJ.Parse(response);
                    LJ d = doc.Get("data");
                    if (d != null) text = d.ToJson();
                }
                catch { }
            }
            Console.WriteLine(raw ? text : Pretty(text));
        }

        // Small, dependency-free pretty printer. String-aware so a brace inside a description
        // does not shift the indentation.
        static string Pretty(string json)
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
                if (c == '"') { inStr = true; sb.Append(c); continue; }
                if (c == '{' || c == '[')
                {
                    sb.Append(c);
                    depth++;
                    sb.Append('\n').Append(new string(' ', depth * 2));
                }
                else if (c == '}' || c == ']')
                {
                    depth--;
                    sb.Append('\n').Append(new string(' ', depth < 0 ? 0 : depth * 2)).Append(c);
                }
                else if (c == ',')
                {
                    sb.Append(c).Append('\n').Append(new string(' ', depth * 2));
                }
                else if (c == ':') sb.Append(": ");
                else if (c == ' ' || c == '\n' || c == '\r' || c == '\t') { }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        static void Usage()
        {
            Console.WriteLine("libwatch-cli - LibraryWatch + MetaApi harness");
            Console.WriteLine();
            Console.WriteLine("  --installed            StateFlags table and the installed verdict");
            Console.WriteLine("  --art <appid> [--net]  art discovery with pixel dimensions");
            Console.WriteLine("  --watch [secs]         start watchers, print lib.changed live");
            Console.WriteLine("  <cmd> [k=v ...]        any lib.* / meta.* / LibraryApi command");
            Console.WriteLine("  --json <json>          raw request");
            Console.WriteLine();
            Console.WriteLine("Commands: lib.watch lib.unwatch lib.changed lib.flush lib.installed");
            Console.WriteLine("          lib.config.get lib.config.set");
            Console.WriteLine("          meta.lookup meta.art meta.cache meta.prefetch");
            Console.WriteLine("          meta.config.get meta.config.set");
        }
    }
}
