// ARC OS - SystemApi
// Native Windows system-control layer for the ARC OS Settings screen.
//
// The WebView2 page posts a JSON command object; SystemApi.Handle() returns a JSON response string.
// Every response has exactly one of two shapes:
//     {"ok":true,"data":{...}}
//     {"ok":false,"error":"code","detail":"human readable"}
// Handle() NEVER throws. A crash here blanks the shell, so every path is wrapped.
//
// A request may carry "reqId":"<anything>" and it is echoed back on the response so the page can
// correlate. It is deliberately NOT called "id" - several commands take an "id" argument of their
// own (audio.setDefault, sys.setTimezone) and the two must not collide.
//
// This file is self-contained: it adds no assembly references beyond what ShellHostWeb.cs already
// uses (System.dll / System.Core.dll). Everything else is P/Invoke, hand-declared COM, or late
// binding through IDispatch. Drop it into the same csc invocation:
//     csc ... ShellHostWeb.cs SystemApi.cs
//
// Language level: C# 5 (inbox .NET Framework csc.exe).
// No string interpolation, no nameof, no expression-bodied members, no async/await.
//
// Namespace is deliberately ArcOs.Sys, not ArcOs.ShellWeb, so that the helper types here
// (Json reader, Native interop) cannot collide with the same-named types in ShellHostWeb.cs.
//
// -------------------------------------------------------------------------------------------
// THREADING CONTRACT
// -------------------------------------------------------------------------------------------
// Handle() is intended to be called from the WebView2 UI thread. Every command it dispatches
// synchronously is bounded well under ~250 ms on normal hardware. Anything that can block -
// Wi-Fi scan/connect, Bluetooth inquiry/pair, Windows Update search/download/install - is started
// as a background JOB and returns immediately:
//     {"ok":true,"data":{"jobId":"job-4","state":"running"}}
// The caller then polls:
//     {"cmd":"job.status","jobId":"job-4"}
// until state is "done" | "error" | "cancelled". See Jobs below and the catalog in ApiCatalog().
//
// -------------------------------------------------------------------------------------------
// ELEVATION
// -------------------------------------------------------------------------------------------
// The shell runs as a STANDARD USER (arcshell). Commands that need administrator rights are
// marked "elevation":"required" (will fail as arcshell) or "maybe" (build/policy dependent) in
// the catalogue. Run {"cmd":"sys.privileges"} at runtime to see what the current token actually has.
//
// Measured on the bench (DESKTOP-6BCSJ3P, Windows 11 IoT Enterprise LTSC 24H2 / 26100.9168) as
// the real arcshell account, interactive session 1, medium integrity, NOT elevated:
//   arcshell's token holds SeShutdownPrivilege and SeTimeZonePrivilege (both present, disabled by
//   default - this code enables them on demand). It does NOT hold SeSystemtimePrivilege.
//   Everything in this file works in that context EXCEPT updates.install.
//
// -------------------------------------------------------------------------------------------
// SESSION 0 IS NOT THE SHELL'S CONTEXT
// -------------------------------------------------------------------------------------------
// The display.* commands need an interactive window station. Run them from a service, a
// scheduled task with no interactive logon, or an SSH shell (all session 0) and QueryDisplayConfig
// returns ERROR_ACCESS_DENIED no matter how elevated the caller is. This is not an elevation
// problem and there is no elevation fix. Test display code in the logged-on session.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ArcOs.Sys
{
    #region JSON  (hand-rolled writer + reader, no Newtonsoft, no System.Web.Extensions)

    // A tiny immutable-ish JSON value. Building a node tree rather than concatenating strings
    // means we can never emit unbalanced braces, and the same type serves as the request reader.
    public sealed class J
    {
        public const int TNull = 0, TBool = 1, TNum = 2, TStr = 3, TArr = 4, TObj = 5;

        int _t;
        bool _b;
        double _n;
        string _s;            // string value, or the verbatim literal for numbers
        List<J> _items;       // array
        List<string> _keys;   // object
        List<J> _vals;        // object

        J(int t) { _t = t; }

        public int Kind { get { return _t; } }

        public static J Obj() { J j = new J(TObj); j._keys = new List<string>(); j._vals = new List<J>(); return j; }
        public static J Arr() { J j = new J(TArr); j._items = new List<J>(); return j; }
        public static J Nul() { return new J(TNull); }

        public static J Of(string s) { if (s == null) return Nul(); J j = new J(TStr); j._s = s; return j; }
        public static J Of(bool b) { J j = new J(TBool); j._b = b; return j; }
        public static J Of(long v) { J j = new J(TNum); j._n = v; j._s = v.ToString(CultureInfo.InvariantCulture); return j; }
        public static J Of(int v) { return Of((long)v); }
        public static J Of(uint v) { return Of((long)v); }
        public static J Of(ulong v) { J j = new J(TNum); j._n = v; j._s = v.ToString(CultureInfo.InvariantCulture); return j; }
        public static J Of(double v)
        {
            J j = new J(TNum);
            j._n = v;
            if (double.IsNaN(v) || double.IsInfinity(v)) { j._t = TNull; return j; }
            j._s = v.ToString("R", CultureInfo.InvariantCulture);
            return j;
        }
        // Rounded double - keeps payloads small and readable (volumes, scale factors, GB).
        public static J Round(double v, int digits)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return Nul();
            return Of(Math.Round(v, digits));
        }

        // ---- building ----
        public J Set(string k, J v) { _keys.Add(k); _vals.Add(v == null ? Nul() : v); return this; }
        public J Set(string k, string v) { return Set(k, Of(v)); }
        public J Set(string k, bool v) { return Set(k, Of(v)); }
        public J Set(string k, int v) { return Set(k, Of(v)); }
        public J Set(string k, uint v) { return Set(k, Of(v)); }
        public J Set(string k, long v) { return Set(k, Of(v)); }
        public J Set(string k, ulong v) { return Set(k, Of(v)); }
        public J Set(string k, double v) { return Set(k, Of(v)); }
        public J SetNull(string k) { return Set(k, Nul()); }

        // Set() always appends - Put() overwrites an existing key, which is what you want when
        // amending an object you already built (merging duplicates, replacing a note).
        public J Put(string k, J v)
        {
            for (int i = 0; i < _keys.Count; i++)
                if (string.Equals(_keys[i], k, StringComparison.Ordinal)) { _vals[i] = v == null ? Nul() : v; return this; }
            return Set(k, v);
        }
        public J Put(string k, string v) { return Put(k, Of(v)); }
        public J Put(string k, bool v) { return Put(k, Of(v)); }
        public J Put(string k, int v) { return Put(k, Of(v)); }
        public J Put(string k, uint v) { return Put(k, Of(v)); }
        public J Put(string k, long v) { return Put(k, Of(v)); }
        public J Put(string k, double v) { return Put(k, Of(v)); }

        public J Add(J v) { _items.Add(v == null ? Nul() : v); return this; }
        public J Add(string v) { return Add(Of(v)); }

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

        public J At(int i)
        {
            if (_t != TArr || i < 0 || i >= _items.Count) return null;
            return _items[i];
        }

        public string KeyAt(int i)
        {
            if (_t != TObj || i < 0 || i >= _keys.Count) return null;
            return _keys[i];
        }

        public J Get(string key)
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

        public string S(string key, string dflt) { J v = Get(key); return v == null ? dflt : v.AsStr(dflt); }
        public double N(string key, double dflt) { J v = Get(key); return v == null ? dflt : v.AsNum(dflt); }
        public int I(string key, int dflt) { J v = Get(key); return v == null ? dflt : (int)v.AsNum(dflt); }
        public bool B(string key, bool dflt) { J v = Get(key); return v == null ? dflt : v.AsBool(dflt); }

        // ---- serialising ----
        public string ToJson()
        {
            StringBuilder sb = new StringBuilder(512);
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
                        // Escape control chars and the two line separators that break JS eval.
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
        public static J Parse(string text)
        {
            int i = 0;
            if (text == null) throw new FormatException("empty input");
            J v = ParseValue(text, ref i);
            SkipWs(text, ref i);
            return v;
        }

        static void SkipWs(string t, ref int i)
        {
            while (i < t.Length && (t[i] == ' ' || t[i] == '\t' || t[i] == '\r' || t[i] == '\n')) i++;
        }

        static J ParseValue(string t, ref int i)
        {
            SkipWs(t, ref i);
            if (i >= t.Length) throw new FormatException("unexpected end of JSON");
            char c = t[i];
            if (c == '{')
            {
                i++;
                J o = Obj();
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
                J a = Arr();
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
            J num = new J(TNum);
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

    #region Background jobs

    // Anything that can take longer than a frame runs here. Handle() returns a jobId at once and
    // the page polls job.status. Worker threads are MTA so COM (Core Audio, WUA, NLM) behaves.
    public static class Jobs
    {
        public sealed class Job
        {
            public string Id;
            public string Cmd;
            public string State;      // running | done | error | cancelled
            public int Percent;       // 0..100, -1 = indeterminate
            public string Phase;      // human-readable stage
            public J Result;
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

        public static Job Start(string cmd, Func<Job, J> work)
        {
            Job job = new Job();
            lock (Gate)
            {
                _seq++;
                job.Id = "job-" + _seq.ToString(CultureInfo.InvariantCulture);
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
                    J r = work(job);
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
                        job.Detail = Api.Describe(ex);
                    }
                }
                finally
                {
                    job.FinishedUtc = DateTime.UtcNow;
                }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.MTA);
            t.Name = "arc-sysapi-" + job.Id;
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

        public static J Describe(Job j)
        {
            J o = J.Obj();
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

    #region Core helpers - uniform envelope, error mapping, token privileges

    public static class Api
    {
        public static string Ok(J data)
        {
            J o = J.Obj();
            o.Set("ok", true);
            o.Set("data", data == null ? J.Obj() : data);
            return o.ToJson();
        }

        public static string Err(string code, string detail)
        {
            J o = J.Obj();
            o.Set("ok", false);
            o.Set("error", code == null ? "error" : code);
            o.Set("detail", detail == null ? "" : detail);
            return o.ToJson();
        }

        // Win32 error -> readable text, locale-independent code plus the OS message.
        public static string Win32(uint err)
        {
            string msg;
            try { msg = new System.ComponentModel.Win32Exception((int)err).Message; }
            catch { msg = "unknown"; }
            return "0x" + err.ToString("X8", CultureInfo.InvariantCulture) + " (" + err.ToString(CultureInfo.InvariantCulture) + "): " + msg;
        }

        public static string Hr(int hr)
        {
            string msg;
            try { msg = Marshal.GetExceptionForHR(hr).Message; }
            catch { msg = "unknown"; }
            return "HRESULT 0x" + hr.ToString("X8", CultureInfo.InvariantCulture) + ": " + msg;
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
                COMException ce = cur as COMException;
                if (ce != null) sb.Append(" [hr=0x").Append(ce.ErrorCode.ToString("X8", CultureInfo.InvariantCulture)).Append("]");
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

        // Standard error used everywhere a call fails only because the token is not elevated.
        public static string NeedsAdmin(string what, string detail)
        {
            return Err("elevation_required",
                what + " requires administrator rights; the shell runs as a standard user. " + (detail == null ? "" : detail));
        }
    }

    #endregion

    #region Native interop  (P/Invoke)

    internal static class N
    {
        // ---------- privileges ----------
        [StructLayout(LayoutKind.Sequential)]
        internal struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct LUID_AND_ATTRIBUTES { public LUID Luid; public uint Attributes; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID_AND_ATTRIBUTES Privileges0; }

        internal const uint SE_PRIVILEGE_ENABLED = 0x00000002;
        internal const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        internal const uint TOKEN_QUERY = 0x0008;

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool LookupPrivilegeValue(string system, string name, out LUID luid);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AdjustTokenPrivileges(IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
            ref TOKEN_PRIVILEGES newState, uint bufLen, IntPtr prev, IntPtr retLen);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetCurrentProcess();

        // Enable a named privilege on the current process token. Returns null on success,
        // otherwise a description of why it could not be enabled.
        internal static string EnablePrivilege(string name)
        {
            IntPtr token = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out token))
                    return "OpenProcessToken failed: " + Api.Win32((uint)Marshal.GetLastWin32Error());
                LUID luid;
                if (!LookupPrivilegeValue(null, name, out luid))
                    return "LookupPrivilegeValue(" + name + ") failed: " + Api.Win32((uint)Marshal.GetLastWin32Error());
                TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
                tp.PrivilegeCount = 1;
                tp.Privileges0.Luid = luid;
                tp.Privileges0.Attributes = SE_PRIVILEGE_ENABLED;
                if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                    return "AdjustTokenPrivileges failed: " + Api.Win32((uint)Marshal.GetLastWin32Error());
                // AdjustTokenPrivileges succeeds with ERROR_NOT_ALL_ASSIGNED when the privilege
                // is simply absent from the token - that is the case we actually care about.
                int le = Marshal.GetLastWin32Error();
                if (le == 1300) return name + " is not held by this token (ERROR_NOT_ALL_ASSIGNED)";
                return null;
            }
            catch (Exception ex) { return Api.Describe(ex); }
            finally { if (token != IntPtr.Zero) CloseHandle(token); }
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returnLength);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool LookupPrivilegeName(string system, ref LUID luid, StringBuilder name, ref int cchName);

        // ---------- service status (read-only; granted to everyone) ----------
        [StructLayout(LayoutKind.Sequential)]
        internal struct SERVICE_STATUS
        {
            public uint dwServiceType;
            public uint dwCurrentState;
            public uint dwControlsAccepted;
            public uint dwWin32ExitCode;
            public uint dwServiceSpecificExitCode;
            public uint dwCheckPoint;
            public uint dwWaitHint;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr OpenSCManager(string machineName, string databaseName, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr OpenService(IntPtr scm, string serviceName, uint access);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryServiceStatus(IntPtr service, ref SERVICE_STATUS status);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseServiceHandle(IntPtr handle);

        internal const uint SC_MANAGER_CONNECT = 0x0001;
        internal const uint SERVICE_QUERY_STATUS = 0x0004;

        // ---------- memory / uptime ----------
        [StructLayout(LayoutKind.Sequential)]
        internal struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buf);

        [DllImport("kernel32.dll")]
        internal static extern ulong GetTickCount64();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetSystemDefaultLocaleName(StringBuilder name, int size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetUserDefaultLocaleName(StringBuilder name, int size);
        [DllImport("kernel32.dll")]
        internal static extern int GetUserGeoID(int geoClass);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetGeoInfo(int location, int geoType, StringBuilder data, int dataLen, ushort langId);
        // CharSet MUST be Unicode here - without it csc binds GetDriveTypeA and every drive comes
        // back as DRIVE_NO_ROOT_DIR.
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetDriveTypeW")]
        internal static extern uint GetDriveType(string rootPath);

        // The real OS version. Environment.OSVersion is subject to the app-compat shim and reports
        // 6.2.9200 for an unmanifested process; RtlGetVersion is not.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct OSVERSIONINFOEX
        {
            public uint dwOSVersionInfoSize;
            public uint dwMajorVersion;
            public uint dwMinorVersion;
            public uint dwBuildNumber;
            public uint dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szCSDVersion;
            public ushort wServicePackMajor;
            public ushort wServicePackMinor;
            public ushort wSuiteMask;
            public byte wProductType;
            public byte wReserved;
        }

        [DllImport("ntdll.dll")]
        internal static extern int RtlGetVersion(ref OSVERSIONINFOEX vi);

        // ---------- power ----------
        [StructLayout(LayoutKind.Sequential)]
        internal struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;        // 0 offline, 1 online, 255 unknown
            public byte BatteryFlag;         // bit flags; 128 = no battery
            public byte BatteryLifePercent;  // 0..100, 255 unknown
            public byte SystemStatusFlag;    // 1 = battery saver on
            public int BatteryLifeTime;      // seconds, -1 unknown
            public int BatteryFullLifeTime;  // seconds, -1 unknown
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

        [DllImport("powrprof.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetSuspendState(
            [MarshalAs(UnmanagedType.Bool)] bool hibernate,
            [MarshalAs(UnmanagedType.Bool)] bool forceCritical,
            [MarshalAs(UnmanagedType.Bool)] bool disableWakeEvent);

        [DllImport("powrprof.dll")]
        internal static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);
        [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
        internal static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid,
            IntPtr subGroupOfPowerSettingsGuid, IntPtr powerSettingGuid, IntPtr buffer, ref uint bufferSize);
        [DllImport("kernel32.dll")]
        internal static extern IntPtr LocalFree(IntPtr h);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ExitWindowsEx(uint flags, uint reason);

        internal const uint EWX_LOGOFF = 0x00000000;
        internal const uint EWX_SHUTDOWN = 0x00000001;
        internal const uint EWX_REBOOT = 0x00000002;
        internal const uint EWX_FORCEIFHUNG = 0x00000010;
        // SHTDN_REASON_MAJOR_OTHER | SHTDN_REASON_MINOR_OTHER | SHTDN_REASON_FLAG_PLANNED
        internal const uint SHTDN_REASON_PLANNED = 0x80000000;

        // ---------- time zone ----------
        [StructLayout(LayoutKind.Sequential)]
        internal struct SYSTEMTIME
        {
            public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct DYNAMIC_TIME_ZONE_INFORMATION
        {
            public int Bias;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string StandardName;
            public SYSTEMTIME StandardDate;
            public int StandardBias;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DaylightName;
            public SYSTEMTIME DaylightDate;
            public int DaylightBias;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string TimeZoneKeyName;
            [MarshalAs(UnmanagedType.U1)] public bool DynamicDaylightTimeDisabled;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint GetDynamicTimeZoneInformation(out DYNAMIC_TIME_ZONE_INFORMATION tz);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetDynamicTimeZoneInformation(ref DYNAMIC_TIME_ZONE_INFORMATION tz);

        // ---------- accounts ----------
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct USER_INFO_3
        {
            public string usri3_name;
            public string usri3_password;
            public uint usri3_password_age;
            public uint usri3_priv;
            public string usri3_home_dir;
            public string usri3_comment;
            public uint usri3_flags;
            public string usri3_script_path;
            public uint usri3_auth_flags;
            public string usri3_full_name;
            public string usri3_usr_comment;
            public string usri3_parms;
            public string usri3_workstations;
            public uint usri3_last_logon;
            public uint usri3_last_logoff;
            public uint usri3_acct_expires;
            public uint usri3_max_storage;
            public uint usri3_units_per_week;
            public IntPtr usri3_logon_hours;
            public uint usri3_bad_pw_count;
            public uint usri3_num_logons;
            public string usri3_logon_server;
            public uint usri3_country_code;
            public uint usri3_code_page;
            public uint usri3_user_id;
            public uint usri3_primary_group_id;
            public string usri3_profile;
            public string usri3_home_dir_drive;
            public uint usri3_password_expired;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct LOCALGROUP_MEMBERS_INFO_1
        {
            public IntPtr lgrmi1_sid;
            public int lgrmi1_sidusage;
            [MarshalAs(UnmanagedType.LPWStr)] public string lgrmi1_name;
        }

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint NetUserEnum(string servername, uint level, uint filter, out IntPtr bufptr,
            uint prefmaxlen, out uint entriesread, out uint totalentries, ref uint resumeHandle);
        [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint NetLocalGroupGetMembers(string servername, string localgroupname, uint level,
            out IntPtr bufptr, uint prefmaxlen, out uint entriesread, out uint totalentries, ref IntPtr resumeHandle);
        [DllImport("netapi32.dll")]
        internal static extern uint NetApiBufferFree(IntPtr buf);

        internal const uint UF_ACCOUNTDISABLE = 0x0002;
        internal const uint UF_LOCKOUT = 0x0010;
        internal const uint UF_PASSWD_NOTREQD = 0x0020;
        internal const uint UF_DONT_EXPIRE_PASSWD = 0x10000;
        internal const uint USER_PRIV_GUEST = 0, USER_PRIV_USER = 1, USER_PRIV_ADMIN = 2;
        internal const uint FILTER_NORMAL_ACCOUNT = 0x0002;

        // Resolve the well-known Administrators group to its localised name so account.list
        // does not break on a non-English install.
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateWellKnownSid(int wellKnownSidType, IntPtr domainSid, IntPtr sid, ref uint cbSid);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool LookupAccountSid(string system, IntPtr sid, StringBuilder name, ref uint cchName,
            StringBuilder domain, ref uint cchDomain, out int use);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr str);

        internal const int WinBuiltinAdministratorsSid = 26;
        internal const int WinBuiltinUsersSid = 27;

        // ---------- logged-on sessions ----------
        [StructLayout(LayoutKind.Sequential)]
        internal struct WTS_SESSION_INFO
        {
            public int SessionId;
            [MarshalAs(UnmanagedType.LPWStr)] public string pWinStationName;
            public int State;
        }

        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version,
            out IntPtr sessionInfo, out int count);
        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass,
            out IntPtr buffer, out int bytesReturned);
        [DllImport("wtsapi32.dll")]
        internal static extern void WTSFreeMemory(IntPtr memory);

        internal const int WTSUserName = 5;
        internal const int WTSDomainName = 7;
        internal const int WTSConnectStateClass_Active = 0;

        // ---------- display: GDI ----------
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public ushort dmSpecVersion;
            public ushort dmDriverVersion;
            public ushort dmSize;
            public ushort dmDriverExtra;
            public uint dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public uint dmDisplayOrientation;
            public uint dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel;
            public uint dmPelsWidth;
            public uint dmPelsHeight;
            public uint dmDisplayFlags;
            public uint dmDisplayFrequency;
            public uint dmICMMethod;
            public uint dmICMIntent;
            public uint dmMediaType;
            public uint dmDitherType;
            public uint dmReserved1;
            public uint dmReserved2;
            public uint dmPanningWidth;
            public uint dmPanningHeight;
        }

        internal const uint DM_BITSPERPEL = 0x00040000;
        internal const uint DM_PELSWIDTH = 0x00080000;
        internal const uint DM_PELSHEIGHT = 0x00100000;
        internal const uint DM_DISPLAYFREQUENCY = 0x00400000;
        internal const int ENUM_CURRENT_SETTINGS = -1;
        internal const int ENUM_REGISTRY_SETTINGS = -2;
        internal const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;
        internal const uint DISPLAY_DEVICE_PRIMARY_DEVICE = 0x00000004;
        internal const uint DISPLAY_DEVICE_MIRRORING_DRIVER = 0x00000008;

        internal const uint CDS_UPDATEREGISTRY = 0x00000001;
        internal const uint CDS_TEST = 0x00000002;
        internal const uint CDS_NORESET = 0x10000000;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplayDevices(string device, uint devNum, ref DISPLAY_DEVICE info, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplaySettingsEx(string deviceName, int modeNum, ref DEVMODE devMode, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE devMode, IntPtr hwnd,
            uint flags, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        internal delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
        [DllImport("shcore.dll")]
        internal static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);
        [DllImport("shcore.dll")]
        internal static extern int GetScaleFactorForMonitor(IntPtr hMonitor, out int scale);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        internal const int MDT_EFFECTIVE_DPI = 0;
        internal const int MDT_RAW_DPI = 1;
        internal static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        // ---------- display: DisplayConfig (QDC/SDC) ----------
        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint outputTechnology;
            public uint rotation;
            public uint scaling;
            public DISPLAYCONFIG_RATIONAL refreshRate;
            public uint scanLineOrdering;
            public int targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_2DREGION { public uint cx; public uint cy; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
        {
            public ulong pixelRate;
            public DISPLAYCONFIG_RATIONAL hSyncFreq;
            public DISPLAYCONFIG_RATIONAL vSyncFreq;
            public DISPLAYCONFIG_2DREGION activeSize;
            public DISPLAYCONFIG_2DREGION totalSize;
            public uint videoStandardAndSyncInfo;
            public uint scanLineOrdering;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINTL { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_SOURCE_MODE
        {
            public uint width; public uint height; public uint pixelFormat; public POINTL position;
        }

        // 64-byte union: targetMode (48) | sourceMode (20) | desktopImageInfo (40) all at offset 16.
        [StructLayout(LayoutKind.Explicit, Size = 64)]
        internal struct DISPLAYCONFIG_MODE_INFO
        {
            [FieldOffset(0)] public uint infoType;
            [FieldOffset(4)] public uint id;
            [FieldOffset(8)] public LUID adapterId;
            [FieldOffset(16)] public DISPLAYCONFIG_VIDEO_SIGNAL_INFO targetMode;
            [FieldOffset(16)] public DISPLAYCONFIG_SOURCE_MODE sourceMode;
        }

        internal const uint DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE = 1;
        internal const uint DISPLAYCONFIG_MODE_INFO_TYPE_TARGET = 2;

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public uint type; public uint size; public LUID adapterId; public uint id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint flags;
            public uint outputTechnology;
            public ushort edidManufactureId;
            public ushort edidProductCodeId;
            public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint value;               // bit0 advancedColorSupported, bit1 advancedColorEnabled,
                                             // bit2 wideColorEnforced, bit3 advancedColorForceDisabled
            public uint colorEncoding;
            public uint bitsPerColorChannel;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint value;               // bit0 enableAdvancedColor
        }

        internal const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
        internal const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
        internal const uint DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9;
        internal const uint DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE = 10;

        internal const uint QDC_ALL_PATHS = 0x00000001;
        internal const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;

        [DllImport("user32.dll")]
        internal static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements,
            out uint numModeInfoArrayElements);
        [DllImport("user32.dll")]
        internal static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements,
            [Out] DISPLAYCONFIG_PATH_INFO[] pathArray, ref uint numModeInfoArrayElements,
            [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);
        [DllImport("user32.dll")]
        internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME req);
        [DllImport("user32.dll")]
        internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME req);
        [DllImport("user32.dll")]
        internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO req);
        [DllImport("user32.dll")]
        internal static extern int DisplayConfigSetDeviceInfo(ref DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE req);

        // ---------- Native Wifi (wlanapi.dll) ----------
        [StructLayout(LayoutKind.Sequential)]
        internal struct DOT11_SSID
        {
            public uint uSSIDLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] ucSSID;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WLAN_INTERFACE_INFO
        {
            public Guid InterfaceGuid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strInterfaceDescription;
            public uint isState;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WLAN_AVAILABLE_NETWORK
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strProfileName;
            public DOT11_SSID dot11Ssid;
            public uint dot11BssType;
            public uint uNumberOfBssids;
            public int bNetworkConnectable;
            public uint wlanNotConnectableReason;
            public uint uNumberOfPhyTypes;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public uint[] dot11PhyTypes;
            public int bMorePhyTypes;
            public uint wlanSignalQuality;
            public int bSecurityEnabled;
            public uint dot11DefaultAuthAlgorithm;
            public uint dot11DefaultCipherAlgorithm;
            public uint dwFlags;
            public uint dwReserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WLAN_ASSOCIATION_ATTRIBUTES
        {
            public DOT11_SSID dot11Ssid;
            public uint dot11BssType;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] dot11Bssid;
            public uint dot11PhyType;
            public uint uDot11PhyIndex;
            public uint wlanSignalQuality;
            public uint ulRxRate;
            public uint ulTxRate;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WLAN_SECURITY_ATTRIBUTES
        {
            public int bSecurityEnabled;
            public int bOneXEnabled;
            public uint dot11AuthAlgorithm;
            public uint dot11CipherAlgorithm;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WLAN_CONNECTION_ATTRIBUTES
        {
            public uint isState;
            public uint wlanConnectionMode;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strProfileName;
            public WLAN_ASSOCIATION_ATTRIBUTES wlanAssociationAttributes;
            public WLAN_SECURITY_ATTRIBUTES wlanSecurityAttributes;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WLAN_CONNECTION_PARAMETERS
        {
            public uint wlanConnectionMode;
            [MarshalAs(UnmanagedType.LPWStr)] public string strProfile;
            public IntPtr pDot11Ssid;
            public IntPtr pDesiredBssidList;
            public uint dot11BssType;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WLAN_NOTIFICATION_DATA
        {
            public uint NotificationSource;
            public uint NotificationCode;
            public Guid InterfaceGuid;
            public uint dwDataSize;
            public IntPtr pData;
        }

        internal delegate void WLAN_NOTIFICATION_CALLBACK(ref WLAN_NOTIFICATION_DATA data, IntPtr context);

        internal const uint WLAN_NOTIFICATION_SOURCE_ACM = 0x00000008;
        internal const uint WLAN_NOTIFICATION_SOURCE_ALL = 0x0000FFFF;
        internal const uint wlan_notification_acm_scan_complete = 7;
        internal const uint wlan_notification_acm_scan_fail = 8;
        internal const uint wlan_notification_acm_connection_start = 9;
        internal const uint wlan_notification_acm_connection_complete = 10;
        internal const uint wlan_notification_acm_connection_attempt_fail = 11;
        internal const uint wlan_notification_acm_disconnected = 21;

        internal const uint wlan_intf_opcode_current_connection = 7;
        internal const uint wlan_connection_mode_profile = 0;
        internal const uint dot11_BSS_type_any = 3;
        internal const uint WLAN_PROFILE_USER = 0x00000002;
        internal const uint WLAN_AVAILABLE_NETWORK_CONNECTED = 0x00000001;
        internal const uint WLAN_AVAILABLE_NETWORK_HAS_PROFILE = 0x00000002;

        [DllImport("wlanapi.dll")]
        internal static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved,
            out uint negotiatedVersion, out IntPtr clientHandle);
        [DllImport("wlanapi.dll")]
        internal static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);
        [DllImport("wlanapi.dll")]
        internal static extern void WlanFreeMemory(IntPtr mem);
        [DllImport("wlanapi.dll")]
        internal static extern uint WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);
        [DllImport("wlanapi.dll")]
        internal static extern uint WlanGetAvailableNetworkList(IntPtr clientHandle, ref Guid interfaceGuid,
            uint flags, IntPtr reserved, out IntPtr availableNetworkList);
        [DllImport("wlanapi.dll")]
        internal static extern uint WlanScan(IntPtr clientHandle, ref Guid interfaceGuid, IntPtr ssid,
            IntPtr ieData, IntPtr reserved);
        [DllImport("wlanapi.dll")]
        internal static extern uint WlanQueryInterface(IntPtr clientHandle, ref Guid interfaceGuid, uint opCode,
            IntPtr reserved, out uint dataSize, out IntPtr data, IntPtr opCodeValueType);
        [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
        internal static extern uint WlanSetProfile(IntPtr clientHandle, ref Guid interfaceGuid, uint flags,
            [MarshalAs(UnmanagedType.LPWStr)] string profileXml, [MarshalAs(UnmanagedType.LPWStr)] string allUserProfileSecurity,
            [MarshalAs(UnmanagedType.Bool)] bool overwrite, IntPtr reserved, out uint reasonCode);
        [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
        internal static extern uint WlanDeleteProfile(IntPtr clientHandle, ref Guid interfaceGuid,
            [MarshalAs(UnmanagedType.LPWStr)] string profileName, IntPtr reserved);
        [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
        internal static extern uint WlanGetProfileList(IntPtr clientHandle, ref Guid interfaceGuid,
            IntPtr reserved, out IntPtr profileList);
        [DllImport("wlanapi.dll")]
        internal static extern uint WlanConnect(IntPtr clientHandle, ref Guid interfaceGuid,
            ref WLAN_CONNECTION_PARAMETERS parameters, IntPtr reserved);
        [DllImport("wlanapi.dll")]
        internal static extern uint WlanDisconnect(IntPtr clientHandle, ref Guid interfaceGuid, IntPtr reserved);
        [DllImport("wlanapi.dll")]
        internal static extern uint WlanRegisterNotification(IntPtr clientHandle, uint notifSource,
            [MarshalAs(UnmanagedType.Bool)] bool ignoreDuplicate, WLAN_NOTIFICATION_CALLBACK callback,
            IntPtr context, IntPtr reserved, out uint prevNotifSource);
        [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
        internal static extern uint WlanReasonCodeToString(uint reasonCode, uint bufferSize,
            StringBuilder buffer, IntPtr reserved);

        // ---------- Bluetooth (bthprops.cpl) ----------
        [StructLayout(LayoutKind.Sequential)]
        internal struct BLUETOOTH_FIND_RADIO_PARAMS { public uint dwSize; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct BLUETOOTH_RADIO_INFO
        {
            public uint dwSize;
            public ulong address;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
            public uint ulClassofDevice;
            public ushort lmpSubversion;
            public ushort manufacturer;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BLUETOOTH_DEVICE_SEARCH_PARAMS
        {
            public uint dwSize;
            public int fReturnAuthenticated;
            public int fReturnRemembered;
            public int fReturnUnknown;
            public int fReturnConnected;
            public int fIssueInquiry;
            public byte cTimeoutMultiplier;
            public IntPtr hRadio;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct BLUETOOTH_DEVICE_INFO
        {
            public uint dwSize;
            public ulong Address;
            public uint ulClassofDevice;
            public int fConnected;
            public int fRemembered;
            public int fAuthenticated;
            public SYSTEMTIME stLastSeen;
            public SYSTEMTIME stLastUsed;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
        }

        [DllImport("bthprops.cpl", SetLastError = true)]
        internal static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS p, out IntPtr hRadio);
        [DllImport("bthprops.cpl", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool BluetoothFindNextRadio(IntPtr hFind, out IntPtr hRadio);
        [DllImport("bthprops.cpl", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool BluetoothFindRadioClose(IntPtr hFind);
        [DllImport("bthprops.cpl", SetLastError = true)]
        internal static extern uint BluetoothGetRadioInfo(IntPtr hRadio, ref BLUETOOTH_RADIO_INFO info);
        [DllImport("bthprops.cpl", SetLastError = true)]
        internal static extern IntPtr BluetoothFindFirstDevice(ref BLUETOOTH_DEVICE_SEARCH_PARAMS p,
            ref BLUETOOTH_DEVICE_INFO info);
        [DllImport("bthprops.cpl", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool BluetoothFindNextDevice(IntPtr hFind, ref BLUETOOTH_DEVICE_INFO info);
        [DllImport("bthprops.cpl", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool BluetoothFindDeviceClose(IntPtr hFind);
        [DllImport("bthprops.cpl", SetLastError = true)]
        internal static extern uint BluetoothRemoveDevice(ref ulong address);
        [DllImport("bthprops.cpl", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool BluetoothIsConnectable(IntPtr hRadio);
        [DllImport("bthprops.cpl", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool BluetoothIsDiscoverable(IntPtr hRadio);
        [DllImport("bthprops.cpl", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint BluetoothAuthenticateDeviceEx(IntPtr hwndParent, IntPtr hRadio,
            ref BLUETOOTH_DEVICE_INFO device, IntPtr oobData, uint authMethod);
        [DllImport("bthprops.cpl", SetLastError = true)]
        internal static extern uint BluetoothRegisterForAuthenticationEx(ref BLUETOOTH_DEVICE_INFO device,
            out IntPtr handle, IntPtr callback, IntPtr param);
        [DllImport("bthprops.cpl", SetLastError = true)]
        internal static extern uint BluetoothSetServiceState(IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO device,
            ref Guid service, uint serviceFlags);

        internal const uint MITMProtectionNotRequired = 1;

        // ---------- WinRT activation via combase (no winmd reference needed) ----------
        [DllImport("combase.dll", CharSet = CharSet.Unicode)]
        internal static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string src,
            int len, out IntPtr hstring);
        [DllImport("combase.dll")]
        internal static extern int WindowsDeleteString(IntPtr hstring);
        [DllImport("combase.dll")]
        internal static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);
        [DllImport("combase.dll")]
        internal static extern int RoInitialize(int initType);
        [DllImport("ole32.dll")]
        internal static extern int PropVariantClear(IntPtr pvar);
    }

    #endregion

    #region Network / Wi-Fi

    // Adapter inventory comes from System.Net.NetworkInformation (fast, managed, no elevation).
    // Internet reachability comes from the Network List Manager COM service - the same source the
    // Windows tray icon uses - via IDispatch late binding, so there is no vtable to get wrong and
    // no network probe to block on.
    public static class NetMod
    {
        static readonly Guid CLSID_NetworkListManager = new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

        public static J Status()
        {
            J root = J.Obj();
            J adapters = J.Arr();

            bool anyWifiUp = false, anyWiredUp = false;
            string primaryType = "none";
            long bestSpeed = -1;

            NetworkInterface[] all;
            try { all = NetworkInterface.GetAllNetworkInterfaces(); }
            catch (Exception ex) { all = new NetworkInterface[0]; root.Set("adapterError", Api.Describe(ex)); }

            for (int i = 0; i < all.Length; i++)
            {
                NetworkInterface ni = all[i];
                J a = J.Obj();
                a.Set("id", ni.Id);
                a.Set("name", ni.Name);
                a.Set("description", ni.Description);

                string kind = "other";
                NetworkInterfaceType t = ni.NetworkInterfaceType;
                if (t == NetworkInterfaceType.Wireless80211) kind = "wifi";
                else if (t == NetworkInterfaceType.Ethernet || t == NetworkInterfaceType.GigabitEthernet ||
                         t == NetworkInterfaceType.FastEthernetT || t == NetworkInterfaceType.FastEthernetFx) kind = "wired";
                else if (t == NetworkInterfaceType.Loopback) kind = "loopback";
                else if (t == NetworkInterfaceType.Tunnel || t == NetworkInterfaceType.Ppp) kind = "tunnel";
                a.Set("kind", kind);
                a.Set("interfaceType", t.ToString());

                bool up = ni.OperationalStatus == OperationalStatus.Up;
                a.Set("status", ni.OperationalStatus.ToString().ToLowerInvariant());
                a.Set("up", up);
                a.Set("isVirtual", ni.Description != null &&
                    (ni.Description.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     ni.Description.IndexOf("Hyper-V", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     ni.Description.IndexOf("Loopback", StringComparison.OrdinalIgnoreCase) >= 0));

                long speed = -1;
                try { speed = ni.Speed; }
                catch { }
                a.Set("linkSpeedBps", speed);
                a.Set("linkSpeedText", FormatBitrate(speed));

                try
                {
                    PhysicalAddress mac = ni.GetPhysicalAddress();
                    byte[] mb = mac == null ? null : mac.GetAddressBytes();
                    a.Set("mac", mb == null || mb.Length == 0 ? null : BitConverter.ToString(mb).Replace('-', ':'));
                }
                catch { a.SetNull("mac"); }

                J v4 = J.Arr(), v6 = J.Arr(), gw = J.Arr(), dns = J.Arr();
                bool dhcp = false;
                try
                {
                    IPInterfaceProperties p = ni.GetIPProperties();
                    foreach (UnicastIPAddressInformation u in p.UnicastAddresses)
                    {
                        string s = u.Address.ToString();
                        if (u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            J e = J.Obj();
                            e.Set("address", s);
                            try { e.Set("prefixLength", u.PrefixLength); }
                            catch { }
                            v4.Add(e);
                        }
                        else if (u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                        {
                            v6.Add(J.Of(s));
                        }
                    }
                    foreach (GatewayIPAddressInformation g in p.GatewayAddresses) gw.Add(J.Of(g.Address.ToString()));
                    foreach (System.Net.IPAddress d in p.DnsAddresses) dns.Add(J.Of(d.ToString()));
                    try { dhcp = p.GetIPv4Properties() != null && p.GetIPv4Properties().IsDhcpEnabled; }
                    catch { }
                }
                catch { }
                a.Set("ipv4", v4);
                a.Set("ipv6", v6);
                a.Set("gateways", gw);
                a.Set("dns", dns);
                a.Set("dhcp", dhcp);

                bool routable = up && v4.Count > 0 && gw.Count > 0;
                a.Set("routable", routable);

                if (routable)
                {
                    if (kind == "wifi") anyWifiUp = true;
                    if (kind == "wired") anyWiredUp = true;
                    if (speed > bestSpeed) bestSpeed = speed;
                }

                adapters.Add(a);
            }

            root.Set("adapters", adapters);
            if (anyWiredUp) primaryType = "wired";
            else if (anyWifiUp) primaryType = "wifi";
            root.Set("connectionType", primaryType);
            root.Set("hostName", SafeHostName());

            // ---- reachability, straight from the OS, no packets sent ----
            J net = J.Obj();
            try
            {
                Type t = Type.GetTypeFromCLSID(CLSID_NetworkListManager, false);
                if (t == null) throw new InvalidOperationException("NetworkListManager CLSID not registered");
                object nlm = Activator.CreateInstance(t);
                try
                {
                    object conn = t.InvokeMember("IsConnectedToInternet", BindingFlags.GetProperty, null, nlm, null);
                    object any = t.InvokeMember("IsConnected", BindingFlags.GetProperty, null, nlm, null);
                    object bits = t.InvokeMember("GetConnectivity", BindingFlags.InvokeMethod, null, nlm, null);
                    uint c = Convert.ToUInt32(bits, CultureInfo.InvariantCulture);
                    net.Set("internet", Convert.ToBoolean(conn, CultureInfo.InvariantCulture));
                    net.Set("connected", Convert.ToBoolean(any, CultureInfo.InvariantCulture));
                    net.Set("ipv4Internet", (c & 0x40) != 0);
                    net.Set("ipv6Internet", (c & 0x400) != 0);
                    net.Set("ipv4LocalNetwork", (c & 0x20) != 0);
                    net.Set("ipv6LocalNetwork", (c & 0x200) != 0);
                    net.Set("connectivityFlags", c);
                    net.Set("source", "INetworkListManager");
                }
                finally { if (nlm != null && Marshal.IsComObject(nlm)) Marshal.ReleaseComObject(nlm); }
            }
            catch (Exception ex)
            {
                net.Set("source", "unavailable");
                net.Set("error", Api.Describe(ex));
                try { net.Set("internet", NetworkInterface.GetIsNetworkAvailable()); }
                catch { }
            }
            root.Set("reachability", net);
            return root;
        }

        static string SafeHostName()
        {
            try { return Environment.MachineName; }
            catch { return null; }
        }

        public static string FormatBitrate(long bps)
        {
            if (bps <= 0) return null;
            if (bps >= 1000000000L) return (bps / 1000000000.0).ToString("0.##", CultureInfo.InvariantCulture) + " Gbps";
            if (bps >= 1000000L) return (bps / 1000000.0).ToString("0.##", CultureInfo.InvariantCulture) + " Mbps";
            if (bps >= 1000L) return (bps / 1000.0).ToString("0.##", CultureInfo.InvariantCulture) + " Kbps";
            return bps.ToString(CultureInfo.InvariantCulture) + " bps";
        }
    }

    // ------------------------------------------------------------------------------------------
    // Wi-Fi: Native Wifi API only (wlanapi.dll). We never shell out to netsh - its output is
    // localised and would break the moment the machine is not English.
    // ------------------------------------------------------------------------------------------
    public static class WifiMod
    {
        public sealed class Session : IDisposable
        {
            public IntPtr Handle = IntPtr.Zero;
            public uint Version;

            public static Session Open(out string error)
            {
                error = null;
                Session s = new Session();
                uint neg;
                IntPtr h;
                uint rc;
                try { rc = N.WlanOpenHandle(2, IntPtr.Zero, out neg, out h); }
                catch (DllNotFoundException)
                {
                    error = "wlanapi.dll is not present - this machine has no WLAN service";
                    return null;
                }
                catch (EntryPointNotFoundException ex) { error = Api.Describe(ex); return null; }
                if (rc != 0)
                {
                    error = "WlanOpenHandle failed: " + Api.Win32(rc);
                    return null;
                }
                s.Handle = h;
                s.Version = neg;
                return s;
            }

            public void Dispose()
            {
                if (Handle != IntPtr.Zero)
                {
                    try { N.WlanCloseHandle(Handle, IntPtr.Zero); }
                    catch { }
                    Handle = IntPtr.Zero;
                }
            }
        }

        public sealed class Iface
        {
            public Guid Guid;
            public string Description;
            public uint State;
        }

        public static List<Iface> Interfaces(Session s, out string error)
        {
            error = null;
            List<Iface> list = new List<Iface>();
            IntPtr p;
            uint rc = N.WlanEnumInterfaces(s.Handle, IntPtr.Zero, out p);
            if (rc != 0) { error = "WlanEnumInterfaces failed: " + Api.Win32(rc); return list; }
            try
            {
                int count = Marshal.ReadInt32(p, 0);
                int stride = Marshal.SizeOf(typeof(N.WLAN_INTERFACE_INFO));
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = new IntPtr(p.ToInt64() + 8 + (long)i * stride);
                    N.WLAN_INTERFACE_INFO info = (N.WLAN_INTERFACE_INFO)Marshal.PtrToStructure(item, typeof(N.WLAN_INTERFACE_INFO));
                    Iface f = new Iface();
                    f.Guid = info.InterfaceGuid;
                    f.Description = info.strInterfaceDescription;
                    f.State = info.isState;
                    list.Add(f);
                }
            }
            finally { N.WlanFreeMemory(p); }
            return list;
        }

        // Pick the interface the caller asked for, else the first one.
        public static Iface Pick(List<Iface> ifs, string wanted)
        {
            if (ifs.Count == 0) return null;
            if (!string.IsNullOrEmpty(wanted))
            {
                for (int i = 0; i < ifs.Count; i++)
                {
                    if (string.Equals(ifs[i].Guid.ToString(), wanted, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(ifs[i].Description, wanted, StringComparison.OrdinalIgnoreCase))
                        return ifs[i];
                }
                return null;
            }
            return ifs[0];
        }

        public static string StateName(uint s)
        {
            switch (s)
            {
                case 0: return "not_ready";
                case 1: return "connected";
                case 2: return "ad_hoc_network_formed";
                case 3: return "disconnecting";
                case 4: return "disconnected";
                case 5: return "associating";
                case 6: return "discovering";
                case 7: return "authenticating";
                default: return "unknown(" + s + ")";
            }
        }

        public static string AuthName(uint a)
        {
            switch (a)
            {
                case 1: return "Open";
                case 2: return "Shared";
                case 3: return "WPA-Enterprise";
                case 4: return "WPA-Personal";
                case 5: return "WPA-None";
                case 6: return "WPA2-Enterprise";
                case 7: return "WPA2-Personal";
                case 8: return "WPA3-Enterprise-192";
                case 9: return "WPA3-Personal";
                case 10: return "OWE";
                case 11: return "WPA3-Enterprise";
                default: return "Unknown(" + a + ")";
            }
        }

        public static string CipherName(uint c)
        {
            switch (c)
            {
                case 0x00: return "None";
                case 0x01: return "WEP40";
                case 0x02: return "TKIP";
                case 0x04: return "CCMP(AES)";
                case 0x05: return "WEP104";
                case 0x06: return "BIP";
                case 0x08: return "GCMP";
                case 0x09: return "GCMP-256";
                case 0x100: return "UseGroup";
                case 0x101: return "WEP";
                default: return "Unknown(0x" + c.ToString("X", CultureInfo.InvariantCulture) + ")";
            }
        }

        internal static string SsidText(N.DOT11_SSID s, out string hex)
        {
            hex = null;
            if (s.ucSSID == null || s.uSSIDLength == 0) return "";
            int len = (int)Math.Min(s.uSSIDLength, 32);
            byte[] b = new byte[len];
            Array.Copy(s.ucSSID, b, len);
            hex = BitConverter.ToString(b).Replace("-", "");
            try
            {
                // Strict UTF-8 first; fall back to Latin-1 for the rare non-conforming SSID.
                UTF8Encoding strict = new UTF8Encoding(false, true);
                return strict.GetString(b);
            }
            catch
            {
                return Encoding.GetEncoding(28591).GetString(b);
            }
        }

        // WLAN signal quality is 0..100 and maps linearly onto -100..-50 dBm per the API docs.
        public static int QualityToDbm(uint q)
        {
            if (q > 100) q = 100;
            return -100 + (int)(q / 2);
        }

        public static int Bars(uint q)
        {
            if (q >= 75) return 4;
            if (q >= 50) return 3;
            if (q >= 25) return 2;
            if (q > 0) return 1;
            return 0;
        }

        // ---------- read paths ----------

        // Returns the cached available-network list. This does NOT trigger a scan, so it is fast
        // (single-digit ms). Use wifi.scan for a fresh sweep.
        public static J List(string ifaceWanted, bool merge)
        {
            string err;
            using (Session s = Session.Open(out err))
            {
                if (s == null) throw new ApiFault("wifi_unavailable", err);
                List<Iface> ifs = Interfaces(s, out err);
                if (err != null) throw new ApiFault("wifi_enum_failed", err);
                if (ifs.Count == 0) throw new ApiFault("no_wifi_adapter", "no wireless interface present on this machine");
                Iface f = Pick(ifs, ifaceWanted);
                if (f == null) throw new ApiFault("no_such_interface", "interface '" + ifaceWanted + "' not found");
                return ListOn(s, f, merge);
            }
        }

        public static J ListOn(Session s, Iface f)
        {
            return ListOn(s, f, true);
        }

        // WlanGetAvailableNetworkList reports one entry per (SSID, security, profile) combination,
        // so a network you have a profile for shows up twice. Merge by default - that is what the
        // Windows UI does and what a settings screen wants - but keep the raw view available.
        public static J ListOn(Session s, Iface f, bool merge)
        {
            J root = J.Obj();
            root.Set("interface", IfaceJson(f));

            HashSet<string> saved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> profileNames = ProfileNames(s, f);
            for (int i = 0; i < profileNames.Count; i++) saved.Add(profileNames[i]);

            IntPtr p;
            Guid g = f.Guid;
            uint rc = N.WlanGetAvailableNetworkList(s.Handle, ref g, 0, IntPtr.Zero, out p);
            if (rc != 0) throw new ApiFault("wifi_list_failed", "WlanGetAvailableNetworkList failed: " + Api.Win32(rc));

            J nets = J.Arr();
            Dictionary<string, J> mergedBy = new Dictionary<string, J>(StringComparer.Ordinal);
            try
            {
                int count = Marshal.ReadInt32(p, 0);
                int stride = Marshal.SizeOf(typeof(N.WLAN_AVAILABLE_NETWORK));
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = new IntPtr(p.ToInt64() + 8 + (long)i * stride);
                    N.WLAN_AVAILABLE_NETWORK n =
                        (N.WLAN_AVAILABLE_NETWORK)Marshal.PtrToStructure(item, typeof(N.WLAN_AVAILABLE_NETWORK));

                    string hex;
                    string ssid = SsidText(n.dot11Ssid, out hex);

                    if (merge)
                    {
                        string key = (hex == null ? "" : hex) + "/" + n.dot11DefaultAuthAlgorithm;
                        J prev;
                        if (mergedBy.TryGetValue(key, out prev))
                        {
                            // Keep the strongest signal and the union of the useful flags.
                            if (n.wlanSignalQuality > (uint)prev.N("signal", 0))
                            {
                                prev.Put("signal", n.wlanSignalQuality);
                                prev.Put("rssiDbm", QualityToDbm(n.wlanSignalQuality));
                                prev.Put("bars", Bars(n.wlanSignalQuality));
                            }
                            if ((n.dwFlags & N.WLAN_AVAILABLE_NETWORK_CONNECTED) != 0) prev.Put("connected", true);
                            if ((n.dwFlags & N.WLAN_AVAILABLE_NETWORK_HAS_PROFILE) != 0)
                            {
                                prev.Put("savedProfile", true);
                                if (!string.IsNullOrEmpty(n.strProfileName)) prev.Put("profileName", n.strProfileName);
                            }
                            if (n.bNetworkConnectable != 0) prev.Put("connectable", true);
                            prev.Put("bssCount", (uint)prev.N("bssCount", 0) + n.uNumberOfBssids);
                            continue;
                        }
                    }

                    J e = J.Obj();
                    e.Set("ssid", ssid);
                    e.Set("ssidHex", hex);
                    e.Set("hidden", string.IsNullOrEmpty(ssid));
                    e.Set("signal", n.wlanSignalQuality);
                    e.Set("rssiDbm", QualityToDbm(n.wlanSignalQuality));
                    e.Set("bars", Bars(n.wlanSignalQuality));
                    e.Set("secure", n.bSecurityEnabled != 0);
                    e.Set("security", n.bSecurityEnabled != 0 ? AuthName(n.dot11DefaultAuthAlgorithm) : "Open");
                    e.Set("authAlgorithm", n.dot11DefaultAuthAlgorithm);
                    e.Set("cipher", CipherName(n.dot11DefaultCipherAlgorithm));
                    e.Set("connectable", n.bNetworkConnectable != 0);
                    if (n.bNetworkConnectable == 0) e.Set("notConnectableReason", ReasonText(n.wlanNotConnectableReason));
                    e.Set("connected", (n.dwFlags & N.WLAN_AVAILABLE_NETWORK_CONNECTED) != 0);
                    bool hasProfile = (n.dwFlags & N.WLAN_AVAILABLE_NETWORK_HAS_PROFILE) != 0;
                    e.Set("savedProfile", hasProfile || (!string.IsNullOrEmpty(n.strProfileName) && saved.Contains(n.strProfileName)));
                    e.Set("profileName", string.IsNullOrEmpty(n.strProfileName) ? null : n.strProfileName);
                    e.Set("bssCount", n.uNumberOfBssids);
                    e.Set("band", PhyBand(n.dot11PhyTypes, n.uNumberOfPhyTypes));
                    nets.Add(e);
                    if (merge) mergedBy[(hex == null ? "" : hex) + "/" + n.dot11DefaultAuthAlgorithm] = e;
                }
            }
            finally { N.WlanFreeMemory(p); }

            root.Set("networks", nets);
            root.Set("count", nets.Count);
            root.Set("savedProfiles", ToArr(profileNames));
            root.Set("merged", merge);
            root.Set("source", "cache");
            root.Set("note", "cached list; call wifi.scan for a fresh sweep");
            return root;
        }

        static J ToArr(List<string> items)
        {
            J a = J.Arr();
            for (int i = 0; i < items.Count; i++) a.Add(items[i]);
            return a;
        }

        static string PhyBand(uint[] phys, uint n)
        {
            if (phys == null) return null;
            bool g2 = false, g5 = false, g6 = false;
            for (int i = 0; i < phys.Length && i < n; i++)
            {
                switch (phys[i])
                {
                    case 4: g2 = true; break;   // HRDSSS  (802.11b, 2.4)
                    case 6: g2 = true; break;   // ERP     (802.11g, 2.4)
                    case 5: g5 = true; break;   // OFDM    (802.11a, 5)
                    case 7: g2 = true; g5 = true; break; // HT   (n)
                    case 8: g5 = true; break;   // VHT     (ac)
                    case 9: g6 = true; break;   // DMG
                    case 10: g2 = true; g5 = true; g6 = true; break; // HE (ax, incl 6 GHz)
                    case 11: g2 = true; g5 = true; g6 = true; break; // EHT (be)
                }
            }
            List<string> parts = new List<string>();
            if (g2) parts.Add("2.4GHz");
            if (g5) parts.Add("5GHz");
            if (g6) parts.Add("6GHz");
            return parts.Count == 0 ? null : string.Join("/", parts.ToArray());
        }

        public static List<string> ProfileNames(Session s, Iface f)
        {
            List<string> names = new List<string>();
            IntPtr p;
            Guid g = f.Guid;
            uint rc = N.WlanGetProfileList(s.Handle, ref g, IntPtr.Zero, out p);
            if (rc != 0) return names;
            try
            {
                int count = Marshal.ReadInt32(p, 0);
                // WLAN_PROFILE_INFO = WCHAR strProfileName[256] + DWORD dwFlags = 516 bytes
                const int stride = 516;
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = new IntPtr(p.ToInt64() + 8 + (long)i * stride);
                    string name = Marshal.PtrToStringUni(item, 256);
                    int z = name.IndexOf('\0');
                    if (z >= 0) name = name.Substring(0, z);
                    names.Add(name);
                }
            }
            finally { N.WlanFreeMemory(p); }
            return names;
        }

        public static J IfaceJson(Iface f)
        {
            J o = J.Obj();
            o.Set("guid", f.Guid.ToString());
            o.Set("description", f.Description);
            o.Set("state", StateName(f.State));
            return o;
        }

        public static J CurrentConnection(Session s, Iface f)
        {
            uint size;
            IntPtr data;
            Guid g = f.Guid;
            uint rc = N.WlanQueryInterface(s.Handle, ref g, N.wlan_intf_opcode_current_connection,
                IntPtr.Zero, out size, out data, IntPtr.Zero);
            if (rc != 0)
            {
                J off = J.Obj();
                off.Set("connected", false);
                off.Set("state", StateName(f.State));
                // ERROR_INVALID_STATE (5023) simply means "not connected".
                if (rc != 5023) off.Set("queryError", Api.Win32(rc));
                return off;
            }
            try
            {
                N.WLAN_CONNECTION_ATTRIBUTES c =
                    (N.WLAN_CONNECTION_ATTRIBUTES)Marshal.PtrToStructure(data, typeof(N.WLAN_CONNECTION_ATTRIBUTES));
                string hex;
                J o = J.Obj();
                o.Set("connected", c.isState == 1);
                o.Set("state", StateName(c.isState));
                o.Set("profileName", c.strProfileName);
                o.Set("ssid", SsidText(c.wlanAssociationAttributes.dot11Ssid, out hex));
                byte[] b = c.wlanAssociationAttributes.dot11Bssid;
                o.Set("bssid", b == null ? null : BitConverter.ToString(b).Replace('-', ':'));
                o.Set("signal", c.wlanAssociationAttributes.wlanSignalQuality);
                o.Set("rssiDbm", QualityToDbm(c.wlanAssociationAttributes.wlanSignalQuality));
                o.Set("bars", Bars(c.wlanAssociationAttributes.wlanSignalQuality));
                o.Set("rxRateKbps", c.wlanAssociationAttributes.ulRxRate);
                o.Set("txRateKbps", c.wlanAssociationAttributes.ulTxRate);
                o.Set("secure", c.wlanSecurityAttributes.bSecurityEnabled != 0);
                o.Set("security", AuthName(c.wlanSecurityAttributes.dot11AuthAlgorithm));
                o.Set("cipher", CipherName(c.wlanSecurityAttributes.dot11CipherAlgorithm));
                return o;
            }
            finally { N.WlanFreeMemory(data); }
        }

        public static J Status(string ifaceWanted)
        {
            string err;
            using (Session s = Session.Open(out err))
            {
                if (s == null) throw new ApiFault("wifi_unavailable", err);
                List<Iface> ifs = Interfaces(s, out err);
                if (err != null) throw new ApiFault("wifi_enum_failed", err);
                J root = J.Obj();
                J arr = J.Arr();
                for (int i = 0; i < ifs.Count; i++)
                {
                    J e = IfaceJson(ifs[i]);
                    e.Set("connection", CurrentConnection(s, ifs[i]));
                    e.Set("savedProfiles", ToArr(ProfileNames(s, ifs[i])));
                    arr.Add(e);
                }
                root.Set("interfaces", arr);
                root.Set("present", ifs.Count > 0);
                return root;
            }
        }

        public static string ReasonText(uint reason)
        {
            if (reason == 0) return null;
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                uint rc = N.WlanReasonCodeToString(reason, (uint)sb.Capacity, sb, IntPtr.Zero);
                string s = rc == 0 ? sb.ToString().Trim() : null;
                if (string.IsNullOrEmpty(s)) return "reason " + reason;
                return s + " (reason " + reason + ")";
            }
            catch { return "reason " + reason; }
        }

        // ---------- scan (async job: ~4-10 s) ----------
        public static J Scan(Jobs.Job job, string ifaceWanted, int timeoutMs, bool merge)
        {
            string err;
            using (Session s = Session.Open(out err))
            {
                if (s == null) throw new ApiFault("wifi_unavailable", err);
                List<Iface> ifs = Interfaces(s, out err);
                if (err != null) throw new ApiFault("wifi_enum_failed", err);
                if (ifs.Count == 0) throw new ApiFault("no_wifi_adapter", "no wireless interface present on this machine");
                Iface f = Pick(ifs, ifaceWanted);
                if (f == null) throw new ApiFault("no_such_interface", "interface '" + ifaceWanted + "' not found");

                job.Report(10, "registering for scan notifications");
                ManualResetEvent done = new ManualResetEvent(false);
                bool failed = false;
                Guid ifGuid = f.Guid;

                N.WLAN_NOTIFICATION_CALLBACK cb = delegate (ref N.WLAN_NOTIFICATION_DATA d, IntPtr ctx)
                {
                    if (d.NotificationSource != N.WLAN_NOTIFICATION_SOURCE_ACM) return;
                    if (d.InterfaceGuid != ifGuid) return;
                    if (d.NotificationCode == N.wlan_notification_acm_scan_complete) done.Set();
                    else if (d.NotificationCode == N.wlan_notification_acm_scan_fail) { failed = true; done.Set(); }
                };

                uint prev;
                uint rr = N.WlanRegisterNotification(s.Handle, N.WLAN_NOTIFICATION_SOURCE_ACM, true, cb,
                    IntPtr.Zero, IntPtr.Zero, out prev);
                bool notifyOk = rr == 0;

                job.Report(20, "scanning");
                Guid g = f.Guid;
                uint rc = N.WlanScan(s.Handle, ref g, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (rc != 0)
                {
                    GC.KeepAlive(cb);
                    // ERROR_ACCESS_DENIED here is the standard-user signal worth surfacing plainly.
                    if (rc == 5) throw new ApiFault("access_denied",
                        "WlanScan denied by the WLAN service ACL for this user: " + Api.Win32(rc));
                    throw new ApiFault("wifi_scan_failed", "WlanScan failed: " + Api.Win32(rc));
                }

                if (notifyOk)
                {
                    done.WaitOne(timeoutMs);
                }
                else
                {
                    // Notification registration unavailable - fall back to a fixed settle window.
                    // The driver is required to finish a scan within ~4 s.
                    for (int waited = 0; waited < timeoutMs && !job.CancelRequested; waited += 250) Thread.Sleep(250);
                }
                GC.KeepAlive(cb);

                job.Report(80, "reading results");
                J res = ListOn(s, f, merge);
                res.Set("scanCompleted", notifyOk ? done.WaitOne(0) : true);
                res.Set("scanFailed", failed);
                res.Set("notificationsUsed", notifyOk);
                res.Put("source", "scan");
                res.Put("note", "fresh scan results");
                return res;
            }
        }

        // ---------- connect (async job: up to ~30 s) ----------
        public static J Connect(Jobs.Job job, string ssid, string password, string security, bool hidden,
                                string ifaceWanted, int timeoutMs)
        {
            if (string.IsNullOrEmpty(ssid)) throw new ApiFault("bad_request", "ssid is required");

            string err;
            using (Session s = Session.Open(out err))
            {
                if (s == null) throw new ApiFault("wifi_unavailable", err);
                List<Iface> ifs = Interfaces(s, out err);
                if (err != null) throw new ApiFault("wifi_enum_failed", err);
                if (ifs.Count == 0) throw new ApiFault("no_wifi_adapter", "no wireless interface present on this machine");
                Iface f = Pick(ifs, ifaceWanted);
                if (f == null) throw new ApiFault("no_such_interface", "interface '" + ifaceWanted + "' not found");
                Guid g = f.Guid;

                // Work out which authentication the network actually advertises unless told.
                List<string> attempts = new List<string>();
                if (!string.IsNullOrEmpty(security) && !string.Equals(security, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    attempts.Add(security.ToUpperInvariant());
                }
                else
                {
                    uint auth = LookupAuth(s, f, ssid);
                    if (auth == 9) { attempts.Add("WPA3SAE"); attempts.Add("WPA2PSK"); }
                    else if (auth == 1) { attempts.Add("OPEN"); }
                    else if (auth == 10) { attempts.Add("OWE"); attempts.Add("OPEN"); }
                    else if (auth == 7 || auth == 0) { attempts.Add("WPA2PSK"); attempts.Add("WPA3SAE"); }
                    else if (auth == 4) { attempts.Add("WPAPSK"); attempts.Add("WPA2PSK"); }
                    else { attempts.Add("WPA2PSK"); attempts.Add("WPA3SAE"); }
                    if (string.IsNullOrEmpty(password) && !attempts.Contains("OPEN")) attempts.Add("OPEN");
                }

                J tried = J.Arr();
                string lastFailure = null;

                for (int i = 0; i < attempts.Count; i++)
                {
                    if (job.CancelRequested) throw new ApiFault("cancelled", "cancelled before attempt " + (i + 1));
                    string mode = attempts[i];
                    job.Report(10 + i * 20, "profile " + mode);

                    string xml = ProfileXml(ssid, password, mode, hidden);
                    uint reason;
                    string scope = "all-user";
                    uint rc = N.WlanSetProfile(s.Handle, ref g, 0, xml, null, true, IntPtr.Zero, out reason);
                    if (rc == 5 /* ERROR_ACCESS_DENIED */)
                    {
                        // Fall back to a per-user profile, which does not need the all-user right.
                        scope = "per-user";
                        rc = N.WlanSetProfile(s.Handle, ref g, N.WLAN_PROFILE_USER, xml, null, true, IntPtr.Zero, out reason);
                    }

                    J att = J.Obj();
                    att.Set("mode", mode);
                    att.Set("profileScope", scope);

                    if (rc != 0)
                    {
                        att.Set("stage", "WlanSetProfile");
                        att.Set("win32", Api.Win32(rc));
                        att.Set("reason", ReasonText(reason));
                        lastFailure = "WlanSetProfile(" + mode + ") failed: " + Api.Win32(rc) +
                                      (reason != 0 ? " / " + ReasonText(reason) : "");
                        tried.Add(att);
                        continue;
                    }

                    job.Report(30 + i * 20, "connecting to " + ssid);
                    ManualResetEvent done = new ManualResetEvent(false);
                    bool ok = false;
                    uint failReason = 0;
                    Guid ifGuid = f.Guid;

                    N.WLAN_NOTIFICATION_CALLBACK cb = delegate (ref N.WLAN_NOTIFICATION_DATA d, IntPtr ctx)
                    {
                        if (d.NotificationSource != N.WLAN_NOTIFICATION_SOURCE_ACM) return;
                        if (d.InterfaceGuid != ifGuid) return;
                        if (d.NotificationCode == N.wlan_notification_acm_connection_complete)
                        {
                            // WLAN_CONNECTION_NOTIFICATION_DATA: mode(4) profileName[256](512)
                            // ssid(36) bssType(4) securityEnabled(4) reasonCode(4) ...
                            uint r = 0;
                            try { if (d.pData != IntPtr.Zero && d.dwDataSize >= 564) r = (uint)Marshal.ReadInt32(d.pData, 560); }
                            catch { }
                            failReason = r;
                            ok = r == 0;
                            done.Set();
                        }
                        else if (d.NotificationCode == N.wlan_notification_acm_connection_attempt_fail)
                        {
                            uint r = 0;
                            try { if (d.pData != IntPtr.Zero && d.dwDataSize >= 564) r = (uint)Marshal.ReadInt32(d.pData, 560); }
                            catch { }
                            failReason = r;
                            ok = false;
                            done.Set();
                        }
                    };

                    uint prev;
                    N.WlanRegisterNotification(s.Handle, N.WLAN_NOTIFICATION_SOURCE_ACM, true, cb, IntPtr.Zero, IntPtr.Zero, out prev);

                    N.WLAN_CONNECTION_PARAMETERS cp = new N.WLAN_CONNECTION_PARAMETERS();
                    cp.wlanConnectionMode = N.wlan_connection_mode_profile;
                    cp.strProfile = ssid;                 // profile name == SSID in the XML we emit
                    cp.pDot11Ssid = IntPtr.Zero;
                    cp.pDesiredBssidList = IntPtr.Zero;
                    cp.dot11BssType = N.dot11_BSS_type_any;
                    cp.dwFlags = 0;

                    uint crc = N.WlanConnect(s.Handle, ref g, ref cp, IntPtr.Zero);
                    if (crc != 0)
                    {
                        GC.KeepAlive(cb);
                        att.Set("stage", "WlanConnect");
                        att.Set("win32", Api.Win32(crc));
                        lastFailure = "WlanConnect(" + mode + ") failed: " + Api.Win32(crc);
                        tried.Add(att);
                        continue;
                    }

                    done.WaitOne(timeoutMs);
                    GC.KeepAlive(cb);

                    // Confirm against the interface itself rather than trusting the notification alone.
                    List<Iface> now = Interfaces(s, out err);
                    Iface f2 = Pick(now, f.Guid.ToString());
                    J conn = f2 != null ? CurrentConnection(s, f2) : null;
                    bool reallyConnected = conn != null && conn.B("connected", false) &&
                                           string.Equals(conn.S("ssid", ""), ssid, StringComparison.Ordinal);

                    att.Set("stage", "complete");
                    att.Set("notifiedOk", ok);
                    att.Set("reason", ReasonText(failReason));
                    att.Set("connected", reallyConnected);
                    tried.Add(att);

                    if (reallyConnected)
                    {
                        J res = J.Obj();
                        res.Set("connected", true);
                        res.Set("ssid", ssid);
                        res.Set("mode", mode);
                        res.Set("profileScope", scope);
                        res.Set("connection", conn);
                        res.Set("attempts", tried);
                        return res;
                    }

                    lastFailure = "association did not complete for " + mode +
                                  (failReason != 0 ? ": " + ReasonText(failReason) : " (timed out after " + timeoutMs + " ms)");

                    // Do not leave a dud profile lying around for a mode that did not work.
                    if (i < attempts.Count - 1)
                    {
                        try { N.WlanDeleteProfile(s.Handle, ref g, ssid, IntPtr.Zero); }
                        catch { }
                    }
                }

                throw new ApiFault("wifi_connect_failed", lastFailure == null ? "no attempt succeeded" : lastFailure,
                    J.Obj().Set("attempts", tried));
            }
        }

        static uint LookupAuth(Session s, Iface f, string ssid)
        {
            try
            {
                J list = ListOn(s, f);
                J nets = list.Get("networks");
                for (int i = 0; i < nets.Count; i++)
                {
                    J n = nets.At(i);
                    if (string.Equals(n.S("ssid", ""), ssid, StringComparison.Ordinal))
                        return (uint)n.N("authAlgorithm", 0);
                }
            }
            catch { }
            return 0;
        }

        // WLAN profile XML. Kept minimal and schema-valid for v1 (Win7+) with the WPA3 values
        // that Windows 10 1903+ accepts.
        public static string ProfileXml(string ssid, string password, string mode, bool hidden)
        {
            string auth, enc;
            bool hasKey = true;
            switch (mode)
            {
                case "OPEN": auth = "open"; enc = "none"; hasKey = false; break;
                case "OWE": auth = "OWE"; enc = "AES"; hasKey = false; break;
                case "WEP": auth = "open"; enc = "WEP"; break;
                case "WPAPSK": auth = "WPAPSK"; enc = "TKIP"; break;
                case "WPA3SAE": auth = "WPA3SAE"; enc = "AES"; break;
                case "WPA3": auth = "WPA3SAE"; enc = "AES"; break;
                default: auth = "WPA2PSK"; enc = "AES"; break;
            }
            if (string.IsNullOrEmpty(password)) hasKey = false;

            StringBuilder sb = new StringBuilder(1024);
            sb.Append("<?xml version=\"1.0\"?>");
            sb.Append("<WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\">");
            sb.Append("<name>").Append(Xml(ssid)).Append("</name>");
            sb.Append("<SSIDConfig><SSID><name>").Append(Xml(ssid)).Append("</name></SSID>");
            sb.Append("<nonBroadcast>").Append(hidden ? "true" : "false").Append("</nonBroadcast></SSIDConfig>");
            sb.Append("<connectionType>ESS</connectionType>");
            sb.Append("<connectionMode>auto</connectionMode>");
            sb.Append("<MSM><security><authEncryption>");
            sb.Append("<authentication>").Append(auth).Append("</authentication>");
            sb.Append("<encryption>").Append(enc).Append("</encryption>");
            sb.Append("<useOneX>false</useOneX>");
            if (mode == "WPA3SAE" || mode == "WPA3")
                sb.Append("<transitionMode xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v4\">true</transitionMode>");
            sb.Append("</authEncryption>");
            if (hasKey)
            {
                sb.Append("<sharedKey>");
                sb.Append("<keyType>").Append(mode == "WEP" ? "networkKey" : "passPhrase").Append("</keyType>");
                sb.Append("<protected>false</protected>");
                sb.Append("<keyMaterial>").Append(Xml(password)).Append("</keyMaterial>");
                sb.Append("</sharedKey>");
            }
            sb.Append("</security></MSM></WLANProfile>");
            return sb.ToString();
        }

        static string Xml(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length + 16);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '&') sb.Append("&amp;");
                else if (c == '<') sb.Append("&lt;");
                else if (c == '>') sb.Append("&gt;");
                else if (c == '"') sb.Append("&quot;");
                else if (c == '\'') sb.Append("&apos;");
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public static J Disconnect(string ifaceWanted)
        {
            string err;
            using (Session s = Session.Open(out err))
            {
                if (s == null) throw new ApiFault("wifi_unavailable", err);
                List<Iface> ifs = Interfaces(s, out err);
                if (ifs.Count == 0) throw new ApiFault("no_wifi_adapter", "no wireless interface present on this machine");
                Iface f = Pick(ifs, ifaceWanted);
                if (f == null) throw new ApiFault("no_such_interface", "interface '" + ifaceWanted + "' not found");
                Guid g = f.Guid;
                uint rc = N.WlanDisconnect(s.Handle, ref g, IntPtr.Zero);
                if (rc == 5) throw new ApiFault("access_denied", "WlanDisconnect denied for this user: " + Api.Win32(rc));
                if (rc != 0) throw new ApiFault("wifi_disconnect_failed", "WlanDisconnect failed: " + Api.Win32(rc));
                J o = J.Obj();
                o.Set("disconnected", true);
                o.Set("interface", IfaceJson(f));
                return o;
            }
        }

        public static J Forget(string profile, string ifaceWanted)
        {
            if (string.IsNullOrEmpty(profile)) throw new ApiFault("bad_request", "ssid (profile name) is required");
            string err;
            using (Session s = Session.Open(out err))
            {
                if (s == null) throw new ApiFault("wifi_unavailable", err);
                List<Iface> ifs = Interfaces(s, out err);
                if (ifs.Count == 0) throw new ApiFault("no_wifi_adapter", "no wireless interface present on this machine");
                Iface f = Pick(ifs, ifaceWanted);
                if (f == null) throw new ApiFault("no_such_interface", "interface '" + ifaceWanted + "' not found");
                Guid g = f.Guid;
                uint rc = N.WlanDeleteProfile(s.Handle, ref g, profile, IntPtr.Zero);
                if (rc == 1168) throw new ApiFault("not_found", "no saved profile named '" + profile + "'");
                if (rc == 5) throw new ApiFault("access_denied",
                    "WlanDeleteProfile denied - all-user profiles can only be removed by an administrator: " + Api.Win32(rc));
                if (rc != 0) throw new ApiFault("wifi_forget_failed", "WlanDeleteProfile failed: " + Api.Win32(rc));
                J o = J.Obj();
                o.Set("forgotten", profile);
                return o;
            }
        }

        // Diagnostic write-path check that touches nothing real: create a profile for an SSID that
        // cannot exist, confirm it appears, then delete it. Used to prove that profile management
        // works for the current user without disturbing the live connection.
        public static J ProfileSelfTest()
        {
            string ssid = "ARC-OS-SELFTEST-" + DateTime.UtcNow.ToString("HHmmss", CultureInfo.InvariantCulture);
            string err;
            using (Session s = Session.Open(out err))
            {
                if (s == null) throw new ApiFault("wifi_unavailable", err);
                List<Iface> ifs = Interfaces(s, out err);
                if (ifs.Count == 0) throw new ApiFault("no_wifi_adapter", "no wireless interface present on this machine");
                Iface f = ifs[0];
                Guid g = f.Guid;
                J o = J.Obj();
                o.Set("ssid", ssid);

                uint reason;
                string scope = "all-user";
                uint rc = N.WlanSetProfile(s.Handle, ref g, 0, ProfileXml(ssid, "selftest-passphrase", "WPA2PSK", false),
                    null, true, IntPtr.Zero, out reason);
                if (rc == 5)
                {
                    scope = "per-user";
                    rc = N.WlanSetProfile(s.Handle, ref g, N.WLAN_PROFILE_USER,
                        ProfileXml(ssid, "selftest-passphrase", "WPA2PSK", false), null, true, IntPtr.Zero, out reason);
                }
                o.Set("setProfileScope", scope);
                o.Set("setProfileOk", rc == 0);
                if (rc != 0)
                {
                    o.Set("setProfileError", Api.Win32(rc));
                    o.Set("setProfileReason", ReasonText(reason));
                    return o;
                }

                List<string> after = ProfileNames(s, f);
                o.Set("visibleAfterCreate", after.Contains(ssid));

                uint drc = N.WlanDeleteProfile(s.Handle, ref g, ssid, IntPtr.Zero);
                o.Set("deleteOk", drc == 0);
                if (drc != 0) o.Set("deleteError", Api.Win32(drc));
                return o;
            }
        }
    }

    #endregion

    #region Typed failure

    // Thrown inside command implementations; Handle() turns it into the standard error envelope.
    // Nothing else escapes - anything unexpected becomes error:"internal".
    public sealed class ApiFault : Exception
    {
        public readonly string Code;
        public readonly J Extra;
        public ApiFault(string code, string detail) : base(detail == null ? code : detail) { Code = code; }
        public ApiFault(string code, string detail, J extra) : base(detail == null ? code : detail) { Code = code; Extra = extra; }
    }

    #endregion

    #region Display

    // Reads come from the DisplayConfig (QDC) API, which is the only one that reports true refresh
    // rate, monitor friendly names and HDR state. Mode changes go through ChangeDisplaySettingsEx,
    // which is per-user and needs no elevation (CDS_GLOBAL, which would, is never used).
    public static class DisplayMod
    {
        sealed class MonInfo
        {
            public IntPtr H;
            public string Device;
            public N.RECT Rect;
            public bool Primary;
            public uint DpiX, DpiY;
            public int ScalePercent;
        }

        static List<MonInfo> Monitors()
        {
            List<MonInfo> list = new List<MonInfo>();
            N.MonitorEnumProc cb = delegate (IntPtr h, IntPtr hdc, ref N.RECT r, IntPtr data)
            {
                MonInfo m = new MonInfo();
                m.H = h;
                m.Rect = r;
                N.MONITORINFOEX mi = new N.MONITORINFOEX();
                mi.cbSize = Marshal.SizeOf(typeof(N.MONITORINFOEX));
                if (N.GetMonitorInfo(h, ref mi))
                {
                    m.Device = mi.szDevice;
                    m.Primary = (mi.dwFlags & 1) != 0;
                }
                uint dx, dy;
                try { if (N.GetDpiForMonitor(h, N.MDT_EFFECTIVE_DPI, out dx, out dy) == 0) { m.DpiX = dx; m.DpiY = dy; } }
                catch { }
                int sc;
                try { if (N.GetScaleFactorForMonitor(h, out sc) == 0) m.ScalePercent = sc; }
                catch { }
                list.Add(m);
                return true;
            };
            try { N.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero); }
            catch { }
            GC.KeepAlive(cb);
            return list;
        }

        static string OutputTechName(uint t)
        {
            switch (t)
            {
                case 0: return "VGA(HD15)";
                case 1: return "S-Video";
                case 2: return "Composite";
                case 3: return "Component";
                case 4: return "DVI";
                case 5: return "HDMI";
                case 6: return "LVDS";
                case 8: return "D-Jpn";
                case 9: return "SDI";
                case 10: return "DisplayPort-External";
                case 11: return "DisplayPort-Embedded";
                case 12: return "UDI-External";
                case 13: return "UDI-Embedded";
                case 14: return "SDTV-Dongle";
                case 15: return "Miracast";
                case 16: return "IndirectWired";
                case 0x80000000: return "Internal";
                default: return "Other(" + t + ")";
            }
        }

        static string ColorEncodingName(uint e)
        {
            switch (e)
            {
                case 0: return "RGB";
                case 1: return "YCbCr444";
                case 2: return "YCbCr422";
                case 3: return "YCbCr420";
                case 4: return "Intensity";
                default: return "Unknown(" + e + ")";
            }
        }

        // Pull the active DisplayConfig topology. Returns null (with reason) if QDC is unavailable.
        static bool QueryConfig(out N.DISPLAYCONFIG_PATH_INFO[] paths, out N.DISPLAYCONFIG_MODE_INFO[] modes, out string error)
        {
            paths = null; modes = null; error = null;
            uint pc, mc;
            int rc = N.GetDisplayConfigBufferSizes(N.QDC_ONLY_ACTIVE_PATHS, out pc, out mc);
            if (rc != 0) { error = "GetDisplayConfigBufferSizes: " + Api.Win32((uint)rc) + NoDesktopHint((uint)rc); return false; }
            paths = new N.DISPLAYCONFIG_PATH_INFO[pc];
            modes = new N.DISPLAYCONFIG_MODE_INFO[mc];
            rc = N.QueryDisplayConfig(N.QDC_ONLY_ACTIVE_PATHS, ref pc, paths, ref mc, modes, IntPtr.Zero);
            if (rc != 0) { error = "QueryDisplayConfig: " + Api.Win32((uint)rc) + NoDesktopHint((uint)rc); return false; }
            Array.Resize(ref paths, (int)pc);
            Array.Resize(ref modes, (int)mc);
            return true;
        }

        // ACCESS_DENIED from the DisplayConfig APIs almost always means "no desktop", not "not
        // admin": a session-0 service or an SSH shell has no window station to query. Elevation
        // does not help; running in the interactive session does.
        static string NoDesktopHint(uint rc)
        {
            if (rc != 5) return "";
            return " - the DisplayConfig APIs need an interactive window station; a session-0 or " +
                   "SSH-hosted process cannot query them regardless of elevation";
        }

        static string SourceGdiName(N.LUID adapter, uint sourceId)
        {
            N.DISPLAYCONFIG_SOURCE_DEVICE_NAME req = new N.DISPLAYCONFIG_SOURCE_DEVICE_NAME();
            req.header.type = N.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
            req.header.size = (uint)Marshal.SizeOf(typeof(N.DISPLAYCONFIG_SOURCE_DEVICE_NAME));
            req.header.adapterId = adapter;
            req.header.id = sourceId;
            if (N.DisplayConfigGetDeviceInfo(ref req) != 0) return null;
            return req.viewGdiDeviceName;
        }

        static bool TargetName(N.LUID adapter, uint targetId, out N.DISPLAYCONFIG_TARGET_DEVICE_NAME req)
        {
            req = new N.DISPLAYCONFIG_TARGET_DEVICE_NAME();
            req.header.type = N.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
            req.header.size = (uint)Marshal.SizeOf(typeof(N.DISPLAYCONFIG_TARGET_DEVICE_NAME));
            req.header.adapterId = adapter;
            req.header.id = targetId;
            return N.DisplayConfigGetDeviceInfo(ref req) == 0;
        }

        static J HdrInfo(N.LUID adapter, uint targetId)
        {
            J o = J.Obj();
            N.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO req = new N.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
            req.header.type = N.DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO;
            req.header.size = (uint)Marshal.SizeOf(typeof(N.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO));
            req.header.adapterId = adapter;
            req.header.id = targetId;
            int rc = N.DisplayConfigGetDeviceInfo(ref req);
            if (rc != 0)
            {
                o.Set("supported", false);
                o.Set("queryable", false);
                o.Set("error", "DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO: " + Api.Win32((uint)rc));
                return o;
            }
            o.Set("queryable", true);
            o.Set("supported", (req.value & 0x1) != 0);
            o.Set("enabled", (req.value & 0x2) != 0);
            o.Set("wideColorEnforced", (req.value & 0x4) != 0);
            o.Set("forceDisabled", (req.value & 0x8) != 0);
            o.Set("colorEncoding", ColorEncodingName(req.colorEncoding));
            o.Set("bitsPerColorChannel", req.bitsPerColorChannel);
            return o;
        }

        public static J List(bool includeModes)
        {
            J root = J.Obj();
            List<MonInfo> mons = Monitors();

            N.DISPLAYCONFIG_PATH_INFO[] paths;
            N.DISPLAYCONFIG_MODE_INFO[] modes;
            string qdcError;
            bool haveQdc = QueryConfig(out paths, out modes, out qdcError);
            if (!haveQdc) root.Set("displayConfigError", qdcError);

            J arr = J.Arr();

            if (haveQdc)
            {
                for (int i = 0; i < paths.Length; i++)
                {
                    N.DISPLAYCONFIG_PATH_INFO p = paths[i];
                    J d = J.Obj();
                    d.Set("index", i);

                    string gdi = SourceGdiName(p.sourceInfo.adapterId, p.sourceInfo.id);
                    d.Set("gdiName", gdi);
                    d.Set("sourceId", p.sourceInfo.id);
                    d.Set("targetId", p.targetInfo.id);
                    d.Set("adapterId", p.sourceInfo.adapterId.HighPart.ToString("X", CultureInfo.InvariantCulture) + ":" +
                                       p.sourceInfo.adapterId.LowPart.ToString("X", CultureInfo.InvariantCulture));

                    N.DISPLAYCONFIG_TARGET_DEVICE_NAME tn;
                    if (TargetName(p.targetInfo.adapterId, p.targetInfo.id, out tn))
                    {
                        string friendly = tn.monitorFriendlyDeviceName;
                        d.Set("name", string.IsNullOrEmpty(friendly) ? "Display " + (i + 1) : friendly);
                        d.Set("devicePath", tn.monitorDevicePath);
                        d.Set("connection", OutputTechName(tn.outputTechnology));
                        d.Set("edidManufacturerId", EdidVendor(tn.edidManufactureId));
                        d.Set("edidProductCode", tn.edidProductCodeId);
                    }
                    else
                    {
                        d.Set("name", "Display " + (i + 1));
                        d.Set("connection", OutputTechName(p.targetInfo.outputTechnology));
                    }

                    // Resolution from the source mode, refresh from the target mode's vSync.
                    uint w = 0, h = 0;
                    if (p.sourceInfo.modeInfoIdx != 0xFFFFFFFF && p.sourceInfo.modeInfoIdx < modes.Length)
                    {
                        N.DISPLAYCONFIG_MODE_INFO m = modes[p.sourceInfo.modeInfoIdx];
                        if (m.infoType == N.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
                        {
                            w = m.sourceMode.width;
                            h = m.sourceMode.height;
                            d.Set("positionX", m.sourceMode.position.x);
                            d.Set("positionY", m.sourceMode.position.y);
                        }
                    }
                    d.Set("width", w);
                    d.Set("height", h);
                    d.Set("resolution", w + "x" + h);

                    double hz = 0;
                    if (p.targetInfo.refreshRate.Denominator != 0)
                        hz = (double)p.targetInfo.refreshRate.Numerator / p.targetInfo.refreshRate.Denominator;
                    if (p.targetInfo.modeInfoIdx != 0xFFFFFFFF && p.targetInfo.modeInfoIdx < modes.Length)
                    {
                        N.DISPLAYCONFIG_MODE_INFO m = modes[p.targetInfo.modeInfoIdx];
                        if (m.infoType == N.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET && m.targetMode.vSyncFreq.Denominator != 0)
                            hz = (double)m.targetMode.vSyncFreq.Numerator / m.targetMode.vSyncFreq.Denominator;
                    }
                    d.Set("refreshHz", J.Round(hz, 3));

                    d.Set("rotation", RotationName(p.targetInfo.rotation));
                    d.Set("scaling", ScalingName(p.targetInfo.scaling));
                    d.Set("hdr", HdrInfo(p.targetInfo.adapterId, p.targetInfo.id));

                    // DPI / scale factor comes from the HMONITOR that owns the same GDI device.
                    MonInfo mon = null;
                    for (int k = 0; k < mons.Count && gdi != null; k++)
                        if (string.Equals(mons[k].Device, gdi, StringComparison.OrdinalIgnoreCase)) { mon = mons[k]; break; }
                    if (mon != null)
                    {
                        d.Set("primary", mon.Primary);
                        d.Set("dpi", mon.DpiX);
                        d.Set("scalePercent", mon.ScalePercent > 0 ? mon.ScalePercent : (mon.DpiX > 0 ? (int)Math.Round(mon.DpiX * 100.0 / 96.0) : 0));
                        d.Set("scaleFactor", J.Round(mon.DpiX > 0 ? mon.DpiX / 96.0 : 1.0, 2));
                        d.Set("workAreaWidth", mon.Rect.Right - mon.Rect.Left);
                        d.Set("workAreaHeight", mon.Rect.Bottom - mon.Rect.Top);
                    }
                    else
                    {
                        d.Set("primary", false);
                        d.Set("dpiNote", "no HMONITOR matched this source");
                    }

                    if (includeModes && gdi != null) d.Set("modes", ModesFor(gdi));
                    arr.Add(d);
                }
            }
            else
            {
                // Fall back to pure GDI when QDC is not available (very old drivers / RDP).
                for (uint i = 0; ; i++)
                {
                    N.DISPLAY_DEVICE dd = new N.DISPLAY_DEVICE();
                    dd.cb = Marshal.SizeOf(typeof(N.DISPLAY_DEVICE));
                    if (!N.EnumDisplayDevices(null, i, ref dd, 0)) break;
                    if ((dd.StateFlags & N.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;
                    N.DEVMODE dm = new N.DEVMODE();
                    dm.dmSize = (ushort)Marshal.SizeOf(typeof(N.DEVMODE));
                    N.EnumDisplaySettingsEx(dd.DeviceName, N.ENUM_CURRENT_SETTINGS, ref dm, 0);
                    J d = J.Obj();
                    d.Set("index", (int)i);
                    d.Set("gdiName", dd.DeviceName);
                    d.Set("name", dd.DeviceString);
                    d.Set("primary", (dd.StateFlags & N.DISPLAY_DEVICE_PRIMARY_DEVICE) != 0);
                    d.Set("width", dm.dmPelsWidth);
                    d.Set("height", dm.dmPelsHeight);
                    d.Set("resolution", dm.dmPelsWidth + "x" + dm.dmPelsHeight);
                    d.Set("refreshHz", dm.dmDisplayFrequency);
                    if (includeModes) d.Set("modes", ModesFor(dd.DeviceName));
                    arr.Add(d);
                }
            }

            root.Set("displays", arr);
            root.Set("count", arr.Count);
            root.Set("modesIncluded", includeModes);
            if (!includeModes) root.Set("note", "pass {\"modes\":true} to include the full mode list per display");
            return root;
        }

        static string EdidVendor(ushort id)
        {
            // EDID manufacturer ID is three 5-bit letters, big-endian.
            if (id == 0) return null;
            ushort be = (ushort)(((id & 0xFF) << 8) | (id >> 8));
            char a = (char)('A' - 1 + ((be >> 10) & 0x1F));
            char b = (char)('A' - 1 + ((be >> 5) & 0x1F));
            char c = (char)('A' - 1 + (be & 0x1F));
            return new string(new char[] { a, b, c });
        }

        static string RotationName(uint r)
        {
            switch (r) { case 1: return "0"; case 2: return "90"; case 3: return "180"; case 4: return "270"; default: return "unspecified"; }
        }

        static string ScalingName(uint s)
        {
            switch (s)
            {
                case 1: return "identity";
                case 2: return "centered";
                case 3: return "stretched";
                case 4: return "aspectRatioCenteredMax";
                case 5: return "custom";
                case 128: return "preferred";
                default: return "unspecified";
            }
        }

        public static J ModesFor(string gdiName)
        {
            J arr = J.Arr();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; ; i++)
            {
                N.DEVMODE dm = new N.DEVMODE();
                dm.dmSize = (ushort)Marshal.SizeOf(typeof(N.DEVMODE));
                if (!N.EnumDisplaySettingsEx(gdiName, i, ref dm, 0)) break;
                if (dm.dmBitsPerPel < 32) continue;   // the UI only ever wants true colour modes
                string key = dm.dmPelsWidth + "x" + dm.dmPelsHeight + "@" + dm.dmDisplayFrequency;
                if (!seen.Add(key)) continue;
                J m = J.Obj();
                m.Set("width", dm.dmPelsWidth);
                m.Set("height", dm.dmPelsHeight);
                m.Set("refreshHz", dm.dmDisplayFrequency);
                m.Set("bpp", dm.dmBitsPerPel);
                arr.Add(m);
                if (i > 4096) break;
            }
            return arr;
        }

        // Resolve a caller-supplied display selector to a GDI device name.
        public static string ResolveGdi(string selector)
        {
            J list = List(false);
            J arr = list.Get("displays");
            if (arr == null || arr.Count == 0) throw new ApiFault("no_display", "no active display found");
            if (string.IsNullOrEmpty(selector))
            {
                for (int i = 0; i < arr.Count; i++)
                    if (arr.At(i).B("primary", false)) return arr.At(i).S("gdiName", null);
                return arr.At(0).S("gdiName", null);
            }
            int idx;
            if (int.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out idx))
            {
                if (idx >= 0 && idx < arr.Count) return arr.At(idx).S("gdiName", null);
            }
            for (int i = 0; i < arr.Count; i++)
            {
                J d = arr.At(i);
                if (string.Equals(d.S("gdiName", ""), selector, StringComparison.OrdinalIgnoreCase)) return d.S("gdiName", null);
                if (string.Equals(d.S("name", ""), selector, StringComparison.OrdinalIgnoreCase)) return d.S("gdiName", null);
            }
            throw new ApiFault("no_such_display", "display '" + selector + "' not found");
        }

        public static J SetMode(string selector, int width, int height, int refreshHz, bool test)
        {
            if (width <= 0 || height <= 0) throw new ApiFault("bad_request", "width and height are required");
            string gdi = ResolveGdi(selector);

            // Start from the current DEVMODE so unspecified fields keep their values.
            N.DEVMODE dm = new N.DEVMODE();
            dm.dmSize = (ushort)Marshal.SizeOf(typeof(N.DEVMODE));
            if (!N.EnumDisplaySettingsEx(gdi, N.ENUM_CURRENT_SETTINGS, ref dm, 0))
                throw new ApiFault("display_query_failed", "EnumDisplaySettingsEx(current) failed for " + gdi);

            uint prevW = dm.dmPelsWidth, prevH = dm.dmPelsHeight, prevHz = dm.dmDisplayFrequency;

            dm.dmPelsWidth = (uint)width;
            dm.dmPelsHeight = (uint)height;
            dm.dmFields = N.DM_PELSWIDTH | N.DM_PELSHEIGHT | N.DM_BITSPERPEL;
            if (refreshHz > 0)
            {
                dm.dmDisplayFrequency = (uint)refreshHz;
                dm.dmFields |= N.DM_DISPLAYFREQUENCY;
            }

            // Always dry-run first so a bad mode is reported instead of blanking the panel.
            int probe = N.ChangeDisplaySettingsEx(gdi, ref dm, IntPtr.Zero, N.CDS_TEST, IntPtr.Zero);
            J o = J.Obj();
            o.Set("display", gdi);
            o.Set("requested", width + "x" + height + (refreshHz > 0 ? "@" + refreshHz : ""));
            o.Set("previous", prevW + "x" + prevH + "@" + prevHz);
            o.Set("testResult", CdsName(probe));
            if (probe != 0)
                throw new ApiFault("mode_not_supported",
                    "ChangeDisplaySettingsEx test rejected the mode: " + CdsName(probe), o);
            if (test)
            {
                o.Set("applied", false);
                o.Set("note", "test only; pass {\"test\":false} to apply");
                return o;
            }

            int rc = N.ChangeDisplaySettingsEx(gdi, ref dm, IntPtr.Zero, N.CDS_UPDATEREGISTRY, IntPtr.Zero);
            o.Set("applied", rc == 0);
            o.Set("result", CdsName(rc));
            if (rc != 0) throw new ApiFault("mode_change_failed", "ChangeDisplaySettingsEx failed: " + CdsName(rc), o);
            return o;
        }

        static string CdsName(int rc)
        {
            switch (rc)
            {
                case 0: return "DISP_CHANGE_SUCCESSFUL";
                case 1: return "DISP_CHANGE_RESTART (requires reboot)";
                case -1: return "DISP_CHANGE_FAILED";
                case -2: return "DISP_CHANGE_BADMODE";
                case -3: return "DISP_CHANGE_NOTUPDATED (registry write failed - needs elevation?)";
                case -4: return "DISP_CHANGE_BADFLAGS";
                case -5: return "DISP_CHANGE_BADPARAM";
                case -6: return "DISP_CHANGE_BADDUALVIEW";
                default: return "DISP_CHANGE_" + rc;
            }
        }

        public static J SetHdr(string selector, bool enable)
        {
            N.DISPLAYCONFIG_PATH_INFO[] paths;
            N.DISPLAYCONFIG_MODE_INFO[] modes;
            string err;
            if (!QueryConfig(out paths, out modes, out err))
                throw new ApiFault("displayconfig_unavailable", err);

            string gdi = ResolveGdi(selector);
            for (int i = 0; i < paths.Length; i++)
            {
                string thisGdi = SourceGdiName(paths[i].sourceInfo.adapterId, paths[i].sourceInfo.id);
                if (!string.Equals(thisGdi, gdi, StringComparison.OrdinalIgnoreCase)) continue;

                J before = HdrInfo(paths[i].targetInfo.adapterId, paths[i].targetInfo.id);
                if (!before.B("queryable", false))
                    throw new ApiFault("hdr_unsupported",
                        "this display/driver does not expose advanced colour state: " + before.S("error", ""), before);
                if (!before.B("supported", false))
                    throw new ApiFault("hdr_unsupported",
                        "the OS reports advancedColorSupported=false for this display - HDR cannot be enabled here", before);

                N.DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE req = new N.DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE();
                req.header.type = N.DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE;
                req.header.size = (uint)Marshal.SizeOf(typeof(N.DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE));
                req.header.adapterId = paths[i].targetInfo.adapterId;
                req.header.id = paths[i].targetInfo.id;
                req.value = enable ? 1u : 0u;

                int rc = N.DisplayConfigSetDeviceInfo(ref req);
                J after = HdrInfo(paths[i].targetInfo.adapterId, paths[i].targetInfo.id);

                J o = J.Obj();
                o.Set("display", gdi);
                o.Set("requested", enable);
                o.Set("before", before);
                o.Set("after", after);
                o.Set("applied", rc == 0 && after.B("enabled", false) == enable);
                if (rc != 0)
                {
                    o.Set("error", Api.Win32((uint)rc));
                    throw new ApiFault("hdr_set_failed",
                        "DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE failed: " + Api.Win32((uint)rc), o);
                }
                return o;
            }
            throw new ApiFault("no_such_display", "display '" + gdi + "' has no active DisplayConfig path");
        }
    }

    #endregion

    #region Audio  (Core Audio / MMDevice API)

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumeratorComObject { }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROPERTYKEY { public Guid fmtid; public int pid; }

    // 24 bytes on x64. We only ever read VT_LPWSTR / VT_UI4 out of it.
    [StructLayout(LayoutKind.Sequential)]
    internal struct PROPVARIANT
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public IntPtr p;
        public IntPtr p2;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int props);
        [PreserveSig] int GetAt(int index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out int count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(int channel, float levelDb, ref Guid eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(int channel, float level, ref Guid eventContext);
        [PreserveSig] int GetChannelVolumeLevel(int channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(int channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        [PreserveSig] int GetVolumeStepInfo(out int step, out int stepCount);
        [PreserveSig] int VolumeStepUp(ref Guid eventContext);
        [PreserveSig] int VolumeStepDown(ref Guid eventContext);
        [PreserveSig] int QueryHardwareSupport(out int hardwareSupportMask);
        [PreserveSig] int GetVolumeRange(out float minDb, out float maxDb, out float incrementDb);
    }

    // ------------------------------------------------------------------------------------------
    // IPolicyConfig is UNDOCUMENTED. It is the only way to change the default audio endpoint
    // programmatically and is what every third-party audio switcher uses, but Microsoft does not
    // support it: the vtable order below is reverse-engineered and HAS CHANGED between Windows
    // versions (the "Vista" variant lacks ResetDeviceFormat, shifting every later slot by one).
    // We therefore try the modern IID first and fall back to the Vista IID, and we verify the
    // result by re-reading the default endpoint rather than trusting the HRESULT.
    // If a future Windows build reorders this vtable again, audio.setDefault will start failing
    // with a bogus HRESULT - that is the failure mode to look for.
    //
    // MEASURED on Windows 11 24H2 (26100.9168): the modern IID {f8679f50-...} is NOT supported -
    // QueryInterface returns E_NOINTERFACE. The Vista IID {568b9108-...} IS supported and works
    // for a standard user. So on current Windows the fallback below is the path that actually
    // runs; the modern branch is kept because it is the one that works on older builds.
    // ------------------------------------------------------------------------------------------
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    internal class PolicyConfigClientComObject { }

    [ComImport, Guid("294935CE-F637-4E7C-A41B-AB255460B862")]
    internal class PolicyConfigVistaClientComObject { }

    [ComImport, Guid("f8679f50-850a-4b0d-a03d-c0c7c6b3f7be"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat();
        [PreserveSig] int GetDeviceFormat();
        [PreserveSig] int ResetDeviceFormat();
        [PreserveSig] int SetDeviceFormat();
        [PreserveSig] int GetProcessingPeriod();
        [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode();
        [PreserveSig] int SetShareMode();
        [PreserveSig] int GetPropertyValue();
        [PreserveSig] int SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }

    [ComImport, Guid("568b9108-44bf-40b4-9006-86afe5b5a620"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfigVista
    {
        [PreserveSig] int GetMixFormat();
        [PreserveSig] int GetDeviceFormat();
        [PreserveSig] int SetDeviceFormat();
        [PreserveSig] int GetProcessingPeriod();
        [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode();
        [PreserveSig] int SetShareMode();
        [PreserveSig] int GetPropertyValue();
        [PreserveSig] int SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }

    public static class AudioMod
    {
        const int eRender = 0, eCapture = 1;
        const int eConsole = 0, eMultimedia = 1, eCommunications = 2;
        const int DEVICE_STATE_ACTIVE = 0x1;
        const int DEVICE_STATE_ALL = 0xF;
        const int STGM_READ = 0;
        const int CLSCTX_ALL = 23;

        static readonly Guid PKEY_FMT_Device = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0");
        static readonly Guid PKEY_FMT_DeviceInterface = new Guid("026e516e-b814-414b-83cd-856d6fef4822");
        static readonly Guid PKEY_FMT_AudioEndpoint = new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e");
        static readonly Guid IID_IAudioEndpointVolume = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
        static Guid _noContext = Guid.Empty;

        // IAudioEndpointVolume returns S_FALSE (1) when the value you asked for is already in
        // effect. That is success, not failure - treating it as an error made audio.setMute
        // report a bogus "HRESULT 0x00000001" whenever the endpoint was already in that state.
        static bool Succeeded(int hr) { return hr >= 0; }

        static IMMDeviceEnumerator NewEnumerator()
        {
            try
            {
                return (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            }
            catch (Exception ex)
            {
                throw new ApiFault("audio_unavailable", "could not create MMDeviceEnumerator: " + Api.Describe(ex));
            }
        }

        static string PropStr(IPropertyStore store, Guid fmtid, int pid)
        {
            PROPERTYKEY k = new PROPERTYKEY();
            k.fmtid = fmtid;
            k.pid = pid;
            PROPVARIANT v;
            if (store.GetValue(ref k, out v) != 0) return null;
            try
            {
                if (v.vt == 31 /* VT_LPWSTR */ && v.p != IntPtr.Zero) return Marshal.PtrToStringUni(v.p);
                if (v.vt == 8 /* VT_BSTR */ && v.p != IntPtr.Zero) return Marshal.PtrToStringBSTR(v.p);
                return null;
            }
            finally { FreeVariant(ref v); }
        }

        static int PropU4(IPropertyStore store, Guid fmtid, int pid, int dflt)
        {
            PROPERTYKEY k = new PROPERTYKEY();
            k.fmtid = fmtid;
            k.pid = pid;
            PROPVARIANT v;
            if (store.GetValue(ref k, out v) != 0) return dflt;
            try
            {
                if (v.vt == 19 /* VT_UI4 */ || v.vt == 3 /* VT_I4 */) return (int)(v.p.ToInt64() & 0xFFFFFFFF);
                return dflt;
            }
            finally { FreeVariant(ref v); }
        }

        static void FreeVariant(ref PROPVARIANT v)
        {
            IntPtr tmp = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(PROPVARIANT)));
            try
            {
                Marshal.StructureToPtr(v, tmp, false);
                N.PropVariantClear(tmp);
            }
            catch { }
            finally { Marshal.FreeHGlobal(tmp); }
        }

        static string FormFactorName(int f)
        {
            switch (f)
            {
                case 0: return "RemoteNetworkDevice";
                case 1: return "Speakers";
                case 2: return "LineLevel";
                case 3: return "Headphones";
                case 4: return "Microphone";
                case 5: return "Headset";
                case 6: return "Handset";
                case 7: return "UnknownDigitalPassthrough";
                case 8: return "SPDIF";
                case 9: return "DigitalAudioDisplayDevice";
                case 10: return "UnknownFormFactor";
                default: return null;
            }
        }

        static string StateName(int s)
        {
            switch (s)
            {
                case 1: return "active";
                case 2: return "disabled";
                case 4: return "notPresent";
                case 8: return "unplugged";
                default: return "unknown";
            }
        }

        static J DeviceJson(IMMDevice dev, string flow, HashSet<string> defaults, Dictionary<string, List<string>> roles)
        {
            string id;
            dev.GetId(out id);
            int state;
            dev.GetState(out state);

            J o = J.Obj();
            o.Set("id", id);
            o.Set("flow", flow);
            o.Set("state", StateName(state));

            IPropertyStore store;
            if (dev.OpenPropertyStore(STGM_READ, out store) == 0 && store != null)
            {
                try
                {
                    o.Set("name", PropStr(store, PKEY_FMT_Device, 14));               // PKEY_Device_FriendlyName
                    o.Set("adapter", PropStr(store, PKEY_FMT_DeviceInterface, 2));    // PKEY_DeviceInterface_FriendlyName
                    o.Set("shortName", PropStr(store, PKEY_FMT_Device, 2));           // PKEY_Device_DeviceDesc
                    o.Set("formFactor", FormFactorName(PropU4(store, PKEY_FMT_AudioEndpoint, 0, -1)));
                }
                finally { Marshal.ReleaseComObject(store); }
            }

            o.Set("isDefault", defaults.Contains(id));
            J r = J.Arr();
            List<string> rl;
            if (roles.TryGetValue(id, out rl)) for (int i = 0; i < rl.Count; i++) r.Add(rl[i]);
            o.Set("defaultForRoles", r);

            // Per-endpoint volume, cheap enough to include in the listing.
            try
            {
                object raw;
                Guid iid = IID_IAudioEndpointVolume;
                if (dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out raw) == 0 && raw != null)
                {
                    IAudioEndpointVolume vol = (IAudioEndpointVolume)raw;
                    try
                    {
                        float lvl;
                        bool mute;
                        if (vol.GetMasterVolumeLevelScalar(out lvl) == 0) o.Set("volume", J.Round(lvl, 4));
                        if (vol.GetMute(out mute) == 0) o.Set("muted", mute);
                    }
                    finally { Marshal.ReleaseComObject(vol); }
                }
            }
            catch { }

            return o;
        }

        public static J Devices(bool includeInactive)
        {
            IMMDeviceEnumerator en = NewEnumerator();
            try
            {
                HashSet<string> defaults = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, List<string>> roles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                J defaultsJson = J.Obj();

                int[] flows = new int[] { eRender, eCapture };
                string[] flowNames = new string[] { "render", "capture" };
                int[] roleIds = new int[] { eConsole, eMultimedia, eCommunications };
                string[] roleNames = new string[] { "console", "multimedia", "communications" };

                for (int fi = 0; fi < flows.Length; fi++)
                {
                    J perFlow = J.Obj();
                    for (int ri = 0; ri < roleIds.Length; ri++)
                    {
                        IMMDevice d;
                        if (en.GetDefaultAudioEndpoint(flows[fi], roleIds[ri], out d) == 0 && d != null)
                        {
                            string id;
                            d.GetId(out id);
                            Marshal.ReleaseComObject(d);
                            defaults.Add(id);
                            List<string> rl;
                            if (!roles.TryGetValue(id, out rl)) { rl = new List<string>(); roles[id] = rl; }
                            rl.Add(flowNames[fi] + ":" + roleNames[ri]);
                            perFlow.Set(roleNames[ri], id);
                        }
                        else perFlow.SetNull(roleNames[ri]);
                    }
                    defaultsJson.Set(flowNames[fi], perFlow);
                }

                J outArr = J.Arr(), inArr = J.Arr();
                int mask = includeInactive ? DEVICE_STATE_ALL : DEVICE_STATE_ACTIVE;

                for (int fi = 0; fi < flows.Length; fi++)
                {
                    IMMDeviceCollection col;
                    int hr = en.EnumAudioEndpoints(flows[fi], mask, out col);
                    if (hr != 0 || col == null) continue;
                    try
                    {
                        int count;
                        col.GetCount(out count);
                        for (int i = 0; i < count; i++)
                        {
                            IMMDevice d;
                            if (col.Item(i, out d) != 0 || d == null) continue;
                            try
                            {
                                J j = DeviceJson(d, flowNames[fi], defaults, roles);
                                if (fi == 0) outArr.Add(j); else inArr.Add(j);
                            }
                            finally { Marshal.ReleaseComObject(d); }
                        }
                    }
                    finally { Marshal.ReleaseComObject(col); }
                }

                J root = J.Obj();
                root.Set("outputs", outArr);
                root.Set("inputs", inArr);
                root.Set("defaults", defaultsJson);
                root.Set("includeInactive", includeInactive);
                return root;
            }
            finally { Marshal.ReleaseComObject(en); }
        }

        static IMMDevice Resolve(IMMDeviceEnumerator en, string id, int flow)
        {
            IMMDevice dev;
            if (string.IsNullOrEmpty(id))
            {
                if (en.GetDefaultAudioEndpoint(flow, eConsole, out dev) != 0 || dev == null)
                    throw new ApiFault("no_default_endpoint", "no default " + (flow == eRender ? "output" : "input") + " device");
                return dev;
            }
            if (en.GetDevice(id, out dev) == 0 && dev != null) return dev;

            // Not an endpoint id - try to match on friendly name so the UI can pass what it shows.
            IMMDeviceCollection col;
            if (en.EnumAudioEndpoints(flow, DEVICE_STATE_ACTIVE, out col) == 0 && col != null)
            {
                try
                {
                    int count;
                    col.GetCount(out count);
                    for (int i = 0; i < count; i++)
                    {
                        IMMDevice d;
                        if (col.Item(i, out d) != 0 || d == null) continue;
                        IPropertyStore ps;
                        string name = null;
                        if (d.OpenPropertyStore(STGM_READ, out ps) == 0 && ps != null)
                        {
                            try { name = PropStr(ps, PKEY_FMT_Device, 14); }
                            finally { Marshal.ReleaseComObject(ps); }
                        }
                        if (name != null && name.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0) return d;
                        Marshal.ReleaseComObject(d);
                    }
                }
                finally { Marshal.ReleaseComObject(col); }
            }
            throw new ApiFault("no_such_device", "audio device '" + id + "' not found");
        }

        static int FlowOf(string s)
        {
            if (string.Equals(s, "input", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s, "capture", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s, "recording", StringComparison.OrdinalIgnoreCase)) return eCapture;
            return eRender;
        }

        public static J GetVolume(string id, string flowName)
        {
            int flow = FlowOf(flowName);
            IMMDeviceEnumerator en = NewEnumerator();
            try
            {
                IMMDevice dev = Resolve(en, id, flow);
                try
                {
                    object raw;
                    Guid iid = IID_IAudioEndpointVolume;
                    int hr = dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out raw);
                    if (hr != 0 || raw == null) throw new ApiFault("volume_unavailable", "IAudioEndpointVolume: " + Api.Hr(hr));
                    IAudioEndpointVolume vol = (IAudioEndpointVolume)raw;
                    try
                    {
                        string devId;
                        dev.GetId(out devId);
                        float lvl = 0, db = 0, min = 0, max = 0, inc = 0;
                        bool mute = false;
                        int channels = 0;
                        vol.GetMasterVolumeLevelScalar(out lvl);
                        vol.GetMasterVolumeLevel(out db);
                        vol.GetMute(out mute);
                        vol.GetChannelCount(out channels);
                        vol.GetVolumeRange(out min, out max, out inc);
                        J o = J.Obj();
                        o.Set("id", devId);
                        o.Set("flow", flow == eRender ? "render" : "capture");
                        o.Set("volume", J.Round(lvl, 4));
                        o.Set("volumePercent", (int)Math.Round(lvl * 100));
                        o.Set("volumeDb", J.Round(db, 2));
                        o.Set("muted", mute);
                        o.Set("channels", channels);
                        o.Set("minDb", J.Round(min, 2));
                        o.Set("maxDb", J.Round(max, 2));
                        o.Set("stepDb", J.Round(inc, 2));
                        return o;
                    }
                    finally { Marshal.ReleaseComObject(vol); }
                }
                finally { Marshal.ReleaseComObject(dev); }
            }
            finally { Marshal.ReleaseComObject(en); }
        }

        public static J SetVolume(string id, string flowName, double level)
        {
            if (level < 0 || level > 1) throw new ApiFault("bad_request", "level must be 0.0 - 1.0");
            int flow = FlowOf(flowName);
            IMMDeviceEnumerator en = NewEnumerator();
            try
            {
                IMMDevice dev = Resolve(en, id, flow);
                try
                {
                    object raw;
                    Guid iid = IID_IAudioEndpointVolume;
                    int hr = dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out raw);
                    if (hr != 0 || raw == null) throw new ApiFault("volume_unavailable", "IAudioEndpointVolume: " + Api.Hr(hr));
                    IAudioEndpointVolume vol = (IAudioEndpointVolume)raw;
                    try
                    {
                        float before;
                        vol.GetMasterVolumeLevelScalar(out before);
                        int shr = vol.SetMasterVolumeLevelScalar((float)level, ref _noContext);
                        float after;
                        vol.GetMasterVolumeLevelScalar(out after);
                        string devId;
                        dev.GetId(out devId);
                        J o = J.Obj();
                        o.Set("id", devId);
                        o.Set("before", J.Round(before, 4));
                        o.Set("requested", J.Round(level, 4));
                        o.Set("after", J.Round(after, 4));
                        o.Set("applied", Succeeded(shr));
                        if (!Succeeded(shr)) throw new ApiFault("volume_set_failed", Api.Hr(shr), o);
                        return o;
                    }
                    finally { Marshal.ReleaseComObject(vol); }
                }
                finally { Marshal.ReleaseComObject(dev); }
            }
            finally { Marshal.ReleaseComObject(en); }
        }

        public static J SetMute(string id, string flowName, bool mute, bool toggle)
        {
            int flow = FlowOf(flowName);
            IMMDeviceEnumerator en = NewEnumerator();
            try
            {
                IMMDevice dev = Resolve(en, id, flow);
                try
                {
                    object raw;
                    Guid iid = IID_IAudioEndpointVolume;
                    int hr = dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out raw);
                    if (hr != 0 || raw == null) throw new ApiFault("volume_unavailable", "IAudioEndpointVolume: " + Api.Hr(hr));
                    IAudioEndpointVolume vol = (IAudioEndpointVolume)raw;
                    try
                    {
                        bool before;
                        vol.GetMute(out before);
                        bool target = toggle ? !before : mute;
                        int shr = vol.SetMute(target, ref _noContext);
                        bool after;
                        vol.GetMute(out after);
                        string devId;
                        dev.GetId(out devId);
                        J o = J.Obj();
                        o.Set("id", devId);
                        o.Set("before", before);
                        o.Set("after", after);
                        o.Set("applied", Succeeded(shr) && after == target);
                        if (!Succeeded(shr)) throw new ApiFault("mute_set_failed", Api.Hr(shr), o);
                        return o;
                    }
                    finally { Marshal.ReleaseComObject(vol); }
                }
                finally { Marshal.ReleaseComObject(dev); }
            }
            finally { Marshal.ReleaseComObject(en); }
        }

        // Uses the undocumented IPolicyConfig - see the block comment above the interface.
        public static J SetDefault(string id, string flowName, string roleName)
        {
            if (string.IsNullOrEmpty(id)) throw new ApiFault("bad_request", "id is required (from audio.devices)");
            int flow = FlowOf(flowName);

            List<int> targetRoles = new List<int>();
            if (string.IsNullOrEmpty(roleName) || string.Equals(roleName, "all", StringComparison.OrdinalIgnoreCase))
            { targetRoles.Add(eConsole); targetRoles.Add(eMultimedia); targetRoles.Add(eCommunications); }
            else if (string.Equals(roleName, "console", StringComparison.OrdinalIgnoreCase)) targetRoles.Add(eConsole);
            else if (string.Equals(roleName, "multimedia", StringComparison.OrdinalIgnoreCase)) targetRoles.Add(eMultimedia);
            else if (string.Equals(roleName, "communications", StringComparison.OrdinalIgnoreCase)) targetRoles.Add(eCommunications);
            else throw new ApiFault("bad_request", "role must be console | multimedia | communications | all");

            IMMDeviceEnumerator en = NewEnumerator();
            string resolvedId, name = null;
            try
            {
                IMMDevice dev = Resolve(en, id, flow);
                try
                {
                    dev.GetId(out resolvedId);
                    IPropertyStore ps;
                    if (dev.OpenPropertyStore(STGM_READ, out ps) == 0 && ps != null)
                    {
                        try { name = PropStr(ps, PKEY_FMT_Device, 14); }
                        finally { Marshal.ReleaseComObject(ps); }
                    }
                }
                finally { Marshal.ReleaseComObject(dev); }
            }
            finally { Marshal.ReleaseComObject(en); }

            J o = J.Obj();
            o.Set("id", resolvedId);
            o.Set("name", name);
            o.Set("flow", flow == eRender ? "render" : "capture");
            o.Set("interface", "IPolicyConfig (undocumented)");

            string lastError = null;
            bool ok = false;

            // Modern client first.
            try
            {
                IPolicyConfig pc = (IPolicyConfig)new PolicyConfigClientComObject();
                try
                {
                    ok = true;
                    for (int i = 0; i < targetRoles.Count; i++)
                    {
                        int hr = pc.SetDefaultEndpoint(resolvedId, targetRoles[i]);
                        if (hr != 0) { ok = false; lastError = "IPolicyConfig.SetDefaultEndpoint: " + Api.Hr(hr); break; }
                    }
                }
                finally { Marshal.ReleaseComObject(pc); }
            }
            catch (Exception ex) { lastError = "IPolicyConfig: " + Api.Describe(ex); ok = false; }

            if (!ok)
            {
                o.Set("modernClientError", lastError);
                try
                {
                    IPolicyConfigVista pc = (IPolicyConfigVista)new PolicyConfigVistaClientComObject();
                    try
                    {
                        ok = true;
                        for (int i = 0; i < targetRoles.Count; i++)
                        {
                            int hr = pc.SetDefaultEndpoint(resolvedId, targetRoles[i]);
                            if (hr != 0) { ok = false; lastError = "IPolicyConfigVista.SetDefaultEndpoint: " + Api.Hr(hr); break; }
                        }
                        if (ok) o.Put("interface", "IPolicyConfigVista (undocumented)");
                    }
                    finally { Marshal.ReleaseComObject(pc); }
                }
                catch (Exception ex) { lastError = (lastError == null ? "" : lastError + " | ") + "IPolicyConfigVista: " + Api.Describe(ex); ok = false; }
            }

            // Never trust the HRESULT here - re-read what the OS now reports.
            J verify = J.Obj();
            bool verified = true;
            IMMDeviceEnumerator en2 = NewEnumerator();
            try
            {
                string[] roleNames = new string[] { "console", "multimedia", "communications" };
                for (int i = 0; i < targetRoles.Count; i++)
                {
                    IMMDevice d;
                    string curId = null;
                    if (en2.GetDefaultAudioEndpoint(flow, targetRoles[i], out d) == 0 && d != null)
                    {
                        d.GetId(out curId);
                        Marshal.ReleaseComObject(d);
                    }
                    verify.Set(roleNames[targetRoles[i]], curId);
                    if (!string.Equals(curId, resolvedId, StringComparison.OrdinalIgnoreCase)) verified = false;
                }
            }
            finally { Marshal.ReleaseComObject(en2); }

            o.Set("rolesSet", targetRoles.Count);
            o.Set("verifiedDefaults", verify);
            o.Set("applied", verified);
            if (!verified)
                throw new ApiFault("audio_set_default_failed",
                    lastError == null ? "the call reported success but the default endpoint did not change" : lastError, o);
            return o;
        }
    }

    #endregion

    #region WinRT bridge (reflection only)

    // .NET Framework 4.5+ can load WinRT types by name from the "Windows" pseudo-assembly without
    // any winmd reference at compile time - exactly what PowerShell's
    // [Windows.Devices.Radios.Radio, Windows, ContentType=WindowsRuntime] does.
    //
    // We use plain reflection rather than hand-declared COM interfaces on purpose: guessing a WinRT
    // vtable slot wrong would corrupt the stack and take the shell down, whereas a wrong reflection
    // lookup is just a caught exception. The cost is that every call is late-bound.
    //
    // Verified working on Windows 11 IoT Enterprise LTSC 24H2 (build 26100) from a plain
    // .NET Framework 4.8 x64 console process.
    internal static class WinRt
    {
        public static Type Type_(string fullName)
        {
            try { return Type.GetType(fullName + ", Windows, ContentType=WindowsRuntime", false); }
            catch { return null; }
        }

        // Poll an IAsyncOperation/IAsyncAction to completion. opType must be the *static* return
        // type of the method that produced op (MethodInfo.ReturnType) - an RCW does not report its
        // own interfaces, so that is the only way to reach GetResults.
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
                catch (Exception ex) { error = "reading IAsyncInfo.Status: " + Api.Describe(ex); return null; }
                if (status != 0) break;                       // 0 = Started
                if (waited >= timeoutMs) { error = "timed out after " + timeoutMs + " ms"; return null; }
                Thread.Sleep(25);
                waited += 25;
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
            if (gr == null) return null;                       // IAsyncAction has no result
            try { return gr.Invoke(op, null); }
            catch (Exception ex) { error = "GetResults: " + Api.Describe(ex); return null; }
        }

        // Convenience: invoke a static WinRT method returning an async op and wait for the result.
        public static object CallStaticAsync(Type t, string method, object[] args, int timeoutMs, out string error)
        {
            error = null;
            if (t == null) { error = "type not available"; return null; }
            MethodInfo mi = Find(t, method, BindingFlags.Public | BindingFlags.Static, args);
            if (mi == null) { error = method + " not found on " + t.FullName; return null; }
            object op;
            try { op = mi.Invoke(null, args); }
            catch (Exception ex) { error = method + ": " + Api.Describe(ex); return null; }
            return Await(mi.ReturnType, op, timeoutMs, out error);
        }

        public static object CallInstanceAsync(object target, Type t, string method, object[] args, int timeoutMs, out string error)
        {
            error = null;
            if (t == null) { error = "type not available"; return null; }
            MethodInfo mi = Find(t, method, BindingFlags.Public | BindingFlags.Instance, args);
            if (mi == null) { error = method + " not found on " + t.FullName; return null; }
            object op;
            try { op = mi.Invoke(target, args); }
            catch (Exception ex) { error = method + ": " + Api.Describe(ex); return null; }
            return Await(mi.ReturnType, op, timeoutMs, out error);
        }

        static MethodInfo Find(Type t, string name, BindingFlags flags, object[] args)
        {
            int n = args == null ? 0 : args.Length;
            MethodInfo[] all = t.GetMethods(flags);
            for (int i = 0; i < all.Length; i++)
                if (all[i].Name == name && all[i].GetParameters().Length == n) return all[i];
            return null;
        }

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
    }

    #endregion

    #region Power

    public static class PowerMod
    {
        public static J Status()
        {
            J o = J.Obj();

            N.SYSTEM_POWER_STATUS ps;
            if (N.GetSystemPowerStatus(out ps))
            {
                string ac = ps.ACLineStatus == 1 ? "ac" : (ps.ACLineStatus == 0 ? "battery" : "unknown");
                bool noBattery = (ps.BatteryFlag & 128) != 0;
                o.Set("source", ac);
                o.Set("onAc", ps.ACLineStatus == 1);
                o.Set("hasBattery", !noBattery && ps.BatteryFlag != 255);
                if (!noBattery && ps.BatteryLifePercent <= 100)
                {
                    o.Set("batteryPercent", ps.BatteryLifePercent);
                    o.Set("charging", (ps.BatteryFlag & 8) != 0);
                    o.Set("batteryHigh", (ps.BatteryFlag & 1) != 0);
                    o.Set("batteryLow", (ps.BatteryFlag & 2) != 0);
                    o.Set("batteryCritical", (ps.BatteryFlag & 4) != 0);
                    if (ps.BatteryLifeTime >= 0) o.Set("secondsRemaining", ps.BatteryLifeTime);
                    if (ps.BatteryFullLifeTime >= 0) o.Set("secondsFullCharge", ps.BatteryFullLifeTime);
                }
                else
                {
                    o.SetNull("batteryPercent");
                }
                o.Set("batterySaver", ps.SystemStatusFlag == 1);
            }
            else
            {
                o.Set("error", "GetSystemPowerStatus failed: " + Api.Win32((uint)Marshal.GetLastWin32Error()));
            }

            // Active power plan.
            IntPtr guidPtr = IntPtr.Zero;
            try
            {
                uint rc = N.PowerGetActiveScheme(IntPtr.Zero, out guidPtr);
                if (rc == 0 && guidPtr != IntPtr.Zero)
                {
                    Guid scheme = (Guid)Marshal.PtrToStructure(guidPtr, typeof(Guid));
                    o.Set("powerPlanGuid", scheme.ToString());
                    o.Set("powerPlan", FriendlyName(scheme));
                }
                else o.Set("powerPlanError", Api.Win32(rc));
            }
            catch (Exception ex) { o.Set("powerPlanError", Api.Describe(ex)); }
            finally { if (guidPtr != IntPtr.Zero) N.LocalFree(guidPtr); }

            o.Set("uptimeMs", (long)N.GetTickCount64());
            return o;
        }

        static string FriendlyName(Guid scheme)
        {
            uint size = 0;
            Guid g = scheme;
            N.PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
            if (size == 0) return null;
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                uint rc = N.PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, buf, ref size);
                if (rc != 0) return null;
                return Marshal.PtrToStringUni(buf);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        public static J Sleep(bool hibernate)
        {
            // SetSuspendState needs SE_SHUTDOWN_NAME. Interactive users hold it (disabled by
            // default), so enabling it is normally enough - no elevation required.
            string privErr = N.EnablePrivilege("SeShutdownPrivilege");
            J o = J.Obj();
            o.Set("privilege", privErr == null ? "SeShutdownPrivilege enabled" : privErr);
            if (privErr != null)
                throw new ApiFault("privilege_unavailable",
                    "cannot enable SeShutdownPrivilege: " + privErr, o);

            bool ok = N.SetSuspendState(hibernate, false, false);
            o.Set("hibernate", hibernate);
            o.Set("requested", true);
            o.Set("returned", ok);
            if (!ok)
            {
                int le = Marshal.GetLastWin32Error();
                o.Set("win32", Api.Win32((uint)le));
                throw new ApiFault("sleep_failed", "SetSuspendState returned FALSE: " + Api.Win32((uint)le), o);
            }
            // Note: SetSuspendState blocks until the machine resumes.
            o.Set("note", "returned after resume");
            return o;
        }

        // Two paths, both documented, caller picks:
        //   mode:"signal" (default) - do nothing to the machine, just tell the shell which exit
        //     code to use. ShellHostWeb already maps exit 2 = restart, exit 3 = shutdown, and Shell
        //     Launcher performs the action. This is the safe path in a kiosk: the shell gets to
        //     tear down cleanly and the Shell Launcher policy stays authoritative.
        //   mode:"direct" - call ExitWindowsEx here and now. Needs SeShutdownPrivilege, which an
        //     interactive standard user does hold, so this works without elevation - but it
        //     bypasses Shell Launcher's own handling.
        public static J EndSession(string action, string mode, bool force)
        {
            bool signal = !string.Equals(mode, "direct", StringComparison.OrdinalIgnoreCase);

            int exitCode;
            uint flags;
            string need;
            switch (action)
            {
                case "restart": exitCode = 2; flags = N.EWX_REBOOT; need = "SeShutdownPrivilege"; break;
                case "shutdown": exitCode = 3; flags = N.EWX_SHUTDOWN; need = "SeShutdownPrivilege"; break;
                case "signout": exitCode = 0; flags = N.EWX_LOGOFF; need = null; break;
                default: throw new ApiFault("bad_request", "action must be restart | shutdown | signout");
            }

            J o = J.Obj();
            o.Set("action", action);
            o.Set("mode", signal ? "signal" : "direct");
            o.Set("exitCode", exitCode);

            if (signal)
            {
                o.Set("performed", false);
                o.Set("instruction", "host should exit with code " + exitCode +
                    " (ShellHostWeb exit-code contract: 2=restart, 3=shutdown, 0=normal exit/signout)");
                return o;
            }

            if (need != null)
            {
                string privErr = N.EnablePrivilege(need);
                o.Set("privilege", privErr == null ? need + " enabled" : privErr);
                if (privErr != null)
                    throw new ApiFault("privilege_unavailable", "cannot enable " + need + ": " + privErr, o);
            }

            uint f = flags | (force ? N.EWX_FORCEIFHUNG : 0u);
            bool ok = N.ExitWindowsEx(f, N.SHTDN_REASON_PLANNED);
            o.Set("performed", ok);
            if (!ok)
            {
                int le = Marshal.GetLastWin32Error();
                o.Set("win32", Api.Win32((uint)le));
                throw new ApiFault("end_session_failed", "ExitWindowsEx failed: " + Api.Win32((uint)le), o);
            }
            return o;
        }
    }

    #endregion

    #region Bluetooth

    // Radio inventory / paired devices / inquiry come from the documented Win32 Bluetooth API
    // (bthprops.cpl). Radio power and pairing go through WinRT, because:
    //   * there is no Win32 API at all for turning a Bluetooth radio on or off, and
    //   * Win32 pairing (BluetoothAuthenticateDeviceEx) does not enable the device's services -
    //     that needs BluetoothSetServiceState, which requires administrator rights. WinRT's
    //     DeviceInformationPairing does the whole ceremony including service enablement and is
    //     the same path the Settings app uses as a standard user.
    public static class BtMod
    {
        sealed class Radio
        {
            public IntPtr H;
            public N.BLUETOOTH_RADIO_INFO Info;
        }

        static List<Radio> FindRadios()
        {
            List<Radio> radios = new List<Radio>();
            N.BLUETOOTH_FIND_RADIO_PARAMS p = new N.BLUETOOTH_FIND_RADIO_PARAMS();
            p.dwSize = (uint)Marshal.SizeOf(typeof(N.BLUETOOTH_FIND_RADIO_PARAMS));
            IntPtr hRadio;
            IntPtr find;
            try { find = N.BluetoothFindFirstRadio(ref p, out hRadio); }
            catch (DllNotFoundException) { return radios; }
            if (find == IntPtr.Zero) return radios;
            try
            {
                do
                {
                    Radio r = new Radio();
                    r.H = hRadio;
                    N.BLUETOOTH_RADIO_INFO info = new N.BLUETOOTH_RADIO_INFO();
                    info.dwSize = (uint)Marshal.SizeOf(typeof(N.BLUETOOTH_RADIO_INFO));
                    if (N.BluetoothGetRadioInfo(hRadio, ref info) == 0) r.Info = info;
                    radios.Add(r);
                } while (N.BluetoothFindNextRadio(find, out hRadio));
            }
            finally { N.BluetoothFindRadioClose(find); }
            return radios;
        }

        static void CloseRadios(List<Radio> radios)
        {
            for (int i = 0; i < radios.Count; i++)
                if (radios[i].H != IntPtr.Zero) N.CloseHandle(radios[i].H);
        }

        public static string AddrText(ulong addr)
        {
            byte[] b = BitConverter.GetBytes(addr);
            // BLUETOOTH_ADDRESS is little-endian in the ULONGLONG; print MSB first.
            return b[5].ToString("X2") + ":" + b[4].ToString("X2") + ":" + b[3].ToString("X2") + ":" +
                   b[2].ToString("X2") + ":" + b[1].ToString("X2") + ":" + b[0].ToString("X2");
        }

        public static bool TryParseAddr(string s, out ulong addr)
        {
            addr = 0;
            if (string.IsNullOrEmpty(s)) return false;
            string clean = s.Replace(":", "").Replace("-", "").Replace(" ", "").Trim();
            if (clean.Length != 12) return false;
            ulong v;
            if (!ulong.TryParse(clean, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)) return false;
            addr = v;
            return true;
        }

        static string MajorClass(uint cod)
        {
            uint major = (cod >> 8) & 0x1F;
            switch (major)
            {
                case 0: return "Miscellaneous";
                case 1: return "Computer";
                case 2: return "Phone";
                case 3: return "NetworkAccessPoint";
                case 4: return "AudioVideo";
                case 5: return "Peripheral";
                case 6: return "Imaging";
                case 7: return "Wearable";
                case 8: return "Toy";
                case 9: return "Health";
                default: return "Uncategorized";
            }
        }

        static bool LooksLikeGamepad(uint cod)
        {
            uint major = (cod >> 8) & 0x1F;
            uint minor = (cod >> 2) & 0x3F;
            // Peripheral major class, "gamepad / joystick" minor bits.
            return major == 5 && ((minor & 0x0F) == 0x02 || (minor & 0x0F) == 0x03 || (minor & 0x0F) == 0x04);
        }

        // WinRT radio state, if it is reachable. Never throws.
        static J RadioStates(out object btRadioObject, out Type radioType)
        {
            btRadioObject = null;
            radioType = null;
            J arr = J.Arr();
            Type t = WinRt.Type_("Windows.Devices.Radios.Radio");
            if (t == null)
            {
                return null;
            }
            radioType = t;
            string err;
            // RequestAccessAsync is required before the state is readable in some contexts; it is
            // a no-op for a desktop process that already has access.
            WinRt.CallStaticAsync(t, "RequestAccessAsync", null, 5000, out err);
            object list = WinRt.CallStaticAsync(t, "GetRadiosAsync", null, 8000, out err);
            if (list == null)
            {
                J e = J.Arr();
                e.Add(J.Obj().Set("error", err == null ? "GetRadiosAsync returned nothing" : err));
                return e;
            }
            System.Collections.IEnumerable en = list as System.Collections.IEnumerable;
            if (en == null) return arr;
            foreach (object r in en)
            {
                string kind = WinRt.PropStr(r, t, "Kind");
                J o = J.Obj();
                o.Set("name", WinRt.PropStr(r, t, "Name"));
                o.Set("kind", kind);
                o.Set("state", WinRt.PropStr(r, t, "State"));
                arr.Add(o);
                if (string.Equals(kind, "Bluetooth", StringComparison.OrdinalIgnoreCase) && btRadioObject == null)
                    btRadioObject = r;
            }
            return arr;
        }

        public static J Status()
        {
            J root = J.Obj();
            List<Radio> radios = FindRadios();
            try
            {
                J arr = J.Arr();
                for (int i = 0; i < radios.Count; i++)
                {
                    Radio r = radios[i];
                    J o = J.Obj();
                    o.Set("name", r.Info.szName);
                    o.Set("address", AddrText(r.Info.address));
                    o.Set("manufacturerId", r.Info.manufacturer);
                    o.Set("lmpSubversion", r.Info.lmpSubversion);
                    bool conn = false, disc = false;
                    try { conn = N.BluetoothIsConnectable(r.H); }
                    catch { }
                    try { disc = N.BluetoothIsDiscoverable(r.H); }
                    catch { }
                    o.Set("acceptsIncoming", conn);
                    o.Set("discoverable", disc);
                    arr.Add(o);
                }
                root.Set("radios", arr);
                root.Set("present", radios.Count > 0);
            }
            finally { CloseRadios(radios); }

            object btObj;
            Type rt;
            J states = RadioStates(out btObj, out rt);
            if (states == null)
            {
                root.Set("radioControl", "unavailable");
                root.Set("radioControlDetail", "Windows.Devices.Radios.Radio is not projectable on this build");
            }
            else
            {
                root.Set("radioControl", "winrt");
                root.Set("winrtRadios", states);
                bool on = false, found = false;
                for (int i = 0; i < states.Count; i++)
                {
                    J s = states.At(i);
                    if (string.Equals(s.S("kind", ""), "Bluetooth", StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        on = string.Equals(s.S("state", ""), "On", StringComparison.OrdinalIgnoreCase);
                    }
                }
                if (found) root.Set("enabled", on);
            }
            return root;
        }

        public static J SetRadio(bool on)
        {
            object btObj;
            Type rt;
            J states = RadioStates(out btObj, out rt);
            if (rt == null)
                throw new ApiFault("bt_radio_control_unavailable",
                    "Windows.Devices.Radios.Radio is not projectable on this build; there is no Win32 API to power a Bluetooth radio");
            if (btObj == null)
                throw new ApiFault("no_bt_radio", "no Bluetooth radio reported by Windows.Devices.Radios");

            // RadioState: 0 Unknown, 1 On, 2 Off, 3 Disabled
            Type stateType = WinRt.Type_("Windows.Devices.Radios.RadioState");
            object target;
            try { target = Enum.Parse(stateType, on ? "On" : "Off"); }
            catch (Exception ex) { throw new ApiFault("bt_radio_control_unavailable", "RadioState enum: " + Api.Describe(ex)); }

            string err;
            object result = WinRt.CallInstanceAsync(btObj, rt, "SetStateAsync", new object[] { target }, 10000, out err);

            J o = J.Obj();
            o.Set("requested", on ? "On" : "Off");
            o.Set("accessStatus", result == null ? null : result.ToString());
            if (err != null) o.Set("error", err);

            // Re-read rather than trust the return value.
            object btObj2;
            Type rt2;
            J after = RadioStates(out btObj2, out rt2);
            o.Set("radios", after);
            string nowState = btObj2 == null ? null : WinRt.PropStr(btObj2, rt2, "State");
            o.Set("state", nowState);
            bool applied = string.Equals(nowState, on ? "On" : "Off", StringComparison.OrdinalIgnoreCase);
            o.Set("applied", applied);

            if (!applied)
            {
                // "DeniedBySystem" / "DeniedByUser" here is the airplane-mode or policy case.
                throw new ApiFault("bt_radio_set_failed",
                    err != null ? err : ("radio state is still " + nowState + " (accessStatus=" +
                        (result == null ? "none" : result.ToString()) + ")"), o);
            }
            return o;
        }

        // Paired / connected devices. No inquiry, so this is fast (a few ms).
        public static J Devices(bool includeUnknown, int inquiryTimeoutMultiplier)
        {
            List<Radio> radios = FindRadios();
            try
            {
                if (radios.Count == 0)
                    throw new ApiFault("no_bt_radio", "no Bluetooth radio present on this machine");

                J arr = J.Arr();
                for (int ri = 0; ri < radios.Count; ri++)
                {
                    N.BLUETOOTH_DEVICE_SEARCH_PARAMS sp = new N.BLUETOOTH_DEVICE_SEARCH_PARAMS();
                    sp.dwSize = (uint)Marshal.SizeOf(typeof(N.BLUETOOTH_DEVICE_SEARCH_PARAMS));
                    sp.fReturnAuthenticated = 1;
                    sp.fReturnRemembered = 1;
                    sp.fReturnConnected = 1;
                    sp.fReturnUnknown = includeUnknown ? 1 : 0;
                    sp.fIssueInquiry = includeUnknown ? 1 : 0;
                    sp.cTimeoutMultiplier = (byte)Math.Max(1, Math.Min(48, inquiryTimeoutMultiplier));
                    sp.hRadio = radios[ri].H;

                    N.BLUETOOTH_DEVICE_INFO di = new N.BLUETOOTH_DEVICE_INFO();
                    di.dwSize = (uint)Marshal.SizeOf(typeof(N.BLUETOOTH_DEVICE_INFO));

                    IntPtr find = N.BluetoothFindFirstDevice(ref sp, ref di);
                    if (find == IntPtr.Zero) continue;
                    try
                    {
                        do
                        {
                            J o = J.Obj();
                            o.Set("name", di.szName);
                            o.Set("address", AddrText(di.Address));
                            o.Set("paired", di.fAuthenticated != 0);
                            o.Set("remembered", di.fRemembered != 0);
                            o.Set("connected", di.fConnected != 0);
                            o.Set("classOfDevice", di.ulClassofDevice);
                            o.Set("deviceClass", MajorClass(di.ulClassofDevice));
                            o.Set("looksLikeGamepad", LooksLikeGamepad(di.ulClassofDevice));
                            o.Set("lastSeen", SysTime(di.stLastSeen));
                            o.Set("lastUsed", SysTime(di.stLastUsed));
                            o.Set("radio", AddrText(radios[ri].Info.address));
                            arr.Add(o);

                            di = new N.BLUETOOTH_DEVICE_INFO();
                            di.dwSize = (uint)Marshal.SizeOf(typeof(N.BLUETOOTH_DEVICE_INFO));
                        } while (N.BluetoothFindNextDevice(find, ref di));
                    }
                    finally { N.BluetoothFindDeviceClose(find); }
                }

                J root = J.Obj();
                root.Set("devices", arr);
                root.Set("count", arr.Count);
                root.Set("inquiryPerformed", includeUnknown);
                return root;
            }
            finally { CloseRadios(radios); }
        }

        static string SysTime(N.SYSTEMTIME st)
        {
            if (st.wYear == 0) return null;
            try
            {
                DateTime d = new DateTime(st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond,
                    DateTimeKind.Utc);
                return d.ToString("o", CultureInfo.InvariantCulture);
            }
            catch { return null; }
        }

        // Inquiry is a radio-level sweep: 1.28 s per timeout unit, so this is always a job.
        public static J Scan(Jobs.Job job, int seconds)
        {
            int mult = Math.Max(1, Math.Min(48, (int)Math.Ceiling(seconds / 1.28)));
            job.Report(10, "bluetooth inquiry (" + mult + " x 1.28 s)");
            J r = Devices(true, mult);
            r.Set("inquirySeconds", J.Round(mult * 1.28, 2));
            return r;
        }

        public static J Unpair(string address)
        {
            ulong addr;
            if (!TryParseAddr(address, out addr))
                throw new ApiFault("bad_request", "address must be a MAC like AA:BB:CC:DD:EE:FF");
            uint rc = N.BluetoothRemoveDevice(ref addr);
            J o = J.Obj();
            o.Set("address", AddrText(addr));
            o.Set("removed", rc == 0);
            if (rc == 0) return o;
            if (rc == 574 /* ERROR_NOT_AUTHENTICATED */ || rc == 1168)
                throw new ApiFault("not_found", "no such paired device: " + Api.Win32(rc), o);
            if (rc == 5)
                throw new ApiFault("elevation_required",
                    "BluetoothRemoveDevice was denied - removing a pairing can require administrator rights on this build: " + Api.Win32(rc), o);
            throw new ApiFault("bt_unpair_failed", "BluetoothRemoveDevice failed: " + Api.Win32(rc), o);
        }

        // ---------------------------------------------------------------------------------------
        // Pairing. WinRT DeviceInformationPairing.PairAsync performs the whole ceremony including
        // enabling the device's services; the plain (non-Custom) overload silently accepts the
        // "ConfirmOnly" ceremony that a DualSense uses, so no UI prompt appears.
        //
        // The Win32 alternative is a dead end for a kiosk: BluetoothAuthenticateDeviceEx pairs, but
        // the HID service is not enabled until BluetoothSetServiceState is called, and that
        // REQUIRES ADMINISTRATOR RIGHTS. A standard-user shell would end up with a paired-but-dead
        // controller. That is why this path is WinRT.
        // ---------------------------------------------------------------------------------------
        public static J Pair(Jobs.Job job, string address, string name, int timeoutMs)
        {
            Type tDevInfo = WinRt.Type_("Windows.Devices.Enumeration.DeviceInformation");
            Type tBtDevice = WinRt.Type_("Windows.Devices.Bluetooth.BluetoothDevice");
            if (tDevInfo == null || tBtDevice == null)
                throw new ApiFault("bt_pair_unavailable",
                    "Windows.Devices.Enumeration / Windows.Devices.Bluetooth are not projectable on this build");

            ulong addr = 0;
            bool haveAddr = TryParseAddr(address, out addr);
            if (!haveAddr && string.IsNullOrEmpty(name))
                throw new ApiFault("bad_request", "address (MAC) or name is required");

            job.Report(10, "building device selector");
            string selector = null;
            try
            {
                MethodInfo sel = tBtDevice.GetMethod("GetDeviceSelectorFromPairingState",
                    BindingFlags.Public | BindingFlags.Static);
                if (sel != null) selector = (string)sel.Invoke(null, new object[] { false });
            }
            catch (Exception ex) { throw new ApiFault("bt_pair_unavailable", "GetDeviceSelectorFromPairingState: " + Api.Describe(ex)); }
            if (selector == null) throw new ApiFault("bt_pair_unavailable", "no Bluetooth device selector available");

            job.Report(25, "enumerating unpaired devices");
            string err;
            object found = WinRt.CallStaticAsync(tDevInfo, "FindAllAsync", new object[] { selector }, timeoutMs, out err);
            if (found == null)
                throw new ApiFault("bt_pair_failed", "DeviceInformation.FindAllAsync failed: " + (err == null ? "no result" : err));

            J seen = J.Arr();
            object match = null;
            string matchId = null, matchName = null;
            System.Collections.IEnumerable en = found as System.Collections.IEnumerable;
            if (en != null)
            {
                foreach (object di in en)
                {
                    string id = WinRt.PropStr(di, tDevInfo, "Id");
                    string dn = WinRt.PropStr(di, tDevInfo, "Name");
                    seen.Add(J.Obj().Set("id", id).Set("name", dn));
                    bool hit = false;
                    if (haveAddr && id != null)
                    {
                        // Device ids look like "Bluetooth#Bluetooth<local>-<remote>"; match the tail.
                        string want = AddrText(addr).Replace(":", "").ToLowerInvariant();
                        string flat = id.Replace(":", "").Replace("-", "").ToLowerInvariant();
                        if (flat.EndsWith(want, StringComparison.Ordinal)) hit = true;
                    }
                    if (!hit && !string.IsNullOrEmpty(name) && dn != null &&
                        dn.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) hit = true;
                    if (hit && match == null) { match = di; matchId = id; matchName = dn; }
                }
            }

            if (match == null)
                throw new ApiFault("bt_device_not_found",
                    "no unpaired Bluetooth device matched (is it in pairing mode?)",
                    J.Obj().Set("candidates", seen));

            job.Report(60, "pairing " + matchName);
            object pairing = WinRt.Prop(match, tDevInfo, "Pairing");
            if (pairing == null) throw new ApiFault("bt_pair_failed", "DeviceInformation.Pairing was null");
            Type tPairing = WinRt.Type_("Windows.Devices.Enumeration.DeviceInformationPairing");
            if (tPairing == null) throw new ApiFault("bt_pair_unavailable", "DeviceInformationPairing not projectable");

            object result = WinRt.CallInstanceAsync(pairing, tPairing, "PairAsync", null, timeoutMs, out err);
            J o = J.Obj();
            o.Set("id", matchId);
            o.Set("name", matchName);
            o.Set("candidates", seen);
            if (result == null)
                throw new ApiFault("bt_pair_failed", "PairAsync failed: " + (err == null ? "no result" : err), o);

            Type tResult = WinRt.Type_("Windows.Devices.Enumeration.DevicePairingResult");
            string status = WinRt.PropStr(result, tResult, "Status");
            o.Set("status", status);
            o.Set("paired", string.Equals(status, "Paired", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(status, "AlreadyPaired", StringComparison.OrdinalIgnoreCase));
            if (!o.B("paired", false))
                throw new ApiFault("bt_pair_failed", "pairing ceremony returned " + status, o);
            return o;
        }
    }

    #endregion

    #region System info / storage / time / locale / privileges

    public static class SysMod
    {
        public static J Info()
        {
            J o = J.Obj();
            o.Set("machineName", Environment.MachineName);
            try { o.Set("userName", Environment.UserDomainName + "\\" + Environment.UserName); }
            catch { o.Set("userName", Environment.UserName); }
            o.Set("elevated", Api.IsElevated());
            o.Set("sessionId", Process.GetCurrentProcess().SessionId);
            o.Set("processArchitecture", IntPtr.Size == 8 ? "x64" : "x86");
            o.Set("osArchitecture", Environment.Is64BitOperatingSystem ? "x64" : "x86");
            o.Set("clrVersion", Environment.Version.ToString());

            J os = J.Obj();
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (k != null)
                    {
                        os.Set("productName", Str(k, "ProductName"));
                        os.Set("edition", Str(k, "EditionID"));
                        os.Set("displayVersion", Str(k, "DisplayVersion"));
                        os.Set("releaseId", Str(k, "ReleaseId"));
                        os.Set("installationType", Str(k, "InstallationType"));
                        string build = Str(k, "CurrentBuild");
                        object ubr = k.GetValue("UBR");
                        os.Set("build", build);
                        os.Set("ubr", ubr == null ? 0 : Convert.ToInt64(ubr, CultureInfo.InvariantCulture));
                        os.Set("buildFull", build + (ubr == null ? "" : "." + ubr));
                        object install = k.GetValue("InstallDate");
                        if (install != null)
                        {
                            long secs = Convert.ToInt64(install, CultureInfo.InvariantCulture);
                            os.Set("installedUtc", new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                                .AddSeconds(secs).ToString("o", CultureInfo.InvariantCulture));
                        }
                    }
                }
            }
            catch (Exception ex) { os.Set("registryError", Api.Describe(ex)); }
            try
            {
                N.OSVERSIONINFOEX vi = new N.OSVERSIONINFOEX();
                vi.dwOSVersionInfoSize = (uint)Marshal.SizeOf(typeof(N.OSVERSIONINFOEX));
                if (N.RtlGetVersion(ref vi) == 0)
                    os.Set("version", vi.dwMajorVersion + "." + vi.dwMinorVersion + "." + vi.dwBuildNumber);
            }
            catch { }
            if (!os.Has("version"))
            {
                Version v = Environment.OSVersion.Version;
                os.Set("version", v.Major + "." + v.Minor + "." + v.Build);
                os.Set("versionNote", "from Environment.OSVersion - subject to the app-compat shim");
            }
            o.Set("os", os);

            J cpu = J.Obj();
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    if (k != null)
                    {
                        cpu.Set("name", Trim(Str(k, "ProcessorNameString")));
                        cpu.Set("identifier", Str(k, "Identifier"));
                        cpu.Set("vendor", Str(k, "VendorIdentifier"));
                        object mhz = k.GetValue("~MHz");
                        if (mhz != null) cpu.Set("baseMhz", Convert.ToInt64(mhz, CultureInfo.InvariantCulture));
                    }
                }
            }
            catch (Exception ex) { cpu.Set("registryError", Api.Describe(ex)); }
            cpu.Set("logicalProcessors", Environment.ProcessorCount);
            o.Set("cpu", cpu);

            J mem = J.Obj();
            N.MEMORYSTATUSEX ms = new N.MEMORYSTATUSEX();
            ms.dwLength = (uint)Marshal.SizeOf(typeof(N.MEMORYSTATUSEX));
            if (N.GlobalMemoryStatusEx(ref ms))
            {
                mem.Set("totalBytes", ms.ullTotalPhys);
                mem.Set("availableBytes", ms.ullAvailPhys);
                mem.Set("usedPercent", ms.dwMemoryLoad);
                mem.Set("totalGB", J.Round(ms.ullTotalPhys / 1073741824.0, 2));
                mem.Set("availableGB", J.Round(ms.ullAvailPhys / 1073741824.0, 2));
            }
            else mem.Set("error", Api.Win32((uint)Marshal.GetLastWin32Error()));
            o.Set("memory", mem);

            ulong ticks = N.GetTickCount64();
            o.Set("uptimeMs", (long)ticks);
            o.Set("uptimeText", Duration(TimeSpan.FromMilliseconds(ticks)));
            o.Set("bootTimeUtc", DateTime.UtcNow.AddMilliseconds(-(double)ticks).ToString("o", CultureInfo.InvariantCulture));
            return o;
        }

        static string Str(Microsoft.Win32.RegistryKey k, string name)
        {
            object v = k.GetValue(name);
            return v == null ? null : v.ToString();
        }

        static string Trim(string s) { return s == null ? null : s.Trim(); }

        public static string Duration(TimeSpan t)
        {
            if (t.TotalDays >= 1)
                return (int)t.TotalDays + "d " + t.Hours + "h " + t.Minutes + "m";
            if (t.TotalHours >= 1) return t.Hours + "h " + t.Minutes + "m";
            return t.Minutes + "m " + t.Seconds + "s";
        }

        public static J Storage()
        {
            J arr = J.Arr();
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch (Exception ex) { throw new ApiFault("storage_failed", Api.Describe(ex)); }

            for (int i = 0; i < drives.Length; i++)
            {
                DriveInfo d = drives[i];
                J o = J.Obj();
                o.Set("name", d.Name);
                uint dt = 0;
                try { dt = N.GetDriveType(d.Name); }
                catch { }
                o.Set("driveType", DriveTypeName(dt));
                o.Set("removable", dt == 2);
                o.Set("fixed", dt == 3);
                o.Set("network", dt == 4);
                o.Set("optical", dt == 5);
                bool ready = false;
                try { ready = d.IsReady; }
                catch { }
                o.Set("ready", ready);
                if (ready)
                {
                    try
                    {
                        o.Set("label", d.VolumeLabel);
                        o.Set("fileSystem", d.DriveFormat);
                        o.Set("totalBytes", d.TotalSize);
                        o.Set("freeBytes", d.TotalFreeSpace);
                        o.Set("availableBytes", d.AvailableFreeSpace);
                        o.Set("usedBytes", d.TotalSize - d.TotalFreeSpace);
                        o.Set("totalGB", J.Round(d.TotalSize / 1073741824.0, 2));
                        o.Set("freeGB", J.Round(d.TotalFreeSpace / 1073741824.0, 2));
                        o.Set("usedPercent", d.TotalSize > 0
                            ? (int)Math.Round((d.TotalSize - d.TotalFreeSpace) * 100.0 / d.TotalSize) : 0);
                    }
                    catch (Exception ex) { o.Set("error", Api.Describe(ex)); }
                }
                arr.Add(o);
            }
            J root = J.Obj();
            root.Set("drives", arr);
            root.Set("count", arr.Count);
            return root;
        }

        static string DriveTypeName(uint t)
        {
            switch (t)
            {
                case 0: return "unknown";
                case 1: return "noRootDir";
                case 2: return "removable";
                case 3: return "fixed";
                case 4: return "network";
                case 5: return "cdrom";
                case 6: return "ramdisk";
                default: return "unknown";
            }
        }

        public static J Time()
        {
            J o = J.Obj();
            DateTime now = DateTime.Now;
            o.Set("localIso", now.ToString("o", CultureInfo.InvariantCulture));
            o.Set("utcIso", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            o.Set("unixSeconds", (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
            try
            {
                o.Set("localFormatted", now.ToString("F", CultureInfo.CurrentCulture));
                o.Set("timeFormatted", now.ToString("t", CultureInfo.CurrentCulture));
                o.Set("dateFormatted", now.ToString("D", CultureInfo.CurrentCulture));
            }
            catch { }

            J tz = J.Obj();
            try
            {
                TimeZoneInfo z = TimeZoneInfo.Local;
                tz.Set("id", z.Id);
                tz.Set("displayName", z.DisplayName);
                tz.Set("standardName", z.StandardName);
                tz.Set("daylightName", z.DaylightName);
                tz.Set("baseUtcOffsetMinutes", (int)z.BaseUtcOffset.TotalMinutes);
                tz.Set("currentUtcOffsetMinutes", (int)z.GetUtcOffset(now).TotalMinutes);
                tz.Set("inDaylightSaving", z.IsDaylightSavingTime(now));
                tz.Set("supportsDaylightSaving", z.SupportsDaylightSavingTime);
            }
            catch (Exception ex) { tz.Set("error", Api.Describe(ex)); }
            N.DYNAMIC_TIME_ZONE_INFORMATION dtz;
            uint tzr = N.GetDynamicTimeZoneInformation(out dtz);
            if (tzr != 0xFFFFFFFF)
            {
                tz.Set("keyName", dtz.TimeZoneKeyName);
                tz.Set("dynamicDstDisabled", dtz.DynamicDaylightTimeDisabled);
            }
            o.Set("timeZone", tz);

            // NTP / automatic time. All read-only registry + service query, no elevation.
            J ntp = J.Obj();
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\W32Time\Parameters"))
                {
                    if (k != null)
                    {
                        string type = Str(k, "Type");
                        ntp.Set("syncType", type);      // NTP | NT5DS | NoSync | AllSync
                        ntp.Set("server", Str(k, "NtpServer"));
                        ntp.Set("autoSync", string.Equals(type, "NTP", StringComparison.OrdinalIgnoreCase) ||
                                            string.Equals(type, "NT5DS", StringComparison.OrdinalIgnoreCase) ||
                                            string.Equals(type, "AllSync", StringComparison.OrdinalIgnoreCase));
                    }
                }
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\W32Time\Config"))
                {
                    if (k != null)
                    {
                        object lkg = k.GetValue("LastKnownGoodTime");
                        if (lkg != null)
                        {
                            long ft = Convert.ToInt64(lkg, CultureInfo.InvariantCulture);
                            if (ft > 0)
                                ntp.Set("lastKnownGoodUtc", DateTime.FromFileTimeUtc(ft).ToString("o", CultureInfo.InvariantCulture));
                        }
                    }
                }
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\W32Time\TimeProviders\NtpClient"))
                {
                    if (k != null)
                    {
                        object en = k.GetValue("Enabled");
                        if (en != null) ntp.Set("ntpClientEnabled", Convert.ToInt32(en, CultureInfo.InvariantCulture) != 0);
                        object poll = k.GetValue("SpecialPollInterval");
                        if (poll != null) ntp.Set("pollIntervalSeconds", Convert.ToInt64(poll, CultureInfo.InvariantCulture));
                    }
                }
                ntp.Set("serviceState", ServiceState("W32Time"));
            }
            catch (Exception ex) { ntp.Set("error", Api.Describe(ex)); }
            o.Set("ntp", ntp);
            return o;
        }

        static string ServiceState(string name)
        {
            IntPtr scm = IntPtr.Zero, svc = IntPtr.Zero;
            try
            {
                scm = N.OpenSCManager(null, null, N.SC_MANAGER_CONNECT);
                if (scm == IntPtr.Zero) return "unknown (OpenSCManager: " + Api.Win32((uint)Marshal.GetLastWin32Error()) + ")";
                svc = N.OpenService(scm, name, N.SERVICE_QUERY_STATUS);
                if (svc == IntPtr.Zero) return "unknown (OpenService: " + Api.Win32((uint)Marshal.GetLastWin32Error()) + ")";
                N.SERVICE_STATUS st = new N.SERVICE_STATUS();
                if (!N.QueryServiceStatus(svc, ref st)) return "unknown";
                switch (st.dwCurrentState)
                {
                    case 1: return "stopped";
                    case 2: return "startPending";
                    case 3: return "stopPending";
                    case 4: return "running";
                    case 5: return "continuePending";
                    case 6: return "pausePending";
                    case 7: return "paused";
                    default: return "unknown";
                }
            }
            catch { return "unknown"; }
            finally
            {
                if (svc != IntPtr.Zero) N.CloseServiceHandle(svc);
                if (scm != IntPtr.Zero) N.CloseServiceHandle(scm);
            }
        }

        public static J TimeZones()
        {
            J arr = J.Arr();
            try
            {
                System.Collections.ObjectModel.ReadOnlyCollection<TimeZoneInfo> zones = TimeZoneInfo.GetSystemTimeZones();
                foreach (TimeZoneInfo z in zones)
                {
                    J o = J.Obj();
                    o.Set("id", z.Id);
                    o.Set("displayName", z.DisplayName);
                    o.Set("baseUtcOffsetMinutes", (int)z.BaseUtcOffset.TotalMinutes);
                    o.Set("supportsDaylightSaving", z.SupportsDaylightSavingTime);
                    arr.Add(o);
                }
            }
            catch (Exception ex) { throw new ApiFault("timezones_failed", Api.Describe(ex)); }
            J root = J.Obj();
            root.Set("zones", arr);
            root.Set("count", arr.Count);
            try { root.Set("current", TimeZoneInfo.Local.Id); }
            catch { }
            return root;
        }

        // Needs SeTimeZonePrivilege, which the Users group holds by default on a workstation, so
        // this normally works for a standard user without elevation.
        public static J SetTimeZone(string id)
        {
            if (string.IsNullOrEmpty(id)) throw new ApiFault("bad_request", "id is required (see sys.timezones)");

            J o = J.Obj();
            string before = null;
            try { before = TimeZoneInfo.Local.Id; }
            catch { }
            o.Set("before", before);
            o.Set("requested", id);

            // Verify the zone exists before touching anything.
            try { TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (Exception ex) { throw new ApiFault("no_such_timezone", "unknown time zone id '" + id + "': " + ex.Message, o); }

            string privErr = N.EnablePrivilege("SeTimeZonePrivilege");
            o.Set("privilege", privErr == null ? "SeTimeZonePrivilege enabled" : privErr);
            if (privErr != null)
                throw new ApiFault("privilege_unavailable",
                    "cannot enable SeTimeZonePrivilege (normally granted to Users): " + privErr, o);

            N.DYNAMIC_TIME_ZONE_INFORMATION tz;
            if (!BuildTzi(id, out tz))
                throw new ApiFault("timezone_registry_missing",
                    "no registry entry under 'Time Zones' for '" + id + "'", o);

            bool ok = N.SetDynamicTimeZoneInformation(ref tz);
            if (!ok)
            {
                int le = Marshal.GetLastWin32Error();
                o.Set("win32", Api.Win32((uint)le));
                throw new ApiFault("timezone_set_failed", "SetDynamicTimeZoneInformation failed: " + Api.Win32((uint)le), o);
            }

            TimeZoneInfo.ClearCachedData();
            string after = null;
            try { after = TimeZoneInfo.Local.Id; }
            catch { }
            o.Set("after", after);
            o.Set("applied", string.Equals(after, id, StringComparison.OrdinalIgnoreCase));
            o.Set("localIso", DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
            if (!o.B("applied", false))
                throw new ApiFault("timezone_set_failed", "call succeeded but the active zone is still " + after, o);
            return o;
        }

        // The registry TZI blob is the only locale-independent source for the transition rules.
        static bool BuildTzi(string id, out N.DYNAMIC_TIME_ZONE_INFORMATION tz)
        {
            tz = new N.DYNAMIC_TIME_ZONE_INFORMATION();
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Time Zones\" + id))
            {
                if (k == null) return false;
                byte[] tzi = k.GetValue("TZI") as byte[];
                if (tzi == null || tzi.Length < 44) return false;
                tz.Bias = BitConverter.ToInt32(tzi, 0);
                tz.StandardBias = BitConverter.ToInt32(tzi, 4);
                tz.DaylightBias = BitConverter.ToInt32(tzi, 8);
                tz.StandardDate = ReadSysTime(tzi, 12);
                tz.DaylightDate = ReadSysTime(tzi, 28);
                tz.StandardName = Str(k, "Std") ?? "";
                tz.DaylightName = Str(k, "Dlt") ?? "";
                tz.TimeZoneKeyName = id;
                tz.DynamicDaylightTimeDisabled = false;
                return true;
            }
        }

        static N.SYSTEMTIME ReadSysTime(byte[] b, int off)
        {
            N.SYSTEMTIME s = new N.SYSTEMTIME();
            s.wYear = BitConverter.ToUInt16(b, off + 0);
            s.wMonth = BitConverter.ToUInt16(b, off + 2);
            s.wDayOfWeek = BitConverter.ToUInt16(b, off + 4);
            s.wDay = BitConverter.ToUInt16(b, off + 6);
            s.wHour = BitConverter.ToUInt16(b, off + 8);
            s.wMinute = BitConverter.ToUInt16(b, off + 10);
            s.wSecond = BitConverter.ToUInt16(b, off + 12);
            s.wMilliseconds = BitConverter.ToUInt16(b, off + 14);
            return s;
        }

        public static J Locale()
        {
            J o = J.Obj();
            try
            {
                CultureInfo c = CultureInfo.CurrentCulture;
                o.Set("culture", c.Name);
                o.Set("cultureDisplayName", c.DisplayName);
                o.Set("cultureEnglishName", c.EnglishName);
                o.Set("uiCulture", CultureInfo.CurrentUICulture.Name);
                o.Set("installedUiCulture", CultureInfo.InstalledUICulture.Name);
                o.Set("isRightToLeft", c.TextInfo.IsRightToLeft);
                o.Set("shortDatePattern", c.DateTimeFormat.ShortDatePattern);
                o.Set("longDatePattern", c.DateTimeFormat.LongDatePattern);
                o.Set("shortTimePattern", c.DateTimeFormat.ShortTimePattern);
                o.Set("firstDayOfWeek", c.DateTimeFormat.FirstDayOfWeek.ToString());
                o.Set("uses24HourClock", c.DateTimeFormat.ShortTimePattern.IndexOf('H') >= 0);
                o.Set("decimalSeparator", c.NumberFormat.NumberDecimalSeparator);
                o.Set("listSeparator", c.TextInfo.ListSeparator);
            }
            catch (Exception ex) { o.Set("cultureError", Api.Describe(ex)); }

            try
            {
                StringBuilder sb = new StringBuilder(96);
                if (N.GetUserDefaultLocaleName(sb, sb.Capacity) > 0) o.Set("userDefaultLocale", sb.ToString());
                sb.Length = 0;
                if (N.GetSystemDefaultLocaleName(sb, sb.Capacity) > 0) o.Set("systemDefaultLocale", sb.ToString());
            }
            catch { }

            try
            {
                RegionInfo r = RegionInfo.CurrentRegion;
                J reg = J.Obj();
                reg.Set("name", r.Name);
                reg.Set("englishName", r.EnglishName);
                reg.Set("displayName", r.DisplayName);
                reg.Set("twoLetterIsoRegionName", r.TwoLetterISORegionName);
                reg.Set("currencySymbol", r.CurrencySymbol);
                reg.Set("isoCurrencySymbol", r.ISOCurrencySymbol);
                reg.Set("isMetric", r.IsMetric);
                o.Set("region", reg);
            }
            catch (Exception ex) { o.Set("regionError", Api.Describe(ex)); }

            try
            {
                int geo = N.GetUserGeoID(16 /* GEOCLASS_NATION */);
                o.Set("geoId", geo);
                StringBuilder sb = new StringBuilder(128);
                if (N.GetGeoInfo(geo, 8 /* GEO_FRIENDLYNAME */, sb, sb.Capacity, 0) > 0) o.Set("country", sb.ToString());
                sb.Length = 0;
                if (N.GetGeoInfo(geo, 4 /* GEO_ISO2 */, sb, sb.Capacity, 0) > 0) o.Set("countryIso2", sb.ToString());
            }
            catch { }

            J kb = J.Arr();
            try
            {
                // Installed input languages, straight from the user's Keyboard Layout preload list.
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Keyboard Layout\Preload"))
                {
                    if (k != null)
                    {
                        string[] names = k.GetValueNames();
                        for (int i = 0; i < names.Length; i++)
                        {
                            string hex = Str(k, names[i]);
                            int lcid;
                            if (hex != null && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out lcid))
                            {
                                try { kb.Add(new CultureInfo(lcid & 0xFFFF).Name); }
                                catch { kb.Add(hex); }
                            }
                        }
                    }
                }
            }
            catch { }
            o.Set("inputLanguages", kb);
            return o;
        }

        // What can this process actually do? The single most useful diagnostic for the kiosk.
        public static J Privileges()
        {
            J o = J.Obj();
            o.Set("elevated", Api.IsElevated());
            try
            {
                using (System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    o.Set("user", id.Name);
                    o.Set("sid", id.User == null ? null : id.User.Value);
                    o.Set("authenticationType", id.AuthenticationType);
                    o.Set("isSystem", id.IsSystem);
                }
            }
            catch (Exception ex) { o.Set("identityError", Api.Describe(ex)); }

            J privs = J.Arr();
            IntPtr token = IntPtr.Zero;
            try
            {
                if (N.OpenProcessToken(N.GetCurrentProcess(), N.TOKEN_QUERY, out token))
                {
                    int len = 0;
                    N.GetTokenInformation(token, 3 /* TokenPrivileges */, IntPtr.Zero, 0, out len);
                    if (len > 0)
                    {
                        IntPtr buf = Marshal.AllocHGlobal(len);
                        try
                        {
                            if (N.GetTokenInformation(token, 3, buf, len, out len))
                            {
                                int count = Marshal.ReadInt32(buf, 0);
                                for (int i = 0; i < count; i++)
                                {
                                    IntPtr rec = new IntPtr(buf.ToInt64() + 4 + i * 12);
                                    N.LUID luid = new N.LUID();
                                    luid.LowPart = (uint)Marshal.ReadInt32(rec, 0);
                                    luid.HighPart = Marshal.ReadInt32(rec, 4);
                                    uint attrs = (uint)Marshal.ReadInt32(rec, 8);
                                    int nameLen = 256;
                                    StringBuilder sb = new StringBuilder(nameLen);
                                    if (N.LookupPrivilegeName(null, ref luid, sb, ref nameLen))
                                    {
                                        J p = J.Obj();
                                        p.Set("name", sb.ToString());
                                        p.Set("enabled", (attrs & N.SE_PRIVILEGE_ENABLED) != 0);
                                        p.Set("enabledByDefault", (attrs & 0x1) != 0);
                                        privs.Add(p);
                                    }
                                }
                            }
                        }
                        finally { Marshal.FreeHGlobal(buf); }
                    }
                }
            }
            catch (Exception ex) { o.Set("privilegeError", Api.Describe(ex)); }
            finally { if (token != IntPtr.Zero) N.CloseHandle(token); }
            o.Set("privileges", privs);

            // Quick verdict on the privileges this API actually depends on.
            J needs = J.Obj();
            needs.Set("SeShutdownPrivilege", Held(privs, "SeShutdownPrivilege"));
            needs.Set("SeTimeZonePrivilege", Held(privs, "SeTimeZonePrivilege"));
            needs.Set("SeSystemtimePrivilege", Held(privs, "SeSystemtimePrivilege"));
            o.Set("relevantPrivileges", needs);
            return o;
        }

        static bool Held(J privs, string name)
        {
            for (int i = 0; i < privs.Count; i++)
                if (string.Equals(privs.At(i).S("name", ""), name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    #endregion

    #region Accounts (read-only)

    public static class AccountsMod
    {
        public static J List()
        {
            J arr = J.Arr();
            J root = J.Obj();

            HashSet<string> admins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string adminGroup = WellKnownGroupName(N.WinBuiltinAdministratorsSid);
            root.Set("administratorsGroup", adminGroup);
            if (adminGroup != null)
            {
                string gerr;
                List<string> members = GroupMembers(adminGroup, out gerr);
                for (int i = 0; i < members.Count; i++)
                {
                    admins.Add(members[i]);
                    int slash = members[i].LastIndexOf('\\');
                    if (slash >= 0) admins.Add(members[i].Substring(slash + 1));
                }
                if (gerr != null) root.Set("groupError", gerr);
                root.Set("administrators", ToArr(members));
            }

            Dictionary<string, string> loggedIn = LoggedInUsers();
            J sessions = J.Arr();
            foreach (KeyValuePair<string, string> kv in loggedIn)
                sessions.Add(J.Obj().Set("user", kv.Key).Set("session", kv.Value));
            root.Set("sessions", sessions);

            IntPtr buf = IntPtr.Zero;
            try
            {
                uint read, total, resume = 0;
                uint rc = N.NetUserEnum(null, 3, N.FILTER_NORMAL_ACCOUNT, out buf, 0xFFFFFFFF,
                    out read, out total, ref resume);
                if (rc != 0)
                {
                    if (rc == 5)
                        throw new ApiFault("access_denied",
                            "NetUserEnum was denied for this user: " + Api.Win32(rc));
                    throw new ApiFault("accounts_failed", "NetUserEnum failed: " + Api.Win32(rc));
                }
                int stride = Marshal.SizeOf(typeof(N.USER_INFO_3));
                for (int i = 0; i < read; i++)
                {
                    IntPtr item = new IntPtr(buf.ToInt64() + (long)i * stride);
                    N.USER_INFO_3 u = (N.USER_INFO_3)Marshal.PtrToStructure(item, typeof(N.USER_INFO_3));
                    J o = J.Obj();
                    o.Set("name", u.usri3_name);
                    o.Set("fullName", string.IsNullOrEmpty(u.usri3_full_name) ? null : u.usri3_full_name);
                    o.Set("comment", string.IsNullOrEmpty(u.usri3_comment) ? null : u.usri3_comment);
                    o.Set("enabled", (u.usri3_flags & N.UF_ACCOUNTDISABLE) == 0);
                    o.Set("lockedOut", (u.usri3_flags & N.UF_LOCKOUT) != 0);
                    o.Set("passwordRequired", (u.usri3_flags & N.UF_PASSWD_NOTREQD) == 0);
                    o.Set("passwordNeverExpires", (u.usri3_flags & N.UF_DONT_EXPIRE_PASSWD) != 0);
                    o.Set("passwordExpired", u.usri3_password_expired != 0);
                    o.Set("privilegeLevel", u.usri3_priv == N.USER_PRIV_ADMIN ? "admin" :
                                            (u.usri3_priv == N.USER_PRIV_GUEST ? "guest" : "user"));
                    o.Set("rid", u.usri3_user_id);
                    o.Set("isAdministrator", u.usri3_priv == N.USER_PRIV_ADMIN || admins.Contains(u.usri3_name));
                    o.Set("profilePath", string.IsNullOrEmpty(u.usri3_profile) ? null : u.usri3_profile);
                    o.Set("homeDirectory", string.IsNullOrEmpty(u.usri3_home_dir) ? null : u.usri3_home_dir);
                    o.Set("numLogons", u.usri3_num_logons);
                    if (u.usri3_last_logon > 0)
                        o.Set("lastLogonUtc", new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                            .AddSeconds(u.usri3_last_logon).ToString("o", CultureInfo.InvariantCulture));
                    o.Set("signedIn", loggedIn.ContainsKey(u.usri3_name));
                    o.Set("isCurrentUser", string.Equals(u.usri3_name, Environment.UserName, StringComparison.OrdinalIgnoreCase));
                    arr.Add(o);
                }
            }
            finally { if (buf != IntPtr.Zero) N.NetApiBufferFree(buf); }

            root.Set("users", arr);
            root.Set("count", arr.Count);
            root.Set("currentUser", Environment.UserName);
            root.Set("readOnly", true);
            root.Set("note", "account creation/deletion is deliberately not implemented");
            return root;
        }

        static J ToArr(List<string> items)
        {
            J a = J.Arr();
            for (int i = 0; i < items.Count; i++) a.Add(items[i]);
            return a;
        }

        // Resolve S-1-5-32-544 etc. to the localised group name so this works on any language.
        static string WellKnownGroupName(int wellKnown)
        {
            IntPtr sid = IntPtr.Zero;
            try
            {
                uint cb = 68; // SECURITY_MAX_SID_SIZE
                sid = Marshal.AllocHGlobal((int)cb);
                if (!N.CreateWellKnownSid(wellKnown, IntPtr.Zero, sid, ref cb)) return null;
                uint cchName = 256, cchDomain = 256;
                StringBuilder name = new StringBuilder((int)cchName);
                StringBuilder domain = new StringBuilder((int)cchDomain);
                int use;
                if (!N.LookupAccountSid(null, sid, name, ref cchName, domain, ref cchDomain, out use)) return null;
                return name.ToString();
            }
            catch { return null; }
            finally { if (sid != IntPtr.Zero) Marshal.FreeHGlobal(sid); }
        }

        static List<string> GroupMembers(string group, out string error)
        {
            error = null;
            List<string> list = new List<string>();
            IntPtr buf = IntPtr.Zero;
            IntPtr resume = IntPtr.Zero;
            try
            {
                uint read, total;
                uint rc = N.NetLocalGroupGetMembers(null, group, 1, out buf, 0xFFFFFFFF, out read, out total, ref resume);
                if (rc != 0) { error = "NetLocalGroupGetMembers(" + group + "): " + Api.Win32(rc); return list; }
                int stride = Marshal.SizeOf(typeof(N.LOCALGROUP_MEMBERS_INFO_1));
                for (int i = 0; i < read; i++)
                {
                    IntPtr item = new IntPtr(buf.ToInt64() + (long)i * stride);
                    N.LOCALGROUP_MEMBERS_INFO_1 m =
                        (N.LOCALGROUP_MEMBERS_INFO_1)Marshal.PtrToStructure(item, typeof(N.LOCALGROUP_MEMBERS_INFO_1));
                    if (!string.IsNullOrEmpty(m.lgrmi1_name)) list.Add(m.lgrmi1_name);
                }
            }
            catch (Exception ex) { error = Api.Describe(ex); }
            finally { if (buf != IntPtr.Zero) N.NetApiBufferFree(buf); }
            return list;
        }

        static Dictionary<string, string> LoggedInUsers()
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            IntPtr sessions = IntPtr.Zero;
            try
            {
                int count;
                if (!N.WTSEnumerateSessions(IntPtr.Zero, 0, 1, out sessions, out count)) return map;
                int stride = Marshal.SizeOf(typeof(N.WTS_SESSION_INFO));
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = new IntPtr(sessions.ToInt64() + (long)i * stride);
                    N.WTS_SESSION_INFO si = (N.WTS_SESSION_INFO)Marshal.PtrToStructure(item, typeof(N.WTS_SESSION_INFO));
                    IntPtr nameBuf;
                    int bytes;
                    if (N.WTSQuerySessionInformation(IntPtr.Zero, si.SessionId, N.WTSUserName, out nameBuf, out bytes))
                    {
                        try
                        {
                            string user = Marshal.PtrToStringUni(nameBuf);
                            if (!string.IsNullOrEmpty(user))
                                map[user] = si.pWinStationName + " (" + si.SessionId + ", " + WtsState(si.State) + ")";
                        }
                        finally { N.WTSFreeMemory(nameBuf); }
                    }
                }
            }
            catch { }
            finally { if (sessions != IntPtr.Zero) N.WTSFreeMemory(sessions); }
            return map;
        }

        static string WtsState(int s)
        {
            switch (s)
            {
                case 0: return "active";
                case 1: return "connected";
                case 2: return "connectQuery";
                case 3: return "shadow";
                case 4: return "disconnected";
                case 5: return "idle";
                case 6: return "listen";
                case 7: return "reset";
                case 8: return "down";
                case 9: return "init";
                default: return "unknown";
            }
        }
    }

    #endregion

    #region Windows Update (WUA COM, late bound)

    // There is no interop assembly for WUAPI on this toolchain, so everything goes through
    // Type.GetTypeFromProgID + IDispatch. WUA's objects are all dual interfaces, so late binding
    // is fully supported and there are no vtable assumptions.
    //
    // Search is slow (typically 10-120 s against Windows Update) and install is slower still, so
    // both are jobs. INSTALL REQUIRES ADMINISTRATOR RIGHTS - see the catalog.
    public static class UpdatesMod
    {
        static readonly object Gate = new object();
        static object _lastSearchResult;      // ISearchResult
        static List<object> _lastUpdates;     // IUpdate[]
        static DateTime _lastSearchUtc;

        static object New(string progId)
        {
            Type t = Type.GetTypeFromProgID(progId, false);
            if (t == null) throw new ApiFault("wua_unavailable", "COM ProgID '" + progId + "' is not registered");
            try { return Activator.CreateInstance(t); }
            catch (Exception ex) { throw new ApiFault("wua_unavailable", "cannot create " + progId + ": " + Api.Describe(ex)); }
        }

        static object Call(object o, string name, params object[] args)
        {
            return o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, args, CultureInfo.InvariantCulture);
        }

        static object Get(object o, string name)
        {
            return o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, null, CultureInfo.InvariantCulture);
        }

        static object GetIndexed(object o, string name, int index)
        {
            return o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o,
                new object[] { index }, CultureInfo.InvariantCulture);
        }

        static void Put(object o, string name, object value)
        {
            o.GetType().InvokeMember(name, BindingFlags.SetProperty, null, o,
                new object[] { value }, CultureInfo.InvariantCulture);
        }

        static string Str(object o) { return o == null ? null : o.ToString(); }

        public static J Check(Jobs.Job job, string criteria, bool online)
        {
            job.Report(5, "creating update session");
            object session = New("Microsoft.Update.Session");
            try
            {
                try { Put(session, "ClientApplicationID", "ARC OS Settings"); }
                catch { }

                object searcher = Call(session, "CreateUpdateSearcher");
                try { Put(searcher, "Online", online); }
                catch { }

                string q = string.IsNullOrEmpty(criteria) ? "IsInstalled=0 AND IsHidden=0" : criteria;
                job.Report(15, online ? "searching Windows Update (online)" : "searching cached catalogue");

                object result;
                try { result = Call(searcher, "Search", q); }
                catch (Exception ex)
                {
                    throw new ApiFault("update_search_failed",
                        "IUpdateSearcher.Search failed: " + Api.Describe(ex) +
                        (Api.IsElevated() ? "" : " (note: this process is NOT elevated)"));
                }

                job.Report(80, "reading results");
                object updates = Get(result, "Updates");
                int count = Convert.ToInt32(Get(updates, "Count"), CultureInfo.InvariantCulture);

                List<object> keep = new List<object>();
                J arr = J.Arr();
                for (int i = 0; i < count; i++)
                {
                    object u = GetIndexed(updates, "Item", i);
                    keep.Add(u);
                    arr.Add(Describe(u, i));
                }

                lock (Gate)
                {
                    _lastSearchResult = result;
                    _lastUpdates = keep;
                    _lastSearchUtc = DateTime.UtcNow;
                }

                J o = J.Obj();
                o.Set("count", count);
                o.Set("updates", arr);
                o.Set("criteria", q);
                o.Set("online", online);
                o.Set("searchedUtc", _lastSearchUtc.ToString("o", CultureInfo.InvariantCulture));
                try { o.Set("resultCode", ResultCodeName(Convert.ToInt32(Get(result, "ResultCode"), CultureInfo.InvariantCulture))); }
                catch { }
                o.Set("elevated", Api.IsElevated());
                if (!Api.IsElevated())
                    o.Set("note", "search works as a standard user; updates.install does NOT - it needs elevation");
                return o;
            }
            finally
            {
                if (session != null && Marshal.IsComObject(session))
                {
                    // The session is not released - the cached IUpdate objects belong to it and
                    // updates.install needs them. It is freed when a later search replaces it.
                }
            }
        }

        static J Describe(object u, int index)
        {
            J o = J.Obj();
            o.Set("index", index);
            try { o.Set("title", Str(Get(u, "Title"))); }
            catch { }
            try { o.Set("id", Str(Get(Get(u, "Identity"), "UpdateID"))); }
            catch { }
            try { o.Set("revision", Convert.ToInt32(Get(Get(u, "Identity"), "RevisionNumber"), CultureInfo.InvariantCulture)); }
            catch { }
            try { o.Set("description", Str(Get(u, "Description"))); }
            catch { }
            try { o.Set("mandatory", Convert.ToBoolean(Get(u, "IsMandatory"), CultureInfo.InvariantCulture)); }
            catch { }
            try { o.Set("downloaded", Convert.ToBoolean(Get(u, "IsDownloaded"), CultureInfo.InvariantCulture)); }
            catch { }
            try { o.Set("rebootRequired", Convert.ToBoolean(Get(u, "RebootRequired"), CultureInfo.InvariantCulture)); }
            catch { }
            try { o.Set("eulaAccepted", Convert.ToBoolean(Get(u, "EulaAccepted"), CultureInfo.InvariantCulture)); }
            catch { }
            try { o.Set("sizeBytes", Convert.ToInt64(Get(u, "MaxDownloadSize"), CultureInfo.InvariantCulture)); }
            catch { }
            try
            {
                object cats = Get(u, "Categories");
                int n = Convert.ToInt32(Get(cats, "Count"), CultureInfo.InvariantCulture);
                J ca = J.Arr();
                for (int i = 0; i < n; i++) ca.Add(Str(Get(GetIndexed(cats, "Item", i), "Name")));
                o.Set("categories", ca);
            }
            catch { }
            try
            {
                object kbs = Get(u, "KBArticleIDs");
                int n = Convert.ToInt32(Get(kbs, "Count"), CultureInfo.InvariantCulture);
                J ka = J.Arr();
                for (int i = 0; i < n; i++) ka.Add("KB" + Str(GetIndexed(kbs, "Item", i)));
                o.Set("kb", ka);
            }
            catch { }
            try { o.Set("severity", Str(Get(u, "MsrcSeverity"))); }
            catch { }
            return o;
        }

        static string ResultCodeName(int c)
        {
            switch (c)
            {
                case 0: return "notStarted";
                case 1: return "inProgress";
                case 2: return "succeeded";
                case 3: return "succeededWithErrors";
                case 4: return "failed";
                case 5: return "aborted";
                default: return "unknown";
            }
        }

        public static J ListCached()
        {
            lock (Gate)
            {
                if (_lastUpdates == null)
                    throw new ApiFault("no_search_yet", "run updates.check first (it is a job; poll job.status)");
                J arr = J.Arr();
                for (int i = 0; i < _lastUpdates.Count; i++) arr.Add(Describe(_lastUpdates[i], i));
                J o = J.Obj();
                o.Set("count", arr.Count);
                o.Set("updates", arr);
                o.Set("searchedUtc", _lastSearchUtc.ToString("o", CultureInfo.InvariantCulture));
                o.Set("ageSeconds", (int)(DateTime.UtcNow - _lastSearchUtc).TotalSeconds);
                return o;
            }
        }

        // REQUIRES ELEVATION. As a standard user IUpdateInstaller.Install returns
        // WU_E_NOT_ENOUGH_PRIVILEGES / E_ACCESSDENIED; we surface that verbatim.
        public static J Install(Jobs.Job job, List<int> indices, bool downloadOnly)
        {
            List<object> chosen = new List<object>();
            lock (Gate)
            {
                if (_lastUpdates == null)
                    throw new ApiFault("no_search_yet", "run updates.check first");
                if (indices == null || indices.Count == 0)
                    for (int i = 0; i < _lastUpdates.Count; i++) chosen.Add(_lastUpdates[i]);
                else
                    for (int i = 0; i < indices.Count; i++)
                    {
                        int ix = indices[i];
                        if (ix < 0 || ix >= _lastUpdates.Count)
                            throw new ApiFault("bad_request", "update index " + ix + " is out of range");
                        chosen.Add(_lastUpdates[ix]);
                    }
            }
            if (chosen.Count == 0) throw new ApiFault("nothing_to_install", "the last search returned no updates");

            J o = J.Obj();
            o.Set("elevated", Api.IsElevated());
            o.Set("selected", chosen.Count);

            job.Report(5, "building update collection");
            object coll = New("Microsoft.Update.UpdateColl");
            for (int i = 0; i < chosen.Count; i++)
            {
                try
                {
                    // Accepting the EULA is required before download; it is not a purchase.
                    if (!Convert.ToBoolean(Get(chosen[i], "EulaAccepted"), CultureInfo.InvariantCulture))
                        Call(chosen[i], "AcceptEula");
                }
                catch { }
                Call(coll, "Add", chosen[i]);
            }

            object session = New("Microsoft.Update.Session");
            job.Report(10, "downloading");
            object downloader = Call(session, "CreateUpdateDownloader");
            Put(downloader, "Updates", coll);
            object dres;
            try { dres = Call(downloader, "Download"); }
            catch (Exception ex)
            {
                throw new ApiFault(Api.IsElevated() ? "update_download_failed" : "elevation_required",
                    "IUpdateDownloader.Download failed: " + Api.Describe(ex) +
                    (Api.IsElevated() ? "" : " - downloading updates requires administrator rights"), o);
            }
            try { o.Set("downloadResult", ResultCodeName(Convert.ToInt32(Get(dres, "ResultCode"), CultureInfo.InvariantCulture))); }
            catch { }

            if (downloadOnly)
            {
                job.Report(100, "downloaded");
                o.Set("installed", false);
                o.Set("note", "downloadOnly was set");
                return o;
            }

            job.Report(60, "installing");
            object installer = Call(session, "CreateUpdateInstaller");
            Put(installer, "Updates", coll);
            object ires;
            try { ires = Call(installer, "Install"); }
            catch (Exception ex)
            {
                throw new ApiFault(Api.IsElevated() ? "update_install_failed" : "elevation_required",
                    "IUpdateInstaller.Install failed: " + Api.Describe(ex) +
                    (Api.IsElevated() ? "" : " - installing updates requires administrator rights"), o);
            }

            try { o.Set("installResult", ResultCodeName(Convert.ToInt32(Get(ires, "ResultCode"), CultureInfo.InvariantCulture))); }
            catch { }
            try { o.Set("rebootRequired", Convert.ToBoolean(Get(ires, "RebootRequired"), CultureInfo.InvariantCulture)); }
            catch { }

            J per = J.Arr();
            try
            {
                for (int i = 0; i < chosen.Count; i++)
                {
                    object r = Call(ires, "GetUpdateResult", i);
                    J e = J.Obj();
                    e.Set("index", i);
                    e.Set("title", Str(Get(chosen[i], "Title")));
                    e.Set("result", ResultCodeName(Convert.ToInt32(Get(r, "ResultCode"), CultureInfo.InvariantCulture)));
                    e.Set("hresult", "0x" + Convert.ToInt32(Get(r, "HResult"), CultureInfo.InvariantCulture)
                        .ToString("X8", CultureInfo.InvariantCulture));
                    per.Add(e);
                }
            }
            catch { }
            o.Set("results", per);
            o.Set("installed", true);
            job.Report(100, "installed");
            return o;
        }
    }

    #endregion

    #region Public entry point

    public static class SystemApi
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
                J req;
                try { req = ParseRequest(commandJson); }
                catch (Exception ex) { return Envelope(false, null, null, "bad_json", Api.Describe(ex)); }

                // "reqId", NOT "id": several commands take an "id" argument of their own
                // (audio.setDefault, sys.setTimezone), so the correlation key must not collide.
                id = req.S("reqId", null);
                cmd = req.S("cmd", null);
                if (string.IsNullOrEmpty(cmd))
                    return Envelope(false, id, null, "bad_request", "missing \"cmd\"");

                J data = Dispatch(cmd.Trim(), req);
                return Envelope(true, id, data, null, null);
            }
            catch (ApiFault f)
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
                    "unhandled in '" + (cmd == null ? "?" : cmd) + "': " + Api.Describe(ex) + trace);
            }
        }

        // Accepts a full command object, or a bare command name for convenience from the CLI.
        static J ParseRequest(string text)
        {
            if (text == null) throw new FormatException("null command");
            string t = text.Trim();
            if (t.Length == 0) throw new FormatException("empty command");
            if (t[0] != '{')
            {
                J o = J.Obj();
                o.Set("cmd", t);
                return o;
            }
            J parsed = J.Parse(t);
            if (parsed.Kind != J.TObj) throw new FormatException("command must be a JSON object");
            return parsed;
        }

        static string Envelope(bool ok, string id, J data, string error, string detail)
        {
            J o = J.Obj();
            o.Set("ok", ok);
            if (id != null) o.Set("reqId", id);
            if (ok)
            {
                o.Set("data", data == null ? J.Obj() : data);
            }
            else
            {
                o.Set("error", error == null ? "error" : error);
                o.Set("detail", detail == null ? "" : detail);
                if (data != null) o.Set("data", data);
            }
            return o.ToJson();
        }

        static J StartJob(string cmd, Func<Jobs.Job, J> work)
        {
            Jobs.Job j = Jobs.Start(cmd, work);
            J o = J.Obj();
            o.Set("jobId", j.Id);
            o.Set("state", j.State);
            o.Set("async", true);
            o.Set("poll", "{\"cmd\":\"job.status\",\"jobId\":\"" + j.Id + "\"}");
            return o;
        }

        // ------------------------------------------------------------------------------------
        static J Dispatch(string cmd, J q)
        {
            switch (cmd.ToLowerInvariant())
            {
                // ---------------- meta ----------------
                case "api.version":
                    {
                        J o = J.Obj();
                        o.Set("apiVersion", ApiVersion);
                        o.Set("elevated", Api.IsElevated());
                        o.Set("user", Environment.UserName);
                        o.Set("machine", Environment.MachineName);
                        return o;
                    }
                case "api.commands":
                    return Catalog();

                // ---------------- jobs ----------------
                case "job.status":
                    {
                        Jobs.Job j = Jobs.Find(q.S("jobId", null));
                        if (j == null) throw new ApiFault("no_such_job", "unknown jobId '" + q.S("jobId", "") + "'");
                        return Jobs.Describe(j);
                    }
                case "job.list":
                    {
                        List<Jobs.Job> all = Jobs.All();
                        J arr = J.Arr();
                        for (int i = 0; i < all.Count; i++) arr.Add(Jobs.Describe(all[i]));
                        return J.Obj().Set("jobs", arr).Set("count", arr.Count);
                    }
                case "job.cancel":
                    {
                        Jobs.Job j = Jobs.Find(q.S("jobId", null));
                        if (j == null) throw new ApiFault("no_such_job", "unknown jobId '" + q.S("jobId", "") + "'");
                        j.RequestCancel();
                        return Jobs.Describe(j);
                    }

                // ---------------- network ----------------
                case "net.status":
                    return NetMod.Status();

                // ---------------- wi-fi ----------------
                case "wifi.list":
                    return WifiMod.List(q.S("interface", null), q.B("merge", true));
                case "wifi.status":
                    return WifiMod.Status(q.S("interface", null));
                case "wifi.profiles":
                    {
                        string err;
                        using (WifiMod.Session s = WifiMod.Session.Open(out err))
                        {
                            if (s == null) throw new ApiFault("wifi_unavailable", err);
                            List<WifiMod.Iface> ifs = WifiMod.Interfaces(s, out err);
                            if (ifs.Count == 0) throw new ApiFault("no_wifi_adapter", "no wireless interface present on this machine");
                            WifiMod.Iface f = WifiMod.Pick(ifs, q.S("interface", null));
                            if (f == null) throw new ApiFault("no_such_interface", "interface not found");
                            List<string> names = WifiMod.ProfileNames(s, f);
                            J arr = J.Arr();
                            for (int i = 0; i < names.Count; i++) arr.Add(names[i]);
                            return J.Obj().Set("profiles", arr).Set("count", arr.Count);
                        }
                    }
                case "wifi.scan":
                    {
                        string iface = q.S("interface", null);
                        int to = q.I("timeoutMs", 10000);
                        bool merge = q.B("merge", true);
                        return StartJob(cmd, delegate (Jobs.Job job) { return WifiMod.Scan(job, iface, to, merge); });
                    }
                case "wifi.connect":
                    {
                        string ssid = q.S("ssid", null);
                        string pw = q.S("password", null);
                        string sec = q.S("security", "auto");
                        bool hidden = q.B("hidden", false);
                        string iface = q.S("interface", null);
                        int to = q.I("timeoutMs", 25000);
                        if (string.IsNullOrEmpty(ssid)) throw new ApiFault("bad_request", "ssid is required");
                        return StartJob(cmd, delegate (Jobs.Job job)
                        {
                            return WifiMod.Connect(job, ssid, pw, sec, hidden, iface, to);
                        });
                    }
                case "wifi.disconnect":
                    return WifiMod.Disconnect(q.S("interface", null));
                case "wifi.forget":
                    return WifiMod.Forget(q.S("ssid", q.S("profile", null)), q.S("interface", null));
                case "wifi.selftest":
                    return WifiMod.ProfileSelfTest();

                // ---------------- display ----------------
                case "display.list":
                    return DisplayMod.List(q.B("modes", false));
                case "display.modes":
                    return J.Obj().Set("display", DisplayMod.ResolveGdi(q.S("display", null)))
                                  .Set("modes", DisplayMod.ModesFor(DisplayMod.ResolveGdi(q.S("display", null))));
                case "display.setmode":
                    return DisplayMod.SetMode(q.S("display", null), q.I("width", 0), q.I("height", 0),
                        q.I("refreshHz", 0), q.B("test", false));
                case "display.sethdr":
                    return DisplayMod.SetHdr(q.S("display", null), q.B("enabled", q.B("enable", true)));

                // ---------------- audio ----------------
                case "audio.devices":
                    return AudioMod.Devices(q.B("includeInactive", false));
                case "audio.setdefault":
                    return AudioMod.SetDefault(q.S("id", null), q.S("flow", "output"), q.S("role", "all"));
                case "audio.getvolume":
                    return AudioMod.GetVolume(q.S("id", null), q.S("flow", "output"));
                case "audio.setvolume":
                    {
                        double level;
                        if (q.Has("percent")) level = q.N("percent", 0) / 100.0;
                        else if (q.Has("level")) level = q.N("level", -1);
                        else throw new ApiFault("bad_request", "level (0.0-1.0) or percent (0-100) is required");
                        return AudioMod.SetVolume(q.S("id", null), q.S("flow", "output"), level);
                    }
                case "audio.setmute":
                    return AudioMod.SetMute(q.S("id", null), q.S("flow", "output"),
                        q.B("muted", q.B("mute", true)), q.B("toggle", false));

                // ---------------- power ----------------
                case "power.status":
                    return PowerMod.Status();
                case "power.sleep":
                    return PowerMod.Sleep(q.B("hibernate", false));
                case "power.restart":
                    return PowerMod.EndSession("restart", q.S("mode", "signal"), q.B("force", false));
                case "power.shutdown":
                    return PowerMod.EndSession("shutdown", q.S("mode", "signal"), q.B("force", false));
                case "power.signout":
                    return PowerMod.EndSession("signout", q.S("mode", "signal"), q.B("force", false));

                // ---------------- bluetooth ----------------
                case "bt.status":
                    return BtMod.Status();
                case "bt.setradio":
                    return BtMod.SetRadio(q.B("enabled", q.B("enable", true)));
                case "bt.devices":
                    return BtMod.Devices(false, 1);
                case "bt.scan":
                    {
                        int secs = q.I("seconds", 8);
                        return StartJob(cmd, delegate (Jobs.Job job) { return BtMod.Scan(job, secs); });
                    }
                case "bt.pair":
                    {
                        string addr = q.S("address", null);
                        string name = q.S("name", null);
                        int to = q.I("timeoutMs", 30000);
                        return StartJob(cmd, delegate (Jobs.Job job) { return BtMod.Pair(job, addr, name, to); });
                    }
                case "bt.unpair":
                    return BtMod.Unpair(q.S("address", null));

                // ---------------- system ----------------
                case "sys.info":
                    return SysMod.Info();
                case "sys.storage":
                    return SysMod.Storage();
                case "sys.time":
                    return SysMod.Time();
                case "sys.timezones":
                    return SysMod.TimeZones();
                case "sys.settimezone":
                    return SysMod.SetTimeZone(q.S("id", null));
                case "sys.locale":
                    return SysMod.Locale();
                case "sys.privileges":
                    return SysMod.Privileges();

                // ---------------- updates ----------------
                case "updates.check":
                    {
                        string crit = q.S("criteria", null);
                        bool online = q.B("online", true);
                        return StartJob(cmd, delegate (Jobs.Job job) { return UpdatesMod.Check(job, crit, online); });
                    }
                case "updates.list":
                    return UpdatesMod.ListCached();
                case "updates.install":
                    {
                        List<int> idx = new List<int>();
                        J a = q.Get("indices");
                        if (a != null && a.Kind == J.TArr)
                            for (int i = 0; i < a.Count; i++) idx.Add((int)a.At(i).AsNum(-1));
                        bool dlOnly = q.B("downloadOnly", false);
                        return StartJob(cmd, delegate (Jobs.Job job) { return UpdatesMod.Install(job, idx, dlOnly); });
                    }

                // ---------------- accounts ----------------
                case "accounts.list":
                    return AccountsMod.List();

                default:
                    throw new ApiFault("unknown_command",
                        "no such command '" + cmd + "'; call api.commands for the catalogue");
            }
        }

        // ------------------------------------------------------------------------------------
        // Machine-readable catalogue. The UI can drive itself off this: which commands exist,
        // which are slow, which mutate state, and which will fail as a standard user.
        // ------------------------------------------------------------------------------------
        static J Cmd(string name, string group, string kind, string elevation, bool async, string notes, string args)
        {
            J o = J.Obj();
            o.Set("cmd", name);
            o.Set("group", group);
            o.Set("kind", kind);              // read | write | action
            o.Set("elevation", elevation);    // no | maybe | required
            o.Set("async", async);            // true = returns a jobId, poll job.status
            o.Set("args", args);
            o.Set("notes", notes);
            return o;
        }

        static J Catalog()
        {
            J a = J.Arr();

            a.Add(Cmd("api.version", "meta", "read", "no", false, "API version and the identity this process runs as", ""));
            a.Add(Cmd("api.commands", "meta", "read", "no", false, "this catalogue", ""));
            a.Add(Cmd("job.status", "meta", "read", "no", false, "poll a background job", "jobId"));
            a.Add(Cmd("job.list", "meta", "read", "no", false, "all known jobs (pruned 10 min after completion)", ""));
            a.Add(Cmd("job.cancel", "meta", "action", "no", false, "request cancellation; only wifi.scan honours it promptly", "jobId"));

            a.Add(Cmd("net.status", "network", "read", "no", false,
                "adapters, IP config, link speed; reachability from INetworkListManager (no packets sent, ~10 ms)", ""));

            a.Add(Cmd("wifi.list", "wifi", "read", "no", false,
                "CACHED visible networks - fast, no scan triggered. Duplicate SSID rows from the API are merged unless merge=false",
                "interface?, merge?=true"));
            a.Add(Cmd("wifi.status", "wifi", "read", "no", false,
                "per-interface state and current association", "interface?"));
            a.Add(Cmd("wifi.profiles", "wifi", "read", "no", false, "saved profile names", "interface?"));
            a.Add(Cmd("wifi.scan", "wifi", "read", "no", true,
                "SLOW (4-10 s). Triggers a real scan, waits for the ACM scan-complete notification, then returns the list",
                "interface?, timeoutMs?=10000, merge?=true"));
            a.Add(Cmd("wifi.connect", "wifi", "write", "no", true,
                "SLOW (up to 25 s) and DISRUPTIVE. Creates an all-user profile (falls back to per-user on ACCESS_DENIED) then associates. security=auto|WPA2PSK|WPA3SAE|WPAPSK|OPEN|WEP",
                "ssid, password?, security?=auto, hidden?=false, interface?, timeoutMs?=25000"));
            a.Add(Cmd("wifi.disconnect", "wifi", "write", "no", false, "DISRUPTIVE - drops the current association", "interface?"));
            a.Add(Cmd("wifi.forget", "wifi", "write", "maybe", false,
                "deletes a saved profile. All-user profiles created by another account may need elevation",
                "ssid|profile, interface?"));
            a.Add(Cmd("wifi.selftest", "wifi", "write", "no", false,
                "safe write-path check: creates then deletes a profile for a fake SSID; touches no live connection", ""));

            a.Add(Cmd("display.list", "display", "read", "no", false,
                "monitors, resolution, true refresh rate, HDR capability/state, DPI and scale factor. Needs an INTERACTIVE session - fails with ACCESS_DENIED in session 0 regardless of elevation",
                "modes?=false"));
            a.Add(Cmd("display.modes", "display", "read", "no", false, "mode list for one display", "display?"));
            a.Add(Cmd("display.setmode", "display", "write", "no", false,
                "DISRUPTIVE - always dry-runs with CDS_TEST first. Per-user (CDS_UPDATEREGISTRY), no elevation. Single display only; multi-monitor topology changes are not supported",
                "display?, width, height, refreshHz?, test?=false"));
            a.Add(Cmd("display.sethdr", "display", "write", "no", false,
                "DISRUPTIVE - toggles advanced colour via DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE; verifies by re-reading",
                "display?, enabled"));

            a.Add(Cmd("audio.devices", "audio", "read", "no", false,
                "render + capture endpoints with per-endpoint volume, and the default for each role", "includeInactive?=false"));
            a.Add(Cmd("audio.setdefault", "audio", "write", "no", false,
                "uses the UNDOCUMENTED IPolicyConfig; verified by re-reading the default endpoint",
                "id, flow?=output, role?=all|console|multimedia|communications"));
            a.Add(Cmd("audio.getvolume", "audio", "read", "no", false, "master volume, dB range and mute", "id?, flow?=output"));
            a.Add(Cmd("audio.setvolume", "audio", "write", "no", false, "scalar 0.0-1.0 or percent 0-100", "id?, flow?, level|percent"));
            a.Add(Cmd("audio.setmute", "audio", "write", "no", false, "set or toggle mute", "id?, flow?, muted|toggle"));

            a.Add(Cmd("power.status", "power", "read", "no", false, "AC/battery, percentage, active power plan, uptime", ""));
            a.Add(Cmd("power.sleep", "power", "action", "no", false,
                "DISRUPTIVE. Needs SeShutdownPrivilege, which an interactive standard user holds; enabled automatically. Blocks until resume",
                "hibernate?=false"));
            a.Add(Cmd("power.restart", "power", "action", "no", false,
                "DISRUPTIVE. mode=signal (default) returns exitCode 2 for the host to exit with; mode=direct calls ExitWindowsEx here",
                "mode?=signal|direct, force?=false"));
            a.Add(Cmd("power.shutdown", "power", "action", "no", false,
                "DISRUPTIVE. mode=signal returns exitCode 3; mode=direct calls ExitWindowsEx", "mode?=signal|direct, force?=false"));
            a.Add(Cmd("power.signout", "power", "action", "no", false,
                "DISRUPTIVE. mode=signal returns exitCode 0; mode=direct calls ExitWindowsEx(EWX_LOGOFF)", "mode?=signal|direct"));

            a.Add(Cmd("bt.status", "bluetooth", "read", "no", false,
                "radios via Win32 bthprops plus on/off state via the WinRT Radio API", ""));
            a.Add(Cmd("bt.setradio", "bluetooth", "write", "no", false,
                "WinRT Radio.SetStateAsync - there is NO Win32 API for this. Verified by re-reading the state", "enabled"));
            a.Add(Cmd("bt.devices", "bluetooth", "read", "no", false, "paired / remembered / connected devices; no inquiry, fast", ""));
            a.Add(Cmd("bt.scan", "bluetooth", "read", "no", true,
                "SLOW - a real inquiry, ~1.28 s per unit (default ~8 s)", "seconds?=8"));
            a.Add(Cmd("bt.pair", "bluetooth", "write", "no", true,
                "SLOW (up to 30 s). WinRT DeviceInformationPairing.PairAsync: no UI for a ConfirmOnly ceremony (DualSense). The Win32 path is NOT used because it needs admin-only BluetoothSetServiceState",
                "address|name, timeoutMs?=30000"));
            a.Add(Cmd("bt.unpair", "bluetooth", "write", "maybe", false,
                "BluetoothRemoveDevice; can return ACCESS_DENIED for a standard user on some builds", "address"));

            a.Add(Cmd("sys.info", "system", "read", "no", false, "machine name, Windows edition/build, CPU, RAM, uptime", ""));
            a.Add(Cmd("sys.storage", "system", "read", "no", false, "drives, total/free, removable flag", ""));
            a.Add(Cmd("sys.time", "system", "read", "no", false, "time, time zone, NTP configuration and W32Time service state", ""));
            a.Add(Cmd("sys.timezones", "system", "read", "no", false, "every installed time zone", ""));
            a.Add(Cmd("sys.settimezone", "system", "write", "no", false,
                "needs SeTimeZonePrivilege, which the Users group holds by default; enabled automatically", "id"));
            a.Add(Cmd("sys.locale", "system", "read", "no", false, "culture, region, geo, input languages", ""));
            a.Add(Cmd("sys.privileges", "system", "read", "no", false,
                "what this token can actually do - run this first when a write command fails", ""));

            a.Add(Cmd("updates.check", "updates", "read", "maybe", true,
                "VERY SLOW (10-120 s). WUA IUpdateSearcher.Search. Usually works unelevated but can be blocked by policy",
                "criteria?=\"IsInstalled=0 AND IsHidden=0\", online?=true"));
            a.Add(Cmd("updates.list", "updates", "read", "no", false,
                "results cached by the last updates.check IN THIS PROCESS - the shell is long-lived so this is fine, but each CLI invocation starts fresh (use the harness's --file mode)", ""));
            a.Add(Cmd("updates.install", "updates", "write", "required", true,
                "ELEVATION REQUIRED - IUpdateDownloader.Download and IUpdateInstaller.Install are administrator-only; as a standard user they return E_ACCESSDENIED and this reports error:\"elevation_required\". VERY SLOW. The only command here that cannot work in the shell's own security context",
                "indices?=[all], downloadOnly?=false"));

            a.Add(Cmd("accounts.list", "accounts", "read", "no", false,
                "local users, admin membership, who is signed in. READ ONLY by design", ""));

            J o = J.Obj();
            o.Set("apiVersion", ApiVersion);
            o.Set("commands", a);
            o.Set("count", a.Count);
            o.Set("elevated", Api.IsElevated());
            o.Set("envelope", "{\"ok\":true,\"data\":{...}} | {\"ok\":false,\"error\":\"code\",\"detail\":\"text\"}");
            o.Set("asyncContract",
                "commands with async=true return {\"jobId\":\"job-N\",\"state\":\"running\"}; poll {\"cmd\":\"job.status\",\"jobId\":\"job-N\"} until state is done|error|cancelled");
            return o;
        }
    }

    #endregion
}
