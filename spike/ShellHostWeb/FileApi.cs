// MarwanOS - FileApi
// Native Windows file-operations layer for the MarwanOS file explorer (ui/files.js).
//
// The WebView2 page posts a JSON command object; FileApi.Handle() returns a JSON response string.
// Same envelope as SystemApi.cs, deliberately, so one page-side transport serves both:
//     {"ok":true,"data":{...}}
//     {"ok":false,"error":"code","detail":"human readable"}
// Handle() NEVER throws. This process IS the Windows shell; an escaped exception blanks a
// television, so every path is wrapped and every native failure becomes an envelope.
//
// A request may carry "reqId":"<anything>" and it is echoed back so the page can correlate.
// It is NOT called "id": several commands take arguments of their own and the two must not collide.
//
// This file is self-contained: no assembly references beyond System.dll / System.Core.dll.
// Everything else is P/Invoke. Drop it into the same csc invocation:
//     csc ... ShellHostWeb.cs SystemApi.cs FileApi.cs
//
// Language level: C# 5 (inbox .NET Framework csc.exe).
// No string interpolation, no nameof, no expression-bodied members, no async/await.
//
// -------------------------------------------------------------------------------------------
// NAMESPACE AND TYPE NAMES
// -------------------------------------------------------------------------------------------
// Namespace is MarwanOs.Files, not MarwanOs.Sys and not MarwanOs.ShellWeb. The helper types are further
// prefixed (FJ, FApi, FJobs, FNative, FsFault) so that a future file which does
// `using MarwanOs.Sys; using MarwanOs.Files;` still compiles - a bare `J` in both namespaces would be
// ambiguous, and finding that out at integration time is exactly the collision this avoids.
//
// -------------------------------------------------------------------------------------------
// THREADING CONTRACT
// -------------------------------------------------------------------------------------------
// Handle() is called from the WebView2 UI thread. Every synchronous command is bounded: the
// slowest is fs.list, which is one FindFirstFileEx sweep (measured at 12 ms for 10,000 entries
// on the bench). Anything unbounded - copy, move, delete, folder sizing - is a background JOB:
//     {"ok":true,"data":{"jobId":"fjob-3","state":"running","async":true}}
// The page then polls {"cmd":"job.status","jobId":"fjob-3"} until state is done|error|cancelled.
// Job ids are prefixed "fjob-" so they can never be confused with SystemApi's "job-N".
//
// -------------------------------------------------------------------------------------------
// PROGRESS AND CANCELLATION
// -------------------------------------------------------------------------------------------
// Copy and move go through CopyFileEx / MoveFileWithProgress with a progress callback. The
// callback returns PROGRESS_CANCEL the moment the job's cancel flag is set, which is why
// cancellation is real rather than cosmetic: the OS abandons the transfer mid-file and deletes
// the partial destination itself. A multi-gigabyte copy therefore stops within one callback
// interval (~64 KB - 1 MB of transfer) instead of at the next file boundary.
//
// Recycling is the exception. SHFileOperation has no progress callback and no cancel channel,
// so a recycle job reports percent -1 (indeterminate) and says so. Permanent delete is our own
// recursive walk, so it has both real progress and real cancellation.
//
// -------------------------------------------------------------------------------------------
// SAFETY
// -------------------------------------------------------------------------------------------
// * Every path argument is canonicalised with Path.GetFullPath before anything touches it, and
//   every *name* argument (mkdir, rename) is rejected if it contains a separator, a colon, a
//   wildcard or "..". Those two rules together are the path-traversal guard.
// * C:\Windows and C:\Program Files* are refused for any mutation unless force:true is passed.
//   Reads are never blocked - you may look at anything you have rights to.
// * A bare drive root can never be deleted, renamed or moved.
// * Recursive delete NEVER follows a reparse point: a junction or symlink is unlinked, and the
//   thing it points at is left alone. Same for recursive copy and folder sizing.
// * Recycle refuses to run on a volume with no Recycle Bin rather than silently destroying the
//   files, which is what SHFileOperation would otherwise do.
//
// -------------------------------------------------------------------------------------------
// ELEVATION
// -------------------------------------------------------------------------------------------
// The shell runs as a STANDARD USER (marwanshell). Nothing in this file requires administrator
// rights for normal user-profile work. What a standard user cannot do is an ACL question, not
// an elevation-of-this-API question, and it surfaces as "Access denied" with the path named.
// fs.eject is the one command whose behaviour genuinely differs: CM_Request_Device_Eject works
// unprivileged, but the volume lock/dismount fallback needs administrator rights and says so.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace MarwanOs.Files
{
    #region JSON  (hand-rolled writer + reader)

    // Same shape as SystemApi's J, renamed so the two can coexist under a single `using` of both
    // namespaces. Building a node tree rather than concatenating strings means we can never emit
    // unbalanced braces, and the same type serves as the request reader.
    public sealed class FJ
    {
        public const int TNull = 0, TBool = 1, TNum = 2, TStr = 3, TArr = 4, TObj = 5;

        int _t;
        bool _b;
        double _n;
        string _s;            // string value, or the verbatim literal for numbers
        List<FJ> _items;      // array
        List<string> _keys;   // object
        List<FJ> _vals;       // object

        FJ(int t) { _t = t; }

        public int Kind { get { return _t; } }

        public static FJ Obj() { FJ j = new FJ(TObj); j._keys = new List<string>(); j._vals = new List<FJ>(); return j; }
        public static FJ Arr() { FJ j = new FJ(TArr); j._items = new List<FJ>(); return j; }
        public static FJ Nul() { return new FJ(TNull); }

        public static FJ Of(string s) { if (s == null) return Nul(); FJ j = new FJ(TStr); j._s = s; return j; }
        public static FJ Of(bool b) { FJ j = new FJ(TBool); j._b = b; return j; }
        public static FJ Of(long v) { FJ j = new FJ(TNum); j._n = v; j._s = v.ToString(CultureInfo.InvariantCulture); return j; }
        public static FJ Of(int v) { return Of((long)v); }
        public static FJ Of(uint v) { return Of((long)v); }
        public static FJ Of(ulong v) { FJ j = new FJ(TNum); j._n = v; j._s = v.ToString(CultureInfo.InvariantCulture); return j; }
        public static FJ Of(double v)
        {
            FJ j = new FJ(TNum);
            j._n = v;
            if (double.IsNaN(v) || double.IsInfinity(v)) { j._t = TNull; return j; }
            j._s = v.ToString("R", CultureInfo.InvariantCulture);
            return j;
        }
        public static FJ Round(double v, int digits)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return Nul();
            return Of(Math.Round(v, digits));
        }

        // ---- building ----
        public FJ Set(string k, FJ v) { _keys.Add(k); _vals.Add(v == null ? Nul() : v); return this; }
        public FJ Set(string k, string v) { return Set(k, Of(v)); }
        public FJ Set(string k, bool v) { return Set(k, Of(v)); }
        public FJ Set(string k, int v) { return Set(k, Of(v)); }
        public FJ Set(string k, uint v) { return Set(k, Of(v)); }
        public FJ Set(string k, long v) { return Set(k, Of(v)); }
        public FJ Set(string k, ulong v) { return Set(k, Of(v)); }
        public FJ Set(string k, double v) { return Set(k, Of(v)); }
        public FJ SetNull(string k) { return Set(k, Nul()); }

        public FJ Put(string k, FJ v)
        {
            for (int i = 0; i < _keys.Count; i++)
                if (string.Equals(_keys[i], k, StringComparison.Ordinal)) { _vals[i] = v == null ? Nul() : v; return this; }
            return Set(k, v);
        }
        public FJ Put(string k, string v) { return Put(k, Of(v)); }
        public FJ Put(string k, bool v) { return Put(k, Of(v)); }
        public FJ Put(string k, long v) { return Put(k, Of(v)); }

        public FJ Add(FJ v) { _items.Add(v == null ? Nul() : v); return this; }
        public FJ Add(string v) { return Add(Of(v)); }

        // ---- reading ----
        public int Count
        {
            get
            {
                if (_t == TArr) return _items.Count;
                if (_t == TObj) return _keys.Count;
                return 0;
            }
        }

        public FJ At(int i)
        {
            if (_t != TArr || i < 0 || i >= _items.Count) return null;
            return _items[i];
        }

        public string KeyAt(int i)
        {
            if (_t != TObj || i < 0 || i >= _keys.Count) return null;
            return _keys[i];
        }

        public FJ Get(string key)
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

        public string S(string key, string dflt) { FJ v = Get(key); return v == null ? dflt : v.AsStr(dflt); }
        public double N(string key, double dflt) { FJ v = Get(key); return v == null ? dflt : v.AsNum(dflt); }
        public int I(string key, int dflt) { FJ v = Get(key); return v == null ? dflt : (int)v.AsNum(dflt); }
        public long L(string key, long dflt) { FJ v = Get(key); return v == null ? dflt : (long)v.AsNum(dflt); }
        public bool B(string key, bool dflt) { FJ v = Get(key); return v == null ? dflt : v.AsBool(dflt); }

        // ---- serialising ----
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
                        // Control chars and the two line separators that break JS eval.
                        if (c < 0x20 || c == '\u2028' || c == '\u2029')
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---- parsing ----
        public static FJ Parse(string text)
        {
            int i = 0;
            if (text == null) throw new FormatException("empty input");
            FJ v = ParseValue(text, ref i);
            SkipWs(text, ref i);
            return v;
        }

        static void SkipWs(string t, ref int i)
        {
            while (i < t.Length && (t[i] == ' ' || t[i] == '\t' || t[i] == '\r' || t[i] == '\n')) i++;
        }

        static FJ ParseValue(string t, ref int i)
        {
            SkipWs(t, ref i);
            if (i >= t.Length) throw new FormatException("unexpected end of JSON");
            char c = t[i];
            if (c == '{')
            {
                i++;
                FJ o = Obj();
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
                FJ a = Arr();
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
            FJ num = new FJ(TNum);
            num._n = d;
            num._s = lit;
            return num;
        }

        static string ParseString(string t, ref int i)
        {
            i++; // opening quote
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

    #region Typed failure

    // Thrown inside command implementations; Handle() turns it into the standard error envelope.
    // Nothing else escapes - anything unexpected becomes error:"internal".
    public sealed class FsFault : Exception
    {
        public readonly string Code;
        public readonly FJ Extra;
        public FsFault(string code, string detail) : base(detail == null ? code : detail) { Code = code; }
        public FsFault(string code, string detail, FJ extra) : base(detail == null ? code : detail) { Code = code; Extra = extra; }
    }

    #endregion

    #region Core helpers - envelope, human error text

    public static class FApi
    {
        public static string Ok(FJ data)
        {
            FJ o = FJ.Obj();
            o.Set("ok", true);
            o.Set("data", data == null ? FJ.Obj() : data);
            return o.ToJson();
        }

        public static string Err(string code, string detail)
        {
            FJ o = FJ.Obj();
            o.Set("ok", false);
            o.Set("error", code == null ? "error" : code);
            o.Set("detail", detail == null ? "" : detail);
            return o.ToJson();
        }

        // ------------------------------------------------------------------------------------
        // Human-readable Win32 text. The brief is explicit: an error must never reach a
        // television as a bare number. Every code we can name is named; anything else falls
        // back to the OS message, and the numeric code rides along in the envelope's "win32"
        // field for a log rather than for the screen.
        // ------------------------------------------------------------------------------------
        public const uint ERROR_FILE_NOT_FOUND = 2;
        public const uint ERROR_PATH_NOT_FOUND = 3;
        public const uint ERROR_ACCESS_DENIED = 5;
        public const uint ERROR_NOT_READY = 21;
        public const uint ERROR_WRITE_PROTECT = 19;
        public const uint ERROR_SHARING_VIOLATION = 32;
        public const uint ERROR_LOCK_VIOLATION = 33;
        public const uint ERROR_HANDLE_DISK_FULL = 39;
        public const uint ERROR_FILE_EXISTS = 80;
        public const uint ERROR_INVALID_NAME = 123;
        public const uint ERROR_DIR_NOT_EMPTY = 145;
        public const uint ERROR_ALREADY_EXISTS = 183;
        public const uint ERROR_FILENAME_EXCED_RANGE = 206;
        public const uint ERROR_DISK_FULL = 112;
        public const uint ERROR_NOT_SAME_DEVICE = 17;
        public const uint ERROR_DIRECTORY = 267;
        public const uint ERROR_CANCELLED = 1223;
        public const uint ERROR_REQUEST_ABORTED = 1235;
        public const uint ERROR_NO_MORE_FILES = 18;
        public const uint ERROR_DEVICE_NOT_CONNECTED = 1167;
        public const uint ERROR_MEDIA_CHANGED = 1110;
        public const uint ERROR_UNRECOGNIZED_VOLUME = 1005;
        public const uint ERROR_NO_MEDIA_IN_DRIVE = 1112;

        public static string Friendly(uint err)
        {
            switch (err)
            {
                case 0: return "The operation completed successfully.";
                case ERROR_FILE_NOT_FOUND: return "The file no longer exists.";
                case ERROR_PATH_NOT_FOUND: return "That folder no longer exists.";
                case ERROR_ACCESS_DENIED: return "Access denied.";
                case ERROR_NOT_SAME_DEVICE: return "The item cannot be moved to a different drive this way.";
                case ERROR_WRITE_PROTECT: return "The drive is write-protected.";
                case ERROR_NOT_READY: return "The drive is not ready - there may be no disc or card in it.";
                case ERROR_SHARING_VIOLATION: return "The file is in use by another program.";
                case ERROR_LOCK_VIOLATION: return "Part of the file is locked by another program.";
                case ERROR_HANDLE_DISK_FULL:
                case ERROR_DISK_FULL: return "There is not enough space on the destination drive.";
                case ERROR_FILE_EXISTS:
                case ERROR_ALREADY_EXISTS: return "Something with that name is already there.";
                case ERROR_INVALID_NAME: return "That name contains characters Windows does not allow.";
                case ERROR_DIR_NOT_EMPTY: return "The folder is not empty.";
                case ERROR_FILENAME_EXCED_RANGE: return "The full path is too long for Windows to handle.";
                case ERROR_DIRECTORY: return "That is a folder, not a file (or the other way round).";
                case ERROR_CANCELLED:
                case ERROR_REQUEST_ABORTED: return "Cancelled.";
                case ERROR_DEVICE_NOT_CONNECTED: return "The device is no longer connected.";
                case ERROR_MEDIA_CHANGED: return "The disc or card was swapped while the drive was in use.";
                case ERROR_UNRECOGNIZED_VOLUME: return "Windows does not recognise the file system on that volume.";
                case ERROR_NO_MEDIA_IN_DRIVE: return "There is no disc or card in the drive.";
            }
            string msg;
            try { msg = new System.ComponentModel.Win32Exception((int)err).Message; }
            catch { msg = null; }
            if (string.IsNullOrEmpty(msg)) msg = "Windows reported an unnamed error.";
            if (!msg.EndsWith(".", StringComparison.Ordinal)) msg += ".";
            return msg;
        }

        // Friendly text with the path stitched in - what the details strip actually shows.
        public static string Friendly(uint err, string path)
        {
            string m = Friendly(err);
            if (string.IsNullOrEmpty(path)) return m;
            return m + "  (" + path + ")";
        }

        public static string Win32Code(uint err)
        {
            return "0x" + err.ToString("X8", CultureInfo.InvariantCulture) + " (" + err.ToString(CultureInfo.InvariantCulture) + ")";
        }

        // A short stable code the UI can branch on without parsing prose.
        public static string CodeFor(uint err)
        {
            switch (err)
            {
                case ERROR_FILE_NOT_FOUND:
                case ERROR_PATH_NOT_FOUND: return "not_found";
                case ERROR_ACCESS_DENIED: return "access_denied";
                case ERROR_SHARING_VIOLATION:
                case ERROR_LOCK_VIOLATION: return "in_use";
                case ERROR_DISK_FULL:
                case ERROR_HANDLE_DISK_FULL: return "disk_full";
                case ERROR_FILE_EXISTS:
                case ERROR_ALREADY_EXISTS: return "exists";
                case ERROR_WRITE_PROTECT: return "write_protected";
                case ERROR_NOT_READY:
                case ERROR_NO_MEDIA_IN_DRIVE: return "not_ready";
                case ERROR_FILENAME_EXCED_RANGE: return "path_too_long";
                case ERROR_INVALID_NAME: return "bad_name";
                case ERROR_DIR_NOT_EMPTY: return "not_empty";
                case ERROR_CANCELLED:
                case ERROR_REQUEST_ABORTED: return "cancelled";
            }
            return "io_error";
        }

        public static FsFault Fault(uint err, string path)
        {
            FJ extra = FJ.Obj();
            extra.Set("win32", (long)err);
            extra.Set("win32Code", Win32Code(err));
            if (path != null) extra.Set("path", path);
            return new FsFault(CodeFor(err), Friendly(err, path), extra);
        }

        public static FsFault LastError(string path)
        {
            return Fault((uint)Marshal.GetLastWin32Error(), path);
        }

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
                cur = cur.InnerException;
                depth++;
            }
            return sb.ToString();
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

        public static string Iso(DateTime utc)
        {
            if (utc.Year < 1601) return null;
            return utc.ToString("o", CultureInfo.InvariantCulture);
        }

        public static string Iso(long fileTime)
        {
            if (fileTime <= 0) return null;
            try { return DateTime.FromFileTimeUtc(fileTime).ToString("o", CultureInfo.InvariantCulture); }
            catch { return null; }
        }
    }

    #endregion

    #region Background jobs

    // Anything unbounded runs here. Handle() returns a jobId at once and the page polls
    // job.status. Copy/move/delete carry byte-level progress; folder sizing carries counts.
    public static class FJobs
    {
        public sealed class Job
        {
            public string Id;
            public string Cmd;
            public string State;      // running | done | error | cancelled
            public int Percent;       // 0..100, -1 = indeterminate
            public string Phase;      // human-readable stage
            public FJ Result;
            public string Error;
            public string Detail;
            public DateTime StartedUtc;
            public DateTime FinishedUtc;

            // Progress, written by the worker and read by whatever thread polls. long fields are
            // read/written atomically on x64; the strings go through the lock.
            public long BytesDone;
            public long BytesTotal;
            public long ItemsDone;
            public long ItemsTotal;
            public bool Indeterminate;
            readonly object _pl = new object();
            string _current = "";
            List<string> _problems = new List<string>();

            public string Current
            {
                get { lock (_pl) { return _current; } }
                set { lock (_pl) { _current = value == null ? "" : value; } }
            }

            // Per-item failures do not abort a batch: a copy of 400 files with one locked file
            // finishes the other 399 and reports the one. Capped so a pathological run cannot
            // grow without bound.
            public void Problem(string path, string text)
            {
                lock (_pl)
                {
                    if (_problems.Count < 200) _problems.Add((path == null ? "" : path) + " - " + text);
                    else if (_problems.Count == 200) _problems.Add("(further problems not listed)");
                }
            }
            public List<string> Problems { get { lock (_pl) { return new List<string>(_problems); } } }

            volatile bool _cancel;
            public bool CancelRequested { get { return _cancel; } }
            public void RequestCancel() { _cancel = true; }
            public void Report(int percent, string phase) { Percent = percent; Phase = phase; }
        }

        static readonly object Gate = new object();
        static readonly Dictionary<string, Job> Map = new Dictionary<string, Job>(StringComparer.OrdinalIgnoreCase);
        static int _seq;

        public static Job Start(string cmd, Func<Job, FJ> work) { return Start(cmd, work, false); }

        // sta:true is for SHFileOperation, which is a shell API and wants a single-threaded
        // apartment. Everything else runs MTA.
        public static Job Start(string cmd, Func<Job, FJ> work, bool sta)
        {
            Job job = new Job();
            lock (Gate)
            {
                _seq++;
                job.Id = "fjob-" + _seq.ToString(CultureInfo.InvariantCulture);
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
                    FJ r = work(job);
                    lock (Gate)
                    {
                        if (job.State == "running")
                        {
                            job.State = job.CancelRequested ? "cancelled" : "done";
                            job.Result = r;
                            if (!job.CancelRequested) job.Percent = 100;
                            job.Phase = job.CancelRequested ? "cancelled" : "complete";
                        }
                    }
                }
                catch (FsFault f)
                {
                    lock (Gate)
                    {
                        job.State = "error";
                        job.Error = f.Code;
                        job.Detail = f.Message;
                    }
                }
                catch (Exception ex)
                {
                    lock (Gate)
                    {
                        job.State = "error";
                        job.Error = "job_failed";
                        job.Detail = FApi.Describe(ex);
                    }
                }
                finally
                {
                    job.FinishedUtc = DateTime.UtcNow;
                }
            });
            t.IsBackground = true;
            t.SetApartmentState(sta ? ApartmentState.STA : ApartmentState.MTA);
            t.Name = "arc-fileapi-" + job.Id;
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

        // Keep the table from growing without bound in a shell that runs for weeks.
        static void Prune()
        {
            if (Map.Count < 64) return;
            List<string> dead = new List<string>();
            foreach (KeyValuePair<string, Job> kv in Map)
            {
                Job j = kv.Value;
                if (j.State != "running" && (DateTime.UtcNow - j.FinishedUtc).TotalMinutes > 10)
                    dead.Add(kv.Key);
            }
            for (int i = 0; i < dead.Count; i++) Map.Remove(dead[i]);
        }

        public static FJ Describe(Job j)
        {
            FJ o = FJ.Obj();
            o.Set("jobId", j.Id);
            o.Set("cmd", j.Cmd);
            o.Set("state", j.State);
            o.Set("phase", j.Phase);

            double secs = ((j.State == "running" ? DateTime.UtcNow : j.FinishedUtc) - j.StartedUtc).TotalSeconds;
            long done = j.BytesDone, total = j.BytesTotal;

            int pct = j.Percent;
            if (!j.Indeterminate && total > 0)
                pct = (int)Math.Min(100, Math.Max(0, (done * 100L) / total));
            o.Set("percent", j.Indeterminate ? -1 : pct);

            o.Set("bytesDone", done);
            o.Set("bytesTotal", total);
            o.Set("itemsDone", j.ItemsDone);
            o.Set("itemsTotal", j.ItemsTotal);
            o.Set("current", j.Current);

            // Rate and ETA are only honest once there is something to divide by. A "0 s
            // remaining" printed in the first 300 ms of a 40 GB copy is a lie the UI would repeat.
            if (secs > 0.4 && done > 0)
            {
                double rate = done / secs;
                o.Set("bytesPerSec", (long)rate);
                if (total > done && rate > 1)
                    o.Set("etaSec", FJ.Round((total - done) / rate, 1));
                else
                    o.SetNull("etaSec");
            }
            else
            {
                o.SetNull("bytesPerSec");
                o.SetNull("etaSec");
            }

            o.Set("startedUtc", j.StartedUtc.ToString("o", CultureInfo.InvariantCulture));
            if (j.State != "running")
                o.Set("finishedUtc", j.FinishedUtc.ToString("o", CultureInfo.InvariantCulture));
            o.Set("elapsedMs", (long)(secs * 1000));
            o.Set("cancelRequested", j.CancelRequested);

            List<string> probs = j.Problems;
            if (probs.Count > 0)
            {
                FJ pa = FJ.Arr();
                for (int i = 0; i < probs.Count; i++) pa.Add(probs[i]);
                o.Set("problems", pa);
                o.Set("problemCount", probs.Count);
            }

            if (j.Result != null) o.Set("result", j.Result);
            if (j.Error != null) { o.Set("error", j.Error); o.Set("detail", j.Detail); }
            return o;
        }
    }

    #endregion

    #region Native interop  (P/Invoke)

    internal static class FNative
    {
        // ---------- error mode: never let a bare drive pop a system dialog in a kiosk ----------
        internal const uint SEM_FAILCRITICALERRORS = 0x0001;
        internal const uint SEM_NOOPENFILEERRORBOX = 0x8000;

        [DllImport("kernel32.dll")]
        internal static extern uint SetErrorMode(uint uMode);

        // ---------- enumeration ----------
        internal const int MAX_PATH = 260;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WIN32_FIND_DATA
        {
            public uint dwFileAttributes;
            public uint ftCreationLow;
            public uint ftCreationHigh;
            public uint ftAccessLow;
            public uint ftAccessHigh;
            public uint ftWriteLow;
            public uint ftWriteHigh;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;
            public uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_PATH)]
            public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            public string cAlternate;
        }

        internal enum FINDEX_INFO_LEVELS { FindExInfoStandard = 0, FindExInfoBasic = 1 }
        internal enum FINDEX_SEARCH_OPS { FindExSearchNameMatch = 0 }
        internal const int FIND_FIRST_EX_LARGE_FETCH = 2;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr FindFirstFileExW(string lpFileName, FINDEX_INFO_LEVELS level,
            out WIN32_FIND_DATA lpFindFileData, FINDEX_SEARCH_OPS op, IntPtr lpSearchFilter, int dwAdditionalFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FindNextFileW(IntPtr hFindFile, out WIN32_FIND_DATA lpFindFileData);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FindClose(IntPtr hFindFile);

        internal static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        // ---------- attributes ----------
        internal const uint FILE_ATTRIBUTE_READONLY = 0x00000001;
        internal const uint FILE_ATTRIBUTE_HIDDEN = 0x00000002;
        internal const uint FILE_ATTRIBUTE_SYSTEM = 0x00000004;
        internal const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
        internal const uint FILE_ATTRIBUTE_ARCHIVE = 0x00000020;
        internal const uint FILE_ATTRIBUTE_TEMPORARY = 0x00000100;
        internal const uint FILE_ATTRIBUTE_SPARSE_FILE = 0x00000200;
        internal const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;
        internal const uint FILE_ATTRIBUTE_COMPRESSED = 0x00000800;
        internal const uint FILE_ATTRIBUTE_OFFLINE = 0x00001000;
        internal const uint FILE_ATTRIBUTE_ENCRYPTED = 0x00004000;
        internal const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;

        [StructLayout(LayoutKind.Sequential)]
        internal struct WIN32_FILE_ATTRIBUTE_DATA
        {
            public uint dwFileAttributes;
            public uint ftCreationLow, ftCreationHigh;
            public uint ftAccessLow, ftAccessHigh;
            public uint ftWriteLow, ftWriteHigh;
            public uint nFileSizeHigh, nFileSizeLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileAttributesExW(string lpFileName, int fInfoLevelId,
            out WIN32_FILE_ATTRIBUTE_DATA lpFileInformation);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint GetFileAttributesW(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetFileAttributesW(string lpFileName, uint dwFileAttributes);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint GetCompressedFileSizeW(string lpFileName, out uint lpFileSizeHigh);

        // ---------- directories and deletes ----------
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateDirectoryW(string lpPathName, IntPtr lpSecurityAttributes);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RemoveDirectoryW(string lpPathName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteFileW(string lpFileName);

        // ---------- copy / move with progress ----------
        internal const uint PROGRESS_CONTINUE = 0;
        internal const uint PROGRESS_CANCEL = 1;
        internal const uint PROGRESS_STOP = 2;
        internal const uint PROGRESS_QUIET = 3;

        internal const uint COPY_FILE_FAIL_IF_EXISTS = 0x00000001;
        internal const uint COPY_FILE_NO_BUFFERING = 0x00001000;

        internal const uint MOVEFILE_COPY_ALLOWED = 0x00000002;
        internal const uint MOVEFILE_REPLACE_EXISTING = 0x00000001;
        internal const uint MOVEFILE_WRITE_THROUGH = 0x00000008;

        internal delegate uint CopyProgressRoutine(
            long totalFileSize, long totalBytesTransferred,
            long streamSize, long streamBytesTransferred,
            uint dwStreamNumber, uint dwCallbackReason,
            IntPtr hSourceFile, IntPtr hDestinationFile, IntPtr lpData);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CopyFileExW(string lpExistingFileName, string lpNewFileName,
            CopyProgressRoutine lpProgressRoutine, IntPtr lpData, ref int pbCancel, uint dwCopyFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool MoveFileWithProgressW(string lpExistingFileName, string lpNewFileName,
            CopyProgressRoutine lpProgressRoutine, IntPtr lpData, uint dwFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool MoveFileExW(string lpExistingFileName, string lpNewFileName, uint dwFlags);

        // ---------- volumes ----------
        internal const uint DRIVE_UNKNOWN = 0, DRIVE_NO_ROOT_DIR = 1, DRIVE_REMOVABLE = 2,
                            DRIVE_FIXED = 3, DRIVE_REMOTE = 4, DRIVE_CDROM = 5, DRIVE_RAMDISK = 6;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint GetLogicalDrives();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern uint GetDriveTypeW(string lpRootPathName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetVolumeInformationW(string lpRootPathName,
            StringBuilder lpVolumeNameBuffer, int nVolumeNameSize,
            out uint lpVolumeSerialNumber, out uint lpMaximumComponentLength,
            out uint lpFileSystemFlags, StringBuilder lpFileSystemNameBuffer, int nFileSystemNameSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetDiskFreeSpaceExW(string lpDirectoryName,
            out ulong lpFreeBytesAvailableToCaller, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetDiskFreeSpaceW(string lpRootPathName,
            out uint lpSectorsPerCluster, out uint lpBytesPerSector,
            out uint lpNumberOfFreeClusters, out uint lpTotalNumberOfClusters);

        // ---------- device / eject ----------
        internal const uint GENERIC_READ = 0x80000000;
        internal const uint GENERIC_WRITE = 0x40000000;
        internal const uint FILE_SHARE_READ = 0x00000001;
        internal const uint FILE_SHARE_WRITE = 0x00000002;
        internal const uint OPEN_EXISTING = 3;
        internal const uint FILE_ATTRIBUTE_NORMAL = 0x80;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode,
            IntPtr lpInBuffer, uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize,
            out uint lpBytesReturned, IntPtr lpOverlapped);

        internal const uint FSCTL_LOCK_VOLUME = 0x00090018;
        internal const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;
        internal const uint IOCTL_STORAGE_MEDIA_REMOVAL = 0x002D4804;
        internal const uint IOCTL_STORAGE_EJECT_MEDIA = 0x002D4808;
        internal const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;
        internal const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

        [StructLayout(LayoutKind.Sequential)]
        internal struct STORAGE_DEVICE_NUMBER
        {
            public uint DeviceType;
            public uint DeviceNumber;
            public int PartitionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PREVENT_MEDIA_REMOVAL
        {
            [MarshalAs(UnmanagedType.U1)] public bool PreventMediaRemoval;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct STORAGE_PROPERTY_QUERY
        {
            public uint PropertyId;      // StorageDeviceProperty = 0
            public uint QueryType;       // PropertyStandardQuery = 0
            public byte AdditionalByte0; // AdditionalParameters[1]
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct STORAGE_DEVICE_DESCRIPTOR
        {
            public uint Version;
            public uint Size;
            public byte DeviceType;
            public byte DeviceTypeModifier;
            [MarshalAs(UnmanagedType.U1)] public bool RemovableMedia;
            [MarshalAs(UnmanagedType.U1)] public bool CommandQueueing;
            public uint VendorIdOffset;
            public uint ProductIdOffset;
            public uint ProductRevisionOffset;
            public uint SerialNumberOffset;
            public uint BusType;         // STORAGE_BUS_TYPE
            public uint RawPropertiesLength;
        }

        internal const uint BusTypeUsb = 0x07;
        internal const uint BusTypeSd = 0x0C;
        internal const uint BusTypeMmc = 0x0D;
        internal const uint BusTypeVirtual = 0x0E;
        internal const uint BusTypeFileBackedVirtual = 0x0F;

        // ---------- Config Manager (safe removal) ----------
        internal const int CR_SUCCESS = 0;

        internal enum PNP_VETO_TYPE
        {
            Ok = 0, TypeUnknown = 1, LegacyDevice = 2, PendingClose = 3, WindowsApp = 4,
            WindowsService = 5, OutstandingOpen = 6, Device = 7, Driver = 8, IllegalDeviceRequest = 9,
            InsufficientPower = 10, NonDisableable = 11, LegacyDriver = 12, InsufficientRights = 13
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

        internal const uint DIGCF_PRESENT = 0x02;
        internal const uint DIGCF_DEVICEINTERFACE = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        internal struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr hDevInfo, IntPtr devInfo,
            ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr hDevInfo,
            ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData,
            uint deviceInterfaceDetailDataSize, out uint requiredSize, ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr hDevInfo);

        [DllImport("cfgmgr32.dll", SetLastError = true)]
        internal static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int CM_Get_Device_IDW(uint dnDevInst, StringBuilder buffer, int bufferLen, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int CM_Request_Device_EjectW(uint dnDevInst, out PNP_VETO_TYPE pVetoType,
            StringBuilder pszVetoName, int ulNameLength, uint ulFlags);

        internal static Guid GUID_DEVINTERFACE_DISK = new Guid("53f56307-b6bf-11d0-94f2-00a0c91efb8b");
        internal static Guid GUID_DEVINTERFACE_CDROM = new Guid("53f56308-b6bf-11d0-94f2-00a0c91efb8b");

        // ---------- shell file operations (Recycle Bin) ----------
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 8)]
        internal struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        internal const uint FO_DELETE = 0x0003;

        internal const ushort FOF_SILENT = 0x0004;
        internal const ushort FOF_NOCONFIRMATION = 0x0010;
        internal const ushort FOF_ALLOWUNDO = 0x0040;
        internal const ushort FOF_NOCONFIRMMKDIR = 0x0200;
        internal const ushort FOF_NOERRORUI = 0x0400;
        internal const ushort FOF_NO_UI = FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_NOCONFIRMMKDIR;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        internal static extern int SHFileOperationW(ref SHFILEOPSTRUCT lpFileOp);

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct SHQUERYRBINFO
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        internal static extern int SHQueryRecycleBinW(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

        // ---------- launching ----------
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct SHELLEXECUTEINFO
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            public string lpVerb;
            public string lpFile;
            public string lpParameters;
            public string lpDirectory;
            public int nShow;
            public IntPtr hInstApp;
            public IntPtr lpIDList;
            public string lpClass;
            public IntPtr hkeyClass;
            public uint dwHotKey;
            public IntPtr hIcon;
            public IntPtr hProcess;
        }

        internal const uint SEE_MASK_NOCLOSEPROCESS = 0x00000040;
        internal const uint SEE_MASK_FLAG_NO_UI = 0x00000400;
        internal const uint SEE_MASK_NOASYNC = 0x00000100;
        internal const int SW_SHOWNORMAL = 1;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShellExecuteExW(ref SHELLEXECUTEINFO lpExecInfo);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint GetProcessId(IntPtr hProcess);
    }

    #endregion

    #region Path guard

    // Every path that crosses this boundary goes through Guard. The rules are deliberately
    // boring: canonicalise, refuse names that are not names, refuse the OS's own directories
    // for writes, and never let a reparse point turn a delete into a recursive disaster.
    internal static class Guard
    {
        static readonly char[] BadNameChars = new char[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

        static readonly string[] ReservedNames = new string[] {
            "CON","PRN","AUX","NUL",
            "COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
            "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"
        };

        static string[] _protectedRoots;
        static readonly object PGate = new object();

        static string[] ProtectedRoots()
        {
            lock (PGate)
            {
                if (_protectedRoots != null) return _protectedRoots;
                List<string> l = new List<string>();
                AddEnv(l, "SystemRoot");
                AddEnv(l, "windir");
                AddEnv(l, "ProgramFiles");
                AddEnv(l, "ProgramFiles(x86)");
                AddEnv(l, "ProgramW6432");
                _protectedRoots = l.ToArray();
                return _protectedRoots;
            }
        }

        static void AddEnv(List<string> l, string var)
        {
            string v = null;
            try { v = Environment.GetEnvironmentVariable(var); }
            catch { }
            if (string.IsNullOrEmpty(v)) return;
            try { v = Path.GetFullPath(v); }
            catch { return; }
            v = v.TrimEnd('\\');
            for (int i = 0; i < l.Count; i++)
                if (string.Equals(l[i], v, StringComparison.OrdinalIgnoreCase)) return;
            l.Add(v);
        }

        // ------------------------------------------------------------------------------------
        // Canonicalise an incoming path. Throws FsFault - never returns something half-checked.
        // ------------------------------------------------------------------------------------
        public static string Resolve(string raw, string what)
        {
            if (raw == null) throw new FsFault("bad_path", "No " + what + " was given.");
            string p = raw.Trim();
            if (p.Length == 0) throw new FsFault("bad_path", "The " + what + " is empty.");

            for (int i = 0; i < p.Length; i++)
                if (p[i] < 0x20) throw new FsFault("bad_path", "The " + what + " contains a control character.");

            // Accept and strip the extended-length prefix on input; we add our own where needed.
            if (p.StartsWith("\\\\?\\UNC\\", StringComparison.Ordinal)) p = "\\\\" + p.Substring(8);
            else if (p.StartsWith("\\\\?\\", StringComparison.Ordinal)) p = p.Substring(4);

            p = p.Replace('/', '\\');

            // Absolute or nothing, and this is checked on the INPUT rather than on the result of
            // GetFullPath. GetFullPath happily resolves ".." and "docs\x" against this process's
            // current directory, which for the shell is wherever it happened to be launched from
            // - a page must never be able to reach a path by leaning on that. A drive-relative
            // "C:docs" is refused for the same reason: it means "relative to the CWD on C:".
            bool driveAbs = p.Length >= 3 && p[1] == ':' && p[2] == '\\';
            bool unc = p.StartsWith("\\\\", StringComparison.Ordinal) && p.Length > 2;
            if (!driveAbs && !unc)
                throw new FsFault("bad_path",
                    "The " + what + " must be a full path such as C:\\Users or \\\\server\\share - \"" + p + "\" is relative.");

            string full;
            try { full = Path.GetFullPath(p); }
            catch (Exception ex) { throw new FsFault("bad_path", "That " + what + " is not a valid Windows path. " + ex.Message); }

            // GetFullPath has now collapsed every "." and ".." segment, so what comes out is
            // canonical and cannot climb anywhere the caller could not already have named
            // directly. Between this and Guard.Name(), no argument can traverse.
            if (full.Length < 3 || (!(full[1] == ':' && full[2] == '\\') && !full.StartsWith("\\\\", StringComparison.Ordinal)))
                throw new FsFault("bad_path", "The " + what + " must be a full path such as C:\\Users or \\\\server\\share.");

            return full;
        }

        // A directory path that must exist.
        public static string ResolveDir(string raw, string what)
        {
            string full = Resolve(raw, what);
            uint at = FNative.GetFileAttributesW(Ex(full));
            if (at == FNative.INVALID_FILE_ATTRIBUTES) throw FApi.LastError(full);
            if ((at & FNative.FILE_ATTRIBUTE_DIRECTORY) == 0)
                throw new FsFault("not_a_directory", "That path is a file, not a folder.  (" + full + ")");
            return full;
        }

        // ------------------------------------------------------------------------------------
        // A NAME is a name: no separators, no drive letters, no traversal, no wildcards. This is
        // the other half of the traversal guard - Resolve() canonicalises, this refuses to let a
        // "name" argument turn into a path at all.
        // ------------------------------------------------------------------------------------
        public static string Name(string raw)
        {
            if (raw == null) throw new FsFault("bad_name", "No name was given.");
            string n = raw.Trim();
            if (n.Length == 0) throw new FsFault("bad_name", "The name is empty.");
            if (n.Length > 255) throw new FsFault("bad_name", "That name is longer than Windows allows (255 characters).");
            if (n == "." || n == "..") throw new FsFault("bad_name", "\".\" and \"..\" are not names.");
            if (n.IndexOf("..", StringComparison.Ordinal) >= 0 && n.Replace(".", "").Length == 0)
                throw new FsFault("bad_name", "A name cannot be made only of dots.");
            if (n.IndexOfAny(BadNameChars) >= 0)
                throw new FsFault("bad_name", "A name cannot contain \\ / : * ? \" < > or |.");
            for (int i = 0; i < n.Length; i++)
                if (n[i] < 0x20) throw new FsFault("bad_name", "That name contains a control character.");
            if (n.EndsWith(".", StringComparison.Ordinal) || n.EndsWith(" ", StringComparison.Ordinal))
                throw new FsFault("bad_name", "Windows will not keep a name that ends in a dot or a space.");

            string stem = n;
            int dot = stem.IndexOf('.');
            if (dot > 0) stem = stem.Substring(0, dot);
            for (int i = 0; i < ReservedNames.Length; i++)
                if (string.Equals(stem, ReservedNames[i], StringComparison.OrdinalIgnoreCase))
                    throw new FsFault("bad_name", "\"" + n + "\" is a name Windows reserves for a device.");

            return n;
        }

        public static bool IsDriveRoot(string full)
        {
            if (full == null) return false;
            string t = full.TrimEnd('\\');
            return t.Length == 2 && t[1] == ':';
        }

        public static bool IsUnder(string child, string parent)
        {
            if (child == null || parent == null) return false;
            string c = child.TrimEnd('\\');
            string p = parent.TrimEnd('\\');
            if (string.Equals(c, p, StringComparison.OrdinalIgnoreCase)) return true;
            if (c.Length <= p.Length) return false;
            if (!c.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return false;
            return c[p.Length] == '\\';
        }

        public static string ProtectedBy(string full)
        {
            string[] roots = ProtectedRoots();
            for (int i = 0; i < roots.Length; i++)
                if (IsUnder(full, roots[i])) return roots[i];
            return null;
        }

        // The gate every mutation passes through.
        public static void EnsureMutable(string full, bool force, string verb)
        {
            if (IsDriveRoot(full))
                throw new FsFault("refused_root",
                    "A whole drive cannot be " + verb + ". Open it and work inside it instead.  (" + full + ")");

            string root = ProtectedBy(full);
            if (root != null && !force)
                throw new FsFault("refused_system",
                    "This is part of Windows itself and will not be " + verb + " from the shell.  (" + full + ")  " +
                    "It sits under " + root + ". Pass force:true only if you genuinely mean it.");
        }

        // Extended-length form for the P/Invoke calls, so a 300-character path still works.
        // SHFileOperation is the exception - it rejects \\?\ - so it gets the plain path.
        public static string Ex(string full)
        {
            if (full == null) return null;
            if (full.StartsWith("\\\\?\\", StringComparison.Ordinal)) return full;
            if (full.StartsWith("\\\\", StringComparison.Ordinal)) return "\\\\?\\UNC\\" + full.Substring(2);
            if (full.Length >= 2 && full[1] == ':') return "\\\\?\\" + full;
            return full;
        }

        public static string Combine(string dir, string name)
        {
            if (dir.EndsWith("\\", StringComparison.Ordinal)) return dir + name;
            return dir + "\\" + name;
        }

        // Parent of a full path, or null at a drive root.
        public static string Parent(string full)
        {
            if (IsDriveRoot(full)) return null;
            try
            {
                string p = Path.GetDirectoryName(full.TrimEnd('\\'));
                if (string.IsNullOrEmpty(p)) return null;
                if (p.Length == 2 && p[1] == ':') p += "\\";
                return p;
            }
            catch { return null; }
        }

        public static string Leaf(string full)
        {
            if (IsDriveRoot(full)) return full.TrimEnd('\\');
            try
            {
                string n = Path.GetFileName(full.TrimEnd('\\'));
                return string.IsNullOrEmpty(n) ? full : n;
            }
            catch { return full; }
        }
    }

    #endregion

    #region Drives

    internal static class DriveMod
    {
        internal sealed class Vol
        {
            public string Root;          // "E:\"
            public string Letter;        // "E"
            public uint Type;
            public string TypeName;
            public string Label;
            public string FileSystem;
            public bool Ready;
            public bool Removable;
            public uint BusType;
            public string BusName;
            public ulong Total, Free, FreeToCaller;
            public bool RecycleBin;
            public string Note;
        }

        public static string BusName(uint b)
        {
            switch (b)
            {
                case 0x01: return "SCSI";
                case 0x02: return "ATAPI";
                case 0x03: return "ATA";
                case 0x04: return "IEEE 1394";
                case 0x05: return "SSA";
                case 0x06: return "Fibre Channel";
                case FNative.BusTypeUsb: return "USB";
                case 0x08: return "RAID";
                case 0x09: return "iSCSI";
                case 0x0A: return "SAS";
                case 0x0B: return "SATA";
                case FNative.BusTypeSd: return "SD";
                case FNative.BusTypeMmc: return "MMC";
                case FNative.BusTypeVirtual: return "Virtual";
                case FNative.BusTypeFileBackedVirtual: return "File-backed virtual";
                case 0x11: return "NVMe";
                case 0x12: return "SCM";
                case 0x13: return "UFS";
            }
            return b == 0 ? "Unknown" : ("Bus type " + b.ToString(CultureInfo.InvariantCulture));
        }

        public static string TypeName(uint t)
        {
            switch (t)
            {
                case FNative.DRIVE_REMOVABLE: return "removable";
                case FNative.DRIVE_FIXED: return "fixed";
                case FNative.DRIVE_REMOTE: return "network";
                case FNative.DRIVE_CDROM: return "optical";
                case FNative.DRIVE_RAMDISK: return "ramdisk";
                case FNative.DRIVE_NO_ROOT_DIR: return "unmounted";
            }
            return "unknown";
        }

        // Ask the storage stack what the volume is actually plugged into. A USB SSD reports
        // DRIVE_FIXED, so drive type alone would file it beside the boot disk; bus type is what
        // makes "removable drives called out" mean anything. Returns 0 when it cannot be read
        // (network drives, and anything the token cannot open), which is not an error.
        public static uint QueryBusType(string letter)
        {
            IntPtr h = IntPtr.Zero;
            IntPtr inBuf = IntPtr.Zero, outBuf = IntPtr.Zero;
            try
            {
                // Zero desired access: enough for a property query, and needs no privilege.
                h = FNative.CreateFileW("\\\\.\\" + letter + ":", 0,
                    FNative.FILE_SHARE_READ | FNative.FILE_SHARE_WRITE, IntPtr.Zero,
                    FNative.OPEN_EXISTING, 0, IntPtr.Zero);
                if (h == FNative.INVALID_HANDLE_VALUE) return 0;

                FNative.STORAGE_PROPERTY_QUERY q = new FNative.STORAGE_PROPERTY_QUERY();
                q.PropertyId = 0;   // StorageDeviceProperty
                q.QueryType = 0;    // PropertyStandardQuery
                int qsz = Marshal.SizeOf(typeof(FNative.STORAGE_PROPERTY_QUERY));
                inBuf = Marshal.AllocHGlobal(qsz);
                Marshal.StructureToPtr(q, inBuf, false);

                int osz = 1024;
                outBuf = Marshal.AllocHGlobal(osz);
                uint ret;
                if (!FNative.DeviceIoControl(h, FNative.IOCTL_STORAGE_QUERY_PROPERTY,
                        inBuf, (uint)qsz, outBuf, (uint)osz, out ret, IntPtr.Zero))
                    return 0;
                if (ret < 32) return 0;

                FNative.STORAGE_DEVICE_DESCRIPTOR d =
                    (FNative.STORAGE_DEVICE_DESCRIPTOR)Marshal.PtrToStructure(outBuf, typeof(FNative.STORAGE_DEVICE_DESCRIPTOR));
                return d.BusType;
            }
            catch { return 0; }
            finally
            {
                if (inBuf != IntPtr.Zero) Marshal.FreeHGlobal(inBuf);
                if (outBuf != IntPtr.Zero) Marshal.FreeHGlobal(outBuf);
                if (h != IntPtr.Zero && h != FNative.INVALID_HANDLE_VALUE) FNative.CloseHandle(h);
            }
        }

        public static bool HasRecycleBin(string root)
        {
            try
            {
                FNative.SHQUERYRBINFO info = new FNative.SHQUERYRBINFO();
                info.cbSize = Marshal.SizeOf(typeof(FNative.SHQUERYRBINFO));
                int hr = FNative.SHQueryRecycleBinW(root, ref info);
                return hr == 0;
            }
            catch { return false; }
        }

        public static Vol Read(string letter, bool wantBus)
        {
            Vol v = new Vol();
            v.Letter = letter;
            v.Root = letter + ":\\";
            v.Type = FNative.GetDriveTypeW(v.Root);
            v.TypeName = TypeName(v.Type);

            uint old = FNative.SetErrorMode(FNative.SEM_FAILCRITICALERRORS | FNative.SEM_NOOPENFILEERRORBOX);
            try
            {
                StringBuilder label = new StringBuilder(261);
                StringBuilder fs = new StringBuilder(64);
                uint serial, maxComp, flags;
                v.Ready = FNative.GetVolumeInformationW(v.Root, label, label.Capacity,
                    out serial, out maxComp, out flags, fs, fs.Capacity);
                if (v.Ready)
                {
                    v.Label = label.ToString();
                    v.FileSystem = fs.ToString();
                }
                else
                {
                    uint e = (uint)Marshal.GetLastWin32Error();
                    v.Note = FApi.Friendly(e);
                }

                if (v.Ready)
                {
                    ulong avail, total, free;
                    if (FNative.GetDiskFreeSpaceExW(v.Root, out avail, out total, out free))
                    {
                        v.FreeToCaller = avail;
                        v.Total = total;
                        v.Free = free;
                    }
                    v.RecycleBin = HasRecycleBin(v.Root);
                }

                if (wantBus && v.Type != FNative.DRIVE_REMOTE && v.Type != FNative.DRIVE_NO_ROOT_DIR)
                {
                    v.BusType = QueryBusType(letter);
                    v.BusName = BusName(v.BusType);
                }
                else v.BusName = v.Type == FNative.DRIVE_REMOTE ? "Network" : "Unknown";

                v.Removable = v.Type == FNative.DRIVE_REMOVABLE
                           || v.Type == FNative.DRIVE_CDROM
                           || v.BusType == FNative.BusTypeUsb
                           || v.BusType == FNative.BusTypeSd
                           || v.BusType == FNative.BusTypeMmc;
            }
            finally { FNative.SetErrorMode(old); }
            return v;
        }

        public static FJ Describe(Vol v)
        {
            FJ o = FJ.Obj();
            o.Set("letter", v.Letter);
            o.Set("path", v.Root);
            o.Set("label", string.IsNullOrEmpty(v.Label) ? null : v.Label);
            // What the tile actually prints. A label wins; otherwise say what kind of thing it is.
            o.Set("display", !string.IsNullOrEmpty(v.Label) ? v.Label
                 : (v.Type == FNative.DRIVE_CDROM ? "Optical drive"
                 : (v.Type == FNative.DRIVE_REMOVABLE ? "Removable drive"
                 : (v.Type == FNative.DRIVE_REMOTE ? "Network drive" : "Local disk"))));
            o.Set("fileSystem", string.IsNullOrEmpty(v.FileSystem) ? null : v.FileSystem);
            o.Set("driveType", v.TypeName);
            o.Set("busType", v.BusName);
            o.Set("removable", v.Removable);
            o.Set("ready", v.Ready);
            o.Set("total", (long)v.Total);
            o.Set("free", (long)v.Free);
            o.Set("freeToCaller", (long)v.FreeToCaller);
            o.Set("used", (long)(v.Total >= v.Free ? v.Total - v.Free : 0));
            o.Set("recycleBin", v.RecycleBin);
            if (v.Total > 0) o.Set("percentUsed", FJ.Round(100.0 * (v.Total - v.Free) / v.Total, 1));
            else o.SetNull("percentUsed");
            if (v.Note != null) o.Set("note", v.Note);
            return o;
        }

        public static FJ List(bool wantBus)
        {
            uint mask = FNative.GetLogicalDrives();
            FJ arr = FJ.Arr();
            int removable = 0, ready = 0;
            for (int i = 0; i < 26; i++)
            {
                if ((mask & (1u << i)) == 0) continue;
                string letter = ((char)('A' + i)).ToString(CultureInfo.InvariantCulture);
                Vol v;
                try { v = Read(letter, wantBus); }
                catch (Exception ex)
                {
                    // One unhappy drive must never take the whole picker down.
                    FJ bad = FJ.Obj();
                    bad.Set("letter", letter);
                    bad.Set("path", letter + ":\\");
                    bad.Set("display", letter + ":");
                    bad.Set("ready", false);
                    bad.Set("removable", false);
                    bad.Set("note", "Could not be read: " + FApi.Describe(ex));
                    arr.Add(bad);
                    continue;
                }
                if (v.Removable) removable++;
                if (v.Ready) ready++;
                arr.Add(Describe(v));
            }
            FJ o = FJ.Obj();
            o.Set("drives", arr);
            o.Set("count", arr.Count);
            o.Set("removableCount", removable);
            o.Set("readyCount", ready);
            return o;
        }

        public static FJ Space(string path)
        {
            string full = Guard.Resolve(path, "path");
            uint old = FNative.SetErrorMode(FNative.SEM_FAILCRITICALERRORS | FNative.SEM_NOOPENFILEERRORBOX);
            try
            {
                ulong avail, total, free;
                if (!FNative.GetDiskFreeSpaceExW(full, out avail, out total, out free))
                    throw FApi.LastError(full);
                FJ o = FJ.Obj();
                o.Set("path", full);
                o.Set("total", (long)total);
                o.Set("free", (long)free);
                o.Set("freeToCaller", (long)avail);
                o.Set("used", (long)(total >= free ? total - free : 0));
                if (total > 0) o.Set("percentUsed", FJ.Round(100.0 * (total - free) / total, 1));

                uint spc, bps, freeClusters, totalClusters;
                string root = Path.GetPathRoot(full);
                if (root != null && FNative.GetDiskFreeSpaceW(root, out spc, out bps, out freeClusters, out totalClusters))
                {
                    o.Set("clusterBytes", (long)spc * bps);
                    o.Set("sectorBytes", (long)bps);
                }
                return o;
            }
            finally { FNative.SetErrorMode(old); }
        }
    }

    #endregion

    #region Listing

    internal static class ListMod
    {
        internal sealed class Entry
        {
            public string Name;
            public bool IsDir;
            public long Size;
            public long Modified;   // FILETIME as long
            public long Created;
            public uint Attr;
            public string Ext;      // lower-case, without the dot; "" for none and for folders
        }

        // A short-lived cache so paging through a 10,000-entry folder is one sweep, not one per
        // page, and so a details strip that re-reads the current folder costs nothing.
        sealed class Cached
        {
            public List<Entry> Items;
            public DateTime At;
            public int Hidden;
        }

        static readonly object CGate = new object();
        static readonly Dictionary<string, Cached> Cache = new Dictionary<string, Cached>(StringComparer.OrdinalIgnoreCase);
        const double CacheSeconds = 4.0;

        public static void Invalidate(string path)
        {
            lock (CGate)
            {
                if (path == null) { Cache.Clear(); return; }
                string p = path.TrimEnd('\\');
                List<string> dead = new List<string>();
                foreach (KeyValuePair<string, Cached> kv in Cache)
                {
                    int bar = kv.Key.IndexOf('|');
                    string k = bar > 0 ? kv.Key.Substring(0, bar) : kv.Key;
                    if (string.Equals(k, p, StringComparison.OrdinalIgnoreCase)) dead.Add(kv.Key);
                }
                for (int i = 0; i < dead.Count; i++) Cache.Remove(dead[i]);
            }
        }

        // ------------------------------------------------------------------------------------
        // One FindFirstFileEx sweep. FindExInfoBasic skips the 8.3 short-name lookup and
        // LARGE_FETCH batches the directory reads: together they are worth roughly 2x on a
        // folder with ten thousand entries, and neither costs anything on a small one.
        // Nothing here recurses. A listing is one directory.
        // ------------------------------------------------------------------------------------
        public static List<Entry> Sweep(string dir, out int hiddenCount)
        {
            hiddenCount = 0;
            List<Entry> outList = new List<Entry>(256);
            string pattern = Guard.Ex(Guard.Combine(dir.TrimEnd('\\'), "*"));

            FNative.WIN32_FIND_DATA fd;
            IntPtr h = FNative.FindFirstFileExW(pattern, FNative.FINDEX_INFO_LEVELS.FindExInfoBasic,
                out fd, FNative.FINDEX_SEARCH_OPS.FindExSearchNameMatch, IntPtr.Zero,
                FNative.FIND_FIRST_EX_LARGE_FETCH);

            if (h == FNative.INVALID_HANDLE_VALUE)
            {
                uint e = (uint)Marshal.GetLastWin32Error();
                if (e == FApi.ERROR_NO_MORE_FILES || e == FApi.ERROR_FILE_NOT_FOUND) return outList;
                throw FApi.Fault(e, dir);
            }

            try
            {
                do
                {
                    string n = fd.cFileName;
                    if (n == "." || n == "..") continue;

                    Entry en = new Entry();
                    en.Name = n;
                    en.Attr = fd.dwFileAttributes;
                    en.IsDir = (fd.dwFileAttributes & FNative.FILE_ATTRIBUTE_DIRECTORY) != 0;
                    en.Size = en.IsDir ? 0 : (((long)fd.nFileSizeHigh) << 32) | (long)fd.nFileSizeLow;
                    en.Modified = (((long)fd.ftWriteHigh) << 32) | (uint)fd.ftWriteLow;
                    en.Created = (((long)fd.ftCreationHigh) << 32) | (uint)fd.ftCreationLow;

                    if (en.IsDir) en.Ext = "";
                    else
                    {
                        int dot = n.LastIndexOf('.');
                        en.Ext = (dot > 0 && dot < n.Length - 1)
                            ? n.Substring(dot + 1).ToLowerInvariant() : "";
                    }

                    if ((en.Attr & (FNative.FILE_ATTRIBUTE_HIDDEN | FNative.FILE_ATTRIBUTE_SYSTEM)) != 0)
                        hiddenCount++;

                    outList.Add(en);
                }
                while (FNative.FindNextFileW(h, out fd));
            }
            finally { FNative.FindClose(h); }

            return outList;
        }

        static int CmpName(Entry a, Entry b)
        {
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        }

        internal sealed class Cmp : IComparer<Entry>
        {
            public string Key;
            public bool Desc;
            public bool DirsFirst;

            public int Compare(Entry a, Entry b)
            {
                if (DirsFirst && a.IsDir != b.IsDir) return a.IsDir ? -1 : 1;
                int r;
                switch (Key)
                {
                    case "size":
                        r = a.Size < b.Size ? -1 : (a.Size > b.Size ? 1 : 0);
                        if (r == 0) r = CmpName(a, b);
                        break;
                    case "modified":
                        r = a.Modified < b.Modified ? -1 : (a.Modified > b.Modified ? 1 : 0);
                        if (r == 0) r = CmpName(a, b);
                        break;
                    case "created":
                        r = a.Created < b.Created ? -1 : (a.Created > b.Created ? 1 : 0);
                        if (r == 0) r = CmpName(a, b);
                        break;
                    case "type":
                        r = string.Compare(a.Ext, b.Ext, StringComparison.OrdinalIgnoreCase);
                        if (r == 0) r = CmpName(a, b);
                        break;
                    default:
                        r = CmpName(a, b);
                        break;
                }
                return Desc ? -r : r;
            }
        }

        public static FJ Describe(Entry e, string dir)
        {
            FJ o = FJ.Obj();
            o.Set("name", e.Name);
            o.Set("path", Guard.Combine(dir.TrimEnd('\\'), e.Name));
            o.Set("isDirectory", e.IsDir);
            o.Set("size", e.IsDir ? -1L : e.Size);
            o.Set("modified", FApi.Iso(e.Modified));
            o.Set("created", FApi.Iso(e.Created));
            o.Set("ext", e.Ext);

            FJ a = FJ.Obj();
            a.Set("hidden", (e.Attr & FNative.FILE_ATTRIBUTE_HIDDEN) != 0);
            a.Set("system", (e.Attr & FNative.FILE_ATTRIBUTE_SYSTEM) != 0);
            a.Set("readonly", (e.Attr & FNative.FILE_ATTRIBUTE_READONLY) != 0);
            a.Set("archive", (e.Attr & FNative.FILE_ATTRIBUTE_ARCHIVE) != 0);
            a.Set("reparse", (e.Attr & FNative.FILE_ATTRIBUTE_REPARSE_POINT) != 0);
            a.Set("compressed", (e.Attr & FNative.FILE_ATTRIBUTE_COMPRESSED) != 0);
            a.Set("encrypted", (e.Attr & FNative.FILE_ATTRIBUTE_ENCRYPTED) != 0);
            a.Set("sparse", (e.Attr & FNative.FILE_ATTRIBUTE_SPARSE_FILE) != 0);
            a.Set("offline", (e.Attr & FNative.FILE_ATTRIBUTE_OFFLINE) != 0);
            o.Set("attributes", a);
            o.Set("attrRaw", (long)e.Attr);
            return o;
        }

        public static FJ List(FJ q)
        {
            string dir = Guard.ResolveDir(q.S("path", null), "path");
            string sort = (q.S("sort", "name") ?? "name").ToLowerInvariant();
            bool desc = q.B("desc", false);
            bool showHidden = q.B("showHidden", false);
            bool dirsFirst = q.B("dirsFirst", true);
            int offset = q.I("offset", 0);
            int limit = q.I("limit", 0);           // 0 = everything
            bool noCache = q.B("refresh", false);

            if (offset < 0) offset = 0;
            if (limit < 0) limit = 0;
            if (sort != "name" && sort != "size" && sort != "modified" && sort != "created" && sort != "type")
                throw new FsFault("bad_sort", "sort must be name, size, modified, created or type - got '" + sort + "'.");

            string key = dir.TrimEnd('\\') + "|" + sort + "|" + (desc ? 1 : 0) + "|" +
                         (showHidden ? 1 : 0) + "|" + (dirsFirst ? 1 : 0);

            List<Entry> items = null;
            int hidden = 0;
            bool fromCache = false;
            DateTime t0 = DateTime.UtcNow;

            if (!noCache)
            {
                lock (CGate)
                {
                    Cached c;
                    if (Cache.TryGetValue(key, out c) && (DateTime.UtcNow - c.At).TotalSeconds < CacheSeconds)
                    {
                        items = c.Items;
                        hidden = c.Hidden;
                        fromCache = true;
                    }
                }
            }

            if (items == null)
            {
                int hc;
                List<Entry> all = Sweep(dir, out hc);
                hidden = hc;
                if (!showHidden)
                {
                    List<Entry> keep = new List<Entry>(all.Count);
                    for (int i = 0; i < all.Count; i++)
                        if ((all[i].Attr & (FNative.FILE_ATTRIBUTE_HIDDEN | FNative.FILE_ATTRIBUTE_SYSTEM)) == 0)
                            keep.Add(all[i]);
                    all = keep;
                }
                Cmp cmp = new Cmp();
                cmp.Key = sort; cmp.Desc = desc; cmp.DirsFirst = dirsFirst;
                all.Sort(cmp);
                items = all;

                lock (CGate)
                {
                    if (Cache.Count > 24) Cache.Clear();
                    Cached c = new Cached();
                    c.Items = items; c.At = DateTime.UtcNow; c.Hidden = hidden;
                    Cache[key] = c;
                }
            }

            int total = items.Count;
            int start = Math.Min(offset, total);
            int take = limit == 0 ? total - start : Math.Min(limit, total - start);

            FJ arr = FJ.Arr();
            long bytes = 0;
            int dirs = 0, files = 0;
            for (int i = 0; i < total; i++)
            {
                if (items[i].IsDir) dirs++; else { files++; bytes += items[i].Size; }
            }
            for (int i = start; i < start + take; i++) arr.Add(Describe(items[i], dir));

            FJ o = FJ.Obj();
            o.Set("path", dir);
            o.Set("name", Guard.Leaf(dir));
            o.Set("parent", Guard.Parent(dir));
            o.Set("entries", arr);
            o.Set("count", arr.Count);
            o.Set("total", total);
            o.Set("offset", start);
            o.Set("limit", limit);
            o.Set("dirCount", dirs);
            o.Set("fileCount", files);
            o.Set("bytes", bytes);
            o.Set("hiddenPresent", hidden);
            o.Set("showHidden", showHidden);
            o.Set("sort", sort);
            o.Set("desc", desc);
            o.Set("dirsFirst", dirsFirst);
            o.Set("cached", fromCache);
            o.Set("elapsedMs", (long)(DateTime.UtcNow - t0).TotalMilliseconds);
            return o;
        }
    }

    #endregion

    #region Walking  (shared by copy, delete and sizing - never follows a reparse point)

    internal static class Walk
    {
        internal sealed class Item
        {
            public string Path;
            public bool IsDir;
            public long Size;
            public bool IsReparse;
        }

        // Breadth-first, iterative (a deep tree must not blow the stack), and it stops at every
        // reparse point: a junction is reported as an item but never descended into. That single
        // rule is what stops "delete this folder" from following a symlink into someone's
        // documents.
        //
        // onProblem is called for a directory that cannot be opened; the walk continues.
        public static List<Item> Enumerate(string root, FJobs.Job job, bool includeRoot,
                                           Action<string, string> onProblem)
        {
            List<Item> outList = new List<Item>();
            uint rootAttr = FNative.GetFileAttributesW(Guard.Ex(root));
            if (rootAttr == FNative.INVALID_FILE_ATTRIBUTES) throw FApi.LastError(root);

            bool rootIsDir = (rootAttr & FNative.FILE_ATTRIBUTE_DIRECTORY) != 0;
            bool rootIsReparse = (rootAttr & FNative.FILE_ATTRIBUTE_REPARSE_POINT) != 0;

            if (!rootIsDir || rootIsReparse)
            {
                Item only = new Item();
                only.Path = root;
                only.IsDir = rootIsDir;
                only.IsReparse = rootIsReparse;
                only.Size = rootIsDir ? 0 : FileSize(root);
                outList.Add(only);
                return outList;
            }

            if (includeRoot)
            {
                Item r = new Item();
                r.Path = root; r.IsDir = true; r.IsReparse = false;
                outList.Add(r);
            }

            Queue<string> pending = new Queue<string>();
            pending.Enqueue(root);

            while (pending.Count > 0)
            {
                if (job != null && job.CancelRequested) break;
                string dir = pending.Dequeue();

                FNative.WIN32_FIND_DATA fd;
                IntPtr h = FNative.FindFirstFileExW(Guard.Ex(Guard.Combine(dir.TrimEnd('\\'), "*")),
                    FNative.FINDEX_INFO_LEVELS.FindExInfoBasic, out fd,
                    FNative.FINDEX_SEARCH_OPS.FindExSearchNameMatch, IntPtr.Zero,
                    FNative.FIND_FIRST_EX_LARGE_FETCH);

                if (h == FNative.INVALID_HANDLE_VALUE)
                {
                    uint e = (uint)Marshal.GetLastWin32Error();
                    if (e != FApi.ERROR_NO_MORE_FILES && e != FApi.ERROR_FILE_NOT_FOUND && onProblem != null)
                        onProblem(dir, FApi.Friendly(e));
                    continue;
                }

                try
                {
                    do
                    {
                        string n = fd.cFileName;
                        if (n == "." || n == "..") continue;
                        string p = Guard.Combine(dir.TrimEnd('\\'), n);

                        Item it = new Item();
                        it.Path = p;
                        it.IsDir = (fd.dwFileAttributes & FNative.FILE_ATTRIBUTE_DIRECTORY) != 0;
                        it.IsReparse = (fd.dwFileAttributes & FNative.FILE_ATTRIBUTE_REPARSE_POINT) != 0;
                        it.Size = it.IsDir ? 0 : ((((long)fd.nFileSizeHigh) << 32) | (long)fd.nFileSizeLow);
                        outList.Add(it);

                        // The one line that makes recursion safe.
                        if (it.IsDir && !it.IsReparse) pending.Enqueue(p);
                    }
                    while (FNative.FindNextFileW(h, out fd));
                }
                finally { FNative.FindClose(h); }
            }

            return outList;
        }

        public static long FileSize(string path)
        {
            FNative.WIN32_FILE_ATTRIBUTE_DATA d;
            if (!FNative.GetFileAttributesExW(Guard.Ex(path), 0, out d)) return 0;
            return (((long)d.nFileSizeHigh) << 32) | (long)d.nFileSizeLow;
        }
    }

    #endregion

    #region Mutations - mkdir, rename, delete, copy, move

    internal static class OpsMod
    {
        // ---------------------------------------------------------------------------------
        public static FJ Mkdir(FJ q)
        {
            string parent = Guard.ResolveDir(q.S("path", null), "path");
            string name = Guard.Name(q.S("name", null));
            bool force = q.B("force", false);
            string full = Guard.Combine(parent, name);
            Guard.EnsureMutable(full, force, "created inside");

            if (!FNative.CreateDirectoryW(Guard.Ex(full), IntPtr.Zero))
            {
                uint e = (uint)Marshal.GetLastWin32Error();
                if (e == FApi.ERROR_ALREADY_EXISTS)
                    throw new FsFault("exists", "There is already something called \"" + name + "\" here.");
                throw FApi.Fault(e, full);
            }

            ListMod.Invalidate(parent);
            FJ o = FJ.Obj();
            o.Set("path", full);
            o.Set("name", name);
            o.Set("parent", parent);
            o.Set("created", true);
            return o;
        }

        // ---------------------------------------------------------------------------------
        public static FJ Rename(FJ q)
        {
            string from = Guard.Resolve(q.S("path", null), "path");
            string name = Guard.Name(q.S("name", null));
            bool force = q.B("force", false);
            Guard.EnsureMutable(from, force, "renamed");

            string parent = Guard.Parent(from);
            if (parent == null) throw new FsFault("refused_root", "A drive root has no name to change.");
            string to = Guard.Combine(parent, name);

            if (string.Equals(from, to, StringComparison.Ordinal))
            {
                FJ same = FJ.Obj();
                same.Set("path", to); same.Set("name", name); same.Set("renamed", false);
                same.Set("note", "The name was already that.");
                return same;
            }

            // A pure case change (report.txt -> Report.txt) is a rename Windows accepts, but the
            // existence probe below would see the file itself and refuse. Compare case-insensitively
            // and let MoveFileEx sort it out.
            bool caseOnly = string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly && FNative.GetFileAttributesW(Guard.Ex(to)) != FNative.INVALID_FILE_ATTRIBUTES)
                throw new FsFault("exists", "There is already something called \"" + name + "\" here.");

            if (!FNative.MoveFileExW(Guard.Ex(from), Guard.Ex(to), 0))
                throw FApi.LastError(from);

            ListMod.Invalidate(parent);
            FJ o = FJ.Obj();
            o.Set("from", from);
            o.Set("path", to);
            o.Set("name", name);
            o.Set("renamed", true);
            return o;
        }

        // ---------------------------------------------------------------------------------
        // Delete. Two genuinely different operations behind one command:
        //
        //   recycle:true  (default) - SHFileOperation with FOF_ALLOWUNDO. Reversible, and the
        //                             only way into the Recycle Bin. No progress, no cancel:
        //                             the shell API offers neither. Refused outright on a volume
        //                             with no bin, because SHFileOperation's answer to that is a
        //                             silent permanent delete and nobody asked for one.
        //   recycle:false           - our own walk. Real progress, real cancellation, and it
        //                             unlinks reparse points instead of walking through them.
        //
        // Both run as jobs: a folder of 40,000 files takes as long as it takes, and the shell's
        // UI thread cannot be the thing waiting.
        // ---------------------------------------------------------------------------------
        public static List<string> TargetList(FJ q, string verb, bool force)
        {
            List<string> paths = new List<string>();
            FJ arr = q.Get("paths");
            if (arr != null && arr.Kind == FJ.TArr)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    string s = arr.At(i).AsStr(null);
                    if (!string.IsNullOrEmpty(s)) paths.Add(s);
                }
            }
            string one = q.S("path", null);
            if (!string.IsNullOrEmpty(one)) paths.Add(one);

            if (paths.Count == 0) throw new FsFault("bad_request", "Nothing was given to " + verb + " - pass path or paths[].");
            if (paths.Count > 5000) throw new FsFault("too_many", "That is more than 5,000 items in one call.");

            List<string> full = new List<string>(paths.Count);
            for (int i = 0; i < paths.Count; i++)
            {
                string f = Guard.Resolve(paths[i], "path");
                Guard.EnsureMutable(f, force, verb + "d");
                full.Add(f);
            }
            return full;
        }

        public static FJ StartDelete(FJ q)
        {
            bool recycle = q.B("recycle", true);
            bool force = q.B("force", false);
            List<string> targets = TargetList(q, "delete", force);

            // Confirm every target exists before starting, so the UI's count is the truth.
            for (int i = 0; i < targets.Count; i++)
                if (FNative.GetFileAttributesW(Guard.Ex(targets[i])) == FNative.INVALID_FILE_ATTRIBUTES)
                    throw FApi.LastError(targets[i]);

            if (recycle)
            {
                // Refuse rather than silently destroy. The page can then offer the permanent
                // delete behind its own stronger confirmation, which is the honest flow.
                for (int i = 0; i < targets.Count; i++)
                {
                    string root = null;
                    try { root = Path.GetPathRoot(targets[i]); }
                    catch { }
                    if (root != null && !DriveMod.HasRecycleBin(root))
                        throw new FsFault("no_recycle_bin",
                            "There is no Recycle Bin on " + root + ", so this cannot be undone. " +
                            "Use a permanent delete if that is genuinely what you want.");
                }
                return JobRun.StartRecycle(targets);
            }

            return JobRun.StartPurge(targets, force);
        }

        // ---------------------------------------------------------------------------------
        public static FJ Properties(FJ q)
        {
            string full = Guard.Resolve(q.S("path", null), "path");
            bool folderSize = q.B("folderSize", false);

            FNative.WIN32_FILE_ATTRIBUTE_DATA d;
            if (!FNative.GetFileAttributesExW(Guard.Ex(full), 0, out d)) throw FApi.LastError(full);

            bool isDir = (d.dwFileAttributes & FNative.FILE_ATTRIBUTE_DIRECTORY) != 0;
            long size = (((long)d.nFileSizeHigh) << 32) | (long)d.nFileSizeLow;

            FJ o = FJ.Obj();
            o.Set("path", full);
            o.Set("name", Guard.Leaf(full));
            o.Set("parent", Guard.Parent(full));
            o.Set("isDirectory", isDir);
            o.Set("size", isDir ? -1L : size);
            o.Set("created", FApi.Iso((((long)d.ftCreationHigh) << 32) | (uint)d.ftCreationLow));
            o.Set("modified", FApi.Iso((((long)d.ftWriteHigh) << 32) | (uint)d.ftWriteLow));
            o.Set("accessed", FApi.Iso((((long)d.ftAccessHigh) << 32) | (uint)d.ftAccessLow));

            FJ a = FJ.Obj();
            a.Set("hidden", (d.dwFileAttributes & FNative.FILE_ATTRIBUTE_HIDDEN) != 0);
            a.Set("system", (d.dwFileAttributes & FNative.FILE_ATTRIBUTE_SYSTEM) != 0);
            a.Set("readonly", (d.dwFileAttributes & FNative.FILE_ATTRIBUTE_READONLY) != 0);
            a.Set("archive", (d.dwFileAttributes & FNative.FILE_ATTRIBUTE_ARCHIVE) != 0);
            a.Set("reparse", (d.dwFileAttributes & FNative.FILE_ATTRIBUTE_REPARSE_POINT) != 0);
            a.Set("compressed", (d.dwFileAttributes & FNative.FILE_ATTRIBUTE_COMPRESSED) != 0);
            a.Set("encrypted", (d.dwFileAttributes & FNative.FILE_ATTRIBUTE_ENCRYPTED) != 0);
            a.Set("sparse", (d.dwFileAttributes & FNative.FILE_ATTRIBUTE_SPARSE_FILE) != 0);
            o.Set("attributes", a);

            if (!isDir)
            {
                o.Set("ext", ExtOf(full));
                o.Set("sizeOnDisk", SizeOnDisk(full, size));
            }
            else
            {
                o.SetNull("ext");
                o.SetNull("sizeOnDisk");
            }

            o.Set("protected", Guard.ProtectedBy(full) != null);
            o.Set("driveRoot", Path.GetPathRoot(full));

            // Folder size is a walk, and a walk over a user profile is not a 250 ms operation.
            // It is offered as a job rather than made the default cost of opening a properties
            // card.
            if (isDir && folderSize)
            {
                FJ job = JobRun.StartFolderSize(full);
                o.Set("sizeJob", job);
            }
            return o;
        }

        public static string ExtOf(string full)
        {
            string n = Guard.Leaf(full);
            int dot = n.LastIndexOf('.');
            return (dot > 0 && dot < n.Length - 1) ? n.Substring(dot + 1).ToLowerInvariant() : "";
        }

        // Real allocated bytes. GetCompressedFileSize is exact for compressed and sparse files;
        // for an ordinary file it returns the logical size, which then gets rounded up to the
        // cluster - which is what Explorer means by "size on disk".
        public static long SizeOnDisk(string full, long logical)
        {
            try
            {
                uint hi;
                uint lo = FNative.GetCompressedFileSizeW(Guard.Ex(full), out hi);
                long actual;
                if (lo == 0xFFFFFFFF && Marshal.GetLastWin32Error() != 0) actual = logical;
                else actual = (((long)hi) << 32) | (long)lo;

                string root = Path.GetPathRoot(full);
                uint spc, bps, freeC, totC;
                if (root != null && FNative.GetDiskFreeSpaceW(root, out spc, out bps, out freeC, out totC))
                {
                    long cluster = (long)spc * bps;
                    if (cluster > 0)
                    {
                        long blocks = (actual + cluster - 1) / cluster;
                        return blocks * cluster;
                    }
                }
                return actual;
            }
            catch { return logical; }
        }

        // ---------------------------------------------------------------------------------
        // Copy / move. Both are jobs; both use the same callback so cancellation is identical.
        // ---------------------------------------------------------------------------------
        public static FJ StartTransfer(FJ q, bool move)
        {
            string verb = move ? "move" : "copy";
            string past = move ? "moved" : "copied";
            bool force = q.B("force", false);
            bool overwrite = q.B("overwrite", false);

            List<string> sources = new List<string>();
            FJ arr = q.Get("paths");
            if (arr != null && arr.Kind == FJ.TArr)
                for (int i = 0; i < arr.Count; i++)
                {
                    string s = arr.At(i).AsStr(null);
                    if (!string.IsNullOrEmpty(s)) sources.Add(s);
                }
            string one = q.S("path", null);
            if (!string.IsNullOrEmpty(one)) sources.Add(one);
            if (sources.Count == 0) throw new FsFault("bad_request", "Nothing was given to " + verb + " - pass path or paths[].");

            string dest = Guard.ResolveDir(q.S("to", null), "destination");
            Guard.EnsureMutable(dest, force, past + " into");

            List<string> full = new List<string>();
            for (int i = 0; i < sources.Count; i++)
            {
                string f = Guard.Resolve(sources[i], "path");
                if (FNative.GetFileAttributesW(Guard.Ex(f)) == FNative.INVALID_FILE_ATTRIBUTES)
                    throw FApi.LastError(f);
                if (move) Guard.EnsureMutable(f, force, "moved");

                // The classic foot-gun: copying a folder into itself, or into its own child,
                // recurses until the disk fills. Refuse before starting rather than half way in.
                if (Guard.IsUnder(dest, f))
                    throw new FsFault("recursive",
                        "\"" + Guard.Leaf(f) + "\" cannot be " + past + " into itself or into a folder inside it.");
                if (string.Equals(Guard.Parent(f), dest, StringComparison.OrdinalIgnoreCase) && move)
                    throw new FsFault("same_place", "\"" + Guard.Leaf(f) + "\" is already in that folder.");
                full.Add(f);
            }

            return JobRun.StartTransfer(full, dest, move, overwrite, force);
        }
    }

    #endregion

    #region Job bodies

    // Named JobRun, not Jobs: MarwanOs.Sys already has a Jobs, and a file that `using`s both
    // namespaces must still compile.
    internal static class JobRun
    {
        // ---------------------------------------------------------------------------------
        // Recycle. SHFileOperation wants a double-NUL-terminated list and an STA thread.
        // ---------------------------------------------------------------------------------
        public static FJ StartRecycle(List<string> targets)
        {
            List<string> copy = new List<string>(targets);
            FJobs.Job job = FJobs.Start("fs.delete(recycle)", delegate (FJobs.Job j)
            {
                j.Indeterminate = true;
                j.ItemsTotal = copy.Count;
                j.Report(-1, "moving to the Recycle Bin");
                j.Current = copy.Count == 1 ? Guard.Leaf(copy[0]) : (copy.Count + " items");

                StringBuilder from = new StringBuilder();
                for (int i = 0; i < copy.Count; i++) { from.Append(copy[i]); from.Append('\0'); }
                from.Append('\0');

                FNative.SHFILEOPSTRUCT op = new FNative.SHFILEOPSTRUCT();
                op.hwnd = IntPtr.Zero;
                op.wFunc = FNative.FO_DELETE;
                op.pFrom = from.ToString();
                op.pTo = null;
                op.fFlags = (ushort)(FNative.FOF_ALLOWUNDO | FNative.FOF_NO_UI);
                op.lpszProgressTitle = "MarwanOS";

                int rc = FNative.SHFileOperationW(ref op);

                FJ o = FJ.Obj();
                o.Set("recycled", rc == 0 && !op.fAnyOperationsAborted);
                o.Set("items", copy.Count);
                o.Set("aborted", op.fAnyOperationsAborted);
                o.Set("shResult", rc);

                for (int i = 0; i < copy.Count; i++)
                {
                    string p = Guard.Parent(copy[i]);
                    if (p != null) ListMod.Invalidate(p);
                }

                if (rc != 0)
                {
                    // SHFileOperation returns its own codes, not Win32 ones; the documented
                    // handful are worth naming, the rest get an honest "it refused".
                    string why;
                    switch (rc)
                    {
                        case 0x7C: why = "The path is too long or malformed."; break;
                        case 0x75: why = "The operation was cancelled by the user."; break;
                        case 0x10000: why = "An unspecified shell error occurred."; break;
                        case 0x402: why = "The item no longer exists."; break;
                        case 0x78: why = "Access denied - the security settings on the file do not allow it."; break;
                        case 0x79: why = "The path is too long."; break;
                        case 0x74: why = "The destination has no room."; break;
                        default: why = "The Windows shell refused the delete (code 0x" +
                                       rc.ToString("X", CultureInfo.InvariantCulture) + ")."; break;
                    }
                    throw new FsFault("recycle_failed", why);
                }
                if (op.fAnyOperationsAborted)
                    throw new FsFault("cancelled", "The delete stopped before it finished; some items may still be there.");

                j.ItemsDone = copy.Count;
                return o;
            }, true);

            return Describe(job);
        }

        // ---------------------------------------------------------------------------------
        // Permanent delete. Our own walk, so: real progress, real cancellation, and reparse
        // points get unlinked rather than followed.
        // ---------------------------------------------------------------------------------
        public static FJ StartPurge(List<string> targets, bool force)
        {
            List<string> copy = new List<string>(targets);
            FJobs.Job job = FJobs.Start("fs.delete(permanent)", delegate (FJobs.Job j)
            {
                j.Report(-1, "counting");
                j.Indeterminate = true;

                List<Walk.Item> all = new List<Walk.Item>();
                for (int i = 0; i < copy.Count; i++)
                {
                    if (j.CancelRequested) break;
                    j.Current = copy[i];
                    List<Walk.Item> part = Walk.Enumerate(copy[i], j, true,
                        delegate (string p, string why) { j.Problem(p, why); });
                    all.AddRange(part);
                }

                long bytes = 0;
                for (int i = 0; i < all.Count; i++) bytes += all[i].Size;
                j.ItemsTotal = all.Count;
                j.BytesTotal = bytes;
                j.Indeterminate = false;
                j.Report(0, "deleting");

                // Deepest first, so a directory is always empty by the time we reach it.
                all.Sort(delegate (Walk.Item a, Walk.Item b)
                {
                    int da = Depth(a.Path), db = Depth(b.Path);
                    if (da != db) return db - da;
                    if (a.IsDir != b.IsDir) return a.IsDir ? 1 : -1;
                    return string.Compare(b.Path, a.Path, StringComparison.OrdinalIgnoreCase);
                });

                long deleted = 0, failed = 0;
                for (int i = 0; i < all.Count; i++)
                {
                    if (j.CancelRequested) break;
                    Walk.Item it = all[i];
                    j.Current = it.Path;

                    bool ok;
                    if (it.IsDir && !it.IsReparse) ok = FNative.RemoveDirectoryW(Guard.Ex(it.Path));
                    else if (it.IsDir && it.IsReparse) ok = FNative.RemoveDirectoryW(Guard.Ex(it.Path));  // unlink, do not follow
                    else ok = FNative.DeleteFileW(Guard.Ex(it.Path));

                    if (!ok)
                    {
                        uint e = (uint)Marshal.GetLastWin32Error();
                        // A read-only attribute is not a permission problem, it is a flag. Clear
                        // it and try once more; anything else is reported and skipped.
                        if (e == FApi.ERROR_ACCESS_DENIED)
                        {
                            uint at = FNative.GetFileAttributesW(Guard.Ex(it.Path));
                            if (at != FNative.INVALID_FILE_ATTRIBUTES && (at & FNative.FILE_ATTRIBUTE_READONLY) != 0)
                            {
                                FNative.SetFileAttributesW(Guard.Ex(it.Path), at & ~FNative.FILE_ATTRIBUTE_READONLY);
                                ok = it.IsDir ? FNative.RemoveDirectoryW(Guard.Ex(it.Path))
                                              : FNative.DeleteFileW(Guard.Ex(it.Path));
                                if (!ok) e = (uint)Marshal.GetLastWin32Error();
                            }
                        }
                        if (!ok)
                        {
                            failed++;
                            j.Problem(it.Path, FApi.Friendly(e));
                        }
                    }

                    if (ok) { deleted++; j.BytesDone += it.Size; }
                    j.ItemsDone = deleted + failed;
                }

                for (int i = 0; i < copy.Count; i++)
                {
                    string p = Guard.Parent(copy[i]);
                    if (p != null) ListMod.Invalidate(p);
                }

                FJ o = FJ.Obj();
                o.Set("deleted", deleted);
                o.Set("failed", failed);
                o.Set("items", all.Count);
                o.Set("bytes", j.BytesDone);
                o.Set("permanent", true);
                o.Set("cancelled", j.CancelRequested);
                return o;
            });

            return Describe(job);
        }

        // ---------------------------------------------------------------------------------
        // Copy / move.
        //
        // Phase 1 measures: walk every source, sum the bytes. Phase 2 transfers with CopyFileEx
        // (or MoveFileWithProgress), whose callback both reports and cancels. The callback is a
        // managed delegate handed to unmanaged code, so it is held in a local for the duration of
        // the call - letting the GC collect it mid-copy would be a hard crash rather than a bug.
        // ---------------------------------------------------------------------------------
        public static FJ StartTransfer(List<string> sources, string dest, bool move, bool overwrite, bool force)
        {
            List<string> srcs = new List<string>(sources);
            string cmdName = move ? "fs.move" : "fs.copy";

            FJobs.Job job = FJobs.Start(cmdName, delegate (FJobs.Job j)
            {
                j.Indeterminate = true;
                j.Report(-1, "measuring");

                // ---- phase 1: measure ----
                List<Walk.Item> plan = new List<Walk.Item>();
                List<string> roots = new List<string>();
                long totalBytes = 0;
                int fileCount = 0, dirCount = 0;

                for (int i = 0; i < srcs.Count; i++)
                {
                    if (j.CancelRequested) break;
                    string src = srcs[i];
                    j.Current = src;
                    List<Walk.Item> part = Walk.Enumerate(src, j, true,
                        delegate (string p, string why) { j.Problem(p, why); });
                    for (int k = 0; k < part.Count; k++)
                    {
                        plan.Add(part[k]);
                        roots.Add(src);
                        if (part[k].IsDir) dirCount++;
                        else { fileCount++; totalBytes += part[k].Size; }
                    }
                }

                j.BytesTotal = totalBytes;
                j.ItemsTotal = fileCount + dirCount;
                j.Indeterminate = false;
                j.Report(0, move ? "moving" : "copying");

                // ---- fast path: a same-volume move is a rename, not a transfer ----
                if (move && !j.CancelRequested && SameVolumeAll(srcs, dest))
                {
                    long movedItems = 0, movedBytes = 0, failedItems = 0;
                    for (int i = 0; i < srcs.Count; i++)
                    {
                        if (j.CancelRequested) break;
                        string src = srcs[i];
                        string target = Guard.Combine(dest, Guard.Leaf(src));
                        j.Current = src;
                        if (!overwrite && FNative.GetFileAttributesW(Guard.Ex(target)) != FNative.INVALID_FILE_ATTRIBUTES)
                        {
                            failedItems++;
                            j.Problem(target, "Something with that name is already there.");
                            continue;
                        }
                        uint flags = overwrite ? FNative.MOVEFILE_REPLACE_EXISTING : 0;
                        if (!FNative.MoveFileExW(Guard.Ex(src), Guard.Ex(target), flags))
                        {
                            failedItems++;
                            j.Problem(src, FApi.Friendly((uint)Marshal.GetLastWin32Error()));
                            continue;
                        }
                        movedItems++;
                        movedBytes += SubtreeBytes(plan, roots, src);
                        j.BytesDone = movedBytes;
                        j.ItemsDone = movedItems;
                    }

                    InvalidateAround(srcs, dest);
                    FJ fo = FJ.Obj();
                    fo.Set("moved", movedItems);
                    fo.Set("failed", failedItems);
                    fo.Set("bytes", movedBytes);
                    fo.Set("sameVolume", true);
                    fo.Set("cancelled", j.CancelRequested);
                    fo.Set("destination", dest);
                    j.BytesDone = j.BytesTotal;   // a rename transfers no bytes; the bar must still finish
                    return fo;
                }

                // ---- phase 2: transfer ----
                long baseDone = 0;
                long filesDone = 0, filesFailed = 0, dirsMade = 0;

                // Directories first, shallowest first, so a file always has somewhere to land.
                List<int> order = new List<int>();
                for (int i = 0; i < plan.Count; i++) if (plan[i].IsDir) order.Add(i);
                order.Sort(delegate (int a, int b) { return Depth(plan[a].Path) - Depth(plan[b].Path); });
                for (int i = 0; i < plan.Count; i++) if (!plan[i].IsDir) order.Add(i);

                for (int oi = 0; oi < order.Count; oi++)
                {
                    if (j.CancelRequested) break;
                    int i = order[oi];
                    Walk.Item it = plan[i];
                    string root = roots[i];
                    string target = MapTarget(it.Path, root, dest);
                    j.Current = it.Path;

                    if (it.IsDir)
                    {
                        // A junction is recreated as a plain folder rather than followed. Copying
                        // the link itself would need the same privilege as creating one, and
                        // silently duplicating whatever it pointed at would be worse.
                        if (!FNative.CreateDirectoryW(Guard.Ex(target), IntPtr.Zero))
                        {
                            uint e = (uint)Marshal.GetLastWin32Error();
                            if (e != FApi.ERROR_ALREADY_EXISTS)
                            {
                                j.Problem(target, FApi.Friendly(e));
                                continue;
                            }
                        }
                        else dirsMade++;
                        if (it.IsReparse) j.Problem(it.Path, "This is a link; a plain folder was created instead of following it.");
                        j.ItemsDone = filesDone + dirsMade;
                        continue;
                    }

                    long thisFile = it.Size;
                    long startedAt = baseDone;

                    FNative.CopyProgressRoutine cb = delegate (long totalFileSize, long transferred,
                        long streamSize, long streamTransferred, uint streamNumber, uint reason,
                        IntPtr hSrc, IntPtr hDst, IntPtr data)
                    {
                        j.BytesDone = startedAt + transferred;
                        // This is the whole cancellation story: the OS asks us on every chunk,
                        // and PROGRESS_CANCEL makes it abandon the transfer and remove the
                        // partial destination file itself.
                        return j.CancelRequested ? FNative.PROGRESS_CANCEL : FNative.PROGRESS_CONTINUE;
                    };

                    int pbCancel = 0;
                    bool ok;
                    if (move)
                    {
                        uint flags = FNative.MOVEFILE_COPY_ALLOWED |
                                     (overwrite ? FNative.MOVEFILE_REPLACE_EXISTING : 0);
                        ok = FNative.MoveFileWithProgressW(Guard.Ex(it.Path), Guard.Ex(target), cb, IntPtr.Zero, flags);
                    }
                    else
                    {
                        uint flags = overwrite ? 0 : FNative.COPY_FILE_FAIL_IF_EXISTS;
                        ok = FNative.CopyFileExW(Guard.Ex(it.Path), Guard.Ex(target), cb, IntPtr.Zero, ref pbCancel, flags);
                    }
                    GC.KeepAlive(cb);

                    if (!ok)
                    {
                        uint e = (uint)Marshal.GetLastWin32Error();
                        if (e == FApi.ERROR_REQUEST_ABORTED || j.CancelRequested) break;
                        filesFailed++;
                        j.Problem(it.Path, FApi.Friendly(e));
                        baseDone += thisFile;         // keep the bar honest about what is left
                        j.BytesDone = baseDone;
                    }
                    else
                    {
                        filesDone++;
                        baseDone += thisFile;
                        j.BytesDone = baseDone;
                    }
                    j.ItemsDone = filesDone + dirsMade;
                }

                // ---- a cross-volume move deletes the sources it managed to copy ----
                long removed = 0;
                if (move && !j.CancelRequested && filesFailed == 0)
                {
                    j.Report(-1, "removing the originals");
                    for (int i = 0; i < srcs.Count; i++)
                    {
                        List<Walk.Item> part = Walk.Enumerate(srcs[i], j, true, null);
                        part.Sort(delegate (Walk.Item a, Walk.Item b) { return Depth(b.Path) - Depth(a.Path); });
                        for (int k = 0; k < part.Count; k++)
                        {
                            bool okd = part[k].IsDir ? FNative.RemoveDirectoryW(Guard.Ex(part[k].Path))
                                                     : FNative.DeleteFileW(Guard.Ex(part[k].Path));
                            if (okd) removed++;
                            else j.Problem(part[k].Path, "Copied, but the original could not be removed: " +
                                                          FApi.Friendly((uint)Marshal.GetLastWin32Error()));
                        }
                    }
                }
                else if (move && (j.CancelRequested || filesFailed > 0))
                {
                    j.Problem(dest, "The originals were left in place because the transfer did not finish cleanly.");
                }

                InvalidateAround(srcs, dest);

                FJ o = FJ.Obj();
                o.Set(move ? "moved" : "copied", filesDone);
                o.Set("failed", filesFailed);
                o.Set("foldersCreated", dirsMade);
                o.Set("bytes", j.BytesDone);
                o.Set("bytesTotal", j.BytesTotal);
                o.Set("originalsRemoved", removed);
                o.Set("sameVolume", false);
                o.Set("cancelled", j.CancelRequested);
                o.Set("destination", dest);
                return o;
            });

            return Describe(job);
        }

        static void InvalidateAround(List<string> srcs, string dest)
        {
            ListMod.Invalidate(dest);
            for (int i = 0; i < srcs.Count; i++)
            {
                string p = Guard.Parent(srcs[i]);
                if (p != null) ListMod.Invalidate(p);
            }
        }

        static long SubtreeBytes(List<Walk.Item> plan, List<string> roots, string root)
        {
            long n = 0;
            for (int i = 0; i < plan.Count; i++)
                if (string.Equals(roots[i], root, StringComparison.OrdinalIgnoreCase)) n += plan[i].Size;
            return n;
        }

        static bool SameVolumeAll(List<string> srcs, string dest)
        {
            string dr;
            try { dr = Path.GetPathRoot(dest); }
            catch { return false; }
            if (string.IsNullOrEmpty(dr)) return false;
            for (int i = 0; i < srcs.Count; i++)
            {
                string sr;
                try { sr = Path.GetPathRoot(srcs[i]); }
                catch { return false; }
                if (!string.Equals(sr, dr, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        static string MapTarget(string itemPath, string root, string dest)
        {
            string leaf = Guard.Leaf(root);
            if (string.Equals(itemPath, root, StringComparison.OrdinalIgnoreCase))
                return Guard.Combine(dest, leaf);
            string rel = itemPath.Substring(root.TrimEnd('\\').Length).TrimStart('\\');
            return Guard.Combine(Guard.Combine(dest, leaf), rel);
        }

        static int Depth(string p)
        {
            int n = 0;
            for (int i = 0; i < p.Length; i++) if (p[i] == '\\') n++;
            return n;
        }

        // ---------------------------------------------------------------------------------
        public static FJ StartFolderSize(string dir)
        {
            FJobs.Job job = FJobs.Start("fs.properties(folderSize)", delegate (FJobs.Job j)
            {
                j.Indeterminate = true;
                j.Report(-1, "measuring");
                long bytes = 0, files = 0, folders = 0, links = 0;

                List<Walk.Item> all = Walk.Enumerate(dir, j, false,
                    delegate (string p, string why) { j.Problem(p, why); });

                for (int i = 0; i < all.Count; i++)
                {
                    if (j.CancelRequested) break;
                    if (all[i].IsReparse) { links++; continue; }
                    if (all[i].IsDir) folders++;
                    else { files++; bytes += all[i].Size; }
                    if ((i & 1023) == 0) j.Current = all[i].Path;
                }

                j.ItemsTotal = all.Count;
                j.ItemsDone = all.Count;
                j.Indeterminate = false;

                FJ o = FJ.Obj();
                o.Set("path", dir);
                o.Set("bytes", bytes);
                o.Set("files", files);
                o.Set("folders", folders);
                o.Set("links", links);
                o.Set("note", links > 0 ? "Links were counted but not followed." : null);
                o.Set("cancelled", j.CancelRequested);
                return o;
            });

            return Describe(job);
        }

        public static FJ Describe(FJobs.Job j)
        {
            FJ o = FJ.Obj();
            o.Set("jobId", j.Id);
            o.Set("state", j.State);
            o.Set("async", true);
            o.Set("poll", "{\"cmd\":\"job.status\",\"jobId\":\"" + j.Id + "\"}");
            return o;
        }
    }

    #endregion

    #region Eject

    internal static class EjectMod
    {
        // ---------------------------------------------------------------------------------
        // Safe removal, in the order Windows itself tries it.
        //
        //   1. Find the volume's storage device number, walk the SetupAPI disk interfaces to the
        //      matching devinst, then CM_Get_Parent up to the USB device.
        //   2. CM_Request_Device_Eject on that parent. This is the real "safely remove": it asks
        //      every driver and every application, and any of them may VETO. The veto type and
        //      the veto name are the honest answer to "why can't I?" - PendingClose means a
        //      handle is still open, and the name is usually the process holding it.
        //   3. If step 1 or 2 is not available, fall back to lock/dismount/eject on the volume
        //      handle. That path needs administrator rights for FSCTL_LOCK_VOLUME on a fixed
        //      disk and will say so rather than pretending.
        //
        // A fixed internal disk is refused outright unless force:true - ejecting the boot volume
        // is not a thing a television should offer.
        // ---------------------------------------------------------------------------------
        public static FJ Eject(FJ q)
        {
            string raw = q.S("drive", null);
            if (string.IsNullOrEmpty(raw)) raw = q.S("path", null);
            if (string.IsNullOrEmpty(raw)) throw new FsFault("bad_request", "Which drive? Pass drive:\"E\" or drive:\"E:\\\\\".");

            string letter = raw.Trim().TrimStart('\\').Substring(0, 1).ToUpperInvariant();
            if (letter[0] < 'A' || letter[0] > 'Z') throw new FsFault("bad_request", "\"" + raw + "\" is not a drive letter.");
            bool force = q.B("force", false);

            DriveMod.Vol v = DriveMod.Read(letter, true);

            if (v.Type == FNative.DRIVE_NO_ROOT_DIR || v.Type == FNative.DRIVE_UNKNOWN)
                throw new FsFault("no_such_drive", "There is no drive " + letter + ": on this machine.");

            if (!v.Removable && !force)
                throw new FsFault("not_removable",
                    "Drive " + letter + ": is an internal " + v.BusName + " " + v.TypeName +
                    " volume, not a removable one. Nothing was ejected.");

            FJ o = FJ.Obj();
            o.Set("drive", letter);
            o.Set("label", v.Label);
            o.Set("busType", v.BusName);
            o.Set("driveType", v.TypeName);

            uint devInst;
            string idErr;
            if (TryFindDevInst(letter, out devInst, out idErr))
            {
                StringBuilder id = new StringBuilder(512);
                if (FNative.CM_Get_Device_IDW(devInst, id, id.Capacity, 0) == FNative.CR_SUCCESS)
                    o.Set("device", id.ToString());

                FNative.PNP_VETO_TYPE veto;
                StringBuilder vetoName = new StringBuilder(512);
                int cr = FNative.CM_Request_Device_EjectW(devInst, out veto, vetoName, vetoName.Capacity, 0);

                o.Set("method", "CM_Request_Device_Eject");
                o.Set("cmResult", cr);
                if (cr == FNative.CR_SUCCESS && veto == FNative.PNP_VETO_TYPE.Ok)
                {
                    o.Set("ejected", true);
                    o.Set("detail", "Drive " + letter + ": is safe to unplug.");
                    return o;
                }

                string who = vetoName.ToString();
                string why = VetoText(veto, who);
                o.Set("ejected", false);
                o.Set("veto", veto.ToString());
                o.Set("vetoName", string.IsNullOrEmpty(who) ? null : who);
                throw new FsFault("eject_vetoed", why, o);
            }

            // ---- fallback: lock, dismount, eject the volume itself ----
            o.Set("method", "volume lock/dismount");
            o.Set("devInstNote", idErr);

            IntPtr h = FNative.CreateFileW("\\\\.\\" + letter + ":",
                FNative.GENERIC_READ | FNative.GENERIC_WRITE,
                FNative.FILE_SHARE_READ | FNative.FILE_SHARE_WRITE, IntPtr.Zero,
                FNative.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == FNative.INVALID_HANDLE_VALUE)
            {
                uint e = (uint)Marshal.GetLastWin32Error();
                o.Set("ejected", false);
                throw new FsFault(e == FApi.ERROR_ACCESS_DENIED ? "elevation_required" : FApi.CodeFor(e),
                    e == FApi.ERROR_ACCESS_DENIED
                        ? ("Opening drive " + letter + ": directly needs administrator rights, and the shell runs as a standard user. " +
                           "Safe removal through the device (the usual path) was not available for this volume either.")
                        : FApi.Friendly(e, letter + ":"), o);
            }

            try
            {
                uint ret;
                if (!FNative.DeviceIoControl(h, FNative.FSCTL_LOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out ret, IntPtr.Zero))
                {
                    uint e = (uint)Marshal.GetLastWin32Error();
                    o.Set("ejected", false);
                    throw new FsFault("in_use",
                        "Drive " + letter + ": could not be locked, which means a program still has a file open on it. " +
                        "Close whatever is using it and try again.  (" + FApi.Friendly(e) + ")", o);
                }
                if (!FNative.DeviceIoControl(h, FNative.FSCTL_DISMOUNT_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out ret, IntPtr.Zero))
                {
                    uint e = (uint)Marshal.GetLastWin32Error();
                    o.Set("ejected", false);
                    throw new FsFault("dismount_failed", "Drive " + letter + ": could not be dismounted.  (" + FApi.Friendly(e) + ")", o);
                }

                // Allow the media to leave, then ask the device to spit it out. A USB stick
                // ignores EJECT_MEDIA and that is fine - after the dismount it is safe to unplug.
                IntPtr pmr = IntPtr.Zero;
                try
                {
                    FNative.PREVENT_MEDIA_REMOVAL s = new FNative.PREVENT_MEDIA_REMOVAL();
                    s.PreventMediaRemoval = false;
                    int sz = Marshal.SizeOf(typeof(FNative.PREVENT_MEDIA_REMOVAL));
                    pmr = Marshal.AllocHGlobal(sz);
                    Marshal.StructureToPtr(s, pmr, false);
                    FNative.DeviceIoControl(h, FNative.IOCTL_STORAGE_MEDIA_REMOVAL, pmr, (uint)sz, IntPtr.Zero, 0, out ret, IntPtr.Zero);
                }
                finally { if (pmr != IntPtr.Zero) Marshal.FreeHGlobal(pmr); }

                bool spat = FNative.DeviceIoControl(h, FNative.IOCTL_STORAGE_EJECT_MEDIA, IntPtr.Zero, 0, IntPtr.Zero, 0, out ret, IntPtr.Zero);
                o.Set("ejected", true);
                o.Set("mediaEjected", spat);
                o.Set("detail", spat
                    ? ("Drive " + letter + ": was ejected.")
                    : ("Drive " + letter + ": was dismounted and is safe to unplug. The device did not support a physical eject, which is normal for a USB stick."));
                return o;
            }
            finally { FNative.CloseHandle(h); }
        }

        static string VetoText(FNative.PNP_VETO_TYPE veto, string who)
        {
            string tail = string.IsNullOrEmpty(who) ? "" : ("  (" + who + ")");
            switch (veto)
            {
                case FNative.PNP_VETO_TYPE.OutstandingOpen:
                case FNative.PNP_VETO_TYPE.PendingClose:
                    return "A program still has a file open on that drive, so Windows would not release it. " +
                           "Close it and try again." + tail;
                case FNative.PNP_VETO_TYPE.WindowsApp:
                    return "An application is still using the drive." + tail;
                case FNative.PNP_VETO_TYPE.WindowsService:
                    return "A Windows service is still using the drive." + tail;
                case FNative.PNP_VETO_TYPE.Device:
                case FNative.PNP_VETO_TYPE.Driver:
                    return "The device driver refused to release the drive." + tail;
                case FNative.PNP_VETO_TYPE.NonDisableable:
                    return "That device cannot be removed while Windows is running." + tail;
                case FNative.PNP_VETO_TYPE.InsufficientRights:
                    return "The shell does not have the rights to remove that device." + tail;
                case FNative.PNP_VETO_TYPE.IllegalDeviceRequest:
                    return "Windows would not accept a removal request for that device." + tail;
                case FNative.PNP_VETO_TYPE.TypeUnknown:
                    return "Windows refused the removal without saying why. Try closing anything that was reading the drive." + tail;
            }
            return "Windows refused to remove the device (" + veto.ToString() + ")." + tail;
        }

        // Volume letter -> device number -> disk interface -> devinst -> parent devinst.
        static bool TryFindDevInst(string letter, out uint devInst, out string err)
        {
            devInst = 0;
            err = null;

            uint deviceNumber;
            uint deviceType;
            if (!TryDeviceNumber(letter, out deviceNumber, out deviceType, out err)) return false;

            Guid iface = deviceType == 2 /* FILE_DEVICE_CD_ROM */
                ? FNative.GUID_DEVINTERFACE_CDROM : FNative.GUID_DEVINTERFACE_DISK;

            IntPtr set = FNative.SetupDiGetClassDevsW(ref iface, IntPtr.Zero, IntPtr.Zero,
                FNative.DIGCF_PRESENT | FNative.DIGCF_DEVICEINTERFACE);
            if (set == FNative.INVALID_HANDLE_VALUE)
            {
                err = "The device list could not be opened: " + FApi.Friendly((uint)Marshal.GetLastWin32Error());
                return false;
            }

            try
            {
                for (uint idx = 0; idx < 512; idx++)
                {
                    FNative.SP_DEVICE_INTERFACE_DATA did = new FNative.SP_DEVICE_INTERFACE_DATA();
                    did.cbSize = Marshal.SizeOf(typeof(FNative.SP_DEVICE_INTERFACE_DATA));
                    if (!FNative.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref iface, idx, ref did)) break;

                    FNative.SP_DEVINFO_DATA dinfo = new FNative.SP_DEVINFO_DATA();
                    dinfo.cbSize = Marshal.SizeOf(typeof(FNative.SP_DEVINFO_DATA));

                    uint need;
                    FNative.SetupDiGetDeviceInterfaceDetailW(set, ref did, IntPtr.Zero, 0, out need, ref dinfo);
                    if (need == 0) continue;

                    IntPtr detail = Marshal.AllocHGlobal((int)need);
                    try
                    {
                        // SP_DEVICE_INTERFACE_DETAIL_DATA_W: cbSize is 8 on x64 (4-byte DWORD +
                        // 4 bytes of padding before a WCHAR[ANYSIZE] at offset 4... the struct is
                        // packed such that the documented value is 8 for Unicode on 64-bit).
                        Marshal.WriteInt32(detail, 8);
                        if (!FNative.SetupDiGetDeviceInterfaceDetailW(set, ref did, detail, need, out need, ref dinfo))
                            continue;

                        string path = Marshal.PtrToStringUni(new IntPtr(detail.ToInt64() + 4));
                        if (string.IsNullOrEmpty(path)) continue;

                        uint dn, dt;
                        string ignore;
                        if (!TryDeviceNumberByPath(path, out dn, out dt, out ignore)) continue;
                        if (dn != deviceNumber) continue;

                        uint parent;
                        int cr = FNative.CM_Get_Parent(out parent, dinfo.DevInst, 0);
                        if (cr != FNative.CR_SUCCESS)
                        {
                            err = "The drive's parent device could not be found (CM_Get_Parent = " + cr + ").";
                            return false;
                        }
                        devInst = parent;
                        return true;
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { FNative.SetupDiDestroyDeviceInfoList(set); }

            err = "No storage device in the system matched drive " + letter + ":.";
            return false;
        }

        static bool TryDeviceNumber(string letter, out uint number, out uint type, out string err)
        {
            return TryDeviceNumberByPath("\\\\.\\" + letter + ":", out number, out type, out err);
        }

        static bool TryDeviceNumberByPath(string path, out uint number, out uint type, out string err)
        {
            number = 0; type = 0; err = null;
            IntPtr h = FNative.CreateFileW(path, 0,
                FNative.FILE_SHARE_READ | FNative.FILE_SHARE_WRITE, IntPtr.Zero,
                FNative.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == FNative.INVALID_HANDLE_VALUE)
            {
                err = "Could not open " + path + ": " + FApi.Friendly((uint)Marshal.GetLastWin32Error());
                return false;
            }
            IntPtr buf = IntPtr.Zero;
            try
            {
                int sz = Marshal.SizeOf(typeof(FNative.STORAGE_DEVICE_NUMBER));
                buf = Marshal.AllocHGlobal(sz);
                uint ret;
                if (!FNative.DeviceIoControl(h, FNative.IOCTL_STORAGE_GET_DEVICE_NUMBER,
                        IntPtr.Zero, 0, buf, (uint)sz, out ret, IntPtr.Zero))
                {
                    err = "The drive did not report a device number: " + FApi.Friendly((uint)Marshal.GetLastWin32Error());
                    return false;
                }
                FNative.STORAGE_DEVICE_NUMBER n =
                    (FNative.STORAGE_DEVICE_NUMBER)Marshal.PtrToStructure(buf, typeof(FNative.STORAGE_DEVICE_NUMBER));
                number = n.DeviceNumber;
                type = n.DeviceType;
                return true;
            }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                FNative.CloseHandle(h);
            }
        }
    }

    #endregion

    #region Watching

    // FileSystemWatcher for a directory, plus a drive poller for USB arrival and departure.
    //
    // Volume arrival is normally WM_DEVICECHANGE, which needs a window - and the window in this
    // process belongs to ShellHostWeb, not to this file. Rather than reach into someone else's
    // message loop, the drive watcher polls GetLogicalDrives every 1.5 s on its own thread. That
    // is two machine instructions' worth of work and it makes this module genuinely standalone;
    // if the host later wants to feed it real WM_DEVICECHANGE events, Watchers.PushDriveEvent is
    // the single entry point to call.
    internal static class WatchMod
    {
        sealed class Watcher
        {
            public string Id;
            public string Path;
            public FileSystemWatcher Fsw;
            public bool Drives;
            public Thread Poller;
            public volatile bool Stop;
            public uint LastMask;
        }

        static readonly object Gate = new object();
        static readonly Dictionary<string, Watcher> Map = new Dictionary<string, Watcher>(StringComparer.OrdinalIgnoreCase);
        static readonly List<FJ> Events = new List<FJ>();
        static long _seq;
        static int _wid;
        const int MaxEvents = 512;

        static void Push(string kind, string path, string extra)
        {
            lock (Gate)
            {
                _seq++;
                FJ e = FJ.Obj();
                e.Set("seq", _seq);
                e.Set("kind", kind);
                e.Set("path", path);
                if (extra != null) e.Set("detail", extra);
                e.Set("atUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                Events.Add(e);
                while (Events.Count > MaxEvents) Events.RemoveAt(0);

                // Any change under a directory makes its cached listing stale.
                string dir = null;
                try { dir = Guard.Parent(path); }
                catch { }
                if (dir != null) ListMod.Invalidate(dir);
            }
        }

        // The hook a host with a real message loop can call from WM_DEVICECHANGE.
        public static void PushDriveEvent(bool arrived, string root)
        {
            Push(arrived ? "drive.added" : "drive.removed", root, null);
            ListMod.Invalidate(null);
        }

        public static FJ Watch(FJ q)
        {
            bool drives = q.B("drives", false);
            string path = q.S("path", null);

            if (!drives && string.IsNullOrEmpty(path))
                throw new FsFault("bad_request", "Pass path to watch a folder, or drives:true to watch for drives appearing.");

            lock (Gate)
            {
                if (Map.Count > 8) throw new FsFault("too_many_watchers", "Too many watchers are already running; unwatch one first.");
            }

            Watcher w = new Watcher();
            _wid++;
            w.Id = "watch-" + _wid.ToString(CultureInfo.InvariantCulture);

            if (drives)
            {
                w.Drives = true;
                w.Path = "(drives)";
                w.LastMask = FNative.GetLogicalDrives();
                Watcher wref = w;
                w.Poller = new Thread(delegate ()
                {
                    while (!wref.Stop)
                    {
                        try
                        {
                            uint now = FNative.GetLogicalDrives();
                            uint changed = now ^ wref.LastMask;
                            if (changed != 0)
                            {
                                for (int i = 0; i < 26; i++)
                                {
                                    if ((changed & (1u << i)) == 0) continue;
                                    string root = ((char)('A' + i)).ToString(CultureInfo.InvariantCulture) + ":\\";
                                    bool arrived = (now & (1u << i)) != 0;
                                    Push(arrived ? "drive.added" : "drive.removed", root, null);
                                }
                                wref.LastMask = now;
                                ListMod.Invalidate(null);
                            }
                        }
                        catch { /* a poll must never be able to stop the loop */ }
                        Thread.Sleep(1500);
                    }
                });
                w.Poller.IsBackground = true;
                w.Poller.Name = "arc-fileapi-drivewatch";
                w.Poller.Start();
            }
            else
            {
                string dir = Guard.ResolveDir(path, "path");
                w.Path = dir;
                FileSystemWatcher f = new FileSystemWatcher(dir);
                f.IncludeSubdirectories = q.B("recursive", false);
                f.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                 NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes;
                f.Created += delegate (object s, FileSystemEventArgs e) { Push("created", e.FullPath, null); };
                f.Deleted += delegate (object s, FileSystemEventArgs e) { Push("deleted", e.FullPath, null); };
                f.Changed += delegate (object s, FileSystemEventArgs e) { Push("changed", e.FullPath, null); };
                f.Renamed += delegate (object s, RenamedEventArgs e) { Push("renamed", e.FullPath, e.OldFullPath); };
                // A watcher whose folder is pulled out from under it (the USB case) raises Error
                // and then goes deaf. Say so as an event rather than dying quietly.
                f.Error += delegate (object s, ErrorEventArgs e)
                {
                    Push("watch.error", w.Path, e.GetException() == null ? "unknown" : e.GetException().Message);
                };
                f.InternalBufferSize = 64 * 1024;
                f.EnableRaisingEvents = true;
                w.Fsw = f;
            }

            lock (Gate) { Map[w.Id] = w; }

            FJ o = FJ.Obj();
            o.Set("watchId", w.Id);
            o.Set("path", w.Path);
            o.Set("drives", w.Drives);
            o.Set("poll", "{\"cmd\":\"fs.events\",\"since\":0}");
            return o;
        }

        public static FJ Unwatch(FJ q)
        {
            string id = q.S("watchId", null);
            bool all = q.B("all", false);
            List<Watcher> kill = new List<Watcher>();
            lock (Gate)
            {
                if (all) { foreach (KeyValuePair<string, Watcher> kv in Map) kill.Add(kv.Value); Map.Clear(); }
                else
                {
                    Watcher w;
                    if (id == null || !Map.TryGetValue(id, out w))
                        throw new FsFault("no_such_watcher", "There is no watcher called '" + (id == null ? "" : id) + "'.");
                    kill.Add(w);
                    Map.Remove(id);
                }
            }
            for (int i = 0; i < kill.Count; i++)
            {
                kill[i].Stop = true;
                try { if (kill[i].Fsw != null) { kill[i].Fsw.EnableRaisingEvents = false; kill[i].Fsw.Dispose(); } }
                catch { }
            }
            FJ o = FJ.Obj();
            o.Set("stopped", kill.Count);
            return o;
        }

        public static FJ Poll(FJ q)
        {
            long since = q.L("since", 0);
            FJ arr = FJ.Arr();
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
            FJ o = FJ.Obj();
            o.Set("events", arr);
            o.Set("count", arr.Count);
            o.Set("since", since);
            o.Set("seq", high);
            lock (Gate)
            {
                FJ ws = FJ.Arr();
                foreach (KeyValuePair<string, Watcher> kv in Map)
                {
                    FJ w = FJ.Obj();
                    w.Set("watchId", kv.Value.Id);
                    w.Set("path", kv.Value.Path);
                    w.Set("drives", kv.Value.Drives);
                    ws.Add(w);
                }
                o.Set("watchers", ws);
            }
            return o;
        }
    }

    #endregion

    #region Launching

    internal static class OpenMod
    {
        // ShellExecuteEx with SEE_MASK_NOCLOSEPROCESS so we get a handle back and can report the
        // PID. The shell must job-track anything it launches - a child that outlives the shell is
        // a process nobody owns - so returning the PID rather than firing and forgetting is the
        // whole point of this command.
        //
        // hProcess comes back null when the file was handed to an ALREADY-RUNNING instance over
        // DDE or a single-instance mutex (a second .txt going to an open Notepad, say). That is
        // not a failure and it is not a PID either; it is reported honestly as pid 0 with a note,
        // and the shell should not try to job-track it.
        public static FJ Open(FJ q)
        {
            string full = Guard.Resolve(q.S("path", null), "path");
            string verb = q.S("verb", null);
            string args = q.S("args", null);

            uint at = FNative.GetFileAttributesW(Guard.Ex(full));
            if (at == FNative.INVALID_FILE_ATTRIBUTES) throw FApi.LastError(full);
            bool isDir = (at & FNative.FILE_ATTRIBUTE_DIRECTORY) != 0;

            FNative.SHELLEXECUTEINFO si = new FNative.SHELLEXECUTEINFO();
            si.cbSize = Marshal.SizeOf(typeof(FNative.SHELLEXECUTEINFO));
            si.fMask = FNative.SEE_MASK_NOCLOSEPROCESS | FNative.SEE_MASK_FLAG_NO_UI | FNative.SEE_MASK_NOASYNC;
            si.hwnd = IntPtr.Zero;
            si.lpVerb = verb;                       // null = the default handler
            si.lpFile = full;
            si.lpParameters = args;
            si.lpDirectory = isDir ? full : Guard.Parent(full);
            si.nShow = FNative.SW_SHOWNORMAL;

            if (!FNative.ShellExecuteExW(ref si))
            {
                uint e = (uint)Marshal.GetLastWin32Error();
                // The one code ShellExecute has that Win32 does not name usefully.
                if (e == 1155)
                    throw new FsFault("no_handler",
                        "Windows has no program associated with \"" + Guard.Leaf(full) + "\".");
                throw FApi.Fault(e, full);
            }

            uint pid = 0;
            if (si.hProcess != IntPtr.Zero)
            {
                pid = FNative.GetProcessId(si.hProcess);
                FNative.CloseHandle(si.hProcess);
            }

            FJ o = FJ.Obj();
            o.Set("path", full);
            o.Set("isDirectory", isDir);
            o.Set("pid", (long)pid);
            o.Set("tracked", pid != 0);
            string name = null;
            if (pid != 0)
            {
                try { name = Process.GetProcessById((int)pid).ProcessName; }
                catch { }
            }
            o.Set("process", name);
            o.Set("note", pid == 0
                ? "The file was handed to a program that was already running, so there is no new process to track."
                : null);
            return o;
        }
    }

    #endregion

    #region Public entry point

    public static class FileApi
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
                FJ req;
                try { req = ParseRequest(commandJson); }
                catch (Exception ex) { return Envelope(false, null, null, "bad_json", FApi.Describe(ex)); }

                id = req.S("reqId", null);
                cmd = req.S("cmd", null);
                if (string.IsNullOrEmpty(cmd))
                    return Envelope(false, id, null, "bad_request", "missing \"cmd\"");

                FJ data = Dispatch(cmd.Trim(), req);
                return Envelope(true, id, data, null, null);
            }
            catch (FsFault f)
            {
                return Envelope(false, id, f.Extra, f.Code, f.Message);
            }
            catch (Exception ex)
            {
                // Anything reaching here is a bug in this file, not a caller error. Include a
                // frame so it can be chased without a debugger, but still return valid JSON.
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
                    "unhandled in '" + (cmd == null ? "?" : cmd) + "': " + FApi.Describe(ex) + trace);
            }
        }

        // Accepts a full command object, or a bare command name for convenience from the CLI.
        static FJ ParseRequest(string text)
        {
            if (text == null) throw new FormatException("null command");
            string t = text.Trim();
            if (t.Length == 0) throw new FormatException("empty command");
            if (t[0] != '{')
            {
                FJ o = FJ.Obj();
                o.Set("cmd", t);
                return o;
            }
            FJ parsed = FJ.Parse(t);
            if (parsed.Kind != FJ.TObj) throw new FormatException("command must be a JSON object");
            return parsed;
        }

        static string Envelope(bool ok, string id, FJ data, string error, string detail)
        {
            FJ o = FJ.Obj();
            o.Set("ok", ok);
            if (id != null) o.Set("reqId", id);
            if (ok)
            {
                o.Set("data", data == null ? FJ.Obj() : data);
            }
            else
            {
                o.Set("error", error == null ? "error" : error);
                o.Set("detail", detail == null ? "" : detail);
                if (data != null) o.Set("data", data);
            }
            return o.ToJson();
        }

        // ------------------------------------------------------------------------------------
        static FJ Dispatch(string cmd, FJ q)
        {
            switch (cmd.ToLowerInvariant())
            {
                // ---------------- meta ----------------
                case "api.version":
                    {
                        FJ o = FJ.Obj();
                        o.Set("apiVersion", ApiVersion);
                        o.Set("api", "FileApi");
                        o.Set("elevated", FApi.IsElevated());
                        o.Set("user", Environment.UserName);
                        o.Set("machine", Environment.MachineName);
                        o.Set("home", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                        return o;
                    }
                case "api.commands":
                    return Catalog();

                // ---------------- jobs ----------------
                case "job.status":
                    {
                        FJobs.Job j = FJobs.Find(q.S("jobId", null));
                        if (j == null) throw new FsFault("no_such_job", "There is no job called '" + q.S("jobId", "") + "'.");
                        return FJobs.Describe(j);
                    }
                case "job.list":
                    {
                        List<FJobs.Job> all = FJobs.All();
                        FJ arr = FJ.Arr();
                        for (int i = 0; i < all.Count; i++) arr.Add(FJobs.Describe(all[i]));
                        return FJ.Obj().Set("jobs", arr).Set("count", arr.Count);
                    }
                case "job.cancel":
                    {
                        FJobs.Job j = FJobs.Find(q.S("jobId", null));
                        if (j == null) throw new FsFault("no_such_job", "There is no job called '" + q.S("jobId", "") + "'.");
                        j.RequestCancel();
                        return FJobs.Describe(j);
                    }

                // ---------------- reads ----------------
                case "fs.drives":
                    return DriveMod.List(q.B("busType", true));
                case "fs.list":
                    return ListMod.List(q);
                case "fs.diskspace":
                    return DriveMod.Space(q.S("path", null));
                case "fs.properties":
                    return OpsMod.Properties(q);
                case "fs.exists":
                    {
                        string full = Guard.Resolve(q.S("path", null), "path");
                        uint at = FNative.GetFileAttributesW(Guard.Ex(full));
                        FJ o = FJ.Obj();
                        o.Set("path", full);
                        o.Set("exists", at != FNative.INVALID_FILE_ATTRIBUTES);
                        o.Set("isDirectory", at != FNative.INVALID_FILE_ATTRIBUTES && (at & FNative.FILE_ATTRIBUTE_DIRECTORY) != 0);
                        return o;
                    }
                case "fs.home":
                    {
                        // The drive picker's "shortcuts" row. Everything is checked for existence,
                        // because a machine without a Videos folder must not get a dead tile.
                        FJ arr = FJ.Arr();
                        AddPlace(arr, "Home", Environment.SpecialFolder.UserProfile, "home");
                        AddPlace(arr, "Desktop", Environment.SpecialFolder.DesktopDirectory, "desktop");
                        AddPlace(arr, "Documents", Environment.SpecialFolder.MyDocuments, "doc");
                        AddPlace(arr, "Downloads", null, "dl");
                        AddPlace(arr, "Pictures", Environment.SpecialFolder.MyPictures, "photo");
                        AddPlace(arr, "Music", Environment.SpecialFolder.MyMusic, "music");
                        AddPlace(arr, "Videos", Environment.SpecialFolder.MyVideos, "video");
                        return FJ.Obj().Set("places", arr).Set("count", arr.Count);
                    }

                // ---------------- writes ----------------
                case "fs.mkdir":
                    return OpsMod.Mkdir(q);
                case "fs.rename":
                    return OpsMod.Rename(q);
                case "fs.delete":
                    return OpsMod.StartDelete(q);
                case "fs.copy":
                    return OpsMod.StartTransfer(q, false);
                case "fs.move":
                    return OpsMod.StartTransfer(q, true);

                // ---------------- devices ----------------
                case "fs.eject":
                    return EjectMod.Eject(q);

                // ---------------- watching ----------------
                case "fs.watch":
                    return WatchMod.Watch(q);
                case "fs.unwatch":
                    return WatchMod.Unwatch(q);
                case "fs.events":
                    return WatchMod.Poll(q);

                // ---------------- launching ----------------
                case "fs.open":
                    return OpenMod.Open(q);

                default:
                    throw new FsFault("unknown_command",
                        "There is no command called '" + cmd + "'. Call api.commands for the list.");
            }
        }

        static void AddPlace(FJ arr, string label, object folder, string icon)
        {
            string p = null;
            try
            {
                if (folder is Environment.SpecialFolder)
                    p = Environment.GetFolderPath((Environment.SpecialFolder)folder);
                else
                {
                    string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    if (!string.IsNullOrEmpty(home)) p = Path.Combine(home, label);
                }
            }
            catch { }
            if (string.IsNullOrEmpty(p)) return;
            uint at = FNative.GetFileAttributesW(Guard.Ex(p));
            if (at == FNative.INVALID_FILE_ATTRIBUTES || (at & FNative.FILE_ATTRIBUTE_DIRECTORY) == 0) return;
            FJ o = FJ.Obj();
            o.Set("label", label);
            o.Set("path", p);
            o.Set("icon", icon);
            arr.Add(o);
        }

        // ------------------------------------------------------------------------------------
        // Machine-readable catalogue, same shape as SystemApi's. The UI can drive itself off it:
        // which commands exist, which are slow, which mutate, which need administrator rights.
        // ------------------------------------------------------------------------------------
        static FJ Cmd(string name, string group, string kind, string elevation, bool async, string notes, string args)
        {
            FJ o = FJ.Obj();
            o.Set("cmd", name);
            o.Set("group", group);
            o.Set("kind", kind);              // read | write | action
            o.Set("elevation", elevation);    // no | maybe | required
            o.Set("async", async);            // true = returns a jobId, poll job.status
            o.Set("args", args);
            o.Set("notes", notes);
            return o;
        }

        static FJ Catalog()
        {
            FJ a = FJ.Arr();

            a.Add(Cmd("api.version", "meta", "read", "no", false, "API version and the identity this process runs as", ""));
            a.Add(Cmd("api.commands", "meta", "read", "no", false, "this catalogue", ""));
            a.Add(Cmd("job.status", "meta", "read", "no", false,
                "poll a background job; carries bytesDone/bytesTotal, current, bytesPerSec and etaSec", "jobId"));
            a.Add(Cmd("job.list", "meta", "read", "no", false, "all known jobs (pruned 10 min after completion)", ""));
            a.Add(Cmd("job.cancel", "meta", "action", "no", false,
                "request cancellation. copy/move/permanent-delete/folder-size honour it within one progress callback; a recycle cannot be cancelled", "jobId"));

            a.Add(Cmd("fs.drives", "drives", "read", "no", false,
                "every mounted volume: label, file system, total/free, removable, ready, whether it has a Recycle Bin. removable is true for DRIVE_REMOVABLE, optical, and anything on a USB/SD/MMC bus - a USB SSD reports DRIVE_FIXED and would otherwise look internal",
                "busType?=true"));
            a.Add(Cmd("fs.list", "browse", "read", "no", false,
                "one directory, never a tree. One FindFirstFileEx sweep with FindExInfoBasic + LARGE_FETCH, sorted server-side and cached for 4 s so paging is free",
                "path, sort?=name|size|modified|created|type, desc?=false, showHidden?=false, dirsFirst?=true, offset?=0, limit?=0 (0=all), refresh?=false"));
            a.Add(Cmd("fs.diskSpace", "drives", "read", "no", false, "free/total for the volume holding a path, plus cluster size", "path"));
            a.Add(Cmd("fs.properties", "browse", "read", "no", false,
                "one item: size, size on disk, timestamps, attributes. folderSize:true additionally starts a sizing JOB and returns its id in sizeJob",
                "path, folderSize?=false"));
            a.Add(Cmd("fs.exists", "browse", "read", "no", false, "cheap existence probe", "path"));
            a.Add(Cmd("fs.home", "browse", "read", "no", false, "the user's real folders, filtered to the ones that exist", ""));

            a.Add(Cmd("fs.mkdir", "write", "write", "no", false,
                "create one folder. name must be a name, not a path", "path (parent), name, force?=false"));
            a.Add(Cmd("fs.rename", "write", "write", "no", false,
                "rename in place. name must be a name, not a path; a case-only change is allowed", "path, name, force?=false"));
            a.Add(Cmd("fs.delete", "write", "write", "no", true,
                "JOB. recycle:true (default) uses SHFileOperation with FOF_ALLOWUNDO and is refused on a volume with no Recycle Bin rather than destroying the files silently; recycle:false is a permanent delete with real progress and real cancellation that unlinks reparse points instead of following them",
                "path | paths[], recycle?=true, force?=false"));
            a.Add(Cmd("fs.copy", "write", "write", "no", true,
                "JOB. CopyFileEx with a progress callback: bytesDone/bytesTotal/current/etaSec, and cancel abandons the transfer mid-file",
                "path | paths[], to, overwrite?=false, force?=false"));
            a.Add(Cmd("fs.move", "write", "write", "no", true,
                "JOB. A same-volume move is a rename and finishes instantly; a cross-volume move is MoveFileWithProgress and only removes the originals if every file arrived",
                "path | paths[], to, overwrite?=false, force?=false"));

            a.Add(Cmd("fs.eject", "devices", "action", "maybe", false,
                "safe removal. Tries CM_Request_Device_Eject first (works unprivileged, reports the VETO and the name of whatever is holding the drive); falls back to volume lock/dismount, which needs administrator rights. Refuses a non-removable volume unless force:true",
                "drive (\"E\" or \"E:\\\\\"), force?=false"));

            a.Add(Cmd("fs.watch", "watch", "action", "no", false,
                "start a FileSystemWatcher on a folder, or drives:true to watch for volumes appearing and disappearing (a 1.5 s GetLogicalDrives poll - this module owns no window, so there is no WM_DEVICECHANGE to hook)",
                "path | drives:true, recursive?=false"));
            a.Add(Cmd("fs.unwatch", "watch", "action", "no", false, "stop one watcher, or all:true", "watchId | all:true"));
            a.Add(Cmd("fs.events", "watch", "read", "no", false,
                "drain the event ring (last 512). Pass the seq you last saw as since", "since?=0"));

            a.Add(Cmd("fs.open", "launch", "action", "no", false,
                "ShellExecuteEx with the default handler. Returns the PID so the shell can job-track the child; pid 0 with a note means the file went to an already-running program and there is no new process",
                "path, verb?, args?"));

            FJ o = FJ.Obj();
            o.Set("api", "FileApi");
            o.Set("apiVersion", ApiVersion);
            o.Set("commands", a);
            o.Set("count", a.Count);
            o.Set("elevated", FApi.IsElevated());
            o.Set("user", Environment.UserName);
            o.Set("notes",
                "commands with async=true return {\"jobId\":\"fjob-N\",\"state\":\"running\"}; poll " +
                "{\"cmd\":\"job.status\",\"jobId\":\"fjob-N\"} until state is done|error|cancelled. " +
                "Job ids are prefixed fjob- so they cannot be confused with SystemApi's job-N. " +
                "Mutations refuse C:\\Windows and C:\\Program Files* unless force:true, refuse a bare drive root outright, " +
                "and never follow a reparse point while recursing.");
            return o;
        }
    }

    #endregion
}
