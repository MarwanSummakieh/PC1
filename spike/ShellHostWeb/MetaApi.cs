// ===========================================================================================
// MetaApi.cs - MarwanOS
//
// Game metadata and artwork. Pluggable provider, OFFLINE-FIRST, because the console has to work
// with the network unplugged.
//
//   meta.lookup  - description, genres, developer/publisher, release date
//   meta.art     - cover (portrait), hero (landscape), logo (transparent)
//   meta.cache   - what is cached, how big, and a way to clear it
//
// -------------------------------------------------------------------------------------------
// THE PROBLEM THIS SOLVES
// -------------------------------------------------------------------------------------------
// The rail was upscaling a 600x900 portrait to fill a 4K screen and it looked soft. It was worse
// than it looked: Steam's own local cache often stores the AUTO-DOWNSCALED capsule, so
// library_600x900.jpg is frequently 300x450 on disk, not 600x900. Measured on this laptop:
//
//     1005300\library_600x900.jpg   300 x 450     <- half size, despite the filename
//     1030300\...\library_capsule.jpg 300 x 450
//     1002300\library_600x900.jpg   600 x 900     <- full size. It varies per title.
//
// Stretching 300x450 across 3840 pixels is a 12.8x upscale. That is the softness.
//
// The fix is that Steam ALSO caches a real landscape hero locally, and the old scanner never
// looked for it - SteamArtNames listed only portrait/header names. Measured on this laptop:
//
//     1005300\library_hero.jpg               1920 x 620
//     1030300\<sha1>\library_hero.jpg        1920 x 620
//     1082430\library_hero.jpg               1920 x 620
//
// 1920x620 across a 3840-wide screen is a 2x upscale of an image already in the right aspect,
// versus 12.8x of one in the wrong aspect. That single change is most of the win, and it costs
// no network at all.
//
// For the rest, Valve publishes a NATIVE 3840x1240 hero on a keyless CDN as library_hero_2x.jpg.
// Steam never caches the _2x files locally (verified: zero on this machine), so that one is
// worth fetching in the background and caching ourselves.
//
// -------------------------------------------------------------------------------------------
// PROVIDER CHOICE
// -------------------------------------------------------------------------------------------
//   1. LOCAL      Steam's appcache\librarycache, then the executable's own icon. Free, instant,
//                 keyless, works offline. Always tried first.
//   2. STEAM CDN  shared.steamstatic.com/store_item_assets/steam/apps/<appid>/<file>
//                 A filename convention against a public CDN. No API, no key, no quota, and a
//                 404 is a plain 404 with no penalty, so speculative probing is safe.
//                 This is the ONLY way to reach hero art from Steam - appdetails does not
//                 return library_hero or library_600x900 in any field.
//   3. APPDETAILS store.steampowered.com/api/appdetails?appids=<id> - keyless, but rate limited
//                 (community figure ~200 requests / 5 minutes per IP; Valve publishes no number
//                 and sends no Retry-After, so back off blind). One appid per call for full
//                 details. Text only.
//   4. SGDB       SteamGridDB. REQUIRES A FREE API KEY, so it is OFF by default and off unless
//                 the user pastes a key into settings. Its real value is non-Steam titles -
//                 emulated games, GOG, Epic, itch - where the Steam CDN has nothing to offer.
//
// Rejected: IGDB (two-secret Twitch OAuth contradicts zero-config, and its art is screenshots
// and small covers - no hero); Wikidata (keyless, but Commons only hosts freely-licensed media
// so game key art is structurally absent).
//
// -------------------------------------------------------------------------------------------
// THE CACHE RULE
// -------------------------------------------------------------------------------------------
// A cache hit NEVER touches the network. Not to revalidate, not to send a conditional GET.
// The console must behave identically with the cable pulled, and store metadata changes far
// more slowly than any freshness window worth paying a round trip for.
//
// -------------------------------------------------------------------------------------------
// REFERENCES
// -------------------------------------------------------------------------------------------
// System.dll and System.Core.dll only. System.Net.HttpWebRequest lives in System.dll, so this
// adds no /reference to build.cmd. Reuses MarwanOs.Library's LJ, LApi, LJobs and LibFault
// rather than carrying a second JSON writer or job runner. Types are M-prefixed in namespace
// MarwanOs.Meta, so nothing collides.
// ===========================================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using MarwanOs.Library;

namespace MarwanOs.Meta
{
    #region Configuration

    // %LOCALAPPDATA%\ArcOS\meta-config.json
    // {
    //   "network":     { "enabled": true,  "timeoutMs": 6000, "minIntervalMs": 1000 },
    //   "steamGridDb": { "enabled": false, "apiKey": "" }
    // }
    //
    // network.enabled defaults TRUE because everything it reaches is keyless and free.
    // steamGridDb defaults OFF and stays off until the user supplies a key of their own.
    // A key is NEVER hardcoded here, and lib.config / meta.config report only whether one is
    // present, never its value.
    public static class MCfg
    {
        public const int DefaultTimeoutMs = 6000;
        public const int DefaultMinIntervalMs = 1000;

        static readonly object Gate = new object();
        static LJ _cached;

        public static string Root()
        {
            string b = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            // "ArcOS" deliberately - matches CacheMod.Dir() and WCfg.Dir(). The product is
            // MarwanOS; the folder on disk is not, and renaming it orphans every cache already
            // deployed on every machine.
            return Path.Combine(b, "ArcOS");
        }

        public static string CacheDir() { return Path.Combine(Root(), "meta"); }
        public static string Path_() { return Path.Combine(Root(), "meta-config.json"); }

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
                if (doc == null || doc.Kind != LJ.TObj) doc = LJ.Obj();
                _cached = Normalize(doc);
                return _cached;
            }
        }

        static LJ Normalize(LJ raw)
        {
            LJ net = raw.Get("network");
            int timeout = DefaultTimeoutMs, interval = DefaultMinIntervalMs;
            bool netOn = true;
            if (net != null && net.Kind == LJ.TObj)
            {
                netOn = net.B("enabled", true);
                timeout = net.I("timeoutMs", DefaultTimeoutMs);
                interval = net.I("minIntervalMs", DefaultMinIntervalMs);
            }
            if (timeout < 1000) timeout = 1000;
            if (timeout > 30000) timeout = 30000;
            if (interval < 250) interval = 250;
            if (interval > 60000) interval = 60000;

            LJ sg = raw.Get("steamGridDb");
            bool sgOn = false; string key = "";
            if (sg != null && sg.Kind == LJ.TObj)
            {
                sgOn = sg.B("enabled", false);
                key = sg.S("apiKey", "");
                if (key == null) key = "";
                key = key.Trim();
            }
            if (key.Length == 0) sgOn = false;      // enabled without a key is just off

            LJ o = LJ.Obj();
            LJ n = LJ.Obj();
            n.Set("enabled", netOn);
            n.Set("timeoutMs", timeout);
            n.Set("minIntervalMs", interval);
            o.Set("network", n);
            LJ s = LJ.Obj();
            s.Set("enabled", sgOn);
            s.Set("apiKey", key);
            o.Set("steamGridDb", s);
            return o;
        }

        public static LJ Save(LJ patch)
        {
            lock (Gate)
            {
                LJ cur = _cached != null ? _cached : Load();
                LJ merged = LJ.Obj();
                merged.Set("network", patch != null && patch.Get("network") != null
                    ? patch.Get("network") : cur.Get("network"));
                merged.Set("steamGridDb", patch != null && patch.Get("steamGridDb") != null
                    ? patch.Get("steamGridDb") : cur.Get("steamGridDb"));
                LJ norm = Normalize(merged);
                _cached = norm;
                try
                {
                    string dir = Root();
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    string tmp = Path_() + ".tmp";
                    File.WriteAllText(tmp, norm.ToJson(), new UTF8Encoding(false));
                    string final = Path_();
                    if (File.Exists(final)) File.Delete(final);
                    File.Move(tmp, final);
                }
                catch { }
                return norm;
            }
        }

        // What the UI is allowed to see: never the key itself.
        public static LJ Redacted()
        {
            LJ c = Load();
            LJ o = LJ.Obj();
            o.Set("network", c.Get("network"));
            LJ sg = c.Get("steamGridDb");
            LJ s = LJ.Obj();
            s.Set("enabled", sg != null && sg.B("enabled", false));
            s.Set("hasApiKey", sg != null && sg.S("apiKey", "").Length > 0);
            o.Set("steamGridDb", s);
            return o;
        }

        public static bool NetworkOn()
        {
            try { LJ n = Load().Get("network"); return n != null && n.B("enabled", true); }
            catch { return false; }
        }

        public static int TimeoutMs()
        {
            try { LJ n = Load().Get("network"); return n == null ? DefaultTimeoutMs : n.I("timeoutMs", DefaultTimeoutMs); }
            catch { return DefaultTimeoutMs; }
        }

        public static int MinIntervalMs()
        {
            try { LJ n = Load().Get("network"); return n == null ? DefaultMinIntervalMs : n.I("minIntervalMs", DefaultMinIntervalMs); }
            catch { return DefaultMinIntervalMs; }
        }

        public static void Forget() { lock (Gate) { _cached = null; } }
    }

    #endregion

    #region Local Steam art

    // Steam's librarycache, with all THREE layouts that occur simultaneously on one machine
    // because Steam never migrates what it has already written:
    //
    //   flat        appcache\librarycache\<appid>_library_600x900.jpg          (pre-2023)
    //   per-appid   appcache\librarycache\<appid>\library_hero.jpg             (2023-ish)
    //   hashed      appcache\librarycache\<appid>\<sha1>\library_hero.jpg      (current)
    //
    // The hashed layout puts DIFFERENT assets under DIFFERENT hashes, verified on this laptop
    // for Palworld (1623730):
    //     2d3bef9d...\library_hero.jpg
    //     6912f19c...\library_header.jpg
    //     f85c38b4...\library_capsule.jpg
    // so it is not enough to find "the" hash directory and look inside it - every subdirectory
    // has to be checked for every candidate filename.
    public static class MSteamArt
    {
        // Candidate filenames per art kind, best first.
        public static string[] Names(string kind)
        {
            switch ((kind == null ? "" : kind).ToLowerInvariant())
            {
                case "hero":
                    // library_hero.jpg is the landscape key art, typically 1920x620 on disk.
                    return new string[] { "library_hero.jpg", "library_hero_2x.jpg" };
                case "logo":
                    // Note: library_logo.png does NOT exist in Steam's naming - the file is
                    // logo.png. It is kept last only to tolerate hand-placed art.
                    return new string[] { "logo.png", "logo_2x.png", "library_logo.png" };
                case "header":
                    return new string[] { "library_header.jpg", "header.jpg" };
                case "cover":
                default:
                    return new string[] { "library_600x900.jpg", "library_600x900_2x.jpg",
                                          "library_capsule.jpg" };
            }
        }

        public static string SteamRoot()
        {
            try { return Sources.SteamInstallPath(); }
            catch { return null; }
        }

        // Find one art kind for one appid, across all three layouts. Returns null, never throws.
        public static string Find(string steamRoot, string appid, string kind)
        {
            if (string.IsNullOrEmpty(steamRoot) || string.IsNullOrEmpty(appid)) return null;
            string[] names = Names(kind);
            try
            {
                string cacheRoot = Path.Combine(steamRoot, @"appcache\librarycache");
                if (!Directory.Exists(cacheRoot)) return null;

                string dir = Path.Combine(cacheRoot, appid);
                if (Directory.Exists(dir))
                {
                    // per-appid, directly
                    for (int i = 0; i < names.Length; i++)
                    {
                        string p = Path.Combine(dir, names[i]);
                        if (File.Exists(p)) return p;
                    }
                    // hashed: every subdirectory, every name. Name-major so a better filename in
                    // a later directory still beats a worse one in an earlier directory.
                    string[] subs;
                    try { subs = Directory.GetDirectories(dir); }
                    catch { subs = new string[0]; }
                    for (int i = 0; i < names.Length; i++)
                        for (int s = 0; s < subs.Length; s++)
                        {
                            string p = Path.Combine(subs[s], names[i]);
                            if (File.Exists(p)) return p;
                        }
                }

                // flat
                for (int i = 0; i < names.Length; i++)
                {
                    string p = Path.Combine(cacheRoot, appid + "_" + names[i]);
                    if (File.Exists(p)) return p;
                }
            }
            catch { }
            return null;
        }

        // Everything we can find locally for one appid.
        public static LJ All(string appid)
        {
            string root = SteamRoot();
            LJ o = LJ.Obj();
            o.Set("cover", Find(root, appid, "cover"));
            o.Set("hero", Find(root, appid, "hero"));
            o.Set("logo", Find(root, appid, "logo"));
            o.Set("header", Find(root, appid, "header"));
            return o;
        }
    }

    #endregion

    #region Remote providers

    // Keyless CDN filename convention. No API, no key, no quota.
    public static class MSteamCdn
    {
        public const string Host = "https://shared.steamstatic.com/store_item_assets/steam/apps/";

        // Best first. The _2x files are the ORIGINALS Valve authored; the unsuffixed ones are
        // auto-generated half-size downscales. _2x is missing on pre-2015 titles, so the
        // fallback is mandatory rather than polish.
        public static string[] Files(string kind)
        {
            switch ((kind == null ? "" : kind).ToLowerInvariant())
            {
                case "hero":
                    return new string[] { "library_hero_2x.jpg", "library_hero.jpg", "capsule_616x353.jpg" };
                case "logo":
                    return new string[] { "logo_2x.png", "logo.png" };
                case "header":
                    return new string[] { "header.jpg" };
                case "cover":
                default:
                    return new string[] { "library_600x900_2x.jpg", "library_600x900.jpg" };
            }
        }

        public static string Url(string appid, string file)
        {
            return Host + appid + "/" + file;
        }
    }

    // A blind, conservative throttle. Valve publishes no rate-limit number and sends no
    // Retry-After header, so this paces itself rather than reacting: one request per
    // minIntervalMs, and a hard cooldown whenever a 429 is seen.
    internal static class MGate
    {
        static readonly object Gate = new object();
        static DateTime _next = DateTime.MinValue;
        static DateTime _cooldownUntil = DateTime.MinValue;
        static int _sent, _throttled;

        public static bool InCooldown
        {
            get { lock (Gate) { return DateTime.UtcNow < _cooldownUntil; } }
        }

        public static bool Acquire(int waitMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
            while (true)
            {
                TimeSpan sleep;
                lock (Gate)
                {
                    DateTime now = DateTime.UtcNow;
                    if (now < _cooldownUntil) return false;
                    if (now >= _next)
                    {
                        _next = now.AddMilliseconds(MCfg.MinIntervalMs());
                        _sent++;
                        return true;
                    }
                    sleep = _next - now;
                }
                if (DateTime.UtcNow.Add(sleep) > deadline) return false;
                Thread.Sleep(sleep < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : sleep);
            }
        }

        public static void Penalize()
        {
            lock (Gate)
            {
                _throttled++;
                // Valve's throttle is measured in minutes. Two is a guess on the safe side.
                _cooldownUntil = DateTime.UtcNow.AddMinutes(2);
            }
        }

        public static LJ Stats()
        {
            lock (Gate)
            {
                LJ o = LJ.Obj();
                o.Set("requests", _sent);
                o.Set("throttled", _throttled);
                o.Set("inCooldown", DateTime.UtcNow < _cooldownUntil);
                if (DateTime.UtcNow < _cooldownUntil)
                    o.Set("cooldownUntilUtc", _cooldownUntil.ToString("o", CultureInfo.InvariantCulture));
                return o;
            }
        }
    }

    internal static class MHttp
    {
        static bool _tlsSet;

        static void EnsureTls()
        {
            if (_tlsSet) return;
            try
            {
                // FX 4.8 defaults to the OS setting, but an old machine policy can still leave
                // SSL3/TLS1.0 selected, and steamstatic refuses those.
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // Tls12
                ServicePointManager.DefaultConnectionLimit = 8;
            }
            catch { }
            _tlsSet = true;
        }

        public sealed class Result
        {
            public bool Ok;
            public int Status;
            public byte[] Body;
            public string Error;
            public string ContentType;
        }

        // Never throws. A dead network, a DNS failure, a 404 and a timeout are all just
        // "Ok=false" - the caller degrades to local art.
        public static Result Get(string url, string bearer, bool head)
        {
            Result r = new Result();
            if (!MCfg.NetworkOn()) { r.Error = "network disabled in meta-config.json"; return r; }
            if (!MGate.Acquire(3000)) { r.Error = MGate.InCooldown ? "rate-limit cooldown" : "throttle wait exceeded"; return r; }

            EnsureTls();
            HttpWebResponse resp = null;
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = head ? "HEAD" : "GET";
                req.Timeout = MCfg.TimeoutMs();
                req.ReadWriteTimeout = MCfg.TimeoutMs();
                req.AllowAutoRedirect = true;
                req.KeepAlive = false;
                req.UserAgent = "MarwanOS-Shell/1.0 (+console shell; contact via device owner)";
                req.Accept = head ? "*/*" : "application/json,image/*;q=0.9,*/*;q=0.8";
                if (!string.IsNullOrEmpty(bearer)) req.Headers["Authorization"] = "Bearer " + bearer;

                resp = (HttpWebResponse)req.GetResponse();
                r.Status = (int)resp.StatusCode;
                r.ContentType = resp.ContentType;
                if (!head)
                {
                    using (Stream s = resp.GetResponseStream())
                    using (MemoryStream ms = new MemoryStream())
                    {
                        byte[] buf = new byte[16384];
                        int n;
                        int total = 0;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0)
                        {
                            total += n;
                            // 24 MB ceiling. Nothing legitimate here is close to that, and an
                            // unbounded read on a shell's background thread is a liability.
                            if (total > 24 * 1024 * 1024) { r.Error = "response too large"; return r; }
                            ms.Write(buf, 0, n);
                        }
                        r.Body = ms.ToArray();
                    }
                }
                r.Ok = r.Status >= 200 && r.Status < 300;
                return r;
            }
            catch (WebException wex)
            {
                try
                {
                    HttpWebResponse er = wex.Response as HttpWebResponse;
                    if (er != null)
                    {
                        r.Status = (int)er.StatusCode;
                        if (r.Status == 429) MGate.Penalize();
                    }
                }
                catch { }
                r.Error = wex.Message;
                return r;
            }
            catch (Exception ex) { r.Error = ex.Message; return r; }
            finally { try { if (resp != null) resp.Close(); } catch { } }
        }
    }

    #endregion

    #region Disk cache

    // %LOCALAPPDATA%\ArcOS\meta\steam\<appid>\{meta.json,hero.jpg,cover.jpg,logo.png}
    public static class MCache
    {
        public static string DirFor(string ns, string key)
        {
            string safe = Safe(key);
            return Path.Combine(Path.Combine(MCfg.CacheDir(), Safe(ns)), safe);
        }

        static string Safe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "_";
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
                    c == '-' || c == '_' || c == '.') sb.Append(c);
                else sb.Append('_');
            }
            string r = sb.ToString();
            if (r.Length > 64) r = r.Substring(0, 64);
            return r.Length == 0 ? "_" : r;
        }

        public static LJ LoadJson(string ns, string key, string file)
        {
            try
            {
                string p = Path.Combine(DirFor(ns, key), file);
                if (!File.Exists(p)) return null;
                return LJ.Parse(File.ReadAllText(p));
            }
            catch { return null; }
        }

        public static void SaveJson(string ns, string key, string file, LJ doc)
        {
            try
            {
                string dir = DirFor(ns, key);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string p = Path.Combine(dir, file);
                string tmp = p + ".tmp";
                File.WriteAllText(tmp, doc.ToJson(), new UTF8Encoding(false));
                if (File.Exists(p)) File.Delete(p);
                File.Move(tmp, p);
            }
            catch { }
        }

        public static string FindFile(string ns, string key, string baseName)
        {
            try
            {
                string dir = DirFor(ns, key);
                if (!Directory.Exists(dir)) return null;
                string[] exts = new string[] { ".jpg", ".png", ".jpeg" };
                for (int i = 0; i < exts.Length; i++)
                {
                    string p = Path.Combine(dir, baseName + exts[i]);
                    if (File.Exists(p)) return p;
                }
            }
            catch { }
            return null;
        }

        public static string SaveBytes(string ns, string key, string baseName, byte[] body, string contentType)
        {
            try
            {
                if (body == null || body.Length == 0) return null;
                string ext = ".jpg";
                if (contentType != null && contentType.IndexOf("png", StringComparison.OrdinalIgnoreCase) >= 0) ext = ".png";
                else if (body.Length > 8 && body[0] == 0x89 && body[1] == 0x50) ext = ".png";

                string dir = DirFor(ns, key);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string p = Path.Combine(dir, baseName + ext);
                string tmp = p + ".tmp";
                File.WriteAllBytes(tmp, body);
                if (File.Exists(p)) File.Delete(p);
                File.Move(tmp, p);
                return p;
            }
            catch { return null; }
        }

        public static LJ Stats()
        {
            LJ o = LJ.Obj();
            string root = MCfg.CacheDir();
            o.Set("path", root);
            long files = 0, bytes = 0, titles = 0;
            try
            {
                if (Directory.Exists(root))
                {
                    string[] all = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
                    files = all.Length;
                    for (int i = 0; i < all.Length; i++)
                    {
                        try { bytes += new FileInfo(all[i]).Length; }
                        catch { }
                    }
                    string steam = Path.Combine(root, "steam");
                    if (Directory.Exists(steam)) titles = Directory.GetDirectories(steam).Length;
                }
            }
            catch { }
            o.Set("exists", Directory.Exists(root));
            o.Set("files", files);
            o.Set("bytes", bytes);
            o.Set("titles", titles);
            return o;
        }

        public static int Clear()
        {
            int n = 0;
            try
            {
                string root = MCfg.CacheDir();
                if (!Directory.Exists(root)) return 0;
                string[] dirs = Directory.GetDirectories(root);
                for (int i = 0; i < dirs.Length; i++)
                {
                    try { Directory.Delete(dirs[i], true); n++; }
                    catch { }
                }
            }
            catch { }
            return n;
        }
    }

    #endregion

    #region The provider chain

    public static class MProvider
    {
        // ---------------------------------------------------------------------------------
        // ART
        // ---------------------------------------------------------------------------------
        // Order, and it matters:
        //   1. our own cache        - a hit here NEVER touches the network
        //   2. Steam's librarycache - free, instant, offline, already on disk
        //   3. Steam's CDN          - keyless, background, upgrades hero to 3840x1240
        // Steps 1 and 2 are synchronous and safe to call on any thread. Step 3 only runs when
        // the caller passes allowNetwork, which meta.art does not do by default.
        public static LJ Art(string appid, string kind, bool allowNetwork)
        {
            LJ o = LJ.Obj();
            o.Set("appId", appid);
            o.Set("kind", kind);

            string cached = MCache.FindFile("steam", appid, kind);
            if (cached != null)
            {
                o.Set("path", cached);
                o.Set("origin", "cache");
                o.Set("network", false);
                return o;
            }

            string local = MSteamArt.Find(MSteamArt.SteamRoot(), appid, kind);
            if (local != null)
            {
                o.Set("path", local);
                o.Set("origin", "steam-local");
                o.Set("network", false);
                // Tell the caller an upgrade exists, without going and getting it here.
                o.Set("upgradable", kind == "hero" || kind == "cover" || kind == "logo");
                return o;
            }

            if (!allowNetwork || !MCfg.NetworkOn())
            {
                o.SetNull("path");
                o.Set("origin", "none");
                o.Set("network", false);
                o.Set("note", "No local art for appid " + appid + ".");
                return o;
            }

            LJ fetched = FetchArt(appid, kind);
            if (fetched != null) return fetched;

            o.SetNull("path");
            o.Set("origin", "none");
            o.Set("network", true);
            return o;
        }

        // Walks the CDN candidate list until one is not a 404. Caches whatever it gets.
        public static LJ FetchArt(string appid, string kind)
        {
            string[] files = MSteamCdn.Files(kind);
            for (int i = 0; i < files.Length; i++)
            {
                string url = MSteamCdn.Url(appid, files[i]);
                MHttp.Result r = MHttp.Get(url, null, false);
                if (!r.Ok || r.Body == null || r.Body.Length == 0) continue;

                string saved = MCache.SaveBytes("steam", appid, kind, r.Body, r.ContentType);
                if (saved == null) continue;

                LJ o = LJ.Obj();
                o.Set("appId", appid);
                o.Set("kind", kind);
                o.Set("path", saved);
                o.Set("origin", "steam-cdn");
                o.Set("network", true);
                o.Set("url", url);
                o.Set("file", files[i]);
                o.Set("bytes", r.Body.Length);
                return o;
            }
            return null;
        }

        // ---------------------------------------------------------------------------------
        // METADATA
        // ---------------------------------------------------------------------------------
        public static LJ Lookup(string appid, string title, bool allowNetwork)
        {
            if (!string.IsNullOrEmpty(appid))
            {
                LJ hit = MCache.LoadJson("steam", appid, "meta.json");
                if (hit != null)
                {
                    hit.Put("origin", "cache");
                    return hit;
                }
            }

            if (string.IsNullOrEmpty(appid) || !allowNetwork || !MCfg.NetworkOn())
            {
                LJ o = LJ.Obj();
                o.Set("appId", appid);
                o.Set("title", title);
                o.Set("origin", "none");
                o.Set("note", string.IsNullOrEmpty(appid)
                    ? "No store id; only local art is available for this title."
                    : "Not cached and network lookup was not requested.");
                return o;
            }

            return FetchMeta(appid, title);
        }

        // store.steampowered.com/api/appdetails - keyless. filters=basic keeps the response
        // around 6 KB instead of 16 KB. Only ONE appid per call: passing several without a
        // filter returns null, which is a Valve quirk rather than an error on our side.
        public static LJ FetchMeta(string appid, string title)
        {
            string url = "https://store.steampowered.com/api/appdetails?appids=" + appid +
                         "&l=english&cc=us";
            MHttp.Result r = MHttp.Get(url, null, false);
            if (!r.Ok || r.Body == null)
            {
                LJ o = LJ.Obj();
                o.Set("appId", appid);
                o.Set("title", title);
                o.Set("origin", "none");
                o.Set("error", r.Error == null ? ("http " + r.Status) : r.Error);
                return o;
            }

            LJ parsed;
            try { parsed = LJ.Parse(Encoding.UTF8.GetString(r.Body)); }
            catch (Exception ex)
            {
                LJ o = LJ.Obj();
                o.Set("appId", appid);
                o.Set("origin", "none");
                o.Set("error", "unparsable appdetails: " + ex.Message);
                return o;
            }

            // {"570":{"success":true,"data":{...}}} - and a missing app is success:false at
            // HTTP 200 with no data key at all, so both have to be guarded.
            LJ wrap = parsed.Get(appid);
            if (wrap == null || wrap.Kind != LJ.TObj || !wrap.B("success", false))
            {
                LJ o = LJ.Obj();
                o.Set("appId", appid);
                o.Set("title", title);
                o.Set("origin", "none");
                o.Set("error", "appdetails reported no data for " + appid);
                return o;
            }
            LJ d = wrap.Get("data");
            if (d == null || d.Kind != LJ.TObj)
            {
                LJ o = LJ.Obj();
                o.Set("appId", appid);
                o.Set("origin", "none");
                o.Set("error", "appdetails success with no data object");
                return o;
            }

            LJ m = LJ.Obj();
            m.Set("appId", appid);
            m.Set("title", d.S("name", title));
            m.Set("type", d.S("type", null));
            m.Set("description", d.S("short_description", null));
            m.Set("headerImage", d.S("header_image", null));
            m.Set("website", d.S("website", null));
            m.Set("genres", NamesOf(d.Get("genres"), "description"));
            m.Set("categories", NamesOf(d.Get("categories"), "description"));
            m.Set("developers", Flat(d.Get("developers")));
            m.Set("publishers", Flat(d.Get("publishers")));

            LJ rd = d.Get("release_date");
            if (rd != null && rd.Kind == LJ.TObj)
            {
                m.Set("releaseDate", rd.S("date", null));
                m.Set("comingSoon", rd.B("coming_soon", false));
            }
            LJ mc = d.Get("metacritic");
            if (mc != null && mc.Kind == LJ.TObj) m.Set("metacritic", mc.I("score", 0));

            LJ shots = LJ.Arr();
            LJ ss = d.Get("screenshots");
            if (ss != null && ss.Kind == LJ.TArr)
                for (int i = 0; i < ss.Count && i < 8; i++)
                {
                    string u = ss.At(i).S("path_full", null);
                    if (!string.IsNullOrEmpty(u)) shots.Add(u);
                }
            m.Set("screenshots", shots);

            m.Set("fetchedUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            m.Set("provider", "steam-appdetails");

            MCache.SaveJson("steam", appid, "meta.json", m);
            m.Put("origin", "network");
            return m;
        }

        static LJ NamesOf(LJ arr, string field)
        {
            LJ o = LJ.Arr();
            if (arr == null || arr.Kind != LJ.TArr) return o;
            for (int i = 0; i < arr.Count; i++)
            {
                string s = arr.At(i).S(field, null);
                if (!string.IsNullOrEmpty(s)) o.Add(s);
            }
            return o;
        }

        static LJ Flat(LJ arr)
        {
            LJ o = LJ.Arr();
            if (arr == null || arr.Kind != LJ.TArr) return o;
            for (int i = 0; i < arr.Count; i++)
            {
                string s = arr.At(i).AsStr(null);
                if (!string.IsNullOrEmpty(s)) o.Add(s);
            }
            return o;
        }

        // appid out of a library entry id ("steam:570") or an explicit field.
        public static string AppIdOf(string id, string explicitId)
        {
            if (!string.IsNullOrEmpty(explicitId)) return explicitId.Trim();
            if (string.IsNullOrEmpty(id)) return null;
            if (id.StartsWith("steam:", StringComparison.OrdinalIgnoreCase))
                return id.Substring(6).Trim();
            return null;
        }
    }

    #endregion

    #region Public entry point

    public static class MetaApi
    {
        public const string ApiVersion = "1.0";

        public static bool Owns(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            switch (cmd.Trim().ToLowerInvariant())
            {
                case "meta.lookup":
                case "meta.art":
                case "meta.cache":
                case "meta.prefetch":
                case "meta.config.get":
                case "meta.config.set":
                    return true;
            }
            return false;
        }

        public static LJ Dispatch(string cmd, LJ q)
        {
            switch (cmd.Trim().ToLowerInvariant())
            {
                case "meta.lookup":
                    {
                        string appid = MProvider.AppIdOf(q.S("id", null), q.S("appId", null));
                        string title = q.S("title", null);
                        bool net = q.B("network", false);
                        return MProvider.Lookup(appid, title, net);
                    }

                case "meta.art":
                    {
                        string appid = MProvider.AppIdOf(q.S("id", null), q.S("appId", null));
                        string kind = q.S("kind", "cover");
                        bool net = q.B("network", false);
                        if (string.IsNullOrEmpty(appid))
                            throw new LibFault("bad_request", "meta.art needs appId, or id in the form \"steam:<appid>\".");
                        if (q.B("all", false))
                        {
                            LJ o = LJ.Obj();
                            o.Set("appId", appid);
                            string[] kinds = new string[] { "hero", "cover", "logo", "header" };
                            for (int i = 0; i < kinds.Length; i++)
                                o.Set(kinds[i], MProvider.Art(appid, kinds[i], net));
                            return o;
                        }
                        return MProvider.Art(appid, kind, net);
                    }

                // Background upgrade pass: fetch the 3840x1240 heroes for a list of appids.
                // Polled through LibraryApi's job.status, because it reuses LJobs.
                case "meta.prefetch":
                    {
                        LJ ids = q.Get("appIds");
                        List<string> want = new List<string>();
                        if (ids != null && ids.Kind == LJ.TArr)
                            for (int i = 0; i < ids.Count; i++)
                            {
                                string s = ids.At(i).AsStr(null);
                                if (!string.IsNullOrEmpty(s)) want.Add(s.Trim());
                            }
                        if (want.Count == 0)
                            throw new LibFault("bad_request", "meta.prefetch needs a non-empty appIds array.");
                        string kind = q.S("kind", "hero");
                        bool meta = q.B("meta", false);
                        List<string> w = want;
                        string k = kind;
                        bool wantMeta = meta;

                        LJobs.Job job = LJobs.Start("meta.prefetch", delegate (LJobs.Job j)
                        {
                            int got = 0, skipped = 0, failed = 0;
                            for (int i = 0; i < w.Count; i++)
                            {
                                if (j.CancelRequested) break;
                                j.Report((int)(100.0 * i / w.Count), "meta " + w[i]);
                                try
                                {
                                    if (MCache.FindFile("steam", w[i], k) != null) { skipped++; }
                                    else if (MProvider.FetchArt(w[i], k) != null) got++;
                                    else failed++;
                                    if (wantMeta && MCache.LoadJson("steam", w[i], "meta.json") == null)
                                        MProvider.FetchMeta(w[i], null);
                                }
                                catch { failed++; }
                            }
                            LJ o = LJ.Obj();
                            o.Set("fetched", got);
                            o.Set("alreadyCached", skipped);
                            o.Set("failed", failed);
                            o.Set("rate", MGate.Stats());
                            return o;
                        });
                        LJ res = LJ.Obj();
                        res.Set("jobId", job.Id);
                        res.Set("state", job.State);
                        res.Set("async", true);
                        res.Set("poll", "{\"cmd\":\"job.status\",\"jobId\":\"" + job.Id + "\"}");
                        return res;
                    }

                case "meta.cache":
                    {
                        if (q.B("clear", false))
                        {
                            int n = MCache.Clear();
                            return LJ.Obj().Set("cleared", n).Set("cache", MCache.Stats());
                        }
                        LJ o = LJ.Obj();
                        o.Set("cache", MCache.Stats());
                        o.Set("rate", MGate.Stats());
                        o.Set("config", MCfg.Redacted());
                        return o;
                    }

                case "meta.config.get":
                    return LJ.Obj().Set("config", MCfg.Redacted()).Set("path", MCfg.Path_());

                case "meta.config.set":
                    {
                        LJ patch = q.Get("config");
                        if (patch == null || patch.Kind != LJ.TObj)
                        {
                            patch = LJ.Obj();
                            if (q.Get("network") != null) patch.Set("network", q.Get("network"));
                            if (q.Get("steamGridDb") != null) patch.Set("steamGridDb", q.Get("steamGridDb"));
                        }
                        MCfg.Save(patch);
                        return LJ.Obj().Set("config", MCfg.Redacted()).Set("path", MCfg.Path_());
                    }
            }
            throw new LibFault("unknown_command", "MetaApi does not handle '" + cmd + "'.");
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
