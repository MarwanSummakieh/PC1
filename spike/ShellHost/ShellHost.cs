// MarwanOS - ShellHost spike
// Minimal native host proving (a) window handoff to a child process tree and
// reliable forced return to foreground, and (b) XInput gamepad input.
//
// Language level: C# 5 (built with the inbox .NET Framework 4.x csc.exe).
// Do not use string interpolation, nameof, expression-bodied members, etc.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace MarwanOs.Spike
{
    #region Native interop

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
        [DllImport("user32.dll")]
        public static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int nMaxCount);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        public const int SW_HIDE = 0;
        public const int SW_SHOWNORMAL = 1;
        public const int SW_SHOWMINIMIZED = 2;
        public const int SW_MINIMIZE = 6;
        public const int SW_SHOW = 5;
        public const int SW_RESTORE = 9;
        public const int SW_SHOWMAXIMIZED = 3;

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
        public const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;

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
        public const uint JOB_OBJECT_MSG_END_OF_JOB_TIME = 1;
        public const uint JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO = 4;
        public const uint JOB_OBJECT_MSG_NEW_PROCESS = 6;
        public const uint JOB_OBJECT_MSG_EXIT_PROCESS = 7;
        public const uint JOB_OBJECT_MSG_ABNORMAL_EXIT_PROCESS = 8;

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

        // --- raw HID (DualSense is NOT an XInput device, so XInput never sees it) ---
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
        public static extern bool CancelIoEx(IntPtr hFile, IntPtr overlapped);

        public const int DIGCF_PRESENT = 0x0002;
        public const int DIGCF_DEVICEINTERFACE = 0x0010;
        public const uint GENERIC_READ = 0x80000000;
        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint FILE_SHARE_WRITE = 0x00000002;
        public const uint OPEN_EXISTING = 3;
    }

    #endregion

    #region Logging

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
            }
            else
            {
                // Walk up from the exe looking for a directory named "spike".
                string dir = System.IO.Path.GetDirectoryName(Application.ExecutablePath);
                string found = null;
                DirectoryInfo d = new DirectoryInfo(dir);
                while (d != null)
                {
                    if (string.Equals(d.Name, "spike", StringComparison.OrdinalIgnoreCase))
                    {
                        found = d.FullName;
                        break;
                    }
                    d = d.Parent;
                }
                if (found == null) found = dir;
                _path = System.IO.Path.Combine(found, "handoff-log.txt");
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

                int stick, btn, trig, btn2Mask;
                string transport;
                if (!SelectLayout(family, buf[0], len, out stick, out btn, out trig, out btn2Mask, out transport))
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
        static bool SelectLayout(int family, byte reportId, int length,
            out int stick, out int btn, out int trig, out int btn2Mask, out string transport)
        {
            stick = 0; btn = 0; trig = 0; btn2Mask = 0; transport = "";
            if (family == FAMILY_DS5)
            {
                if (reportId == 0x31 && length >= 12)
                {
                    // Bluetooth full report: the wired payload shifted by +1.
                    stick = 2; btn = 9; trig = 6; btn2Mask = 0x07; transport = "BT";
                    return true;
                }
                if (reportId == 0x01 && length <= 64 && length >= 11)
                {
                    // Wired full report. Verified byte-by-byte on the bench.
                    stick = 1; btn = 8; trig = 5; btn2Mask = 0x07; transport = "USB";
                    return true;
                }
                if (reportId == 0x01 && length > 64)
                {
                    // Bluetooth compatibility report. Verified on the dev box.
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
    }

    #endregion

    #region Child process tree tracker

    public enum TrackingMode { None, Job, Poll }

    /// <summary>
    /// Launches a child and watches its whole process tree.
    /// Primary mechanism: Win32 job object + IO completion port (JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO).
    /// Fallback: CreateToolhelp32Snapshot descendant walk.
    /// </summary>
    public class ChildTracker
    {
        IntPtr _job = IntPtr.Zero;
        IntPtr _port = IntPtr.Zero;
        Thread _watcher;
        volatile bool _stop;

        public int RootPid { get; private set; }
        public TrackingMode Mode { get; private set; }
        public string JobFailureReason = "";

        /// <summary>Raised (on a background thread) when the tracked tree becomes empty.</summary>
        public event Action TreeEmpty;

        public bool Start(string commandLine, bool forceNoJob)
        {
            Mode = TrackingMode.None;
            _stop = false;

            bool jobReady = false;
            if (!forceNoJob)
            {
                _job = Native.CreateJobObject(IntPtr.Zero, null);
                if (_job == IntPtr.Zero)
                {
                    JobFailureReason = "CreateJobObject failed, err=" + Marshal.GetLastWin32Error();
                }
                else
                {
                    _port = Native.CreateIoCompletionPort(Native.INVALID_HANDLE_VALUE, IntPtr.Zero, UIntPtr.Zero, 1);
                    if (_port == IntPtr.Zero)
                    {
                        JobFailureReason = "CreateIoCompletionPort failed, err=" + Marshal.GetLastWin32Error();
                    }
                    else
                    {
                        JOBOBJECT_ASSOCIATE_COMPLETION_PORT assoc = new JOBOBJECT_ASSOCIATE_COMPLETION_PORT();
                        assoc.CompletionKey = _job;
                        assoc.CompletionPort = _port;
                        int size = Marshal.SizeOf(typeof(JOBOBJECT_ASSOCIATE_COMPLETION_PORT));
                        IntPtr buf = Marshal.AllocHGlobal(size);
                        try
                        {
                            Marshal.StructureToPtr(assoc, buf, false);
                            if (!Native.SetInformationJobObject(_job, Native.JobObjectAssociateCompletionPortInformation, buf, (uint)size))
                                JobFailureReason = "SetInformationJobObject(AssociateCompletionPort) failed, err=" + Marshal.GetLastWin32Error();
                            else
                                jobReady = true;
                        }
                        finally { Marshal.FreeHGlobal(buf); }
                    }
                }
            }
            else
            {
                JobFailureReason = "--no-job specified (fallback path forced for testing)";
            }

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
            // Descendant walk. Note the known weakness this fallback has and the job object does not:
            // once an intermediate launcher exits, its children's PPID becomes stale/reused, so a
            // "launcher spawns game then exits" chain can be lost. Reported honestly in the results.
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

        public static List<int> GetDescendants(int rootPid)
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

            if (parents.ContainsKey(rootPid)) result.Add(rootPid);
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

        /// <summary>Current PIDs in the tracked tree (job PID list, or descendant walk).</summary>
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
        }
    }

    #endregion

    #region Foreground restoration

    public static class Foreground
    {
        /// <summary>
        /// Restore + force to foreground, escalating through documented workarounds.
        /// Returns the name of the code path that succeeded, or null on total failure.
        /// </summary>
        public static string ForceForeground(IntPtr hwnd)
        {
            Native.ShowWindow(hwnd, Native.SW_RESTORE);

            if (Try(hwnd)) return "path0:SW_RESTORE+BringWindowToTop+SetForegroundWindow";

            // Path 1: documented ALT keypress workaround - a synthetic key event makes this
            // thread the "last input" owner, which lifts the foreground lock.
            Log.Write("FOREGROUND", "path0 failed (fg=" + Describe(Native.GetForegroundWindow()) + "), trying ALT-key workaround");
            Native.keybd_event(Native.VK_MENU, 0, 0, UIntPtr.Zero);
            Native.keybd_event(Native.VK_MENU, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
            if (Try(hwnd)) return "path1:keybd_event(ALT down/up)+SetForegroundWindow";

            // Path 2: AttachThreadInput to the current foreground window's thread.
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

            // Path 3: combined - attach AND alt key.
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

            // Path 4: last resort - transient TOPMOST toggle. We do NOT stay topmost
            // (hard requirement: must not fight games) and we do NOT touch
            // SPI_SETFOREGROUNDLOCKTIMEOUT, which would be a system setting change.
            Log.Write("FOREGROUND", "path3 failed, trying transient topmost toggle");
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_SHOWWINDOW);
            Native.SetWindowPos(hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_SHOWWINDOW);
            if (Try(hwnd)) return "path4:transient HWND_TOPMOST toggle (reverted to NOTOPMOST)";

            // Path 5: minimize/restore cycle.
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
            // Give the shell a moment to actually apply the activation before verifying.
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
    }

    #endregion

    #region Host form

    public class HostForm : Form
    {
        // Buttons per XINPUT_GAMEPAD_*
        const ushort BTN_A = 0x1000;
        const ushort BTN_B = 0x2000;
        const ushort BTN_X = 0x4000;
        const ushort BTN_Y = 0x8000;
        const ushort BTN_GUIDE = 0x0400;

        readonly Options _opt;
        readonly XInput _xi = new XInput();
        readonly DualSense _ds = new DualSense();
        readonly Label _status = new Label();
        readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();

        ChildTracker _tracker;
        bool _childRunning;
        int _exitCode;
        bool _exiting;

        DateTime _t0;
        DateTime _launchedAt = DateTime.MinValue;
        DateTime _returnedAt = DateTime.MinValue;
        bool _didAutoKill, _didAutoExit;
        int _returnsPassed, _returnsFailed;
        readonly List<string> _pathsUsed = new List<string>();

        ushort _lastButtons;
        uint _connectedMask;
        string _padText = "no gamepad polled yet";
        string _lastDsTransport = "";
        bool _lastDsConnected;
        string _lastPadPress = "(none yet)";
        int _padPressCount;
        string _lastFgPath = "(none yet)";
        string _lastVerify = "(no return yet)";
        int _launchCount, _returnCount;

        public int ExitCodeValue { get { return _exitCode; } }

        public HostForm(Options opt)
        {
            _opt = opt;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            BackColor = ColorTranslator.FromHtml("#04060B");
            TopMost = false;                    // hard requirement: never fight games
            ShowInTaskbar = true;               // needed so restore-from-minimized behaves
            KeyPreview = true;
            Text = "MarwanOS ShellHost (spike)";
            DoubleBuffered = true;

            Rectangle b = Screen.PrimaryScreen.Bounds;
            if (_opt.Windowed) b = new Rectangle(b.X + 80, b.Y + 80, 1000, 620);
            Bounds = b;

            _status.AutoSize = false;
            _status.Dock = DockStyle.Fill;
            _status.Padding = new Padding(28, 24, 24, 24);
            _status.Font = new Font("Consolas", 11f, FontStyle.Regular);
            _status.ForeColor = Color.FromArgb(200, 214, 236);
            _status.BackColor = Color.Transparent;
            _status.Text = "starting...";
            Controls.Add(_status);

            KeyDown += OnKeyDown;
            Load += OnLoadForm;
            FormClosing += OnClosingForm;
        }

        void OnLoadForm(object sender, EventArgs e)
        {
            _t0 = DateTime.Now;
            Log.Write("HOST", "=========================================================");
            Log.Write("HOST", "ShellHost started. args='" + _opt.RawArgs + "'");
            Log.Write("HOST", "child command = '" + _opt.ChildCommand + "'");
            Log.Write("HOST", "hwnd=0x" + Handle.ToInt64().ToString("X") + " pid=" + Process.GetCurrentProcess().Id
                + " bounds=" + Bounds + " topmost=" + TopMost);

            _xi.Load();

            // A DualSense is not an XInput device, so XInput will report nothing for it.
            // The raw HID reader below is what actually sees the pad.
            Log.Write("PAD", "starting raw HID reader thread (DualSense / DualShock)");
            _ds.Start();

            _timer.Interval = 16;   // ~60 Hz
            _timer.Tick += OnTick;
            _timer.Start();

            // Take foreground on startup so keyboard fallbacks work unattended.
            string p = Foreground.ForceForeground(Handle);
            Log.Write("FOREGROUND", "startup activation via " + (p == null ? "FAILED" : p));
        }

        void OnClosingForm(object sender, FormClosingEventArgs e)
        {
            _timer.Stop();
            _ds.Stop();
            if (_tracker != null) _tracker.Cleanup();
            Log.Write("HOST", "ShellHost exiting with code " + _exitCode);
        }

        #region input

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) DoAction("ENTER");
            else if (e.KeyCode == Keys.Escape) DoAction("ESC");
            else if (e.KeyCode == Keys.D2) DoAction("X");
            else if (e.KeyCode == Keys.D3) DoAction("Y");
        }

        void DoAction(string action)
        {
            Log.Write("ACTION", "action '" + action + "' triggered");
            switch (action)
            {
                case "A":
                case "ENTER":
                case "CROSS":
                    Launch();
                    break;
                case "B":
                case "ESC":
                case "CIRCLE":
                    ExitWith(0, action);
                    break;
                case "X":
                case "SQUARE":
                    ExitWith(2, action);
                    break;
                case "Y":
                case "TRIANGLE":
                    ExitWith(3, action);
                    break;
            }
        }

        void ExitWith(int code, string why)
        {
            if (_exiting) return;
            _exiting = true;
            _exitCode = code;
            Log.Write("EXIT", "exiting with code " + code + " (trigger=" + why + ")");
            _timer.Stop();
            Close();
        }

        #endregion

        #region launch / return

        void Launch()
        {
            if (_childRunning) { Log.Write("LAUNCH", "ignored - child already running"); return; }

            _launchCount++;
            _launchedAt = DateTime.Now;
            _childRunning = true;

            _tracker = new ChildTracker();
            _tracker.TreeEmpty += OnTreeEmpty;

            Log.Write("LAUNCH", "launching child, then yielding the screen...");
            if (!_tracker.Start(_opt.ChildCommand, _opt.NoJob))
            {
                _childRunning = false;
                Log.Write("LAUNCH", "launch FAILED - staying in foreground");
                return;
            }

            // Get out of the way.
            Native.ShowWindow(Handle, Native.SW_MINIMIZE);
            Log.Write("HANDOFF", "host minimized (SW_MINIMIZE); tracking mode=" + _tracker.Mode
                + " rootPid=" + _tracker.RootPid);
        }

        void OnTreeEmpty()
        {
            // Called on the tracker's background thread.
            try
            {
                if (IsHandleCreated) BeginInvoke(new Action(OnChildTreeGone));
            }
            catch { }
        }

        void OnChildTreeGone()
        {
            if (!_childRunning) return;
            _childRunning = false;
            _returnCount++;
            DateTime detected = DateTime.Now;
            Log.Write("RETURN", "child tree empty detected; beginning forced-foreground return");
            Log.Write("RETURN", "foreground before restore: " + Foreground.Describe(Native.GetForegroundWindow()));

            WindowState = FormWindowState.Normal;
            Stopwatch sw = Stopwatch.StartNew();
            string path = Foreground.ForceForeground(Handle);
            sw.Stop();

            bool ok = Native.GetForegroundWindow() == Handle;
            if (ok) _returnsPassed++; else _returnsFailed++;
            _pathsUsed.Add("c" + _returnCount + ":" + (path == null ? "FAILED" : path));
            _lastFgPath = (path == null ? "ALL PATHS FAILED" : path) + "  (" + sw.ElapsedMilliseconds + " ms)";
            _lastVerify = ok
                ? "PASS GetForegroundWindow()==host hwnd (0x" + Handle.ToInt64().ToString("X") + ")"
                : "FAIL foreground is " + Foreground.Describe(Native.GetForegroundWindow());

            Log.Write("RETURN", "forced-foreground path: " + _lastFgPath);
            Log.Write("VERIFY", _lastVerify);

            if (_tracker != null) { _tracker.Cleanup(); _tracker = null; }
            _returnedAt = detected;

            // Re-poll the gamepad after return (device may have been grabbed/released by the child).
            _connectedMask = 0;
            PollPads();
            Log.Write("XINPUT", "re-poll after return: " + _padText);
            Log.Write("PAD", "raw HID pad after return: " + PadLine());
        }

        #endregion

        #region polling loop

        void OnTick(object sender, EventArgs e)
        {
            PollPads();
            PollDualSense();
            RunAutomation();
            string txt = BuildStatus();
            if (_status.Text != txt) _status.Text = txt;
        }

        void PollPads()
        {
            uint mask = 0;
            ushort buttons = 0;
            StringBuilder sb = new StringBuilder();
            bool any = false;
            for (uint i = 0; i < 4; i++)
            {
                XINPUT_STATE st;
                uint res = _xi.ExAvailable ? _xi.GetStateEx(i, out st) : _xi.GetState(i, out st);
                if (res == 0)
                {
                    any = true;
                    mask |= (1u << (int)i);
                    if (buttons == 0) buttons = st.Gamepad.wButtons;
                    sb.Append("slot" + i + " CONNECTED  pkt=" + st.dwPacketNumber
                        + "  btn=0x" + st.Gamepad.wButtons.ToString("X4") + " [" + ButtonNames(st.Gamepad.wButtons) + "]"
                        + "  LT=" + st.Gamepad.bLeftTrigger + " RT=" + st.Gamepad.bRightTrigger
                        + "  LX=" + st.Gamepad.sThumbLX + " LY=" + st.Gamepad.sThumbLY
                        + "  RX=" + st.Gamepad.sThumbRX + " RY=" + st.Gamepad.sThumbRY + "\n                 ");
                }
            }
            if (!any) sb.Append("no controller in slots 0-3 (all return ERROR_DEVICE_NOT_CONNECTED)");
            _padText = sb.ToString().TrimEnd();

            if (mask != _connectedMask)
            {
                Log.Write("XINPUT", "connection mask changed 0x" + _connectedMask.ToString("X") + " -> 0x" + mask.ToString("X"));
                _connectedMask = mask;
            }

            // Edge-triggered button handling.
            ushort pressed = (ushort)(buttons & ~_lastButtons);
            _lastButtons = buttons;
            if (pressed != 0)
            {
                Log.Write("XINPUT", "buttons pressed: 0x" + pressed.ToString("X4") + " [" + ButtonNames(pressed) + "]");
                if ((pressed & BTN_A) != 0) DoAction("A");
                else if ((pressed & BTN_B) != 0) DoAction("B");
                else if ((pressed & BTN_X) != 0) DoAction("X");
                else if ((pressed & BTN_Y) != 0) DoAction("Y");
                if ((pressed & BTN_GUIDE) != 0) Log.Write("XINPUT", "GUIDE button observed (only visible via XInputGetStateEx)");
            }
        }

        static string ButtonNames(ushort b)
        {
            if (b == 0) return "";
            List<string> n = new List<string>();
            if ((b & 0x0001) != 0) n.Add("DUP");
            if ((b & 0x0002) != 0) n.Add("DDOWN");
            if ((b & 0x0004) != 0) n.Add("DLEFT");
            if ((b & 0x0008) != 0) n.Add("DRIGHT");
            if ((b & 0x0010) != 0) n.Add("START");
            if ((b & 0x0020) != 0) n.Add("BACK");
            if ((b & 0x0040) != 0) n.Add("LTHUMB");
            if ((b & 0x0080) != 0) n.Add("RTHUMB");
            if ((b & 0x0100) != 0) n.Add("LB");
            if ((b & 0x0200) != 0) n.Add("RB");
            if ((b & 0x0400) != 0) n.Add("GUIDE");
            if ((b & 0x1000) != 0) n.Add("A");
            if ((b & 0x2000) != 0) n.Add("B");
            if ((b & 0x4000) != 0) n.Add("X");
            if ((b & 0x8000) != 0) n.Add("Y");
            return string.Join("+", n.ToArray());
        }

        /// <summary>
        /// Consumes the raw-HID pad on the UI thread: logs connect/disconnect transitions and
        /// dispatches the debounced press edges latched by the reader thread. Debouncing is
        /// edge-only by construction - a held button latches exactly one edge.
        /// </summary>
        void PollDualSense()
        {
            PadSnapshot s = _ds.Snapshot;

            if (s.Connected != _lastDsConnected)
            {
                _lastDsConnected = s.Connected;
                if (s.Connected)
                    Log.Write("PAD", "pad CONNECTED: " + s.Model + " VID=0x" + s.Vid.ToString("X4")
                        + " PID=0x" + s.Pid.ToString("X4"));
                else
                {
                    Log.Write("PAD", "pad DISCONNECTED - rescanning");
                    _lastDsTransport = "";
                }
            }
            if (s.Connected && s.Transport != _lastDsTransport && s.Transport != "?")
            {
                _lastDsTransport = s.Transport;
                Log.Write("PAD", "transport now " + s.Transport + " (report id 0x" + s.ReportId.ToString("X2")
                    + ", " + s.ReportLength + " bytes)");
            }

            int pressed = _ds.TakePressEdges();
            if (pressed == 0) return;

            _padPressCount++;
            _lastPadPress = DualSense.ButtonNames(pressed) + "  (0x" + pressed.ToString("X5") + ")";
            Log.Write("PAD", "button press edge: 0x" + pressed.ToString("X5")
                + " [" + DualSense.ButtonNames(pressed) + "]"
                + "  LX=" + s.LX + " LY=" + s.LY + " RX=" + s.RX + " RY=" + s.RY);

            // The PS/Guide button is reserved for the future home overlay - never an exit.
            if ((pressed & DualSense.BTN_PS) != 0)
                Log.Write("PAD", "PS (Guide) button pressed - reserved for the future guide/home action, no exit");

            // Safety: while a child owns the screen the host is minimized and the human is
            // playing, so a stray Circle/Square/Triangle must not restart or shut down the
            // machine. Keyboard bindings are implicitly foreground-gated by WinForms focus;
            // raw HID is not, so gate it explicitly here.
            int actionable = pressed & (DualSense.BTN_CROSS | DualSense.BTN_CIRCLE
                | DualSense.BTN_SQUARE | DualSense.BTN_TRIANGLE);
            if (actionable != 0 && _childRunning)
            {
                Log.Write("PAD", "pad action [" + DualSense.ButtonNames(actionable)
                    + "] ignored - child is running and owns the screen");
                return;
            }

            if ((pressed & DualSense.BTN_CROSS) != 0) DoAction("CROSS");
            else if ((pressed & DualSense.BTN_CIRCLE) != 0) DoAction("CIRCLE");
            else if ((pressed & DualSense.BTN_SQUARE) != 0) DoAction("SQUARE");
            else if ((pressed & DualSense.BTN_TRIANGLE) != 0) DoAction("TRIANGLE");
        }

        string PadLine()
        {
            PadSnapshot s = _ds.Snapshot;
            if (!s.Connected) return "no pad  (raw HID scan found no DualSense / DualShock)";
            string t = (s.Transport == "?") ? "detecting transport" : s.Transport;
            return s.Model + " (" + t + ")  id=0x" + s.ReportId.ToString("X2")
                + " len=" + s.ReportLength + "  reports=" + s.Reports;
        }

        #endregion

        #region unattended automation

        void RunAutomation()
        {
            DateTime now = DateTime.Now;

            // Launch cycle N: first cycle is timed from startup, later cycles from the previous return.
            if (_opt.AutoLaunchMs >= 0 && !_childRunning && _launchCount < _opt.Cycles)
            {
                DateTime baseline = (_launchCount == 0) ? _t0 : _returnedAt;
                if (baseline != DateTime.MinValue && (now - baseline).TotalMilliseconds >= _opt.AutoLaunchMs)
                {
                    _didAutoKill = false;
                    Log.Write("AUTO", "auto-launch fired, cycle " + (_launchCount + 1) + "/" + _opt.Cycles
                        + " (simulating " + _opt.LaunchAs + ")");
                    DoAction(_opt.LaunchAs);
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
                    // Wait until every requested cycle has completed its return.
                    baseline = (_returnCount >= _opt.Cycles && _returnedAt != DateTime.MinValue)
                        ? _returnedAt : DateTime.MaxValue;
                }
                if (baseline != DateTime.MaxValue && (now - baseline).TotalMilliseconds >= _opt.AutoExitMs)
                {
                    _didAutoExit = true;
                    Log.Write("AUTO", "auto-exit fired (simulating " + _opt.ExitAs + ")");
                    Log.Write("SUMMARY", "cycles launched=" + _launchCount + " returned=" + _returnCount
                        + " foregroundPASS=" + _returnsPassed + " foregroundFAIL=" + _returnsFailed
                        + " paths=[" + string.Join(" | ", _pathsUsed.ToArray()) + "]");
                    DoAction(_opt.ExitAs);
                }
            }

            if (_opt.WatchdogMs > 0 && (now - _t0).TotalMilliseconds >= _opt.WatchdogMs && !_exiting)
            {
                Log.Write("AUTO", "WATCHDOG TRIPPED at " + _opt.WatchdogMs + " ms - test did not complete; exiting 99");
                ExitWith(99, "watchdog");
            }
        }

        void KillChildTree()
        {
            if (_tracker == null) return;
            List<int> pids = _tracker.GetTrackedPids();
            Log.Write("AUTO", "auto-kill: tracked pids = [" + string.Join(",", pids.ConvertAll<string>(
                delegate(int p) { return p.ToString(); }).ToArray()) + "]");
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

        string BuildStatus()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("MarwanOS  //  ShellHost spike");
            sb.AppendLine("----------------------------------------------------------------------");
            sb.AppendLine("state            : " + (_childRunning ? "CHILD RUNNING (host yielded)" : "IDLE / FOREGROUND"));
            sb.AppendLine("hwnd             : 0x" + Handle.ToInt64().ToString("X") + "   pid " + Process.GetCurrentProcess().Id
                + "   topmost " + TopMost);
            sb.AppendLine("child command    : " + _opt.ChildCommand);
            sb.AppendLine("tracking         : " + (_tracker == null ? "(idle)" : _tracker.Mode.ToString()
                + (_tracker.Mode == TrackingMode.Poll ? "  [" + _tracker.JobFailureReason + "]" : "")
                + "  rootPid=" + _tracker.RootPid));
            sb.AppendLine("launches/returns : " + _launchCount + " / " + _returnCount);
            sb.AppendLine("last fg path     : " + _lastFgPath);
            sb.AppendLine("fg verification  : " + _lastVerify);
            sb.AppendLine();
            sb.AppendLine("xinput module    : " + _xi.ModuleName + "   XInputGetState " + (_xi.Available ? "OK" : "MISSING"));
            sb.AppendLine("GetStateEx (#100): " + _xi.ExStatus);
            sb.AppendLine("xinput pads      : " + _padText);
            sb.AppendLine();
            PadSnapshot ds = _ds.Snapshot;
            sb.AppendLine("pad (raw HID)    : " + PadLine());
            if (ds.Connected)
            {
                sb.AppendLine("pad buttons      : " + (ds.Buttons == 0
                    ? "(none held)"
                    : DualSense.ButtonNames(ds.Buttons) + "   0x" + ds.Buttons.ToString("X5")));
                sb.AppendLine("pad sticks       : L " + ds.LX.ToString().PadLeft(3) + "," + ds.LY.ToString().PadLeft(3)
                    + "    R " + ds.RX.ToString().PadLeft(3) + "," + ds.RY.ToString().PadLeft(3)
                    + "    triggers L2=" + ds.L2 + " R2=" + ds.R2 + "   (centre is ~128)");
                sb.AppendLine("pad presses      : " + _padPressCount + "   last: " + _lastPadPress);
            }
            sb.AppendLine();
            sb.AppendLine("launch child     : Enter key   or pad CROSS");
            sb.AppendLine("exit 0  (restart shell)  : Esc key   or pad CIRCLE");
            sb.AppendLine("exit 2  (restart device) : '2' key   or pad SQUARE");
            sb.AppendLine("exit 3  (shut down)      : '3' key   or pad TRIANGLE");
            sb.AppendLine("pad PS button    : logged only (reserved for the guide/home overlay)");
            sb.AppendLine();
            sb.AppendLine("log: " + Log.Path);
            sb.AppendLine("uptime: " + (DateTime.Now - _t0).TotalSeconds.ToString("F1") + " s");
            return sb.ToString();
        }
    }

    #endregion

    #region Options + entry point

    public class Options
    {
        public string ChildCommand = "notepad.exe";
        public string RawArgs = "";
        public bool NoJob;
        public bool Windowed;
        public int AutoLaunchMs = -1;
        public int AutoKillMs = -1;
        public int AutoExitMs = -1;
        public int WatchdogMs = 0;
        public int Cycles = 1;
        public string LaunchAs = "ENTER";  // ENTER or A
        public string ExitAs = "ESC";      // ESC / B / X / Y
        public string LogPath = null;

        public static Options Parse(string[] args)
        {
            Options o = new Options();
            o.RawArgs = string.Join(" ", args);
            List<string> positional = new List<string>();
            bool childExplicit = false;
            foreach (string a in args)
            {
                if (a.StartsWith("--"))
                {
                    string key = a, val = "";
                    int eq = a.IndexOf('=');
                    if (eq > 0) { key = a.Substring(0, eq); val = a.Substring(eq + 1); }
                    if (val.Length > 1 && val[0] == '"' && val[val.Length - 1] == '"')
                        val = val.Substring(1, val.Length - 2);
                    switch (key)
                    {
                        case "--child": o.ChildCommand = val; childExplicit = true; break;
                        case "--no-job": o.NoJob = true; break;
                        case "--windowed": o.Windowed = true; break;
                        case "--auto-launch": o.AutoLaunchMs = int.Parse(val); break;
                        case "--auto-kill": o.AutoKillMs = int.Parse(val); break;
                        case "--auto-exit": o.AutoExitMs = int.Parse(val); break;
                        case "--watchdog": o.WatchdogMs = int.Parse(val); break;
                        case "--cycles": o.Cycles = int.Parse(val); break;
                        case "--launch-as": o.LaunchAs = val.ToUpperInvariant(); break;
                        case "--exit-as": o.ExitAs = val.ToUpperInvariant(); break;
                        case "--log": o.LogPath = val; break;
                    }
                }
                else positional.Add(a);
            }
            // Positional form: ShellHost.exe <child command line>.
            // Never let stray positionals clobber an explicit --child= (that bug cost a test run).
            if (positional.Count > 0 && !childExplicit)
                o.ChildCommand = string.Join(" ", positional.ToArray());
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
    }

    #endregion
}
