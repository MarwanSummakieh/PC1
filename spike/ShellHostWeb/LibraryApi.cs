// ARC OS - LibraryApi
// Real installed-software discovery for the ARC OS home rail (ui/library.js).
//
// The WebView2 page posts a JSON command object; LibraryApi.Handle() returns a JSON response
// string. Same envelope as SystemApi.cs and FileApi.cs, deliberately, so one page-side transport
// serves all three:
//     {"ok":true,"data":{...}}
//     {"ok":false,"error":"code","detail":"human readable"}
// Handle() NEVER throws. This process IS the Windows shell; an escaped exception blanks a
// television, so every path is wrapped and every native failure becomes an envelope.
//
// A request may carry "reqId":"<anything>" and it is echoed back so the page can correlate.
// It is NOT called "id": lib.launch and lib.icon take an "id" argument of their own.
//
// Drop it into the same csc invocation:
//     csc ... ShellHostWeb.cs SystemApi.cs FileApi.cs LibraryApi.cs
//
// Language level: C# 5 (inbox .NET Framework csc.exe).
// No string interpolation, no nameof, no expression-bodied members, no async/await.
//
// -------------------------------------------------------------------------------------------
// NAMESPACE AND TYPE NAMES
// -------------------------------------------------------------------------------------------
// Namespace is ArcOs.Library. Helper types are prefixed (LJ, LApi, LJobs, LNative, LWinRt,
// LibFault, LKv) so that a file doing `using ArcOs.Sys; using ArcOs.Files; using ArcOs.Library;`
// still compiles. A bare `J` in three namespaces would be ambiguous and finding that out at
// integration time is exactly the collision this avoids.
//
// -------------------------------------------------------------------------------------------
// REFERENCES
// -------------------------------------------------------------------------------------------
// System.dll, System.Core.dll and System.Drawing.dll. System.Drawing is the ONLY thing this file
// adds over FileApi's reference list, and only for lib.icon: HICON -> 32bpp bitmap -> PNG. It is
// already in build.cmd because ShellHostWeb.cs is a WinForms host, so build.cmd gains nothing but
// the source line. build-lib-cli.cmd carries it explicitly.
// Registry access is Microsoft.Win32.Registry, which lives in mscorlib - no extra reference.
// There is no System.Xml reference: the two XML files this reads (AppxManifest.xml,
// MicrosoftGame.config) are scanned for attributes with a regex rather than parsed, because a
// missing attribute must degrade to "no icon", never to an exception on the shell's boundary.
//
// -------------------------------------------------------------------------------------------
// THE RULE THIS FILE EXISTS TO ENFORCE
// -------------------------------------------------------------------------------------------
// A source that reports nothing is fine. A source that reports something which is not really
// installed is a bug. Every entry below is admitted only on filesystem evidence:
//   * Steam    - appmanifest StateFlags has the FullyInstalled bit AND steamapps\common\<installdir>
//                exists on disk.
//   * Epic     - InstallLocation exists AND the LaunchExecutable file inside it exists.
//   * GOG      - the registry key's "path" exists AND the exe named by the key exists.
//   * Bnet     - InstallLocation exists.
//   * Xbox     - the package's InstalledPath exists AND contains MicrosoftGame.config.
//   * Shortcut - the .lnk resolves to an .exe that exists.
//   * Folder   - the directory exists AND a plausible game exe was identified inside it.
// Registry presence alone is never enough. Uninstall entries outlive uninstalls constantly.
//
// -------------------------------------------------------------------------------------------
// THREADING CONTRACT
// -------------------------------------------------------------------------------------------
// Handle() is called from the WebView2 UI thread. lib.list, lib.sources, lib.icon and lib.launch
// are synchronous and bounded. lib.scan is NOT - a cold scan touches hundreds of files and the
// package manager - so it is a background JOB:
//     {"ok":true,"data":{"jobId":"ljob-1","state":"running","async":true}}
// The page polls {"cmd":"job.status","jobId":"ljob-1"} until state is done|error|cancelled.
// Job ids are prefixed "ljob-" so they can never be confused with SystemApi's "job-N" or
// FileApi's "fjob-N".
//
// -------------------------------------------------------------------------------------------
// APARTMENTS
// -------------------------------------------------------------------------------------------
// Job threads are MTA, matching SystemApi, because WinRT is happiest there. But three things here
// are STA-only in practice and each runs on its own short-lived STA thread that is joined before
// the caller returns (LSta.Run):
//   * IShellLinkW      - CLSID_ShellLink is ThreadingModel=Apartment. Creating it from an MTA
//                        thread would go through a marshalling proxy; running it on a real STA
//                        thread removes the question entirely.
//   * ShellExecuteEx   - documented as requiring an STA caller.
//   * IApplicationActivationManager - ditto, and it is how packaged apps are started.
// The CLI harness marks Main [STAThread] for the same reason.
//
// -------------------------------------------------------------------------------------------
// ELEVATION
// -------------------------------------------------------------------------------------------
// Nothing in this file requires administrator rights, and one thing actively requires the ABSENCE
// of them: IApplicationActivationManager::ActivateApplication returns E_ACCESSDENIED (0x80070005)
// when the caller is elevated, because a packaged app may not be activated from a high-integrity
// process. The shell runs as standard user arcshell, so that is the correct side of the fence.
// PackageManager.FindPackagesForUser("") enumerates only the CURRENT user's packages and needs no
// capability; FindPackages() (all users) would need admin and is deliberately not used.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace ArcOs.Library
{
    #region JSON  (hand-rolled writer + reader; same shape as SystemApi's J, renamed to LJ)

    public sealed class LJ
    {
        public const int TNull = 0, TBool = 1, TNum = 2, TStr = 3, TArr = 4, TObj = 5;

        int _t;
        bool _b;
        double _n;
        string _s;
        List<LJ> _items;
        List<string> _keys;
        List<LJ> _vals;

        LJ(int t) { _t = t; }

        public int Kind { get { return _t; } }

        public static LJ Obj() { LJ j = new LJ(TObj); j._keys = new List<string>(); j._vals = new List<LJ>(); return j; }
        public static LJ Arr() { LJ j = new LJ(TArr); j._items = new List<LJ>(); return j; }
        public static LJ Nul() { return new LJ(TNull); }

        public static LJ Of(string s) { if (s == null) return Nul(); LJ j = new LJ(TStr); j._s = s; return j; }
        public static LJ Of(bool b) { LJ j = new LJ(TBool); j._b = b; return j; }
        public static LJ Of(long v) { LJ j = new LJ(TNum); j._n = v; j._s = v.ToString(CultureInfo.InvariantCulture); return j; }
        public static LJ Of(int v) { return Of((long)v); }
        public static LJ Of(double v)
        {
            LJ j = new LJ(TNum);
            if (double.IsNaN(v) || double.IsInfinity(v)) { j._t = TNull; return j; }
            j._n = v;
            j._s = v.ToString("R", CultureInfo.InvariantCulture);
            return j;
        }

        public LJ Set(string k, LJ v) { _keys.Add(k); _vals.Add(v == null ? Nul() : v); return this; }
        public LJ Set(string k, string v) { return Set(k, Of(v)); }
        public LJ Set(string k, bool v) { return Set(k, Of(v)); }
        public LJ Set(string k, int v) { return Set(k, Of(v)); }
        public LJ Set(string k, long v) { return Set(k, Of(v)); }
        public LJ Set(string k, double v) { return Set(k, Of(v)); }
        public LJ SetNull(string k) { return Set(k, Nul()); }

        public LJ Put(string k, LJ v)
        {
            for (int i = 0; i < _keys.Count; i++)
                if (string.Equals(_keys[i], k, StringComparison.Ordinal)) { _vals[i] = v == null ? Nul() : v; return this; }
            return Set(k, v);
        }
        public LJ Put(string k, string v) { return Put(k, Of(v)); }
        public LJ Put(string k, int v) { return Put(k, Of(v)); }

        public LJ Add(LJ v) { _items.Add(v == null ? Nul() : v); return this; }
        public LJ Add(string v) { return Add(Of(v)); }

        public int Count
        {
            get
            {
                if (_t == TArr) return _items.Count;
                if (_t == TObj) return _keys.Count;
                return 0;
            }
        }

        public LJ At(int i)
        {
            if (_t != TArr || i < 0 || i >= _items.Count) return null;
            return _items[i];
        }

        public string KeyAt(int i)
        {
            if (_t != TObj || i < 0 || i >= _keys.Count) return null;
            return _keys[i];
        }

        public LJ Get(string key)
        {
            if (_t != TObj || key == null) return null;
            for (int i = 0; i < _keys.Count; i++)
                if (string.Equals(_keys[i], key, StringComparison.OrdinalIgnoreCase)) return _vals[i];
            return null;
        }

        public bool Has(string key) { return Get(key) != null; }

        public string AsStr(string dflt)
        {
            if (_t == TStr) return _s;
            if (_t == TNum) return _s;
            if (_t == TBool) return _b ? "true" : "false";
            return dflt;
        }

        public double AsNum(double dflt)
        {
            if (_t == TNum) return _n;
            if (_t == TStr)
            {
                double d;
                if (double.TryParse(_s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            }
            if (_t == TBool) return _b ? 1 : 0;
            return dflt;
        }

        public bool AsBool(bool dflt)
        {
            if (_t == TBool) return _b;
            if (_t == TNum) return _n != 0;
            if (_t == TStr)
            {
                if (string.Equals(_s, "true", StringComparison.OrdinalIgnoreCase) || _s == "1") return true;
                if (string.Equals(_s, "false", StringComparison.OrdinalIgnoreCase) || _s == "0") return false;
            }
            return dflt;
        }

        public string S(string key, string dflt) { LJ v = Get(key); return v == null ? dflt : v.AsStr(dflt); }
        public double N(string key, double dflt) { LJ v = Get(key); return v == null ? dflt : v.AsNum(dflt); }
        public int I(string key, int dflt) { LJ v = Get(key); return v == null ? dflt : (int)v.AsNum(dflt); }
        public long L(string key, long dflt) { LJ v = Get(key); return v == null ? dflt : (long)v.AsNum(dflt); }
        public bool B(string key, bool dflt) { LJ v = Get(key); return v == null ? dflt : v.AsBool(dflt); }

        public string ToJson()
        {
            StringBuilder sb = new StringBuilder(1024);
            Write(sb);
            return sb.ToString();
        }

        void Write(StringBuilder sb)
        {
            switch (_t)
            {
                case TNull: sb.Append("null"); break;
                case TBool: sb.Append(_b ? "true" : "false"); break;
                case TNum: sb.Append(_s != null ? _s : _n.ToString("R", CultureInfo.InvariantCulture)); break;
                case TStr: Escape(sb, _s); break;
                case TArr:
                    sb.Append('[');
                    for (int i = 0; i < _items.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        _items[i].Write(sb);
                    }
                    sb.Append(']');
                    break;
                case TObj:
                    sb.Append('{');
                    for (int i = 0; i < _keys.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        Escape(sb, _keys[i]);
                        sb.Append(':');
                        _vals[i].Write(sb);
                    }
                    sb.Append('}');
                    break;
            }
        }

        public static void Escape(StringBuilder sb, string s)
        {
            if (s == null) { sb.Append("null"); return; }
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ' || c == '\u2028' || c == '\u2029')
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        public static LJ Parse(string text)
        {
            int i = 0;
            if (text == null) throw new FormatException("empty input");
            // A UTF-8 BOM in front of a manifest is common and is not an error.
            if (text.Length > 0 && text[0] == '\uFEFF') i = 1;
            LJ v = ParseValue(text, ref i);
            SkipWs(text, ref i);
            return v;
        }

        static void SkipWs(string t, ref int i)
        {
            while (i < t.Length && (t[i] == ' ' || t[i] == '\t' || t[i] == '\r' || t[i] == '\n')) i++;
        }

        static LJ ParseValue(string t, ref int i)
        {
            SkipWs(t, ref i);
            if (i >= t.Length) throw new FormatException("unexpected end of JSON");
            char c = t[i];
            if (c == '{')
            {
                i++;
                LJ o = Obj();
                SkipWs(t, ref i);
                if (i < t.Length && t[i] == '}') { i++; return o; }
                while (true)
                {
                    SkipWs(t, ref i);
                    if (i >= t.Length || t[i] != '"') throw new FormatException("expected key at " + i);
                    string k = ParseString(t, ref i);
                    SkipWs(t, ref i);
                    if (i >= t.Length || t[i] != ':') throw new FormatException("expected ':' at " + i);
                    i++;
                    o.Set(k, ParseValue(t, ref i));
                    SkipWs(t, ref i);
                    if (i < t.Length && t[i] == ',') { i++; continue; }
                    if (i < t.Length && t[i] == '}') { i++; return o; }
                    throw new FormatException("expected ',' or '}' at " + i);
                }
            }
            if (c == '[')
            {
                i++;
                LJ a = Arr();
                SkipWs(t, ref i);
                if (i < t.Length && t[i] == ']') { i++; return a; }
                while (true)
                {
                    a.Add(ParseValue(t, ref i));
                    SkipWs(t, ref i);
                    if (i < t.Length && t[i] == ',') { i++; continue; }
                    if (i < t.Length && t[i] == ']') { i++; return a; }
                    throw new FormatException("expected ',' or ']' at " + i);
                }
            }
            if (c == '"') return Of(ParseString(t, ref i));
            if (string.CompareOrdinal(t, i, "true", 0, 4) == 0) { i += 4; return Of(true); }
            if (string.CompareOrdinal(t, i, "false", 0, 5) == 0) { i += 5; return Of(false); }
            if (string.CompareOrdinal(t, i, "null", 0, 4) == 0) { i += 4; return Nul(); }

            int start = i;
            if (i < t.Length && (t[i] == '-' || t[i] == '+')) i++;
            while (i < t.Length && ((t[i] >= '0' && t[i] <= '9') || t[i] == '.' || t[i] == 'e' || t[i] == 'E' || t[i] == '-' || t[i] == '+')) i++;
            if (i == start) throw new FormatException("unexpected character '" + c + "' at " + i);
            string lit = t.Substring(start, i - start);
            double d;
            if (!double.TryParse(lit, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                throw new FormatException("bad number '" + lit + "'");
            LJ num = new LJ(TNum);
            num._n = d;
            num._s = lit;
            return num;
        }

        static string ParseString(string t, ref int i)
        {
            i++;
            StringBuilder sb = new StringBuilder();
            while (i < t.Length)
            {
                char c = t[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= t.Length) break;
                char e = t[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > t.Length) throw new FormatException("bad \\u escape");
                        sb.Append((char)int.Parse(t.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new FormatException("bad escape '\\" + e + "'");
                }
            }
            throw new FormatException("unterminated string");
        }
    }

    #endregion

    #region Faults

    public sealed class LibFault : Exception
    {
        public readonly string Code;
        public readonly LJ Extra;
        public LibFault(string code, string detail) : base(detail == null ? code : detail) { Code = code; }
        public LibFault(string code, string detail, LJ extra) : base(detail == null ? code : detail) { Code = code; Extra = extra; }
    }

    #endregion

    #region Core helpers

    public static class LApi
    {
        public static string Describe(Exception ex)
        {
            if (ex == null) return "";
            StringBuilder sb = new StringBuilder();
            Exception cur = ex;
            int depth = 0;
            while (cur != null && depth < 4)
            {
                if (depth > 0) sb.Append(" <- ");
                sb.Append(cur.GetType().Name).Append(": ").Append(cur.Message);
                COMException ce = cur as COMException;
                if (ce != null) sb.Append(" [hr=0x").Append(ce.ErrorCode.ToString("X8", CultureInfo.InvariantCulture)).Append("]");
                cur = cur.InnerException;
                depth++;
            }
            return sb.ToString();
        }

        public static string Win32(uint err)
        {
            string msg;
            try { msg = new System.ComponentModel.Win32Exception((int)err).Message; }
            catch { msg = "unknown"; }
            return "0x" + err.ToString("X8", CultureInfo.InvariantCulture) + " (" + err.ToString(CultureInfo.InvariantCulture) + "): " + msg;
        }

        public static bool IsElevated()
        {
            try
            {
                using (System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    System.Security.Principal.WindowsPrincipal p = new System.Security.Principal.WindowsPrincipal(id);
                    return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }

        // Stable, short, filename-safe id fragment. FNV-1a 64. Used where the natural key is a
        // path: two scans of the same machine must produce the same entry ids or the page's
        // focus, cache and icon lookups all break across a refresh.
        public static string Hash(string s)
        {
            if (s == null) s = "";
            s = s.ToLowerInvariant();
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < s.Length; i++)
            {
                h ^= s[i];
                h *= 1099511628211UL;
            }
            return h.ToString("x16", CultureInfo.InvariantCulture);
        }

        public static bool DirExists(string p)
        {
            if (string.IsNullOrEmpty(p)) return false;
            try { return Directory.Exists(p); }
            catch { return false; }
        }

        public static bool FileExists(string p)
        {
            if (string.IsNullOrEmpty(p)) return false;
            try { return File.Exists(p); }
            catch { return false; }
        }

        public static string Full(string p)
        {
            if (string.IsNullOrEmpty(p)) return p;
            try { return Path.GetFullPath(p.Trim().Trim('"')); }
            catch { return p; }
        }

        public static DateTime FromUnix(long seconds)
        {
            if (seconds <= 0) return DateTime.MinValue;
            try { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds); }
            catch { return DateTime.MinValue; }
        }

        // Environment.ExpandEnvironmentVariables, but never throwing and never returning null.
        public static string Expand(string s)
        {
            if (s == null) return null;
            try { return Environment.ExpandEnvironmentVariables(s); }
            catch { return s; }
        }
    }

    #endregion

    #region Background jobs  (ids are "ljob-N")

    public static class LJobs
    {
        public sealed class Job
        {
            public string Id;
            public string Cmd;
            public string State;      // running | done | error | cancelled
            public int Percent;       // 0..100, -1 = indeterminate
            public string Phase;
            public LJ Result;
            public string Error;
            public string Detail;
            public DateTime StartedUtc;
            public DateTime FinishedUtc;
            volatile bool _cancel;
            public bool CancelRequested { get { return _cancel; } }
            public void RequestCancel() { _cancel = true; }
            public void Report(int percent, string phase) { Percent = percent; Phase = phase; }
        }

        static readonly object Gate = new object();
        static readonly Dictionary<string, Job> Map = new Dictionary<string, Job>(StringComparer.OrdinalIgnoreCase);
        static int _seq;

        public static Job Start(string cmd, Func<Job, LJ> work)
        {
            Job job = new Job();
            lock (Gate)
            {
                _seq++;
                job.Id = "ljob-" + _seq.ToString(CultureInfo.InvariantCulture);
                job.Cmd = cmd;
                job.State = "running";
                job.Percent = -1;
                job.Phase = "starting";
                job.StartedUtc = DateTime.UtcNow;
                Map[job.Id] = job;
                Prune();
            }

            Thread t = new Thread(delegate ()
            {
                try
                {
                    LJ r = work(job);
                    lock (Gate)
                    {
                        if (job.State == "running")
                        {
                            job.State = job.CancelRequested ? "cancelled" : "done";
                            job.Result = r;
                            job.Percent = 100;
                            job.Phase = "complete";
                        }
                    }
                }
                catch (Exception ex)
                {
                    lock (Gate)
                    {
                        job.State = "error";
                        job.Error = "job_failed";
                        job.Detail = LApi.Describe(ex);
                    }
                }
                finally
                {
                    job.FinishedUtc = DateTime.UtcNow;
                }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.MTA);
            t.Name = "arc-libapi-" + job.Id;
            t.Start();
            return job;
        }

        public static Job Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (Gate)
            {
                Job j;
                if (Map.TryGetValue(id, out j)) return j;
                return null;
            }
        }

        public static List<Job> All()
        {
            lock (Gate) { return new List<Job>(Map.Values); }
        }

        static void Prune()
        {
            if (Map.Count < 32) return;
            List<string> dead = new List<string>();
            foreach (KeyValuePair<string, Job> kv in Map)
            {
                Job j = kv.Value;
                if (j.State != "running" && (DateTime.UtcNow - j.FinishedUtc).TotalMinutes > 10)
                    dead.Add(kv.Key);
            }
            for (int i = 0; i < dead.Count; i++) Map.Remove(dead[i]);
        }

        public static LJ Describe(Job j)
        {
            LJ o = LJ.Obj();
            o.Set("jobId", j.Id);
            o.Set("cmd", j.Cmd);
            o.Set("state", j.State);
            o.Set("percent", j.Percent);
            o.Set("phase", j.Phase);
            o.Set("startedUtc", j.StartedUtc.ToString("o", CultureInfo.InvariantCulture));
            if (j.State != "running")
                o.Set("finishedUtc", j.FinishedUtc.ToString("o", CultureInfo.InvariantCulture));
            o.Set("elapsedMs", (long)((j.State == "running" ? DateTime.UtcNow : j.FinishedUtc) - j.StartedUtc).TotalMilliseconds);
            if (j.Result != null) o.Set("result", j.Result);
            if (j.Error != null) { o.Set("error", j.Error); o.Set("detail", j.Detail); }
            return o;
        }
    }

    #endregion

    #region STA helper

    // Run a body on a real STA thread and wait for it. If we are already STA (the WebView2 UI
    // thread, or the CLI harness's [STAThread] Main) the body runs inline - creating a second
    // STA thread there would only lose the caller's foreground rights.
    internal static class LSta
    {
        public static void Run(ThreadStart body)
        {
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) { body(); return; }
            Exception cap = null;
            Thread t = new Thread(delegate ()
            {
                try { body(); }
                catch (Exception ex) { cap = ex; }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (cap != null) throw new LibFault("sta_failed", LApi.Describe(cap));
        }
    }

    #endregion

    #region Native interop

    internal static class LNative
    {
        // ---------- shell link ----------

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        internal class ShellLink { }

        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out ushort pwHotkey);
            void SetHotkey(ushort wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport, Guid("0000010b-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder ppszFileName);
        }

        internal const uint STGM_READ = 0x00000000;
        internal const uint SLGP_RAWPATH = 0x0004;

        // ---------- app activation (packaged apps, no explorer.exe involved) ----------

        [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
        internal class ApplicationActivationManager { }

        [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IApplicationActivationManager
        {
            // Slot 3. The only method used. The other two are declared purely so that the vtable
            // layout is stated explicitly rather than implied.
            [PreserveSig]
            int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
                                    [MarshalAs(UnmanagedType.LPWStr)] string arguments,
                                    int options, out uint processId);
            [PreserveSig]
            int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
                                IntPtr itemArray, [MarshalAs(UnmanagedType.LPWStr)] string verb,
                                out uint processId);
            [PreserveSig]
            int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
                                    IntPtr itemArray, out uint processId);
        }

        internal const int AO_NONE = 0;
        internal const int AO_NOERRORUI = 0x00000002;

        // ---------- ShellExecuteEx ----------

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct SHELLEXECUTEINFO
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpVerb;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpFile;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpParameters;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpDirectory;
            public int nShow;
            public IntPtr hInstApp;
            public IntPtr lpIDList;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpClass;
            public IntPtr hkeyClass;
            public uint dwHotKey;
            public IntPtr hIcon;
            public IntPtr hProcess;
        }

        internal const uint SEE_MASK_NOCLOSEPROCESS = 0x00000040;
        internal const uint SEE_MASK_NOASYNC = 0x00000100;
        internal const uint SEE_MASK_FLAG_NO_UI = 0x00000400;
        internal const int SW_SHOWNORMAL = 1;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShellExecuteExW(ref SHELLEXECUTEINFO lpExecInfo);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint GetProcessId(IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr h);

        // ---------- icons ----------

        // PrivateExtractIconsW is the one that lets you ASK for a size. ExtractIconEx only ever
        // gives 32x32 and 16x16, which look like a 2005 taskbar on a television.
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int PrivateExtractIconsW(string szFileName, int nIconIndex,
            int cxIcon, int cyIcon, IntPtr[] phicon, int[] piconid, int nIcons, uint flags);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        internal static extern int ExtractIconExW(string lpszFile, int nIconIndex,
            IntPtr[] phiconLarge, IntPtr[] phiconSmall, int nIcons);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyIcon(IntPtr hIcon);
    }

    #endregion

    #region WinRT bridge (reflection only)

    // Same approach and the same reasoning as SystemApi.cs: load WinRT types by name from the
    // "Windows" pseudo-assembly and late-bind. A wrong reflection lookup is a caught exception;
    // a wrong hand-declared WinRT vtable slot corrupts the stack and takes the shell down.
    internal static class LWinRt
    {
        public static Type Type_(string fullName)
        {
            try { return Type.GetType(fullName + ", Windows, ContentType=WindowsRuntime", false); }
            catch { return null; }
        }

        public static object Await(Type opType, object op, int timeoutMs, out string error)
        {
            error = null;
            if (op == null) { error = "async operation returned null"; return null; }
            Type tInfo = Type_("Windows.Foundation.IAsyncInfo");
            if (tInfo == null) { error = "Windows.Foundation.IAsyncInfo is not projectable on this build"; return null; }
            PropertyInfo pStatus = tInfo.GetProperty("Status");
            if (pStatus == null) { error = "IAsyncInfo.Status not found"; return null; }

            int waited = 0;
            int status;
            while (true)
            {
                try { status = Convert.ToInt32(pStatus.GetValue(op, null), CultureInfo.InvariantCulture); }
                catch (Exception ex) { error = "reading IAsyncInfo.Status: " + LApi.Describe(ex); return null; }
                if (status != 0) break;
                if (waited >= timeoutMs) { error = "timed out after " + timeoutMs + " ms"; return null; }
                Thread.Sleep(15);
                waited += 15;
            }
            if (status == 2) { error = "operation was cancelled"; return null; }
            if (status == 3)
            {
                try
                {
                    object e = tInfo.GetProperty("ErrorCode").GetValue(op, null);
                    error = e == null ? "operation failed" : e.ToString();
                }
                catch { error = "operation failed"; }
                return null;
            }
            MethodInfo gr = opType == null ? null : opType.GetMethod("GetResults");
            if (gr == null) return null;
            try { return gr.Invoke(op, null); }
            catch (Exception ex) { error = "GetResults: " + LApi.Describe(ex); return null; }
        }

        public static object CallInstanceAsync(object target, Type t, string method, object[] args, int timeoutMs, out string error)
        {
            error = null;
            if (t == null) { error = "type not available"; return null; }
            MethodInfo mi = Find(t, method, BindingFlags.Public | BindingFlags.Instance, args);
            if (mi == null) { error = method + " not found on " + t.FullName; return null; }
            object op;
            try { op = mi.Invoke(target, args); }
            catch (Exception ex) { error = method + ": " + LApi.Describe(ex); return null; }
            return Await(mi.ReturnType, op, timeoutMs, out error);
        }

        public static object CallInstance(object target, Type t, string method, object[] args, out string error)
        {
            error = null;
            if (t == null) { error = "type not available"; return null; }
            MethodInfo mi = Find(t, method, BindingFlags.Public | BindingFlags.Instance, args);
            if (mi == null) { error = method + " not found on " + t.FullName; return null; }
            try { return mi.Invoke(target, args); }
            catch (Exception ex) { error = method + ": " + LApi.Describe(ex); return null; }
        }

        static MethodInfo Find(Type t, string name, BindingFlags flags, object[] args)
        {
            int n = args == null ? 0 : args.Length;
            MethodInfo[] all = t.GetMethods(flags);
            for (int i = 0; i < all.Length; i++)
                if (all[i].Name == name && all[i].GetParameters().Length == n) return all[i];
            return null;
        }

        // An RCW reports itself as System.__ComObject, so the PropertyInfo must come from the
        // projected type, never from target.GetType().
        public static object Prop(object target, Type t, string name)
        {
            if (t == null || target == null) return null;
            PropertyInfo p = t.GetProperty(name);
            if (p == null) return null;
            try { return p.GetValue(target, null); }
            catch { return null; }
        }

        public static string PropStr(object target, Type t, string name)
        {
            object v = Prop(target, t, name);
            return v == null ? null : v.ToString();
        }

        public static bool PropBool(object target, Type t, string name, bool dflt)
        {
            object v = Prop(target, t, name);
            if (v == null) return dflt;
            try { return Convert.ToBoolean(v, CultureInfo.InvariantCulture); }
            catch { return dflt; }
        }
    }

    #endregion

    #region Valve KeyValues (VDF) reader

    // libraryfolders.vdf and appmanifest_*.acf are Valve KeyValues text, not JSON. The grammar is
    // small: quoted or bare tokens, {} nesting, // comments, and optional [$PLATFORM] conditionals
    // that are skipped. Written by hand because pulling a VDF library in is not an option here.
    internal sealed class LKv
    {
        public string Key;
        public string Val;                 // null when this node has children
        public List<LKv> Kids;

        public LKv Child(string name)
        {
            if (Kids == null || name == null) return null;
            for (int i = 0; i < Kids.Count; i++)
                if (string.Equals(Kids[i].Key, name, StringComparison.OrdinalIgnoreCase)) return Kids[i];
            return null;
        }

        public string Str(string name, string dflt)
        {
            LKv c = Child(name);
            return (c == null || c.Val == null) ? dflt : c.Val;
        }

        public long Num(string name, long dflt)
        {
            string s = Str(name, null);
            if (s == null) return dflt;
            long v;
            if (long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            return dflt;
        }

        public static LKv Parse(string text)
        {
            int i = 0;
            LKv root = new LKv();
            root.Key = "";
            root.Kids = new List<LKv>();
            ParseInto(text, ref i, root, 0);
            return root;
        }

        static void ParseInto(string t, ref int i, LKv parent, int depth)
        {
            if (depth > 32) throw new FormatException("vdf nested too deep");
            while (true)
            {
                string key = NextToken(t, ref i);
                if (key == null) return;
                if (key == "}") return;
                if (key == "{") continue;                 // stray brace: tolerate

                SkipTrivia(t, ref i);
                if (i < t.Length && t[i] == '{')
                {
                    i++;
                    LKv node = new LKv();
                    node.Key = key;
                    node.Kids = new List<LKv>();
                    ParseInto(t, ref i, node, depth + 1);
                    parent.Kids.Add(node);
                    continue;
                }

                string val = NextToken(t, ref i);
                if (val == null) return;
                if (val == "}") { parent.Kids.Add(Leaf(key, "")); return; }
                if (val == "{")
                {
                    LKv node = new LKv();
                    node.Key = key;
                    node.Kids = new List<LKv>();
                    ParseInto(t, ref i, node, depth + 1);
                    parent.Kids.Add(node);
                    continue;
                }
                parent.Kids.Add(Leaf(key, val));

                // Optional trailing [$WIN32] style conditional - consumed and ignored.
                int save = i;
                SkipTrivia(t, ref i);
                if (i < t.Length && t[i] == '[')
                {
                    while (i < t.Length && t[i] != ']') i++;
                    if (i < t.Length) i++;
                }
                else i = save;
            }
        }

        static LKv Leaf(string k, string v)
        {
            LKv n = new LKv();
            n.Key = k;
            n.Val = v;
            return n;
        }

        static void SkipTrivia(string t, ref int i)
        {
            while (i < t.Length)
            {
                char c = t[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { i++; continue; }
                if (c == '/' && i + 1 < t.Length && t[i + 1] == '/')
                {
                    while (i < t.Length && t[i] != '\n') i++;
                    continue;
                }
                break;
            }
        }

        static string NextToken(string t, ref int i)
        {
            SkipTrivia(t, ref i);
            if (i >= t.Length) return null;
            char c = t[i];
            if (c == '{' || c == '}') { i++; return c.ToString(); }
            if (c == '"')
            {
                i++;
                StringBuilder sb = new StringBuilder();
                while (i < t.Length)
                {
                    char d = t[i++];
                    if (d == '"') return sb.ToString();
                    if (d == '\\' && i < t.Length)
                    {
                        char e = t[i++];
                        if (e == 'n') sb.Append('\n');
                        else if (e == 't') sb.Append('\t');
                        else if (e == '\\') sb.Append('\\');
                        else if (e == '"') sb.Append('"');
                        else { sb.Append('\\'); sb.Append(e); }
                        continue;
                    }
                    sb.Append(d);
                }
                return sb.ToString();     // unterminated: take what we have
            }
            int start = i;
            while (i < t.Length)
            {
                char d = t[i];
                if (d == ' ' || d == '\t' || d == '\r' || d == '\n' || d == '{' || d == '}' || d == '"') break;
                i++;
            }
            if (i == start) { i++; return NextToken(t, ref i); }
            return t.Substring(start, i - start);
        }
    }

    #endregion

    #region Entry model

    public sealed class LibEntry
    {
        public string Id;
        public string Title;
        public string Source;        // steam | epic | gog | battlenet | xbox | shortcut | folder
        public string SourceLabel;   // "Steam", "Epic Games", ...
        public string Kind;          // game | app | launcher
        public string LaunchKind;    // uri | exe | aumid
        public string LaunchTarget;
        public string LaunchArgs;
        public string WorkDir;
        public string InstallDir;
        public long SizeBytes = -1;
        public DateTime LastPlayedUtc = DateTime.MinValue;
        public string IconPath;      // exe / ico / png / jpg to draw the small glyph from
        public int IconIndex;
        public string ArtPath;       // portrait cover, when the source ships one
        public string Note;
        public string Icon;          // small PNG data URI, filled when scan is asked for icons

        public LJ ToJson(bool withIcon)
        {
            LJ o = LJ.Obj();
            o.Set("id", Id);
            o.Set("title", Title);
            o.Set("source", Source);
            o.Set("sourceLabel", SourceLabel);
            o.Set("kind", Kind);
            o.Set("launchKind", LaunchKind);
            o.Set("launchTarget", LaunchTarget);
            if (!string.IsNullOrEmpty(LaunchArgs)) o.Set("launchArgs", LaunchArgs);
            if (!string.IsNullOrEmpty(WorkDir)) o.Set("workDir", WorkDir);
            if (!string.IsNullOrEmpty(InstallDir)) o.Set("installDir", InstallDir);
            if (SizeBytes >= 0) o.Set("sizeBytes", SizeBytes); else o.SetNull("sizeBytes");
            if (LastPlayedUtc > DateTime.MinValue)
                o.Set("lastPlayedUtc", LastPlayedUtc.ToString("o", CultureInfo.InvariantCulture));
            else o.SetNull("lastPlayedUtc");
            o.Set("hasArt", !string.IsNullOrEmpty(ArtPath));
            o.Set("hasIcon", !string.IsNullOrEmpty(IconPath) || !string.IsNullOrEmpty(ArtPath));
            if (!string.IsNullOrEmpty(Note)) o.Set("note", Note);
            if (withIcon && !string.IsNullOrEmpty(Icon)) o.Set("icon", Icon);
            return o;
        }

        public static LibEntry FromJson(LJ o)
        {
            if (o == null || o.Kind != LJ.TObj) return null;
            LibEntry e = new LibEntry();
            e.Id = o.S("id", null);
            e.Title = o.S("title", null);
            e.Source = o.S("source", null);
            e.SourceLabel = o.S("sourceLabel", null);
            e.Kind = o.S("kind", "app");
            e.LaunchKind = o.S("launchKind", null);
            e.LaunchTarget = o.S("launchTarget", null);
            e.LaunchArgs = o.S("launchArgs", null);
            e.WorkDir = o.S("workDir", null);
            e.InstallDir = o.S("installDir", null);
            e.SizeBytes = o.L("sizeBytes", -1);
            string lp = o.S("lastPlayedUtc", null);
            if (!string.IsNullOrEmpty(lp))
            {
                DateTime dt;
                if (DateTime.TryParse(lp, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out dt)) e.LastPlayedUtc = dt.ToUniversalTime();
            }
            e.IconPath = o.S("iconPath", null);
            e.IconIndex = o.I("iconIndex", 0);
            e.ArtPath = o.S("artPath", null);
            e.Note = o.S("note", null);
            e.Icon = o.S("icon", null);
            return e;
        }

        // The cache keeps the private fields the wire form hides, so lib.icon still works after a
        // restart with nothing but the cache on disk.
        public LJ ToCacheJson()
        {
            LJ o = ToJson(true);
            if (!string.IsNullOrEmpty(IconPath)) o.Set("iconPath", IconPath);
            if (IconIndex != 0) o.Set("iconIndex", IconIndex);
            if (!string.IsNullOrEmpty(ArtPath)) o.Set("artPath", ArtPath);
            return o;
        }
    }

    // What one source had to say for itself. This is the honesty ledger: the page can show
    // "Steam: not installed" rather than silently pretending the machine has no games.
    public sealed class LibSource
    {
        public string Name;
        public string Label;
        public bool Present;         // the source's data location exists on this machine
        public int Found;
        public string Where;
        public string Note;
        public string Error;
        public long Ms;

        public LJ ToJson()
        {
            LJ o = LJ.Obj();
            o.Set("name", Name);
            o.Set("label", Label);
            o.Set("present", Present);
            o.Set("found", Found);
            if (!string.IsNullOrEmpty(Where)) o.Set("where", Where);
            if (!string.IsNullOrEmpty(Note)) o.Set("note", Note);
            if (!string.IsNullOrEmpty(Error)) o.Set("error", Error);
            o.Set("ms", Ms);
            return o;
        }
    }

    #endregion

    #region Registry helpers

    internal static class LReg
    {
        public static RegistryKey Open(RegistryHive hive, RegistryView view, string path)
        {
            try
            {
                RegistryKey b = RegistryKey.OpenBaseKey(hive, view);
                if (b == null) return null;
                RegistryKey k = b.OpenSubKey(path, false);
                if (k == null) { b.Close(); return null; }
                return k;
            }
            catch { return null; }
        }

        public static string Str(RegistryKey k, string name)
        {
            if (k == null) return null;
            try
            {
                object v = k.GetValue(name, null);
                if (v == null) return null;
                string s = v.ToString();
                return s.Length == 0 ? null : s;
            }
            catch { return null; }
        }

        public static string[] SubKeys(RegistryKey k)
        {
            if (k == null) return new string[0];
            try { return k.GetSubKeyNames(); }
            catch { return new string[0]; }
        }
    }

    #endregion

    #region Sources

    internal static class Sources
    {
        // ----------------------------------------------------------------------------------
        // Steam
        // ----------------------------------------------------------------------------------
        // Appids that are runtimes, redistributables or tooling rather than something you would
        // ever put on a rail. They install exactly like games and would otherwise show up as one.
        static readonly string[] SteamNonGameIds = new string[] {
            "228980",   // Steamworks Common Redistributables
            "1070560",  // Steam Linux Runtime 1.0 (scout)
            "1391110",  // Steam Linux Runtime 2.0 (soldier)
            "1628350",  // Steam Linux Runtime 3.0 (sniper)
            "1493710",  // Proton Experimental
            "1826330",  // Proton
            "2180100",  // Proton Hotfix
            "1887720",  // Proton
            "858280",   // Proton
            "250820",   // SteamVR
            "1007",     // Steamworks Shared
            "228988", "228989", "228990"
        };

        public static string SteamInstallPath()
        {
            // 64-bit machines write the 32-bit view. Try that first, then the native view, then
            // the per-user key, which survives when the machine-wide one has been cleaned up.
            string[] hits = new string[4];
            RegistryKey k = LReg.Open(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Valve\Steam");
            if (k != null) { hits[0] = LReg.Str(k, "InstallPath"); k.Close(); }
            k = LReg.Open(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Valve\Steam");
            if (k != null) { hits[1] = LReg.Str(k, "InstallPath"); k.Close(); }
            k = LReg.Open(RegistryHive.CurrentUser, RegistryView.Default, @"SOFTWARE\Valve\Steam");
            if (k != null) { hits[2] = LReg.Str(k, "SteamPath"); k.Close(); }
            hits[3] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");

            for (int i = 0; i < hits.Length; i++)
            {
                if (string.IsNullOrEmpty(hits[i])) continue;
                string p = LApi.Full(hits[i].Replace('/', '\\'));
                if (LApi.DirExists(p)) return p;
            }
            return null;
        }

        public static void Steam(List<LibEntry> outList, LibSource src)
        {
            string root = SteamInstallPath();
            src.Where = root;
            if (root == null) { src.Note = "Steam is not installed."; return; }
            src.Present = true;

            List<string> libs = new List<string>();
            libs.Add(root);

            string vdf = Path.Combine(root, @"steamapps\libraryfolders.vdf");
            if (LApi.FileExists(vdf))
            {
                try
                {
                    LKv kv = LKv.Parse(File.ReadAllText(vdf));
                    LKv folders = kv.Child("libraryfolders");
                    if (folders == null) folders = kv.Child("LibraryFolders");
                    if (folders != null && folders.Kids != null)
                    {
                        for (int i = 0; i < folders.Kids.Count; i++)
                        {
                            LKv e = folders.Kids[i];
                            string p = null;
                            if (e.Val != null)
                            {
                                // Old format: "1" "D:\\SteamLibrary". Numeric keys only; the file
                                // also carries "TimeNextStatsReport" and friends at this level.
                                int dummy;
                                if (int.TryParse(e.Key, out dummy)) p = e.Val;
                            }
                            else p = e.Str("path", null);
                            if (string.IsNullOrEmpty(p)) continue;
                            string full = LApi.Full(p);
                            if (!LApi.DirExists(full)) continue;
                            bool dup = false;
                            for (int n = 0; n < libs.Count; n++)
                                if (string.Equals(libs[n], full, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                            if (!dup) libs.Add(full);
                        }
                    }
                }
                catch (Exception ex) { src.Error = "libraryfolders.vdf: " + LApi.Describe(ex); }
            }

            src.Note = libs.Count + (libs.Count == 1 ? " library" : " libraries");

            for (int li = 0; li < libs.Count; li++)
            {
                string apps = Path.Combine(libs[li], "steamapps");
                if (!LApi.DirExists(apps)) continue;
                string[] acfs;
                try { acfs = Directory.GetFiles(apps, "appmanifest_*.acf"); }
                catch { continue; }

                for (int ai = 0; ai < acfs.Length; ai++)
                {
                    try
                    {
                        LKv kv = LKv.Parse(File.ReadAllText(acfs[ai]));
                        LKv st = kv.Child("AppState");
                        if (st == null) continue;

                        string appid = st.Str("appid", null);
                        string name = st.Str("name", null);
                        string installdir = st.Str("installdir", null);
                        long flags = st.Num("StateFlags", 0);
                        if (string.IsNullOrEmpty(appid) || string.IsNullOrEmpty(installdir)) continue;

                        // StateFlags bit 2 (value 4) is StateFullyInstalled. Bit 1 (2) is
                        // UpdateRequired and is fine - a game pending an update is installed.
                        if ((flags & 4) == 0) continue;

                        bool skip = false;
                        for (int b = 0; b < SteamNonGameIds.Length; b++)
                            if (SteamNonGameIds[b] == appid) { skip = true; break; }
                        if (skip) continue;

                        string dir = Path.Combine(apps, "common", installdir);
                        if (!LApi.DirExists(dir)) continue;      // manifest without payload

                        LibEntry e = new LibEntry();
                        e.Id = "steam:" + appid;
                        e.Title = string.IsNullOrEmpty(name) ? installdir : name;
                        e.Source = "steam";
                        e.SourceLabel = "Steam";
                        e.Kind = "game";
                        // steam://rungameid, never the raw exe. Launching the exe directly is what
                        // breaks DRM stubs, skips the overlay and loses cloud saves; the URI is
                        // the same thing the client's own shortcuts use.
                        e.LaunchKind = "uri";
                        e.LaunchTarget = "steam://rungameid/" + appid;
                        e.InstallDir = dir;
                        e.SizeBytes = st.Num("SizeOnDisk", -1);
                        e.LastPlayedUtc = LApi.FromUnix(st.Num("LastPlayed", 0));
                        if ((flags & 2) != 0) e.Note = "Update pending";

                        SteamArt(root, appid, e);
                        outList.Add(e);
                        src.Found++;
                    }
                    catch { /* one bad manifest must not lose the library */ }
                }
            }
        }

        // Steam already downloaded the artwork, so the rail gets real covers with no network call.
        // Three layouts have to be handled, and all three occur on the SAME machine because Steam
        // never migrates what it has already cached:
        //   flat    appcache\librarycache\<appid>_library_600x900.jpg      (pre-2023)
        //   folder  appcache\librarycache\<appid>\library_600x900.jpg      (2023-ish)
        //   hashed  appcache\librarycache\<appid>\<sha1>\library_capsule.jpg
        // Verified: on this laptop Counter-Strike 2 is the folder layout and Palworld is the
        // hashed one. Looking only at the top level finds a cover for one and not the other.
        static readonly string[] SteamArtNames = new string[] {
            "library_600x900.jpg", "library_capsule.jpg", "library_header.jpg", "header.jpg"
        };

        static void SteamArt(string steamRoot, string appid, LibEntry e)
        {
            try
            {
                string cacheRoot = Path.Combine(steamRoot, @"appcache\librarycache");
                string dir = Path.Combine(cacheRoot, appid);

                if (LApi.DirExists(dir))
                {
                    e.ArtPath = FindNamed(dir, SteamArtNames);
                    string logo = FindNamed(dir, new string[] { "logo.png", "library_logo.png" });
                    if (logo != null) e.IconPath = logo;
                }
                if (e.ArtPath == null)
                {
                    string flat = Path.Combine(cacheRoot, appid + "_library_600x900.jpg");
                    if (LApi.FileExists(flat)) e.ArtPath = flat;
                }
                if (e.IconPath == null)
                {
                    string flatLogo = Path.Combine(cacheRoot, appid + "_logo.png");
                    if (LApi.FileExists(flatLogo)) e.IconPath = flatLogo;
                }
                // The portrait cover is a better tile glyph than nothing at all.
                if (e.IconPath == null && e.ArtPath != null) e.IconPath = e.ArtPath;
            }
            catch { }
        }

        // Named file in dir, or in any immediate subdirectory of dir. One level only - the hashed
        // layout never goes deeper and an unbounded walk of appcache is not worth the milliseconds.
        static string FindNamed(string dir, string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                string p = Path.Combine(dir, names[i]);
                if (LApi.FileExists(p)) return p;
            }
            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { return null; }
            for (int i = 0; i < names.Length; i++)
                for (int s = 0; s < subs.Length; s++)
                {
                    string p = Path.Combine(subs[s], names[i]);
                    if (LApi.FileExists(p)) return p;
                }
            return null;
        }

        // ----------------------------------------------------------------------------------
        // Epic Games
        // ----------------------------------------------------------------------------------
        public static void Epic(List<LibEntry> outList, LibSource src)
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                @"Epic\EpicGamesLauncher\Data\Manifests");
            src.Where = dir;
            if (!LApi.DirExists(dir)) { src.Note = "The Epic Games Launcher is not installed."; return; }
            src.Present = true;

            string[] items;
            try { items = Directory.GetFiles(dir, "*.item"); }
            catch (Exception ex) { src.Error = LApi.Describe(ex); return; }

            for (int i = 0; i < items.Length; i++)
            {
                try
                {
                    LJ m = LJ.Parse(File.ReadAllText(items[i]));
                    string appName = m.S("AppName", null);
                    string display = m.S("DisplayName", null);
                    string install = m.S("InstallLocation", null);
                    string exe = m.S("LaunchExecutable", null);
                    if (string.IsNullOrEmpty(appName) || string.IsNullOrEmpty(install)) continue;

                    install = LApi.Full(install);
                    if (!LApi.DirExists(install)) continue;

                    // A manifest whose payload has been deleted by hand still sits in Manifests.
                    // The executable is the proof.
                    string exeFull = null;
                    if (!string.IsNullOrEmpty(exe))
                    {
                        exeFull = LApi.Full(Path.Combine(install, exe.Replace('/', '\\')));
                        if (!LApi.FileExists(exeFull)) continue;
                    }

                    // Epic ships its own plugins and redistributables through the same manifests.
                    string cats = "";
                    LJ ac = m.Get("AppCategories");
                    if (ac != null && ac.Kind == LJ.TArr)
                        for (int c = 0; c < ac.Count; c++) cats += ac.At(c).AsStr("") + ",";
                    bool isGame = cats.Length == 0 || cats.IndexOf("games", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!isGame) continue;

                    LibEntry e = new LibEntry();
                    e.Id = "epic:" + appName;
                    e.Title = string.IsNullOrEmpty(display) ? appName : display;
                    e.Source = "epic";
                    e.SourceLabel = "Epic Games";
                    e.Kind = "game";
                    e.LaunchKind = "uri";
                    e.LaunchTarget = "com.epicgames.launcher://apps/" + appName + "?action=launch&silent=true";
                    e.InstallDir = install;
                    e.WorkDir = install;
                    e.IconPath = exeFull;
                    long size = m.L("InstallSize", -1);
                    if (size > 0) e.SizeBytes = size;
                    outList.Add(e);
                    src.Found++;
                }
                catch { }
            }
        }

        // ----------------------------------------------------------------------------------
        // GOG
        // ----------------------------------------------------------------------------------
        // DECISION: per-game registry keys, NOT galaxy-2.0.db.
        //
        // The brief allowed writing a minimal read-only SQLite reader. It is the wrong trade here.
        // Reading galaxy-2.0.db properly means the file header, b-tree interior/leaf pages, varint
        // record headers, serial-type decoding and overflow-page chains, and then GOG's own schema
        // which has changed shape across Galaxy 2.x. Every one of those is a place to misread a
        // row - and a misread row here means the rail advertises a game the machine does not have,
        // which is the single failure this whole file exists to prevent.
        //
        // The registry keys are written by the GOG installer itself, one per installed product,
        // and they carry path/exe/gameName directly. They are evidence of an install rather than
        // a record of one. They are also the source Playnite's GOG plugin falls back to.
        // Cost of the choice: games added to Galaxy's library but not installed are invisible -
        // which is exactly what we want - and a game installed while Galaxy was signed out is
        // still found, because the installer wrote the key regardless.
        public static void Gog(List<LibEntry> outList, LibSource src)
        {
            src.Where = @"HKLM\SOFTWARE\WOW6432Node\GOG.com\Games";
            RegistryKey games = LReg.Open(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\GOG.com\Games");
            if (games == null)
                games = LReg.Open(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\GOG.com\Games");
            if (games == null)
            {
                src.Note = "No GOG games are registered on this machine.";
                return;
            }
            src.Present = true;

            try
            {
                string[] ids = LReg.SubKeys(games);
                for (int i = 0; i < ids.Length; i++)
                {
                    RegistryKey g = null;
                    try
                    {
                        g = games.OpenSubKey(ids[i], false);
                        if (g == null) continue;
                        string path = LReg.Str(g, "path");
                        if (string.IsNullOrEmpty(path)) path = LReg.Str(g, "PATH");
                        string name = LReg.Str(g, "gameName");
                        string exe = LReg.Str(g, "exe");
                        string exeFile = LReg.Str(g, "exeFile");
                        string workDir = LReg.Str(g, "workingDir");
                        string gameId = LReg.Str(g, "gameID");
                        if (string.IsNullOrEmpty(gameId)) gameId = ids[i];
                        if (string.IsNullOrEmpty(path)) continue;

                        path = LApi.Full(path);
                        if (!LApi.DirExists(path)) continue;      // key outlived the uninstall

                        string exeFull = null;
                        if (!string.IsNullOrEmpty(exe))
                        {
                            exeFull = LApi.Full(exe);
                            if (!LApi.FileExists(exeFull)) exeFull = null;
                        }
                        if (exeFull == null && !string.IsNullOrEmpty(exeFile))
                        {
                            string cand = LApi.Full(Path.Combine(path, exeFile));
                            if (LApi.FileExists(cand)) exeFull = cand;
                        }
                        if (exeFull == null) continue;            // nothing to launch: do not list it

                        LibEntry e = new LibEntry();
                        e.Id = "gog:" + gameId;
                        e.Title = string.IsNullOrEmpty(name) ? Path.GetFileName(path) : name;
                        e.Source = "gog";
                        e.SourceLabel = "GOG";
                        e.Kind = "game";
                        // GOG titles are DRM-free, so the game's own exe IS the supported entry
                        // point. goggalaxy://openGameView/<id> only opens a Galaxy page, and the
                        // GalaxyClient /command=runGame form needs Galaxy running to mean anything.
                        e.LaunchKind = "exe";
                        e.LaunchTarget = exeFull;
                        e.WorkDir = string.IsNullOrEmpty(workDir) ? path : LApi.Full(workDir);
                        e.InstallDir = path;
                        e.IconPath = exeFull;
                        string ico = Path.Combine(path, "goggame-" + gameId + ".ico");
                        if (LApi.FileExists(ico)) e.IconPath = ico;
                        outList.Add(e);
                        src.Found++;
                    }
                    catch { }
                    finally { if (g != null) g.Close(); }
                }
            }
            finally { games.Close(); }
        }

        // ----------------------------------------------------------------------------------
        // Battle.net
        // ----------------------------------------------------------------------------------
        // DECISION: Uninstall registry entries, NOT Agent\product.db.
        //
        // product.db is a protobuf with no published .proto. Decoding it means inferring field
        // numbers and wire types from bytes and hoping Blizzard does not renumber - and a
        // mis-decoded length-delimited field yields plausible-looking garbage rather than an
        // error, which is the worst possible failure mode for a list that must not lie.
        // The Uninstall keys Battle.net writes carry the product uid in the UninstallString
        // ("Battle.net.exe" --uninstall --uid=prometheus ...), and that uid is exactly what the
        // battlenet:// protocol takes. InstallLocation is then checked on disk, so a stale key
        // from a removed game drops out.
        static readonly Regex BnetUid = new Regex("--uid=([A-Za-z0-9_]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static void BattleNet(List<LibEntry> outList, LibSource src)
        {
            string agent = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                @"Battle.net\Agent\product.db");
            src.Where = @"Uninstall keys with a Battle.net.exe uninstaller";
            src.Note = LApi.FileExists(agent)
                ? "Battle.net is installed (product.db present but deliberately not parsed - see the source comment)."
                : "Battle.net is not installed.";

            bool any = false;
            RegistryHive[] hives = new RegistryHive[] { RegistryHive.LocalMachine, RegistryHive.LocalMachine, RegistryHive.CurrentUser };
            RegistryView[] views = new RegistryView[] { RegistryView.Registry32, RegistryView.Registry64, RegistryView.Default };

            for (int h = 0; h < hives.Length; h++)
            {
                RegistryKey un = LReg.Open(hives[h], views[h], @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (un == null) continue;
                try
                {
                    string[] names = LReg.SubKeys(un);
                    for (int i = 0; i < names.Length; i++)
                    {
                        RegistryKey k = null;
                        try
                        {
                            k = un.OpenSubKey(names[i], false);
                            if (k == null) continue;
                            string uninst = LReg.Str(k, "UninstallString");
                            if (uninst == null || uninst.IndexOf("Battle.net.exe", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            any = true;

                            Match m = BnetUid.Match(uninst);
                            if (!m.Success) continue;
                            string uid = m.Groups[1].Value;
                            // uids carry a locale suffix on some products (wow_enus). The protocol
                            // takes the bare product code.
                            string code = uid;
                            int us = code.LastIndexOf('_');
                            if (us > 0 && code.Length - us <= 6) code = code.Substring(0, us);

                            string loc = LReg.Str(k, "InstallLocation");
                            if (string.IsNullOrEmpty(loc)) continue;
                            loc = LApi.Full(loc);
                            if (!LApi.DirExists(loc)) continue;   // stale key

                            string title = LReg.Str(k, "DisplayName");
                            if (string.IsNullOrEmpty(title)) title = names[i];

                            LibEntry e = new LibEntry();
                            e.Id = "bnet:" + uid.ToLowerInvariant();
                            e.Title = title;
                            e.Source = "battlenet";
                            e.SourceLabel = "Battle.net";
                            e.Kind = "game";
                            e.LaunchKind = "uri";
                            e.LaunchTarget = "battlenet://" + code;
                            e.InstallDir = loc;
                            string di = LReg.Str(k, "DisplayIcon");
                            if (!string.IsNullOrEmpty(di))
                            {
                                int idx;
                                string p = SplitIconSpec(di, out idx);
                                if (LApi.FileExists(p)) { e.IconPath = p; e.IconIndex = idx; }
                            }
                            outList.Add(e);
                            src.Found++;
                        }
                        catch { }
                        finally { if (k != null) k.Close(); }
                    }
                }
                finally { un.Close(); }
            }
            src.Present = any || LApi.FileExists(agent);
        }

        // ----------------------------------------------------------------------------------
        // Xbox / Game Pass  (packaged apps)
        // ----------------------------------------------------------------------------------
        // Gameness is decided by MicrosoftGame.config, which every GDK / Game Pass PC title ships
        // in its install root. That is a filesystem fact about the installed payload, not a
        // category string someone could have set on any app. Packages without it are simply not
        // reported by this source - a Store calculator is not a rail tile.
        public static void Xbox(List<LibEntry> outList, LibSource src)
        {
            src.Where = "Windows.Management.Deployment.PackageManager.FindPackagesForUser(\"\")";

            Type tPm = LWinRt.Type_("Windows.Management.Deployment.PackageManager");
            Type tPkg = LWinRt.Type_("Windows.ApplicationModel.Package");
            if (tPm == null || tPkg == null)
            {
                src.Note = "WinRT package types are not projectable on this build.";
                return;
            }

            object pm;
            try { pm = Activator.CreateInstance(tPm); }
            catch (Exception ex) { src.Error = "PackageManager: " + LApi.Describe(ex); return; }
            src.Present = true;

            System.Collections.IEnumerable pkgs;
            try
            {
                MethodInfo mi = tPm.GetMethod("FindPackagesForUser", new Type[] { typeof(string) });
                if (mi == null) { src.Error = "FindPackagesForUser(string) not found"; return; }
                // "" means the calling user. FindPackages() (every user) would need admin.
                pkgs = mi.Invoke(pm, new object[] { "" }) as System.Collections.IEnumerable;
            }
            catch (Exception ex) { src.Error = "FindPackagesForUser: " + LApi.Describe(ex); return; }
            if (pkgs == null) { src.Error = "FindPackagesForUser returned nothing enumerable"; return; }

            Type tPkgId = LWinRt.Type_("Windows.ApplicationModel.PackageId");
            int looked = 0;

            foreach (object pkg in pkgs)
            {
                try
                {
                    looked++;
                    if (LWinRt.PropBool(pkg, tPkg, "IsFramework", false)) continue;
                    if (LWinRt.PropBool(pkg, tPkg, "IsResourcePackage", false)) continue;
                    if (LWinRt.PropBool(pkg, tPkg, "IsBundle", false)) continue;

                    string path = PackagePath(pkg, tPkg);
                    if (!LApi.DirExists(path)) continue;

                    string cfg = GameConfigPath(path);
                    if (cfg == null) continue;                    // not a game: skip silently

                    object id = LWinRt.Prop(pkg, tPkg, "Id");
                    string family = id == null ? null : LWinRt.PropStr(id, tPkgId, "FamilyName");
                    if (string.IsNullOrEmpty(family)) continue;

                    string title = null, aumid = null;
                    AppListEntry(pkg, tPkg, ref title, ref aumid);
                    if (string.IsNullOrEmpty(aumid))
                        aumid = family + "!" + AppIdFromManifest(path);
                    if (aumid.EndsWith("!", StringComparison.Ordinal)) continue;  // no launchable app

                    if (string.IsNullOrEmpty(title)) title = ShellVisualName(cfg);
                    if (string.IsNullOrEmpty(title)) title = LWinRt.PropStr(pkg, tPkg, "DisplayName");
                    if (string.IsNullOrEmpty(title) && id != null) title = LWinRt.PropStr(id, tPkgId, "Name");
                    if (string.IsNullOrEmpty(title)) continue;

                    LibEntry e = new LibEntry();
                    e.Id = "xbox:" + family;
                    e.Title = title;
                    e.Source = "xbox";
                    e.SourceLabel = "Xbox";
                    e.Kind = "game";
                    // AUMID through IApplicationActivationManager. NOT
                    // explorer.exe shell:AppsFolder\<aumid> - that starts a real Windows shell
                    // process on a machine that deliberately has none.
                    e.LaunchKind = "aumid";
                    e.LaunchTarget = aumid;
                    e.InstallDir = path;
                    e.IconPath = PackageLogo(path);
                    outList.Add(e);
                    src.Found++;
                }
                catch { }
            }
            src.Note = looked + " packages examined; a package counts as a game only when its "
                     + "install root has MicrosoftGame.config.";
        }

        static string PackagePath(object pkg, Type tPkg)
        {
            // InstalledPath is a plain string and cheap. InstalledLocation is a StorageFolder and
            // throws for packages whose payload is gone, which is precisely the case we filter.
            string p = null;
            try { p = LWinRt.PropStr(pkg, tPkg, "InstalledPath"); }
            catch { }
            if (!string.IsNullOrEmpty(p)) return p;
            try
            {
                object loc = LWinRt.Prop(pkg, tPkg, "InstalledLocation");
                Type tSf = LWinRt.Type_("Windows.Storage.StorageFolder");
                if (loc != null && tSf != null) p = LWinRt.PropStr(loc, tSf, "Path");
            }
            catch { }
            return p;
        }

        static string GameConfigPath(string root)
        {
            try
            {
                string a = Path.Combine(root, "MicrosoftGame.config");
                if (File.Exists(a)) return a;
                // Some titles register the package at the parent of Content\.
                string b = Path.Combine(root, @"Content\MicrosoftGame.config");
                if (File.Exists(b)) return b;
            }
            catch { }
            return null;
        }

        static readonly Regex RxDisplayName = new Regex("DefaultDisplayName\\s*=\\s*\"([^\"]+)\"",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex RxAppId = new Regex("<Application\\b[^>]*\\bId\\s*=\\s*\"([^\"]+)\"",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex RxLogo = new Regex("(Square150x150Logo|Square44x44Logo|Logo)\\s*=\\s*\"([^\"]+)\"",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        static string ShellVisualName(string cfgPath)
        {
            try
            {
                string t = File.ReadAllText(cfgPath);
                Match m = RxDisplayName.Match(t);
                if (m.Success) return m.Groups[1].Value;
            }
            catch { }
            return null;
        }

        static string AppIdFromManifest(string root)
        {
            try
            {
                string mf = Path.Combine(root, "AppxManifest.xml");
                if (!File.Exists(mf)) return "";
                string t = File.ReadAllText(mf);
                Match m = RxAppId.Match(t);
                return m.Success ? m.Groups[1].Value : "";
            }
            catch { return ""; }
        }

        static string PackageLogo(string root)
        {
            try
            {
                string mf = Path.Combine(root, "AppxManifest.xml");
                if (!File.Exists(mf)) return null;
                string t = File.ReadAllText(mf);
                foreach (Match m in RxLogo.Matches(t))
                {
                    string rel = m.Groups[2].Value.Replace('/', '\\');
                    string best = ResolveScaledAsset(root, rel);
                    if (best != null) return best;
                }
            }
            catch { }
            return null;
        }

        // "Assets\Square150x150Logo.png" is almost never on disk under that name; what is there is
        // Square150x150Logo.scale-200.png and friends. Take the largest variant that exists.
        static string ResolveScaledAsset(string root, string rel)
        {
            try
            {
                string full = Path.Combine(root, rel);
                if (File.Exists(full)) return full;
                string dir = Path.GetDirectoryName(full);
                string stem = Path.GetFileNameWithoutExtension(full);
                string ext = Path.GetExtension(full);
                if (!Directory.Exists(dir)) return null;
                string[] cands = Directory.GetFiles(dir, stem + ".*" + ext);
                string best = null;
                long bestLen = -1;
                for (int i = 0; i < cands.Length; i++)
                {
                    long len;
                    try { len = new FileInfo(cands[i]).Length; }
                    catch { continue; }
                    if (len > bestLen) { bestLen = len; best = cands[i]; }
                }
                return best;
            }
            catch { return null; }
        }

        static void AppListEntry(object pkg, Type tPkg, ref string title, ref string aumid)
        {
            try
            {
                string err;
                object list = LWinRt.CallInstanceAsync(pkg, tPkg, "GetAppListEntriesAsync", new object[0], 4000, out err);
                if (list == null) return;
                System.Collections.IEnumerable en = list as System.Collections.IEnumerable;
                if (en == null) return;
                Type tAle = LWinRt.Type_("Windows.ApplicationModel.Core.AppListEntry");
                Type tAdi = LWinRt.Type_("Windows.ApplicationModel.AppDisplayInfo");
                foreach (object ale in en)
                {
                    string a = LWinRt.PropStr(ale, tAle, "AppUserModelId");
                    if (string.IsNullOrEmpty(a)) continue;
                    aumid = a;
                    object di = LWinRt.Prop(ale, tAle, "DisplayInfo");
                    if (di != null) title = LWinRt.PropStr(di, tAdi, "DisplayName");
                    break;
                }
            }
            catch { }
        }

        // ----------------------------------------------------------------------------------
        // Start Menu shortcuts
        // ----------------------------------------------------------------------------------
        // On a machine with no store clients this is the only source that returns anything, which
        // makes its filtering the part most worth being strict about.
        static readonly string[] ShortcutFolderBlock = new string[] {
            "administrative tools", "windows administrative tools", "windows tools",
            "system tools", "accessibility", "windows powershell", "accessories",
            "windows accessories", "windows ease of access", "windows system",
            "maintenance", "startup", "windows kits", "windows sdk", "debugging tools"
        };

        static readonly string[] ShortcutNameBlock = new string[] {
            "uninstall", "readme", "read me", "release notes", "documentation", "docs",
            "help", "license", "licence", "changelog", "revision history", "faq",
            "website", "web site", "support", "manual", "manuals", "example scripts",
            "online documentation", "repair", "modify", "setup", "installer",
            "safe mode", "command prompt", "control panel", "file explorer",
            "run", "task manager", "registry editor"
        };

        // Directories that are launcher roots rather than games, used by both the shortcut source
        // (to decide gameness) and the games-folder source (to avoid describing a launcher as a
        // game).
        static readonly string[] GameRootMarkers = new string[] {
            @"\steamapps\common\", @"\epic games\", @"\gog galaxy\games\", @"\gog games\",
            @"\battle.net\", @"\riot games\", @"\ubisoft\", @"\ea games\", @"\ea\",
            @"\origin games\", @"\xboxgames\", @"\games\", @"\amazon games\"
        };

        static readonly string[] LauncherDirNames = new string[] {
            "riot games", "steam", "epic games", "gog galaxy", "battle.net", "ubisoft",
            "ubisoft game launcher", "ea games", "ea", "origin", "amazon games", "steamapps",
            "electronic arts", "rockstar games launcher"
        };

        public static void Shortcuts(List<LibEntry> outList, LibSource src)
        {
            string common = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs");
            string user = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
            src.Where = common + " ; " + user;
            src.Present = LApi.DirExists(common) || LApi.DirExists(user);
            if (!src.Present) { src.Note = "Neither Start Menu Programs folder exists."; return; }

            List<string> lnks = new List<string>();
            CollectLnk(common, lnks);
            CollectLnk(user, lnks);

            string windir = LApi.Expand("%SystemRoot%");
            List<LibEntry> found = new List<LibEntry>();
            string staError = null;

            // IShellLinkW is ThreadingModel=Apartment. One STA thread, all the links, joined.
            LSta.Run(delegate ()
            {
                LNative.IShellLinkW link = null;
                try
                {
                    link = (LNative.IShellLinkW)new LNative.ShellLink();
                }
                catch (Exception ex) { staError = "CoCreateInstance(ShellLink): " + LApi.Describe(ex); return; }

                LNative.IPersistFile pf = link as LNative.IPersistFile;
                if (pf == null) { staError = "ShellLink does not expose IPersistFile"; return; }

                StringBuilder buf = new StringBuilder(1040);
                for (int i = 0; i < lnks.Count; i++)
                {
                    try
                    {
                        string lnk = lnks[i];
                        string leaf = Path.GetFileNameWithoutExtension(lnk);
                        if (NameBlocked(leaf)) continue;
                        if (FolderBlocked(lnk, common, user)) continue;

                        pf.Load(lnk, LNative.STGM_READ);
                        // Deliberately no Resolve(): it can hit the network chasing a moved
                        // target and can show UI. GetPath on a loaded link is enough.
                        buf.Length = 0; buf.EnsureCapacity(1040);
                        link.GetPath(buf, 1024, IntPtr.Zero, LNative.SLGP_RAWPATH);
                        string target = LApi.Expand(buf.ToString());
                        if (string.IsNullOrEmpty(target)) continue;      // AppsFolder link, no file
                        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!LApi.FileExists(target)) continue;
                        if (!string.IsNullOrEmpty(windir) &&
                            target.StartsWith(windir, StringComparison.OrdinalIgnoreCase)) continue;

                        buf.Length = 0;
                        link.GetArguments(buf, 1024);
                        string args = buf.ToString();

                        buf.Length = 0;
                        link.GetWorkingDirectory(buf, 1024);
                        string wd = LApi.Expand(buf.ToString());

                        int iconIdx = 0;
                        buf.Length = 0;
                        link.GetIconLocation(buf, 1024, out iconIdx);
                        string iconPath = LApi.Expand(buf.ToString());
                        if (!LApi.FileExists(iconPath)) { iconPath = target; iconIdx = 0; }

                        LibEntry e = new LibEntry();
                        e.Id = "lnk:" + LApi.Hash(target + "|" + args);
                        e.Title = leaf;
                        e.Source = "shortcut";
                        e.SourceLabel = "Start Menu";
                        e.Kind = ClassifyShortcut(target, args, lnk);
                        e.LaunchKind = "exe";
                        e.LaunchTarget = target;
                        e.LaunchArgs = string.IsNullOrEmpty(args) ? null : args;
                        e.WorkDir = LApi.DirExists(wd) ? wd : Path.GetDirectoryName(target);
                        e.InstallDir = Path.GetDirectoryName(target);
                        e.IconPath = iconPath;
                        e.IconIndex = iconIdx;
                        found.Add(e);
                    }
                    catch { }
                }
            });

            if (staError != null) { src.Error = staError; return; }

            // Two Start Menus routinely carry the same shortcut (Steam does exactly this).
            Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < found.Count; i++)
            {
                if (seen.ContainsKey(found[i].Id)) continue;
                seen[found[i].Id] = true;
                outList.Add(found[i]);
                src.Found++;
            }
            src.Note = lnks.Count + " shortcuts examined";
        }

        static void CollectLnk(string root, List<string> into)
        {
            if (!LApi.DirExists(root)) return;
            try
            {
                string[] all = Directory.GetFiles(root, "*.lnk", SearchOption.AllDirectories);
                for (int i = 0; i < all.Length; i++) into.Add(all[i]);
            }
            catch { }
        }

        static bool NameBlocked(string leaf)
        {
            if (string.IsNullOrEmpty(leaf)) return true;
            string l = leaf.ToLowerInvariant();
            for (int i = 0; i < ShortcutNameBlock.Length; i++)
            {
                string b = ShortcutNameBlock[i];
                if (l == b) return true;
                if (l.StartsWith(b + " ", StringComparison.Ordinal)) return true;
                if (l.IndexOf("uninstall", StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        static bool FolderBlocked(string lnk, string common, string user)
        {
            try
            {
                string dir = Path.GetDirectoryName(lnk);
                if (dir == null) return false;
                string rel = dir;
                if (common != null && dir.StartsWith(common, StringComparison.OrdinalIgnoreCase))
                    rel = dir.Substring(common.Length);
                else if (user != null && dir.StartsWith(user, StringComparison.OrdinalIgnoreCase))
                    rel = dir.Substring(user.Length);
                string[] parts = rel.Split(new char[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                for (int p = 0; p < parts.Length; p++)
                {
                    string seg = parts[p].ToLowerInvariant();
                    for (int b = 0; b < ShortcutFolderBlock.Length; b++)
                        if (seg == ShortcutFolderBlock[b]) return true;
                }
            }
            catch { }
            return false;
        }

        // The store clients themselves. A shortcut to one of these is a launcher, not a game -
        // "Steam" must not sit on the rail claiming to be something you can play.
        static readonly string[] LauncherExeNames = new string[] {
            "steam", "epicgameslauncher", "galaxyclient", "battle.net", "upc",
            "ubisoftconnect", "eadesktop", "eaapp", "origin", "riotclientservices",
            "amazon games", "rockstar games launcher", "playnite.desktopapp"
        };

        // Gameness from a shortcut alone is guesswork, so it is decided by WHERE the target lives
        // and which vendor folder the shortcut sits in - both facts about the install - rather
        // than by anything in the name. Anything unproven is "app", never "game": the Play tab
        // showing too little is a smaller lie than the Play tab showing Git Bash.
        static string ClassifyShortcut(string target, string args, string lnkPath)
        {
            string exe = "";
            try { exe = Path.GetFileNameWithoutExtension(target).ToLowerInvariant(); }
            catch { }

            bool launcherExe = false;
            for (int i = 0; i < LauncherExeNames.Length; i++)
                if (exe == LauncherExeNames[i]) { launcherExe = true; break; }

            // RiotClientServices.exe is BOTH: bare it is the client, with --launch-product it is
            // a specific game. Same shape for Ubisoft's upc.exe (-uplay_steam_mode / uplay://).
            bool launchesATitle = !string.IsNullOrEmpty(args) &&
                (args.IndexOf("--launch-product", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 args.IndexOf("uplay://", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 args.IndexOf("-applaunch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 args.IndexOf("origin2://", StringComparison.OrdinalIgnoreCase) >= 0);

            if (launcherExe && !launchesATitle) return "launcher";

            string t = ("\\" + target.ToLowerInvariant()).Replace("//", "\\");
            for (int i = 0; i < GameRootMarkers.Length; i++)
                if (t.IndexOf(GameRootMarkers[i], StringComparison.Ordinal) >= 0) return "game";
            string l = lnkPath.ToLowerInvariant();
            for (int i = 0; i < LauncherDirNames.Length; i++)
                if (l.IndexOf("\\" + LauncherDirNames[i] + "\\", StringComparison.Ordinal) >= 0) return "game";
            return "app";
        }

        // ----------------------------------------------------------------------------------
        // Loose installs in games folders
        // ----------------------------------------------------------------------------------
        // The case this exists for: a standalone build dropped in C:\Games that never wrote a
        // shortcut, a registry key or a manifest. Bounded on purpose - immediate children of a
        // handful of roots, exe search no deeper than three levels - because an unbounded disk
        // walk on a shell's startup path is not acceptable.
        static readonly string[] ExeNameBlock = new string[] {
            "unins", "uninstall", "setup", "install", "vcredist", "vc_redist", "dxsetup",
            "dxwebsetup", "dotnetfx", "directx", "oalinst", "quicksfv", "crashhandler",
            "crashreport", "crashpad", "eossetup", "ueprereqsetup", "prereq", "redist",
            "activation", "patcher", "updater", "config", "settings", "benchmark"
        };

        public static void GameFolders(List<LibEntry> outList, LibSource src, List<string> roots)
        {
            if (roots == null || roots.Count == 0) roots = DefaultGameRoots();
            List<string> live = new List<string>();
            for (int i = 0; i < roots.Count; i++)
                if (LApi.DirExists(roots[i])) live.Add(roots[i]);

            src.Where = string.Join(" ; ", live.ToArray());
            if (live.Count == 0)
            {
                src.Note = "No games folder exists on this machine (looked for <drive>\\Games, "
                         + "<drive>\\XboxGames and %USERPROFILE%\\Games).";
                return;
            }
            src.Present = true;

            for (int r = 0; r < live.Count; r++)
            {
                string[] subs;
                try { subs = Directory.GetDirectories(live[r]); }
                catch { continue; }

                for (int i = 0; i < subs.Length; i++)
                {
                    try
                    {
                        string name = Path.GetFileName(subs[i]);
                        if (IsLauncherDir(name)) continue;        // C:\Games\Riot Games is not a game
                        string exe = PickGameExe(subs[i], name);
                        if (exe == null) continue;

                        LibEntry e = new LibEntry();
                        e.Id = "dir:" + LApi.Hash(subs[i]);
                        e.Title = name;
                        e.Source = "folder";
                        e.SourceLabel = "Installed folder";
                        e.Kind = "game";
                        e.LaunchKind = "exe";
                        e.LaunchTarget = exe;
                        e.WorkDir = Path.GetDirectoryName(exe);
                        e.InstallDir = subs[i];
                        e.IconPath = exe;
                        outList.Add(e);
                        src.Found++;
                    }
                    catch { }
                }
            }
        }

        public static List<string> DefaultGameRoots()
        {
            List<string> roots = new List<string>();
            try
            {
                DriveInfo[] drives = DriveInfo.GetDrives();
                for (int i = 0; i < drives.Length; i++)
                {
                    try
                    {
                        if (drives[i].DriveType != DriveType.Fixed || !drives[i].IsReady) continue;
                        string r = drives[i].RootDirectory.FullName;
                        roots.Add(Path.Combine(r, "Games"));
                        roots.Add(Path.Combine(r, "XboxGames"));
                    }
                    catch { }
                }
            }
            catch { }
            try
            {
                roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games"));
            }
            catch { }
            return roots;
        }

        static bool IsLauncherDir(string name)
        {
            string n = (name == null ? "" : name).ToLowerInvariant();
            for (int i = 0; i < LauncherDirNames.Length; i++)
                if (n == LauncherDirNames[i]) return true;
            return false;
        }

        // Which exe in a game folder is THE game. Name-match against the folder first, because
        // "TEKKEN 8\TEKKEN 8.exe" is 196 KB while its uninstaller is 1.5 MB - picking the biggest
        // binary gets that exactly backwards. If nothing matches and there is more than one
        // candidate, report nothing rather than guess.
        static string PickGameExe(string dir, string folderName)
        {
            string want = Norm(folderName);

            string[] rootExes;
            try { rootExes = Directory.GetFiles(dir, "*.exe"); }
            catch { return null; }

            for (int i = 0; i < rootExes.Length; i++)
                if (Norm(Path.GetFileNameWithoutExtension(rootExes[i])) == want) return rootExes[i];

            List<string> plausible = new List<string>();
            for (int i = 0; i < rootExes.Length; i++)
                if (!ExeBlocked(rootExes[i])) plausible.Add(rootExes[i]);
            if (plausible.Count == 1) return plausible[0];

            // Xbox/GDK layout puts the payload under Content\, and Unreal under
            // <Game>\Binaries\Win64\. Both are within three levels.
            string[] deep;
            try { deep = SafeFiles(dir, "*.exe", 3); }
            catch { return null; }
            for (int i = 0; i < deep.Length; i++)
                if (Norm(Path.GetFileNameWithoutExtension(deep[i])) == want && !ExeBlocked(deep[i])) return deep[i];

            if (plausible.Count > 1) return null;   // ambiguous: say nothing
            return null;
        }

        static string[] SafeFiles(string root, string pattern, int maxDepth)
        {
            List<string> outp = new List<string>();
            Walk(root, pattern, maxDepth, 0, outp);
            return outp.ToArray();
        }

        static void Walk(string dir, string pattern, int maxDepth, int depth, List<string> outp)
        {
            if (depth > maxDepth || outp.Count > 4000) return;
            try
            {
                string[] fs = Directory.GetFiles(dir, pattern);
                for (int i = 0; i < fs.Length; i++) outp.Add(fs[i]);
            }
            catch { }
            if (depth == maxDepth) return;
            try
            {
                string[] ds = Directory.GetDirectories(dir);
                for (int i = 0; i < ds.Length; i++) Walk(ds[i], pattern, maxDepth, depth + 1, outp);
            }
            catch { }
        }

        static bool ExeBlocked(string path)
        {
            string n = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            for (int i = 0; i < ExeNameBlock.Length; i++)
                if (n.IndexOf(ExeNameBlock[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        static string Norm(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = char.ToLowerInvariant(s[i]);
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
            }
            return sb.ToString();
        }

        // "C:\path\thing.exe,3" -> path + index. Uninstall keys and shortcuts both use this form.
        public static string SplitIconSpec(string spec, out int index)
        {
            index = 0;
            if (string.IsNullOrEmpty(spec)) return null;
            string s = LApi.Expand(spec.Trim().Trim('"'));
            int comma = s.LastIndexOf(',');
            if (comma > 2)
            {
                string tail = s.Substring(comma + 1).Trim();
                int idx;
                if (int.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out idx))
                {
                    index = idx;
                    s = s.Substring(0, comma).Trim().Trim('"');
                }
            }
            return s;
        }
    }

    #endregion

    #region Icons

    internal static class IconMod
    {
        // Everything ends up as a PNG data URI, which is the only form the page can put straight
        // into a style attribute. Files that are ALREADY web-renderable (the .png and .jpg Steam
        // downloaded, a package's logo asset) are passed through byte-for-byte at their own mime
        // type when no resize is wanted, because re-encoding a 600x900 jpeg to PNG would quadruple
        // the payload for no visible gain.
        public static LJ Render(string path, int iconIndex, int maxSize, bool passthrough)
        {
            if (string.IsNullOrEmpty(path) || !LApi.FileExists(path))
                throw new LibFault("no_icon", "There is no icon file at '" + (path == null ? "" : path) + "'.");

            string ext = "";
            try { ext = Path.GetExtension(path).ToLowerInvariant(); }
            catch { }

            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".bmp")
            {
                byte[] raw;
                try { raw = File.ReadAllBytes(path); }
                catch (Exception ex) { throw new LibFault("icon_read_failed", LApi.Describe(ex)); }

                if (passthrough && raw.Length <= 512 * 1024)
                {
                    LJ o = LJ.Obj();
                    o.Set("dataUri", "data:" + Mime(ext) + ";base64," + Convert.ToBase64String(raw));
                    o.Set("source", "file");
                    o.Set("path", path);
                    o.Set("bytes", raw.Length);
                    return o;
                }
                using (MemoryStream ms = new MemoryStream(raw))
                using (Image img = Image.FromStream(ms))
                using (Bitmap fit = Fit(img, maxSize))
                    return Emit(fit, "file", path);
            }

            // .exe, .dll, .ico, .lnk-target: pull the icon out of the binary.
            IntPtr h = Extract(path, iconIndex, maxSize);
            if (h == IntPtr.Zero)
                throw new LibFault("no_icon", "'" + path + "' has no extractable icon at index " + iconIndex + ".");
            try
            {
                using (Icon ico = Icon.FromHandle(h))
                using (Bitmap bmp = ico.ToBitmap())
                using (Bitmap fit = Fit(bmp, maxSize))
                    return Emit(fit, "extracted", path);
            }
            finally { LNative.DestroyIcon(h); }
        }

        // Best-effort variant for the scan: never throws, returns null when there is nothing.
        public static string DataUriOrNull(string path, int iconIndex, int maxSize)
        {
            try
            {
                LJ o = Render(path, iconIndex, maxSize, false);
                return o.S("dataUri", null);
            }
            catch { return null; }
        }

        static string Mime(string ext)
        {
            if (ext == ".jpg" || ext == ".jpeg") return "image/jpeg";
            if (ext == ".gif") return "image/gif";
            if (ext == ".bmp") return "image/bmp";
            return "image/png";
        }

        static LJ Emit(Bitmap bmp, string source, string path)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                byte[] raw = ms.ToArray();
                LJ o = LJ.Obj();
                o.Set("dataUri", "data:image/png;base64," + Convert.ToBase64String(raw));
                o.Set("source", source);
                o.Set("path", path);
                o.Set("width", bmp.Width);
                o.Set("height", bmp.Height);
                o.Set("bytes", raw.Length);
                return o;
            }
        }

        static IntPtr Extract(string path, int index, int size)
        {
            // PrivateExtractIcons is the only one that honours a requested size, which matters on
            // a television: ExtractIconEx tops out at 32x32.
            int want = size < 16 ? 16 : (size > 256 ? 256 : size);
            IntPtr[] h = new IntPtr[1];
            int[] ids = new int[1];
            try
            {
                int n = LNative.PrivateExtractIconsW(path, index, want, want, h, ids, 1, 0);
                if (n > 0 && h[0] != IntPtr.Zero) return h[0];
            }
            catch { }

            IntPtr[] big = new IntPtr[1];
            IntPtr[] small = new IntPtr[1];
            try
            {
                int n = LNative.ExtractIconExW(path, index, big, small, 1);
                if (n > 0 && big[0] != IntPtr.Zero)
                {
                    if (small[0] != IntPtr.Zero) LNative.DestroyIcon(small[0]);
                    return big[0];
                }
                if (small[0] != IntPtr.Zero) return small[0];
            }
            catch { }
            return IntPtr.Zero;
        }

        static Bitmap Fit(Image src, int max)
        {
            int w = src.Width, h = src.Height;
            if (w <= max && h <= max) return new Bitmap(src);
            double s = Math.Min((double)max / w, (double)max / h);
            int nw = Math.Max(1, (int)Math.Round(w * s));
            int nh = Math.Max(1, (int)Math.Round(h * s));
            Bitmap b = new Bitmap(nw, nh, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, nw, nh));
            }
            return b;
        }
    }

    #endregion

    #region Cache

    internal static class CacheMod
    {
        public static string Dir()
        {
            string b = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(b, "ArcOS");
        }

        public static string Path_() { return System.IO.Path.Combine(Dir(), "library.json"); }

        // Written to a sibling temp file then moved into place, so a scan interrupted by a
        // power cut cannot leave a half-written cache that the next boot would fail to parse.
        public static void Save(LJ doc)
        {
            try
            {
                string dir = Dir();
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string tmp = Path_() + ".tmp";
                File.WriteAllText(tmp, doc.ToJson(), new UTF8Encoding(false));
                string final = Path_();
                if (File.Exists(final)) File.Delete(final);
                File.Move(tmp, final);
            }
            catch { /* a cache we cannot write is a slow boot, not a broken shell */ }
        }

        public static LJ Load()
        {
            try
            {
                string p = Path_();
                if (!File.Exists(p)) return null;
                return LJ.Parse(File.ReadAllText(p));
            }
            catch { return null; }
        }
    }

    #endregion

    #region Launching

    internal static class LaunchMod
    {
        public static LJ Launch(LibEntry e, string extraArgs)
        {
            LJ o = LJ.Obj();
            o.Set("id", e.Id);
            o.Set("title", e.Title);
            o.Set("launchKind", e.LaunchKind);
            o.Set("launchTarget", e.LaunchTarget);

            if (string.Equals(e.LaunchKind, "aumid", StringComparison.OrdinalIgnoreCase))
            {
                uint pid = 0;
                int hr = 0;
                LSta.Run(delegate ()
                {
                    LNative.IApplicationActivationManager aam =
                        (LNative.IApplicationActivationManager)new LNative.ApplicationActivationManager();
                    hr = aam.ActivateApplication(e.LaunchTarget, extraArgs == null ? "" : extraArgs,
                                                 LNative.AO_NONE, out pid);
                });
                if (hr < 0)
                {
                    string hint = "";
                    if ((uint)hr == 0x80070005u && LApi.IsElevated())
                        hint = " The caller is elevated; a packaged app cannot be activated from an "
                             + "elevated process. Run the shell as a standard user.";
                    throw new LibFault("activation_failed",
                        "ActivateApplication('" + e.LaunchTarget + "') failed: 0x" +
                        hr.ToString("X8", CultureInfo.InvariantCulture) + "." + hint);
                }
                o.Set("pid", (long)pid);
                // The AUMID activation returns the app's OWN process, so the host may job-track it.
                o.Set("pidIsTarget", true);
                o.Set("trackable", true);
                o.Set("detail", "Packaged app activated through IApplicationActivationManager. "
                              + "The returned pid is the application itself.");
                return o;
            }

            bool isUri = string.Equals(e.LaunchKind, "uri", StringComparison.OrdinalIgnoreCase);
            string file = e.LaunchTarget;
            string args = e.LaunchArgs;
            if (!string.IsNullOrEmpty(extraArgs))
                args = string.IsNullOrEmpty(args) ? extraArgs : (args + " " + extraArgs);

            if (!isUri)
            {
                if (!LApi.FileExists(file))
                    throw new LibFault("not_installed",
                        "'" + e.Title + "' points at '" + file + "', which is no longer there. Rescan the library.");
            }

            uint pid2 = 0;
            uint gle = 0;
            bool ok = false;
            LSta.Run(delegate ()
            {
                LNative.SHELLEXECUTEINFO si = new LNative.SHELLEXECUTEINFO();
                si.cbSize = Marshal.SizeOf(typeof(LNative.SHELLEXECUTEINFO));
                si.fMask = LNative.SEE_MASK_NOCLOSEPROCESS | LNative.SEE_MASK_FLAG_NO_UI | LNative.SEE_MASK_NOASYNC;
                si.hwnd = IntPtr.Zero;
                si.lpVerb = "open";
                si.lpFile = file;
                si.lpParameters = string.IsNullOrEmpty(args) ? null : args;
                si.lpDirectory = LApi.DirExists(e.WorkDir) ? e.WorkDir : null;
                si.nShow = LNative.SW_SHOWNORMAL;
                ok = LNative.ShellExecuteExW(ref si);
                if (!ok) gle = (uint)Marshal.GetLastWin32Error();
                else if (si.hProcess != IntPtr.Zero)
                {
                    pid2 = LNative.GetProcessId(si.hProcess);
                    LNative.CloseHandle(si.hProcess);
                }
            });

            if (!ok)
            {
                string why = LApi.Win32(gle);
                if (isUri && gle == 1155)
                    why += " - no application is registered for the '" +
                           file.Substring(0, Math.Max(0, file.IndexOf(':'))) + ":' protocol. Is the launcher installed?";
                throw new LibFault("launch_failed", "ShellExecuteEx failed for '" + file + "': " + why);
            }

            if (pid2 != 0) o.Set("pid", (long)pid2); else o.SetNull("pid");
            o.Set("pidIsTarget", !isUri);
            o.Set("trackable", !isUri);
            if (isUri)
            {
                // This is the bit the shell's job-object tracking has to know. steam:// and
                // com.epicgames.launcher:// resolve to the launcher's own exe, which hands the
                // request to the already-running client and exits within a second or two. The pid
                // we return is that stub, NOT the game. Putting it in a job object and waiting on
                // the job will report "finished" almost immediately and would take the shell back
                // to the rail while the game is still loading.
                o.Set("detail", "URI launch. The pid returned is the protocol handler that Windows "
                              + "started (the launcher stub); it exits as soon as it has handed the "
                              + "request to the client. Do NOT treat its exit as the game exiting.");
            }
            else
            {
                o.Set("detail", "Direct executable launch. The pid returned is the process itself and "
                              + "is safe to assign to a job object.");
            }
            return o;
        }
    }

    #endregion

    #region Scan

    internal static class ScanMod
    {
        // Last scan, in memory, keyed by id. lib.icon and lib.launch resolve ids through here;
        // if it is empty (fresh process, no scan yet) the on-disk cache is loaded into it.
        static readonly object Gate = new object();
        static Dictionary<string, LibEntry> ById = new Dictionary<string, LibEntry>(StringComparer.OrdinalIgnoreCase);
        static LJ LastDoc;

        public static LibEntry Resolve(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (Gate)
            {
                if (ById.Count == 0) LoadCacheLocked();
                LibEntry e;
                if (ById.TryGetValue(id, out e)) return e;
                return null;
            }
        }

        public static LJ Cached()
        {
            lock (Gate)
            {
                if (LastDoc == null) LoadCacheLocked();
                return LastDoc;
            }
        }

        static void LoadCacheLocked()
        {
            LJ doc = CacheMod.Load();
            if (doc == null) return;
            LastDoc = doc;
            LJ arr = doc.Get("entries");
            if (arr == null || arr.Kind != LJ.TArr) return;
            Dictionary<string, LibEntry> map = new Dictionary<string, LibEntry>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < arr.Count; i++)
            {
                LibEntry e = LibEntry.FromJson(arr.At(i));
                if (e != null && !string.IsNullOrEmpty(e.Id)) map[e.Id] = e;
            }
            ById = map;
        }

        public static LJ Run(LJobs.Job job, bool wantIcons, int iconSize, List<string> roots)
        {
            List<LibEntry> entries = new List<LibEntry>();
            List<LibSource> srcs = new List<LibSource>();
            DateTime t0 = DateTime.UtcNow;

            RunOne(job, srcs, entries, "steam", "Steam", 8, delegate (List<LibEntry> o, LibSource s) { Sources.Steam(o, s); });
            if (job.CancelRequested) return Finish(job, entries, srcs, t0, wantIcons, iconSize);
            RunOne(job, srcs, entries, "epic", "Epic Games", 20, delegate (List<LibEntry> o, LibSource s) { Sources.Epic(o, s); });
            if (job.CancelRequested) return Finish(job, entries, srcs, t0, wantIcons, iconSize);
            RunOne(job, srcs, entries, "gog", "GOG", 28, delegate (List<LibEntry> o, LibSource s) { Sources.Gog(o, s); });
            if (job.CancelRequested) return Finish(job, entries, srcs, t0, wantIcons, iconSize);
            RunOne(job, srcs, entries, "battlenet", "Battle.net", 36, delegate (List<LibEntry> o, LibSource s) { Sources.BattleNet(o, s); });
            if (job.CancelRequested) return Finish(job, entries, srcs, t0, wantIcons, iconSize);
            RunOne(job, srcs, entries, "xbox", "Xbox", 55, delegate (List<LibEntry> o, LibSource s) { Sources.Xbox(o, s); });
            if (job.CancelRequested) return Finish(job, entries, srcs, t0, wantIcons, iconSize);
            RunOne(job, srcs, entries, "shortcut", "Start Menu", 75, delegate (List<LibEntry> o, LibSource s) { Sources.Shortcuts(o, s); });
            if (job.CancelRequested) return Finish(job, entries, srcs, t0, wantIcons, iconSize);
            List<string> r = roots;
            RunOne(job, srcs, entries, "folder", "Installed folders", 85, delegate (List<LibEntry> o, LibSource s) { Sources.GameFolders(o, s, r); });

            return Finish(job, entries, srcs, t0, wantIcons, iconSize);
        }

        delegate void SourceFn(List<LibEntry> outList, LibSource src);

        static void RunOne(LJobs.Job job, List<LibSource> srcs, List<LibEntry> entries,
                           string name, string label, int pct, SourceFn fn)
        {
            LibSource s = new LibSource();
            s.Name = name;
            s.Label = label;
            job.Report(pct, "scanning " + label);
            Stopwatch sw = Stopwatch.StartNew();
            try { fn(entries, s); }
            catch (Exception ex) { s.Error = LApi.Describe(ex); }
            sw.Stop();
            s.Ms = sw.ElapsedMilliseconds;
            srcs.Add(s);
        }

        static LJ Finish(LJobs.Job job, List<LibEntry> entries, List<LibSource> srcs,
                         DateTime t0, bool wantIcons, int iconSize)
        {
            job.Report(90, "de-duplicating");
            List<LibEntry> keep = Dedupe(entries);

            if (wantIcons)
            {
                job.Report(94, "reading icons");
                for (int i = 0; i < keep.Count; i++)
                {
                    if (job.CancelRequested) break;
                    LibEntry e = keep[i];
                    string p = e.IconPath;
                    int idx = e.IconIndex;
                    if (string.IsNullOrEmpty(p)) { p = e.ArtPath; idx = 0; }
                    if (string.IsNullOrEmpty(p)) continue;
                    e.Icon = IconMod.DataUriOrNull(p, idx, iconSize);
                }
            }

            keep.Sort(delegate (LibEntry a, LibEntry b)
            {
                // Most recently played first when we know, then alphabetical. Everything without a
                // last-played timestamp sorts after everything with one, rather than pretending
                // the epoch is a play date.
                bool ha = a.LastPlayedUtc > DateTime.MinValue;
                bool hb = b.LastPlayedUtc > DateTime.MinValue;
                if (ha != hb) return ha ? -1 : 1;
                if (ha && hb)
                {
                    int c = b.LastPlayedUtc.CompareTo(a.LastPlayedUtc);
                    if (c != 0) return c;
                }
                return string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);
            });

            job.Report(98, "writing cache");

            LJ doc = LJ.Obj();
            doc.Set("scannedUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            doc.Set("machine", Environment.MachineName);
            doc.Set("user", Environment.UserName);
            doc.Set("elevated", LApi.IsElevated());
            doc.Set("elapsedMs", (long)(DateTime.UtcNow - t0).TotalMilliseconds);
            doc.Set("iconSize", wantIcons ? iconSize : 0);

            int games = 0, apps = 0, launchers = 0;
            LJ arr = LJ.Arr();
            LJ cacheArr = LJ.Arr();
            for (int i = 0; i < keep.Count; i++)
            {
                arr.Add(keep[i].ToJson(true));
                cacheArr.Add(keep[i].ToCacheJson());
                if (keep[i].Kind == "game") games++;
                else if (keep[i].Kind == "launcher") launchers++;
                else apps++;
            }
            doc.Set("counts", LJ.Obj().Set("total", keep.Count).Set("games", games)
                                      .Set("launchers", launchers).Set("apps", apps));

            LJ sarr = LJ.Arr();
            for (int i = 0; i < srcs.Count; i++) sarr.Add(srcs[i].ToJson());
            doc.Set("sources", sarr);
            doc.Set("entries", arr);

            LJ cacheDoc = LJ.Obj();
            for (int i = 0; i < doc.Count; i++)
            {
                string k = doc.KeyAt(i);
                if (k == "entries") continue;
                cacheDoc.Set(k, doc.Get(k));
            }
            cacheDoc.Set("entries", cacheArr);
            CacheMod.Save(cacheDoc);

            lock (Gate)
            {
                Dictionary<string, LibEntry> map = new Dictionary<string, LibEntry>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < keep.Count; i++) map[keep[i].Id] = keep[i];
                ById = map;
                LastDoc = doc;
            }
            return doc;
        }

        // The same game legitimately shows up twice: a Steam title also has a desktop-installed
        // Start Menu shortcut, and C:\Games\X may be both a folder find and a shortcut target.
        // Source order below is the trust order - the store entry knows the appid, the play time
        // and the artwork, so it wins and the shortcut is dropped.
        static readonly string[] SourceRank = new string[] { "steam", "epic", "xbox", "gog", "battlenet", "folder", "shortcut" };

        static int Rank(string s)
        {
            for (int i = 0; i < SourceRank.Length; i++) if (SourceRank[i] == s) return i;
            return 99;
        }

        static List<LibEntry> Dedupe(List<LibEntry> all)
        {
            all.Sort(delegate (LibEntry a, LibEntry b) { return Rank(a.Source).CompareTo(Rank(b.Source)); });

            List<LibEntry> keep = new List<LibEntry>();
            Dictionary<string, bool> byId = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < all.Count; i++)
            {
                LibEntry e = all[i];
                if (string.IsNullOrEmpty(e.Id) || byId.ContainsKey(e.Id)) continue;

                bool dup = false;
                for (int n = 0; n < keep.Count && !dup; n++) dup = IsSameThing(keep[n], e);
                if (dup) continue;

                byId[e.Id] = true;
                keep.Add(e);
            }
            return keep;
        }

        // `kept` was admitted first, so it comes from the more trusted source. Two rules, and the
        // second one is deliberately restricted to CROSS-source pairs:
        //
        //  1. Identical command line. Same exe, same arguments, twice - the all-users and per-user
        //     Start Menus both carrying Steam.lnk is the everyday case.
        //  2. The candidate launches something that lives inside an entry we already have. A
        //     desktop shortcut to steamapps\common\Palworld\Palworld.exe is the Steam entry we
        //     already listed, so it goes.
        //
        // Rule 2 must not apply within a source, and that restriction is load-bearing: Riot's
        // three Start Menu shortcuts are all RiotClientServices.exe in one directory and differ
        // only by --launch-product, and a containment rule applied inside the shortcut source
        // silently collapses League of Legends, TFT and 2XKO into whichever sorted first. Same for
        // Git Bash / Git CMD / Git GUI out of one Program Files\Git.
        static bool IsSameThing(LibEntry kept, LibEntry cand)
        {
            bool keptExe = string.Equals(kept.LaunchKind, "exe", StringComparison.OrdinalIgnoreCase);
            bool candExe = string.Equals(cand.LaunchKind, "exe", StringComparison.OrdinalIgnoreCase);

            if (keptExe && candExe &&
                string.Equals(kept.LaunchTarget, cand.LaunchTarget, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Arg(kept), Arg(cand), StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(kept.Source, cand.Source, StringComparison.OrdinalIgnoreCase)) return false;
            if (string.IsNullOrEmpty(kept.InstallDir)) return false;

            string root = kept.InstallDir.TrimEnd('\\').ToLowerInvariant();
            if (candExe && !string.IsNullOrEmpty(cand.LaunchTarget))
            {
                string x = cand.LaunchTarget.ToLowerInvariant();
                if (x.StartsWith(root + "\\", StringComparison.Ordinal)) return true;
            }
            if (!string.IsNullOrEmpty(cand.InstallDir))
            {
                string d = cand.InstallDir.TrimEnd('\\').ToLowerInvariant();
                if (d == root || d.StartsWith(root + "\\", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        static string Arg(LibEntry e) { return e.LaunchArgs == null ? "" : e.LaunchArgs.Trim(); }

        // Fast, synchronous "which launchers exist" probe. No file walking, no WinRT.
        public static LJ Probe()
        {
            LJ arr = LJ.Arr();
            arr.Add(Probe1("steam", "Steam", Sources.SteamInstallPath()));
            arr.Add(Probe1("epic", "Epic Games", Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                @"Epic\EpicGamesLauncher\Data\Manifests")));
            string gog = null;
            RegistryKey k = LReg.Open(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\GOG.com\Games");
            if (k != null) { gog = @"HKLM\SOFTWARE\WOW6432Node\GOG.com\Games"; k.Close(); }
            arr.Add(Probe1("gog", "GOG", gog));
            string bnet = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                @"Battle.net\Agent");
            arr.Add(Probe1("battlenet", "Battle.net", bnet));
            arr.Add(Probe1("shortcut", "Start Menu", Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs")));

            List<string> roots = Sources.DefaultGameRoots();
            string live = null;
            for (int i = 0; i < roots.Count; i++) if (LApi.DirExists(roots[i])) { live = roots[i]; break; }
            arr.Add(Probe1("folder", "Installed folders", live));

            LJ x = LJ.Obj();
            x.Set("name", "xbox");
            x.Set("label", "Xbox");
            x.Set("present", LWinRt.Type_("Windows.Management.Deployment.PackageManager") != null);
            x.Set("where", "WinRT PackageManager");
            arr.Add(x);

            return LJ.Obj().Set("sources", arr);
        }

        static LJ Probe1(string name, string label, string where)
        {
            bool present = where != null && (LApi.DirExists(where) || where.StartsWith("HKLM", StringComparison.Ordinal));
            LJ o = LJ.Obj();
            o.Set("name", name);
            o.Set("label", label);
            o.Set("present", present);
            if (where != null) o.Set("where", where);
            return o;
        }
    }

    #endregion

    #region Public entry point

    public static class LibraryApi
    {
        public const string ApiVersion = "1.0";

        // ------------------------------------------------------------------------------------
        // The one and only boundary. Never throws.
        // ------------------------------------------------------------------------------------
        public static string Handle(string commandJson)
        {
            string id = null;
            string cmd = null;
            try
            {
                LJ req;
                try { req = ParseRequest(commandJson); }
                catch (Exception ex) { return Envelope(false, null, null, "bad_json", LApi.Describe(ex)); }

                id = req.S("reqId", null);
                cmd = req.S("cmd", null);
                if (string.IsNullOrEmpty(cmd))
                    return Envelope(false, id, null, "bad_request", "missing \"cmd\"");

                LJ data = Dispatch(cmd.Trim(), req);
                return Envelope(true, id, data, null, null);
            }
            catch (LibFault f)
            {
                return Envelope(false, id, f.Extra, f.Code, f.Message);
            }
            catch (Exception ex)
            {
                string trace = "";
                try
                {
                    string st = ex.StackTrace;
                    if (st != null)
                    {
                        int nl = st.IndexOf('\n');
                        trace = " @ " + (nl > 0 ? st.Substring(0, nl) : st).Trim();
                    }
                }
                catch { }
                return Envelope(false, id, null, "internal",
                    "unhandled in '" + (cmd == null ? "?" : cmd) + "': " + LApi.Describe(ex) + trace);
            }
        }

        static LJ ParseRequest(string text)
        {
            if (text == null) throw new FormatException("null command");
            string t = text.Trim();
            if (t.Length == 0) throw new FormatException("empty command");
            if (t[0] != '{')
            {
                LJ o = LJ.Obj();
                o.Set("cmd", t);
                return o;
            }
            LJ parsed = LJ.Parse(t);
            if (parsed.Kind != LJ.TObj) throw new FormatException("command must be a JSON object");
            return parsed;
        }

        static string Envelope(bool ok, string id, LJ data, string error, string detail)
        {
            LJ o = LJ.Obj();
            o.Set("ok", ok);
            if (id != null) o.Set("reqId", id);
            if (ok) o.Set("data", data == null ? LJ.Obj() : data);
            else
            {
                o.Set("error", error == null ? "error" : error);
                o.Set("detail", detail == null ? "" : detail);
                if (data != null) o.Set("data", data);
            }
            return o.ToJson();
        }

        static LJ StartJob(string cmd, Func<LJobs.Job, LJ> work)
        {
            LJobs.Job j = LJobs.Start(cmd, work);
            LJ o = LJ.Obj();
            o.Set("jobId", j.Id);
            o.Set("state", j.State);
            o.Set("async", true);
            o.Set("poll", "{\"cmd\":\"job.status\",\"jobId\":\"" + j.Id + "\"}");
            return o;
        }

        // ------------------------------------------------------------------------------------
        static LJ Dispatch(string cmd, LJ q)
        {
            switch (cmd.ToLowerInvariant())
            {
                // ---------------- meta ----------------
                case "api.version":
                    {
                        LJ o = LJ.Obj();
                        o.Set("apiVersion", ApiVersion);
                        o.Set("api", "LibraryApi");
                        o.Set("elevated", LApi.IsElevated());
                        o.Set("user", Environment.UserName);
                        o.Set("machine", Environment.MachineName);
                        o.Set("cache", CacheMod.Path_());
                        return o;
                    }
                case "api.commands":
                    return Catalog();

                // ---------------- jobs ----------------
                case "job.status":
                    {
                        LJobs.Job j = LJobs.Find(q.S("jobId", null));
                        if (j == null) throw new LibFault("no_such_job", "There is no job called '" + q.S("jobId", "") + "'.");
                        return LJobs.Describe(j);
                    }
                case "job.list":
                    {
                        List<LJobs.Job> all = LJobs.All();
                        LJ arr = LJ.Arr();
                        for (int i = 0; i < all.Count; i++) arr.Add(LJobs.Describe(all[i]));
                        return LJ.Obj().Set("jobs", arr).Set("count", arr.Count);
                    }
                case "job.cancel":
                    {
                        LJobs.Job j = LJobs.Find(q.S("jobId", null));
                        if (j == null) throw new LibFault("no_such_job", "There is no job called '" + q.S("jobId", "") + "'.");
                        j.RequestCancel();
                        return LJobs.Describe(j);
                    }

                // ---------------- library ----------------
                case "lib.sources":
                    return ScanMod.Probe();

                case "lib.scan":
                    {
                        bool icons = q.B("icons", true);
                        int size = q.I("iconSize", 96);
                        if (size < 16) size = 16;
                        if (size > 256) size = 256;
                        List<string> roots = null;
                        LJ r = q.Get("roots");
                        if (r != null && r.Kind == LJ.TArr)
                        {
                            roots = new List<string>();
                            for (int i = 0; i < r.Count; i++)
                            {
                                string p = r.At(i).AsStr(null);
                                if (!string.IsNullOrEmpty(p)) roots.Add(LApi.Full(p));
                            }
                        }
                        bool sync = q.B("sync", false);
                        if (sync)
                        {
                            // Only ever for the CLI harness. The shell must not block its UI
                            // thread on a disk sweep.
                            LJobs.Job jj = null;
                            LJ res = null;
                            List<string> rr = roots;
                            bool ic = icons;
                            int sz = size;
                            LJobs.Job fake = new LJobs.Job();
                            fake.Id = "sync";
                            fake.State = "running";
                            res = ScanMod.Run(fake, ic, sz, rr);
                            if (jj != null) { }
                            return res;
                        }
                        List<string> roots2 = roots;
                        bool icons2 = icons;
                        int size2 = size;
                        return StartJob("lib.scan", delegate (LJobs.Job job)
                        {
                            return ScanMod.Run(job, icons2, size2, roots2);
                        });
                    }

                case "lib.list":
                    {
                        LJ doc = ScanMod.Cached();
                        if (doc == null)
                        {
                            LJ o = LJ.Obj();
                            o.Set("never", true);
                            o.Set("entries", LJ.Arr());
                            o.Set("sources", LJ.Arr());
                            o.Set("counts", LJ.Obj().Set("total", 0).Set("games", 0).Set("apps", 0));
                            o.Set("detail", "Nothing has been scanned on this machine yet. Run lib.scan.");
                            return o;
                        }
                        LJ copy = LJ.Obj();
                        for (int i = 0; i < doc.Count; i++) copy.Set(doc.KeyAt(i), doc.Get(doc.KeyAt(i)));
                        copy.Put("never", LJ.Of(false));
                        copy.Put("cached", LJ.Of(true));
                        // Age lets the page decide whether to repaint from cache and refresh
                        // quietly, or to show the scan as running.
                        try
                        {
                            DateTime dt;
                            if (DateTime.TryParse(doc.S("scannedUtc", ""), CultureInfo.InvariantCulture,
                                    DateTimeStyles.RoundtripKind, out dt))
                                copy.Put("ageSeconds", (int)(DateTime.UtcNow - dt.ToUniversalTime()).TotalSeconds);
                        }
                        catch { }
                        return copy;
                    }

                case "lib.entry":
                    {
                        LibEntry e = ScanMod.Resolve(q.S("id", null));
                        if (e == null) throw new LibFault("no_such_entry",
                            "There is no library entry called '" + q.S("id", "") + "'. Run lib.scan.");
                        return e.ToCacheJson();
                    }

                case "lib.launch":
                    {
                        LibEntry e = ScanMod.Resolve(q.S("id", null));
                        if (e == null) throw new LibFault("no_such_entry",
                            "There is no library entry called '" + q.S("id", "") + "'. Run lib.scan first.");
                        return LaunchMod.Launch(e, q.S("args", null));
                    }

                case "lib.icon":
                    {
                        int size = q.I("size", 128);
                        if (size < 16) size = 16;
                        if (size > 512) size = 512;
                        bool wantArt = q.B("art", false);
                        string path = q.S("path", null);
                        int idx = q.I("index", 0);

                        if (string.IsNullOrEmpty(path))
                        {
                            LibEntry e = ScanMod.Resolve(q.S("id", null));
                            if (e == null) throw new LibFault("no_such_entry",
                                "There is no library entry called '" + q.S("id", "") + "'. Run lib.scan first.");
                            if (wantArt && !string.IsNullOrEmpty(e.ArtPath)) { path = e.ArtPath; idx = 0; }
                            else if (!string.IsNullOrEmpty(e.IconPath)) { path = e.IconPath; idx = e.IconIndex; }
                            else if (!string.IsNullOrEmpty(e.ArtPath)) { path = e.ArtPath; idx = 0; }
                            else throw new LibFault("no_icon", "'" + e.Title + "' has no icon on disk.");
                        }
                        else path = LApi.Full(path);

                        // Passthrough only when the caller asked for the big art: shrinking a
                        // 600x900 cover to a 128px glyph is the whole point of the other case.
                        LJ o = IconMod.Render(path, idx, size, wantArt);
                        o.Set("requestedSize", size);
                        return o;
                    }

                default:
                    throw new LibFault("unknown_command",
                        "'" + cmd + "' is not a LibraryApi command. Try {\"cmd\":\"api.commands\"}.");
            }
        }

        static LJ Cmd(string name, string what, string elevation)
        {
            return LJ.Obj().Set("cmd", name).Set("what", what).Set("elevation", elevation);
        }

        static LJ Catalog()
        {
            LJ arr = LJ.Arr();
            arr.Add(Cmd("api.version", "api version, identity and cache path", "no"));
            arr.Add(Cmd("api.commands", "this catalogue", "no"));
            arr.Add(Cmd("job.status", "poll a background job by jobId", "no"));
            arr.Add(Cmd("job.list", "every job this process has started", "no"));
            arr.Add(Cmd("job.cancel", "ask a running job to stop", "no"));
            arr.Add(Cmd("lib.sources", "which sources exist on this machine - fast, no scanning", "no"));
            arr.Add(Cmd("lib.scan", "JOB. Discover installed software from every source. "
                                  + "Args: icons(bool,true), iconSize(int,96), roots(array of paths), sync(bool,false - CLI only)", "no"));
            arr.Add(Cmd("lib.list", "the last cached scan, immediately, from %LOCALAPPDATA%\\ArcOS\\library.json", "no"));
            arr.Add(Cmd("lib.entry", "one entry by id, including its icon and art paths", "no"));
            arr.Add(Cmd("lib.launch", "launch an entry by id. Returns pid plus launchKind and whether the pid is the target", "no"));
            arr.Add(Cmd("lib.icon", "PNG data URI for an entry (id) or a path. Args: size(int,128), art(bool,false), index(int,0)", "no"));

            LJ o = LJ.Obj();
            o.Set("apiVersion", ApiVersion);
            o.Set("api", "LibraryApi");
            o.Set("commands", arr);
            o.Set("jobPrefix", "ljob-");
            o.Set("sources", "steam, epic, gog, battlenet, xbox, shortcut, folder");
            o.Set("note", "Every entry is admitted on filesystem evidence, never on a registry key alone.");
            return o;
        }
    }

    #endregion
}
