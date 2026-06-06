using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ResizeWidth
{
    internal static class NativeMethods
    {
        // ── Window enumeration ──────────────────────────────────────────────

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern long GetWindowLong(IntPtr hWnd, int nIndex);

        public const int GWL_STYLE    = -16;
        public const int GWL_EXSTYLE  = -20;
        public const long WS_CHILD       = 0x40000000L;
        public const long WS_CAPTION     = 0x00C00000L;
        public const long WS_THICKFRAME  = 0x00040000L;
        public const long WS_EX_TOOLWINDOW  = 0x00000080L;
        public const long WS_EX_APPWINDOW   = 0x00040000L;

        // ── DWM (cloaked windows) ──────────────────────────────────────────
        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, uint dwAttribute,
            out int pvAttribute, int cbAttribute);

        public const uint DWMWA_CLOAKED = 14;

        // ── Process name ───────────────────────────────────────────────────
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        public const uint GW_HWNDNEXT = 2;

        // ── Window placement / size ────────────────────────────────────────
        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        public const uint SWP_NOZORDER    = 0x0004;
        public const uint SWP_NOACTIVATE  = 0x0010;

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        public const int SW_RESTORE = 9;

        [DllImport("user32.dll")]
        public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWPLACEMENT
        {
            public uint  length;
            public uint  flags;
            public uint  showCmd;
            public POINT ptMinPosition;
            public POINT ptMaxPosition;
            public RECT  rcNormalPosition;
        }

        public const uint SW_SHOWMAXIMIZED = 3;

        // ── Monitor ───────────────────────────────────────────────────────
        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        public const uint MONITOR_DEFAULTTONEAREST = 2;
        public const uint MONITOR_DEFAULTTONULL    = 0;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFO
        {
            public uint cbSize;
            public RECT rcMonitor;   // full monitor rect
            public RECT rcWork;      // working area (excluding taskbar)
            public uint dwFlags;
        }

        // ── RECT / POINT ──────────────────────────────────────────────────
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width  => Right  - Left;
            public int Height => Bottom - Top;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        // ── Icon retrieval (best-effort) ──────────────────────────────────
        [DllImport("user32.dll")]
        public static extern IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex);

        public const int GCL_HICON      = -14;
        public const int GCL_HICONSM    = -34;

        // ── Global hotkey ─────────────────────────────────────────────────
        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public const uint MOD_ALT      = 0x0001;
        public const uint MOD_CONTROL  = 0x0002;
        public const uint MOD_SHIFT    = 0x0004;
        public const uint MOD_WIN      = 0x0008;
        public const uint MOD_NOREPEAT = 0x4000;

        public const uint VK_RIGHT = 0x27;
        public const uint VK_LEFT  = 0x25;
        public const uint VK_UP    = 0x26;
        public const uint VK_DOWN  = 0x28;
        public const int WM_HOTKEY = 0x0312;

        // ── DPI awareness ─────────────────────────────────────────────────
        [DllImport("user32.dll")]
        public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        // ── Foreground window tracking ────────────────────────────────────
        public delegate void WinEventDelegate(
            IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll")]
        public static extern IntPtr SetWinEventHook(
            uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        public const uint WINEVENT_OUTOFCONTEXT   = 0x0000;
        public const uint WINEVENT_SKIPOWNPROCESS  = 0x0002;

        // ── Display device enumeration ────────────────────────────────────
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool EnumDisplayDevices(
            string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        public const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x00000001;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAY_DEVICE
        {
            public uint cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }

        public const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool EnumDisplaySettingsEx(
            string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode, uint dwFlags);

        public const int ENUM_CURRENT_SETTINGS  = -1;
        public const int ENUM_REGISTRY_SETTINGS = -2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public ushort dmSpecVersion;
            public ushort dmDriverVersion;
            public ushort dmSize;
            public ushort dmDriverExtra;
            public uint   dmFields;
            // Union: position / display orientation
            public int dmPositionX;
            public int dmPositionY;
            public uint dmDisplayOrientation;
            public uint dmDisplayFixedOutput;
            // end union
            public short  dmColor;
            public short  dmDuplex;
            public short  dmYResolution;
            public short  dmTTOption;
            public short  dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public ushort dmLogPixels;
            public uint   dmBitsPerPel;
            public uint   dmPelsWidth;
            public uint   dmPelsHeight;
            public uint   dmDisplayFlags;
            public uint   dmDisplayFrequency;
            // ICM / media fields (included so sizeof is correct)
            public uint dmICMMethod;
            public uint dmICMIntent;
            public uint dmMediaType;
            public uint dmDitherType;
            public uint dmReserved1;
            public uint dmReserved2;
            public uint dmPanningWidth;
            public uint dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int ChangeDisplaySettingsEx(
            string lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd,
            uint dwflags, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int ChangeDisplaySettingsEx(
            string lpszDeviceName, IntPtr lpDevMode, IntPtr hwnd,
            uint dwflags, IntPtr lParam);

        public const uint CDS_UPDATEREGISTRY = 0x00000001;
        public const uint CDS_NORESET        = 0x10000000;
        public const uint CDS_RESET          = 0x40000000;

        public const uint DM_PELSWIDTH             = 0x00080000;
        public const uint DM_PELSHEIGHT            = 0x00100000;
        public const uint DM_POSITION              = 0x00000020;
        public const uint DM_DISPLAYORIENTATION    = 0x00000080;

        public const int DISP_CHANGE_SUCCESSFUL = 0;
        public const int DISP_CHANGE_BADMODE    = -2;

        // ── CCD (Connecting and Configuring Displays) ─────────────────────

        [DllImport("user32.dll")]
        public static extern int GetDisplayConfigBufferSizes(
            uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        public static extern int QueryDisplayConfig(
            uint flags,
            ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
            ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
            IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        public static extern int SetDisplayConfig(
            uint numPathArrayElements, [In] DISPLAYCONFIG_PATH_INFO[]? pathArray,
            uint numModeInfoArrayElements, [In] DISPLAYCONFIG_MODE_INFO[]? modeInfoArray,
            uint flags);

        [DllImport("user32.dll")]
        public static extern int DisplayConfigGetDeviceInfo(
            ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

        [DllImport("user32.dll")]
        public static extern int DisplayConfigGetDeviceInfo(
            ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

        // QueryDisplayConfig flags
        public const uint QDC_ALL_PATHS          = 0x00000001;
        public const uint QDC_ONLY_ACTIVE_PATHS  = 0x00000002;

        // SetDisplayConfig flags
        public const uint SDC_TOPOLOGY_SUPPLIED           = 0x00000010;
        public const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x00000020;
        public const uint SDC_APPLY                       = 0x00000080;
        public const uint SDC_SAVE_TO_DATABASE            = 0x00000200;
        public const uint SDC_ALLOW_CHANGES               = 0x00000400;

        // Path flags
        public const uint DISPLAYCONFIG_PATH_ACTIVE = 0x00000001;

        // DisplayConfigGetDeviceInfo types
        public const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
        public const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

        // DISPLAYCONFIG_PATH_MODE_IDX_INVALID
        public const uint DISPLAYCONFIG_PATH_MODE_IDX_INVALID = 0xFFFFFFFF;

        // ── CCD structs ───────────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        public struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_RATIONAL
        {
            public uint Numerator;
            public uint Denominator;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_PATH_TARGET_INFO
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
        public struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_MODE_INFO
        {
            public uint infoType;
            public uint id;
            public LUID adapterId;
            // 48-byte union (largest member: DISPLAYCONFIG_TARGET_MODE / VIDEO_SIGNAL_INFO)
            public ulong u0, u1, u2, u3, u4, u5;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public uint type;
            public uint size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint flags;
            public uint outputTechnology;
            public ushort edidManufactureId;
            public ushort edidProductCodeId;
            public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string monitorDevicePath;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string viewGdiDeviceName;
        }

        // ── Low-level keyboard hook ───────────────────────────────────────
        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string? lpModuleName);

        public const int WH_KEYBOARD_LL = 13;
        public const int WM_KEYDOWN     = 0x0100;
        public const int WM_SYSKEYDOWN  = 0x0104;

        public const uint VK_LWIN = 0x5B;
        public const uint VK_RWIN = 0x5C;
        public const uint VK_SHIFT = 0x10;
        public const uint VK_MENU    = 0x12;
        public const uint VK_CONTROL = 0x11;

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        public const int WM_KEYUP    = 0x0101;
        public const int WM_SYSKEYUP = 0x0105;
    }
}
