// MarwanOS - ShellHostWeb
// The real MarwanOS UI (index.html / boot.html) hosted in WebView2, running as the Windows shell.
//
// Native plumbing (job-object child tracking, forced-foreground return ladder, exit-code contract,
// handoff logging) is lifted verbatim from spike/ShellHost/ShellHost.cs, which is the proven host.
// The only substantive change is that the window contents are a WebView2 control instead of a label.
//
// Language level: C# 5 (inbox .NET Framework csc.exe).
// No string interpolation, no nameof, no expression-bodied members, no async/await.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;          // installing an extension from a .zip or .crx
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MarwanOs.ShellWeb
{
    /// <summary>
    /// The name this console uses ON DISK, which is deliberately NOT the product name.
    ///
    /// The product was renamed from ARC OS to MarwanOS. The machine was not: the install root
    /// is still C:\ArcOS, the shell account is still 'arcshell', and the per-user state -
    /// %LOCALAPPDATA%\ArcOS\WebView2 (browser profile, pinned sites, cookies, every installed
    /// extension) and %LOCALAPPDATA%\ArcOS\library.json (the installed-software scan) - is
    /// still under the old name.
    ///
    /// Renaming these constants does not migrate any of that. It abandons it: a shell that
    /// boots with a blank browser profile, no extensions, and a full disk re-scan on the home
    /// rail, with the real data sitting untouched one folder over. So the strings stay, in one
    /// place, with this note attached, until somebody deliberately moves the folders.
    /// </summary>
    internal static class OnDisk
    {
        public const string Brand = "ArcOS";
        public const string Root = @"C:\ArcOS";
    }

    #region Native interop  (copied from ShellHost.cs)

    [StructLayout(LayoutKind.Sequential)]
    public struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_ASSOCIATE_COMPLETION_PORT
    {
        public IntPtr CompletionKey;
        public IntPtr CompletionPort;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    // --- gamepad interop (copied verbatim from ShellHost.cs, which is hardware-verified) ---

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    // ── SendInput, for the pointer the pad drives over a foreign window ──────────────────
    // Nothing this shell draws can appear over another process's window, so a pointer drawn
    // by the page would be invisible exactly when it is needed. Windows already draws a
    // cursor; this is how it gets driven. The union is laid out by hand because C# 5 has no
    // other way to express it: on x64 the type field is at 0, the union at 8 (IntPtr
    // alignment), and sizeof(INPUT) is 40 - SendInput rejects any other cbSize.
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate uint XInputGetStateDelegate(uint dwUserIndex, out XINPUT_STATE pState);

    public static class Native
    {
        // --- window ---
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern IntPtr SetActiveWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);
        [DllImport("user32.dll")]
        public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int nMaxCount);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        // --- synthetic input (pointer mode over a foreign window) ---
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        public const uint INPUT_MOUSE = 0;
        public const uint INPUT_KEYBOARD = 1;

        public const uint MOUSEEVENTF_MOVE = 0x0001;
        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        public const uint MOUSEEVENTF_WHEEL = 0x0800;
        public const uint MOUSEEVENTF_HWHEEL = 0x1000;
        public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
        public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

        public const uint KEYEVENTF_KEYUP2 = 0x0002;   // KEYEVENTF_KEYUP already exists above
        public const uint KEYEVENTF_UNICODE = 0x0004;

        public const ushort VK_RETURN = 0x0D;
        public const ushort VK_ESCAPE = 0x1B;
        public const ushort VK_PRIOR = 0x21;           // Page Up
        public const ushort VK_NEXT = 0x22;            // Page Down

        // Virtual screen, in the coordinate space SetCursorPos/SendInput absolute use.
        public const int SM_XVIRTUALSCREEN = 76;
        public const int SM_YVIRTUALSCREEN = 77;
        public const int SM_CXVIRTUALSCREEN = 78;
        public const int SM_CYVIRTUALSCREEN = 79;

        // --- power ---
        // Restart and shut down deliberately do NOT go through here: they are
        // shell exit codes (2 and 3) so Shell Launcher performs the action and
        // stays in charge of the session. Only the two states Shell Launcher has
        // no exit code for need a direct call.
        [DllImport("powrprof.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetSuspendState(
            [MarshalAs(UnmanagedType.Bool)] bool hibernate,
            [MarshalAs(UnmanagedType.Bool)] bool forceCritical,
            [MarshalAs(UnmanagedType.Bool)] bool disableWakeEvent);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ExitWindowsEx(uint uFlags, uint dwReason);

        public const uint EWX_LOGOFF = 0x00000000;
        public const uint SHTDN_REASON_MAJOR_OTHER = 0x00000000;
        public const uint SHTDN_REASON_MINOR_OTHER = 0x00000000;
        public const uint SHTDN_REASON_FLAG_PLANNED = 0x80000000;

        public const int SW_HIDE = 0;
        public const int SW_SHOWNORMAL = 1;
        public const int SW_MINIMIZE = 6;
        public const int SW_SHOW = 5;
        public const int SW_RESTORE = 9;

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;

        public const byte VK_MENU = 0x12;
        public const uint KEYEVENTF_KEYUP = 0x0002;

        // --- process / job ---
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateProcess(
            string lpApplicationName,
            StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        public const uint CREATE_SUSPENDED = 0x00000004;
        public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int ResumeThread(IntPtr hThread);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfo);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfo, out uint returnLength);

        public const int JobObjectBasicProcessIdList = 3;
        public const int JobObjectAssociateCompletionPortInformation = 7;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateIoCompletionPort(IntPtr fileHandle, IntPtr existing, UIntPtr key, uint threads);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetQueuedCompletionStatus(IntPtr port, out uint bytes, out IntPtr key, out IntPtr overlapped, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostQueuedCompletionStatus(IntPtr port, uint bytes, IntPtr key, IntPtr overlapped);

        public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
        public const uint JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO = 4;
        public const uint JOB_OBJECT_MSG_NEW_PROCESS = 6;
        public const uint JOB_OBJECT_MSG_EXIT_PROCESS = 7;
        public const uint JOB_OBJECT_MSG_ABNORMAL_EXIT_PROCESS = 8;

        // --- adopting a process this host did NOT create ---
        // A launch that went through LibraryApi (ShellExecuteEx / IApplicationActivationManager)
        // hands back a pid and nothing else. To own that launch the way the host owns a child it
        // created, it has to reopen the process with enough rights to put it in a job object:
        // PROCESS_SET_QUOTA is what AssignProcessToJobObject actually checks, TERMINATE is what
        // the auto-kill path needs, SYNCHRONIZE is what the polling fallback waits on.
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

        public const uint PROCESS_TERMINATE = 0x0001;
        public const uint PROCESS_SET_QUOTA = 0x0100;
        public const uint PROCESS_QUERY_INFORMATION = 0x0400;
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const uint SYNCHRONIZE = 0x00100000;
        public const uint ADOPT_ACCESS = PROCESS_SET_QUOTA | PROCESS_TERMINATE
                                       | SYNCHRONIZE | PROCESS_QUERY_INFORMATION;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        public const uint WAIT_OBJECT_0 = 0;
        public const uint WAIT_TIMEOUT = 258;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryFullProcessImageName(IntPtr process, uint flags,
            StringBuilder exeName, ref uint size);

        // --- finding an already-running app's window, so a second activation raises it ---
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        public const uint GW_OWNER = 4;
        [DllImport("user32.dll")]
        public static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        // --- toolhelp (fallback tracking) ---
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
        public const uint TH32CS_SNAPPROCESS = 0x00000002;

        // --- dynamic loading (XInput, incl. hidden ordinal 100) ---
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadLibrary(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, EntryPoint = "GetProcAddress")]
        public static extern IntPtr GetProcAddressByName(IntPtr hModule, string name);
        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetProcAddress")]
        public static extern IntPtr GetProcAddressByOrdinal(IntPtr hModule, IntPtr ordinal);

        // --- raw HID (a DualSense is NOT an XInput device, so XInput never sees it) ---
        [DllImport("hid.dll")]
        public static extern void HidD_GetHidGuid(out Guid gHid);
        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool HidD_GetAttributes(IntPtr hDevice, ref HIDD_ATTRIBUTES attributes);
        [DllImport("hid.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool HidD_GetProductString(IntPtr hDevice, StringBuilder buffer, int bufferLen);
        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool HidD_GetPreparsedData(IntPtr hDevice, out IntPtr preparsed);
        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool HidD_FreePreparsedData(IntPtr preparsed);
        [DllImport("hid.dll")]
        public static extern int HidP_GetCaps(IntPtr preparsed, ref HIDP_CAPS caps);
        // Output reports: the haptics path. HidD_SetOutputReport goes through the class
        // driver's SET_REPORT IOCTL, which reaches devices whose interrupt OUT endpoint
        // WriteFile cannot use. Both take a buffer of exactly OutputReportByteLength.
        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool HidD_SetOutputReport(IntPtr hDevice, byte[] buffer, int bufferLen);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr devInfoSet, IntPtr devInfoData,
            ref Guid interfaceClassGuid, int memberIndex, ref SP_DEVICE_INTERFACE_DATA interfaceData);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devInfoSet,
            ref SP_DEVICE_INTERFACE_DATA interfaceData, IntPtr detailData, int detailSize,
            ref int requiredSize, IntPtr devInfoData);
        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr devInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateFile(string fileName, uint access, uint share,
            IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ReadFile(IntPtr hFile, byte[] buffer, int toRead, out int read, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WriteFile(IntPtr hFile, byte[] buffer, int toWrite, out int written, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CancelIoEx(IntPtr hFile, IntPtr overlapped);

        public const int DIGCF_PRESENT = 0x0002;
        public const int DIGCF_DEVICEINTERFACE = 0x0010;
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint FILE_SHARE_WRITE = 0x00000002;
        public const uint OPEN_EXISTING = 3;
    }

    #endregion

    #region Logging  (copied from ShellHost.cs)

    public static class Log
    {
        static readonly object Gate = new object();
        static string _path;

        public static string Path { get { return _path; } }

        public static void Init(string explicitPath)
        {
            if (!string.IsNullOrEmpty(explicitPath))
            {
                _path = explicitPath;
                return;
            }
            string dir = System.IO.Path.GetDirectoryName(Application.ExecutablePath);
            string candidate = System.IO.Path.Combine(dir, "shellhostweb-log.txt");
            try
            {
                File.AppendAllText(candidate, "");
                _path = candidate;
            }
            catch
            {
                // exe directory not writable (very likely under Shell Launcher) - fall back to LOCALAPPDATA.
                // "ArcOS", not "MarwanOS": see OnDisk.Brand below. The product was renamed; the
                // folders on the bench were not, and a rename here silently orphans them.
                string fallbackDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), OnDisk.Brand);
                try { Directory.CreateDirectory(fallbackDir); }
                catch { }
                _path = System.IO.Path.Combine(fallbackDir, "shellhostweb-log.txt");
            }
        }

        public static void Write(string category, string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                + "  [" + category + "] " + message;
            lock (Gate)
            {
                try { File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8); }
                catch { /* logging must never take the host down */ }
            }
            Debug.WriteLine(line);
        }
    }

    #endregion

    // The XInput wrapper and the whole raw-HID DualSense stack below are copied verbatim from
    // spike/ShellHost/ShellHost.cs, where every byte offset was verified on the bench against the
    // device's own HID report descriptor. Do not "improve" the offsets without re-verifying there.

    #region XInput wrapper

    public class XInput
    {
        IntPtr _module = IntPtr.Zero;
        XInputGetStateDelegate _getState;
        XInputGetStateDelegate _getStateEx;

        public string ModuleName = "(none)";
        public bool Available { get { return _getState != null; } }
        public bool ExAvailable { get { return _getStateEx != null; } }
        public string ExStatus = "not probed";

        public void Load()
        {
            string[] candidates = new string[] { "xinput1_4.dll", "xinput1_3.dll", "xinput9_1_0.dll" };
            foreach (string c in candidates)
            {
                IntPtr h = Native.LoadLibrary(c);
                if (h != IntPtr.Zero)
                {
                    IntPtr p = Native.GetProcAddressByName(h, "XInputGetState");
                    if (p != IntPtr.Zero)
                    {
                        _module = h;
                        ModuleName = c;
                        _getState = (XInputGetStateDelegate)Marshal.GetDelegateForFunctionPointer(p, typeof(XInputGetStateDelegate));
                        break;
                    }
                }
            }
            if (_getState == null)
            {
                Log.Write("XINPUT", "FAIL: no XInput module exported XInputGetState");
                ExStatus = "n/a (no XInput module)";
                return;
            }
            Log.Write("XINPUT", "loaded " + ModuleName + ", XInputGetState resolved by name");

            // Undocumented XInputGetStateEx is exported by ordinal 100 only (no name).
            IntPtr pEx = Native.GetProcAddressByOrdinal(_module, new IntPtr(100));
            if (pEx != IntPtr.Zero)
            {
                try
                {
                    _getStateEx = (XInputGetStateDelegate)Marshal.GetDelegateForFunctionPointer(pEx, typeof(XInputGetStateDelegate));
                    ExStatus = "RESOLVED (ordinal #100 @ 0x" + pEx.ToInt64().ToString("X") + ")";
                }
                catch (Exception ex)
                {
                    ExStatus = "resolve ok but delegate bind failed: " + ex.Message;
                }
            }
            else
            {
                ExStatus = "NOT RESOLVED (GetProcAddress ordinal #100 -> NULL, err=" + Marshal.GetLastWin32Error() + ")";
            }
            Log.Write("XINPUT", "XInputGetStateEx ordinal #100: " + ExStatus);
        }

        public uint GetState(uint index, out XINPUT_STATE state)
        {
            state = new XINPUT_STATE();
            if (_getState == null) return 1167; // ERROR_DEVICE_NOT_CONNECTED
            return _getState(index, out state);
        }

        // Returns ERROR_SUCCESS(0) / ERROR_DEVICE_NOT_CONNECTED(1167); Ex variant also reports the Guide bit (0x0400).
        public uint GetStateEx(uint index, out XINPUT_STATE state)
        {
            state = new XINPUT_STATE();
            if (_getStateEx == null) return 1167;
            return _getStateEx(index, out state);
        }
    }

    #endregion

    #region DualSense / DualShock raw HID input

    /// <summary>
    /// Immutable-by-convention snapshot of the pad, published by the reader thread and
    /// consumed by the UI thread. Replaced wholesale so the UI never sees a torn state.
    /// </summary>
    public class PadSnapshot
    {
        public bool Connected;
        public string Model = "";
        public string Transport = "";       // "USB" / "BT"
        public ushort Vid, Pid;
        public string Path = "";
        public int Buttons;                 // DualSense.BTN_* mask
        public byte LX = 128, LY = 128, RX = 128, RY = 128;
        public byte L2, R2;
        public byte ReportId;
        public int ReportLength;
        public long Reports;
        public string Status = "no pad";

        // Battery, read from the DualSense's own input report. BatteryKnown is false whenever
        // the running report layout has no battery byte (DualShock 4, the Bluetooth
        // compatibility report) or the read was short - the Settings screen must be able to
        // tell "no battery reading" from "0%".
        public bool BatteryKnown;
        public int BatteryPercent;          // 0-100, approximate: the pad reports 0-10 steps
        public string BatteryState = "";    // discharging | charging | full | temperature-fault | error
        public byte BatteryRaw;

        // The touchpad surface, as opposed to the touchpad BUTTON (which is BTN_TOUCHPAD and
        // has always been decoded). Two fingers, 12-bit each, 0..1919 x 0..1079 over roughly
        // 52 x 23 mm of glass. TouchKnown is false on report layouts that carry no touch
        // block, so a consumer can tell "no fingers" from "this pad's report cannot say".
        public bool TouchKnown;
        public bool T1Down, T2Down;
        public int T1X, T1Y, T2X, T2Y;
        public int T1Id, T2Id;
    }

    /// <summary>
    /// Reads DualSense / DualShock input reports off a raw HID handle on a dedicated
    /// background thread (blocking ReadFile), so nothing here can stall the UI thread.
    ///
    /// BYTE OFFSETS - verified empirically on the bench against a live DualSense
    /// (VID 0x054C / PID 0x0CE6, USB) rather than taken from any single source. The
    /// verification fed the Windows HID parser (HidP_GetUsageValue / HidP_GetUsages)
    /// both live reports and synthetic single-bit reports, so every offset below is
    /// confirmed by the device's own report descriptor:
    ///     byte 0        report id (0x01 USB / 0x31 BT)
    ///     byte 1        LX   (usage 0x30 X)      byte 2   LY   (usage 0x31 Y)
    ///     byte 3        RX   (usage 0x32 Z)      byte 4   RY   (usage 0x35 Rz)
    ///     byte 5        L2 analog (usage 0x33)   byte 6   R2 analog (usage 0x34)
    ///     byte 7        sequence counter
    ///     byte 8 lo nib hat, 0-7 clockwise from up, 8 = centred (usage 0x39)
    ///     byte 8 hi nib Square/Cross/Circle/Triangle  (buttons 1-4)
    ///     byte 9        L1 R1 L2 R2 Create Options L3 R3 (buttons 5-12, bits 0-7)
    ///     byte 10 b0-2  PS / Touchpad / Mute            (buttons 13-15)
    /// Bluetooth (report 0x31) shifts the whole payload by +1.
    ///
    /// The TOUCH SURFACE sits later in the same payload, at payload offset +32 (absolute 33
    /// wired, 34 over Bluetooth's 0x31), as two four-byte fingers:
    ///     b0  bit7 = 1 when the finger is NOT down; bits 0-6 = contact id
    ///     b1  x low 8              b2  low nibble = x high 4, high nibble = y low 4
    ///     b3  y high 8             -> 12-bit x (0..1919), 12-bit y (0..1079)
    /// The offset is anchored to the same payload base the sticks and buttons use rather
    /// than hardcoded per transport, and it is corroborated by the battery byte this decode
    /// already reads at payload +52: both land where the DualSense full report puts them,
    /// and both are read defensively, so a wrong guess can only cost the reading itself.
    /// The Bluetooth COMPATIBILITY report (a DualShock-4-shaped 0x01 frame) has a different
    /// layout and is left with no touch at all rather than decoded on a guess.
    /// </summary>
    public class DualSense
    {
        // Normalised button mask. Bits 0-14 are deliberately the HID button indices
        // 1-15 in report-descriptor order, so the decode is a straight bit shuffle.
        public const int BTN_SQUARE = 0x00001;
        public const int BTN_CROSS = 0x00002;
        public const int BTN_CIRCLE = 0x00004;
        public const int BTN_TRIANGLE = 0x00008;
        public const int BTN_L1 = 0x00010;
        public const int BTN_R1 = 0x00020;
        public const int BTN_L2 = 0x00040;
        public const int BTN_R2 = 0x00080;
        public const int BTN_CREATE = 0x00100;
        public const int BTN_OPTIONS = 0x00200;
        public const int BTN_L3 = 0x00400;
        public const int BTN_R3 = 0x00800;
        public const int BTN_PS = 0x01000;
        public const int BTN_TOUCHPAD = 0x02000;
        public const int BTN_MUTE = 0x04000;
        public const int BTN_DUP = 0x10000;
        public const int BTN_DDOWN = 0x20000;
        public const int BTN_DLEFT = 0x40000;
        public const int BTN_DRIGHT = 0x80000;

        const ushort VID_SONY = 0x054C;
        const int FAMILY_DS5 = 0;
        const int FAMILY_DS4 = 1;

        Thread _thread;
        volatile bool _stop;
        volatile IntPtr _handle = Native.INVALID_HANDLE_VALUE;
        volatile int _inputLen = 64;
        PadSnapshot _snap = new PadSnapshot();

        readonly object _edgeGate = new object();
        int _pendingPress;
        int _lastButtons;

        public PadSnapshot Snapshot { get { return _snap; } }

        public void Start()
        {
            _stop = false;
            _thread = new Thread(new ThreadStart(ReaderLoop));
            _thread.IsBackground = true;
            _thread.Name = "DualSenseReader";
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
            IntPtr h = _handle;
            if (h != Native.INVALID_HANDLE_VALUE)
            {
                // Unblock the pending ReadFile so the thread can observe _stop.
                try { Native.CancelIoEx(h, IntPtr.Zero); }
                catch { }
            }
        }

        /// <summary>Drains and clears the latched not-pressed -> pressed transitions.</summary>
        public int TakePressEdges()
        {
            lock (_edgeGate)
            {
                int p = _pendingPress;
                _pendingPress = 0;
                return p;
            }
        }

        #region reader thread

        void ReaderLoop()
        {
            // This process IS the shell: an escaped exception here blanks the screen.
            // Every iteration is therefore individually guarded.
            while (!_stop)
            {
                try
                {
                    if (!OpenPad())
                    {
                        for (int i = 0; i < 20 && !_stop; i++) Thread.Sleep(100);
                        continue;
                    }
                    ReadUntilFailure();
                }
                catch (Exception ex)
                {
                    Log.Write("PAD", "reader thread caught: " + ex.Message);
                    ClosePad();
                    for (int i = 0; i < 20 && !_stop; i++) Thread.Sleep(100);
                }
            }
            ClosePad();
            Log.Write("PAD", "reader thread stopped");
        }

        bool OpenPad()
        {
            string path = null;
            ushort vid = 0, pid = 0;
            string product = "";
            if (!FindPad(out path, out vid, out pid, out product)) return false;

            IntPtr h = Native.CreateFile(path, Native.GENERIC_READ,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
                Native.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == Native.INVALID_HANDLE_VALUE)
            {
                Log.Write("PAD", "found VID_" + vid.ToString("X4") + "&PID_" + pid.ToString("X4")
                    + " but CreateFile(GENERIC_READ) failed, err=" + Marshal.GetLastWin32Error());
                return false;
            }
            _handle = h;

            // The declared input report length is what distinguishes a wired DualSense
            // (64) from a Bluetooth one (78), and it MUST also be the exact ReadFile
            // buffer size: given a larger buffer, HIDClass concatenates several queued
            // reports into a single read (observed: 234 bytes = 3 x 78).
            _inputLen = 64;
            IntPtr pre;
            if (Native.HidD_GetPreparsedData(h, out pre))
            {
                try
                {
                    HIDP_CAPS caps = new HIDP_CAPS();
                    Native.HidP_GetCaps(pre, ref caps);
                    if (caps.InputReportByteLength > 0) _inputLen = caps.InputReportByteLength;
                }
                finally { Native.HidD_FreePreparsedData(pre); }
            }
            else
            {
                Log.Write("PAD", "HidD_GetPreparsedData failed, err=" + Marshal.GetLastWin32Error()
                    + " - assuming a " + _inputLen + " byte input report");
            }

            PadSnapshot s = new PadSnapshot();
            s.Connected = true;
            s.Vid = vid;
            s.Pid = pid;
            s.Path = path;
            s.Model = ModelName(pid) + (product.Length > 0 ? " (" + product + ")" : "");
            s.Transport = "?";
            s.Status = "opened, waiting for first report";
            _snap = s;

            Log.Write("PAD", "pad discovered: VID=0x" + vid.ToString("X4") + " PID=0x" + pid.ToString("X4")
                + " model='" + ModelName(pid) + "' product='" + product + "'"
                + " inputReportLength=" + _inputLen);
            Log.Write("PAD", "pad path: " + path);
            return true;
        }

        void ClosePad()
        {
            IntPtr h = _handle;
            _handle = Native.INVALID_HANDLE_VALUE;
            if (h != Native.INVALID_HANDLE_VALUE)
            {
                try { Native.CloseHandle(h); }
                catch { }
            }
            _lastButtons = 0;
            PadSnapshot s = new PadSnapshot();
            s.Status = "no pad";
            _snap = s;
        }

        void ReadUntilFailure()
        {
            // Exactly one report per read - see the note in OpenPad about concatenation.
            int len = _inputLen;
            byte[] buf = new byte[len];
            int family = FamilyOf(_snap.Pid);
            bool loggedTransport = false;
            bool loggedBattery = false;
            bool loggedTouch = false;
            bool haveBaseline = false;
            long count = 0;

            while (!_stop)
            {
                int got;
                IntPtr h = _handle;
                if (h == Native.INVALID_HANDLE_VALUE) return;
                if (!Native.ReadFile(h, buf, len, out got, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (_stop) return;
                    Log.Write("PAD", "ReadFile failed err=" + err + " after " + count
                        + " reports - treating as disconnect, will rescan");
                    ClosePad();
                    return;
                }
                if (got <= 0) continue;
                count++;

                int stick, btn, trig, btn2Mask, bat, touch;
                string transport;
                if (!SelectLayout(family, buf[0], len, out stick, out btn, out trig, out btn2Mask,
                                  out bat, out touch, out transport))
                {
                    if (count < 4)
                        Log.Write("PAD", "unrecognised report: id=0x" + buf[0].ToString("X2") + " len=" + got + " (ignored)");
                    continue;
                }
                if (got < btn + 3) continue;

                if (!loggedTransport)
                {
                    loggedTransport = true;
                    Log.Write("PAD", "transport detected: " + transport + " (report id 0x" + buf[0].ToString("X2")
                        + ", declared length " + len + " bytes, read " + got + ", stick offset " + stick
                        + ", buttons offset " + btn + ", trigger offset " + trig + ")");
                    Log.Write("PAD", "first report bytes: " + Hex(buf, Math.Min(got, 16)));
                    Log.Write("PAD", "resting sticks: LX=" + buf[stick] + " LY=" + buf[stick + 1]
                        + " RX=" + buf[stick + 2] + " RY=" + buf[stick + 3]
                        + "  L2=" + buf[trig] + " R2=" + buf[trig + 1]
                        + "  buttons=0x" + buf[btn].ToString("X2") + buf[btn + 1].ToString("X2") + buf[btn + 2].ToString("X2"));
                }

                int buttons = Decode(buf, btn, btn2Mask);

                PadSnapshot s = new PadSnapshot();
                s.Connected = true;
                s.Model = _snap.Model;
                s.Vid = _snap.Vid;
                s.Pid = _snap.Pid;
                s.Path = _snap.Path;
                s.Transport = transport;
                s.ReportId = buf[0];
                s.ReportLength = got;
                s.Reports = count;
                s.Buttons = buttons;
                s.LX = buf[stick];
                s.LY = buf[stick + 1];
                s.RX = buf[stick + 2];
                s.RY = buf[stick + 3];
                s.L2 = buf[trig];
                s.R2 = buf[trig + 1];
                s.Status = "streaming";

                // The touch surface. Defensive in exactly the way the battery read is: a
                // layout with no touch block, or a short read, leaves TouchKnown false rather
                // than publishing coordinates nobody can trust.
                if (touch >= 0 && touch + 7 < got)
                {
                    try
                    {
                        s.TouchKnown = true;
                        DecodeFinger(buf, touch, out s.T1Down, out s.T1X, out s.T1Y, out s.T1Id);
                        DecodeFinger(buf, touch + 4, out s.T2Down, out s.T2X, out s.T2Y, out s.T2Id);
                        if (!loggedTouch && s.T1Down)
                        {
                            loggedTouch = true;
                            Log.Write("PAD", "touch surface at offset " + touch + ": finger 1 down at "
                                + s.T1X + "," + s.T1Y + " (12-bit, 0-1919 x 0-1079)"
                                + "  bytes " + Hex4(buf, touch));
                        }
                    }
                    catch { s.TouchKnown = false; }
                }

                // Battery is a bonus reading, never a precondition: if anything about it is
                // unexpected the snapshot simply carries BatteryKnown=false and the UI says
                // "not reported" instead of inventing a level.
                if (bat >= 0 && bat < got)
                {
                    try
                    {
                        int pct; string bstate;
                        DecodeBattery(buf[bat], out pct, out bstate);
                        s.BatteryKnown = true;
                        s.BatteryPercent = pct;
                        s.BatteryState = bstate;
                        s.BatteryRaw = buf[bat];
                        if (!loggedBattery)
                        {
                            loggedBattery = true;
                            Log.Write("PAD", "battery byte at offset " + bat + " = 0x" + buf[bat].ToString("X2")
                                + " -> ~" + pct + "% " + bstate + " (approximate: the pad reports 11 steps)");
                        }
                    }
                    catch { s.BatteryKnown = false; }
                }
                else if (!loggedBattery)
                {
                    loggedBattery = true;
                    Log.Write("PAD", "no battery byte in this report layout (offset " + bat
                        + ", read " + got + " bytes) - the Settings screen will say so");
                }
                _snap = s;

                // Edge-latch on the reader thread so a fast tap cannot fall between UI ticks.
                // The first report after a connect only seeds the baseline: a button that is
                // already held when the pad appears must not fire an action by itself.
                if (!haveBaseline)
                {
                    haveBaseline = true;
                    _lastButtons = buttons;
                    if (buttons != 0)
                        Log.Write("PAD", "buttons already held at connect (0x" + buttons.ToString("X5")
                            + " [" + ButtonNames(buttons) + "]) - taken as baseline, not dispatched");
                    continue;
                }
                int edges = buttons & ~_lastButtons;
                _lastButtons = buttons;
                if (edges != 0)
                {
                    lock (_edgeGate) { _pendingPress |= edges; }
                }
            }
        }

        /// <summary>
        /// Maps report id + declared report length to payload offsets. Nothing here is
        /// hardcoded to one transport; both are detected and adapted to.
        ///
        /// Report id alone is NOT sufficient for a DualSense: over Bluetooth the pad sends
        /// id 0x01 too, until a host asks it for the full report. That Bluetooth 0x01 frame
        /// is 78 bytes carrying the DualShock-4-compatible layout (buttons at byte 5,
        /// analog triggers at 8/9), whereas the wired 0x01 frame is 64 bytes carrying the
        /// full DualSense layout (analog triggers at 5/6, buttons at byte 8). Both were
        /// observed directly: 64-byte wired on the bench, 78-byte Bluetooth on the dev box.
        /// The declared length therefore does the disambiguating.
        /// </summary>
        /// <remarks>
        /// bat is the offset of the DualSense battery byte, or -1 when this layout has none.
        /// It is an ADDITION to the verified decode, never an input to it: every existing
        /// offset below is byte-for-byte what was verified on the bench, and the battery is
        /// read separately and defensively so a bad guess can only cost a battery reading.
        /// </remarks>
        static bool SelectLayout(int family, byte reportId, int length,
            out int stick, out int btn, out int trig, out int btn2Mask, out int bat, out string transport)
        {
            int touch;
            return SelectLayout(family, reportId, length, out stick, out btn, out trig,
                                out btn2Mask, out bat, out touch, out transport);
        }

        static bool SelectLayout(int family, byte reportId, int length,
            out int stick, out int btn, out int trig, out int btn2Mask, out int bat,
            out int touch, out string transport)
        {
            stick = 0; btn = 0; trig = 0; btn2Mask = 0; bat = -1; touch = -1; transport = "";
            if (family == FAMILY_DS5)
            {
                if (reportId == 0x31 && length >= 12)
                {
                    // Bluetooth full report: the wired payload shifted by +1.
                    stick = 2; btn = 9; trig = 6; btn2Mask = 0x07; transport = "BT";
                    bat = 0x35 + 1;
                    touch = stick + 32;
                    return true;
                }
                if (reportId == 0x01 && length <= 64 && length >= 11)
                {
                    // Wired full report. Verified byte-by-byte on the bench.
                    stick = 1; btn = 8; trig = 5; btn2Mask = 0x07; transport = "USB";
                    bat = 0x35;
                    touch = stick + 32;
                    return true;
                }
                if (reportId == 0x01 && length > 64)
                {
                    // Bluetooth compatibility report. Verified on the dev box. This one is the
                    // DualShock-4-shaped frame and carries no DualSense battery status byte.
                    stick = 1; btn = 5; trig = 8; btn2Mask = 0x03; transport = "BT";
                    return true;
                }
                return false;
            }
            // DualShock 4: sticks in the same place, but the button bytes sit at 5/6/7 and
            // the analog triggers after them. Byte 7 bits 2-7 are a counter, hence mask 0x03.
            // Best effort - no DS4 was available to verify against.
            if (reportId == 0x01 && length >= 10)
            {
                stick = 1; btn = 5; trig = 8; btn2Mask = 0x03; transport = "USB";
                return true;
            }
            if (reportId == 0x11 && length >= 13)
            {
                stick = 3; btn = 7; trig = 10; btn2Mask = 0x03; transport = "BT";
                return true;
            }
            return false;
        }

        /// <summary>
        /// DualSense battery status byte: low nibble is a 0-10 charge step, high nibble is the
        /// charging state. Reported as approximate because the pad itself only has 11 steps.
        /// </summary>
        static void DecodeBattery(byte v, out int percent, out string state)
        {
            int step = v & 0x0F;
            int st = (v >> 4) & 0x0F;
            switch (st)
            {
                case 0x0: state = "discharging"; break;
                case 0x1: state = "charging"; break;
                case 0x2: state = "full"; step = 10; break;
                case 0xA:
                case 0xB: state = "temperature-fault"; break;
                default: state = "error"; break;
            }
            if (step > 10) step = 10;
            if (step < 0) step = 0;
            percent = step * 10;
        }

        static int Decode(byte[] b, int btn, int btn2Mask)
        {
            // Buttons 1-15 are contiguous starting at the high nibble of the first button
            // byte, so they shuffle straight into bits 0-14.
            int v = ((b[btn] >> 4) & 0x0F)
                  | (b[btn + 1] << 4)
                  | ((b[btn + 2] & btn2Mask) << 12);

            int hat = b[btn] & 0x0F;
            switch (hat)
            {
                case 0: v |= BTN_DUP; break;
                case 1: v |= BTN_DUP | BTN_DRIGHT; break;
                case 2: v |= BTN_DRIGHT; break;
                case 3: v |= BTN_DDOWN | BTN_DRIGHT; break;
                case 4: v |= BTN_DDOWN; break;
                case 5: v |= BTN_DDOWN | BTN_DLEFT; break;
                case 6: v |= BTN_DLEFT; break;
                case 7: v |= BTN_DUP | BTN_DLEFT; break;
                default: break;   // 8 (and anything else) = centred
            }
            return v;
        }

        #endregion

        #region enumeration

        static bool FindPad(out string path, out ushort vid, out ushort pid, out string product)
        {
            path = null; vid = 0; pid = 0; product = "";
            Guid hidGuid;
            Native.HidD_GetHidGuid(out hidGuid);
            IntPtr set = Native.SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
                Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
            if (set == Native.INVALID_HANDLE_VALUE)
            {
                Log.Write("PAD", "SetupDiGetClassDevs failed, err=" + Marshal.GetLastWin32Error());
                return false;
            }
            try
            {
                int index = 0;
                while (true)
                {
                    SP_DEVICE_INTERFACE_DATA did = new SP_DEVICE_INTERFACE_DATA();
                    did.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                    if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref did)) break;
                    index++;

                    int needed = 0;
                    Native.SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, ref needed, IntPtr.Zero);
                    if (needed <= 0) continue;

                    IntPtr detail = Marshal.AllocHGlobal(needed);
                    try
                    {
                        // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W: 8 on x64, 6 on x86.
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        int unused = 0;
                        if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref did, detail, needed, ref unused, IntPtr.Zero))
                            continue;
                        string devPath = Marshal.PtrToStringUni(new IntPtr(detail.ToInt64() + 4));
                        if (string.IsNullOrEmpty(devPath)) continue;

                        // Probe with zero desired access: this never disturbs whoever else has
                        // the device open, and is still enough for HidD_GetAttributes.
                        IntPtr h = Native.CreateFile(devPath, 0,
                            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
                            Native.OPEN_EXISTING, 0, IntPtr.Zero);
                        if (h == Native.INVALID_HANDLE_VALUE) continue;
                        try
                        {
                            HIDD_ATTRIBUTES attr = new HIDD_ATTRIBUTES();
                            attr.Size = Marshal.SizeOf(typeof(HIDD_ATTRIBUTES));
                            if (!Native.HidD_GetAttributes(h, ref attr)) continue;
                            if (attr.VendorID != VID_SONY || !IsSupported(attr.ProductID)) continue;

                            StringBuilder sb = new StringBuilder(128);
                            if (Native.HidD_GetProductString(h, sb, 256)) product = sb.ToString();
                            path = devPath;
                            vid = attr.VendorID;
                            pid = attr.ProductID;
                            return true;
                        }
                        finally { Native.CloseHandle(h); }
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
            return false;
        }

        static bool IsSupported(ushort pid)
        {
            return pid == 0x0CE6 || pid == 0x0DF2 || pid == 0x09CC || pid == 0x05C4;
        }

        static int FamilyOf(ushort pid)
        {
            return (pid == 0x0CE6 || pid == 0x0DF2) ? FAMILY_DS5 : FAMILY_DS4;
        }

        static string ModelName(ushort pid)
        {
            switch (pid)
            {
                case 0x0CE6: return "DualSense";
                case 0x0DF2: return "DualSense Edge";
                case 0x09CC: return "DualShock 4";
                case 0x05C4: return "DualShock 4 (v1)";
                default: return "Sony pad 0x" + pid.ToString("X4");
            }
        }

        #endregion

        public static string ButtonNames(int b)
        {
            if (b == 0) return "";
            List<string> n = new List<string>();
            if ((b & BTN_SQUARE) != 0) n.Add("SQUARE");
            if ((b & BTN_CROSS) != 0) n.Add("CROSS");
            if ((b & BTN_CIRCLE) != 0) n.Add("CIRCLE");
            if ((b & BTN_TRIANGLE) != 0) n.Add("TRIANGLE");
            if ((b & BTN_L1) != 0) n.Add("L1");
            if ((b & BTN_R1) != 0) n.Add("R1");
            if ((b & BTN_L2) != 0) n.Add("L2");
            if ((b & BTN_R2) != 0) n.Add("R2");
            if ((b & BTN_CREATE) != 0) n.Add("CREATE");
            if ((b & BTN_OPTIONS) != 0) n.Add("OPTIONS");
            if ((b & BTN_L3) != 0) n.Add("L3");
            if ((b & BTN_R3) != 0) n.Add("R3");
            if ((b & BTN_PS) != 0) n.Add("PS");
            if ((b & BTN_TOUCHPAD) != 0) n.Add("TOUCHPAD");
            if ((b & BTN_MUTE) != 0) n.Add("MUTE");
            if ((b & BTN_DUP) != 0) n.Add("DUP");
            if ((b & BTN_DDOWN) != 0) n.Add("DDOWN");
            if ((b & BTN_DLEFT) != 0) n.Add("DLEFT");
            if ((b & BTN_DRIGHT) != 0) n.Add("DRIGHT");
            return string.Join("+", n.ToArray());
        }

        static string Hex(byte[] b, int n)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(b[i].ToString("X2"));
            }
            return sb.ToString();
        }

        static string Hex4(byte[] b, int at)
        {
            return b[at].ToString("X2") + " " + b[at + 1].ToString("X2") + " "
                 + b[at + 2].ToString("X2") + " " + b[at + 3].ToString("X2");
        }

        /// <summary>
        /// One four-byte finger. Bit 7 of the first byte is the INACTIVE flag - it is set
        /// while nothing is touching - so the sense is inverted here once, at the decode,
        /// rather than in every consumer.
        /// </summary>
        static void DecodeFinger(byte[] b, int at, out bool down, out int x, out int y, out int id)
        {
            byte b0 = b[at], b1 = b[at + 1], b2 = b[at + 2], b3 = b[at + 3];
            down = (b0 & 0x80) == 0;
            id = b0 & 0x7F;
            x = b1 | ((b2 & 0x0F) << 8);
            y = ((b2 & 0xF0) >> 4) | (b3 << 4);
        }
    }

    /// <summary>
    /// The other half of the pad: writing to it.
    ///
    /// DESIGN — why this owns a SECOND handle.
    /// The reader thread sits in a blocking ReadFile on its own handle and must never be
    /// disturbed; that handle is opened GENERIC_READ and is never touched from here. This
    /// class opens its own GENERIC_WRITE handle to the same device path, so a write and a
    /// read can never contend and a failure to open the write handle costs nothing but
    /// haptics. Two handles on one HID collection is ordinary: HIDClass keeps per-handle
    /// state and the device is opened FILE_SHARE_READ | FILE_SHARE_WRITE at both ends.
    ///
    /// OUTPUT REPORT LAYOUT (DualSense).
    ///   USB       report id 0x02, 48 bytes total: id + a 47-byte common block.
    ///   Bluetooth report id 0x31, 78 bytes total: id, seq_tag, tag, the same 47-byte
    ///             common block, 24 reserved bytes, then a little-endian CRC-32 over the
    ///             byte 0xA2 followed by the first 74 bytes. Without a correct CRC the pad
    ///             silently ignores the report.
    /// Common block, offsets from the first byte AFTER the report id:
    ///     0   valid_flag0   bit0 COMPATIBLE_VIBRATION, bit1 HAPTICS_SELECT
    ///     1   valid_flag1
    ///     2   motor_right   (high frequency)
    ///     3   motor_left    (low frequency)
    ///    38   valid_flag2   bit2 COMPATIBLE_VIBRATION2 (newer firmware)
    ///    44-46 lightbar RGB (deliberately left alone - see below)
    /// Only the rumble flags are ever set. Every other valid_flag stays clear, which is what
    /// tells the pad "the bytes for those features in this report are not mine, keep what you
    /// have" - so this never turns the light bar off, never resets the player LEDs and never
    /// touches the adaptive triggers as a side effect of asking for a tick.
    ///
    /// WHAT IS NOT HERE. The DualSense's true haptic actuators are driven by streaming PCM to
    /// the pad's USB audio endpoint, not by HID at all; and the adaptive-trigger blocks sit in
    /// the reserved span of this report where published offsets disagree with each other. Both
    /// were left out deliberately: an unreliable trigger effect that occasionally wedges the
    /// pad would be worse than no trigger effect, and a menu shell has nothing to say with a
    /// trigger anyway. What ships is the rumble pair, tuned short.
    /// </summary>
    public class DualSenseHaptics
    {
        // One step of an effect: motor levels held for a span. Levels are 0-255 before the
        // user's intensity setting scales them.
        struct Step
        {
            public byte L, R;   // L = low frequency (left), R = high frequency (right)
            public int Ms;
            public Step(byte l, byte r, int ms) { L = l; R = r; Ms = ms; }
        }

        /// <summary>
        /// The vocabulary, matched one for one to the sound palette. Every effect is short on
        /// purpose: a haptic that outstays its welcome is the single cheapest-feeling thing a
        /// controller can do, and a constant buzz is worse than silence.
        /// </summary>
        static readonly Dictionary<string, Step[]> EFFECTS = BuildEffects();

        static Dictionary<string, Step[]> BuildEffects()
        {
            Dictionary<string, Step[]> d = new Dictionary<string, Step[]>(StringComparer.OrdinalIgnoreCase);

            // MOVE - barely there. One high-frequency blip, 14 ms, gone. This fires as often as
            // the move sound does, so it is the one effect that has to be under the threshold
            // of "I noticed that" and at the threshold of "I felt that".
            d["move"] = new Step[] { new Step(0, 42, 14), new Step(0, 0, 1) };

            // NUDGE - the wall at the end of a rail. Low frequency only, so it reads as a dull
            // stop rather than a tick that went wrong.
            d["nudge"] = new Step[] { new Step(46, 0, 22), new Step(0, 0, 1) };

            // ACTIVATE - the yes. A bright leading edge on the high-frequency motor, then the
            // low motor takes over and decays. Two motors in sequence is what gives a pulse a
            // front and a body instead of a flat buzz.
            d["activate"] = new Step[] {
                new Step(0, 90, 16), new Step(120, 30, 26), new Step(70, 0, 26),
                new Step(34, 0, 18), new Step(0, 0, 1)
            };

            // LAUNCH - activate with more floor and a slightly longer decay. Handing the
            // machine to another program deserves to be felt a little more.
            d["launch"] = new Step[] {
                new Step(0, 110, 18), new Step(150, 40, 34), new Step(105, 0, 34),
                new Step(60, 0, 26), new Step(26, 0, 20), new Step(0, 0, 1)
            };

            // BACK - the retreat. Low motor only, shorter and softer than activate, with no
            // bright edge at all: nothing about going back should feel like an arrival.
            d["back"] = new Step[] { new Step(74, 0, 26), new Step(36, 0, 20), new Step(0, 0, 1) };

            // PUSH / POP - a two-step ramp and its mirror. Small, but the direction is
            // unmistakable through the fingertips, which is the whole point of a scope cue.
            d["push"] = new Step[] { new Step(0, 34, 14), new Step(0, 66, 16), new Step(0, 0, 1) };
            d["pop"]  = new Step[] { new Step(0, 66, 14), new Step(0, 34, 16), new Step(0, 0, 1) };

            // TAB - between move and activate, because the whole screen changed.
            d["tab"] = new Step[] { new Step(0, 58, 14), new Step(40, 0, 16), new Step(0, 0, 1) };

            // TOGGLE - one clean detent. The sound carries the direction; the hand only needs
            // to know the switch moved.
            d["toggle"] = new Step[] { new Step(0, 62, 16), new Step(0, 0, 1) };

            // ERROR - a double tap on the low motor. Two knocks is universally "no" and it
            // needs no volume to say so.
            d["error"] = new Step[] {
                new Step(120, 0, 34), new Step(0, 0, 46), new Step(120, 0, 34), new Step(0, 0, 1)
            };

            // BOOT DONE - the one effect allowed a shape. A slow swell on the low motor under
            // the boot chord, a bright accent as the home screen lands, then a decay that gets
            // out of the way. ~460 ms, once per boot.
            d["bootDone"] = new Step[] {
                new Step(30, 0, 70), new Step(60, 0, 70), new Step(100, 0, 70),
                new Step(140, 90, 46), new Step(96, 0, 60), new Step(58, 0, 60),
                new Step(28, 0, 60), new Step(0, 0, 1)
            };

            // A deliberately obvious effect for the Settings "test vibration" row.
            d["test"] = new Step[] {
                new Step(0, 120, 60), new Step(0, 0, 60), new Step(160, 0, 90),
                new Step(90, 0, 60), new Step(0, 0, 1)
            };
            return d;
        }

        readonly DualSense _pad;
        Thread _thread;
        volatile bool _stop;

        IntPtr _h = Native.INVALID_HANDLE_VALUE;
        string _openPath = null;
        int _outLen = 48;
        bool _bt;
        byte _btSeq;
        bool _useSetOutputReport;      // WriteFile failed once; use the IOCTL path instead

        readonly object _gate = new object();
        string _pending;               // the effect the UI thread most recently asked for
        int _generation;               // bumped whenever _pending changes, to abort a running effect
        readonly AutoResetEvent _wake = new AutoResetEvent(false);

        double _intensity = 0.55;      // default: on, but gentle
        readonly Dictionary<string, int> _lastAt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Evidence for the verification pass. Read by PadInfoJson / the haptic status reply.
        public long Writes;
        public long WriteFailures;
        public int LastError;
        public string Status = "not started";

        public DualSenseHaptics(DualSense pad) { _pad = pad; }

        public double Intensity
        {
            get { return _intensity; }
            set { _intensity = value < 0 ? 0 : (value > 1 ? 1 : value); }
        }

        public bool Ready { get { return _h != Native.INVALID_HANDLE_VALUE; } }
        public string Transport { get { return _bt ? "BT" : "USB"; } }
        public int OutputLength { get { return _outLen; } }

        public static bool Known(string effect)
        {
            return !string.IsNullOrEmpty(effect) && EFFECTS.ContainsKey(effect);
        }

        public void Start()
        {
            _stop = false;
            _thread = new Thread(new ThreadStart(Loop));
            _thread.IsBackground = true;
            _thread.Name = "DualSenseHaptics";
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
            _wake.Set();
            // A stuck motor is the worst possible failure mode, so the last thing this class
            // ever does is write zeros - best effort, never throwing on the way out.
            try { if (Ready) WriteRumble(0, 0); }
            catch { }
            ClosePort();
        }

        /// <summary>
        /// Called from the UI thread. Never blocks, never touches the device: it drops a name
        /// and wakes the effect thread. An effect that arrives while another is playing
        /// replaces it, because the newest input is the one the hand is waiting on.
        /// </summary>
        public bool Play(string effect)
        {
            if (_stop || string.IsNullOrEmpty(effect)) return false;
            if (!EFFECTS.ContainsKey(effect)) return false;
            if (_intensity <= 0) return false;

            // Repeat storm guard, mirroring the sound engine's: the same effect inside 40 ms
            // is one intention, not two.
            int now = Environment.TickCount;
            lock (_gate)
            {
                int prev;
                if (_lastAt.TryGetValue(effect, out prev) && unchecked(now - prev) < 40) return false;
                _lastAt[effect] = now;
                _pending = effect;
                _generation++;
            }
            _wake.Set();
            return true;
        }

        #region effect thread

        void Loop()
        {
            while (!_stop)
            {
                try
                {
                    if (!_wake.WaitOne(500)) { continue; }
                    while (!_stop)
                    {
                        string name;
                        int gen;
                        lock (_gate) { name = _pending; _pending = null; gen = _generation; }
                        if (name == null) break;
                        RunEffect(name, gen);
                    }
                }
                catch (Exception ex)
                {
                    // This process IS the shell. Nothing in here may escape.
                    Log.Write("HAPTIC", "effect thread caught (swallowed): " + ex.Message);
                    ClosePort();
                    Thread.Sleep(500);
                }
            }
        }

        void RunEffect(string name, int gen)
        {
            if (!EnsurePort()) return;
            Step[] steps = EFFECTS[name];
            double k = _intensity;

            for (int i = 0; i < steps.Length; i++)
            {
                lock (_gate) { if (_generation != gen) break; }   // superseded: let the new one take over
                if (_stop) break;
                Step s = steps[i];
                if (!WriteRumble(Scale(s.L, k), Scale(s.R, k))) { ClosePort(); return; }
                if (s.Ms > 1) Thread.Sleep(s.Ms);
            }
            // Always land on silence, even when superseded - the replacement effect writes its
            // own first step immediately afterwards, so this costs one report, never a gap.
            WriteRumble(0, 0);
        }

        /// <summary>
        /// Scales a designed level by the user's intensity, with a floor: below roughly 12 the
        /// actuator does not move at all, so a naive multiply turns "gentle" into "nothing"
        /// rather than into "gentle".
        /// </summary>
        static byte Scale(byte v, double k)
        {
            if (v == 0) return 0;
            int x = (int)Math.Round(v * k);
            if (x < 12) x = 12;
            if (x > 255) x = 255;
            return (byte)x;
        }

        #endregion

        #region the device

        bool EnsurePort()
        {
            PadSnapshot s = _pad == null ? null : _pad.Snapshot;
            if (s == null || !s.Connected || string.IsNullOrEmpty(s.Path))
            {
                Status = "no pad";
                return false;
            }
            // The reader re-opens on reconnect and the path can change; follow it.
            if (Ready && _openPath == s.Path) { SyncTransport(s); return true; }
            ClosePort();
            return OpenPort(s);
        }

        void SyncTransport(PadSnapshot s)
        {
            bool bt = s.ReportId == 0x31 || s.ReportLength >= 70;
            if (bt != _bt)
            {
                _bt = bt;
                _outLen = _bt ? 78 : 48;
                Log.Write("HAPTIC", "transport now " + Transport + ", output report " + _outLen + " bytes");
            }
        }

        bool OpenPort(PadSnapshot s)
        {
            // GENERIC_WRITE alone first: a second GENERIC_READ handle would make HIDClass queue
            // a second copy of every input report for a handle that never reads, which is pure
            // waste. The wider modes are only fallbacks for a driver that refuses write-only.
            uint[] modes = new uint[] { Native.GENERIC_WRITE, Native.GENERIC_READ | Native.GENERIC_WRITE, 0 };
            string[] names = new string[] { "GENERIC_WRITE", "GENERIC_READ|GENERIC_WRITE", "no-access (IOCTL only)" };

            for (int i = 0; i < modes.Length; i++)
            {
                IntPtr h = Native.CreateFile(s.Path, modes[i],
                    Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
                    Native.OPEN_EXISTING, 0, IntPtr.Zero);
                if (h == Native.INVALID_HANDLE_VALUE)
                {
                    LastError = Marshal.GetLastWin32Error();
                    Log.Write("HAPTIC", "CreateFile(" + names[i] + ") failed, err=" + LastError);
                    continue;
                }
                _h = h;
                _openPath = s.Path;
                _useSetOutputReport = (modes[i] == 0);
                SyncTransport(s);

                // Ask the device what its output report length really is; the buffer handed to
                // WriteFile must be exactly that, the same rule the reader already follows for
                // input reports.
                IntPtr pre;
                if (Native.HidD_GetPreparsedData(h, out pre))
                {
                    try
                    {
                        HIDP_CAPS caps = new HIDP_CAPS();
                        Native.HidP_GetCaps(pre, ref caps);
                        if (caps.OutputReportByteLength > 0) _outLen = caps.OutputReportByteLength;
                    }
                    finally { Native.HidD_FreePreparsedData(pre); }
                }
                Status = "open (" + names[i] + ", " + Transport + ", " + _outLen + " byte output report)";
                Log.Write("HAPTIC", "write handle opened: " + names[i]
                    + " transport=" + Transport + " outputReportLength=" + _outLen);
                return true;
            }
            Status = "could not open a write handle (err " + LastError + ")";
            Log.Write("HAPTIC", "no write handle could be opened - haptics disabled for this pad");
            return false;
        }

        void ClosePort()
        {
            IntPtr h = _h;
            _h = Native.INVALID_HANDLE_VALUE;
            _openPath = null;
            if (h != Native.INVALID_HANDLE_VALUE)
            {
                try { Native.CloseHandle(h); }
                catch { }
            }
        }

        /// <summary>Builds and sends one output report. Returns false if the device rejected it.</summary>
        public bool WriteRumble(byte left, byte right)
        {
            IntPtr h = _h;
            if (h == Native.INVALID_HANDLE_VALUE) return false;

            byte[] buf = new byte[_outLen];
            int common;                       // index of the common block's first byte

            if (_bt)
            {
                buf[0] = 0x31;
                buf[1] = (byte)((_btSeq << 4) & 0xF0);
                _btSeq = (byte)((_btSeq + 1) & 0x0F);
                buf[2] = 0x10;                // DS_OUTPUT_TAG
                common = 3;
            }
            else
            {
                buf[0] = 0x02;
                common = 1;
            }

            buf[common + 0] = 0x03;           // COMPATIBLE_VIBRATION | HAPTICS_SELECT
            buf[common + 1] = 0x00;           // nothing else in this report is ours
            buf[common + 2] = right;          // high-frequency motor
            buf[common + 3] = left;           // low-frequency motor

            if (_bt && buf.Length >= 4)
            {
                // CRC-32 over 0xA2 followed by everything up to the CRC field itself.
                uint crc = Crc32.Seeded(0xA2, buf, buf.Length - 4);
                buf[buf.Length - 4] = (byte)(crc & 0xFF);
                buf[buf.Length - 3] = (byte)((crc >> 8) & 0xFF);
                buf[buf.Length - 2] = (byte)((crc >> 16) & 0xFF);
                buf[buf.Length - 1] = (byte)((crc >> 24) & 0xFF);
            }

            return Send(h, buf);
        }

        bool Send(IntPtr h, byte[] buf)
        {
            if (!_useSetOutputReport)
            {
                int wrote;
                if (Native.WriteFile(h, buf, buf.Length, out wrote, IntPtr.Zero) && wrote == buf.Length)
                {
                    Writes++;
                    return true;
                }
                LastError = Marshal.GetLastWin32Error();
                // One fallback, then stay on it: the IOCTL path reaches devices whose interrupt
                // OUT endpoint the class driver will not expose.
                Log.Write("HAPTIC", "WriteFile(" + buf.Length + ") failed err=" + LastError
                    + " - falling back to HidD_SetOutputReport");
                _useSetOutputReport = true;
            }

            if (Native.HidD_SetOutputReport(h, buf, buf.Length))
            {
                Writes++;
                return true;
            }
            LastError = Marshal.GetLastWin32Error();
            WriteFailures++;
            Status = "write failed, err " + LastError;
            if (WriteFailures <= 5 || (WriteFailures % 200) == 0)
                Log.Write("HAPTIC", "HidD_SetOutputReport(" + buf.Length + ") failed err=" + LastError
                    + " (failure #" + WriteFailures + ")");
            return false;
        }

        #endregion
    }

    /// <summary>
    /// CRC-32 (IEEE 802.3, reflected, poly 0xEDB88320) with a leading seed byte. The DualSense
    /// requires this over its Bluetooth output reports, computed as if the report were prefixed
    /// by 0xA2 - the Bluetooth HID "DATA / output" transaction header byte, which is on the wire
    /// but not in the buffer. Get this wrong and the pad simply ignores the report: no error, no
    /// symptom, no rumble.
    /// </summary>
    public static class Crc32
    {
        static readonly uint[] T = Build();

        static uint[] Build()
        {
            uint[] t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = ((c & 1) != 0) ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
                t[i] = c;
            }
            return t;
        }

        public static uint Seeded(byte seed, byte[] data, int count)
        {
            uint c = 0xFFFFFFFFu;
            c = T[(c ^ seed) & 0xFF] ^ (c >> 8);
            for (int i = 0; i < count; i++)
                c = T[(c ^ data[i]) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }
    }

    #endregion


    #region Child process tree tracker  (copied verbatim from ShellHost.cs)

    public enum TrackingMode { None, Job, Poll }

    public class ChildTracker
    {
        IntPtr _job = IntPtr.Zero;
        IntPtr _port = IntPtr.Zero;
        IntPtr _rootHandle = IntPtr.Zero;   // adopted launches only: what the poll fallback waits on
        Thread _watcher;
        volatile bool _stop;

        public int RootPid { get; private set; }
        public TrackingMode Mode { get; private set; }
        public string JobFailureReason = "";
        /// <summary>True when this tracker took over a process the host did not create.</summary>
        public bool Adopted { get; private set; }

        public event Action TreeEmpty;

        /// <summary>
        /// Job object + completion port. Split out of Start() because Adopt() needs exactly the
        /// same thing: the only difference between creating a child and taking one over is which
        /// handle gets passed to AssignProcessToJobObject.
        /// </summary>
        bool CreateJob(bool forceNoJob)
        {
            if (forceNoJob)
            {
                JobFailureReason = "--no-job specified (fallback path forced for testing)";
                return false;
            }

            _job = Native.CreateJobObject(IntPtr.Zero, null);
            if (_job == IntPtr.Zero)
            {
                JobFailureReason = "CreateJobObject failed, err=" + Marshal.GetLastWin32Error();
                return false;
            }

            _port = Native.CreateIoCompletionPort(Native.INVALID_HANDLE_VALUE, IntPtr.Zero, UIntPtr.Zero, 1);
            if (_port == IntPtr.Zero)
            {
                JobFailureReason = "CreateIoCompletionPort failed, err=" + Marshal.GetLastWin32Error();
                return false;
            }

            JOBOBJECT_ASSOCIATE_COMPLETION_PORT assoc = new JOBOBJECT_ASSOCIATE_COMPLETION_PORT();
            assoc.CompletionKey = _job;
            assoc.CompletionPort = _port;
            int size = Marshal.SizeOf(typeof(JOBOBJECT_ASSOCIATE_COMPLETION_PORT));
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(assoc, buf, false);
                if (!Native.SetInformationJobObject(_job, Native.JobObjectAssociateCompletionPortInformation, buf, (uint)size))
                {
                    JobFailureReason = "SetInformationJobObject(AssociateCompletionPort) failed, err="
                        + Marshal.GetLastWin32Error();
                    return false;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return true;
        }

        /// <summary>
        /// Take ownership of a process this host did not create — the pid LibraryApi's lib.launch
        /// handed back. This is the whole point of the adoption work: a launch that went through
        /// ShellExecuteEx used to be invisible to the host, which then sat in the foreground
        /// eating every pad press while the app it started owned the screen.
        ///
        /// Two modes, and which one is in use is logged, because they are NOT equivalent:
        ///
        ///   JOB OBJECT — OpenProcess for PROCESS_SET_QUOTA and AssignProcessToJobObject. Nested
        ///     jobs are permitted from Windows 8 onwards, so a process already inside somebody
        ///     else's job (Steam's own, a container, an installer) can still be assigned to ours.
        ///     Everything it spawns from that moment on is in the job too, and the completion
        ///     port reports ACTIVE_PROCESS_ZERO when the last of them exits. Note the "from that
        ///     moment": children the process had ALREADY spawned before we got to it are not
        ///     retro-fitted into the job, which is why adoption happens on the launch reply and
        ///     not seconds later.
        ///
        ///   HANDLE WAIT + DESCENDANT SWEEP — the fallback when the assignment is refused. It
        ///     waits on the process handle, and when the root goes it keeps sweeping whatever the
        ///     root had spawned. It is known unreliable for launcher-style apps: a launcher that
        ///     re-parents or hands off to an already-running client leaves nothing for the sweep
        ///     to find, and the shell comes back over an app that is still running. That was
        ///     proven in the original spike; it is kept only so that a refused assignment
        ///     degrades instead of failing.
        /// </summary>
        public bool Adopt(int pid, bool forceNoJob)
        {
            Mode = TrackingMode.None;
            Adopted = true;
            _stop = false;
            RootPid = pid;

            _rootHandle = Native.OpenProcess(Native.ADOPT_ACCESS, false, pid);
            if (_rootHandle == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                Log.Write("ADOPT", "OpenProcess(pid=" + pid + ") FAILED err=" + err
                    + " - the host cannot take ownership of this launch"
                    + (err == 5 ? " (access denied: the process is running at a higher integrity"
                                + " level or under another account)" : ""));
                Cleanup();
                return false;
            }

            bool jobReady = CreateJob(forceNoJob);

            if (jobReady && Native.AssignProcessToJobObject(_job, _rootHandle))
            {
                Mode = TrackingMode.Job;
                Log.Write("ADOPT", "AssignProcessToJobObject ok for pid " + pid
                    + " - tracking mode = JOB OBJECT (adopted). Descendants spawned from now on"
                    + " are tracked; anything it spawned before this instant is not.");
            }
            else
            {
                if (jobReady)
                    JobFailureReason = "AssignProcessToJobObject failed for the adopted pid, err="
                        + Marshal.GetLastWin32Error();
                Mode = TrackingMode.Poll;
                Log.Write("ADOPT", "WARN: could not job-adopt pid " + pid + " (" + JobFailureReason
                    + ") - tracking mode = HANDLE WAIT + DESCENDANT SWEEP."
                    + " This mode is KNOWN UNRELIABLE for launcher-style apps: if the app hands off"
                    + " to a client the host never saw, the shell will come back too early.");
            }

            _watcher = new Thread(Mode == TrackingMode.Job ? (ThreadStart)JobWatchLoop : AdoptedWatchLoop);
            _watcher.IsBackground = true;
            _watcher.Name = "AdoptedTracker";
            _watcher.Start();
            return true;
        }

        public bool Start(string commandLine, bool forceNoJob)
        {
            Mode = TrackingMode.None;
            Adopted = false;
            _stop = false;

            bool jobReady = CreateJob(forceNoJob);

            // Always create suspended so we can assign to the job BEFORE any grandchild can spawn.
            STARTUPINFO si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
            PROCESS_INFORMATION pi;
            StringBuilder cmd = new StringBuilder(commandLine, 32768);

            bool created = Native.CreateProcess(null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                Native.CREATE_SUSPENDED | Native.CREATE_UNICODE_ENVIRONMENT, IntPtr.Zero, null, ref si, out pi);
            if (!created)
            {
                Log.Write("LAUNCH", "CreateProcess FAILED for '" + commandLine + "' err=" + Marshal.GetLastWin32Error());
                Cleanup();
                return false;
            }
            RootPid = pi.dwProcessId;
            Log.Write("LAUNCH", "CreateProcess(suspended) ok: '" + commandLine + "' pid=" + RootPid);

            if (jobReady)
            {
                if (Native.AssignProcessToJobObject(_job, pi.hProcess))
                {
                    Mode = TrackingMode.Job;
                    Log.Write("TRACK", "AssignProcessToJobObject ok - tracking mode = JOB OBJECT");
                }
                else
                {
                    JobFailureReason = "AssignProcessToJobObject failed, err=" + Marshal.GetLastWin32Error();
                    Log.Write("TRACK", "WARN: " + JobFailureReason + " - falling back to descendant polling");
                }
            }

            if (Mode != TrackingMode.Job)
            {
                Mode = TrackingMode.Poll;
                Log.Write("TRACK", "tracking mode = TOOLHELP POLL (" + JobFailureReason + ")");
            }

            Native.ResumeThread(pi.hThread);
            Native.CloseHandle(pi.hThread);
            Native.CloseHandle(pi.hProcess);

            _watcher = new Thread(Mode == TrackingMode.Job ? (ThreadStart)JobWatchLoop : PollWatchLoop);
            _watcher.IsBackground = true;
            _watcher.Name = "ChildTracker";
            _watcher.Start();
            return true;
        }

        void JobWatchLoop()
        {
            while (!_stop)
            {
                uint code;
                IntPtr key, ov;
                bool ok = Native.GetQueuedCompletionStatus(_port, out code, out key, out ov, 1000);
                if (!ok)
                {
                    if (_stop) return;
                    continue; // timeout
                }
                if (key == IntPtr.Zero && code == 0xFFFFFFFF) return; // our own stop packet
                long pid = ov.ToInt64();
                switch (code)
                {
                    case Native.JOB_OBJECT_MSG_NEW_PROCESS:
                        Log.Write("TRACK", "job: NEW_PROCESS pid=" + pid);
                        break;
                    case Native.JOB_OBJECT_MSG_EXIT_PROCESS:
                        Log.Write("TRACK", "job: EXIT_PROCESS pid=" + pid);
                        break;
                    case Native.JOB_OBJECT_MSG_ABNORMAL_EXIT_PROCESS:
                        Log.Write("TRACK", "job: ABNORMAL_EXIT_PROCESS pid=" + pid);
                        break;
                    case Native.JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO:
                        Log.Write("TRACK", "job: ACTIVE_PROCESS_ZERO - child tree is empty");
                        Action h = TreeEmpty;
                        if (h != null) h();
                        return;
                }
            }
        }

        void PollWatchLoop()
        {
            while (!_stop)
            {
                Thread.Sleep(400);
                List<int> tree = GetDescendants(RootPid);
                if (tree.Count == 0)
                {
                    Log.Write("TRACK", "poll: no live descendants of pid " + RootPid + " - child tree is empty");
                    Action h = TreeEmpty;
                    if (h != null) h();
                    return;
                }
            }
        }

        /// <summary>
        /// The adopted fallback. Waits on the real process handle so the root's exit is noticed
        /// immediately rather than up to 400 ms late, then keeps sweeping the set of pids that
        /// were alive under it. The watch set is carried forward on every pass, so a child that
        /// outlives its parent stays tracked even after the exit re-parents it — which is the one
        /// thing a plain GetDescendants(root) sweep gets wrong.
        /// </summary>
        void AdoptedWatchLoop()
        {
            List<int> watch = new List<int>();
            watch.Add(RootPid);
            bool rootGone = false;

            while (!_stop)
            {
                if (!rootGone && _rootHandle != IntPtr.Zero)
                {
                    uint w = Native.WaitForSingleObject(_rootHandle, 300);
                    if (w == Native.WAIT_OBJECT_0)
                    {
                        rootGone = true;
                        Log.Write("TRACK", "adopted poll: root pid " + RootPid + " has exited");
                    }
                }
                else
                {
                    Thread.Sleep(300);
                }
                if (_stop) return;

                List<int> live = LiveTreeOf(watch);
                if (live.Count == 0)
                {
                    Log.Write("TRACK", "adopted poll: nothing left alive under pid " + RootPid
                        + " - app tree is empty");
                    Action h = TreeEmpty;
                    if (h != null) h();
                    return;
                }
                watch = live;
            }
        }

        public static List<int> GetDescendants(int rootPid)
        {
            List<int> roots = new List<int>();
            roots.Add(rootPid);
            return LiveTreeOf(roots);
        }

        /// <summary>
        /// Every pid in <paramref name="roots"/> that is still alive, plus everything descended
        /// from any of them. One toolhelp snapshot, expanded until it stops growing.
        /// </summary>
        public static List<int> LiveTreeOf(List<int> roots)
        {
            List<int> result = new List<int>();
            Dictionary<int, int> parents = new Dictionary<int, int>();
            IntPtr snap = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPPROCESS, 0);
            if (snap == Native.INVALID_HANDLE_VALUE) return result;
            try
            {
                PROCESSENTRY32 pe = new PROCESSENTRY32();
                pe.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                if (!Native.Process32First(snap, ref pe)) return result;
                do
                {
                    parents[(int)pe.th32ProcessID] = (int)pe.th32ParentProcessID;
                } while (Native.Process32Next(snap, ref pe));
            }
            finally { Native.CloseHandle(snap); }

            foreach (int r in roots)
                if (parents.ContainsKey(r) && !result.Contains(r)) result.Add(r);

            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (KeyValuePair<int, int> kv in parents)
                {
                    if (result.Contains(kv.Key)) continue;
                    if (result.Contains(kv.Value)) { result.Add(kv.Key); grew = true; }
                }
            }
            return result;
        }

        public List<int> GetTrackedPids()
        {
            if (Mode == TrackingMode.Job && _job != IntPtr.Zero)
            {
                List<int> pids = new List<int>();
                int cap = 512;
                int size = 8 + IntPtr.Size * cap;
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    uint ret;
                    if (Native.QueryInformationJobObject(_job, Native.JobObjectBasicProcessIdList, buf, (uint)size, out ret))
                    {
                        int inList = Marshal.ReadInt32(buf, 4);
                        for (int i = 0; i < inList && i < cap; i++)
                            pids.Add((int)Marshal.ReadIntPtr(buf, 8 + i * IntPtr.Size).ToInt64());
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
                return pids;
            }
            return GetDescendants(RootPid);
        }

        public void Cleanup()
        {
            _stop = true;
            if (_port != IntPtr.Zero)
            {
                Native.PostQueuedCompletionStatus(_port, 0xFFFFFFFF, IntPtr.Zero, IntPtr.Zero);
                Native.CloseHandle(_port);
                _port = IntPtr.Zero;
            }
            if (_job != IntPtr.Zero)
            {
                // NOTE: no JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE - closing the handle must not kill the game.
                Native.CloseHandle(_job);
                _job = IntPtr.Zero;
            }
            if (_rootHandle != IntPtr.Zero)
            {
                Native.CloseHandle(_rootHandle);
                _rootHandle = IntPtr.Zero;
            }
        }

        /// <summary>Is anything this tracker owns still running?</summary>
        public bool AnythingAlive()
        {
            List<int> pids = GetTrackedPids();
            return pids != null && pids.Count > 0;
        }
    }

    #endregion

    #region Finding a running app's window  (so a second activation raises rather than relaunches)

    public static class AppWindows
    {
        /// <summary>
        /// The best top-level window belonging to any of <paramref name="pids"/>: visible, not
        /// owned by another window, with a caption, largest first. That is the ordinary heuristic
        /// for "the app's main window" and it is deliberately conservative — a splash or a tray
        /// tooltip has no caption, and a modal dialog has an owner.
        /// </summary>
        public static IntPtr MainWindowOf(List<int> pids)
        {
            if (pids == null || pids.Count == 0) return IntPtr.Zero;
            IntPtr best = IntPtr.Zero;
            long bestArea = -1;

            Native.EnumWindowsProc cb = delegate(IntPtr hwnd, IntPtr lp)
            {
                if (!Native.IsWindowVisible(hwnd)) return true;
                if (Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero) return true;
                if (Native.GetWindowTextLength(hwnd) == 0) return true;
                uint wpid;
                Native.GetWindowThreadProcessId(hwnd, out wpid);
                if (!pids.Contains((int)wpid)) return true;
                RECT r;
                if (!Native.GetWindowRect(hwnd, out r)) return true;
                long area = (long)Math.Max(0, r.Right - r.Left) * Math.Max(0, r.Bottom - r.Top);
                if (area > bestArea) { bestArea = area; best = hwnd; }
                return true;
            };
            try { Native.EnumWindows(cb, IntPtr.Zero); }
            catch (Exception ex) { Log.Write("RAISE", "EnumWindows threw (swallowed): " + ex.Message); }
            GC.KeepAlive(cb);
            return best;
        }

        /// <summary>Full image path of a running process, or null. Never throws.</summary>
        public static string ImagePath(int pid)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                uint cap = (uint)sb.Capacity;
                if (Native.QueryFullProcessImageName(h, 0, sb, ref cap)) return sb.ToString();
                return null;
            }
            catch { return null; }
            finally { Native.CloseHandle(h); }
        }

        /// <summary>
        /// The pid of a process already running the given executable, or 0. Used to answer "is
        /// this app already up?" before a second launch — including apps the human started
        /// outside the shell, which is exactly the case that produced four Steam clients.
        /// </summary>
        public static int FindRunningByImage(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return 0;
            string want;
            try { want = Path.GetFileNameWithoutExtension(exePath); }
            catch { return 0; }
            if (string.IsNullOrEmpty(want)) return 0;

            Process[] all;
            try { all = Process.GetProcessesByName(want); }
            catch { return 0; }

            int fallback = 0;
            foreach (Process p in all)
            {
                int pid;
                try { pid = p.Id; }
                catch { continue; }
                finally { try { p.Dispose(); } catch { } }
                if (fallback == 0) fallback = pid;
                string img = ImagePath(pid);
                if (img != null && string.Equals(img, exePath, StringComparison.OrdinalIgnoreCase))
                    return pid;
            }
            // Same executable name but the path could not be read (another account, or a
            // higher integrity level). Treating it as a match is the safer error: raising the
            // wrong window is recoverable, starting a second game client is not.
            return fallback;
        }
    }

    #endregion

    #region Foreground restoration  (copied verbatim from ShellHost.cs)

    public static class Foreground
    {
        public static string ForceForeground(IntPtr hwnd)
        {
            Native.ShowWindow(hwnd, Native.SW_RESTORE);

            if (Try(hwnd)) return "path0:SW_RESTORE+BringWindowToTop+SetForegroundWindow";

            Log.Write("FOREGROUND", "path0 failed (fg=" + Describe(Native.GetForegroundWindow()) + "), trying ALT-key workaround");
            Native.keybd_event(Native.VK_MENU, 0, 0, UIntPtr.Zero);
            Native.keybd_event(Native.VK_MENU, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
            if (Try(hwnd)) return "path1:keybd_event(ALT down/up)+SetForegroundWindow";

            Log.Write("FOREGROUND", "path1 failed, trying AttachThreadInput");
            IntPtr fg = Native.GetForegroundWindow();
            uint targetPid;
            uint fgThread = Native.GetWindowThreadProcessId(fg, out targetPid);
            uint ourThread = Native.GetCurrentThreadId();
            bool attached = false;
            if (fgThread != 0 && fgThread != ourThread)
                attached = Native.AttachThreadInput(ourThread, fgThread, true);
            try
            {
                if (Try(hwnd)) return "path2:AttachThreadInput(attached=" + attached + ")+SetForegroundWindow";
            }
            finally
            {
                if (attached) Native.AttachThreadInput(ourThread, fgThread, false);
            }

            Log.Write("FOREGROUND", "path2 failed, trying AttachThreadInput + ALT key");
            attached = (fgThread != 0 && fgThread != ourThread) && Native.AttachThreadInput(ourThread, fgThread, true);
            try
            {
                Native.keybd_event(Native.VK_MENU, 0, 0, UIntPtr.Zero);
                Native.keybd_event(Native.VK_MENU, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
                if (Try(hwnd)) return "path3:AttachThreadInput+ALT+SetForegroundWindow";
            }
            finally
            {
                if (attached) Native.AttachThreadInput(ourThread, fgThread, false);
            }

            // Transient TOPMOST toggle. We do NOT stay topmost (must not fight games) and we do NOT
            // touch SPI_SETFOREGROUNDLOCKTIMEOUT, which would be a system setting change.
            Log.Write("FOREGROUND", "path3 failed, trying transient topmost toggle");
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_SHOWWINDOW);
            Native.SetWindowPos(hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_SHOWWINDOW);
            if (Try(hwnd)) return "path4:transient HWND_TOPMOST toggle (reverted to NOTOPMOST)";

            Log.Write("FOREGROUND", "path4 failed, trying minimize/restore cycle");
            Native.ShowWindow(hwnd, Native.SW_MINIMIZE);
            Thread.Sleep(40);
            Native.ShowWindow(hwnd, Native.SW_RESTORE);
            if (Try(hwnd)) return "path5:SW_MINIMIZE/SW_RESTORE cycle";

            Log.Write("FOREGROUND", "ALL PATHS FAILED - foreground is " + Describe(Native.GetForegroundWindow()));
            return null;
        }

        static bool Try(IntPtr hwnd)
        {
            Native.BringWindowToTop(hwnd);
            Native.SetForegroundWindow(hwnd);
            Native.SetActiveWindow(hwnd);
            for (int i = 0; i < 12; i++)
            {
                if (Native.GetForegroundWindow() == hwnd) return true;
                Thread.Sleep(20);
                Application.DoEvents();
            }
            return Native.GetForegroundWindow() == hwnd;
        }

        public static string Describe(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "hwnd=0";
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            string name = "?";
            try { name = Process.GetProcessById((int)pid).ProcessName; }
            catch { }
            StringBuilder sb = new StringBuilder(256);
            Native.GetWindowText(hwnd, sb, 256);
            return "hwnd=0x" + hwnd.ToInt64().ToString("X") + " pid=" + pid + " proc=" + name + " title='" + sb + "'";
        }

        public static string Title(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "";
            StringBuilder sb = new StringBuilder(256);
            Native.GetWindowText(hwnd, sb, 256);
            return sb.ToString();
        }

        public static int PidOf(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return 0;
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            return (int)pid;
        }
    }

    #endregion

    #region The pointer  (a real Windows cursor, driven by the pad)

    /// <summary>
    /// The mouse the DualSense does not have.
    ///
    /// The shell's own pointer (ui/mosnav.js cursor mode) is drawn by the page and therefore
    /// exists only inside a WebView. Over a FOREIGN window - an elevated installer, a
    /// launcher's sign-in - nothing this process paints can appear at all, so a drawn pointer
    /// would be invisible at exactly the moment it is the only way through. Windows already
    /// draws a cursor for every session; this class drives THAT one, with SendInput.
    ///
    /// Everything is absolute over the VIRTUAL screen (MOUSEEVENTF_ABSOLUTE |
    /// MOUSEEVENTF_VIRTUALDESK, normalised to 0..65535) rather than relative: a relative move
    /// is put through the pointer ballistics ("enhance pointer precision"), so the same stick
    /// deflection travels a different distance depending on a system setting this shell must
    /// not read or change. Absolute goes where it is told.
    ///
    /// The position is read back from the OS on every step instead of being remembered, so a
    /// human touching a real mouse mid-move is followed rather than fought; only the
    /// sub-pixel remainder is carried between ticks, which is what keeps a small stick
    /// deflection moving slowly instead of not at all.
    /// </summary>
    public class PointerMode
    {
        double _fracX, _fracY;          // sub-pixel remainder between ticks
        double _wheelAcc, _hwheelAcc;   // wheel accumulates until it is worth a notch
        bool _leftDown, _rightDown;

        public bool LeftDown { get { return _leftDown; } }
        public bool RightDown { get { return _rightDown; } }

        public static POINT Cursor()
        {
            POINT p;
            if (!Native.GetCursorPos(out p)) { p.X = 0; p.Y = 0; }
            return p;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr info, int len, out int ret);
        const uint TOKEN_QUERY = 0x0008;
        const int TokenElevation = 20;

        /// <summary>
        /// Is the process behind that pid running with an ELEVATED token - the full admin
        /// token, not the filtered one a signed-in admin's desktop normally runs on?
        /// 1 = yes, 0 = no, -1 = could not tell (the process would not open, or is gone).
        ///
        /// Asked by PointerRule for one reason: since 2026-08-16 the install broker starts an
        /// `interactive` installer under the CONSOLE USER's linked admin token, at the
        /// desktop's own (medium) integrity, precisely so that this pointer can drive it. That
        /// window opens fine (same user, same integrity) and so is invisible to the "refuses
        /// to open" rule - and its token is the only thing that tells it apart from any other
        /// window of ours. TokenElevation survives the integrity change (measured on the bench:
        /// IL=0x2000 elevated=1), which is what makes it usable as the identity test.
        /// </summary>
        public static int TokenElevated(int pid)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return -1;
            IntPtr tok;
            int result = -1;
            if (OpenProcessToken(h, TOKEN_QUERY, out tok))
            {
                IntPtr buf = Marshal.AllocHGlobal(4);
                int got;
                if (GetTokenInformation(tok, TokenElevation, buf, 4, out got))
                    result = Marshal.ReadInt32(buf) != 0 ? 1 : 0;
                Marshal.FreeHGlobal(buf);
                Native.CloseHandle(tok);
            }
            Native.CloseHandle(h);
            return result;
        }

        public static RECT VirtualScreen()
        {
            RECT r;
            r.Left = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
            r.Top = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
            int w = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
            int h = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
            if (w <= 0) w = 1920;
            if (h <= 0) h = 1080;
            r.Right = r.Left + w;
            r.Bottom = r.Top + h;
            return r;
        }

        /// <summary>
        /// Absolute move to a point in virtual-screen pixels. Clamped, then normalised.
        ///
        /// Returns false when the cursor did NOT end up where it was asked to go although it
        /// was asked to move somewhere else - which is how this class finds out that Windows
        /// is refusing the input. SendInput is subject to UIPI: a process may only inject
        /// input into applications at its own integrity level or lower, and it fails SILENTLY
        /// - the call succeeds, the input is discarded. See PointerRefused in HostForm.
        /// </summary>
        public bool MoveTo(int x, int y)
        {
            RECT v = VirtualScreen();
            int w = v.Right - v.Left, h = v.Bottom - v.Top;
            if (x < v.Left) x = v.Left;
            if (y < v.Top) y = v.Top;
            if (x > v.Right - 1) x = v.Right - 1;
            if (y > v.Bottom - 1) y = v.Bottom - 1;
            POINT before = Cursor();

            INPUT[] inp = new INPUT[1];
            inp[0].type = Native.INPUT_MOUSE;
            inp[0].u.mi.dx = (int)(((double)(x - v.Left) * 65535.0) / Math.Max(1, w - 1) + 0.5);
            inp[0].u.mi.dy = (int)(((double)(y - v.Top) * 65535.0) / Math.Max(1, h - 1) + 0.5);
            inp[0].u.mi.dwFlags = Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE
                                | Native.MOUSEEVENTF_VIRTUALDESK;
            Native.SendInput(1, inp, Marshal.SizeOf(typeof(INPUT)));

            POINT after = Cursor();
            if (before.X == x && before.Y == y) return true;         // nothing was asked for
            return after.X != before.X || after.Y != before.Y;
        }

        /// <summary>
        /// Move by a fractional pixel delta, carrying the remainder. Returns 0 when the delta
        /// did not add up to a whole pixel yet, +1 when the cursor moved, -1 when it was asked
        /// to move and did not (see MoveTo).
        /// </summary>
        public int MoveBy(double dx, double dy)
        {
            _fracX += dx; _fracY += dy;
            int sx = (int)_fracX, sy = (int)_fracY;
            _fracX -= sx; _fracY -= sy;
            if (sx == 0 && sy == 0) return 0;
            POINT p = Cursor();
            return MoveTo(p.X + sx, p.Y + sy) ? 1 : -1;
        }

        public void ResetFraction() { _fracX = _fracY = 0; _wheelAcc = _hwheelAcc = 0; }

        public void Button(bool right, bool down)
        {
            INPUT[] inp = new INPUT[1];
            inp[0].type = Native.INPUT_MOUSE;
            inp[0].u.mi.dwFlags = right
                ? (down ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_RIGHTUP)
                : (down ? Native.MOUSEEVENTF_LEFTDOWN : Native.MOUSEEVENTF_LEFTUP);
            Native.SendInput(1, inp, Marshal.SizeOf(typeof(INPUT)));
            if (right) _rightDown = down; else _leftDown = down;
        }

        /// <summary>Release anything this class is still holding down. Called whenever pointer mode ends.</summary>
        public void ReleaseButtons()
        {
            if (_leftDown) Button(false, false);
            if (_rightDown) Button(true, false);
        }

        /// <summary>
        /// One wheel notch is WHEEL_DELTA (120). The right stick produces pixels-per-tick like
        /// the browser's inertial scroll does, and those pixels are banked until they are worth
        /// a notch - anything else either scrolls in unusable jumps or sends 60 messages a
        /// second to an application that treats each one as three lines.
        /// </summary>
        public void WheelPixels(double dy, double dx)
        {
            _wheelAcc += dy; _hwheelAcc += dx;
            const double PxPerNotch = 34.0;
            int notches = (int)(_wheelAcc / PxPerNotch);
            if (notches != 0)
            {
                _wheelAcc -= notches * PxPerNotch;
                Wheel(false, notches * 120);
            }
            int hn = (int)(_hwheelAcc / PxPerNotch);
            if (hn != 0)
            {
                _hwheelAcc -= hn * PxPerNotch;
                Wheel(true, hn * 120);
            }
        }

        public void Wheel(bool horizontal, int delta)
        {
            INPUT[] inp = new INPUT[1];
            inp[0].type = Native.INPUT_MOUSE;
            inp[0].u.mi.mouseData = unchecked((uint)delta);
            inp[0].u.mi.dwFlags = horizontal ? Native.MOUSEEVENTF_HWHEEL : Native.MOUSEEVENTF_WHEEL;
            Native.SendInput(1, inp, Marshal.SizeOf(typeof(INPUT)));
        }

        public void KeyTap(ushort vk)
        {
            INPUT[] inp = new INPUT[2];
            inp[0].type = Native.INPUT_KEYBOARD;
            inp[0].u.ki.wVk = vk;
            inp[1].type = Native.INPUT_KEYBOARD;
            inp[1].u.ki.wVk = vk;
            inp[1].u.ki.dwFlags = Native.KEYEVENTF_KEYUP2;
            Native.SendInput(2, inp, Marshal.SizeOf(typeof(INPUT)));
        }

        /// <summary>
        /// Type a string as text, not as keystrokes: KEYEVENTF_UNICODE carries the character
        /// itself, so nothing depends on the keyboard layout the machine happens to have and
        /// no dead key or AltGr combination can turn one character into another. Surrogate
        /// pairs go through as their two UTF-16 units, which is what the flag expects.
        /// Returns the number of characters sent.
        /// </summary>
        public int TypeText(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int sent = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\n' || c == '\r') { KeyTap(Native.VK_RETURN); sent++; continue; }
                INPUT[] inp = new INPUT[2];
                inp[0].type = Native.INPUT_KEYBOARD;
                inp[0].u.ki.wScan = c;
                inp[0].u.ki.dwFlags = Native.KEYEVENTF_UNICODE;
                inp[1].type = Native.INPUT_KEYBOARD;
                inp[1].u.ki.wScan = c;
                inp[1].u.ki.dwFlags = Native.KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP2;
                Native.SendInput(2, inp, Marshal.SizeOf(typeof(INPUT)));
                sent++;
                Thread.Sleep(8);      // an installer's edit control drops a burst sent faster
            }
            return sent;
        }
    }

    #endregion

    #region Tiny JSON field reader

    // The page posts flat JSON objects only. A full parser would be dead weight here.
    public static class Json
    {
        public static string Str(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return null;
            return m.Groups[1].Value.Replace("\\\\", "\\").Replace("\\\"", "\"").Replace("\\n", "\n");
        }

        public static int Int(string json, string key, int fallback)
        {
            if (string.IsNullOrEmpty(json)) return fallback;
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(-?[0-9]+)");
            if (!m.Success) return fallback;
            int v;
            if (int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            return fallback;
        }

        /// <summary>
        /// A fractional number. The haptic intensity is a 0..1 setting, and Int() would read
        /// 0.55 as 0 and silently switch vibration off.
        /// </summary>
        public static double Num(string json, string key, double fallback)
        {
            if (string.IsNullOrEmpty(json)) return fallback;
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(-?[0-9]+(?:\\.[0-9]+)?(?:[eE][-+]?[0-9]+)?)");
            if (!m.Success) return fallback;
            double v;
            if (double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            return fallback;
        }

        /// <summary>
        /// A real JSON boolean, with 1/0 accepted as well. The page's own messages are hand-built
        /// strings in places and JSON.stringify output in others, so "allow":true and "allow":1
        /// both turn up; Int() reads the first as the fallback and would silently turn an Allow
        /// into a Deny.
        /// </summary>
        public static bool Bool(string json, string key, bool fallback)
        {
            if (string.IsNullOrEmpty(json)) return fallback;
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(true|false|-?[0-9]+)",
                                  RegexOptions.IgnoreCase);
            if (!m.Success) return fallback;
            string v = m.Groups[1].Value;
            if (string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(v, "false", StringComparison.OrdinalIgnoreCase)) return false;
            int n;
            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n != 0;
            return fallback;
        }

        /// <summary>
        /// Return the raw JSON text of an OBJECT-valued field, braces included, or null.
        ///
        /// The file explorer wraps a whole FileApi command inside {"type":"fs","command":{...}},
        /// so this one field is not flat and the regex reader above cannot see into it. Rather
        /// than parse the payload - which would mean re-serialising it before handing it to
        /// FileApi, and getting a chance to corrupt a path on the way - the substring is lifted
        /// out verbatim by counting braces, with string literals and their escapes skipped so a
        /// filename containing '{' or '"' cannot end the scan early.
        /// </summary>
        public static string Sub(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\\{");
            if (!m.Success) return null;
            int start = m.Index + m.Length - 1;          // the '{' itself
            int depth = 0;
            bool inStr = false, esc = false;
            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];
                if (inStr)
                {
                    if (esc) esc = false;
                    else if (c == '\\') esc = true;
                    else if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') { inStr = true; continue; }
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return json.Substring(start, i - start + 1);
                }
            }
            return null;                                  // unbalanced - the caller reports it
        }
    }

    #endregion

    #region SystemApi worker  (the Settings screen's channel into the OS)

    /// <summary>
    /// Runs MarwanOs.Sys.SystemApi.Handle() off the UI thread.
    ///
    /// WHY A DEDICATED THREAD AND NOT THE THREAD POOL
    /// SystemApi's contract says Handle() is called from the WebView2 UI thread: calls are
    /// serialised, and the COM objects it creates (MMDeviceEnumerator, IPolicyConfig, the WUA
    /// searcher, the WinRT Radio activation) live in one apartment for the process's lifetime.
    /// A pool thread would give neither guarantee - two Handle() calls could overlap, and each
    /// would land in whatever MTA thread happened to be free. One dedicated STA thread with a
    /// serial queue reproduces the UI thread's semantics exactly, minus the freezing.
    ///
    /// WHY IT CANNOT WEDGE THE SHELL
    /// The queue is drained one item at a time, so a pathologically slow command delays later
    /// commands but never the UI: the message pump keeps running, the pad keeps moving focus,
    /// and the page's own per-request timeout turns the silence into a visible error. Anything
    /// genuinely slow (Wi-Fi scan, Bluetooth inquiry, Windows Update search) is already a job
    /// inside SystemApi and returns in milliseconds with a jobId.
    /// </summary>
    public class SysWorker
    {
        readonly Queue<string> _q = new Queue<string>();
        readonly object _gate = new object();
        readonly Action<string> _reply;           // called ON THE WORKER THREAD with the envelope
        Thread _thread;
        volatile bool _stop;
        long _served;

        public long Served { get { return Interlocked.Read(ref _served); } }

        public SysWorker(Action<string> reply)
        {
            _reply = reply;
        }

        public void Start()
        {
            if (_thread != null) return;
            _stop = false;
            _thread = new Thread(new ThreadStart(Loop));
            _thread.IsBackground = true;          // never keeps the shell alive at exit
            _thread.Name = "MarwanSysApi";
            try { _thread.SetApartmentState(ApartmentState.STA); }
            catch (Exception ex) { Log.Write("SYS", "could not set STA on the worker thread: " + ex.Message); }
            _thread.Start();
            Log.Write("SYS", "SystemApi worker thread started (STA, serial queue)");
        }

        public void Stop()
        {
            _stop = true;
            lock (_gate) { Monitor.PulseAll(_gate); }
        }

        /// <summary>Queue one raw request. Returns immediately; never throws.</summary>
        public void Post(string requestJson)
        {
            if (string.IsNullOrEmpty(requestJson)) return;
            lock (_gate)
            {
                if (_q.Count > 64)
                {
                    // A page bug must not grow this without bound. Drop the oldest and say so;
                    // the page's timeout turns the dropped request into a visible message.
                    Log.Write("SYS", "request queue over 64 deep - dropping the oldest");
                    _q.Dequeue();
                }
                _q.Enqueue(requestJson);
                Monitor.Pulse(_gate);
            }
        }

        void Loop()
        {
            while (!_stop)
            {
                string item = null;
                try
                {
                    lock (_gate)
                    {
                        while (!_stop && _q.Count == 0) Monitor.Wait(_gate, 500);
                        if (_stop) break;
                        item = _q.Dequeue();
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("SYS", "queue wait threw (swallowed): " + ex.Message);
                    continue;
                }
                if (item == null) continue;
                Serve(item);
            }
            Log.Write("SYS", "SystemApi worker thread stopped after " + Served + " requests");
        }

        void Serve(string request)
        {
            string cmd = Json.Str(request, "cmd");
            string reqId = Json.Str(request, "reqId");
            string envelope;
            DateTime t0 = DateTime.UtcNow;
            try
            {
                // SystemApi.Handle() is documented never to throw. The try/catch is here because
                // this process is the Windows shell and "documented" is not "proven for every
                // input"; a thrown exception on this thread would take the whole shell down.
                envelope = MarwanOs.Sys.SystemApi.Handle(request);
            }
            catch (Exception ex)
            {
                envelope = "{\"ok\":false" + (reqId == null ? "" : ",\"reqId\":\"" + Esc(reqId) + "\"")
                    + ",\"error\":\"host_exception\",\"detail\":\"" + Esc(ex.Message) + "\"}";
                Log.Write("SYS", "Handle('" + cmd + "') THREW (contained): " + ex.ToString());
            }
            if (envelope == null)
                envelope = "{\"ok\":false" + (reqId == null ? "" : ",\"reqId\":\"" + Esc(reqId) + "\"")
                    + ",\"error\":\"host_null\",\"detail\":\"SystemApi returned nothing\"}";

            int ms = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
            Interlocked.Increment(ref _served);
            bool ok = envelope.StartsWith("{\"ok\":true", StringComparison.Ordinal);
            string tail = ok ? "" : "  " + Trim(envelope, 220);
            Log.Write("SYS", (ok ? "ok  " : "ERR ") + (cmd == null ? "?" : cmd)
                + " reqId=" + (reqId == null ? "-" : reqId) + " in " + ms + " ms" + tail);
            if (ms > 1500)
                Log.Write("SYS", "NOTE: '" + cmd + "' took " + ms + " ms on the worker thread - "
                    + "later requests queued behind it. The UI thread was not blocked.");

            try { if (_reply != null) _reply(envelope); }
            catch (Exception ex) { Log.Write("SYS", "reply dispatch threw (swallowed): " + ex.Message); }
        }

        static string Trim(string s, int n)
        {
            if (s == null) return "";
            return s.Length <= n ? s : s.Substring(0, n) + "...";
        }

        static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder b = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') { b.Append('\\').Append(c); }
                else if (c == '\n') b.Append("\\n");
                else if (c == '\r') b.Append("\\r");
                else if (c == '\t') b.Append("\\t");
                else if (c < ' ') b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else b.Append(c);
            }
            return b.ToString();
        }
    }

    #endregion

    #region FileApi worker  (the file explorer's channel into the file system)

    /// <summary>
    /// Runs MarwanOs.Files.FileApi.Handle() off the UI thread, on the same serial-queue pattern
    /// SysWorker uses. It is a separate class rather than a second instance of SysWorker because
    /// the two have genuinely different tolerances: a SystemApi call that takes 1.5 s is worth a
    /// warning, whereas a directory sweep over a 2 TB disk taking 8 s is normal and the explorer's
    /// own progress UI is built for it. Sharing one queue would also let a slow sweep sit in front
    /// of the volume slider, which is the one coupling worth paying a hundred lines to avoid.
    ///
    /// WHY IT MUST NOT RUN ON THE UI THREAD
    /// fs.list on a cold directory, fs.size on a tree, fs.copy's job start and fs.open's
    /// ShellExecuteExW all block for as long as the disk takes. On the UI thread that is a frozen
    /// television with no way back: the pad stops moving focus because the message pump is inside
    /// the call. Off it, a slow command delays only later file commands.
    ///
    /// STA for the same reason SysWorker is STA: FileApi reaches the Windows shell (IShellItem,
    /// ShellExecuteExW, the recycle bin) and those APIs want a single-threaded apartment.
    /// </summary>
    public class FileWorker
    {
        /// <summary>One queued request. A pair, not a delimited string: a reqId is page-supplied
        /// text and any separator picked here would be a separator a page could also send.</summary>
        sealed class Item
        {
            public readonly string ReqId;
            public readonly string Command;
            public Item(string reqId, string command) { ReqId = reqId; Command = command; }
        }

        readonly Queue<Item> _q = new Queue<Item>();
        readonly object _gate = new object();
        readonly Action<string, string> _reply;              // (reqId, FileApi envelope) ON THE WORKER THREAD
        Thread _thread;
        volatile bool _stop;
        long _served;

        public long Served { get { return Interlocked.Read(ref _served); } }

        public FileWorker(Action<string, string> reply)
        {
            _reply = reply;
        }

        public void Start()
        {
            if (_thread != null) return;
            _stop = false;
            _thread = new Thread(new ThreadStart(Loop));
            _thread.IsBackground = true;
            _thread.Name = "MarwanFileApi";
            try { _thread.SetApartmentState(ApartmentState.STA); }
            catch (Exception ex) { Log.Write("FS", "could not set STA on the file worker thread: " + ex.Message); }
            _thread.Start();
            Log.Write("FS", "FileApi worker thread started (STA, serial queue)");
        }

        public void Stop()
        {
            _stop = true;
            lock (_gate) { Monitor.PulseAll(_gate); }
        }

        /// <summary>Queue one raw FileApi command (the inner object, not the fs envelope).</summary>
        public void Post(string reqId, string commandJson)
        {
            if (string.IsNullOrEmpty(commandJson)) return;
            lock (_gate)
            {
                if (_q.Count > 64)
                {
                    Log.Write("FS", "request queue over 64 deep - dropping the oldest");
                    _q.Dequeue();
                }
                _q.Enqueue(new Item(reqId, commandJson));
                Monitor.Pulse(_gate);
            }
        }

        void Loop()
        {
            while (!_stop)
            {
                Item item = null;
                try
                {
                    lock (_gate)
                    {
                        while (!_stop && _q.Count == 0) Monitor.Wait(_gate, 500);
                        if (_stop) break;
                        item = _q.Dequeue();
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("FS", "queue wait threw (swallowed): " + ex.Message);
                    continue;
                }
                if (item == null) continue;
                Serve(item.ReqId, item.Command);
            }
            Log.Write("FS", "FileApi worker thread stopped after " + Served + " requests");
        }

        void Serve(string reqId, string command)
        {
            string cmd = Json.Str(command, "cmd");
            string envelope;
            DateTime t0 = DateTime.UtcNow;
            try
            {
                // FileApi.Handle() is documented never to throw, and its own catch-all proves it
                // for every input it has been given. This process is the Windows shell, so the
                // claim is belt-and-braced here too: an escaped exception on this thread would
                // take the television down with it.
                envelope = MarwanOs.Files.FileApi.Handle(command);
            }
            catch (Exception ex)
            {
                envelope = "{\"ok\":false" + (reqId == null ? "" : ",\"reqId\":\"" + Esc(reqId) + "\"")
                    + ",\"error\":\"host_exception\",\"detail\":\"" + Esc(ex.Message) + "\"}";
                Log.Write("FS", "Handle('" + cmd + "') THREW (contained): " + ex.ToString());
            }
            if (envelope == null)
                envelope = "{\"ok\":false" + (reqId == null ? "" : ",\"reqId\":\"" + Esc(reqId) + "\"")
                    + ",\"error\":\"host_null\",\"detail\":\"FileApi returned nothing\"}";

            int ms = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
            Interlocked.Increment(ref _served);
            bool ok = envelope.StartsWith("{\"ok\":true", StringComparison.Ordinal);
            // Successful listings are the shell's chattiest traffic by an order of magnitude
            // (fs.watch polls while a folder is open). Log the shape, never the payload.
            string tail = ok ? " (" + envelope.Length + " bytes)" : "  " + Trim(envelope, 220);
            Log.Write("FS", (ok ? "ok  " : "ERR ") + (cmd == null ? "?" : cmd)
                + " reqId=" + (reqId == null ? "-" : reqId) + " in " + ms + " ms" + tail);

            try { if (_reply != null) _reply(reqId, envelope); }
            catch (Exception ex) { Log.Write("FS", "reply dispatch threw (swallowed): " + ex.Message); }
        }

        static string Trim(string s, int n)
        {
            if (s == null) return "";
            return s.Length <= n ? s : s.Substring(0, n) + "...";
        }

        internal static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder b = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') { b.Append('\\').Append(c); }
                else if (c == '\n') b.Append("\\n");
                else if (c == '\r') b.Append("\\r");
                else if (c == '\t') b.Append("\\t");
                else if (c < ' ') b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else b.Append(c);
            }
            return b.ToString();
        }
    }

    /// <summary>
    /// The WebView2 host object the explorer probes for second, after window.mosFileApi and
    /// before the postMessage channel.
    ///
    /// READ THIS BEFORE ENABLING IT. AddHostObjectToScript dispatches every proxy call onto the
    /// thread that created the CoreWebView2 - the UI thread. The JavaScript side is asynchronous
    /// (the proxy returns a promise), which makes it *look* safe, but the host side is not: while
    /// Handle() is inside a directory sweep the message pump is not running, so the pad is dead
    /// and the screen is frozen. That is the exact failure this shell cannot have.
    ///
    /// So it is registered only under --file-host-object, which exists to prove the transport
    /// works and to leave the door open if a future FileApi is provably non-blocking. The
    /// shipping default is the {type:"fs"} message channel, which lands on FileWorker's thread.
    /// </summary>
    [System.Runtime.InteropServices.ComVisible(true)]
    public class FileApiBridge
    {
        public string Handle(string json)
        {
            try { return MarwanOs.Files.FileApi.Handle(json); }
            catch (Exception ex)
            {
                Log.Write("FS", "host-object Handle threw (contained): " + ex.ToString());
                return "{\"ok\":false,\"error\":\"host_exception\",\"detail\":\""
                    + FileWorker.Esc(ex.Message) + "\"}";
            }
        }
    }

    #endregion

    #region LibraryApi worker  (the home rail's channel into what is installed)

    /// <summary>
    /// Runs MarwanOs.Library.LibraryApi.Handle() off the UI thread, on the same serial-queue pattern
    /// SysWorker and FileWorker use, and on a queue of its own for the same reason FileWorker has
    /// one: a cold library scan is measured in seconds, and if it shared the explorer's queue a
    /// scan started by the home rail would sit in front of every directory read the human is
    /// waiting on. Three small queues cost a hundred lines each and remove the coupling entirely.
    ///
    /// WHY IT MUST NOT RUN ON THE UI THREAD
    /// lib.scan walks Steam's appmanifests, Epic's and GOG's registry, the Start Menu's .lnk
    /// files and the package manager, and lib.icon renders HICONs to PNG. On the UI thread that
    /// is a frozen television: the pad stops moving focus because the message pump is inside the
    /// call. Off it, a slow scan delays only later library commands. LibraryApi runs lib.scan as
    /// its own background job on top of that, so the queue is usually free again immediately -
    /// the page is handed a jobId and polls job.status through this same channel.
    ///
    /// STA for the same reason the other two workers are STA: LibraryApi reaches IShellLinkW,
    /// ShellExecuteEx and IApplicationActivationManager. It does put each of those on its own
    /// short-lived STA thread (LSta.Run) so it is correct either way, but a worker thread that is
    /// already STA is one marshalling hop that never has to happen.
    /// </summary>
    public class LibWorker
    {
        /// <summary>One queued request. A pair, not a delimited string: a reqId is page-supplied
        /// text and any separator picked here would be a separator a page could also send.</summary>
        sealed class Item
        {
            public readonly string ReqId;
            public readonly string Command;
            public Item(string reqId, string command) { ReqId = reqId; Command = command; }
        }

        readonly Queue<Item> _q = new Queue<Item>();
        readonly object _gate = new object();
        readonly Action<string, string> _reply;              // (reqId, LibraryApi envelope) ON THE WORKER THREAD
        Thread _thread;
        volatile bool _stop;
        long _served;

        public long Served { get { return Interlocked.Read(ref _served); } }

        public LibWorker(Action<string, string> reply)
        {
            _reply = reply;
        }

        public void Start()
        {
            if (_thread != null) return;
            _stop = false;
            _thread = new Thread(new ThreadStart(Loop));
            _thread.IsBackground = true;
            _thread.Name = "MarwanLibraryApi";
            try { _thread.SetApartmentState(ApartmentState.STA); }
            catch (Exception ex) { Log.Write("LIB", "could not set STA on the library worker thread: " + ex.Message); }
            _thread.Start();
            Log.Write("LIB", "LibraryApi worker thread started (STA, serial queue)");
        }

        public void Stop()
        {
            _stop = true;
            lock (_gate) { Monitor.PulseAll(_gate); }
        }

        /// <summary>Queue one raw LibraryApi command (the inner object, not the lib envelope).</summary>
        public void Post(string reqId, string commandJson)
        {
            if (string.IsNullOrEmpty(commandJson)) return;
            lock (_gate)
            {
                // Shallower than FileWorker's 64 on purpose: the library's traffic is a handful of
                // commands plus a job poll every 350 ms. Anything past 32 deep is a page in a loop,
                // and the oldest request in that queue is the one nothing is waiting for any more.
                if (_q.Count > 32)
                {
                    Log.Write("LIB", "request queue over 32 deep - dropping the oldest");
                    _q.Dequeue();
                }
                _q.Enqueue(new Item(reqId, commandJson));
                Monitor.Pulse(_gate);
            }
        }

        void Loop()
        {
            while (!_stop)
            {
                Item item = null;
                try
                {
                    lock (_gate)
                    {
                        while (!_stop && _q.Count == 0) Monitor.Wait(_gate, 500);
                        if (_stop) break;
                        item = _q.Dequeue();
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("LIB", "queue wait threw (swallowed): " + ex.Message);
                    continue;
                }
                if (item == null) continue;
                Serve(item.ReqId, item.Command);
            }
            Log.Write("LIB", "LibraryApi worker thread stopped after " + Served + " requests");
        }

        void Serve(string reqId, string command)
        {
            string cmd = Json.Str(command, "cmd");
            string envelope;
            DateTime t0 = DateTime.UtcNow;
            try
            {
                // LibraryApi.Handle() is documented never to throw and its own catch-all proves
                // it. This process is the Windows shell, so the claim is belt-and-braced here as
                // well: an escaped exception on this thread would take the television down.
                envelope = MarwanOs.Library.LibraryApi.Handle(command);
            }
            catch (Exception ex)
            {
                envelope = "{\"ok\":false" + (reqId == null ? "" : ",\"reqId\":\"" + FileWorker.Esc(reqId) + "\"")
                    + ",\"error\":\"host_exception\",\"detail\":\"" + FileWorker.Esc(ex.Message) + "\"}";
                Log.Write("LIB", "Handle('" + cmd + "') THREW (contained): " + ex.ToString());
            }
            if (envelope == null)
                envelope = "{\"ok\":false" + (reqId == null ? "" : ",\"reqId\":\"" + FileWorker.Esc(reqId) + "\"")
                    + ",\"error\":\"host_null\",\"detail\":\"LibraryApi returned nothing\"}";

            int ms = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
            Interlocked.Increment(ref _served);
            bool ok = envelope.StartsWith("{\"ok\":true", StringComparison.Ordinal);
            // NEVER the payload. A lib.list with icons is a megabyte of base64 and a lib.scan
            // result names every game on the disk; either one would drown the log and neither
            // tells you anything the length and the timing do not. Failures are different: the
            // envelope is then a short error code and a sentence, and that is worth having.
            string tail = ok ? " (" + envelope.Length + " bytes)" : "  " + Trim(envelope, 220);
            Log.Write("LIB", (ok ? "ok  " : "ERR ") + (cmd == null ? "?" : cmd)
                + " reqId=" + (reqId == null ? "-" : reqId) + " in " + ms + " ms" + tail);

            try { if (_reply != null) _reply(reqId, envelope); }
            catch (Exception ex) { Log.Write("LIB", "reply dispatch threw (swallowed): " + ex.Message); }
        }

        static string Trim(string s, int n)
        {
            if (s == null) return "";
            return s.Length <= n ? s : s.Substring(0, n) + "...";
        }
    }

    #endregion

    #region Browser  (a SECOND WebView2, for web content)

    /// <summary>
    /// ════════════════════════════════════════════════════════════════════════════════════
    /// THE SPLIT. This is the crux of the browser's design; everything else follows from it.
    /// ════════════════════════════════════════════════════════════════════════════════════
    ///
    /// There are two entirely separate WebView2 worlds in this process.
    ///
    ///   1. THE SHELL WEBVIEW  (HostForm._web, the WinForms control)
    ///      Draws index.html and nothing else: the home rail, Settings, the on-screen
    ///      keyboard, the file explorer, and - for the browser - the whole of its chrome.
    ///      The tab strip, the address bar, the TLS indicator, the history list, the start
    ///      page of pinned tiles and the hint bar are all ordinary DOM in index.html. It
    ///      keeps its kiosk hardening exactly as it was and never loads a remote origin.
    ///
    ///   2. THE CONTENT WEBVIEWS  (this class)
    ///      One CoreWebView2Controller per tab, all on a second CoreWebView2Environment with
    ///      its own user-data folder, parented to a Panel the host positions inside the
    ///      shell's layout. These load the actual web. They legitimately need script,
    ///      storage, media and cookies; none of the shell's hardening applies to them and
    ///      none of it is weakened for them either.
    ///
    /// WHY NOT AN IFRAME. An <iframe> inside index.html would be the obvious answer and it
    /// cannot work: index.html cannot script into a cross-origin document, so it could never
    /// build a focus list, draw a focus ring, or click a link inside youtube.com. Spatial
    /// navigation is the entire product here, and it requires code running INSIDE the page.
    /// AddScriptToExecuteOnDocumentCreatedAsync gives exactly that, at the top of every
    /// document, on every origin - and it is only available to a WebView the host owns.
    ///
    /// WHY A SECOND ENVIRONMENT AND NOT A SECOND CONTROLLER ON THE FIRST ONE. Tabs share one
    /// environment with each other (the requirement, and the only way cookies and logins
    /// survive a tab switch), but they do NOT share the shell's. An environment is a browser
    /// process tree: sharing one would put the shell's renderer, the GPU process and the
    /// network service in the same failure domain as whatever the human just browsed to. The
    /// second environment costs one extra browser process, perhaps 60 MB, and buys the
    /// property this whole machine is built around - a page cannot take the television down.
    ///
    /// WHY A PANEL AND NOT A LAYER. A CoreWebView2Controller is a real child HWND, not a
    /// composited surface, so it cannot be interleaved with the shell page's DOM: nothing
    /// index.html draws can appear on top of it. Rather than pretend otherwise, the content
    /// view is a RECTANGLE the shell page reserves in its own layout, and any shell UI that
    /// needs the whole screen (the keyboard, the tab switcher, the history list) hides the
    /// content view for as long as it is up. That is an honest, debuggable arrangement;
    /// the alternative is CoreWebView2CompositionController and a Windows.UI.Composition
    /// visual tree, which is a great deal of interop for a modal keyboard nobody wants to
    /// see the page behind anyway.
    ///
    /// WHO OWNS INPUT. The pad is read by the host over raw HID and delivered to the SHELL
    /// page, exactly as before - there is still only one input authority. When the browser's
    /// content pane has focus the shell page relays the action back down here
    /// ({type:"browser.pad"}), and this class forwards it to the active tab's injected
    /// script. The analog sticks are the one exception: they are streamed straight from the
    /// pad timer to the content view at 30 Hz, because routing 30 messages a second through
    /// the shell page to move a cursor would be silly.
    ///
    /// KEYBOARD FOCUS deliberately never moves to a content view. The shell WebView keeps it,
    /// which is what keeps Esc/F2/F3 working through the shell's accelerator hook. As a
    /// second line of defence the same hook is installed on every content controller, so the
    /// native escape hatches survive even if focus ends up somewhere unexpected.
    /// </summary>
    public class BrowserHost
    {
        /// <summary>
        /// Four. Each tab is a CoreWebView2Controller, and each of those is at minimum one
        /// renderer process plus its own document, compositor surface and JS heap; four tabs
        /// of ordinary modern web is comfortably 700 MB. This machine is a television, not a
        /// workstation, and a shell that gets OOM-killed is a black screen with no way back.
        /// Four is also about the limit of what can be told apart on a tab strip read from
        /// three metres. Opening a fifth reuses the oldest inactive tab rather than refusing,
        /// because "no" is a worse answer than "here, in this one".
        /// </summary>
        public const int MaxTabs = 4;

        public class Tab
        {
            public int Id;
            public CoreWebView2Controller Ctl;
            public CoreWebView2 Core;
            public string Url = "";
            public string Title = "";
            public string Favicon = "";
            public bool Secure;
            public bool Loading;
            public bool Crashed;
            public double Zoom = 1.0;
            public DateTime Touched = DateTime.UtcNow;
        }

        readonly Control _parent;                 // the Panel the content views live in
        readonly Action<string> _toPage;          // post a JSON string to the SHELL page
        readonly string _script;                  // ui/mosnav.js, injected into every document
        readonly string _userData;
        readonly EventHandler<CoreWebView2AcceleratorKeyPressedEventArgs> _accel;

        CoreWebView2Environment _env;
        readonly List<Tab> _tabs = new List<Tab>();
        int _nextId = 1;
        Tab _active;
        bool _opening;

        public bool Open;                          // the browser is the visible thing
        public bool ContentFocused;                // the pad's actions belong to the page
        public bool FullScreen;

        public BrowserHost(Control parent, Action<string> toPage, string script, string userData,
                           EventHandler<CoreWebView2AcceleratorKeyPressedEventArgs> accel)
        {
            _parent = parent;
            _toPage = toPage;
            _script = script;
            _userData = userData;
            _accel = accel;
        }

        public int TabCount { get { return _tabs.Count; } }
        public Tab Active { get { return _active; } }

        // ── Environment ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Create the content environment on first use, not at startup: a shell that never
        /// opens the browser should never pay for a second browser process.
        /// </summary>
        void EnsureEnvironment(Action then)
        {
            if (_env != null) { then(); return; }
            if (_opening) return;
            _opening = true;

            CoreWebView2EnvironmentOptions o = new CoreWebView2EnvironmentOptions();
            // Not a security setting, and applied to the CONTENT environment only. Every
            // video on this machine is started by a pad button that becomes a synthesised
            // click, and a synthesised click is not a user activation as far as Chromium is
            // concerned - so without this, the play button works and the video does not.
            // Nothing here disables web security, site isolation or the sandbox.
            o.AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required";

            // Chromium extensions, on the CONTENT environment only. The shell's own WebView
            // never gets this: the chrome is our UI and nothing third-party belongs in it.
            //
            // This flag is fixed for the lifetime of the browser process the environment
            // starts. It cannot be turned on later, and attaching to an already-running
            // environment that was created with a different value fails outright with
            // ERROR_INVALID_STATE - which is why it is set here, before anything is created,
            // rather than at the point the extensions are actually loaded.
            //
            // While it is false (the default), AddBrowserExtensionAsync fails with
            // ERROR_NOT_SUPPORTED and GetBrowserExtensionsAsync returns nothing.
            try { o.AreBrowserExtensionsEnabled = true; }
            catch (Exception ex)
            {
                // Only reachable on an SDK generation older than ICoreWebView2EnvironmentOptions6.
                Log.Write("BROWSER", "extensions unsupported by this SDK: " + ex.Message);
            }

            Log.Write("BROWSER", "creating the content environment (user data: " + _userData + ")");
            try { Directory.CreateDirectory(_userData); }
            catch (Exception ex) { Log.Write("BROWSER", "WARN: content user-data folder: " + ex.Message); }

            Task<CoreWebView2Environment> t = CoreWebView2Environment.CreateAsync(null, _userData, o);
            t.ContinueWith(delegate(Task<CoreWebView2Environment> done)
            {
                _opening = false;
                if (done.IsFaulted || done.Result == null)
                {
                    Log.Write("BROWSER", "FATAL for the browser only: content environment failed: "
                        + (done.Exception == null ? "(no exception)" : done.Exception.GetBaseException().Message));
                    Say("{\"type\":\"browser\",\"ev\":\"unavailable\",\"detail\":\""
                        + Esc(done.Exception == null ? "unknown" : done.Exception.GetBaseException().Message) + "\"}");
                    return;
                }
                _env = done.Result;
                Log.Write("BROWSER", "content environment ready, runtime " + _env.BrowserVersionString);
                then();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // ── Tabs ────────────────────────────────────────────────────────────────────────

        public void OpenBrowser(string url)
        {
            Open = true;
            EnsureEnvironment(delegate
            {
                if (_tabs.Count == 0) NewTab(url);
                else { Activate(_active != null ? _active.Id : _tabs[0].Id); if (!string.IsNullOrEmpty(url)) Navigate(url); }
                Say("{\"type\":\"browser\",\"ev\":\"opened\"}");
                PushTabs();
                // Closing the browser does not stop a download - the tabs stay loaded and so
                // does whatever they were fetching - so coming back has to show what is there.
                RefreshDownloads("browser opened");
            });
        }

        public void CloseBrowser()
        {
            Open = false;
            ContentFocused = false;
            FullScreen = false;
            /* Tabs are NOT destroyed. Coming back to a browser that lost every page is the
               single most irritating thing a television browser can do, and four idle
               renderers cost far less than the human's place in a video. They are torn down
               on {type:"browser.quit"} and at shell exit. */
            foreach (Tab t in _tabs) Visible(t, false);
            _parent.Visible = false;
            Say("{\"type\":\"browser\",\"ev\":\"closed\"}");
        }

        public void Quit()
        {
            Open = false;
            ContentFocused = false;
            List<Tab> all = new List<Tab>(_tabs);
            foreach (Tab t in all) Destroy(t);
            _tabs.Clear();
            _active = null;
            _parent.Visible = false;
            Log.Write("BROWSER", "every tab closed; the content processes are gone");
            Say("{\"type\":\"browser\",\"ev\":\"closed\"}");
            PushTabs();
            // A download belongs to the WebView that started it. With every one of them torn
            // down, whatever was in flight has stopped - re-read rather than assume, so the
            // list says what really happened instead of what we expected.
            RefreshDownloads("tabs closed");
        }

        public void NewTab(string url)
        {
            if (_env == null) { EnsureEnvironment(delegate { NewTab(url); }); return; }

            if (_tabs.Count >= MaxTabs)
            {
                // Reuse the least recently touched tab that is not the active one.
                Tab victim = null;
                foreach (Tab t in _tabs)
                    if (t != _active && (victim == null || t.Touched < victim.Touched)) victim = t;
                if (victim == null) victim = _tabs[0];
                Log.Write("BROWSER", "tab cap of " + MaxTabs + " reached - reusing tab " + victim.Id);
                Activate(victim.Id);
                Navigate(url);
                return;
            }

            Tab tab = new Tab();
            tab.Id = _nextId++;
            _tabs.Add(tab);
            Log.Write("BROWSER", "creating tab " + tab.Id + " for " + (url == null ? "(blank)" : url));

            Task<CoreWebView2Controller> t2 = _env.CreateCoreWebView2ControllerAsync(_parent.Handle);
            t2.ContinueWith(delegate(Task<CoreWebView2Controller> done)
            {
                if (done.IsFaulted || done.Result == null)
                {
                    Log.Write("BROWSER", "tab " + tab.Id + " controller failed: "
                        + (done.Exception == null ? "?" : done.Exception.GetBaseException().Message));
                    _tabs.Remove(tab);
                    PushTabs();
                    return;
                }
                Wire(tab, done.Result, url);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // ── Extensions ──────────────────────────────────────────────────────────────────

        bool _extensionsDone;
        // On-disk name, not the product name - see OnDisk.Brand.
        public static string ExtensionsFolder = OnDisk.Root + @"\extensions";

        /// <summary>
        /// Load every unpacked extension in ExtensionsFolder, once, as soon as there is a
        /// CoreWebView2 to hang them off.
        ///
        /// Why it is here and not next to the environment: AddBrowserExtensionAsync lives on
        /// CoreWebView2.Profile, and a profile only exists once a CoreWebView2 does. The
        /// environment alone is not enough.
        ///
        /// Each child folder of ExtensionsFolder is one extension and must contain
        /// manifest.json AT ITS TOP LEVEL. That is worth stating because it is the usual
        /// mistake: several projects ship a zip with one more folder inside it
        /// (uBlock0_x.y.z.chromium.zip unpacks to uBlock0.chromium/), and passing the
        /// wrapper rather than the folder with the manifest in it fails with
        /// ERROR_FILE_NOT_FOUND. So the manifest is looked for one level down as well, and
        /// the right folder is used.
        ///
        /// Failures are reported to the shell page, never swallowed. An ad blocker that
        /// silently did not load is a browser that quietly shows adverts on a television,
        /// and the human has no way to tell that from one that loaded and found nothing to
        /// block.
        /// </summary>
        void LoadExtensions(CoreWebView2 core)
        {
            // Kept for every later command: the profile is the only handle on the installed
            // set, and it exists only once a CoreWebView2 does.
            if (_profile == null) { try { _profile = core.Profile; } catch { } }

            if (_extensionsDone) { PushExtensions("another tab"); return; }
            _extensionsDone = true;

            string root = ExtensionsFolder;
            if (_profile == null)
            {
                Log.Write("BROWSER", "no profile, so no extensions");
                ReportExtension(null, false, "this runtime exposes no profile, so extensions cannot be loaded");
                return;
            }

            if (!Directory.Exists(root))
            {
                Log.Write("BROWSER", "no extensions folder at " + root + " yet; nothing to load from disk");
                PushExtensions("startup");
                return;
            }

            string[] dirs;
            try { dirs = Directory.GetDirectories(root); }
            catch (Exception ex)
            {
                Log.Write("BROWSER", "cannot read " + root + ": " + ex.Message);
                ReportExtension(null, false, "the extensions folder could not be read: " + ex.Message);
                return;
            }
            if (dirs.Length == 0)
            {
                Log.Write("BROWSER", "extensions folder " + root + " is empty");
                PushExtensions("startup");
                return;
            }

            for (int i = 0; i < dirs.Length; i++) AddOneExtension(_profile, dirs[i]);
            PushExtensions("startup");
        }

        void AddOneExtension(CoreWebView2Profile profile, string dir)
        {
            string name = Path.GetFileName(dir);
            string folder = dir;

            if (!File.Exists(Path.Combine(folder, "manifest.json")))
            {
                // The one-folder-too-deep case described above.
                string found = null;
                try
                {
                    string[] inner = Directory.GetDirectories(dir);
                    for (int i = 0; i < inner.Length && found == null; i++)
                        if (File.Exists(Path.Combine(inner[i], "manifest.json"))) found = inner[i];
                }
                catch { }

                if (found == null)
                {
                    Log.Write("BROWSER", "extension '" + name + "': no manifest.json in " + dir);
                    ReportExtension(name, false, "there is no manifest.json in that folder");
                    return;
                }
                folder = found;
            }

            Task<CoreWebView2BrowserExtension> t;
            try { t = profile.AddBrowserExtensionAsync(folder); }
            catch (Exception ex)
            {
                Log.Write("BROWSER", "extension '" + name + "' threw immediately: " + ex.Message);
                ReportExtension(name, false, Describe(ex));
                return;
            }

            t.ContinueWith(delegate(Task<CoreWebView2BrowserExtension> done)
            {
                if (done.IsFaulted || done.Result == null)
                {
                    Exception ex = done.Exception == null ? null : done.Exception.GetBaseException();
                    Log.Write("BROWSER", "extension '" + name + "' FAILED: "
                        + (ex == null ? "(no exception)" : ex.ToString()));
                    ReportExtension(name, false, ex == null ? "it did not load" : Describe(ex));
                    return;
                }
                CoreWebView2BrowserExtension x = done.Result;
                Log.Write("BROWSER", "extension loaded: " + x.Name + " (id=" + x.Id
                    + ", enabled=" + x.IsEnabled + ") from " + folder);
                ReportExtension(x.Name, true, null);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>The HRESULT is the diagnostic here, so it is kept and named.</summary>
        static string Describe(Exception ex)
        {
            int hr = 0;
            try { hr = System.Runtime.InteropServices.Marshal.GetHRForException(ex); }
            catch { }
            string plain;
            switch ((uint)hr)
            {
                case 0x80070002: plain = "the folder has no valid extension manifest in it"; break;
                case 0x80070032: plain = "this build has browser extensions switched off"; break;
                case 0x80070005: plain = "the folder could not be read"; break;
                default:         plain = ex.Message; break;
            }
            return plain + " (0x" + ((uint)hr).ToString("X8") + ")";
        }

        void ReportExtension(string name, bool ok, string detail)
        {
            if (!ok)
            {
                // Kept, so the list can show a folder that is on disk and did NOT load next
                // to the ones that did. A failure that only ever existed as a toast is a
                // failure nobody can go back and read.
                bool seen = false;
                for (int i = 0; i < _extFailures.Count; i++)
                    if (_extFailures[i].Name == name) { _extFailures[i].Detail = detail; seen = true; break; }
                if (!seen && name != null)
                {
                    ExtFail f = new ExtFail();
                    f.Name = name; f.Detail = detail;
                    _extFailures.Add(f);
                }
            }
            Say("{\"type\":\"browser\",\"ev\":\"extension\""
                + ",\"name\":\"" + Esc(name == null ? "extensions" : name) + "\""
                + ",\"ok\":" + (ok ? "true" : "false")
                + ",\"detail\":\"" + Esc(detail == null ? "" : detail) + "\"}");
        }

        // ── Installing extensions ───────────────────────────────────────────────────────

        /// <summary>
        /// Adding an extension to a television, without a mouse, a file dialog or a web
        /// store. The three things that make this possible at all:
        ///
        ///   * ADD IS A RUNTIME CALL. CoreWebView2Profile.AddBrowserExtensionAsync takes a
        ///     folder holding a manifest.json and loads it into every document in the
        ///     profile immediately, and the profile remembers it - so an extension added
        ///     from the sofa is running before the human puts the pad down, and is still
        ///     there next boot. Enable and Remove are the same shape. Nothing here needs a
        ///     restart, and nothing here edits a preferences file behind Chromium's back.
        ///   * THE FILE CAN COME FROM THE BROWSER ITSELF. Most extensions worth having ship
        ///     a .zip of the unpacked folder on their releases page, and this console can
        ///     now download one (see the Downloads region). So the flow is: download the
        ///     zip, open Extensions, install it. No typing, no file dialog.
        ///   * A USB STICK IS THE OTHER HALF. Removable drives are scanned too, because a
        ///     machine with no keyboard is exactly the machine somebody hands a stick to.
        ///
        /// WHAT IS DELIBERATELY NOT HERE. There is no Chrome Web Store install: the store
        /// serves .crx to Chrome-branded browsers with a signed request this host cannot
        /// and should not fake, and pretending otherwise would be a button that fails for
        /// reasons nobody in a living room can act on. A .crx that is already ON the disk is
        /// accepted - it is a zip with a signature header, and the header is skipped.
        ///
        /// EVERY archive is unpacked with ZipFile.ExtractToDirectory into a fresh folder
        /// under C:\ArcOS\extensions (OnDisk.Root). That call refuses entries that would land outside the
        /// destination, which is the zip-slip defence; the extension is then only as
        /// trusted as the human who chose it, which is the same deal as on a desktop.
        /// </summary>
        public class ExtFail
        {
            public string Name;
            public string Detail;
        }

        /// <summary>One installable thing found on disk, offered to the human as a row.</summary>
        public class ExtCand
        {
            public string Path;
            public string Name;
            public string Kind;      // zip | crx | folder
            public long Size;
            public string Where;     // "Downloads", "USB stick (E:)", ...
        }

        readonly List<ExtFail> _extFailures = new List<ExtFail>();
        CoreWebView2Profile _profile;

        /// <summary>
        /// The shell page's control channel for extensions. Same shape as the download one:
        /// the sheet is drawn upstairs, this only does the thing.
        /// </summary>
        public void ExtCommand(string act, string id, string path)
        {
            if (_profile == null)
            {
                Log.Write("BROWSER", "extension command '" + act + "' before any tab existed");
                /* "list" is asked automatically the moment the browser opens, which is
                   before the first tab has finished creating its profile. Answering that
                   with an error would put a toast in front of a human who has not pressed
                   anything yet; the empty not-ready list says the same thing quietly, and
                   the real answer follows as soon as a tab exists. Anything else here IS a
                   press, and a press that does nothing has to say why. */
                if (act == "list") { PushExtensions("no profile yet"); return; }
                ExtResult(false, "the browser has not started a page yet, so it has no profile to install into");
                return;
            }

            switch (act)
            {
                case "list":    PushExtensions("asked"); return;
                case "install": InstallExtension(path); return;
                case "enable":  SetExtensionEnabled(id, true); return;
                case "disable": SetExtensionEnabled(id, false); return;
                case "remove":  RemoveExtension(id); return;
                default:
                    Log.Write("BROWSER", "unhandled extension command '" + act + "'");
                    return;
            }
        }

        void ExtResult(bool ok, string detail)
        {
            Say("{\"type\":\"browser\",\"ev\":\"extresult\",\"ok\":" + (ok ? "true" : "false")
                + ",\"detail\":\"" + Esc(detail == null ? "" : detail) + "\"}");
        }

        /// <summary>
        /// True for the extensions the WebView2 runtime injects into every profile on its
        /// own - Microsoft's, not the human's. The match is on the name because their IDs
        /// are not documented to be stable across runtime versions, and the whole point is
        /// that this machine shows nothing of Microsoft's: an extension the console did not
        /// install and whose name says Microsoft or Edge is exactly that class. A
        /// third-party extension the human deliberately installs that happens to have
        /// "Microsoft" in its name is a price worth paying here, because the standing order
        /// is zero Microsoft surface, not "some, if it slipped in helpfully".
        /// </summary>
        static bool IsBuiltinMicrosoft(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            return n.IndexOf("microsoft", StringComparison.Ordinal) >= 0
                || n.IndexOf("edge", StringComparison.Ordinal) >= 0;
        }

        readonly HashSet<string> _builtinsSuppressed = new HashSet<string>();

        /// <summary>
        /// Switch a built-in off, once. A component extension may refuse - EnableAsync can
        /// throw or simply not stick, because these are not ordinary user extensions - and
        /// that is reported plainly rather than hidden: hiding it from the list is the part
        /// that always works, disabling it is the part that depends on the runtime. Either
        /// way the human sees no Microsoft extension in their browser.
        /// </summary>
        void SuppressBuiltin(CoreWebView2BrowserExtension x)
        {
            string id = null;
            try { id = x.Id; } catch { }
            if (id == null || _builtinsSuppressed.Contains(id)) return;
            _builtinsSuppressed.Add(id);

            bool on = true;
            try { on = x.IsEnabled; } catch { }
            Log.Write("BROWSER", "runtime built-in '" + x.Name + "' (id=" + id
                + ") hidden from the extensions list" + (on ? "; asking the runtime to disable it" : "; already disabled"));
            if (!on) return;

            Task t;
            try { t = x.EnableAsync(false); }
            catch (Exception ex) { Log.Write("BROWSER", "built-in '" + x.Name + "' would not disable: " + ex.Message); return; }
            t.ContinueWith(delegate(Task done)
            {
                if (done.IsFaulted)
                    Log.Write("BROWSER", "built-in '" + x.Name + "' disable was refused by the runtime: "
                        + done.Exception.GetBaseException().Message + " (it stays hidden from the list regardless)");
                else
                    Log.Write("BROWSER", "built-in '" + x.Name + "' disabled in every page");
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void WithExtension(string id, Action<CoreWebView2BrowserExtension> then, string what)
        {
            Task<IReadOnlyList<CoreWebView2BrowserExtension>> t;
            try { t = _profile.GetBrowserExtensionsAsync(); }
            catch (Exception ex) { ExtResult(false, "the installed list could not be read: " + Describe(ex)); return; }

            t.ContinueWith(delegate(Task<IReadOnlyList<CoreWebView2BrowserExtension>> done)
            {
                if (done.IsFaulted || done.Result == null)
                {
                    ExtResult(false, "the installed list could not be read");
                    return;
                }
                for (int i = 0; i < done.Result.Count; i++)
                {
                    if (done.Result[i].Id != id) continue;
                    then(done.Result[i]);
                    return;
                }
                Log.Write("BROWSER", what + ": no extension with id " + id + " is installed");
                ExtResult(false, "that extension is not installed any more");
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void SetExtensionEnabled(string id, bool on)
        {
            WithExtension(id, delegate(CoreWebView2BrowserExtension x)
            {
                string name = x.Name;
                Task t;
                try { t = x.EnableAsync(on); }
                catch (Exception ex) { ExtResult(false, name + " could not be turned " + (on ? "on" : "off") + ": " + Describe(ex)); return; }
                t.ContinueWith(delegate(Task done)
                {
                    if (done.IsFaulted)
                    {
                        Log.Write("BROWSER", "extension '" + name + "' enable(" + on + ") FAILED: "
                            + done.Exception.GetBaseException().Message);
                        ExtResult(false, name + " could not be turned " + (on ? "on" : "off"));
                    }
                    else
                    {
                        Log.Write("BROWSER", "extension '" + name + "' is now " + (on ? "ON" : "OFF")
                            + " in every open page");
                        ExtResult(true, name + (on ? " is on" : " is off"));
                    }
                    PushExtensions("enable");
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }, "enable");
        }

        void RemoveExtension(string id)
        {
            WithExtension(id, delegate(CoreWebView2BrowserExtension x)
            {
                string name = x.Name;
                Task t;
                try { t = x.RemoveAsync(); }
                catch (Exception ex) { ExtResult(false, name + " could not be removed: " + Describe(ex)); return; }
                t.ContinueWith(delegate(Task done)
                {
                    if (done.IsFaulted)
                    {
                        Log.Write("BROWSER", "extension '" + name + "' remove FAILED: "
                            + done.Exception.GetBaseException().Message);
                        ExtResult(false, name + " could not be removed");
                        PushExtensions("remove");
                        return;
                    }
                    Log.Write("BROWSER", "extension '" + name + "' removed from the profile");
                    // And from disk, but ONLY out of the folder this host owns. An extension
                    // the human pointed at on a USB stick or in their own folder is theirs;
                    // deleting it because they turned it off here would be theft.
                    string mine = FolderWeInstalledInto(name);
                    if (mine != null)
                    {
                        try { Directory.Delete(mine, true); Log.Write("BROWSER", "deleted " + mine); }
                        catch (Exception ex) { Log.Write("BROWSER", "could not delete " + mine + ": " + ex.Message); }
                    }
                    ExtResult(true, name + " was removed");
                    PushExtensions("remove");
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }, "remove");
        }

        /// <summary>
        /// The folder under our own extensions root whose manifest carries this name, or
        /// null. Matching on the manifest rather than on a remembered path so that a folder
        /// installed by an earlier run of the shell is still recognised as ours.
        /// </summary>
        string FolderWeInstalledInto(string name)
        {
            if (string.IsNullOrEmpty(name) || !Directory.Exists(ExtensionsFolder)) return null;
            string[] dirs;
            try { dirs = Directory.GetDirectories(ExtensionsFolder); }
            catch { return null; }
            for (int i = 0; i < dirs.Length; i++)
            {
                string folder = ManifestFolder(dirs[i]);
                if (folder == null) continue;
                if (string.Equals(ManifestName(folder, Path.GetFileName(dirs[i])), name, StringComparison.OrdinalIgnoreCase))
                    return dirs[i];
            }
            return null;
        }

        void InstallExtension(string path)
        {
            if (string.IsNullOrEmpty(path)) { ExtResult(false, "no file was chosen"); return; }
            Log.Write("BROWSER", "installing an extension from " + path);

            string folder = null;      // the folder holding manifest.json, ready to be added
            string staged = null;      // what to delete if the add fails

            try
            {
                if (Directory.Exists(path))
                {
                    string src = ManifestFolder(path);
                    if (src == null) { ExtResult(false, "there is no manifest.json in that folder, so it is not an extension"); return; }
                    // Copied in, not added in place: a folder on a USB stick disappears when
                    // the stick does, and an extension whose files vanish takes the browser's
                    // extension host down with it on the next start.
                    staged = FreshExtensionFolder(ManifestName(src, Path.GetFileName(path)));
                    CopyTree(src, staged);
                    folder = staged;
                }
                else if (File.Exists(path))
                {
                    string ext = Path.GetExtension(path).ToLowerInvariant();
                    if (ext != ".zip" && ext != ".crx")
                    {
                        ExtResult(false, "an extension is a .zip or .crx file, or a folder with a manifest.json in it");
                        return;
                    }
                    staged = Unpack(path);
                    string src = ManifestFolder(staged);
                    if (src == null)
                    {
                        Directory.Delete(staged, true);
                        ExtResult(false, "there is no manifest.json inside that file, so it is not an extension");
                        return;
                    }
                    // The usual packaging mistake: the zip contains ONE folder and the
                    // manifest is inside it. Move that folder up so the extension root is
                    // the folder we add, not its parent.
                    if (!string.Equals(src, staged, StringComparison.OrdinalIgnoreCase))
                    {
                        string better = FreshExtensionFolder(ManifestName(src, Path.GetFileNameWithoutExtension(path)));
                        CopyTree(src, better);
                        try { Directory.Delete(staged, true); } catch { }
                        staged = better;
                    }
                    else
                    {
                        string named = FreshExtensionFolder(ManifestName(src, Path.GetFileNameWithoutExtension(path)));
                        try { Directory.Delete(named, true); } catch { }
                        try { Directory.Move(staged, named); staged = named; } catch { }
                    }
                    folder = staged;
                }
                else
                {
                    ExtResult(false, "that file is not there any more");
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Write("BROWSER", "extension staging failed: " + ex);
                if (staged != null) { try { Directory.Delete(staged, true); } catch { } }
                ExtResult(false, "it could not be unpacked: " + ex.Message);
                return;
            }

            string display = ManifestName(folder, Path.GetFileName(folder));
            string cleanup = staged;

            Task<CoreWebView2BrowserExtension> add;
            try { add = _profile.AddBrowserExtensionAsync(folder); }
            catch (Exception ex)
            {
                if (cleanup != null) { try { Directory.Delete(cleanup, true); } catch { } }
                Log.Write("BROWSER", "AddBrowserExtensionAsync threw for " + folder + ": " + ex);
                ExtResult(false, display + " was not accepted: " + Describe(ex));
                return;
            }

            add.ContinueWith(delegate(Task<CoreWebView2BrowserExtension> done)
            {
                if (done.IsFaulted || done.Result == null)
                {
                    Exception ex = done.Exception == null ? null : done.Exception.GetBaseException();
                    Log.Write("BROWSER", "extension install FAILED for " + folder + ": "
                        + (ex == null ? "(no exception)" : ex.ToString()));
                    if (cleanup != null) { try { Directory.Delete(cleanup, true); } catch { } }
                    ExtResult(false, display + " was not accepted: " + (ex == null ? "the browser refused it" : Describe(ex)));
                    PushExtensions("install failed");
                    return;
                }
                Log.Write("BROWSER", "extension INSTALLED: " + done.Result.Name + " (id=" + done.Result.Id
                    + ") from " + folder + " - live in every open page, and remembered for next boot");
                ExtResult(true, done.Result.Name + " is installed and running");
                PushExtensions("installed");
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>A new, empty folder under the extensions root, named after the extension.</summary>
        string FreshExtensionFolder(string name)
        {
            Directory.CreateDirectory(ExtensionsFolder);
            string slug = Slug(name);
            string target = Path.Combine(ExtensionsFolder, slug);
            int n = 2;
            while (Directory.Exists(target) && n < 100)
                target = Path.Combine(ExtensionsFolder, slug + "-" + n++);
            Directory.CreateDirectory(target);
            return target;
        }

        static string Slug(string s)
        {
            if (string.IsNullOrEmpty(s)) return "extension";
            StringBuilder b = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c)) b.Append(char.ToLowerInvariant(c));
                else if (b.Length > 0 && b[b.Length - 1] != '-') b.Append('-');
            }
            string outp = b.ToString().Trim('-');
            if (outp.Length == 0) outp = "extension";
            if (outp.Length > 48) outp = outp.Substring(0, 48).Trim('-');
            return outp;
        }

        static void CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            string[] dirs = Directory.GetDirectories(from, "*", SearchOption.AllDirectories);
            for (int i = 0; i < dirs.Length; i++)
                Directory.CreateDirectory(Path.Combine(to, dirs[i].Substring(from.Length).TrimStart('\\', '/')));
            string[] files = Directory.GetFiles(from, "*", SearchOption.AllDirectories);
            for (int i = 0; i < files.Length; i++)
                File.Copy(files[i], Path.Combine(to, files[i].Substring(from.Length).TrimStart('\\', '/')), true);
        }

        /// <summary>
        /// Unpack a .zip, or a .crx - which is a zip with a signature header glued on the
        /// front. The header is parsed for both crx versions rather than guessed at, and if
        /// it is some third thing the local zip signature is searched for instead, because
        /// "this file is not what its extension says" is a better failure than a stack trace.
        /// </summary>
        string Unpack(string file)
        {
            string dest = FreshExtensionFolder(Path.GetFileNameWithoutExtension(file));
            string zip = file;
            string temp = null;

            if (Path.GetExtension(file).ToLowerInvariant() == ".crx")
            {
                byte[] raw = File.ReadAllBytes(file);
                int start = CrxPayloadOffset(raw);
                if (start <= 0 || start >= raw.Length) throw new IOException("that .crx file has no archive inside it");
                temp = Path.Combine(Path.GetTempPath(), "marwanos-ext-" + Guid.NewGuid().ToString("N") + ".zip");
                using (FileStream fs = new FileStream(temp, FileMode.Create, FileAccess.Write))
                    fs.Write(raw, start, raw.Length - start);
                zip = temp;
                Log.Write("BROWSER", "crx header skipped: archive starts at byte " + start + " of " + raw.Length);
            }

            try { ZipFile.ExtractToDirectory(zip, dest); }
            finally { if (temp != null) { try { File.Delete(temp); } catch { } } }
            return dest;
        }

        static int CrxPayloadOffset(byte[] b)
        {
            if (b.Length > 16 && b[0] == 'C' && b[1] == 'r' && b[2] == '2' && b[3] == '4')
            {
                int version = BitConverter.ToInt32(b, 4);
                if (version == 3)
                {
                    int headerLen = BitConverter.ToInt32(b, 8);
                    if (headerLen > 0 && 12 + headerLen < b.Length) return 12 + headerLen;
                }
                else if (version == 2)
                {
                    int keyLen = BitConverter.ToInt32(b, 8);
                    int sigLen = BitConverter.ToInt32(b, 12);
                    if (keyLen >= 0 && sigLen >= 0 && 16 + keyLen + sigLen < b.Length) return 16 + keyLen + sigLen;
                }
            }
            // Fall back to the local file header of the first zip entry.
            int limit = Math.Min(b.Length - 4, 1 << 16);
            for (int i = 0; i < limit; i++)
                if (b[i] == 0x50 && b[i + 1] == 0x4B && b[i + 2] == 0x03 && b[i + 3] == 0x04) return i;
            return -1;
        }

        /// <summary>The folder holding manifest.json: this one, or the single level below it.</summary>
        static string ManifestFolder(string dir)
        {
            try
            {
                if (File.Exists(Path.Combine(dir, "manifest.json"))) return dir;
                string[] inner = Directory.GetDirectories(dir);
                for (int i = 0; i < inner.Length; i++)
                    if (File.Exists(Path.Combine(inner[i], "manifest.json"))) return inner[i];
            }
            catch { }
            return null;
        }

        /// <summary>
        /// The extension's own name, out of its manifest, for the folder and the row. Only
        /// the TOP level of the manifest is looked at: "name" also appears inside
        /// browser_action, commands and content_scripts, and a regex over the whole file
        /// finds whichever comes first. A localised name ("__MSG_extName__") cannot be
        /// resolved without reading the locale files, so the fallback is used instead.
        /// </summary>
        static string ManifestName(string folder, string fallback)
        {
            string name = null;
            try
            {
                string json = File.ReadAllText(Path.Combine(folder, "manifest.json"));
                name = TopLevelString(json, "name");
            }
            catch { }
            if (string.IsNullOrEmpty(name) || name.StartsWith("__MSG_", StringComparison.Ordinal))
                name = fallback;
            return string.IsNullOrEmpty(name) ? "extension" : name.Trim();
        }

        static string TopLevelString(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int depth = 0;
            bool inStr = false, esc = false;
            int i = 0;
            string want = "\"" + key + "\"";
            for (; i < json.Length; i++)
            {
                char c = json[i];
                if (inStr)
                {
                    if (esc) esc = false;
                    else if (c == '\\') esc = true;
                    else if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"')
                {
                    if (depth == 1 && string.CompareOrdinal(json, i, want, 0, want.Length) == 0)
                    {
                        int colon = json.IndexOf(':', i + want.Length);
                        if (colon < 0) return null;
                        int q = json.IndexOf('"', colon + 1);
                        if (q < 0) return null;
                        StringBuilder b = new StringBuilder();
                        for (int j = q + 1; j < json.Length; j++)
                        {
                            if (json[j] == '\\' && j + 1 < json.Length) { b.Append(json[j + 1]); j++; continue; }
                            if (json[j] == '"') break;
                            b.Append(json[j]);
                        }
                        return b.ToString();
                    }
                    inStr = true;
                    continue;
                }
                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
            }
            return null;
        }

        // ── What is installable, and where it is ────────────────────────────────────────

        /// <summary>
        /// Everything on this machine that could be an extension: archives the browser has
        /// downloaded, and anything on a removable drive. Two places only, because a scan
        /// of the whole disk on a television is a spinner nobody asked for, and because
        /// these are the two ways a file gets onto this machine at all.
        /// </summary>
        List<ExtCand> ScanCandidates()
        {
            List<ExtCand> found = new List<ExtCand>();
            string home = null;
            try { home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
            catch { }
            if (!string.IsNullOrEmpty(home))
                ScanOneFolder(found, Path.Combine(home, "Downloads"), "Downloads", 1);

            try
            {
                DriveInfo[] drives = DriveInfo.GetDrives();
                for (int i = 0; i < drives.Length; i++)
                {
                    if (drives[i].DriveType != DriveType.Removable) continue;
                    bool ready = false;
                    try { ready = drives[i].IsReady; } catch { }
                    if (!ready) continue;
                    string label = "";
                    try { label = drives[i].VolumeLabel; } catch { }
                    ScanOneFolder(found, drives[i].RootDirectory.FullName,
                        (string.IsNullOrEmpty(label) ? "USB drive" : label) + " (" + drives[i].Name.TrimEnd('\\') + ")", 2);
                }
            }
            catch (Exception ex) { Log.Write("BROWSER", "removable-drive scan: " + ex.Message); }

            return found;
        }

        void ScanOneFolder(List<ExtCand> into, string dir, string where, int depth)
        {
            if (into.Count >= 24 || depth <= 0 || !Directory.Exists(dir)) return;
            try
            {
                string[] files = Directory.GetFiles(dir);
                for (int i = 0; i < files.Length && into.Count < 24; i++)
                {
                    string ext = Path.GetExtension(files[i]).ToLowerInvariant();
                    if (ext != ".zip" && ext != ".crx") continue;
                    ExtCand c = new ExtCand();
                    c.Path = files[i];
                    c.Name = Path.GetFileName(files[i]);
                    c.Kind = ext.Substring(1);
                    c.Where = where;
                    try { c.Size = new FileInfo(files[i]).Length; } catch { }
                    into.Add(c);
                }

                string[] dirs = Directory.GetDirectories(dir);
                for (int i = 0; i < dirs.Length && into.Count < 24; i++)
                {
                    // An unpacked extension announces itself; anything else is just a folder.
                    if (ManifestFolder(dirs[i]) == null) continue;
                    if (dirs[i].StartsWith(ExtensionsFolder, StringComparison.OrdinalIgnoreCase)) continue;
                    ExtCand c = new ExtCand();
                    c.Path = dirs[i];
                    c.Name = ManifestName(ManifestFolder(dirs[i]), Path.GetFileName(dirs[i]));
                    c.Kind = "folder";
                    c.Where = where;
                    into.Add(c);
                }
            }
            catch (Exception ex) { Log.Write("BROWSER", "scanning " + dir + ": " + ex.Message); }
        }

        public void PushExtensions(string reason)
        {
            if (_profile == null)
            {
                Say("{\"type\":\"browser\",\"ev\":\"extensions\",\"reason\":\"" + Esc(reason)
                    + "\",\"folder\":\"" + Esc(ExtensionsFolder) + "\",\"list\":[],\"candidates\":[],\"ready\":false}");
                return;
            }

            Task<IReadOnlyList<CoreWebView2BrowserExtension>> t;
            try { t = _profile.GetBrowserExtensionsAsync(); }
            catch (Exception ex)
            {
                Log.Write("BROWSER", "GetBrowserExtensionsAsync threw: " + ex.Message);
                return;
            }

            t.ContinueWith(delegate(Task<IReadOnlyList<CoreWebView2BrowserExtension>> done)
            {
                StringBuilder b = new StringBuilder(256);
                b.Append("{\"type\":\"browser\",\"ev\":\"extensions\",\"reason\":\"").Append(Esc(reason))
                 .Append("\",\"folder\":\"").Append(Esc(ExtensionsFolder)).Append("\",\"ready\":true,\"list\":[");
                int n = 0;
                if (!done.IsFaulted && done.Result != null)
                {
                    for (int i = 0; i < done.Result.Count; i++)
                    {
                        CoreWebView2BrowserExtension x = done.Result[i];
                        // The WebView2 runtime ships two extensions of its own - the Edge PDF
                        // viewer and a clipboard helper - baked into every profile. The human
                        // never chose them, cannot use them from a pad, and this machine is
                        // meant to carry no Microsoft surface at all. They are hidden from the
                        // list and, once, switched off; see SuppressBuiltin. Nothing about
                        // "how many extensions are installed" should ever count them.
                        if (IsBuiltinMicrosoft(x.Name)) { SuppressBuiltin(x); continue; }
                        if (n++ > 0) b.Append(',');
                        b.Append("{\"id\":\"").Append(Esc(x.Id)).Append('"')
                         .Append(",\"name\":\"").Append(Esc(x.Name)).Append('"')
                         .Append(",\"enabled\":").Append(x.IsEnabled ? "true" : "false")
                         .Append(",\"ok\":true}");
                    }
                }
                // Folders that are on disk and did not load. They have no id, so the page
                // shows them as a problem to read rather than a switch to flip.
                for (int i = 0; i < _extFailures.Count; i++)
                {
                    if (n++ > 0) b.Append(',');
                    b.Append("{\"id\":\"\",\"name\":\"").Append(Esc(_extFailures[i].Name)).Append('"')
                     .Append(",\"enabled\":false,\"ok\":false,\"detail\":\"")
                     .Append(Esc(_extFailures[i].Detail)).Append("\"}");
                }
                b.Append("],\"candidates\":[");
                List<ExtCand> cands = ScanCandidates();
                for (int i = 0; i < cands.Count; i++)
                {
                    if (i > 0) b.Append(',');
                    b.Append("{\"path\":\"").Append(Esc(cands[i].Path)).Append('"')
                     .Append(",\"name\":\"").Append(Esc(cands[i].Name)).Append('"')
                     .Append(",\"kind\":\"").Append(Esc(cands[i].Kind)).Append('"')
                     .Append(",\"where\":\"").Append(Esc(cands[i].Where)).Append('"')
                     .Append(",\"size\":").Append(cands[i].Size).Append('}');
                }
                b.Append("]}");
                Say(b.ToString());
                Log.Write("BROWSER", "extensions pushed (" + reason + "): " + n + " known, "
                    + cands.Count + " installable file(s) found");
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void Wire(Tab tab, CoreWebView2Controller ctl, string url)
        {
            tab.Ctl = ctl;
            tab.Core = ctl.CoreWebView2;

            // First view: this is the earliest point a profile exists to load them into.
            try { LoadExtensions(tab.Core); }
            catch (Exception ex) { Log.Write("BROWSER", "LoadExtensions threw: " + ex.Message); }

            try { ctl.DefaultBackgroundColor = Color.FromArgb(255, 0x04, 0x06, 0x0B); }
            catch { }
            ctl.Bounds = new Rectangle(0, 0, Math.Max(1, _parent.ClientSize.Width),
                                             Math.Max(1, _parent.ClientSize.Height));
            ctl.IsVisible = false;

            CoreWebView2Settings s = tab.Core.Settings;
            // Chrome the human must not see inside a kiosk. NOTE what is deliberately NOT
            // here: script, storage, cookies, media, images and the sandbox are all left at
            // their defaults, which is to say on. This browser is a browser.
            s.AreDefaultContextMenusEnabled = false;
            s.IsStatusBarEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;
            s.IsPasswordAutosaveEnabled = false;
            s.IsGeneralAutofillEnabled = false;
            s.IsWebMessageEnabled = true;
            s.IsZoomControlEnabled = false;        // zoom is ours, from the pad
            s.IsSwipeNavigationEnabled = false;
            // OFF, and it used to be on. The built-in error page is Chromium's own, and on a
            // WebView2 runtime that is the EDGE error page - Microsoft wordmark, "Edge",
            // buttons the pad cannot press. A dead link on this machine must show OUR page,
            // not Microsoft's, so the failure is reported up to the shell (see the
            // NavigationCompleted handler) and browser.js draws its own reachable surface.
            s.IsBuiltInErrorPageEnabled = false;

            // Downloads. Every one of them is taken off Chromium and given to the shell page;
            // see the Downloads region below for why a television cannot use the built-in
            // flyout. e.Handled = true inside the handler is what actually suppresses it.
            tab.Core.DownloadStarting += delegate(object o, CoreWebView2DownloadStartingEventArgs e)
            {
                StartDownload(tab, e);
            };

            // Camera, microphone, location, notifications... Chromium would draw its own bubble
            // for these, in the child HWND, where the pad cannot reach it. Taken here instead and
            // asked of the human through the shell's own sheet. See the Permissions region.
            try
            {
                tab.Core.PermissionRequested += delegate(object o, CoreWebView2PermissionRequestedEventArgs e)
                {
                    OnPermissionRequested(tab, e);
                };
                Log.Write("PERM", "tab " + tab.Id + " permission prompts are the shell's now");
            }
            catch (Exception ex)
            {
                Log.Write("PERM", "WARN: tab " + tab.Id + " PermissionRequested unavailable on this runtime ("
                    + ex.Message + ") - Edge's own bubble would be drawn instead");
            }

            tab.Core.NavigationStarting += delegate(object o, CoreWebView2NavigationStartingEventArgs e)
            {
                tab.Loading = true;
                tab.Url = e.Uri;
                tab.Secure = IsSecure(e.Uri);
                // The page that asked is on its way out. Answering a prompt that belongs to a
                // document nobody is looking at any more is exactly how a grant ends up on the
                // wrong origin, so anything outstanding for this tab is denied now.
                DenyPending(tab, "navigated to " + e.Uri);
                PushTab(tab);
            };
            tab.Core.NavigationCompleted += delegate(object o, CoreWebView2NavigationCompletedEventArgs e)
            {
                tab.Loading = false;
                bool cb = false, cf = false;
                try { cb = tab.Core.CanGoBack; cf = tab.Core.CanGoForward; } catch { }
                Log.Write("BROWSER", "tab " + tab.Id + " navigation " + (e.IsSuccess ? "ok" : "FAILED " + e.WebErrorStatus)
                    + " canBack=" + cb + " canForward=" + cf + " -> " + tab.Url);
                // With Edge's error page off, a real failure would otherwise leave a blank
                // content view. Tell the shell page so it can draw its own - but ONLY for the
                // statuses that mean "the page could not be reached". A download reports as a
                // failed navigation with ConnectionAborted (the request is handed to the
                // downloader instead), and OperationCanceled is the human pressing Stop or
                // walking away; neither is an error and neither gets an error page.
                if (!e.IsSuccess && tab == _active && IsRealNavError(e.WebErrorStatus))
                {
                    Say("{\"type\":\"browser\",\"ev\":\"loaderror\",\"tab\":" + tab.Id
                        + ",\"status\":\"" + Esc(e.WebErrorStatus.ToString()) + "\""
                        + ",\"detail\":\"" + Esc(NavErrorText(e.WebErrorStatus)) + "\""
                        + ",\"url\":\"" + Esc(tab.Url) + "\"}");
                }
                PushTab(tab);
            };
            tab.Core.SourceChanged += delegate(object o, CoreWebView2SourceChangedEventArgs e)
            {
                try { tab.Url = tab.Core.Source; } catch { }
                tab.Secure = IsSecure(tab.Url);
                PushTab(tab);
            };
            tab.Core.DocumentTitleChanged += delegate(object o, object e)
            {
                try { tab.Title = tab.Core.DocumentTitle; } catch { }
                PushTab(tab);
            };
            tab.Core.HistoryChanged += delegate(object o, object e) { PushTab(tab); };
            // Favicons are fetched HERE and handed up as data: URIs, never as the remote URL
            // the site advertised. Putting a third-party image URL into the shell page's tab
            // strip would make the SHELL's WebView issue a request to that server - the one
            // thing the shell is not allowed to do, and a rule that exists precisely so that
            // "what the shell can talk to" stays a short and auditable list. The content
            // WebView is already talking to that server; letting it do the fetch keeps the
            // request inside the browsing session where it belongs.
            try
            {
                tab.Core.FaviconChanged += delegate(object o, object e) { FetchFavicon(tab); };
            }
            catch (Exception ex) { Log.Write("BROWSER", "no favicon events on this runtime: " + ex.Message); }

            // A page must never be able to spawn a window this shell cannot close.
            tab.Core.NewWindowRequested += delegate(object o, CoreWebView2NewWindowRequestedEventArgs e)
            {
                e.Handled = true;
                Log.Write("BROWSER", "tab " + tab.Id + " asked for a new window: " + e.Uri);
                NewTab(e.Uri);
            };

            tab.Core.ContainsFullScreenElementChanged += delegate(object o, object e)
            {
                bool on = false;
                try { on = tab.Core.ContainsFullScreenElement; } catch { }
                if (tab != _active) return;
                FullScreen = on;
                Log.Write("BROWSER", "tab " + tab.Id + " full screen = " + on);
                Say("{\"type\":\"browser\",\"ev\":\"fullscreen\",\"on\":" + (on ? "true" : "false") + "}");
            };

            // THE isolation guarantee, in one handler. A renderer that crashes, hangs or is
            // killed from Task Manager lands here; the shell is a different process tree
            // entirely and carries on drawing. Nothing in this method exits the process.
            tab.Core.ProcessFailed += delegate(object o, CoreWebView2ProcessFailedEventArgs e)
            {
                string kind = e.ProcessFailedKind.ToString();
                Log.Write("BROWSER", "tab " + tab.Id + " PROCESS FAILED kind=" + kind
                    + " reason=" + e.Reason + " exit=" + e.ExitCode + " desc=" + e.ProcessDescription
                    + "  (the shell is unaffected: separate environment, separate process tree)");
                tab.Crashed = true;
                tab.Loading = false;
                Say("{\"type\":\"browser\",\"ev\":\"crashed\",\"tab\":" + tab.Id
                    + ",\"kind\":\"" + Esc(kind) + "\",\"reason\":\"" + Esc(e.Reason.ToString()) + "\"}");
                PushTab(tab);
            };

            tab.Core.WebMessageReceived += delegate(object o, CoreWebView2WebMessageReceivedEventArgs e)
            {
                string raw = null;
                try { raw = e.TryGetWebMessageAsString(); }
                catch { }
                if (raw == null) { try { raw = e.WebMessageAsJson; } catch { } }
                if (string.IsNullOrEmpty(raw)) return;
                // Straight through to the shell page, with the tab stamped on it. The
                // payload is the injected script's own object; nothing here interprets it,
                // which is what keeps mosnav.js the single source of truth for its protocol.
                if (raw.Length > 1 && raw[0] == '{')
                    Say("{\"type\":\"browser\",\"ev\":\"mosnav\",\"tab\":" + tab.Id + ",\"msg\":" + raw + "}");
            };

            // The native escape hatches, on this controller too. Keyboard focus is never
            // moved to a content view - the shell WebView keeps it, which is what normally
            // routes Esc/F2/F3 to the shell's own hook - but "normally" is not a guarantee
            // worth betting a television on, so the same handler is installed here as well.
            try
            {
                ctl.AcceleratorKeyPressed += _accel;
                Log.Write("BROWSER", "tab " + tab.Id + " escape keys hooked (Esc/F2/F3 work even if this view takes focus)");
            }
            catch (Exception ex) { Log.Write("BROWSER", "could not hook the escape keys on tab " + tab.Id + ": " + ex.Message); }

            Task<string> add = tab.Core.AddScriptToExecuteOnDocumentCreatedAsync(_script);
            add.ContinueWith(delegate(Task<string> d2)
            {
                if (d2.IsFaulted)
                    Log.Write("BROWSER", "tab " + tab.Id + " mosnav injection FAILED: " + d2.Exception.GetBaseException().Message);
                else
                    Log.Write("BROWSER", "tab " + tab.Id + " mosnav injected (id=" + d2.Result + ", "
                        + _script.Length + " bytes)");
                Activate(tab.Id);
                if (!string.IsNullOrEmpty(url)) Navigate(url);
                PushTabs();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>
        /// Pull the favicon out of the CONTENT WebView as bytes and turn it into a data: URI
        /// for the tab strip. See the comment at the FaviconChanged hook-up for why the raw
        /// URL never leaves this class.
        ///
        /// Anything over 48 KB is dropped rather than sent: a site is free to advertise a
        /// 2 MB PNG as its icon, and that would arrive at the shell page as a 2.7 MB
        /// base64 string inside a JSON message posted several times a second while a tab
        /// loads. The tab strip draws it at about twenty pixels.
        /// </summary>
        void FetchFavicon(Tab tab)
        {
            if (tab == null || tab.Core == null) return;
            string uri = null;
            try { uri = tab.Core.FaviconUri; }
            catch { }
            if (string.IsNullOrEmpty(uri)) { tab.Favicon = ""; PushTab(tab); return; }

            Task<System.IO.Stream> t;
            try { t = tab.Core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png); }
            catch (Exception ex) { Log.Write("BROWSER", "favicon read not available: " + ex.Message); return; }

            t.ContinueWith(delegate(Task<System.IO.Stream> done)
            {
                if (done.IsFaulted || done.Result == null) return;
                try
                {
                    using (MemoryStream ms = new MemoryStream())
                    {
                        done.Result.CopyTo(ms);
                        if (ms.Length == 0 || ms.Length > 48 * 1024)
                        {
                            if (ms.Length > 0)
                                Log.Write("BROWSER", "tab " + tab.Id + " favicon is " + ms.Length
                                    + " bytes - too big for the tab strip, ignored");
                            return;
                        }
                        tab.Favicon = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                        Log.Write("BROWSER", "tab " + tab.Id + " favicon fetched inside the content "
                            + "WebView (" + ms.Length + " bytes) and handed up as a data: URI");
                    }
                    PushTab(tab);
                }
                catch (Exception ex) { Log.Write("BROWSER", "favicon decode: " + ex.Message); }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>
        /// A navigation failure that deserves an error page, as opposed to the two that do
        /// not: ConnectionAborted (the request became a download) and OperationCanceled (Stop,
        /// or the human left). Everything else - no such host, refused, timed out, a bad
        /// certificate - is a page the human tried to reach and could not, and silence there
        /// is a blank rectangle with no explanation.
        /// </summary>
        static bool IsRealNavError(CoreWebView2WebErrorStatus s)
        {
            return s != CoreWebView2WebErrorStatus.ConnectionAborted
                && s != CoreWebView2WebErrorStatus.OperationCanceled
                && s != CoreWebView2WebErrorStatus.Unknown;
        }

        /// <summary>The failure as a sentence a human reads, not an enum name.</summary>
        static string NavErrorText(CoreWebView2WebErrorStatus s)
        {
            switch (s)
            {
                case CoreWebView2WebErrorStatus.HostNameNotResolved:
                    return "That address could not be found. Check the spelling, or the connection.";
                case CoreWebView2WebErrorStatus.CannotConnect:
                case CoreWebView2WebErrorStatus.ServerUnreachable:
                    return "The site did not answer. It may be down, or off the network.";
                case CoreWebView2WebErrorStatus.Timeout:
                    return "The site took too long to answer.";
                case CoreWebView2WebErrorStatus.ConnectionReset:
                case CoreWebView2WebErrorStatus.Disconnected:
                    return "The connection dropped while the page was loading.";
                case CoreWebView2WebErrorStatus.CertificateExpired:
                case CoreWebView2WebErrorStatus.CertificateIsInvalid:
                case CoreWebView2WebErrorStatus.CertificateRevoked:
                case CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect:
                    return "The site's security certificate could not be trusted, so it was not opened.";
                case CoreWebView2WebErrorStatus.ValidAuthenticationCredentialsRequired:
                case CoreWebView2WebErrorStatus.ValidProxyAuthenticationRequired:
                    return "The site asked for a sign-in the console could not give it.";
                case CoreWebView2WebErrorStatus.ErrorHttpInvalidServerResponse:
                case CoreWebView2WebErrorStatus.RedirectFailed:
                    return "The site sent something the browser could not make sense of.";
                default:
                    return "The page could not be opened.";
            }
        }

        void Destroy(Tab t)
        {
            // Before the controller goes: a deferral held on a WebView that is being torn down is
            // never completed, and an uncompleted deferral is a leak with a prompt on the other
            // end of it that the human will never be asked.
            DenyPending(t, "the tab was closed");
            try { if (t.Ctl != null) { t.Ctl.AcceleratorKeyPressed -= _accel; t.Ctl.Close(); } }
            catch (Exception ex) { Log.Write("BROWSER", "closing tab " + t.Id + " threw (swallowed): " + ex.Message); }
            t.Ctl = null; t.Core = null;
        }

        public void CloseTab(int id)
        {
            Tab t = Find(id);
            if (t == null) return;
            bool wasActive = t == _active;
            _tabs.Remove(t);
            Destroy(t);
            Log.Write("BROWSER", "tab " + id + " closed; " + _tabs.Count + " left");
            if (wasActive)
            {
                _active = null;
                if (_tabs.Count > 0) Activate(_tabs[_tabs.Count - 1].Id);
                else { _parent.Visible = false; Say("{\"type\":\"browser\",\"ev\":\"empty\"}"); }
            }
            PushTabs();
            RefreshDownloads("tab " + id + " closed");
        }

        public void Activate(int id)
        {
            Tab t = Find(id);
            if (t == null || t.Ctl == null) return;
            foreach (Tab other in _tabs) if (other != t) Visible(other, false);
            _active = t;
            t.Touched = DateTime.UtcNow;
            // `Open` is not enough. The shell page hides the content view whenever
            // something of its own has to cover the screen — the keyboard above all —
            // and that suspend is recorded as _parent.Visible, not as a flag here.
            // Activating a tab while a suspend is up used to set IsVisible = true on
            // it regardless, which put the host's per-tab state at odds with what the
            // page had asked for. Nothing was painted (the parent panel was hidden),
            // so it was invisible in every sense including "invisible when it breaks":
            // a page can trigger this itself through NewWindowRequested, and the tab
            // would then be live the instant anything showed the panel again.
            Visible(t, Open && _parent.Visible);
            Log.Write("BROWSER", "tab " + id + " active"
                + (Open && !_parent.Visible ? " (content suspended; left hidden)" : ""));
            PushTabs();
        }

        void Visible(Tab t, bool on)
        {
            if (t == null || t.Ctl == null) return;
            try { t.Ctl.IsVisible = on; }
            catch (Exception ex) { Log.Write("BROWSER", "IsVisible on tab " + t.Id + ": " + ex.Message); }
        }

        Tab Find(int id)
        {
            foreach (Tab t in _tabs) if (t.Id == id) return t;
            return null;
        }

        // ── Geometry ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The shell page measures the hole it left in its own layout and posts it here.
        /// The panel moves; every controller is then resized to fill the panel, so a tab
        /// that was hidden during a layout change is still the right size when it comes back.
        /// </summary>
        public void SetBounds(int x, int y, int w, int h)
        {
            if (w < 8 || h < 8) return;
            Rectangle want = new Rectangle(x, y, w, h);
            if (_parent.Bounds != want)
            {
                _parent.Bounds = want;
                Log.Write("BROWSER", "content viewport -> " + want);
            }
            Rectangle inner = new Rectangle(0, 0, w, h);
            foreach (Tab t in _tabs)
            {
                if (t.Ctl == null) continue;
                try { if (t.Ctl.Bounds != inner) t.Ctl.Bounds = inner; }
                catch { }
            }
        }

        /// <summary>
        /// Show or hide the whole content rectangle. The shell page calls this whenever
        /// something of its own has to cover the screen - the keyboard, the tab switcher,
        /// the history list, the start page - because a child HWND cannot be drawn over.
        /// </summary>
        public void Show(bool on)
        {
            if (_parent.Visible != on)
            {
                _parent.Visible = on;
                Log.Write("BROWSER", "content viewport " + (on ? "shown" : "hidden"));
            }
            if (on)
            {
                _parent.BringToFront();
                Visible(_active, true);
            }
        }

        // ── Navigation ──────────────────────────────────────────────────────────────────

        public void Navigate(string url)
        {
            if (_active == null || _active.Core == null) { NewTab(url); return; }
            _active.Crashed = false;
            try { _active.Core.Navigate(url); }
            catch (Exception ex)
            {
                Log.Write("BROWSER", "Navigate('" + url + "') threw: " + ex.Message);
                Say("{\"type\":\"browser\",\"ev\":\"navfail\",\"url\":\"" + Esc(url) + "\",\"detail\":\""
                    + Esc(ex.Message) + "\"}");
            }
        }

        public bool GoBack()
        {
            if (_active == null || _active.Core == null) { Log.Write("BROWSER", "back: no active tab"); return false; }
            bool can = false;
            string why = "";
            try { can = _active.Core.CanGoBack; }
            catch (Exception ex) { why = " (CanGoBack threw: " + ex.Message + ")"; }
            if (!can) { Log.Write("BROWSER", "back: tab " + _active.Id + " has no history left" + why); return false; }
            try { _active.Core.GoBack(); }
            catch (Exception ex) { Log.Write("BROWSER", "back: GoBack threw: " + ex.Message); return false; }
            Log.Write("BROWSER", "back: tab " + _active.Id + " went back");
            return true;
        }

        public void GoForward()
        {
            if (_active == null || _active.Core == null) return;
            try { if (_active.Core.CanGoForward) _active.Core.GoForward(); } catch { }
        }

        public void Reload()
        {
            if (_active == null || _active.Core == null) return;
            _active.Crashed = false;
            try { _active.Core.Reload(); } catch { }
        }

        public void Stop()
        {
            if (_active == null || _active.Core == null) return;
            try { _active.Core.Stop(); } catch { }
        }

        public void SetZoom(double factor)
        {
            if (_active == null || _active.Ctl == null) return;
            if (factor < 0.4) factor = 0.4;
            if (factor > 4.0) factor = 4.0;
            _active.Zoom = factor;
            try
            {
                _active.Ctl.ZoomFactor = factor;
                Log.Write("BROWSER", "tab " + _active.Id + " zoom -> "
                    + (int)Math.Round(factor * 100) + "%");
            }
            catch (Exception ex) { Log.Write("BROWSER", "zoom: " + ex.Message); }
            ToActive("{\"t\":\"zoom\"}");
            PushTab(_active);
        }

        // ── Downloads ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// WebView2 already has a download experience, and it is the wrong one for this
        /// machine on three counts, all of them structural rather than cosmetic:
        ///
        ///   * IT IS DRAWN INSIDE THE CONTENT WINDOW. The flyout is Chromium's own UI,
        ///     painted by the renderer in the top corner of the child HWND. The shell cannot
        ///     restyle it, cannot move it out of the way of a video, and above all cannot
        ///     put a focus ring on it - so from the sofa it is a panel that exists and
        ///     cannot be operated.
        ///   * IT IS A MOUSE UI. Its buttons (keep, discard, open, "show all downloads")
        ///     are hit-tested targets with no keyboard order that survives our injected
        ///     navigation layer, and the pad is not a mouse. mosnav.js reaches into the
        ///     DOCUMENT; the flyout is not in the document.
        ///   * IT DISAPPEARS. It fades after a few seconds, and the only way back to it is
        ///     a toolbar button this browser does not have. A download that failed at 90%
        ///     then leaves no trace anywhere the human can reach.
        ///
        /// So the event is handled here (e.Handled = true is what hides the flyout, per the
        /// SDK contract) and the whole life of the download - name, destination, bytes,
        /// state, and the reason it stopped if it stopped - is reported to the SHELL page,
        /// which draws it as ordinary DOM with a focus ring like everything else. The
        /// commands come back the same way: pause, resume, cancel, forget.
        ///
        /// WHERE THE FILE GOES. The path Chromium chose is kept, deliberately. It is the
        /// profile's download folder (the user's Downloads), already made unique against
        /// what is on disk - and Downloads is one of the places the console's own file
        /// explorer lists, so "Show in Files" lands somewhere the human recognises. Setting
        /// our own path here would mean re-implementing the uniquifying, and the SDK is
        /// explicit that a path pointing at an existing file OVERWRITES it.
        ///
        /// WHAT IS NOT DONE HERE. Nothing is blocked, and nothing is scanned. A download is
        /// reported, saved, and left on disk for the human to deal with in the file
        /// explorer; the shell does not launch it. Pretending to a virus opinion this host
        /// does not have would be worse than saying nothing.
        /// </summary>
        public class Down
        {
            public int Id;
            public int Tab;
            public CoreWebView2DownloadOperation Op;
            public string Url = "";
            public string Name = "";
            public string Path = "";
            public string Mime = "";
            public long Total = -1;          // -1 when the server did not say
            public long Got;
            public string State = "running"; // running | paused | done | cancelled | failed
            public string Why = "";
            public bool CanResume;
            public DateTime Started = DateTime.UtcNow;
        }

        /// <summary>
        /// Enough to be a history, few enough that the list message stays small. Finished
        /// entries are dropped oldest-first once it is full; a download still running is
        /// never dropped, because the record is the only handle the human has on it.
        /// </summary>
        public const int MaxDownloads = 40;

        readonly List<Down> _downloads = new List<Down>();
        int _nextDownload = 1;
        DateTime _downPushed = DateTime.MinValue;

        void StartDownload(Tab tab, CoreWebView2DownloadStartingEventArgs e)
        {
            CoreWebView2DownloadOperation op = null;
            try { op = e.DownloadOperation; }
            catch (Exception ex) { Log.Write("BROWSER", "download started with no operation: " + ex.Message); }
            if (op == null) return;

            // THE line. Everything else in this method is bookkeeping; this is what stops
            // Chromium drawing its own panel over the page.
            try { e.Handled = true; }
            catch (Exception ex) { Log.Write("BROWSER", "could not suppress the built-in download dialog: " + ex.Message); }

            Down d = new Down();
            d.Id = _nextDownload++;
            d.Tab = tab.Id;
            d.Op = op;
            try { d.Url = op.Uri == null ? "" : op.Uri; } catch { }
            try { d.Mime = op.MimeType == null ? "" : op.MimeType; } catch { }
            try { d.Path = e.ResultFilePath == null ? "" : e.ResultFilePath; } catch { }
            if (d.Path.Length == 0) { try { d.Path = op.ResultFilePath == null ? "" : op.ResultFilePath; } catch { } }
            try { d.Name = Path.GetFileName(d.Path); } catch { }
            if (string.IsNullOrEmpty(d.Name)) d.Name = "download";
            ReadDown(d);

            _downloads.Insert(0, d);
            TrimDownloads();

            op.BytesReceivedChanged += delegate(object o2, object e2) { OnDownloadProgress(d); };
            op.StateChanged += delegate(object o2, object e2) { OnDownloadState(d); };

            Log.Write("BROWSER", "download " + d.Id + " from tab " + tab.Id + ": " + d.Url
                + " -> " + d.Path + " (" + (d.Total > 0 ? d.Total + " bytes" : "size not declared")
                + (d.Mime.Length > 0 ? ", " + d.Mime : "")
                + ") - the built-in flyout is suppressed; the shell page draws this one");
            PushDownloads("started", d.Id, true);
        }

        void TrimDownloads()
        {
            for (int i = _downloads.Count - 1; i >= 0 && _downloads.Count > MaxDownloads; i--)
            {
                if (_downloads[i].State == "running" || _downloads[i].State == "paused") continue;
                _downloads.RemoveAt(i);
            }
        }

        /// <summary>
        /// Re-read one download off its operation. Every property is a call across to the
        /// browser process and any of them can throw once the operation is gone, so each is
        /// guarded separately and the last known value is kept rather than zeroed - a
        /// progress bar that jumps to 0% because a getter threw is a lie about the download.
        /// </summary>
        static void ReadDown(Down d)
        {
            if (d.Op == null) return;
            try { d.Got = d.Op.BytesReceived; } catch { }
            // Nullable, and 0 when the server declared no Content-Length. Both mean the same
            // thing to the interface - "no total, so no percentage" - and both become -1 here
            // so that the page has one case to handle rather than three.
            try
            {
                ulong? total = d.Op.TotalBytesToReceive;
                d.Total = (total.HasValue && total.Value > 0 && total.Value <= long.MaxValue)
                    ? (long)total.Value : -1;
            }
            catch { }
            try { if (!string.IsNullOrEmpty(d.Op.ResultFilePath)) d.Path = d.Op.ResultFilePath; } catch { }
            try { d.CanResume = d.Op.CanResume; } catch { }

            CoreWebView2DownloadState state = CoreWebView2DownloadState.InProgress;
            bool known = false;
            try { state = d.Op.State; known = true; } catch { }
            if (!known) return;

            CoreWebView2DownloadInterruptReason why = CoreWebView2DownloadInterruptReason.None;
            try { why = d.Op.InterruptReason; } catch { }

            if (state == CoreWebView2DownloadState.Completed) { d.State = "done"; d.Why = ""; }
            else if (state == CoreWebView2DownloadState.Interrupted)
            {
                // Pause and cancel are both "interrupted" with a reason. They are three very
                // different things to a human, so they are three different states here.
                if (why == CoreWebView2DownloadInterruptReason.UserPaused) { d.State = "paused"; d.Why = ""; }
                else if (why == CoreWebView2DownloadInterruptReason.UserCanceled) { d.State = "cancelled"; d.Why = ""; }
                else { d.State = "failed"; d.Why = ReasonText(why); }
            }
            else { d.State = "running"; d.Why = ""; }
        }

        /// <summary>
        /// The interrupt reason as a sentence. The enum name is kept on the end because it
        /// is what anyone reading the log will search for, but what the human reads first
        /// has to say what actually went wrong and, where there is one, what to do about it.
        /// </summary>
        static string ReasonText(CoreWebView2DownloadInterruptReason why)
        {
            string s;
            switch (why)
            {
                case CoreWebView2DownloadInterruptReason.FileNoSpace:
                    s = "there is no room left on the disk"; break;
                case CoreWebView2DownloadInterruptReason.FileAccessDenied:
                    s = "the console is not allowed to write that file"; break;
                case CoreWebView2DownloadInterruptReason.FileNameTooLong:
                    s = "the file name the site asked for is too long"; break;
                case CoreWebView2DownloadInterruptReason.FileTooLarge:
                    s = "the file is too large to save"; break;
                case CoreWebView2DownloadInterruptReason.FileMalicious:
                case CoreWebView2DownloadInterruptReason.FileBlockedByPolicy:
                case CoreWebView2DownloadInterruptReason.FileSecurityCheckFailed:
                    s = "the browser refused the file as unsafe"; break;
                case CoreWebView2DownloadInterruptReason.FileTransientError:
                case CoreWebView2DownloadInterruptReason.FileFailed:
                    s = "writing it to disk failed"; break;
                case CoreWebView2DownloadInterruptReason.FileHashMismatch:
                case CoreWebView2DownloadInterruptReason.FileTooShort:
                case CoreWebView2DownloadInterruptReason.ServerContentLengthMismatch:
                    s = "what arrived did not match what the server promised"; break;
                case CoreWebView2DownloadInterruptReason.NetworkDisconnected:
                    s = "the network went away"; break;
                case CoreWebView2DownloadInterruptReason.NetworkTimeout:
                    s = "the server stopped answering"; break;
                case CoreWebView2DownloadInterruptReason.NetworkServerDown:
                    s = "the server is down"; break;
                case CoreWebView2DownloadInterruptReason.NetworkFailed:
                case CoreWebView2DownloadInterruptReason.NetworkInvalidRequest:
                    s = "the connection failed"; break;
                case CoreWebView2DownloadInterruptReason.ServerUnauthorized:
                case CoreWebView2DownloadInterruptReason.ServerForbidden:
                    s = "the server would not hand it over without signing in"; break;
                case CoreWebView2DownloadInterruptReason.ServerCertificateProblem:
                    s = "the server's certificate could not be trusted"; break;
                case CoreWebView2DownloadInterruptReason.ServerNoRange:
                    s = "the server will not resume a part-finished download"; break;
                case CoreWebView2DownloadInterruptReason.ServerBadContent:
                case CoreWebView2DownloadInterruptReason.ServerUnexpectedResponse:
                case CoreWebView2DownloadInterruptReason.ServerFailed:
                    s = "the server sent something the browser could not use"; break;
                case CoreWebView2DownloadInterruptReason.ServerCrossOriginRedirect:
                    s = "the download was redirected to another site part-way through"; break;
                case CoreWebView2DownloadInterruptReason.DownloadProcessCrashed:
                    s = "the process doing the downloading stopped"; break;
                case CoreWebView2DownloadInterruptReason.UserShutdown:
                    s = "the browser was shut down"; break;
                case CoreWebView2DownloadInterruptReason.None:
                    s = "it stopped for no stated reason"; break;
                default:
                    s = "it was interrupted"; break;
            }
            return s + " (" + why + ")";
        }

        void OnDownloadProgress(Down d)
        {
            ReadDown(d);
            // Bytes arrive far faster than a television can be read. The state changes are
            // never throttled; this stream is, to a few frames a second.
            PushDownloads("progress", d.Id, false);
        }

        void OnDownloadState(Down d)
        {
            string was = d.State;
            ReadDown(d);
            if (d.State != was)
                Log.Write("BROWSER", "download " + d.Id + " " + was + " -> " + d.State
                    + " (" + d.Got + (d.Total > 0 ? " of " + d.Total : "") + " bytes"
                    + (d.Why.Length > 0 ? "; " + d.Why : "") + ") " + d.Path);
            PushDownloads(d.State, d.Id, true);
        }

        Down FindDown(int id)
        {
            foreach (Down d in _downloads) if (d.Id == id) return d;
            return null;
        }

        /// <summary>
        /// The shell page's control channel for one download. Pause, resume and cancel go
        /// to the operation and come back as a StateChanged; forget and clear only touch
        /// this list, and refuse to drop anything still running.
        /// </summary>
        public void DownloadCommand(string act, int id)
        {
            Down d = FindDown(id);
            if (act == "list") { PushDownloads("list", 0, true); return; }

            if (act == "clear")
            {
                int gone = 0;
                for (int i = _downloads.Count - 1; i >= 0; i--)
                {
                    if (_downloads[i].State == "running" || _downloads[i].State == "paused") continue;
                    _downloads.RemoveAt(i); gone++;
                }
                Log.Write("BROWSER", "downloads: cleared " + gone + " finished entries; "
                    + _downloads.Count + " left (the files themselves are untouched)");
                PushDownloads("cleared", 0, true);
                return;
            }

            if (d == null)
            {
                Log.Write("BROWSER", "download command '" + act + "' for unknown id " + id);
                return;
            }

            switch (act)
            {
                case "pause":
                    try { d.Op.Pause(); Log.Write("BROWSER", "download " + id + " paused"); }
                    catch (Exception ex) { DownFailed(d, "pause", ex); }
                    break;
                case "resume":
                    try { d.Op.Resume(); Log.Write("BROWSER", "download " + id + " resumed"); }
                    catch (Exception ex) { DownFailed(d, "resume", ex); }
                    break;
                case "cancel":
                    try { d.Op.Cancel(); Log.Write("BROWSER", "download " + id + " cancelled"); }
                    catch (Exception ex) { DownFailed(d, "cancel", ex); }
                    break;
                case "forget":
                    if (d.State == "running" || d.State == "paused")
                    {
                        Log.Write("BROWSER", "refused to forget download " + id + ": it is still " + d.State);
                        return;
                    }
                    _downloads.Remove(d);
                    Log.Write("BROWSER", "download " + id + " removed from the list (the file is untouched)");
                    PushDownloads("forgotten", id, true);
                    return;
                default:
                    Log.Write("BROWSER", "unhandled download command '" + act + "'");
                    return;
            }
            // Pause/resume/cancel report themselves through StateChanged, but a runtime that
            // does not raise it must not leave the row lying about what it is doing.
            ReadDown(d);
            PushDownloads(d.State, id, true);
        }

        void DownFailed(Down d, string what, Exception ex)
        {
            Log.Write("BROWSER", "download " + d.Id + ": " + what + " threw: " + ex.Message);
            Say("{\"type\":\"browser\",\"ev\":\"downfail\",\"id\":" + d.Id
                + ",\"act\":\"" + Esc(what) + "\",\"detail\":\"" + Esc(ex.Message) + "\"}");
        }

        /// <summary>
        /// Re-read every download and report. Called after anything that can kill one
        /// underneath us - a tab closing, every tab closing - because the operations belong
        /// to the content WebViews and a torn-down WebView does not always get to raise a
        /// last StateChanged on its way out.
        /// </summary>
        public void RefreshDownloads(string why)
        {
            if (_downloads.Count == 0) return;
            for (int i = 0; i < _downloads.Count; i++) ReadDown(_downloads[i]);
            PushDownloads(why, 0, true);
        }

        public int ActiveDownloads()
        {
            int n = 0;
            foreach (Down d in _downloads) if (d.State == "running" || d.State == "paused") n++;
            return n;
        }

        void PushDownloads(string reason, int id, bool force)
        {
            DateTime now = DateTime.UtcNow;
            if (!force && (now - _downPushed).TotalMilliseconds < 280) return;
            _downPushed = now;

            StringBuilder b = new StringBuilder(256);
            b.Append("{\"type\":\"browser\",\"ev\":\"downloads\",\"reason\":\"").Append(Esc(reason))
             .Append("\",\"id\":").Append(id)
             .Append(",\"active\":").Append(ActiveDownloads())
             .Append(",\"list\":[");
            for (int i = 0; i < _downloads.Count; i++)
            {
                Down d = _downloads[i];
                string folder = "";
                try { folder = Path.GetDirectoryName(d.Path); }
                catch { }
                if (i > 0) b.Append(',');
                b.Append("{\"id\":").Append(d.Id)
                 .Append(",\"tab\":").Append(d.Tab)
                 .Append(",\"name\":\"").Append(Esc(d.Name)).Append('"')
                 .Append(",\"path\":\"").Append(Esc(d.Path)).Append('"')
                 .Append(",\"folder\":\"").Append(Esc(folder == null ? "" : folder)).Append('"')
                 .Append(",\"url\":\"").Append(Esc(d.Url)).Append('"')
                 .Append(",\"mime\":\"").Append(Esc(d.Mime)).Append('"')
                 .Append(",\"state\":\"").Append(Esc(d.State)).Append('"')
                 .Append(",\"why\":\"").Append(Esc(d.Why)).Append('"')
                 .Append(",\"got\":").Append(d.Got)
                 .Append(",\"total\":").Append(d.Total)
                 .Append(",\"canResume\":").Append(d.CanResume ? "true" : "false")
                 .Append(",\"ageMs\":").Append((long)(now - d.Started).TotalMilliseconds)
                 .Append('}');
            }
            b.Append("]}");
            Say(b.ToString());
        }

        // ── Permissions ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Camera, microphone, location, notifications, clipboard, sensors: the browser's own
        /// consent prompts, taken away from Chromium and asked through the shell.
        ///
        /// WHY. Edge draws these as a bubble anchored inside the content window - the same three
        /// structural problems as the download flyout, and one more that matters more. It is a
        /// MOUSE UI inside a child HWND: mosnav.js reaches into the DOCUMENT and the bubble is not
        /// in the document, so from the sofa it is a dialogue that exists and cannot be answered.
        /// A permission prompt that cannot be answered is not a safe default either way - Deny by
        /// timeout looks like the page is broken, and there is no Allow at all.
        ///
        /// THE MECHANISM. PermissionRequested gives a deferral. Take it, and the request is parked
        /// with no UI of any kind on screen; complete it with State set and Chromium never draws
        /// anything. That is the whole trick, and it is why nothing Microsoft ever appears here:
        /// the built-in bubble is what an UNHANDLED request produces, and this handler always
        /// handles it - allow, deny, timeout, tab closed, shell shutting down.
        ///
        /// THE ROUND TRIP, over the same channel as loaderror and the download events:
        ///     host -> shell   {"type":"browser","ev":"permission","id":N,"origin":"https://x",
        ///                      "uri":"https://x/page","kind":"camera","userInitiated":true}
        ///     shell -> host   {"type":"browser","cmd":"permission","id":N,"allow":true,"remember":false}
        /// and, for the settings screen:
        ///     shell -> host   {"type":"browser","cmd":"permissions.list"}
        ///     host -> shell   {"type":"browser","ev":"permissions","items":[{origin,kind,allow}]}
        ///     shell -> host   {"type":"browser","cmd":"permissions.forget","origin":"https://x","kind":"camera"}
        ///                     (kind omitted = every grant for that origin)
        ///
        /// WHO REMEMBERS. Not WebView2. SavesInProfile is turned OFF on every request, so the
        /// runtime's own per-profile memory never fills in an answer we did not give: this store
        /// is the only thing that decides, which is what makes "forget" mean something and what
        /// keeps the list the human is shown the truth. The store is permissions.json in the
        /// content profile's user-data folder - beside the profile it describes, so wiping the
        /// browser wipes its grants with it.
        ///
        /// 60 SECONDS. A page that asks and is never answered gets Deny. Not remembered: a
        /// timeout is "nobody was there", not "no".
        /// </summary>
        sealed class PermReq
        {
            public int Id;
            public Tab Tab;
            public string Origin = "";
            public string Uri = "";
            public string Kind = "";
            public CoreWebView2PermissionRequestedEventArgs Args;
            public CoreWebView2Deferral Deferral;
            public DateTime Asked = DateTime.UtcNow;
        }

        const int PermTimeoutMs = 60000;

        readonly Dictionary<string, bool> _perms = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        readonly List<PermReq> _permPending = new List<PermReq>();
        bool _permsLoaded;
        int _nextPermId = 1;
        System.Windows.Forms.Timer _permTimer;

        string PermStorePath()
        {
            return Path.Combine(_userData, "permissions.json");
        }

        static string PermKey(string origin, string kind)
        {
            return (origin == null ? "" : origin) + "|" + (kind == null ? "" : kind);
        }

        /// <summary>
        /// Load the remembered decisions once per run. The file is written by SavePerms below, so
        /// the reader is deliberately lenient rather than a parser: three known fields, matched
        /// wherever they appear, and anything it cannot understand is skipped rather than
        /// throwing. A corrupt store must cost the human their saved answers, not their browser.
        /// </summary>
        void LoadPerms()
        {
            if (_permsLoaded) return;
            _permsLoaded = true;
            string path = PermStorePath();
            string text;
            try
            {
                if (!File.Exists(path)) { Log.Write("PERM", "no saved decisions (" + path + ")"); return; }
                text = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception ex) { Log.Write("PERM", "could not read " + path + ": " + ex.Message); return; }

            int n = 0;
            try
            {
                MatchCollection ms = Regex.Matches(text,
                    "\"origin\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"\\s*,\\s*\"kind\"\\s*:\\s*\"([^\"]*)\"\\s*,\\s*\"allow\"\\s*:\\s*(true|false)");
                foreach (Match m in ms)
                {
                    string origin = m.Groups[1].Value.Replace("\\\\", "\\").Replace("\\\"", "\"");
                    string kind = m.Groups[2].Value;
                    bool allow = string.Equals(m.Groups[3].Value, "true", StringComparison.OrdinalIgnoreCase);
                    if (origin.Length == 0 || kind.Length == 0) continue;
                    _perms[PermKey(origin, kind)] = allow;
                    n++;
                }
            }
            catch (Exception ex) { Log.Write("PERM", "could not parse " + path + ": " + ex.Message); }
            Log.Write("PERM", n + " remembered decision(s) loaded from " + path);
        }

        void SavePerms()
        {
            string path = PermStorePath();
            StringBuilder b = new StringBuilder(256);
            b.Append("{\"version\":1,\"items\":[");
            bool first = true;
            foreach (KeyValuePair<string, bool> kv in _perms)
            {
                int bar = kv.Key.LastIndexOf('|');
                if (bar < 0) continue;
                if (!first) b.Append(',');
                first = false;
                b.Append("{\"origin\":\"").Append(Esc(kv.Key.Substring(0, bar)))
                 .Append("\",\"kind\":\"").Append(Esc(kv.Key.Substring(bar + 1)))
                 .Append("\",\"allow\":").Append(kv.Value ? "true" : "false").Append('}');
            }
            b.Append("]}");
            try
            {
                Directory.CreateDirectory(_userData);
                File.WriteAllText(path, b.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Write("PERM", "could not write " + path + ": " + ex.Message); }
        }

        /// <summary>
        /// scheme://host[:port] - the unit a grant is given to. A grant belongs to an ORIGIN and
        /// never to a page: allowing the camera on https://example.com/call must not be a
        /// different decision from https://example.com/room, and must not extend to
        /// http://example.com, which is a different origin and a different trust story.
        /// </summary>
        static string OriginOf(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return "";
            try
            {
                Uri u = new Uri(uri);
                return u.GetLeftPart(UriPartial.Authority);
            }
            catch { return uri; }
        }

        /// <summary>
        /// The permission kind as the shell names it: lower camel case, and the two enum names
        /// that do not translate cleanly spelled out. Written as a switch over the enum's NAME
        /// rather than over its members so that a runtime or SDK that grows a kind this build has
        /// never heard of reports "unknown" instead of failing to compile or throwing.
        /// </summary>
        static string KindName(CoreWebView2PermissionKind k)
        {
            string s;
            try { s = k.ToString(); }
            catch { return "unknown"; }
            switch (s)
            {
                case "Microphone": return "microphone";
                case "Camera": return "camera";
                case "Geolocation": return "geolocation";
                case "Notifications": return "notifications";
                case "OtherSensors": return "otherSensors";
                case "ClipboardRead": return "clipboardRead";
                case "MultipleAutomaticDownloads": return "multipleAutomaticDownloads";
                case "FileReadWrite": return "fileReadWrite";
                case "Autoplay": return "autoplay";
                case "LocalFonts": return "localFonts";
                case "MidiSystemExclusiveMessages": return "midiSystemExclusive";
                case "WindowManagement": return "windowManagement";
                default: return "unknown";
            }
        }

        void OnPermissionRequested(Tab tab, CoreWebView2PermissionRequestedEventArgs e)
        {
            string uri = "";
            string kind = "unknown";
            bool userInitiated = false;
            try { uri = e.Uri == null ? "" : e.Uri; }
            catch { }
            try { kind = KindName(e.PermissionKind); }
            catch { }
            try { userInitiated = e.IsUserInitiated; }
            catch { }
            string origin = OriginOf(uri);

            // WebView2's own memory is switched off deliberately - see the region comment. If the
            // runtime is too old to have the property, ours still decides; the only cost is that
            // the runtime may also remember, which can only ever suppress a prompt we would have
            // answered from the store the same way.
            try { e.SavesInProfile = false; }
            catch { }

            CoreWebView2Deferral deferral = null;
            try { deferral = e.GetDeferral(); }
            catch (Exception ex)
            {
                // No deferral means no time to ask. Deny synchronously rather than let Chromium
                // draw a bubble the pad cannot answer.
                Log.Write("PERM", "no deferral available (" + ex.Message + ") - denying " + origin + " " + kind);
                try { e.State = CoreWebView2PermissionState.Deny; }
                catch { }
                return;
            }

            LoadPerms();
            bool remembered;
            if (_perms.TryGetValue(PermKey(origin, kind), out remembered))
            {
                Apply(e, deferral, remembered);
                Log.Write("PERM", origin + " " + kind + " " + (remembered ? "allow" : "deny")
                    + " remembered (no prompt drawn)");
                return;
            }

            PermReq r = new PermReq();
            r.Id = _nextPermId++;
            r.Tab = tab;
            r.Origin = origin;
            r.Uri = uri;
            r.Kind = kind;
            r.Args = e;
            r.Deferral = deferral;
            _permPending.Add(r);
            StartPermTimer();

            Log.Write("PERM", "asking the shell: id=" + r.Id + " tab=" + tab.Id + " " + origin + " " + kind
                + " userInitiated=" + userInitiated);
            Say("{\"type\":\"browser\",\"ev\":\"permission\",\"id\":" + r.Id
                + ",\"tab\":" + tab.Id
                + ",\"origin\":\"" + Esc(origin) + "\""
                + ",\"uri\":\"" + Esc(uri) + "\""
                + ",\"kind\":\"" + Esc(kind) + "\""
                + ",\"userInitiated\":" + (userInitiated ? "true" : "false") + "}");
        }

        static void Apply(CoreWebView2PermissionRequestedEventArgs e, CoreWebView2Deferral deferral, bool allow)
        {
            try { e.State = allow ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny; }
            catch (Exception ex) { Log.Write("PERM", "setting the state threw: " + ex.Message); }
            // ALWAYS, on every path. An uncompleted deferral leaves the page waiting forever and
            // the request alive inside the browser process.
            try { if (deferral != null) deferral.Complete(); }
            catch (Exception ex) { Log.Write("PERM", "completing the deferral threw: " + ex.Message); }
        }

        /// <summary>The shell's answer. {"type":"browser","cmd":"permission","id":N,"allow":b,"remember":b}</summary>
        public void PermissionReply(int id, bool allow, bool remember)
        {
            PermReq r = null;
            for (int i = 0; i < _permPending.Count; i++)
                if (_permPending[i].Id == id) { r = _permPending[i]; _permPending.RemoveAt(i); break; }

            if (r == null)
            {
                // Timed out, or the tab went away while the sheet was up. Saying so is worth a
                // line: from the shell's side the sheet looked answered.
                Log.Write("PERM", "reply for id=" + id + " arrived too late or twice; ignored");
                return;
            }

            Apply(r.Args, r.Deferral, allow);
            if (remember)
            {
                LoadPerms();
                _perms[PermKey(r.Origin, r.Kind)] = allow;
                SavePerms();
            }
            Log.Write("PERM", r.Origin + " " + r.Kind + " " + (allow ? "allow" : "deny")
                + " " + (remember ? "remember" : "once"));
            StopPermTimerIfIdle();
        }

        public void PermissionsList()
        {
            LoadPerms();
            StringBuilder b = new StringBuilder(128);
            b.Append("{\"type\":\"browser\",\"ev\":\"permissions\",\"items\":[");
            bool first = true;
            foreach (KeyValuePair<string, bool> kv in _perms)
            {
                int bar = kv.Key.LastIndexOf('|');
                if (bar < 0) continue;
                if (!first) b.Append(',');
                first = false;
                b.Append("{\"origin\":\"").Append(Esc(kv.Key.Substring(0, bar)))
                 .Append("\",\"kind\":\"").Append(Esc(kv.Key.Substring(bar + 1)))
                 .Append("\",\"allow\":").Append(kv.Value ? "true" : "false").Append('}');
            }
            b.Append("],\"store\":\"").Append(Esc(PermStorePath())).Append("\"}");
            Say(b.ToString());
        }

        /// <summary>Forget one grant, or every grant for an origin when kind is omitted.</summary>
        public void PermissionsForget(string origin, string kind)
        {
            LoadPerms();
            if (string.IsNullOrEmpty(origin))
            {
                Log.Write("PERM", "permissions.forget with no origin; nothing done");
                PermissionsList();
                return;
            }
            List<string> dead = new List<string>();
            foreach (KeyValuePair<string, bool> kv in _perms)
            {
                int bar = kv.Key.LastIndexOf('|');
                if (bar < 0) continue;
                if (!string.Equals(kv.Key.Substring(0, bar), origin, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(kind)
                    && !string.Equals(kv.Key.Substring(bar + 1), kind, StringComparison.OrdinalIgnoreCase)) continue;
                dead.Add(kv.Key);
            }
            for (int i = 0; i < dead.Count; i++) _perms.Remove(dead[i]);
            SavePerms();
            Log.Write("PERM", "forgot " + dead.Count + " decision(s) for " + origin
                + (string.IsNullOrEmpty(kind) ? " (all kinds)" : " " + kind));
            PermissionsList();
        }

        /// <summary>
        /// Deny everything outstanding for one tab (null = every tab). Called when a tab
        /// navigates, when it is closed, and at shutdown.
        /// </summary>
        void DenyPending(Tab tab, string why)
        {
            if (_permPending.Count == 0) return;
            for (int i = _permPending.Count - 1; i >= 0; i--)
            {
                PermReq r = _permPending[i];
                if (tab != null && r.Tab != tab) continue;
                _permPending.RemoveAt(i);
                Apply(r.Args, r.Deferral, false);
                Log.Write("PERM", r.Origin + " " + r.Kind + " deny once (" + why + ")");
                Say("{\"type\":\"browser\",\"ev\":\"permissiondone\",\"id\":" + r.Id
                    + ",\"reason\":\"" + Esc(why) + "\"}");
            }
            StopPermTimerIfIdle();
        }

        void StartPermTimer()
        {
            if (_permTimer == null)
            {
                _permTimer = new System.Windows.Forms.Timer();
                _permTimer.Interval = 1000;
                _permTimer.Tick += delegate(object o, EventArgs e) { PermTick(); };
            }
            if (!_permTimer.Enabled) _permTimer.Enabled = true;
        }

        void StopPermTimerIfIdle()
        {
            if (_permTimer != null && _permPending.Count == 0) _permTimer.Enabled = false;
        }

        void PermTick()
        {
            DateTime now = DateTime.UtcNow;
            for (int i = _permPending.Count - 1; i >= 0; i--)
            {
                PermReq r = _permPending[i];
                if ((now - r.Asked).TotalMilliseconds < PermTimeoutMs) continue;
                _permPending.RemoveAt(i);
                Apply(r.Args, r.Deferral, false);
                // NOT remembered. Nobody said no; nobody said anything.
                Log.Write("PERM", r.Origin + " " + r.Kind + " deny once (no answer in "
                    + (PermTimeoutMs / 1000) + " s)");
                Say("{\"type\":\"browser\",\"ev\":\"permissiondone\",\"id\":" + r.Id
                    + ",\"reason\":\"timeout\"}");
            }
            StopPermTimerIfIdle();
        }

        // ── Relay into the page ─────────────────────────────────────────────────────────

        public void ToActive(string json)
        {
            if (_active == null || _active.Core == null) return;
            try { _active.Core.PostWebMessageAsString(json); }
            catch (Exception ex) { Log.Write("BROWSER", "post to tab " + _active.Id + " failed: " + ex.Message); }
        }

        /// <summary>
        /// One discrete pad action, delivered by ExecuteScriptAsync rather than by
        /// PostWebMessageAsString.
        ///
        /// THE REASON, because it is not obvious and it cost a bench session to find.
        /// A click synthesised inside a message handler has no user activation behind it.
        /// Chromium withholds three things from a navigation that lacks one, and the third is
        /// the expensive one:
        ///   * requestFullscreen() rejects with "Permissions check failed";
        ///   * autoplay is blocked (worked around separately, in the environment options);
        ///   * the history entry the navigation creates is flagged skippable by the
        ///     back-forward intervention, so CanGoBack stays FALSE however many links the
        ///     human follows - and Circle can never take them back one page.
        /// Script delivered through ExecuteScriptAsync is treated as user-initiated, which
        /// restores all three. It is a heavier call than a web message, and that is fine:
        /// discrete actions happen at the speed of a human pressing buttons. The analog
        /// stick stream, which really is 30 a second, stays on ToActive().
        ///
        /// If a runtime ever stops granting activation here the failure is visible rather
        /// than silent: CanGoBack goes back to false and the log says "no history left"
        /// after a navigation the human just made.
        /// </summary>
        public void Pad(string action, string phase)
        {
            if (_active == null || _active.Core == null) return;
            string js = "window.__mosnav&&window.__mosnav.act(\"" + Esc(action) + "\",\"" + Esc(phase) + "\")";
            try { _active.Core.ExecuteScriptAsync(js); }
            catch (Exception ex)
            {
                Log.Write("BROWSER", "ExecuteScript for '" + action + "' failed (" + ex.Message
                    + ") - falling back to the message channel, which loses user activation");
                ToActive("{\"t\":\"act\",\"action\":\"" + Esc(action) + "\",\"phase\":\"" + Esc(phase) + "\"}");
            }
        }

        /// <summary>
        /// Analog sticks, straight from the pad timer. Only sent when the content pane
        /// actually owns input, and only while something is off centre plus one final
        /// zeroing message - a permanent 30 Hz stream into an idle page would keep a
        /// renderer awake for no reason.
        /// </summary>
        bool _axesWereLive;
        DateTime _axesNext = DateTime.MinValue;

        public void Axes(double lx, double ly, double rx, double ry)
        {
            if (!Open || !ContentFocused || _active == null) return;
            bool live = Math.Abs(lx) > 0.14 || Math.Abs(ly) > 0.14 || Math.Abs(rx) > 0.14 || Math.Abs(ry) > 0.14;
            if (!live && !_axesWereLive) return;
            DateTime now = DateTime.UtcNow;
            if (live && now < _axesNext) return;
            _axesNext = now.AddMilliseconds(33);
            _axesWereLive = live;
            ToActive("{\"t\":\"axes\",\"lx\":" + N(lx) + ",\"ly\":" + N(ly)
                   + ",\"rx\":" + N(rx) + ",\"ry\":" + N(ry) + "}");
        }

        static string N(double v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        // ── Reporting up to the shell page ──────────────────────────────────────────────

        void Say(string json)
        {
            try { if (_toPage != null) _toPage(json); }
            catch (Exception ex) { Log.Write("BROWSER", "reporting to the page failed: " + ex.Message); }
        }

        void PushTab(Tab t) { PushTabs(); }

        public void PushTabs()
        {
            StringBuilder b = new StringBuilder(256);
            b.Append("{\"type\":\"browser\",\"ev\":\"tabs\",\"max\":").Append(MaxTabs);
            b.Append(",\"active\":").Append(_active == null ? 0 : _active.Id);
            b.Append(",\"tabs\":[");
            for (int i = 0; i < _tabs.Count; i++)
            {
                Tab t = _tabs[i];
                bool back = false, fwd = false;
                try { if (t.Core != null) { back = t.Core.CanGoBack; fwd = t.Core.CanGoForward; } }
                catch { }
                if (i > 0) b.Append(',');
                b.Append("{\"id\":").Append(t.Id)
                 .Append(",\"url\":\"").Append(Esc(t.Url)).Append('"')
                 .Append(",\"title\":\"").Append(Esc(t.Title)).Append('"')
                 .Append(",\"favicon\":\"").Append(Esc(t.Favicon)).Append('"')
                 .Append(",\"secure\":").Append(t.Secure ? "true" : "false")
                 .Append(",\"loading\":").Append(t.Loading ? "true" : "false")
                 .Append(",\"crashed\":").Append(t.Crashed ? "true" : "false")
                 .Append(",\"canBack\":").Append(back ? "true" : "false")
                 .Append(",\"canForward\":").Append(fwd ? "true" : "false")
                 .Append(",\"zoom\":").Append(N(t.Zoom))
                 .Append('}');
            }
            b.Append("]}");
            Say(b.ToString());
        }

        static bool IsSecure(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            return url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder b = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') b.Append('\\').Append(c);
                else if (c == '\n') b.Append("\\n");
                else if (c == '\r') b.Append("\\r");
                else if (c == '\t') b.Append("\\t");
                else if (c < ' ') b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else b.Append(c);
            }
            return b.ToString();
        }
    }

    #endregion

    #region Host form

    public class HostForm : Form
    {
        // Small, additive shim. It does not restructure index.html's own JavaScript: it only
        // listens (capture phase) and forwards to the host, plus reports what the Gamepad API sees.
        const string Shim = @"
(function(){
  if (window.__mosShim) return;
  window.__mosShim = true;
  var post = function(o){ try { window.chrome.webview.postMessage(JSON.stringify(o)); } catch(e){} };
  window.mosPost = post;

  post({type:'ready', target:location.href});

  window.addEventListener('keydown', function(e){
    // The on-screen keyboard owns Escape while it is up: Escape there means
    // 'cancel this field', and exiting the shell instead would be a trap. This
    // listener is on window and therefore runs before the keyboard's own
    // capture handler, so the check has to live here.
    var oskUp = false;
    try { oskUp = !!(window.MarwanOSK && window.MarwanOSK.isOpen()); } catch(err){}
    if (oskUp && (e.key === 'Escape' || e.key === 'Enter')) return;

    if (e.key === 'Escape') { post({type:'exit', code:0, target:'Escape'}); return; }
    if (e.key === '2')      { post({type:'exit', code:2, target:'key2'});   return; }
    if (e.key === '3')      { post({type:'exit', code:3, target:'key3'});   return; }
    // Enter is the page's business once the page has a focus manager: posting
    // launch from here regardless of what is focused would fire the child while
    // the human is three levels deep in a settings panel. Only a page with no
    // mosPad (the bare probe pages) still gets the old blunt behaviour.
    if (e.key === 'Enter' && typeof window.mosPad !== 'function') {
      post({type:'launch', target:''});
      return;
    }
  }, true);

  var wire = function(){
    var b = document.getElementById('btnLaunch');
    if (b && !b.__mosWired){
      b.__mosWired = true;
      b.addEventListener('click', function(){ post({type:'launch', target:''}); });
      post({type:'log', target:'shim wired #btnLaunch'});
    }
  };

  // ---- host -> page: semantic pad actions ------------------------------------------
  // The pad drives index.html's OWN functions (select / setTab / toggleCC / closeCC and a
  // real click on #btnLaunch). Synthetic KeyboardEvents are the fallback only, and the
  // deliberate choice for the control-centre cursor, whose index lives in the page's
  // lexical scope and is owned by the page's own key handler.
  var ccOpen = function(){
    var c = document.getElementById('cc');
    return !!c && c.classList.contains('open');
  };
  var synthKey = function(k){
    try {
      window.dispatchEvent(new KeyboardEvent('keydown', {key:k, bubbles:true, cancelable:true}));
      return true;
    } catch(e){ return false; }
  };
  var focusedTile = function(){
    var t = document.querySelectorAll('#rail .tile');
    for (var i = 0; i < t.length; i++)
      if (t[i].getAttribute('aria-current') === 'true') return i;
    return 0;
  };
  var move = function(d){
    if (ccOpen()){
      synthKey(d > 0 ? 'ArrowRight' : 'ArrowLeft');
      return 'control-centre cursor ' + (d > 0 ? 'right' : 'left');
    }
    if (typeof window.select === 'function'){
      window.select(focusedTile() + d);   // select() already wraps
      return 'rail index ' + focusedTile();
    }
    synthKey(d > 0 ? 'ArrowRight' : 'ArrowLeft');
    return 'rail via synthetic key (select() not on this page)';
  };

  var actions = {
    left:  function(){ return move(-1); },
    right: function(){ return move(1); },
    launch: function(){
      var b = document.getElementById('btnLaunch');
      if (!b){ post({type:'launch', target:''}); return 'no #btnLaunch here - posted launch directly'; }
      b.classList.add('pressed');
      setTimeout(function(){ try { b.classList.remove('pressed'); } catch(e){} }, 160);
      b.click();                       // the click handler wired above posts {type:launch}
      return 'clicked #btnLaunch';
    },
    back: function(){
      if (!ccOpen()) return 'nothing to close (control centre already shut)';
      if (typeof window.closeCC === 'function') window.closeCC(); else synthKey('Escape');
      return 'closed the control centre';
    },
    tabPlay:  function(){
      if (typeof window.setTab === 'function'){ window.setTab('play'); return 'tab=play'; }
      synthKey('Tab'); return 'tab toggled via synthetic Tab';
    },
    tabMedia: function(){
      if (typeof window.setTab === 'function'){ window.setTab('media'); return 'tab=media'; }
      synthKey('Tab'); return 'tab toggled via synthetic Tab';
    },
    cc: function(){
      if (typeof window.toggleCC === 'function'){ window.toggleCC(); return 'control centre now ' + (ccOpen() ? 'OPEN' : 'closed'); }
      synthKey('c'); return 'control centre via synthetic c';
    }
  };

  var onHostMessage = function(ev){
    var d = ev ? ev.data : null;
    if (typeof d !== 'string'){ try { d = JSON.stringify(d); } catch(e){ return; } }
    var m = null;
    try { m = JSON.parse(d); } catch(e){ post({type:'log', target:'unparsable host message: ' + d}); return; }
    if (!m || m.type !== 'pad') return;             // hostinfo / sysinfo / launching / returned
                                                    // are for the page's own listener
    // A page that owns a focus manager takes every action through one entry
    // point; the table below is the fallback for pages that do not have one.
    var r;
    if (typeof window.mosPad === 'function'){
      try { r = window.mosPad(m.action, m.phase, m.button); }
      catch(err){ post({type:'log', target:'mosPad(' + m.action + ') threw: ' + err}); return; }
      if (m.phase === 'release' || m.phase === 'repeat') return;   // too chatty for the log
      post({type:'log', target:'pad ' + m.action + ' -> ' + r});
      return;
    }
    if (m.phase === 'release') return;             // the fallback table has no release handling
    var fn = actions[m.action];
    if (!fn){ post({type:'log', target:'pad action unknown, ignored: ' + m.action}); return; }
    try { r = fn(); }
    catch(err){ post({type:'log', target:'pad action ' + m.action + ' threw: ' + err}); return; }
    post({type:'log', target:'pad ' + m.action + ' -> ' + r});
  };
  try {
    window.chrome.webview.addEventListener('message', onHostMessage);
    post({type:'log', target:'shim wired host->page pad channel; actions = ' + Object.keys(actions).join(',')});
  } catch(e){
    post({type:'log', target:'FAILED to wire host->page pad channel: ' + e});
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', wire);
  else wire();
  setTimeout(wire, 500); setTimeout(wire, 2000); setTimeout(wire, 8000);

  var seen = '';
  var probe = function(){
    var out = [];
    try {
      var gp = navigator.getGamepads ? navigator.getGamepads() : [];
      for (var i = 0; i < gp.length; i++){
        var p = gp[i];
        if (p) out.push('slot' + p.index + ' id=[' + p.id + '] mapping=' + p.mapping
                        + ' axes=' + p.axes.length + ' buttons=' + p.buttons.length
                        + ' connected=' + p.connected);
      }
    } catch(err){ out.push('getGamepads threw: ' + err); }
    var s = out.length ? out.join(' ;; ') : 'none visible to navigator.getGamepads()';
    if (s !== seen){ seen = s; post({type:'gamepad', target:s}); }
  };
  window.addEventListener('gamepadconnected', probe);
  window.addEventListener('gamepaddisconnected', probe);
  setInterval(probe, 1000);
  probe();

  window.addEventListener('error', function(ev){
    post({type:'log', target:'page error: ' + (ev.message || '?') + ' @' + (ev.filename || '?') + ':' + (ev.lineno || 0)});
  });
})();
";

        static readonly Color Void = Color.FromArgb(255, 0x04, 0x06, 0x0B);

        // XInput slot masks (kept for a future Xbox pad; a DualSense never appears here).
        const ushort XI_DUP = 0x0001;
        const ushort XI_DDOWN = 0x0002;
        const ushort XI_DLEFT = 0x0004;
        const ushort XI_DRIGHT = 0x0008;
        const ushort XI_START = 0x0010;
        const ushort XI_BACK = 0x0020;
        const ushort XI_LB = 0x0100;
        const ushort XI_RB = 0x0200;
        const ushort XI_GUIDE = 0x0400;
        const ushort XI_A = 0x1000;
        const ushort XI_B = 0x2000;
        const ushort XI_X = 0x4000;
        const ushort XI_Y = 0x8000;

        // A DualSense stick axis is one unsigned byte centred on 128, so the XInput-scale
        // deadzone of 8000/32767 is 8000/32768*128 = 31 counts either side of centre.
        const int StickDeadzoneByte = 31;
        const short XiDeadzone = 8000;
        const int RepeatFirstMs = 400;   // hold this long before the rail starts scrolling...
        const int RepeatNextMs = 200;    // ...then one tile every 200 ms (~5/sec)

        readonly Options _opt;
        readonly WebView2 _web = new WebView2();
        readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer _padTimer = new System.Windows.Forms.Timer();
        readonly XInput _xi = new XInput();
        readonly DualSense _ds = new DualSense();
        readonly DualSenseHaptics _haptics;

        ChildTracker _tracker;
        bool _childRunning;
        int _exitCode;
        bool _exiting;
        bool _webReady;

        // ── The app the shell has yielded the screen to ───────────────────────────────
        // One at a time, deliberately: a console shows one thing at a time and the return
        // path (forced foreground) has exactly one destination. Null whenever the shell
        // owns the screen.
        sealed class RunningApp
        {
            public string Id;          // library entry id, for "is this the same tile?"
            public string Title;
            public string Kind;        // exe | uri | aumid
            public string EntryKind;   // game | launcher | app - what the LIBRARY calls it,
                                       // which is a different question from how it was started
                                       // and the one pointer mode needs: a game reads the pad
                                       // itself, a launcher expects a mouse.
            public string Target;      // the executable path, for exe launches
            public int Pid;
            public bool Tracked;       // false = URI stub, nothing to wait on
        }
        RunningApp _running;

        // True once the human has pressed the guide button and pulled the shell back over a
        // still-running app. It is what re-opens the pad gate without pretending the app is
        // gone: _childRunning stays true so the same tile raises it again instead of starting
        // a second copy.
        bool _shellPulledForward;

        // ── Pointer mode ─────────────────────────────────────────────────────────────────
        // See the "pointer mode" region below for the rules. These live here because the pad
        // pump, the foreground gate and the web-message router all touch them.
        readonly System.Windows.Forms.Timer _ptrTimer = new System.Windows.Forms.Timer();
        readonly PointerMode _ptr = new PointerMode();
        bool _ptrOn;
        string _ptrReason = "";
        int _ptrManual;                       // 0 = follow the rules, +1 = L3 on, -1 = L3 off
        int _ptrManualSaved;
        DateTime _ptrEvalAt = DateTime.MinValue;
        bool _ptrTyping;                      // the OSK is up on the shell, for a foreign window
        IntPtr _ptrTypeHwnd = IntPtr.Zero;
        string _ptrTypeTitle = "";
        POINT _ptrMoveFrom;
        bool _ptrMoving;
        // --walk stick tokens: a synthetic thumbstick, so pointer mode is drivable with
        // --no-pad on a bench where the live shell owns the real one.
        double _synthLX, _synthLY, _synthRX, _synthRY;
        DateTime _synthUntil = DateTime.MinValue;

        // The touchpad, as a trackpad. Contact state lives here rather than in the reader
        // thread because it is per-gesture, and a gesture spans reports.
        bool _touchDown, _touchTwo;
        int _touchLastX, _touchLastY;
        DateTime _touchStart = DateTime.MinValue;
        double _touchTravel;
        // …and its synthetic twin, for --walk.
        bool _synthTouch, _synthTouchTwo;
        int _stx0, _sty0, _stx1, _sty1, _stMs;
        DateTime _stStart = DateTime.MinValue;

        // Windows refusing the input (UIPI). See PointerRefused.
        int _ptrRefusals;
        IntPtr _ptrBlockedFor = IntPtr.Zero;

        int _ownPid;
        bool _padGateClosed;
        DateTime _gateCheckedAt = DateTime.MinValue;
        bool _gateClosedCache;
        int _gateDropped;
        DateTime _guideAt = DateTime.MinValue;
        const int GuideDebounceMs = 250;

        // ── The guide button's press machine ─────────────────────────────────────────────
        // A short press opens the guide menu; a hold goes straight to the shell. The hold
        // threshold is 700 ms: long enough that a normal tap (a human press-and-release is
        // 60-150 ms) never reaches it, short enough that the human is not left wondering
        // whether the button did anything. A hold that fires MUST NOT also fire the short
        // press when the button comes up, which is what _psConsumed is for.
        const int GuideHoldMs = 700;
        bool _psDown;
        DateTime _psDownAt = DateTime.MinValue;
        bool _psConsumed;

        // What is running behind the shell, published to the page rather than polled by it.
        DateTime _appsPublishedAt = DateTime.MinValue;
        string _lastAppsJson = "";

        DateTime _t0;
        DateTime _launchedAt = DateTime.MinValue;
        DateTime _returnedAt = DateTime.MinValue;
        bool _didAutoKill, _didAutoExit;
        int _launchCount, _returnCount, _returnsPassed, _returnsFailed;
        readonly List<string> _pathsUsed = new List<string>();
        string _gamepadReport = "(no gamepad report from the page yet)";

        bool _padConnected;
        string _padTransport = "";
        uint _xiConnectedMask;
        ushort _xiLastButtons;
        int _padDir;                              // -1 / 0 / +1 held direction, either device
        DateTime _padRepeatAt = DateTime.MaxValue;
        int _padDirV;                             // the same, vertically
        DateTime _padRepeatAtV = DateTime.MaxValue;
        int _hidPressed;                          // buttons we have announced a press for
        int _padActionCount;
        string _lastPadAction = "(none yet)";

        // --pad-selftest drives the page through the same host->page channel a real pad uses,
        // so the routing can be proven end-to-end without a human at the console. It covers
        // everything except the HID button decode itself, which is verified in ShellHost.cs.
        // "launch" is deliberately excluded: it would spawn the child.
        // "launch" is deliberately excluded: it would spawn the child. Everything
        // else in the vocabulary is here, including the vertical axis and the face
        // buttons the focus manager binds, and the walk deliberately ends back at
        // the shell root so the run leaves the console where it found it.
        static readonly string[] SelfTestSeq = new string[] {
            "right", "right", "up", "left", "down", "tabMedia", "right", "tabPlay",
            "square", "down", "back",
            "cc", "right", "right", "up", "down", "back", "back"
        };
        // --sys-selftest walks the settings tree instead: control centre -> Settings -> every
        // category in turn, so each pane really builds itself against the real machine and says
        // so in the log. It ends back at the shell root, like the pad walk does.
        // The control centre's sixth item (index 5) is Settings; five rights get there from mic.
        // Then it backs out to the control centre and works the Sound panel's volume slider
        // with two rights and two lefts - a real pair of writes to the audio endpoint that
        // deliberately nets to zero, so the run leaves the volume exactly where it found it.
        static readonly string[] SettingsSelfTestSeq = new string[] {
            "cc", "right", "right", "right", "right", "right", "select",
            "down", "down", "down", "down", "down", "down", "down", "down",
            "right", "left",
            "back",
            "left", "left", "left", "left", "select",
            "right", "right", "left", "left",
            "back", "back"
        };
        // --display-selftest proves the one safety mechanism that cannot be argued about on
        // paper: pick a mode, then press NOTHING. The walk stops after selecting the mode, so
        // the countdown runs out on its own and the page must put the old mode back by itself.
        // Never run this on a machine you cannot see; on the bench it is off by default.
        //   Settings -> Display -> cross into the pane -> "Resolution and refresh rate"
        //   -> second row in the mode list -> select -> silence.
        static readonly string[] DisplaySelfTestSeq = new string[] {
            "cc", "right", "right", "right", "right", "right", "select",
            "down", "right", "select", "down", "select"
        };
        bool _browseSent;
        string[] _selfSeq = SelfTestSeq;
        int _selfGapMs = 600;
        int _selfTestAt;
        DateTime _selfTestNext = DateTime.MaxValue;

        // A walk step that is still holding a button down. See the hold:/press:/release:
        // vocabulary in OnTick - a press with no release is how a HOLD is expressed, and
        // this is the timer that eventually lets go of it.
        DateTime _walkRelAt = DateTime.MaxValue;
        string _walkRelAction, _walkRelButton;

        // The Settings screen's channel into SystemApi. Runs on its own thread; see SysWorker.
        SysWorker _sys;

        // The file explorer's channel into FileApi. Its own thread and its own queue, so a
        // directory sweep cannot sit in front of the volume slider; see FileWorker.
        FileWorker _files;

        // The home rail's channel into LibraryApi - what is actually installed. Its own thread
        // and its own queue again: a cold scan is seconds long and must not sit in front of the
        // explorer's directory reads, nor they in front of it. See LibWorker.
        LibWorker _lib;

        // The browser. A second WebView2 world in its own environment and its own process
        // tree, living inside _contentPanel. Read the comment on BrowserHost first.
        readonly Panel _contentPanel = new Panel();
        BrowserHost _browser;

        public int ExitCodeValue { get { return _exitCode; } }
        public string GamepadReport { get { return _gamepadReport; } }

        public HostForm(Options opt)
        {
            _opt = opt;
            _haptics = new DualSenseHaptics(_ds);

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            BackColor = Void;                   // no white flash behind the control
            TopMost = false;                    // hard requirement: never fight games
            ShowInTaskbar = true;               // needed so restore-from-minimized behaves
            KeyPreview = true;
            Text = "MarwanOS";
            DoubleBuffered = true;

            Rectangle b = Screen.PrimaryScreen.Bounds;
            if (_opt.Windowed) b = new Rectangle(b.X + 80, b.Y + 80, 1280, 760);
            Bounds = b;

            _web.Dock = DockStyle.Fill;
            _web.DefaultBackgroundColor = Void; // applied to the controller before first paint
            _web.CreationProperties = new CoreWebView2CreationProperties();
            _web.CreationProperties.UserDataFolder = _opt.UserDataFolder;
            // The shell has a sound palette (ui/sfx.js) and Chromium keeps an AudioContext
            // suspended until the page has user activation. This page never gets any: every
            // input arrives as a relayed HID report, and a relayed report is not a gesture,
            // so without this flag the shell is permanently silent. Measured on the bench:
            // without it a fresh AudioContext reports state=suspended and resume() never
            // settles at all. Set on CreationProperties rather than through the
            // WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS environment variable because the
            // environment variable demonstrably did not reach the browser process here.
            // The content browser passes the same flag through its own environment options.
            _web.CreationProperties.AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required";
            _web.CoreWebView2InitializationCompleted += OnCoreInit;
            Controls.Add(_web);

            // The browser's content rectangle. A real child control rather than a raw HWND
            // parented to the form, because WinForms then owns the z-order and one
            // BringToFront() is the whole story - the alternative is SetWindowPos against a
            // handle the WebView2 wrapper does not expose. It starts hidden and stays hidden
            // until the browser is opened, so nothing about the shell changes for a human
            // who never uses it.
            _contentPanel.BackColor = Void;
            _contentPanel.Visible = false;
            _contentPanel.Bounds = new Rectangle(0, 0, 16, 16);
            Controls.Add(_contentPanel);
            _contentPanel.BringToFront();

            KeyDown += OnFormKeyDown;
            Load += OnLoadForm;
            FormClosing += OnClosingForm;
        }

        #region external URL handoff  (MarwanOS as the machine's browser)

        // Set once the shell page - not boot.html - has finished loading. Nothing can be
        // asked of the page before that, and a URL handed to the shell during the boot
        // sequence is normal: the console autologs in, and a queued link from the last
        // session arrives while the boot animation is still playing.
        bool _shellPageUp;

        // The URL an outside process handed us that the page has not been told about yet.
        // One slot, last-wins, same reasoning as the on-disk queue in OpenUrl.cs.
        string _openUrlPending;

        // The on-disk queue is read exactly once per shell start.
        bool _openUrlFileRead;

        const int WM_COPYDATA_MSG = 0x004A;
        const int OpenUrlMagic = 0x4D4F5355;   // 'MOSU' - the tag MarwanOpenUrl.exe sends

        [StructLayout(LayoutKind.Sequential)]
        struct COPYDATASTRUCT
        {
            public IntPtr dwData;
            public int cbData;
            public IntPtr lpData;
        }

        /// <summary>
        /// The receiving end of MarwanOpenUrl.exe.
        ///
        /// This is how a link clicked anywhere on the machine - in a game's launcher, in
        /// Riot's client, in a file explorer - ends up in the console's own browser instead
        /// of starting a second copy of the shell. See the header of OpenUrl.cs for why the
        /// registered handler is a separate program and this is only its inbox.
        ///
        /// The message is trusted no further than any other local caller: anything running
        /// in this session can post it, so the URL is re-validated here rather than on the
        /// sender's word. Returning 1 tells the sender it was accepted; returning 0 makes
        /// the sender fall through to its queue, which is the honest answer when the page
        /// is not up yet - except that we hold it ourselves, so we return 1 either way and
        /// deliver when the page arrives.
        /// </summary>
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_COPYDATA_MSG && m.LParam != IntPtr.Zero)
            {
                COPYDATASTRUCT cds = (COPYDATASTRUCT)Marshal.PtrToStructure(m.LParam, typeof(COPYDATASTRUCT));
                if (cds.dwData.ToInt64() == OpenUrlMagic)
                {
                    string url = null;
                    try
                    {
                        if (cds.cbData > 0 && cds.cbData <= 8192 && cds.lpData != IntPtr.Zero)
                            url = Marshal.PtrToStringUni(cds.lpData, cds.cbData / 2).TrimEnd('\0');
                    }
                    catch (Exception ex) { Log.Write("OPENURL", "unreadable WM_COPYDATA: " + ex.Message); }

                    m.Result = AcceptOpenUrl(url, "WM_COPYDATA") ? new IntPtr(1) : IntPtr.Zero;
                    return;
                }
            }
            base.WndProc(ref m);
        }

        /// <summary>
        /// http, https and file only, 2048 characters, no control characters - the same rule
        /// the sender applies, applied again because this end is the one that matters.
        /// </summary>
        static bool UrlIsOpenable(string url)
        {
            if (string.IsNullOrEmpty(url) || url.Length > 2048) return false;
            for (int i = 0; i < url.Length; i++) if (char.IsControl(url[i])) return false;

            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
            string s = u.Scheme.ToLowerInvariant();
            return s == "http" || s == "https" || s == "file";
        }

        bool AcceptOpenUrl(string url, string via)
        {
            if (!UrlIsOpenable(url))
            {
                Log.Write("OPENURL", "REFUSED (" + via + "): '"
                    + (url == null ? "(null)" : (url.Length > 160 ? url.Substring(0, 160) + "..." : url)) + "'");
                return false;
            }

            Log.Write("OPENURL", "accepted (" + via + "): " + url);
            _openUrlPending = url;
            DrainOpenUrl();
            return true;
        }

        /// <summary>
        /// Hand whatever is waiting to the page, once the page exists.
        ///
        /// It goes through the same {"type":"browse"} message --browse uses, for the same
        /// reason: calling BrowserHost.OpenBrowser directly would show a content view with
        /// no chrome around it and no bounds set. The page opens the browser the way Cross
        /// opens it.
        ///
        /// The shell is also pulled to the foreground. A link is only ever clicked by
        /// something the person is looking at, and if that something was a game running in
        /// front of the shell, opening a tab behind it would look like nothing happened.
        /// </summary>
        void DrainOpenUrl()
        {
            if (!_shellPageUp) return;

            // The queue file is read on the first drain after the page comes up, whether or
            // not something live is already waiting - reading it only when the live slot is
            // empty would leave a queued URL unread forever on any start where a link was
            // clicked during the boot sequence, and leave the file on disk to reopen at the
            // next boot instead.
            if (!_openUrlFileRead)
            {
                _openUrlFileRead = true;
                string queued = ReadQueuedUrl();
                if (!string.IsNullOrEmpty(queued))
                {
                    if (string.IsNullOrEmpty(_openUrlPending)) _openUrlPending = queued;
                    else Log.Write("OPENURL", "dropping the queued URL in favour of the live one: " + queued);
                }
            }

            if (string.IsNullOrEmpty(_openUrlPending)) return;

            string url = _openUrlPending;
            _openUrlPending = null;

            Log.Write("OPENURL", "asking the page to open " + url);
            PostToPage("{\"type\":\"browse\",\"url\":\"" + JsonEsc(url) + "\"}");

            string how = Foreground.ForceForeground(Handle);
            Log.Write("OPENURL", "foreground for the shell: " + (how == null ? "FAILED" : how));
        }

        /// <summary>
        /// The one-slot queue MarwanOpenUrl.exe writes when no shell was running. Read once
        /// and deleted, so a link clicked yesterday does not reopen on every boot from now on.
        /// </summary>
        string ReadQueuedUrl()
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                OnDisk.Brand + "\\openurl\\pending.url");
            try
            {
                if (!File.Exists(path)) return null;
                string url = File.ReadAllText(path).Trim();
                File.Delete(path);
                if (!UrlIsOpenable(url)) { Log.Write("OPENURL", "queued URL refused: " + url); return null; }
                Log.Write("OPENURL", "picked up a URL queued while the shell was down: " + url);
                return url;
            }
            catch (Exception ex) { Log.Write("OPENURL", "could not read " + path + ": " + ex.Message); return null; }
        }

        #endregion

        #region startup

        void OnLoadForm(object sender, EventArgs e)
        {
            _t0 = DateTime.Now;
            _ownPid = Process.GetCurrentProcess().Id;
            Log.Write("HOST", "=========================================================");
            Log.Write("HOST", "ShellHostWeb started. args='" + _opt.RawArgs + "'");
            Log.Write("HOST", "assets folder     = '" + _opt.AssetFolder + "'");
            Log.Write("HOST", "user data folder  = '" + _opt.UserDataFolder + "'");
            Log.Write("HOST", "start url         = '" + _opt.StartUrl + "'");
            Log.Write("HOST", "child command     = '" + _opt.ChildCommand + "'");
            Log.Write("HOST", "hwnd=0x" + Handle.ToInt64().ToString("X") + " pid=" + Process.GetCurrentProcess().Id
                + " bounds=" + Bounds + " topmost=" + TopMost + " session=" + Process.GetCurrentProcess().SessionId);

            if (!Directory.Exists(_opt.AssetFolder))
                Log.Write("ASSETS", "WARN: asset folder does not exist: " + _opt.AssetFolder);
            else
                Log.Write("ASSETS", "index.html present=" + File.Exists(Path.Combine(_opt.AssetFolder, "index.html"))
                    + " boot.html present=" + File.Exists(Path.Combine(_opt.AssetFolder, "boot.html")));

            try { Directory.CreateDirectory(_opt.UserDataFolder); }
            catch (Exception ex) { Log.Write("WEBVIEW", "WARN: could not create user data folder: " + ex.Message); }

            _timer.Interval = 200;
            _timer.Tick += OnTick;
            _timer.Start();

            // Pointer mode runs on its own 60 Hz timer rather than on the pad's, because it
            // must work with --no-pad (the bench drives it from --walk tokens while the live
            // shell owns the real DualSense) and because it has to keep evaluating what is in
            // the foreground even when nobody is touching anything.
            _ptrTimer.Interval = 16;
            _ptrTimer.Tick += PointerTimerTick;
            _ptrTimer.Start();
            Log.Write("PTR", _opt.PtrDisabled
                ? "pointer mode DISABLED by --ptr=off"
                : "pointer mode armed" + (_opt.PtrForce ? " and FORCED ON by --ptr=on" : "")
                  + ": touchpad surface and left stick both move the cursor, d-pad steps "
                  + PtrStepPx + " px, Cross and the touchpad button = left, Square = right,"
                  + " two fingers or the right stick = wheel, a touchpad tap = a click,"
                  + " L1/R1=PageUp/PageDown, Options=Enter, Circle=Escape, Triangle=keyboard,"
                  + " L3 toggles. Off whenever this shell's own window is in front.");

            if (_opt.NoPad)
            {
                Log.Write("PAD", "gamepad input DISABLED by --no-pad");
            }
            else
            {
                // XInput first: cheap, and it covers a future Xbox pad. It will never see a
                // DualSense, which is why the raw HID reader below exists at all.
                try { _xi.Load(); }
                catch (Exception ex) { Log.Write("XINPUT", "WARN: XInput load threw: " + ex.Message); }

                Log.Write("PAD", "starting raw HID reader thread (DualSense / DualShock)");
                try { _ds.Start(); }
                catch (Exception ex) { Log.Write("PAD", "WARN: reader thread failed to start: " + ex.Message); }

                // Haptics ride alongside on their own thread and their own write handle, so a
                // pad that refuses output reports costs the shell nothing at all.
                try { _haptics.Start(); Log.Write("HAPTIC", "effect thread started, intensity "
                        + Math.Round(_haptics.Intensity * 100) + "%"); }
                catch (Exception ex) { Log.Write("HAPTIC", "WARN: haptics failed to start: " + ex.Message); }

                _padTimer.Interval = 16;   // ~60 Hz; the reader thread latches edges between ticks
                _padTimer.Tick += OnPadTick;
                _padTimer.Start();
                Log.Write("PAD", "pad->page routing armed (deadzone=" + StickDeadzoneByte
                    + "/128 counts, repeat " + RepeatFirstMs + " ms then every " + RepeatNextMs + " ms)");
                Log.Write("PAD", "mapping: DPadL/R+LStickX=left/right, DPadU/D+LStickY=up/down,"
                    + " Cross=launch, Circle=back, Square=square, Triangle=triangle,"
                    + " L1=tabPlay, R1=tabMedia, Options/Touchpad=cc, Create=create, PS=guide."
                    + " Every message carries button+phase (press/repeat/release)."
                    + " No pad button can exit this host - Esc/F2/F3 remain the only exits.");
                Log.Write("GATE", _opt.NoFgGate
                    ? "foreground gate DISABLED by --no-fg-gate: this instance will act on the pad"
                      + " even when it is not the front window. Diagnostic only."
                    : "foreground gate ARMED: the shell acts on the pad only while its own window"
                      + " is in the foreground. The guide button (PS) is the single exception and"
                      + " brings the shell back over a running app.");
            }

            // Take foreground on startup so keyboard fallbacks work unattended.
            string p = Foreground.ForceForeground(Handle);
            Log.Write("FOREGROUND", "startup activation via " + (p == null ? "FAILED" : p));

            Log.Write("WEBVIEW", "calling EnsureCoreWebView2Async...");
            try
            {
                Task t = _web.EnsureCoreWebView2Async(null);
                t.ContinueWith(delegate(Task done)
                {
                    if (done.IsFaulted && done.Exception != null)
                        Log.Write("WEBVIEW", "FATAL: EnsureCoreWebView2Async faulted: " + done.Exception.ToString());
                });
            }
            catch (Exception ex)
            {
                Log.Write("WEBVIEW", "FATAL: EnsureCoreWebView2Async threw synchronously: " + ex.ToString());
            }
        }

        void OnCoreInit(object sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                Log.Write("WEBVIEW", "FATAL: CoreWebView2 initialization FAILED: "
                    + (e.InitializationException == null ? "(no exception)" : e.InitializationException.ToString()));
                ExitWith(101, "webview-init-failed");
                return;
            }

            CoreWebView2 core = _web.CoreWebView2;
            _webReady = true;
            Log.Write("WEBVIEW", "CoreWebView2 initialised OK. runtime=" + core.Environment.BrowserVersionString
                + " browserPid=" + core.BrowserProcessId);

            try { _web.DefaultBackgroundColor = Void; }
            catch (Exception ex) { Log.Write("WEBVIEW", "WARN: DefaultBackgroundColor: " + ex.Message); }

            // --- kiosk hygiene ---
            CoreWebView2Settings s = core.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.AreDevToolsEnabled = _opt.DevTools;
            s.IsZoomControlEnabled = false;
            s.IsStatusBarEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;
            s.IsBuiltInErrorPageEnabled = false;   // a built-in error page would be a white flash
            s.IsPasswordAutosaveEnabled = false;
            s.IsGeneralAutofillEnabled = false;
            s.IsWebMessageEnabled = true;
            Log.Write("WEBVIEW", "kiosk settings applied (contextMenus=off devTools=" + _opt.DevTools
                + " zoom=off statusBar=off browserAccelKeys=off builtInErrorPage=off)");

            // --- serve the assets over a virtual host: avoids every file:// restriction ---
            try
            {
                core.SetVirtualHostNameToFolderMapping(_opt.VirtualHost, _opt.AssetFolder,
                    CoreWebView2HostResourceAccessKind.Allow);
                Log.Write("WEBVIEW", "virtual host mapping OK: https://" + _opt.VirtualHost + "/ -> " + _opt.AssetFolder);
            }
            catch (Exception ex)
            {
                Log.Write("WEBVIEW", "WARN: SetVirtualHostNameToFolderMapping failed (" + ex.Message
                    + ") - falling back to file:// URLs");
                _opt.UseFileUrls = true;
            }

            // The Settings screen's channel into the OS. Started before the first navigation so
            // the page can call it the moment it loads.
            try
            {
                _sys = new SysWorker(OnSysReply);
                _sys.Start();
            }
            catch (Exception ex)
            {
                _sys = null;
                Log.Write("SYS", "FATAL for settings only: could not start the SystemApi worker: " + ex.Message
                    + " - sys.* calls will be answered with sys_unavailable and the panels will say so");
            }

            // The file explorer's channel into the file system. Same deal, separate queue.
            try
            {
                _files = new FileWorker(OnFileReply);
                _files.Start();
            }
            catch (Exception ex)
            {
                _files = null;
                Log.Write("FS", "FATAL for the explorer only: could not start the FileApi worker: " + ex.Message
                    + " - fs.* calls will be answered with fs_unavailable and the explorer will say so");
            }

            // The home rail's channel into the installed-software scan. Third queue, same deal.
            try
            {
                _lib = new LibWorker(OnLibReply);
                _lib.Start();
            }
            catch (Exception ex)
            {
                _lib = null;
                Log.Write("LIB", "FATAL for the home rail only: could not start the LibraryApi worker: " + ex.Message
                    + " - lib.* calls will be answered with lib_unavailable and the rail will say so");
            }

            // The host object the explorer probes for before the message channel. Off by default
            // on purpose: its calls land on THIS thread, and FileApi blocks. See FileApiBridge.
            if (_opt.FileHostObject)
            {
                try
                {
                    core.AddHostObjectToScript("mosFiles", new FileApiBridge());
                    Log.Write("FS", "host object 'mosFiles' registered (--file-host-object). "
                        + "WARNING: FileApi will run on the UI thread for every call the explorer makes.");
                }
                catch (Exception ex)
                {
                    Log.Write("FS", "AddHostObjectToScript('mosFiles') failed: " + ex.Message
                        + " - the explorer will fall back to the {type:\"fs\"} message channel");
                }
            }
            else
            {
                Log.Write("FS", "host object 'mosFiles' NOT registered; the explorer will select the "
                    + "{type:\"fs\"} message channel, which is answered on the FileApi worker thread");
            }

            // The browser. Its injected navigation layer is read off disk rather than being
            // a string constant in here: ui/mosnav.js is real, checkable JavaScript, and the
            // host is only its courier. A missing file disables the browser and says so; it
            // does not stop the shell.
            try
            {
                string navPath = Path.Combine(_opt.AssetFolder, "mosnav.js");
                if (!File.Exists(navPath))
                {
                    Log.Write("BROWSER", "mosnav.js is not next to index.html (" + navPath
                        + ") - the browser will refuse to open rather than load pages it cannot navigate");
                }
                else
                {
                    string nav = File.ReadAllText(navPath);
                    _browser = new BrowserHost(_contentPanel, PostToPage, nav,
                        _opt.UserDataFolder + "-content", OnAcceleratorKey);
                    Log.Write("BROWSER", "browser host ready; mosnav.js is " + nav.Length
                        + " bytes, cap " + BrowserHost.MaxTabs + " tabs");
                }
            }
            catch (Exception ex)
            {
                _browser = null;
                Log.Write("BROWSER", "FATAL for the browser only: " + ex.Message);
            }

            core.WebMessageReceived += OnWebMessage;
            core.NavigationStarting += OnNavStarting;
            core.NavigationCompleted += OnNavCompleted;
            core.ProcessFailed += OnProcessFailed;
            core.NewWindowRequested += OnNewWindowRequested;
            core.ContainsFullScreenElementChanged += OnFullScreenChanged;
            HookAcceleratorKeys();
            core.DocumentTitleChanged += delegate(object o, object a)
            {
                Log.Write("PAGE", "document title = '" + _web.CoreWebView2.DocumentTitle + "'");
            };

            // Register the shim FIRST, then navigate - otherwise the first document can race past it.
            Task<string> add = core.AddScriptToExecuteOnDocumentCreatedAsync(Shim);
            add.ContinueWith(delegate(Task<string> done)
            {
                if (done.IsFaulted && done.Exception != null)
                    Log.Write("WEBVIEW", "WARN: AddScriptToExecuteOnDocumentCreatedAsync faulted: " + done.Exception.Message);
                else
                    Log.Write("WEBVIEW", "shim registered (AddScriptToExecuteOnDocumentCreated id=" + done.Result + ")");
                NavigateToStart();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void NavigateToStart()
        {
            string url = _opt.ResolveStartUrl();
            Log.Write("NAV", "start url decided: " + _opt.StartUrlReason);
            Log.Write("NAV", "navigating to " + url);
            try
            {
                _web.CoreWebView2.Navigate(url);
                _web.Focus();
            }
            catch (Exception ex)
            {
                Log.Write("NAV", "FATAL: Navigate threw: " + ex.ToString());
            }
        }

        #endregion

        #region webview events

        void OnNavStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            Log.Write("NAV", "starting -> " + e.Uri);
        }

        void OnNavCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            Log.Write("NAV", "completed success=" + e.IsSuccess + " status=" + e.WebErrorStatus
                + " url=" + _web.Source);
            if (e.IsSuccess) _web.Focus();

            // Match on the path only: the boot URL carries "next=index.html" in its query,
            // and firing the self-test at boot.html would test a page that has no shell UI.
            string src = (_web.Source == null) ? "" : _web.Source.ToString();
            int qmark = src.IndexOf('?');
            if (qmark >= 0) src = src.Substring(0, qmark);

            bool onShell = src.EndsWith("index.html", StringComparison.OrdinalIgnoreCase);

            // The page can be asked for things from here on. Anything an outside process
            // handed us during the boot sequence - or left in the queue file while the shell
            // was down - is delivered now. See the external URL handoff region.
            if (e.IsSuccess && onShell && !_shellPageUp)
            {
                _shellPageUp = true;
                DrainOpenUrl();
            }

            if (_opt.PadSelfTest && e.IsSuccess && _selfTestNext == DateTime.MaxValue && onShell)
            {
                _selfSeq = SelfTestSeq;
                _selfGapMs = 600;
                _selfTestNext = DateTime.Now.AddMilliseconds(2000);
                Log.Write("PAD", "--pad-selftest armed: " + _selfSeq.Length
                    + " synthetic pad actions over the real host->page channel, starting in 2 s");
            }

            if (_opt.SysSelfTest && e.IsSuccess && _selfTestNext == DateTime.MaxValue && onShell)
            {
                _selfSeq = SettingsSelfTestSeq;
                _selfGapMs = 2000;                 // each category reads the OS before it renders
                _selfTestNext = DateTime.Now.AddMilliseconds(4000);
                Log.Write("SYS", "--sys-selftest armed: a direct read-only command sweep, then "
                    + _selfSeq.Length + " pad actions walking every settings category ("
                    + _selfGapMs + " ms apart, starting in 4 s)");
                SysCommandSweep();
            }

            // --browse opens the browser on a real URL before the walk starts, so an
            // unattended run can exercise spatial navigation on an actual site. The page
            // does the opening, not this class: going straight to BrowserHost would leave
            // the chrome undrawn and the content view with no bounds.
            if (!string.IsNullOrEmpty(_opt.Browse) && e.IsSuccess && onShell && !_browseSent)
            {
                _browseSent = true;
                Log.Write("BROWSER", "--browse: asking the page to open " + _opt.Browse);
                PostToPage("{\"type\":\"browse\",\"url\":\"" + JsonEsc(_opt.Browse) + "\"}");
            }

            // --walk is the general form of all of the above: an explicit comma-separated list
            // of pad actions. Anything that can be reached with the pad can be verified with no
            // human present, which is the only kind of verification a TV shell can have.
            if (!string.IsNullOrEmpty(_opt.Walk) && e.IsSuccess && _selfTestNext == DateTime.MaxValue && onShell)
            {
                List<string> steps = new List<string>();
                foreach (string s2 in _opt.Walk.Split(','))
                {
                    string t = s2.Trim();
                    if (t.Length > 0) steps.Add(t);
                }
                if (steps.Count > 0)
                {
                    _selfSeq = steps.ToArray();
                    _selfGapMs = _opt.WalkGapMs;
                    _selfTestNext = DateTime.Now.AddMilliseconds(4000);
                    Log.Write("WALK", "--walk armed: " + _selfSeq.Length + " actions, "
                        + _selfGapMs + " ms apart, starting in 4 s: " + string.Join(" ", _selfSeq));
                }
            }

            if (_opt.DisplaySelfTest && e.IsSuccess && _selfTestNext == DateTime.MaxValue && onShell)
            {
                _selfSeq = DisplaySelfTestSeq;
                _selfGapMs = 2600;
                _selfTestNext = DateTime.Now.AddMilliseconds(4000);
                Log.Write("SYS", "--display-selftest armed: walks to a display mode, applies it, then "
                    + "DELIBERATELY presses nothing so the confirm-or-revert countdown has to revert on its own");
            }
        }

        /// <summary>
        /// Fire every READ-ONLY command straight at the worker. Nothing here writes, disconnects,
        /// pairs, changes a mode or ends a session: the point is to prove each one answers in the
        /// shell's real security context and session, and the log line SysWorker writes for each
        /// is the evidence. display.* is included deliberately - it is the one family that cannot
        /// be verified over SSH, because session 0 has no window station.
        /// </summary>
        void SysCommandSweep()
        {
            string[] reads = new string[] {
                "api.version", "sys.privileges", "sys.info", "sys.storage", "sys.time",
                "sys.locale", "power.status", "net.status", "audio.devices",
                "display.list", "display.modes",
                "wifi.status", "wifi.list", "bt.status", "bt.devices", "accounts.list"
            };
            for (int i = 0; i < reads.Length; i++)
            {
                string body = "{\"type\":\"sys\",\"reqId\":\"sweep-" + (i + 1) + "\",\"cmd\":\"" + reads[i] + "\"";
                if (reads[i] == "display.list") body += ",\"modes\":true";
                body += "}";
                _sys.Post(body);
            }
            Log.Write("SYS", "sweep queued: " + reads.Length + " read-only commands");
        }

        void OnProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
        {
            Log.Write("WEBVIEW", "PROCESS FAILED kind=" + e.ProcessFailedKind + " reason=" + e.Reason
                + " exit=" + e.ExitCode + " desc=" + e.ProcessDescription);
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
                ExitWith(102, "browser-process-exited");
        }

        void OnNewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            // A kiosk never opens a second window; keep the navigation in-place.
            e.Handled = true;
            Log.Write("NAV", "new window suppressed for " + e.Uri);
            try { _web.CoreWebView2.Navigate(e.Uri); }
            catch { }
        }

        void OnFullScreenChanged(object sender, object e)
        {
            Log.Write("PAGE", "ContainsFullScreenElement = " + _web.CoreWebView2.ContainsFullScreenElement);
        }

        // AcceleratorKeyPressed lives on CoreWebView2Controller, which the WinForms wrapper keeps
        // private. Reach it once by reflection; if that ever breaks we still have the injected shim
        // and the WinForms KeyDown path, so the console is never without an exit.
        void HookAcceleratorKeys()
        {
            try
            {
                System.Reflection.FieldInfo fi = typeof(WebView2).GetField("_coreWebView2Controller",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (fi == null)
                {
                    Log.Write("KEY", "WARN: _coreWebView2Controller field not found - no native accelerator hook");
                    return;
                }
                CoreWebView2Controller ctl = fi.GetValue(_web) as CoreWebView2Controller;
                if (ctl == null)
                {
                    Log.Write("KEY", "WARN: controller instance was null - no native accelerator hook");
                    return;
                }
                ctl.AcceleratorKeyPressed += OnAcceleratorKey;
                Log.Write("KEY", "native AcceleratorKeyPressed hook installed (Esc=exit0, F2=exit2, F3=exit3, F9=launch)");
            }
            catch (Exception ex)
            {
                Log.Write("KEY", "WARN: accelerator hook failed: " + ex.Message);
            }
        }

        // Native escape hatch. AcceleratorKeyPressed only fires for keys that are NOT character input,
        // so Escape and the F-keys arrive here even if the page's JavaScript is dead. Digits 2/3 are
        // character input and therefore arrive through the injected shim instead.
        void OnAcceleratorKey(object sender, CoreWebView2AcceleratorKeyPressedEventArgs e)
        {
            if (e.KeyEventKind != CoreWebView2KeyEventKind.KeyDown
                && e.KeyEventKind != CoreWebView2KeyEventKind.SystemKeyDown) return;

            Keys k = (Keys)e.VirtualKey;
            if (k == Keys.Escape) { e.Handled = true; Log.Write("KEY", "native accelerator: Escape"); ExitWith(0, "Esc(native)"); }
            else if (k == Keys.F2) { e.Handled = true; Log.Write("KEY", "native accelerator: F2"); ExitWith(2, "F2(native)"); }
            else if (k == Keys.F3) { e.Handled = true; Log.Write("KEY", "native accelerator: F3"); ExitWith(3, "F3(native)"); }
            else if (k == Keys.F5) { e.Handled = true; Log.Write("KEY", "native accelerator: F5 (reload suppressed)"); }
            else if (k == Keys.F9) { e.Handled = true; Log.Write("KEY", "native accelerator: F9 -> launch"); Launch(_opt.ChildCommand); }
        }

        // Message handling must never take the shell down; the page is chattier now that the
        // pad channel echoes every action back.
        void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try { HandleWebMessage(e); }
            catch (Exception ex) { Log.Write("MSG", "web message handler caught (swallowed): " + ex.ToString()); }
        }

        void HandleWebMessage(CoreWebView2WebMessageReceivedEventArgs e)
        {
            string raw = null;
            try { raw = e.TryGetWebMessageAsString(); }
            catch { }
            if (raw == null)
            {
                try { raw = e.WebMessageAsJson; }
                catch { }
            }
            if (string.IsNullOrEmpty(raw)) return;

            string type = Json.Str(raw, "type");
            string target = Json.Str(raw, "target");
            if (type == null) { Log.Write("MSG", "unparsed message: " + raw); return; }

            switch (type)
            {
                case "ready":
                    Log.Write("MSG", "page shim ready on " + target);
                    // Tell the page what this host can do, so it can route power
                    // properly instead of guessing from a timeout.
                    PostToPage("{\"type\":\"hostinfo\",\"host\":\"ShellHostWeb v4\","
                        + "\"caps\":[\"power\",\"sysinfo\",\"nav\",\"phase\",\"haptic\""
                        + (_sys != null ? ",\"sys\",\"padinfo\"" : "")
                        + (_files != null ? ",\"fs\"" : "")
                        + (_lib != null ? ",\"lib\"" : "")
                        + (_browser != null ? ",\"browser\"" : "") + "]}");
                    break;
                case "log":
                    Log.Write("PAGE", target);
                    break;
                case "gamepad":
                    _gamepadReport = target;
                    Log.Write("GAMEPAD", target);
                    break;
                case "launch":
                    Log.Write("MSG", "launch requested by page, target='" + (target == null ? "" : target) + "'");
                    Launch(string.IsNullOrEmpty(target) ? _opt.ChildCommand : target);
                    break;
                case "exit":
                    int code = Json.Int(raw, "code", 0);
                    Log.Write("MSG", "exit requested by page, code=" + code + " trigger=" + target);
                    ExitWith(code, "page:" + target);
                    break;
                case "power":
                    DoPower(Json.Str(raw, "action"));
                    break;
                case "sysinfo":
                    Log.Write("MSG", "page asked for system information");
                    PostToPage(SysInfoJson());
                    break;

                // The Settings screen. The whole message IS the SystemApi request: "type" is
                // ignored by SystemApi and "reqId" is echoed back by it, so nothing needs
                // rewriting on the way in. Queued, never executed here - see SysWorker.
                case "sys":
                    if (_sys == null)
                    {
                        string rid = Json.Str(raw, "reqId");
                        Log.Write("SYS", "sys request but the worker never started: " + raw);
                        PostToPage("{\"type\":\"sysres\",\"payload\":{\"ok\":false"
                            + (rid == null ? "" : ",\"reqId\":\"" + JsonEsc(rid) + "\"")
                            + ",\"error\":\"sys_unavailable\","
                            + "\"detail\":\"the host could not start its system-control thread; "
                            + "restart the shell to try again\"}}");
                        break;
                    }
                    _sys.Post(raw);
                    break;

                // The file explorer. Unlike "sys", the FileApi command is NOT the outer message:
                // ui/files.js sends {type:"fs", reqId, command:{cmd:...}} so that its own reqId
                // can never collide with an argument named "id" inside a command. The inner
                // object is lifted out verbatim - see Json.Sub - and queued. The reply is the
                // FileApi envelope with a "type" added; files.js matches on reqId.
                case "fs":
                {
                    string fid = Json.Str(raw, "reqId");
                    if (_files == null)
                    {
                        Log.Write("FS", "fs request but the worker never started: " + raw);
                        PostToPage("{\"type\":\"fs.reply\",\"ok\":false"
                            + (fid == null ? "" : ",\"reqId\":\"" + JsonEsc(fid) + "\"")
                            + ",\"error\":\"fs_unavailable\","
                            + "\"detail\":\"the host could not start its file-system thread; "
                            + "restart the shell to try again\"}");
                        break;
                    }
                    string command = Json.Sub(raw, "command");
                    if (command == null)
                    {
                        Log.Write("FS", "fs message with no readable 'command' object: " + raw);
                        PostToPage("{\"type\":\"fs.reply\",\"ok\":false"
                            + (fid == null ? "" : ",\"reqId\":\"" + JsonEsc(fid) + "\"")
                            + ",\"error\":\"bad_request\","
                            + "\"detail\":\"the fs message carried no command object\"}");
                        break;
                    }
                    _files.Post(fid, command);
                    break;
                }

                // The home rail. Shaped exactly like "fs" and for the same reason: ui/library.js
                // sends {type:"lib", reqId, command:{cmd:...}} so that its own reqId can never
                // collide with the "id" argument lib.launch and lib.icon take. The inner object
                // is lifted out verbatim - see Json.Sub - and queued on the library worker's own
                // thread. The reply is LibraryApi's envelope with a "type" added; library.js
                // matches on reqId.
                case "lib":
                {
                    string lid = Json.Str(raw, "reqId");
                    if (_lib == null)
                    {
                        Log.Write("LIB", "lib request but the worker never started: " + Trim(raw, 200));
                        PostToPage("{\"type\":\"lib.reply\",\"ok\":false"
                            + (lid == null ? "" : ",\"reqId\":\"" + JsonEsc(lid) + "\"")
                            + ",\"error\":\"lib_unavailable\","
                            + "\"detail\":\"the host could not start its library thread; "
                            + "restart the shell to try again\"}");
                        break;
                    }
                    string libCommand = Json.Sub(raw, "command");
                    if (libCommand == null)
                    {
                        Log.Write("LIB", "lib message with no readable 'command' object: " + Trim(raw, 200));
                        PostToPage("{\"type\":\"lib.reply\",\"ok\":false"
                            + (lid == null ? "" : ",\"reqId\":\"" + JsonEsc(lid) + "\"")
                            + ",\"error\":\"bad_request\","
                            + "\"detail\":\"the lib message carried no command object\"}");
                        break;
                    }
                    _lib.Post(lid, libCommand);
                    break;
                }

                // What the page just started. LibraryApi did the starting - it owns the launch
                // paths, the protocol handlers and the packaged-app activation - and the host
                // now takes ownership of the result: hide, track the process and everything it
                // spawns, and force the foreground back when the tree is empty. See
                // OnPageLaunched for the exe/uri distinction, which is LibraryApi's and is
                // honoured rather than re-derived here.
                case "launched":
                    OnPageLaunched(raw);
                    break;

                // Asked by ui/library.js BEFORE every lib.launch. If the answer is "running",
                // the page does not launch and the host has already raised the app. This is the
                // only thing standing between an impatient human and four Steam clients.
                case "lib.run.activate":
                    OnLibActivate(raw);
                    break;

                // The guide menu's three actions, plus a way for the page to ask for the
                // running/background state instead of waiting for the next push.
                //   {"type":"app","cmd":"resume"}    raise it, hide the shell
                //   {"type":"app","cmd":"minimise"}  stay in the shell, leave it running
                //   {"type":"app","cmd":"close"}     terminate the whole tracked tree
                //   {"type":"app","cmd":"state"}     re-publish {"type":"apps",...} now
                case "app":
                    HandleAppMessage(raw, Json.Str(raw, "cmd"));
                    break;

                // Pointer mode's one page-facing channel, and it exists for one thing: typing.
                //   host->page {"type":"ptr","ev":"type","title":"<window>"}   open the keyboard
                //   page->host {"type":"ptr","cmd":"text","text":"…","enter":1} type it and go back
                //   page->host {"type":"ptr","cmd":"cancel"}                    go back, type nothing
                //   page->host {"type":"ptr","cmd":"state"}                     where is the cursor
                // Everything else about the pointer is host-side; the page is never asked to
                // draw it, because over a foreign window it could not.
                case "ptr":
                    HandlePointerMessage(raw, Json.Str(raw, "cmd"));
                    break;

                // Battery and transport for the pad the host itself is reading over raw HID.
                // The page's Gamepad API view cannot see either.
                case "padinfo":
                    PostToPage(PadInfoJson());
                    break;

                // The feel of the shell. One message per event, fired by the same code in
                // ui/sfx.js that plays the sound, so a cue and a sensation can never drift
                // apart. Deliberately fire-and-forget: the page never waits on a rumble, and
                // a pad that is unplugged is a no-op rather than an error the UI has to
                // handle. {"cmd":"status"} is the only form that answers.
                case "haptic":
                    HandleHapticMessage(raw);
                    break;

                // ── The browser ────────────────────────────────────────────────────────
                // Every one of these is a command from the shell page's own chrome. They
                // are deliberately thin: the shell page decides what the browser does, the
                // host only owns the content WebViews' lifetime, bounds and plumbing.
                case "browser":
                    HandleBrowserMessage(raw, Json.Str(raw, "cmd"));
                    break;

                default:
                    Log.Write("MSG", "unhandled message type '" + type + "': " + raw);
                    break;
            }
        }

        /// <summary>
        /// The shell page's control channel for the browser. One switch, no cleverness: the
        /// chrome is drawn and decided upstairs in index.html, and this only carries out
        /// what it asks for on the content WebViews.
        /// </summary>
        void HandleBrowserMessage(string raw, string cmd)
        {
            if (_browser == null)
            {
                Log.Write("BROWSER", "browser command '" + cmd + "' but the browser host never started");
                PostToPage("{\"type\":\"browser\",\"ev\":\"unavailable\",\"detail\":\"this host build "
                    + "could not load mosnav.js, so it will not open pages it cannot navigate\"}");
                return;
            }

            switch (cmd)
            {
                case "open":     _browser.OpenBrowser(Json.Str(raw, "url")); break;
                case "close":    _browser.CloseBrowser(); break;
                case "quit":     _browser.Quit(); break;
                case "show":     _browser.Show(Json.Int(raw, "on", 1) != 0); break;
                case "bounds":
                    _browser.SetBounds(Json.Int(raw, "x", 0), Json.Int(raw, "y", 0),
                                       Json.Int(raw, "w", 0), Json.Int(raw, "h", 0));
                    break;
                case "navigate": _browser.Navigate(Json.Str(raw, "url")); break;
                case "newtab":   _browser.NewTab(Json.Str(raw, "url")); break;
                case "closetab": _browser.CloseTab(Json.Int(raw, "tab", 0)); break;
                case "activate": _browser.Activate(Json.Int(raw, "tab", 0)); break;
                case "back":
                    // The page's own history first; the shell page decides what "no history
                    // left" means (it leaves the browser), so the answer has to go back up.
                    PostToPage("{\"type\":\"browser\",\"ev\":\"backresult\",\"went\":"
                        + (_browser.GoBack() ? "true" : "false") + "}");
                    break;
                case "forward":  _browser.GoForward(); break;
                case "reload":   _browser.Reload(); break;
                case "stop":     _browser.Stop(); break;
                case "zoom":     _browser.SetZoom(Json.Int(raw, "pct", 100) / 100.0); break;
                case "tabs":     _browser.PushTabs(); break;

                // Downloads. The chrome for them is drawn upstairs like everything else; this
                // only carries the verb to the operation the host is holding.
                case "download":
                    _browser.DownloadCommand(Json.Str(raw, "do"), Json.Int(raw, "id", 0));
                    break;

                // Permissions. The answer to one {"ev":"permission"} the host asked, and the two
                // management verbs behind the settings list. See the Permissions region in
                // BrowserHost for the whole round trip.
                case "permission":
                    _browser.PermissionReply(Json.Int(raw, "id", 0),
                                             Json.Bool(raw, "allow", false),
                                             Json.Bool(raw, "remember", false));
                    break;
                case "permissions.list":
                    _browser.PermissionsList();
                    break;
                case "permissions.forget":
                    _browser.PermissionsForget(Json.Str(raw, "origin"), Json.Str(raw, "kind"));
                    break;

                // Extensions: list, install from a file or folder, enable, disable, remove.
                case "extension":
                    _browser.ExtCommand(Json.Str(raw, "do"), Json.Str(raw, "id"), Json.Str(raw, "path"));
                    break;

                // Input. "focus" decides whether the analog sticks are streamed at all.
                case "focus":
                    _browser.ContentFocused = Json.Int(raw, "content", 0) != 0;
                    Log.Write("BROWSER", "pad input target = " + (_browser.ContentFocused ? "content" : "chrome"));
                    break;
                case "pad":
                    _browser.Pad(Json.Str(raw, "action"), Json.Str(raw, "phase"));
                    break;
                case "mode":
                    _browser.ToActive("{\"t\":\"mode\",\"mode\":\"" + Json.Str(raw, "mode") + "\"}");
                    break;
                case "text":
                    // The OSK's answer for a field inside the page. Sub-object, not a flat
                    // field, so a value containing a quote survives the trip intact.
                    {
                        string sub = Json.Sub(raw, "payload");
                        _browser.ToActive("{\"t\":\"text\",\"value\":" + ValueOf(sub, "value") + "}");
                    }
                    break;
                case "option":
                    _browser.ToActive("{\"t\":\"option\",\"index\":" + Json.Int(raw, "index", 0) + "}");
                    break;
                case "cancel":   _browser.ToActive("{\"t\":\"cancel\"}"); break;
                case "scan":     _browser.ToActive("{\"t\":\"scan\"}"); break;

                default:
                    Log.Write("BROWSER", "unhandled browser command '" + cmd + "': " + Trim(raw, 200));
                    break;
            }
        }

        /// <summary>
        /// Lift one already-encoded JSON string value out of an object, quotes and all, so
        /// it can be re-emitted without a decode/encode round trip that could change it.
        /// </summary>
        static string ValueOf(string obj, string key)
        {
            if (string.IsNullOrEmpty(obj)) return "\"\"";
            Match m = Regex.Match(obj, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(\"(?:[^\"\\\\]|\\\\.)*\")");
            return m.Success ? m.Groups[1].Value : "\"\"";
        }

        static string Trim(string s, int n)
        {
            if (s == null) return "";
            return s.Length <= n ? s : s.Substring(0, n) + "...";
        }

        void PostToPage(string json)
        {
            if (!_webReady) return;
            try { _web.CoreWebView2.PostWebMessageAsString(json); }
            catch (Exception ex) { Log.Write("MSG", "PostWebMessageAsString failed: " + ex.Message); }
        }

        /// <summary>
        /// Called ON THE SYSTEMAPI WORKER THREAD. PostWebMessageAsString is not thread-safe and
        /// touching the WebView2 from here would be undefined behaviour, so the envelope is
        /// marshalled back to the UI thread first. BeginInvoke, not Invoke: the worker must never
        /// wait on the UI thread, or a slow paint would stall the queue behind it.
        /// </summary>
        void OnSysReply(string envelope)
        {
            if (envelope == null) return;
            // Replies to the host's own --sys-selftest sweep have no waiter on the page. Posting
            // them anyway would make the page log a "response for unknown reqId" line for each,
            // which is exactly the diagnostic that should stay meaningful. The SYS log line
            // SysWorker already wrote is the evidence for those.
            string rid = Json.Str(envelope, "reqId");
            if (rid != null && rid.StartsWith("sweep-", StringComparison.Ordinal)) return;
            string wrapped = "{\"type\":\"sysres\",\"payload\":" + envelope + "}";
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((MethodInvoker)delegate
                {
                    try { PostToPage(wrapped); }
                    catch (Exception ex) { Log.Write("SYS", "posting the reply failed: " + ex.Message); }
                });
            }
            catch (Exception ex)
            {
                // Happens only if the form is tearing down between the two checks above.
                Log.Write("SYS", "could not marshal a reply to the UI thread (shell closing?): " + ex.Message);
            }
        }

        /// <summary>
        /// Called ON THE FILEAPI WORKER THREAD; marshalled exactly like OnSysReply, and for the
        /// same reason.
        ///
        /// The envelope is not re-serialised, only prefixed: {"ok":... becomes
        /// {"type":"fs.reply","reqId":"...","ok":... . FileApi echoes reqId inside the envelope
        /// too, so the key can appear twice; JSON.parse keeps the last, which is FileApi's own and
        /// identical. Writing ours first is what makes a reply to a request FileApi could not even
        /// parse still routable back to the promise that is waiting for it.
        /// </summary>
        void OnFileReply(string reqId, string envelope)
        {
            if (envelope == null || envelope.Length < 2 || envelope[0] != '{') return;
            string wrapped = "{\"type\":\"fs.reply\""
                + (reqId == null ? "" : ",\"reqId\":\"" + JsonEsc(reqId) + "\"")
                + "," + envelope.Substring(1);
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((MethodInvoker)delegate
                {
                    try { PostToPage(wrapped); }
                    catch (Exception ex) { Log.Write("FS", "posting the reply failed: " + ex.Message); }
                });
            }
            catch (Exception ex)
            {
                Log.Write("FS", "could not marshal a reply to the UI thread (shell closing?): " + ex.Message);
            }
        }

        /// <summary>
        /// Called ON THE LIBRARYAPI WORKER THREAD; marshalled exactly like OnFileReply, and for
        /// the same reason.
        ///
        /// The envelope is not re-serialised, only prefixed: {"ok":... becomes
        /// {"type":"lib.reply","reqId":"...","ok":... . That matters more here than anywhere
        /// else: a lib.list envelope carries every icon as base64 and re-encoding it would mean
        /// parsing and rebuilding a megabyte of JSON on the way past for no gain. LibraryApi
        /// echoes reqId inside the envelope too, so the key can appear twice; JSON.parse keeps
        /// the last, which is LibraryApi's own and identical. Writing ours first is what makes a
        /// reply to a request LibraryApi could not even parse still routable back to the promise
        /// waiting for it.
        /// </summary>
        void OnLibReply(string reqId, string envelope)
        {
            if (envelope == null || envelope.Length < 2 || envelope[0] != '{') return;
            string wrapped = "{\"type\":\"lib.reply\""
                + (reqId == null ? "" : ",\"reqId\":\"" + JsonEsc(reqId) + "\"")
                + "," + envelope.Substring(1);
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((MethodInvoker)delegate
                {
                    try { PostToPage(wrapped); }
                    catch (Exception ex) { Log.Write("LIB", "posting the reply failed: " + ex.Message); }
                });
            }
            catch (Exception ex)
            {
                Log.Write("LIB", "could not marshal a reply to the UI thread (shell closing?): " + ex.Message);
            }
        }

        #endregion

        #region power  (the page confirms; the host acts)

        /// <summary>
        /// The page is responsible for asking twice. By the time a power
        /// message arrives the human has already confirmed, so this just acts —
        /// but it acknowledges first, so a page that gets no acknowledgement
        /// can tell it is talking to an older host and fall back.
        /// </summary>
        void DoPower(string action)
        {
            if (string.IsNullOrEmpty(action)) { Log.Write("POWER", "power message with no action, ignored"); return; }
            action = action.Trim().ToLowerInvariant();
            Log.Write("POWER", "page requested '" + action + "'");
            PostToPage("{\"type\":\"powerAck\",\"action\":\"" + JsonEsc(action) + "\"}");

            switch (action)
            {
                // Shell Launcher owns these two: exit 2 -> restart device,
                // exit 3 -> shut down. Reusing them keeps one authority over
                // the session instead of two racing each other.
                case "restart":
                    ExitWith(2, "page:power:restart");
                    break;
                case "shutdown":
                    ExitWith(3, "page:power:shutdown");
                    break;

                case "sleep":
                case "rest":
                    try
                    {
                        bool ok = Native.SetSuspendState(false, false, false);
                        Log.Write("POWER", "SetSuspendState(sleep) returned " + ok
                            + (ok ? "" : "  err=" + Marshal.GetLastWin32Error()));
                        if (!ok) PostToPage("{\"type\":\"powerFail\",\"action\":\"sleep\"}");
                    }
                    catch (Exception ex)
                    {
                        Log.Write("POWER", "SetSuspendState threw (swallowed): " + ex.Message);
                        PostToPage("{\"type\":\"powerFail\",\"action\":\"sleep\"}");
                    }
                    break;

                case "signout":
                case "logoff":
                    try
                    {
                        bool ok = Native.ExitWindowsEx(Native.EWX_LOGOFF,
                            Native.SHTDN_REASON_MAJOR_OTHER | Native.SHTDN_REASON_MINOR_OTHER
                            | Native.SHTDN_REASON_FLAG_PLANNED);
                        Log.Write("POWER", "ExitWindowsEx(LOGOFF) returned " + ok
                            + (ok ? "" : "  err=" + Marshal.GetLastWin32Error()));
                        if (!ok) PostToPage("{\"type\":\"powerFail\",\"action\":\"signout\"}");
                    }
                    catch (Exception ex)
                    {
                        Log.Write("POWER", "ExitWindowsEx threw (swallowed): " + ex.Message);
                        PostToPage("{\"type\":\"powerFail\",\"action\":\"signout\"}");
                    }
                    break;

                default:
                    Log.Write("POWER", "unknown power action '" + action + "', ignored");
                    break;
            }
        }

        #endregion

        #region system information  (cheap facts only)

        static string JsonEsc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder b = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') { b.Append('\\').Append(c); }
                else if (c == '\n') b.Append("\\n");
                else if (c == '\r') b.Append("\\r");
                else if (c == '\t') b.Append("\\t");
                else if (c < ' ') b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else b.Append(c);
            }
            return b.ToString();
        }

        static void Field(StringBuilder b, string key, string val)
        {
            if (string.IsNullOrEmpty(val)) return;              // omit rather than send an empty
            if (b[b.Length - 1] != '{') b.Append(',');           // string the page would show as real
            b.Append('"').Append(JsonEsc(key)).Append("\":\"").Append(JsonEsc(val)).Append('"');
        }

        /// <summary>
        /// Only facts that are cheap and certain. Anything that would need a
        /// COM audio endpoint or a WMI round trip is left out entirely, because
        /// the page labels a missing value as a placeholder — which is honest —
        /// whereas a guessed value would not be.
        /// </summary>
        string SysInfoJson()
        {
            StringBuilder b = new StringBuilder("{\"type\":\"sysinfo\",\"data\":{");
            try
            {
                Field(b, "user", Environment.UserName);
                Field(b, "domain", Environment.UserDomainName);
                Field(b, "machine", Environment.MachineName);
                Field(b, "os", Environment.OSVersion.Version.ToString());

                try
                {
                    TimeSpan up = TimeSpan.FromMilliseconds(Environment.TickCount & 0x7FFFFFFF);
                    Field(b, "uptime", (int)up.TotalHours + " h " + up.Minutes + " min");
                }
                catch { }

                try
                {
                    System.Net.NetworkInformation.NetworkInterface best = null;
                    foreach (System.Net.NetworkInformation.NetworkInterface ni
                             in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                        if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                        if (best == null || ni.Speed > best.Speed) best = ni;
                    }
                    if (best != null)
                    {
                        Field(b, "netName", best.Name);
                        Field(b, "netType", best.NetworkInterfaceType.ToString());
                        if (best.Speed > 0)
                            Field(b, "netSpeed", (best.Speed / 1000000L).ToString(CultureInfo.InvariantCulture) + " Mbps");
                        foreach (System.Net.NetworkInformation.UnicastIPAddressInformation ip
                                 in best.GetIPProperties().UnicastAddresses)
                        {
                            if (ip.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                            Field(b, "netIp", ip.Address.ToString());
                            break;
                        }
                    }
                }
                catch (Exception ex) { Log.Write("SYSINFO", "network probe failed (skipped): " + ex.Message); }

                try
                {
                    DriveInfo sys = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory));
                    if (sys.IsReady)
                    {
                        double tb = sys.AvailableFreeSpace / 1099511627776.0;
                        Field(b, "diskFree", tb >= 1
                            ? tb.ToString("0.0", CultureInfo.InvariantCulture) + " TB free"
                            : (sys.AvailableFreeSpace / 1073741824.0).ToString("0", CultureInfo.InvariantCulture) + " GB free");
                    }
                }
                catch (Exception ex) { Log.Write("SYSINFO", "disk probe failed (skipped): " + ex.Message); }

                try
                {
                    Rectangle sb = Screen.PrimaryScreen.Bounds;
                    Field(b, "screen", sb.Width + " × " + sb.Height);
                }
                catch { }
            }
            catch (Exception ex)
            {
                Log.Write("SYSINFO", "gather failed (sending what we have): " + ex.Message);
            }
            b.Append("}}");
            Log.Write("SYSINFO", "sent " + b.Length + " bytes to the page");
            return b.ToString();
        }

        /// <summary>
        /// What the host's own raw-HID reader knows about the pad, including the battery, which
        /// no web API exposes. Everything is reported as measured or reported as absent - a pad
        /// whose battery byte was never seen says so rather than showing a plausible number.
        /// </summary>
        /// <summary>
        /// The page's channel into the pad's motors.
        ///
        ///   {"type":"haptic","effect":"move"}            play an effect
        ///   {"type":"haptic","cmd":"intensity","value":0.55}
        ///   {"type":"haptic","cmd":"stop"}               silence immediately
        ///   {"type":"haptic","cmd":"status"}             -> {"type":"hapticAck", ...}
        ///
        /// Only "status" answers. Everything else is fire-and-forget on purpose: a UI that
        /// waits for a rumble to be acknowledged has already lost the thing that makes a
        /// rumble worth having.
        /// </summary>
        void HandleHapticMessage(string raw)
        {
            if (_haptics == null) return;
            string cmd = Json.Str(raw, "cmd");

            if (!string.IsNullOrEmpty(cmd))
            {
                switch (cmd)
                {
                    case "intensity":
                        _haptics.Intensity = Json.Num(raw, "value", _haptics.Intensity);
                        Log.Write("HAPTIC", "intensity set to " + Math.Round(_haptics.Intensity * 100) + "%");
                        return;
                    case "stop":
                        try { _haptics.WriteRumble(0, 0); }
                        catch { }
                        return;
                    case "status":
                        PostToPage(HapticStatusJson());
                        return;
                    default:
                        Log.Write("HAPTIC", "unknown haptic cmd '" + cmd + "'");
                        return;
                }
            }

            string effect = Json.Str(raw, "effect");
            double inten = Json.Num(raw, "intensity", -1);
            if (inten >= 0 && Math.Abs(inten - _haptics.Intensity) > 0.001) _haptics.Intensity = inten;
            if (string.IsNullOrEmpty(effect)) return;
            if (!DualSenseHaptics.Known(effect))
            {
                Log.Write("HAPTIC", "unknown effect '" + effect + "'");
                return;
            }
            _haptics.Play(effect);
        }

        string HapticStatusJson()
        {
            StringBuilder b = new StringBuilder("{\"type\":\"hapticAck\"");
            b.Append(",\"ready\":").Append(_haptics.Ready ? "true" : "false");
            b.Append(",\"transport\":\"").Append(JsonEsc(_haptics.Transport)).Append("\"");
            b.Append(",\"outputLength\":").Append(_haptics.OutputLength.ToString(CultureInfo.InvariantCulture));
            b.Append(",\"intensity\":").Append(_haptics.Intensity.ToString("0.###", CultureInfo.InvariantCulture));
            b.Append(",\"writes\":").Append(_haptics.Writes.ToString(CultureInfo.InvariantCulture));
            b.Append(",\"failures\":").Append(_haptics.WriteFailures.ToString(CultureInfo.InvariantCulture));
            b.Append(",\"lastError\":").Append(_haptics.LastError.ToString(CultureInfo.InvariantCulture));
            b.Append(",\"status\":\"").Append(JsonEsc(_haptics.Status)).Append("\"");
            b.Append("}");
            return b.ToString();
        }

        string PadInfoJson()
        {
            StringBuilder b = new StringBuilder("{\"type\":\"padinfo\",\"data\":{");
            try
            {
                PadSnapshot s = _ds == null ? null : _ds.Snapshot;
                if (s == null) s = new PadSnapshot();
                b.Append("\"connected\":").Append(s.Connected ? "true" : "false");
                b.Append(",\"status\":\"").Append(JsonEsc(s.Status)).Append("\"");
                b.Append(",\"model\":\"").Append(JsonEsc(s.Model)).Append("\"");
                b.Append(",\"transport\":\"").Append(JsonEsc(s.Transport)).Append("\"");
                b.Append(",\"vid\":").Append(s.Vid.ToString(CultureInfo.InvariantCulture));
                b.Append(",\"pid\":").Append(s.Pid.ToString(CultureInfo.InvariantCulture));
                b.Append(",\"reports\":").Append(s.Reports.ToString(CultureInfo.InvariantCulture));
                b.Append(",\"reportId\":").Append(((int)s.ReportId).ToString(CultureInfo.InvariantCulture));
                b.Append(",\"reportLength\":").Append(s.ReportLength.ToString(CultureInfo.InvariantCulture));
                if (s.BatteryKnown)
                {
                    b.Append(",\"batteryKnown\":true");
                    b.Append(",\"batteryPercent\":").Append(s.BatteryPercent.ToString(CultureInfo.InvariantCulture));
                    b.Append(",\"batteryState\":\"").Append(JsonEsc(s.BatteryState)).Append("\"");
                    b.Append(",\"batteryRaw\":").Append(((int)s.BatteryRaw).ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    b.Append(",\"batteryKnown\":false");
                    b.Append(",\"batteryNote\":\"this pad's report layout does not carry a battery byte the host can read\"");
                }
                b.Append(",\"xinputPads\":").Append(CountBits(_xiConnectedMask).ToString(CultureInfo.InvariantCulture));

                // The write half of the same pad, so the Controllers panel can say whether
                // vibration is actually reaching the device rather than only that it is
                // switched on in Settings.
                if (_haptics != null)
                {
                    b.Append(",\"haptic\":{");
                    b.Append("\"ready\":").Append(_haptics.Ready ? "true" : "false");
                    b.Append(",\"intensity\":").Append(_haptics.Intensity.ToString("0.###", CultureInfo.InvariantCulture));
                    b.Append(",\"outputLength\":").Append(_haptics.OutputLength.ToString(CultureInfo.InvariantCulture));
                    b.Append(",\"writes\":").Append(_haptics.Writes.ToString(CultureInfo.InvariantCulture));
                    b.Append(",\"failures\":").Append(_haptics.WriteFailures.ToString(CultureInfo.InvariantCulture));
                    b.Append(",\"lastError\":").Append(_haptics.LastError.ToString(CultureInfo.InvariantCulture));
                    b.Append(",\"status\":\"").Append(JsonEsc(_haptics.Status)).Append("\"");
                    b.Append("}");
                }
            }
            catch (Exception ex)
            {
                Log.Write("PADINFO", "gather failed (sending what we have): " + ex.Message);
            }
            b.Append("}}");
            return b.ToString();
        }

        static int CountBits(uint v)
        {
            int n = 0;
            while (v != 0) { n += (int)(v & 1u); v >>= 1; }
            return n;
        }

        #endregion

        #region keys / exit

        // Only reached when the WebView does not have focus (e.g. before init).
        void OnFormKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) ExitWith(0, "Esc(form)");
            else if (e.KeyCode == Keys.D2) ExitWith(2, "2(form)");
            else if (e.KeyCode == Keys.D3) ExitWith(3, "3(form)");
            else if (e.KeyCode == Keys.Enter) Launch(_opt.ChildCommand);
        }

        void ExitWith(int code, string why)
        {
            if (_exiting) return;
            _exiting = true;
            _exitCode = code;
            Log.Write("EXIT", "exiting with code " + code + " (trigger=" + why + ")");
            Log.Write("SUMMARY", "launches=" + _launchCount + " returns=" + _returnCount
                + " foregroundPASS=" + _returnsPassed + " foregroundFAIL=" + _returnsFailed
                + " paths=[" + string.Join(" | ", _pathsUsed.ToArray()) + "]");
            Log.Write("SUMMARY", "gamepad (navigator.getGamepads inside WebView2): " + _gamepadReport);
            Log.Write("SUMMARY", "pad (raw HID): " + PadSummary());
            Log.Write("SUMMARY", "pad actions sent to the page = " + _padActionCount + "   last: " + _lastPadAction);
            _timer.Stop();
            _padTimer.Stop();
            Close();
        }

        void OnClosingForm(object sender, FormClosingEventArgs e)
        {
            _timer.Stop();
            _padTimer.Stop();
            // A mouse button this process pressed and never released would be held down by
            // the SESSION, not by this process, and would survive it. Nothing else in the
            // system can undo that.
            _ptrTimer.Stop();
            try { _ptr.ReleaseButtons(); }
            catch { }
            // Haptics first: it must get its zero-write in while the pad is still open.
            try { if (_haptics != null) _haptics.Stop(); }
            catch { }
            try { _ds.Stop(); }
            catch { }
            try { if (_sys != null) _sys.Stop(); }
            catch { }
            try { if (_files != null) _files.Stop(); }
            catch { }
            try { if (_lib != null) _lib.Stop(); }
            catch { }
            // Close every content WebView before the form goes: they are child HWNDs of a
            // panel that is about to be destroyed, and an orphaned controller leaves a
            // browser process behind that nothing will ever reap.
            try { if (_browser != null) _browser.Quit(); }
            catch (Exception ex) { Log.Write("BROWSER", "teardown threw (swallowed): " + ex.Message); }
            if (_tracker != null) _tracker.Cleanup();
            try { _web.Dispose(); }
            catch { }
            Log.Write("HOST", "ShellHostWeb exiting with code " + _exitCode);
        }

        #endregion

        #region pad input -> page

        // The whole pad path is wrapped: this process IS the shell, so an escaped exception
        // here would blank the console. Anything unexpected is logged and swallowed.
        void OnPadTick(object sender, EventArgs e)
        {
            try { PumpPad(); }
            catch (Exception ex) { Log.Write("PAD", "pad tick caught (swallowed): " + ex.ToString()); }
        }

        void PumpPad()
        {
            PadSnapshot s = _ds.Snapshot;
            TracePadState(s);

            // Always drain, even when the input is going to be discarded, so nothing piles up.
            int hidEdges = _ds.TakePressEdges();
            int xiHeldDir, xiHeldDirV;
            ushort xiEdges = PollXInput(out xiHeldDir, out xiHeldDirV);

            if (!_webReady) return;

            // ── The guide button, and only the guide button ──────────────────────────────
            // Handled before anything else and outside the foreground gate, because it is the
            // one press whose entire job is to work when the shell is NOT in front. See
            // PumpGuide().
            PumpGuide(s, hidEdges, xiEdges);
            hidEdges &= ~DualSense.BTN_PS;
            xiEdges = (ushort)(xiEdges & ~XI_GUIDE);

            // Everything else keeps flowing through the state machines below so that held
            // buttons and held directions stay accounted for across a gate close; the gate
            // itself lives in SendPad, which is the single point where an input becomes an
            // action on the shell.

            // The browser's analog channel. Straight from here to the content WebView, not
            // through the shell page: a cursor being pushed around by a thumbstick is 30
            // messages a second and there is nothing for index.html to decide about any of
            // them. BrowserHost.Axes() no-ops unless the content pane actually owns input.
            // …unless the pointer owns the sticks. The two must never both act on one push:
            // the browser's cursor would drift behind a window nobody can see it through.
            if (_browser != null && _browser.Open && s.Connected && !PadInputGated() && !_ptrOn)
            {
                _browser.Axes(
                    ((int)s.LX - 128) / 127.0, ((int)s.LY - 128) / 127.0,
                    ((int)s.RX - 128) / 127.0, ((int)s.RY - 128) / 127.0);
            }

            PumpDirection(s, xiHeldDir, xiHeldDirV, hidEdges, xiEdges);

            if (hidEdges != 0)
            {
                Log.Write("PAD", "press edge 0x" + hidEdges.ToString("X5") + " [" + DualSense.ButtonNames(hidEdges)
                    + "]  LX=" + s.LX + " LY=" + s.LY + " RX=" + s.RX + " RY=" + s.RY);
                DispatchHid(hidEdges);
            }
            if (xiEdges != 0)
            {
                Log.Write("XINPUT", "press edge 0x" + xiEdges.ToString("X4"));
                DispatchXInput(xiEdges);
            }

            PumpReleases(s);
        }

        /// <summary>
        /// One direction machine per axis, for both the d-pad and the left stick on either
        /// device: a flick moves exactly one step, a hold auto-repeats after an initial
        /// delay, and letting go emits a release so a consumer doing its own hold-to-repeat
        /// (the on-screen keyboard) knows when to stop.
        /// </summary>
        void PumpDirection(PadSnapshot s, int xiHeldDir, int xiHeldDirV, int hidEdges, ushort xiEdges)
        {
            int heldX = 0, heldY = 0;
            if (s.Connected)
            {
                if ((s.Buttons & DualSense.BTN_DLEFT) != 0) heldX = -1;
                else if ((s.Buttons & DualSense.BTN_DRIGHT) != 0) heldX = 1;
                else
                {
                    int dx = (int)s.LX - 128;
                    if (dx <= -StickDeadzoneByte) heldX = -1;
                    else if (dx >= StickDeadzoneByte) heldX = 1;
                }

                // A DualSense Y axis grows downwards, so "less than centre" is up.
                if ((s.Buttons & DualSense.BTN_DUP) != 0) heldY = -1;
                else if ((s.Buttons & DualSense.BTN_DDOWN) != 0) heldY = 1;
                else
                {
                    int dy = (int)s.LY - 128;
                    if (dy <= -StickDeadzoneByte) heldY = -1;
                    else if (dy >= StickDeadzoneByte) heldY = 1;
                }
            }
            if (heldX == 0) heldX = xiHeldDir;
            if (heldY == 0) heldY = xiHeldDirV;

            // ── One direction at a time ──────────────────────────────────────────────
            // X and Y are two independent repeat machines, so a diagonal used to emit
            // BOTH axes in the same 16 ms tick, each with its own repeat timer. In the
            // log that reads as two directions sharing a timestamp:
            //
            //     [PAD] -> page: left   [held]
            //     [PAD] -> page: up     [held]
            //
            // and on the screen it reads as a cursor that will not go where it is
            // pushed. Menus are not diagonal: a list, a grid of tiles and a keyboard
            // all want exactly one step per push, and a thumb on an analog stick is
            // never on a perfect axis.
            //
            // So when both axes are live at once, the one that is pushed FURTHER
            // wins and the other is treated as centred. Magnitude is only knowable
            // here, which is why this cannot be fixed in the shell page: by the time
            // the page sees "left" and "up" the deflection is gone.
            //
            // The d-pad is deliberately included. Its diagonals are two switches
            // closed at once, and pressing up-left on a hardware d-pad is far more
            // often a badly-hit "up" than a deliberate diagonal. A tie (the d-pad
            // case, where both are exactly 1) keeps the vertical, because rails and
            // lists are vertical and that is the more likely intent.
            if (heldX != 0 && heldY != 0)
            {
                int magX = s.Connected ? Math.Abs((int)s.LX - 128) : 0;
                int magY = s.Connected ? Math.Abs((int)s.LY - 128) : 0;
                bool dpadX = s.Connected &&
                    ((s.Buttons & DualSense.BTN_DLEFT) != 0 || (s.Buttons & DualSense.BTN_DRIGHT) != 0);
                bool dpadY = s.Connected &&
                    ((s.Buttons & DualSense.BTN_DUP) != 0 || (s.Buttons & DualSense.BTN_DDOWN) != 0);
                if (dpadX || dpadY) { magX = dpadX ? 255 : magX; magY = dpadY ? 255 : magY; }
                if (magX > magY) heldY = 0; else heldX = 0;
            }

            DateTime now = DateTime.Now;
            bool movedX = PumpAxis(heldX, now, ref _padDir, ref _padRepeatAt, "left", "right");
            bool movedY = PumpAxis(heldY, now, ref _padDirV, ref _padRepeatAtV, "up", "down");

            // A d-pad tap shorter than one tick is invisible to the sampler above, but the
            // reader thread latched its edge - honour that so no tap is ever dropped.
            if (!movedX && heldX == 0)
            {
                if ((hidEdges & DualSense.BTN_DLEFT) != 0) SendPad("left", "d-pad tap", "dpadLeft", "press");
                else if ((hidEdges & DualSense.BTN_DRIGHT) != 0) SendPad("right", "d-pad tap", "dpadRight", "press");
                else if ((xiEdges & XI_DLEFT) != 0) SendPad("left", "xinput d-pad tap", "dpadLeft", "press");
                else if ((xiEdges & XI_DRIGHT) != 0) SendPad("right", "xinput d-pad tap", "dpadRight", "press");
            }
            if (!movedY && heldY == 0)
            {
                if ((hidEdges & DualSense.BTN_DUP) != 0) SendPad("up", "d-pad tap", "dpadUp", "press");
                else if ((hidEdges & DualSense.BTN_DDOWN) != 0) SendPad("down", "d-pad tap", "dpadDown", "press");
                else if ((xiEdges & XI_DUP) != 0) SendPad("up", "xinput d-pad tap", "dpadUp", "press");
                else if ((xiEdges & XI_DDOWN) != 0) SendPad("down", "xinput d-pad tap", "dpadDown", "press");
            }
        }

        bool PumpAxis(int held, DateTime now, ref int dir, ref DateTime repeatAt, string neg, string pos)
        {
            if (held != dir)
            {
                int was = dir;
                dir = held;
                if (held == 0)
                {
                    repeatAt = DateTime.MaxValue;
                    if (was != 0) SendPad(was < 0 ? neg : pos, "released", was < 0 ? neg : pos, "release");
                    return false;
                }
                SendPad(held < 0 ? neg : pos, "held", held < 0 ? neg : pos, "press");
                repeatAt = now.AddMilliseconds(RepeatFirstMs);
                return true;
            }
            if (held != 0 && now >= repeatAt)
            {
                SendPad(held < 0 ? neg : pos, "auto-repeat", held < 0 ? neg : pos, "repeat");
                repeatAt = now.AddMilliseconds(RepeatNextMs);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Face and shoulder buttons. The semantic names that already existed
        /// (launch / back / tabPlay / tabMedia / cc) are unchanged — pages in the
        /// wild bind to them — and every message now also carries the literal
        /// button name, so a consumer that wants "the Options button" rather than
        /// "the control-centre action" has it. The on-screen keyboard is exactly
        /// such a consumer: while it is open, L1/R1 move the caret and Options
        /// commits, which are not the shell meanings of those buttons.
        /// </summary>
        void DispatchHid(int e)
        {
            if ((e & DualSense.BTN_CROSS) != 0) SendPad("launch", "Cross", "cross", "press");
            if ((e & DualSense.BTN_CIRCLE) != 0) SendPad("back", "Circle", "circle", "press");
            if ((e & DualSense.BTN_SQUARE) != 0) SendPad("square", "Square", "square", "press");
            if ((e & DualSense.BTN_TRIANGLE) != 0) SendPad("triangle", "Triangle", "triangle", "press");
            if ((e & DualSense.BTN_L1) != 0) SendPad("tabPlay", "L1", "l1", "press");
            if ((e & DualSense.BTN_R1) != 0) SendPad("tabMedia", "R1", "r1", "press");
            if ((e & DualSense.BTN_OPTIONS) != 0) SendPad("cc", "Options", "options", "press");
            if ((e & DualSense.BTN_TOUCHPAD) != 0) SendPad("cc", "Touchpad click", "touchpad", "press");
            if ((e & DualSense.BTN_CREATE) != 0) SendPad("create", "Create", "create", "press");
            // PS is deliberately absent: PumpPad() takes it before the gate. See GuidePress().
            // The stick clicks had no shell meaning until the browser arrived. L3 is the
            // documented toggle between spatial navigation and the virtual cursor - the one
            // control the human must be able to find when a site defeats the focus ring -
            // and R3 recentres on whatever is focused. They are semantic-name-free on
            // purpose: nothing outside the browser binds them, so the literal button is the
            // action.
            if ((e & DualSense.BTN_L3) != 0) SendPad("l3", "L3 (stick click)", "l3", "press");
            if ((e & DualSense.BTN_R3) != 0) SendPad("r3", "R3 (stick click)", "r3", "press");

            // Still true, and worth keeping written down: no pad button exits this
            // host. Square and Triangle used to be exit codes 2 and 3 in the bare
            // native harness; in the real UI that would reboot the machine
            // mid-browse. Power lives behind a confirmation in the control centre.
        }

        void DispatchXInput(ushort e)
        {
            if ((e & XI_A) != 0) SendPad("launch", "xinput A", "cross", "press");
            if ((e & XI_B) != 0) SendPad("back", "xinput B", "circle", "press");
            if ((e & XI_X) != 0) SendPad("square", "xinput X", "square", "press");
            if ((e & XI_Y) != 0) SendPad("triangle", "xinput Y", "triangle", "press");
            if ((e & XI_LB) != 0) SendPad("tabPlay", "xinput LB", "l1", "press");
            if ((e & XI_RB) != 0) SendPad("tabMedia", "xinput RB", "r1", "press");
            if ((e & (XI_START | XI_BACK)) != 0) SendPad("cc", "xinput Start/Back", "options", "press");
            // XI_GUIDE is taken by PumpPad() before the gate, like BTN_PS.
        }

        // Buttons whose release matters to the page (hold-to-repeat in the on-screen
        // keyboard). Directions are handled by the axis machine, which emits its own
        // release; these are the face and shoulder buttons.
        static readonly int[] ReleaseWatch = new int[] {
            DualSense.BTN_CROSS, DualSense.BTN_CIRCLE, DualSense.BTN_SQUARE, DualSense.BTN_TRIANGLE,
            DualSense.BTN_L1, DualSense.BTN_R1, DualSense.BTN_OPTIONS, DualSense.BTN_TOUCHPAD,
            DualSense.BTN_CREATE, DualSense.BTN_PS, DualSense.BTN_L3, DualSense.BTN_R3
        };
        static readonly string[] ReleaseAction = new string[] {
            "launch", "back", "square", "triangle", "tabPlay", "tabMedia", "cc", "cc", "create", "guide",
            "l3", "r3"
        };
        static readonly string[] ReleaseButton = new string[] {
            "cross", "circle", "square", "triangle", "l1", "r1", "options", "touchpad", "create", "ps",
            "l3", "r3"
        };

        /// <summary>
        /// Emit a release for any watched button we announced a press for and that
        /// the snapshot no longer shows held. A tap too short to appear in any
        /// snapshot releases on the very next tick, which is what we want: a
        /// consumer must never be left repeating a key nobody is holding.
        /// </summary>
        void PumpReleases(PadSnapshot s)
        {
            int held = s.Connected ? s.Buttons : 0;
            for (int i = 0; i < ReleaseWatch.Length; i++)
            {
                int bit = ReleaseWatch[i];
                bool down = (held & bit) != 0;
                bool announced = (_hidPressed & bit) != 0;
                if (down && !announced) _hidPressed |= bit;
                else if (!down && announced)
                {
                    _hidPressed &= ~bit;
                    SendPad(ReleaseAction[i], "released", ReleaseButton[i], "release");
                }
            }
        }

        // ── The foreground gate ──────────────────────────────────────────────────────────
        //
        // The raw HID reader is deliberately NOT focus-gated: that is the only reason this
        // shell can see a DualSense at all, and it is why the guide button can work from
        // inside a game. But *reading* the pad and *acting* on it are two different things,
        // and conflating them is what produced the incident this gate exists for: Steam was
        // launched, the launch was never adopted, the shell stayed in the foreground state
        // machine, and every press the human made while looking at Steam moved a cursor on a
        // shell they could not see. Four Cross presses later there were four Steam clients.
        //
        // The rule: the shell drives its own UI from the pad only when the shell's own window
        // is in the foreground. One exception, the guide button, handled in PumpPad() before
        // this is ever consulted.
        //
        // The check is on the whole process, not just this HWND, because the browser's content
        // WebViews and any owned dialog are separate windows of the same process; foreground
        // belonging to one of them is still the shell being in front.

        bool ShellIsForeground()
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;       // locked, secure desktop, nothing active
            if (fg == Handle) return true;
            uint fpid;
            Native.GetWindowThreadProcessId(fg, out fpid);
            return _ownPid != 0 && fpid == (uint)_ownPid;
        }

        bool PadInputGated()
        {
            if (_opt.NoFgGate) return false;
            DateTime now = DateTime.Now;
            if ((now - _gateCheckedAt).TotalMilliseconds >= 50)
            {
                _gateCheckedAt = now;
                // One test, and deliberately only one. Anything else - "is a child running",
                // "did we mean to hide" - is state that can get out of step with what is
                // actually on the screen, and a gate that is wrong in the closed direction is a
                // shell that ignores the human. Who owns the foreground is never wrong.
                _gateClosedCache = !ShellIsForeground();
            }
            return _gateClosedCache;
        }

        #region pointer mode  (the pad as a mouse, over a window this shell did not draw)

        // ── Why this exists ──────────────────────────────────────────────────────────────
        //
        // The foreground gate above is correct and it leaves a hole. When the foreground is
        // NOT the shell, every pad press is dropped - which is right for a game, because the
        // game is reading the pad itself, and wrong for everything that is not a game. An
        // elevated installer opened by the install broker, a launcher's sign-in window, a
        // client that is not in Big Picture: those are mouse UIs on a console with no mouse,
        // and until now the pad simply could not touch them.
        //
        // It has to be done at the host, not in the page: nothing this process DRAWS can
        // appear over another process's window, so a pointer painted by the shell would be
        // invisible exactly where it is needed. Windows draws the cursor; this drives it.
        //
        // ── When it turns itself on ──────────────────────────────────────────────────────
        // In order, first match wins:
        //   1. the shell's own window is in front            -> OFF (the page navigates itself)
        //   2. the human pressed L3 out here                  -> whatever they chose, until (1)
        //   3. --ptr=on                                       -> ON (test instances)
        //   4. the front window belongs to the tree we launched, and that entry is a GAME
        //                                                     -> OFF (it reads the pad itself)
        //   5. the front process is a known launcher          -> ON
        //   6. the entry we launched calls itself launcher/app-> ON
        //   7. the front window covers the whole screen and is nothing we recognise
        //                                                     -> OFF (assume a game)
        //   8. the front process refuses to open              -> ON (elevated / SYSTEM: the
        //      install broker's interactive installers landed here until 2026-08-16, and
        //      anything running as a different user still does - which by definition cannot
        //      be reading this pad)
        //   8b. it opens, but runs on an ELEVATED token       -> ON (the install broker's
        //      interactive installers land here NOW: started under the console user's own
        //      admin token at the desktop's integrity, so that SendInput actually reaches
        //      them - a SYSTEM or High window swallows it, see PointerRefused)
        //   9. anything else                                  -> OFF
        //
        // Rule 8 is a permission test used as an identity test, and that is deliberate: a
        // standard user cannot OpenProcess a SYSTEM process for anything, so the failure is
        // stable, needs no privilege of its own, and is exactly the class of window the pad
        // could not reach. Rule 8b is the same idea one notch down: a window of ours that
        // carries an elevated token was put there by something privileged on our behalf.
        // Rule 4 and rule 7 stand in front of both so that an anti-cheat that seals its own
        // game process (Vanguard does) cannot be mistaken for an installer.

        void PointerTimerTick(object sender, EventArgs e)
        {
            try { PointerPump(); }
            catch (Exception ex) { Log.Write("PTR", "pointer tick caught (swallowed): " + ex.Message); }
        }

        const double PtrSpeed = 15.0;      // px per 16 ms tick at full deflection == ui/mosnav.js
        const double PtrScroll = 22.0;     // px per tick of wheel intent, likewise
        const int PtrStepPx = 8;           // one d-pad press

        void PointerPump()
        {
            DateTime now = DateTime.Now;
            if ((now - _ptrEvalAt).TotalMilliseconds >= 250)
            {
                _ptrEvalAt = now;
                PointerEvaluate();
            }
            if (!_ptrOn) return;

            double lx, ly, rx, ry;
            PointerSticks(out lx, out ly, out rx, out ry);

            double vx = StickCurve(lx), vy = StickCurve(ly);
            double sy = StickCurve(ry), sx = StickCurve(rx) * 0.6;

            if (vx != 0 || vy != 0)
            {
                if (!_ptrMoving) { _ptrMoving = true; _ptrMoveFrom = PointerMode.Cursor(); }
                PointerRefused(_ptr.MoveBy(vx * PtrSpeed, vy * PtrSpeed));
            }
            else if (_ptrMoving)
            {
                _ptrMoving = false;
                POINT to = PointerMode.Cursor();
                Log.Write("PTR", "stick move (" + _ptrMoveFrom.X + "," + _ptrMoveFrom.Y + ") -> ("
                    + to.X + "," + to.Y + ")  d=(" + (to.X - _ptrMoveFrom.X) + "," + (to.Y - _ptrMoveFrom.Y) + ")");
                _ptr.ResetFraction();
            }

            if (sx != 0 || sy != 0) _ptr.WheelPixels(-sy * PtrScroll, sx * PtrScroll);

            PointerTouchPump();
        }

        // ── The touchpad, as a trackpad ──────────────────────────────────────────────────
        //
        // The DualSense has a 52 x 23 mm glass surface reporting 1920 x 1080 units, and until
        // now this shell used only the BUTTON under it. As a pointer source it is the thing
        // on the pad that is already a mouse: relative motion, no dead zone, no acceleration
        // curve to fight, and a human already knows how to use one.
        //
        // Relative, never absolute. Landing the cursor wherever the thumb happens to touch
        // down would move it a screenful on every contact; a trackpad that does that is
        // unusable, which is why first contact only seeds the reference point.
        //
        // The gains: 0.55 px per unit across, and 0.55 * (36.9/47) down, because the surface
        // reports 36.9 units per mm across and 47 per mm down. Without that correction a
        // circle drawn with the thumb comes out as an ellipse.
        //
        // 0.55 was measured, not chosen: the first bench run used 1.6 and a single drag
        // across the glass threw the cursor into the far edge of a 3440 px screen and stayed
        // there. At 0.55, with the acceleration below, a full sweep of the pad is a little
        // more than half the screen at thumb speed and about two thirds of it at a flick,
        // which is the ratio a laptop trackpad has.
        const double TouchGain = 0.55;
        const double TouchGainY = TouchGain * 36.9 / 47.0;
        const double TouchScroll = 0.9;      // units -> wheel pixels, two fingers
        const int TapMs = 150;               // a contact shorter than this…
        const int TapTravel = 10;            // …that moved less than this, is a tap

        void PointerTouchPump()
        {
            bool down, two;
            int tx, ty;
            PointerTouchSample(out down, out tx, out ty, out two);

            if (down && !_touchDown)
            {
                _touchDown = true;
                _touchTwo = two;
                _touchLastX = tx; _touchLastY = ty;
                _touchStart = DateTime.Now;
                _touchTravel = 0;
                Log.Write("PTR", "touch down at " + tx + "," + ty + (two ? " (two fingers)" : ""));
                return;                        // no motion on the contact itself
            }

            if (down && _touchDown)
            {
                int dx = tx - _touchLastX, dy = ty - _touchLastY;
                _touchLastX = tx; _touchLastY = ty;
                if (dx == 0 && dy == 0) return;
                _touchTravel += Math.Abs(dx) + Math.Abs(dy);
                if (two && !_touchTwo) { _touchTwo = true; Log.Write("PTR", "touch: second finger down, scrolling"); }

                if (_touchTwo)
                {
                    // Two fingers scroll, in the direction the fingers move (content follows
                    // the fingers), which is what every trackpad on this machine does.
                    _ptr.WheelPixels(-dy * TouchScroll, dx * TouchScroll);
                    return;
                }

                // Light acceleration: a slow drag is 1:1-ish for placing the cursor on a
                // small control, a fast flick crosses a 3440 px screen without lifting.
                double speed = Math.Sqrt((double)dx * dx + (double)dy * dy);
                double accel = 1.0 + Math.Min(1.2, speed / 30.0);
                PointerRefused(_ptr.MoveBy(dx * TouchGain * accel, dy * TouchGainY * accel));
                return;
            }

            if (!down && _touchDown)
            {
                _touchDown = false;
                double ms = (DateTime.Now - _touchStart).TotalMilliseconds;
                bool tap = !_touchTwo && ms < TapMs && _touchTravel < TapTravel;
                _touchTwo = false;
                _ptr.ResetFraction();
                if (tap)
                {
                    POINT p = PointerMode.Cursor();
                    _ptr.Button(false, true);
                    _ptr.Button(false, false);
                    Log.Write("PTR", "touch tap -> left click at (" + p.X + "," + p.Y + ")"
                        + "  [" + (int)ms + " ms, " + (int)_touchTravel + " units]");
                }
                else
                {
                    Log.Write("PTR", "touch up after " + (int)ms + " ms, " + (int)_touchTravel + " units");
                }
            }
        }

        /// <summary>
        /// One finger, from whichever source is real: the synthetic drag a --walk token is
        /// playing, or the pad's own touch surface.
        /// </summary>
        void PointerTouchSample(out bool down, out int x, out int y, out bool two)
        {
            down = false; x = 0; y = 0; two = false;
            if (_synthTouch)
            {
                double ms = (DateTime.Now - _stStart).TotalMilliseconds;
                if (ms > _stMs) { _synthTouch = false; return; }
                double t = _stMs <= 0 ? 1.0 : ms / _stMs;
                down = true;
                two = _synthTouchTwo;
                x = (int)(_stx0 + (_stx1 - _stx0) * t);
                y = (int)(_sty0 + (_sty1 - _sty0) * t);
                return;
            }
            if (_opt.NoPad || _ds == null) return;
            PadSnapshot s = _ds.Snapshot;
            if (s == null || !s.Connected || !s.TouchKnown) return;
            down = s.T1Down; x = s.T1X; y = s.T1Y; two = s.T1Down && s.T2Down;
        }

        /// <summary>
        /// Windows says no.
        ///
        /// SendInput is subject to UIPI: a process may inject input only into applications at
        /// its own integrity level or lower, and when it refuses it does so SILENTLY - the
        /// call succeeds and the input is dropped. Measured on the bench 2026-08-16 against
        /// the install broker's own installer window, which runs as SYSTEM: pointer mode
        /// engaged correctly, and every move and click went nowhere.
        ///
        /// A pointer that is drawn as "on" and cannot move is worse than no pointer, so
        /// after three refused moves against the same window it turns itself off and says
        /// why. It will engage again for any other window.
        /// </summary>
        void PointerRefused(int moveResult)
        {
            if (moveResult == 0) return;
            if (moveResult > 0) { _ptrRefusals = 0; return; }
            _ptrRefusals++;
            if (_ptrRefusals < 3) return;
            IntPtr fg = Native.GetForegroundWindow();
            _ptrBlockedFor = fg;
            _ptrRefusals = 0;
            Log.Write("PTR", "Windows REFUSED the injected input three times for "
                + Foreground.Describe(fg) + ". SendInput is subject to UIPI: this shell runs at"
                + " medium integrity and cannot drive a window owned by an elevated or SYSTEM"
                + " process. The pointer cannot operate this window - turning it off.");
            PointerSet(false, "Windows refuses synthetic input to this window (it is elevated"
                + " or SYSTEM, and this shell is not)", fg);
        }

        /// <summary>
        /// The stick, from whichever source is real: the synthetic vector a --walk token set,
        /// or the pad the HID reader is holding. Same normalisation the browser's analog
        /// channel uses (-1..1 from the raw byte), so the feel is the same in both.
        /// </summary>
        void PointerSticks(out double lx, out double ly, out double rx, out double ry)
        {
            lx = ly = rx = ry = 0;
            if (_synthUntil != DateTime.MinValue)
            {
                if (DateTime.Now < _synthUntil)
                {
                    lx = _synthLX; ly = _synthLY; rx = _synthRX; ry = _synthRY;
                    return;
                }
                _synthUntil = DateTime.MinValue;
                _synthLX = _synthLY = _synthRX = _synthRY = 0;
                Log.Write("PTR", "synthetic stick expired, back to centre");
            }
            if (_opt.NoPad || _ds == null) return;
            PadSnapshot s = _ds.Snapshot;
            if (s == null || !s.Connected) return;
            lx = ((int)s.LX - 128) / 127.0; ly = ((int)s.LY - 128) / 127.0;
            rx = ((int)s.RX - 128) / 127.0; ry = ((int)s.RY - 128) / 127.0;
        }

        /// <summary>
        /// ui/mosnav.js's curve(), to the digit: dead zone 0.16, then the magnitude squared.
        /// Squaring is what makes a thumbstick usable as a pointer at all - it keeps a small
        /// deflection genuinely slow - and the browser's pointer and this one must not feel
        /// like two different devices.
        /// </summary>
        static double StickCurve(double v)
        {
            const double dead = 0.16;
            double m = Math.Abs(v);
            if (m < dead) return 0;
            double t = (m - dead) / (1 - dead);
            return (v < 0 ? -1 : 1) * t * t;
        }

        void PointerEvaluate()
        {
            if (_ptrTyping) return;              // suspended by hand; PointerFinishTyping resumes
            if (_opt.PtrDisabled) { PointerSet(false, "--ptr=off", IntPtr.Zero); return; }

            IntPtr fg = Native.GetForegroundWindow();

            if (ShellIsForeground())
            {
                if (_ptrManual != 0)
                {
                    Log.Write("PTR", "manual choice cleared - the shell is in the foreground again");
                    _ptrManual = 0;
                }
                PointerSet(false, "the shell is in the foreground", fg);
                return;
            }
            if (_ptrBlockedFor != IntPtr.Zero)
            {
                // Already found out the hard way that this window will not take injected
                // input. Stay off for it, and clear the moment anything else comes forward.
                if (fg == _ptrBlockedFor) { PointerSet(false, "Windows refuses synthetic input to this window", fg); return; }
                _ptrBlockedFor = IntPtr.Zero;
            }
            if (_ptrManual > 0) { PointerSet(true, "L3 (turned on by hand)", fg); return; }
            if (_ptrManual < 0) { PointerSet(false, "L3 (turned off by hand)", fg); return; }
            if (_opt.PtrForce) { PointerSet(true, "--ptr=on", fg); return; }

            string why = PointerRule(fg);
            PointerSet(why != null, why == null ? "nothing in front asks for a pointer" : why, fg);
        }

        /// <summary>Null = leave it off. Non-null = the reason to turn it on.</summary>
        string PointerRule(IntPtr fg)
        {
            if (fg == IntPtr.Zero) return null;
            int pid = Foreground.PidOf(fg);
            if (pid == 0 || pid == _ownPid) return null;

            string proc = "";
            try { proc = Process.GetProcessById(pid).ProcessName.ToLowerInvariant(); }
            catch { }

            string category = null, friendly = null;
            for (int i = 0; i < BackgroundTable.GetLength(0); i++)
            {
                if (!string.Equals(proc, BackgroundTable[i, 0], StringComparison.Ordinal)) continue;
                friendly = BackgroundTable[i, 1];
                category = BackgroundTable[i, 2];
                break;
            }

            // 4. something we launched, and the library calls it a game.
            bool ours = false;
            if (_tracker != null)
            {
                try { ours = _tracker.GetTrackedPids().Contains(pid); }
                catch { }
            }
            if (_running != null && _running.Pid == pid) ours = true;
            if (ours)
            {
                string ek = _running == null || _running.EntryKind == null ? "" : _running.EntryKind;
                if (category == "game" || ek == "game" || (ek == "" && category == null))
                    return null;
            }

            // 5 / 6. a launcher, either by name or by what the tile said it was.
            if (category == "launcher") return "a launcher window is in front (" + friendly + ")";
            if (ours && _running != null
                && (_running.EntryKind == "launcher" || _running.EntryKind == "app"))
                return "the entry we launched is a " + _running.EntryKind + " ('" + _running.Title + "')";

            // 7. full screen and unrecognised: assume a game rather than poke a mouse at it.
            RECT r;
            if (Native.GetWindowRect(fg, out r))
            {
                RECT v = PointerMode.VirtualScreen();
                if (r.Right - r.Left >= (v.Right - v.Left) - 2 && r.Bottom - r.Top >= (v.Bottom - v.Top) - 2)
                    return null;
            }

            // 8. a process this shell cannot open at all: elevated, or another user's.
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero)
                return "the front window belongs to a process this shell cannot open"
                     + " (elevated or SYSTEM: '" + proc + "')";
            Native.CloseHandle(h);

            // 8b. opens fine, but runs on an ELEVATED token: the install broker's interactive
            //     installer, started under this user's admin token at this desktop's own
            //     integrity so that the pad can reach it (see PointerMode.TokenElevated). A
            //     program the user elevated through UAC lands here too; if it is really at
            //     High, the three-refusals rule turns the pointer off again for it.
            if (PointerMode.TokenElevated(pid) == 1)
                return "the front window runs on an elevated token as this user"
                     + " (an interactive installer from the install broker, or something"
                     + " elevated by hand: '" + proc + "')";

            return null;
        }

        void PointerSet(bool on, string reason, IntPtr fg)
        {
            if (on == _ptrOn && reason == _ptrReason) return;
            _ptrReason = reason;
            if (on == _ptrOn) return;
            _ptrOn = on;
            if (!on)
            {
                _ptr.ReleaseButtons();
                _ptr.ResetFraction();
                _ptrMoving = false;
            }
            else
            {
                _ptr.ResetFraction();
            }
            POINT c = PointerMode.Cursor();
            Log.Write("PTR", (on ? "on" : "off") + " reason=" + reason
                + " fg=" + Foreground.PidOf(fg) + " '" + Foreground.Title(fg) + "'"
                + " cursor=(" + c.X + "," + c.Y + ")");
        }

        // ── The bindings ─────────────────────────────────────────────────────────────────
        //
        // Deliberately the browser's pointer mode (ui/browser.js renderHints), moved onto a
        // real cursor: left stick moves, d-pad steps, Cross clicks, right stick scrolls,
        // L1/R1 page, Circle goes back, Options accepts, Triangle types, L3 leaves. A human
        // who learned it inside the browser already knows it out here.
        //
        // Called from SendPad BEFORE the foreground gate, because "the shell is not in front"
        // is the whole precondition for this mode. Returns true when the press was consumed
        // and must not also reach the page.
        bool PointerTake(string action, string button, string phase)
        {
            if (!_ptrOn) return false;
            string b = (string.IsNullOrEmpty(button) ? action : button).ToLowerInvariant();
            if (b.StartsWith("dpad")) b = b.Substring(4);
            // A real press always carries the physical button. The --walk vocabulary carries
            // the SHELL ACTION instead ("launch", not "cross"), because that is what the page
            // is asked to do, so both spellings have to arrive at the same binding or every
            // token in a walkthrough would land on the default and silently do nothing.
            switch (b)
            {
                case "launch":   b = "cross"; break;
                case "back":     b = "circle"; break;
                case "tabplay":  b = "l1"; break;
                case "tabmedia": b = "r1"; break;
                case "cc":       b = "options"; break;
            }
            bool press = phase != "release";

            switch (b)
            {
                case "up":    if (press) PointerStep(0, -1); return true;
                case "down":  if (press) PointerStep(0, 1); return true;
                case "left":  if (press) PointerStep(-1, 0); return true;
                case "right": if (press) PointerStep(1, 0); return true;

                // Down on press and up on release, so a HOLD is a drag rather than a click
                // that guessed at its own duration. Everything a mouse can do on a window
                // this shell cannot see depends on that distinction.
                case "cross":
                    _ptr.Button(false, press);
                    if (press) { POINT p = PointerMode.Cursor(); Log.Write("PTR", "left down at (" + p.X + "," + p.Y + ")"); }
                    else Log.Write("PTR", "left up");
                    return true;
                case "square":
                    _ptr.Button(true, press);
                    if (press) { POINT p2 = PointerMode.Cursor(); Log.Write("PTR", "right down at (" + p2.X + "," + p2.Y + ")"); }
                    else Log.Write("PTR", "right up");
                    return true;

                // The touchpad BUTTON is the click that belongs with the touchpad SURFACE:
                // press to hold, release to let go, so a drag works exactly as it does on a
                // laptop. Options keeps Enter for itself - the two used to share it, and once
                // the surface drives the cursor, clicking the thing under your thumb is the
                // only thing that button can sensibly mean.
                case "touchpad":
                    _ptr.Button(false, press);
                    if (press) { POINT tp = PointerMode.Cursor(); Log.Write("PTR", "touchpad click: left down at (" + tp.X + "," + tp.Y + ")"); }
                    else Log.Write("PTR", "touchpad click: left up");
                    return true;

                case "l1": if (press) { _ptr.KeyTap(Native.VK_PRIOR); Log.Write("PTR", "PageUp"); } return true;
                case "r1": if (press) { _ptr.KeyTap(Native.VK_NEXT); Log.Write("PTR", "PageDown"); } return true;
                case "options": if (press) { _ptr.KeyTap(Native.VK_RETURN); Log.Write("PTR", "Enter"); } return true;
                case "circle": if (press) { _ptr.KeyTap(Native.VK_ESCAPE); Log.Write("PTR", "Escape"); } return true;

                case "triangle": if (press) PointerBeginTyping(); return true;

                case "l3":
                    if (press)
                    {
                        _ptrManual = -1;
                        Log.Write("PTR", "L3: the human turned the pointer off");
                        PointerEvaluate();
                    }
                    return true;

                default:
                    // Everything else is swallowed rather than passed on. With the shell behind
                    // a foreign window the gate would drop it anyway; consuming it here keeps
                    // one rule ("while the pointer is up, the pad is a mouse") instead of two.
                    return true;
            }
        }

        void PointerStep(int dx, int dy)
        {
            POINT p = PointerMode.Cursor();
            bool ok = _ptr.MoveTo(p.X + dx * PtrStepPx, p.Y + dy * PtrStepPx);
            POINT q = PointerMode.Cursor();
            Log.Write("PTR", "d-pad step (" + p.X + "," + p.Y + ") -> (" + q.X + "," + q.Y + ")");
            PointerRefused(ok ? 1 : -1);
        }

        /// <summary>
        /// L3 when the pointer is NOT up and the shell is not in front. The only way back in
        /// after turning it off by hand, and the way to force it on over a window none of the
        /// rules recognise. Called from SendPad ahead of the gate, like PointerTake.
        /// </summary>
        bool PointerL3(string button, string phase)
        {
            if (_ptrOn || phase == "release") return false;
            if (string.IsNullOrEmpty(button) || button.ToLowerInvariant() != "l3") return false;
            if (ShellIsForeground()) return false;      // the page's own cursor mode owns L3 there
            if (_opt.PtrDisabled) return false;
            _ptrManual = 1;
            Log.Write("PTR", "L3: the human turned the pointer on");
            PointerEvaluate();
            return true;
        }

        // ── Typing into a window the shell cannot draw on ────────────────────────────────
        //
        // Triangle. The host remembers the target, brings the SHELL forward with its own
        // on-screen keyboard open (the only keyboard on this machine), and when the human is
        // done it puts the target back in front and types the text as Unicode. The text is
        // never logged - a sign-in field is the obvious use and the count is all the log needs.
        void PointerBeginTyping()
        {
            IntPtr t = Native.GetForegroundWindow();
            if (t == IntPtr.Zero) { Log.Write("PTR", "Triangle: nothing is in the foreground to type into"); return; }
            _ptrTypeHwnd = t;
            _ptrTypeTitle = Foreground.Title(t);
            _ptrManualSaved = _ptrManual;
            _ptrTyping = true;
            PointerSet(false, "typing: the shell comes forward with the keyboard", t);
            Log.Write("PTR", "Triangle: keyboard requested for " + Foreground.Describe(t));

            BringShellForward("pointer typing");
            PostToPage("{\"type\":\"ptr\",\"ev\":\"type\",\"title\":\"" + JsonEsc(_ptrTypeTitle) + "\"}");
        }

        void HandlePointerMessage(string raw, string cmd)
        {
            if (cmd == "text" || cmd == "cancel")
            {
                if (!_ptrTyping) { Log.Write("PTR", "page sent ptr." + cmd + " but nothing was waiting for it"); return; }
                IntPtr t = _ptrTypeHwnd;
                int pid = Foreground.PidOf(t);
                string text = cmd == "text" ? Json.Str(raw, "text") : null;
                bool enter = cmd == "text" && Json.Int(raw, "enter", 0) != 0;

                string path = Foreground.ForceForeground(t);
                Log.Write("PTR", "returning to " + Foreground.Describe(t) + " via " + (path == null ? "FAILED" : path));

                if (cmd == "cancel")
                {
                    Log.Write("PTR", "typing cancelled - nothing was typed");
                }
                else if (Native.GetForegroundWindow() != t)
                {
                    Log.Write("PTR", "REFUSING to type: '" + _ptrTypeTitle + "' did not come back to the"
                        + " foreground, and the keystrokes would land in whatever did");
                }
                else
                {
                    Thread.Sleep(250);          // let the window settle its own focus first
                    int n = _ptr.TypeText(text == null ? "" : text);
                    if (enter) _ptr.KeyTap(Native.VK_RETURN);
                    Log.Write("PTR", "typed " + n + " chars into " + pid + " (enter=" + (enter ? "yes" : "no") + ")");
                }

                _ptrTyping = false;
                _ptrTypeHwnd = IntPtr.Zero;
                _ptrManual = _ptrManualSaved;
                _ptrEvalAt = DateTime.MinValue;
                PointerEvaluate();
                return;
            }
            if (cmd == "state")
            {
                POINT c = PointerMode.Cursor();
                PostToPage("{\"type\":\"ptr\",\"ev\":\"state\",\"on\":" + (_ptrOn ? "true" : "false")
                    + ",\"reason\":\"" + JsonEsc(_ptrReason) + "\",\"x\":" + c.X + ",\"y\":" + c.Y + "}");
                return;
            }
            Log.Write("PTR", "unknown ptr command from the page: " + Trim(raw, 160));
        }

        /// <summary>
        /// The two --walk tokens that move a thumbstick nobody is holding:
        ///
        ///   lstick:&lt;x&gt;/&lt;y&gt;[:&lt;ms&gt;]     left stick, -1..1, for ms (default 500)
        ///   rstick:&lt;x&gt;/&lt;y&gt;[:&lt;ms&gt;]     right stick (the wheel)
        ///
        /// The vector is SLASH-separated, not comma-separated, because --walk itself is a
        /// comma-separated list: a comma inside a token is eaten by the outer split before
        /// this is ever called, and the first bench run of this feature spent its whole
        /// sequence on the halves of torn tokens. A comma is still accepted for the case
        /// where the vector arrives whole from somewhere else.
        ///
        /// Without these, pointer mode could only ever be tested by a human thumb: the live
        /// shell owns the real pad over raw HID, so a test instance runs --no-pad and has no
        /// analog input at all. They feed exactly the vector the HID decode would produce, so
        /// what they exercise is the real curve, the real dead zone and the real speed.
        /// </summary>
        bool WalkStickStep(string step, string why)
        {
            if (string.IsNullOrEmpty(step)) return false;
            string[] p = step.Split(':');
            string head = p[0].Trim().ToLowerInvariant();
            if (head != "lstick" && head != "rstick") return false;
            if (p.Length < 2) { Log.Write("WALK", "step '" + step + "' ignored: wants " + head + ":<x>/<y>[:<ms>]"); return true; }

            string[] xy = p[1].Split('/', ',');
            double x = 0, y = 0;
            if (xy.Length < 2
                || !double.TryParse(xy[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                || !double.TryParse(xy[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out y))
            {
                Log.Write("WALK", "step '" + step + "' ignored: could not read the vector");
                return true;
            }
            int ms = 500;
            if (p.Length > 2) int.TryParse(p[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ms);

            if (head == "lstick") { _synthLX = x; _synthLY = y; }
            else { _synthRX = x; _synthRY = y; }
            _synthUntil = DateTime.Now.AddMilliseconds(ms);
            POINT c = PointerMode.Cursor();
            Log.Write("WALK", head + " " + x.ToString("0.##", CultureInfo.InvariantCulture) + ","
                + y.ToString("0.##", CultureInfo.InvariantCulture) + " for " + ms + " ms (" + why + ")"
                + " pointer=" + (_ptrOn ? "on" : "OFF") + " cursor=(" + c.X + "," + c.Y + ")");
            return true;
        }

        /// <summary>
        /// The touchpad's --walk tokens: a finger nobody is putting on the glass.
        ///
        ///   touch:&lt;x0&gt;/&lt;y0&gt;&gt;&lt;x1&gt;/&lt;y1&gt;[:&lt;ms&gt;]   drag finger 1 in a straight line
        ///   touch2:… same, with a second finger down: the scroll gesture
        ///   tap:&lt;x&gt;/&lt;y&gt;                            touch and lift in one place
        ///
        /// Coordinates are the surface's own units, 0-1919 across and 0-1079 down. Slash
        /// separated for the same reason the stick tokens are: --walk splits on commas.
        /// The synthetic finger is sampled by the same PointerTouchSample the real one goes
        /// through, so a walk exercises the real gesture machine - the gain, the
        /// acceleration, the tap threshold - and not a shortcut past it.
        /// </summary>
        bool WalkTouchStep(string step, string why)
        {
            if (string.IsNullOrEmpty(step)) return false;
            string[] p = step.Split(':');
            string head = p[0].Trim().ToLowerInvariant();
            if (head != "touch" && head != "tap" && head != "touch2") return false;
            if (p.Length < 2) { Log.Write("WALK", "step '" + step + "' ignored: wants " + head + ":<x>/<y>…"); return true; }

            int ms = 400;
            if (p.Length > 2) int.TryParse(p[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ms);

            string body = p[1].Trim();
            string from = body, to = body;
            int arrow = body.IndexOf('>');
            if (arrow > 0) { from = body.Substring(0, arrow); to = body.Substring(arrow + 1); }

            int x0, y0, x1, y1;
            if (!TouchPoint(from, out x0, out y0) || !TouchPoint(to, out x1, out y1))
            {
                Log.Write("WALK", "step '" + step + "' ignored: could not read the coordinates");
                return true;
            }

            if (head == "tap") { x1 = x0; y1 = y0; ms = 90; }      // inside the 150 ms tap window
            _stx0 = x0; _sty0 = y0; _stx1 = x1; _sty1 = y1; _stMs = ms;
            _stStart = DateTime.Now;
            _synthTouch = true;
            _synthTouchTwo = head == "touch2";
            POINT c = PointerMode.Cursor();
            Log.Write("WALK", head + " " + x0 + "," + y0 + " -> " + x1 + "," + y1 + " over " + ms + " ms ("
                + why + ") pointer=" + (_ptrOn ? "on" : "OFF") + " cursor=(" + c.X + "," + c.Y + ")");
            return true;
        }

        static bool TouchPoint(string s, out int x, out int y)
        {
            x = 0; y = 0;
            string[] a = s.Trim().Split('/', ',');
            return a.Length >= 2
                && int.TryParse(a[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out x)
                && int.TryParse(a[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out y);
        }

        #endregion

        /// <summary>
        /// The three --walk steps that carry a PHASE rather than just an action:
        ///
        ///   hold:&lt;ms&gt;:&lt;action&gt;[:&lt;button&gt;]   press now, release &lt;ms&gt; later
        ///   press:&lt;action&gt;[:&lt;button&gt;]         press and do not let go
        ///   release:&lt;action&gt;[:&lt;button&gt;]       let go
        ///
        /// Same vocabulary as .stage/serve.mjs's ?pad= tokens, so a walkthrough written against
        /// the staged page reads the same against the real host.
        ///
        /// Why this had to exist. The consent sheet (Grant.ask) deliberately refuses a tap and
        /// requires Cross HELD for ~800 ms - that gesture is the whole security argument, it
        /// stands in for a UAC dialog on a screen no controller can reach. A walk of bare action
        /// tokens could not express it in either direction: every step was a press with no
        /// release, so a "tap" was indistinguishable from an infinite hold, and the one thing
        /// worth proving - that a press on its own does NOTHING - could not be driven at all.
        /// Returns false for anything that is not one of the three, so ordinary steps fall
        /// through to the old path untouched.
        /// </summary>
        bool WalkPhaseStep(string step, string why)
        {
            if (string.IsNullOrEmpty(step)) return false;
            string[] p = step.Split(':');
            string head = p[0].Trim().ToLowerInvariant();
            if (head != "hold" && head != "press" && head != "release") return false;

            int at = 1;
            int ms = 0;
            if (head == "hold")
            {
                if (p.Length < 3
                    || !int.TryParse(p[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ms)
                    || ms < 0)
                {
                    Log.Write("WALK", "step '" + step + "' ignored: hold wants hold:<ms>:<action>[:<button>]");
                    return true;                      // consumed, and said why
                }
                at = 2;
            }
            if (p.Length <= at || p[at].Trim().Length == 0)
            {
                Log.Write("WALK", "step '" + step + "' ignored: no action");
                return true;
            }
            string action = p[at].Trim();
            string button = (p.Length > at + 1 && p[at + 1].Trim().Length > 0) ? p[at + 1].Trim() : action;

            if (head == "release")
            {
                _walkRelAt = DateTime.MaxValue; _walkRelAction = null; _walkRelButton = null;
                SendPad(action, why + " [release]", button, "release");
                return true;
            }

            // Any earlier hold is let go of first: two buttons stuck down at once is a state a
            // thumb cannot produce and the page has never been asked to handle.
            if (_walkRelAt != DateTime.MaxValue)
            {
                SendPad(_walkRelAction, "--walk release (superseded)", _walkRelButton, "release");
                _walkRelAt = DateTime.MaxValue; _walkRelAction = null; _walkRelButton = null;
            }

            SendPad(action, why + (head == "hold" ? " [hold " + ms + " ms]" : " [press]"), button, "press");
            if (head == "hold")
            {
                _walkRelAction = action; _walkRelButton = button;
                _walkRelAt = DateTime.Now.AddMilliseconds(ms);
            }
            return true;
        }

        void SendPad(string action, string why)
        {
            SendPad(action, why, action, "press");
        }

        void SendPad(string action, string why, string button, string phase)
        {
            // Pointer mode, before the gate and for the same reason the guide button is: the
            // gate's whole job is to drop input when the shell is not in front, and "the shell
            // is not in front" is the precondition for the pointer existing at all. See the
            // pointer-mode region. PointerL3 is the way back IN after it was turned off.
            if (PointerTake(action, button, phase)) return;
            if (PointerL3(button, phase)) return;

            bool gated = PadInputGated();
            if (gated != _padGateClosed)
            {
                _padGateClosed = gated;
                if (gated)
                {
                    _gateDropped = 0;
                    Log.Write("GATE", "pad input gate CLOSED - the shell is not driving its UI."
                        + " foreground is " + Foreground.Describe(Native.GetForegroundWindow())
                        + (_childRunning ? "; app '" + (_running == null ? "?" : _running.Title) + "' owns the screen" : "")
                        + ". Only the guide button acts from here.");
                }
                else
                {
                    Log.Write("GATE", "pad input gate OPEN - the shell is in the foreground again"
                        + (_gateDropped > 0 ? " (" + _gateDropped + " pad actions were dropped while it was closed)" : ""));
                }
            }
            if (gated)
            {
                _gateDropped++;
                if (phase != "release")
                    Log.Write("GATE", "dropped '" + action + "' [" + why + "] - the shell is not in the foreground"
                        + " (drop #" + _gateDropped + ")");
                return;
            }

            _padActionCount++;
            _lastPadAction = action + " (" + why + ")";
            // Releases are frequent and uninteresting; log them at a lower volume.
            //
            // The sequence number and the phase are here because this line is what
            // gets read when somebody asks "did that press fire twice?". It is the
            // only line in the log written at the moment a press is dispatched:
            // every [PAGE] line is stamped when the HOST RECEIVED it over the
            // WebView2 message channel, not when the page emitted it, so a [PAGE]
            // line can appear before or after host-written lines that describe
            // later events. A report of a double dispatch was traced to exactly
            // that — one press, one dispatch, and two [PAGE] lines either side of
            // it that looked like two. A monotonic counter settles it: two actions
            // from one press means two of these lines, and nothing else does.
            if (phase != "release")
                Log.Write("PAD", "#" + _padActionCount + " -> page: " + action
                    + "   [" + why + "] phase=" + phase + " button=" + button);
            PostToPage("{\"type\":\"pad\",\"action\":\"" + action
                + "\",\"button\":\"" + button + "\",\"phase\":\"" + phase + "\"}");
        }

        void TracePadState(PadSnapshot s)
        {
            if (s.Connected != _padConnected)
            {
                _padConnected = s.Connected;
                if (s.Connected)
                    Log.Write("PAD", "pad CONNECTED: " + s.Model + " VID=0x" + s.Vid.ToString("X4")
                        + " PID=0x" + s.Pid.ToString("X4"));
                else
                {
                    Log.Write("PAD", "pad DISCONNECTED - rescanning");
                    _padTransport = "";
                    _padDir = 0;
                    _padRepeatAt = DateTime.MaxValue;
                }
            }
            if (s.Connected && s.Transport != _padTransport && s.Transport != "?")
            {
                _padTransport = s.Transport;
                Log.Write("PAD", "transport now " + s.Transport + " (report id 0x" + s.ReportId.ToString("X2")
                    + ", " + s.ReportLength + " bytes)  resting sticks LX=" + s.LX + " LY=" + s.LY
                    + " RX=" + s.RX + " RY=" + s.RY);
            }
        }

        ushort PollXInput(out int heldDir, out int heldDirV)
        {
            heldDir = 0;
            heldDirV = 0;
            if (!_xi.Available) return 0;

            uint mask = 0;
            ushort buttons = 0;
            for (uint i = 0; i < 4; i++)
            {
                XINPUT_STATE st;
                uint res = _xi.ExAvailable ? _xi.GetStateEx(i, out st) : _xi.GetState(i, out st);
                if (res != 0) continue;
                mask |= (1u << (int)i);
                buttons |= st.Gamepad.wButtons;
                if (st.Gamepad.sThumbLX <= -XiDeadzone || (st.Gamepad.wButtons & XI_DLEFT) != 0) heldDir = -1;
                else if (st.Gamepad.sThumbLX >= XiDeadzone || (st.Gamepad.wButtons & XI_DRIGHT) != 0) heldDir = 1;
                // XInput's Y axis grows upwards, the opposite of the DualSense's,
                // so the comparisons are the other way round here.
                if (st.Gamepad.sThumbLY >= XiDeadzone || (st.Gamepad.wButtons & XI_DUP) != 0) heldDirV = -1;
                else if (st.Gamepad.sThumbLY <= -XiDeadzone || (st.Gamepad.wButtons & XI_DDOWN) != 0) heldDirV = 1;
            }
            if (mask != _xiConnectedMask)
            {
                Log.Write("XINPUT", "connection mask changed 0x" + _xiConnectedMask.ToString("X")
                    + " -> 0x" + mask.ToString("X"));
                _xiConnectedMask = mask;
            }

            ushort pressed = (ushort)(buttons & ~_xiLastButtons);
            _xiLastButtons = buttons;
            return pressed;
        }

        string PadSummary()
        {
            PadSnapshot s = _ds.Snapshot;
            if (!s.Connected) return "no pad seen (raw HID scan found no DualSense / DualShock)";
            return s.Model + " over " + (s.Transport == "?" ? "(detecting)" : s.Transport)
                + "  reportId=0x" + s.ReportId.ToString("X2") + " len=" + s.ReportLength
                + " reports=" + s.Reports + "  resting L=" + s.LX + "," + s.LY + " R=" + s.RX + "," + s.RY;
        }

        #endregion

        #region launch / return  (same cycle as ShellHost.cs)

        void Launch(string command)
        {
            if (_childRunning) { Log.Write("LAUNCH", "ignored - child already running"); return; }
            if (string.IsNullOrEmpty(command)) command = _opt.ChildCommand;

            _launchCount++;
            _launchedAt = DateTime.Now;
            _childRunning = true;
            _shellPulledForward = false;
            _running = new RunningApp();
            _running.Title = command;
            _running.Kind = "exe";
            _running.Target = command;
            _running.Tracked = true;

            _tracker = new ChildTracker();
            _tracker.TreeEmpty += OnTreeEmpty;

            Log.Write("LAUNCH", "launching child, then yielding the screen...");
            if (!_tracker.Start(command, _opt.NoJob))
            {
                _childRunning = false;
                _running = null;
                Log.Write("LAUNCH", "launch FAILED - staying in foreground");
                return;
            }

            _running.Pid = _tracker.RootPid;
            PostToPage("{\"type\":\"launching\"}");
            Native.ShowWindow(Handle, Native.SW_MINIMIZE);
            Log.Write("HANDOFF", "host minimized (SW_MINIMIZE); tracking mode=" + _tracker.Mode
                + " rootPid=" + _tracker.RootPid);
        }

        // ── Adopting a launch the host did not make ──────────────────────────────────────
        //
        // ui/library.js posts this the instant lib.launch answers. Until this build the host
        // only logged it, which is precisely the bug: the shell stayed in the foreground with
        // the app on top of it, still acting on the pad.
        //
        // The distinction LibraryApi already draws is honoured rather than re-derived:
        //   launchKind=exe / aumid  + pidIsTarget  -> the pid IS the app. Adopt it.
        //   launchKind=uri          + !pidIsTarget -> the pid is a protocol-handler stub that
        //                                             exits in a second. Adopting it would take
        //                                             the shell back over a game that is still
        //                                             loading, so it is NOT adopted; the shell
        //                                             yields the screen and the guide button is
        //                                             the way back.
        void OnPageLaunched(string raw)
        {
            int pid = Json.Int(raw, "pid", 0);
            string id = Json.Str(raw, "id");
            string title = Json.Str(raw, "title");
            string kind = Json.Str(raw, "launchKind");
            string target = Json.Str(raw, "launchTarget");
            bool trackable = Regex.IsMatch(raw, "\"trackable\"\\s*:\\s*true");
            bool pidIsTarget = Regex.IsMatch(raw, "\"pidIsTarget\"\\s*:\\s*true");

            Log.Write("LIB", "page launched id='" + id + "' title='" + title + "' kind=" + kind
                + " pid=" + pid + " trackable=" + (trackable ? "yes" : "no")
                + " pidIsTarget=" + (pidIsTarget ? "yes" : "no"));

            ReleaseCurrentApp("a new launch arrived");

            _running = new RunningApp();
            _running.Id = id;
            _running.Title = string.IsNullOrEmpty(title) ? "(untitled)" : title;
            _running.Kind = kind;
            // What the LIBRARY calls this tile - game, launcher or app. Pointer mode is the
            // only consumer: a game reads the pad itself and must never have a mouse pushed
            // at it, a launcher is a mouse UI and must. Absent on an older page, and absence
            // is treated as "assume a game", which is the safe direction.
            _running.EntryKind = Json.Str(raw, "entryKind");
            _running.Target = target;
            _running.Pid = pid;
            _running.Tracked = false;

            bool adopt = pid > 0 && trackable && pidIsTarget;
            if (adopt)
            {
                _tracker = new ChildTracker();
                _tracker.TreeEmpty += OnTreeEmpty;
                if (_tracker.Adopt(pid, _opt.NoJob))
                {
                    _running.Tracked = true;
                }
                else
                {
                    _tracker.Cleanup();
                    _tracker = null;
                    Log.Write("ADOPT", "adoption of pid " + pid + " failed outright - the shell will"
                        + " still yield the screen, but only the guide button can bring it back.");
                }
            }
            else if (pid > 0)
            {
                Log.Write("ADOPT", "NOT adopting pid " + pid + ": launchKind=" + kind
                    + " means that pid is the launcher stub, not '" + _running.Title + "'."
                    + " Its exit says nothing about the app, so waiting on it would take the shell"
                    + " back over a game that is still loading. Guide button returns.");
            }
            else
            {
                Log.Write("ADOPT", "NOT adopting: lib.launch returned no pid for '" + _running.Title + "'.");
            }

            YieldScreen(_running.Title, _running.Tracked);
            PublishAppState(true);
        }

        /// <summary>Hide the shell and hand the screen to whatever was just started.</summary>
        void YieldScreen(string what, bool tracked)
        {
            _childRunning = true;
            _shellPulledForward = false;
            _launchedAt = DateTime.Now;
            PostToPage("{\"type\":\"launching\"}");
            Native.ShowWindow(Handle, Native.SW_MINIMIZE);
            Log.Write("HANDOFF", "shell minimized for '" + what + "'; "
                + (tracked
                    ? "tracking mode=" + _tracker.Mode + " rootPid=" + _tracker.RootPid
                      + " - the shell returns by itself when the app tree is empty"
                    : "UNTRACKED - the shell will NOT return by itself; press the guide button"));
        }

        // ── The guide button ─────────────────────────────────────────────────────────────
        //
        // On a console the guide button is how you get out of whatever you are in, and this
        // shell now treats it as the app-lifecycle control rather than a second control-centre
        // key. Two gestures, and only two:
        //
        //   SHORT PRESS (down and up inside 700 ms)
        //     * an app is running -> the shell comes forward over it and the guide MENU opens,
        //       naming the app: Resume, Minimise, Close. Close is behind a confirmation because
        //       it terminates the process tree and a game loses unsaved progress.
        //     * nothing running, shell in front -> unchanged from every previous build: the
        //       "guide" action goes to the page and opens the control centre.
        //     * nothing running, shell behind something -> just come forward.
        //
        //   HOLD (700 ms)
        //     Straight to the shell, no menu. The app keeps running. This is the "get me out"
        //     gesture and it deliberately has no confirmation and no destination choice.
        //
        // The app is left RUNNING in every path except an explicitly confirmed Close. That is
        // what a console does, and it is why Resume exists: the rail tile for a running app
        // raises it rather than starting a second copy (see OnLibActivate).
        //
        // Why a hold cannot fire twice: the hold fires the instant the threshold is crossed and
        // sets _psConsumed, which the release path checks. Why a brush of the palm is safe: the
        // short press opens a menu whose default focus is Resume, and Circle closes it. Nothing
        // destructive is one press away, from anywhere.
        void PumpGuide(PadSnapshot s, int hidEdges, ushort xiEdges)
        {
            bool down = (s.Connected && (s.Buttons & DualSense.BTN_PS) != 0)
                        || (_xiLastButtons & XI_GUIDE) != 0;
            DateTime now = DateTime.Now;

            // A tap shorter than one 16 ms tick never appears as "down" in any snapshot, but the
            // reader thread latched its edge. Honour it as a short press.
            if (!down && !_psDown && ((hidEdges & DualSense.BTN_PS) != 0 || (xiEdges & XI_GUIDE) != 0))
            {
                GuideShort("PS tap (shorter than one tick)");
                return;
            }

            if (down && !_psDown)
            {
                _psDown = true;
                _psDownAt = now;
                _psConsumed = false;
                return;
            }

            if (down && _psDown)
            {
                if (!_psConsumed && (now - _psDownAt).TotalMilliseconds >= GuideHoldMs)
                {
                    _psConsumed = true;
                    GuideHold("PS held past " + GuideHoldMs + " ms");
                }
                return;
            }

            if (!down && _psDown)
            {
                _psDown = false;
                if (!_psConsumed)
                    GuideShort("PS short press (" + (int)(now - _psDownAt).TotalMilliseconds + " ms)");
            }
        }

        bool GuideDebounced()
        {
            DateTime now = DateTime.Now;
            if ((now - _guideAt).TotalMilliseconds < GuideDebounceMs)
            {
                Log.Write("GUIDE", "guide gesture ignored - within the " + GuideDebounceMs + " ms debounce");
                return true;
            }
            _guideAt = now;
            return false;
        }

        void GuideShort(string why)
        {
            if (GuideDebounced()) return;

            if (_running != null)
            {
                // Snapshot BEFORE coming forward. BringShellForward ends an UNTRACKED yield
                // outright - it has to, because nothing will ever tell the shell that app
                // exited - and that clears _running. Reading it afterwards posted a menu with
                // "app":null, which the page can only answer with "Nothing is running" one
                // instant after the log said Hollow Knight was. Bench-observed 2026-08-16.
                bool tracked = _running.Tracked;
                string title = _running.Title;
                string appJson = RunningAppJson(_running);

                if (!tracked)
                {
                    // A URI launch the host could not attach to. Coming forward IS the whole
                    // action: there is no tree to Close and no window the shell can promise to
                    // Resume, so offering a menu of three things that cannot be honoured is
                    // worse than offering none. The app is left running.
                    Log.Write("GUIDE", "short press (" + why + ") with untracked '" + title
                        + "' on screen -> shell forward, no menu (nothing about it can be acted on:"
                        + " the shell never attached to it)");
                    if (!ShellIsForeground()) BringShellForward("guide");
                    PublishAppState(true);
                    return;
                }

                Log.Write("GUIDE", "short press (" + why + ") with '" + title
                    + "' running -> shell forward + guide menu");
                if (!ShellIsForeground()) BringShellForward("guide");
                PublishAppState(true);
                PostToPage("{\"type\":\"guide\",\"ev\":\"menu\",\"app\":" + appJson + "}");
                return;
            }

            if (ShellIsForeground())
            {
                Log.Write("GUIDE", "short press (" + why + ") with nothing running and the shell in"
                    + " front -> routed to the page as the control-centre action");
                SendPad("guide", why, "ps", "press");
                return;
            }

            Log.Write("GUIDE", "short press (" + why + ") with nothing tracked, but the shell is behind "
                + Foreground.Describe(Native.GetForegroundWindow()) + " -> bringing it forward");
            BringShellForward("guide");
        }

        void GuideHold(string why)
        {
            if (GuideDebounced()) return;

            if (!ShellIsForeground())
            {
                Log.Write("GUIDE", "hold (" + why + ") -> straight to the shell, no menu."
                    + (_running == null ? "" : " '" + _running.Title + "' keeps running."));
                BringShellForward("guide hold");
                PublishAppState(true);
                return;
            }
            Log.Write("GUIDE", "hold (" + why + ") but the shell is already in front - nothing to do");
        }

        // ── The guide menu's three actions ───────────────────────────────────────────────
        void HandleAppMessage(string raw, string cmd)
        {
            switch (cmd)
            {
                case "resume":
                {
                    if (_running == null)
                    {
                        Log.Write("APP", "resume asked for, but nothing is running");
                        PostToPage("{\"type\":\"app\",\"ev\":\"resume\",\"ok\":false,"
                            + "\"detail\":\"nothing is running\"}");
                        break;
                    }
                    string title = _running.Title;
                    bool ok = RaiseRunningApp(AllAppPids(), title);
                    Log.Write("APP", "resume '" + title + "' -> " + (ok ? "raised, shell hidden" : "FAILED"));
                    PostToPage("{\"type\":\"app\",\"ev\":\"resume\",\"ok\":" + (ok ? "true" : "false")
                        + ",\"title\":\"" + JsonEsc(title) + "\"}");
                    break;
                }

                case "minimise":
                case "minimize":
                    // The shell is already in front - the menu is drawn over it. All this does is
                    // say so, and confirm the app was left alone.
                    Log.Write("APP", "minimise: staying in the shell; '"
                        + (_running == null ? "(nothing)" : _running.Title) + "' is left running");
                    PostToPage("{\"type\":\"app\",\"ev\":\"minimise\",\"ok\":true}");
                    break;

                case "close":
                {
                    if (_running == null)
                    {
                        Log.Write("APP", "close asked for, but nothing is running");
                        PostToPage("{\"type\":\"app\",\"ev\":\"close\",\"ok\":false,"
                            + "\"detail\":\"nothing is running\"}");
                        break;
                    }
                    string title = _running.Title;
                    int killed = CloseRunningApp();
                    PostToPage("{\"type\":\"app\",\"ev\":\"close\",\"ok\":" + (killed > 0 ? "true" : "false")
                        + ",\"title\":\"" + JsonEsc(title) + "\",\"killed\":" + killed + "}");
                    break;
                }

                case "state":
                    PublishAppState(true);
                    break;

                default:
                    Log.Write("APP", "unknown app command '" + cmd + "'");
                    break;
            }
        }

        /// <summary>
        /// Every pid the shell believes belongs to the running app: what the job object holds,
        /// unioned with whatever is descended from the pid that was adopted. The union matters
        /// for an app that was already running when the shell adopted it - its existing children
        /// (Steam's steamwebhelper fleet is the standing example) were never in the job, because
        /// a job only captures what is spawned after the assignment.
        /// </summary>
        List<int> AllAppPids()
        {
            List<int> all = new List<int>();
            if (_tracker != null)
            {
                foreach (int p in _tracker.GetTrackedPids())
                    if (!all.Contains(p)) all.Add(p);
            }
            if (_running != null && _running.Pid > 0)
            {
                foreach (int p in ChildTracker.GetDescendants(_running.Pid))
                    if (!all.Contains(p)) all.Add(p);
            }
            return all;
        }

        /// <summary>
        /// Terminate the running app's whole tree. Not just the pid that was adopted: taskkill
        /// /T walks the parent chain, and the pid list is the union above, so the helper
        /// processes a launcher leaves behind go with it. Returns how many pids were signalled.
        /// </summary>
        int CloseRunningApp()
        {
            List<int> pids = AllAppPids();
            string title = _running == null ? "(unnamed)" : _running.Title;
            Log.Write("APP", "close '" + title + "': terminating the tracked tree, pids=[" + JoinPids(pids) + "]");
            if (pids.Count == 0)
            {
                Log.Write("APP", "close '" + title + "': nothing left to terminate - treating it as gone");
                ReleaseCurrentApp("close found nothing running");
                PublishAppState(true);
                return 0;
            }

            int signalled = 0;
            foreach (int pid in pids)
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo("taskkill.exe", "/PID " + pid + " /T /F");
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.RedirectStandardOutput = true;
                    psi.RedirectStandardError = true;
                    Process tk = Process.Start(psi);
                    string outp = (tk.StandardOutput.ReadToEnd() + " " + tk.StandardError.ReadToEnd()).Trim();
                    tk.WaitForExit(5000);
                    if (tk.ExitCode == 0) signalled++;
                    Log.Write("APP", "taskkill /PID " + pid + " /T /F -> exit " + tk.ExitCode + " : " + outp);
                }
                catch (Exception ex)
                {
                    Log.Write("APP", "taskkill for pid " + pid + " threw: " + ex.Message);
                }
            }

            // The tracker's TreeEmpty will fire on its own and run the ordinary return path. If
            // the app was never trackable there is nothing to fire, so end the yield here.
            if (_running != null && !_running.Tracked)
            {
                ReleaseCurrentApp("closed an app the shell could not track");
                PublishAppState(true);
            }
            return signalled;
        }

        void BringShellForward(string reason)
        {
            _shellPulledForward = true;

            // An untracked yield (a URI launch) has nothing that will ever tell the shell the
            // app is gone, so coming forward ends that yield outright. A tracked app stays
            // recorded: it is still running, and the tile must raise it rather than relaunch.
            if (_childRunning && (_running == null || !_running.Tracked))
            {
                Log.Write("GUIDE", "the yield was untracked, so it ends here - the shell is live again"
                    + (_running == null ? "" : " (" + _running.Title + " is left running)"));
                _childRunning = false;
                _running = null;
            }

            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Stopwatch sw = Stopwatch.StartNew();
            string path = Foreground.ForceForeground(Handle);
            sw.Stop();
            bool ok = Native.GetForegroundWindow() == Handle;
            Log.Write("GUIDE", "forced-foreground path: " + (path == null ? "ALL PATHS FAILED" : path)
                + "  (" + sw.ElapsedMilliseconds + " ms)");
            Log.Write("VERIFY", ok
                ? "PASS shell is foreground after " + reason + " (hwnd 0x" + Handle.ToInt64().ToString("X") + ")"
                : "FAIL after " + reason + ", foreground is " + Foreground.Describe(Native.GetForegroundWindow()));

            try { _web.Focus(); }
            catch { }
            PostToPage("{\"type\":\"shellforward\",\"reason\":\"" + JsonEsc(reason) + "\",\"app\":\""
                + JsonEsc(_running == null ? "" : _running.Title) + "\"}");
        }

        /// <summary>
        /// Bring an already-running app back to the front and hide the shell again. This is the
        /// other half of the guide button: coming back to the shell must not be a one-way door.
        /// </summary>
        bool RaiseRunningApp(List<int> pids, string title)
        {
            IntPtr hwnd = AppWindows.MainWindowOf(pids);
            if (hwnd == IntPtr.Zero)
            {
                Log.Write("RAISE", "no visible top-level window found for '" + title + "' (pids=["
                    + JoinPids(pids) + "]) - cannot raise it");
                return false;
            }
            if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);
            string path = Foreground.ForceForeground(hwnd);
            bool ok = Native.GetForegroundWindow() == hwnd;
            Log.Write("RAISE", "raised '" + title + "' " + Foreground.Describe(hwnd)
                + " via " + (path == null ? "ALL PATHS FAILED" : path) + " -> " + (ok ? "PASS" : "FAIL"));
            if (ok)
            {
                _shellPulledForward = false;
                Native.ShowWindow(Handle, Native.SW_MINIMIZE);
            }
            return ok;
        }

        static string JoinPids(List<int> pids)
        {
            return string.Join(",", pids.ConvertAll<string>(delegate(int p)
            {
                return p.ToString(CultureInfo.InvariantCulture);
            }).ToArray());
        }

        // ── "Is it already running?" ─────────────────────────────────────────────────────
        //
        // ui/library.js asks this before every lib.launch and does not launch if the answer is
        // yes. Four Steam clients is a bug in its own right and this is where it is stopped.
        //
        // Two ways to answer yes:
        //   1. the shell already adopted this tile and its process tree is still alive;
        //   2. nothing was adopted, but a process is already running the exact executable this
        //      exe-kind tile points at - which covers an app the human started outside the
        //      shell entirely, and Steam started from the desktop is exactly that case.
        // A uri or aumid tile cannot be answered this way and is reported as not running.
        void OnLibActivate(string raw)
        {
            string reqId = Json.Str(raw, "reqId");
            string id = Json.Str(raw, "id");
            string title = Json.Str(raw, "title");
            string kind = Json.Str(raw, "launchKind");
            string target = Json.Str(raw, "launchTarget");
            bool raised = false;
            string detail;

            List<int> alive = null;
            if (_childRunning && _running != null && _running.Tracked && _tracker != null
                && (id == null || id == _running.Id))
            {
                alive = _tracker.GetTrackedPids();
                if (alive.Count == 0) alive = null;
            }

            if (alive == null && string.Equals(kind, "exe", StringComparison.OrdinalIgnoreCase))
            {
                int found = AppWindows.FindRunningByImage(target);
                if (found > 0)
                {
                    alive = new List<int>();
                    alive.Add(found);
                    Log.Write("ACTIVATE", "'" + title + "' is already running as pid " + found
                        + " (" + target + ") - it was not started by this shell, adopting it now");
                    AdoptExisting(found, id, title, kind, target);
                    if (_tracker != null) alive = _tracker.GetTrackedPids();
                    if (alive.Count == 0) { alive = new List<int>(); alive.Add(found); }
                }
            }

            if (alive != null)
            {
                raised = RaiseRunningApp(alive, title == null ? "(untitled)" : title);
                detail = raised
                    ? "already running - raised instead of launching a second copy"
                    : "already running, but no window could be raised";
                // Even a failed raise must not become a second launch: the app is up.
                raised = true;
            }
            else
            {
                detail = "not running - go ahead and launch";
            }

            Log.Write("ACTIVATE", "'" + title + "' kind=" + kind + " -> " + detail);
            PostToPage("{\"type\":\"lib.run.reply\""
                + (reqId == null ? "" : ",\"reqId\":\"" + JsonEsc(reqId) + "\"")
                + ",\"running\":" + (raised ? "true" : "false")
                + ",\"detail\":\"" + JsonEsc(detail) + "\"}");
        }

        /// <summary>
        /// Stop owning whatever the shell was yielded to. It is NOT killed and NOT interfered
        /// with - it is simply no longer the thing the shell will come back from. The shell can
        /// only be in front of one app at a time, so starting a second one abandons the first
        /// to the background, which is what the human just asked for by launching it.
        /// </summary>
        void ReleaseCurrentApp(string why)
        {
            if (!_childRunning && _tracker == null && _running == null) return;
            Log.Write("APP", "releasing '" + (_running == null ? "(unnamed)" : _running.Title)
                + "' - " + why + ". It is left running; the shell just stops waiting on it.");
            if (_tracker != null) { _tracker.Cleanup(); _tracker = null; }
            _running = null;
            _childRunning = false;
            _shellPulledForward = false;
        }

        void AdoptExisting(int pid, string id, string title, string kind, string target)
        {
            ReleaseCurrentApp("adopting an app that was already running");
            _running = new RunningApp();
            _running.Id = id;
            _running.Title = string.IsNullOrEmpty(title) ? "(untitled)" : title;
            _running.Kind = kind;
            _running.Target = target;
            _running.Pid = pid;
            _running.Tracked = false;

            _tracker = new ChildTracker();
            _tracker.TreeEmpty += OnTreeEmpty;
            if (_tracker.Adopt(pid, _opt.NoJob)) _running.Tracked = true;
            else { _tracker.Cleanup(); _tracker = null; }
            _childRunning = true;
            _shellPulledForward = false;
        }

        void OnTreeEmpty()
        {
            try
            {
                if (IsHandleCreated) BeginInvoke(new Action(OnChildTreeGone));
            }
            catch { }
        }

        void OnChildTreeGone()
        {
            if (!_childRunning) return;
            bool wasForward = _shellPulledForward;
            _childRunning = false;
            _shellPulledForward = false;
            string what = _running == null ? "the child" : "'" + _running.Title + "'";
            _running = null;
            _returnCount++;
            DateTime detected = DateTime.Now;
            Log.Write("RETURN", what + " exited; beginning forced-foreground return"
                + (wasForward ? " (the shell was already in front - this only refreshes it)" : ""));
            Log.Write("RETURN", "foreground before restore: " + Foreground.Describe(Native.GetForegroundWindow()));

            WindowState = FormWindowState.Normal;
            Stopwatch sw = Stopwatch.StartNew();
            string path = Foreground.ForceForeground(Handle);
            sw.Stop();

            bool ok = Native.GetForegroundWindow() == Handle;
            if (ok) _returnsPassed++; else _returnsFailed++;
            _pathsUsed.Add("c" + _returnCount + ":" + (path == null ? "FAILED" : path));
            string lastFgPath = (path == null ? "ALL PATHS FAILED" : path) + "  (" + sw.ElapsedMilliseconds + " ms)";
            string lastVerify = ok
                ? "PASS GetForegroundWindow()==host hwnd (0x" + Handle.ToInt64().ToString("X") + ")"
                : "FAIL foreground is " + Foreground.Describe(Native.GetForegroundWindow());

            Log.Write("RETURN", "forced-foreground path: " + lastFgPath);
            Log.Write("VERIFY", lastVerify);

            if (_tracker != null) { _tracker.Cleanup(); _tracker = null; }
            _returnedAt = detected;

            // Put keyboard focus back inside the page, otherwise the UI stops responding to keys.
            try { _web.Focus(); }
            catch { }
            PostToPage("{\"type\":\"returned\"}");
            PublishAppState(true);
        }

        #endregion

        #region what is running  (published to the page, never polled by it)

        // ── The contract ────────────────────────────────────────────────────────────────
        //
        // The host is the only thing in this system that can see the process table, so it
        // publishes; the page subscribes. One message, pushed on every change and at most once
        // every 4 s otherwise, and suppressed entirely when nothing has changed:
        //
        //   {"type":"apps",
        //    "running":[ {"id":"lnk:721b…","title":"Steam","kind":"exe","pid":7052,
        //                 "tracked":true,"foreground":false,"pids":12} ],
        //    "background":[ {"name":"Steam","category":"launcher","pid":4480,"procs":8,
        //                    "actionable":true},
        //                   {"name":"Riot Vanguard","category":"anticheat","pid":1180,
        //                    "procs":2,"actionable":false} ]}
        //
        // "running" is the one app the shell has yielded to and can resume, minimise or close -
        // zero or one entry, never more, because the shell can only be in front of one thing.
        // "background" is everything worth naming that is up right now, whether the shell
        // started it or not. It is the answer to "what is running behind this", which is a
        // different question from "what did I launch".
        //
        // category: game | launcher | social | media | capture | service | anticheat
        // actionable=false means the shell must not offer to close it. Riot Vanguard is the
        // standing example: it is a kernel-mode driver plus a service, it cannot be closed from
        // a menu, and offering a Close that silently fails is worse than not offering one.
        //
        // Deliberately modest: a curated table of names, not every process on the box. A status
        // area listing svchost is noise, and a heuristic that guesses which svchost matters
        // would be wrong often enough to be untrustworthy.

        sealed class BgApp
        {
            public string Name;
            public string Category;
            public bool Actionable;
            public int Pid;
            public int Procs;
        }

        // process name (lower case, no extension) -> friendly name | category | actionable
        static readonly string[,] BackgroundTable = new string[,] {
            { "steam",                "Steam",                 "launcher", "1" },
            { "steamwebhelper",       "Steam",                 "launcher", "1" },
            { "steamservice",         "Steam",                 "launcher", "1" },
            { "gameoverlayui",        "Steam",                 "launcher", "1" },
            { "discord",              "Discord",               "social",   "1" },
            { "discordptb",           "Discord",               "social",   "1" },
            { "discordcanary",        "Discord",               "social",   "1" },
            { "epicgameslauncher",    "Epic Games Launcher",   "launcher", "1" },
            { "epicwebhelper",        "Epic Games Launcher",   "launcher", "1" },
            { "galaxyclient",         "GOG Galaxy",            "launcher", "1" },
            { "battle.net",           "Battle.net",            "launcher", "1" },
            { "eadesktop",            "EA app",                "launcher", "1" },
            { "ubisoftconnect",       "Ubisoft Connect",       "launcher", "1" },
            { "upc",                  "Ubisoft Connect",       "launcher", "1" },
            { "riotclientservices",   "Riot Client",           "launcher", "1" },
            { "riotclientux",         "Riot Client",           "launcher", "1" },
            { "leagueclient",         "League of Legends",     "game",     "1" },
            { "spotify",              "Spotify",               "media",    "1" },
            { "obs64",                "OBS Studio",            "capture",  "1" },
            { "obs32",                "OBS Studio",            "capture",  "1" },
            // Anti-cheat. Informational only: every one of these is a service or a kernel
            // driver, and none of them can be shut down from a shell menu.
            { "vgtray",               "Riot Vanguard",         "anticheat", "0" },
            { "vgc",                  "Riot Vanguard",         "anticheat", "0" },
            { "vgk",                  "Riot Vanguard",         "anticheat", "0" },
            { "easyanticheat",        "EasyAntiCheat",         "anticheat", "0" },
            { "easyanticheat_eos",    "EasyAntiCheat",         "anticheat", "0" },
            { "beservice",            "BattlEye",              "anticheat", "0" },
            { "bedaisy",              "BattlEye",              "anticheat", "0" },
            // Services worth naming because they explain fan noise and disk activity.
            { "nvcontainer",          "NVIDIA services",       "service",  "0" },
            { "nvdisplay.container",  "NVIDIA services",       "service",  "0" },
            { "searchindexer",        "Windows Search index",  "service",  "0" }
        };

        List<BgApp> ScanBackground()
        {
            List<BgApp> found = new List<BgApp>();
            IntPtr snap = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPPROCESS, 0);
            if (snap == Native.INVALID_HANDLE_VALUE) return found;
            try
            {
                PROCESSENTRY32 pe = new PROCESSENTRY32();
                pe.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                if (!Native.Process32First(snap, ref pe)) return found;
                do
                {
                    string exe = pe.szExeFile == null ? "" : pe.szExeFile.ToLowerInvariant();
                    if (exe.EndsWith(".exe")) exe = exe.Substring(0, exe.Length - 4);
                    for (int i = 0; i < BackgroundTable.GetLength(0); i++)
                    {
                        if (!string.Equals(exe, BackgroundTable[i, 0], StringComparison.Ordinal)) continue;
                        string friendly = BackgroundTable[i, 1];
                        BgApp b = null;
                        foreach (BgApp x in found) if (x.Name == friendly) { b = x; break; }
                        if (b == null)
                        {
                            b = new BgApp();
                            b.Name = friendly;
                            b.Category = BackgroundTable[i, 2];
                            b.Actionable = BackgroundTable[i, 3] == "1";
                            b.Pid = (int)pe.th32ProcessID;
                            found.Add(b);
                        }
                        b.Procs++;
                        break;
                    }
                } while (Native.Process32Next(snap, ref pe));
            }
            catch (Exception ex) { Log.Write("APPS", "background scan threw (swallowed): " + ex.Message); }
            finally { Native.CloseHandle(snap); }
            return found;
        }

        string RunningAppJson(RunningApp a)
        {
            if (a == null) return "null";
            return "{\"id\":\"" + JsonEsc(a.Id == null ? "" : a.Id) + "\""
                 + ",\"title\":\"" + JsonEsc(a.Title) + "\""
                 + ",\"kind\":\"" + JsonEsc(a.Kind == null ? "" : a.Kind) + "\""
                 + ",\"pid\":" + a.Pid
                 + ",\"tracked\":" + (a.Tracked ? "true" : "false")
                 + ",\"foreground\":" + (!ShellIsForeground() ? "true" : "false")
                 + ",\"pids\":" + AllAppPids().Count + "}";
        }

        void PublishAppState(bool force)
        {
            if (!_webReady) return;
            DateTime now = DateTime.Now;
            if (!force && (now - _appsPublishedAt).TotalMilliseconds < 4000) return;
            _appsPublishedAt = now;

            StringBuilder sb = new StringBuilder(512);
            sb.Append("{\"type\":\"apps\",\"running\":[");
            if (_running != null) sb.Append(RunningAppJson(_running));
            sb.Append("],\"background\":[");
            List<BgApp> bg = ScanBackground();
            for (int i = 0; i < bg.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("{\"name\":\"").Append(JsonEsc(bg[i].Name))
                  .Append("\",\"category\":\"").Append(bg[i].Category)
                  .Append("\",\"pid\":").Append(bg[i].Pid)
                  .Append(",\"procs\":").Append(bg[i].Procs)
                  .Append(",\"actionable\":").Append(bg[i].Actionable ? "true" : "false")
                  .Append("}");
            }
            sb.Append("]}");
            string json = sb.ToString();
            if (!force && json == _lastAppsJson) return;   // nothing changed; say nothing
            _lastAppsJson = json;
            PostToPage(json);
        }

        #endregion

        #region unattended automation  (for headless verification runs)

        void OnTick(object sender, EventArgs e)
        {
            DateTime now = DateTime.Now;

            // What is running, pushed to the page. Rate-limited inside, and silent when the
            // answer has not changed, so this is one toolhelp snapshot every four seconds.
            try { PublishAppState(false); }
            catch (Exception ex) { Log.Write("APPS", "publish threw (swallowed): " + ex.Message); }

            if (_selfTestNext != DateTime.MaxValue && _selfTestAt < _selfSeq.Length && now >= _selfTestNext)
            {
                string step = _selfSeq[_selfTestAt];
                _selfTestAt++;
                string why = "self-test step " + _selfTestAt + "/" + _selfSeq.Length;

                // Two pseudo-steps that are NOT pad actions and must not go through SendPad.
                //
                // The guide button is the one control this host handles itself, ahead of the
                // foreground gate, so it never reaches the page as an action and a walk step
                // called "guide" would exercise the control centre instead. Without these two
                // the entire app-lifecycle control is only reachable by a human thumb, and the
                // rule in this file is that anything reachable with the pad is verifiable with
                // nobody present. They enter at exactly the point a real press does - the same
                // GuideShort/GuideHold the HID decode calls - so what they prove is the real
                // behaviour and not a parallel test path.
                //
                //   ps        the short press: shell forward + guide menu
                //   ps-hold   the hold: straight to the shell, no menu
                if (step == "ps") GuideShort("--walk step 'ps' (" + why + ")");
                else if (step == "ps-hold") GuideHold("--walk step 'ps-hold' (" + why + ")");
                else if (WalkStickStep(step, why)) { /* analog: lstick:/rstick: */ }
                else if (WalkTouchStep(step, why)) { /* the touch surface: touch:/tap: */ }
                else if (!WalkPhaseStep(step, why)) SendPad(step, why);
                _selfTestNext = now.AddMilliseconds(_selfGapMs);
            }

            // Let go of whatever the last hold: step pressed. Deliberately here and not on a
            // one-shot timer: this is the same 200 ms tick everything else in the walk runs on,
            // so the release is ordered against the walk's own steps rather than racing them.
            if (_walkRelAt != DateTime.MaxValue && now >= _walkRelAt)
            {
                string a = _walkRelAction, b = _walkRelButton;
                _walkRelAt = DateTime.MaxValue; _walkRelAction = null; _walkRelButton = null;
                SendPad(a, "--walk release", b, "release");
            }

            if (_opt.AutoLaunchMs >= 0 && !_childRunning && _launchCount < _opt.Cycles)
            {
                DateTime baseline = (_launchCount == 0) ? _t0 : _returnedAt;
                if (baseline != DateTime.MinValue && (now - baseline).TotalMilliseconds >= _opt.AutoLaunchMs)
                {
                    _didAutoKill = false;
                    Log.Write("AUTO", "auto-launch fired, cycle " + (_launchCount + 1) + "/" + _opt.Cycles);
                    Launch(_opt.ChildCommand);
                }
            }

            if (_opt.AutoKillMs >= 0 && !_didAutoKill && _childRunning
                && (now - _launchedAt).TotalMilliseconds >= _opt.AutoKillMs)
            {
                _didAutoKill = true;
                KillChildTree();
            }

            if (_opt.AutoExitMs >= 0 && !_didAutoExit)
            {
                DateTime baseline = _t0;
                if (_opt.AutoLaunchMs >= 0)
                {
                    baseline = (_returnCount >= _opt.Cycles && _returnedAt != DateTime.MinValue)
                        ? _returnedAt : DateTime.MaxValue;
                }
                if (baseline != DateTime.MaxValue && (now - baseline).TotalMilliseconds >= _opt.AutoExitMs)
                {
                    _didAutoExit = true;
                    Log.Write("AUTO", "auto-exit fired");
                    ExitWith(0, "auto-exit");
                }
            }

            if (_opt.WatchdogMs > 0 && (now - _t0).TotalMilliseconds >= _opt.WatchdogMs && !_exiting)
            {
                Log.Write("AUTO", "WATCHDOG TRIPPED at " + _opt.WatchdogMs + " ms; exiting 99");
                ExitWith(99, "watchdog");
            }
        }

        void KillChildTree()
        {
            if (_tracker == null) return;
            List<int> pids = _tracker.GetTrackedPids();
            Log.Write("AUTO", "auto-kill: tracked pids = [" + string.Join(",", pids.ConvertAll<string>(
                delegate(int p) { return p.ToString(CultureInfo.InvariantCulture); }).ToArray()) + "]");
            foreach (int pid in pids)
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo("taskkill.exe", "/PID " + pid + " /T /F");
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.RedirectStandardOutput = true;
                    psi.RedirectStandardError = true;
                    Process tk = Process.Start(psi);
                    string outp = tk.StandardOutput.ReadToEnd().Trim() + " " + tk.StandardError.ReadToEnd().Trim();
                    tk.WaitForExit(5000);
                    Log.Write("AUTO", "taskkill /PID " + pid + " /T /F -> exit " + tk.ExitCode + " : " + outp.Trim());
                }
                catch (Exception ex)
                {
                    Log.Write("AUTO", "taskkill for pid " + pid + " threw: " + ex.Message);
                }
            }
            if (pids.Count == 0)
                Log.Write("AUTO", "auto-kill: NO tracked pids found - tracker has lost the tree");
        }

        #endregion
    }

    #endregion

    #region Options + entry point

    public class Options
    {
        public string ChildCommand = "notepad.exe";
        public string RawArgs = "";
        public string AssetFolder;
        public string UserDataFolder;
        public string VirtualHost = "marwanos.local";
        public string StartUrl = null;              // explicit override
        public int BootDuration = 6400;
        public bool UseFileUrls;
        public bool NoBoot;
        // --boot forces the sequence whatever the uptime says, and
        // --cold-window=<ms> moves the line between a cold start and a
        // relaunch. Both exist so both branches of ResolveStartUrl can be
        // exercised on a machine nobody is allowed to reboot.
        public bool ForceBoot;
        public int ColdWindowMs = 120000;
        // --print-url resolves the start URL, logs why, and exits without a
        // window — the same family as --haptic-test, and for the same reason:
        // the answer has to be readable over SSH on a machine that is in use.
        public bool PrintUrl;
        public bool NoJob;
        public bool NoPad;
        // Diagnostic, in the same family as --no-job and --no-pad: turn the foreground gate off
        // so pad actions reach the page even when this instance is not the front window. Only
        // useful for an unattended run sitting behind a live shell; never for a real session,
        // where a shell that acts on the pad from behind a game is the bug this gate fixes.
        public bool NoFgGate;
        // --ptr=on forces pointer mode for any foreground window that is not this instance's
        // own, which is the only way to test it in a WINDOWED host: a windowed test instance
        // sitting behind the live shell is never the foreground window, and none of the
        // engagement rules would fire for whatever is. --ptr=off disables it outright.
        public bool PtrForce;
        public bool PtrDisabled;
        public bool PadSelfTest;
        public string HapticTest;     // --haptic-test[=a,b,c]: exercise the write path, no UI
        public bool SysSelfTest;
        public bool DisplaySelfTest;
        public string Walk;
        public int WalkGapMs = 2500;
        public string Browse;               // --browse=<url>: unattended entry into the browser
        public bool Windowed;
        public bool DevTools;
        // Register the mosFiles COM host object. Off by default: its calls run on the UI thread
        // and FileApi blocks. See FileApiBridge for the whole argument.
        public bool FileHostObject;
        public int AutoLaunchMs = -1;
        public int AutoKillMs = -1;
        public int AutoExitMs = -1;
        public int WatchdogMs = 0;
        public int Cycles = 1;
        public string LogPath = null;

        // ── Cold start, or just this process starting again? ──────────────
        // The boot sequence belongs to a MACHINE starting, not to this exe
        // starting. Shell Launcher relaunches the shell every time it exits —
        // that is the redeploy path, and it is also the crash-recovery path —
        // and each of those relaunches used to replay the whole 6.4 s sequence.
        // A console that has just recovered from a crash should be back on the
        // home screen, not re-introducing itself.
        //
        // The page cannot make this call. boot.html has no host bridge, and the
        // one thing it could persist for itself (localStorage) survives a reboot
        // exactly as well as it survives a relaunch, so it cannot tell the two
        // apart. The host can, from one number: how long the OS has been up.
        // GetTickCount64 is milliseconds since the machine started — it counts
        // time spent asleep, so it tracks LastBootUpTime rather than drifting
        // away from it — and this process only exists after autologon, so a
        // small value can only mean the machine has just come up.
        //
        // 120 s, and the two failure modes are why it is generous. Too wide and
        // a redeploy done inside two minutes of a reboot plays the sequence,
        // which is what the machine was going to look like anyway. Too tight and
        // a slow cold start — an update pass, a cold disk cache, a profile that
        // takes its time — silently loses the one screen the machine has, on the
        // one occasion it is meant to appear. On this bench the shell's first
        // [HOST] line lands about a second after Shell Launcher runs it, and the
        // whole autologon path is comfortably inside the window; 120 s is
        // headroom on a measured number, not a guess about hardware.
        public string StartUrlReason = "";

        public string ResolveStartUrl()
        {
            // An explicitly requested URL always wins, so a forced boot stays
            // one flag away for testing: --url, or --boot below.
            if (!string.IsNullOrEmpty(StartUrl)){ StartUrlReason = "--url was given"; return StartUrl; }

            long upMs = (long)MarwanOs.Sys.N.GetTickCount64();
            bool cold;
            if (NoBoot){ cold = false; StartUrlReason = "--no-boot"; }
            else if (ForceBoot){ cold = true; StartUrlReason = "--boot"; }
            else
            {
                cold = upMs <= ColdWindowMs;
                StartUrlReason = "os up " + upMs.ToString(CultureInfo.InvariantCulture) + " ms, cold-start window "
                               + ColdWindowMs.ToString(CultureInfo.InvariantCulture) + " ms -> "
                               + (cold ? "COLD START, playing the boot sequence"
                                       : "SHELL RELAUNCH on a machine that is already up, straight to the shell");
            }

            if (UseFileUrls)
            {
                string f = Path.Combine(AssetFolder, cold ? "boot.html" : "index.html");
                string u = new Uri(f).AbsoluteUri;
                if (!cold) return u;
                return u + "?duration=" + BootDuration.ToString(CultureInfo.InvariantCulture) + "&next=index.html";
            }
            string b = "https://" + VirtualHost + "/";
            if (!cold) return b + "index.html";
            return b + "boot.html?duration=" + BootDuration.ToString(CultureInfo.InvariantCulture) + "&next=index.html";
        }

        public static Options Parse(string[] args)
        {
            Options o = new Options();
            o.RawArgs = string.Join(" ", args);
            o.AssetFolder = Path.GetDirectoryName(Application.ExecutablePath);
            // OnDisk.Brand, not "MarwanOS": this folder holds the browser profile, the pinned
            // sites, the cookies and every installed extension. Renaming it does not move any
            // of that - it abandons it and starts empty.
            o.UserDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                OnDisk.Brand + "\\WebView2");

            foreach (string a in args)
            {
                if (!a.StartsWith("--")) continue;
                string key = a, val = "";
                int eq = a.IndexOf('=');
                if (eq > 0) { key = a.Substring(0, eq); val = a.Substring(eq + 1); }
                if (val.Length > 1 && val[0] == '"' && val[val.Length - 1] == '"')
                    val = val.Substring(1, val.Length - 2);
                switch (key)
                {
                    case "--child": o.ChildCommand = val; break;
                    case "--assets": o.AssetFolder = Path.GetFullPath(val); break;
                    case "--user-data": o.UserDataFolder = val; break;
                    case "--virtual-host": o.VirtualHost = val; break;
                    case "--url": o.StartUrl = val; break;
                    case "--duration": o.BootDuration = int.Parse(val, CultureInfo.InvariantCulture); break;
                    case "--file-urls": o.UseFileUrls = true; break;
                    case "--no-boot": o.NoBoot = true; break;
                    case "--boot": o.ForceBoot = true; break;
                    case "--print-url": o.PrintUrl = true; break;
                    case "--cold-window": o.ColdWindowMs = int.Parse(val, CultureInfo.InvariantCulture); break;
                    case "--no-job": o.NoJob = true; break;
                    case "--no-fg-gate": o.NoFgGate = true; break;
                    case "--no-pad": o.NoPad = true; break;
                    case "--ptr":
                        o.PtrDisabled = (val == "off" || val == "0" || val == "no");
                        o.PtrForce = !o.PtrDisabled;
                        break;
                    case "--pad-selftest": o.PadSelfTest = true; break;
                    case "--haptic-test": o.HapticTest = string.IsNullOrEmpty(val) ? "*" : val; break;
                    case "--sys-selftest": o.SysSelfTest = true; break;
                    case "--display-selftest": o.DisplaySelfTest = true; break;
                    case "--walk": o.Walk = val; break;
                    // Open the browser on this URL as soon as the shell page is up, so a
                    // --walk can start from inside a real web page. The pad vocabulary
                    // cannot type an address, and driving the on-screen keyboard one key at
                    // a time to reach one is forty walk steps of nothing useful.
                    case "--browse": o.Browse = val; break;
                    case "--walk-gap": o.WalkGapMs = int.Parse(val, CultureInfo.InvariantCulture); break;
                    case "--windowed": o.Windowed = true; break;
                    case "--dev-tools": o.DevTools = true; break;
                    case "--file-host-object": o.FileHostObject = true; break;
                    case "--auto-launch": o.AutoLaunchMs = int.Parse(val, CultureInfo.InvariantCulture); break;
                    case "--auto-kill": o.AutoKillMs = int.Parse(val, CultureInfo.InvariantCulture); break;
                    case "--auto-exit": o.AutoExitMs = int.Parse(val, CultureInfo.InvariantCulture); break;
                    case "--watchdog": o.WatchdogMs = int.Parse(val, CultureInfo.InvariantCulture); break;
                    case "--cycles": o.Cycles = int.Parse(val, CultureInfo.InvariantCulture); break;
                    case "--log": o.LogPath = val; break;
                }
            }
            return o;
        }
    }

    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            Options opt = Options.Parse(args);
            Log.Init(opt.LogPath);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Log.Write("FATAL", "unhandled: " + (e.ExceptionObject == null ? "(null)" : e.ExceptionObject.ToString()));
            };

            if (!string.IsNullOrEmpty(opt.HapticTest)) return HapticTest(opt);

            if (opt.PrintUrl)
            {
                string u0 = opt.ResolveStartUrl();
                Log.Write("NAV", "start url decided: " + opt.StartUrlReason);
                Log.Write("NAV", "would navigate to " + u0);
                try { Console.Out.WriteLine(opt.StartUrlReason); Console.Out.WriteLine(u0); } catch { }
                return 0;
            }

            HostForm f = new HostForm(opt);
            try
            {
                Application.Run(f);
            }
            catch (Exception ex)
            {
                Log.Write("FATAL", ex.ToString());
                return 100;
            }
            return f.ExitCodeValue;
        }

        /// <summary>
        /// --haptic-test[=move,activate,...]  — proves the output-report path without a UI.
        ///
        /// Haptics cannot be verified by reading a log line that says "sent". What CAN be
        /// proved from a log is the thing that actually goes wrong: a malformed output report
        /// makes a DualSense stop streaming input. So this counts input reports either side of
        /// every effect. Input still flowing after a write means the pad accepted the report;
        /// input stopping dead is the unmistakable signature of a bad one.
        ///
        /// It never starts a WebView or a window, so it runs over SSH in session 0, and it
        /// leaves the motors at zero on every exit path.
        /// </summary>
        static int HapticTest(Options opt)
        {
            string[] effects = opt.HapticTest == "*"
                ? new string[] { "move", "nudge", "push", "pop", "tab", "toggle",
                                 "back", "activate", "launch", "error", "bootDone" }
                : opt.HapticTest.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

            Log.Write("HTEST", "=== haptic write-path test: " + string.Join(",", effects) + " ===");
            DualSense ds = new DualSense();
            DualSenseHaptics hap = new DualSenseHaptics(ds);
            int rc = 0;
            try
            {
                ds.Start();

                // Wait for the reader to see a real report, not just an open handle: the
                // transport is only known once a report has arrived, and the transport decides
                // whether the output report is 48 bytes or 78 with a CRC.
                PadSnapshot s = null;
                for (int i = 0; i < 100; i++)
                {
                    s = ds.Snapshot;
                    if (s != null && s.Connected && s.Reports > 0) break;
                    Thread.Sleep(100);
                }
                s = ds.Snapshot;
                if (s == null || !s.Connected || s.Reports <= 0)
                {
                    Log.Write("HTEST", "FAIL: no pad streaming after 10 s (status='"
                        + (s == null ? "?" : s.Status) + "')");
                    return 2;
                }
                Log.Write("HTEST", "pad: " + s.Model + " over " + s.Transport
                    + " reportId=0x" + s.ReportId.ToString("X2") + " inputLen=" + s.ReportLength
                    + " reports=" + s.Reports);

                hap.Intensity = 1.0;
                hap.Start();
                Thread.Sleep(300);

                long baseline = ds.Snapshot.Reports;
                Thread.Sleep(500);
                long idleRate = ds.Snapshot.Reports - baseline;
                Log.Write("HTEST", "idle input rate: " + idleRate + " reports in 500 ms (before any write)");

                foreach (string e in effects)
                {
                    string name = e.Trim();
                    if (!DualSenseHaptics.Known(name)) { Log.Write("HTEST", "skip unknown '" + name + "'"); continue; }

                    long before = ds.Snapshot.Reports;
                    long wBefore = hap.Writes, fBefore = hap.WriteFailures;
                    hap.Play(name);
                    Thread.Sleep(700);          // longer than the longest effect
                    long after = ds.Snapshot.Reports;
                    long wrote = hap.Writes - wBefore;
                    long failed = hap.WriteFailures - fBefore;

                    bool alive = (after - before) > 5;
                    if (!alive) rc = 3;
                    Log.Write("HTEST", (alive ? "OK   " : "DEAD ") + name.PadRight(9)
                        + " writes=" + wrote + " failures=" + failed
                        + " lastErr=" + hap.LastError
                        + " inputReports during=" + (after - before)
                        + (alive ? "" : "   <-- the pad STOPPED streaming: bad output report"));
                    Thread.Sleep(400);
                }

                Log.Write("HTEST", "final: ready=" + hap.Ready + " transport=" + hap.Transport
                    + " outLen=" + hap.OutputLength + " totalWrites=" + hap.Writes
                    + " totalFailures=" + hap.WriteFailures + " status='" + hap.Status + "'");

                long endBase = ds.Snapshot.Reports;
                Thread.Sleep(500);
                long endRate = ds.Snapshot.Reports - endBase;
                Log.Write("HTEST", "input rate after every write: " + endRate
                    + " reports in 500 ms (was " + idleRate + " before)");
                if (endRate < 5) { Log.Write("HTEST", "FAIL: the input stream did not survive the test"); rc = 3; }
                if (hap.Writes == 0) { Log.Write("HTEST", "FAIL: not a single output report was accepted"); rc = 4; }
                Log.Write("HTEST", rc == 0 ? "=== PASS ===" : "=== FAIL (rc=" + rc + ") ===");
            }
            catch (Exception ex)
            {
                Log.Write("HTEST", "threw: " + ex.ToString());
                rc = 5;
            }
            finally
            {
                try { hap.Stop(); } catch { }
                try { ds.Stop(); } catch { }
                Thread.Sleep(200);
            }
            return rc;
        }
    }

    #endregion
}
