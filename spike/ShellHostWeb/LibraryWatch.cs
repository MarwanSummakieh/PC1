// ===========================================================================================
// LibraryWatch.cs - MarwanOS
//
// Three things live here, all of them additions to LibraryApi.cs rather than edits of it:
//
//   1. WCfg    - user-configurable scan folders and per-source switches, JSON under
//                %LOCALAPPDATA%\ArcOS\library-config.json.  lib.config.get / lib.config.set.
//   2. WSteam  - THE "is it installed" rule, in one place, usable by every source.
//   3. WWatch  - FileSystemWatchers over the things that actually change when a game is
//                installed, debounced into a single rescan.  lib.watch / lib.unwatch /
//                lib.changed.
//
// -------------------------------------------------------------------------------------------
// WHY THIS FILE EXISTS
// -------------------------------------------------------------------------------------------
// The shell scanned at boot and painted from cache, so two games installed while it was running
// stayed invisible until someone restarted it. Watching is the fix, but a naive watcher is worse
// than none: Steam rewrites an appmanifest dozens of times over the course of a download, and a
// rescan per write would peg a core and make the rail flicker between "downloading" and gone.
// Hence the debounce, and hence WSteam - a title must not appear at all until it is really there.
//
// -------------------------------------------------------------------------------------------
// REFERENCES
// -------------------------------------------------------------------------------------------
// System.dll and System.Core.dll only. No System.Drawing, no WinRT, no registry beyond what
// LibraryApi already does for us. Add this file to build.cmd's source list; it is purely
// additive and compiles into the same binary.
//
// -------------------------------------------------------------------------------------------
// REUSE, NOT DUPLICATION
// -------------------------------------------------------------------------------------------
// This file deliberately does NOT carry its own JSON writer, VDF parser, path helpers or folder
// scanner. It uses MarwanOs.Library's LJ, LKv, LApi, LibEntry, Sources and ScanMod, all of which
// are same-assembly visible. In particular the configured folders from WCfg are handed to
// Sources.GameFolders as roots, so they get the exact exe-picking logic that already knows
// TEKKEN 8's uninstaller is bigger than its game exe and that "biggest binary wins" is wrong.
//
// Type names are W-prefixed and the namespace is MarwanOs.LibWatch, so nothing here can collide
// with SystemApi's, FileApi's or LibraryApi's helpers.
//
// -------------------------------------------------------------------------------------------
// THREADING CONTRACT
// -------------------------------------------------------------------------------------------
// Mirrors FileApi's WatchMod exactly, because the page already knows that shape:
//   lib.watch   -> starts watchers, returns immediately
//   lib.changed -> {"cmd":"lib.changed","since":N} long-poll-free cursor read
//   lib.unwatch -> stops one or all
// FileSystemWatcher raises on a threadpool thread; everything mutable here is behind Gate.
// The debounce/rescan worker is one background thread, MTA, started lazily and never joined.
// A rescan takes the same path as lib.scan (ScanMod.Run) so there is exactly one scanner.
// ===========================================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using MarwanOs.Library;

namespace MarwanOs.LibWatch
{
    #region Configuration

    // One configured scan folder.
    public sealed class WFolder
    {
        public string Path;
        public bool Recurse;
        public bool Enabled = true;

        public LJ ToJson()
        {
            LJ o = LJ.Obj();
            o.Set("path", Path);
            o.Set("recurse", Recurse);
            o.Set("enabled", Enabled);
            // Reported so Settings can grey out a folder that has gone away (unplugged USB,
            // deleted directory) WITHOUT the scan itself treating that as an error.
            o.Set("exists", WCfg.DirOk(Path));
            return o;
        }
    }

    // The persisted shape, and the rules for reading it safely.
    //
    //   %LOCALAPPDATA%\ArcOS\library-config.json
    //   {
    //     "version": 1,
    //     "folders": [ { "path": "D:\\Games", "recurse": true, "enabled": true } ],
    //     "disabledSources": [ "xbox" ],
    //     "watch": { "enabled": true, "debounceMs": 4000 }
    //   }
    //
    // "ArcOS" is intentional and matches CacheMod.Dir(): the product is MarwanOS, the folder on
    // disk is not, and renaming it would orphan every cache on every machine already deployed.
    public static class WCfg
    {
        public const int Version = 1;
        public const int MinDebounceMs = 500;
        public const int MaxDebounceMs = 120000;
        public const int DefaultDebounceMs = 4000;
        public const int MaxFolders = 32;

        // Sources a user is allowed to switch off. Anything not in here cannot be disabled,
        // which stops a typo in the config file from silently emptying the library.
        public static readonly string[] KnownSources = new string[] {
            "steam", "epic", "gog", "battlenet", "xbox", "shortcut", "folder"
        };

        static readonly object Gate = new object();
        static LJ _cached;

        public static string Dir()
        {
            string b = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return System.IO.Path.Combine(b, "ArcOS");
        }

        public static string Path_() { return System.IO.Path.Combine(Dir(), "library-config.json"); }

        public static bool DirOk(string p)
        {
            if (string.IsNullOrEmpty(p)) return false;
            try { return Directory.Exists(p); }
            catch { return false; }
        }

        // Never throws, never returns null. A config file that is missing, truncated, or full of
        // the wrong types degrades to defaults - a bad config must cost the user their custom
        // folders, never their whole library.
        public static LJ Load()
        {
            lock (Gate)
            {
                if (_cached != null) return _cached;
                LJ doc = null;
                try
                {
                    string p = Path_();
                    if (File.Exists(p)) doc = LJ.Parse(File.ReadAllText(p));
                }
                catch { doc = null; }
                if (doc == null || doc.Kind != LJ.TObj) doc = Defaults();
                _cached = Normalize(doc);
                return _cached;
            }
        }

        public static LJ Defaults()
        {
            LJ o = LJ.Obj();
            o.Set("version", Version);
            o.Set("folders", LJ.Arr());
            o.Set("disabledSources", LJ.Arr());
            LJ w = LJ.Obj();
            w.Set("enabled", true);
            w.Set("debounceMs", DefaultDebounceMs);
            o.Set("watch", w);
            return o;
        }

        // Coerce whatever we read into the documented shape. Every field is defended
        // individually so one bad entry loses only itself.
        static LJ Normalize(LJ raw)
        {
            LJ o = LJ.Obj();
            o.Set("version", Version);

            LJ folders = LJ.Arr();
            LJ f = raw.Get("folders");
            if (f != null && f.Kind == LJ.TArr)
            {
                Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < f.Count && folders.Count < MaxFolders; i++)
                {
                    LJ it = f.At(i);
                    if (it == null) continue;
                    string path = null;
                    bool rec = false, en = true;
                    if (it.Kind == LJ.TStr) path = it.AsStr(null);
                    else if (it.Kind == LJ.TObj)
                    {
                        path = it.S("path", null);
                        rec = it.B("recurse", false);
                        en = it.B("enabled", true);
                    }
                    if (string.IsNullOrEmpty(path)) continue;

                    string full = SafeFull(path);
                    if (full == null) continue;                    // unusable path, drop it
                    if (seen.ContainsKey(full)) continue;          // same folder twice
                    seen[full] = true;

                    LJ e = LJ.Obj();
                    e.Set("path", full);
                    e.Set("recurse", rec);
                    e.Set("enabled", en);
                    e.Set("exists", DirOk(full));
                    folders.Add(e);
                }
            }
            o.Set("folders", folders);

            LJ dis = LJ.Arr();
            LJ d = raw.Get("disabledSources");
            if (d != null && d.Kind == LJ.TArr)
            {
                for (int i = 0; i < d.Count; i++)
                {
                    string s = d.At(i).AsStr(null);
                    if (string.IsNullOrEmpty(s)) continue;
                    s = s.Trim().ToLowerInvariant();
                    bool known = false;
                    for (int k = 0; k < KnownSources.Length; k++)
                        if (KnownSources[k] == s) { known = true; break; }
                    if (!known) continue;
                    bool dup = false;
                    for (int k = 0; k < dis.Count; k++)
                        if (dis.At(k).AsStr("") == s) { dup = true; break; }
                    if (!dup) dis.Add(s);
                }
            }
            o.Set("disabledSources", dis);

            LJ wIn = raw.Get("watch");
            LJ w = LJ.Obj();
            bool wen = true;
            int deb = DefaultDebounceMs;
            if (wIn != null && wIn.Kind == LJ.TObj)
            {
                wen = wIn.B("enabled", true);
                deb = wIn.I("debounceMs", DefaultDebounceMs);
            }
            if (deb < MinDebounceMs) deb = MinDebounceMs;
            if (deb > MaxDebounceMs) deb = MaxDebounceMs;
            w.Set("enabled", wen);
            w.Set("debounceMs", deb);
            o.Set("watch", w);

            return o;
        }

        // Path.GetFullPath throws on a dozen different malformed inputs; none of them should
        // reach the caller.
        static string SafeFull(string p)
        {
            try
            {
                string t = Environment.ExpandEnvironmentVariables(p.Trim());
                if (t.Length == 0) return null;
                string full = System.IO.Path.GetFullPath(t);
                return full.TrimEnd('\\', '/');
            }
            catch { return null; }
        }

        // Merge-on-write: a caller may send only "folders" and keep the rest. Returns the
        // normalized document that was actually persisted.
        public static LJ Save(LJ patch)
        {
            lock (Gate)
            {
                LJ cur = _cached != null ? _cached : Load();
                LJ merged = LJ.Obj();
                merged.Set("version", Version);
                merged.Set("folders", patch != null && patch.Get("folders") != null
                    ? patch.Get("folders") : cur.Get("folders"));
                merged.Set("disabledSources", patch != null && patch.Get("disabledSources") != null
                    ? patch.Get("disabledSources") : cur.Get("disabledSources"));
                merged.Set("watch", patch != null && patch.Get("watch") != null
                    ? patch.Get("watch") : cur.Get("watch"));

                LJ norm = Normalize(merged);
                _cached = norm;

                // Same temp-then-move dance CacheMod uses: a power cut must not leave a
                // half-written config that the next boot cannot parse.
                try
                {
                    string dir = Dir();
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    string tmp = Path_() + ".tmp";
                    File.WriteAllText(tmp, norm.ToJson(), new UTF8Encoding(false));
                    string final = Path_();
                    if (File.Exists(final)) File.Delete(final);
                    File.Move(tmp, final);
                }
                catch { /* an unwritable config is a lost preference, not a broken shell */ }

                return norm;
            }
        }

        public static void Forget() { lock (Gate) { _cached = null; } }

        public static bool SourceDisabled(string name)
        {
            try
            {
                LJ d = Load().Get("disabledSources");
                if (d == null || d.Kind != LJ.TArr) return false;
                for (int i = 0; i < d.Count; i++)
                    if (string.Equals(d.At(i).AsStr(""), name, StringComparison.OrdinalIgnoreCase))
                        return true;
            }
            catch { }
            return false;
        }

        public static int DebounceMs()
        {
            try { LJ w = Load().Get("watch"); if (w != null) return w.I("debounceMs", DefaultDebounceMs); }
            catch { }
            return DefaultDebounceMs;
        }

        // The roots handed to Sources.GameFolders. A folder marked recurse also contributes its
        // immediate subdirectories, so "D:\Games\Emulated\Dolphin" is reachable from "D:\Games"
        // without an unbounded disk walk. Folders that have vanished are skipped silently -
        // an unplugged drive is an ordinary Tuesday, not an error.
        public static List<string> ScanRoots(bool includeBuiltIn)
        {
            List<string> roots = new List<string>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            if (includeBuiltIn && !SourceDisabled("folder"))
            {
                try
                {
                    List<string> b = Sources.DefaultGameRoots();
                    for (int i = 0; i < b.Count; i++) AddRoot(roots, seen, b[i]);
                }
                catch { }
            }

            try
            {
                LJ f = Load().Get("folders");
                if (f != null && f.Kind == LJ.TArr)
                {
                    for (int i = 0; i < f.Count; i++)
                    {
                        LJ it = f.At(i);
                        if (it == null || it.Kind != LJ.TObj) continue;
                        if (!it.B("enabled", true)) continue;
                        string p = it.S("path", null);
                        if (!DirOk(p)) continue;
                        AddRoot(roots, seen, p);
                        if (!it.B("recurse", false)) continue;
                        string[] subs;
                        try { subs = Directory.GetDirectories(p); }
                        catch { continue; }
                        for (int s = 0; s < subs.Length; s++) AddRoot(roots, seen, subs[s]);
                    }
                }
            }
            catch { }

            return roots;
        }

        static void AddRoot(List<string> roots, Dictionary<string, bool> seen, string p)
        {
            if (string.IsNullOrEmpty(p)) return;
            string k = p.TrimEnd('\\');
            if (seen.ContainsKey(k)) return;
            seen[k] = true;
            roots.Add(p);
        }
    }

    #endregion

    #region The single "is it installed" rule

    // One Steam app as the manifest describes it.
    public sealed class WSteamApp
    {
        public string AppId;
        public string Name;
        public string InstallDir;     // full path under <library>\steamapps\common
        public long Flags;
        public long BytesDownloaded;
        public long BytesToDownload;

        public bool Installed { get { return WSteam.FlagsMeanInstalled(Flags); } }
    }

    // -----------------------------------------------------------------------------------------
    // THE RULE
    // -----------------------------------------------------------------------------------------
    // Steam's StateFlags is a BITFIELD, not an enum. The only bit that means "the payload is on
    // this disk and can be launched" is bit 2 (value 4, StateFullyInstalled). Everything else is
    // orthogonal progress reporting:
    //
    //     1    StateUncommitted
    //     2    StateUpdateRequired      - installed, an update is pending. Still playable.
    //     4    StateFullyInstalled      - THE bit.
    //   1024   StateUpdateStarted       - a download is running right now.
    //
    // So:
    //     4     = installed                      (Hollow Knight, AV-Racer on the bench)
    //     6     = 4|2 installed, update pending  (CS2 and Dota 2 on the laptop)
    //     2     = NOT installed                  (manifest exists, payload does not)
    //     1026  = 1024|2 downloading, NOT installed
    //
    // The rule was already correct inside Sources.Steam(). What was missing is that it lived
    // ONLY there: any other source that walks into a steamapps\common directory - the Start Menu
    // shortcut source, and now the user-configured folders from WCfg - admitted a title on the
    // strength of "the folder exists", with no idea a download was half finished. Once a user
    // adds "D:\SteamLibrary\steamapps\common" as a scan folder, that stops being theoretical.
    //
    // WSteam therefore indexes every manifest on the machine ONCE per scan, and WGuard applies
    // the rule to the finished entry list regardless of which source produced each entry.
    // -----------------------------------------------------------------------------------------
    public static class WSteam
    {
        public const long StateFullyInstalled = 4;
        public const long StateUpdateStarted = 1024;

        public static bool FlagsMeanInstalled(long flags)
        {
            return (flags & StateFullyInstalled) != 0;
        }

        // Test seam. Points the manifest index at a SCRATCH COPY of a Steam tree so the rule can
        // be exercised against StateFlags values the real machine does not currently have,
        // without touching Steam's own files. Null restores the real install.
        static string _rootOverride;
        public static void OverrideRoot(string p) { _rootOverride = p; }

        // Every Steam library root on the machine, from libraryfolders.vdf. Reuses LibraryApi's
        // LKv rather than carrying a second VDF parser.
        public static List<string> Libraries()
        {
            List<string> libs = new List<string>();
            string root = _rootOverride;
            if (string.IsNullOrEmpty(root))
            {
                try { root = Sources.SteamInstallPath(); }
                catch { }
            }
            if (string.IsNullOrEmpty(root)) return libs;
            libs.Add(root);

            try
            {
                string vdf = System.IO.Path.Combine(root, @"steamapps\libraryfolders.vdf");
                if (!File.Exists(vdf)) return libs;
                LKv kv = LKv.Parse(File.ReadAllText(vdf));
                LKv folders = kv.Child("libraryfolders");
                if (folders == null) folders = kv.Child("LibraryFolders");
                if (folders == null || folders.Kids == null) return libs;
                for (int i = 0; i < folders.Kids.Count; i++)
                {
                    LKv e = folders.Kids[i];
                    string p = null;
                    if (e.Val != null)
                    {
                        int dummy;
                        if (int.TryParse(e.Key, out dummy)) p = e.Val;
                    }
                    else p = e.Str("path", null);
                    if (string.IsNullOrEmpty(p)) continue;
                    string full;
                    try { full = System.IO.Path.GetFullPath(p); }
                    catch { continue; }
                    if (!Directory.Exists(full)) continue;
                    bool dup = false;
                    for (int n = 0; n < libs.Count; n++)
                        if (string.Equals(libs[n], full, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                    if (!dup) libs.Add(full);
                }
            }
            catch { }
            return libs;
        }

        // appid -> app, plus installdir -> app, for every manifest in every library.
        public static Dictionary<string, WSteamApp> Index()
        {
            Dictionary<string, WSteamApp> byDir =
                new Dictionary<string, WSteamApp>(StringComparer.OrdinalIgnoreCase);
            List<string> libs = Libraries();
            for (int li = 0; li < libs.Count; li++)
            {
                string apps = System.IO.Path.Combine(libs[li], "steamapps");
                string[] acfs;
                try { acfs = Directory.GetFiles(apps, "appmanifest_*.acf"); }
                catch { continue; }
                for (int i = 0; i < acfs.Length; i++)
                {
                    WSteamApp a = ReadManifest(acfs[i], apps);
                    if (a == null) continue;
                    byDir[a.InstallDir.TrimEnd('\\')] = a;
                }
            }
            return byDir;
        }

        public static WSteamApp ReadManifest(string acf, string steamappsDir)
        {
            try
            {
                LKv kv = LKv.Parse(File.ReadAllText(acf));
                LKv st = kv.Child("AppState");
                if (st == null) return null;
                string appid = st.Str("appid", null);
                string installdir = st.Str("installdir", null);
                if (string.IsNullOrEmpty(appid) || string.IsNullOrEmpty(installdir)) return null;

                WSteamApp a = new WSteamApp();
                a.AppId = appid;
                a.Name = st.Str("name", installdir);
                a.Flags = st.Num("StateFlags", 0);
                a.BytesDownloaded = st.Num("BytesDownloaded", -1);
                a.BytesToDownload = st.Num("BytesToDownload", -1);
                a.InstallDir = System.IO.Path.Combine(steamappsDir, "common", installdir);
                return a;
            }
            catch { return null; }
        }

        // Is this path inside a Steam app that is NOT fully installed?
        // Returns the offending app so the caller can say why it dropped something.
        public static WSteamApp NotInstalledOwnerOf(string path, Dictionary<string, WSteamApp> index)
        {
            if (string.IsNullOrEmpty(path) || index == null) return null;
            string p;
            try { p = path.TrimEnd('\\').ToLowerInvariant(); }
            catch { return null; }

            foreach (KeyValuePair<string, WSteamApp> kv in index)
            {
                if (kv.Value.Installed) continue;
                string root = kv.Key.ToLowerInvariant();
                if (p == root || p.StartsWith(root + "\\", StringComparison.Ordinal))
                    return kv.Value;
            }
            return null;
        }
    }

    // Applies WSteam's rule to a finished entry list, whatever produced it.
    public static class WGuard
    {
        public sealed class Drop
        {
            public string Id;
            public string Title;
            public string Source;
            public string Reason;
            public string AppId;
            public long Flags;
        }

        // Removes entries that live inside a not-fully-installed Steam app. Returns what it
        // dropped so lib.scan / lib.changed can report it instead of silently disagreeing with
        // the user about what is on their disk.
        public static List<Drop> Apply(List<LibEntry> entries)
        {
            List<Drop> dropped = new List<Drop>();
            if (entries == null || entries.Count == 0) return dropped;

            Dictionary<string, WSteamApp> index;
            try { index = WSteam.Index(); }
            catch { return dropped; }
            if (index.Count == 0) return dropped;

            bool any = false;
            foreach (KeyValuePair<string, WSteamApp> kv in index)
                if (!kv.Value.Installed) { any = true; break; }
            if (!any) return dropped;

            for (int i = entries.Count - 1; i >= 0; i--)
            {
                LibEntry e = entries[i];
                if (e == null) continue;
                // A steam: entry has already been through the rule in Sources.Steam().
                if (string.Equals(e.Source, "steam", StringComparison.OrdinalIgnoreCase)) continue;

                WSteamApp bad = WSteam.NotInstalledOwnerOf(e.InstallDir, index);
                if (bad == null) bad = WSteam.NotInstalledOwnerOf(e.LaunchTarget, index);
                if (bad == null) continue;

                Drop d = new Drop();
                d.Id = e.Id;
                d.Title = e.Title;
                d.Source = e.Source;
                d.AppId = bad.AppId;
                d.Flags = bad.Flags;
                d.Reason = (bad.Flags & WSteam.StateUpdateStarted) != 0
                    ? "Steam app " + bad.AppId + " is still downloading (StateFlags " + bad.Flags + ")."
                    : "Steam app " + bad.AppId + " is not fully installed (StateFlags " + bad.Flags + ").";
                dropped.Add(d);
                entries.RemoveAt(i);
            }
            return dropped;
        }

        public static LJ DropsToJson(List<Drop> drops)
        {
            LJ arr = LJ.Arr();
            if (drops == null) return arr;
            for (int i = 0; i < drops.Count; i++)
            {
                LJ o = LJ.Obj();
                o.Set("id", drops[i].Id);
                o.Set("title", drops[i].Title);
                o.Set("source", drops[i].Source);
                o.Set("appId", drops[i].AppId);
                o.Set("stateFlags", drops[i].Flags);
                o.Set("reason", drops[i].Reason);
                arr.Add(o);
            }
            return arr;
        }
    }

    #endregion

    #region Watching

    // FileSystemWatchers over the places an install actually shows up, plus a drive poller,
    // all funnelling into ONE debounced rescan.
    //
    // What is watched:
    //   * every Steam library's steamapps directory, filter appmanifest_*.acf, NOT recursive.
    //     A manifest appearing, being rewritten forty times during the download, and finally
    //     settling on StateFlags 4 is the entire install lifecycle, and it is all in that one
    //     directory. Watching steamapps\common instead would mean watching every file of a
    //     70 GB download land.
    //   * Epic's ProgramData manifests folder, filter *.item.
    //   * every enabled configured folder from WCfg (recursive only if that folder says so).
    //   * drive arrival/removal, by polling GetLogicalDrives-equivalent every 1.5 s, because a
    //     Steam library on an external disk appears without any file event we could have
    //     subscribed to. Same interval and same reasoning as FileApi's WatchMod.
    //
    // Debounce: every raw event sets a deadline of now + debounceMs. The worker sleeps in short
    // slices and only rescans once the deadline has passed with no further events. Steam's
    // manifest churn therefore costs exactly one rescan, a few seconds after the download ends.
    internal static class WWatch
    {
        sealed class Sub
        {
            public string Id;
            public string Path;
            public string What;          // steam | epic | folder | drives
            public FileSystemWatcher Fsw;
            public Thread Poller;
            public volatile bool Stop;
            public string[] LastDrives;
        }

        static readonly object Gate = new object();
        static readonly Dictionary<string, Sub> Subs = new Dictionary<string, Sub>(StringComparer.OrdinalIgnoreCase);
        static readonly List<LJ> Events = new List<LJ>();
        static long _seq;
        static int _sid;
        const int MaxEvents = 512;

        static Thread _worker;
        static volatile bool _workerStop;
        static DateTime _due = DateTime.MaxValue;
        static string _reason;
        static int _rescans;
        static Dictionary<string, string> _snapshot;   // id -> title, from the last rescan

        // -------------------------------------------------------------------------------------
        // Event log, same cursor shape as fs.events
        // -------------------------------------------------------------------------------------
        static void Push(string kind, string path, string detail, LJ extra)
        {
            lock (Gate)
            {
                _seq++;
                LJ e = LJ.Obj();
                e.Set("seq", _seq);
                e.Set("kind", kind);
                if (path != null) e.Set("path", path);
                if (detail != null) e.Set("detail", detail);
                if (extra != null) e.Set("data", extra);
                e.Set("atUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                Events.Add(e);
                while (Events.Count > MaxEvents) Events.RemoveAt(0);
            }
        }

        // -------------------------------------------------------------------------------------
        // Debounce
        // -------------------------------------------------------------------------------------
        static void Poke(string why)
        {
            lock (Gate)
            {
                _due = DateTime.UtcNow.AddMilliseconds(WCfg.DebounceMs());
                if (_reason == null) _reason = why;
                EnsureWorkerLocked();
            }
        }

        static void EnsureWorkerLocked()
        {
            if (_worker != null) return;
            _workerStop = false;
            _worker = new Thread(WorkerLoop);
            _worker.IsBackground = true;
            _worker.Name = "arc-libwatch-debounce";
            _worker.SetApartmentState(ApartmentState.MTA);
            _worker.Start();
        }

        static void WorkerLoop()
        {
            while (!_workerStop)
            {
                string why = null;
                bool fire = false;
                lock (Gate)
                {
                    if (_due != DateTime.MaxValue && DateTime.UtcNow >= _due)
                    {
                        fire = true;
                        why = _reason;
                        _due = DateTime.MaxValue;
                        _reason = null;
                    }
                }
                if (fire)
                {
                    try { Rescan(why); }
                    catch (Exception ex) { Push("watch.error", null, LApi.Describe(ex), null); }
                }
                Thread.Sleep(250);
            }
        }

        // -------------------------------------------------------------------------------------
        // The rescan. Same scanner as lib.scan - there is only ever one.
        // -------------------------------------------------------------------------------------
        static void Rescan(string why)
        {
            LJobs.Job job = new LJobs.Job();
            job.Id = "libwatch";
            job.State = "running";

            List<string> roots = WCfg.ScanRoots(true);
            LJ doc = ScanMod.Run(job, false, 96, roots);
            _rescans++;

            // Diff against the previous rescan so the page is told what actually changed rather
            // than being handed the whole library and left to work it out.
            Dictionary<string, string> now = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            LJ arr = doc == null ? null : doc.Get("entries");
            if (arr != null && arr.Kind == LJ.TArr)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    LJ it = arr.At(i);
                    string id = it.S("id", null);
                    if (!string.IsNullOrEmpty(id)) now[id] = it.S("title", "");
                }
            }

            LJ added = LJ.Arr(), removed = LJ.Arr();
            if (_snapshot != null)
            {
                foreach (KeyValuePair<string, string> kv in now)
                    if (!_snapshot.ContainsKey(kv.Key))
                        added.Add(LJ.Obj().Set("id", kv.Key).Set("title", kv.Value));
                foreach (KeyValuePair<string, string> kv in _snapshot)
                    if (!now.ContainsKey(kv.Key))
                        removed.Add(LJ.Obj().Set("id", kv.Key).Set("title", kv.Value));
            }
            _snapshot = now;

            LJ data = LJ.Obj();
            data.Set("reason", why == null ? "change" : why);
            data.Set("total", now.Count);
            data.Set("added", added);
            data.Set("removed", removed);
            data.Set("addedCount", added.Count);
            data.Set("removedCount", removed.Count);
            data.Set("rescan", _rescans);
            Push("lib.changed", null, null, data);
        }

        // -------------------------------------------------------------------------------------
        // Wiring the watchers
        // -------------------------------------------------------------------------------------
        static Sub NewDirSub(string dir, string filter, bool recursive, string what)
        {
            Sub s = new Sub();
            _sid++;
            s.Id = "libwatch-" + _sid.ToString(CultureInfo.InvariantCulture);
            s.Path = dir;
            s.What = what;

            FileSystemWatcher f = new FileSystemWatcher(dir);
            if (!string.IsNullOrEmpty(filter)) f.Filter = filter;
            f.IncludeSubdirectories = recursive;
            f.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                             NotifyFilters.LastWrite | NotifyFilters.Size;
            string tag = what;
            f.Created += delegate (object o, FileSystemEventArgs e) { Raw(tag, "created", e.FullPath); };
            f.Deleted += delegate (object o, FileSystemEventArgs e) { Raw(tag, "deleted", e.FullPath); };
            f.Changed += delegate (object o, FileSystemEventArgs e) { Raw(tag, "changed", e.FullPath); };
            f.Renamed += delegate (object o, RenamedEventArgs e) { Raw(tag, "renamed", e.FullPath); };
            // A watcher whose directory is pulled out from under it goes deaf silently. Say so.
            f.Error += delegate (object o, ErrorEventArgs e)
            {
                Exception ex = e.GetException();
                Push("watch.error", dir, ex == null ? "unknown" : ex.Message, null);
            };
            f.InternalBufferSize = 64 * 1024;
            f.EnableRaisingEvents = true;
            s.Fsw = f;
            return s;
        }

        // Raw file events are NOT surfaced to the page - there are hundreds of them per download
        // and the page cannot do anything useful with any single one. They only move the
        // debounce deadline. The page hears "lib.changed", once.
        static void Raw(string what, string verb, string path)
        {
            string leaf = path;
            try { leaf = System.IO.Path.GetFileName(path); }
            catch { }
            Poke(what + ":" + verb + ":" + leaf);
        }

        public static LJ Start(LJ q)
        {
            bool wantSteam = q.B("steam", true);
            bool wantEpic = q.B("epic", true);
            bool wantFolders = q.B("folders", true);
            bool wantDrives = q.B("drives", true);

            List<Sub> made = new List<Sub>();
            LJ watching = LJ.Arr();
            LJ problems = LJ.Arr();

            if (wantSteam)
            {
                List<string> libs = WSteam.Libraries();
                for (int i = 0; i < libs.Count; i++)
                {
                    string apps = System.IO.Path.Combine(libs[i], "steamapps");
                    if (!WCfg.DirOk(apps)) continue;
                    try { made.Add(NewDirSub(apps, "appmanifest_*.acf", false, "steam")); }
                    catch (Exception ex) { problems.Add(LJ.Obj().Set("path", apps).Set("error", LApi.Describe(ex))); }
                }
            }

            if (wantEpic)
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    @"Epic\EpicGamesLauncher\Data\Manifests");
                if (WCfg.DirOk(dir))
                {
                    try { made.Add(NewDirSub(dir, "*.item", false, "epic")); }
                    catch (Exception ex) { problems.Add(LJ.Obj().Set("path", dir).Set("error", LApi.Describe(ex))); }
                }
            }

            if (wantFolders)
            {
                LJ f = WCfg.Load().Get("folders");
                if (f != null && f.Kind == LJ.TArr)
                {
                    for (int i = 0; i < f.Count; i++)
                    {
                        LJ it = f.At(i);
                        if (it == null || it.Kind != LJ.TObj || !it.B("enabled", true)) continue;
                        string p = it.S("path", null);
                        if (!WCfg.DirOk(p)) continue;
                        try { made.Add(NewDirSub(p, null, it.B("recurse", false), "folder")); }
                        catch (Exception ex) { problems.Add(LJ.Obj().Set("path", p).Set("error", LApi.Describe(ex))); }
                    }
                }
            }

            if (wantDrives)
            {
                Sub s = new Sub();
                _sid++;
                s.Id = "libwatch-" + _sid.ToString(CultureInfo.InvariantCulture);
                s.Path = "(drives)";
                s.What = "drives";
                s.LastDrives = SafeDrives();
                Sub self = s;
                s.Poller = new Thread(delegate ()
                {
                    while (!self.Stop)
                    {
                        try
                        {
                            string[] now = SafeDrives();
                            if (Differ(self.LastDrives, now))
                            {
                                self.LastDrives = now;
                                // A new volume can carry a whole Steam library, so this has to
                                // re-enumerate rather than just rescan.
                                Poke("drives:changed");
                            }
                        }
                        catch { /* a failed poll must never end the loop */ }
                        Thread.Sleep(1500);
                    }
                });
                s.Poller.IsBackground = true;
                s.Poller.Name = "arc-libwatch-drives";
                s.Poller.Start();
                made.Add(s);
            }

            lock (Gate)
            {
                for (int i = 0; i < made.Count; i++) Subs[made[i].Id] = made[i];
                SeedSnapshotLocked();
                EnsureWorkerLocked();
                foreach (KeyValuePair<string, Sub> kv in Subs)
                {
                    LJ w = LJ.Obj();
                    w.Set("watchId", kv.Value.Id);
                    w.Set("path", kv.Value.Path);
                    w.Set("what", kv.Value.What);
                    watching.Add(w);
                }
            }

            LJ o = LJ.Obj();
            o.Set("started", made.Count);
            o.Set("watching", watching);
            o.Set("debounceMs", WCfg.DebounceMs());
            if (problems.Count > 0) o.Set("problems", problems);
            o.Set("poll", "{\"cmd\":\"lib.changed\",\"since\":0}");
            return o;
        }

        // Without this the FIRST lib.changed after lib.watch has nothing to diff against and
        // reports an empty added/removed list - exactly the case that matters, because it is the
        // one where the user just installed something. Seeding from the cache the shell already
        // painted from means the first change is a real diff.
        static void SeedSnapshotLocked()
        {
            if (_snapshot != null) return;
            Dictionary<string, string> seed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                LJ doc = ScanMod.Cached();
                LJ arr = doc == null ? null : doc.Get("entries");
                if (arr != null && arr.Kind == LJ.TArr)
                    for (int i = 0; i < arr.Count; i++)
                    {
                        LJ it = arr.At(i);
                        string id = it.S("id", null);
                        if (!string.IsNullOrEmpty(id)) seed[id] = it.S("title", "");
                    }
            }
            catch { }
            // An empty cache stays null: seeding an EMPTY baseline would make the first rescan
            // announce the entire library as newly added.
            if (seed.Count > 0) _snapshot = seed;
        }

        static string[] SafeDrives()
        {
            List<string> outp = new List<string>();
            try
            {
                DriveInfo[] ds = DriveInfo.GetDrives();
                for (int i = 0; i < ds.Length; i++)
                {
                    try { if (ds[i].IsReady) outp.Add(ds[i].Name); }
                    catch { }
                }
            }
            catch { }
            return outp.ToArray();
        }

        static bool Differ(string[] a, string[] b)
        {
            if (a == null || b == null) return true;
            if (a.Length != b.Length) return true;
            for (int i = 0; i < a.Length; i++)
                if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static LJ Stop(LJ q)
        {
            string id = q.S("watchId", null);
            bool all = q.B("all", false) || string.IsNullOrEmpty(id);
            List<Sub> kill = new List<Sub>();
            lock (Gate)
            {
                if (all) { foreach (KeyValuePair<string, Sub> kv in Subs) kill.Add(kv.Value); Subs.Clear(); }
                else
                {
                    Sub s;
                    if (!Subs.TryGetValue(id, out s))
                        throw new LibFault("no_such_watcher", "There is no library watcher called '" + id + "'.");
                    kill.Add(s);
                    Subs.Remove(id);
                }
                if (Subs.Count == 0) { _workerStop = true; _worker = null; _due = DateTime.MaxValue; }
            }
            for (int i = 0; i < kill.Count; i++)
            {
                kill[i].Stop = true;
                try { if (kill[i].Fsw != null) { kill[i].Fsw.EnableRaisingEvents = false; kill[i].Fsw.Dispose(); } }
                catch { }
            }
            return LJ.Obj().Set("stopped", kill.Count);
        }

        public static LJ Poll(LJ q)
        {
            long since = q.L("since", 0);
            LJ arr = LJ.Arr();
            long high = since;
            lock (Gate)
            {
                for (int i = 0; i < Events.Count; i++)
                {
                    long s = (long)Events[i].N("seq", 0);
                    if (s <= since) continue;
                    arr.Add(Events[i]);
                    if (s > high) high = s;
                }
            }
            LJ o = LJ.Obj();
            o.Set("events", arr);
            o.Set("count", arr.Count);
            o.Set("since", since);
            o.Set("seq", high);
            lock (Gate)
            {
                LJ ws = LJ.Arr();
                foreach (KeyValuePair<string, Sub> kv in Subs)
                {
                    LJ w = LJ.Obj();
                    w.Set("watchId", kv.Value.Id);
                    w.Set("path", kv.Value.Path);
                    w.Set("what", kv.Value.What);
                    ws.Add(w);
                }
                o.Set("watchers", ws);
                o.Set("pending", _due != DateTime.MaxValue);
                o.Set("rescans", _rescans);
            }
            return o;
        }

        // Test seam: force the debounce to fire now instead of waiting. Used by the harness so a
        // verification run does not have to sleep for the debounce window.
        public static LJ Flush()
        {
            lock (Gate) { _due = DateTime.UtcNow.AddMilliseconds(-1); if (_reason == null) _reason = "manual"; EnsureWorkerLocked(); }
            return LJ.Obj().Set("flushed", true);
        }
    }

    #endregion

    #region Public entry point

    public static class LibWatchApi
    {
        public const string ApiVersion = "1.0";

        // The commands this file owns. LibraryApi.Dispatch forwards anything in this list;
        // see the integration snippet in the report.
        public static bool Owns(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            switch (cmd.Trim().ToLowerInvariant())
            {
                case "lib.watch":
                case "lib.unwatch":
                case "lib.changed":
                case "lib.flush":
                case "lib.installed":
                case "lib.config.get":
                case "lib.config.set":
                    return true;
            }
            return false;
        }

        // Returns LJ and may throw LibFault - designed to be called from inside
        // LibraryApi.Dispatch, which already knows how to turn both into the envelope.
        public static LJ Dispatch(string cmd, LJ q)
        {
            switch (cmd.Trim().ToLowerInvariant())
            {
                case "lib.watch":
                    return WWatch.Start(q);

                case "lib.unwatch":
                    return WWatch.Stop(q);

                case "lib.changed":
                    return WWatch.Poll(q);

                case "lib.flush":
                    return WWatch.Flush();

                // Diagnostic: the installed rule, applied to every Steam manifest on the box.
                // This is what proves the AV-Racer verdict on any machine.
                case "lib.installed":
                    {
                        LJ arr = LJ.Arr();
                        Dictionary<string, WSteamApp> idx = WSteam.Index();
                        foreach (KeyValuePair<string, WSteamApp> kv in idx)
                        {
                            WSteamApp a = kv.Value;
                            LJ o = LJ.Obj();
                            o.Set("appId", a.AppId);
                            o.Set("name", a.Name);
                            o.Set("stateFlags", a.Flags);
                            o.Set("installed", a.Installed);
                            o.Set("downloading", (a.Flags & WSteam.StateUpdateStarted) != 0);
                            o.Set("updatePending", (a.Flags & 2) != 0);
                            o.Set("installDir", a.InstallDir);
                            o.Set("dirExists", WCfg.DirOk(a.InstallDir));
                            o.Set("bytesDownloaded", a.BytesDownloaded);
                            o.Set("bytesToDownload", a.BytesToDownload);
                            arr.Add(o);
                        }
                        return LJ.Obj().Set("apps", arr).Set("count", arr.Count)
                                       .Set("libraries", Str(WSteam.Libraries()));
                    }

                case "lib.config.get":
                    {
                        LJ cfg = WCfg.Load();
                        LJ o = LJ.Obj();
                        o.Set("config", cfg);
                        o.Set("path", WCfg.Path_());
                        o.Set("knownSources", Str(new List<string>(WCfg.KnownSources)));
                        o.Set("scanRoots", Str(WCfg.ScanRoots(true)));
                        return o;
                    }

                case "lib.config.set":
                    {
                        LJ patch = q.Get("config");
                        if (patch == null || patch.Kind != LJ.TObj)
                        {
                            // Also accept the fields inline, which is what a Settings page will
                            // naturally send.
                            patch = LJ.Obj();
                            if (q.Get("folders") != null) patch.Set("folders", q.Get("folders"));
                            if (q.Get("disabledSources") != null) patch.Set("disabledSources", q.Get("disabledSources"));
                            if (q.Get("watch") != null) patch.Set("watch", q.Get("watch"));
                        }
                        LJ saved = WCfg.Save(patch);
                        LJ o = LJ.Obj();
                        o.Set("config", saved);
                        o.Set("path", WCfg.Path_());
                        o.Set("scanRoots", Str(WCfg.ScanRoots(true)));
                        return o;
                    }
            }
            throw new LibFault("unknown_command", "LibWatchApi does not handle '" + cmd + "'.");
        }

        static LJ Str(List<string> xs)
        {
            LJ a = LJ.Arr();
            if (xs != null) for (int i = 0; i < xs.Count; i++) a.Add(xs[i]);
            return a;
        }

        // Standalone boundary for the console harness. Never throws.
        public static string Handle(string commandJson)
        {
            string id = null, cmd = null;
            try
            {
                LJ req;
                try
                {
                    string t = commandJson == null ? "" : commandJson.Trim();
                    if (t.Length == 0) throw new FormatException("empty command");
                    if (t[0] != '{') { req = LJ.Obj(); req.Set("cmd", t); }
                    else req = LJ.Parse(t);
                }
                catch (Exception ex) { return Env(false, null, null, "bad_json", LApi.Describe(ex)); }

                id = req.S("reqId", null);
                cmd = req.S("cmd", null);
                if (string.IsNullOrEmpty(cmd)) return Env(false, id, null, "bad_request", "missing \"cmd\"");
                return Env(true, id, Dispatch(cmd, req), null, null);
            }
            catch (LibFault f) { return Env(false, id, null, f.Code, f.Message); }
            catch (Exception ex)
            {
                return Env(false, id, null, "internal",
                    "unhandled in '" + (cmd == null ? "?" : cmd) + "': " + LApi.Describe(ex));
            }
        }

        static string Env(bool ok, string id, LJ data, string error, string detail)
        {
            LJ o = LJ.Obj();
            o.Set("ok", ok);
            if (id != null) o.Set("reqId", id);
            if (ok) o.Set("data", data == null ? LJ.Obj() : data);
            else { o.Set("error", error == null ? "error" : error); o.Set("detail", detail == null ? "" : detail); }
            return o.ToJson();
        }
    }

    #endregion
}
